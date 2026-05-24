using System.Net.Http.Json;
using System.Text.Json;

namespace pr_timeline_app.Tests;

public sealed class GitHubApiSmokeTests(AppHostFixture fixture) : IClassFixture<AppHostFixture>
{
    [Fact]
    public async Task AuthStatusReportsLoggedOutAfterLocalLogout()
    {
        var logout = await Client.PostAsJsonAsync("/api/github/logout", new { });
        logout.EnsureSuccessStatusCode();

        using var response = await Client.GetAsync("/api/github/auth-status");

        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
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
        using var response = await Client.GetAsync("/api/github/login?returnUrl=https%3A%2F%2Fevil.example");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PullListRejectsInvalidRepositoryWithoutCallingGitHub()
    {
        using var response = await Client.GetAsync("/api/github/pulls?repo=not-a-repo&state=open");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("repo", out var repoErrors));
        Assert.Contains("owner/repo", repoErrors[0].GetString());
    }

    [Fact]
    public async Task PullListRejectsInvalidStateWithoutCallingGitHub()
    {
        using var response = await Client.GetAsync("/api/github/pulls?repo=microsoft/aspire&state=merged");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("state", out var stateErrors));
        Assert.Contains("open, closed, or all", stateErrors[0].GetString());
    }

    [Fact]
    public async Task TimelineRejectsInvalidPullRequestNumberWithoutCallingGitHub()
    {
        using var response = await Client.GetAsync("/api/github/pulls/0/timeline?repo=microsoft/aspire");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("number", out var numberErrors));
        Assert.Contains("greater than zero", numberErrors[0].GetString());
    }

    private HttpClient Client => fixture.Client;
}

public sealed class AppHostFixture : IAsyncLifetime
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    private DistributedApplication? app;
    private HttpClient? client;

    public HttpClient Client => client ?? throw new InvalidOperationException("Test app was not initialized.");

    public async Task InitializeAsync()
    {
        var cancellationToken = CancellationToken.None;
        var appHost = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.pr_timeline_app_AppHost>(["IncludeFrontend=false"], cancellationToken);

        app = await appHost.BuildAsync(cancellationToken)
            .WaitAsync(DefaultTimeout, cancellationToken);

        // GitHub-hosted runners can be much slower to initialize the Aspire test host than a
        // developer machine. Start the AppHost once per test class so startup cost and DCP
        // initialization happen once, then give health checks a CI-sized timeout.
        await app.StartAsync(cancellationToken)
            .WaitAsync(DefaultTimeout, cancellationToken);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("server", cancellationToken)
            .WaitAsync(DefaultTimeout, cancellationToken);

        client = app.CreateHttpClient("server");
    }

    public async Task DisposeAsync()
    {
        client?.Dispose();

        if (app is not null)
        {
            await app.DisposeAsync().AsTask();
        }
    }
}
