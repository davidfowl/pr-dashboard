using Microsoft.Extensions.Options;

public static class DashboardConfigRoutes
{
    public static IEndpointRouteBuilder MapDashboardConfigRoutes(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/dashboard/config", (IOptions<DashboardOptions> options) =>
        {
            var dashboardOptions = options.Value;
            var repositories = Normalize(dashboardOptions.Repositories);
            var shipWeekRepositories = Normalize(dashboardOptions.ShipWeekRepositories);

            return Results.Ok(new DashboardConfigResponse(
                Repositories: repositories,
                RepositoryInput: string.Join(", ", repositories),
                ShipWeekRepositories: shipWeekRepositories,
                ShipWeekRepositoryInput: string.Join(", ", shipWeekRepositories),
                CoreTeamMembers: Normalize(dashboardOptions.CoreTeamMembers),
                CoreTeamMemberAliasSuffixes: Normalize(dashboardOptions.CoreTeamMemberAliasSuffixes),
                CommunityRepositories: Normalize(dashboardOptions.CommunityRepositories),
                CurrentRelease: dashboardOptions.CurrentRelease?.Trim() ?? "",
                ShipWeekReleaseBranch: dashboardOptions.ShipWeekReleaseBranch?.Trim() ?? "",
                DocsFromCodeRepository: dashboardOptions.DocsFromCode.Repository?.Trim() ?? "",
                DocsFromCodeLabel: dashboardOptions.DocsFromCode.Label?.Trim() ?? "",
                DoNotMergeLabels: Normalize(dashboardOptions.DoNotMergeLabels),
                BotAuthors: Normalize(dashboardOptions.BotAuthors),
                NonBlockingCheckFailureRules: Normalize(dashboardOptions.NonBlockingCheckFailureRules)));
        });

        return endpoints;
    }

    private static string[] Normalize(IEnumerable<string>? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static DashboardCheckFailureRuleResponse[] Normalize(IEnumerable<DashboardCheckFailureRuleOptions>? rules) =>
        (rules ?? [])
            .Where(rule => rule is not null)
            .Select(rule => new DashboardCheckFailureRuleResponse(
                Repository: rule.Repository?.Trim() ?? "",
                Label: rule.Label?.Trim() ?? "",
                CheckNames: Normalize(rule.CheckNames),
                CheckNameContains: Normalize(rule.CheckNameContains)))
            .Where(rule => rule.Repository.Length > 0 && rule.Label.Length > 0)
            .ToArray();
}

record DashboardConfigResponse(
    string[] Repositories,
    string RepositoryInput,
    string[] ShipWeekRepositories,
    string ShipWeekRepositoryInput,
    string[] CoreTeamMembers,
    string[] CoreTeamMemberAliasSuffixes,
    string[] CommunityRepositories,
    string CurrentRelease,
    string ShipWeekReleaseBranch,
    string DocsFromCodeRepository,
    string DocsFromCodeLabel,
    string[] DoNotMergeLabels,
    string[] BotAuthors,
    DashboardCheckFailureRuleResponse[] NonBlockingCheckFailureRules);

record DashboardCheckFailureRuleResponse(
    string Repository,
    string Label,
    string[] CheckNames,
    string[] CheckNameContains);
