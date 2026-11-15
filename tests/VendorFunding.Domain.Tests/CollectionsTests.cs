using VendorFunding.Domain;

namespace VendorFunding.Domain.Tests;

public class ReceivableAgingTests
{
    private static readonly DateOnly AsOf = new(2026, 11, 14);

    [Fact]
    public void Ages_open_amounts_by_posting_date()
    {
        var result = ReceivableAging.Calculate(
        [
            Charge("2026-07-31", 100m), // 106 days
            Charge("2026-08-31", 200m), // 75 days
            Charge("2026-09-30", 300m), // 45 days
            Charge("2026-10-31", 400m), // 14 days
        ], AsOf);

        Assert.Equal(new AgingBuckets(Current: 400m, Days31To60: 300m, Days61To90: 200m, Over90: 100m), result.Open);
        Assert.Equal(1_000m, result.Open.Total);
        Assert.Equal(0m, result.UnappliedCredit);
    }

    [Fact]
    public void Payments_settle_the_oldest_amounts_first()
    {
        var result = ReceivableAging.Calculate(
        [
            Charge("2026-07-31", 100m),
            Charge("2026-08-31", 200m),
            Charge("2026-10-31", 400m),
            Payment("2026-10-15", 250m), // clears July and half of August
        ], AsOf);

        Assert.Equal(new AgingBuckets(Current: 400m, Days31To60: 0m, Days61To90: 50m, Over90: 0m), result.Open);
    }

    [Fact]
    public void Day_boundaries_fall_in_the_earlier_bucket()
    {
        var result = ReceivableAging.Calculate(
        [
            Charge(AsOf.AddDays(-30), 1m),
            Charge(AsOf.AddDays(-31), 10m),
            Charge(AsOf.AddDays(-60), 100m),
            Charge(AsOf.AddDays(-61), 1_000m),
            Charge(AsOf.AddDays(-90), 10_000m),
            Charge(AsOf.AddDays(-91), 100_000m),
        ], AsOf);

        Assert.Equal(new AgingBuckets(1m, 110m, 11_000m, 100_000m), result.Open);
    }

    [Fact]
    public void Overpayments_are_reported_as_unapplied_credit()
    {
        var result = ReceivableAging.Calculate([Charge("2026-09-30", 100m), Payment("2026-10-10", 150m)], AsOf);

        Assert.Equal(0m, result.Open.Total);
        Assert.Equal(50m, result.UnappliedCredit);
    }

    [Fact]
    public void Ignores_transactions_after_the_as_of_date()
    {
        var result = ReceivableAging.Calculate([Charge("2026-09-30", 100m), Payment("2026-11-20", 100m)], AsOf);

        Assert.Equal(100m, result.Open.Days31To60);
    }

    private static ReceivableTransaction Charge(string date, decimal amount) => new(DateOnly.Parse(date), amount);
    private static ReceivableTransaction Charge(DateOnly date, decimal amount) => new(date, amount);
    private static ReceivableTransaction Payment(string date, decimal amount) => new(DateOnly.Parse(date), -amount);
}

public class CashReceiptTests
{
    private static readonly AccountingPeriod October = new() { Id = 4, StartDate = new(2026, 10, 1) };
    private static readonly Vendor Vendor = new() { Id = 3, VendorNumber = "V1003", Name = "Palmetto Dairy" };

    [Fact]
    public void Debits_cash_and_credits_the_receivable_on_the_received_date()
    {
        var entry = JournalEntry.ForCashReceipt(October, Vendor, new(2026, 10, 20), 1_250.50m, "ACH-123", "test", DateTimeOffset.UtcNow);

        Assert.Equal(JournalEntryType.CashReceipt, entry.Type);
        Assert.Equal(new DateOnly(2026, 10, 20), entry.PostingDate);
        Assert.Equal(3, entry.VendorId);
        Assert.Null(entry.DealId);
        Assert.Collection(entry.Lines,
            debit => Assert.Equal((ChartOfAccounts.Cash, 1_250.50m), (debit.AccountNumber, debit.Debit)),
            credit => Assert.Equal((ChartOfAccounts.VendorFundingReceivable, 1_250.50m), (credit.AccountNumber, credit.Credit)));
    }

    [Fact]
    public void Rejects_a_date_outside_the_period()
    {
        Assert.Throws<AccountingRuleException>(() =>
            JournalEntry.ForCashReceipt(October, Vendor, new(2026, 11, 2), 10m, "ACH-1", "test", DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Rejects_amounts_that_are_not_positive(decimal amount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JournalEntry.ForCashReceipt(October, Vendor, new(2026, 10, 2), amount, "ACH-1", "test", DateTimeOffset.UtcNow));
    }
}
