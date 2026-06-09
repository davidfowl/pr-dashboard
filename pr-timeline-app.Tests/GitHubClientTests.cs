using System.Collections.Concurrent;
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
    public async Task PublicRepositoryPullListUsesAnonymousSharedCacheAcrossTokens()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var listRequests = 0;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            requests.Enqueue((path, token));

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Public list {++listRequests}")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is null => Json("[]"),
                "repos/example/repo/pulls/1" when token is null => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var firstClient = CreateClientFromRequests(route, cache, "token-a");

        var firstPullRequests = await firstClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);
        var secondClient = CreateClientFromRequests(route, cache, "token-b");
        var secondPullRequests = await secondClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Public list 1", Assert.Single(firstPullRequests).Title);
        Assert.Equal("Public list 1", Assert.Single(secondPullRequests).Title);
        Assert.Equal(1, listRequests);
        Assert.All(requests, request => Assert.Null(request.Token));
    }

    [Fact]
    public async Task PublicRepositoryForceRefreshWritesTokenOverlayWithoutReplacingSharedCache()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var publicListRequests = 0;
        var tokenListRequests = 0;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            requests.Enqueue((path, token));

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Public list {++publicListRequests}")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is "token-a" => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Token list {++tokenListRequests}")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is null => Json("[]"),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is "token-a" => Json("[]"),
                "repos/example/repo/pulls/1" when token is null => Json(PullRequestDetailsJson(1)),
                "repos/example/repo/pulls/1" when token is "token-a" => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var firstClient = CreateClientFromRequests(route, cache, "token-a");

        var originalPullRequests = await firstClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);
        var refreshedPullRequests = await firstClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken);
        var secondClient = CreateClientFromRequests(route, cache, "token-b");
        var sharedPullRequests = await secondClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Public list 1", Assert.Single(originalPullRequests).Title);
        Assert.Equal("Token list 1", Assert.Single(refreshedPullRequests).Title);
        Assert.Equal("Public list 1", Assert.Single(sharedPullRequests).Title);
        Assert.Equal(1, publicListRequests);
        Assert.Equal(1, tokenListRequests);
        Assert.Contains(requests, request => request.Token == "token-a");
        Assert.DoesNotContain(requests, request => request.Token == "token-b");
    }

    [Fact]
    public async Task PublicRepositoryForceRefreshUsesCooldownForRepeatedTokenOverlay()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var publicListRequests = 0;
        var tokenListRequests = 0;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Public list {++publicListRequests}")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is "token-a" => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Token list {++tokenListRequests}")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is null => Json("[]"),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is "token-a" => Json("[]"),
                "repos/example/repo/pulls/1" when token is null => Json(PullRequestDetailsJson(1)),
                "repos/example/repo/pulls/1" when token is "token-a" => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var client = CreateClientFromRequests(route, cache, "token-a");

        await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);
        var firstRefresh = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken);
        var secondRefresh = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken);

        Assert.Equal("Token list 1", Assert.Single(firstRefresh).Title);
        Assert.Equal("Token list 1", Assert.Single(secondRefresh).Title);
        Assert.Equal(1, publicListRequests);
        Assert.Equal(1, tokenListRequests);
    }

    [Fact]
    public async Task PublicRepositoryAnonymousForceRefreshDoesNotReplaceSharedCache()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var listRequests = 0;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            requests.Enqueue((path, token));

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Public list {++listRequests}")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is null => Json("[]"),
                "repos/example/repo/pulls/1" when token is null => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var anonymousClient = CreateAnonymousClientFromRequests(route, cache);

        var originalPullRequests = await anonymousClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);
        var refreshedPullRequests = await anonymousClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken);

        Assert.Equal("Public list 1", Assert.Single(originalPullRequests).Title);
        Assert.Equal("Public list 1", Assert.Single(refreshedPullRequests).Title);
        Assert.Equal(1, listRequests);
        Assert.All(requests, request => Assert.Null(request.Token));
    }

    [Fact]
    public async Task PublicRepositoryForceRefreshWritesTokenEnrichmentWithoutReplacingSharedCache()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var publicListRequests = 0;
        var tokenListRequests = 0;
        var publicReviewRequests = 0;
        var tokenReviewRequests = 0;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Public list {++publicListRequests}")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is "token-a" => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Token list {++tokenListRequests}")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is null && ++publicReviewRequests == 1 => Json("[]"),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is "token-a" && ++tokenReviewRequests == 1 => Json(
                    """
                    [
                      {
                        "user": { "login": "reviewer" },
                        "state": "APPROVED",
                        "submitted_at": "2026-01-03T00:00:00Z"
                      }
                    ]
                    """),
                "repos/example/repo/pulls/1" when token is null => Json(PullRequestDetailsJson(1)),
                "repos/example/repo/pulls/1" when token is "token-a" => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var firstClient = CreateClientFromRequests(route, cache, "token-a");

        var originalPullRequests = await firstClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);
        var refreshedPullRequests = await firstClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken);
        var secondClient = CreateClientFromRequests(route, cache, "token-b");
        var sharedPullRequests = await secondClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal("waiting", Assert.Single(originalPullRequests).Review.State);
        Assert.Equal("approved", Assert.Single(refreshedPullRequests).Review.State);
        Assert.Equal("waiting", Assert.Single(sharedPullRequests).Review.State);
        Assert.Equal(1, publicListRequests);
        Assert.Equal(1, tokenListRequests);
        Assert.Equal(1, publicReviewRequests);
        Assert.Equal(1, tokenReviewRequests);
    }

    [Fact]
    public async Task PublicRepositoryForceRefreshKeepsSharedCacheWhenRefreshFails()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var listRequests = 0;
        var failedListRequests = 0;
        var failListRefresh = false;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null && !failListRefresh => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Public list {++listRequests}")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is "token-a" && ++failedListRequests >= 1 => Json(
                    """{ "message": "API rate limit exceeded" }""",
                    HttpStatusCode.TooManyRequests),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is null => Json("[]"),
                "repos/example/repo/pulls/1" when token is null => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var client = CreateClientFromRequests(route, cache, "token-a");

        var originalPullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);
        failListRefresh = true;
        var fallbackPullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken);

        Assert.Equal("Public list 1", Assert.Single(originalPullRequests).Title);
        Assert.Equal("Public list 1", Assert.Single(fallbackPullRequests).Title);
        Assert.Equal(originalPullRequests[0].FetchedAt, fallbackPullRequests[0].FetchedAt);
        Assert.Equal(1, listRequests);
        Assert.Equal(1, failedListRequests);
    }

    [Fact]
    public async Task PublicRepositorySharedBaselineUsesHourlyTtlAndKeepsLastGoodWhenExpiredEntryRefreshFails()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repositoryName = new RepositoryName("example", "repo");
        var cacheKey = GitHubCachePolicy.CreateRepositoryCacheKey(
            GitHubCachePolicy.CreatePublicRepositoryScope(),
            repositoryName,
            "pulls",
            "open");
        var visibilityProbeRequests = 0;
        var listRequests = 0;
        var failListRefresh = false;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null && ++visibilityProbeRequests == 1 => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null && !failListRefresh => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Public list {++listRequests}")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null => Json(
                    """{ "message": "API rate limit exceeded" }""",
                    HttpStatusCode.TooManyRequests),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is null => Json("[]"),
                "repos/example/repo/pulls/1" when token is null => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var client = CreateClientFromRequests(route, cache, "token-a");

        Assert.Equal(TimeSpan.FromHours(1), GitHubClient.PublicCacheDuration);
        var originalPullRequests = await client.GetPullRequestsAsync(
            repositoryName,
            "open",
            false,
            TestContext.Current.CancellationToken);
        cache.Remove(cacheKey);
        failListRefresh = true;
        var fallbackPullRequests = await client.GetPullRequestsAsync(
            repositoryName,
            "open",
            false,
            TestContext.Current.CancellationToken);
        failListRefresh = false;
        var refreshedPullRequests = await client.GetPullRequestsAsync(
            repositoryName,
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Public list 1", Assert.Single(originalPullRequests).Title);
        Assert.Equal("Public list 1", Assert.Single(fallbackPullRequests).Title);
        Assert.Equal("Public list 2", Assert.Single(refreshedPullRequests).Title);
        Assert.Equal(1, visibilityProbeRequests);
        Assert.Equal(2, listRequests);
    }

    [Fact]
    public async Task PublicRepositoryPrewarmPopulatesSharedBaselineWithoutToken()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var listRequests = 0;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            requests.Enqueue((path, token));

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Public list {++listRequests}")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is null => Json("[]"),
                "repos/example/repo/pulls/1" when token is null => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var warmupClient = CreateClientFromRequests(route, cache, "token-a");

        var warmed = await warmupClient.TryPrewarmPublicPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);
        var readClient = CreateClientFromRequests(route, cache, "token-b");
        var pullRequests = await readClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.True(warmed);
        Assert.Equal("Public list 1", Assert.Single(pullRequests).Title);
        Assert.Equal(1, listRequests);
        Assert.All(requests, request => Assert.Null(request.Token));
    }

    [Fact]
    public async Task PublicRepositoryPrewarmSkipsUnprovenRepositoriesWithoutTokenFetch()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var client = CreateClientFromRequests((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            requests.Enqueue((path, token));

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "message": "Not Found" }""", HttpStatusCode.NotFound),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        }, cache, "token-a");

        var warmed = await client.TryPrewarmPublicPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        Assert.False(warmed);
        Assert.Single(requests);
        Assert.All(requests, request => Assert.Null(request.Token));
    }

    [Fact]
    public async Task PublicRepositoryVisibilityProbeFollowsGitHubRedirectIntoSharedCache()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var listRequests = 0;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            requests.Enqueue((path, token));

            return Task.FromResult(path switch
            {
                "repos/old/repo" when token is null => Json(
                    """{ "message": "Moved Permanently" }""",
                    HttpStatusCode.MovedPermanently,
                    locationHeader: "https://api.github.com/repositories/42"),
                "repositories/42" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/old/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Redirected public list {++listRequests}")])),
                "repos/old/repo/pulls/1/reviews?per_page=100" when token is null => Json("[]"),
                "repos/old/repo/pulls/1" when token is null => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var firstClient = CreateClientFromRequests(route, cache, "token-a");

        var firstPullRequests = await firstClient.GetPullRequestsAsync(
            new RepositoryName("old", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);
        var secondClient = CreateClientFromRequests(route, cache, "token-b");
        var secondPullRequests = await secondClient.GetPullRequestsAsync(
            new RepositoryName("old", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Redirected public list 1", Assert.Single(firstPullRequests).Title);
        Assert.Equal("Redirected public list 1", Assert.Single(secondPullRequests).Title);
        Assert.Equal(1, listRequests);
        Assert.Contains(requests, request => request.Path == "repositories/42");
        Assert.All(requests, request => Assert.Null(request.Token));
    }

    [Fact]
    public async Task PublicRepositoryVisibilityProofIsCachedForSharedBaseline()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repoIsPublic = true;
        var visibilityProbeRequests = 0;
        var publicListRequests = 0;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => VisibilityProbe(),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Public list {++publicListRequests}")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is null => Json("[]"),
                "repos/example/repo/pulls/1" when token is null => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });

            HttpResponseMessage VisibilityProbe()
            {
                visibilityProbeRequests++;
                return repoIsPublic
                    ? Json("""{ "visibility": "public" }""")
                    : throw new InvalidOperationException("Public visibility should be cached for the shared baseline.");
            }
        };
        var firstClient = CreateClientFromRequests(route, cache, "token-a");

        Assert.Equal(TimeSpan.FromHours(1), GitHubCacheScopeResolver.PublicVisibilityCacheDuration);
        var firstPullRequests = await firstClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);
        repoIsPublic = false;
        var secondClient = CreateClientFromRequests(route, cache, "token-b");
        var secondPullRequests = await secondClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Public list 1", Assert.Single(firstPullRequests).Title);
        Assert.Equal("Public list 1", Assert.Single(secondPullRequests).Title);
        Assert.Equal(1, visibilityProbeRequests);
        Assert.Equal(1, publicListRequests);
    }

    [Fact]
    public async Task PublicCacheWarmupStopsAfterRateLimit()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            requests.Enqueue((path, token));

            return Task.FromResult(path switch
            {
                "repos/example/rate-limited" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/rate-limited/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null => Json(
                    """{ "message": "API rate limit exceeded" }""",
                    HttpStatusCode.TooManyRequests),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var client = CreateClientFromRequests(route, cache, "token-a");
        var services = new ServiceCollection()
            .AddSingleton(client)
            .BuildServiceProvider();
        var service = new GitHubPublicCacheWarmupService(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new GitHubCacheWarmupOptions
            {
                Enabled = true,
                Repositories = ["example/rate-limited", "example/should-not-run"]
            }),
            new TestHostEnvironment(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubPublicCacheWarmupService>.Instance);

        await service.ExecuteWarmupAsync(TestContext.Current.CancellationToken);

        Assert.Contains(requests, request => request.Path == "repos/example/rate-limited");
        Assert.DoesNotContain(requests, request => request.Path == "repos/example/should-not-run");
        Assert.All(requests, request => Assert.Null(request.Token));
    }

    [Fact]
    public void GitHubCacheWarmupOptionsDefaultsAreOptIn()
    {
        var options = new GitHubCacheWarmupOptions();

        Assert.False(options.Enabled);
        Assert.Empty(options.Repositories);
    }

    [Fact]
    public async Task UnknownRepositoryPullListUsesTokenScopedCacheAcrossTokens()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var listRequestTokens = new List<string>();
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "message": "Not Found" }""", HttpStatusCode.NotFound),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is not null => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Token list {RecordListRequest(token)}")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is not null => Json("[]"),
                "repos/example/repo/pulls/1" when token is not null => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });

            string RecordListRequest(string value)
            {
                listRequestTokens.Add(value);
                return value;
            }
        };
        var firstClient = CreateClientFromRequests(route, cache, "token-a");

        var firstPullRequests = await firstClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);
        var secondClient = CreateClientFromRequests(route, cache, "token-b");
        var secondPullRequests = await secondClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Token list token-a", Assert.Single(firstPullRequests).Title);
        Assert.Equal("Token list token-b", Assert.Single(secondPullRequests).Title);
        Assert.Equal(["token-a", "token-b"], listRequestTokens);
    }

    [Fact]
    public async Task RepositoryVisibilityProbeTransportFailureFallsBackToTokenScopedCache()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var listRequestTokens = new List<string>();
        var client = CreateClientFromRequests((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            if (path == "repos/example/repo" && token is null)
            {
                throw new HttpRequestException("visibility probe failed");
            }

            return Task.FromResult(path switch
            {
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is not null => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Token list {RecordListRequest(token)}")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is not null => Json("[]"),
                "repos/example/repo/pulls/1" when token is not null => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });

            string RecordListRequest(string value)
            {
                listRequestTokens.Add(value);
                return value;
            }
        }, cache, "token-a");

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Token list token-a", Assert.Single(pullRequests).Title);
        Assert.Equal(["token-a"], listRequestTokens);
    }

    [Fact]
    public async Task RepositoryVisibilityProbeTooManyRedirectsFallsBackToTokenScopedCache()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var anonymousProbeRequests = 0;
        var listRequestTokens = new List<string>();
        var client = CreateClientFromRequests((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            if (path == "repos/example/repo" && token is null)
            {
                anonymousProbeRequests++;
                return Task.FromResult(Json(
                    """{ "message": "Moved Permanently" }""",
                    HttpStatusCode.MovedPermanently,
                    locationHeader: "https://api.github.com/repos/example/repo"));
            }

            return Task.FromResult(path switch
            {
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is not null => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Token list {RecordListRequest(token)}")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is not null => Json("[]"),
                "repos/example/repo/pulls/1" when token is not null => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });

            string RecordListRequest(string value)
            {
                listRequestTokens.Add(value);
                return value;
            }
        }, cache, "token-a");

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal(GitHubHttpRedirects.MaxRedirects + 1, anonymousProbeRequests);
        Assert.Equal("Token list token-a", Assert.Single(pullRequests).Title);
        Assert.Equal(["token-a"], listRequestTokens);
    }

    [Fact]
    public async Task PublicRepositoryPullListSkipsUnprovenCrossRepositoryLinkedIssues()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var client = CreateClientFromRequests((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            requests.Enqueue((path, token));

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/private/repo" when token is null => Json("""{ "message": "Not Found" }""", HttpStatusCode.NotFound),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null => Json(
                    PullRequestsJson([PullRequestJson(1, body: "Fixes private/repo#123")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is null => Json("[]"),
                "repos/example/repo/pulls/1" when token is null => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        }, cache, "token-a");

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(pullRequests).LinkedIssues);
        Assert.Contains(requests, request => request.Path == "repos/private/repo");
        Assert.DoesNotContain(requests, request => request.Path == "repos/private/repo/issues/123");
        Assert.All(requests, request => Assert.Null(request.Token));
    }

    [Fact]
    public async Task PublicRepositoryPullListIncludesPublicCrossRepositoryLinkedIssuesAnonymously()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var client = CreateClientFromRequests((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            requests.Enqueue((path, token));

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/other/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null => Json(
                    PullRequestsJson([PullRequestJson(1, body: "Fixes other/repo#123")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is null => Json("[]"),
                "repos/example/repo/pulls/1" when token is null => Json(PullRequestDetailsJson(1)),
                "repos/other/repo/issues/123" when token is null => Json(
                    """
                    {
                      "number": 123,
                      "title": "Public linked issue",
                      "html_url": "https://github.com/other/repo/issues/123",
                      "repository_url": "https://api.github.com/repos/other/repo",
                      "labels": []
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        }, cache, "token-a");

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        var linkedIssue = Assert.Single(Assert.Single(pullRequests).LinkedIssues);
        Assert.Equal("other/repo", linkedIssue.Repository);
        Assert.Equal(123, linkedIssue.Number);
        Assert.All(requests, request => Assert.Null(request.Token));
    }

    [Fact]
    public async Task PublicRepositoryLinkedIssueNotFoundRefreshClearsOlderLastGoodIssue()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repositoryName = new RepositoryName("example", "repo");
        var issueCacheKey = GitHubCachePolicy.CreateRepositoryCacheKey(
            GitHubCachePolicy.CreatePublicRepositoryScope(),
            repositoryName,
            "issue",
            "404");
        var issueState = "exists";
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is null => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Linked issue PR", body: "Fixes #404")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is "token-a" => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Linked issue PR", body: "Fixes #404")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is null => Json("[]"),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is "token-a" => Json("[]"),
                "repos/example/repo/pulls/1" when token is null => Json(PullRequestDetailsJson(1)),
                "repos/example/repo/pulls/1" when token is "token-a" => Json(PullRequestDetailsJson(1)),
                "repos/example/repo/issues/404" when token is null && issueState == "exists" => Json(
                    """
                    {
                      "number": 404,
                      "title": "Old linked issue",
                      "html_url": "https://github.com/example/repo/issues/404",
                      "repository_url": "https://api.github.com/repos/example/repo",
                      "labels": []
                    }
                    """),
                "repos/example/repo/issues/404" when token is null && issueState == "not-found" => Json(
                    """{ "message": "Not Found" }""",
                    HttpStatusCode.NotFound),
                "repos/example/repo/issues/404" when token is "token-a" && issueState == "not-found" => Json(
                    """{ "message": "Not Found" }""",
                    HttpStatusCode.NotFound),
                "repos/example/repo/issues/404" when token is null => Json(
                    """{ "message": "GitHub unavailable" }""",
                    HttpStatusCode.ServiceUnavailable),
                "repos/example/repo/issues/404" when token is "token-a" => Json(
                    """{ "message": "GitHub unavailable" }""",
                    HttpStatusCode.ServiceUnavailable),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var client = CreateClientFromRequests(route, cache, "token-a");

        var originalPullRequests = await client.GetPullRequestsAsync(
            repositoryName,
            "open",
            false,
            TestContext.Current.CancellationToken);
        issueState = "not-found";
        var notFoundPullRequests = await client.GetPullRequestsAsync(
            repositoryName,
            "open",
            true,
            TestContext.Current.CancellationToken);
        cache.Remove(issueCacheKey);
        issueState = "transient";
        var fallbackPullRequests = await client.GetPullRequestsAsync(
            repositoryName,
            "open",
            true,
            TestContext.Current.CancellationToken);

        Assert.Single(Assert.Single(originalPullRequests).LinkedIssues);
        Assert.Empty(Assert.Single(notFoundPullRequests).LinkedIssues);
        Assert.Empty(Assert.Single(fallbackPullRequests).LinkedIssues);
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
    public async Task FocusIssuesDiscoverRegressionLabelsAndExcludePullRequests()
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
                _ when path == CtiTeamIssueSearchPath("open") => Json("""{ "total_count": 0, "incomplete_results": false, "items": [] }"""),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        var issues = await client.GetFocusIssuesAsync(
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
        Assert.Contains(CtiTeamIssueSearchPath("open"), requestedPaths);
        Assert.DoesNotContain(
            "repos/example/repo/issues?state=open&sort=updated&direction=desc&per_page=100",
            requestedPaths);
    }

    [Fact]
    public async Task FocusIssuesIncludeCtiTeamTitleIssues()
    {
        var requestedPaths = new List<string>();
        var client = CreateClient(path =>
        {
            requestedPaths.Add(path);
            return path switch
            {
                "repos/example/repo/labels?per_page=100" => Json("[]"),
                _ when path == CtiTeamIssueSearchPath("open") => Json(
                    """
                    {
                      "total_count": 3,
                      "incomplete_results": false,
                      "items": [
                        {
                          "number": 20,
                          "title": "[AspireE2E] Validate dashboard flows",
                          "state": "open",
                          "user": { "login": "jinzhao1127" },
                          "html_url": "https://github.com/example/repo/issues/20",
                          "repository_url": "https://api.github.com/repos/example/repo",
                          "created_at": "2026-01-01T00:00:00Z",
                          "updated_at": "2026-01-08T00:00:00Z",
                          "labels": [],
                          "assignees": [{ "login": "cti-owner" }]
                        },
                        {
                          "number": 21,
                          "title": "[AspireE2E] Pull request mirror",
                          "state": "open",
                          "user": { "login": "EmilyFeng97" },
                          "html_url": "https://github.com/example/repo/pull/21",
                          "repository_url": "https://api.github.com/repos/example/repo",
                          "created_at": "2026-01-02T00:00:00Z",
                          "updated_at": "2026-01-09T00:00:00Z",
                          "labels": [],
                          "assignees": [],
                          "pull_request": { "url": "https://api.github.com/repos/example/repo/pulls/21" }
                        },
                        {
                          "number": 22,
                          "title": "AspireE2E without the title marker",
                          "state": "open",
                          "user": { "login": "Susie-1989" },
                          "html_url": "https://github.com/example/repo/issues/22",
                          "repository_url": "https://api.github.com/repos/example/repo",
                          "created_at": "2026-01-03T00:00:00Z",
                          "updated_at": "2026-01-10T00:00:00Z",
                          "labels": [],
                          "assignees": []
                        }
                      ]
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        var issues = await client.GetFocusIssuesAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        var issue = Assert.Single(issues);
        Assert.Equal(20, issue.Number);
        Assert.Equal("[AspireE2E] Validate dashboard flows", issue.Title);
        Assert.Equal("jinzhao1127", issue.Author);
        Assert.Equal(["cti-owner"], issue.Assignees);
        Assert.Contains(CtiTeamIssueSearchPath("open"), requestedPaths);
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
    public async Task PullListUsesLastGoodDataWhenForcedRefreshHitsTransientFailure()
    {
        var listRequests = 0;
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

            return path switch
            {
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when ++listRequests == 1 => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Last known good")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                    """{ "message": "API rate limit exceeded" }""",
                    HttpStatusCode.TooManyRequests),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        var originalPullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        var fallbackPullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(fallbackPullRequests);
        Assert.Equal("Last known good", pullRequest.Title);
        Assert.Equal(originalPullRequests[0].FetchedAt, pullRequest.FetchedAt);
    }

    [Fact]
    public async Task PullListUsesLastGoodDataWhenForcedRefreshHitsTransportFailure()
    {
        var listRequests = 0;
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

            return path switch
            {
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when ++listRequests == 1 => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Last known good")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => throw new HttpRequestException("No sockets available."),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        var originalPullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        var fallbackPullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(fallbackPullRequests);
        Assert.Equal("Last known good", pullRequest.Title);
        Assert.Equal(originalPullRequests[0].FetchedAt, pullRequest.FetchedAt);
    }

    [Fact]
    public async Task PullListConvertsTransportFailureToGitHubApiFailure()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => throw new HttpRequestException("No sockets available."),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var exception = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    [Fact]
    public async Task StreamPullRequestsReplaysLastGoodDataWhenTransientFailureHappensBeforeFirstItem()
    {
        var listRequests = 0;
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

            return path switch
            {
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when ++listRequests == 1 => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Streamed good")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                    """{ "message": "GitHub unavailable" }""",
                    HttpStatusCode.ServiceUnavailable),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        var originalPullRequests = await EnumerateAsync(client.StreamPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken));

        var fallbackPullRequests = await EnumerateAsync(client.StreamPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken));

        var pullRequest = Assert.Single(fallbackPullRequests);
        Assert.Equal("Streamed good", pullRequest.Title);
        Assert.Equal(originalPullRequests[0].FetchedAt, pullRequest.FetchedAt);
    }

    [Fact]
    public async Task StreamPullRequestsDoesNotAppendLastGoodDataAfterFreshItemsWereEmitted()
    {
        var listRequests = 0;
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
                return Json(
                    """{ "message": "GitHub unavailable" }""",
                    HttpStatusCode.ServiceUnavailable);
            }

            return path switch
            {
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when ++listRequests == 1 => Json(
                    PullRequestsJson(Enumerable.Range(1, 21).Select(number => PullRequestJson(number, title: $"Good {number}")))),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                    PullRequestsJson(Enumerable.Range(1, 20).Select(number => PullRequestJson(number, title: $"Fresh {number}"))),
                    linkHeader: "<https://api.github.com/repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100&page=2>; rel=\"next\""),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        await EnumerateAsync(client.StreamPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken));

        await using var enumerator = client.StreamPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var streamedNumbers = new List<int>();

        await Assert.ThrowsAsync<GitHubApiException>(async () =>
        {
            while (await enumerator.MoveNextAsync())
            {
                streamedNumbers.Add(enumerator.Current.Number);
            }
        });

        Assert.Equal(Enumerable.Range(1, 20), streamedNumbers);
    }

    [Fact]
    public async Task PullListDoesNotUseLastGoodDataForNonTransientFailures()
    {
        var listRequests = 0;
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

            return path switch
            {
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when ++listRequests == 1 => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Last known good")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                    """{ "message": "Not Found" }""",
                    HttpStatusCode.NotFound),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<GitHubApiException>(() => client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PullListDoesNotUseLastGoodDataForCallerCancellation()
    {
        var listRequests = 0;
        var client = CreateClient((path, cancellationToken) =>
        {
            if (TryGetPullRequestNumber(path, "/reviews?per_page=100", out _))
            {
                return Task.FromResult(Json("[]"));
            }

            if (TryGetPullRequestNumber(path, "", out var detailsNumber))
            {
                return Task.FromResult(Json(PullRequestDetailsJson(detailsNumber)));
            }

            return path switch
            {
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when ++listRequests == 1 => Task.FromResult(
                    Json(PullRequestsJson([PullRequestJson(1, title: "Last known good")]))),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Task.FromCanceled<HttpResponseMessage>(cancellationToken),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            cancellation.Token));
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
            "repos/example/repo/pulls/1" => Json(PullRequestDetailsJson(1)),
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
    public async Task PullListIncludesMergeableStateFromDetailsForOpenPullRequests()
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
                    "state": "APPROVED",
                    "submitted_at": "2026-01-02T00:00:00Z"
                  }
                ]
                """),
            "repos/example/repo/pulls/1" => Json(PullRequestJson(1, mergeableState: "dirty")),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal("approved", pullRequest.Review.State);
        Assert.Equal("dirty", pullRequest.MergeableState);
    }

    [Fact]
    public async Task PullListByLabelReusesAlreadyFetchedPullRequestDetails()
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
                      }
                    ]
                    """),
                "repos/example/repo/pulls/5" => Json(PullRequestJson(
                    5,
                    title: "Generated docs",
                    mergeableState: "dirty")),
                "repos/example/repo/pulls/5/reviews?per_page=100" => Json(
                    """
                    [
                      {
                        "user": { "login": "reviewer" },
                        "state": "APPROVED",
                        "submitted_at": "2026-01-02T00:00:00Z"
                      }
                    ]
                    """),
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
        Assert.Equal("dirty", pullRequest.MergeableState);
        Assert.Equal(1, requestedPaths.Count(path => path == "repos/example/repo/pulls/5"));
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
    public async Task GitHubRequestsLimitConcurrentHttpRequests()
    {
        const int requestCount = 16;
        var activeRequests = 0;
        var maxActiveRequests = 0;
        var activeGate = new object();

        var client = CreateClient(async (path, cancellationToken) =>
        {
            if (!TryGetPullRequestNumber(path, "", out var number))
            {
                throw new InvalidOperationException($"Unexpected GitHub request: {path}");
            }

            lock (activeGate)
            {
                activeRequests++;
                maxActiveRequests = Math.Max(maxActiveRequests, activeRequests);
            }

            try
            {
                await Task.Delay(50, cancellationToken);
                return Json(PullRequestDetailsJson(number));
            }
            finally
            {
                lock (activeGate)
                {
                    activeRequests--;
                }
            }
        });

        await Task.WhenAll(Enumerable.Range(1, requestCount).Select(number => client.GetPullRequestDetailsAsync(
            new RepositoryName("example", "repo"),
            number,
            false,
            TestContext.Current.CancellationToken)));

        Assert.True(
            maxActiveRequests <= GitHubClient.MaxConcurrentGitHubRequests,
            $"Expected at most {GitHubClient.MaxConcurrentGitHubRequests} concurrent GitHub requests but saw {maxActiveRequests}.");
    }

    [Fact]
    public async Task PullRequestDetailsForceRefreshUsesLastGoodDetailsAfterCachedDetailsExpire()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repositoryName = new RepositoryName("example", "repo");
        var cacheKey = GitHubCachePolicy.CreateRepositoryCacheKey(
            GitHubCachePolicy.CreatePublicRepositoryScope(),
            repositoryName,
            "pull",
            "1");
        var failRefresh = false;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls/1" when token is null && !failRefresh => Json(PullRequestDetailsJson(1)),
                "repos/example/repo/pulls/1" when token is "token-a" && failRefresh => Json(
                    """{ "message": "GitHub unavailable" }""",
                    HttpStatusCode.ServiceUnavailable),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var client = CreateClientFromRequests(route, cache, "token-a");

        var original = await client.GetPullRequestDetailsAsync(
            repositoryName,
            1,
            false,
            TestContext.Current.CancellationToken);
        cache.Remove(cacheKey);
        failRefresh = true;
        var fallback = await client.GetPullRequestDetailsAsync(
            repositoryName,
            1,
            true,
            TestContext.Current.CancellationToken);

        Assert.Equal(original.CommitCount, fallback.CommitCount);
        Assert.Equal(original.ChangedFiles, fallback.ChangedFiles);
    }

    [Fact]
    public async Task PullRequestChecksForceRefreshUsesCooldownAfterBypassingCachedStatus()
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

        await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            request,
            true,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, requestCount);
    }

    [Fact]
    public async Task PullRequestChecksForceRefreshKeepsCachedStatusWhenRefreshFailsTransiently()
    {
        var checkRunRequests = 0;
        var statusRequests = 0;
        var failRefresh = false;
        var client = CreateClient(path =>
        {
            if (path.Contains("/check-runs?", StringComparison.Ordinal))
            {
                checkRunRequests++;
            }

            if (path.Contains("/status?", StringComparison.Ordinal))
            {
                statusRequests++;
            }

            return path switch
            {
                "repos/example/repo/commits/cache123/check-runs?filter=latest&per_page=100" when !failRefresh => Json(
                    """
                    {
                      "total_count": 1,
                      "check_runs": [
                        { "id": 1, "name": "tests", "status": "completed", "conclusion": "failure", "completed_at": "2026-01-02T00:45:00Z" }
                      ]
                    }
                    """),
                "repos/example/repo/commits/cache123/check-runs?filter=latest&per_page=100" => Json(
                    """{ "message": "GitHub unavailable" }""",
                    HttpStatusCode.ServiceUnavailable),
                "repos/example/repo/commits/cache123/status?per_page=100" => Json(
                    """{ "state": "success", "total_count": 0, "statuses": [] }"""),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });
        var request = new[] { new PullRequestChecksRequestItem(1, "cache123") };

        var original = await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            request,
            false,
            TestContext.Current.CancellationToken);
        failRefresh = true;
        var fallback = await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            request,
            true,
            TestContext.Current.CancellationToken);

        Assert.Equal("failure", Assert.Single(original).Checks.State);
        Assert.Equal("failure", Assert.Single(fallback).Checks.State);
        Assert.Equal(2, checkRunRequests);
        Assert.Equal(2, statusRequests);
    }

    [Fact]
    public async Task PublicPullRequestChecksColdTransientFailureDoesNotPinSharedBaseline()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var failCheckRuns = true;
        var checkRunRequests = 0;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/commits/cache123/check-runs?filter=latest&per_page=100" when token is null && failCheckRuns => Json(
                    """{ "message": "GitHub unavailable" }""",
                    HttpStatusCode.ServiceUnavailable),
                "repos/example/repo/commits/cache123/check-runs?filter=latest&per_page=100" when token is null => Json(
                    $$"""
                    {
                      "total_count": 1,
                      "check_runs": [
                        { "id": {{++checkRunRequests}}, "name": "tests", "status": "completed", "conclusion": "failure", "completed_at": "2026-01-02T00:45:00Z" }
                      ]
                    }
                    """),
                "repos/example/repo/commits/cache123/status?per_page=100" when token is null => Json(
                    """{ "state": "success", "total_count": 0, "statuses": [] }"""),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var client = CreateClientFromRequests(route, cache, "token-a");
        var request = new[] { new PullRequestChecksRequestItem(1, "cache123") };

        var incomplete = await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            request,
            false,
            TestContext.Current.CancellationToken);
        failCheckRuns = false;
        cache.Compact(1.0);
        var recovered = await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            request,
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal("none", Assert.Single(incomplete).Checks.State);
        Assert.Equal("failure", Assert.Single(recovered).Checks.State);
        Assert.Equal(1, checkRunRequests);
    }

    [Fact]
    public async Task PullRequestChecksForceRefreshUsesLastGoodStatusAfterCachedStatusExpires()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repositoryName = new RepositoryName("example", "repo");
        var cacheKey = GitHubCachePolicy.CreateRepositoryCacheKey(
            GitHubCachePolicy.CreatePublicRepositoryScope(),
            repositoryName,
            "checks",
            "cache123");
        var failRefresh = false;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/commits/cache123/check-runs?filter=latest&per_page=100" when token is null && !failRefresh => Json(
                    """
                    {
                      "total_count": 1,
                      "check_runs": [
                        { "id": 1, "name": "tests", "status": "completed", "conclusion": "failure", "completed_at": "2026-01-02T00:45:00Z" }
                      ]
                    }
                    """),
                "repos/example/repo/commits/cache123/check-runs?filter=latest&per_page=100" when token is "token-a" && failRefresh => Json(
                    """{ "message": "GitHub unavailable" }""",
                    HttpStatusCode.ServiceUnavailable),
                "repos/example/repo/commits/cache123/status?per_page=100" when token is null => Json(
                    """{ "state": "success", "total_count": 0, "statuses": [] }"""),
                "repos/example/repo/commits/cache123/status?per_page=100" when token is "token-a" => Json(
                    """{ "state": "success", "total_count": 0, "statuses": [] }"""),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var client = CreateClientFromRequests(route, cache, "token-a");
        var request = new[] { new PullRequestChecksRequestItem(1, "cache123") };

        var original = await client.GetPullRequestChecksAsync(
            repositoryName,
            request,
            false,
            TestContext.Current.CancellationToken);
        cache.Remove(cacheKey);
        failRefresh = true;
        var fallback = await client.GetPullRequestChecksAsync(
            repositoryName,
            request,
            true,
            TestContext.Current.CancellationToken);

        Assert.Equal("failure", Assert.Single(original).Checks.State);
        Assert.Equal("failure", Assert.Single(fallback).Checks.State);
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
    public async Task ShipWeekValidationRefreshClearsOlderLastGoodSuccess()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repositoryName = new RepositoryName("example", "repo");
        var cacheKey = GitHubCachePolicy.CreateRepositoryCacheKey(
            GitHubCachePolicy.CreatePublicRepositoryScope(),
            repositoryName,
            "ship-week",
            "13.4",
            "release/13.4");
        var branchExists = true;
        var failMilestones = false;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/milestones?state=all&per_page=100" when token is null && !failMilestones => Json(
                    """[{ "number": 7, "title": "13.4" }]"""),
                "repos/example/repo/milestones?state=all&per_page=100" when token is "token-a" && !failMilestones => Json(
                    """[{ "number": 7, "title": "13.4" }]"""),
                "repos/example/repo/milestones?state=all&per_page=100" when token is "token-a" => Json(
                    """{ "message": "GitHub unavailable" }""",
                    HttpStatusCode.ServiceUnavailable),
                "repos/example/repo/branches/release%2F13.4" when token is null && branchExists => Json(
                    """{ "name": "release/13.4" }"""),
                "repos/example/repo/branches/release%2F13.4" when token is "token-a" && branchExists => Json(
                    """{ "name": "release/13.4" }"""),
                "repos/example/repo/branches/release%2F13.4" when token is "token-a" => Json(
                    """{ "message": "Not Found" }""",
                    HttpStatusCode.NotFound),
                "repos/example/repo/issues?state=open&milestone=7&per_page=100" when token is null => Json("[]"),
                "repos/example/repo/issues?state=open&milestone=7&per_page=100" when token is "token-a" => Json("[]"),
                "repos/example/repo/pulls?state=open&base=release%2F13.4&sort=created&direction=asc&per_page=100" when token is null => Json("[]"),
                "repos/example/repo/pulls?state=open&base=release%2F13.4&sort=created&direction=asc&per_page=100" when token is "token-a" => Json("[]"),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var client = CreateClientFromRequests(route, cache, "token-a");

        var success = await client.GetShipWeekAsync(
            repositoryName,
            "13.4",
            "release/13.4",
            false,
            TestContext.Current.CancellationToken);
        branchExists = false;
        var validation = await client.GetShipWeekAsync(
            repositoryName,
            "13.4",
            "release/13.4",
            true,
            TestContext.Current.CancellationToken);
        cache.Remove(cacheKey);
        failMilestones = true;

        var transientFallback = await client.GetShipWeekAsync(
            repositoryName,
            "13.4",
            "release/13.4",
            true,
            TestContext.Current.CancellationToken);

        Assert.NotNull(success.Response);
        Assert.Null(validation.Response);
        Assert.True(validation.ValidationErrors.ContainsKey("releaseBranch"));
        Assert.Null(transientFallback.Response);
        Assert.True(transientFallback.ValidationErrors.ContainsKey("releaseBranch"));
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
        var cache = new MemoryCache(new MemoryCacheOptions());
        var cacheScopeResolver = new GitHubCacheScopeResolver(httpClient, tokenProvider, cache);

        return new GitHubClient(httpClient, tokenProvider, cacheScopeResolver, cache, new TestHostEnvironment());
    }

    private static GitHubClient CreateClientFromRequests(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route,
        IMemoryCache cache,
        string token)
    {
        var httpClient = new HttpClient(new RequestStubGitHubHandler(route))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var tokenProvider = new GitHubTokenProvider(
            new HttpContextAccessor { HttpContext = CreateHttpContextWithGitHubToken(token) },
            new TestHostEnvironment());
        var cacheScopeResolver = new GitHubCacheScopeResolver(httpClient, tokenProvider, cache);

        return new GitHubClient(httpClient, tokenProvider, cacheScopeResolver, cache, new TestHostEnvironment());
    }

    private static GitHubClient CreateAnonymousClientFromRequests(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route,
        IMemoryCache cache)
    {
        var httpClient = new HttpClient(new RequestStubGitHubHandler(route))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var tokenProvider = new GitHubTokenProvider(
            new HttpContextAccessor { HttpContext = CreateAnonymousHttpContext() },
            new TestHostEnvironment());
        var cacheScopeResolver = new GitHubCacheScopeResolver(httpClient, tokenProvider, cache);

        return new GitHubClient(httpClient, tokenProvider, cacheScopeResolver, cache, new TestHostEnvironment());
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

    private static DefaultHttpContext CreateHttpContextWithGitHubToken(string token = "unit-test-token")
    {
        var context = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddSingleton(new TestGitHubToken(token));
        services.AddLogging();
        services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("test", _ => { });
        context.RequestServices = services.BuildServiceProvider();

        return context;
    }

    private static DefaultHttpContext CreateAnonymousHttpContext()
    {
        var context = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication("anonymous")
            .AddScheme<AuthenticationSchemeOptions, TestAnonymousAuthHandler>("anonymous", _ => { });
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

    private static string CtiTeamIssueSearchPath(string state) =>
        $"search/issues?q=repo%3Aexample%2Frepo%20is%3Aissue%20state%3A{state}%20in%3Atitle%20AspireE2E&sort=updated&order=desc&per_page=100";

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
        string? baseRef = null,
        string? mergeableState = null)
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
          "base": {{baseJson}},
          "mergeable_state": {{JsonSerializer.Serialize(mergeableState)}}
        }
        """;
    }

    private sealed class StubGitHubHandler(Func<string, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            if (request.Headers.Authorization is null)
            {
                if (IsRepositoryVisibilityProbe(path))
                {
                    return Json("""{ "message": "Not Found" }""", HttpStatusCode.NotFound);
                }

                throw new InvalidOperationException($"Unexpected anonymous GitHub request: {path}");
            }

            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("unit-test-token", request.Headers.Authorization?.Parameter);
            return await route(path, cancellationToken);
        }
    }

    private static bool IsRepositoryVisibilityProbe(string path)
    {
        var parts = path.Split('/');
        return parts.Length == 3
            && parts[0].Equals("repos", StringComparison.Ordinal)
            && parts.All(part => !string.IsNullOrWhiteSpace(part))
            && !path.Contains('?', StringComparison.Ordinal);
    }

    private sealed class RequestStubGitHubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            route(request, cancellationToken);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "pr-timeline-app.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed record TestGitHubToken(string Value);

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TestGitHubToken token)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[] { new Claim(ClaimTypes.Name, "octocat") };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var properties = new AuthenticationProperties();
            properties.StoreTokens([
                new AuthenticationToken
                {
                    Name = "access_token",
                    Value = token.Value
                }
            ]);
            var ticket = new AuthenticationTicket(principal, properties, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class TestAnonymousAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

}
