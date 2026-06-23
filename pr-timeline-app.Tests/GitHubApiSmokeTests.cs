using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
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

public sealed class AppHostFixture : IAsyncLifetime
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim startLock = new(1, 1);
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

            startup = startupTask ??= StartAppHostAsync(cancellationToken);
        }
        finally
        {
            startLock.Release();
        }

        await startup;
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
        client?.Dispose();
        startLock.Dispose();
        await DisposeAppAsync();
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
