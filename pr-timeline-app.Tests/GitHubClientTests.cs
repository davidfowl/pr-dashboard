using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace pr_timeline_app.Tests;

public sealed class GitHubClientTests
{
    [Fact]
    public async Task PullListSkipsLinkedIssuesThatGitHubReturnsNotFound()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                """
                [
                  {
                    "number": 1,
                    "title": "Update docs",
                    "state": "open",
                    "body": "Fixes #404",
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-02T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/1",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": []
                  }
                ]
                """),
            "repos/example/repo/pulls/1/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/1" => Json(PullRequestDetailsJson(1)),
            "repos/example/repo/issues/404" => Json("""{ "message": "Not Found" }""", HttpStatusCode.NotFound),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Empty(pullRequest.LinkedIssues);
        Assert.Equal(1, pullRequest.CommitCount);
        Assert.Equal(10, pullRequest.Additions);
        Assert.Equal(2, pullRequest.Deletions);
        Assert.Equal(1, pullRequest.ChangedFiles);
    }

    [Fact]
    public async Task PullListFollowsLinkedIssueRedirectsWithAuthorization()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                """
                [
                  {
                    "number": 1,
                    "title": "Update docs",
                    "state": "open",
                    "body": "Fixes dotnet/aspire#6279",
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-02T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/1",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": []
                  }
                ]
                """),
            "repos/example/repo/pulls/1/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/1" => Json(PullRequestDetailsJson(1)),
            "repos/dotnet/aspire/issues/6279" => Json(
                """{ "message": "Moved Permanently" }""",
                HttpStatusCode.MovedPermanently,
                locationHeader: "https://api.github.com/repositories/696529789/issues/6279"),
            "repositories/696529789/issues/6279" => Json(
                """
                {
                  "number": 6279,
                  "title": "Canonical issue",
                  "html_url": "https://github.com/microsoft/aspire/issues/6279",
                  "repository_url": "https://api.github.com/repos/microsoft/aspire",
                  "labels": []
                }
                """),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        var linkedIssue = Assert.Single(pullRequest.LinkedIssues);
        Assert.Equal("microsoft/aspire", linkedIssue.Repository);
        Assert.Equal(6279, linkedIssue.Number);
    }

    [Fact]
    public async Task PullListTreatsMissingReviewsAsWaiting()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                """
                [
                  {
                    "number": 1,
                    "title": "Update docs",
                    "state": "open",
                    "body": null,
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-02T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/1",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": []
                  }
                ]
            """),
            "repos/example/repo/pulls/1/reviews?per_page=100" => Json("""{ "message": "Not Found" }""", HttpStatusCode.NotFound),
            "repos/example/repo/pulls/1" => Json(PullRequestDetailsJson(1)),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal("waiting", pullRequest.Review.State);
        Assert.Equal(0, pullRequest.Review.ReviewerCount);
    }

    [Fact]
    public async Task PullListExcludesDraftPullRequests()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                """
                [
                  {
                    "number": 1,
                    "title": "Work in progress",
                    "state": "open",
                    "body": null,
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-02T00:00:00Z",
                    "draft": true,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/1",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": []
                  },
                  {
                    "number": 2,
                    "title": "Ready for review",
                    "state": "open",
                    "body": null,
                    "created_at": "2026-01-03T00:00:00Z",
                    "updated_at": "2026-01-04T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/2",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": []
                  }
                ]
            """),
            "repos/example/repo/pulls/2/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/2" => Json(PullRequestDetailsJson(2)),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal(2, pullRequest.Number);
        Assert.False(pullRequest.Draft);
    }

    [Fact]
    public async Task PullListReadsAllPages()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                """
                [
                  {
                    "number": 1,
                    "title": "First page",
                    "state": "open",
                    "body": null,
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-02T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/1",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": []
                  }
                ]
                """,
                linkHeader: "<https://api.github.com/repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100&page=2>; rel=\"next\""),
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100&page=2" => Json(
                """
                [
                  {
                    "number": 2,
                    "title": "Second page",
                    "state": "open",
                    "body": null,
                    "created_at": "2026-01-03T00:00:00Z",
                    "updated_at": "2026-01-04T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/2",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": []
                  }
                ]
                """),
            "repos/example/repo/pulls/1/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/2/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/1" => Json(PullRequestDetailsJson(1)),
            "repos/example/repo/pulls/2" => Json(PullRequestDetailsJson(2)),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], pullRequests.Select(pullRequest => pullRequest.Number));
    }

    [Fact]
    public async Task PullListIncludesLastCommitAfterReviewForReReviewSignals()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                """
                [
                  {
                    "number": 1,
                    "title": "Update feature",
                    "state": "open",
                    "body": null,
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-03T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/1",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": []
                  }
                ]
                """),
            "repos/example/repo/pulls/1/reviews?per_page=100" => Json(
                """
                [
                  {
                    "user": { "login": "reviewer" },
                    "state": "COMMENTED",
                    "submitted_at": "2026-01-02T00:00:00Z"
                  }
                ]
                """),
            "repos/example/repo/pulls/1/commits?per_page=100" => Json(
                """
                [
                  {
                    "commit": {
                      "author": { "date": "2026-01-03T00:00:00Z" },
                      "committer": { "date": "2026-01-03T00:00:00Z" }
                    }
                  }
                ]
                """),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal("reviewed", pullRequest.Review.State);
        Assert.Equal(DateTimeOffset.Parse("2026-01-03T00:00:00Z"), pullRequest.LastCommitAt);
        Assert.Equal("none", pullRequest.Checks.State);
    }

    [Fact]
    public async Task PullListAttachesFailingChecksWhenHeadShaIsKnown()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                """
                [
                  {
                    "number": 1,
                    "title": "Add feature",
                    "state": "open",
                    "body": null,
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-02T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/1",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": [],
                    "head": { "sha": "abc123", "ref": "feature" }
                  }
                ]
                """),
            "repos/example/repo/pulls/1/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/1" => Json(PullRequestDetailsJson(1)),
            "repos/example/repo/commits/abc123/check-runs?filter=latest&per_page=100" => Json(
                """
                {
                  "total_count": 3,
                  "check_runs": [
                    { "id": 1, "name": "build", "status": "completed", "conclusion": "success", "completed_at": "2026-01-02T00:30:00Z", "html_url": "https://ci.example/build" },
                    { "id": 2, "name": "tests", "status": "completed", "conclusion": "failure", "completed_at": "2026-01-02T00:45:00Z", "html_url": "https://ci.example/tests" },
                    { "id": 3, "name": "lint", "status": "in_progress", "conclusion": null, "completed_at": null, "html_url": "https://ci.example/lint" }
                  ]
                }
                """),
            "repos/example/repo/commits/abc123/status?per_page=100" => Json(
                """
                {
                  "state": "pending",
                  "total_count": 1,
                  "statuses": [
                    { "state": "pending", "context": "azure-pipelines", "target_url": "https://az.example", "updated_at": "2026-01-02T00:50:00Z" }
                  ]
                }
                """),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal("failure", pullRequest.Checks.State);
        Assert.Equal(4, pullRequest.Checks.TotalCount);
        Assert.Equal(1, pullRequest.Checks.SuccessCount);
        Assert.Equal(1, pullRequest.Checks.FailureCount);
        Assert.Equal(2, pullRequest.Checks.PendingCount);
        var failing = Assert.Single(pullRequest.Checks.FailingChecks);
        Assert.Equal("tests", failing.Name);
        Assert.Equal("failure", failing.Conclusion);
    }

    [Fact]
    public async Task PullListTreatsAllGreenChecksAsSuccess()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                """
                [
                  {
                    "number": 1,
                    "title": "Add feature",
                    "state": "open",
                    "body": null,
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-02T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/1",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": [],
                    "head": { "sha": "def456", "ref": "feature" }
                  }
                ]
                """),
            "repos/example/repo/pulls/1/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/1" => Json(PullRequestDetailsJson(1)),
            "repos/example/repo/commits/def456/check-runs?filter=latest&per_page=100" => Json(
                """
                {
                  "total_count": 2,
                  "check_runs": [
                    { "id": 1, "name": "build", "status": "completed", "conclusion": "success", "completed_at": "2026-01-02T00:30:00Z" },
                    { "id": 2, "name": "tests", "status": "completed", "conclusion": "success", "completed_at": "2026-01-02T00:45:00Z" }
                  ]
                }
                """),
            "repos/example/repo/commits/def456/status?per_page=100" => Json(
                """{ "state": "success", "total_count": 0, "statuses": [] }"""),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal("success", pullRequest.Checks.State);
        Assert.Equal(2, pullRequest.Checks.TotalCount);
        Assert.Empty(pullRequest.Checks.FailingChecks);
    }

    [Fact]
    public async Task PullListSkipsChecksWhenStateIsClosed()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=closed&sort=updated&direction=desc&per_page=100" => Json(
                """
                [
                  {
                    "number": 1,
                    "title": "Add feature",
                    "state": "closed",
                    "body": null,
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-02T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/1",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": [],
                    "head": { "sha": "abc123", "ref": "feature" }
                  }
                ]
                """),
            "repos/example/repo/pulls/1/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/1" => Json(PullRequestDetailsJson(1)),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "closed",
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal("none", pullRequest.Checks.State);
    }

    [Fact]
    public async Task PullListTreatsAllNeutralOrSkippedChecksAsSuccess()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                """
                [
                  {
                    "number": 1,
                    "title": "Add feature",
                    "state": "open",
                    "body": null,
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-02T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/1",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": [],
                    "head": { "sha": "neu789", "ref": "feature" }
                  }
                ]
                """),
            "repos/example/repo/pulls/1/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/1" => Json(PullRequestDetailsJson(1)),
            "repos/example/repo/commits/neu789/check-runs?filter=latest&per_page=100" => Json(
                """
                {
                  "total_count": 2,
                  "check_runs": [
                    { "id": 1, "name": "irrelevant", "status": "completed", "conclusion": "neutral", "completed_at": "2026-01-02T00:30:00Z" },
                    { "id": 2, "name": "doc-job", "status": "completed", "conclusion": "skipped", "completed_at": "2026-01-02T00:35:00Z" }
                  ]
                }
                """),
            "repos/example/repo/commits/neu789/status?per_page=100" => Json(
                """{ "state": "success", "total_count": 0, "statuses": [] }"""),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal("success", pullRequest.Checks.State);
        Assert.Equal(2, pullRequest.Checks.TotalCount);
        Assert.Equal(0, pullRequest.Checks.SuccessCount);
        Assert.Equal(1, pullRequest.Checks.NeutralCount);
        Assert.Equal(1, pullRequest.Checks.SkippedCount);
    }

    [Fact]
    public async Task PullListSwallowsRateLimitOnChecksFetch()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                """
                [
                  {
                    "number": 1,
                    "title": "Add feature",
                    "state": "open",
                    "body": null,
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-02T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/1",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": [],
                    "head": { "sha": "rl1", "ref": "feature" }
                  }
                ]
                """),
            "repos/example/repo/pulls/1/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/1" => Json(PullRequestDetailsJson(1)),
            "repos/example/repo/commits/rl1/check-runs?filter=latest&per_page=100" => Json(
                """{ "message": "API rate limit exceeded" }""",
                (HttpStatusCode)403),
            "repos/example/repo/commits/rl1/status?per_page=100" => Json(
                """{ "message": "Server error" }""",
                HttpStatusCode.InternalServerError),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        // Rate limit and 5xx on checks must degrade gracefully — the PR still appears.
        Assert.Equal("none", pullRequest.Checks.State);
    }

    [Fact]
    public async Task PullListFetchesChecksForOpenPrsInAllQuery()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=all&sort=updated&direction=desc&per_page=100" => Json(
                """
                [
                  {
                    "number": 1,
                    "title": "Open feature",
                    "state": "open",
                    "body": null,
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-02T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/1",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": [],
                    "head": { "sha": "open123", "ref": "feature" }
                  },
                  {
                    "number": 2,
                    "title": "Closed feature",
                    "state": "closed",
                    "body": null,
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-02T00:00:00Z",
                    "draft": false,
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/2",
                    "labels": [],
                    "requested_reviewers": [],
                    "requested_teams": [],
                    "head": { "sha": "closed456", "ref": "older" }
                  }
                ]
                """),
            "repos/example/repo/pulls/1/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/2/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/1" => Json(PullRequestDetailsJson(1)),
            "repos/example/repo/pulls/2" => Json(PullRequestDetailsJson(2)),
            "repos/example/repo/commits/open123/check-runs?filter=latest&per_page=100" => Json(
                """
                {
                  "total_count": 1,
                  "check_runs": [
                    { "id": 1, "name": "tests", "status": "completed", "conclusion": "success", "completed_at": "2026-01-02T00:30:00Z" }
                  ]
                }
                """),
            "repos/example/repo/commits/open123/status?per_page=100" => Json(
                """{ "state": "success", "total_count": 0, "statuses": [] }"""),
            // Intentionally NO check-runs / status stubs for closed456 — the test asserts they
            // are never requested for closed PRs even inside an all query.
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "all",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, pullRequests.Count);
        var open = pullRequests.Single(pullRequest => pullRequest.Number == 1);
        var closed = pullRequests.Single(pullRequest => pullRequest.Number == 2);
        Assert.Equal("success", open.Checks.State);
        Assert.Equal("none", closed.Checks.State);
    }

    private static GitHubClient CreateClient(Func<string, HttpResponseMessage> route)
    {
        var httpClient = new HttpClient(new StubGitHubHandler(route))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var tokenProvider = new GitHubTokenProvider(
            new HttpContextAccessor { HttpContext = CreateHttpContextWithGitHubToken() },
            new TestHostEnvironment());

        return new GitHubClient(httpClient, tokenProvider, new MemoryCache(new MemoryCacheOptions()), new TestHostEnvironment());
    }

    private static DefaultHttpContext CreateHttpContextWithGitHubToken()
    {
        var context = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("test", _ => { });
        context.RequestServices = services.BuildServiceProvider();

        return context;
    }

    private static HttpResponseMessage Json(
        string content,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? linkHeader = null,
        string? locationHeader = null)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

        if (linkHeader is not null)
        {
            response.Headers.Add("Link", linkHeader);
        }

        if (locationHeader is not null)
        {
            response.Headers.Location = new Uri(locationHeader);
        }

        return response;
    }

    private static string PullRequestDetailsJson(int number) =>
        $$"""
        {
          "number": {{number}},
          "title": "Ready for review",
          "state": "open",
          "created_at": "2026-01-01T00:00:00Z",
          "updated_at": "2026-01-02T00:00:00Z",
          "draft": false,
          "user": { "login": "octocat" },
          "html_url": "https://github.com/example/repo/pull/{{number}}",
          "labels": [],
          "requested_reviewers": [],
          "requested_teams": [],
          "commits": 1,
          "additions": 10,
          "deletions": 2,
          "changed_files": 1
        }
        """;

    private sealed class StubGitHubHandler(Func<string, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("unit-test-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(route(request.RequestUri?.PathAndQuery.TrimStart('/') ?? ""));
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "pr-timeline-app.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[] { new Claim(ClaimTypes.Name, "octocat") };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var properties = new AuthenticationProperties();
            properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = "unit-test-token" }]);
            var ticket = new AuthenticationTicket(principal, properties, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
