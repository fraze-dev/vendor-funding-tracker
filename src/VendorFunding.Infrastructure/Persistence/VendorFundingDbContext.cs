using Microsoft.EntityFrameworkCore;
using VendorFunding.Domain;

namespace VendorFunding.Infrastructure.Persistence;

public class VendorFundingDbContext(DbContextOptions<VendorFundingDbContext> options) : DbContext(options)
{
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<Deal> Deals => Set<Deal>();
    public DbSet<SalesRecord> SalesRecords => Set<SalesRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Vendor>(vendor =>
        {
            vendor.Property(v => v.VendorNumber).HasMaxLength(20);
            vendor.Property(v => v.Name).HasMaxLength(200);
            vendor.HasIndex(v => v.VendorNumber).IsUnique();
        });

        modelBuilder.Entity<Deal>(deal =>
        {
            deal.Property(d => d.DealNumber).HasMaxLength(20);
            deal.Property(d => d.Description).HasMaxLength(200);
            deal.Property(d => d.Type).HasConversion<string>().HasMaxLength(20);
            deal.Property(d => d.Sku).HasMaxLength(20);
            deal.Property(d => d.RatePerUnit).HasPrecision(18, 4);
            deal.Property(d => d.LumpSumAmount).HasPrecision(18, 2);
            deal.HasIndex(d => d.DealNumber).IsUnique();
            deal.ToTable(t => t.HasCheckConstraint("CK_Deals_EndDate", "[EndDate] >= [StartDate]"));
        });

        modelBuilder.Entity<SalesRecord>(sale =>
        {
            sale.Property(s => s.StoreNumber).HasMaxLength(10);
            sale.Property(s => s.Sku).HasMaxLength(20);
            sale.Property(s => s.NetSales).HasPrecision(18, 2);
            // Funding calculations filter by item and date range.
            sale.HasIndex(s => new { s.Sku, s.SaleDate }).IncludeProperties(s => s.UnitsSold);
        });
    }
}
