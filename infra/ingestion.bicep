// Sales file ingestion:
//   Blob Storage (sales-inbox) → Event Grid → Service Bus (sales-files) → Function: ParseSalesFile
//     → Service Bus (sales-records) → Function: LoadSalesBatch → Azure SQL
// plus a nightly timer Function that re-runs the accrual. Everything signs in with managed identities:
// shared-key access is disabled on the storage account and local (SAS) auth is disabled on Service Bus.

param location string
param suffix string
param tags object

@description('Principal ID of the API\'s managed identity, which uploads files and reads rejects.')
param apiIdentityPrincipalId string

param sqlServerFqdn string
param databaseName string

@description('Base URL of the API, which the nightly accrual Function calls.')
param apiBaseUrl string

@secure()
param appInsightsConnectionString string

@description('When the nightly accrual runs (NCRONTAB, UTC). Default: 07:00 UTC, i.e. 2-3 AM US Eastern.')
param nightlyAccrualSchedule string = '0 0 7 * * *'

var inboxContainerName = 'sales-inbox'
var rejectsContainerName = 'sales-rejects'
var deploymentsContainerName = 'function-deployments'

// Built-in role IDs. infra/bootstrap.ps1 limits the deploy identity to assigning only these.
var roles = {
  blobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  blobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  blobDataReader: '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
}

resource functionsIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-vendor-funding-functions'
  location: location
  tags: tags
}

// ---------- Storage ----------

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stvf${suffix}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false // Managed identity only: no account keys or SAS tokens to leak.
    defaultToOAuthAuthentication: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

resource inbox 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: inboxContainerName
}

resource rejects 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: rejectsContainerName
}

resource deployments 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: deploymentsContainerName
}

// Sales files are loaded into SQL within minutes; keep them 90 days for troubleshooting, then delete.
resource retention 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'delete-old-sales-files'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: ['blockBlob']
              prefixMatch: ['${inboxContainerName}/', '${rejectsContainerName}/']
            }
            actions: { baseBlob: { delete: { daysAfterModificationGreaterThan: 90 } } }
          }
        }
      ]
    }
  }
}

// ---------- Messaging ----------

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: 'sb-vendor-funding-${suffix}'
  location: location
  tags: tags
  sku: { name: 'Basic', tier: 'Basic' } // About $0.05 per million operations.
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }
}

// One message per new file. Failed parses retry, then go to the dead-letter queue for investigation.
resource salesFilesQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: serviceBus
  name: 'sales-files'
  properties: {
    lockDuration: 'PT5M'
    maxDeliveryCount: 5
    defaultMessageTimeToLive: 'P14D'
    deadLetteringOnMessageExpiration: true
  }
}

// One message per batch of up to 500 rows. More retries, since the database may be resuming from auto-pause.
resource salesRecordsQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: serviceBus
  name: 'sales-records'
  properties: {
    lockDuration: 'PT2M'
    maxDeliveryCount: 10
    defaultMessageTimeToLive: 'P14D'
    deadLetteringOnMessageExpiration: true
  }
}

// ---------- Events: new file in sales-inbox → sales-files queue ----------

resource storageEvents 'Microsoft.EventGrid/systemTopics@2022-06-15' = {
  name: 'evgt-vendor-funding-storage'
  location: location
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    source: storage.id
    topicType: 'Microsoft.Storage.StorageAccounts'
  }
}

resource eventsSendToQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: salesFilesQueue
  name: guid(salesFilesQueue.id, storageEvents.id, roles.serviceBusDataSender)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataSender)
    principalId: storageEvents.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource newSalesFile 'Microsoft.EventGrid/systemTopics/eventSubscriptions@2022-06-15' = {
  parent: storageEvents
  name: 'new-sales-file'
  dependsOn: [eventsSendToQueue]
  properties: {
    eventDeliverySchema: 'EventGridSchema'
    filter: {
      includedEventTypes: ['Microsoft.Storage.BlobCreated']
      subjectBeginsWith: '/blobServices/default/containers/${inboxContainerName}/'
    }
    deliveryWithResourceIdentity: {
      identity: { type: 'SystemAssigned' }
      destination: {
        endpointType: 'ServiceBusQueue'
        properties: { resourceId: salesFilesQueue.id }
      }
    }
    retryPolicy: { maxDeliveryAttempts: 30, eventTimeToLiveInMinutes: 1440 }
  }
}

// ---------- Functions (Flex Consumption: scales to zero, monthly free grant) ----------

resource functionsPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'asp-vendor-funding-functions'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: { tier: 'FlexConsumption', name: 'FC1' }
  properties: { reserved: true }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: 'func-vendor-funding-${suffix}'
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${functionsIdentity.id}': {} }
  }
  properties: {
    serverFarmId: functionsPlan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentsContainerName}'
          authentication: { type: 'UserAssignedIdentity', userAssignedIdentityResourceId: functionsIdentity.id }
        }
      }
      scaleAndConcurrency: { maximumInstanceCount: 40, instanceMemoryMB: 512 }
      runtime: { name: 'dotnet-isolated', version: '10.0' }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      appSettings: [
        // Identity-based connections: no keys or connection strings with secrets.
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: functionsIdentity.properties.clientId }
        { name: 'ServiceBus__fullyQualifiedNamespace', value: '${serviceBus.name}.servicebus.windows.net' }
        { name: 'ServiceBus__credential', value: 'managedidentity' }
        { name: 'ServiceBus__clientId', value: functionsIdentity.properties.clientId }
        { name: 'AZURE_CLIENT_ID', value: functionsIdentity.properties.clientId }
        { name: 'SalesStorage__BlobServiceUri', value: storage.properties.primaryEndpoints.blob }
        {
          name: 'ConnectionStrings__VendorFunding'
          value: 'Server=tcp:${sqlServerFqdn},1433;Database=${databaseName};Authentication=Active Directory Managed Identity;User Id=${functionsIdentity.properties.clientId};Encrypt=True;Connect Timeout=60'
        }
        { name: 'VendorFundingApi__BaseUrl', value: apiBaseUrl }
        { name: 'NightlyAccrualSchedule', value: nightlyAccrualSchedule }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
      ]
    }
  }
  dependsOn: [deployments, functionsStorageAccess]
}

// ---------- Least-privilege access ----------

// Functions: host storage (deployment package, timer leases), read the inbox, write rejects.
resource functionsStorageAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, functionsIdentity.id, roles.blobDataOwner)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataOwner)
    principalId: functionsIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionsReadFileEvents 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: salesFilesQueue
  name: guid(salesFilesQueue.id, functionsIdentity.id, roles.serviceBusDataReceiver)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataReceiver)
    principalId: functionsIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionsSendBatches 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: salesRecordsQueue
  name: guid(salesRecordsQueue.id, functionsIdentity.id, roles.serviceBusDataSender)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataSender)
    principalId: functionsIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource functionsReadBatches 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: salesRecordsQueue
  name: guid(salesRecordsQueue.id, functionsIdentity.id, roles.serviceBusDataReceiver)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataReceiver)
    principalId: functionsIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// API: upload into the inbox and download rejects. Nothing else in the storage account.
resource apiUploadToInbox 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: inbox
  name: guid(inbox.id, apiIdentityPrincipalId, roles.blobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataContributor)
    principalId: apiIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource apiReadRejects 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: rejects
  name: guid(rejects.id, apiIdentityPrincipalId, roles.blobDataReader)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.blobDataReader)
    principalId: apiIdentityPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output blobServiceUri string = storage.properties.primaryEndpoints.blob
output functionAppName string = functionApp.name
output functionsIdentityName string = functionsIdentity.name
output functionsIdentityClientId string = functionsIdentity.properties.clientId
