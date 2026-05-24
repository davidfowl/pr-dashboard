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
            "repos/example/repo/pulls?state=open&sort=updated&direction=desc&per_page=30" => Json(
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
            "repos/example/repo/issues/404" => Json("""{ "message": "Not Found" }""", HttpStatusCode.NotFound),
            _ => throw new InvalidOperationException($"Unexpected GitHub request: {path}")
        });

        var pullRequests = await client.GetPullRequestsAsync(
            new RepositoryName("example", "repo"),
            "open",
            TestContext.Current.CancellationToken);

        var pullRequest = Assert.Single(pullRequests);
        Assert.Empty(pullRequest.LinkedIssues);
    }

    [Fact]
    public async Task PullListTreatsMissingReviewsAsWaiting()
    {
        var client = CreateClient(path => path switch
        {
            "repos/example/repo/pulls?state=open&sort=updated&direction=desc&per_page=30" => Json(
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
            "repos/example/repo/pulls?state=open&sort=updated&direction=desc&per_page=30" => Json(
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

    private static HttpResponseMessage Json(string content, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

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
