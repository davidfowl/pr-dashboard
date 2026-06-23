using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

[assembly: CaptureConsole]

namespace pr_timeline_app.Tests;

public sealed class GitHubApiSmokeTests(ApiSmokeTestFixture fixture) : IClassFixture<ApiSmokeTestFixture>
{
    [Fact]
    public async Task AuthStatusReportsLoggedOutAfterLocalLogout()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = await GetClientAsync(cancellationToken);
        var logout = await client.PostAsJsonAsync("/api/github/logout", new { }, cancellationToken);
        logout.EnsureSuccessStatusCode();

        using var response = await client.GetAsync("/api/github/auth-status", cancellationToken);

        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        var root = document.RootElement;

        Assert.False(root.GetProperty("authenticated").GetBoolean());
        Assert.True(root.TryGetProperty("configured", out _));
        Assert.True(root.TryGetProperty("canLogin", out _));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("login").ValueKind);
        Assert.NotEmpty(root.GetProperty("message").GetString() ?? "");
    }

    [Fact]
    public async Task LoginRejectsExternalReturnUrl()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = await GetClientAsync(cancellationToken);
        using var response = await client.GetAsync("/api/github/login?returnUrl=https%3A%2F%2Fevil.example", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AppInfoReportsCommitSha()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = await GetClientAsync(cancellationToken);
        using var response = await client.GetAsync("/api/app-info", cancellationToken);

        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.NotEmpty(document.RootElement.GetProperty("commitSha").GetString() ?? "");
        Assert.NotEmpty(document.RootElement.GetProperty("shortCommitSha").GetString() ?? "");
        Assert.True(document.RootElement.TryGetProperty("commitUrl", out _));
    }

    [Fact]
    public async Task PullListRejectsInvalidRepositoryWithoutCallingGitHub()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = await GetClientAsync(cancellationToken);
        using var response = await client.GetAsync("/api/github/pulls?repo=not-a-repo&state=open", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("repo", out var repoErrors));
        Assert.Contains("owner/repo", repoErrors[0].GetString());
    }

    [Fact]
    public async Task PullListRejectsInvalidStateWithoutCallingGitHub()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = await GetClientAsync(cancellationToken);
        using var response = await client.GetAsync("/api/github/pulls?repo=microsoft/aspire&state=merged", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("state", out var stateErrors));
        Assert.Contains("open, closed, or all", stateErrors[0].GetString());
    }

    [Fact]
    public async Task TimelineRejectsInvalidPullRequestNumberWithoutCallingGitHub()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = await GetClientAsync(cancellationToken);
        using var response = await client.GetAsync("/api/github/pulls/0/timeline?repo=microsoft/aspire", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("number", out var numberErrors));
        Assert.Contains("greater than zero", numberErrors[0].GetString());
    }

    private async Task<HttpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        await fixture.EnsureStartedAsync(cancellationToken);
        return fixture.Client;
    }
}

public sealed class ApiSmokeTestFixture : IAsyncLifetime
{
    private WebApplication? app;
    private HttpClient? client;

    public HttpClient Client => client ?? throw new InvalidOperationException("Test app was not initialized.");

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddProblemDetails();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMemoryCache();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie();

        builder.Services.AddSingleton(Options.Create(new GitHubCacheWarmupOptions()));
        builder.Services.AddSingleton(_ => new HttpClient(new ThrowingGitHubHandler())
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
        builder.Services.AddScoped<GitHubAuthService>();
        builder.Services.AddScoped<GitHubPullRequestService>();

        app = builder.Build();
        app.UseAuthentication();
        app.MapGitHubAuthRoutes();
        app.MapGitHubPullRequestRoutes();
        app.MapGet("/api/app-info", (IConfiguration configuration) =>
        {
            var commitSha = configuration["GIT_COMMIT_SHA"]?.Trim() is { Length: > 0 } configuredCommitSha
                ? configuredCommitSha
                : "local";
            var shortCommitSha = commitSha[..Math.Min(7, commitSha.Length)];
            var commitUrl = commitSha == "local"
                ? null
                : $"https://github.com/davidfowl/pr-dashboard/commit/{commitSha}";

            return new { commitSha, shortCommitSha, commitUrl };
        });

        await app.StartAsync(TestContext.Current.CancellationToken);

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()?
            .Addresses;
        var address = Assert.Single(addresses ?? []);
        client = new HttpClient
        {
            BaseAddress = new Uri(address)
        };
    }

    public Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (app is not null)
        {
            await app.DisposeAsync().AsTask();
        }
    }

    private sealed class ThrowingGitHubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"Unexpected GitHub request: {request.RequestUri?.PathAndQuery}");
    }
}
