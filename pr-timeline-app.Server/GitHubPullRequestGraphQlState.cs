using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

sealed class GitHubPullRequestGraphQlState(IMemoryCache? cache = null)
{
    private readonly IMemoryCache cache = cache ?? new MemoryCache(new MemoryCacheOptions());

    public ConcurrentDictionary<string, Task> RefreshTasks { get; } = new(StringComparer.Ordinal);

    public void SetRefreshError(string cacheKey, string error, TimeSpan lifetime) =>
        SetStateValue(StateValueKind.RefreshError, cacheKey, error, lifetime);

    public bool TryGetRefreshError(string cacheKey, out string? error) =>
        cache.TryGetValue(CreateStateKey(StateValueKind.RefreshError, cacheKey), out error);

    public void RemoveRefreshError(string cacheKey) =>
        cache.Remove(CreateStateKey(StateValueKind.RefreshError, cacheKey));

    public void SetListFetchedAt(string cacheKey, DateTimeOffset fetchedAt, TimeSpan lifetime) =>
        SetStateValue(StateValueKind.ListFetchedAt, cacheKey, fetchedAt, lifetime);

    public bool TryGetListFetchedAt(string cacheKey, out DateTimeOffset fetchedAt) =>
        cache.TryGetValue(CreateStateKey(StateValueKind.ListFetchedAt, cacheKey), out fetchedAt);

    public void SetRefreshCooldownUntil(string cacheKey, DateTimeOffset cooldownUntil)
    {
        var lifetime = cooldownUntil - DateTimeOffset.UtcNow;
        if (lifetime <= TimeSpan.Zero)
        {
            RemoveRefreshCooldownUntil(cacheKey);
            return;
        }

        SetStateValue(StateValueKind.RefreshCooldownUntil, cacheKey, cooldownUntil, lifetime);
    }

    public bool TryGetRefreshCooldownUntil(string cacheKey, out DateTimeOffset cooldownUntil) =>
        cache.TryGetValue(CreateStateKey(StateValueKind.RefreshCooldownUntil, cacheKey), out cooldownUntil);

    public void RemoveRefreshCooldownUntil(string cacheKey) =>
        cache.Remove(CreateStateKey(StateValueKind.RefreshCooldownUntil, cacheKey));

    public void Remove(string cacheKey)
    {
        RemoveRefreshError(cacheKey);
        cache.Remove(CreateStateKey(StateValueKind.ListFetchedAt, cacheKey));
        RemoveRefreshCooldownUntil(cacheKey);
    }

    private void SetStateValue<T>(StateValueKind kind, string cacheKey, T value, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            cache.Remove(CreateStateKey(kind, cacheKey));
            return;
        }

        cache.Set(CreateStateKey(kind, cacheKey), value, CreateCacheEntryOptions(lifetime));
    }

    private static MemoryCacheEntryOptions CreateCacheEntryOptions(TimeSpan lifetime)
    {
        var options = new MemoryCacheEntryOptions();
        if (lifetime == Timeout.InfiniteTimeSpan)
        {
            options.Priority = CacheItemPriority.NeverRemove;
        }
        else
        {
            options.AbsoluteExpirationRelativeToNow = lifetime;
        }

        return options;
    }

    private static StateKey CreateStateKey(StateValueKind kind, string cacheKey) =>
        new(kind, cacheKey);

    private enum StateValueKind
    {
        RefreshError,
        ListFetchedAt,
        RefreshCooldownUntil
    }

    private readonly record struct StateKey(StateValueKind Kind, string CacheKey);
}
