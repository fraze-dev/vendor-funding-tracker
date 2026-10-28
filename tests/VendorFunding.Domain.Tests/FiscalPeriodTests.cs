using VendorFunding.Domain;

namespace VendorFunding.Domain.Tests;

public class FiscalPeriodTests
{
    [Theory]
    [InlineData("2026-Q1", "2026-01-01", "2026-03-31")]
    [InlineData("2026-q3", "2026-07-01", "2026-09-30")]
    [InlineData("2026-Q4", "2026-10-01", "2026-12-31")]
    [InlineData("2028-02", "2028-02-01", "2028-02-29")] // leap year
    [InlineData("2026-12", "2026-12-01", "2026-12-31")]
    [InlineData("2026", "2026-01-01", "2026-12-31")]
    [InlineData(" 2026-09 ", "2026-09-01", "2026-09-30")]
    public void TryParse_accepts_years_quarters_and_months(string input, string expectedStart, string expectedEnd)
    {
        Assert.True(FiscalPeriod.TryParse(input, out var period));
        Assert.Equal(DateOnly.Parse(expectedStart), period.Range.Start);
        Assert.Equal(DateOnly.Parse(expectedEnd), period.Range.End);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-Q5")]
    [InlineData("2026-13")]
    [InlineData("2026-00")]
    [InlineData("Q3-2026")]
    [InlineData("26-Q3")]
    public void TryParse_rejects_invalid_input(string? input)
    {
        Assert.False(FiscalPeriod.TryParse(input, out _));
    }
}

public class DateRangeTests
{
    [Fact]
    public void Intersect_returns_overlap()
    {
        var a = new DateRange(new(2026, 7, 1), new(2026, 9, 30));
        var b = new DateRange(new(2026, 9, 15), new(2026, 10, 15));

        Assert.Equal(new DateRange(new(2026, 9, 15), new(2026, 9, 30)), a.Intersect(b));
    }

    [Fact]
    public void Intersect_returns_null_when_ranges_do_not_overlap()
    {
        var a = new DateRange(new(2026, 7, 1), new(2026, 7, 31));
        var b = new DateRange(new(2026, 8, 1), new(2026, 8, 31));

        Assert.Null(a.Intersect(b));
    }

    [Fact]
    public void Days_counts_both_endpoints()
    {
        Assert.Equal(1, new DateRange(new(2026, 7, 1), new(2026, 7, 1)).Days);
        Assert.Equal(92, new DateRange(new(2026, 7, 1), new(2026, 9, 30)).Days);
    }

    [Fact]
    public void Constructor_rejects_end_before_start()
    {
        Assert.Throws<ArgumentException>(() => new DateRange(new(2026, 7, 2), new(2026, 7, 1)));
    }
}
