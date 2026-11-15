using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using VendorFunding.Infrastructure.SalesImports;

namespace VendorFunding.Functions;

/// <summary>
/// Blob Storage → Event Grid → "sales-files" queue → ParseSalesFile → "sales-records" queue → LoadSalesBatch → SQL.
/// The queues absorb bursts and let failed work retry (then dead-letter) without losing files.
/// </summary>
public sealed class SalesFileFunctions(SalesImportProcessor processor, ILogger<SalesFileFunctions> logger)
{
    [Function(nameof(ParseSalesFile))]
    [ServiceBusOutput("sales-records", Connection = "ServiceBus")]
    public async Task<string[]> ParseSalesFile(
        [ServiceBusTrigger("sales-files", Connection = "ServiceBus")] string eventJson,
        CancellationToken cancellationToken)
    {
        if (!BlobCreatedEvent.TryGetInboxBlobName(eventJson, out var blobName))
        {
            logger.LogWarning("Ignoring an event that isn't a new file in {Container}.", SalesFileStore.InboxContainer);
            return [];
        }

        var batches = await processor.ProcessFileAsync(blobName, cancellationToken);
        return batches.Select(batch => JsonSerializer.Serialize(batch)).ToArray();
    }

    [Function(nameof(LoadSalesBatch))]
    public async Task LoadSalesBatch(
        [ServiceBusTrigger("sales-records", Connection = "ServiceBus")] string message,
        CancellationToken cancellationToken)
    {
        var batch = JsonSerializer.Deserialize<SalesBatchMessage>(message)
            ?? throw new InvalidOperationException("The sales batch message was empty.");
        await processor.LoadBatchAsync(batch, cancellationToken);
    }
}
