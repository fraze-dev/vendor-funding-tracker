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

## Demo

```
GET /api/vendors/1/funding-summary?period=2026-Q3
```

`period` accepts a year (`2026`), quarter (`2026-Q3`) or month (`2026-08`). Interactive API docs are at `/scalar`.

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

## Roadmap

- [ ] Deploy to Azure App Service + Azure SQL with Bicep, from GitHub Actions using OIDC (no stored secrets)
- [ ] Generate accrual journal entries (Dr Vendor Receivable / Cr Cost of Goods Sold) with an audit trail
- [ ] Nightly sales file ingestion: Blob Storage → Azure Function → Service Bus
- [ ] Billback and off-invoice deal types based on purchase orders
- [ ] Frontend dashboard: earned vs. collected funding, receivable aging
- [ ] AI-assisted exception review with Azure OpenAI
- [ ] 52/53-week retail fiscal calendar
