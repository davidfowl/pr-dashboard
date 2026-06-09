sealed class GitHubCacheWarmupOptions
{
    public const string SectionName = "GitHubCacheWarmup";

    public bool Enabled { get; init; }

    public bool EnabledInDevelopment { get; init; }

    public string State { get; init; } = "open";

    public string[] Repositories { get; init; } = [];
}
