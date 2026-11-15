using VendorFunding.Domain;

namespace VendorFunding.Contracts;

public record CreateVendorRequest(string VendorNumber, string Name);

public record CreateDealRequest(
    string DealNumber,
    string Description,
    DealType Type,
    DateOnly StartDate,
    DateOnly EndDate,
    string? Sku,
    decimal? RatePerUnit,
    decimal? LumpSumAmount);

public record SalesRecordRequest(string StoreNumber, string Sku, DateOnly SaleDate, int UnitsSold, decimal NetSales);

/// <param name="Reference">The vendor's payment reference, such as an ACH trace or check number.</param>
public record RecordPaymentRequest(string Reference, DateOnly ReceivedDate, decimal Amount, string? Memo);
