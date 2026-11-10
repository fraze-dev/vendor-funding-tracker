using VendorFunding.Domain;

namespace VendorFunding.Domain.Tests;

public class JournalEntryTests
{
    private static readonly AccountingPeriod August = new() { Id = 2, StartDate = new(2026, 8, 1) };
    private static readonly Deal Deal = new() { Id = 7, DealNumber = "D-1", Description = "OJ promo" };
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Positive_amount_debits_receivable_and_credits_cogs()
    {
        var entry = JournalEntry.ForDealFunding(August, Deal, JournalEntryType.Accrual, 125.50m, "test", Now);

        Assert.Collection(entry.Lines,
            debit => Assert.Equal((ChartOfAccounts.VendorFundingReceivable, 125.50m, 0m), (debit.AccountNumber, debit.Debit, debit.Credit)),
            credit => Assert.Equal((ChartOfAccounts.CogsVendorAllowances, 0m, 125.50m), (credit.AccountNumber, credit.Debit, credit.Credit)));
        Assert.Equal(new DateOnly(2026, 8, 31), entry.PostingDate);
        Assert.Equal(7, entry.DealId);
        Assert.StartsWith("Funding accrual: D-1", entry.Description);
    }

    [Fact]
    public void Negative_amount_reverses_the_entry()
    {
        var entry = JournalEntry.ForDealFunding(August, Deal, JournalEntryType.PriorPeriodAdjustment, -4.25m, "test", Now);

        Assert.Collection(entry.Lines,
            debit => Assert.Equal((ChartOfAccounts.CogsVendorAllowances, 4.25m), (debit.AccountNumber, debit.Debit)),
            credit => Assert.Equal((ChartOfAccounts.VendorFundingReceivable, 4.25m), (credit.AccountNumber, credit.Credit)));
        Assert.StartsWith("Prior-period adjustment", entry.Description);
    }

    [Fact]
    public void Zero_amount_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JournalEntry.ForDealFunding(August, Deal, JournalEntryType.Accrual, 0m, "test", Now));
    }

    [Fact]
    public void EnsureBalanced_rejects_unequal_debits_and_credits()
    {
        var ex = Assert.Throws<AccountingRuleException>(() => JournalEntry.EnsureBalanced(
        [
            new JournalLine { AccountNumber = "1310", Debit = 100m },
            new JournalLine { AccountNumber = "5010", Credit = 99.99m },
        ]));
        Assert.Contains("must equal", ex.Message);
    }

    [Theory]
    [InlineData(10, 10)] // both sides
    [InlineData(0, 0)]   // neither side
    [InlineData(-5, 0)]  // negative
    public void EnsureBalanced_rejects_lines_that_are_not_one_positive_side(decimal debit, decimal credit)
    {
        Assert.Throws<AccountingRuleException>(() => JournalEntry.EnsureBalanced(
        [
            new JournalLine { AccountNumber = "1310", Debit = debit, Credit = credit },
            new JournalLine { AccountNumber = "5010", Credit = 10m },
        ]));
    }

    [Fact]
    public void EnsureBalanced_requires_at_least_two_lines()
    {
        Assert.Throws<AccountingRuleException>(() =>
            JournalEntry.EnsureBalanced([new JournalLine { AccountNumber = "1310", Debit = 1m }]));
    }
}

public class AccrualCalculatorTests
{
    [Fact]
    public void First_run_books_the_month_earnings()
    {
        var trueUp = AccrualCalculator.Calculate(
            earnedThroughPriorPeriods: 0m, earnedThroughPeriod: 500m, bookedInPriorPeriods: 0m, bookedInPeriod: 0m);

        Assert.Equal(new AccrualTrueUp(0m, 500m), trueUp);
    }

    [Fact]
    public void Re_running_with_no_new_activity_books_nothing()
    {
        var trueUp = AccrualCalculator.Calculate(
            earnedThroughPriorPeriods: 300m, earnedThroughPeriod: 500m, bookedInPriorPeriods: 300m, bookedInPeriod: 200m);

        Assert.Equal(new AccrualTrueUp(0m, 0m), trueUp);
    }

    [Fact]
    public void Late_sales_for_a_closed_month_become_a_prior_period_adjustment()
    {
        // 300 was booked for July; late July sales raised July's earnings to 310.
        var trueUp = AccrualCalculator.Calculate(
            earnedThroughPriorPeriods: 310m, earnedThroughPeriod: 510m, bookedInPriorPeriods: 300m, bookedInPeriod: 0m);

        Assert.Equal(new AccrualTrueUp(10m, 200m), trueUp);
    }

    [Fact]
    public void Returns_produce_negative_true_ups()
    {
        var trueUp = AccrualCalculator.Calculate(
            earnedThroughPriorPeriods: 0m, earnedThroughPeriod: 180m, bookedInPriorPeriods: 0m, bookedInPeriod: 200m);

        Assert.Equal(new AccrualTrueUp(0m, -20m), trueUp);
    }
}

public class EarnedThroughTests
{
    private static readonly Deal ScanDown = new()
    {
        DealNumber = "D-1", Description = "x", Type = DealType.ScanDown, RatePerUnit = 0.50m,
        StartDate = new(2026, 7, 1), EndDate = new(2026, 8, 31),
    };

    private static readonly Deal LumpSum = new()
    {
        DealNumber = "D-2", Description = "x", Type = DealType.LumpSum, LumpSumAmount = 3_100m,
        StartDate = new(2026, 7, 1), EndDate = new(2026, 8, 31), // 62 days = $50/day
    };

    [Fact]
    public void Nothing_is_earned_before_the_deal_starts()
    {
        Assert.Equal(0m, FundingCalculator.EarnedThrough(ScanDown, new(2026, 6, 30), unitsSoldThroughDate: 999));
        Assert.Equal(0m, FundingCalculator.EarnedThrough(LumpSum, new(2026, 6, 30), unitsSoldThroughDate: 0));
    }

    [Fact]
    public void Scan_down_uses_units_sold_to_date()
    {
        Assert.Equal(55m, FundingCalculator.EarnedThrough(ScanDown, new(2026, 7, 31), unitsSoldThroughDate: 110));
    }

    [Fact]
    public void Lump_sum_is_earned_per_day_and_caps_at_the_contract_amount()
    {
        Assert.Equal(1_550m, FundingCalculator.EarnedThrough(LumpSum, new(2026, 7, 31), 0));
        Assert.Equal(3_100m, FundingCalculator.EarnedThrough(LumpSum, new(2026, 12, 31), 0));
    }
}
