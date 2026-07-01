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
