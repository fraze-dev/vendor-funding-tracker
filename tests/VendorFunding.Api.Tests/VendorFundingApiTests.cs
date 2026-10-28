using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VendorFunding.Api.Contracts;
using VendorFunding.Domain;

namespace VendorFunding.Api.Tests;

public class VendorFundingApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_check_reports_healthy()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_vendor_then_get_it()
    {
        var created = await CreateVendorAsync("Test Vendor");

        var fetched = await _client.GetFromJsonAsync<VendorResponse>($"/api/vendors/{created.Id}", Json);

        Assert.Equal(created, fetched);
    }

    [Fact]
    public async Task Duplicate_vendor_number_returns_conflict()
    {
        var created = await CreateVendorAsync("Original");

        var response = await _client.PostAsJsonAsync("/api/vendors", new CreateVendorRequest(created.VendorNumber, "Copy"), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_deal_returns_validation_errors()
    {
        var vendor = await CreateVendorAsync("Validation Vendor");
        var deal = new CreateDealRequest(UniqueNumber("D"), "Missing sku and rate", DealType.ScanDown,
            new(2026, 8, 1), new(2026, 7, 1), Sku: null, RatePerUnit: null, LumpSumAmount: null);

        var response = await _client.PostAsJsonAsync($"/api/vendors/{vendor.Id}/deals", deal, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("EndDate", body);
        Assert.Contains("Sku", body);
        Assert.Contains("RatePerUnit", body);
    }

    [Fact]
    public async Task Funding_summary_combines_scan_down_and_lump_sum_deals()
    {
        var vendor = await CreateVendorAsync("Funding Vendor");
        var sku = UniqueNumber("SKU");

        // $0.50/unit scan-down for August.
        await CreateDealAsync(vendor.Id, new CreateDealRequest(UniqueNumber("D"), "August promo", DealType.ScanDown,
            new(2026, 8, 1), new(2026, 8, 31), sku, RatePerUnit: 0.50m, LumpSumAmount: null));

        // $9,200 lump sum over 92 days (Jul 1 - Sep 30) = $100/day.
        await CreateDealAsync(vendor.Id, new CreateDealRequest(UniqueNumber("D"), "Q3 endcap", DealType.LumpSum,
            new(2026, 7, 1), new(2026, 9, 30), Sku: null, RatePerUnit: null, LumpSumAmount: 9_200m));

        var sales = await _client.PostAsJsonAsync("/api/sales", new[]
        {
            new SalesRecordRequest("S0001", sku, new(2026, 8, 5), 100, 499m),
            new SalesRecordRequest("S0002", sku, new(2026, 8, 20), 60, 299.40m),
            new SalesRecordRequest("S0001", sku, new(2026, 9, 2), 500, 2_495m),        // after the deal ends
            new SalesRecordRequest("S0001", "OTHER-SKU", new(2026, 8, 5), 999, 10m),  // different item
        }, Json);
        sales.EnsureSuccessStatusCode();

        var summary = await _client.GetFromJsonAsync<FundingSummaryResponse>(
            $"/api/vendors/{vendor.Id}/funding-summary?period=2026-08", Json);

        Assert.NotNull(summary);
        Assert.Equal(new DateOnly(2026, 8, 1), summary.PeriodStart);
        Assert.Equal(new DateOnly(2026, 8, 31), summary.PeriodEnd);

        var scanDown = Assert.Single(summary.Deals, d => d.Type == DealType.ScanDown);
        Assert.Equal(160, scanDown.UnitsSold);
        Assert.Equal(80.00m, scanDown.Earned);

        var lumpSum = Assert.Single(summary.Deals, d => d.Type == DealType.LumpSum);
        Assert.Equal(3_100.00m, lumpSum.Earned); // 31 days x $100

        Assert.Equal(3_180.00m, summary.TotalEarned);
    }

    [Fact]
    public async Task Funding_summary_rejects_bad_period()
    {
        var vendor = await CreateVendorAsync("Period Vendor");

        var response = await _client.GetAsync($"/api/vendors/{vendor.Id}/funding-summary?period=Q3");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Funding_summary_for_unknown_vendor_returns_not_found()
    {
        var response = await _client.GetAsync("/api/vendors/999999/funding-summary?period=2026-Q3");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<VendorResponse> CreateVendorAsync(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/vendors", new CreateVendorRequest(UniqueNumber("V"), name), Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VendorResponse>(Json))!;
    }

    private async Task CreateDealAsync(int vendorId, CreateDealRequest deal)
    {
        var response = await _client.PostAsJsonAsync($"/api/vendors/{vendorId}/deals", deal, Json);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // Tests share one database, so give every record a unique business key.
    private static string UniqueNumber(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..12];
}
