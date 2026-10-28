using VendorFunding.Domain;

namespace VendorFunding.Api.Contracts;

public record VendorResponse(int Id, string VendorNumber, string Name, bool IsActive)
{
    public static VendorResponse From(Vendor v) => new(v.Id, v.VendorNumber, v.Name, v.IsActive);
}

public record DealResponse(
    int Id,
    int VendorId,
    string DealNumber,
    string Description,
    DealType Type,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Sku,
    decimal? RatePerUnit,
    decimal? LumpSumAmount)
{
    public static DealResponse From(Deal d) => new(
        d.Id, d.VendorId, d.DealNumber, d.Description, d.Type,
        d.StartDate, d.EndDate, d.Sku, d.RatePerUnit, d.LumpSumAmount);
}

public record SalesBatchResponse(int RecordsInserted);

public record FundingSummaryResponse(
    int VendorId,
    string VendorNumber,
    string VendorName,
    string Period,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal TotalEarned,
    IReadOnlyList<DealFundingLine> Deals);

/// <summary>Funding earned from one deal within the requested period.</summary>
public record DealFundingLine(
    int DealId,
    string DealNumber,
    string Description,
    DealType Type,
    string? Sku,
    DateOnly EarnedFrom,
    DateOnly EarnedThrough,
    int? UnitsSold,
    decimal Earned);
