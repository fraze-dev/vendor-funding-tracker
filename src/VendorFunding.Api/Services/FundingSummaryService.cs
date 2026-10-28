using Microsoft.EntityFrameworkCore;
using VendorFunding.Api.Contracts;
using VendorFunding.Domain;
using VendorFunding.Infrastructure.Persistence;

namespace VendorFunding.Api.Services;

public sealed class FundingSummaryService(VendorFundingDbContext db)
{
    /// <summary>Returns funding earned by a vendor's deals in the period, or null if the vendor doesn't exist.</summary>
    public async Task<FundingSummaryResponse?> GetAsync(int vendorId, FiscalPeriod period, CancellationToken cancellationToken)
    {
        var vendor = await db.Vendors
            .AsNoTracking()
            .Include(v => v.Deals)
            .FirstOrDefaultAsync(v => v.Id == vendorId, cancellationToken);

        if (vendor is null)
        {
            return null;
        }

        var lines = new List<DealFundingLine>();
        foreach (var deal in vendor.Deals.OrderBy(d => d.StartDate).ThenBy(d => d.DealNumber))
        {
            if (deal.Term.Intersect(period.Range) is not { } window)
            {
                continue;
            }

            switch (deal.Type)
            {
                case DealType.ScanDown:
                    // Aggregated in SQL so only one number comes back per deal, not every sales row.
                    var units = await db.SalesRecords
                        .Where(s => s.Sku == deal.Sku && s.SaleDate >= window.Start && s.SaleDate <= window.End)
                        .SumAsync(s => (int?)s.UnitsSold, cancellationToken) ?? 0;
                    lines.Add(ToLine(deal, window, units, FundingCalculator.ScanDownEarned(deal.RatePerUnit!.Value, units)));
                    break;

                case DealType.LumpSum:
                    lines.Add(ToLine(deal, window, null, FundingCalculator.LumpSumEarned(deal.LumpSumAmount!.Value, deal.Term, window)));
                    break;
            }
        }

        return new FundingSummaryResponse(
            vendor.Id,
            vendor.VendorNumber,
            vendor.Name,
            period.Label,
            period.Range.Start,
            period.Range.End,
            lines.Sum(l => l.Earned),
            lines);
    }

    private static DealFundingLine ToLine(Deal deal, DateRange window, int? units, decimal earned) =>
        new(deal.Id, deal.DealNumber, deal.Description, deal.Type, deal.Sku, window.Start, window.End, units, earned);
}
