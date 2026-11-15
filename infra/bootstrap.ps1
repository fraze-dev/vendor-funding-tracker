<#
.SYNOPSIS
    One-time Azure setup so GitHub Actions can deploy this app. Safe to re-run.

.DESCRIPTION
    Run this as a subscription Owner, signed in with `az login`. It:
      1. Registers the Azure resource providers the app uses.
      2. Creates the resource group.
      3. Creates a Microsoft Entra group that administers the SQL server, and adds you to it.
      4. Creates a "github-vendor-funding-deploy" identity that GitHub Actions signs in as using
         OpenID Connect (federated credentials), so no Azure secret is stored in GitHub.
      5. Lets that identity deploy to the resource group, administer the database, and grant the app's
         managed identities a fixed list of storage and Service Bus data roles (nothing broader).
      6. Creates a monthly budget alert.
      7. Prints (or, if the GitHub CLI is signed in, sets) the GitHub repository variables.

.EXAMPLE
    ./infra/bootstrap.ps1 -BudgetEmail you@example.com
#>
param(
    [Parameter(Mandatory)] [string] $BudgetEmail,
    [int] $BudgetAmount = 5,
    [string] $Location = 'eastus2',
    [string] $ResourceGroup = 'rg-vendor-funding',
    [string] $GitHubRepo = 'fraze-dev/vendor-funding-tracker',
    [string] $GitHubEnvironment = 'production',
    [string] $SqlAdminGroupName = 'vendor-funding-sql-admins',
    [string] $DeployAppName = 'github-vendor-funding-deploy'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Step([string] $message) { Write-Host "`n==> $message" -ForegroundColor Cyan }

$subscriptionId = az account show --query id -o tsv
$tenantId = az account show --query tenantId -o tsv
Write-Host "Subscription: $(az account show --query name -o tsv) ($subscriptionId)"

$ghSignedIn = $false
if (Get-Command gh -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
    gh auth status *> $null
    $ghSignedIn = $LASTEXITCODE -eq 0
    $PSNativeCommandUseErrorActionPreference = $true
}

Step 'Registering resource providers (can take a few minutes the first time)'
foreach ($namespace in 'Microsoft.App', 'Microsoft.Sql', 'Microsoft.OperationalInsights', 'Microsoft.Insights',
                       'Microsoft.ManagedIdentity', 'Microsoft.Consumption', 'Microsoft.Storage', 'Microsoft.ServiceBus',
                       'Microsoft.EventGrid', 'Microsoft.Web') {
    az provider register --namespace $namespace --wait --output none
    Write-Host "  $namespace registered"
}

Step "Creating resource group $ResourceGroup in $Location"
$resourceGroupId = az group create --name $ResourceGroup --location $Location --tags app=vendor-funding-tracker --query id -o tsv

Step "Creating Entra group $SqlAdminGroupName"
$groupId = az ad group list --filter "displayName eq '$SqlAdminGroupName'" --query '[0].id' -o tsv
if (-not $groupId) {
    $groupId = az ad group create --display-name $SqlAdminGroupName --mail-nickname $SqlAdminGroupName --query id -o tsv
}
$myObjectId = az ad signed-in-user show --query id -o tsv
if ((az ad group member check --group $groupId --member-id $myObjectId --query value -o tsv) -ne 'true') {
    az ad group member add --group $groupId --member-id $myObjectId
}

Step "Creating deploy identity $DeployAppName"
$appId = az ad app list --filter "displayName eq '$DeployAppName'" --query '[0].appId' -o tsv
if (-not $appId) {
    $appId = az ad app create --display-name $DeployAppName --query appId -o tsv
}
$spObjectId = az ad sp list --filter "appId eq '$appId'" --query '[0].id' -o tsv
if (-not $spObjectId) {
    $spObjectId = az ad sp create --id $appId --query id -o tsv
}

Step "Trusting GitHub Actions runs in $GitHubRepo (environment: $GitHubEnvironment)"
# The subject must match GitHub's token exactly. Newer repos use an "immutable" format that includes the
# owner and repo IDs (repo:owner@123/name@456), so ask GitHub for the prefix instead of assuming it.
$subjectPrefix = "repo:$GitHubRepo"
if ($ghSignedIn) {
    $subjectPrefix = gh api "repos/$GitHubRepo/actions/oidc/customization/sub" --jq '.sub_claim_prefix // empty'
    if (-not $subjectPrefix) { $subjectPrefix = "repo:$GitHubRepo" }
} else {
    Write-Host "  GitHub CLI isn't signed in; assuming the subject prefix '$subjectPrefix'." -ForegroundColor Yellow
}
$subject = "${subjectPrefix}:environment:$GitHubEnvironment"
Write-Host "  subject: $subject"

$credentialName = "github-$GitHubEnvironment"
# Filtering in PowerShell rather than with --query: az.cmd on Windows mangles JMESPath filters like [?name=='x'].
$existingCredential = az ad app federated-credential list --id $appId -o json | ConvertFrom-Json |
    Where-Object name -eq $credentialName
if (-not $existingCredential -or $existingCredential.subject -ne $subject) {
    $credentialFile = New-TemporaryFile
    @{
        name      = $credentialName
        issuer    = 'https://token.actions.githubusercontent.com'
        subject   = $subject
        audiences = @('api://AzureADTokenExchange')
    } | ConvertTo-Json | Set-Content $credentialFile
    if ($existingCredential) {
        az ad app federated-credential update --id $appId --federated-credential-id $existingCredential.id `
            --parameters "@$credentialFile" --output none
    } else {
        az ad app federated-credential create --id $appId --parameters "@$credentialFile" --output none
    }
    Remove-Item $credentialFile
}

Step 'Granting the deploy identity Contributor on the resource group'
$existing = @(az role assignment list --assignee $spObjectId --scope $resourceGroupId --role Contributor -o json | ConvertFrom-Json)
if ($existing.Count -eq 0) {
    az role assignment create --assignee-object-id $spObjectId --assignee-principal-type ServicePrincipal `
        --role Contributor --scope $resourceGroupId --output none
}

Step 'Letting the deploy identity grant data-access roles to the app identities (and nothing else)'
# Role Based Access Control Administrator, restricted by an Azure ABAC condition: the pipeline may only
# assign (or remove) these storage and Service Bus data roles, and only to service principals such as
# managed identities. It can't grant Owner, Contributor or anything to a user. Must match infra/ingestion.bicep.
$allowedRoles = @(
    'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' # Storage Blob Data Owner
    'ba92f5b4-2d11-453d-a403-e96b0029c9fe' # Storage Blob Data Contributor
    '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1' # Storage Blob Data Reader
    '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39' # Azure Service Bus Data Sender
    '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0' # Azure Service Bus Data Receiver
) -join ', '
$condition = "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR " +
    "(@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$allowedRoles} AND " +
    "@Request[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase {'ServicePrincipal'})) AND " +
    "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR " +
    "(@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$allowedRoles}))"
$rbacAdminRoleId = 'f58310d9-a9f6-439a-9e8d-f62e7b41a168' # Role Based Access Control Administrator
# A deterministic assignment name, so re-running updates this assignment instead of adding another.
$assignmentName = [guid]::new([System.Security.Cryptography.MD5]::HashData(
    [Text.Encoding]::UTF8.GetBytes("$resourceGroupId|$spObjectId|$rbacAdminRoleId")))
$assignmentFile = New-TemporaryFile
@{
    properties = @{
        roleDefinitionId = "/subscriptions/$subscriptionId/providers/Microsoft.Authorization/roleDefinitions/$rbacAdminRoleId"
        principalId      = $spObjectId
        principalType    = 'ServicePrincipal'
        condition        = $condition
        conditionVersion = '2.0'
    }
} | ConvertTo-Json -Depth 5 | Set-Content $assignmentFile
# Sent as a file through the REST API: az.cmd on Windows mangles the condition's special characters.
az rest --method put --output none --body "@$assignmentFile" `
    --url "https://management.azure.com$resourceGroupId/providers/Microsoft.Authorization/roleAssignments/$assignmentName`?api-version=2022-04-01"
Remove-Item $assignmentFile

Step 'Adding the deploy identity to the SQL admin group (so it can run migrations)'
if ((az ad group member check --group $groupId --member-id $spObjectId --query value -o tsv) -ne 'true') {
    az ad group member add --group $groupId --member-id $spObjectId
}

Step "Creating a $BudgetAmount/month budget alert for $BudgetEmail"
az deployment group create --resource-group $ResourceGroup --name budget `
    --template-file "$PSScriptRoot/budget.bicep" `
    --parameters amount=$BudgetAmount contactEmail=$BudgetEmail --output none

Step 'GitHub repository variables'
$variables = [ordered]@{
    AZURE_CLIENT_ID       = $appId
    AZURE_TENANT_ID       = $tenantId
    AZURE_SUBSCRIPTION_ID = $subscriptionId
    AZURE_RESOURCE_GROUP  = $ResourceGroup
    SQL_ADMIN_GROUP_NAME  = $SqlAdminGroupName
    SQL_ADMIN_GROUP_ID    = $groupId
}

foreach ($name in $variables.Keys) {
    if ($ghSignedIn) {
        gh variable set $name --repo $GitHubRepo --body $variables[$name]
        Write-Host "  set $name"
    } else {
        Write-Host "  gh variable set $name --repo $GitHubRepo --body $($variables[$name])"
    }
}
if (-not $ghSignedIn) {
    Write-Host "`nThe GitHub CLI isn't signed in. Run 'gh auth login', then run the commands above (or re-run this script)." -ForegroundColor Yellow
}

Write-Host "`nDone. Push to main (or run the CI workflow manually) to deploy." -ForegroundColor Green
