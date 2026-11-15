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
    public DbSet<SalesImport> SalesImports => Set<SalesImport>();
    public DbSet<SalesImportBatch> SalesImportBatches => Set<SalesImportBatch>();
    public DbSet<VendorPayment> VendorPayments => Set<VendorPayment>();

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

    /// <summary>
    /// Posted journal entries and the payments behind them are the audit trail: they can be added to,
    /// never changed or deleted.
    /// </summary>
    private void GuardPostedJournalEntries()
    {
        var tampered = ChangeTracker.Entries()
            .Any(e => e.Entity is JournalEntry or JournalLine or VendorPayment && e.State is EntityState.Modified or EntityState.Deleted);
        if (tampered)
        {
            throw new InvalidOperationException(
                "Posted journal entries and payments can't be changed or deleted. Post an adjusting entry instead.");
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
            sale.HasOne<SalesImport>().WithMany().HasForeignKey(s => s.ImportId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VendorPayment>(payment =>
        {
            payment.Property(p => p.Reference).HasMaxLength(30);
            payment.Property(p => p.Memo).HasMaxLength(200);
            payment.Property(p => p.Amount).HasPrecision(18, 2);
            // The same payment can't be recorded twice.
            payment.HasIndex(p => new { p.VendorId, p.Reference }).IsUnique();
            payment.HasOne(p => p.Vendor).WithMany().HasForeignKey(p => p.VendorId).OnDelete(DeleteBehavior.Restrict);
            payment.HasOne(p => p.JournalEntry).WithOne().HasForeignKey<VendorPayment>(p => p.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            payment.ToTable(t => t.HasCheckConstraint("CK_VendorPayments_Amount", "[Amount] > 0"));
        });

        modelBuilder.Entity<SalesImport>(import =>
        {
            import.Property(i => i.FileName).HasMaxLength(260);
            import.Property(i => i.BlobName).HasMaxLength(400);
            import.Property(i => i.ContentHash).HasMaxLength(64).IsFixedLength();
            import.Property(i => i.Status).HasConversion<string>().HasMaxLength(30);
            import.Property(i => i.RejectsBlobName).HasMaxLength(400);
            import.Property(i => i.Error).HasMaxLength(1000);
            import.HasIndex(i => i.BlobName).IsUnique();
            import.HasIndex(i => i.ContentHash);
            import.HasIndex(i => i.ReceivedAt);
        });

        modelBuilder.Entity<SalesImportBatch>(batch =>
        {
            // The key is what makes loading a batch idempotent.
            batch.HasKey(b => new { b.ImportId, b.BatchNumber });
            batch.HasOne<SalesImport>().WithMany().HasForeignKey(b => b.ImportId).OnDelete(DeleteBehavior.Restrict);
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
            entry.HasOne<Vendor>().WithMany().HasForeignKey(e => e.VendorId).OnDelete(DeleteBehavior.Restrict);
            // Receivable aging reads each vendor's entries in date order.
            entry.HasIndex(e => new { e.VendorId, e.PostingDate });
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
