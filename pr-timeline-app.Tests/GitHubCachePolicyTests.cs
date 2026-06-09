namespace pr_timeline_app.Tests;

public sealed class GitHubCachePolicyTests
{
    private static readonly RepositoryName MixedCaseRepository = new("Example", "Repo");

    [Theory]
    [InlineData("public", nameof(GitHubRepositoryVisibility.Public))]
    [InlineData(" PUBLIC ", nameof(GitHubRepositoryVisibility.Public))]
    [InlineData("private", nameof(GitHubRepositoryVisibility.Private))]
    [InlineData("internal", nameof(GitHubRepositoryVisibility.Internal))]
    [InlineData("", nameof(GitHubRepositoryVisibility.Unknown))]
    [InlineData(null, nameof(GitHubRepositoryVisibility.Unknown))]
    [InlineData("enterprise", nameof(GitHubRepositoryVisibility.Unknown))]
    public void ClassifiesGitHubRepositoryVisibility(string? visibility, string expected)
    {
        Assert.Equal(
            Enum.Parse<GitHubRepositoryVisibility>(expected),
            GitHubCachePolicy.ClassifyRepositoryVisibility(visibility));
    }

    [Fact]
    public void PublicRepositoryScopeUsesSharedAnonymousLane()
    {
        const string authCacheKey = "oauth:abcdef0123456789:0";

        var scope = GitHubCachePolicy.CreateRepositoryScope(authCacheKey, GitHubRepositoryVisibility.Public);
        var cacheKey = GitHubCachePolicy.CreateRepositoryCacheKey(scope, MixedCaseRepository, "pulls", "open");

        Assert.Equal(GitHubCacheScopeKind.Public, scope.Kind);
        Assert.True(scope.IsShared);
        Assert.Equal(GitHubRequestAuthorization.Anonymous, scope.RequestAuthorization);
        Assert.Equal("pulls:public:example/repo:open", cacheKey);
        Assert.DoesNotContain(authCacheKey, cacheKey, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(GitHubRepositoryVisibility.Unknown))]
    [InlineData(nameof(GitHubRepositoryVisibility.Private))]
    [InlineData(nameof(GitHubRepositoryVisibility.Internal))]
    public void NonPublicRepositoryScopeUsesTokenLane(string visibilityName)
    {
        const string authCacheKey = "oauth:abcdef0123456789:0";
        var visibility = Enum.Parse<GitHubRepositoryVisibility>(visibilityName);

        var scope = GitHubCachePolicy.CreateRepositoryScope(authCacheKey, visibility);
        var cacheKey = GitHubCachePolicy.CreateRepositoryCacheKey(scope, MixedCaseRepository, "pulls", "open");

        Assert.Equal(GitHubCacheScopeKind.Token, scope.Kind);
        Assert.False(scope.IsShared);
        Assert.Equal(GitHubRequestAuthorization.Token, scope.RequestAuthorization);
        Assert.Equal("pulls:token:oauth:abcdef0123456789:0:example/repo:open", cacheKey);
    }

    [Fact]
    public void UserScopeUsesTokenLaneAndNeverShares()
    {
        const string authCacheKey = "oauth:abcdef0123456789:0";

        var scope = GitHubCachePolicy.CreateUserScope(authCacheKey);
        var cacheKey = GitHubCachePolicy.CreateUserCacheKey(scope, "current-user");

        Assert.Equal(GitHubCacheScopeKind.User, scope.Kind);
        Assert.False(scope.IsShared);
        Assert.Equal(GitHubRequestAuthorization.Token, scope.RequestAuthorization);
        Assert.Equal("current-user:user:oauth:abcdef0123456789:0", cacheKey);
    }

    [Fact]
    public void UserCacheKeysRejectNonUserScopes()
    {
        var scope = GitHubCachePolicy.CreateTokenScope("oauth:abcdef0123456789:0");

        Assert.Throws<ArgumentException>(() => GitHubCachePolicy.CreateUserCacheKey(scope, "current-user"));
    }

    [Theory]
    [InlineData(nameof(GitHubCacheScopeKind.Public), "public", nameof(GitHubRequestAuthorization.Token))]
    [InlineData(nameof(GitHubCacheScopeKind.Public), "token:oauth:abcdef0123456789:0", nameof(GitHubRequestAuthorization.Anonymous))]
    [InlineData(nameof(GitHubCacheScopeKind.Token), "token:oauth:abcdef0123456789:0", nameof(GitHubRequestAuthorization.Anonymous))]
    [InlineData(nameof(GitHubCacheScopeKind.Token), "public", nameof(GitHubRequestAuthorization.Token))]
    [InlineData(nameof(GitHubCacheScopeKind.User), "user:oauth:abcdef0123456789:0", nameof(GitHubRequestAuthorization.Anonymous))]
    [InlineData(nameof(GitHubCacheScopeKind.User), "token:oauth:abcdef0123456789:0", nameof(GitHubRequestAuthorization.Token))]
    public void CacheScopesRejectMismatchedKeyAndAuthorizationLanes(
        string kindName,
        string keyPrefix,
        string authorizationName)
    {
        var kind = Enum.Parse<GitHubCacheScopeKind>(kindName);
        var authorization = Enum.Parse<GitHubRequestAuthorization>(authorizationName);

        Assert.ThrowsAny<ArgumentException>(() => new GitHubCacheScope(kind, keyPrefix, authorization));
    }

    [Fact]
    public void RepositoryCacheKeysRejectDefaultScope()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            GitHubCachePolicy.CreateRepositoryCacheKey(default, MixedCaseRepository, "pulls"));
    }

    [Fact]
    public void CacheScopesRejectBlankRequiredParts()
    {
        Assert.Throws<ArgumentException>(() => GitHubCachePolicy.CreateTokenScope(" "));
    }

    [Fact]
    public void RepositoryCacheKeysNormalizeRepositoryCasing()
    {
        var scope = GitHubCachePolicy.CreateTokenScope("oauth:abcdef0123456789:0");
        var lowerCaseKey = GitHubCachePolicy.CreateRepositoryCacheKey(
            scope,
            new RepositoryName("example", "repo"),
            "timeline",
            "1");
        var mixedCaseKey = GitHubCachePolicy.CreateRepositoryCacheKey(
            scope,
            MixedCaseRepository,
            "timeline",
            "1");

        Assert.Equal(lowerCaseKey, mixedCaseKey);
    }

    [Fact]
    public void GitHubRedirectsAcceptRelativeUrls()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
        response.Headers.Location = new Uri("repositories/42", UriKind.Relative);

        Assert.True(GitHubHttpRedirects.TryGetRedirectUrl(response, out var redirectUrl));
        Assert.Equal("repositories/42", redirectUrl);
    }

    [Fact]
    public void GitHubRedirectsRejectNonGitHubAbsoluteUrls()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
        response.Headers.Location = new Uri("https://example.com/repositories/42");

        Assert.False(GitHubHttpRedirects.TryGetRedirectUrl(response, out _));
    }

    [Fact]
    public void GitHubRedirectsIgnoreResponsesWithoutLocation()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.MovedPermanently);

        Assert.False(GitHubHttpRedirects.TryGetRedirectUrl(response, out _));
    }
}
