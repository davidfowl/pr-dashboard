using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace pr_timeline_app.Tests;

public sealed class GitHubTokenProviderTests
{
    [Fact]
    public void RecordLoginStoresOAuthTicketCacheDiscriminator()
    {
        var provider = CreateProvider();
        var properties = new AuthenticationProperties();

        provider.RecordLogin(properties);

        Assert.True(properties.Items.TryGetValue(
            GitHubTokenProvider.OAuthTicketCacheDiscriminatorKey,
            out var discriminator));
        Assert.False(string.IsNullOrWhiteSpace(discriminator));
    }

    [Fact]
    public async Task RecordLoginRefreshesCachedGitHubCliToken()
    {
        var tokens = new Queue<string>(["first-token", "second-token"]);
        var provider = CreateProvider(
            developmentGitHubCliAuth: new TestDevelopmentGitHubCliAuth((_, _) =>
                Task.FromResult(GitHubCliTokenResult.Success(tokens.Dequeue()))),
            configuration: CreateConfiguration());

        var first = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        var cached = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        provider.RecordLogin(new AuthenticationProperties());
        var refreshed = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("first-token", first?.Value);
        Assert.Equal("first-token", cached?.Value);
        Assert.Equal("second-token", refreshed?.Value);
    }

    [Fact]
    public async Task RecordLoginWaitsForInFlightGitHubCliTokenLookupBeforeResettingFallbackCache()
    {
        var lookupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLookup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenRequests = 0;
        var provider = CreateProvider(
            developmentGitHubCliAuth: new TestDevelopmentGitHubCliAuth(async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref tokenRequests) == 1)
                {
                    lookupStarted.SetResult();
                    await releaseLookup.Task.WaitAsync(cancellationToken);
                    return GitHubCliTokenResult.Success("first-token");
                }

                return GitHubCliTokenResult.Success("second-token");
            }),
            configuration: CreateConfiguration());

        var firstTokenTask = provider.GetTokenAsync(TestContext.Current.CancellationToken);
        await lookupStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var resetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resetTask = Task.Run(() =>
        {
            resetStarted.SetResult();
            provider.RecordLogin(new AuthenticationProperties());
        }, TestContext.Current.CancellationToken);

        await resetStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var resetCompletedBeforeLookup = await Task.WhenAny(
            resetTask,
            Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken)) == resetTask;

        Assert.False(resetCompletedBeforeLookup);

        releaseLookup.SetResult();
        var first = await firstTokenTask;
        await resetTask.WaitAsync(TestContext.Current.CancellationToken);
        var second = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("first-token", first?.Value);
        Assert.Equal("second-token", second?.Value);
        Assert.Equal(2, tokenRequests);
    }

    [Fact]
    public async Task DevelopmentGitHubCliTokenIsTrimmedBeforeCaching()
    {
        var calls = 0;
        var provider = CreateProvider(
            developmentGitHubCliAuth: new TestDevelopmentGitHubCliAuth((_, _) =>
            {
                calls++;
                return Task.FromResult(GitHubCliTokenResult.Success(" gh-token \n"));
            }),
            configuration: CreateConfiguration());

        var first = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        var cached = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal("gh-token", first?.Value);
        Assert.Equal("gh-token", cached?.Value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task OAuthCacheKeyUsesTicketDiscriminator()
    {
        var firstProvider = CreateProvider(
            httpContext: CreateHttpContextWithGitHubToken("oauth-token", "first-ticket"),
            environmentName: Environments.Production);
        var firstCacheKey = await firstProvider.GetCacheKeyAsync(TestContext.Current.CancellationToken);

        var secondProvider = CreateProvider(
            httpContext: CreateHttpContextWithGitHubToken("oauth-token", "second-ticket"),
            environmentName: Environments.Production);
        var secondCacheKey = await secondProvider.GetCacheKeyAsync(TestContext.Current.CancellationToken);

        Assert.Contains(":first-ticket", firstCacheKey);
        Assert.Contains(":second-ticket", secondCacheKey);
        Assert.NotEqual(firstCacheKey, secondCacheKey);
    }

    [Fact]
    public async Task OAuthCacheKeyUsesIssuedTimeWhenTicketDiscriminatorIsMissing()
    {
        var provider = CreateProvider(
            httpContext: CreateHttpContextWithGitHubToken(
                "oauth-token",
                cacheDiscriminator: null,
                issuedUtc: DateTimeOffset.FromUnixTimeMilliseconds(123456000)),
            environmentName: Environments.Production);

        var cacheKey = await provider.GetCacheKeyAsync(TestContext.Current.CancellationToken);

        Assert.Contains(":123456000", cacheKey);
    }

    [Fact]
    public async Task RecordLoginCreatesDifferentOAuthCacheKeysForNewTickets()
    {
        var firstProperties = new AuthenticationProperties();
        var secondProperties = new AuthenticationProperties();
        var provider = CreateProvider(
            httpContext: CreateHttpContextWithGitHubToken("oauth-token"),
            environmentName: Environments.Production);

        provider.RecordLogin(firstProperties);
        provider.RecordLogin(secondProperties);
        var firstCacheKey = await CreateProvider(
            httpContext: CreateHttpContextWithGitHubToken(
                "oauth-token",
                firstProperties.Items[GitHubTokenProvider.OAuthTicketCacheDiscriminatorKey]),
            environmentName: Environments.Production)
            .GetCacheKeyAsync(TestContext.Current.CancellationToken);
        var secondCacheKey = await CreateProvider(
            httpContext: CreateHttpContextWithGitHubToken(
                "oauth-token",
                secondProperties.Items[GitHubTokenProvider.OAuthTicketCacheDiscriminatorKey]),
            environmentName: Environments.Production)
            .GetCacheKeyAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(firstCacheKey, secondCacheKey);
    }

    [Fact]
    public async Task ProductionOAuthTokenIsReadFromCurrentHttpContextOnSingletonProvider()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = CreateHttpContextWithGitHubToken("first-oauth-token", "first-ticket")
        };
        var provider = CreateProvider(
            httpContextAccessor: accessor,
            environmentName: Environments.Production,
            developmentGitHubCliAuth: ThrowingDevelopmentGitHubCliAuth());

        var first = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        var firstCacheKey = await provider.GetCacheKeyAsync(TestContext.Current.CancellationToken);
        accessor.HttpContext = CreateHttpContextWithGitHubToken("second-oauth-token", "second-ticket");
        var second = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        var secondCacheKey = await provider.GetCacheKeyAsync(TestContext.Current.CancellationToken);

        Assert.Equal("first-oauth-token", first?.Value);
        Assert.Equal("oauth", first?.Source);
        Assert.Contains(":first-ticket", firstCacheKey);
        Assert.Equal("second-oauth-token", second?.Value);
        Assert.Equal("oauth", second?.Source);
        Assert.Contains(":second-ticket", secondCacheKey);
        Assert.NotEqual(firstCacheKey, secondCacheKey);
    }

    [Fact]
    public async Task ProductionOAuthCacheKeyRotatesForNewTicketEvenWithSameAccessToken()
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = CreateHttpContextWithGitHubToken("same-oauth-token", "first-ticket")
        };
        var provider = CreateProvider(
            httpContextAccessor: accessor,
            environmentName: Environments.Production,
            developmentGitHubCliAuth: ThrowingDevelopmentGitHubCliAuth());

        var firstCacheKey = await provider.GetCacheKeyAsync(TestContext.Current.CancellationToken);
        accessor.HttpContext = CreateHttpContextWithGitHubToken("same-oauth-token", "second-ticket");
        var secondCacheKey = await provider.GetCacheKeyAsync(TestContext.Current.CancellationToken);

        Assert.Contains(":first-ticket", firstCacheKey);
        Assert.Contains(":second-ticket", secondCacheKey);
        Assert.NotEqual(firstCacheKey, secondCacheKey);
    }

    [Fact]
    public async Task ProductionLogoutRotatesAnonymousCacheKeyAndKeepsFallbacksDisabled()
    {
        var provider = CreateProvider(
            environmentName: Environments.Production,
            developmentGitHubCliAuth: ThrowingDevelopmentGitHubCliAuth(),
            configuration: CreateConfiguration(new Dictionary<string, string?>
            {
                ["GITHUB_TOKEN"] = "environment-token",
                ["GH_TOKEN"] = "alternate-environment-token"
            }));

        var beforeLogoutToken = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        var beforeLogoutCacheKey = await provider.GetCacheKeyAsync(TestContext.Current.CancellationToken);
        provider.Logout();
        var afterLogoutToken = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        var afterLogoutCacheKey = await provider.GetCacheKeyAsync(TestContext.Current.CancellationToken);

        Assert.Null(beforeLogoutToken);
        Assert.Null(afterLogoutToken);
        Assert.Equal("anonymous:0", beforeLogoutCacheKey);
        Assert.Equal("anonymous:1", afterLogoutCacheKey);
    }

    [Fact]
    public async Task ProductionOAuthTokenWorksAfterLogoutSuppressedFallback()
    {
        var accessor = new HttpContextAccessor();
        var provider = CreateProvider(
            httpContextAccessor: accessor,
            environmentName: Environments.Production,
            developmentGitHubCliAuth: ThrowingDevelopmentGitHubCliAuth(),
            configuration: CreateConfiguration(new Dictionary<string, string?>
            {
                ["GITHUB_TOKEN"] = "environment-token"
            }));

        provider.Logout();
        var loggedOutCacheKey = await provider.GetCacheKeyAsync(TestContext.Current.CancellationToken);
        accessor.HttpContext = CreateHttpContextWithGitHubToken("post-logout-oauth-token", "post-logout-ticket");
        var token = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        var oauthCacheKey = await provider.GetCacheKeyAsync(TestContext.Current.CancellationToken);

        Assert.Equal("anonymous:1", loggedOutCacheKey);
        Assert.Equal("post-logout-oauth-token", token?.Value);
        Assert.Equal("oauth", token?.Source);
        Assert.Contains(":post-logout-ticket", oauthCacheKey);
        Assert.NotEqual(loggedOutCacheKey, oauthCacheKey);
    }

    [Fact]
    public async Task RecordLoginReEnablesDevelopmentFallbackAfterLogout()
    {
        var provider = CreateProvider(configuration: CreateConfiguration(new Dictionary<string, string?>
        {
            ["GITHUB_TOKEN"] = "environment-token"
        }));

        provider.Logout();
        var loggedOut = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        provider.RecordLogin(new AuthenticationProperties());
        var loggedIn = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Null(loggedOut);
        Assert.Equal("environment-token", loggedIn?.Value);
    }

    [Fact]
    public async Task ProductionWithoutOAuthIgnoresDevelopmentFallbacks()
    {
        var developmentGitHubCliAuth = new TestDevelopmentGitHubCliAuth(
            getTokenAsync: (_, _) => throw new InvalidOperationException("gh auth should not be called in Production."),
            getAccountsAsync: _ => throw new InvalidOperationException("gh auth status should not be called in Production."));
        var provider = CreateProvider(
            environmentName: Environments.Production,
            developmentGitHubCliAuth: developmentGitHubCliAuth,
            configuration: CreateConfiguration(new Dictionary<string, string?>
            {
                ["GITHUB_TOKEN"] = "environment-token",
                ["GH_TOKEN"] = "alternate-environment-token"
            }));

        var token = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Null(token);
    }

    [Fact]
    public async Task GetTokenAsyncReportsGitHubCliFailureInDevelopment()
    {
        var provider = CreateProvider(
            developmentGitHubCliAuth: new TestDevelopmentGitHubCliAuth((_, _) =>
                Task.FromResult(GitHubCliTokenResult.Failed(127, "gh: command not found"))));

        var token = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Null(token);
        Assert.Equal("`gh auth token` exited with code 127: gh: command not found", provider.LocalAuthFailureMessage);
    }

    [Fact]
    public async Task AuthStatusReportsLocalGitHubCliFailure()
    {
        var provider = CreateProvider(
            developmentGitHubCliAuth: new TestDevelopmentGitHubCliAuth((_, _) =>
                Task.FromResult(GitHubCliTokenResult.NotFound("gh"))));
        var service = new GitHubAuthService(
            provider,
            gitHub: null!,
            new TestHostEnvironment { EnvironmentName = Environments.Development },
            new LoggerFactory().CreateLogger<GitHubAuthService>());

        var status = await service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.False(status.Authenticated);
        Assert.Contains("No GitHub token is available to the local backend.", status.Message);
        Assert.Contains("Last local token check failed: `gh` was not found", status.Message);
        Assert.Contains("If running with Aspire, restart the app after `gh auth login`", status.Message);
    }

    [Fact]
    public async Task OAuthTokenTakesPrecedenceOverSelectedDevelopmentGitHubUser()
    {
        var developmentGitHubCliAuth = new TestDevelopmentGitHubCliAuth(
            getTokenAsync: (_, _) => throw new InvalidOperationException("gh auth should not be called when OAuth is active."));
        var provider = CreateProvider(
            httpContext: CreateHttpContextWithGitHubToken("oauth-token", "oauth-ticket"),
            developmentGitHubCliAuth: developmentGitHubCliAuth);

        provider.SetDevelopmentGitHubUser("local-dev-user");
        var token = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(token);
        Assert.Equal("oauth-token", token.Value);
        Assert.Equal("oauth", token.Source);
        Assert.Equal("oauth-ticket", token.CacheDiscriminator);
    }

    [Fact]
    public void SelectingDevelopmentGitHubUserThrowsOutsideDevelopment()
    {
        var provider = CreateProvider(environmentName: Environments.Production);

        var ex = Assert.Throws<InvalidOperationException>(() => provider.SetDevelopmentGitHubUser("local-dev-user"));

        Assert.Contains("only supported in Development", ex.Message);
    }

    [Fact]
    public void LogoutClearsSelectedDevelopmentGitHubUser()
    {
        var provider = CreateProvider();

        provider.SetDevelopmentGitHubUser("alternate-user");
        provider.Logout();

        Assert.Null(provider.GetDevelopmentGitHubUser());
    }

    [Fact]
    public async Task SelectedDevelopmentGitHubUserSwitchesTokenInPlace()
    {
        var requestedUsers = new List<string?>();
        var provider = CreateProvider(
            developmentGitHubCliAuth: new TestDevelopmentGitHubCliAuth((user, _) =>
            {
                requestedUsers.Add(user);
                return Task.FromResult(GitHubCliTokenResult.Success($"{user}-token"));
            }),
            configuration: CreateConfiguration(new Dictionary<string, string?>
            {
                ["GH_TOKEN"] = "environment-token"
            }));

        provider.SetDevelopmentGitHubUser("davifowl_microsoft");
        var first = await provider.GetTokenAsync(TestContext.Current.CancellationToken);
        provider.SetDevelopmentGitHubUser("davidfowl");
        var second = await provider.GetTokenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["davifowl_microsoft", "davidfowl"], requestedUsers);
        Assert.Equal("davifowl_microsoft-token", first?.Value);
        Assert.Equal("davidfowl-token", second?.Value);
        Assert.Equal("davidfowl", provider.GetDevelopmentGitHubUser());
    }

    [Fact]
    public void ParseDevelopmentGitHubAccountsUsesSuccessfulKeyringAccounts()
    {
        const string output = """
            {
              "hosts": {
                "github.com": [
                  {"state":"success","active":true,"login":"davidfowl","tokenSource":"GH_TOKEN"},
                  {"state":"success","active":false,"login":"davidfowl","tokenSource":"keyring"},
                  {"state":"success","active":false,"login":"davifowl_microsoft","tokenSource":"keyring"},
                  {"state":"failed","active":false,"login":"bad","tokenSource":"keyring"}
                ]
              }
            }
            """;

        var accounts = DevelopmentGitHubCliAuth.ParseAccounts(output);

        Assert.Equal(["davidfowl", "davifowl_microsoft"], accounts.Select(account => account.Login));
        Assert.False(accounts[0].Active);
    }

    [Fact]
    public void ParseDevelopmentGitHubAccountsIgnoresMalformedOutput()
    {
        var accounts = DevelopmentGitHubCliAuth.ParseAccounts("not-json");

        Assert.Empty(accounts);
    }

    private static GitHubTokenProvider CreateProvider(
        HttpContext? httpContext = null,
        IHttpContextAccessor? httpContextAccessor = null,
        string environmentName = "Development",
        IDevelopmentGitHubCliAuth? developmentGitHubCliAuth = null,
        IConfiguration? configuration = null) =>
        new(
            httpContextAccessor ?? new HttpContextAccessor { HttpContext = httpContext },
            new TestHostEnvironment { EnvironmentName = environmentName },
            configuration ?? CreateConfiguration(),
            developmentGitHubCliAuth ?? new TestDevelopmentGitHubCliAuth());

    private static IConfiguration CreateConfiguration(
        IEnumerable<KeyValuePair<string, string?>>? values = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

    private static DefaultHttpContext CreateHttpContextWithGitHubToken(
        string token,
        string? cacheDiscriminator = null,
        DateTimeOffset? issuedUtc = null)
    {
        var context = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddSingleton(new TestGitHubTicket(token, cacheDiscriminator, issuedUtc));
        services.AddLogging();
        services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("test", _ => { });
        context.RequestServices = services.BuildServiceProvider();

        return context;
    }

    private sealed record TestGitHubTicket(string Value, string? CacheDiscriminator, DateTimeOffset? IssuedUtc);

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TestGitHubTicket githubTicket)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "octocat")], Scheme.Name);
            var properties = new AuthenticationProperties();
            properties.StoreTokens([
                new AuthenticationToken
                {
                    Name = "access_token",
                    Value = githubTicket.Value
                }
            ]);
            if (!string.IsNullOrWhiteSpace(githubTicket.CacheDiscriminator))
            {
                properties.Items[GitHubTokenProvider.OAuthTicketCacheDiscriminatorKey] = githubTicket.CacheDiscriminator;
            }

            properties.IssuedUtc = githubTicket.IssuedUtc;

            var authenticationTicket = new AuthenticationTicket(new ClaimsPrincipal(identity), properties, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(authenticationTicket));
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "pr-timeline-app.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static TestDevelopmentGitHubCliAuth ThrowingDevelopmentGitHubCliAuth() =>
        new(
            getTokenAsync: (_, _) => throw new InvalidOperationException("gh auth should not be called in Production."),
            getAccountsAsync: _ => throw new InvalidOperationException("gh auth status should not be called in Production."));

    private sealed class TestDevelopmentGitHubCliAuth(
        Func<string?, CancellationToken, Task<GitHubCliTokenResult>>? getTokenAsync = null,
        Func<CancellationToken, Task<IReadOnlyList<DevelopmentGitHubAccount>>>? getAccountsAsync = null)
        : IDevelopmentGitHubCliAuth
    {
        public Task<GitHubCliTokenResult> GetTokenAsync(string? user, CancellationToken cancellationToken) =>
            getTokenAsync?.Invoke(user, cancellationToken) ??
            Task.FromResult(GitHubCliTokenResult.NotFound("gh"));

        public Task<IReadOnlyList<DevelopmentGitHubAccount>> GetAccountsAsync(CancellationToken cancellationToken) =>
            getAccountsAsync?.Invoke(cancellationToken) ??
            Task.FromResult<IReadOnlyList<DevelopmentGitHubAccount>>([]);
    }
}
