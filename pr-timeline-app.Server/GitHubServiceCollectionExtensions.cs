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
        services.AddSingleton<GitHubTokenProvider>();

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
            });
        }

        services.AddHttpClient<GitHubClient>(httpClient =>
        {
            httpClient.BaseAddress = new Uri("https://api.github.com/");

            // GitHub REST API requires a User-Agent and recommends this version header.
            // https://docs.github.com/en/rest/using-the-rest-api/getting-started-with-the-rest-api
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("pr-timeline-app", "1.0"));
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            // GitHub redirects old repository names to canonical repository IDs. HttpClient drops
            // Authorization when it auto-follows redirects, so GitHub sees the redirected request as anonymous.
            AllowAutoRedirect = false,
            MaxConnectionsPerServer = GitHubClient.MaxConcurrentGitHubRequests
        });

        return services;
    }
}
