using System.Security.Claims;

// Resolves the current GitHub identity for notification endpoints. The user id/login is taken
// from the authenticated principal and is NEVER read from the request body or path, so a
// caller cannot act on behalf of another user.
//
// Primary path (production): the OAuth cookie carries the GitHub numeric id (NameIdentifier)
// and login (Name) claims, so no extra GitHub call is needed.
//
// Dev fallback: when the developer is authenticated via a gh CLI / environment token instead
// of the OAuth cookie there is no principal, so we resolve the identity from GitHub's /user
// endpoint. This keeps local testing usable without standing up an OAuth app.
sealed class NotificationUserResolver(
    IHttpContextAccessor httpContextAccessor,
    GitHubClient gitHub,
    GitHubTokenProvider tokenProvider,
    IHostEnvironment environment)
{
    private const string GitHubLoginClaim = "urn:github:login";

    public async Task<NotificationUser?> ResolveAsync(CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated == true
            && long.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            && id > 0)
        {
            var login = principal.FindFirstValue(ClaimTypes.Name)
                ?? principal.FindFirstValue(GitHubLoginClaim);
            if (!string.IsNullOrWhiteSpace(login))
            {
                return new NotificationUser(id, login.Trim());
            }
        }

        if (!environment.IsDevelopment())
        {
            return null;
        }

        // Dev-only: only attempt the API resolution when a usable token is present so we do
        // not make anonymous calls for unauthenticated callers.
        var token = await tokenProvider.GetTokenAsync(cancellationToken);
        if (token is null)
        {
            return null;
        }

        return await gitHub.GetCurrentUserIdentityAsync(cancellationToken);
    }
}
