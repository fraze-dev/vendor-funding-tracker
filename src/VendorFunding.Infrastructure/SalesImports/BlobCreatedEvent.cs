using System.Text.Json;

namespace VendorFunding.Infrastructure.SalesImports;

/// <summary>Reads the Event Grid "BlobCreated" event that Blob Storage sends (through Service Bus) when a file arrives.</summary>
public static class BlobCreatedEvent
{
    private const string InboxSubjectPrefix = $"/blobServices/default/containers/{SalesFileStore.InboxContainer}/blobs/";

    /// <summary>Gets the inbox blob name from an Event Grid event, or false if the event isn't a new inbox file.</summary>
    public static bool TryGetInboxBlobName(string eventJson, out string blobName)
    {
        blobName = "";
        using var document = JsonDocument.Parse(eventJson);
        var root = document.RootElement;

        // Event Grid can deliver a single event or an array of them.
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() != 1)
            {
                return false;
            }

            root = root[0];
        }

        if (!root.TryGetProperty("eventType", out var type) || type.GetString() != "Microsoft.Storage.BlobCreated" ||
            !root.TryGetProperty("subject", out var subject) || subject.GetString() is not { } value ||
            !value.StartsWith(InboxSubjectPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        blobName = value[InboxSubjectPrefix.Length..];
        return blobName.Length > 0;
    }
}
