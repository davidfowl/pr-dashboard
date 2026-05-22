using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace pr_timeline_app.Tests;

public sealed class GitHubApiSmokeTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task AuthStatusReportsLoggedOutAfterLocalLogout()
    {
        using var client = factory.CreateClient();

        var logout = await client.PostAsJsonAsync("/api/github/logout", new { });
        logout.EnsureSuccessStatusCode();

        using var response = await client.GetAsync("/api/github/auth-status");

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
    public async Task LoginStartRejectsNonBrowserMutationRequest()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/api/github/login/start", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PullListRejectsInvalidRepositoryWithoutCallingGitHub()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/github/pulls?repo=not-a-repo&state=open");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("repo", out var repoErrors));
        Assert.Contains("owner/repo", repoErrors[0].GetString());
    }

    [Fact]
    public async Task PullListRejectsInvalidStateWithoutCallingGitHub()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/github/pulls?repo=microsoft/aspire&state=merged");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("state", out var stateErrors));
        Assert.Contains("open, closed, or all", stateErrors[0].GetString());
    }

    [Fact]
    public async Task TimelineRejectsInvalidPullRequestNumberWithoutCallingGitHub()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/github/pulls/0/timeline?repo=microsoft/aspire");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.True(document.RootElement.GetProperty("errors").TryGetProperty("number", out var numberErrors));
        Assert.Contains("greater than zero", numberErrors[0].GetString());
    }
}
