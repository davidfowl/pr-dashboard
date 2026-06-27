using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

public static class GitHubPullRequestRoutes
{
    private static readonly JsonSerializerOptions s_streamJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] s_newLine = [(byte)'\n'];

    public static IEndpointRouteBuilder MapGitHubPullRequestRoutes(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/github");

        api.MapGet("pulls", async (
            [FromQuery] string? repo,
            [FromQuery] string? state,
            [FromQuery] string? label,
            [FromQuery] bool? refresh,
            GitHubPullRequestService pullRequests,
            CancellationToken cancellationToken) =>
        {
            if (!RepositoryName.TryParse(repo ?? "microsoft/aspire", out var repositoryName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["repo"] = ["Use the owner/repo format, for example microsoft/aspire."]
                });
            }

            var normalizedState = string.IsNullOrWhiteSpace(state) ? "open" : state.Trim().ToLowerInvariant();
            if (normalizedState is not ("open" or "closed" or "all"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["state"] = ["State must be open, closed, or all."]
                });
            }

            var forceRefresh = refresh == true;
            var pulls = string.IsNullOrWhiteSpace(label)
                ? await pullRequests.GetPullRequestsAsync(repositoryName, normalizedState, forceRefresh, cancellationToken)
                : await pullRequests.GetPullRequestsByLabelAsync(repositoryName, normalizedState, label.Trim(), forceRefresh, cancellationToken);
            return Results.Ok(new PullRequestListResponse(repositoryName.ToString(), pulls));
        });

        api.MapGet("pulls/graphql", async (
            [FromQuery] string? repo,
            [FromQuery] string? state,
            [FromQuery] string? label,
            [FromQuery] bool? refresh,
            GitHubPullRequestService pullRequests,
            CancellationToken cancellationToken) =>
        {
            if (!RepositoryName.TryParse(repo ?? "microsoft/aspire", out var repositoryName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["repo"] = ["Use the owner/repo format, for example microsoft/aspire."]
                });
            }

            var normalizedState = string.IsNullOrWhiteSpace(state) ? "open" : state.Trim().ToLowerInvariant();
            if (normalizedState is not ("open" or "closed" or "all"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["state"] = ["State must be open, closed, or all."]
                });
            }

            var response = await pullRequests.GetPullRequestsGraphQlSnapshotAsync(
                repositoryName,
                normalizedState,
                refresh == true,
                cancellationToken);
            var pulls = response.PullRequests;
            if (!string.IsNullOrWhiteSpace(label))
            {
                var trimmedLabel = label.Trim();
                pulls = pulls
                    .Where(pullRequest => pullRequest.Labels.Contains(trimmedLabel, StringComparer.OrdinalIgnoreCase))
                    .ToArray();
            }

            return Results.Ok(response with { PullRequests = pulls });
        });

        api.MapGet("pulls/stream", (
            [FromQuery] string? repo,
            [FromQuery] string? state,
            [FromQuery] string? label,
            [FromQuery] bool? refresh,
            GitHubPullRequestService pullRequests,
            CancellationToken cancellationToken) =>
        {
            if (!RepositoryName.TryParse(repo ?? "microsoft/aspire", out var repositoryName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["repo"] = ["Use the owner/repo format, for example microsoft/aspire."]
                });
            }

            var normalizedState = string.IsNullOrWhiteSpace(state) ? "open" : state.Trim().ToLowerInvariant();
            if (normalizedState is not ("open" or "closed" or "all"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["state"] = ["State must be open, closed, or all."]
                });
            }

            var forceRefresh = refresh == true;
            var stream = string.IsNullOrWhiteSpace(label)
                ? pullRequests.StreamPullRequestEntriesAsync(repositoryName, normalizedState, forceRefresh, cancellationToken)
                : pullRequests.StreamPullRequestEntriesByLabelAsync(repositoryName, normalizedState, label.Trim(), forceRefresh, cancellationToken);

            return JsonLines(
                CreatePullRequestStreamItems(repositoryName.ToString(), stream, cancellationToken),
                cancellationToken);
        });

        api.MapGet("issues/focus", GetFocusIssuesAsync);
        api.MapGet("regression-issues", GetFocusIssuesAsync);

        api.MapGet("ship-week", async (
            [FromQuery] string? repo,
            [FromQuery] string? milestone,
            [FromQuery] string? releaseBranch,
            [FromQuery] bool? refresh,
            GitHubPullRequestService pullRequests,
            CancellationToken cancellationToken) =>
        {
            if (!RepositoryName.TryParse(repo ?? "microsoft/aspire", out var repositoryName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["repo"] = ["Use the owner/repo format, for example microsoft/aspire."]
                });
            }

            var normalizedMilestone = milestone?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedMilestone))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["milestone"] = ["Milestone is required, for example 13.4."]
                });
            }

            var result = await pullRequests.GetShipWeekAsync(
                repositoryName,
                normalizedMilestone,
                releaseBranch?.Trim(),
                refresh == true,
                cancellationToken);
            if (result.Response is null)
            {
                return Results.ValidationProblem(result.ValidationErrors);
            }

            return Results.Ok(result.Response);
        });

        api.MapPost("pulls/checks", async (
            [FromQuery] string? repo,
            [FromQuery] bool? refresh,
            PullRequestChecksRequest request,
            GitHubPullRequestService pullRequests,
            CancellationToken cancellationToken) =>
        {
            if (!RepositoryName.TryParse(repo ?? "microsoft/aspire", out var repositoryName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["repo"] = ["Use the owner/repo format, for example microsoft/aspire."]
                });
            }

            var requestedPullRequests = request.PullRequests ?? [];
            if (requestedPullRequests.Any(pullRequest => pullRequest.Number <= 0))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["pullRequests"] = ["Pull request numbers must be greater than zero."]
                });
            }

            var checks = await pullRequests.GetPullRequestChecksAsync(
                repositoryName,
                requestedPullRequests,
                refresh == true,
                cancellationToken);
            return Results.Ok(new PullRequestChecksResponse(repositoryName.ToString(), checks));
        });

        api.MapGet("pulls/{number:int}/timeline", async (
            int number,
            [FromQuery] string? repo,
            [FromQuery] bool? refresh,
            GitHubPullRequestService pullRequests,
            CancellationToken cancellationToken) =>
        {
            if (number <= 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["number"] = ["Pull request number must be greater than zero."]
                });
            }

            if (!RepositoryName.TryParse(repo ?? "microsoft/aspire", out var repositoryName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["repo"] = ["Use the owner/repo format, for example microsoft/aspire."]
                });
            }

            return Results.Ok(await pullRequests.GetTimelineAsync(repositoryName, number, refresh == true, cancellationToken));
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
        GitHubPullRequestService pullRequests,
        CancellationToken cancellationToken)
    {
        if (!RepositoryName.TryParse(repo ?? "microsoft/aspire", out var repositoryName))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["repo"] = ["Use the owner/repo format, for example microsoft/aspire."]
            });
        }

        var normalizedState = string.IsNullOrWhiteSpace(state) ? "open" : state.Trim().ToLowerInvariant();
        if (normalizedState is not ("open" or "closed" or "all"))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["state"] = ["State must be open, closed, or all."]
            });
        }

        var issues = await pullRequests.GetFocusIssuesAsync(
            repositoryName,
            normalizedState,
            refresh == true,
            cancellationToken);
        return Results.Ok(new IssueListResponse(repositoryName.ToString(), issues));
    }

    private static async IAsyncEnumerable<PullRequestStreamItem> CreatePullRequestStreamItems(
        string repository,
        IAsyncEnumerable<PullRequestStreamEntry> pullRequests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var entry in pullRequests.WithCancellation(cancellationToken))
        {
            yield return entry.IsComplete
                ? new PullRequestStreamItem(repository, null, IsComplete: true)
                : new PullRequestStreamItem(repository, entry.PullRequest, entry.IsStale);
        }
    }
}
