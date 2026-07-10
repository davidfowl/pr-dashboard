using System.Net.Http.Headers;
using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication.Cookies;

public static class GitHubServiceCollectionExtensions
{
    public static IServiceCollection AddGitHubApiServices(this IServiceCollection services, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (!environment.IsDevelopment() && !GitHubOAuthConfiguration.IsConfigured)
        {
            throw new InvalidOperationException(
                "GitHub OAuth must be configured outside Development. Set GITHUB_CLIENT_ID and GITHUB_CLIENT_SECRET.");
        }

        services.AddHttpContextAccessor();
        services.AddMemoryCache();
        services.AddScoped<GitHubAuthService>();
        services.AddScoped<GitHubPullRequestService>();
        services.AddSingleton<IDevelopmentGitHubCliAuth, DevelopmentGitHubCliAuth>();
        services.AddSingleton<GitHubTokenProvider>();
        services.AddSingleton<GitHubPublicCacheIdentity>();
        services.AddSingleton<GitHubPublicCacheStore>();
        services.AddSingleton<GitHubResponseCache>();
        services.AddSingleton<GitHubPullRequestGraphQlState>();
        services.AddHostedService<GitHubPublicCacheWarmupService>();

        var authentication = services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            });

        authentication.AddCookie();

        if (GitHubOAuthConfiguration.IsConfigured)
        {
            authentication.AddGitHub(options =>
            {
                options.ClientId = GitHubOAuthConfiguration.ClientId ?? "";
                options.ClientSecret = GitHubOAuthConfiguration.ClientSecret ?? "";
                options.SaveTokens = true;
                options.Scope.Clear();
                options.Events.OnRedirectToAuthorizationEndpoint = context =>
                {
                    context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("GitHubOAuth")
                        .LogInformation("Redirecting to GitHub OAuth authorization endpoint.");
                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
                options.Events.OnCreatingTicket = context =>
                {
                    context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("GitHubOAuth")
                        .LogInformation(
                            "GitHub OAuth ticket received. AccessTokenPresent={GitHubAccessTokenPresent}, RefreshTokenPresent={GitHubRefreshTokenPresent}.",
                            !string.IsNullOrWhiteSpace(context.AccessToken),
                            !string.IsNullOrWhiteSpace(context.RefreshToken));

                    context.HttpContext.RequestServices
                        .GetRequiredService<GitHubTokenProvider>()
                        .RecordLogin(context.Properties);
                    return Task.CompletedTask;
                };
                options.Events.OnTicketReceived = context =>
                {
                    context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("GitHubOAuth")
                        .LogInformation(
                            "GitHub OAuth ticket accepted. PrincipalAuthenticated={GitHubPrincipalAuthenticated}.",
                            context.Principal?.Identity?.IsAuthenticated == true);
                    return Task.CompletedTask;
                };
                options.Events.OnRemoteFailure = context =>
                {
                    var failureMessage = NormalizeOAuthFailureMessage(context.Failure?.Message);
                    context.HttpContext.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("GitHubOAuth")
                        .LogWarning(
                            "GitHub OAuth remote failure. FailureType={GitHubOAuthFailureType}, FailureMessage={GitHubOAuthFailureMessage}.",
                            context.Failure?.GetType().Name ?? "unknown",
                            failureMessage);
                    context.HandleResponse();
                    context.Response.Redirect(CreateOAuthFailureRedirectPath(context.Properties?.RedirectUri, failureMessage));
                    return Task.CompletedTask;
                };
            });
        }

        services.AddHttpClient<GitHubCacheScopeResolver>(ConfigureGitHubHttpClient)
        .ConfigurePrimaryHttpMessageHandler(CreateGitHubHttpMessageHandler);

        services.AddHttpClient<GitHubClient>(ConfigureGitHubHttpClient)
        .ConfigurePrimaryHttpMessageHandler(CreateGitHubHttpMessageHandler);

        return services;
    }

    private static void ConfigureGitHubHttpClient(HttpClient httpClient)
    {
        httpClient.BaseAddress = new Uri("https://api.github.com/");

        // GitHub REST API requires a User-Agent and recommends this version header.
        // https://docs.github.com/en/rest/using-the-rest-api/getting-started-with-the-rest-api
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("pr-timeline-app", "1.0"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    private static SocketsHttpHandler CreateGitHubHttpMessageHandler() =>
        new()
        {
            // GitHub redirects old repository names to canonical repository IDs. HttpClient drops
            // Authorization when it auto-follows redirects, so GitHub sees the redirected request as anonymous.
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = GitHubClient.MaxConcurrentGitHubRequests
        };

    internal static string NormalizeOAuthFailureMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "GitHub sign-in failed. Your GitHub account or organization may not allow this OAuth app.";
        }

        var normalized = message.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 240 ? normalized : normalized[..240];
    }

    internal static string CreateOAuthFailureRedirectPath(string? redirectUri, string failureMessage)
    {
        var path = IsLocalRedirectPath(redirectUri) ? redirectUri! : "/";
        var hashIndex = path.IndexOf('#', StringComparison.Ordinal);
        var hash = hashIndex >= 0 ? path[hashIndex..] : "";
        var pathAndQuery = hashIndex >= 0 ? path[..hashIndex] : path;
        var separator = pathAndQuery.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{pathAndQuery}{separator}githubAuthError={Uri.EscapeDataString(failureMessage)}{hash}";
    }

    private static bool IsLocalRedirectPath(string? redirectUri) =>
        !string.IsNullOrWhiteSpace(redirectUri)
        && Uri.TryCreate(redirectUri, UriKind.Relative, out _)
        && redirectUri.StartsWith("/", StringComparison.Ordinal)
        && !redirectUri.StartsWith("//", StringComparison.Ordinal)
        && !redirectUri.Contains('\\', StringComparison.Ordinal);
}
