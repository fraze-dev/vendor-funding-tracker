using Microsoft.EntityFrameworkCore;
using VendorFunding.Domain;

namespace VendorFunding.Infrastructure.Persistence;

/// <summary>Loads fictional vendors, deals and daily sales so the API has something to show.</summary>
public static class DemoDataSeeder
{
    private static readonly DateOnly SalesStart = new(2026, 7, 1);
    private static readonly DateOnly SalesEnd = new(2026, 10, 31);

    private static readonly (string Sku, decimal Price)[] Items =
    [
        ("100245", 4.99m),  // Orange juice, 52 oz
        ("100246", 6.49m),  // Grapefruit juice, 89 oz
        ("200810", 3.79m),  // Potato chips, family size
        ("200811", 2.99m),  // Tortilla chips
        ("300112", 3.49m),  // Whole milk, gallon
        ("300140", 5.29m),  // Greek yogurt, 4-pack
    ];

    public static async Task SeedAsync(VendorFundingDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Vendors.AnyAsync(cancellationToken))
        {
            return;
        }

        db.Vendors.AddRange(
            new Vendor
            {
                VendorNumber = "V1001",
                Name = "Sunshine Citrus Co.",
                Deals =
                [
                    ScanDown("D-24001", "Back-to-school OJ promo", "100245", 0.50m, new(2026, 8, 1), new(2026, 8, 31)),
                    ScanDown("D-24002", "Grapefruit fall feature", "100246", 0.75m, new(2026, 9, 15), new(2026, 10, 15)),
                    LumpSum("D-24003", "Q3 endcap placement", 12_000m, new(2026, 7, 1), new(2026, 9, 30)),
                ],
            },
            new Vendor
            {
                VendorNumber = "V1002",
                Name = "Gulf Coast Snacks",
                Deals =
                [
                    ScanDown("D-24010", "Football season chips", "200810", 0.40m, new(2026, 9, 1), new(2026, 11, 30)),
                    ScanDown("D-24011", "Tortilla chip BOGO support", "200811", 0.25m, new(2026, 7, 4), new(2026, 7, 31)),
                    LumpSum("D-24012", "New item slotting allowance", 5_000m, new(2026, 8, 15), new(2026, 12, 31)),
                ],
            },
            new Vendor
            {
                VendorNumber = "V1003",
                Name = "Palmetto Dairy",
                Deals =
                [
                    ScanDown("D-24020", "Milk price support", "300112", 0.15m, new(2026, 7, 1), new(2026, 12, 31)),
                    ScanDown("D-24021", "Yogurt circular ad", "300140", 0.60m, new(2026, 10, 1), new(2026, 10, 14)),
                ],
            });

        // Fixed seed so every environment gets identical demo numbers.
        var random = new Random(20260701);
        var stores = Enumerable.Range(1, 12).Select(n => $"S{n:D4}").ToArray();
        for (var date = SalesStart; date <= SalesEnd; date = date.AddDays(1))
        {
            var weekendBoost = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1.3 : 1.0;
            foreach (var store in stores)
            {
                foreach (var (sku, price) in Items)
                {
                    var units = (int)(random.Next(8, 40) * weekendBoost);
                    db.SalesRecords.Add(new SalesRecord
                    {
                        StoreNumber = store,
                        Sku = sku,
                        SaleDate = date,
                        UnitsSold = units,
                        NetSales = units * price,
                    });
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static Deal ScanDown(string number, string description, string sku, decimal rate, DateOnly start, DateOnly end) =>
        new()
        {
            DealNumber = number,
            Description = description,
            Type = DealType.ScanDown,
            Sku = sku,
            RatePerUnit = rate,
            StartDate = start,
            EndDate = end,
        };

    private static Deal LumpSum(string number, string description, decimal amount, DateOnly start, DateOnly end) =>
        new()
        {
            DealNumber = number,
            Description = description,
            Type = DealType.LumpSum,
            LumpSumAmount = amount,
            StartDate = start,
            EndDate = end,
        };
}
