using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using VendorFunding.Api.Validation;
using VendorFunding.Contracts;
using VendorFunding.Domain;
using VendorFunding.Infrastructure.Persistence;
using VendorFunding.Infrastructure.SalesImports;

namespace VendorFunding.Api.Endpoints;

public static class SalesEndpoints
{
    private const int MaxBatchSize = 5_000;
    private const long MaxFileBytes = 10 * 1024 * 1024;

    public static IEndpointRouteBuilder MapSalesEndpoints(this IEndpointRouteBuilder app)
    {
        var sales = app.MapGroup("/api/sales").WithTags("Sales");

        sales.MapPost("/", ImportSales)
            .WithSummary("Import a batch of daily sales records")
            .WithDescription($"Accepts up to {MaxBatchSize:N0} records per request. The whole batch is rejected if any record is invalid.");

        sales.MapPost("/files", UploadSalesFile)
            .WithSummary("Upload a store's daily sales file (CSV)")
            .WithDescription(
                $"The first line must be `{SalesFileParser.ExpectedHeader}`. The file is stored and processed in the " +
                "background: valid rows are loaded and invalid rows are set aside with a reason. Poll the returned " +
                "import to follow its progress. Sending the same file twice loads it only once.")
            .DisableAntiforgery(); // An API called by systems, not a browser form, so there's no antiforgery token.

        sales.MapGet("/imports", GetImports).WithSummary("List recent sales file imports");
        sales.MapGet("/imports/{importId:guid}", GetImport).WithName(nameof(GetImport)).WithSummary("Get a sales file import");
        sales.MapGet("/imports/{importId:guid}/rejects", GetRejects).WithSummary("Download the rejected rows of an import (CSV)");

        return app;
    }

    private static async Task<Results<Ok<SalesBatchResponse>, ValidationProblem>> ImportSales(
        List<SalesRecordRequest> records, VendorFundingDbContext db, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (records.Count is 0 or > MaxBatchSize)
        {
            errors.Add("records", $"Send between 1 and {MaxBatchSize:N0} records.");
        }

        for (var i = 0; i < records.Count && records.Count <= MaxBatchSize; i++)
        {
            errors.RequiredMaxLength($"records[{i}].{nameof(SalesRecordRequest.StoreNumber)}", records[i].StoreNumber, SalesFileParser.MaxStoreNumberLength);
            errors.RequiredMaxLength($"records[{i}].{nameof(SalesRecordRequest.Sku)}", records[i].Sku, SalesFileParser.MaxSkuLength);
        }

        if (errors.ToDictionary() is { Count: > 0 } problems)
        {
            return TypedResults.ValidationProblem(problems);
        }

        db.SalesRecords.AddRange(records.Select(r => new SalesRecord
        {
            StoreNumber = r.StoreNumber.Trim(),
            Sku = r.Sku.Trim(),
            SaleDate = r.SaleDate,
            UnitsSold = r.UnitsSold,
            NetSales = r.NetSales,
        }));
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(new SalesBatchResponse(records.Count));
    }

    private static async Task<Results<AcceptedAtRoute<SalesImportResponse>, ValidationProblem, ProblemHttpResult>> UploadSalesFile(
        IFormFile file, SalesFileStore files, VendorFundingDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (!files.IsConfigured)
        {
            return TypedResults.Problem("Sales file storage isn't configured in this environment.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var errors = new ValidationErrors();
        if (!Path.GetExtension(file.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("file", "Upload a .csv file.");
        }

        if (file.Length is 0 or > MaxFileBytes)
        {
            errors.Add("file", $"The file must be between 1 byte and {MaxFileBytes / (1024 * 1024)} MB.");
        }

        if (errors.ToDictionary() is { Count: > 0 } problems)
        {
            return TypedResults.ValidationProblem(problems);
        }

        // Record the import first so its status can be checked as soon as this request returns.
        var now = clock.GetUtcNow();
        var importId = Guid.NewGuid();
        var import = new SalesImport
        {
            Id = importId,
            FileName = Path.GetFileName(file.FileName),
            BlobName = $"{now:yyyy/MM/dd}/{importId}.csv",
            ReceivedAt = now,
        };
        db.SalesImports.Add(import);
        await db.SaveChangesAsync(ct);

        // Storing the file raises a BlobCreated event, which starts processing.
        await using var content = file.OpenReadStream();
        await files.UploadToInboxAsync(import.BlobName, content, import.FileName, importId, ct);

        return TypedResults.AcceptedAtRoute(SalesImportResponse.From(import), nameof(GetImport), new { importId });
    }

    private static async Task<Ok<List<SalesImportResponse>>> GetImports(VendorFundingDbContext db, CancellationToken ct)
    {
        var imports = await db.SalesImports.AsNoTracking().OrderByDescending(i => i.ReceivedAt).Take(50).ToListAsync(ct);
        return TypedResults.Ok(imports.Select(SalesImportResponse.From).ToList());
    }

    private static async Task<Results<Ok<SalesImportResponse>, NotFound>> GetImport(Guid importId, VendorFundingDbContext db, CancellationToken ct)
    {
        var import = await db.SalesImports.AsNoTracking().FirstOrDefaultAsync(i => i.Id == importId, ct);
        return import is null ? TypedResults.NotFound() : TypedResults.Ok(SalesImportResponse.From(import));
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> GetRejects(
        Guid importId, VendorFundingDbContext db, SalesFileStore files, CancellationToken ct)
    {
        var import = await db.SalesImports.AsNoTracking().FirstOrDefaultAsync(i => i.Id == importId, ct);
        if (import?.RejectsBlobName is null || !files.IsConfigured)
        {
            return TypedResults.NotFound();
        }

        var csv = await files.DownloadRejectsAsync(import.RejectsBlobName, ct);
        return TypedResults.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", $"{importId}-rejects.csv");
    }
}
