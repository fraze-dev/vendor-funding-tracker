using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using VendorFunding.Api.Contracts;
using VendorFunding.Domain;
using VendorFunding.Infrastructure.Persistence;

namespace VendorFunding.Api.Tests;

/// <summary>
/// Accounting periods are global state (one open period at a time), so this class gets its own
/// SQL Server container and walks through month-end in a single ordered scenario.
/// </summary>
public class AccountingApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Month_end_lifecycle_books_true_ups_and_prior_period_adjustments()
    {
        // Two deals running July 1 - August 31.
        var vendor = await PostAsync<VendorResponse>("/api/vendors", new CreateVendorRequest("V-ACCT", "Accounting Test Vendor"));
        const string sku = "ACCT-SKU";
        await PostAsync<DealResponse>($"/api/vendors/{vendor.Id}/deals", new CreateDealRequest(
            "D-ACCT-SD", "Scan-down", DealType.ScanDown, new(2026, 7, 1), new(2026, 8, 31), sku, 0.50m, null));
        await PostAsync<DealResponse>($"/api/vendors/{vendor.Id}/deals", new CreateDealRequest(
            "D-ACCT-LS", "Lump sum", DealType.LumpSum, new(2026, 7, 1), new(2026, 8, 31), null, null, 3_100m)); // $50/day

        await PostSalesAsync(sku, new(2026, 7, 10), 100);
        await PostSalesAsync(sku, new(2026, 8, 5), 40);

        // July: $50 scan-down (100 units) + $1,550 lump sum (31 days).
        var july = await RunAccrualAsync("2026-07");
        Assert.Equal(2, july.EntriesPosted);
        Assert.Equal(1_600m, july.NetAmountPosted);
        Assert.All(july.Entries, e => Assert.Equal(JournalEntryType.Accrual, e.Type));
        Assert.All(july.Entries, e => Assert.Equal(e.Lines.Sum(l => l.Debit), e.Lines.Sum(l => l.Credit)));

        // Re-running with nothing new posts nothing.
        Assert.Equal(0, (await RunAccrualAsync("2026-07")).EntriesPosted);

        // A new July sale while July is still open is trued up as a July accrual.
        await PostSalesAsync(sku, new(2026, 7, 20), 10);
        var rerun = await RunAccrualAsync("2026-07");
        Assert.Equal(1, rerun.EntriesPosted);
        Assert.Equal(5m, rerun.NetAmountPosted);

        // Periods go in order: August can't open while July is open.
        await AssertConflictAsync(_client.PostAsync("/api/accruals/2026-08/run", null), "2026-07 is still open");

        await PostAsync<PeriodResponse>("/api/periods/2026-07/close", null);
        await AssertConflictAsync(_client.PostAsync("/api/accruals/2026-07/run", null), "2026-07 is closed");

        // A late July sale arrives after July closed. August's run books it as a prior-period adjustment.
        await PostSalesAsync(sku, new(2026, 7, 25), 20);
        var august = await RunAccrualAsync("2026-08");
        Assert.Equal(3, august.EntriesPosted);
        Assert.Equal(1_580m, august.NetAmountPosted);
        var adjustment = Assert.Single(august.Entries, e => e.Type == JournalEntryType.PriorPeriodAdjustment);
        Assert.Equal(10m, adjustment.Lines.Sum(l => l.Debit)); // 20 late units x $0.50
        Assert.Equal(new DateOnly(2026, 8, 31), adjustment.PostingDate);

        // The ledger balances, and agrees with the funding calculation for the whole deal term.
        var trialBalance = await _client.GetFromJsonAsync<TrialBalanceResponse>("/api/trial-balance?period=2026-08", Json);
        Assert.NotNull(trialBalance);
        Assert.True(trialBalance.IsBalanced);
        Assert.Equal(3_185m, trialBalance.Accounts.Single(a => a.Account == ChartOfAccounts.VendorFundingReceivable).Balance);
        Assert.Equal(-3_185m, trialBalance.Accounts.Single(a => a.Account == ChartOfAccounts.CogsVendorAllowances).Balance);

        var funding = await _client.GetFromJsonAsync<FundingSummaryResponse>($"/api/vendors/{vendor.Id}/funding-summary?period=2026", Json);
        Assert.Equal(3_185m, funding!.TotalEarned);

        var augustEntries = await _client.GetFromJsonAsync<List<JournalEntryResponse>>("/api/journal-entries?period=2026-08", Json);
        Assert.Equal(3, augustEntries!.Count);

        // Close August; September is next, so October is refused.
        await PostAsync<PeriodResponse>("/api/periods/2026-08/close", null);
        await AssertConflictAsync(_client.PostAsync("/api/accruals/2026-10/run", null), "next period is 2026-09");

        var periods = (await _client.GetFromJsonAsync<List<PeriodResponse>>("/api/periods", Json))!;
        Assert.Equal(["2026-07", "2026-08"], periods.Select(p => p.Period));
        Assert.All(periods, p => Assert.Equal(PeriodStatus.Closed, p.Status));
    }

    [Fact]
    public async Task Posted_journal_entries_cannot_be_changed_or_deleted()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VendorFundingDbContext>();
        var entry = new JournalEntry { Id = 1, Description = "Posted entry", CreatedBy = "test" };
        db.Attach(entry);

        entry.Description = "Edited";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        db.Remove(entry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData("/api/accruals/2026-Q3/run")]
    [InlineData("/api/periods/2026/close")]
    public async Task Periods_must_be_months(string url)
    {
        var response = await _client.PostAsync(url, null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<AccrualRunResponse> RunAccrualAsync(string period) =>
        await PostAsync<AccrualRunResponse>($"/api/accruals/{period}/run", null);

    private async Task PostSalesAsync(string sku, DateOnly date, int units) =>
        await PostAsync<SalesBatchResponse>("/api/sales", new[] { new SalesRecordRequest("S0001", sku, date, units, units * 4.99m) });

    private async Task<T> PostAsync<T>(string url, object? body)
    {
        var response = body is null ? await _client.PostAsync(url, null) : await _client.PostAsJsonAsync(url, body, Json);
        Assert.True(response.IsSuccessStatusCode, $"POST {url} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private static async Task AssertConflictAsync(Task<HttpResponseMessage> request, string expectedMessage)
    {
        var response = await request;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(expectedMessage, await response.Content.ReadAsStringAsync());
    }
}
