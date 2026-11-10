using Microsoft.AspNetCore.Http.HttpResults;
using VendorFunding.Api.Contracts;
using VendorFunding.Api.Services;
using VendorFunding.Domain;

namespace VendorFunding.Api.Endpoints;

public static class AccountingEndpoints
{
    public static IEndpointRouteBuilder MapAccountingEndpoints(this IEndpointRouteBuilder app)
    {
        var accounting = app.MapGroup("/api").WithTags("Accounting");

        accounting.MapPost("/accruals/{period}/run", RunAccrual)
            .WithSummary("Run the funding accrual for a month")
            .WithDescription(
                "Posts journal entries for funding earned but not yet booked, through the end of the month. " +
                "Safe to re-run: only the difference is posted. Late sales for closed months are booked as " +
                "prior-period adjustments. Periods open and close in order, one at a time.");

        accounting.MapGet("/periods", GetPeriods).WithSummary("List accounting periods");

        accounting.MapPost("/periods/{period}/close", ClosePeriod)
            .WithSummary("Close a month")
            .WithDescription("Locks the month so nothing more can be posted to it, and lets the next month open.");

        accounting.MapGet("/journal-entries", GetJournalEntries)
            .WithSummary("List posted journal entries")
            .WithDescription("Optionally filter by `period` (a month such as `2026-08`) and `dealId`. Returns up to 500 entries.");

        accounting.MapGet("/trial-balance", GetTrialBalance)
            .WithSummary("Trial balance as of the end of a month")
            .WithDescription("Account totals from every entry posted through the end of `period`. Debits always equal credits.");

        return app;
    }

    private static async Task<Results<Ok<AccrualRunResponse>, ValidationProblem>> RunAccrual(
        string period, AccrualService accruals, CancellationToken ct)
    {
        if (!TryParseMonth(period, out var month, out var problem))
        {
            return problem;
        }

        return TypedResults.Ok(await accruals.RunAccrualAsync(month, ct));
    }

    private static async Task<Ok<List<PeriodResponse>>> GetPeriods(AccrualService accruals, CancellationToken ct) =>
        TypedResults.Ok(await accruals.GetPeriodsAsync(ct));

    private static async Task<Results<Ok<PeriodResponse>, ValidationProblem>> ClosePeriod(
        string period, AccrualService accruals, CancellationToken ct)
    {
        if (!TryParseMonth(period, out var month, out var problem))
        {
            return problem;
        }

        return TypedResults.Ok(await accruals.ClosePeriodAsync(month, ct));
    }

    private static async Task<Results<Ok<List<JournalEntryResponse>>, ValidationProblem>> GetJournalEntries(
        string? period, int? dealId, AccrualService accruals, CancellationToken ct)
    {
        FiscalPeriod? month = null;
        if (period is not null && !TryParseMonth(period, out month, out var problem))
        {
            return problem;
        }

        return TypedResults.Ok(await accruals.GetJournalEntriesAsync(month, dealId, ct));
    }

    private static async Task<Results<Ok<TrialBalanceResponse>, ValidationProblem>> GetTrialBalance(
        string period, AccrualService accruals, CancellationToken ct)
    {
        if (!TryParseMonth(period, out var month, out var problem))
        {
            return problem;
        }

        return TypedResults.Ok(await accruals.GetTrialBalanceAsync(month, ct));
    }

    private static bool TryParseMonth(string value, out FiscalPeriod month, out ValidationProblem problem)
    {
        if (FiscalPeriod.TryParse(value, out var parsed) && parsed.IsMonth)
        {
            month = parsed;
            problem = null!;
            return true;
        }

        month = null!;
        problem = TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["period"] = ["Accounting periods are months. Use a month such as 2026-08."],
        });
        return false;
    }
}
