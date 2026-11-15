using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VendorFunding.Domain;
using VendorFunding.Infrastructure.Persistence;

namespace VendorFunding.Infrastructure.SalesImports;

/// <summary>A slice of a file's valid rows, sent through the queue and loaded in one transaction.</summary>
public record SalesBatchMessage(Guid ImportId, int BatchNumber, IReadOnlyList<ParsedSalesRow> Rows);

/// <summary>
/// Turns stored sales files into sales records. Both steps are safe to repeat, because queue messages
/// can be delivered more than once: a file is only parsed while its import is unfinished, a file whose
/// content was already imported is marked Duplicate, and each batch is loaded at most once.
/// </summary>
public sealed class SalesImportProcessor(
    VendorFundingDbContext db, SalesFileStore files, TimeProvider clock, ILogger<SalesImportProcessor> logger)
{
    public const int BatchSize = 500;

    /// <summary>Parses a file from the inbox and returns the batches to queue for loading (none if nothing to load).</summary>
    public async Task<IReadOnlyList<SalesBatchMessage>> ProcessFileAsync(string blobName, CancellationToken cancellationToken)
    {
        var file = await files.DownloadFromInboxAsync(blobName, cancellationToken);
        var import = await FindOrCreateImportAsync(blobName, file, cancellationToken);
        if (import.IsFinished)
        {
            logger.LogInformation("Import {ImportId} is already {Status}; ignoring repeat event for {BlobName}.", import.Id, import.Status, blobName);
            return [];
        }

        // A retry of a file that already reached Loading skips this, so it isn't flagged as a duplicate of itself.
        if (import.Status == SalesImportStatus.Received)
        {
            import.ContentHash = Convert.ToHexString(SHA256.HashData(file.Content.ToMemory().Span));
            var original = await db.SalesImports
                .Where(i => i.ContentHash == import.ContentHash && i.Id != import.Id
                    && i.Status != SalesImportStatus.Duplicate && i.Status != SalesImportStatus.Failed)
                .Select(i => (Guid?)i.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (original is not null)
            {
                Finish(import, SalesImportStatus.Duplicate, $"This file has the same content as import {original}, so nothing was loaded.");
                await db.SaveChangesAsync(cancellationToken);
                return [];
            }
        }

        using var reader = new StreamReader(file.Content.ToStream(), Encoding.UTF8);
        var result = SalesFileParser.Parse(reader);
        if (result.FileError is not null)
        {
            Finish(import, SalesImportStatus.Failed, result.FileError);
            await db.SaveChangesAsync(cancellationToken);
            return [];
        }

        import.TotalRows = result.Rows.Count + result.Errors.Count;
        import.RejectedRows = result.Errors.Count;
        if (result.Errors.Count > 0)
        {
            import.RejectsBlobName = $"{import.Id}.csv";
            await files.UploadRejectsAsync(import.RejectsBlobName, RejectsCsv(result.Errors), cancellationToken);
        }

        var batches = result.Rows
            .Chunk(BatchSize)
            .Select((rows, index) => new SalesBatchMessage(import.Id, index + 1, rows))
            .ToList();
        import.TotalBatches = batches.Count;

        if (batches.Count == 0)
        {
            Finish(import, result.Errors.Count > 0 ? SalesImportStatus.CompletedWithErrors : SalesImportStatus.Completed, null);
        }
        else
        {
            import.Status = SalesImportStatus.Loading;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Import {ImportId}: {Rows} valid rows in {Batches} batches, {Rejected} rejected.",
            import.Id, result.Rows.Count, batches.Count, result.Errors.Count);
        return batches;
    }

    /// <summary>Loads one batch of rows, unless it was already loaded by an earlier delivery of the same message.</summary>
    public async Task LoadBatchAsync(SalesBatchMessage batch, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            if (await db.SalesImportBatches.AnyAsync(
                    b => b.ImportId == batch.ImportId && b.BatchNumber == batch.BatchNumber, cancellationToken))
            {
                logger.LogInformation("Batch {BatchNumber} of import {ImportId} was already loaded; skipping.", batch.BatchNumber, batch.ImportId);
                return;
            }

            db.SalesRecords.AddRange(batch.Rows.Select(r => new SalesRecord
            {
                StoreNumber = r.StoreNumber,
                Sku = r.Sku,
                SaleDate = r.SaleDate,
                UnitsSold = r.UnitsSold,
                NetSales = r.NetSales,
                ImportId = batch.ImportId,
            }));
            db.SalesImportBatches.Add(new SalesImportBatch
            {
                ImportId = batch.ImportId,
                BatchNumber = batch.BatchNumber,
                RowCount = batch.Rows.Count,
                LoadedAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        await UpdateProgressAsync(batch.ImportId, cancellationToken);
    }

    /// <summary>
    /// Recomputes progress from the loaded batches in single UPDATE statements, so batches finishing at the same
    /// time on different instances can't overwrite each other's counts.
    /// </summary>
    private async Task UpdateProgressAsync(Guid importId, CancellationToken cancellationToken)
    {
        await db.SalesImports
            .Where(i => i.Id == importId)
            .ExecuteUpdateAsync(s => s.SetProperty(
                i => i.LoadedRows,
                i => db.SalesImportBatches.Where(b => b.ImportId == i.Id).Sum(b => b.RowCount)), cancellationToken);

        var now = clock.GetUtcNow();
        await db.SalesImports
            .Where(i => i.Id == importId && i.Status == SalesImportStatus.Loading
                && db.SalesImportBatches.Count(b => b.ImportId == i.Id) >= i.TotalBatches)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, i => i.RejectedRows > 0 ? SalesImportStatus.CompletedWithErrors : SalesImportStatus.Completed)
                .SetProperty(i => i.CompletedAt, now), cancellationToken);
    }

    private async Task<SalesImport> FindOrCreateImportAsync(string blobName, StoredSalesFile file, CancellationToken cancellationToken)
    {
        var import = await db.SalesImports.FirstOrDefaultAsync(i => i.BlobName == blobName, cancellationToken);
        if (import is not null)
        {
            return import;
        }

        // Files dropped straight into Blob Storage (not uploaded through the API) have no import yet.
        import = new SalesImport
        {
            Id = file.ImportId ?? Guid.NewGuid(),
            FileName = file.FileName,
            BlobName = blobName,
            ReceivedAt = clock.GetUtcNow(),
        };
        db.SalesImports.Add(import);
        return import;
    }

    private void Finish(SalesImport import, SalesImportStatus status, string? error)
    {
        import.Status = status;
        import.Error = error;
        import.CompletedAt = clock.GetUtcNow();
    }

    private static string RejectsCsv(IEnumerable<SalesFileRowError> errors)
    {
        var csv = new StringBuilder("line_number,error,original_line\n");
        foreach (var e in errors)
        {
            csv.Append(e.LineNumber).Append(",\"").Append(e.Error.Replace("\"", "\"\"")).Append("\",\"")
               .Append(e.Line.Replace("\"", "\"\"")).Append("\"\n");
        }

        return csv.ToString();
    }
}
