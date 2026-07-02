using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace pr_timeline_app.Tests;

public sealed class GitHubAuthRoutesTests
{
    [Theory]
    [InlineData("/api/github/dev/accounts")]
    [InlineData("/api/github/dev/account")]
    public async Task DevelopmentGitHubAccountRoutesAreNotAvailableInProduction(string path)
    {
        await using var app = await CreateAuthRoutesAppAsync(Environments.Production);
        using var client = CreateHttpClient(app);
        using var response = path.EndsWith("/account", StringComparison.Ordinal)
            ? await client.PostAsJsonAsync(path, new { login = "local-dev-user" }, TestContext.Current.CancellationToken)
            : await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<WebApplication> CreateAuthRoutesAppAsync(string environmentName)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        builder.Services.AddSingleton<IDevelopmentGitHubCliAuth, ThrowingDevelopmentGitHubCliAuth>();
        builder.Services.AddSingleton<GitHubTokenProvider>();
        builder.Services.AddScoped<GitHubAuthService>();

        var app = builder.Build();
        app.MapGitHubAuthRoutes();
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

    private sealed class ThrowingDevelopmentGitHubCliAuth : IDevelopmentGitHubCliAuth
    {
        public Task<GitHubCliTokenResult> GetTokenAsync(string? user, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("gh auth should not be called by production dev routes.");

        public Task<IReadOnlyList<DevelopmentGitHubAccount>> GetAccountsAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("gh auth status should not be called by production dev routes.");
    }
}
