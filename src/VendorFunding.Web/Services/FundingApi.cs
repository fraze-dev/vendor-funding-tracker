using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components.Forms;
using VendorFunding.Contracts;

namespace VendorFunding.Web.Services;

/// <summary>The result of a write call: the created resource, or a message saying why the API refused it.</summary>
public sealed record ApiResult<T>(T? Value, string? Error)
{
    public bool Succeeded => Error is null;
}

/// <summary>Typed client for the Vendor Funding API, using the same contract types the API serves.</summary>
public sealed class FundingApi(HttpClient http)
{
    public const long MaxUploadBytes = 10 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public Uri BaseAddress => http.BaseAddress!;

    public Task<DashboardResponse> GetDashboardAsync(CancellationToken ct = default) => GetAsync<DashboardResponse>("api/dashboard", ct);
    public Task<VendorResponse> GetVendorAsync(int id, CancellationToken ct = default) => GetAsync<VendorResponse>($"api/vendors/{id}", ct);
    public Task<List<DealResponse>> GetDealsAsync(int vendorId, CancellationToken ct = default) => GetAsync<List<DealResponse>>($"api/vendors/{vendorId}/deals", ct);
    public Task<List<VendorPaymentResponse>> GetPaymentsAsync(int vendorId, CancellationToken ct = default) => GetAsync<List<VendorPaymentResponse>>($"api/vendors/{vendorId}/payments", ct);
    public Task<List<PeriodResponse>> GetPeriodsAsync(CancellationToken ct = default) => GetAsync<List<PeriodResponse>>("api/periods", ct);
    public Task<TrialBalanceResponse> GetTrialBalanceAsync(string period, CancellationToken ct = default) => GetAsync<TrialBalanceResponse>($"api/trial-balance?period={period}", ct);
    public Task<List<JournalEntryResponse>> GetJournalEntriesAsync(string period, CancellationToken ct = default) => GetAsync<List<JournalEntryResponse>>($"api/journal-entries?period={period}", ct);
    public Task<List<SalesImportResponse>> GetImportsAsync(CancellationToken ct = default) => GetAsync<List<SalesImportResponse>>("api/sales/imports", ct);
    public Task<SalesImportResponse> GetImportAsync(Guid id, CancellationToken ct = default) => GetAsync<SalesImportResponse>($"api/sales/imports/{id}", ct);

    /// <summary>A plain link works for the download: it's a navigation, not a script request, so it needs no CORS.</summary>
    public string RejectsUrl(Guid importId) => new Uri(BaseAddress, $"api/sales/imports/{importId}/rejects").ToString();

    public string ApiDocsUrl => new Uri(BaseAddress, "scalar").ToString();

    public async Task<ApiResult<VendorPaymentResponse>> RecordPaymentAsync(int vendorId, RecordPaymentRequest request, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/vendors/{vendorId}/payments", request, Json, ct);
        return await ToResultAsync<VendorPaymentResponse>(response, ct);
    }

    public async Task<ApiResult<SalesImportResponse>> UploadSalesFileAsync(IBrowserFile file, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var content = new StreamContent(file.OpenReadStream(MaxUploadBytes, ct));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(content, "file", file.Name);

        using var response = await http.PostAsync("api/sales/files", form, ct);
        return await ToResultAsync<SalesImportResponse>(response, ct);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(await ReadProblemAsync(response, ct), null, response.StatusCode);
        }

        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }

    private static async Task<ApiResult<T>> ToResultAsync<T>(HttpResponseMessage response, CancellationToken ct) =>
        response.IsSuccessStatusCode
            ? new ApiResult<T>(await response.Content.ReadFromJsonAsync<T>(Json, ct), null)
            : new ApiResult<T>(default, await ReadProblemAsync(response, ct));

    /// <summary>Turns the API's problem details (RFC 9457) into one readable sentence.</summary>
    private static async Task<string> ReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<Problem>(Json, ct);
            if (problem?.Errors is { Count: > 0 } errors)
            {
                return string.Join(" ", errors.SelectMany(e => e.Value));
            }

            if (!string.IsNullOrWhiteSpace(problem?.Detail) || !string.IsNullOrWhiteSpace(problem?.Title))
            {
                return string.Join(" ", new[] { problem!.Title, problem.Detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
        }
        catch (JsonException)
        {
            // Not a problem-details body; fall through to the status code.
        }

        return $"The API returned {(int)response.StatusCode} {response.ReasonPhrase}.";
    }

    private sealed record Problem(string? Title, string? Detail, Dictionary<string, string[]>? Errors);
}
