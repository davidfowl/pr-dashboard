using Microsoft.Extensions.Options;

/// <summary>
/// Normalized, queryable view of <see cref="TeamIdentityOptions"/>. Serves the two concerns that
/// depend on team identities: routing a repository to the login that can read it (token routing) and
/// folding a person's multiple logins into one identity (attribution/grouping).
/// </summary>
sealed class TeamIdentityMap
{
    public static readonly TeamIdentityMap Empty = new(new TeamIdentityOptions());

    private readonly Dictionary<string, string> repositoryKinds;
    private readonly Dictionary<string, Dictionary<string, string>> memberKindLogins;
    private readonly Dictionary<string, string> aliasToPrimary;

    public TeamIdentityMap(IOptions<TeamIdentityOptions> options)
        : this(options.Value)
    {
    }

    public TeamIdentityMap(TeamIdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        DefaultKind = string.IsNullOrWhiteSpace(options.DefaultKind)
            ? TeamIdentityOptions.PublicKind
            : options.DefaultKind.Trim();

        repositoryKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (repository, kind) in options.RepositoryKinds)
        {
            if (!string.IsNullOrWhiteSpace(repository) && !string.IsNullOrWhiteSpace(kind))
            {
                repositoryKinds[repository.Trim()] = kind.Trim();
            }
        }

        memberKindLogins = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        aliasToPrimary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in options.Members)
        {
            if (string.IsNullOrWhiteSpace(member.Name))
            {
                continue;
            }

            var name = member.Name.Trim();
            // The member name is itself an identity of the person.
            aliasToPrimary[name] = name;

            var kindLogins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var identity in member.Identities)
            {
                if (string.IsNullOrWhiteSpace(identity.Login) || string.IsNullOrWhiteSpace(identity.Kind))
                {
                    continue;
                }

                var login = identity.Login.Trim();
                kindLogins[identity.Kind.Trim()] = login;
                aliasToPrimary[login] = name;
            }

            if (kindLogins.Count > 0)
            {
                memberKindLogins[name] = kindLogins;
            }
        }

        CurrentDeveloper = ResolveCurrentDeveloper(options);
    }

    /// <summary>The identity kind used when a repository has no explicit kind.</summary>
    public string DefaultKind { get; }

    /// <summary>The member the local developer is, for development token routing; may be null.</summary>
    public string? CurrentDeveloper { get; }

    /// <summary>True when at least one member has usable identities.</summary>
    public bool HasMembers => memberKindLogins.Count > 0;

    /// <summary>Maps every configured login (and member name) to its member's canonical name.</summary>
    public IReadOnlyDictionary<string, string> AliasToPrimary => aliasToPrimary;

    /// <summary>Returns the identity kind that can read <paramref name="repositoryName"/>.</summary>
    public string ResolveKind(RepositoryName? repositoryName) =>
        repositoryName is { } repository && repositoryKinds.TryGetValue(repository.ToString(), out var kind)
            ? kind
            : DefaultKind;

    /// <summary>
    /// Resolves the login <paramref name="memberName"/> uses to read <paramref name="repositoryName"/>.
    /// Falls back to the member's default-kind identity when the repository's kind has no identity,
    /// setting <paramref name="usedDefaultKindFallback"/> so the caller can surface a warning.
    /// </summary>
    public bool TryResolveLoginForRepository(
        string? memberName,
        RepositoryName? repositoryName,
        out string login,
        out bool usedDefaultKindFallback)
    {
        login = "";
        usedDefaultKindFallback = false;
        if (string.IsNullOrWhiteSpace(memberName) ||
            !memberKindLogins.TryGetValue(memberName, out var kindLogins))
        {
            return false;
        }

        var kind = ResolveKind(repositoryName);
        if (kindLogins.TryGetValue(kind, out var kindLogin))
        {
            login = kindLogin;
            return true;
        }

        if (!string.Equals(kind, DefaultKind, StringComparison.OrdinalIgnoreCase) &&
            kindLogins.TryGetValue(DefaultKind, out var defaultLogin))
        {
            login = defaultLogin;
            usedDefaultKindFallback = true;
            return true;
        }

        return false;
    }

    /// <summary>Canonicalizes a login to its member name, or returns the login unchanged when unmapped.</summary>
    public string CanonicalizeLogin(string login)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            return login;
        }

        return aliasToPrimary.TryGetValue(login.Trim(), out var primary) ? primary : login;
    }

    private static string? ResolveCurrentDeveloper(TeamIdentityOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.CurrentDeveloper))
        {
            return options.CurrentDeveloper.Trim();
        }

        // A single-member configuration (a developer describing just themselves) needs no explicit pick.
        var namedMembers = options.Members
            .Where(member => !string.IsNullOrWhiteSpace(member.Name))
            .ToArray();
        return namedMembers.Length == 1 ? namedMembers[0].Name.Trim() : null;
    }
}
