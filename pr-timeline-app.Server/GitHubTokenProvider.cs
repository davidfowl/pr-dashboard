using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

sealed class GitHubTokenProvider
{
    internal const string OAuthTicketCacheDiscriminatorKey = "github:auth-ticket-cache-discriminator";

    private readonly SemaphoreSlim semaphore = new(1, 1);
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly IHostEnvironment environment;
    private readonly IConfiguration configuration;
    private readonly IDevelopmentGitHubCliAuth developmentGitHubCliAuth;
    private readonly ILogger<GitHubTokenProvider> logger;
    private TokenResult? cachedGitHubCliToken;
    private string? cachedGitHubCliUser;
    private string? attemptedGitHubCliUser;
    private string? selectedDevelopmentGitHubUser;
    private bool attemptedGitHubCli;
    private bool suppressFallback;
    private long fallbackGeneration;

    public GitHubTokenProvider(
        IHttpContextAccessor httpContextAccessor,
        IHostEnvironment environment,
        IConfiguration configuration,
        IDevelopmentGitHubCliAuth developmentGitHubCliAuth,
        ILogger<GitHubTokenProvider>? logger = null)
    {
        this.httpContextAccessor = httpContextAccessor;
        this.environment = environment;
        this.configuration = configuration;
        this.developmentGitHubCliAuth = developmentGitHubCliAuth;
        this.logger = logger ?? NullLogger<GitHubTokenProvider>.Instance;
    }

    public void Logout()
    {
        selectedDevelopmentGitHubUser = null;
        suppressFallback = true;
        ResetFallbackCache();
        var generation = Interlocked.Increment(ref fallbackGeneration);
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
        ResetFallbackCache();
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
        ResetFallbackCache();
        var generation = Interlocked.Increment(ref fallbackGeneration);
        logger.LogInformation(
            "Development GitHub account selection changed. Selected={DevelopmentGitHubAccountSelected}, FallbackGeneration={FallbackGeneration}.",
            selectedDevelopmentGitHubUser is not null,
            generation);
    }

    public async Task<TokenResult?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (httpContextAccessor.HttpContext is { } context)
        {
            var result = await context.AuthenticateAsync();
            if (result?.Properties?.GetTokenValue("access_token") is { Length: > 0 } accessToken)
            {
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

        if (selectedDevelopmentGitHubUser is not null)
        {
            logger.LogDebug("Resolving GitHub token from the selected development gh account.");
            return await GetCachedGitHubCliTokenAsync(selectedDevelopmentGitHubUser, cancellationToken);
        }

        var environmentToken = configuration["GITHUB_TOKEN"]
            ?? configuration["GH_TOKEN"];
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            logger.LogDebug("Resolving GitHub token from development environment configuration.");
            return new TokenResult(environmentToken.Trim(), "environment", GetFallbackGeneration());
        }

        logger.LogDebug("Resolving GitHub token from the default development gh account.");
        return await GetCachedGitHubCliTokenAsync(user: null, cancellationToken);
    }

    private async Task<TokenResult?> GetCachedGitHubCliTokenAsync(string? user, CancellationToken cancellationToken)
    {
        var normalizedUser = string.IsNullOrWhiteSpace(user) ? null : user.Trim();
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (cachedGitHubCliToken is not null &&
                string.Equals(cachedGitHubCliUser, normalizedUser, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug(
                    "Using cached development gh token. SelectedDevelopmentAccount={DevelopmentGitHubAccountSelected}.",
                    normalizedUser is not null);
                return cachedGitHubCliToken;
            }

            if (attemptedGitHubCli &&
                string.Equals(attemptedGitHubCliUser, normalizedUser, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug(
                    "Skipping development gh token lookup after prior miss. SelectedDevelopmentAccount={DevelopmentGitHubAccountSelected}.",
                    normalizedUser is not null);
                return null;
            }

            attemptedGitHubCli = true;
            attemptedGitHubCliUser = normalizedUser;
            var ghToken = await developmentGitHubCliAuth.GetTokenAsync(normalizedUser, cancellationToken);
            if (!string.IsNullOrWhiteSpace(ghToken))
            {
                cachedGitHubCliToken = new TokenResult(ghToken.Trim(), "gh", GetFallbackGeneration());
                cachedGitHubCliUser = normalizedUser;
                logger.LogDebug(
                    "Development gh token resolved. SelectedDevelopmentAccount={DevelopmentGitHubAccountSelected}.",
                    normalizedUser is not null);
                return cachedGitHubCliToken;
            }

            logger.LogDebug(
                "Development gh token was unavailable. SelectedDevelopmentAccount={DevelopmentGitHubAccountSelected}.",
                normalizedUser is not null);
            return null;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void ResetFallbackCache()
    {
        cachedGitHubCliToken = null;
        cachedGitHubCliUser = null;
        attemptedGitHubCliUser = null;
        attemptedGitHubCli = false;
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

    public async Task<string> GetCacheKeyAsync(CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
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
