using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VendorFunding.Api.Contracts;
using VendorFunding.Api.Services;
using VendorFunding.Domain;
using VendorFunding.Infrastructure.Persistence;

namespace VendorFunding.Api.Endpoints;

public static class VendorEndpoints
{
    public static IEndpointRouteBuilder MapVendorEndpoints(this IEndpointRouteBuilder app)
    {
        var vendors = app.MapGroup("/api/vendors").WithTags("Vendors");

        vendors.MapGet("/", GetVendors).WithSummary("List vendors");
        vendors.MapGet("/{vendorId:int}", GetVendor).WithName(nameof(GetVendor)).WithSummary("Get a vendor");
        vendors.MapPost("/", CreateVendor).WithSummary("Create a vendor");

        vendors.MapGet("/{vendorId:int}/deals", GetDeals).WithTags("Deals").WithSummary("List a vendor's deals");
        vendors.MapPost("/{vendorId:int}/deals", CreateDeal).WithTags("Deals").WithSummary("Create a deal for a vendor");

        vendors.MapGet("/{vendorId:int}/funding-summary", GetFundingSummary)
            .WithTags("Funding")
            .WithSummary("Funding earned by a vendor in a period")
            .WithDescription("`period` accepts a year (`2026`), quarter (`2026-Q3`) or month (`2026-09`).");

        return app;
    }

    private static async Task<Ok<List<VendorResponse>>> GetVendors(VendorFundingDbContext db, CancellationToken ct)
    {
        var vendors = await db.Vendors.AsNoTracking().OrderBy(v => v.VendorNumber).ToListAsync(ct);
        return TypedResults.Ok(vendors.Select(VendorResponse.From).ToList());
    }

    private static async Task<Results<Ok<VendorResponse>, NotFound>> GetVendor(int vendorId, VendorFundingDbContext db, CancellationToken ct)
    {
        var vendor = await db.Vendors.AsNoTracking().FirstOrDefaultAsync(v => v.Id == vendorId, ct);
        return vendor is null ? TypedResults.NotFound() : TypedResults.Ok(VendorResponse.From(vendor));
    }

    private static async Task<Results<CreatedAtRoute<VendorResponse>, ValidationProblem, Conflict<ProblemDetails>>> CreateVendor(
        CreateVendorRequest request, VendorFundingDbContext db, CancellationToken ct)
    {
        if (request.Validate() is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var vendorNumber = request.VendorNumber.Trim();
        if (await db.Vendors.AnyAsync(v => v.VendorNumber == vendorNumber, ct))
        {
            return TypedResults.Conflict(new ProblemDetails { Title = $"Vendor {vendorNumber} already exists." });
        }

        var vendor = new Vendor { VendorNumber = vendorNumber, Name = request.Name.Trim() };
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync(ct);

        return TypedResults.CreatedAtRoute(VendorResponse.From(vendor), nameof(GetVendor), new { vendorId = vendor.Id });
    }

    private static async Task<Results<Ok<List<DealResponse>>, NotFound>> GetDeals(int vendorId, VendorFundingDbContext db, CancellationToken ct)
    {
        if (!await db.Vendors.AnyAsync(v => v.Id == vendorId, ct))
        {
            return TypedResults.NotFound();
        }

        var deals = await db.Deals.AsNoTracking()
            .Where(d => d.VendorId == vendorId)
            .OrderBy(d => d.StartDate).ThenBy(d => d.DealNumber)
            .ToListAsync(ct);
        return TypedResults.Ok(deals.Select(DealResponse.From).ToList());
    }

    private static async Task<Results<Created<DealResponse>, ValidationProblem, NotFound, Conflict<ProblemDetails>>> CreateDeal(
        int vendorId, CreateDealRequest request, VendorFundingDbContext db, CancellationToken ct)
    {
        if (!await db.Vendors.AnyAsync(v => v.Id == vendorId, ct))
        {
            return TypedResults.NotFound();
        }

        if (request.Validate() is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var dealNumber = request.DealNumber.Trim();
        if (await db.Deals.AnyAsync(d => d.DealNumber == dealNumber, ct))
        {
            return TypedResults.Conflict(new ProblemDetails { Title = $"Deal {dealNumber} already exists." });
        }

        var deal = new Deal
        {
            VendorId = vendorId,
            DealNumber = dealNumber,
            Description = request.Description.Trim(),
            Type = request.Type,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Sku = request.Type == DealType.ScanDown ? request.Sku!.Trim() : null,
            RatePerUnit = request.RatePerUnit,
            LumpSumAmount = request.LumpSumAmount,
        };
        db.Deals.Add(deal);
        await db.SaveChangesAsync(ct);

        return TypedResults.Created($"/api/vendors/{vendorId}/deals", DealResponse.From(deal));
    }

    private static async Task<Results<Ok<FundingSummaryResponse>, ValidationProblem, NotFound>> GetFundingSummary(
        int vendorId, string period, FundingSummaryService fundingSummaries, CancellationToken ct)
    {
        if (!FiscalPeriod.TryParse(period, out var fiscalPeriod))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(period)] = ["Use a year (2026), quarter (2026-Q3) or month (2026-09)."],
            });
        }

        var summary = await fundingSummaries.GetAsync(vendorId, fiscalPeriod, ct);
        return summary is null ? TypedResults.NotFound() : TypedResults.Ok(summary);
    }
}
