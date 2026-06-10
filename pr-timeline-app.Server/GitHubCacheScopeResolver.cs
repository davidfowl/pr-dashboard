using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

enum GitHubPublicCacheRepositoryEligibility
{
    Public,
    NotAllowlisted,
    MissingPublicCacheIdentity,
    NotPublic,
    Unverified
}

sealed class GitHubCacheScopeResolver(
    HttpClient httpClient,
    GitHubTokenProvider tokenProvider,
    GitHubPublicCacheIdentity publicCacheIdentity,
    GitHubPublicCacheStore publicCacheStore,
    IOptions<GitHubCacheWarmupOptions> options,
    IMemoryCache cache)
{
    internal static readonly TimeSpan PublicVisibilityCacheDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan UnknownVisibilityCacheDuration = TimeSpan.FromMinutes(2);
    private HashSet<string>? normalizedPublicCacheAllowlist;

    public async Task<GitHubCacheScope> GetRepositoryScopeAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken)
    {
        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        if (authCacheKey.StartsWith("anonymous:", StringComparison.Ordinal)
            && IsPublicCacheAllowlisted(repositoryName))
        {
            var eligibility = await GetPublicCacheRepositoryEligibilityOrUnverifiedAsync(repositoryName, cancellationToken);
            if (eligibility == GitHubPublicCacheRepositoryEligibility.Public)
            {
                return GitHubCachePolicy.CreatePublicRepositoryScope();
            }

            if (eligibility == GitHubPublicCacheRepositoryEligibility.NotPublic)
            {
                publicCacheStore.RemoveRepository(repositoryName);
            }

            if (eligibility == GitHubPublicCacheRepositoryEligibility.Unverified
                && publicCacheStore.HasTrackedSnapshot(repositoryName))
            {
                return GitHubCachePolicy.CreatePublicRepositoryScope();
            }
        }

        return GitHubCachePolicy.CreateTokenScope(authCacheKey);
    }

    public async Task<GitHubPublicCacheRepositoryEligibility> GetPublicCacheRepositoryEligibilityOrUnverifiedAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetPublicCacheRepositoryEligibilityAsync(
                repositoryName,
                forceRefresh: false,
                cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsTransientEligibilityFailure(ex))
        {
            return GitHubPublicCacheRepositoryEligibility.Unverified;
        }
    }

    public bool IsPublicCacheAllowlisted(RepositoryName repositoryName) =>
        GetNormalizedPublicCacheAllowlist().Contains(GitHubCachePolicy.NormalizeRepositoryName(repositoryName));

    private HashSet<string> GetNormalizedPublicCacheAllowlist()
    {
        if (normalizedPublicCacheAllowlist is { } allowlist)
        {
            return allowlist;
        }

        allowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in options.Value.Repositories)
        {
            if (RepositoryName.TryParse(repository, out var parsedRepository))
            {
                allowlist.Add(GitHubCachePolicy.NormalizeRepositoryName(parsedRepository));
            }
        }

        normalizedPublicCacheAllowlist = allowlist;
        return allowlist;
    }

    public async Task<GitHubPublicCacheRepositoryEligibility> GetPublicCacheRepositoryEligibilityAsync(
        RepositoryName repositoryName,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!IsPublicCacheAllowlisted(repositoryName))
        {
            return GitHubPublicCacheRepositoryEligibility.NotAllowlisted;
        }

        if (publicCacheIdentity.GetToken() is null)
        {
            return GitHubPublicCacheRepositoryEligibility.MissingPublicCacheIdentity;
        }

        var result = await GetRepositoryVisibilityResultAsync(repositoryName, forceRefresh, cancellationToken);
        return result.Visibility switch
        {
            GitHubRepositoryVisibility.Public => GitHubPublicCacheRepositoryEligibility.Public,
            GitHubRepositoryVisibility.Private or GitHubRepositoryVisibility.Internal => GitHubPublicCacheRepositoryEligibility.NotPublic,
            GitHubRepositoryVisibility.Unknown when result.CacheDuration is null => GitHubPublicCacheRepositoryEligibility.Unverified,
            _ => GitHubPublicCacheRepositoryEligibility.NotPublic
        };
    }

    private async Task<RepositoryVisibilityProbeResult> GetRepositoryVisibilityResultAsync(
        RepositoryName repositoryName,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"repo-visibility:{GitHubCachePolicy.NormalizeRepositoryName(repositoryName)}";
        if (!forceRefresh
            && cache.TryGetValue(cacheKey, out GitHubRepositoryVisibility cachedVisibility))
        {
            return new RepositoryVisibilityProbeResult(cachedVisibility, UnknownVisibilityCacheDuration);
        }

        var result = await ProbeRepositoryVisibilityAsync(repositoryName, cancellationToken);
        if (result.CacheDuration is { } cacheDuration)
        {
            cache.Set(cacheKey, result.Visibility, CreateVisibilityCacheOptions(cacheDuration));
        }

        return result;
    }

    private async Task<RepositoryVisibilityProbeResult> ProbeRepositoryVisibilityAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}";
            var token = publicCacheIdentity.GetToken()
                ?? throw new GitHubApiException(
                    HttpStatusCode.ServiceUnavailable,
                    "GitHub public cache refresh is not configured with a server token.");
            for (var redirectCount = 0; redirectCount <= GitHubHttpRedirects.MaxRedirects; redirectCount++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
                using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (GitHubHttpRedirects.TryGetRedirectUrl(response, out var redirectUrl))
                {
                    url = redirectUrl;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return UnknownVisibility(cacheable: true);
                    }

                    throw new GitHubApiException(
                        response.StatusCode,
                        $"GitHub API returned {(int)response.StatusCode} while verifying public cache repository visibility.");
                }

                var repository = await response.Content.ReadFromJsonAsync(
                    GitHubJsonSerializerContext.Default.GitHubRepositoryDto,
                    cancellationToken);
                var visibility = GitHubCachePolicy.ClassifyRepositoryVisibility(repository?.Visibility);
                return new RepositoryVisibilityProbeResult(
                    visibility,
                    visibility == GitHubRepositoryVisibility.Public
                        ? PublicVisibilityCacheDuration
                        : UnknownVisibilityCacheDuration);
            }

            return UnknownVisibility(cacheable: false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested
            && IsProbeFailure(ex))
        {
            return UnknownVisibility(cacheable: false);
        }
    }

    private static RepositoryVisibilityProbeResult UnknownVisibility(bool cacheable) =>
        new(
            GitHubRepositoryVisibility.Unknown,
            cacheable ? UnknownVisibilityCacheDuration : null);

    private static MemoryCacheEntryOptions CreateVisibilityCacheOptions(TimeSpan cacheDuration)
    {
        var options = new MemoryCacheEntryOptions();
        if (cacheDuration == Timeout.InfiniteTimeSpan)
        {
            options.Priority = CacheItemPriority.NeverRemove;
        }
        else
        {
            options.AbsoluteExpirationRelativeToNow = cacheDuration;
        }

        return options;
    }

    private static bool IsProbeFailure(Exception exception) =>
        exception is HttpRequestException
            or TimeoutException
            or OperationCanceledException
            or System.Text.Json.JsonException;

    private static bool IsTransientEligibilityFailure(Exception exception) =>
        exception is GitHubApiException ex
            && ex.StatusCode is (HttpStatusCode.Forbidden
                or HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout);

    private readonly record struct RepositoryVisibilityProbeResult(
        GitHubRepositoryVisibility Visibility,
        TimeSpan? CacheDuration);
}
