namespace pr_timeline_app.Tests;

public sealed class ReadyToMergeDetectionTests
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.UnixEpoch + TimeSpan.FromDays(30);
    private static readonly HashSet<string> s_doNotMergeLabels =
        new(StringComparer.OrdinalIgnoreCase) { "needs-author-action", "no-merge" };

    [Fact]
    public void EventKeyAndTryGetRepositoryFollowConventions()
    {
        Assert.Equal("ready_to_merge:microsoft/aspire#101", ReadyToMergeDetection.EventKey("Microsoft/Aspire", 101));
        Assert.True(ReadyToMergeDetection.TryGetRepository("ready_to_merge:Microsoft/Aspire#101", out var repo));
        Assert.Equal("microsoft/aspire", repo);
        Assert.False(ReadyToMergeDetection.TryGetRepository("review_requested:foo/bar#1", out _));
    }

    [Fact]
    public void DetectYieldsAuthorFirstThenEnabledApprovers()
    {
        var pr = ReadyPr(1, ownerUserId: 7, approverIds: [8, 9]);

        var detected = ReadyToMergeDetection
            .DetectForRepository("O/R", [pr], new HashSet<long> { 7, 8, 9 }, s_now, s_doNotMergeLabels)
            .ToList();

        Assert.Equal(3, detected.Count);
        Assert.Equal(7, detected[0].UserId);
        Assert.Equal(ReadyToMergeRole.Author, detected[0].Role);
        Assert.Equal("o/r", detected[0].Repository);
        Assert.Equal("/#pr/o%2Fr/1", detected[0].Url);
        Assert.Equal([ReadyToMergeRole.Approver, ReadyToMergeRole.Approver], detected.Skip(1).Select(d => d.Role));
    }

    [Fact]
    public void DetectSkipsUsersWhoAreNotEnabled()
    {
        var pr = ReadyPr(1, ownerUserId: 7, approverIds: [8]);

        var detected = ReadyToMergeDetection
            .DetectForRepository("o/r", [pr], new HashSet<long> { 8 }, s_now, s_doNotMergeLabels)
            .ToList();

        Assert.Single(detected);
        Assert.Equal(8, detected[0].UserId);
        Assert.Equal(ReadyToMergeRole.Approver, detected[0].Role);
    }

    [Fact]
    public void AuthorWhoAlsoApprovedIsNaggedOnceAsAuthor()
    {
        var pr = ReadyPr(1, ownerUserId: 7, approverIds: [7, 8]);

        var detected = ReadyToMergeDetection
            .DetectForRepository("o/r", [pr], new HashSet<long> { 7, 8 }, s_now, s_doNotMergeLabels)
            .ToList();

        Assert.Equal(2, detected.Count);
        Assert.Equal(7, detected[0].UserId);
        Assert.Equal(ReadyToMergeRole.Author, detected[0].Role);
        Assert.Equal(8, detected[1].UserId);
        Assert.Equal(ReadyToMergeRole.Approver, detected[1].Role);
    }

    [Fact]
    public void IsReadyToMergeAcceptsAFreshlyApprovedCleanPr()
    {
        Assert.True(ReadyToMergeDetection.IsReadyToMerge(ReadyPr(1, 7, [8]), s_now, s_doNotMergeLabels));
    }

    [Fact]
    public void IsReadyToMergeRejectsIneligiblePrs()
    {
        var notReady = new[]
        {
            ReadyPr(1, 7, [8]) with { Draft = true },
            ReadyPr(1, 7, [8]) with { State = "closed" },
            ReadyPr(1, 7, [8]) with { Review = Approved([8]) with { State = "changes_requested" } },
            ReadyPr(1, 7, [8]) with { Review = Approved([8]) with { ApprovalCount = 0 } },
            // Aging: approved more than two days ago.
            ReadyPr(1, 7, [8]) with { Review = Approved([8]) with { LastApprovedAt = s_now - TimeSpan.FromDays(3) } },
            ReadyPr(1, 7, [8]) with { Checks = Failing() },
            ReadyPr(1, 7, [8]) with { Checks = Pending() },
            ReadyPr(1, 7, [8]) with { Checks = ChecksStatus.Unknown },
            ReadyPr(1, 7, [8]) with { Review = Approved([8]) with { UnresolvedThreadCount = 1, RequiresConversationResolution = true } },
            ReadyPr(1, 7, [8]) with { MergeableState = "dirty" },
            ReadyPr(1, 7, [8]) with { Labels = ["no-merge"] },
            ReadyPr(1, 7, [8]) with { Labels = ["needs-author-action"] },
        };

        Assert.All(notReady, pullRequest => Assert.False(ReadyToMergeDetection.IsReadyToMerge(pullRequest, s_now, s_doNotMergeLabels)));
    }

    [Fact]
    public void IsReadyToMergeUsesConfiguredDoNotMergeLabels()
    {
        var configuredLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "blocked-by-policy" };

        Assert.False(ReadyToMergeDetection.IsReadyToMerge(
            ReadyPr(1, 7, [8]) with { Labels = ["BLOCKED-BY-POLICY"] },
            s_now,
            configuredLabels));
        Assert.True(ReadyToMergeDetection.IsReadyToMerge(
            ReadyPr(2, 7, [8]) with { Labels = ["no-merge"] },
            s_now,
            configuredLabels));
    }

    [Fact]
    public void IsReadyToMergeUsesConfiguredNonBlockingCheckFailureRules()
    {
        var nonBlockingRules = new[]
        {
            null!,
            new DashboardCheckFailureRuleOptions
            {
                Repository = "o/r",
                CheckNames = [null!, " "],
                CheckNameContains = null!
            },
            new DashboardCheckFailureRuleOptions
            {
                Repository = "O/R",
                CheckNames = [null!, "GitOps/GitHubPop"],
                CheckNameContains = [null!, " ", "proof of presence"]
            }
        };

        Assert.True(ReadyToMergeDetection.IsReadyToMerge(
            "o/r",
            ReadyPr(1, 7, [8]) with { Checks = Failing("GitOps/GitHubPop") },
            s_now,
            s_doNotMergeLabels,
            nonBlockingRules));
        Assert.True(ReadyToMergeDetection.IsReadyToMerge(
            "o/r",
            ReadyPr(2, 7, [8]) with { Checks = Failing("required proof of presence") },
            s_now,
            s_doNotMergeLabels,
            nonBlockingRules));
    }

    [Fact]
    public void IsReadyToMergeRejectsPendingOrMixedNonBlockingFailures()
    {
        var nonBlockingRules = new[]
        {
            new DashboardCheckFailureRuleOptions
            {
                Repository = "o/r",
                CheckNames = ["GitOps/GitHubPop"]
            }
        };

        Assert.False(ReadyToMergeDetection.IsReadyToMerge(
            "o/r",
            ReadyPr(1, 7, [8]) with { Checks = Failing("GitOps/GitHubPop") with { PendingCount = 1 } },
            s_now,
            s_doNotMergeLabels,
            nonBlockingRules));
        Assert.False(ReadyToMergeDetection.IsReadyToMerge(
            "o/r",
            ReadyPr(2, 7, [8]) with { Checks = Failing("GitOps/GitHubPop", "blocking-check") },
            s_now,
            s_doNotMergeLabels,
            nonBlockingRules));
        Assert.False(ReadyToMergeDetection.IsReadyToMerge(
            "other/repo",
            ReadyPr(3, 7, [8]) with { Checks = Failing("GitOps/GitHubPop") },
            s_now,
            s_doNotMergeLabels,
            nonBlockingRules));
    }

    private static ReviewStatus Approved(long[] approverIds) =>
        new(
            State: "approved",
            LatestState: "approved",
            ReviewerCount: approverIds.Length,
            ApprovalCount: approverIds.Length,
            ChangesRequestedCount: 0,
            CommentedReviewCount: 0,
            LastApprovedAt: s_now,
            LastReviewedAt: s_now)
        {
            ApprovedReviewerIds = approverIds
        };

    private static ChecksStatus Passing() =>
        ChecksStatus.Unknown with { State = "success", TotalCount = 1, SuccessCount = 1 };

    private static ChecksStatus Pending() =>
        ChecksStatus.Unknown with { State = "pending", TotalCount = 1, PendingCount = 1 };

    private static ChecksStatus Failing(params string[] names) =>
        ChecksStatus.Unknown with
        {
            State = "failure",
            FailureCount = names.Length == 0 ? 1 : names.Length,
            TotalCount = names.Length == 0 ? 1 : names.Length,
            FailingChecks = names.Length == 0
                ? [new FailingCheck("blocking-check", "failure", null)]
                : names.Select(name => new FailingCheck(name, "failure", null)).ToArray()
        };

    private static PullRequestSummary ReadyPr(int number, long ownerUserId, long[] approverIds) =>
        new(
            number,
            $"PR {number}",
            "open",
            false,
            "author",
            $"https://github.com/o/r/pull/{number}",
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            [],
            [],
            null,
            [],
            0,
            0,
            0,
            0,
            null,
            null,
            null,
            "clean",
            Approved(approverIds),
            Passing())
        {
            OwnerUserId = ownerUserId
        };
}
