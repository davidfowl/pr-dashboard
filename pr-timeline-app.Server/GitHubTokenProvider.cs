using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging.Abstractions;

sealed class GitHubTokenProvider
{
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private readonly IHttpContextAccessor httpContextAccessor;
    private readonly IHostEnvironment environment;
    private readonly ILogger<GitHubTokenProvider> logger;
    private readonly Func<EnvironmentTokenResult?> getEnvironmentToken;
    private readonly Func<CancellationToken, Task<GitHubCliTokenResult>> getGitHubCliTokenAsync;
    private TokenResult? cachedGitHubCliToken;
    private bool attemptedGitHubCli;
    private bool suppressFallback;

    public GitHubTokenProvider(
        IHttpContextAccessor httpContextAccessor,
        IHostEnvironment environment,
        ILogger<GitHubTokenProvider>? logger = null)
        : this(
            httpContextAccessor,
            environment,
            logger ?? NullLogger<GitHubTokenProvider>.Instance,
            GetEnvironmentToken,
            GetGitHubCliTokenAsync)
    {
    }

    internal GitHubTokenProvider(
        IHttpContextAccessor httpContextAccessor,
        IHostEnvironment environment,
        ILogger<GitHubTokenProvider> logger,
        Func<EnvironmentTokenResult?> getEnvironmentToken,
        Func<CancellationToken, Task<GitHubCliTokenResult>> getGitHubCliTokenAsync)
    {
        this.httpContextAccessor = httpContextAccessor;
        this.environment = environment;
        this.logger = logger;
        this.getEnvironmentToken = getEnvironmentToken;
        this.getGitHubCliTokenAsync = getGitHubCliTokenAsync;
    }

    public long AuthGeneration { get; private set; }

    public string? LocalAuthFailureMessage { get; private set; }

    public void Logout()
    {
        suppressFallback = true;
        LocalAuthFailureMessage = "Local token fallback is disabled for this browser session after sign-out.";
        AuthGeneration++;
    }

    public async Task<TokenResult?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (httpContextAccessor.HttpContext is { } context
            && await context.GetTokenAsync("access_token") is { Length: > 0 } accessToken)
        {
            LocalAuthFailureMessage = null;
            return new TokenResult(accessToken, "oauth");
        }

        if (suppressFallback || !environment.IsDevelopment())
        {
            return null;
        }

        var environmentToken = getEnvironmentToken();
        if (environmentToken is not null)
        {
            LocalAuthFailureMessage = null;
            logger.LogInformation(
                "Using GitHub token from {VariableName} for local backend GitHub API calls.",
                environmentToken.VariableName);
            return new TokenResult(environmentToken.Value, "environment");
        }

        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (cachedGitHubCliToken is not null)
            {
                return cachedGitHubCliToken;
            }

            if (attemptedGitHubCli)
            {
                return null;
            }

            attemptedGitHubCli = true;
            var ghToken = await getGitHubCliTokenAsync(cancellationToken);
            if (ghToken.Status == GitHubCliTokenStatus.Success
                && !string.IsNullOrWhiteSpace(ghToken.Token))
            {
                LocalAuthFailureMessage = null;
                logger.LogInformation("Using GitHub CLI token for local backend GitHub API calls.");
                cachedGitHubCliToken = new TokenResult(ghToken.Token.Trim(), "gh");
                return cachedGitHubCliToken;
            }

            LocalAuthFailureMessage = ghToken.FailureMessage;
            LogGitHubCliFailure(ghToken);
            return null;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static EnvironmentTokenResult? GetEnvironmentToken()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { } githubToken
            && !string.IsNullOrWhiteSpace(githubToken))
        {
            return new EnvironmentTokenResult(githubToken.Trim(), "GITHUB_TOKEN");
        }

        if (Environment.GetEnvironmentVariable("GH_TOKEN") is { } ghToken
            && !string.IsNullOrWhiteSpace(ghToken))
        {
            return new EnvironmentTokenResult(ghToken.Trim(), "GH_TOKEN");
        }

        return null;
    }

    private static async Task<GitHubCliTokenResult> GetGitHubCliTokenAsync(CancellationToken cancellationToken)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = "gh",
                ArgumentList = { "auth", "token" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
        }
        catch (Win32Exception ex)
        {
            return GitHubCliTokenResult.NotFound("gh", ex.Message);
        }

        using (process)
        {
            if (process is null)
            {
                return GitHubCliTokenResult.NotFound("gh");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                await ObserveProcessOutputTaskAsync(stdoutTask);
                await ObserveProcessOutputTaskAsync(stderrTask);
                return GitHubCliTokenResult.TimedOut();
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                return GitHubCliTokenResult.Failed(process.ExitCode, stderr);
            }

            return string.IsNullOrWhiteSpace(stdout)
                ? GitHubCliTokenResult.Failed(process.ExitCode, "gh auth token returned an empty token.")
                : GitHubCliTokenResult.Success(stdout);
        }
    }

    private static async Task ObserveProcessOutputTaskAsync(Task<string> outputTask)
    {
        try
        {
            _ = await outputTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void LogGitHubCliFailure(GitHubCliTokenResult result)
    {
        switch (result.Status)
        {
            case GitHubCliTokenStatus.NotFound:
                logger.LogWarning(
                    "GitHub CLI token fallback failed because {ExecutableName} was not found. If running locally with Aspire, ensure PATH/HOME are forwarded to the server resource or set GITHUB_TOKEN/GH_TOKEN before starting.",
                    result.ExecutableName ?? "gh");
                break;

            case GitHubCliTokenStatus.Failed:
                logger.LogWarning(
                    "GitHub CLI token fallback failed with exit code {ExitCode}: {Error}",
                    result.ExitCode,
                    result.SafeError);
                break;

            case GitHubCliTokenStatus.TimedOut:
                logger.LogWarning("GitHub CLI token fallback timed out after 5 seconds.");
                break;
        }
    }

    public async Task<string> GetCacheKeyAsync(CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync(cancellationToken);
        if (token is null)
        {
            return $"anonymous:{AuthGeneration}";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token.Value));
        return $"{token.Source}:{Convert.ToHexString(hash)[..16]}:{AuthGeneration}";
    }
}

internal sealed record EnvironmentTokenResult(string Value, string VariableName);

internal enum GitHubCliTokenStatus
{
    Success,
    NotFound,
    Failed,
    TimedOut
}

internal sealed record GitHubCliTokenResult(
    GitHubCliTokenStatus Status,
    string? Token = null,
    string? ExecutableName = null,
    int? ExitCode = null,
    string? SafeError = null)
{
    public string FailureMessage => Status switch
    {
        GitHubCliTokenStatus.NotFound => $"`{ExecutableName ?? "gh"}` was not found on PATH.",
        GitHubCliTokenStatus.Failed => $"`gh auth token` exited with code {ExitCode}: {SafeError}",
        GitHubCliTokenStatus.TimedOut => "`gh auth token` timed out after 5 seconds.",
        _ => ""
    };

    public static GitHubCliTokenResult Success(string token) =>
        new(GitHubCliTokenStatus.Success, Token: token);

    public static GitHubCliTokenResult NotFound(string executableName, string? error = null) =>
        new(GitHubCliTokenStatus.NotFound, ExecutableName: executableName, SafeError: SanitizeError(error));

    public static GitHubCliTokenResult Failed(int exitCode, string? error) =>
        new(GitHubCliTokenStatus.Failed, ExitCode: exitCode, SafeError: SanitizeError(error));

    public static GitHubCliTokenResult TimedOut() =>
        new(GitHubCliTokenStatus.TimedOut);

    private static string SanitizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "no stderr output";
        }

        var sanitized = error.ReplaceLineEndings(" ").Trim();
        return sanitized.Length <= 300 ? sanitized : $"{sanitized[..300]}...";
    }
}
