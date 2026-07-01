using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

interface IDevelopmentGitHubCliAuth
{
    Task<GitHubCliTokenResult> GetTokenAsync(string? user, CancellationToken cancellationToken);

    Task<IReadOnlyList<DevelopmentGitHubAccount>> GetAccountsAsync(CancellationToken cancellationToken);
}

sealed class DevelopmentGitHubCliAuth : IDevelopmentGitHubCliAuth
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    public async Task<GitHubCliTokenResult> GetTokenAsync(string? user, CancellationToken cancellationToken)
    {
        var result = await RunGhAuthAsync(startInfo =>
        {
            startInfo.ArgumentList.Add("token");

            if (!string.IsNullOrWhiteSpace(user))
            {
                startInfo.ArgumentList.Add("--user");
                startInfo.ArgumentList.Add(user);
                startInfo.Environment.Remove("GITHUB_TOKEN");
                startInfo.Environment.Remove("GH_TOKEN");
            }
        }, cancellationToken);

        if (result.Status != GitHubCliTokenStatus.Success)
        {
            return new GitHubCliTokenResult(
                result.Status,
                ExecutableName: result.ExecutableName,
                ExitCode: result.ExitCode,
                SafeError: GitHubCliTokenResult.SanitizeError(result.SafeError));
        }

        return string.IsNullOrWhiteSpace(result.Output)
            ? GitHubCliTokenResult.Failed(0, "gh auth token returned an empty token.")
            : GitHubCliTokenResult.Success(result.Output);
    }

    public async Task<IReadOnlyList<DevelopmentGitHubAccount>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        var result = await RunGhAuthAsync(startInfo =>
        {
            startInfo.ArgumentList.Add("status");
            startInfo.ArgumentList.Add("--json");
            startInfo.ArgumentList.Add("hosts");
        }, cancellationToken);

        return ParseAccounts(result.Output);
    }

    internal static IReadOnlyList<DevelopmentGitHubAccount> ParseAccounts(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("hosts", out var hosts) ||
                !hosts.TryGetProperty("github.com", out var githubAccounts) ||
                githubAccounts.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var accounts = new Dictionary<string, DevelopmentGitHubAccount>(StringComparer.OrdinalIgnoreCase);
            foreach (var account in githubAccounts.EnumerateArray())
            {
                var login = GetJsonString(account, "login");
                var state = GetJsonString(account, "state");
                var tokenSource = GetJsonString(account, "tokenSource");
                if (string.IsNullOrWhiteSpace(login) ||
                    !string.Equals(state, "success", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(tokenSource, "keyring", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var active = account.TryGetProperty("active", out var activeElement) &&
                    activeElement.ValueKind is JsonValueKind.True;
                accounts.TryAdd(login, new DevelopmentGitHubAccount(login, active));
            }

            return accounts.Values
                .OrderByDescending(account => account.Active)
                .ThenBy(account => account.Login, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private static string? GetJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static async Task<GitHubCliCommandResult> RunGhAuthAsync(
        Action<ProcessStartInfo> configureAuthCommand,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("auth");
        configureAuthCommand(startInfo);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            return GitHubCliCommandResult.NotFound("gh", ex.Message);
        }

        using (process)
        {
            if (process is null)
            {
                return GitHubCliCommandResult.NotFound("gh");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(CommandTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await ObserveProcessOutputTaskAsync(outputTask);
                await ObserveProcessOutputTaskAsync(errorTask);
                return GitHubCliCommandResult.TimedOut();
            }

            var output = await outputTask;
            var error = await errorTask;

            return process.ExitCode == 0
                ? GitHubCliCommandResult.Success(output)
                : GitHubCliCommandResult.Failed(process.ExitCode, error);
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
}

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

    internal static string SanitizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "no stderr output";
        }

        var sanitized = error.ReplaceLineEndings(" ").Trim();
        return sanitized.Length <= 300 ? sanitized : $"{sanitized[..300]}...";
    }
}

sealed record GitHubCliCommandResult(
    GitHubCliTokenStatus Status,
    string? Output = null,
    string? ExecutableName = null,
    int? ExitCode = null,
    string? SafeError = null)
{
    public static GitHubCliCommandResult Success(string output) =>
        new(GitHubCliTokenStatus.Success, Output: output);

    public static GitHubCliCommandResult NotFound(string executableName, string? error = null) =>
        new(GitHubCliTokenStatus.NotFound, ExecutableName: executableName, SafeError: error);

    public static GitHubCliCommandResult Failed(int exitCode, string? error) =>
        new(GitHubCliTokenStatus.Failed, ExitCode: exitCode, SafeError: error);

    public static GitHubCliCommandResult TimedOut() =>
        new(GitHubCliTokenStatus.TimedOut);
}
