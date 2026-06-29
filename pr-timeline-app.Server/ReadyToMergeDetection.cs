// Pure detection helpers for the `ready_to_merge` trigger: when an open PR is approved and
// genuinely mergeable, nag both its author and its approver(s) so it gets merged. Kept free
// of GitHub/storage/push dependencies so the ready-to-merge criteria can be unit tested.

// One nag candidate: user `UserId` should be told that `Repository#Number` is ready to merge,
// in their `Role` (the PR's author or one of its approvers).
sealed record DetectedReadyToMerge(long UserId, string Repository, int Number, string Title, string Url, ReadyToMergeRole Role);

enum ReadyToMergeRole
{
    Author,
    Approver
}

static class ReadyToMergeDetection
{
    public const string EventPrefix = "ready_to_merge";

    // Presence of a state entry means "already notified that this PR is ready to merge".
    // Removing the entry (when it stops being ready) lets a later re-entry notify again.
    public const string ReadyFingerprint = "ready";

    // An approval older than this is "aging" and no longer counts as freshly ready to merge,
    // mirroring the frontend's Ready-to-merge lane.
    private static readonly TimeSpan s_approvalAging = TimeSpan.FromDays(2);

    // Labels that explicitly hold a PR back from merging, mirroring the frontend list.
    private static readonly HashSet<string> s_doNotMergeLabels =
        new(StringComparer.OrdinalIgnoreCase) { "needs-author-action", "no-merge" };

    public static string EventKey(string repository, int number) =>
        $"{EventPrefix}:{ReviewRequestDetection.NormalizeRepository(repository)}#{number}";

    public static string DeepLink(string repository, int number) =>
        ReviewRequestDetection.DeepLink(repository, number);

    // Recovers the repository slug from a ready_to_merge event key so stale state for scanned
    // repos can be pruned. Returns false for keys that aren't ready_to_merge events.
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

        repository = ReviewRequestDetection.NormalizeRepository(eventKey[prefix.Length..hashIndex]);
        return repository.Length > 0;
    }

    // True when an open PR is approved and clean enough to merge: it carries the frontend's
    // "Ready to merge" lane rules (approved, fresh, CI not failing, no merge-blocking threads,
    // no conflicts, no do-not-merge label).
    public static bool IsReadyToMerge(PullRequestSummary pullRequest, DateTimeOffset now)
    {
        if (pullRequest.Draft || !pullRequest.State.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (pullRequest.Review.State != "approved" || pullRequest.Review.ApprovalCount <= 0)
        {
            return false;
        }

        if (IsApprovalAging(pullRequest, now))
        {
            return false;
        }

        if (string.Equals(pullRequest.Checks.State, "failure", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (pullRequest.Review.UnresolvedThreadCount > 0 && pullRequest.Review.RequiresConversationResolution)
        {
            return false;
        }

        if (string.Equals(pullRequest.MergeableState, "dirty", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !pullRequest.Labels.Any(label => s_doNotMergeLabels.Contains(label));
    }

    // For one repository's PRs, yield a nag candidate for every ready-to-merge PR's author and
    // approver(s) that map to an enabled user id. The author is yielded first so a user who both
    // authored and approved is nagged once, as the author.
    public static IEnumerable<DetectedReadyToMerge> DetectForRepository(
        string repository,
        IReadOnlyList<PullRequestSummary> pullRequests,
        IReadOnlySet<long> enabledUserIds,
        DateTimeOffset now)
    {
        var normalizedRepository = ReviewRequestDetection.NormalizeRepository(repository);
        foreach (var pullRequest in pullRequests)
        {
            if (!IsReadyToMerge(pullRequest, now))
            {
                continue;
            }

            var url = DeepLink(normalizedRepository, pullRequest.Number);
            var notified = new HashSet<long>();

            if (pullRequest.OwnerUserId is > 0
                && enabledUserIds.Contains(pullRequest.OwnerUserId.Value)
                && notified.Add(pullRequest.OwnerUserId.Value))
            {
                yield return new DetectedReadyToMerge(
                    pullRequest.OwnerUserId.Value,
                    normalizedRepository,
                    pullRequest.Number,
                    pullRequest.Title,
                    url,
                    ReadyToMergeRole.Author);
            }

            foreach (var approverId in pullRequest.Review.ApprovedReviewerIds)
            {
                if (approverId > 0 && enabledUserIds.Contains(approverId) && notified.Add(approverId))
                {
                    yield return new DetectedReadyToMerge(
                        approverId,
                        normalizedRepository,
                        pullRequest.Number,
                        pullRequest.Title,
                        url,
                        ReadyToMergeRole.Approver);
                }
            }
        }
    }

    private static bool IsApprovalAging(PullRequestSummary pullRequest, DateTimeOffset now) =>
        (pullRequest.Review.LastApprovedAt ?? pullRequest.Review.LastReviewedAt) is { } approvedAt
        && now - approvedAt >= s_approvalAging;
}
