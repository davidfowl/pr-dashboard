using Microsoft.AspNetCore.Mvc;

public static class GitHubPullRequestRoutes
{
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

        api.MapGet("regression-issues", async (
            [FromQuery] string? repo,
            [FromQuery] string? state,
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

            var issues = await pullRequests.GetRegressionIssuesAsync(
                repositoryName,
                normalizedState,
                refresh == true,
                cancellationToken);
            return Results.Ok(new IssueListResponse(repositoryName.ToString(), issues));
        });

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
}
