using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

// Cache lookups need an explicit Found flag so callers can distinguish a missing entry
// from a cached default/null value after deserialization.
readonly record struct GitHubCacheLookup<T>(bool Found, T? Value);

// Central response cache used by GitHubClient.
//
// Layer 1 is IMemoryCache. It is always used first for every cache lane because it is
// fast and safe for user/token-scoped data that must never leave the process.
//
// Layer 2 is Blob Storage through GitHubPublicCacheStore. Only public cache keys are
// written there, including their last-good variants, so scale-to-zero or a new backend
// instance can reload shared public snapshots without persisting user-specific data.
sealed class GitHubResponseCache(IMemoryCache memoryCache, GitHubPublicCacheStore publicCacheStore)
{
    public bool TryGetLocalValue<T>(string cacheKey, out T? value) =>
        memoryCache.TryGetValue(cacheKey, out value);

    public void SetLocalValue<T>(string cacheKey, T value, TimeSpan cacheDuration) =>
        memoryCache.Set(cacheKey, value, CreateCacheEntryOptions(cacheDuration));

    public async Task<GitHubCacheLookup<T>> GetAsync<T>(string cacheKey, CancellationToken cancellationToken)
    {
        if (memoryCache.TryGetValue(cacheKey, out T? value))
        {
            return new GitHubCacheLookup<T>(true, value);
        }

        // Token/user scoped entries stop at L1. Falling through to Blob is intentionally
        // limited to public cache keys so authenticated response data is not externalized.
        if (!GitHubCachePolicy.IsPublicCacheKey(cacheKey))
        {
            return default;
        }

        var publicLookup = await publicCacheStore.GetAsync<T>(cacheKey, cancellationToken);
        if (publicLookup.Found)
        {
            // Promote Blob hits back into memory with the remaining Blob TTL. This keeps
            // steady-state requests fast while preserving Blob as the durable source.
            memoryCache.Set(
                cacheKey,
                publicLookup.Value,
                CreateCacheEntryOptions(publicLookup.LocalCacheDuration));
        }

        return new GitHubCacheLookup<T>(publicLookup.Found, publicLookup.Value);
    }

    public async Task SetAsync<T>(
        string cacheKey,
        T value,
        TimeSpan cacheDuration,
        CancellationToken cancellationToken)
    {
        memoryCache.Set(cacheKey, value, CreateCacheEntryOptions(cacheDuration));

        // Public entries use write-through caching: memory is the hot path, Blob is the
        // durable shared path used after process restarts or scale-to-zero.
        if (GitHubCachePolicy.IsPublicCacheKey(cacheKey))
        {
            await publicCacheStore.SetAsync(cacheKey, value, cacheDuration, cancellationToken);
            if (GitHubCachePolicy.TryGetPublicCacheRepositoryName(cacheKey, out var repositoryName))
            {
                await publicCacheStore.TrackAsync(repositoryName, cacheKey, cancellationToken);
            }
        }
    }

    public async Task RemoveAsync(string cacheKey, CancellationToken cancellationToken)
    {
        memoryCache.Remove(cacheKey);

        // Removing a public entry must clear both layers so revoked or expired public
        // snapshots cannot be resurrected from Blob.
        if (GitHubCachePolicy.IsPublicCacheKey(cacheKey))
        {
            await publicCacheStore.RemoveAsync(cacheKey, cancellationToken);
        }
    }

    internal static MemoryCacheEntryOptions CreateCacheEntryOptions(TimeSpan cacheDuration)
    {
        var options = new MemoryCacheEntryOptions();
        if (cacheDuration == Timeout.InfiniteTimeSpan)
        {
            options.Priority = CacheItemPriority.NeverRemove;
        }
        else
        {
            options.AbsoluteExpirationRelativeToNow = cacheDuration;
        }

        return options;
    }
}

readonly record struct GitHubPublicCacheLookup<T>(
    bool Found,
    T? Value,
    TimeSpan LocalCacheDuration);

// Blob-backed store for shared public GitHub responses.
//
// Entries are content blobs under entries/{hash(cacheKey)}. The cache key itself is
// hashed so arbitrary GitHub/resource key parts never become blob path segments.
// The full key is not stored in metadata, so cache invalidation that needs to find a
// repository's entries goes through the repository index described below.
//
// Repository indexes are stored separately under repositories/{hash(owner/name)}.
// They map a public repository to every cache key we have warmed for it, which lets us
// purge both normal and last-good snapshots if that repository is no longer eligible
// for anonymous/public-cache fallback.
//
// This class intentionally knows nothing about token/user cache lanes. GitHubResponseCache
// is responsible for deciding which keys are public enough to reach this store.
sealed class GitHubPublicCacheStore
{
    public const string ConnectionName = "github-cache";
    private const string LastGoodCacheKeyPrefix = "last-good:";
    private const string ExpiresAtMetadataName = "expiresatunixtimeseconds";
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IMemoryCache memoryCache;
    private readonly BlobContainerClient? blobContainer;

    // Hot in-process mirror of each repository index. Blob remains the durable index
    // used after a restart or by another scaled-out instance.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> cacheKeysByRepository = new(StringComparer.OrdinalIgnoreCase);

    public GitHubPublicCacheStore(IMemoryCache memoryCache)
    {
        this.memoryCache = memoryCache;
    }

    public GitHubPublicCacheStore(
        IMemoryCache memoryCache,
        [FromKeyedServices(ConnectionName)] BlobContainerClient blobContainer)
    {
        this.memoryCache = memoryCache;
        this.blobContainer = blobContainer;
    }

    public async Task<GitHubPublicCacheLookup<T>> GetAsync<T>(
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (blobContainer is null)
        {
            // Test-only/memory-only construction path. Production registers the Aspire
            // BlobContainerClient and should always have a container.
            return default;
        }

        // Blob TTL is stored as metadata because the object content is just the cached
        // GitHub payload. Missing or expired metadata is treated as a miss and cleaned up.
        var blob = GetEntryBlob(cacheKey);
        BlobDownloadResult download;
        try
        {
            download = (await blob.DownloadContentAsync(cancellationToken)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return default;
        }
        catch (RequestFailedException ex)
        {
            throw CreateCacheUnavailableException("reading", ex);
        }

        // The object exists, but it is only usable if its metadata can prove the entry is
        // still fresh. Bad metadata is equivalent to corruption, so delete the blob.
        if (!TryGetExpiresAt(download.Details.Metadata, out var expiresAt)
            || expiresAt <= DateTimeOffset.UtcNow)
        {
            await RemoveAsync(cacheKey, cancellationToken);
            return default;
        }

        var value = download.Content.ToObjectFromJson<T>(s_jsonOptions);
        var localCacheDuration = expiresAt is null
            ? Timeout.InfiniteTimeSpan
            : expiresAt.Value - DateTimeOffset.UtcNow;
        // Re-check after deserialization because time can pass between reading metadata
        // and calculating the remaining L1 duration.
        if (localCacheDuration <= TimeSpan.Zero)
        {
            await RemoveAsync(cacheKey, cancellationToken);
            return default;
        }

        return new GitHubPublicCacheLookup<T>(true, value, localCacheDuration);
    }

    public async Task SetAsync<T>(
        string cacheKey,
        T value,
        TimeSpan cacheDuration,
        CancellationToken cancellationToken)
    {
        if (blobContainer is null)
        {
            // Keep tests and direct unit construction memory-only; GitHubResponseCache
            // already populated L1 before calling into this store.
            return;
        }

        // Preserve the caller's cache duration in Blob metadata so a cold process can
        // rehydrate the item with only the remaining TTL.
        var expiresAt = cacheDuration == Timeout.InfiniteTimeSpan
            ? (DateTimeOffset?)null
            : DateTimeOffset.UtcNow.Add(cacheDuration);
        var metadata = expiresAt is null
            ? null
            : new Dictionary<string, string>
            {
                [ExpiresAtMetadataName] = expiresAt.Value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
            };

        try
        {
            // Lazily create the container so local Azurite and published Azure Storage use
            // the same code path without a separate startup provisioning step.
            await blobContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            await GetEntryBlob(cacheKey).UploadAsync(
                BinaryData.FromObjectAsJson(value, s_jsonOptions),
                new BlobUploadOptions { Metadata = metadata },
                cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            throw CreateCacheUnavailableException("writing", ex);
        }
    }

    public async Task TrackAsync(
        RepositoryName repositoryName,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var repositoryKey = GitHubCachePolicy.NormalizeRepositoryName(repositoryName);

        // Track the un-hashed cache key in the repo index because entry blob names are
        // hashes and cannot be recovered or listed by repository name later.
        var cacheKeys = cacheKeysByRepository.GetOrAdd(
            repositoryKey,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        if (!cacheKeys.TryAdd(cacheKey, 0))
        {
            return;
        }

        if (blobContainer is null)
        {
            return;
        }

        try
        {
            var trackedKeys = await ReadTrackedCacheKeysAsync(repositoryKey, cancellationToken);
            if (trackedKeys.Add(cacheKey))
            {
                // The index blob is intentionally small JSON, not blob tags, because Azurite
                // support and local emulator behavior are simpler for plain blobs.
                await WriteTrackedCacheKeysAsync(repositoryKey, trackedKeys, cancellationToken);
            }
        }
        catch
        {
            cacheKeys.TryRemove(cacheKey, out _);
            throw;
        }
    }

    public async Task<bool> HasTrackedSnapshotAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken)
    {
        var repositoryKey = GitHubCachePolicy.NormalizeRepositoryName(repositoryName);
        if (cacheKeysByRepository.ContainsKey(repositoryKey))
        {
            return true;
        }

        if (blobContainer is null)
        {
            return false;
        }

        // A persisted index is enough proof that we have previously warmed this public
        // repo, even if this process has not loaded any of its entries yet.
        try
        {
            return await GetRepositoryIndexBlob(repositoryKey).ExistsAsync(cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            throw CreateCacheUnavailableException("checking", ex);
        }
    }

    public async Task RemoveRepositoryAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken)
    {
        var repositoryKey = GitHubCachePolicy.NormalizeRepositoryName(repositoryName);

        // Start with both known indexes: the hot in-memory one for this process and the
        // durable Blob index that may have been written by a previous process instance.
        var cacheKeys = new HashSet<string>(StringComparer.Ordinal);
        if (cacheKeysByRepository.TryRemove(repositoryKey, out var localCacheKeys))
        {
            cacheKeys.UnionWith(localCacheKeys.Keys);
        }

        if (blobContainer is not null)
        {
            cacheKeys.UnionWith(await ReadTrackedCacheKeysAsync(repositoryKey, cancellationToken));
        }

        foreach (var cacheKey in cacheKeys)
        {
            // Remove both the live cache key and its last-good companion. Last-good entries
            // share the same public marker and are persisted separately in Blob.
            memoryCache.Remove(cacheKey);
            memoryCache.Remove($"{LastGoodCacheKeyPrefix}{cacheKey}");
            await RemoveAsync(cacheKey, cancellationToken);
            await RemoveAsync($"{LastGoodCacheKeyPrefix}{cacheKey}", cancellationToken);
        }

        if (blobContainer is null)
        {
            return;
        }

        try
        {
            await GetRepositoryIndexBlob(repositoryKey).DeleteIfExistsAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            throw CreateCacheUnavailableException("deleting", ex);
        }
    }

    public async Task RemoveAsync(string cacheKey, CancellationToken cancellationToken)
    {
        if (blobContainer is null)
        {
            return;
        }

        try
        {
            await GetEntryBlob(cacheKey).DeleteIfExistsAsync(cancellationToken: cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            throw CreateCacheUnavailableException("deleting", ex);
        }
    }

    private async Task<HashSet<string>> ReadTrackedCacheKeysAsync(
        string repositoryKey,
        CancellationToken cancellationToken)
    {
        if (blobContainer is null)
        {
            return [];
        }

        var blob = GetRepositoryIndexBlob(repositoryKey);
        try
        {
            var download = (await blob.DownloadContentAsync(cancellationToken)).Value;
            // Missing or invalid JSON is treated as an empty index rather than guessing
            // entry blob names, because entry names are one-way hashes of cache keys.
            return download.Content.ToObjectFromJson<string[]>(s_jsonOptions)?.ToHashSet(StringComparer.Ordinal)
                ?? [];
        }
        catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return [];
        }
        catch (RequestFailedException ex)
        {
            throw CreateCacheUnavailableException("reading", ex);
        }
    }

    private async Task WriteTrackedCacheKeysAsync(
        string repositoryKey,
        HashSet<string> cacheKeys,
        CancellationToken cancellationToken)
    {
        if (blobContainer is null)
        {
            return;
        }

        try
        {
            await blobContainer.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            // Sort for stable blob contents; overwrite is acceptable because this is only
            // an invalidation index, not the source of cached response data itself.
            await GetRepositoryIndexBlob(repositoryKey).UploadAsync(
                BinaryData.FromObjectAsJson(cacheKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray(), s_jsonOptions),
                overwrite: true,
                cancellationToken);
        }
        catch (RequestFailedException ex)
        {
            throw CreateCacheUnavailableException("writing", ex);
        }
    }

    private BlobClient GetEntryBlob(string cacheKey) =>
        blobContainer!.GetBlobClient($"entries/{Hash(cacheKey)}.json");

    private BlobClient GetRepositoryIndexBlob(string repositoryKey) =>
        blobContainer!.GetBlobClient($"repositories/{Hash(repositoryKey)}.json");

    private static bool TryGetExpiresAt(
        IDictionary<string, string> metadata,
        out DateTimeOffset? expiresAt)
    {
        expiresAt = null;
        if (!metadata.TryGetValue(ExpiresAtMetadataName, out var value))
        {
            return true;
        }

        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixTimeSeconds))
        {
            return false;
        }

        expiresAt = DateTimeOffset.FromUnixTimeSeconds(unixTimeSeconds);
        return true;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static GitHubApiException CreateCacheUnavailableException(
        string operation,
        RequestFailedException exception) =>
        new(
            HttpStatusCode.ServiceUnavailable,
            $"The shared public GitHub cache is temporarily unavailable while {operation} blob storage: {exception.Message}");
}
