# Vendor Funding Tracker

![CI](https://github.com/fraze-dev/vendor-funding-tracker/actions/workflows/ci.yml/badge.svg)

A .NET 10 API that tracks vendor promotional funding deals and calculates how much each vendor owes a retailer, based on daily store sales.

## Business context

Retailers get funding from vendors to promote their products. This project models two common deal types:

| Deal type | How it's earned | Example |
|---|---|---|
| **Scan-down** | A fixed rate for every unit sold during the deal | $0.50 per carton of orange juice sold in August |
| **Lump sum** | A fixed amount, earned evenly per day over the deal term | $12,000 for endcap placement during Q3 |

Finance teams need to know how much funding has been **earned** in each period so they can book it (an accrual) before the vendor actually pays. The `funding-summary` endpoint answers that for any vendor and period.

**Accounting detail:** lump-sum deals are recognized straight-line by day. Each period's amount is computed as *earned-to-date at period end* minus *earned-to-date at the previous period end*. That way the per-period amounts always add up to exactly the contract amount, with no rounding drift (see `FundingCalculatorTests`).

## Live demo

- **Interactive API docs:** https://vendor-funding-api.ambitiousforest-135ed071.centralus.azurecontainerapps.io/scalar
- **Q3 funding for one vendor:** https://vendor-funding-api.ambitiousforest-135ed071.centralus.azurecontainerapps.io/api/vendors/1/funding-summary?period=2026-Q3

> The demo scales to zero when nobody is using it, so the **first request after a quiet period can take up to a minute** while the app and database wake up. After that, responses take well under a second.

`period` accepts a year (`2026`), quarter (`2026-Q3`) or month (`2026-08`).

## Architecture

```
src/
  VendorFunding.Domain          Entities and pure business logic (FundingCalculator, FiscalPeriod). No dependencies.
  VendorFunding.Infrastructure  EF Core DbContext, SQL Server migrations, demo data seeder
  VendorFunding.Api             ASP.NET Core minimal API, OpenAPI + Scalar docs, health checks
tests/
  VendorFunding.Domain.Tests    Fast unit tests for calculations
  VendorFunding.Api.Tests       Integration tests against real SQL Server via Testcontainers
```

## Running locally

Prerequisites: .NET 10 SDK, Docker Desktop, VS Code with the C# Dev Kit extension.

```bash
docker compose up -d            # SQL Server on localhost:1433
dotnet run --project src/VendorFunding.Api
```

Then open http://localhost:5041/scalar. In Development the API applies migrations and seeds three fictional vendors plus four months of sales data on startup.

In VS Code, press **F5** to start SQL Server and debug the API. `requests.http` has ready-made demo requests.

```bash
dotnet test                     # Integration tests need Docker running
```

## Deployment

Every push to `main` that passes tests is deployed to Azure by the `deploy` job in [ci.yml](.github/workflows/ci.yml):

1. Builds a container image and pushes it to GitHub Container Registry.
2. Signs in to Azure with **OpenID Connect**, so no Azure credentials are stored in GitHub.
3. Deploys [infra/main.bicep](infra/main.bicep): Azure Container Apps, Azure SQL Database (free offer), Log Analytics and Application Insights.
4. Applies EF Core migrations from the pipeline, then grants the API's managed identity access to the database.
5. Switches the app to the new image and runs a smoke test.

```
GitHub Actions ──OIDC──▶ Azure ──▶ Container Apps (scales to 0) ──managed identity──▶ Azure SQL (auto-pauses)
                                         │
                                         └──▶ Application Insights
```

**No passwords anywhere:** the API connects to SQL with a managed identity, and SQL accepts Microsoft Entra sign-in only.

**Cost:** close to $0. The app scales to zero and the database auto-pauses when idle, so the first request after a quiet period can take up to a minute while they wake up. Log ingestion has a daily cap, and a budget alert is set up.

One-time setup for a new subscription: `./infra/bootstrap.ps1 -BudgetEmail you@example.com` (see the script for details).

## Roadmap

- [x] Deploy to Azure Container Apps + Azure SQL with Bicep, from GitHub Actions using OIDC (no stored secrets)
- [ ] Generate accrual journal entries (Dr Vendor Receivable / Cr Cost of Goods Sold) with an audit trail
- [ ] Nightly sales file ingestion: Blob Storage → Azure Function → Service Bus
- [ ] Billback and off-invoice deal types based on purchase orders
- [ ] Frontend dashboard: earned vs. collected funding, receivable aging
- [ ] AI-assisted exception review with Azure OpenAI
- [ ] 52/53-week retail fiscal calendar
