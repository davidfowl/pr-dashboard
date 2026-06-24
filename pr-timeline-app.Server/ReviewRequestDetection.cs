// Pure detection helpers for the v1 `review_requested` trigger, kept separate from the
// BackgroundService so the matching and key/deep-link conventions can be unit tested without
// any GitHub, storage, or push dependencies.

// A single candidate review request: user `UserId` is currently a requested reviewer on
// `Repository#Number`.
sealed record DetectedReviewRequest(long UserId, string Repository, int Number, string Title, string Url);

static class ReviewRequestDetection
{
    public const string EventPrefix = "review_requested";

    // The fingerprint is a constant marker: the presence of a state entry means "already
    // notified that this user is a requested reviewer here". Removing the entry (when the user
    // is no longer requested) lets a later re-request notify again.
    public const string RequestedFingerprint = "requested";

    public static string NormalizeRepository(string repository) =>
        repository.Trim().ToLowerInvariant();

    public static string EventKey(string repository, int number) =>
        $"{EventPrefix}:{NormalizeRepository(repository)}#{number}";

    // Deep link consumed by the SW/app router. Matches the `#pr/<owner%2Frepo>/<number>`
    // convention used by the frontend (App.tsx parseDetailHash).
    public static string DeepLink(string repository, int number) =>
        $"/#pr/{Uri.EscapeDataString(NormalizeRepository(repository))}/{number}";

    // Recovers the repository slug from an event key so stale state for scanned repos can be
    // pruned. Returns false for keys that aren't review_requested events.
    public static bool TryGetRepository(string eventKey, out string repository)
    {
        repository = string.Empty;
        var prefix = EventPrefix + ":";
        if (!eventKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var hashIndex = eventKey.LastIndexOf('#');
        if (hashIndex <= prefix.Length)
        {
            return false;
        }

        repository = NormalizeRepository(eventKey[prefix.Length..hashIndex]);
        return repository.Length > 0;
    }

    // For one repository's open PRs, yield a candidate for every requested reviewer id that maps
    // to a subscribed user id. Draft and non-open PRs are ignored.
    public static IEnumerable<DetectedReviewRequest> DetectForRepository(
        string repository,
        IReadOnlyList<PullRequestSummary> pullRequests,
        IReadOnlySet<long> subscribedUserIds)
    {
        var normalizedRepository = NormalizeRepository(repository);
        foreach (var pullRequest in pullRequests)
        {
            if (pullRequest.Draft
                || !pullRequest.State.Equals("open", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var reviewerId in pullRequest.RequestedReviewerIds)
            {
                if (subscribedUserIds.Contains(reviewerId))
                {
                    yield return new DetectedReviewRequest(
                        reviewerId,
                        normalizedRepository,
                        pullRequest.Number,
                        pullRequest.Title,
                        DeepLink(normalizedRepository, pullRequest.Number));
                }
            }
        }
    }
}
