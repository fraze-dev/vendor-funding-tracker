using VendorFunding.Domain;

namespace VendorFunding.Contracts;

public record VendorPaymentResponse(
    int Id,
    int VendorId,
    string Reference,
    DateOnly ReceivedDate,
    decimal Amount,
    string? Memo,
    int JournalEntryId,
    string EntryNumber,
    DateTimeOffset RecordedAt)
{
    public static VendorPaymentResponse From(VendorPayment p) => new(
        p.Id, p.VendorId, p.Reference, p.ReceivedDate, p.Amount, p.Memo, p.JournalEntryId,
        $"JE-{p.JournalEntryId:D6}", p.RecordedAt);
}

/// <summary>Everything the dashboard shows, in one response so a page load wakes the API and database only once.</summary>
public record DashboardResponse(
    DateOnly AsOf,
    string? OpenPeriod,
    DashboardTotals Totals,
    IReadOnlyList<MonthlyFunding> Monthly,
    IReadOnlyList<VendorReceivable> Vendors,
    IReadOnlyList<SalesImportResponse> RecentImports);

/// <param name="Earned">Funding accrued in the ledger.</param>
/// <param name="Collected">Payments received from vendors.</param>
/// <param name="OpenReceivable">Earned but not yet collected.</param>
/// <param name="Over60Days">Open receivable outstanding for more than 60 days.</param>
public record DashboardTotals(decimal Earned, decimal Collected, decimal OpenReceivable, decimal Over60Days, decimal UnappliedCredit);

/// <param name="Month">yyyy-MM. Earned is by the period it was booked in; collected is by the month it was received.</param>
public record MonthlyFunding(string Month, decimal Earned, decimal Collected);

public record VendorReceivable(
    int VendorId,
    string VendorNumber,
    string Name,
    decimal Earned,
    decimal Collected,
    decimal Balance,
    AgingResponse Aging,
    decimal UnappliedCredit);

public record AgingResponse(decimal Current, decimal Days31To60, decimal Days61To90, decimal Over90, decimal Total)
{
    public static AgingResponse From(AgingBuckets b) => new(b.Current, b.Days31To60, b.Days61To90, b.Over90, b.Total);
}
