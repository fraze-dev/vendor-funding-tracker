using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VendorFunding.Contracts;
using VendorFunding.Domain;
using VendorFunding.Infrastructure.Persistence;
using VendorFunding.Infrastructure.SalesImports;

namespace VendorFunding.Api.Tests;

/// <summary>
/// Uploads go through the real API into Azurite. The Functions are thin wrappers, so these tests call the
/// same processor they do, in the order the queues would deliver messages (including redeliveries).
/// </summary>
public class SalesImportTests(ApiWithStorageFactory factory) : IClassFixture<ApiWithStorageFactory>
{
    private const string Header = SalesFileParser.ExpectedHeader;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Upload_stores_the_file_and_records_a_received_import()
    {
        var (response, import) = await UploadAsync("store-0001.csv", $"{Header}\nS0001,UP-1,2026-11-13,1,4.99\n");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/api/sales/imports/{import.Id}", response.Headers.Location?.AbsolutePath);
        Assert.Equal(SalesImportStatus.Received, import.Status);
        Assert.Equal("store-0001.csv", import.FileName);

        var stored = await WithServicesAsync(async s =>
        {
            var blobName = await s.GetRequiredService<VendorFundingDbContext>().SalesImports
                .Where(i => i.Id == import.Id).Select(i => i.BlobName).SingleAsync();
            return await s.GetRequiredService<SalesFileStore>().DownloadFromInboxAsync(blobName, default);
        });
        Assert.Equal(import.Id, stored.ImportId);
        Assert.Equal("store-0001.csv", stored.FileName);
        Assert.Contains("UP-1", stored.Content.ToString());
    }

    [Fact]
    public async Task Valid_rows_load_and_bad_rows_are_set_aside_with_reasons()
    {
        var sku = "MIX-" + Guid.NewGuid().ToString("N")[..8];
        var (_, import) = await UploadAsync("mixed.csv", $"""
            {Header}
            S0001,{sku},2026-11-13,10,49.90
            S0002,{sku},13/11/2026,5,24.95
            S0003,{sku},2026-11-13,20,99.80
            """);

        var batches = await ProcessAsync(import.Id);
        Assert.Single(batches);
        await LoadAsync(batches[0]);

        var finished = await GetImportAsync(import.Id);
        Assert.Equal(SalesImportStatus.CompletedWithErrors, finished.Status);
        Assert.Equal((3, 2, 1), (finished.TotalRows, finished.LoadedRows, finished.RejectedRows));
        Assert.True(finished.HasRejects);
        Assert.Equal(30, await UnitsForSkuAsync(sku));

        var rejects = await _client.GetStringAsync($"/api/sales/imports/{import.Id}/rejects");
        Assert.Contains("line_number,error,original_line", rejects);
        Assert.Contains("3,\"sale_date must be a date", rejects);
    }

    [Fact]
    public async Task Large_files_load_in_batches_and_redelivered_batches_are_not_loaded_twice()
    {
        var sku = "BIG-" + Guid.NewGuid().ToString("N")[..8];
        var lines = Enumerable.Range(1, 1_200).Select(n => $"S{n % 50:D4},{sku},2026-11-13,1,4.99");
        var (_, import) = await UploadAsync("big.csv", Header + "\n" + string.Join("\n", lines));

        var batches = await ProcessAsync(import.Id);
        Assert.Equal([1, 2, 3], batches.Select(b => b.BatchNumber));
        Assert.Equal([500, 500, 200], batches.Select(b => b.Rows.Count));

        // Out of order, with batch 2 delivered twice, like a queue retry after a timeout.
        await LoadAsync(batches[2]);
        await LoadAsync(batches[1]);
        Assert.Equal(SalesImportStatus.Loading, (await GetImportAsync(import.Id)).Status);
        await LoadAsync(batches[1]);
        await LoadAsync(batches[0]);

        var finished = await GetImportAsync(import.Id);
        Assert.Equal(SalesImportStatus.Completed, finished.Status);
        Assert.Equal(1_200, finished.LoadedRows);
        Assert.NotNull(finished.CompletedAt);
        Assert.Equal(1_200, await UnitsForSkuAsync(sku));
    }

    [Fact]
    public async Task Sending_the_same_file_twice_loads_it_once()
    {
        var sku = "DUP-" + Guid.NewGuid().ToString("N")[..8];
        var content = $"{Header}\nS0001,{sku},2026-11-13,7,34.93\n";

        var (_, first) = await UploadAsync("monday.csv", content);
        foreach (var batch in await ProcessAsync(first.Id))
        {
            await LoadAsync(batch);
        }

        var (_, second) = await UploadAsync("monday-resent.csv", content);
        Assert.Empty(await ProcessAsync(second.Id));

        var duplicate = await GetImportAsync(second.Id);
        Assert.Equal(SalesImportStatus.Duplicate, duplicate.Status);
        Assert.Contains(first.Id.ToString(), duplicate.Error);
        Assert.Equal(7, await UnitsForSkuAsync(sku));
    }

    [Fact]
    public async Task Repeat_events_for_a_finished_file_do_nothing()
    {
        var (_, import) = await UploadAsync("once.csv", $"{Header}\nS0001,ONCE-1,2026-11-13,1,4.99\n");
        foreach (var batch in await ProcessAsync(import.Id))
        {
            await LoadAsync(batch);
        }

        Assert.Empty(await ProcessAsync(import.Id));
        Assert.Equal(SalesImportStatus.Completed, (await GetImportAsync(import.Id)).Status);
    }

    [Fact]
    public async Task A_file_without_the_header_fails_with_a_reason()
    {
        var (_, import) = await UploadAsync("bad.csv", "store,sku,date\nS0001,X,2026-11-13\n");

        Assert.Empty(await ProcessAsync(import.Id));

        var failed = await GetImportAsync(import.Id);
        Assert.Equal(SalesImportStatus.Failed, failed.Status);
        Assert.Contains(Header, failed.Error);
    }

    [Theory]
    [InlineData("sales.txt", "S0001,X")]
    [InlineData("empty.csv", "")]
    public async Task Upload_rejects_wrong_file_types_and_empty_files(string fileName, string content)
    {
        var (response, _) = await UploadAsync(fileName, content, expectSuccess: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void BlobCreatedEvent_reads_inbox_files_and_ignores_everything_else()
    {
        const string inbox = """{"eventType":"Microsoft.Storage.BlobCreated","subject":"/blobServices/default/containers/sales-inbox/blobs/2026/11/14/abc.csv"}""";
        const string rejects = """{"eventType":"Microsoft.Storage.BlobCreated","subject":"/blobServices/default/containers/sales-rejects/blobs/abc.csv"}""";
        const string deleted = """{"eventType":"Microsoft.Storage.BlobDeleted","subject":"/blobServices/default/containers/sales-inbox/blobs/abc.csv"}""";

        Assert.True(BlobCreatedEvent.TryGetInboxBlobName(inbox, out var name));
        Assert.Equal("2026/11/14/abc.csv", name);
        Assert.True(BlobCreatedEvent.TryGetInboxBlobName($"[{inbox}]", out _));
        Assert.False(BlobCreatedEvent.TryGetInboxBlobName(rejects, out _));
        Assert.False(BlobCreatedEvent.TryGetInboxBlobName(deleted, out _));
    }

    private async Task<(HttpResponseMessage Response, SalesImportResponse Import)> UploadAsync(
        string fileName, string content, bool expectSuccess = true)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(file, "file", fileName);

        var response = await _client.PostAsync("/api/sales/files", form);
        if (!expectSuccess)
        {
            return (response, null!);
        }

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (response, (await response.Content.ReadFromJsonAsync<SalesImportResponse>(Json))!);
    }

    /// <summary>What ParseSalesFile does when the BlobCreated event for this import arrives.</summary>
    private Task<IReadOnlyList<SalesBatchMessage>> ProcessAsync(Guid importId) => WithServicesAsync(async s =>
    {
        var blobName = await s.GetRequiredService<VendorFundingDbContext>().SalesImports
            .Where(i => i.Id == importId).Select(i => i.BlobName).SingleAsync();
        return await ActivatorUtilities.CreateInstance<SalesImportProcessor>(s).ProcessFileAsync(blobName, default);
    });

    /// <summary>What LoadSalesBatch does for one queue message (serialized the same way).</summary>
    private Task LoadAsync(SalesBatchMessage batch) => WithServicesAsync(async s =>
    {
        var message = JsonSerializer.Deserialize<SalesBatchMessage>(JsonSerializer.Serialize(batch))!;
        await ActivatorUtilities.CreateInstance<SalesImportProcessor>(s).LoadBatchAsync(message, default);
        return true;
    });

    private async Task<SalesImportResponse> GetImportAsync(Guid importId) =>
        (await _client.GetFromJsonAsync<SalesImportResponse>($"/api/sales/imports/{importId}", Json))!;

    private Task<int> UnitsForSkuAsync(string sku) => WithServicesAsync(s =>
        s.GetRequiredService<VendorFundingDbContext>().SalesRecords.Where(r => r.Sku == sku).SumAsync(r => r.UnitsSold));

    private async Task<T> WithServicesAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }
}
