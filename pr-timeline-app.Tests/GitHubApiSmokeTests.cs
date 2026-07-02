using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

[assembly: CaptureConsole]

namespace pr_timeline_app.Tests;

public sealed class GitHubApiSmokeTests(ServerSmokeFixture fixture) : IClassFixture<ServerSmokeFixture>
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
        var authStatus = await response.Content.ReadFromJsonAsync<AuthStatusSmokeResponse>(cancellationToken);

        Assert.NotNull(authStatus);
        Assert.False(authStatus.Authenticated);
        Assert.Equal(IsGitHubOAuthConfigured(), authStatus.Configured);
        Assert.Equal(IsGitHubOAuthConfigured(), authStatus.CanLogin);
        Assert.Null(authStatus.Source);
        Assert.Null(authStatus.Login);
        Assert.NotEmpty(authStatus.Message);
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
        var appInfo = await response.Content.ReadFromJsonAsync<AppInfoSmokeResponse>(cancellationToken);

        Assert.NotNull(appInfo);
        Assert.NotEmpty(appInfo.CommitSha);
        Assert.NotEmpty(appInfo.ShortCommitSha);
        Assert.Equal(appInfo.CommitSha[..Math.Min(7, appInfo.CommitSha.Length)], appInfo.ShortCommitSha);
        if (appInfo.CommitSha == "local")
        {
            Assert.Null(appInfo.CommitUrl);
        }
        else
        {
            Assert.Equal($"https://github.com/davidfowl/pr-dashboard/commit/{appInfo.CommitSha}", appInfo.CommitUrl);
        }
    }

    [Fact]
    public async Task AgentSchemaReportsModeUseCases()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = await GetClientAsync(cancellationToken);
        using var response = await client.GetAsync("/api/agents/schema", cancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/pr-dashboard-agent-schema+json", response.Content.Headers.ContentType?.MediaType);
        var schema = await response.Content.ReadFromJsonAsync<AgentSchemaSmokeResponse>(cancellationToken);

        Assert.NotNull(schema);
        Assert.Equal(1, schema.SchemaVersion);
        Assert.Contains(schema.Modes, mode =>
            mode.Id == "review"
            && mode.UseCases.Any(useCase => useCase.Contains("pull requests", StringComparison.OrdinalIgnoreCase))
            && mode.ApiEndpoints.Any(endpoint =>
                endpoint.Path.Contains("/api/github/pulls/stream", StringComparison.Ordinal)
                && endpoint.Description.Contains("isStale", StringComparison.Ordinal)
                && endpoint.Description.Contains("isComplete", StringComparison.Ordinal)
                && endpoint.Description.Contains("refresh=true", StringComparison.Ordinal)));
        Assert.Contains(schema.Modes, mode => mode.Id == "issues");
        Assert.Contains(schema.Modes, mode =>
            mode.Id == "ship"
            && !mode.DashboardUrl.Contains("repos=", StringComparison.Ordinal)
            && mode.DashboardUrl.Contains("milestone={milestone}", StringComparison.Ordinal)
            && mode.ApiEndpoints.Any(endpoint =>
                endpoint.Path.Contains("repo={owner}/{repo}", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AgentSchemaIsMachineDiscoverable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = await GetClientAsync(cancellationToken);
        using var response = await client.GetAsync("/.well-known/pr-dashboard-agent-schema", cancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/pr-dashboard-agent-schema+json", response.Content.Headers.ContentType?.MediaType);
        var schema = await response.Content.ReadFromJsonAsync<AgentSchemaSmokeResponse>(cancellationToken);

        Assert.NotNull(schema);
        Assert.Equal("pr-dashboard", schema.Name);
        Assert.Contains(schema.Discovery.SchemaUrls, url => url == "/api/agents/schema.json");
    }

    [Fact]
    public async Task PullListRejectsInvalidRepositoryWithoutCallingGitHub()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = await GetClientAsync(cancellationToken);
        using var response = await client.GetAsync("/api/github/pulls?repo=not-a-repo&state=open", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var validationProblem = await response.Content.ReadFromJsonAsync<ValidationProblemSmokeResponse>(cancellationToken);
        Assert.NotNull(validationProblem);
        Assert.True(validationProblem.Errors.TryGetValue("repo", out var repoErrors));
        Assert.Contains("owner/repo", repoErrors[0]);
    }

    [Fact]
    public async Task PullListRejectsInvalidStateWithoutCallingGitHub()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = await GetClientAsync(cancellationToken);
        using var response = await client.GetAsync("/api/github/pulls?repo=microsoft/aspire&state=merged", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var validationProblem = await response.Content.ReadFromJsonAsync<ValidationProblemSmokeResponse>(cancellationToken);
        Assert.NotNull(validationProblem);
        Assert.True(validationProblem.Errors.TryGetValue("state", out var stateErrors));
        Assert.Contains("open, closed, or all", stateErrors[0]);
    }

    [Fact]
    public async Task TimelineRejectsInvalidPullRequestNumberWithoutCallingGitHub()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var client = await GetClientAsync(cancellationToken);
        using var response = await client.GetAsync("/api/github/pulls/0/timeline?repo=microsoft/aspire", cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var validationProblem = await response.Content.ReadFromJsonAsync<ValidationProblemSmokeResponse>(cancellationToken);
        Assert.NotNull(validationProblem);
        Assert.True(validationProblem.Errors.TryGetValue("number", out var numberErrors));
        Assert.Contains("greater than zero", numberErrors[0]);
    }

    private async Task<HttpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        await fixture.EnsureStartedAsync(cancellationToken);
        return fixture.Client;
    }

    private static bool IsGitHubOAuthConfigured() =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_CLIENT_ID"))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GITHUB_CLIENT_SECRET"));

    private sealed record AuthStatusSmokeResponse(
        bool Authenticated,
        bool Configured,
        bool CanLogin,
        string? Source,
        string? Login,
        string Message);

    private sealed record AppInfoSmokeResponse(
        string CommitSha,
        string ShortCommitSha,
        string? CommitUrl);

    private sealed record AgentSchemaSmokeResponse(
        int SchemaVersion,
        string Name,
        AgentDiscoverySchemaSmokeResponse Discovery,
        IReadOnlyList<AgentModeSchemaSmokeResponse> Modes);

    private sealed record AgentModeSchemaSmokeResponse(
        string Id,
        string DashboardUrl,
        IReadOnlyList<string> UseCases,
        IReadOnlyList<AgentApiEndpointSchemaSmokeResponse> ApiEndpoints);

    private sealed record AgentApiEndpointSchemaSmokeResponse(string Path, string Description);

    private sealed record AgentDiscoverySchemaSmokeResponse(IReadOnlyList<string> SchemaUrls);

    private sealed record ValidationProblemSmokeResponse(Dictionary<string, string[]> Errors);
}

public sealed class ServerSmokeFixture : IAsyncLifetime
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim startLock = new(1, 1);
    private readonly CancellationTokenSource startupCts = new();
    private Task? startupTask;
    private WebApplication? app;
    private HttpClient? client;

    public HttpClient Client => client ?? throw new InvalidOperationException("Test app was not initialized.");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (client is not null)
        {
            return;
        }

        Task startup;
        await startLock.WaitAsync(cancellationToken);
        try
        {
            if (client is not null)
            {
                return;
            }

            startup = startupTask ??= StartServerAsync(startupCts.Token);
        }
        finally
        {
            startLock.Release();
        }

        await startup.WaitAsync(cancellationToken);
    }

    private async Task StartServerAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddProblemDetails();
            builder.Services.Configure<GitHubCacheWarmupOptions>(
                builder.Configuration.GetSection(GitHubCacheWarmupOptions.SectionName));
            builder.Services.Configure<WebPushOptions>(
                builder.Configuration.GetSection(WebPushOptions.SectionName));
            builder.Services.Configure<GitHubReviewPolicyOptions>(
                builder.Configuration.GetSection(GitHubReviewPolicyOptions.SectionName));
            builder.Services.AddGitHubApiServices(builder.Environment);
            builder.Services.RemoveAll<IHostedService>();

            app = builder.Build();

            app.UseGitHubApiExceptionHandler();
            app.UseAuthentication();
            app.MapGitHubAuthRoutes();
            app.MapGitHubPullRequestRoutes();
            app.MapAgentSchemaRoutes();
            app.MapGet("/api/app-info", (IConfiguration configuration) =>
            {
                var commitSha = configuration["GIT_COMMIT_SHA"]?.Trim() is { Length: > 0 } configuredCommitSha
                    ? configuredCommitSha
                    : "local";
                var shortCommitSha = commitSha[..Math.Min(7, commitSha.Length)];
                var commitUrl = commitSha == "local"
                    ? null
                    : $"https://github.com/davidfowl/pr-dashboard/commit/{commitSha}";

                return new AppInfoResponse(commitSha, shortCommitSha, commitUrl);
            });

            await app.StartAsync(timeout.Token);
            client = CreateHttpClient(app);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            await DisposeAppAsync();
            throw new TimeoutException($"Server smoke fixture did not start within {DefaultTimeout}.", ex);
        }
        catch
        {
            await DisposeAppAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await startupCts.CancelAsync();
        if (startupTask is not null)
        {
            try
            {
                await startupTask;
            }
            catch (OperationCanceledException) when (startupCts.IsCancellationRequested)
            {
                // Dispose requested cancellation while startup was still in flight.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"Ignoring server smoke fixture startup failure during fixture disposal: {ex}");
            }
        }

        client?.Dispose();
        startLock.Dispose();
        await DisposeAppAsync();
        startupCts.Dispose();
    }

    private async Task DisposeAppAsync()
    {
        if (app is not null)
        {
            await app.DisposeAsync().AsTask();
            app = null;
        }
    }

    private static HttpClient CreateHttpClient(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("The test server did not publish any addresses.");

        return new HttpClient
        {
            BaseAddress = new Uri(addresses.Addresses.Single())
        };
    }
}
