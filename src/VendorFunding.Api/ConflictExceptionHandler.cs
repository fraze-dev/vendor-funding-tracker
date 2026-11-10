using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using VendorFunding.Domain;

namespace VendorFunding.Api;

/// <summary>
/// Returns 409 Conflict for requests that break an accounting rule (posting to a closed period, closing out
/// of order) or that lost a race with another request changing the same data.
/// </summary>
internal sealed class ConflictExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var title = exception switch
        {
            AccountingRuleException rule => rule.Message,
            DbUpdateConcurrencyException => "Someone else changed this period at the same time. Please try again.",
            // Unique index violations: e.g. two requests opening the same period at once.
            DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } } =>
                "Someone else made a conflicting change at the same time. Please try again.",
            _ => null,
        };

        if (title is null)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = StatusCodes.Status409Conflict, Title = title },
        });
    }
}
