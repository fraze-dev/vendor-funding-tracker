using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace VendorFunding.Infrastructure.SalesImports;

/// <summary>Blob Storage for incoming sales files and the rejected rows written back for each one.</summary>
public sealed class SalesFileStore
{
    public const string InboxContainer = "sales-inbox";
    public const string RejectsContainer = "sales-rejects";

    private const string ImportIdKey = "importid";
    private const string FileNameKey = "filename";

    private readonly BlobServiceClient? _service;
    private readonly bool _createContainers;

    public SalesFileStore(BlobServiceClient? service, bool createContainers)
    {
        _service = service;
        _createContainers = createContainers;
    }

    /// <summary>
    /// Uses a connection string when one is given (Azurite locally and in tests, which also creates the containers);
    /// otherwise the account URL with managed identity (Azure, where Bicep creates the containers).
    /// With neither, the store is unconfigured and file features are unavailable.
    /// </summary>
    public static SalesFileStore FromSettings(string? connectionString, string? blobServiceUri, string? managedIdentityClientId)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return new SalesFileStore(new BlobServiceClient(connectionString), createContainers: true);
        }

        if (!string.IsNullOrWhiteSpace(blobServiceUri))
        {
            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = string.IsNullOrWhiteSpace(managedIdentityClientId) ? null : managedIdentityClientId,
            });
            return new SalesFileStore(new BlobServiceClient(new Uri(blobServiceUri), credential), createContainers: false);
        }

        return new SalesFileStore(null, createContainers: false);
    }

    public bool IsConfigured => _service is not null;

    public async Task UploadToInboxAsync(string blobName, Stream content, string fileName, Guid importId, CancellationToken cancellationToken)
    {
        var container = await GetContainerAsync(InboxContainer, cancellationToken);
        await container.GetBlobClient(blobName).UploadAsync(content, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "text/csv" },
            // Metadata values must be ASCII, so the original file name is URL-encoded.
            Metadata = new Dictionary<string, string>
            {
                [ImportIdKey] = importId.ToString(),
                [FileNameKey] = Uri.EscapeDataString(fileName),
            },
        }, cancellationToken);
    }

    public async Task<StoredSalesFile> DownloadFromInboxAsync(string blobName, CancellationToken cancellationToken)
    {
        var container = await GetContainerAsync(InboxContainer, cancellationToken);
        var result = await container.GetBlobClient(blobName).DownloadContentAsync(cancellationToken);
        var metadata = result.Value.Details.Metadata;

        Guid? importId = metadata.TryGetValue(ImportIdKey, out var id) && Guid.TryParse(id, out var parsed) ? parsed : null;
        var fileName = metadata.TryGetValue(FileNameKey, out var name) ? Uri.UnescapeDataString(name) : Path.GetFileName(blobName);
        return new StoredSalesFile(result.Value.Content, importId, fileName);
    }

    public async Task UploadRejectsAsync(string blobName, string csv, CancellationToken cancellationToken)
    {
        var container = await GetContainerAsync(RejectsContainer, cancellationToken);
        await container.GetBlobClient(blobName).UploadAsync(BinaryData.FromString(csv), overwrite: true, cancellationToken);
    }

    public async Task<string> DownloadRejectsAsync(string blobName, CancellationToken cancellationToken)
    {
        var container = await GetContainerAsync(RejectsContainer, cancellationToken);
        var result = await container.GetBlobClient(blobName).DownloadContentAsync(cancellationToken);
        return result.Value.Content.ToString();
    }

    /// <summary>True if any file arrived in the inbox since <paramref name="since"/>. Doesn't touch the database.</summary>
    public async Task<bool> InboxHasFilesSinceAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var container = await GetContainerAsync(InboxContainer, cancellationToken);
        await foreach (var blob in container.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            if (blob.Properties.LastModified >= since)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<BlobContainerClient> GetContainerAsync(string name, CancellationToken cancellationToken)
    {
        if (_service is null)
        {
            throw new InvalidOperationException("Sales file storage isn't configured.");
        }

        var container = _service.GetBlobContainerClient(name);
        if (_createContainers)
        {
            await container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        }

        return container;
    }
}

public record StoredSalesFile(BinaryData Content, Guid? ImportId, string FileName);
