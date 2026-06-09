enum GitHubRepositoryVisibility
{
    Unknown,
    Public,
    Private,
    Internal
}

enum GitHubCacheScopeKind
{
    User,
    Token,
    Public
}

enum GitHubRequestAuthorization
{
    Token,
    Anonymous
}

readonly record struct GitHubCacheScope
{
    public GitHubCacheScope(
        GitHubCacheScopeKind kind,
        string keyPrefix,
        GitHubRequestAuthorization requestAuthorization)
    {
        Kind = kind;
        KeyPrefix = RequireKeyPrefix(keyPrefix);
        RequestAuthorization = requestAuthorization;
        EnsureValid();
    }

    public GitHubCacheScopeKind Kind { get; }

    public string KeyPrefix { get; }

    public GitHubRequestAuthorization RequestAuthorization { get; }

    public bool IsShared => Kind == GitHubCacheScopeKind.Public;

    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(KeyPrefix))
        {
            throw new ArgumentException("Cache scope key prefix cannot be blank.");
        }

        if (IsShared)
        {
            if (RequestAuthorization != GitHubRequestAuthorization.Anonymous)
            {
                throw new ArgumentException("Public cache scopes must use anonymous GitHub requests.");
            }

            if (!KeyPrefix.Equals("public", StringComparison.Ordinal))
            {
                throw new ArgumentException("Public cache scopes must use the public key prefix.");
            }

            return;
        }

        if (RequestAuthorization != GitHubRequestAuthorization.Token)
        {
            throw new ArgumentException("User and token cache scopes must use token-authorized GitHub requests.");
        }

        var expectedPrefix = Kind switch
        {
            GitHubCacheScopeKind.User => "user:",
            GitHubCacheScopeKind.Token => "token:",
            _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unknown GitHub cache scope kind.")
        };

        if (!KeyPrefix.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Cache scope kind {Kind} must use the {expectedPrefix} key prefix.");
        }
    }

    private static string RequireKeyPrefix(string keyPrefix)
    {
        if (string.IsNullOrWhiteSpace(keyPrefix))
        {
            throw new ArgumentException("Cache scope key prefix cannot be blank.", nameof(keyPrefix));
        }

        return keyPrefix.Trim();
    }
}

static class GitHubCachePolicy
{
    public static GitHubCacheScope CreateUserScope(string authCacheKey) =>
        new(
            GitHubCacheScopeKind.User,
            $"user:{RequireCachePart(authCacheKey, nameof(authCacheKey))}",
            GitHubRequestAuthorization.Token);

    public static GitHubCacheScope CreateTokenScope(string authCacheKey) =>
        new(
            GitHubCacheScopeKind.Token,
            $"token:{RequireCachePart(authCacheKey, nameof(authCacheKey))}",
            GitHubRequestAuthorization.Token);

    public static GitHubCacheScope CreatePublicRepositoryScope() =>
        new(
            GitHubCacheScopeKind.Public,
            "public",
            GitHubRequestAuthorization.Anonymous);

    public static GitHubCacheScope CreateRepositoryScope(
        string authCacheKey,
        GitHubRepositoryVisibility visibility) =>
        visibility == GitHubRepositoryVisibility.Public
            ? CreatePublicRepositoryScope()
            : CreateTokenScope(authCacheKey);

    public static GitHubRepositoryVisibility ClassifyRepositoryVisibility(string? visibility) =>
        visibility?.Trim().ToLowerInvariant() switch
        {
            "public" => GitHubRepositoryVisibility.Public,
            "private" => GitHubRepositoryVisibility.Private,
            "internal" => GitHubRepositoryVisibility.Internal,
            _ => GitHubRepositoryVisibility.Unknown
        };

    public static string CreateUserCacheKey(
        GitHubCacheScope scope,
        string resourceName,
        params string?[] parts)
    {
        if (scope.Kind != GitHubCacheScopeKind.User)
        {
            throw new ArgumentException("User cache keys require a user cache scope.", nameof(scope));
        }

        return CreateCacheKey(scope, resourceName, parts);
    }

    public static string CreateRepositoryCacheKey(
        GitHubCacheScope scope,
        RepositoryName repositoryName,
        string resourceName,
        params string?[] parts) =>
        CreateCacheKey(
            scope,
            resourceName,
            [NormalizeRepositoryName(repositoryName), .. parts]);

    public static string NormalizeRepositoryName(RepositoryName repositoryName) =>
        $"{repositoryName.Owner.ToLowerInvariant()}/{repositoryName.Name.ToLowerInvariant()}";

    private static string CreateCacheKey(
        GitHubCacheScope scope,
        string resourceName,
        params string?[] parts)
    {
        scope.EnsureValid();
        var keyParts = new List<string>
        {
            RequireCachePart(resourceName, nameof(resourceName)),
            RequireCachePart(scope.KeyPrefix, nameof(scope))
        };

        foreach (var part in parts)
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                keyParts.Add(part.Trim());
            }
        }

        return string.Join(':', keyParts);
    }

    private static string RequireCachePart(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Cache key parts cannot be blank.", parameterName);
        }

        return value.Trim();
    }
}
