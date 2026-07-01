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
    public static bool IsReadyToMerge(
        PullRequestSummary pullRequest,
        DateTimeOffset now,
        IReadOnlySet<string> doNotMergeLabels) =>
        IsReadyToMerge(
            repository: "",
            pullRequest,
            now,
            doNotMergeLabels,
            nonBlockingCheckFailureRules: []);

    public static bool IsReadyToMerge(
        string repository,
        PullRequestSummary pullRequest,
        DateTimeOffset now,
        IReadOnlySet<string> doNotMergeLabels,
        IReadOnlyList<DashboardCheckFailureRuleOptions> nonBlockingCheckFailureRules)
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

        if (!AreChecksReady(repository, pullRequest, nonBlockingCheckFailureRules))
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

        return !pullRequest.Labels.Any(doNotMergeLabels.Contains);
    }

    // For one repository's PRs, yield a nag candidate for every ready-to-merge PR's author and
    // approver(s) that map to an enabled user id. The author is yielded first so a user who both
    // authored and approved is nagged once, as the author.
    public static IEnumerable<DetectedReadyToMerge> DetectForRepository(
        string repository,
        IReadOnlyList<PullRequestSummary> pullRequests,
        IReadOnlySet<long> enabledUserIds,
        DateTimeOffset now,
        IReadOnlySet<string> doNotMergeLabels) =>
        DetectForRepository(
            repository,
            pullRequests,
            enabledUserIds,
            now,
            doNotMergeLabels,
            nonBlockingCheckFailureRules: []);

    public static IEnumerable<DetectedReadyToMerge> DetectForRepository(
        string repository,
        IReadOnlyList<PullRequestSummary> pullRequests,
        IReadOnlySet<long> enabledUserIds,
        DateTimeOffset now,
        IReadOnlySet<string> doNotMergeLabels,
        IReadOnlyList<DashboardCheckFailureRuleOptions> nonBlockingCheckFailureRules)
    {
        var normalizedRepository = ReviewRequestDetection.NormalizeRepository(repository);
        foreach (var pullRequest in pullRequests)
        {
            if (!IsReadyToMerge(normalizedRepository, pullRequest, now, doNotMergeLabels, nonBlockingCheckFailureRules))
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

    private static bool AreChecksReady(
        string repository,
        PullRequestSummary pullRequest,
        IReadOnlyList<DashboardCheckFailureRuleOptions> nonBlockingCheckFailureRules)
    {
        if (string.Equals(pullRequest.Checks.State, "pending", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pullRequest.Checks.State, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(pullRequest.Checks.State, "failure", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsNonBlockingOnlyFailure(repository, pullRequest, nonBlockingCheckFailureRules)
            && pullRequest.Checks.PendingCount == 0;
    }

    private static bool IsNonBlockingOnlyFailure(
        string repository,
        PullRequestSummary pullRequest,
        IReadOnlyList<DashboardCheckFailureRuleOptions> nonBlockingCheckFailureRules)
    {
        if (pullRequest.Checks.FailingChecks.Count == 0)
        {
            return false;
        }

        return pullRequest.Checks.FailingChecks.All(check =>
            MatchingNonBlockingCheckFailureRule(repository, check.Name, nonBlockingCheckFailureRules) is not null);
    }

    private static DashboardCheckFailureRuleOptions? MatchingNonBlockingCheckFailureRule(
        string repository,
        string checkName,
        IReadOnlyList<DashboardCheckFailureRuleOptions> nonBlockingCheckFailureRules)
    {
        foreach (var rule in nonBlockingCheckFailureRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Repository) ||
                !string.Equals(rule.Repository.Trim(), repository, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (MatchesNonBlockingCheckFailureName(rule, checkName))
            {
                return rule;
            }
        }

        return null;
    }

    private static bool MatchesNonBlockingCheckFailureName(DashboardCheckFailureRuleOptions rule, string checkName)
    {
        var normalized = checkName.Trim();
        return rule.CheckNames.Any(name => normalized.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            || rule.CheckNameContains.Any(fragment =>
                !string.IsNullOrWhiteSpace(fragment) &&
                normalized.Contains(fragment.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
