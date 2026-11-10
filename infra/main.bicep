// Everything the API runs on. Deployed by the GitHub Actions "deploy" job on every push to main.
// Sized to stay inside Azure free grants: the app scales to zero and the database auto-pauses when idle.

// Not the resource group's region (East US 2): Azure SQL isn't accepting new servers there, or in
// most other US regions, for many subscriptions. Check with:
//   az rest --method get --url "https://management.azure.com/subscriptions/<id>/providers/Microsoft.Sql/locations/<region>/capabilities?api-version=2023-08-01-preview"
@description('Azure region for all resources. Keep the app and database together to avoid cross-region latency.')
param location string = 'centralus'

@description('Name of the container app (also used in its public URL).')
param containerAppName string = 'vendor-funding-api'

@description('Container image to run, e.g. ghcr.io/owner/repo:sha.')
param containerImage string

@description('Display name of the Microsoft Entra group that administers the SQL server.')
param sqlAdminGroupName string

@description('Object ID of that Entra group.')
param sqlAdminGroupObjectId string

// Includes the region so moving regions gets fresh names: Azure can keep a failed resource's
// name reserved in its old region for a while, which blocks re-creating it elsewhere.
var suffix = uniqueString(resourceGroup().id, location)
var tags = { app: 'vendor-funding-tracker' }

// ---------- Monitoring ----------

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-vendor-funding'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    // Hard cap on ingestion so a log storm can't run up a bill (first 5 GB/month are free).
    workspaceCapping: { dailyQuotaGb: json('0.2') }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-vendor-funding'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------- Identity ----------

// The API signs in to SQL as this identity, so no database password exists anywhere.
resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-vendor-funding-api'
  location: location
  tags: tags
}

// ---------- Database ----------

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-vendor-funding-${suffix}'
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true // Entra sign-in only; SQL logins with passwords are disabled.
      principalType: 'Group'
      login: sqlAdminGroupName
      sid: sqlAdminGroupObjectId
      tenantId: subscription().tenantId
    }
  }
}

// Lets Azure services (including Container Apps) reach the server. Everything else is blocked
// by default; the deploy pipeline opens a temporary rule for its own runner.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Azure SQL free offer: serverless, auto-pauses when idle, and pauses (rather than bills)
// if the monthly free allowance runs out.
resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'vendorfunding'
  location: location
  tags: tags
  sku: {
    name: 'GP_S_Gen5_2'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 2
  }
  properties: {
    useFreeLimit: true
    freeLimitExhaustionBehavior: 'AutoPause'
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368 // 32 GB, the free offer's limit
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Local'
  }
}

// ---------- App ----------

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-vendor-funding'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${apiIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      secrets: [
        { name: 'appinsights-connection-string', value: appInsights.properties.ConnectionString }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: containerImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            {
              name: 'ConnectionStrings__VendorFunding'
              value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${database.name};Authentication=Active Directory Managed Identity;User Id=${apiIdentity.properties.clientId};Encrypt=True;Connect Timeout=60'
            }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights-connection-string' }
            { name: 'Database__SeedDemoData', value: 'true' }
            // Container Apps terminates HTTPS; this makes the app see the original https scheme.
            { name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED', value: 'true' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/alive', port: 8080 }
              initialDelaySeconds: 5
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0 // Scale to zero when idle; the first request after a quiet period takes a few seconds.
        maxReplicas: 1
      }
    }
  }
}

output appUrl string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
output sqlServerName string = sqlServer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = database.name
output apiIdentityName string = apiIdentity.name
output apiIdentityClientId string = apiIdentity.properties.clientId
