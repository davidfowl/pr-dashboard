using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;

sealed class GitHubTokenProvider(IHttpContextAccessor httpContextAccessor, IHostEnvironment environment)
{
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private TokenResult? cachedGitHubCliToken;
    private bool attemptedGitHubCli;
    private bool suppressFallback;

    public long AuthGeneration { get; private set; }

    public void Logout()
    {
        suppressFallback = true;
        AuthGeneration++;
    }

    public async Task<TokenResult?> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (httpContextAccessor.HttpContext is { } context
            && await context.GetTokenAsync("access_token") is { Length: > 0 } accessToken)
        {
            return new TokenResult(accessToken, "oauth");
        }

        if (suppressFallback || !environment.IsDevelopment())
        {
            return null;
        }

        var environmentToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
            ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            return new TokenResult(environmentToken.Trim(), "environment");
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
            var ghToken = await GetGitHubCliTokenAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(ghToken))
            {
                cachedGitHubCliToken = new TokenResult(ghToken.Trim(), "gh");
                return cachedGitHubCliToken;
            }

            return null;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<string?> GetGitHubCliTokenAsync(CancellationToken cancellationToken)
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
        catch (Win32Exception)
        {
            return null;
        }

        using (process)
        {
            if (process is null)
            {
                return null;
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            return await process.StandardOutput.ReadToEndAsync(cancellationToken);
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
