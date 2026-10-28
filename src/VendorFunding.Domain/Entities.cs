namespace VendorFunding.Domain;

public enum DealType
{
    /// <summary>Vendor pays a fixed rate for every unit the retailer sells during the deal.</summary>
    ScanDown,

    /// <summary>Vendor pays a fixed amount for the deal, earned evenly over the deal term.</summary>
    LumpSum,
}

public class Vendor
{
    public int Id { get; set; }
    public required string VendorNumber { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public List<Deal> Deals { get; set; } = [];
}

public class Deal
{
    public int Id { get; set; }
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    public required string DealNumber { get; set; }
    public required string Description { get; set; }
    public DealType Type { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }

    /// <summary>Item the deal applies to. Required for scan-down deals.</summary>
    public string? Sku { get; set; }

    /// <summary>Amount earned per unit sold. Scan-down deals only.</summary>
    public decimal? RatePerUnit { get; set; }

    /// <summary>Total contract amount. Lump-sum deals only.</summary>
    public decimal? LumpSumAmount { get; set; }

    public DateRange Term => new(StartDate, EndDate);
}

/// <summary>Daily units sold for one item at one store.</summary>
public class SalesRecord
{
    public long Id { get; set; }
    public required string StoreNumber { get; set; }
    public required string Sku { get; set; }
    public DateOnly SaleDate { get; set; }

    /// <summary>Negative when returns exceed sales for the day.</summary>
    public int UnitsSold { get; set; }

    public decimal NetSales { get; set; }
}
