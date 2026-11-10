using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

namespace VendorFunding.Api;

/// <summary>
/// Turns malformed request bodies (bad JSON, a date like "10/28/2026") into a 400 that says which field is wrong,
/// instead of letting them surface as a 500 Internal Server Error.
/// </summary>
internal sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        httpContext.Response.StatusCode = badRequest.StatusCode;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = badRequest.StatusCode,
                Title = "The request body could not be read.",
                Detail = badRequest.InnerException is JsonException { Path: { } path }
                    ? $"Invalid value at '{path}'. Dates use the format yyyy-MM-dd."
                    : badRequest.Message,
            },
        });
    }
}
