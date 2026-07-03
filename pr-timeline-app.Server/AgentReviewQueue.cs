using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

static class AgentReviewQueueRoutes
{
    private const int MaxConcurrentRepositoryLoads = 4;
    private const int MaxExplicitRepositoryCount = 50;

    public static IEndpointRouteBuilder MapAgentReviewQueueRoutes(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/agents/review-queue", async (
            [FromQuery] string? repo,
            [FromQuery] bool? refresh,
            [FromQuery] int? limit,
            IOptions<DashboardOptions> dashboardOptions,
            GitHubPullRequestService pullRequests,
            CancellationToken cancellationToken) =>
        {
            var options = AgentReviewQueueBuilder.NormalizeOptions(dashboardOptions.Value);
            if (!TryResolveRepositories(repo, options.Repositories, out var repositories, out var errors))
            {
                return Results.ValidationProblem(errors);
            }

            return await BuildReviewQueueResponseAsync(
                repositories,
                options,
                refresh == true,
                AgentReviewQueueBuilder.ClampLimit(limit.GetValueOrDefault(10)),
                (repository, forceRefresh, token) => pullRequests.GetPullRequestsGraphQlSnapshotAsync(
                    repository,
                    "open",
                    forceRefresh,
                    token),
                DateTimeOffset.UtcNow,
                cancellationToken);
        });

        return endpoints;
    }

    internal static async Task<IResult> BuildReviewQueueResponseAsync(
        IReadOnlyList<RepositoryName> repositories,
        DashboardOptions dashboardOptions,
        bool forceRefresh,
        int limit,
        Func<RepositoryName, bool, CancellationToken, Task<PullRequestListResponse>> loadPullRequests,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var repositoryLoadSemaphore = new SemaphoreSlim(MaxConcurrentRepositoryLoads);
        var repositoryLoadTasks = repositories
            .Select((repository, index) => LoadRepositoryAsync(index, repository))
            .ToArray();
        var repositoryLoadResults = await Task.WhenAll(repositoryLoadTasks);

        var responses = new List<PullRequestListResponse>();
        var repositoryResults = new List<AgentReviewQueueRepositoryResult>();
        foreach (var loadResult in repositoryLoadResults.OrderBy(result => result.Index))
        {
            if (loadResult.Response is not null)
            {
                responses.Add(loadResult.Response);
            }

            repositoryResults.Add(loadResult.Result);
        }

        if (responses.Count == 0 && repositoryResults.Count > 0)
        {
            return Results.Problem(
                title: "Agent review queue unavailable",
                detail: "No requested repository data was available for the agent review queue.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var queue = AgentReviewQueueBuilder.Build(
            responses,
            dashboardOptions,
            now,
            limit);

        return Results.Ok(new AgentReviewQueueResponse(
            queue.Items,
            repositoryResults,
            queue.TotalCount,
            now));

        async Task<(int Index, PullRequestListResponse? Response, AgentReviewQueueRepositoryResult Result)> LoadRepositoryAsync(
            int index,
            RepositoryName repository)
        {
            await repositoryLoadSemaphore.WaitAsync(cancellationToken);
            try
            {
                var response = await loadPullRequests(
                    repository,
                    forceRefresh,
                    cancellationToken);
                return (index, response, new AgentReviewQueueRepositoryResult(
                    repository.ToString(),
                    response.PullRequests.Count,
                    response.Snapshot,
                    Error: null));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return (index, null, new AgentReviewQueueRepositoryResult(
                    repository.ToString(),
                    PullRequestCount: 0,
                    Snapshot: null,
                    Error: AgentReviewQueueBuilder.RepositoryUnavailableMessage));
            }
            finally
            {
                repositoryLoadSemaphore.Release();
            }
        }
    }

    internal static bool TryResolveRepositories(
        string? repo,
        IReadOnlyList<string> configuredRepositories,
        out IReadOnlyList<RepositoryName> repositories,
        out Dictionary<string, string[]> errors)
    {
        var usingConfiguredRepositories = string.IsNullOrWhiteSpace(repo);
        var inputs = usingConfiguredRepositories
            ? configuredRepositories
            : (repo ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<RepositoryName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var invalid = new List<string>();

        foreach (var input in inputs.Select(input => input.Trim()).Where(input => !string.IsNullOrWhiteSpace(input)))
        {
            if (RepositoryName.TryParse(input, out var repositoryName))
            {
                if (seen.Add(repositoryName.ToString()))
                {
                    parsed.Add(repositoryName);
                }
            }
            else if (!usingConfiguredRepositories)
            {
                invalid.Add(input);
            }
        }

        repositories = parsed;
        errors = [];
        if (invalid.Count > 0)
        {
            errors["repo"] = [$"Invalid repository value(s): {string.Join(", ", invalid)}. Use owner/repo."];
        }
        else if (parsed.Count == 0)
        {
            errors["repo"] = ["Configure at least one dashboard repository or pass repo=owner/repo."];
        }
        else if (!usingConfiguredRepositories && parsed.Count > MaxExplicitRepositoryCount)
        {
            errors["repo"] = [$"Pass at most {MaxExplicitRepositoryCount} repositories in repo=."];
        }

        return errors.Count == 0;
    }
}

static class AgentReviewQueueBuilder
{
    private static readonly TimeSpan s_approvedAging = TimeSpan.FromDays(2);
    private static readonly TimeSpan s_focusAgeLimit = TimeSpan.FromDays(14);
    private static readonly TimeSpan s_stalledPullRequest = TimeSpan.FromDays(7);
    private static readonly TimeSpan s_recentlyUpdatedWindow = TimeSpan.FromDays(2);
    private const int QuickWinLineThreshold = 80;
    private const int QuickWinFileThreshold = 3;
    private const string ApprovedButAgingBucketLabel = "Approved but aging";
    private const string RegressionBucketLabel = "Regression";
    private const string AgedOutCommunityBucketLabel = "Aged out community";
    internal const string RepositoryUnavailableMessage = "Repository data unavailable.";
    private const int MaxQueueLimit = 1000;

    private static readonly HashSet<string> s_excludedFocusBucketLabels = new(StringComparer.Ordinal)
    {
        "Stalled",
        "Draft",
        "My draft PRs",
        "Docs",
        "Community Toolkit",
        "Bots / automation",
        "Community",
        AgedOutCommunityBucketLabel,
        "Unresolved feedback",
        "Merge conflicts",
        "CI failing",
        "Author response"
    };

    private static readonly HashSet<string> s_disqualifyingFocusBucketLabels = new(StringComparer.Ordinal)
    {
        "Draft",
        "My draft PRs",
        "Docs",
        "Community Toolkit",
        "Bots / automation",
        "Community",
        AgedOutCommunityBucketLabel,
        "Unresolved feedback",
        "Merge conflicts"
    };

    private static readonly Dictionary<string, int> s_focusBucketRanks = new(StringComparer.Ordinal)
    {
        [RegressionBucketLabel] = -2,
        [ApprovedButAgingBucketLabel] = 0,
        ["Re-review needed"] = 1,
        ["Ready to merge"] = 2,
        ["Author response"] = 3,
        ["Needs review"] = 4,
        ["Quick wins"] = 5,
        ["Review started"] = 6
    };

    private static readonly Dictionary<string, int> s_listBucketRanks = new(StringComparer.Ordinal)
    {
        [RegressionBucketLabel] = -2,
        ["CI failing"] = -1,
        [ApprovedButAgingBucketLabel] = 0,
        ["Re-review needed"] = 1,
        ["Ready to merge"] = 2,
        ["Author response"] = 3,
        ["Needs review"] = 4,
        ["Quick wins"] = 5,
        ["Review started"] = 6
    };

    public static AgentReviewQueue Build(
        IReadOnlyList<PullRequestListResponse> responses,
        DashboardOptions options,
        DateTimeOffset now,
        int limit = 10)
    {
        var candidates = responses
            .SelectMany(response => response.PullRequests.Select(pullRequest => new AgentReviewQueueCandidate(response.Repository, pullRequest)))
            .ToArray();
        options = NormalizeOptions(options);
        var bucketItems = new List<AgentReviewQueueItem>();
        var bucketLabelsByKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates.Where(candidate =>
            candidate.PullRequest.State.Equals("open", StringComparison.OrdinalIgnoreCase)
            && !HasNeedsAuthorActionLabel(candidate.PullRequest, options)))
        {
            foreach (var label in ReviewBucketLabels(candidate, options, now))
            {
                if (!bucketLabelsByKey.TryGetValue(Key(candidate), out var labels))
                {
                    labels = [];
                    bucketLabelsByKey[Key(candidate)] = labels;
                }

                labels.Add(label);
                bucketItems.Add(new AgentReviewQueueItem(
                    candidate.Repository,
                    candidate.PullRequest,
                    label,
                    ReviewSignal(candidate.PullRequest, label)));
            }
        }

        var blockedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, labels) in bucketLabelsByKey)
        {
            if (labels.Any(label => s_disqualifyingFocusBucketLabels.Contains(label))
                || IsWaitingOnAuthor(labels))
            {
                blockedKeys.Add(key);
            }
        }

        var dedupedItemsByKey = new Dictionary<string, AgentReviewQueueItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in bucketItems.Where(item => !s_excludedFocusBucketLabels.Contains(item.BucketLabel)))
        {
            var candidate = new AgentReviewQueueCandidate(item.Repository, item.PullRequest);
            var key = Key(candidate);
            if (blockedKeys.Contains(key))
            {
                continue;
            }

            if (!dedupedItemsByKey.TryGetValue(key, out var existing)
                || FocusBucketRank(item.BucketLabel) < FocusBucketRank(existing.BucketLabel))
            {
                dedupedItemsByKey[key] = item;
            }
        }

        var orderedItems = dedupedItemsByKey.Values
            .Where(item => IsPullRequestWithinFocusAgeLimit(item.PullRequest, item.BucketLabel, now))
            .Where(item => !IsCommunityPullRequest(new AgentReviewQueueCandidate(item.Repository, item.PullRequest), options, now))
            .Where(item => !IsChecksFailing(new AgentReviewQueueCandidate(item.Repository, item.PullRequest), options))
            .Order(AgentReviewQueueItemComparer.Create(now))
            .ToArray();

        return new AgentReviewQueue(
            orderedItems.Take(ClampLimit(limit)).ToArray(),
            orderedItems.Length);
    }

    internal static int ClampLimit(int limit) => Math.Clamp(limit, 1, MaxQueueLimit);

    internal static DashboardOptions NormalizeOptions(DashboardOptions options) =>
        new()
        {
            Repositories = NormalizeList(options.Repositories),
            ShipWeekRepositories = NormalizeList(options.ShipWeekRepositories),
            CoreTeamMembers = NormalizeList(options.CoreTeamMembers),
            CoreTeamMemberAliasSuffixes = NormalizeList(options.CoreTeamMemberAliasSuffixes),
            CommunityRepositories = NormalizeList(options.CommunityRepositories),
            CurrentRelease = options.CurrentRelease?.Trim() ?? "",
            ShipWeekReleaseBranch = options.ShipWeekReleaseBranch?.Trim() ?? "",
            DocsFromCode = new DashboardDocsFromCodeOptions
            {
                Repository = options.DocsFromCode.Repository?.Trim() ?? "",
                Label = options.DocsFromCode.Label?.Trim() ?? ""
            },
            DoNotMergeLabels = NormalizeList(options.DoNotMergeLabels),
            BotAuthors = NormalizeList(options.BotAuthors),
            NonBlockingCheckFailureRules = NormalizeCheckFailureRules(options.NonBlockingCheckFailureRules)
        };

    private static string[] NormalizeList(IEnumerable<string>? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static DashboardCheckFailureRuleOptions[] NormalizeCheckFailureRules(IEnumerable<DashboardCheckFailureRuleOptions>? rules) =>
        (rules ?? [])
            .Where(rule => rule is not null)
            .Select(rule => new DashboardCheckFailureRuleOptions
            {
                Repository = rule.Repository?.Trim() ?? "",
                Label = rule.Label?.Trim() ?? "",
                CheckNames = NormalizeList(rule.CheckNames),
                CheckNameContains = NormalizeList(rule.CheckNameContains)
            })
            .Where(rule =>
                rule.Repository.Length > 0
                && rule.Label.Length > 0
                && HasCheckMatcher(rule))
            .ToArray();

    private static bool HasCheckMatcher(DashboardCheckFailureRuleOptions rule) =>
        rule.CheckNames.Length > 0 || rule.CheckNameContains.Length > 0;

    private static IReadOnlyList<string> ReviewBucketLabels(
        AgentReviewQueueCandidate candidate,
        DashboardOptions options,
        DateTimeOffset now)
    {
        var pullRequest = candidate.PullRequest;
        var labels = new List<string>();
        if (HasRegressionSignal(pullRequest))
        {
            labels.Add(RegressionBucketLabel);
        }

        if (pullRequest.Draft)
        {
            labels.Add("Draft");
            return labels;
        }

        if (IsCommunityPullRequest(candidate, options, now) && !IsAgedOutCommunityPullRequest(candidate, options, now))
        {
            return labels;
        }

        if (IsBotAuthor(pullRequest.Author, options))
        {
            labels.Add("Bots / automation");
        }

        if (IsGeneratedDocsPullRequest(candidate, options))
        {
            labels.Add("Docs");
        }

        if (IsConfiguredCommunityRepository(candidate.Repository, options))
        {
            labels.Add("Community Toolkit");
        }

        var ciFailing = IsChecksFailing(candidate, options);
        if (ciFailing)
        {
            labels.Add("CI failing");
        }

        var mergeConflicts = HasMergeConflicts(pullRequest);
        if (mergeConflicts)
        {
            labels.Add("Merge conflicts");
        }

        var approvedButAging = IsApprovedButAging(pullRequest, now);
        if (approvedButAging)
        {
            labels.Add(ApprovedButAgingBucketLabel);
        }

        var unresolvedFeedback = pullRequest.Review.UnresolvedThreadCount > 0;
        if (unresolvedFeedback)
        {
            labels.Add("Unresolved feedback");
        }

        var unresolvedFeedbackBlocksMerge = unresolvedFeedback && pullRequest.Review.RequiresConversationResolution;
        if (pullRequest.Review.State.Equals("approved", StringComparison.OrdinalIgnoreCase)
            && !approvedButAging
            && !ciFailing
            && !IsChecksPending(candidate, options)
            && !unresolvedFeedbackBlocksMerge
            && !mergeConflicts
            && !HasNeedsAuthorActionLabel(pullRequest, options))
        {
            labels.Add("Ready to merge");
        }

        if (NeedsReReview(pullRequest))
        {
            labels.Add("Re-review needed");
        }

        if (pullRequest.Review.State.Equals("changes_requested", StringComparison.OrdinalIgnoreCase))
        {
            labels.Add("Author response");
        }

        if (IsIdle(pullRequest, now))
        {
            labels.Add("Stalled");
        }

        if (IsCommunityPullRequest(candidate, options, now))
        {
            labels.Add(IsAgedOutCommunityPullRequest(candidate, options, now) ? AgedOutCommunityBucketLabel : "Community");
        }

        if (IsQuickWin(candidate, options, now) && !ciFailing)
        {
            labels.Add("Quick wins");
        }

        if (NeedsReview(candidate, options))
        {
            labels.Add("Needs review");
        }

        if (labels.Count == 0)
        {
            labels.Add("Review started");
        }

        return labels;
    }

    private static string ReviewSignal(PullRequestSummary pullRequest, string bucketLabel) =>
        bucketLabel switch
        {
            RegressionBucketLabel => "Regression",
            ApprovedButAgingBucketLabel => "Approved",
            "CI failing" => pullRequest.Checks.FailureCount > 0 ? $"{pullRequest.Checks.FailureCount} failing check{(pullRequest.Checks.FailureCount == 1 ? "" : "s")}" : "CI failing",
            "Unresolved feedback" => $"{pullRequest.Review.UnresolvedThreadCount} unresolved thread{(pullRequest.Review.UnresolvedThreadCount == 1 ? "" : "s")}",
            "Ready to merge" => $"{pullRequest.Review.ApprovalCount} approval{(pullRequest.Review.ApprovalCount == 1 ? "" : "s")}",
            "Re-review needed" => "Pushed after review",
            "Quick wins" => $"{pullRequest.ChangedFiles} file{(pullRequest.ChangedFiles == 1 ? "" : "s")}",
            "Needs review" => "No reviews",
            "Stalled" => "Idle",
            "Author response" => "Changes requested",
            _ => bucketLabel
        };

    private static bool IsPullRequestWithinFocusAgeLimit(PullRequestSummary pullRequest, string bucketLabel, DateTimeOffset now) =>
        now - PullRequestFocusActivityAt(pullRequest, bucketLabel) <= s_focusAgeLimit;

    private static DateTimeOffset PullRequestFocusActivityAt(PullRequestSummary pullRequest, string bucketLabel) =>
        bucketLabel switch
        {
            ApprovedButAgingBucketLabel or "Ready to merge" => ReviewActivityAt(pullRequest),
            "Re-review needed" => pullRequest.LastCommitAt ?? DateTimeOffset.MinValue,
            "Author response" or "Review started" => pullRequest.Review.LastReviewedAt ?? pullRequest.UpdatedAt,
            "CI failing" => pullRequest.Checks.CompletedAt ?? pullRequest.UpdatedAt,
            _ => pullRequest.UpdatedAt
        };

    private static bool IsWaitingOnAuthor(IReadOnlyCollection<string> bucketLabels) =>
        bucketLabels.Contains("Author response") && !bucketLabels.Contains("Re-review needed");

    private static int FocusBucketRank(string label) =>
        s_focusBucketRanks.TryGetValue(label, out var rank) ? rank : int.MaxValue;

    private static int ListBucketRank(string label) =>
        s_listBucketRanks.TryGetValue(label, out var rank) ? rank : int.MaxValue;

    private static string Key(AgentReviewQueueCandidate candidate) =>
        $"{candidate.Repository.ToLowerInvariant()}#{candidate.PullRequest.Number}";

    private static bool HasNeedsAuthorActionLabel(PullRequestSummary pullRequest, DashboardOptions options) =>
        pullRequest.Labels.Any(label => options.DoNotMergeLabels.Contains(label, StringComparer.OrdinalIgnoreCase));

    private static bool HasRegressionSignal(PullRequestSummary pullRequest) =>
        pullRequest.Labels.Any(HasRegressionLabel)
        || pullRequest.LinkedIssues.Any(issue => issue.Labels.Any(HasRegressionLabel));

    private static bool HasRegressionLabel(string label) =>
        label.Contains("regression", StringComparison.OrdinalIgnoreCase);

    private static bool HasMergeConflicts(PullRequestSummary pullRequest) =>
        pullRequest.MergeableState?.Equals("dirty", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsApprovedButAging(PullRequestSummary pullRequest, DateTimeOffset now) =>
        pullRequest.Review.State.Equals("approved", StringComparison.OrdinalIgnoreCase)
        && ApprovalAgeAt(pullRequest) is { } approvedAt
        && now - approvedAt >= s_approvedAging;

    private static DateTimeOffset? ApprovalAgeAt(PullRequestSummary pullRequest) =>
        pullRequest.Review.LastApprovedAt ?? pullRequest.Review.LastReviewedAt;

    private static DateTimeOffset ReviewActivityAt(PullRequestSummary pullRequest)
    {
        if (pullRequest.Review.LastApprovedAt is { } approvedAt
            && pullRequest.Review.LastReviewedAt is { } reviewedAt)
        {
            return approvedAt > reviewedAt ? approvedAt : reviewedAt;
        }

        return pullRequest.Review.LastApprovedAt ?? pullRequest.Review.LastReviewedAt ?? pullRequest.UpdatedAt;
    }

    private static bool NeedsReReview(PullRequestSummary pullRequest) =>
        pullRequest.Review.LastReviewedAt is { } lastReviewedAt
        && pullRequest.LastCommitAt is { } lastCommitAt
        && (pullRequest.Review.State.Equals("reviewed", StringComparison.OrdinalIgnoreCase)
            || pullRequest.Review.State.Equals("changes_requested", StringComparison.OrdinalIgnoreCase))
        && lastCommitAt > lastReviewedAt;

    private static bool IsChecksFailing(AgentReviewQueueCandidate candidate, DashboardOptions options) =>
        candidate.PullRequest.Checks.State.Equals("failure", StringComparison.OrdinalIgnoreCase)
        && !IsNonBlockingAggregateFailure(candidate, options)
        && !NonBlockingOnlyFailureRule(candidate, options);

    private static bool IsChecksPending(AgentReviewQueueCandidate candidate, DashboardOptions options)
    {
        if (IsNonBlockingAggregateFailure(candidate, options))
        {
            return true;
        }

        if (NonBlockingOnlyFailureRule(candidate, options))
        {
            return candidate.PullRequest.Checks.PendingCount > 0;
        }

        return candidate.PullRequest.Checks.State.Equals("pending", StringComparison.OrdinalIgnoreCase)
            || candidate.PullRequest.Checks.State.Equals("unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NonBlockingOnlyFailureRule(AgentReviewQueueCandidate candidate, DashboardOptions options)
    {
        var pullRequest = candidate.PullRequest;
        if (!pullRequest.Checks.State.Equals("failure", StringComparison.OrdinalIgnoreCase)
            || pullRequest.Checks.FailureCount != pullRequest.Checks.FailingChecks.Count
            || pullRequest.Checks.FailingChecks.Count == 0)
        {
            return false;
        }

        var matchingRules = pullRequest.Checks.FailingChecks.Select(check =>
            options.NonBlockingCheckFailureRules.FirstOrDefault(rule =>
                candidate.Repository.Equals(rule.Repository, StringComparison.OrdinalIgnoreCase)
                && MatchesNonBlockingCheckFailureName(rule, check.Name)));
        return matchingRules.All(rule => rule is not null);
    }

    private static bool MatchesNonBlockingCheckFailureName(DashboardCheckFailureRuleOptions rule, string name)
    {
        var normalizedName = name.Trim();
        return rule.CheckNames.Contains(normalizedName, StringComparer.OrdinalIgnoreCase)
            || rule.CheckNameContains.Any(fragment => normalizedName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNonBlockingAggregateFailure(AgentReviewQueueCandidate candidate, DashboardOptions options)
    {
        var checks = candidate.PullRequest.Checks;
        return checks.State.Equals("failure", StringComparison.OrdinalIgnoreCase)
            && options.NonBlockingCheckFailureRules.Any(rule => candidate.Repository.Equals(rule.Repository, StringComparison.OrdinalIgnoreCase))
            && checks.TotalCount == 0
            && checks.FailureCount == 0
            && checks.FailingChecks.Count == 0;
    }

    private static bool IsGeneratedDocsPullRequest(AgentReviewQueueCandidate candidate, DashboardOptions options) =>
        !string.IsNullOrWhiteSpace(options.DocsFromCode.Repository)
        && !string.IsNullOrWhiteSpace(options.DocsFromCode.Label)
        && candidate.Repository.Equals(options.DocsFromCode.Repository, StringComparison.OrdinalIgnoreCase)
        && candidate.PullRequest.Labels.Contains(options.DocsFromCode.Label, StringComparer.OrdinalIgnoreCase);

    private static bool IsConfiguredCommunityRepository(string repository, DashboardOptions options) =>
        options.CommunityRepositories.Contains(repository, StringComparer.OrdinalIgnoreCase);

    private static bool IsCommunityPullRequest(AgentReviewQueueCandidate candidate, DashboardOptions options, DateTimeOffset now) =>
        IsCommunityAuthor(candidate.PullRequest.Author, options)
        && !IsConfiguredCommunityRepository(candidate.Repository, options);

    private static bool IsAgedOutCommunityPullRequest(AgentReviewQueueCandidate candidate, DashboardOptions options, DateTimeOffset now) =>
        IsCommunityPullRequest(candidate, options, now)
        && now - candidate.PullRequest.UpdatedAt > s_focusAgeLimit;

    private static bool IsQuickWin(AgentReviewQueueCandidate candidate, DashboardOptions options, DateTimeOffset now) =>
        candidate.PullRequest.Review.State.Equals("waiting", StringComparison.OrdinalIgnoreCase)
        && candidate.PullRequest.Review.UnresolvedThreadCount == 0
        && !HasMergeConflicts(candidate.PullRequest)
        && IsCoreTeamAuthor(candidate.PullRequest.Author, options)
        && !TargetsCurrentRelease(candidate.PullRequest, options)
        && candidate.PullRequest.LinkedIssues.Count <= 1
        && candidate.PullRequest.CommitCount <= 2
        && candidate.PullRequest.ChangedFiles is > 0 and <= QuickWinFileThreshold
        && ChangedLineCount(candidate.PullRequest) is > 0 and <= QuickWinLineThreshold
        && !IsIdle(candidate.PullRequest, now);

    private static bool NeedsReview(AgentReviewQueueCandidate candidate, DashboardOptions options) =>
        candidate.PullRequest.Review.State.Equals("waiting", StringComparison.OrdinalIgnoreCase)
        && candidate.PullRequest.Review.UnresolvedThreadCount == 0
        && !HasMergeConflicts(candidate.PullRequest)
        && IsCoreTeamAuthor(candidate.PullRequest.Author, options);

    private static bool IsIdle(PullRequestSummary pullRequest, DateTimeOffset now) =>
        now - pullRequest.UpdatedAt >= s_stalledPullRequest;

    private static int ChangedLineCount(PullRequestSummary pullRequest) =>
        pullRequest.Additions + pullRequest.Deletions;

    internal static bool TargetsCurrentRelease(PullRequestSummary pullRequest, DashboardOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.CurrentRelease))
        {
            return false;
        }

        var currentRelease = options.CurrentRelease.Trim();
        return ReleaseSignalMatches(pullRequest.Title, currentRelease)
            || ReleaseSignalMatches(pullRequest.Milestone, currentRelease)
            || pullRequest.Labels.Any(label => ReleaseSignalMatches(label, currentRelease))
            || pullRequest.LinkedIssues.Any(issue =>
                ReleaseSignalMatches(issue.Title, currentRelease)
                || ReleaseSignalMatches(issue.Milestone, currentRelease)
                || issue.Labels.Any(label => ReleaseSignalMatches(label, currentRelease)));
    }

    private static bool ReleaseSignalMatches(string? value, string release) =>
        !string.IsNullOrWhiteSpace(value)
        && System.Text.RegularExpressions.Regex.IsMatch(
            value,
            $"(^|[^0-9]){System.Text.RegularExpressions.Regex.Escape(release)}([^0-9]|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsCommunityAuthor(string author, DashboardOptions options) =>
        !IsBotAuthor(author, options) && !IsCoreTeamAuthor(author, options);

    private static bool IsBotAuthor(string author, DashboardOptions options)
    {
        if (IsCopilotAttributedAuthor(author))
        {
            return false;
        }

        var normalized = author.ToLowerInvariant();
        if (normalized.EndsWith("[bot]", StringComparison.Ordinal)
            || normalized.Contains("bot", StringComparison.Ordinal)
            || normalized.Equals("copilot", StringComparison.Ordinal)
            || normalized.Equals("github-actions", StringComparison.Ordinal))
        {
            return true;
        }

        return options.BotAuthors.Contains(author, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsCoreTeamAuthor(string author, DashboardOptions options) =>
        MatchingCoreTeamMember(author, options) is not null;

    private static string? MatchingCoreTeamMember(string author, DashboardOptions options)
    {
        var authorKey = ActorIdentityKey(author);
        var matchingMember = options.CoreTeamMembers.FirstOrDefault(member => ActorIdentityKey(member) == authorKey);
        if (matchingMember is not null)
        {
            return matchingMember;
        }

        var aliasBase = ConfiguredTeamAliasBase(author, options);
        if (aliasBase is null)
        {
            return null;
        }

        var aliasBaseKey = ActorIdentityKey(aliasBase);
        return options.CoreTeamMembers.FirstOrDefault(member => ActorIdentityKey(member) == aliasBaseKey) ?? author;
    }

    private static string? ConfiguredTeamAliasBase(string author, DashboardOptions options)
    {
        var normalizedAuthor = StripCopilotAttribution(author);
        var suffix = options.CoreTeamMemberAliasSuffixes.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(candidate)
            && normalizedAuthor.EndsWith(candidate, StringComparison.OrdinalIgnoreCase)
            && normalizedAuthor.Length > candidate.Length);

        return suffix is null ? null : normalizedAuthor[..^suffix.Length];
    }

    private static string ActorIdentityKey(string actor)
    {
        var human = StripCopilotAttribution(actor).ToLowerInvariant();
        return string.Concat(human.Where(char.IsAsciiLetterOrDigit));
    }

    private static string StripCopilotAttribution(string actor) =>
        IsCopilotAttributedAuthor(actor)
            ? actor[..^"/copilot".Length]
            : actor;

    private static bool IsCopilotAttributedAuthor(string actor) =>
        actor.EndsWith("/copilot", StringComparison.OrdinalIgnoreCase);

    private sealed class AgentReviewQueueItemComparer(DateTimeOffset now) : IComparer<AgentReviewQueueItem>
    {
        public static AgentReviewQueueItemComparer Create(DateTimeOffset now) => new(now);

        public int Compare(AgentReviewQueueItem? first, AgentReviewQueueItem? second)
        {
            if (ReferenceEquals(first, second))
            {
                return 0;
            }

            if (first is null)
            {
                return -1;
            }

            if (second is null)
            {
                return 1;
            }

            return CompareSameBucketWait(first, second)
                ?? CompareBy(ListBucketRank(first.BucketLabel).CompareTo(ListBucketRank(second.BucketLabel)))
                ?? CompareBy((IsRecentlyUpdated(second.PullRequest) ? 1 : 0) - (IsRecentlyUpdated(first.PullRequest) ? 1 : 0))
                ?? CompareBy(first.PullRequest.CreatedAt.CompareTo(second.PullRequest.CreatedAt))
                ?? CompareBy(string.Compare(first.Repository, second.Repository, StringComparison.Ordinal))
                ?? first.PullRequest.Number.CompareTo(second.PullRequest.Number);
        }

        private static int? CompareBy(int value) => value == 0 ? null : value;

        private int? CompareSameBucketWait(AgentReviewQueueItem first, AgentReviewQueueItem second)
        {
            if (!first.BucketLabel.Equals(second.BucketLabel, StringComparison.Ordinal))
            {
                return null;
            }

            var wait = BucketWaitTime(first.PullRequest, first.BucketLabel).CompareTo(BucketWaitTime(second.PullRequest, second.BucketLabel));
            if (wait != 0)
            {
                return wait;
            }

            var quickWin = QuickWinScore(first.PullRequest, first.BucketLabel).CompareTo(QuickWinScore(second.PullRequest, second.BucketLabel));
            return quickWin == 0 ? null : quickWin;
        }

        private bool IsRecentlyUpdated(PullRequestSummary pullRequest) =>
            now - pullRequest.UpdatedAt <= s_recentlyUpdatedWindow;

        private static DateTimeOffset BucketWaitTime(PullRequestSummary pullRequest, string bucketLabel) =>
            bucketLabel switch
            {
                ApprovedButAgingBucketLabel or "Ready to merge" => ApprovalAgeAt(pullRequest) ?? DateTimeOffset.MaxValue,
                "Re-review needed" => pullRequest.LastCommitAt ?? DateTimeOffset.MaxValue,
                "Author response" or "Review started" => pullRequest.Review.LastReviewedAt ?? pullRequest.UpdatedAt,
                _ => pullRequest.UpdatedAt
            };

        private static int QuickWinScore(PullRequestSummary pullRequest, string bucketLabel) =>
            bucketLabel == "Quick wins"
                ? pullRequest.Additions + pullRequest.Deletions + (pullRequest.ChangedFiles * 10) + (pullRequest.CommitCount * 5)
                : 0;
    }
}

record AgentReviewQueueResponse(
    IReadOnlyList<AgentReviewQueueItem> Items,
    IReadOnlyList<AgentReviewQueueRepositoryResult> Repositories,
    int TotalCount,
    DateTimeOffset GeneratedAt);

record AgentReviewQueue(
    IReadOnlyList<AgentReviewQueueItem> Items,
    int TotalCount);

record AgentReviewQueueItem(
    string Repository,
    PullRequestSummary PullRequest,
    string BucketLabel,
    string Reason);

record AgentReviewQueueRepositoryResult(
    string Repository,
    int PullRequestCount,
    PullRequestListSnapshot? Snapshot,
    string? Error);

readonly record struct AgentReviewQueueCandidate(string Repository, PullRequestSummary PullRequest);
