// Monthly cost alert for the resource group. Deployed once by bootstrap.ps1 (a budget's start
// date can't be changed later, so it isn't part of the per-release deployment).

@description('Monthly budget in your billing currency.')
param amount int = 5

@description('Email address that receives budget alerts.')
param contactEmail string

@description('First day of the month the budget starts.')
param startDate string = utcNow('yyyy-MM-01')

resource budget 'Microsoft.Consumption/budgets@2023-05-01' = {
  name: 'budget-vendor-funding'
  properties: {
    category: 'Cost'
    amount: amount
    timeGrain: 'Monthly'
    timePeriod: { startDate: startDate }
    notifications: {
      actualOver80Percent: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: [contactEmail]
      }
      forecastOver100Percent: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: [contactEmail]
      }
    }
  }
}
