sealed class GitHubAuthService(
    GitHubTokenProvider tokenProvider,
    GitHubClient gitHub,
    IHostEnvironment environment,
    ILogger<GitHubAuthService> logger)
{
    public async Task<AuthStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        logger.LogDebug(
            "Checking GitHub authentication status. OAuthConfigured={GitHubOAuthConfigured}.",
            GitHubOAuthConfiguration.IsConfigured);

        var token = await tokenProvider.GetTokenAsync(cancellationToken);
        string? login = null;
        if (token is not null)
        {
            try
            {
                login = await gitHub.GetCurrentUserLoginAsync(cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex,
                    "GitHub authentication status could not resolve the current user. TokenSource={GitHubTokenSource}.",
                    token.Source);
                throw;
            }
        }

        logger.LogDebug(
            "GitHub authentication status resolved. Authenticated={GitHubAuthenticated}, TokenSource={GitHubTokenSource}, LoginResolved={GitHubLoginResolved}, OAuthConfigured={GitHubOAuthConfigured}.",
            token is not null,
            token?.Source ?? "none",
            !string.IsNullOrWhiteSpace(login),
            GitHubOAuthConfiguration.IsConfigured);

        return new AuthStatusResponse(
            Authenticated: token is not null,
            Configured: GitHubOAuthConfiguration.IsConfigured,
            CanLogin: GitHubOAuthConfiguration.IsConfigured,
            Source: token?.Source,
            Login: login,
            Message: token is null
                ? GitHubOAuthConfiguration.IsConfigured
                    ? "Sign in with GitHub to let the dashboard call the GitHub API."
                    : environment.IsDevelopment()
                        ? "Set GITHUB_CLIENT_ID and GITHUB_CLIENT_SECRET for GitHub login, or set GITHUB_TOKEN/GH_TOKEN, or run `gh auth login`."
                        : "GitHub login is not configured. Set GITHUB_CLIENT_ID and GITHUB_CLIENT_SECRET."
                : token.Source == "oauth"
                    ? "Signed in with GitHub for this local session."
                    : "GitHub API token is available to the local backend.");
    }

    public void Logout() => tokenProvider.Logout();
}
