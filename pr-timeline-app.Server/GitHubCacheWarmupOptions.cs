sealed class GitHubCacheWarmupOptions
{
    public const string SectionName = "GitHubCacheWarmup";

    public bool Enabled { get; init; }

    public bool EnabledInDevelopment { get; init; }

    public string State { get; init; } = "open";

    public int RefreshIntervalMinutes { get; init; } = 60;

    public string? PublicCacheToken { get; init; }

    public string[] Repositories { get; init; } = [];
}
