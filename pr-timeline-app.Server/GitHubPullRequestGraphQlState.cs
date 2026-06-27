using System.Collections.Concurrent;

sealed class GitHubPullRequestGraphQlState
{
    public ConcurrentDictionary<string, Task> RefreshTasks { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, string> RefreshErrors { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, DateTimeOffset> ListFetchedAt { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, DateTimeOffset> RefreshCooldownUntil { get; } = new(StringComparer.Ordinal);
}
