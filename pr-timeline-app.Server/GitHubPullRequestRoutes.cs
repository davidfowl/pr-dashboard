using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

public static class GitHubPullRequestRoutes
{
    private static readonly JsonSerializerOptions s_streamJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] s_newLine = [(byte)'\n'];

    public static IEndpointRouteBuilder MapGitHubPullRequestRoutes(this IEndpointRouteBuilder endpoints)
    {
        var logger = endpoints.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("GitHubPullRequestRoutes");
        var api = endpoints.MapGroup("/api/github");

        api.MapGet("pulls", async (
            [FromQuery] string? repo,
            [FromQuery] string? state,
            [FromQuery] string? label,
            [FromQuery] bool? refresh,
            IOptions<DashboardOptions> dashboardOptions,
            GitHubPullRequestService pullRequests,
            CancellationToken cancellationToken) =>
        {
            const string routeName = "pulls";
            if (!TryResolveRepositoryName(repo, dashboardOptions.Value.Repositories, logger, routeName, "Dashboard:Repositories", out var repositoryName))
            {
                return InvalidRepository();
            }

            var normalizedState = string.IsNullOrWhiteSpace(state) ? "open" : state.Trim().ToLowerInvariant();
            if (normalizedState is not ("open" or "closed" or "all"))
            {
                return InvalidState(logger, routeName, repositoryName, state);
            }

            var forceRefresh = refresh == true;
            LogGitHubRepositoryRequest(logger, routeName, repositoryName, normalizedState, label, forceRefresh);
            var pulls = string.IsNullOrWhiteSpace(label)
                ? await pullRequests.GetPullRequestsAsync(repositoryName, normalizedState, forceRefresh, cancellationToken)
                : await pullRequests.GetPullRequestsByLabelAsync(repositoryName, normalizedState, label.Trim(), forceRefresh, cancellationToken);
            logger.LogDebug(
                "GitHub route completed. Route={GitHubRoute}, Repository={Repository}, PullRequestCount={PullRequestCount}.",
                routeName,
                repositoryName,
                pulls.Count);
            return Results.Ok(new PullRequestListResponse(repositoryName.ToString(), pulls));
        });

        api.MapGet("pulls/graphql", async (
            [FromQuery] string? repo,
            [FromQuery] string? state,
            [FromQuery] string? label,
            [FromQuery] bool? refresh,
            IOptions<DashboardOptions> dashboardOptions,
            GitHubPullRequestService pullRequests,
            CancellationToken cancellationToken) =>
        {
            const string routeName = "pulls/graphql";
            if (!TryResolveRepositoryName(repo, dashboardOptions.Value.Repositories, logger, routeName, "Dashboard:Repositories", out var repositoryName))
            {
                return InvalidRepository();
            }

            var normalizedState = string.IsNullOrWhiteSpace(state) ? "open" : state.Trim().ToLowerInvariant();
            if (normalizedState is not ("open" or "closed" or "all"))
            {
                return InvalidState(logger, routeName, repositoryName, state);
            }

            var forceRefresh = refresh == true;
            LogGitHubRepositoryRequest(logger, routeName, repositoryName, normalizedState, label, forceRefresh);
            var response = await pullRequests.GetPullRequestsGraphQlSnapshotAsync(
                repositoryName,
                normalizedState,
                forceRefresh,
                cancellationToken);
            var pulls = response.PullRequests;
            var unfilteredPullRequestCount = pulls.Count;
            if (!string.IsNullOrWhiteSpace(label))
            {
                var trimmedLabel = label.Trim();
                pulls = pulls
                    .Where(pullRequest => pullRequest.Labels.Contains(trimmedLabel, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
            }

            var snapshot = response.Snapshot;
            logger.LogDebug(
                "GitHub route completed. Route={GitHubRoute}, Repository={Repository}, PullRequestCount={PullRequestCount}, UnfilteredPullRequestCount={UnfilteredPullRequestCount}, SnapshotSource={SnapshotSource}, SnapshotStale={SnapshotStale}, SnapshotRefreshInProgress={SnapshotRefreshInProgress}, SnapshotRefreshQueued={SnapshotRefreshQueued}, SnapshotHasError={SnapshotHasError}.",
                routeName,
                repositoryName,
                pulls.Count,
                unfilteredPullRequestCount,
                snapshot?.Source,
                snapshot?.Stale,
                snapshot?.RefreshInProgress,
                snapshot?.RefreshQueued,
                !string.IsNullOrWhiteSpace(snapshot?.Error));
            return Results.Ok(response with { PullRequests = pulls });
        });

        api.MapGet("pulls/stream", (
            [FromQuery] string? repo,
            [FromQuery] string? state,
            [FromQuery] string? label,
            [FromQuery] bool? refresh,
            IOptions<DashboardOptions> dashboardOptions,
            GitHubPullRequestService pullRequests,
            CancellationToken cancellationToken) =>
        {
            const string routeName = "pulls/stream";
            if (!TryResolveRepositoryName(repo, dashboardOptions.Value.Repositories, logger, routeName, "Dashboard:Repositories", out var repositoryName))
            {
                return InvalidRepository();
            }

            var normalizedState = string.IsNullOrWhiteSpace(state) ? "open" : state.Trim().ToLowerInvariant();
            if (normalizedState is not ("open" or "closed" or "all"))
            {
                return InvalidState(logger, routeName, repositoryName, state);
            }

            var forceRefresh = refresh == true;
            LogGitHubRepositoryRequest(logger, routeName, repositoryName, normalizedState, label, forceRefresh);
            var stream = string.IsNullOrWhiteSpace(label)
                ? pullRequests.StreamPullRequestEntriesAsync(repositoryName, normalizedState, forceRefresh, cancellationToken)
                : pullRequests.StreamPullRequestEntriesByLabelAsync(repositoryName, normalizedState, label.Trim(), forceRefresh, cancellationToken);

            return JsonLines(
                CreatePullRequestStreamItems(repositoryName.ToString(), stream, logger, routeName, cancellationToken),
                cancellationToken);
        });

        api.MapGet("issues/focus", GetFocusIssuesAsync);
        api.MapGet("regression-issues", GetFocusIssuesAsync);

        api.MapGet("ship-week", async (
            [FromQuery] string? repo,
            [FromQuery] string? milestone,
            [FromQuery] string? releaseBranch,
            [FromQuery] bool? refresh,
            IOptions<DashboardOptions> dashboardOptions,
            GitHubPullRequestService pullRequests,
            CancellationToken cancellationToken) =>
        {
            const string routeName = "ship-week";
            if (!TryResolveRepositoryName(repo, dashboardOptions.Value.ShipWeekRepositories, logger, routeName, "Dashboard:ShipWeekRepositories", out var repositoryName))
            {
                return InvalidRepository();
            }

            var normalizedMilestone = milestone?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedMilestone))
            {
                logger.LogWarning(
                    "Rejected GitHub route request because milestone was missing. Route={GitHubRoute}, Repository={Repository}, ReleaseBranchProvided={ReleaseBranchProvided}, Refresh={GitHubRefreshRequested}.",
                    routeName,
                    repositoryName,
                    !string.IsNullOrWhiteSpace(releaseBranch),
                    refresh == true);
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["milestone"] = ["Milestone is required, for example 13.4."]
                });
            }

            logger.LogDebug(
                "GitHub route requested. Route={GitHubRoute}, Repository={Repository}, Milestone={Milestone}, ReleaseBranchProvided={ReleaseBranchProvided}, Refresh={GitHubRefreshRequested}.",
                routeName,
                repositoryName,
                normalizedMilestone,
                !string.IsNullOrWhiteSpace(releaseBranch),
                refresh == true);
            var result = await pullRequests.GetShipWeekAsync(
                repositoryName,
                normalizedMilestone,
                releaseBranch?.Trim(),
                refresh == true,
                cancellationToken);
            if (result.Response is null)
            {
                logger.LogWarning(
                    "GitHub route validation failed. Route={GitHubRoute}, Repository={Repository}, Milestone={Milestone}, ValidationFields={ValidationFields}.",
                    routeName,
                    repositoryName,
                    normalizedMilestone,
                    result.ValidationErrors.Keys.ToArray());
                return Results.ValidationProblem(result.ValidationErrors);
            }

            logger.LogDebug(
                "GitHub route completed. Route={GitHubRoute}, Repository={Repository}, Milestone={Milestone}, ReleaseBranch={ReleaseBranch}, PullRequestCount={PullRequestCount}, IssueCount={IssueCount}.",
                routeName,
                repositoryName,
                result.Response.Milestone,
                result.Response.ReleaseBranch,
                result.Response.PullRequests.Count,
                result.Response.Issues.Count);
            return Results.Ok(result.Response);
        });

        api.MapPost("pulls/checks", async (
            [FromQuery] string? repo,
            [FromQuery] bool? refresh,
            PullRequestChecksRequest request,
            IOptions<DashboardOptions> dashboardOptions,
            GitHubPullRequestService pullRequests,
            CancellationToken cancellationToken) =>
        {
            const string routeName = "pulls/checks";
            if (!TryResolveRepositoryName(repo, dashboardOptions.Value.Repositories, logger, routeName, "Dashboard:Repositories", out var repositoryName))
            {
                return InvalidRepository();
            }

            var requestedPullRequests = request.PullRequests ?? [];
            if (requestedPullRequests.Any(pullRequest => pullRequest.Number <= 0))
            {
                logger.LogWarning(
                    "Rejected GitHub route request because one or more pull request numbers were invalid. Route={GitHubRoute}, Repository={Repository}, RequestedPullRequestCount={RequestedPullRequestCount}.",
                    routeName,
                    repositoryName,
                    requestedPullRequests.Count);
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["pullRequests"] = ["Pull request numbers must be greater than zero."]
                });
            }

            logger.LogDebug(
                "GitHub route requested. Route={GitHubRoute}, Repository={Repository}, RequestedPullRequestCount={RequestedPullRequestCount}, Refresh={GitHubRefreshRequested}.",
                routeName,
                repositoryName,
                requestedPullRequests.Count,
                refresh == true);
            var checks = await pullRequests.GetPullRequestChecksAsync(
                repositoryName,
                requestedPullRequests,
                refresh == true,
                cancellationToken);
            logger.LogDebug(
                "GitHub route completed. Route={GitHubRoute}, Repository={Repository}, PullRequestChecksCount={PullRequestChecksCount}.",
                routeName,
                repositoryName,
                checks.Count);
            return Results.Ok(new PullRequestChecksResponse(repositoryName.ToString(), checks));
        });

        api.MapGet("pulls/{number:int}/timeline", async (
            int number,
            [FromQuery] string? repo,
            [FromQuery] bool? refresh,
            IOptions<DashboardOptions> dashboardOptions,
            GitHubPullRequestService pullRequests,
            CancellationToken cancellationToken) =>
        {
            const string routeName = "pulls/timeline";
            if (number <= 0)
            {
                logger.LogWarning(
                    "Rejected GitHub route request because pull request number was invalid. Route={GitHubRoute}, PullRequestNumber={PullRequestNumber}.",
                    routeName,
                    number);
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["number"] = ["Pull request number must be greater than zero."]
                });
            }

            if (!TryResolveRepositoryName(repo, dashboardOptions.Value.Repositories, logger, routeName, "Dashboard:Repositories", out var repositoryName))
            {
                return InvalidRepository();
            }

            logger.LogDebug(
                "GitHub route requested. Route={GitHubRoute}, Repository={Repository}, PullRequestNumber={PullRequestNumber}, Refresh={GitHubRefreshRequested}.",
                routeName,
                repositoryName,
                number,
                refresh == true);
            var timeline = await pullRequests.GetTimelineAsync(repositoryName, number, refresh == true, cancellationToken);
            logger.LogDebug(
                "GitHub route completed. Route={GitHubRoute}, Repository={Repository}, PullRequestNumber={PullRequestNumber}, TimelineItemCount={TimelineItemCount}, CheckState={CheckState}, MergeableState={MergeableState}.",
                routeName,
                repositoryName,
                number,
                timeline.Items.Count,
                timeline.Checks.State,
                timeline.MergeableState);
            return Results.Ok(timeline);
        });

        return endpoints;
    }

    private static IResult JsonLines<T>(IAsyncEnumerable<T> items, CancellationToken cancellationToken) =>
        Results.Stream(async stream =>
        {
            await foreach (var item in items.WithCancellation(cancellationToken))
            {
                await JsonSerializer.SerializeAsync(stream, item, s_streamJsonOptions, cancellationToken);
                await stream.WriteAsync(s_newLine, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
        }, "application/x-ndjson");

    private static async Task<IResult> GetFocusIssuesAsync(
        [FromQuery] string? repo,
        [FromQuery] string? state,
        [FromQuery] bool? refresh,
        IOptions<DashboardOptions> dashboardOptions,
        GitHubPullRequestService pullRequests,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        const string routeName = "issues/focus";
        var logger = loggerFactory.CreateLogger("GitHubPullRequestRoutes");
        if (!TryResolveRepositoryName(repo, dashboardOptions.Value.Repositories, logger, routeName, "Dashboard:Repositories", out var repositoryName))
        {
            return InvalidRepository();
        }

        var normalizedState = string.IsNullOrWhiteSpace(state) ? "open" : state.Trim().ToLowerInvariant();
        if (normalizedState is not ("open" or "closed" or "all"))
        {
            return InvalidState(logger, routeName, repositoryName, state);
        }

        LogGitHubRepositoryRequest(logger, routeName, repositoryName, normalizedState, label: null, refresh: refresh == true);
        var issues = await pullRequests.GetFocusIssuesAsync(
            repositoryName,
            normalizedState,
            refresh == true,
            cancellationToken);
        logger.LogDebug(
            "GitHub route completed. Route={GitHubRoute}, Repository={Repository}, IssueCount={IssueCount}.",
            routeName,
            repositoryName,
            issues.Count);
        return Results.Ok(new IssueListResponse(repositoryName.ToString(), issues));
    }

    private static async IAsyncEnumerable<PullRequestStreamItem> CreatePullRequestStreamItems(
        string repository,
        IAsyncEnumerable<PullRequestStreamEntry> pullRequests,
        ILogger logger,
        string routeName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pullRequestCount = 0;
        var stalePullRequestCount = 0;
        try
        {
            await foreach (var entry in pullRequests.WithCancellation(cancellationToken))
            {
                if (entry.PullRequest is not null)
                {
                    pullRequestCount++;
                    if (entry.IsStale)
                    {
                        stalePullRequestCount++;
                    }
                }

                yield return entry.IsComplete
                    ? new PullRequestStreamItem(repository, null, IsComplete: true)
                    : new PullRequestStreamItem(repository, entry.PullRequest, entry.IsStale);
            }
        }
        finally
        {
            logger.LogDebug(
                "GitHub route stream completed. Route={GitHubRoute}, Repository={Repository}, PullRequestCount={PullRequestCount}, StalePullRequestCount={StalePullRequestCount}, Canceled={RequestCanceled}.",
                routeName,
                repository,
                pullRequestCount,
                stalePullRequestCount,
                cancellationToken.IsCancellationRequested);
        }
    }

    private static bool TryResolveRepositoryName(
        string? repo,
        IReadOnlyList<string> configuredRepositories,
        ILogger logger,
        string routeName,
        string configSection,
        out RepositoryName repositoryName)
    {
        repositoryName = default;
        var repositoryInputProvided = !string.IsNullOrWhiteSpace(repo);
        var repositoryInput = string.IsNullOrWhiteSpace(repo)
            ? configuredRepositories.FirstOrDefault(repository => !string.IsNullOrWhiteSpace(repository))
            : repo;

        if (!string.IsNullOrWhiteSpace(repositoryInput)
            && RepositoryName.TryParse(repositoryInput, out repositoryName))
        {
            return true;
        }

        logger.LogWarning(
            "Rejected GitHub route request because repository input was missing or invalid. Route={GitHubRoute}, RepositoryInputProvided={RepositoryInputProvided}, RepositoryInput={RepositoryInput}, ConfiguredRepositoryCount={ConfiguredRepositoryCount}, ConfigSection={DashboardConfigSection}.",
            routeName,
            repositoryInputProvided,
            TruncateForLog(repositoryInput),
            configuredRepositories.Count,
            configSection);
        return false;
    }

    private static IResult InvalidRepository() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["repo"] = ["Use the owner/repo format."]
        });

    private static IResult InvalidState(ILogger logger, string routeName, RepositoryName repositoryName, string? state)
    {
        logger.LogWarning(
            "Rejected GitHub route request because state was invalid. Route={GitHubRoute}, Repository={Repository}, State={GitHubState}.",
            routeName,
            repositoryName,
            TruncateForLog(state));
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["state"] = ["State must be open, closed, or all."]
        });
    }

    private static void LogGitHubRepositoryRequest(
        ILogger logger,
        string routeName,
        RepositoryName repositoryName,
        string state,
        string? label,
        bool refresh)
    {
        logger.LogDebug(
            "GitHub route requested. Route={GitHubRoute}, Repository={Repository}, State={GitHubState}, LabelProvided={GitHubLabelProvided}, Refresh={GitHubRefreshRequested}.",
            routeName,
            repositoryName,
            state,
            !string.IsNullOrWhiteSpace(label),
            refresh);
    }

    private static string? TruncateForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }
}
