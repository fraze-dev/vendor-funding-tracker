# Vendor Funding Tracker

![CI](https://github.com/fraze-dev/vendor-funding-tracker/actions/workflows/ci.yml/badge.svg)

A .NET 10 system that tracks vendor promotional funding deals, calculates how much each vendor owes a retailer from daily store sales, books it in a ledger, and tracks what's been collected. It has an ASP.NET Core API, Azure Functions for sales file ingestion, and a Blazor WebAssembly dashboard.

## Business context

Retailers get funding from vendors to promote their products. This project models two common deal types:

| Deal type | How it's earned | Example |
|---|---|---|
| **Scan-down** | A fixed rate for every unit sold during the deal | $0.50 per carton of orange juice sold in August |
| **Lump sum** | A fixed amount, earned evenly per day over the deal term | $12,000 for endcap placement during Q3 |

Finance teams need to know how much funding has been **earned** in each period so they can book it (an accrual) before the vendor actually pays. The `funding-summary` endpoint answers that for any vendor and period.

**Accounting detail:** lump-sum deals are recognized straight-line by day. Each period's amount is computed as *earned-to-date at period end* minus *earned-to-date at the previous period end*. That way the per-period amounts always add up to exactly the contract amount, with no rounding drift (see `FundingCalculatorTests`).

## Accrual accounting

Once funding is earned, finance books it each month as an accrual:

```
Dr  1310  Vendor Funding Receivable        3,180.00
    Cr  5010  COGS - Vendor Allowances            3,180.00
```

| Endpoint | What it does |
|---|---|
| `POST /api/accruals/{month}/run` | Posts entries for funding earned but not yet booked, through the end of the month |
| `POST /api/periods/{month}/close` | Locks the month and lets the next one open |
| `GET /api/journal-entries?period={month}` | Lists posted entries with their debit and credit lines |
| `GET /api/trial-balance?period={month}` | Account balances as of the end of the month |

The rules it follows:

- **True-ups, not duplicates.** Each run compares *earned to date* with *booked to date* for every deal and posts only the difference, so re-running a month with no new sales posts nothing (`AccrualCalculator`).
- **Posted entries are permanent.** Corrections are new entries, never edits or deletes, so the journal is a complete audit trail. The DbContext rejects any attempt to modify or delete a posted entry.
- **Periods close in order, one at a time.** Only the single open month accepts postings. A filtered unique index guarantees there's never more than one open period, and a row version stops two simultaneous runs from double-posting.
- **Late data doesn't reopen closed months.** Sales that arrive after a month closes are booked in the open month as a **prior-period adjustment**.
- **Double-entry is enforced.** Debits must equal credits on every entry, and a database check constraint requires each line to be a single positive debit or credit.

The integration tests walk through a full month-end close (accrue, re-run, close, late sale, adjust) and confirm the trial balance agrees with the funding calculation to the cent.

## Collections and receivable aging

When a vendor pays, the payment is booked against what it owes:

```
Dr  1010  Cash                             6,000.00
    Cr  1310  Vendor Funding Receivable           6,000.00
```

| Endpoint | What it does |
|---|---|
| `POST /api/vendors/{id}/payments` | Record a payment (reference, received date, amount) in the open period |
| `GET /api/vendors/{id}/payments` | A vendor's payments |
| `GET /api/dashboard` | Earned vs. collected by month, and each vendor's receivable aging, in one call |

- **Aging, oldest first.** Payments settle the oldest receivable first (FIFO), and what's left is aged from its posting date into 0–30, 31–60, 61–90 and over 90 days (`ReceivableAging`). Overpayments show as unapplied credit.
- **Same controls as the ledger.** Payments post only into the open period, can't be future-dated, can't be recorded twice (a unique reference per vendor), and can't be edited or deleted once recorded.

## Dashboard (Blazor WebAssembly)

`src/VendorFunding.Web` is a Blazor WebAssembly app hosted on **Azure Static Web Apps** (free tier). It runs in the browser and calls the API, so the site itself costs nothing and loads instantly even while the API is scaled to zero; a "waking up" message covers the API's cold start.

- **Dashboard:** open receivable, earned, collected and amounts over 60 days; earned vs. collected by month; aging by vendor; recent sales files.
- **Vendor:** receivable and aging, deals, payment history, and a form to record a payment.
- **Ledger:** periods, trial balance and journal entries.
- **Sales files:** upload a CSV and watch it move through the ingestion pipeline.

It shares request and response types with the API through `VendorFunding.Contracts`, so a contract change that breaks the UI breaks the build. The charts are plain SVG with no chart library: colors were checked with a color-vision-deficiency validator, they're drawn at the real pixel width (measured with a `ResizeObserver`), and every chart has hover tooltips and a table view. Components are tested with bUnit.

## Sales file ingestion

Stores send their daily sales as CSV files. Each file is processed in the background, and nothing is lost if the database is paused or a step fails:

```
POST /api/sales/files  (or a file dropped straight into Blob Storage)
   ▼
Blob Storage: sales-inbox/2026/11/14/<import-id>.csv
   │  Event Grid "BlobCreated"
   ▼
Service Bus queue: sales-files ──▶ Function: ParseSalesFile
   │   validates every row; bad rows go to sales-rejects with a reason
   ▼
Service Bus queue: sales-records (batches of 500 rows) ──▶ Function: LoadSalesBatch ──▶ Azure SQL
```

```
store_number,sku,sale_date,units_sold,net_sales
S0001,100245,2026-11-14,42,209.58
```

| Endpoint | What it does |
|---|---|
| `POST /api/sales/files` | Upload a CSV (multipart form field `file`). Returns `202 Accepted` with the import to follow |
| `GET /api/sales/imports/{id}` | Import status: `Received` → `Loading` → `Completed` / `CompletedWithErrors` / `Duplicate` / `Failed` |
| `GET /api/sales/imports/{id}/rejects` | Download the rejected rows with the reason for each |

How it stays reliable:

- **Queues between every step.** If SQL is resuming from auto-pause, messages wait and retry. After repeated failures they go to a dead-letter queue instead of disappearing.
- **Everything is safe to repeat.** Queues can deliver a message more than once, so a batch is recorded when it's loaded and never loaded twice, and a file's content hash means sending the same file again loads nothing.
- **One bad row doesn't block a store's sales.** Valid rows load; invalid ones are set aside with a line number and reason.
- **Traceable.** Every sales record keeps the ID of the file it came from.
- **Nightly accrual.** A timer Function re-runs the accrual for the open period each night, but only if files arrived that day, so quiet nights don't wake the database.
- **No secrets.** The Functions, API and Event Grid each use their own managed identity with only the access they need. Storage account keys and Service Bus SAS keys are disabled.

The parsing and loading logic lives in the shared `VendorFunding.Infrastructure` project, so the integration tests run it against real SQL Server and Azurite, including redelivered and out-of-order batches.

## Live demo

- **Interactive API docs:** https://vendor-funding-api.ambitiousforest-135ed071.centralus.azurecontainerapps.io/scalar
- **Q3 funding for one vendor:** https://vendor-funding-api.ambitiousforest-135ed071.centralus.azurecontainerapps.io/api/vendors/1/funding-summary?period=2026-Q3

> The demo scales to zero when nobody is using it, so the **first request after a quiet period can take up to a minute** while the app and database wake up. After that, responses take well under a second.

`period` accepts a year (`2026`), quarter (`2026-Q3`) or month (`2026-08`).

## Architecture

```
src/
  VendorFunding.Domain          Entities and pure business logic (FundingCalculator, AccrualCalculator, JournalEntry,
                                SalesFileParser). No dependencies.
  VendorFunding.Contracts       Request and response types shared by the API and the dashboard
  VendorFunding.Infrastructure  EF Core DbContext, SQL Server migrations, demo data seeder, sales file processing
  VendorFunding.Api             ASP.NET Core minimal API, OpenAPI + Scalar docs, health checks
  VendorFunding.Functions       Azure Functions (isolated worker): sales file pipeline and nightly accrual
  VendorFunding.Web             Blazor WebAssembly dashboard
tests/
  VendorFunding.Domain.Tests    Fast unit tests for calculations, aging and parsing
  VendorFunding.Api.Tests       Integration tests against real SQL Server and Azurite via Testcontainers
  VendorFunding.Web.Tests       bUnit component tests for the dashboard
```

## Running locally

Prerequisites: .NET 10 SDK, Docker Desktop, VS Code with the C# Dev Kit extension.

```bash
docker compose up -d            # SQL Server on localhost:1433, Azurite (Blob Storage) on localhost:10000
dotnet run --project src/VendorFunding.Api
```

Then open http://localhost:5041/scalar. In Development the API applies migrations and seeds three fictional vendors plus four months of sales data on startup.

For the dashboard, run this in a second terminal and open http://localhost:5280:

```bash
dotnet run --project src/VendorFunding.Web
```

File uploads are stored in Azurite locally. Processing them needs Azure Service Bus, so the Functions run in Azure; their logic is covered by the integration tests. To run them locally anyway, see `src/VendorFunding.Functions/local.settings.sample.json`.

In VS Code, press **F5** to start SQL Server and debug the API. `requests.http` has ready-made demo requests.

```bash
dotnet test                     # Integration tests need Docker running
```

## Deployment

Every push to `main` that passes tests is deployed to Azure by the `deploy` job in [ci.yml](.github/workflows/ci.yml):

1. Builds a container image and pushes it to GitHub Container Registry.
2. Signs in to Azure with **OpenID Connect**, so no Azure credentials are stored in GitHub.
3. Deploys [infra/main.bicep](infra/main.bicep): Azure Container Apps, Azure SQL Database (free offer), Log Analytics and Application Insights, plus [infra/ingestion.bicep](infra/ingestion.bicep): Blob Storage, Event Grid, Service Bus and a Flex Consumption Function app.
4. Applies EF Core migrations from the pipeline, then grants the API's and Functions' managed identities access to the database.
5. Deploys the Functions, switches the API to the new image, and runs smoke tests, including a real file through the whole ingestion pipeline.

```
GitHub Actions ──OIDC──▶ Azure ──▶ Container Apps (scales to 0) ──managed identity──▶ Azure SQL (auto-pauses)
                                         │                                              ▲
                                         ├──▶ Blob Storage ─▶ Event Grid ─▶ Service Bus ─▶ Functions (scale to 0)
                                         └──▶ Application Insights
```

**No passwords anywhere:** everything connects with managed identities, SQL accepts Microsoft Entra sign-in only, and storage keys and Service Bus SAS keys are disabled. The deploy identity can assign only a fixed list of data-access roles, enforced by an Azure ABAC condition.

**Cost:** close to $0. The app scales to zero and the database auto-pauses when idle, so the first request after a quiet period can take up to a minute while they wake up. Log ingestion has a daily cap, and a budget alert is set up.

One-time setup for a new subscription: `./infra/bootstrap.ps1 -BudgetEmail you@example.com` (see the script for details).

## Roadmap

- [x] Deploy to Azure Container Apps + Azure SQL with Bicep, from GitHub Actions using OIDC (no stored secrets)
- [x] Generate accrual journal entries (Dr Vendor Receivable / Cr Cost of Goods Sold) with an audit trail
- [x] Nightly sales file ingestion: Blob Storage → Event Grid → Service Bus → Azure Functions
- [ ] Billback and off-invoice deal types based on purchase orders
- [x] Frontend dashboard: earned vs. collected funding, receivable aging (Blazor WebAssembly + collections)
- [ ] AI-assisted exception review with Azure OpenAI
- [ ] 52/53-week retail fiscal calendar
