sealed class GitHubReviewPolicyOptions
{
    public const string SectionName = "GitHubReviewPolicy";

    // Repositories (owner/repo) whose branch protection requires conversation
    // resolution, so unresolved review threads block merging an approved PR.
    // The dashboard surfaces unresolved-thread signals only for these repos.
    public string[] RequireConversationResolution { get; init; } = [];
}
