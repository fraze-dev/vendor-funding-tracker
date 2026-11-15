using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VendorFunding.Contracts;
using VendorFunding.Domain;
using VendorFunding.Infrastructure.Persistence;

namespace VendorFunding.Api.Services;

/// <summary>Records vendor payments and reports what's earned, collected and still owed.</summary>
public sealed class CollectionsService(VendorFundingDbContext db, TimeProvider clock)
{
    // There's no sign-in yet, so entries are attributed to the process that posted them.
    private const string PostedBy = "payments-api";

    /// <summary>Books a payment in the open period. Returns null if the vendor doesn't exist.</summary>
    public async Task<VendorPaymentResponse?> RecordPaymentAsync(int vendorId, RecordPaymentRequest request, CancellationToken cancellationToken)
    {
        var vendor = await db.Vendors.FirstOrDefaultAsync(v => v.Id == vendorId, cancellationToken);
        if (vendor is null)
        {
            return null;
        }

        var reference = request.Reference.Trim();
        if (await db.VendorPayments.AnyAsync(p => p.VendorId == vendorId && p.Reference == reference, cancellationToken))
        {
            throw new AccountingRuleException($"Payment {reference} is already recorded for {vendor.VendorNumber}.");
        }

        var now = clock.GetUtcNow();
        if (request.ReceivedDate > DateOnly.FromDateTime(now.UtcDateTime))
        {
            throw new AccountingRuleException("A payment's received date can't be in the future.");
        }

        var period = await db.AccountingPeriods.FirstOrDefaultAsync(p => p.Status == PeriodStatus.Open, cancellationToken)
            ?? throw new AccountingRuleException("No accounting period is open. Run an accrual to open one.");
        if (!period.Range.Contains(request.ReceivedDate))
        {
            throw new AccountingRuleException(
                $"Payments are booked in the open period, {period.Label}, and {request.ReceivedDate:yyyy-MM-dd} is outside it.");
        }

        var entry = JournalEntry.ForCashReceipt(period, vendor, request.ReceivedDate, request.Amount, reference, PostedBy, now);
        var payment = new VendorPayment
        {
            VendorId = vendorId,
            Reference = reference,
            ReceivedDate = request.ReceivedDate,
            Amount = request.Amount,
            Memo = string.IsNullOrWhiteSpace(request.Memo) ? null : request.Memo.Trim(),
            JournalEntry = entry,
            RecordedAt = now,
        };
        db.VendorPayments.Add(payment);

        // Include the period in the save so its row version is checked: if someone closed it a moment ago,
        // this fails (409) instead of posting into a closed period.
        db.Entry(period).Property(p => p.Status).IsModified = true;
        await db.SaveChangesAsync(cancellationToken);
        return VendorPaymentResponse.From(payment);
    }

    public async Task<List<VendorPaymentResponse>?> GetPaymentsAsync(int vendorId, CancellationToken cancellationToken)
    {
        if (!await db.Vendors.AnyAsync(v => v.Id == vendorId, cancellationToken))
        {
            return null;
        }

        var payments = await db.VendorPayments.AsNoTracking()
            .Where(p => p.VendorId == vendorId)
            .OrderByDescending(p => p.ReceivedDate).ThenByDescending(p => p.Id)
            .ToListAsync(cancellationToken);
        return payments.Select(VendorPaymentResponse.From).ToList();
    }

    public async Task<DashboardResponse> GetDashboardAsync(DateOnly asOf, CancellationToken cancellationToken)
    {
        var openPeriod = await db.AccountingPeriods.AsNoTracking()
            .Where(p => p.Status == PeriodStatus.Open)
            .Select(p => (DateOnly?)p.StartDate)
            .FirstOrDefaultAsync(cancellationToken);

        // Funding earned: net credits to COGS - Vendor Allowances, by the period they were booked in.
        var earned = await db.JournalLines
            .Where(l => l.AccountNumber == ChartOfAccounts.CogsVendorAllowances)
            .Join(db.JournalEntries.Where(e => e.PostingDate <= asOf), l => l.JournalEntryId, e => e.Id,
                (l, e) => new { e.VendorId, e.AccountingPeriodId, Amount = l.Credit - l.Debit })
            .Join(db.AccountingPeriods, x => x.AccountingPeriodId, p => p.Id, (x, p) => new { x.VendorId, Month = p.StartDate, x.Amount })
            .GroupBy(x => new { x.VendorId, x.Month })
            .Select(g => new { g.Key.VendorId, g.Key.Month, Amount = g.Sum(x => x.Amount) })
            .ToListAsync(cancellationToken);

        var payments = await db.VendorPayments.AsNoTracking()
            .Where(p => p.ReceivedDate <= asOf)
            .Select(p => new { p.VendorId, p.ReceivedDate, p.Amount })
            .ToListAsync(cancellationToken);

        // Every change to each vendor's receivable, for aging.
        var movements = await db.JournalLines
            .Where(l => l.AccountNumber == ChartOfAccounts.VendorFundingReceivable)
            .Join(db.JournalEntries.Where(e => e.VendorId != null && e.PostingDate <= asOf), l => l.JournalEntryId, e => e.Id,
                (l, e) => new { VendorId = e.VendorId!.Value, e.PostingDate, Amount = l.Debit - l.Credit })
            .ToListAsync(cancellationToken);

        var vendors = await db.Vendors.AsNoTracking().OrderBy(v => v.VendorNumber).ToListAsync(cancellationToken);
        var vendorLines = vendors.Select(v =>
        {
            var aging = ReceivableAging.Calculate(
                movements.Where(m => m.VendorId == v.Id).Select(m => new ReceivableTransaction(m.PostingDate, m.Amount)), asOf);
            return new VendorReceivable(
                v.Id, v.VendorNumber, v.Name,
                Earned: earned.Where(e => e.VendorId == v.Id).Sum(e => e.Amount),
                Collected: payments.Where(p => p.VendorId == v.Id).Sum(p => p.Amount),
                Balance: movements.Where(m => m.VendorId == v.Id).Sum(m => m.Amount),
                AgingResponse.From(aging.Open),
                aging.UnappliedCredit);
        }).ToList();

        var months = earned.Select(e => e.Month)
            .Concat(payments.Select(p => new DateOnly(p.ReceivedDate.Year, p.ReceivedDate.Month, 1)))
            .Distinct()
            .Order()
            .Select(month => new MonthlyFunding(
                month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                earned.Where(e => e.Month == month).Sum(e => e.Amount),
                payments.Where(p => p.ReceivedDate.Year == month.Year && p.ReceivedDate.Month == month.Month).Sum(p => p.Amount)))
            .ToList();

        var imports = await db.SalesImports.AsNoTracking().OrderByDescending(i => i.ReceivedAt).Take(5).ToListAsync(cancellationToken);

        var totals = new DashboardTotals(
            Earned: vendorLines.Sum(v => v.Earned),
            Collected: vendorLines.Sum(v => v.Collected),
            OpenReceivable: vendorLines.Sum(v => v.Aging.Total),
            Over60Days: vendorLines.Sum(v => v.Aging.Days61To90 + v.Aging.Over90),
            UnappliedCredit: vendorLines.Sum(v => v.UnappliedCredit));

        return new DashboardResponse(
            asOf,
            openPeriod?.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            totals,
            months,
            vendorLines,
            imports.Select(SalesImportResponse.From).ToList());
    }
}
