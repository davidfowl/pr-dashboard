using System.Net;
using Microsoft.Extensions.Options;

sealed class GitHubPublicCacheWarmupService(
    IServiceScopeFactory scopeFactory,
    IOptions<GitHubCacheWarmupOptions> options,
    IHostEnvironment environment,
    ILogger<GitHubPublicCacheWarmupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        => await ExecuteWarmupAsync(stoppingToken);

    internal async Task ExecuteWarmupAsync(CancellationToken stoppingToken)
    {
        var warmupOptions = options.Value;
        if (!warmupOptions.Enabled
            || environment.IsDevelopment() && !warmupOptions.EnabledInDevelopment)
        {
            return;
        }

        var state = NormalizeState(warmupOptions.State);
        foreach (var repository in warmupOptions.Repositories)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (!RepositoryName.TryParse(repository, out var repositoryName))
            {
                logger.LogWarning("Skipping cache warmup for invalid repository '{Repository}'.", repository);
                continue;
            }

            try
            {
                using var scope = scopeFactory.CreateScope();
                var gitHub = scope.ServiceProvider.GetRequiredService<GitHubClient>();
                var warmed = await gitHub.TryPrewarmPublicPullRequestsAsync(repositoryName, state, stoppingToken);
                if (warmed)
                {
                    logger.LogInformation("Warmed public GitHub cache for {Repository}.", repositoryName);
                }
                else
                {
                    logger.LogInformation("Skipped GitHub cache warmup for {Repository} because it is not public.", repositoryName);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (IsGitHubRateLimit(ex))
                {
                    logger.LogInformation(
                        ex,
                        "Stopping GitHub cache warmup after hitting rate limits while warming {Repository}.",
                        repositoryName);
                    return;
                }

                logger.LogWarning(ex, "GitHub cache warmup failed for {Repository}.", repositoryName);
            }
        }
    }

    private static string NormalizeState(string? state) =>
        string.IsNullOrWhiteSpace(state) || state.Trim().ToLowerInvariant() is not ("open" or "closed" or "all")
            ? "open"
            : state.Trim().ToLowerInvariant();

    private static bool IsGitHubRateLimit(Exception exception) =>
        exception is GitHubApiException ex
        && (ex.StatusCode == HttpStatusCode.TooManyRequests
            || ex.StatusCode == HttpStatusCode.Forbidden
            && (ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("secondary rate", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("abuse detection", StringComparison.OrdinalIgnoreCase)));
}
