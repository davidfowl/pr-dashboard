using System.Net;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

readonly partial record struct RepositoryName(string Owner, string Name)
{
    public static bool TryParse(string value, out RepositoryName repositoryName)
    {
        repositoryName = default;

        if (RepositoryRegex().Match(value.Trim()) is not { Success: true } match)
        {
            return false;
        }

        repositoryName = new RepositoryName(match.Groups["owner"].Value, match.Groups["repo"].Value);
        return true;
    }

    public override string ToString() => $"{Owner}/{Name}";

    [GeneratedRegex("^(?<owner>[A-Za-z0-9._-]+)/(?<repo>[A-Za-z0-9._-]+)$")]
    private static partial Regex RepositoryRegex();
}

sealed class GitHubApiException(HttpStatusCode statusCode, string message) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

record TokenResult(string Value, string Source);

record AuthStatusResponse(bool Authenticated, bool Configured, bool CanLogin, string? Source, string? Login, string Message);

record PullRequestListResponse(string Repository, IReadOnlyList<PullRequestSummary> PullRequests);

record PullRequestChecksRequest(IReadOnlyList<PullRequestChecksRequestItem>? PullRequests);

record PullRequestChecksRequestItem(int Number, string? HeadSha);

record PullRequestChecksResponse(string Repository, IReadOnlyList<PullRequestChecksSummary> PullRequests);

record PullRequestChecksSummary(int Number, string HeadSha, ChecksStatus Checks);

record PullRequestSummary(
    int Number,
    string Title,
    string State,
    bool Draft,
    string Author,
    string HtmlUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> RequestedReviewers,
    string? Milestone,
    IReadOnlyList<LinkedIssueSummary> LinkedIssues,
    int CommitCount,
    int Additions,
    int Deletions,
    int ChangedFiles,
    DateTimeOffset? LastCommitAt,
    string? HeadSha,
    ReviewStatus Review,
    ChecksStatus Checks)
{
    public static PullRequestSummary FromDto(GitHubPullRequestDto pullRequest) =>
        new(
            pullRequest.Number,
            pullRequest.Title ?? "",
            pullRequest.State ?? "",
            pullRequest.Draft,
            pullRequest.User?.Login ?? "unknown",
            pullRequest.HtmlUrl ?? "",
            pullRequest.CreatedAt,
            pullRequest.UpdatedAt,
            pullRequest.Labels
                .Select(label => label.Name)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label!)
                .ToArray(),
            pullRequest.RequestedReviewers
                .Select(reviewer => reviewer.Login)
                .Concat(pullRequest.RequestedTeams.Select(team => team.Name))
                .Where(reviewer => !string.IsNullOrWhiteSpace(reviewer))
                .Select(reviewer => reviewer!)
                .ToArray(),
            pullRequest.Milestone?.Title,
            [],
            pullRequest.Commits,
            pullRequest.Additions,
            pullRequest.Deletions,
            pullRequest.ChangedFiles,
            null,
            pullRequest.Head?.Sha,
            ReviewStatus.Waiting,
            pullRequest.State?.Equals("open", StringComparison.OrdinalIgnoreCase) is true
                && !string.IsNullOrEmpty(pullRequest.Head?.Sha)
                    ? ChecksStatus.Unknown
                    : ChecksStatus.None);
}

record LinkedIssueSummary(
    string Repository,
    int Number,
    string Title,
    string? Milestone,
    IReadOnlyList<string> Labels,
    string HtmlUrl)
{
    public static LinkedIssueSummary FromDto(RepositoryName repositoryName, GitHubIssueDto issue) =>
        new(
            repositoryName.ToString(),
            issue.Number,
            issue.Title ?? "",
            issue.Milestone?.Title,
            issue.Labels
                .Select(label => label.Name)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label!)
                .ToArray(),
            issue.HtmlUrl ?? "");
}

record ReviewStatus(
    string State,
    string? LatestState,
    int ReviewerCount,
    int ApprovalCount,
    int ChangesRequestedCount,
    int CommentedReviewCount,
    DateTimeOffset? LastApprovedAt,
    DateTimeOffset? LastReviewedAt)
{
    public static ReviewStatus Waiting { get; } = new(
        State: "waiting",
        LatestState: null,
        ReviewerCount: 0,
        ApprovalCount: 0,
        ChangesRequestedCount: 0,
        CommentedReviewCount: 0,
        LastApprovedAt: null,
        LastReviewedAt: null);
}

record ChecksStatus(
    string State,
    int TotalCount,
    int SuccessCount,
    int FailureCount,
    int PendingCount,
    int NeutralCount,
    int SkippedCount,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<FailingCheck> FailingChecks)
{
    public static ChecksStatus Unknown { get; } = new(
        State: "unknown",
        TotalCount: 0,
        SuccessCount: 0,
        FailureCount: 0,
        PendingCount: 0,
        NeutralCount: 0,
        SkippedCount: 0,
        CompletedAt: null,
        FailingChecks: []);

    public static ChecksStatus None { get; } = new(
        State: "none",
        TotalCount: 0,
        SuccessCount: 0,
        FailureCount: 0,
        PendingCount: 0,
        NeutralCount: 0,
        SkippedCount: 0,
        CompletedAt: null,
        FailingChecks: []);
}

record FailingCheck(string Name, string? Conclusion, string? HtmlUrl);

record ReviewEvent(string Actor, string State, DateTimeOffset SubmittedAt)
{
    public static ReviewEvent FromDto(GitHubReviewDto review) =>
        new(
            Actor: review.User?.Login ?? "unknown",
            State: review.State ?? "UNKNOWN",
            SubmittedAt: review.SubmittedAt);
}

record PullRequestDetails(
    DateTimeOffset CreatedAt,
    string Author,
    DateTimeOffset? MergedAt,
    int CommitCount,
    int Additions,
    int Deletions,
    int ChangedFiles,
    string? HeadSha,
    string? MergeableState)
{
    public static PullRequestDetails FromDto(GitHubPullRequestDto pullRequest) =>
        new(
            pullRequest.CreatedAt,
            pullRequest.User?.Login ?? "unknown",
            pullRequest.MergedAt,
            pullRequest.Commits,
            pullRequest.Additions,
            pullRequest.Deletions,
            pullRequest.ChangedFiles,
            pullRequest.Head?.Sha,
            pullRequest.MergeableState);
}

record TimelineResponse(
    string Repository,
    int Number,
    TimelineStats Stats,
    ChecksStatus Checks,
    string? MergeableState,
    IReadOnlyList<TimelineItem> Items);

record TimelineStats(
    int CommitCount,
    int HumanCommenterCount,
    int HumanCommentCount,
    int ReviewCount,
    int ApprovalCount,
    double? FirstHumanCommentDelayMs,
    double? FirstReviewDelayMs,
    double? FirstApprovalDelayMs,
    double? ApprovalToMergeDelayMs,
    double? CreatedToMergeDelayMs,
    double? AverageHumanCommentGapMs,
    double? LongestHumanCommentGapMs,
    DateTimeOffset? MergedAt,
    IReadOnlyList<DeveloperStats> Developers)
{
    public static TimelineStats Create(PullRequestDetails pullRequest, IReadOnlyList<TimelineItem> timeline)
    {
        var humanComments = timeline
            .Where(item => item.Event == "commented"
                && IsHuman(item.Actor)
                && !SameActor(item.Actor, pullRequest.Author))
            .OrderBy(item => item.OccurredAt)
            .ToArray();

        var humanReviews = timeline
            .Where(item => item.Event == "reviewed" && IsHuman(item.Actor))
            .OrderBy(item => item.OccurredAt)
            .ToArray();

        var approvals = humanReviews
            .Where(item => item.State?.Equals("APPROVED", StringComparison.OrdinalIgnoreCase) is true)
            .ToArray();

        var mergedAt = pullRequest.MergedAt
            ?? timeline.FirstOrDefault(item => item.Event == "merged")?.OccurredAt;
        var lastApprovalBeforeMerge = mergedAt is null
            ? null
            : approvals.LastOrDefault(item => item.OccurredAt <= mergedAt.Value);

        return new TimelineStats(
            CommitCount: pullRequest.CommitCount,
            HumanCommenterCount: humanComments.Select(item => NormalizeActorIdentity(item.Actor)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            HumanCommentCount: humanComments.Length,
            ReviewCount: humanReviews.Length,
            ApprovalCount: approvals.Length,
            FirstHumanCommentDelayMs: DelayMs(pullRequest.CreatedAt, humanComments.FirstOrDefault()?.OccurredAt),
            FirstReviewDelayMs: DelayMs(pullRequest.CreatedAt, humanReviews.FirstOrDefault()?.OccurredAt),
            FirstApprovalDelayMs: DelayMs(pullRequest.CreatedAt, approvals.FirstOrDefault()?.OccurredAt),
            ApprovalToMergeDelayMs: DelayMs(lastApprovalBeforeMerge?.OccurredAt, mergedAt),
            CreatedToMergeDelayMs: DelayMs(pullRequest.CreatedAt, mergedAt),
            AverageHumanCommentGapMs: AverageGapMs(humanComments),
            LongestHumanCommentGapMs: LongestGapMs(humanComments),
            MergedAt: mergedAt,
            Developers: CreateDeveloperStats(timeline));
    }

    private static IReadOnlyList<DeveloperStats> CreateDeveloperStats(IReadOnlyList<TimelineItem> timeline) =>
        timeline
            .Where(item => IsHuman(item.Actor))
            .GroupBy(item => NormalizeActorIdentity(item.Actor), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group.OrderBy(item => item.OccurredAt).ToArray();
                return new DeveloperStats(
                    Actor: PreferredActorName(ordered.Select(item => item.Actor)),
                    ActivityCount: ordered.Length,
                    CommitCount: ordered.Count(item => item.Event == "committed"),
                    CommentCount: ordered.Count(item => item.Event == "commented"),
                    ReviewCount: ordered.Count(item => item.Event == "reviewed"),
                    ApprovalCount: ordered.Count(item => item.Event == "reviewed" && item.State?.Equals("APPROVED", StringComparison.OrdinalIgnoreCase) is true),
                    ChangesRequestedCount: ordered.Count(item => item.Event == "reviewed" && item.State?.Equals("CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase) is true),
                    FirstActivityAt: ordered.First().OccurredAt,
                    LastActivityAt: ordered.Last().OccurredAt);
            })
            .OrderByDescending(developer => developer.ActivityCount)
            .ThenBy(developer => developer.Actor)
            .ToArray();

    private static bool IsHuman(string actor) =>
        !string.IsNullOrWhiteSpace(actor)
        && !actor.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)
        && !s_knownBotActors.Contains(actor);

    private static bool SameActor(string first, string second) =>
        NormalizeActorIdentity(first).Equals(NormalizeActorIdentity(second), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeActorIdentity(string actor) =>
        string.Concat(actor.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string PreferredActorName(IEnumerable<string> actors) =>
        actors
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(actor => actor.Any(char.IsWhiteSpace))
            .ThenBy(actor => actor.Length)
            .First();

    private static double? DelayMs(DateTimeOffset? start, DateTimeOffset? end) =>
        start is null || end is null ? null : Math.Max(0, (end.Value - start.Value).TotalMilliseconds);

    private static double? AverageGapMs(IReadOnlyList<TimelineItem> items)
    {
        if (items.Count < 2)
        {
            return null;
        }

        return items.Zip(items.Skip(1), (first, second) => (second.OccurredAt - first.OccurredAt).TotalMilliseconds)
            .Average();
    }

    private static double? LongestGapMs(IReadOnlyList<TimelineItem> items)
    {
        if (items.Count < 2)
        {
            return null;
        }

        return items.Zip(items.Skip(1), (first, second) => (second.OccurredAt - first.OccurredAt).TotalMilliseconds)
            .Max();
    }

    private static readonly HashSet<string> s_knownBotActors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Copilot",
        "dependabot",
        "dependabot-preview",
        "github-actions",
        "renovate"
    };
}

record DeveloperStats(
    string Actor,
    int ActivityCount,
    int CommitCount,
    int CommentCount,
    int ReviewCount,
    int ApprovalCount,
    int ChangesRequestedCount,
    DateTimeOffset FirstActivityAt,
    DateTimeOffset LastActivityAt);

record TimelineItem(
    string Id,
    string Event,
    string Actor,
    DateTimeOffset OccurredAt,
    string? State,
    string Summary,
    string? Body,
    string? HtmlUrl)
{
    public static TimelineItem FromDto(GitHubTimelineItemDto item)
    {
        var eventName = item.Event ?? "event";
        var occurredAt = item.CreatedAt
            ?? item.SubmittedAt
            ?? item.CommittedAt
            ?? item.Author?.Date
            ?? item.Committer?.Date
            ?? DateTimeOffset.MinValue;
        var actor = item.Actor?.Login
            ?? item.User?.Login
            ?? item.Author?.Login
            ?? item.Author?.Name
            ?? item.Committer?.Login
            ?? item.Committer?.Name
            ?? "unknown";

        return new TimelineItem(
            Id: item.Id?.ToString(CultureInfo.InvariantCulture) ?? item.Sha ?? $"{eventName}-{occurredAt.ToUnixTimeMilliseconds()}",
            Event: eventName,
            Actor: actor,
            OccurredAt: occurredAt,
            State: item.State,
            Summary: BuildSummary(item, eventName, actor),
            Body: item.Body,
            HtmlUrl: item.HtmlUrl);
    }

    private static string BuildSummary(GitHubTimelineItemDto item, string eventName, string actor)
    {
        var normalizedEvent = eventName.Replace('_', ' ');
        return eventName switch
        {
            "commented" => $"{actor} commented",
            "committed" => $"{actor} pushed commit {ShortSha(item.Sha ?? item.CommitId)}",
            "reviewed" => $"{actor} reviewed with state {item.State ?? "unknown"}",
            "review_requested" => $"{actor} requested review from {item.RequestedReviewer?.Login ?? item.RequestedTeam?.Name ?? "someone"}",
            "ready_for_review" => $"{actor} marked the PR ready for review",
            "converted_to_draft" => $"{actor} converted the PR to draft",
            "labeled" => $"{actor} added label {item.Label?.Name ?? "unknown"}",
            "unlabeled" => $"{actor} removed label {item.Label?.Name ?? "unknown"}",
            "assigned" => $"{actor} assigned {item.Assignee?.Login ?? "someone"}",
            "unassigned" => $"{actor} unassigned {item.Assignee?.Login ?? "someone"}",
            "cross-referenced" => $"{actor} cross-referenced another issue or PR",
            "renamed" => $"{actor} renamed the title",
            "closed" => $"{actor} closed the PR",
            "reopened" => $"{actor} reopened the PR",
            "merged" => $"{actor} merged the PR",
            _ => $"{actor} {normalizedEvent}"
        };
    }

    private static string ShortSha(string? sha) => string.IsNullOrWhiteSpace(sha)
        ? "unknown"
        : sha[..Math.Min(7, sha.Length)];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubActorDto))]
[JsonSerializable(typeof(GitHubCheckRunsResponseDto))]
[JsonSerializable(typeof(GitHubCombinedStatusDto))]
[JsonSerializable(typeof(GitHubErrorDto))]
[JsonSerializable(typeof(GitHubIssueDto))]
[JsonSerializable(typeof(GitHubIssuePullRequestDto))]
[JsonSerializable(typeof(GitHubMilestoneDto))]
[JsonSerializable(typeof(GitHubPullRequestCommitDto[]))]
[JsonSerializable(typeof(GitHubPullRequestDto))]
[JsonSerializable(typeof(GitHubPullRequestDto[]))]
[JsonSerializable(typeof(GitHubReviewDto[]))]
[JsonSerializable(typeof(GitHubTimelineItemDto[]))]
partial class GitHubJsonSerializerContext : JsonSerializerContext;

sealed class GitHubActorDto
{
    public string? Login { get; init; }
    public string? Name { get; init; }
    public DateTimeOffset? Date { get; init; }
}

sealed class GitHubErrorDto
{
    public string? Message { get; init; }
}

sealed class GitHubLabelDto
{
    public string? Name { get; init; }
}

sealed class GitHubTeamDto
{
    public string? Name { get; init; }
}

sealed class GitHubMilestoneDto
{
    public string? Title { get; init; }
}

sealed class GitHubIssuePullRequestDto
{
    public string? Url { get; init; }
}

sealed class GitHubIssueDto
{
    public int Number { get; init; }
    public string? Title { get; init; }
    public string? HtmlUrl { get; init; }
    public string? RepositoryUrl { get; init; }
    public GitHubLabelDto[] Labels { get; init; } = [];
    public GitHubMilestoneDto? Milestone { get; init; }
    public GitHubIssuePullRequestDto? PullRequest { get; init; }
}

sealed class GitHubPullRequestDto
{
    public int Number { get; init; }
    public string? Title { get; init; }
    public string? State { get; init; }
    public bool Draft { get; init; }
    public GitHubActorDto? User { get; init; }
    public string? HtmlUrl { get; init; }
    public string? Body { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public GitHubLabelDto[] Labels { get; init; } = [];
    public GitHubActorDto[] RequestedReviewers { get; init; } = [];
    public GitHubTeamDto[] RequestedTeams { get; init; } = [];
    public GitHubMilestoneDto? Milestone { get; init; }
    public DateTimeOffset? MergedAt { get; init; }
    public int Commits { get; init; }
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public int ChangedFiles { get; init; }
    public GitHubPullRequestRefDto? Head { get; init; }
    public string? MergeableState { get; init; }
}

sealed class GitHubPullRequestRefDto
{
    public string? Sha { get; init; }
    public string? Ref { get; init; }
}

sealed class GitHubCheckRunsResponseDto
{
    public int TotalCount { get; init; }
    public GitHubCheckRunDto[] CheckRuns { get; init; } = [];
}

sealed class GitHubCheckRunDto
{
    public long Id { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }
    public string? Conclusion { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? HtmlUrl { get; init; }
}

sealed class GitHubCombinedStatusDto
{
    public string? State { get; init; }
    public int TotalCount { get; init; }
    public GitHubStatusDto[] Statuses { get; init; } = [];
}

sealed class GitHubStatusDto
{
    public string? State { get; init; }
    public string? Context { get; init; }
    public string? Description { get; init; }
    public string? TargetUrl { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

sealed class GitHubCommitDto
{
    public GitHubActorDto? Author { get; init; }
    public GitHubActorDto? Committer { get; init; }
}

sealed class GitHubPullRequestCommitDto
{
    public GitHubCommitDto? Commit { get; init; }
}

sealed class GitHubReviewDto
{
    public GitHubActorDto? User { get; init; }
    public string? State { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }
}

sealed class GitHubTimelineItemDto
{
    public long? Id { get; init; }
    public string? Sha { get; init; }
    public string? CommitId { get; init; }
    public string? Event { get; init; }
    public GitHubActorDto? Actor { get; init; }
    public GitHubActorDto? User { get; init; }
    public GitHubActorDto? Author { get; init; }
    public GitHubActorDto? Committer { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }
    public DateTimeOffset? CommittedAt { get; init; }
    public string? State { get; init; }
    public string? Body { get; init; }
    public string? HtmlUrl { get; init; }
    public GitHubActorDto? RequestedReviewer { get; init; }
    public GitHubTeamDto? RequestedTeam { get; init; }
    public GitHubLabelDto? Label { get; init; }
    public GitHubActorDto? Assignee { get; init; }
}
