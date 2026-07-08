/// <summary>
/// Team members and their multiple GitHub identities. A member owns one identity per <em>kind</em>
/// (for example a <c>public</c> identity and a <c>microsoft</c> identity), repositories are routed to
/// a kind, and each developer supplies their own login per kind — so one mapping works for the whole
/// team.
/// </summary>
/// <remarks>
/// Team-wide data (<see cref="DefaultKind"/>, <see cref="RepositoryKinds"/>, <see cref="Members"/>)
/// can be committed; the development-only <see cref="CurrentDeveloper"/> and any personal logins are
/// stored in user-secrets. The identities are local <c>gh</c> accounts in development.
/// </remarks>
sealed class TeamIdentityOptions
{
    public const string SectionName = "TeamIdentity";
    public const string PublicKind = "public";

    /// <summary>The identity kind used for repositories without an explicit <see cref="RepositoryKinds"/> entry.</summary>
    public string DefaultKind { get; init; } = PublicKind;

    /// <summary>Maps an <c>owner/repo</c> repository name (case-insensitive) to the identity kind that can read it.</summary>
    public Dictionary<string, string> RepositoryKinds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The team members and their identities.</summary>
    public TeamMemberOptions[] Members { get; init; } = [];

    /// <summary>
    /// Development-only: which member the local developer is, so token routing can pick their identity
    /// per repository kind. If unset and exactly one member is configured, that member is assumed.
    /// </summary>
    public string? CurrentDeveloper { get; init; }
}

sealed class TeamMemberOptions
{
    /// <summary>The member's canonical name (typically their primary/public login).</summary>
    public string Name { get; init; } = "";

    public TeamMemberIdentityOptions[] Identities { get; init; } = [];
}

sealed class TeamMemberIdentityOptions
{
    /// <summary>The GitHub login for this identity.</summary>
    public string Login { get; init; } = "";

    /// <summary>The identity kind (for example <c>public</c> or <c>microsoft</c>).</summary>
    public string Kind { get; init; } = "";
}
