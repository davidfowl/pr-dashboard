using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;

sealed class GitHubCacheScopeResolver(HttpClient httpClient, GitHubTokenProvider tokenProvider, IMemoryCache cache)
{
    internal static readonly TimeSpan PublicVisibilityCacheDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan UnknownVisibilityCacheDuration = TimeSpan.FromMinutes(2);

    public async Task<GitHubCacheScope> GetRepositoryScopeAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken)
    {
        var visibility = await GetRepositoryVisibilityAsync(repositoryName, cancellationToken);
        if (visibility == GitHubRepositoryVisibility.Public)
        {
            return GitHubCachePolicy.CreatePublicRepositoryScope();
        }

        var authCacheKey = await tokenProvider.GetCacheKeyAsync(cancellationToken);
        return GitHubCachePolicy.CreateTokenScope(authCacheKey);
    }

    public async Task<GitHubRepositoryVisibility> GetRepositoryVisibilityAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"repo-visibility:{GitHubCachePolicy.NormalizeRepositoryName(repositoryName)}";
        if (cache.TryGetValue(cacheKey, out GitHubRepositoryVisibility cachedVisibility))
        {
            return cachedVisibility;
        }

        var result = await ProbeRepositoryVisibilityAsync(repositoryName, cancellationToken);
        if (result.CacheDuration is { } cacheDuration)
        {
            cache.Set(cacheKey, result.Visibility, CreateVisibilityCacheOptions(cacheDuration));
        }

        return result.Visibility;
    }

    private async Task<RepositoryVisibilityProbeResult> ProbeRepositoryVisibilityAsync(
        RepositoryName repositoryName,
        CancellationToken cancellationToken)
    {
        try
        {
            var url = $"repos/{repositoryName.Owner}/{repositoryName.Name}";
            for (var redirectCount = 0; redirectCount <= GitHubHttpRedirects.MaxRedirects; redirectCount++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
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
                    return response.StatusCode == HttpStatusCode.NotFound
                        ? UnknownVisibility(cacheable: true)
                        : UnknownVisibility(cacheable: false);
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

    private readonly record struct RepositoryVisibilityProbeResult(
        GitHubRepositoryVisibility Visibility,
        TimeSpan? CacheDuration);
}
