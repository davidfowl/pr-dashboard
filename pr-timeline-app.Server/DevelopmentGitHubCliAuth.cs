using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

interface IDevelopmentGitHubCliAuth
{
    Task<string?> GetTokenAsync(string? user, CancellationToken cancellationToken);

    Task<IReadOnlyList<DevelopmentGitHubAccount>> GetAccountsAsync(CancellationToken cancellationToken);
}

sealed class DevelopmentGitHubCliAuth : IDevelopmentGitHubCliAuth
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    public async Task<string?> GetTokenAsync(string? user, CancellationToken cancellationToken)
    {
        var output = await RunGhAuthAsync(startInfo =>
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

        return string.IsNullOrWhiteSpace(output) ? null : output.Trim();
    }

    public async Task<IReadOnlyList<DevelopmentGitHubAccount>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        var output = await RunGhAuthAsync(startInfo =>
        {
            startInfo.ArgumentList.Add("status");
            startInfo.ArgumentList.Add("--json");
            startInfo.ArgumentList.Add("hosts");
        }, cancellationToken);

        return ParseAccounts(output);
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

    private static async Task<string?> RunGhAuthAsync(
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

                return null;
            }

            var output = await outputTask;
            await errorTask;

            return process.ExitCode == 0 ? output : null;
        }
    }
}
