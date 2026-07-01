using Microsoft.Extensions.Options;

public static class DashboardConfigRoutes
{
    public static IEndpointRouteBuilder MapDashboardConfigRoutes(this IEndpointRouteBuilder endpoints)
    {
        var logger = endpoints.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("DashboardConfigRoutes");

        endpoints.MapGet("/api/dashboard/config", (IOptions<DashboardOptions> options) =>
        {
            var dashboardOptions = options.Value;
            var repositories = Normalize(dashboardOptions.Repositories);
            var shipWeekRepositories = Normalize(dashboardOptions.ShipWeekRepositories);
            var invalidRepositoryValues = GetInvalidRepositoryConfigValues(dashboardOptions);
            if (invalidRepositoryValues.Count > 0)
            {
                logger.LogWarning(
                    "Dashboard config contains invalid repository values. InvalidRepositoryValueCount={InvalidRepositoryValueCount}, InvalidRepositoryValues={InvalidRepositoryValues}.",
                    invalidRepositoryValues.Count,
                    invalidRepositoryValues);
            }

            logger.LogInformation(
                "Dashboard config served. RepositoryCount={DashboardRepositoryCount}, ShipWeekRepositoryCount={DashboardShipWeekRepositoryCount}, CoreTeamMemberCount={DashboardCoreTeamMemberCount}, CommunityRepositoryCount={DashboardCommunityRepositoryCount}, CurrentReleaseConfigured={DashboardCurrentReleaseConfigured}, ShipWeekReleaseBranchConfigured={DashboardShipWeekReleaseBranchConfigured}, DocsFromCodeConfigured={DashboardDocsFromCodeConfigured}, DoNotMergeLabelCount={DashboardDoNotMergeLabelCount}, BotAuthorCount={DashboardBotAuthorCount}, NonBlockingRuleCount={DashboardNonBlockingRuleCount}, InvalidRepositoryValueCount={InvalidRepositoryValueCount}.",
                repositories.Length,
                shipWeekRepositories.Length,
                Normalize(dashboardOptions.CoreTeamMembers).Length,
                Normalize(dashboardOptions.CommunityRepositories).Length,
                !string.IsNullOrWhiteSpace(dashboardOptions.CurrentRelease),
                !string.IsNullOrWhiteSpace(dashboardOptions.ShipWeekReleaseBranch),
                !string.IsNullOrWhiteSpace(dashboardOptions.DocsFromCode.Repository)
                    && !string.IsNullOrWhiteSpace(dashboardOptions.DocsFromCode.Label),
                Normalize(dashboardOptions.DoNotMergeLabels).Length,
                Normalize(dashboardOptions.BotAuthors).Length,
                dashboardOptions.NonBlockingCheckFailureRules.Length,
                invalidRepositoryValues.Count);

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

    private static IReadOnlyList<string> GetInvalidRepositoryConfigValues(DashboardOptions options)
    {
        var invalid = new List<string>();
        AddInvalidRepositoryValues(invalid, "Dashboard:Repositories", options.Repositories);
        AddInvalidRepositoryValues(invalid, "Dashboard:ShipWeekRepositories", options.ShipWeekRepositories);
        AddInvalidRepositoryValues(invalid, "Dashboard:CommunityRepositories", options.CommunityRepositories);
        AddInvalidRepositoryValues(invalid, "Dashboard:DocsFromCode:Repository", [options.DocsFromCode.Repository]);
        AddInvalidRepositoryValues(
            invalid,
            "Dashboard:NonBlockingCheckFailureRules:Repository",
            options.NonBlockingCheckFailureRules.Select(rule => rule.Repository));
        return invalid;
    }

    private static void AddInvalidRepositoryValues(List<string> invalid, string path, IEnumerable<string?> values)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!RepositoryName.TryParse(value, out _))
            {
                invalid.Add($"{path}={TruncateForLog(value)}");
            }
        }
    }

    private static string TruncateForLog(string value)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }
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
