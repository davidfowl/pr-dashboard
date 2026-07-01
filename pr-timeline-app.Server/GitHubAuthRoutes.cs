using AspNet.Security.OAuth.GitHub;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

public static class GitHubAuthRoutes
{
    public static IEndpointRouteBuilder MapGitHubAuthRoutes(this IEndpointRouteBuilder endpoints)
    {
        var logger = endpoints.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("GitHubAuthRoutes");
        var api = endpoints.MapGroup("/api/github");

        api.MapGet("auth-status", async (GitHubAuthService auth, CancellationToken cancellationToken) =>
            Results.Ok(await auth.GetStatusAsync(cancellationToken)));

        api.MapGet("dev/accounts", async Task<IResult> (
            GitHubTokenProvider tokenProvider,
            IDevelopmentGitHubCliAuth developmentGitHubCliAuth,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            if (!environment.IsDevelopment())
            {
                logger.LogWarning("Rejected development GitHub account list request outside Development.");
                return Results.NotFound();
            }

            var accounts = await developmentGitHubCliAuth.GetAccountsAsync(cancellationToken);
            logger.LogInformation(
                "Development GitHub account list resolved. AccountCount={DevelopmentGitHubAccountCount}, Selected={DevelopmentGitHubAccountSelected}.",
                accounts.Count,
                !string.IsNullOrWhiteSpace(tokenProvider.GetDevelopmentGitHubUser()));
            return Results.Ok(new DevelopmentGitHubAccountsResponse(accounts, tokenProvider.GetDevelopmentGitHubUser()));
        });

        api.MapPost("dev/account", async Task<IResult> (
            HttpContext context,
            GitHubTokenProvider tokenProvider,
            IDevelopmentGitHubCliAuth developmentGitHubCliAuth,
            IHostEnvironment environment,
            DevelopmentGitHubAccountSelectionRequest request,
            CancellationToken cancellationToken) =>
        {
            if (!environment.IsDevelopment())
            {
                logger.LogWarning("Rejected development GitHub account selection request outside Development.");
                return Results.NotFound();
            }

            if (!IsBrowserMutationRequest(context))
            {
                logger.LogWarning("Rejected development GitHub account selection because the request was not a trusted browser mutation.");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var login = string.IsNullOrWhiteSpace(request.Login) ? null : request.Login.Trim();
            if (login is not null)
            {
                var accounts = await developmentGitHubCliAuth.GetAccountsAsync(cancellationToken);
                if (!accounts.Any(account => account.Login.Equals(login, StringComparison.OrdinalIgnoreCase)))
                {
                    logger.LogWarning("Rejected development GitHub account selection because the requested account is not available.");
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["login"] = ["Choose a keyring-backed GitHub account from `gh auth status`."]
                    });
                }
            }

            tokenProvider.SetDevelopmentGitHubUser(login);
            logger.LogInformation(
                "Development GitHub account selection completed. Selected={DevelopmentGitHubAccountSelected}.",
                login is not null);
            return Results.Ok(new DevelopmentGitHubAccountSelectionResponse(login));
        });

        api.MapGet("login", ([FromQuery] string? returnUrl) =>
        {
            logger.LogInformation(
                "GitHub OAuth login requested. ReturnUrlProvided={GitHubReturnUrlProvided}, OAuthConfigured={GitHubOAuthConfigured}.",
                !string.IsNullOrWhiteSpace(returnUrl),
                GitHubOAuthConfiguration.IsConfigured);

            if (!GitHubOAuthConfiguration.IsConfigured)
            {
                logger.LogWarning("Rejected GitHub OAuth login because OAuth is not configured.");
                return Results.Problem(
                    title: "GitHub login is not configured",
                    detail: "Set GITHUB_CLIENT_ID and GITHUB_CLIENT_SECRET for a GitHub OAuth App.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!TryNormalizeLocalReturnUrl(returnUrl, out var localReturnUrl))
            {
                logger.LogWarning("Rejected GitHub OAuth login because the return URL was not local.");
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["returnUrl"] = ["Return URL must be a local path."]
                });
            }

            logger.LogInformation("Issuing GitHub OAuth challenge.");
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = localReturnUrl },
                [GitHubAuthenticationDefaults.AuthenticationScheme]);
        });

        api.MapPost("logout", async (HttpContext context, GitHubAuthService auth) =>
        {
            logger.LogInformation(
                "GitHub logout requested. AuthenticatedPrincipal={GitHubAuthenticatedPrincipal}.",
                context.User.Identity?.IsAuthenticated == true);

            if (!IsBrowserMutationRequest(context))
            {
                logger.LogWarning("Rejected GitHub logout because the request was not a trusted browser mutation.");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            auth.Logout();
            logger.LogInformation("GitHub logout completed.");
            return Results.Ok(new { authenticated = false });
        });

        return endpoints;
    }

    private static bool IsBrowserMutationRequest(HttpContext context)
    {
        if (!context.Request.HasJsonContentType())
        {
            return false;
        }

        var origin = context.Request.Headers.Origin.ToString();
        return string.IsNullOrEmpty(origin)
            || Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && (uri.IsLoopback || uri.Host.Equals(context.Request.Host.Host, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryNormalizeLocalReturnUrl(string? returnUrl, out string localReturnUrl)
    {
        localReturnUrl = "/";

        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return true;
        }

        if (!Uri.TryCreate(returnUrl, UriKind.Relative, out _)
            || !returnUrl.StartsWith("/", StringComparison.Ordinal)
            || returnUrl.StartsWith("//", StringComparison.Ordinal)
            || returnUrl.Contains("\\", StringComparison.Ordinal))
        {
            return false;
        }

        localReturnUrl = returnUrl;
        return true;
    }
}
