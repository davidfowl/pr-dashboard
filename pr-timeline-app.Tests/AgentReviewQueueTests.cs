using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace pr_timeline_app.Tests;

public sealed class AgentReviewQueueTests
{
    private static readonly DateTimeOffset s_now = new(2026, 07, 02, 02, 30, 00, TimeSpan.Zero);

    [Fact]
    public void BuildFocusQueueMatchesHomepagePriorityAndExclusions()
    {
        var options = new DashboardOptions
        {
            CoreTeamMembers = ["alice", "bob"],
            DoNotMergeLabels = ["needs-author-action"],
            BotAuthors = ["github-actions"],
            CommunityRepositories = ["CommunityToolkit/Aspire"]
        };
        var pullRequests = new[]
        {
            Pr(1, "Recent review needed", "alice", updatedAt: s_now.AddHours(-1)),
            Pr(2, "Ready to merge", "bob", updatedAt: s_now.AddHours(-2)) with
            {
                Review = Approved(s_now.AddHours(-2)),
                MergeableState = "clean"
            },
            Pr(3, "Re-review needed", "alice", updatedAt: s_now.AddHours(-3)) with
            {
                LastCommitAt = s_now.AddHours(-1),
                Review = Reviewed(s_now.AddHours(-2))
            },
            Pr(4, "CI failing", "alice", updatedAt: s_now.AddMinutes(-30)) with
            {
                Checks = Failing()
            },
            Pr(5, "Community", "external-user", updatedAt: s_now.AddMinutes(-10)),
            Pr(6, "Held", "alice", updatedAt: s_now.AddMinutes(-5), labels: ["needs-author-action"])
        };

        var queue = AgentReviewQueueBuilder.Build(
            [new PullRequestListResponse("microsoft/aspire", pullRequests)],
            options,
            s_now);

        Assert.Equal([3, 2, 1], queue.Items.Select(item => item.PullRequest.Number));
        Assert.Equal(["Re-review needed", "Ready to merge", "Needs review"], queue.Items.Select(item => item.BucketLabel));
        Assert.DoesNotContain(queue.Items, item => item.PullRequest.Number is 4 or 5 or 6);
    }

    [Fact]
    public void BuildFocusQueueOrdersSameBucketByOldestWaitLikeHomepage()
    {
        var options = new DashboardOptions { CoreTeamMembers = ["alice"] };
        var pullRequests = new[]
        {
            Pr(10, "Newer waiting PR", "alice", createdAt: s_now.AddDays(-1), updatedAt: s_now.AddHours(-1)),
            Pr(11, "Older waiting PR", "alice", createdAt: s_now.AddDays(-3), updatedAt: s_now.AddHours(-2))
        };

        var queue = AgentReviewQueueBuilder.Build(
            [new PullRequestListResponse("microsoft/aspire", pullRequests)],
            options,
            s_now);

        Assert.Equal([11, 10], queue.Items.Select(item => item.PullRequest.Number));
    }

    [Fact]
    public void BuildFocusQueueOrdersRegressionBeforeLowerPriorityBuckets()
    {
        var options = new DashboardOptions { CoreTeamMembers = ["alice"] };
        var pullRequests = new[]
        {
            Pr(19, "Normal review", "alice", createdAt: s_now.AddDays(-2), updatedAt: s_now.AddHours(-1)),
            Pr(20, "Regression review", "alice", createdAt: s_now.AddDays(-1), updatedAt: s_now.AddDays(-3), labels: ["regression"])
        };

        var queue = AgentReviewQueueBuilder.Build(
            [new PullRequestListResponse("microsoft/aspire", pullRequests)],
            options,
            s_now,
            limit: 1);

        var item = Assert.Single(queue.Items);
        Assert.Equal(20, item.PullRequest.Number);
        Assert.Equal("Regression", item.BucketLabel);
        Assert.Equal(2, queue.TotalCount);
    }

    [Fact]
    public void BuildFocusQueueClampsLargeLimits()
    {
        var options = new DashboardOptions { CoreTeamMembers = ["alice"] };
        var pullRequests = Enumerable
            .Range(1, 1002)
            .Select(number => Pr(number, $"PR {number}", "alice", createdAt: s_now.AddDays(-number)))
            .ToArray();

        var queue = AgentReviewQueueBuilder.Build(
            [new PullRequestListResponse("microsoft/aspire", pullRequests)],
            options,
            s_now,
            limit: 5000);

        Assert.Equal(1002, queue.TotalCount);
        Assert.Equal(1000, queue.Items.Count);
    }

    [Fact]
    public void ClampLimitBoundsAgentQueueRequests()
    {
        Assert.Equal(1, AgentReviewQueueBuilder.ClampLimit(0));
        Assert.Equal(10, AgentReviewQueueBuilder.ClampLimit(10));
        Assert.Equal(1000, AgentReviewQueueBuilder.ClampLimit(5000));
    }

    [Fact]
    public void TryResolveRepositoriesRejectsExcessiveExplicitRepositoryLists()
    {
        var repo = string.Join(",", Enumerable.Range(1, 51).Select(index => $"owner/repo-{index}"));

        var resolved = AgentReviewQueueRoutes.TryResolveRepositories(
            repo,
            configuredRepositories: [],
            out var repositories,
            out var errors);

        Assert.False(resolved);
        Assert.Equal(51, repositories.Count);
        Assert.Equal("Pass at most 50 repositories in repo=.", Assert.Single(errors["repo"]));
    }

    [Fact]
    public void BuildFocusQueueTreatsHumanCopilotAuthorAsCoreTeam()
    {
        var options = new DashboardOptions { CoreTeamMembers = ["JamesNK", "abbot"] };

        var queue = AgentReviewQueueBuilder.Build(
            [
                new PullRequestListResponse("microsoft/aspire",
                [
                    Pr(12, "Copilot PR", "JamesNK/copilot"),
                    Pr(25, "Copilot PR from bot-named human", "abbot/copilot")
                ])
            ],
            options,
            s_now);

        Assert.Equal([12, 25], queue.Items.Select(item => item.PullRequest.Number));
        Assert.All(queue.Items, item => Assert.Equal("Needs review", item.BucketLabel));
    }

    [Fact]
    public void BuildFocusQueueRoutesUnconfiguredBotAuthorsToAutomation()
    {
        var options = new DashboardOptions { CoreTeamMembers = ["alice"] };

        var queue = AgentReviewQueueBuilder.Build(
            [new PullRequestListResponse("microsoft/aspire", [Pr(21, "Bot PR", "dependabot[bot]")])],
            options,
            s_now);

        Assert.Empty(queue.Items);
        Assert.Equal(0, queue.TotalCount);
    }

    [Fact]
    public void BuildFocusQueueTreatsConfiguredTeamAliasAsCoreTeam()
    {
        var options = new DashboardOptions { CoreTeamMemberAliasSuffixes = ["_microsoft"] };

        var queue = AgentReviewQueueBuilder.Build(
            [new PullRequestListResponse("microsoft/aspire", [Pr(16, "Alias author PR", "teammate_microsoft")])],
            options,
            s_now);

        var item = Assert.Single(queue.Items);
        Assert.Equal(16, item.PullRequest.Number);
        Assert.Equal("Needs review", item.BucketLabel);
    }

    [Fact]
    public void BuildFocusQueueTreatsConfiguredTeamAliasCopilotAuthorAsCoreTeam()
    {
        var options = new DashboardOptions { CoreTeamMemberAliasSuffixes = ["_microsoft"] };

        var queue = AgentReviewQueueBuilder.Build(
            [new PullRequestListResponse("microsoft/aspire", [Pr(24, "Alias Copilot author PR", "teammate_microsoft/copilot")])],
            options,
            s_now);

        var item = Assert.Single(queue.Items);
        Assert.Equal(24, item.PullRequest.Number);
        Assert.Equal("Needs review", item.BucketLabel);
    }

    [Fact]
    public void BuildFocusQueueNormalizesHoldLabelsAndDropsIncompleteNonBlockingRules()
    {
        var options = new DashboardOptions
        {
            CoreTeamMembers = ["alice"],
            DoNotMergeLabels = [" no-merge "],
            NonBlockingCheckFailureRules =
            [
                new DashboardCheckFailureRuleOptions
                {
                    Repository = " microsoft/aspire ",
                    Label = "",
                    CheckNames = [" Build "]
                },
                new DashboardCheckFailureRuleOptions
                {
                    Repository = "microsoft/aspire",
                    Label = "Known flaky"
                }
            ]
        };

        var queue = AgentReviewQueueBuilder.Build(
            [
                new PullRequestListResponse("microsoft/aspire",
                [
                    Pr(22, "Held by normalized label", "alice", labels: ["no-merge"]),
                    Pr(23, "Incomplete non-blocking rule", "alice") with
                    {
                        Checks = Failing("Build")
                    },
                    Pr(26, "Matcherless aggregate placeholder", "alice") with
                    {
                        Checks = ChecksStatus.Unknown with
                        {
                            State = "failure",
                            TotalCount = 0,
                            FailureCount = 0,
                            FailingChecks = []
                        }
                    }
                ])
            ],
            options,
            s_now);

        Assert.Empty(queue.Items);
        Assert.Equal(0, queue.TotalCount);
    }

    [Fact]
    public void BuildFocusQueueAllowsConfiguredNonBlockingCheckFailuresWithLabel()
    {
        var options = new DashboardOptions
        {
            CoreTeamMembers = ["alice"],
            NonBlockingCheckFailureRules =
            [
                new DashboardCheckFailureRuleOptions
                {
                    Repository = "microsoft/aspire",
                    Label = "Known flaky",
                    CheckNames = ["GitOps/GitHubPop"]
                }
            ]
        };

        var queue = AgentReviewQueueBuilder.Build(
            [
                new PullRequestListResponse("microsoft/aspire",
                [
                    Pr(13, "Non-blocking CI", "alice") with
                    {
                        Checks = Failing("GitOps/GitHubPop")
                    }
                ])
            ],
            options,
            s_now);

        var item = Assert.Single(queue.Items);
        Assert.Equal(13, item.PullRequest.Number);
        Assert.Equal("Needs review", item.BucketLabel);
    }

    [Fact]
    public void BuildFocusQueueAllowsConfiguredNonBlockingAggregateFailurePlaceholder()
    {
        var options = new DashboardOptions
        {
            CoreTeamMembers = ["alice"],
            NonBlockingCheckFailureRules =
            [
                new DashboardCheckFailureRuleOptions
                {
                    Repository = "microsoft/aspire",
                    Label = "Known flaky",
                    CheckNames = ["GitOps/GitHubPop"]
                }
            ]
        };

        var queue = AgentReviewQueueBuilder.Build(
            [
                new PullRequestListResponse("microsoft/aspire",
                [
                    Pr(17, "Aggregate placeholder CI", "alice") with
                    {
                        Checks = ChecksStatus.Unknown with
                        {
                            State = "failure",
                            TotalCount = 0,
                            FailureCount = 0,
                            FailingChecks = []
                        }
                    }
                ])
            ],
            options,
            s_now);

        var item = Assert.Single(queue.Items);
        Assert.Equal(17, item.PullRequest.Number);
        Assert.Equal("Needs review", item.BucketLabel);
    }

    [Fact]
    public void BuildFocusQueueExcludesCurrentReleaseQuickWins()
    {
        var options = new DashboardOptions
        {
            CoreTeamMembers = ["alice"],
            CurrentRelease = "13.4"
        };

        var queue = AgentReviewQueueBuilder.Build(
            [new PullRequestListResponse("microsoft/aspire", [Pr(14, "Fix release 13.4 notes", "alice")])],
            options,
            s_now);

        var item = Assert.Single(queue.Items);
        Assert.Equal(14, item.PullRequest.Number);
        Assert.Equal("Needs review", item.BucketLabel);
    }

    [Fact]
    public void BuildFocusQueueUsesLatestReviewActivityForApprovedFocusAge()
    {
        var options = new DashboardOptions { CoreTeamMembers = ["alice"] };
        var queue = AgentReviewQueueBuilder.Build(
            [
                new PullRequestListResponse("microsoft/aspire",
                [
                    Pr(15, "Approved with recent review activity", "alice", updatedAt: s_now.AddDays(-20)) with
                    {
                        Review = Approved(s_now.AddDays(-20)) with
                        {
                            LastReviewedAt = s_now.AddDays(-1)
                        },
                        MergeableState = "clean"
                    }
                ])
            ],
            options,
            s_now);

        var item = Assert.Single(queue.Items);
        Assert.Equal(15, item.PullRequest.Number);
        Assert.Equal("Approved but aging", item.BucketLabel);
    }

    [Fact]
    public void TargetsCurrentReleaseTrimsConfiguredRelease()
    {
        var options = new DashboardOptions { CurrentRelease = " 13.4 " };

        Assert.True(AgentReviewQueueBuilder.TargetsCurrentRelease(
            Pr(18, "Fix release-v13.4-notes", "alice"),
            options));
    }

    [Fact]
    public async Task BuildReviewQueueResponseLoadsRepositoriesConcurrently()
    {
        Assert.True(RepositoryName.TryParse("microsoft/aspire", out var firstRepository));
        Assert.True(RepositoryName.TryParse("microsoft/extensions", out var secondRepository));
        var activeLoads = 0;
        var maxActiveLoads = 0;

        var result = await AgentReviewQueueRoutes.BuildReviewQueueResponseAsync(
            [firstRepository, secondRepository],
            new DashboardOptions(),
            forceRefresh: false,
            limit: 10,
            async (repository, _, cancellationToken) =>
            {
                var currentActiveLoads = Interlocked.Increment(ref activeLoads);
                UpdateMaxActiveLoads(currentActiveLoads);
                await Task.Delay(100, cancellationToken);
                Interlocked.Decrement(ref activeLoads);
                return new PullRequestListResponse(repository.ToString(), []);
            },
            s_now,
            TestContext.Current.CancellationToken);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.True(maxActiveLoads > 1);

        void UpdateMaxActiveLoads(int currentActiveLoads)
        {
            int currentMax;
            do
            {
                currentMax = maxActiveLoads;
                if (currentActiveLoads <= currentMax)
                {
                    return;
                }
            } while (Interlocked.CompareExchange(ref maxActiveLoads, currentActiveLoads, currentMax) != currentMax);
        }
    }

    [Fact]
    public async Task BuildReviewQueueResponseReturnsServiceUnavailableWhenAllRepositoriesFail()
    {
        Assert.True(RepositoryName.TryParse("microsoft/aspire", out var repository));

        var result = await AgentReviewQueueRoutes.BuildReviewQueueResponseAsync(
            [repository],
            new DashboardOptions(),
            forceRefresh: false,
            limit: 10,
            static (_, _, _) => throw new InvalidOperationException("public cache unavailable"),
            s_now,
            TestContext.Current.CancellationToken);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusResult.StatusCode);

        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddProblemDetails()
            .BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();
        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        var body = await new StreamReader(httpContext.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("No requested repository data was available", body);
        Assert.DoesNotContain("public cache unavailable", body);
    }

    [Fact]
    public void TryResolveRepositoriesTrimsDedupesAndIgnoresInvalidConfiguredRepositories()
    {
        var resolved = AgentReviewQueueRoutes.TryResolveRepositories(
            repo: null,
            configuredRepositories:
            [
                " microsoft/aspire ",
                "microsoft/aspire",
                "not-a-repo",
                ""
            ],
            out var repositories,
            out var errors);

        Assert.True(resolved);
        Assert.Empty(errors);
        var repository = Assert.Single(repositories);
        Assert.Equal("microsoft/aspire", repository.ToString());
    }

    private static PullRequestSummary Pr(
        int number,
        string title,
        string author,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null,
        IReadOnlyList<string>? labels = null) =>
        new(
            number,
            title,
            "open",
            false,
            author,
            $"https://github.com/microsoft/aspire/pull/{number}",
            createdAt ?? s_now.AddDays(-1),
            updatedAt ?? s_now.AddHours(-1),
            labels ?? [],
            [],
            null,
            [],
            1,
            10,
            5,
            1,
            null,
            $"head-{number}",
            "main",
            "clean",
            ReviewStatus.Waiting,
            Passing());

    private static ReviewStatus Approved(DateTimeOffset approvedAt) =>
        new(
            "approved",
            "APPROVED",
            1,
            1,
            0,
            0,
            approvedAt,
            approvedAt);

    private static ReviewStatus Reviewed(DateTimeOffset reviewedAt) =>
        new(
            "reviewed",
            "COMMENTED",
            1,
            0,
            0,
            1,
            null,
            reviewedAt);

    private static ChecksStatus Passing() =>
        ChecksStatus.Unknown with { State = "success", TotalCount = 1, SuccessCount = 1 };

    private static ChecksStatus Failing(params string[] names) =>
        ChecksStatus.Unknown with
        {
            State = "failure",
            TotalCount = names.Length == 0 ? 1 : names.Length,
            FailureCount = names.Length == 0 ? 1 : names.Length,
            FailingChecks = names.Length == 0
                ? [new FailingCheck("Build", "failure", null)]
                : names.Select(name => new FailingCheck(name, "failure", null)).ToArray()
        };
}
