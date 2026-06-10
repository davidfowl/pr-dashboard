using Microsoft.Extensions.Options;

sealed class GitHubPublicCacheIdentity(IOptions<GitHubCacheWarmupOptions> options)
{
    private const string PublicCacheTokenEnvironmentVariable = "GITHUB_PUBLIC_CACHE_TOKEN";

    public TokenResult? GetToken()
    {
        var configuredToken = options.Value.PublicCacheToken;
        if (!string.IsNullOrWhiteSpace(configuredToken))
        {
            return new TokenResult(configuredToken.Trim(), "public-cache");
        }

        var environmentToken = Environment.GetEnvironmentVariable(PublicCacheTokenEnvironmentVariable);
        return string.IsNullOrWhiteSpace(environmentToken)
            ? null
            : new TokenResult(environmentToken.Trim(), "public-cache");
    }
}
