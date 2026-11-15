using Microsoft.AspNetCore.Http.HttpResults;
using VendorFunding.Api.Services;
using VendorFunding.Api.Validation;
using VendorFunding.Contracts;

namespace VendorFunding.Api.Endpoints;

public static class CollectionsEndpoints
{
    public static IEndpointRouteBuilder MapCollectionsEndpoints(this IEndpointRouteBuilder app)
    {
        var payments = app.MapGroup("/api/vendors/{vendorId:int}/payments").WithTags("Collections");

        payments.MapPost("/", RecordPayment)
            .WithSummary("Record a payment from a vendor")
            .WithDescription(
                "Books Dr Cash / Cr Vendor Funding Receivable in the open period. The received date must fall in the " +
                "open period and can't be in the future. References are unique per vendor, so the same payment can't be recorded twice.");

        payments.MapGet("/", GetPayments).WithSummary("List a vendor's payments");

        app.MapGet("/api/dashboard", GetDashboard)
            .WithTags("Collections")
            .WithSummary("Earned vs. collected funding and receivable aging")
            .WithDescription("Aging buckets are 0-30, 31-60, 61-90 and over 90 days; payments settle the oldest amounts first. " +
                             "`asOf` defaults to today (UTC).");

        return app;
    }

    private static async Task<Results<Created<VendorPaymentResponse>, ValidationProblem, NotFound>> RecordPayment(
        int vendorId, RecordPaymentRequest request, CollectionsService collections, CancellationToken ct)
    {
        if (request.Validate() is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var payment = await collections.RecordPaymentAsync(vendorId, request, ct);
        return payment is null
            ? TypedResults.NotFound()
            : TypedResults.Created($"/api/vendors/{vendorId}/payments", payment);
    }

    private static async Task<Results<Ok<List<VendorPaymentResponse>>, NotFound>> GetPayments(
        int vendorId, CollectionsService collections, CancellationToken ct)
    {
        var payments = await collections.GetPaymentsAsync(vendorId, ct);
        return payments is null ? TypedResults.NotFound() : TypedResults.Ok(payments);
    }

    private static async Task<Ok<DashboardResponse>> GetDashboard(
        DateOnly? asOf, CollectionsService collections, TimeProvider clock, CancellationToken ct) =>
        TypedResults.Ok(await collections.GetDashboardAsync(asOf ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), ct));
}
