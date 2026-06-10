using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

sealed class GitHubPublicCacheStore(IMemoryCache cache)
{
    private const string LastGoodCacheKeyPrefix = "last-good:";
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> cacheKeysByRepository = new(StringComparer.OrdinalIgnoreCase);

    public void Track(RepositoryName repositoryName, string cacheKey)
    {
        var repositoryKey = GitHubCachePolicy.NormalizeRepositoryName(repositoryName);
        var cacheKeys = cacheKeysByRepository.GetOrAdd(
            repositoryKey,
            _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        cacheKeys.TryAdd(cacheKey, 0);
    }

    public bool HasTrackedSnapshot(RepositoryName repositoryName) =>
        cacheKeysByRepository.ContainsKey(GitHubCachePolicy.NormalizeRepositoryName(repositoryName));

    public void RemoveRepository(RepositoryName repositoryName)
    {
        var repositoryKey = GitHubCachePolicy.NormalizeRepositoryName(repositoryName);
        if (!cacheKeysByRepository.TryRemove(repositoryKey, out var cacheKeys))
        {
            return;
        }

        foreach (var cacheKey in cacheKeys.Keys)
        {
            cache.Remove(cacheKey);
            cache.Remove($"{LastGoodCacheKeyPrefix}{cacheKey}");
        }
    }
}
