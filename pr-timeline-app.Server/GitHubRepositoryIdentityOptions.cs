using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Maps repositories to a specific development GitHub account (a <c>gh</c> keyring login) so the
/// dashboard can read repositories that the default identity cannot see. Repositories without an
/// override keep using the default identity resolution.
/// </summary>
/// <remarks>
/// This is a development-only mechanism: the identities are local <c>gh auth</c> accounts, and the
/// mapping is inherently per-developer (each contributor's EMU alias differs), so configure it via
/// user-secrets or environment rather than committing it to shared appsettings. Example:
/// <c>dotnet user-secrets set "GitHubRepositoryIdentities:Repositories:devdiv-microsoft/aspire-1p" "your-emu-login"</c>.
/// </remarks>
sealed class GitHubRepositoryIdentityOptions
{
    public const string SectionName = "GitHubRepositoryIdentities";

    /// <summary>
    /// Maps an <c>owner/repo</c> repository name to the <c>gh</c> account login used for that
    /// repository. Keys are compared case-insensitively.
    /// </summary>
    public Dictionary<string, string> Repositories { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds a normalized (case-insensitive, trimmed, blank-filtered) lookup of the configured
    /// repository identities.
    /// </summary>
    public IReadOnlyDictionary<string, string> BuildNormalizedMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (repository, login) in Repositories)
        {
            if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(login))
            {
                continue;
            }

            map[repository.Trim()] = login.Trim();
        }

        return map;
    }
}
