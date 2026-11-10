using Microsoft.EntityFrameworkCore;
using VendorFunding.Domain;

namespace VendorFunding.Infrastructure.Persistence;

public class VendorFundingDbContext(DbContextOptions<VendorFundingDbContext> options) : DbContext(options)
{
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<Deal> Deals => Set<Deal>();
    public DbSet<SalesRecord> SalesRecords => Set<SalesRecord>();
    public DbSet<AccountingPeriod> AccountingPeriods => Set<AccountingPeriod>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardPostedJournalEntries();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardPostedJournalEntries();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Posted journal entries are the audit trail: they can be added to, never changed or deleted.</summary>
    private void GuardPostedJournalEntries()
    {
        var tampered = ChangeTracker.Entries()
            .Any(e => e.Entity is JournalEntry or JournalLine && e.State is EntityState.Modified or EntityState.Deleted);
        if (tampered)
        {
            throw new InvalidOperationException(
                "Posted journal entries can't be changed or deleted. Post an adjusting entry instead.");
        }
    }

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

        modelBuilder.Entity<AccountingPeriod>(period =>
        {
            period.Property(p => p.Status).HasConversion<string>().HasMaxLength(10);
            period.Property(p => p.RowVersion).IsRowVersion();
            period.HasIndex(p => p.StartDate).IsUnique();
            // The database itself guarantees there's never more than one open period.
            period.HasIndex(p => p.Status).IsUnique().HasFilter("[Status] = 'Open'").HasDatabaseName("IX_AccountingPeriods_SingleOpen");
        });

        modelBuilder.Entity<JournalEntry>(entry =>
        {
            entry.Property(e => e.Type).HasConversion<string>().HasMaxLength(30);
            entry.Property(e => e.Description).HasMaxLength(300);
            entry.Property(e => e.CreatedBy).HasMaxLength(100);
            entry.HasOne(e => e.AccountingPeriod).WithMany().HasForeignKey(e => e.AccountingPeriodId).OnDelete(DeleteBehavior.Restrict);
            entry.HasOne(e => e.Deal).WithMany().HasForeignKey(e => e.DealId).OnDelete(DeleteBehavior.Restrict);
            entry.HasMany(e => e.Lines).WithOne().HasForeignKey(l => l.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            entry.HasIndex(e => e.PostingDate);
        });

        modelBuilder.Entity<JournalLine>(line =>
        {
            line.Property(l => l.AccountNumber).HasMaxLength(10);
            line.Property(l => l.Debit).HasPrecision(18, 2);
            line.Property(l => l.Credit).HasPrecision(18, 2);
            line.HasIndex(l => l.AccountNumber).IncludeProperties(l => new { l.Debit, l.Credit });
            line.ToTable(t => t.HasCheckConstraint(
                "CK_JournalLines_OneSide",
                "[Debit] >= 0 AND [Credit] >= 0 AND ([Debit] = 0 OR [Credit] = 0) AND [Debit] + [Credit] > 0"));
        });
    }
}
