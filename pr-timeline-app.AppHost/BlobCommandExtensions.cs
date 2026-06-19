using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure;
using Azure.Storage.Blobs;

// Local test command helpers for blob-backed resources. The clear command is used by
// Aspire e2e flows to reset the Azurite-backed public cache between scenarios.
static class BlobCommandExtensions
{
    public static IResourceBuilder<AzureBlobStorageContainerResource> WithClearCacheCommand(
        this IResourceBuilder<AzureBlobStorageContainerResource> githubCache)
    {
        githubCache.WithCommand(
            "clear-cache",
            "Clear cache",
            async context =>
            {
                try
                {
                    var connectionString = await ((IResourceWithConnectionString)githubCache.Resource)
                        .GetConnectionStringAsync(context.CancellationToken);
                    if (string.IsNullOrWhiteSpace(connectionString))
                    {
                        return CommandResults.Failure("The local GitHub cache connection string is not available.");
                    }

                    var container = CreateBlobContainerClient(connectionString);
                    var deletedCount = 0;

                    await container.CreateIfNotExistsAsync(cancellationToken: context.CancellationToken);
                    await foreach (var blob in container.GetBlobsAsync(cancellationToken: context.CancellationToken))
                    {
                        await container.DeleteBlobIfExistsAsync(blob.Name, cancellationToken: context.CancellationToken);
                        deletedCount++;
                    }

                    return CommandResults.Success($"Cleared {deletedCount} blob(s) from {container.Name}.");
                }
                catch (RequestFailedException ex)
                {
                    return CommandResults.Failure($"Failed to clear the local GitHub cache blob container: {ex.Message}");
                }
            },
            new CommandOptions
            {
                Description = "Deletes all blobs from the local GitHub public cache container."
            });

        return githubCache;
    }

    private static BlobContainerClient CreateBlobContainerClient(string connectionString)
    {
        const string blobContainerNameKey = "ContainerName";

        string? blobContainerName = null;
        List<string> storageConnectionStringParts = [];

        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = part.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex > 0 && part[..separatorIndex].Equals(blobContainerNameKey, StringComparison.OrdinalIgnoreCase))
            {
                blobContainerName = part[(separatorIndex + 1)..];
                continue;
            }

            storageConnectionStringParts.Add(part);
        }

        if (string.IsNullOrWhiteSpace(blobContainerName))
        {
            throw new InvalidOperationException("The github-cache connection string is missing a ContainerName value.");
        }

        return new BlobContainerClient(string.Join(';', storageConnectionStringParts), blobContainerName);
    }
}
