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
      5. Lets that identity deploy to the resource group and administer the database.
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

Step 'Registering resource providers (can take a few minutes the first time)'
foreach ($namespace in 'Microsoft.App', 'Microsoft.Sql', 'Microsoft.OperationalInsights', 'Microsoft.Insights',
                       'Microsoft.ManagedIdentity', 'Microsoft.Consumption') {
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
$credentialName = "github-$GitHubEnvironment"
# Filtering in PowerShell rather than with --query: az.cmd on Windows mangles JMESPath filters like [?name=='x'].
$credentialNames = az ad app federated-credential list --id $appId -o json | ConvertFrom-Json | ForEach-Object name
if ($credentialName -notin $credentialNames) {
    $credentialFile = New-TemporaryFile
    @{
        name      = $credentialName
        issuer    = 'https://token.actions.githubusercontent.com'
        subject   = "repo:${GitHubRepo}:environment:$GitHubEnvironment"
        audiences = @('api://AzureADTokenExchange')
    } | ConvertTo-Json | Set-Content $credentialFile
    az ad app federated-credential create --id $appId --parameters "@$credentialFile" --output none
    Remove-Item $credentialFile
}

Step 'Granting the deploy identity Contributor on the resource group'
$existing = @(az role assignment list --assignee $spObjectId --scope $resourceGroupId --role Contributor -o json | ConvertFrom-Json)
if ($existing.Count -eq 0) {
    az role assignment create --assignee-object-id $spObjectId --assignee-principal-type ServicePrincipal `
        --role Contributor --scope $resourceGroupId --output none
}

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

$ghSignedIn = $false
if (Get-Command gh -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
    gh auth status *> $null
    $ghSignedIn = $LASTEXITCODE -eq 0
    $PSNativeCommandUseErrorActionPreference = $true
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
