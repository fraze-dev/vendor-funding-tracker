using System.Net.Http.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using VendorFunding.Infrastructure.SalesImports;

namespace VendorFunding.Functions;

/// <summary>
/// Re-runs the accrual for the open period each night, so the ledger reflects the day's sales files.
/// It calls the API rather than duplicating the accounting rules, which stay in one place.
/// </summary>
public sealed class NightlyAccrual(
    SalesFileStore files, IHttpClientFactory httpClients, TimeProvider clock, ILogger<NightlyAccrual> logger)
{
    [Function(nameof(NightlyAccrual))]
    public async Task Run([TimerTrigger("%NightlyAccrualSchedule%")] TimerInfo timer, CancellationToken cancellationToken)
    {
        // Checking Blob Storage first means a quiet night doesn't wake the auto-paused database,
        // which would use up its monthly free compute for nothing.
        if (!await files.InboxHasFilesSinceAsync(clock.GetUtcNow().AddHours(-25), cancellationToken))
        {
            logger.LogInformation("No sales files arrived in the last day; skipping the accrual run.");
            return;
        }

        var api = httpClients.CreateClient("VendorFundingApi");
        var periods = await api.GetFromJsonAsync<List<AccountingPeriod>>("/api/periods", cancellationToken) ?? [];
        if (periods.FirstOrDefault(p => p.Status == "Open") is not { } open)
        {
            logger.LogInformation("No accounting period is open; nothing to accrue.");
            return;
        }

        var response = await api.PostAsync($"/api/accruals/{open.Period}/run", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var run = await response.Content.ReadFromJsonAsync<AccrualRun>(cancellationToken);
        logger.LogInformation("Nightly accrual for {Period}: {Entries} entries, net {Amount}.",
            run?.Period, run?.EntriesPosted, run?.NetAmountPosted);
    }

    private sealed record AccountingPeriod(string Period, string Status);

    private sealed record AccrualRun(string Period, int EntriesPosted, decimal NetAmountPosted);
}
