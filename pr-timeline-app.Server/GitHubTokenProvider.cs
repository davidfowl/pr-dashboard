using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

sealed class GitHubTokenProvider
{
    internal const string OAuthTicketCacheDiscriminatorKey = "github:auth-ticket-cache-discriminator";

    private readonly SemaphoreSlim semaphore = new(1, 1);
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly IHostEnvironment environment;
    private readonly IConfiguration configuration;
    private readonly IDevelopmentGitHubCliAuth developmentGitHubCliAuth;
    private readonly ILogger<GitHubTokenProvider> logger;
    private readonly IReadOnlyDictionary<string, string> repositoryIdentities;

    // Development gh tokens cached per account login so a default identity and any per-repository
    // override identities can be resolved and reused concurrently. Key is the normalized login, or
    // the empty string for the default (no --user) account. A present key with a null value records
    // a prior failed lookup so we do not re-shell out for a known miss until the cache is reset.
    private readonly Dictionary<string, TokenResult?> cachedGitHubCliTokens = new(StringComparer.OrdinalIgnoreCase);
    private string? selectedDevelopmentGitHubUser;
    private bool suppressFallback;
    private long fallbackGeneration;

    public GitHubTokenProvider(
        IHttpContextAccessor httpContextAccessor,
        IHostEnvironment environment,
        IConfiguration configuration,
        IDevelopmentGitHubCliAuth developmentGitHubCliAuth,
        IOptions<GitHubRepositoryIdentityOptions> repositoryIdentityOptions,
        ILogger<GitHubTokenProvider>? logger = null)
    {
        this.httpContextAccessor = httpContextAccessor;
        this.environment = environment;
        this.configuration = configuration;
        this.developmentGitHubCliAuth = developmentGitHubCliAuth;
        this.logger = logger ?? NullLogger<GitHubTokenProvider>.Instance;
        repositoryIdentities = (repositoryIdentityOptions?.Value ?? new GitHubRepositoryIdentityOptions())
            .BuildNormalizedMap();
    }

    public string? LocalAuthFailureMessage { get; private set; }

    public void Logout()
    {
        selectedDevelopmentGitHubUser = null;
        suppressFallback = true;
        LocalAuthFailureMessage = "Local token fallback is disabled for this development backend after sign-out.";
        var generation = ResetFallbackCache(incrementFallbackGeneration: true);
        logger.LogInformation(
            "GitHub auth logout reset fallback token state. FallbackGeneration={FallbackGeneration}.",
            generation);
    }

    public void RecordLogin(AuthenticationProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        // A new OAuth ticket can represent a different GitHub account or org SSO state. Store
        // the discriminator on the ticket so cache rotation is per browser session, not global.
        properties.Items[OAuthTicketCacheDiscriminatorKey] = Guid.NewGuid().ToString("N");
        suppressFallback = false;
        LocalAuthFailureMessage = null;
        ResetFallbackCache(incrementFallbackGeneration: false);
        logger.LogInformation("GitHub OAuth login ticket recorded and token fallback cache reset.");
    }

    public string? GetDevelopmentGitHubUser() =>
        selectedDevelopmentGitHubUser;

    public void SetDevelopmentGitHubUser(string? user)
    {
        if (!environment.IsDevelopment())
        {
            logger.LogWarning("Rejected local GitHub account selection outside Development.");
            throw new InvalidOperationException("Selecting a local GitHub account is only supported in Development.");
        }

        selectedDevelopmentGitHubUser = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
        suppressFallback = false;
        LocalAuthFailureMessage = null;
        var generation = ResetFallbackCache(incrementFallbackGeneration: true);
        logger.LogInformation(
            "Development GitHub account selection changed. Selected={DevelopmentGitHubAccountSelected}, FallbackGeneration={FallbackGeneration}.",
            selectedDevelopmentGitHubUser is not null,
            generation);
    }

    public Task<TokenResult?> GetTokenAsync(CancellationToken cancellationToken) =>
        GetTokenAsync(repositoryName: null, cancellationToken);

    public async Task<TokenResult?> GetTokenAsync(RepositoryName? repositoryName, CancellationToken cancellationToken)
    {
        if (httpContextAccessor.HttpContext is { } context)
        {
            var result = await context.AuthenticateAsync();
            if (result?.Properties?.GetTokenValue("access_token") is { Length: > 0 } accessToken)
            {
                LocalAuthFailureMessage = null;
                logger.LogDebug(
                    "GitHub token resolved from OAuth cookie. AuthTicketDiscriminatorPresent={AuthTicketDiscriminatorPresent}.",
                    GetOAuthCacheDiscriminator(result.Properties) is not null);
                return new TokenResult(
                    accessToken,
                    "oauth",
                    GetOAuthCacheDiscriminator(result.Properties));
            }

            if (result?.Succeeded == true)
            {
                logger.LogWarning("GitHub cookie authentication succeeded but no OAuth access token was present.");
            }
            else if (result?.Failure is not null)
            {
                logger.LogWarning(
                    "GitHub cookie authentication failed while resolving token. FailureType={AuthenticationFailureType}.",
                    result.Failure.GetType().Name);
            }
        }

        if (suppressFallback)
        {
            logger.LogDebug("No GitHub token resolved because fallback token sources are suppressed.");
            return null;
        }

        if (!environment.IsDevelopment())
        {
            logger.LogDebug("No GitHub token resolved because production fallback token sources are disabled.");
            return null;
        }

        // Per-repository development identity override. When a repository is mapped to a specific gh
        // account (for example an EMU-only repo that the default identity cannot read), resolve that
        // account's token. This takes precedence over the globally selected dev account and env token
        // so the same dashboard can read different repositories with different identities at once.
        if (TryGetRepositoryIdentityLogin(repositoryName, out var overrideLogin))
        {
            var overrideToken = await GetCachedGitHubCliTokenAsync(overrideLogin, cancellationToken);
            if (overrideToken is not null)
            {
                logger.LogDebug("Resolving GitHub token from the per-repository identity override.");
                return overrideToken;
            }

            // A misconfigured or unavailable override should not silently mask the repository: log
            // loudly, then fall through to the default resolution so the repository still attempts a load.
            logger.LogWarning(
                "Per-repository GitHub identity override is configured but its gh account token was unavailable; falling back to the default identity.");
        }

        if (selectedDevelopmentGitHubUser is not null)
        {
            logger.LogDebug("Resolving GitHub token from the selected development gh account.");
            return await GetCachedGitHubCliTokenAsync(selectedDevelopmentGitHubUser, cancellationToken);
        }

        var environmentToken = configuration["GITHUB_TOKEN"]
            ?? configuration["GH_TOKEN"];
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            LocalAuthFailureMessage = null;
            logger.LogDebug("Resolving GitHub token from development environment configuration.");
            return new TokenResult(environmentToken.Trim(), "environment", GetFallbackGeneration());
        }

        logger.LogDebug("Resolving GitHub token from the default development gh account.");
        return await GetCachedGitHubCliTokenAsync(user: null, cancellationToken);
    }

    private bool TryGetRepositoryIdentityLogin(RepositoryName? repositoryName, out string login)
    {
        login = "";
        if (repositoryName is not { } repository || repositoryIdentities.Count == 0)
        {
            return false;
        }

        if (repositoryIdentities.TryGetValue(repository.ToString(), out var mappedLogin))
        {
            login = mappedLogin;
            return true;
        }

        return false;
    }

    private async Task<TokenResult?> GetCachedGitHubCliTokenAsync(string? user, CancellationToken cancellationToken)
    {
        var normalizedUser = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
        var cacheKey = normalizedUser ?? string.Empty;
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (cachedGitHubCliTokens.TryGetValue(cacheKey, out var cachedToken))
            {
                logger.LogDebug(
                    "Using cached development gh token lookup. SelectedDevelopmentAccount={DevelopmentGitHubAccountSelected}, CacheHit={DevelopmentGitHubTokenCacheHit}.",
                    normalizedUser is not null,
                    cachedToken is not null);
                return cachedToken;
            }

            var ghToken = await developmentGitHubCliAuth.GetTokenAsync(normalizedUser, cancellationToken);
            if (ghToken.Status == GitHubCliTokenStatus.Success &&
                !string.IsNullOrWhiteSpace(ghToken.Token))
            {
                LocalAuthFailureMessage = null;
                var resolved = new TokenResult(ghToken.Token.Trim(), "gh", GetFallbackGeneration());
                cachedGitHubCliTokens[cacheKey] = resolved;
                logger.LogDebug(
                    "Development gh token resolved. SelectedDevelopmentAccount={DevelopmentGitHubAccountSelected}.",
                    normalizedUser is not null);
                return resolved;
            }

            LocalAuthFailureMessage = ghToken.FailureMessage;
            LogGitHubCliFailure(ghToken, normalizedUser is not null);
            cachedGitHubCliTokens[cacheKey] = null;
            return null;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private long ResetFallbackCache(bool incrementFallbackGeneration)
    {
        semaphore.Wait();
        try
        {
            cachedGitHubCliTokens.Clear();
            return incrementFallbackGeneration
                ? Interlocked.Increment(ref fallbackGeneration)
                : Volatile.Read(ref fallbackGeneration);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private string GetFallbackGeneration() =>
        Volatile.Read(ref fallbackGeneration).ToString();

    private static string? GetOAuthCacheDiscriminator(AuthenticationProperties properties)
    {
        if (properties.Items.TryGetValue(OAuthTicketCacheDiscriminatorKey, out var discriminator)
            && !string.IsNullOrWhiteSpace(discriminator))
        {
            return discriminator;
        }

        return properties.IssuedUtc?.ToUnixTimeMilliseconds().ToString();
    }

    private void LogGitHubCliFailure(GitHubCliTokenResult result, bool selectedDevelopmentAccount)
    {
        switch (result.Status)
        {
            case GitHubCliTokenStatus.NotFound:
                logger.LogWarning(
                    "Development gh token fallback failed because {ExecutableName} was not found. SelectedDevelopmentAccount={DevelopmentGitHubAccountSelected}. If running locally with Aspire, ensure PATH/HOME are forwarded to the server resource or set GITHUB_TOKEN/GH_TOKEN before starting.",
                    result.ExecutableName ?? "gh",
                    selectedDevelopmentAccount);
                break;

            case GitHubCliTokenStatus.Failed:
                logger.LogWarning(
                    "Development gh token fallback failed with exit code {ExitCode}: {Error}. SelectedDevelopmentAccount={DevelopmentGitHubAccountSelected}.",
                    result.ExitCode,
                    result.SafeError,
                    selectedDevelopmentAccount);
                break;

            case GitHubCliTokenStatus.TimedOut:
                logger.LogWarning(
                    "Development gh token fallback timed out after 5 seconds. SelectedDevelopmentAccount={DevelopmentGitHubAccountSelected}.",
                    selectedDevelopmentAccount);
                break;
        }
    }

    public Task<string> GetCacheKeyAsync(CancellationToken cancellationToken) =>
        GetCacheKeyAsync(repositoryName: null, cancellationToken);

    public async Task<string> GetCacheKeyAsync(RepositoryName? repositoryName, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(repositoryName, cancellationToken);
        if (token is null)
        {
            logger.LogDebug(
                "GitHub auth cache key resolved as anonymous. FallbackGeneration={FallbackGeneration}.",
                GetFallbackGeneration());
            return $"anonymous:{GetFallbackGeneration()}";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token.Value));
        var discriminator = string.IsNullOrWhiteSpace(token.CacheDiscriminator)
            ? "none"
            : token.CacheDiscriminator;
        logger.LogDebug(
            "GitHub auth cache key resolved. TokenSource={GitHubTokenSource}, CacheDiscriminatorPresent={CacheDiscriminatorPresent}.",
            token.Source,
            discriminator != "none");
        return $"{token.Source}:{Convert.ToHexString(hash)[..16]}:{discriminator}";
    }
}
