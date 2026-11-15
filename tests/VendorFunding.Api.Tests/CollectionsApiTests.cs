using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using VendorFunding.Contracts;
using VendorFunding.Domain;
using VendorFunding.Infrastructure.Persistence;

namespace VendorFunding.Api.Tests;

/// <summary>Accounting periods are global state, so this class gets its own SQL Server container.</summary>
public class CollectionsApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Payments_reduce_the_receivable_and_age_oldest_first()
    {
        // $3,100 lump sum over Jul 1 - Aug 31 = $1,550 booked at the end of each month.
        var vendor = await PostAsync<VendorResponse>("/api/vendors", new CreateVendorRequest("V-COLL", "Collections Vendor"));
        await PostAsync<DealResponse>($"/api/vendors/{vendor.Id}/deals", new CreateDealRequest(
            "D-COLL", "Endcap", DealType.LumpSum, new(2026, 7, 1), new(2026, 8, 31), null, null, 3_100m));
        await PostAsync<AccrualRunResponse>("/api/accruals/2026-07/run", null);
        await PostAsync<PeriodResponse>("/api/periods/2026-07/close", null);
        await PostAsync<AccrualRunResponse>("/api/accruals/2026-08/run", null);

        var payment = await PostAsync<VendorPaymentResponse>($"/api/vendors/{vendor.Id}/payments",
            new RecordPaymentRequest("ACH-1001", new(2026, 8, 20), 1_000m, "Partial payment"));
        Assert.Equal(1_000m, payment.Amount);
        Assert.StartsWith("JE-", payment.EntryNumber);

        var entry = (await _client.GetFromJsonAsync<List<JournalEntryResponse>>("/api/journal-entries?period=2026-08", Json))!
            .Single(e => e.Type == JournalEntryType.CashReceipt);
        Assert.Equal(new DateOnly(2026, 8, 20), entry.PostingDate);
        Assert.Equal((ChartOfAccounts.Cash, 1_000m), (entry.Lines[0].Account, entry.Lines[0].Debit));
        Assert.Equal((ChartOfAccounts.VendorFundingReceivable, 1_000m), (entry.Lines[1].Account, entry.Lines[1].Credit));

        // As of Sep 30: July's $1,550 is 61 days old, August's is 30. The payment settles July first.
        var dashboard = (await _client.GetFromJsonAsync<DashboardResponse>("/api/dashboard?asOf=2026-09-30", Json))!;
        Assert.Equal("2026-08", dashboard.OpenPeriod);
        Assert.Equal(new DashboardTotals(Earned: 3_100m, Collected: 1_000m, OpenReceivable: 2_100m, Over60Days: 550m, UnappliedCredit: 0m),
            dashboard.Totals);
        var line = Assert.Single(dashboard.Vendors);
        Assert.Equal(2_100m, line.Balance);
        Assert.Equal(new AgingResponse(Current: 1_550m, Days31To60: 0m, Days61To90: 550m, Over90: 0m, Total: 2_100m), line.Aging);
        Assert.Equal(
            [new MonthlyFunding("2026-07", 1_550m, 0m), new MonthlyFunding("2026-08", 1_550m, 1_000m)],
            dashboard.Monthly);

        // The ledger agrees: cash in, receivable down by the same amount, still balanced.
        var trialBalance = (await _client.GetFromJsonAsync<TrialBalanceResponse>("/api/trial-balance?period=2026-08", Json))!;
        Assert.True(trialBalance.IsBalanced);
        Assert.Equal(1_000m, trialBalance.Accounts.Single(a => a.Account == ChartOfAccounts.Cash).Balance);
        Assert.Equal(2_100m, trialBalance.Accounts.Single(a => a.Account == ChartOfAccounts.VendorFundingReceivable).Balance);

        var payments = await _client.GetFromJsonAsync<List<VendorPaymentResponse>>($"/api/vendors/{vendor.Id}/payments", Json);
        Assert.Equal("ACH-1001", Assert.Single(payments!).Reference);

        // Rules: the same reference can't be recorded twice, and payments go to the open period only.
        await AssertStatusAsync(HttpStatusCode.Conflict, $"/api/vendors/{vendor.Id}/payments",
            new RecordPaymentRequest("ACH-1001", new(2026, 8, 21), 5m, null), "already recorded");
        await AssertStatusAsync(HttpStatusCode.Conflict, $"/api/vendors/{vendor.Id}/payments",
            new RecordPaymentRequest("ACH-1002", new(2026, 7, 15), 5m, null), "open period, 2026-08");
        await AssertStatusAsync(HttpStatusCode.BadRequest, $"/api/vendors/{vendor.Id}/payments",
            new RecordPaymentRequest("ACH-1003", new(2026, 8, 21), 0m, null), "Amount");
        await AssertStatusAsync(HttpStatusCode.BadRequest, $"/api/vendors/{vendor.Id}/payments",
            new RecordPaymentRequest("ACH-1004", new(2026, 8, 21), 1.005m, null), "Amount");
        await AssertStatusAsync(HttpStatusCode.NotFound, "/api/vendors/999999/payments",
            new RecordPaymentRequest("ACH-1005", new(2026, 8, 21), 5m, null), null);
    }

    [Fact]
    public async Task Recorded_payments_cannot_be_changed_or_deleted()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VendorFundingDbContext>();
        var payment = new VendorPayment { Id = 1, Reference = "ACH-1", Amount = 10m };
        db.Attach(payment);

        payment.Amount = 1m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Dashboard_works_with_no_activity()
    {
        var dashboard = await _client.GetFromJsonAsync<DashboardResponse>("/api/dashboard?asOf=2026-01-31", Json);

        Assert.NotNull(dashboard);
        Assert.Equal(0m, dashboard.Totals.Collected);
    }

    private async Task<T> PostAsync<T>(string url, object? body)
    {
        var response = body is null ? await _client.PostAsync(url, null) : await _client.PostAsJsonAsync(url, body, Json);
        Assert.True(response.IsSuccessStatusCode, $"POST {url} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private async Task AssertStatusAsync(HttpStatusCode expected, string url, object body, string? expectedText)
    {
        var response = await _client.PostAsJsonAsync(url, body, Json);
        var content = await response.Content.ReadAsStringAsync();
        Assert.True(expected == response.StatusCode, $"Expected {(int)expected} but got {(int)response.StatusCode}: {content}");
        if (expectedText is not null)
        {
            Assert.Contains(expectedText, content);
        }
    }
}
