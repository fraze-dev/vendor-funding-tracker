using VendorFunding.Domain;

namespace VendorFunding.Domain.Tests;

public class FundingCalculatorTests
{
    [Theory]
    [InlineData(0.50, 1_000, 500.00)]
    [InlineData(0.125, 3, 0.38)]   // 0.375 rounds half away from zero
    [InlineData(0.40, -10, -4.00)] // net returns reduce funding
    [InlineData(0.75, 0, 0.00)]
    public void ScanDownEarned_multiplies_rate_by_units_and_rounds_to_cents(decimal rate, int units, decimal expected)
    {
        Assert.Equal(expected, FundingCalculator.ScanDownEarned(rate, units));
    }

    [Fact]
    public void LumpSumEarned_returns_full_amount_when_window_covers_term()
    {
        var term = Range("2026-07-01", "2026-09-30");

        Assert.Equal(12_000m, FundingCalculator.LumpSumEarned(12_000m, term, Range("2026-01-01", "2026-12-31")));
    }

    [Fact]
    public void LumpSumEarned_prorates_by_day()
    {
        // 92-day term; July has 31 days.
        var term = Range("2026-07-01", "2026-09-30");

        var july = FundingCalculator.LumpSumEarned(9_200m, term, Range("2026-07-01", "2026-07-31"));

        Assert.Equal(3_100m, july);
    }

    [Fact]
    public void LumpSumEarned_monthly_amounts_add_up_to_exact_contract_amount()
    {
        // $1,000 over 7 days doesn't divide evenly into cents; per-day rounding would drift.
        var term = Range("2026-03-01", "2026-03-07");
        var earnedPerDay = Enumerable.Range(1, 7)
            .Select(day => new DateOnly(2026, 3, day))
            .Select(date => FundingCalculator.LumpSumEarned(1_000m, term, new DateRange(date, date)))
            .ToList();

        Assert.Equal(1_000m, earnedPerDay.Sum());
        Assert.All(earnedPerDay, amount => Assert.InRange(amount, 142.85m, 142.86m));
    }

    [Fact]
    public void LumpSumEarned_is_zero_outside_term()
    {
        var term = Range("2026-07-01", "2026-09-30");

        Assert.Equal(0m, FundingCalculator.LumpSumEarned(5_000m, term, Range("2026-10-01", "2026-10-31")));
    }

    private static DateRange Range(string start, string end) => new(DateOnly.Parse(start), DateOnly.Parse(end));
}
