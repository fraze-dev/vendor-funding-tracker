using Microsoft.AspNetCore.Http.HttpResults;
using VendorFunding.Api.Contracts;
using VendorFunding.Domain;
using VendorFunding.Infrastructure.Persistence;

namespace VendorFunding.Api.Endpoints;

public static class SalesEndpoints
{
    private const int MaxBatchSize = 5_000;

    public static IEndpointRouteBuilder MapSalesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sales", ImportSales)
            .WithTags("Sales")
            .WithSummary("Import a batch of daily sales records")
            .WithDescription($"Accepts up to {MaxBatchSize:N0} records per request. The whole batch is rejected if any record is invalid.");

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
            errors.RequiredMaxLength($"records[{i}].{nameof(SalesRecordRequest.StoreNumber)}", records[i].StoreNumber, 10);
            errors.RequiredMaxLength($"records[{i}].{nameof(SalesRecordRequest.Sku)}", records[i].Sku, 20);
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
}
