sealed class GitHubReviewPolicyOptions
{
    public const string SectionName = "GitHubReviewPolicy";

    // Repositories (owner/repo) whose branch protection requires conversation
    // resolution, so unresolved review threads block merging an approved PR.
    // This list gates only the approved-PR case: an *approved* PR surfaces
    // unresolved-thread signals only when its repo is listed here. Waiting PRs
    // reviewed by the Copilot bot surface unresolved-thread signals regardless
    // of this list (see GitHubClient.GetReviewStatusAsync).
    public string[] RequireConversationResolution { get; init; } = [];
}
