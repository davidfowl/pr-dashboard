using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
            false,
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
            false,
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
            false,
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
            false,
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal(2, pullRequest.Number);
        Assert.False(pullRequest.Draft);
    }

    [Fact]
    public async Task PullListByLabelLoadsOnlyMatchingPullRequests()
    {
        var requestedPaths = new List<string>();
        var client = CreateClient(path =>
        {
            requestedPaths.Add(path);
            return path switch
            {
                "repos/example/repo/issues?state=open&labels=docs-from-code&sort=created&direction=asc&per_page=100" => Json(
                    """
                    [
                      {
                        "number": 5,
                        "title": "Generated docs",
                        "state": "open",
                        "created_at": "2026-01-01T00:00:00Z",
                        "updated_at": "2026-01-02T00:00:00Z",
                        "user": { "login": "octocat" },
                        "html_url": "https://github.com/example/repo/pull/5",
                        "labels": [{ "name": "docs-from-code" }],
                        "pull_request": { "url": "https://api.github.com/repos/example/repo/pulls/5" }
                      },
                      {
                        "number": 6,
                        "title": "Plain issue",
                        "state": "open",
                        "created_at": "2026-01-03T00:00:00Z",
                        "updated_at": "2026-01-04T00:00:00Z",
                        "user": { "login": "octocat" },
                        "html_url": "https://github.com/example/repo/issues/6",
                        "labels": [{ "name": "docs-from-code" }]
                      }
                    ]
                    """),
                "repos/example/repo/pulls/5" => Json(
                    """
                    {
                      "number": 5,
                      "title": "Generated docs",
                      "state": "open",
                      "body": null,
                      "created_at": "2026-01-01T00:00:00Z",
                      "updated_at": "2026-01-02T00:00:00Z",
                      "draft": false,
                      "user": { "login": "octocat" },
                      "html_url": "https://github.com/example/repo/pull/5",
                      "labels": [{ "name": "docs-from-code" }],
                      "requested_reviewers": [],
                      "requested_teams": [],
                      "commits": 1,
                      "additions": 10,
                      "deletions": 2,
                      "changed_files": 1
                    }
                    """),
                "repos/example/repo/pulls/5/reviews?per_page=100" => Json("[]"),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        var pullRequests = await client.GetPullRequestsByLabelAsync(
            new RepositoryName("example", "repo"),
            "open",
            "docs-from-code",
            false,
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal(5, pullRequest.Number);
        Assert.Equal(["docs-from-code"], pullRequest.Labels);
        Assert.DoesNotContain(
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100",
            requestedPaths);
    }

    [Fact]
    public async Task RegressionIssuesDiscoverLabelsAndExcludePullRequests()
    {
        var requestedPaths = new List<string>();
        var client = CreateClient(path =>
        {
            requestedPaths.Add(path);
            return path switch
            {
                "repos/example/repo/labels?per_page=100" => Json(
                    """
                    [
                      { "name": "area-cli" },
                      { "name": "regression-from-last-release" }
                    ]
                    """),
                "repos/example/repo/issues?state=open&labels=regression-from-last-release&sort=updated&direction=desc&per_page=100" => Json(
                    """
                    [
                      {
                        "number": 10,
                        "title": "Broken from last release",
                        "state": "open",
                        "user": { "login": "reporter" },
                        "html_url": "https://github.com/example/repo/issues/10",
                        "repository_url": "https://api.github.com/repos/example/repo",
                        "created_at": "2026-01-01T00:00:00Z",
                        "updated_at": "2026-01-05T00:00:00Z",
                        "labels": [{ "name": "regression-from-last-release" }],
                        "assignees": [{ "login": "owner" }]
                      },
                      {
                        "number": 11,
                        "title": "Regression PR",
                        "state": "open",
                        "user": { "login": "contributor" },
                        "html_url": "https://github.com/example/repo/pull/11",
                        "repository_url": "https://api.github.com/repos/example/repo",
                        "created_at": "2026-01-02T00:00:00Z",
                        "updated_at": "2026-01-06T00:00:00Z",
                        "labels": [{ "name": "regression-from-last-release" }],
                        "assignees": [],
                        "pull_request": { "url": "https://api.github.com/repos/example/repo/pulls/11" }
                      },
                      {
                        "number": 12,
                        "title": "Wrong label",
                        "state": "open",
                        "user": { "login": "reporter" },
                        "html_url": "https://github.com/example/repo/issues/12",
                        "repository_url": "https://api.github.com/repos/example/repo",
                        "created_at": "2026-01-03T00:00:00Z",
                        "updated_at": "2026-01-07T00:00:00Z",
                        "labels": [{ "name": "area-cli" }],
                        "assignees": []
                      }
                    ]
                    """),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        var issues = await client.GetRegressionIssuesAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        var issue = Assert.Single(issues);
        Assert.Equal(10, issue.Number);
        Assert.Equal("example/repo", issue.Repository);
        Assert.Equal(["regression-from-last-release"], issue.Labels);
        Assert.Equal(["owner"], issue.Assignees);
        Assert.Contains("repos/example/repo/labels?per_page=100", requestedPaths);
        Assert.Contains("repos/example/repo/issues?state=open&labels=regression-from-last-release&sort=updated&direction=desc&per_page=100", requestedPaths);
        Assert.DoesNotContain(
            "repos/example/repo/issues?state=open&sort=updated&direction=desc&per_page=100",
            requestedPaths);
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
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], pullRequests.Select(pullRequest => pullRequest.Number));
    }

    [Fact]
    public async Task StreamPullRequestsYieldsFirstEnrichedBatchBeforeFetchingNextPage()
    {
        var secondPageRequested = false;
        var client = CreateClient(path =>
        {
            if (TryGetPullRequestNumber(path, "/reviews?per_page=100", out _))
            {
                return Json("[]");
            }

            if (TryGetPullRequestNumber(path, "", out var detailsNumber))
            {
                return Json(PullRequestDetailsJson(detailsNumber));
            }

            if (path == "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100&page=2")
            {
                secondPageRequested = true;
                return Json(PullRequestsJson(new[] { PullRequestJson(21, title: "Second page") }));
            }

            return path switch
            {
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                    PullRequestsJson(Enumerable.Range(1, 20)
                        .Select(number => PullRequestJson(
                            number,
                            title: $"First page {number}",
                            body: number == 1 ? "Fixes #404" : null))),
                    linkHeader: "<https://api.github.com/repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100&page=2>; rel=\"next\""),
                "repos/example/repo/issues/404" => Json(
                    """
                    {
                      "number": 404,
                      "title": "Linked issue",
                      "html_url": "https://github.com/example/repo/issues/404",
                      "repository_url": "https://api.github.com/repos/example/repo",
                      "labels": []
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        await using var enumerator = client.StreamPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.False(secondPageRequested);
        Assert.Equal(1, enumerator.Current.Number);
        var linkedIssue = Assert.Single(enumerator.Current.LinkedIssues);
        Assert.Equal(404, linkedIssue.Number);

        var streamedNumbers = new List<int> { enumerator.Current.Number };
        while (await enumerator.MoveNextAsync())
        {
            streamedNumbers.Add(enumerator.Current.Number);
        }

        Assert.True(secondPageRequested);
        Assert.Equal(Enumerable.Range(1, 21), streamedNumbers);
    }

    [Fact]
    public async Task StreamPullRequestsByLabelReadsPagedIssuesAndFiltersPullRequests()
    {
        var requestedPaths = new List<string>();
        var client = CreateClient(path =>
        {
            requestedPaths.Add(path);

            if (TryGetPullRequestNumber(path, "/reviews?per_page=100", out _))
            {
                return Json("[]");
            }

            return path switch
            {
                "repos/example/repo/issues?state=open&labels=docs-from-code&sort=created&direction=asc&per_page=100" => Json(
                    """
                    [
                      {
                        "number": 5,
                        "title": "Generated docs",
                        "state": "open",
                        "created_at": "2026-01-01T00:00:00Z",
                        "updated_at": "2026-01-02T00:00:00Z",
                        "user": { "login": "octocat" },
                        "html_url": "https://github.com/example/repo/pull/5",
                        "labels": [{ "name": "docs-from-code" }],
                        "pull_request": { "url": "https://api.github.com/repos/example/repo/pulls/5" }
                      },
                      {
                        "number": 6,
                        "title": "Plain issue",
                        "state": "open",
                        "created_at": "2026-01-03T00:00:00Z",
                        "updated_at": "2026-01-04T00:00:00Z",
                        "user": { "login": "octocat" },
                        "html_url": "https://github.com/example/repo/issues/6",
                        "labels": [{ "name": "docs-from-code" }]
                      },
                      {
                        "number": 7,
                        "title": "Draft generated docs",
                        "state": "open",
                        "created_at": "2026-01-05T00:00:00Z",
                        "updated_at": "2026-01-06T00:00:00Z",
                        "user": { "login": "octocat" },
                        "html_url": "https://github.com/example/repo/pull/7",
                        "labels": [{ "name": "docs-from-code" }],
                        "pull_request": { "url": "https://api.github.com/repos/example/repo/pulls/7" }
                      }
                    ]
                    """,
                    linkHeader: "<https://api.github.com/repos/example/repo/issues?state=open&labels=docs-from-code&sort=created&direction=asc&per_page=100&page=2>; rel=\"next\""),
                "repos/example/repo/issues?state=open&labels=docs-from-code&sort=created&direction=asc&per_page=100&page=2" => Json(
                    """
                    [
                      {
                        "number": 8,
                        "title": "Second page docs",
                        "state": "open",
                        "created_at": "2026-01-07T00:00:00Z",
                        "updated_at": "2026-01-08T00:00:00Z",
                        "user": { "login": "octocat" },
                        "html_url": "https://github.com/example/repo/pull/8",
                        "labels": [{ "name": "docs-from-code" }],
                        "pull_request": { "url": "https://api.github.com/repos/example/repo/pulls/8" }
                      }
                    ]
                    """),
                "repos/example/repo/pulls/5" => Json(PullRequestJson(5, title: "Generated docs", body: "Fixes #10")),
                "repos/example/repo/pulls/7" => Json(PullRequestJson(7, title: "Draft generated docs", draft: true)),
                "repos/example/repo/pulls/8" => Json(PullRequestJson(8, title: "Second page docs")),
                "repos/example/repo/issues/10" => Json(
                    """
                    {
                      "number": 10,
                      "title": "Source docs issue",
                      "html_url": "https://github.com/example/repo/issues/10",
                      "repository_url": "https://api.github.com/repos/example/repo",
                      "labels": []
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        var pullRequests = await EnumerateAsync(client.StreamPullRequestsByLabelAsync(
            new RepositoryName("example", "repo"),
            "open",
            "docs-from-code",
            false,
            TestContext.Current.CancellationToken));

        Assert.Equal([5, 8], pullRequests.Select(pullRequest => pullRequest.Number));
        Assert.Equal(10, Assert.Single(pullRequests[0].LinkedIssues).Number);
        Assert.DoesNotContain("repos/example/repo/pulls/6", requestedPaths);
        Assert.DoesNotContain(
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100",
            requestedPaths);
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
            false,
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal("reviewed", pullRequest.Review.State);
        Assert.Equal(DateTimeOffset.Parse("2026-01-03T00:00:00Z"), pullRequest.LastCommitAt);
        Assert.Equal("none", pullRequest.Checks.State);
    }

    [Fact]
    public async Task PullRequestChecksFetchesFailingChecksForVisiblePullRequests()
    {
        var client = CreateClient(path => path switch
        {
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

        var pullRequests = await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            [new PullRequestChecksRequestItem(1, "abc123")],
            false,
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
    public async Task PullRequestChecksTreatsAllGreenChecksAsSuccess()
    {
        var client = CreateClient(path => path switch
        {
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

        var pullRequests = await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            [new PullRequestChecksRequestItem(1, "def456")],
            false,
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
            false,
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal("none", pullRequest.Checks.State);
    }

    [Fact]
    public async Task PullRequestChecksTreatsAllNeutralOrSkippedChecksAsSuccess()
    {
        var client = CreateClient(path => path switch
        {
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

        var pullRequests = await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            [new PullRequestChecksRequestItem(1, "neu789")],
            false,
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal("success", pullRequest.Checks.State);
        Assert.Equal(2, pullRequest.Checks.TotalCount);
        Assert.Equal(0, pullRequest.Checks.SuccessCount);
        Assert.Equal(1, pullRequest.Checks.NeutralCount);
        Assert.Equal(1, pullRequest.Checks.SkippedCount);
    }

    [Fact]
    public async Task PullRequestChecksSwallowsRateLimitOnChecksFetch()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/commits/rl1/check-runs?filter=latest&per_page=100" => Json(
                """{ "message": "API rate limit exceeded" }""",
                (HttpStatusCode)403),
            "repos/example/repo/commits/rl1/status?per_page=100" => Json(
                """{ "message": "Server error" }""",
                HttpStatusCode.InternalServerError),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            [new PullRequestChecksRequestItem(1, "rl1")],
            false,
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        // Rate limit and 5xx on checks must degrade gracefully — the PR still appears.
        Assert.Equal("none", pullRequest.Checks.State);
    }

    [Fact]
    public async Task PullListDefersChecksForOpenPrsInAllQuery()
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
            // Intentionally NO check-runs / status stubs — the list response should not fetch CI
            // until the client asks for checks on visible PRs.
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "all",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, pullRequests.Count);
        var open = pullRequests.Single(pullRequest => pullRequest.Number == 1);
        var closed = pullRequests.Single(pullRequest => pullRequest.Number == 2);
        Assert.Equal("unknown", open.Checks.State);
        Assert.Equal("none", closed.Checks.State);
    }

    [Fact]
    public async Task PullRequestChecksLimitsConcurrentFetches()
    {
        const int pullRequestCount = 6;
        var activeChecksByHead = new Dictionary<string, int>(StringComparer.Ordinal);
        var activeGate = new object();
        var maxActiveHeads = 0;

        var client = CreateClient(async (path, cancellationToken) =>
        {
            if (TryGetChecksHeadSha(path, out var headSha))
            {
                lock (activeGate)
                {
                    activeChecksByHead.TryGetValue(headSha, out var activeRequestsForHead);
                    activeChecksByHead[headSha] = activeRequestsForHead + 1;
                    maxActiveHeads = Math.Max(maxActiveHeads, activeChecksByHead.Count);
                }

                try
                {
                    await Task.Delay(50, cancellationToken);
                    return path.Contains("/check-runs?", StringComparison.Ordinal)
                        ? Json("""{ "total_count": 0, "check_runs": [] }""")
                        : Json("""{ "state": "success", "total_count": 0, "statuses": [] }""");
                }
                finally
                {
                    lock (activeGate)
                    {
                        var activeRequestsForHead = activeChecksByHead[headSha] - 1;
                        if (activeRequestsForHead == 0)
                        {
                            activeChecksByHead.Remove(headSha);
                        }
                        else
                        {
                            activeChecksByHead[headSha] = activeRequestsForHead;
                        }
                    }
                }
            }

            throw new InvalidOperationException($"Unexpected GitHub request: {path}");
        });

        var pullRequests = await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            Enumerable.Range(1, pullRequestCount)
                .Select(number => new PullRequestChecksRequestItem(number, $"sha{number}"))
                .ToArray(),
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal(pullRequestCount, pullRequests.Count);
        Assert.True(maxActiveHeads <= 4, $"Expected at most 4 concurrent checks fetches but saw {maxActiveHeads}.");
    }

    [Fact]
    public async Task PullRequestChecksForceRefreshBypassesCachedStatus()
    {
        var requestCount = 0;
        var client = CreateClient(path =>
        {
            requestCount++;
            return path switch
            {
                "repos/example/repo/commits/cache123/check-runs?filter=latest&per_page=100" => Json(
                    """{ "total_count": 0, "check_runs": [] }"""),
                "repos/example/repo/commits/cache123/status?per_page=100" => Json(
                    """{ "state": "success", "total_count": 0, "statuses": [] }"""),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });
        var request = new[] { new PullRequestChecksRequestItem(1, "cache123") };

        await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            request,
            false,
            TestContext.Current.CancellationToken);
        await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            request,
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, requestCount);

        await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            request,
            true,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, requestCount);
    }

    [Fact]
    public async Task ShipWeekCombinesMilestoneIssuesAndReleaseBranchPullRequests()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/milestones?state=all&per_page=100" => Json(
                """
                [
                  { "number": 7, "title": "13.4" }
                ]
                """),
            "repos/example/repo/branches/release%2F13.4" => Json("""{ "name": "release/13.4" }"""),
            "repos/example/repo/issues?state=open&milestone=7&per_page=100" => Json(
                """
                [
                  {
                    "number": 10,
                    "title": "Validate CLI channel",
                    "state": "open",
                    "user": { "login": "pm" },
                    "html_url": "https://github.com/example/repo/issues/10",
                    "repository_url": "https://api.github.com/repos/example/repo",
                    "created_at": "2026-01-01T00:00:00Z",
                    "updated_at": "2026-01-05T00:00:00Z",
                    "labels": [{ "name": "area-cli" }],
                    "assignees": [{ "login": "owner" }],
                    "milestone": { "number": 7, "title": "13.4" }
                  },
                  {
                    "number": 1,
                    "title": "Draft release PR",
                    "state": "open",
                    "user": { "login": "octocat" },
                    "html_url": "https://github.com/example/repo/pull/1",
                    "repository_url": "https://api.github.com/repos/example/repo",
                    "created_at": "2026-01-02T00:00:00Z",
                    "updated_at": "2026-01-06T00:00:00Z",
                    "labels": [],
                    "assignees": [],
                    "milestone": { "number": 7, "title": "13.4" },
                    "pull_request": { "url": "https://api.github.com/repos/example/repo/pulls/1" }
                  }
                ]
                """),
            "repos/example/repo/pulls?state=open&base=release%2F13.4&sort=created&direction=asc&per_page=100" => Json(
                $$"""
                [
                  {{PullRequestJson(2, title: "Fix linked issue", body: "Fixes #10", headSha: "sha2", baseRef: "release/13.4")}},
                  {{PullRequestJson(3, title: "Hotfix outside milestone", headSha: "sha3", baseRef: "release/13.4")}}
                ]
                """),
            "repos/example/repo/pulls/1" => Json(PullRequestJson(
                1,
                title: "Draft release PR",
                draft: true,
                milestone: "13.4",
                headSha: "sha1",
                baseRef: "main")),
            "repos/example/repo/pulls/2" => Json(PullRequestJson(
                2,
                title: "Fix linked issue",
                body: "Fixes #10",
                headSha: "sha2",
                baseRef: "release/13.4")),
            "repos/example/repo/pulls/3" => Json(PullRequestJson(
                3,
                title: "Hotfix outside milestone",
                headSha: "sha3",
                baseRef: "release/13.4")),
            "repos/example/repo/pulls/1/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/2/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/3/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/issues/10" => Json(
                """
                {
                  "number": 10,
                  "title": "Validate CLI channel",
                  "html_url": "https://github.com/example/repo/issues/10",
                  "repository_url": "https://api.github.com/repos/example/repo",
                  "updated_at": "2026-01-05T00:00:00Z",
                  "labels": [{ "name": "area-cli" }],
                  "assignees": [{ "login": "owner" }],
                  "milestone": { "number": 7, "title": "13.4" }
                }
                """),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var result = await client.GetShipWeekAsync(
            new RepositoryName("example", "repo"),
            "13.4",
            "release/13.4",
            false,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.Response);
        var response = result.Response!;
        Assert.Empty(result.ValidationErrors);
        Assert.Equal("example/repo", response.Repository);
        Assert.Equal("13.4", response.Milestone);
        Assert.Equal("release/13.4", response.ReleaseBranch);
        Assert.Equal(3, response.PullRequests.Count);

        var draftMilestonePullRequest = response.PullRequests.Single(item => item.PullRequest.Number == 1);
        Assert.True(draftMilestonePullRequest.PullRequest.Draft);
        Assert.True(draftMilestonePullRequest.ReleaseScope.InMilestone);
        Assert.False(draftMilestonePullRequest.ReleaseScope.TargetsReleaseBranch);
        Assert.False(draftMilestonePullRequest.ReleaseScope.ReleaseBranchException);

        var linkedReleasePullRequest = response.PullRequests.Single(item => item.PullRequest.Number == 2);
        Assert.True(linkedReleasePullRequest.ReleaseScope.InMilestone);
        Assert.True(linkedReleasePullRequest.ReleaseScope.TargetsReleaseBranch);
        Assert.False(linkedReleasePullRequest.ReleaseScope.ReleaseBranchException);
        Assert.Equal([10], linkedReleasePullRequest.ReleaseScope.MilestoneIssueNumbers);

        var exceptionPullRequest = response.PullRequests.Single(item => item.PullRequest.Number == 3);
        Assert.False(exceptionPullRequest.ReleaseScope.InMilestone);
        Assert.True(exceptionPullRequest.ReleaseScope.TargetsReleaseBranch);
        Assert.True(exceptionPullRequest.ReleaseScope.ReleaseBranchException);

        var issue = Assert.Single(response.Issues);
        Assert.Equal(10, issue.Number);
        Assert.Equal([2], issue.LinkedOpenPullRequests);
    }

    [Fact]
    public async Task ShipWeekAutoDetectsLatestReleaseBranchWhenNotSpecified()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/milestones?state=all&per_page=100" => Json(
                """[{ "number": 7, "title": "13.4" }]"""),
            "repos/example/repo/git/matching-refs/heads/release/" => Json(
                """
                [
                  { "ref": "refs/heads/release/12.9" },
                  { "ref": "refs/heads/release/13.4" },
                  { "ref": "refs/heads/release/13.10" }
                ]
                """),
            "repos/example/repo/issues?state=open&milestone=7&per_page=100" => Json("[]"),
            "repos/example/repo/pulls?state=open&base=release%2F13.10&sort=created&direction=asc&per_page=100" => Json("[]"),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var result = await client.GetShipWeekAsync(
            new RepositoryName("example", "repo"),
            "13.4",
            null,
            false,
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.Response);
        Assert.Equal("release/13.10", result.Response!.ReleaseBranch);
    }

    [Fact]
    public async Task ShipWeekReturnsValidationWhenMilestoneIsMissing()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/milestones?state=all&per_page=100" => Json("[]"),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var result = await client.GetShipWeekAsync(
            new RepositoryName("example", "repo"),
            "13.4",
            "release/13.4",
            false,
            TestContext.Current.CancellationToken);

        Assert.Null(result.Response);
        Assert.True(result.ValidationErrors.ContainsKey("milestone"));
    }

    [Fact]
    public async Task ShipWeekReturnsValidationWhenReleaseBranchIsMissing()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/milestones?state=all&per_page=100" => Json(
                """[{ "number": 7, "title": "13.4" }]"""),
            "repos/example/repo/branches/release%2F13.4" => Json(
                """{ "message": "Not Found" }""",
                HttpStatusCode.NotFound),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var result = await client.GetShipWeekAsync(
            new RepositoryName("example", "repo"),
            "13.4",
            "release/13.4",
            false,
            TestContext.Current.CancellationToken);

        Assert.Null(result.Response);
        Assert.True(result.ValidationErrors.ContainsKey("releaseBranch"));
    }

    private static GitHubClient CreateClient(Func<string, HttpResponseMessage> route)
        => CreateClient((path, _) => Task.FromResult(route(path)));

    private static GitHubClient CreateClient(Func<string, CancellationToken, Task<HttpResponseMessage>> route)
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

    private static bool TryGetChecksHeadSha(string path, out string headSha)
    {
        headSha = "";
        const string marker = "/commits/";
        var markerIndex = path.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        var afterMarker = path[(markerIndex + marker.Length)..];
        var slashIndex = afterMarker.IndexOf('/');
        if (slashIndex <= 0)
        {
            return false;
        }

        var endpoint = afterMarker[(slashIndex + 1)..];
        if (!endpoint.StartsWith("check-runs?", StringComparison.Ordinal)
            && !endpoint.StartsWith("status?", StringComparison.Ordinal))
        {
            return false;
        }

        headSha = afterMarker[..slashIndex];
        return true;
    }

    private static bool TryGetPullRequestNumber(string path, string suffix, out int number)
    {
        number = 0;
        const string prefix = "repos/example/repo/pulls/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)
            || (suffix.Length > 0 && !path.EndsWith(suffix, StringComparison.Ordinal)))
        {
            return false;
        }

        var numberText = path[prefix.Length..];
        if (suffix.Length > 0)
        {
            numberText = numberText[..^suffix.Length];
        }

        return int.TryParse(numberText, out number);
    }

    private static async Task<IReadOnlyList<T>> EnumerateAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source.WithCancellation(TestContext.Current.CancellationToken))
        {
            items.Add(item);
        }

        return items;
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
        PullRequestJson(number);

    private static string PullRequestsJson(IEnumerable<string> pullRequests) =>
        $"[\n{string.Join(",\n", pullRequests)}\n]";

    private static string PullRequestJson(
        int number,
        string title = "Ready for review",
        string? body = null,
        bool draft = false,
        string? milestone = null,
        string? headSha = null,
        string? baseRef = null)
    {
        var milestoneJson = milestone is null
            ? "null"
            : $$"""{ "title": {{JsonSerializer.Serialize(milestone)}} }""";
        var headJson = headSha is null
            ? "null"
            : $$"""{ "sha": {{JsonSerializer.Serialize(headSha)}}, "ref": "feature-{{number}}" }""";
        var baseJson = baseRef is null
            ? "null"
            : $$"""{ "ref": {{JsonSerializer.Serialize(baseRef)}} }""";

        return
        $$"""
        {
          "number": {{number}},
          "title": {{JsonSerializer.Serialize(title)}},
          "state": "open",
          "body": {{JsonSerializer.Serialize(body)}},
          "created_at": "2026-01-01T00:00:00Z",
          "updated_at": "2026-01-02T00:00:00Z",
          "draft": {{draft.ToString().ToLowerInvariant()}},
          "user": { "login": "octocat" },
          "html_url": "https://github.com/example/repo/pull/{{number}}",
          "labels": [],
          "requested_reviewers": [],
          "requested_teams": [],
          "milestone": {{milestoneJson}},
          "commits": 1,
          "additions": 10,
          "deletions": 2,
          "changed_files": 1,
          "head": {{headJson}},
          "base": {{baseJson}}
        }
        """;
    }

    private sealed class StubGitHubHandler(Func<string, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("unit-test-token", request.Headers.Authorization?.Parameter);
            return await route(request.RequestUri?.PathAndQuery.TrimStart('/') ?? "", cancellationToken);
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
