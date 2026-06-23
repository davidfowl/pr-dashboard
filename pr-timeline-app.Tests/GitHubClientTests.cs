using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
    public async Task AuthenticatedAllowlistedRepositoryUsesTokenScopedCacheAcrossTokens()
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
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is not null => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Token list {token}")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is not null => Json("[]"),
                "repos/example/repo/pulls/1" when token is not null => Json(PullRequestDetailsJson(1)),
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

        Assert.Equal("Token list token-a", Assert.Single(firstPullRequests).Title);
        Assert.Equal("Token list token-b", Assert.Single(secondPullRequests).Title);
        Assert.DoesNotContain(requests, request => request.Token is null);
    }

    [Fact]
    public async Task AnonymousAllowlistedRepositoryColdCacheUsesServerVisibilityGateAndReturnsUnavailable()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var client = CreateAnonymousClientFromRequests((request, _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            requests.Enqueue((path, token));
            return Task.FromResult(path switch
            {
                "repos/example/repo" when token == "public-cache-token" => Json("""{ "visibility": "public" }"""),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        }, cache);

        var exception = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(("repos/example/repo", "public-cache-token"), Assert.Single(requests));
    }

    [Fact]
    public async Task AnonymousNonAllowlistedRepositoryDoesNotProbeAndStaysTokenScoped()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var options = CreateWarmupOptions(repositories: ["other/repo"]);
        var client = CreateAnonymousClientFromRequests((request, _) =>
        {
            requests.Enqueue((request.RequestUri?.PathAndQuery.TrimStart('/') ?? "", request.Headers.Authorization?.Parameter));
            throw new InvalidOperationException("Non-allowlisted anonymous repos should require a token before HTTP.");
        }, cache, options);

        var exception = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task PublicCacheWarmupUsesServerTokenAndWritesListOnlyBaseline()
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
                "repos/example/repo" when token == "public-cache-token" => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "public-cache-token" => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Public list {++listRequests}", body: "Fixes #404")])),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var warmupClient = CreateClientFromRequests(route, cache, "token-a");

        var warmed = await warmupClient.TryPrewarmPublicPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);
        var readClient = CreateAnonymousClientFromRequests(route, cache);
        var pullRequests = await readClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.True(warmed);
        Assert.Equal("Public list 1", pullRequest.Title);
        Assert.Empty(pullRequest.LinkedIssues);
        Assert.Equal("waiting", pullRequest.Review.State);
        Assert.Equal("none", pullRequest.Checks.State);
        Assert.Equal(1, pullRequest.CommitCount);
        Assert.Equal(1, listRequests);
        Assert.DoesNotContain(requests, request => request.Path.Contains("/reviews", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, request => request.Path == "repos/example/repo/pulls/1");
        Assert.All(requests, request => Assert.Equal("public-cache-token", request.Token));
    }

    [Fact]
    public async Task AnonymousAllowlistedRepositoryTimelineLoadsLivePublicDetailsAfterListWarmup()
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
                "repos/example/repo" when token == "public-cache-token" => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "public-cache-token" => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Public list item", headSha: "abc123")])),
                "repos/example/repo/pulls/1" when token == "public-cache-token" => Json(
                    PullRequestJson(1, headSha: "abc123", mergeableState: "clean")),
                "repos/example/repo/issues/1/timeline?per_page=100" when token == "public-cache-token" => Json(
                    """
                    [
                      {
                        "id": 100,
                        "event": "commented",
                        "actor": { "login": "reviewer" },
                        "created_at": "2026-01-02T12:00:00Z",
                        "body": "Looks good",
                        "html_url": "https://github.com/example/repo/pull/1#issuecomment-100"
                      }
                    ]
                    """),
                "repos/example/repo/commits/abc123/check-runs?filter=latest&per_page=100" when token == "public-cache-token" => Json(
                    """
                    {
                      "total_count": 1,
                      "check_runs": [
                        { "id": 1, "name": "build", "status": "completed", "conclusion": "success", "completed_at": "2026-01-02T12:30:00Z" }
                      ]
                    }
                    """),
                "repos/example/repo/commits/abc123/status?per_page=100" when token == "public-cache-token" => Json(
                    """{ "state": "success", "total_count": 0, "statuses": [] }"""),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var warmupClient = CreateClientFromRequests(route, cache, "token-a");

        var warmed = await warmupClient.TryPrewarmPublicPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);
        var readClient = CreateAnonymousClientFromRequests(route, cache);
        var pullRequests = await readClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);
        var timeline = await new GitHubPullRequestService(readClient).GetTimelineAsync(
            new RepositoryName("example", "repo"),
            1,
            false,
            TestContext.Current.CancellationToken);

        Assert.True(warmed);
        Assert.Equal("Public list item", Assert.Single(pullRequests).Title);
        Assert.Equal("example/repo", timeline.Repository);
        Assert.Equal(1, timeline.Number);
        Assert.Equal("clean", timeline.MergeableState);
        Assert.Equal("success", timeline.Checks.State);
        var item = Assert.Single(timeline.Items);
        Assert.Equal("commented", item.Event);
        Assert.Equal("reviewer", item.Actor);
        Assert.All(requests, request => Assert.Equal("public-cache-token", request.Token));
    }

    [Fact]
    public async Task AuthenticatedAllowlistedRepositoryDoesNotUsePublicBaselineBeforeTokenLoad()
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
                "repos/example/repo" when token == "public-cache-token" => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "public-cache-token" => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Public baseline")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "token-a" => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Token enriched list")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token == "token-a" => Json("[]"),
                "repos/example/repo/pulls/1" when token == "token-a" => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var warmupClient = CreateClientFromRequests(route, cache, "token-a");
        await warmupClient.TryPrewarmPublicPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);
        var authenticatedClient = CreateClientFromRequests(route, cache, "token-a");

        var pullRequests = await authenticatedClient.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Token enriched list", Assert.Single(pullRequests).Title);
        Assert.Contains(requests, request => request.Token == "token-a" && request.Path.Contains("/reviews", StringComparison.Ordinal));
        Assert.Contains(requests, request => request.Token == "token-a" && request.Path == "repos/example/repo/pulls/1");
    }

    [Fact]
    public async Task AnonymousAllowlistedRepositoryReadsLastGoodAfterPrimaryPublicEntryExpires()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repositoryName = new RepositoryName("example", "repo");
        var cacheKey = GitHubCachePolicy.CreateRepositoryCacheKey(
            GitHubCachePolicy.CreatePublicRepositoryScope(),
            repositoryName,
            "pulls",
            "open");
        var listRequests = 0;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token == "public-cache-token" => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "public-cache-token" && ++listRequests == 1 => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Last good public list")])),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var warmupClient = CreateClientFromRequests(route, cache, "token-a");
        await warmupClient.TryPrewarmPublicPullRequestsAsync(repositoryName, "open", TestContext.Current.CancellationToken);
        cache.Remove(cacheKey);
        var anonymousClient = CreateAnonymousClientFromRequests(route, cache);

        var pullRequests = await anonymousClient.GetPullRequestsAsync(
            repositoryName,
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Last good public list", Assert.Single(pullRequests).Title);
        Assert.Equal(1, listRequests);
    }

    [Fact]
    public async Task PublicCacheWarmupPurgesTrackedPublicSnapshotWhenRepositoryStopsBeingPublic()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repositoryName = new RepositoryName("example", "repo");
        var visibility = "public";
        var listRequests = 0;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token == "public-cache-token" => Json($$"""{ "visibility": "{{visibility}}" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "public-cache-token" => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"Public list {++listRequests}")])),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var warmupClient = CreateClientFromRequests(route, cache, "token-a");

        var initialWarmup = await warmupClient.TryPrewarmPublicPullRequestsAsync(
            repositoryName,
            "open",
            TestContext.Current.CancellationToken);
        var anonymousClient = CreateAnonymousClientFromRequests(route, cache);
        var initialRead = await anonymousClient.GetPullRequestsAsync(
            repositoryName,
            "open",
            false,
            TestContext.Current.CancellationToken);
        visibility = "private";
        var privateWarmup = await warmupClient.TryPrewarmPublicPullRequestsAsync(
            repositoryName,
            "open",
            TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<GitHubApiException>(() => anonymousClient.GetPullRequestsAsync(
            repositoryName,
            "open",
            false,
            TestContext.Current.CancellationToken));

        Assert.True(initialWarmup);
        Assert.Equal("Public list 1", Assert.Single(initialRead).Title);
        Assert.False(privateWarmup);
        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(1, listRequests);
    }

    [Fact]
    public async Task AuthenticatedAllowlistedRepositoryDoesNotUsePublicFallbackAfterRepositorySnapshotIsPurged()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repositoryName = new RepositoryName("example", "repo");
        var visibility = "public";
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token == "public-cache-token" => Json($$"""{ "visibility": "{{visibility}}" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "public-cache-token" => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Public list")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "token-a" => Json(
                    """{ "message": "GitHub unavailable" }""",
                    HttpStatusCode.ServiceUnavailable),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var client = CreateClientFromRequests(route, cache, "token-a");
        await client.TryPrewarmPublicPullRequestsAsync(repositoryName, "open", TestContext.Current.CancellationToken);
        visibility = "private";
        await client.TryPrewarmPublicPullRequestsAsync(repositoryName, "open", TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetPullRequestsAsync(
            repositoryName,
            "open",
            false,
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    [Fact]
    public async Task AnonymousAllowlistedRepositoryUsesTrackedLastGoodWhenVisibilityVerificationIsTransient()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repositoryName = new RepositoryName("example", "repo");
        var cacheKey = GitHubCachePolicy.CreateRepositoryCacheKey(
            GitHubCachePolicy.CreatePublicRepositoryScope(),
            repositoryName,
            "pulls",
            "open");
        var rateLimitVisibility = false;
        var listRequests = 0;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token == "public-cache-token" && rateLimitVisibility => Json(
                    """{ "message": "API rate limit exceeded" }""",
                    HttpStatusCode.Forbidden),
                "repos/example/repo" when token == "public-cache-token" => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "public-cache-token" && ++listRequests == 1 => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Last good public list")])),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var publicCacheStore = new GitHubPublicCacheStore(cache);
        var warmupClient = CreateClientFromRequests(route, cache, "token-a", publicCacheStore: publicCacheStore);
        await warmupClient.TryPrewarmPublicPullRequestsAsync(repositoryName, "open", TestContext.Current.CancellationToken);
        cache.Remove(cacheKey);
        rateLimitVisibility = true;
        var anonymousClient = CreateAnonymousClientFromRequests(route, cache, publicCacheStore: publicCacheStore);

        var pullRequests = await anonymousClient.GetPullRequestsAsync(
            repositoryName,
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Equal("Last good public list", Assert.Single(pullRequests).Title);
        Assert.Equal(1, listRequests);
    }

    [Fact]
    public async Task PublicCacheWarmupSkipsWithoutServerTokenAndDoesNotCallGitHub()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var options = CreateWarmupOptions(publicCacheToken: null);
        var client = CreateClientFromRequests((request, _) =>
        {
            requests.Enqueue((request.RequestUri?.PathAndQuery.TrimStart('/') ?? "", request.Headers.Authorization?.Parameter));
            throw new InvalidOperationException("Warmup without a server token should not call GitHub.");
        }, cache, "token-a", options);

        var warmed = await client.TryPrewarmPublicPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        Assert.False(warmed);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task TokenStreamWritesDurablePublicBaselineWithoutServerToken()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var publicCacheStore = new GitHubPublicCacheStore(cache);
        var options = CreateWarmupOptions(publicCacheToken: null);
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            requests.Enqueue((path, token));

            return Task.FromResult(path switch
            {
                "repos/example/repo" when token is null => Json("""{ "visibility": "public" }"""),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "token-a" => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Token A list")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "token-b" => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Token B live")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is "token-a" or "token-b" => Json("[]"),
                "repos/example/repo/pulls/1" when token is "token-a" or "token-b" => Json(PullRequestDetailsJson(1)),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        };
        var firstClient = CreateClientFromRequests(route, cache, "token-a", options, publicCacheStore);

        await EnumerateAsync(firstClient.StreamPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken));
        Assert.True(await publicCacheStore.HasTrackedSnapshotAsync(
            new RepositoryName("example", "repo"),
            TestContext.Current.CancellationToken));
        var publicCacheKey = GitHubCachePolicy.CreateRepositoryCacheKey(
            GitHubCachePolicy.CreatePublicRepositoryScope(),
            new RepositoryName("example", "repo"),
            "pulls",
            "open");
        var publicCacheLookup = await new GitHubResponseCache(cache, publicCacheStore)
            .GetAsync<IReadOnlyList<PullRequestSummary>>(publicCacheKey, TestContext.Current.CancellationToken);
        Assert.True(publicCacheLookup.Found);
        Assert.Equal("Token A list", Assert.Single(publicCacheLookup.Value!).Title);
        var tokenBKey = GitHubCachePolicy.CreateRepositoryCacheKey(
            CreateTokenScopeForTestToken("token-b"),
            new RepositoryName("example", "repo"),
            "pulls",
            "open");
        var tokenBLookup = await new GitHubResponseCache(cache, publicCacheStore)
            .GetAsync<IReadOnlyList<PullRequestSummary>>(tokenBKey, TestContext.Current.CancellationToken);
        Assert.False(tokenBLookup.Found);
        var secondClient = CreateClientFromRequests(route, cache, "token-b", options, publicCacheStore);
        var secondStream = await EnumerateAsync(secondClient.StreamPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken));

        Assert.Equal("Token A list", secondStream[0].Title);
        Assert.Equal("waiting", secondStream[0].Review.State);
        Assert.Equal("none", secondStream[0].Checks.State);
        Assert.Contains(secondStream.Skip(1), pullRequest => pullRequest.Title == "Token B live");
        Assert.Contains(requests, request => request.Path == "repos/example/repo" && request.Token is null);
        Assert.DoesNotContain(requests, request => request.Token == "public-cache-token");
    }

    [Fact]
    public async Task PublicCacheWarmupRequiresAllowlistBeforeCallingGitHub()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var requests = new ConcurrentQueue<(string Path, string? Token)>();
        var options = CreateWarmupOptions(repositories: ["other/repo"]);
        var client = CreateClientFromRequests((request, _) =>
        {
            requests.Enqueue((request.RequestUri?.PathAndQuery.TrimStart('/') ?? "", request.Headers.Authorization?.Parameter));
            throw new InvalidOperationException("Non-allowlisted warmup should not call GitHub.");
        }, cache, "token-a", options);

        var warmed = await client.TryPrewarmPublicPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        Assert.False(warmed);
        Assert.Empty(requests);
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
                "repos/example/rate-limited" when token == "public-cache-token" => Json("""{ "visibility": "public" }"""),
                "repos/example/rate-limited/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "public-cache-token" => Json(
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
                PublicCacheToken = "public-cache-token",
                Repositories = ["example/rate-limited", "example/should-not-run"]
            }),
            new TestHostEnvironment(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubPublicCacheWarmupService>.Instance);

        await service.ExecuteWarmupAsync(TestContext.Current.CancellationToken);

        Assert.Contains(requests, request => request.Path == "repos/example/rate-limited");
        Assert.DoesNotContain(requests, request => request.Path == "repos/example/should-not-run");
        Assert.All(requests, request => Assert.Equal("public-cache-token", request.Token));
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
    public async Task RepositoryScopeDoesNotProbeVisibilityBeforeTokenScopedLoad()
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

        Assert.Equal(0, anonymousProbeRequests);
        Assert.Equal("Token list token-a", Assert.Single(pullRequests).Title);
        Assert.Equal(["token-a"], listRequestTokens);
    }

    [Fact]
    public async Task TokenScopedPullListSkipsMissingCrossRepositoryLinkedIssues()
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
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "token-a" => Json(
                    PullRequestsJson([PullRequestJson(1, body: "Fixes private/repo#123")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token == "token-a" => Json("[]"),
                "repos/example/repo/pulls/1" when token == "token-a" => Json(PullRequestDetailsJson(1)),
                "repos/private/repo/issues/123" when token == "token-a" => Json("""{ "message": "Not Found" }""", HttpStatusCode.NotFound),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}")
            });
        }, cache, "token-a");

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        Assert.Empty(Assert.Single(pullRequests).LinkedIssues);
        Assert.DoesNotContain(requests, request => request.Path == "repos/private/repo");
        Assert.Contains(requests, request => request.Path == "repos/private/repo/issues/123");
        Assert.All(requests, request => Assert.Equal("token-a", request.Token));
    }

    [Fact]
    public async Task TokenScopedPullListIncludesCrossRepositoryLinkedIssuesWithToken()
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
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token == "token-a" => Json(
                    PullRequestsJson([PullRequestJson(1, body: "Fixes other/repo#123")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token == "token-a" => Json("[]"),
                "repos/example/repo/pulls/1" when token == "token-a" => Json(PullRequestDetailsJson(1)),
                "repos/other/repo/issues/123" when token == "token-a" => Json(
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
        Assert.DoesNotContain(requests, request => request.Path == "repos/other/repo");
        Assert.All(requests, request => Assert.Equal("token-a", request.Token));
    }

    [Fact]
    public async Task TokenScopedLinkedIssueNotFoundRefreshClearsOlderLastGoodIssue()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var repositoryName = new RepositoryName("example", "repo");
        var issueState = "exists";
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" when token is "token-a" => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Linked issue PR", body: "Fixes #404")])),
                "repos/example/repo/pulls/1/reviews?per_page=100" when token is "token-a" => Json("[]"),
                "repos/example/repo/pulls/1" when token is "token-a" => Json(PullRequestDetailsJson(1)),
                "repos/example/repo/issues/404" when token is "token-a" && issueState == "exists" => Json(
                    """
                    {
                      "number": 404,
                      "title": "Old linked issue",
                      "html_url": "https://github.com/example/repo/issues/404",
                      "repository_url": "https://api.github.com/repos/example/repo",
                      "labels": []
                    }
                    """),
                "repos/example/repo/issues/404" when token is "token-a" && issueState == "not-found" => Json(
                    """{ "message": "Not Found" }""",
                    HttpStatusCode.NotFound),
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
    public async Task PullListAttributesCopilotAuthorToHumanAssignee()
    {
        var client = CreateCopilotAttributionClient("Copilot", "JamesNK", "Copilot");

        Assert.Equal("JamesNK/copilot", await ResolveSingleAuthorAsync(client));
    }

    [Fact]
    public async Task PullListAttributesCopilotBotLoginToHumanAssignee()
    {
        var client = CreateCopilotAttributionClient(
            "copilot-swe-agent[bot]",
            "JamesNK",
            "copilot-swe-agent[bot]");

        Assert.Equal("JamesNK/copilot", await ResolveSingleAuthorAsync(client));
    }

    [Fact]
    public async Task PullListExcludesCopilotAssigneeCaseInsensitively()
    {
        var client = CreateCopilotAttributionClient("Copilot", "octocat", "copilot");

        Assert.Equal("octocat/copilot", await ResolveSingleAuthorAsync(client));
    }

    [Fact]
    public async Task PullListKeepsCopilotAuthorWhenNoHumanAssignee()
    {
        var client = CreateCopilotAttributionClient("Copilot", "Copilot");

        Assert.Equal("Copilot", await ResolveSingleAuthorAsync(client));
    }

    [Fact]
    public async Task PullListKeepsCopilotAuthorWhenMultipleHumanAssignees()
    {
        var client = CreateCopilotAttributionClient("Copilot", "JamesNK", "adamint", "Copilot");

        Assert.Equal("Copilot", await ResolveSingleAuthorAsync(client));
    }

    [Fact]
    public async Task PullListLeavesNonCopilotAuthorUnchanged()
    {
        var client = CreateCopilotAttributionClient("octocat", "octocat", "Copilot");

        Assert.Equal("octocat", await ResolveSingleAuthorAsync(client));
    }

    [Fact]
    public async Task PullListDoesNotTreatNonCopilotBotAsCopilot()
    {
        var client = CreateCopilotAttributionClient("mycopilot-helper[bot]", "JamesNK", "mycopilot-helper[bot]");

        Assert.Equal("mycopilot-helper[bot]", await ResolveSingleAuthorAsync(client));
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
    public async Task StreamPullRequestsYieldsBaselineBeforeEnrichedBatchAndNextPage()
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
        Assert.Empty(enumerator.Current.LinkedIssues);

        var pullRequests = new List<PullRequestSummary> { enumerator.Current };
        PullRequestSummary? enrichedFirstPullRequest = null;
        while (await enumerator.MoveNextAsync())
        {
            pullRequests.Add(enumerator.Current);
            if (enumerator.Current.Number == 1 && enumerator.Current.LinkedIssues.Count > 0)
            {
                enrichedFirstPullRequest = enumerator.Current;
                Assert.False(secondPageRequested);
            }
        }

        Assert.NotNull(enrichedFirstPullRequest);
        var linkedIssue = Assert.Single(enrichedFirstPullRequest.LinkedIssues);
        Assert.Equal(404, linkedIssue.Number);
        Assert.True(secondPageRequested);
        Assert.Equal(Enumerable.Range(1, 21), pullRequests.Select(pullRequest => pullRequest.Number).Distinct());
    }

    [Fact]
    public async Task PullListForcedRefreshAlwaysFetchesFreshData()
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
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                    PullRequestsJson([PullRequestJson(1, title: $"List {++listRequests}")])),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        var originalPullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);
        var firstRefreshPullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken);
        var secondRefreshPullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken);

        Assert.Equal("List 1", Assert.Single(originalPullRequests).Title);
        Assert.Equal("List 2", Assert.Single(firstRefreshPullRequests).Title);
        Assert.Equal("List 3", Assert.Single(secondRefreshPullRequests).Title);
        Assert.Equal(3, listRequests);
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
        Assert.Equal(originalPullRequests[^1].FetchedAt, pullRequest.FetchedAt);
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

        var refreshedPullRequests = await EnumerateAsync(client.StreamPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken));

        Assert.Equal(Enumerable.Range(1, 21), refreshedPullRequests.Take(21).Select(pullRequest => pullRequest.Number));
        Assert.Equal(Enumerable.Range(1, 20), refreshedPullRequests.Skip(21).Select(pullRequest => pullRequest.Number));
        Assert.All(refreshedPullRequests.Take(21), pullRequest => Assert.StartsWith("Good ", pullRequest.Title));
        Assert.All(refreshedPullRequests.Skip(21), pullRequest => Assert.StartsWith("Fresh ", pullRequest.Title));
    }

    [Fact]
    public async Task StreamPullRequestEntriesMarkStaleRowsAndCompletionForHardRefresh()
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
                    PullRequestsJson([PullRequestJson(1, title: "Cached")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                    PullRequestsJson([PullRequestJson(2, title: "Live")])),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        await EnumerateAsync(client.StreamPullRequestEntriesAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken));

        var entries = await EnumerateAsync(client.StreamPullRequestEntriesAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken));

        Assert.Equal("Cached", entries[0].PullRequest?.Title);
        Assert.True(entries[0].IsStale);
        Assert.Contains(entries, entry => entry.PullRequest?.Title == "Live" && !entry.IsStale);
        Assert.True(entries[^1].IsComplete);
    }

    [Fact]
    public async Task StreamPullRequestEntriesPreserveStaleEnrichmentUntilLiveRowCompletes()
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
                    PullRequestsJson([PullRequestJson(1, title: "Cached enriched", body: "Fixes #404")])),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                    PullRequestsJson([PullRequestJson(1, title: "Live baseline", body: "Fixes #405")])),
                "repos/example/repo/issues/404" => Json(
                    """
                    {
                      "number": 404,
                      "title": "Old cached issue",
                      "html_url": "https://github.com/example/repo/issues/404",
                      "repository_url": "https://api.github.com/repos/example/repo",
                      "labels": []
                    }
                    """),
                "repos/example/repo/issues/405" => Json(
                    """
                    {
                      "number": 405,
                      "title": "Fresh live issue",
                      "html_url": "https://github.com/example/repo/issues/405",
                      "repository_url": "https://api.github.com/repos/example/repo",
                      "labels": []
                    }
                    """),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        await EnumerateAsync(client.StreamPullRequestEntriesAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken));

        var entries = await EnumerateAsync(client.StreamPullRequestEntriesAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken));

        Assert.Equal("Cached enriched", entries[0].PullRequest?.Title);
        Assert.True(entries[0].IsStale);
        Assert.Equal(404, Assert.Single(entries[0].PullRequest!.LinkedIssues).Number);

        Assert.Equal("Live baseline", entries[1].PullRequest?.Title);
        Assert.True(entries[1].IsStale);
        Assert.True(entries[1].IsStaleRefreshOverlay);
        Assert.Equal(404, Assert.Single(entries[1].PullRequest!.LinkedIssues).Number);

        var completedLiveRow = entries.Single(entry =>
            entry.PullRequest?.Title == "Live baseline"
            && !entry.IsStale
            && !entry.IsComplete);
        Assert.Equal(405, Assert.Single(completedLiveRow.PullRequest!.LinkedIssues).Number);
        Assert.True(entries[^1].IsComplete);
    }

    [Fact]
    public async Task PullStreamEndpointSerializesStalePreservingLiveBaselineAsStale()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var gitHub = new FakeGitHubApi()
            .Respond("repos/example/repo", "public-cache-token", _ => Json("""{ "visibility": "public" }"""))
            .Respond(
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100",
                "token-a",
                requestCount => Json(requestCount == 1
                    ? PullRequestsJson([PullRequestJson(1, title: "Cached enriched", body: "Fixes #404")])
                    : PullRequestsJson([PullRequestJson(1, title: "Live baseline", body: "Fixes #405")])))
            .Respond("repos/example/repo/pulls/1/reviews?per_page=100", "token-a", _ => Json("[]"))
            .Respond("repos/example/repo/pulls/1", "token-a", _ => Json(PullRequestDetailsJson(1)))
            .Respond(
                "repos/example/repo/issues/404",
                "token-a",
                _ => Json(
                    """
                    {
                      "number": 404,
                      "title": "Old cached issue",
                      "html_url": "https://github.com/example/repo/issues/404",
                      "repository_url": "https://api.github.com/repos/example/repo",
                      "labels": []
                    }
                    """))
            .Respond(
                "repos/example/repo/issues/405",
                "token-a",
                _ => Json(
                    """
                    {
                      "number": 405,
                      "title": "Fresh live issue",
                      "html_url": "https://github.com/example/repo/issues/405",
                      "repository_url": "https://api.github.com/repos/example/repo",
                      "labels": []
                    }
                    """));
        await using var app = await CreatePullRequestTestAppAsync(gitHub, cache, "token-a");
        using var httpClient = CreateHttpClient(app);

        using var warmupResponse = await httpClient.GetAsync(
            "/api/github/pulls/stream?repo=example/repo&state=open",
            TestContext.Current.CancellationToken);
        await ReadJsonLineElementsAsync(warmupResponse);
        using var response = await httpClient.GetAsync(
            "/api/github/pulls/stream?repo=example/repo&state=open&refresh=true",
            TestContext.Current.CancellationToken);

        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        var items = await ReadJsonLineElementsAsync(response);

        Assert.Equal("Cached enriched", GetPullRequestTitle(items[0]));
        Assert.True(items[0].GetProperty("isStale").GetBoolean());
        Assert.Equal(404, GetFirstLinkedIssueNumber(items[0]));

        Assert.Equal("Live baseline", GetPullRequestTitle(items[1]));
        Assert.True(items[1].GetProperty("isStale").GetBoolean());
        Assert.Equal(404, GetFirstLinkedIssueNumber(items[1]));

        var completedLiveItem = items.Single(item =>
            GetPullRequestTitle(item) == "Live baseline"
            && !item.GetProperty("isStale").GetBoolean()
            && !item.GetProperty("isComplete").GetBoolean());
        Assert.Equal(405, GetFirstLinkedIssueNumber(completedLiveItem));
        Assert.True(items[^1].GetProperty("isComplete").GetBoolean());
    }

    [Fact]
    public async Task StreamPullRequestsDoesNotDowngradeStaleItemsWhenRefreshFailsBeforeBatchIsEnriched()
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
                    PullRequestsJson(Enumerable.Range(1, 5).Select(number => PullRequestJson(number, title: $"Good {number}")))),
                "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(
                    PullRequestsJson(Enumerable.Range(1, 5).Select(number => PullRequestJson(number, title: $"Fresh baseline {number}"))),
                    linkHeader: "<https://api.github.com/repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100&page=2>; rel=\"next\""),
                _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
            };
        });

        await EnumerateAsync(client.StreamPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken));

        var refreshedPullRequests = await EnumerateAsync(client.StreamPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            true,
            TestContext.Current.CancellationToken));

        Assert.Equal(Enumerable.Range(1, 5), refreshedPullRequests.Select(pullRequest => pullRequest.Number));
        Assert.All(refreshedPullRequests, pullRequest => Assert.StartsWith("Good ", pullRequest.Title));
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

        Assert.Equal([5, 8, 5, 8], pullRequests.Select(pullRequest => pullRequest.Number));
        Assert.Empty(pullRequests[0].LinkedIssues);
        Assert.Equal(10, Assert.Single(pullRequests[2].LinkedIssues).Number);
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
    public async Task PullRequestChecksUsesLatestRunWhenSameShaHasRerunChecks()
    {
        // A re-triggered workflow leaves an older failing run and a newer passing run with the same
        // name on the same head SHA. GitHub's check-runs API returns both; the rollup must count only
        // the latest run per name (newer started_at), matching GitHub's own PR check rollup.
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/commits/rerun/check-runs?filter=latest&per_page=100" => Json(
                """
                {
                  "total_count": 4,
                  "check_runs": [
                    { "id": 11, "name": "Tests / Final Test Results", "status": "completed", "conclusion": "success", "started_at": "2026-06-10T22:56:39Z", "completed_at": "2026-06-10T23:00:00Z" },
                    { "id": 12, "name": "Tests / Final Test Results", "status": "completed", "conclusion": "failure", "started_at": "2026-06-09T23:13:08Z", "completed_at": "2026-06-09T23:14:21Z", "html_url": "https://ci.example/stale" },
                    { "id": 13, "name": "Tests / Qdrant.Client", "status": "completed", "conclusion": "failure", "started_at": "2026-06-09T22:54:35Z", "completed_at": "2026-06-09T22:56:21Z", "html_url": "https://ci.example/stale-qdrant" },
                    { "id": 14, "name": "Tests / Qdrant.Client", "status": "completed", "conclusion": "success", "started_at": "2026-06-10T01:12:30Z", "completed_at": "2026-06-10T01:20:00Z" }
                  ]
                }
                """),
            "repos/example/repo/commits/rerun/status?per_page=100" => Json(
                """{ "state": "success", "total_count": 0, "statuses": [] }"""),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            [new PullRequestChecksRequestItem(1, "rerun")],
            false,
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal("success", pullRequest.Checks.State);
        Assert.Equal(2, pullRequest.Checks.TotalCount);
        Assert.Equal(2, pullRequest.Checks.SuccessCount);
        Assert.Equal(0, pullRequest.Checks.FailureCount);
        Assert.Empty(pullRequest.Checks.FailingChecks);
    }

    [Fact]
    public async Task PullRequestChecksReportsFailureWhenLatestRerunFails()
    {
        // Inverse of the re-run case: the newer run is the failing one. Dedup must keep the latest by
        // started_at, so the rollup is failure even though an older run for the same name passed.
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/commits/regress/check-runs?filter=latest&per_page=100" => Json(
                """
                {
                  "total_count": 2,
                  "check_runs": [
                    { "id": 21, "name": "build", "status": "completed", "conclusion": "success", "started_at": "2026-06-09T10:00:00Z", "completed_at": "2026-06-09T10:05:00Z" },
                    { "id": 22, "name": "build", "status": "completed", "conclusion": "failure", "started_at": "2026-06-10T10:00:00Z", "completed_at": "2026-06-10T10:05:00Z", "html_url": "https://ci.example/build-rerun" }
                  ]
                }
                """),
            "repos/example/repo/commits/regress/status?per_page=100" => Json(
                """{ "state": "success", "total_count": 0, "statuses": [] }"""),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            [new PullRequestChecksRequestItem(1, "regress")],
            false,
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal("failure", pullRequest.Checks.State);
        Assert.Equal(1, pullRequest.Checks.TotalCount);
        Assert.Equal(1, pullRequest.Checks.FailureCount);
        var failing = Assert.Single(pullRequest.Checks.FailingChecks);
        Assert.Equal("build", failing.Name);
        Assert.Equal("https://ci.example/build-rerun", failing.HtmlUrl);
    }

    [Fact]
    public async Task PullRequestChecksDoesNotCollapseRunsWithEmptyNames()
    {
        // Dedup groups only by name. If the API returns blank names, those runs must be treated as
        // distinct (like null names) rather than collapsed under a shared empty-string key — otherwise
        // a passing blank-named run could hide a failing one.
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/commits/blank/check-runs?filter=latest&per_page=100" => Json(
                """
                {
                  "total_count": 2,
                  "check_runs": [
                    { "id": 31, "name": "", "status": "completed", "conclusion": "success", "started_at": "2026-06-10T10:00:00Z", "completed_at": "2026-06-10T10:05:00Z" },
                    { "id": 32, "name": "", "status": "completed", "conclusion": "failure", "started_at": "2026-06-09T10:00:00Z", "completed_at": "2026-06-09T10:05:00Z", "html_url": "https://ci.example/blank-fail" }
                  ]
                }
                """),
            "repos/example/repo/commits/blank/status?per_page=100" => Json(
                """{ "state": "success", "total_count": 0, "statuses": [] }"""),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestChecksAsync(
            new RepositoryName("example", "repo"),
            [new PullRequestChecksRequestItem(1, "blank")],
            false,
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Equal("failure", pullRequest.Checks.State);
        Assert.Equal(2, pullRequest.Checks.TotalCount);
        Assert.Equal(1, pullRequest.Checks.SuccessCount);
        Assert.Equal(1, pullRequest.Checks.FailureCount);
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
            CreateTokenScopeForTestToken("token-a"),
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
                "repos/example/repo/pulls/1" when token is "token-a" && !failRefresh => Json(PullRequestDetailsJson(1)),
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
    public async Task PullRequestChecksForceRefreshAlwaysBypassesCachedStatus()
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

        Assert.Equal(6, requestCount);
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
    public async Task PullRequestChecksColdTransientFailureDoesNotPinCachedStatus()
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
                "repos/example/repo/commits/cache123/check-runs?filter=latest&per_page=100" when token is "token-a" && failCheckRuns => Json(
                    """{ "message": "GitHub unavailable" }""",
                    HttpStatusCode.ServiceUnavailable),
                "repos/example/repo/commits/cache123/check-runs?filter=latest&per_page=100" when token is "token-a" => Json(
                    $$"""
                    {
                      "total_count": 1,
                      "check_runs": [
                        { "id": {{++checkRunRequests}}, "name": "tests", "status": "completed", "conclusion": "failure", "completed_at": "2026-01-02T00:45:00Z" }
                      ]
                    }
                    """),
                "repos/example/repo/commits/cache123/status?per_page=100" when token is "token-a" => Json(
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
            CreateTokenScopeForTestToken("token-a"),
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
                "repos/example/repo/commits/cache123/check-runs?filter=latest&per_page=100" when token is "token-a" && !failRefresh => Json(
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
        var branchExists = true;
        var failMilestones = false;
        var route = (HttpRequestMessage request, CancellationToken _) =>
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;

            return Task.FromResult(path switch
            {
                "repos/example/repo/milestones?state=all&per_page=100" when token is "token-a" && !failMilestones => Json(
                    """[{ "number": 7, "title": "13.4" }]"""),
                "repos/example/repo/milestones?state=all&per_page=100" when token is "token-a" => Json(
                    """{ "message": "GitHub unavailable" }""",
                    HttpStatusCode.ServiceUnavailable),
                "repos/example/repo/branches/release%2F13.4" when token is "token-a" && branchExists => Json(
                    """{ "name": "release/13.4" }"""),
                "repos/example/repo/branches/release%2F13.4" when token is "token-a" => Json(
                    """{ "message": "Not Found" }""",
                    HttpStatusCode.NotFound),
                "repos/example/repo/issues?state=open&milestone=7&per_page=100" when token is "token-a" => Json("[]"),
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
        var options = CreateWarmupOptions();
        var publicCacheIdentity = new GitHubPublicCacheIdentity(options);
        var publicCacheStore = new GitHubPublicCacheStore(cache);
        var responseCache = new GitHubResponseCache(cache, publicCacheStore);
        var cacheScopeResolver = new GitHubCacheScopeResolver(httpClient, tokenProvider, publicCacheIdentity, publicCacheStore, options, cache);

        return new GitHubClient(httpClient, tokenProvider, publicCacheIdentity, publicCacheStore, cacheScopeResolver, responseCache, new TestHostEnvironment());
    }

    private static async Task<WebApplication> CreatePullRequestTestAppAsync(
        FakeGitHubApi gitHub,
        IMemoryCache cache,
        string token)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLogging();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(new TestGitHubToken(token));
        builder.Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("test", _ => { });

        var options = CreateWarmupOptions();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(cache);
        builder.Services.AddSingleton(_ => new HttpClient(new RequestStubGitHubHandler(gitHub.SendAsync))
        {
            BaseAddress = new Uri("https://api.github.com/")
        });
        builder.Services.AddSingleton<GitHubTokenProvider>();
        builder.Services.AddSingleton<GitHubPublicCacheIdentity>();
        builder.Services.AddSingleton<GitHubPublicCacheStore>();
        builder.Services.AddSingleton<GitHubResponseCache>();
        builder.Services.AddSingleton(sp => new GitHubCacheScopeResolver(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<GitHubTokenProvider>(),
            sp.GetRequiredService<GitHubPublicCacheIdentity>(),
            sp.GetRequiredService<GitHubPublicCacheStore>(),
            sp.GetRequiredService<IOptions<GitHubCacheWarmupOptions>>(),
            sp.GetRequiredService<IMemoryCache>()));
        builder.Services.AddSingleton(sp => new GitHubClient(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<GitHubTokenProvider>(),
            sp.GetRequiredService<GitHubPublicCacheIdentity>(),
            sp.GetRequiredService<GitHubPublicCacheStore>(),
            sp.GetRequiredService<GitHubCacheScopeResolver>(),
            sp.GetRequiredService<GitHubResponseCache>(),
            sp.GetRequiredService<IHostEnvironment>()));
        builder.Services.AddSingleton<GitHubPullRequestService>();

        var app = builder.Build();
        app.UseAuthentication();
        app.MapGitHubPullRequestRoutes();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static HttpClient CreateHttpClient(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        var address = Assert.Single(addresses ?? []);
        return new HttpClient
        {
            BaseAddress = new Uri(address)
        };
    }

    private static async Task<IReadOnlyList<JsonElement>> ReadJsonLineElementsAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToArray();
    }

    private static string? GetPullRequestTitle(JsonElement streamItem) =>
        streamItem.GetProperty("pullRequest") is { ValueKind: JsonValueKind.Object } pullRequest
            ? pullRequest.GetProperty("title").GetString()
            : null;

    private static int GetFirstLinkedIssueNumber(JsonElement streamItem) =>
        streamItem.GetProperty("pullRequest").GetProperty("linkedIssues")[0].GetProperty("number").GetInt32();

    private static GitHubClient CreateClientFromRequests(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route,
        IMemoryCache cache,
        string token,
        IOptions<GitHubCacheWarmupOptions>? options = null,
        GitHubPublicCacheStore? publicCacheStore = null)
    {
        var httpClient = new HttpClient(new RequestStubGitHubHandler(route))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var tokenProvider = new GitHubTokenProvider(
            new HttpContextAccessor { HttpContext = CreateHttpContextWithGitHubToken(token) },
            new TestHostEnvironment());
        options ??= CreateWarmupOptions();
        var publicCacheIdentity = new GitHubPublicCacheIdentity(options);
        publicCacheStore ??= new GitHubPublicCacheStore(cache);
        var responseCache = new GitHubResponseCache(cache, publicCacheStore);
        var cacheScopeResolver = new GitHubCacheScopeResolver(httpClient, tokenProvider, publicCacheIdentity, publicCacheStore, options, cache);

        return new GitHubClient(httpClient, tokenProvider, publicCacheIdentity, publicCacheStore, cacheScopeResolver, responseCache, new TestHostEnvironment());
    }

    private static GitHubClient CreateAnonymousClientFromRequests(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route,
        IMemoryCache cache,
        IOptions<GitHubCacheWarmupOptions>? options = null,
        GitHubPublicCacheStore? publicCacheStore = null)
    {
        var httpClient = new HttpClient(new RequestStubGitHubHandler(route))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var tokenProvider = new GitHubTokenProvider(
            new HttpContextAccessor { HttpContext = CreateAnonymousHttpContext() },
            new TestHostEnvironment());
        options ??= CreateWarmupOptions();
        var publicCacheIdentity = new GitHubPublicCacheIdentity(options);
        publicCacheStore ??= new GitHubPublicCacheStore(cache);
        var responseCache = new GitHubResponseCache(cache, publicCacheStore);
        var cacheScopeResolver = new GitHubCacheScopeResolver(httpClient, tokenProvider, publicCacheIdentity, publicCacheStore, options, cache);

        return new GitHubClient(httpClient, tokenProvider, publicCacheIdentity, publicCacheStore, cacheScopeResolver, responseCache, new TestHostEnvironment());
    }

    private static IOptions<GitHubCacheWarmupOptions> CreateWarmupOptions(
        string? publicCacheToken = "public-cache-token",
        params string[] repositories) =>
        Options.Create(new GitHubCacheWarmupOptions
        {
            PublicCacheToken = publicCacheToken,
            Repositories = repositories.Length == 0
                ? ["example/repo", "old/repo", "example/rate-limited"]
                : repositories
        });

    private static GitHubCacheScope CreateTokenScopeForTestToken(string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return GitHubCachePolicy.CreateTokenScope($"oauth:{Convert.ToHexString(hash)[..16]}:0");
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

    private static GitHubClient CreateCopilotAttributionClient(string authorLogin, params string[] assigneeLogins)
    {
        var assigneesJson = string.Join(
            ",\n",
            assigneeLogins.Select(login => $$"""{ "login": {{JsonSerializer.Serialize(login)}} }"""));

        var listJson =
            $$"""
            [
              {
                "number": 1,
                "title": "Copilot change",
                "state": "open",
                "body": null,
                "created_at": "2026-01-01T00:00:00Z",
                "updated_at": "2026-01-02T00:00:00Z",
                "draft": false,
                "user": { "login": {{JsonSerializer.Serialize(authorLogin)}} },
                "html_url": "https://github.com/example/repo/pull/1",
                "labels": [],
                "assignees": [ {{assigneesJson}} ],
                "requested_reviewers": [],
                "requested_teams": []
              }
            ]
            """;

        return CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=open&sort=created&direction=asc&per_page=100" => Json(listJson),
            "repos/example/repo/pulls/1/reviews?per_page=100" => Json("[]"),
            "repos/example/repo/pulls/1" => Json(PullRequestDetailsJson(1)),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });
    }

    private static async Task<string> ResolveSingleAuthorAsync(GitHubClient client)
    {
        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            false,
            TestContext.Current.CancellationToken);

        return Assert.Single(pullRequests).Author;
    }

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

    private sealed class FakeGitHubApi
    {
        private readonly List<FakeGitHubRoute> routes = [];

        public FakeGitHubApi Respond(
            string path,
            string? token,
            Func<int, HttpResponseMessage> responseFactory)
        {
            routes.Add(new FakeGitHubRoute(path, token, responseFactory));
            return this;
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken _)
        {
            var path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? "";
            var token = request.Headers.Authorization?.Parameter;
            var route = routes.FirstOrDefault(candidate =>
                candidate.Path.Equals(path, StringComparison.Ordinal)
                && string.Equals(candidate.Token, token, StringComparison.Ordinal));
            if (route is null)
            {
                throw new InvalidOperationException($"Unexpected GitHub request: {path} auth={token ?? "anonymous"}");
            }

            route.RequestCount++;
            return Task.FromResult(route.ResponseFactory(route.RequestCount));
        }

        private sealed class FakeGitHubRoute(
            string path,
            string? token,
            Func<int, HttpResponseMessage> responseFactory)
        {
            public string Path { get; } = path;

            public string? Token { get; } = token;

            public Func<int, HttpResponseMessage> ResponseFactory { get; } = responseFactory;

            public int RequestCount { get; set; }
        }
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
            if (request.Headers.Authorization?.Parameter == "public-cache-token"
                && IsRepositoryVisibilityProbe(path))
            {
                return Json("""{ "visibility": "public" }""");
            }

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
