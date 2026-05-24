sealed class GitHubAuthService(GitHubTokenProvider tokenProvider, GitHubClient gitHub, IHostEnvironment environment)
{
    public async Task<AuthStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken);
        var login = token is null ? null : await gitHub.GetCurrentUserLoginAsync(cancellationToken);

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
