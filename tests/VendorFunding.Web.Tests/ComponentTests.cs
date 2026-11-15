using Bunit;
using VendorFunding.Contracts;
using VendorFunding.Web;
using VendorFunding.Web.Components;

namespace VendorFunding.Web.Tests;

public class EarnedVsCollectedChartTests : BunitContext
{
    public EarnedVsCollectedChartTests()
    {
        // The chart measures its width through a small JS module; tests don't need a real one.
        JSInterop.SetupModule("./js/chart-size.js").Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Draws_one_column_per_non_zero_value_with_a_legend()
    {
        var cut = Render<EarnedVsCollectedChart>(p => p.Add(c => c.Data,
        [
            new MonthlyFunding("2026-07", 7_596.23m, 0m),        // collected is zero: no column drawn
            new MonthlyFunding("2026-08", 10_804.19m, 6_000m),
        ]));

        Assert.Equal(3, cut.FindAll("svg path").Count);
        Assert.Equal(["Earned", "Collected"], cut.FindAll(".legend li").Select(li => li.TextContent.Trim()));
        Assert.Contains("Jul 2026: earned $7,596.23, collected $0.00", cut.Find("svg").GetAttribute("aria-label"));
    }

    [Fact]
    public void Table_view_shows_every_value()
    {
        var cut = Render<EarnedVsCollectedChart>(p => p.Add(c => c.Data, [new MonthlyFunding("2026-08", 10_804.19m, 6_000m)]));

        cut.Find("button.link-button").Click();

        var cells = cut.FindAll("tbody td").Select(td => td.TextContent).ToList();
        Assert.Equal(["Aug 2026", "$10,804.19", "$6,000.00"], cells);
    }

    [Fact]
    public void Hovering_a_month_shows_its_values()
    {
        var cut = Render<EarnedVsCollectedChart>(p => p.Add(c => c.Data, [new MonthlyFunding("2026-08", 10_804.19m, 6_000m)]));

        cut.FindAll("rect[fill='transparent']")[0].MouseEnter();

        var tooltip = cut.Find(".tooltip").TextContent;
        Assert.Contains("Aug 2026", tooltip);
        Assert.Contains("$6,000.00", tooltip);
    }

    [Fact]
    public void Shows_a_message_when_there_is_no_data()
    {
        var cut = Render<EarnedVsCollectedChart>(p => p.Add(c => c.Data, []));

        Assert.Contains("Nothing has been accrued", cut.Markup);
        Assert.Empty(cut.FindAll("svg"));
    }
}

public class AgingChartTests : BunitContext
{
    [Fact]
    public void Draws_a_segment_for_each_non_zero_bucket_and_handles_vendors_who_owe_nothing()
    {
        var cut = Render<AgingChart>(p => p.Add(c => c.Vendors,
        [
            Vendor(1, "Palmetto Dairy", new AgingResponse(0m, 0m, 1_450.20m, 1_384.50m, 2_834.70m)),
            Vendor(2, "Gulf Coast Snacks", new AgingResponse(0m, 0m, 0m, 0m, 0m), unapplied: 25m),
        ]));

        var rows = cut.FindAll(".aging-row");
        Assert.Equal(2, rows[0].QuerySelectorAll(".aging-seg").Length);
        Assert.Contains("$2,834.70", rows[0].TextContent);
        Assert.Contains("Nothing owed · $25.00 credit", rows[1].TextContent);
        Assert.Contains("61–90 days $1,450.20", rows[0].QuerySelector(".aging-track")!.GetAttribute("aria-label"));
    }

    [Fact]
    public void Amounts_view_lists_every_bucket()
    {
        var cut = Render<AgingChart>(p => p.Add(c => c.Vendors,
            [Vendor(1, "Palmetto Dairy", new AgingResponse(10m, 20m, 30m, 40m, 100m))]));

        cut.Find("button.link-button").Click();

        Assert.Equal(["Palmetto Dairy", "$10.00", "$20.00", "$30.00", "$40.00", "$100.00"],
            cut.FindAll("tbody td").Select(td => td.TextContent.Trim()));
    }

    private static VendorReceivable Vendor(int id, string name, AgingResponse aging, decimal unapplied = 0m) =>
        new(id, $"V{id}", name, Earned: aging.Total, Collected: 0m, Balance: aging.Total - unapplied, aging, unapplied);
}

public class LoadingStateTests : BunitContext
{
    [Fact]
    public void Shows_the_error_with_a_retry_button()
    {
        var retried = false;
        var cut = Render<LoadingState>(p => p
            .Add(c => c.Error, "The API didn't respond in time.")
            .Add(c => c.OnRetry, () => retried = true));

        Assert.Contains("didn't respond", cut.Find("[role=alert]").TextContent);
        cut.Find("button").Click();
        Assert.True(retried);
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(950, "$950")]
    [InlineData(1_250, "$1.3K")]
    [InlineData(12_900, "$13K")]
    [InlineData(1_200_000, "$1.2M")]
    [InlineData(-4_000, "-$4K")]
    public void CompactMoney_shortens_large_amounts(decimal value, string expected) =>
        Assert.Equal(expected, Format.CompactMoney(value));

    [Fact]
    public void Month_formats_period_labels() =>
        Assert.Equal(("Jul 2026", "Jul"), (Format.Month("2026-07"), Format.Month("2026-07", shortForm: true)));
}
