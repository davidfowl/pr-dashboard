sealed class DashboardOptions
{
    public const string SectionName = "Dashboard";

    public string[] Repositories { get; init; } = [];

    public string[] ShipWeekRepositories { get; init; } = [];

    public string[] CoreTeamMembers { get; init; } = [];

    public string[] CoreTeamMemberAliasSuffixes { get; init; } = [];

    public string[] CommunityRepositories { get; init; } = [];

    public string CurrentRelease { get; init; } = "";

    public string ShipWeekReleaseBranch { get; init; } = "";

    public DashboardDocsFromCodeOptions DocsFromCode { get; init; } = new();

    public string[] DoNotMergeLabels { get; init; } = [];

    public string[] BotAuthors { get; init; } = [];

    public DashboardCheckFailureRuleOptions[] NonBlockingCheckFailureRules { get; init; } = [];

    /// <summary>
    /// Maps an alias GitHub login to the team member's canonical login, so a person's multiple
    /// identities group as one. Populated at runtime from <see cref="TeamIdentityMap"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> IdentityAliases { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

sealed class DashboardDocsFromCodeOptions
{
    public string Repository { get; init; } = "";

    public string Label { get; init; } = "";
}

sealed class DashboardCheckFailureRuleOptions
{
    public string Repository { get; init; } = "";

    public string Label { get; init; } = "";

    public string[] CheckNames { get; init; } = [];

    public string[] CheckNameContains { get; init; } = [];
}
