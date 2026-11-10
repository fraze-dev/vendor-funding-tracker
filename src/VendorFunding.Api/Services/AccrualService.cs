using Microsoft.EntityFrameworkCore;
using VendorFunding.Api.Contracts;
using VendorFunding.Domain;
using VendorFunding.Infrastructure.Persistence;

namespace VendorFunding.Api.Services;

/// <summary>Runs monthly funding accruals, closes periods and reports on the ledger.</summary>
public sealed class AccrualService(VendorFundingDbContext db, TimeProvider clock)
{
    // There's no sign-in yet, so entries are attributed to the process that posted them.
    private const string PostedBy = "accrual-run";

    /// <summary>
    /// Posts whatever each deal has earned but not yet booked, up to the end of <paramref name="month"/>.
    /// Safe to run repeatedly: a second run with no new sales posts nothing.
    /// </summary>
    public async Task<AccrualRunResponse> RunAccrualAsync(FiscalPeriod month, CancellationToken cancellationToken)
    {
        // The retry strategy (EnableRetryOnFailure) requires explicit transactions to run inside it.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var period = await GetPostablePeriodAsync(month, cancellationToken);
            var now = clock.GetUtcNow();
            period.LastAccrualRunAt = now;
            // Saving the period first claims it (via its row version) and gives a new period its Id.
            await db.SaveChangesAsync(cancellationToken);

            var periodEnd = period.Range.End;
            var priorEnd = period.StartDate.AddDays(-1);

            var deals = await db.Deals.AsNoTracking()
                .Where(d => d.StartDate <= periodEnd)
                .OrderBy(d => d.Id)
                .ToListAsync(cancellationToken);

            var booked = await db.JournalLines
                .Where(l => l.AccountNumber == ChartOfAccounts.CogsVendorAllowances)
                .Join(db.JournalEntries.Where(e => e.DealId != null),
                    line => line.JournalEntryId,
                    entry => entry.Id,
                    (line, entry) => new { DealId = entry.DealId!.Value, entry.AccountingPeriodId, Amount = line.Credit - line.Debit })
                .GroupBy(x => new { x.DealId, x.AccountingPeriodId })
                .Select(g => new { g.Key.DealId, g.Key.AccountingPeriodId, Amount = g.Sum(x => x.Amount) })
                .ToListAsync(cancellationToken);

            var entries = new List<JournalEntry>();
            foreach (var deal in deals)
            {
                var trueUp = AccrualCalculator.Calculate(
                    earnedThroughPriorPeriods: await EarnedThroughAsync(deal, priorEnd, cancellationToken),
                    earnedThroughPeriod: await EarnedThroughAsync(deal, periodEnd, cancellationToken),
                    bookedInPriorPeriods: booked.Where(b => b.DealId == deal.Id && b.AccountingPeriodId != period.Id).Sum(b => b.Amount),
                    bookedInPeriod: booked.Where(b => b.DealId == deal.Id && b.AccountingPeriodId == period.Id).Sum(b => b.Amount));

                if (trueUp.PriorPeriodAdjustment != 0m)
                {
                    entries.Add(JournalEntry.ForDealFunding(
                        period, deal, JournalEntryType.PriorPeriodAdjustment, trueUp.PriorPeriodAdjustment, PostedBy, now));
                }

                if (trueUp.CurrentPeriod != 0m)
                {
                    entries.Add(JournalEntry.ForDealFunding(
                        period, deal, JournalEntryType.Accrual, trueUp.CurrentPeriod, PostedBy, now));
                }
            }

            db.JournalEntries.AddRange(entries);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var dealNumbers = deals.ToDictionary(d => d.Id, d => d.DealNumber);
            return new AccrualRunResponse(
                period.Label,
                entries.Count,
                entries.Sum(SignedFundingAmount),
                entries.Select(e => JournalEntryResponse.From(e, period.Label, dealNumbers[e.DealId!.Value])).ToList());
        });
    }

    public async Task<PeriodResponse> ClosePeriodAsync(FiscalPeriod month, CancellationToken cancellationToken)
    {
        var period = await db.AccountingPeriods.FirstOrDefaultAsync(p => p.StartDate == month.Range.Start, cancellationToken)
            ?? throw new AccountingRuleException($"Nothing has been posted to {month.Label} yet. Run its accrual before closing it.");

        if (period.Status == PeriodStatus.Closed)
        {
            throw new AccountingRuleException($"{period.Label} is already closed.");
        }

        period.Status = PeriodStatus.Closed;
        period.ClosedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return PeriodResponse.From(period);
    }

    public async Task<List<PeriodResponse>> GetPeriodsAsync(CancellationToken cancellationToken)
    {
        var periods = await db.AccountingPeriods.AsNoTracking().OrderBy(p => p.StartDate).ToListAsync(cancellationToken);
        return periods.Select(PeriodResponse.From).ToList();
    }

    public async Task<List<JournalEntryResponse>> GetJournalEntriesAsync(FiscalPeriod? month, int? dealId, CancellationToken cancellationToken)
    {
        var query = db.JournalEntries.AsNoTracking()
            .Include(e => e.Lines)
            .Include(e => e.AccountingPeriod)
            .Include(e => e.Deal)
            .AsQueryable();

        if (month is not null)
        {
            query = query.Where(e => e.AccountingPeriod!.StartDate == month.Range.Start);
        }

        if (dealId is not null)
        {
            query = query.Where(e => e.DealId == dealId);
        }

        var entries = await query.OrderBy(e => e.Id).Take(500).ToListAsync(cancellationToken);
        return entries.Select(e => JournalEntryResponse.From(e, e.AccountingPeriod!.Label, e.Deal?.DealNumber)).ToList();
    }

    /// <summary>Account balances from every entry posted through the end of <paramref name="month"/>.</summary>
    public async Task<TrialBalanceResponse> GetTrialBalanceAsync(FiscalPeriod month, CancellationToken cancellationToken)
    {
        var asOf = month.Range.End;
        var accounts = await db.JournalLines
            .Join(db.JournalEntries.Where(e => e.PostingDate <= asOf), line => line.JournalEntryId, entry => entry.Id, (line, _) => line)
            .GroupBy(l => l.AccountNumber)
            .Select(g => new { Account = g.Key, Debits = g.Sum(l => l.Debit), Credits = g.Sum(l => l.Credit) })
            .OrderBy(a => a.Account)
            .ToListAsync(cancellationToken);

        var lines = accounts
            .Select(a => new TrialBalanceLine(a.Account, ChartOfAccounts.NameOf(a.Account), a.Debits, a.Credits, a.Debits - a.Credits))
            .ToList();
        var totalDebits = lines.Sum(l => l.Debits);
        var totalCredits = lines.Sum(l => l.Credits);
        return new TrialBalanceResponse(month.Label, asOf, lines, totalDebits, totalCredits, totalDebits == totalCredits);
    }

    /// <summary>
    /// Periods open and close in order, one at a time. Returns the open period if <paramref name="month"/> is it,
    /// opens <paramref name="month"/> if it's next in line (or the very first period), and refuses anything else.
    /// </summary>
    private async Task<AccountingPeriod> GetPostablePeriodAsync(FiscalPeriod month, CancellationToken cancellationToken)
    {
        var start = month.Range.Start;
        var open = await db.AccountingPeriods.FirstOrDefaultAsync(p => p.Status == PeriodStatus.Open, cancellationToken);
        if (open is not null)
        {
            if (open.StartDate == start)
            {
                return open;
            }

            throw new AccountingRuleException(start < open.StartDate
                ? $"{month.Label} is closed. Late activity for it is booked in {open.Label} as a prior-period adjustment."
                : $"{open.Label} is still open. Close it before posting to {month.Label}.");
        }

        var latest = await db.AccountingPeriods.OrderByDescending(p => p.StartDate).FirstOrDefaultAsync(cancellationToken);
        if (latest is not null)
        {
            var next = new AccountingPeriod { StartDate = latest.StartDate.AddMonths(1) };
            if (start <= latest.StartDate)
            {
                throw new AccountingRuleException(
                    $"{month.Label} is closed. Late activity for it is booked in {next.Label} as a prior-period adjustment.");
            }

            if (start != next.StartDate)
            {
                throw new AccountingRuleException($"Periods open in order. The next period is {next.Label}.");
            }
        }

        var period = new AccountingPeriod { StartDate = start };
        db.AccountingPeriods.Add(period);
        return period;
    }

    private async Task<decimal> EarnedThroughAsync(Deal deal, DateOnly date, CancellationToken cancellationToken)
    {
        if (date < deal.StartDate)
        {
            return 0m;
        }

        var units = 0;
        if (deal.Type == DealType.ScanDown)
        {
            var through = date < deal.EndDate ? date : deal.EndDate;
            // One aggregate query per deal. Fine at this scale; a large deal book would batch these.
            units = await db.SalesRecords
                .Where(s => s.Sku == deal.Sku && s.SaleDate >= deal.StartDate && s.SaleDate <= through)
                .SumAsync(s => (int?)s.UnitsSold, cancellationToken) ?? 0;
        }

        return FundingCalculator.EarnedThrough(deal, date, units);
    }

    /// <summary>The entry's effect on funding: positive when it credits COGS - Vendor Allowances.</summary>
    private static decimal SignedFundingAmount(JournalEntry entry) =>
        entry.Lines.Where(l => l.AccountNumber == ChartOfAccounts.CogsVendorAllowances).Sum(l => l.Credit - l.Debit);
}
