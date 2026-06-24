using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

[assembly: CaptureConsole]

namespace pr_timeline_app.Tests;

public sealed class GitHubApiSmokeTests(AppHostFixture fixture) : IClassFixture<AppHostFixture>
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

    private sealed record ValidationProblemSmokeResponse(Dictionary<string, string[]> Errors);
}

public sealed class AppHostFixture : IAsyncLifetime
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim startLock = new(1, 1);
    private readonly CancellationTokenSource startupCts = new();
    private Task? startupTask;
    private DistributedApplication? app;
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

            startup = startupTask ??= StartAppHostAsync(startupCts.Token);
        }
        finally
        {
            startLock.Release();
        }

        await startup.WaitAsync(cancellationToken);
    }

    private async Task StartAppHostAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DefaultTimeout);

        try
        {
            var appHost = await DistributedApplicationTestingBuilder
                .CreateAsync<Projects.pr_timeline_app_AppHost>(["IncludeFrontend=false"], timeout.Token);

            appHost.Services.AddLogging(logging => logging.AddSimpleConsole());

            app = await appHost.BuildAsync(timeout.Token);

            await app.StartAsync(timeout.Token);
            await app.ResourceNotifications.WaitForResourceHealthyAsync("server", timeout.Token);

            client = app.CreateHttpClient("server");
            Console.WriteLine($"Aspire AppHost smoke fixture started in {stopwatch.Elapsed}.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            await DisposeAppAsync();
            throw new TimeoutException($"Aspire AppHost smoke fixture did not start within {DefaultTimeout}.", ex);
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
                Console.WriteLine($"Ignoring Aspire AppHost startup failure during fixture disposal: {ex}");
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
}
