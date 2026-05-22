using Microsoft.AspNetCore.Mvc;

public static class GitHubPullRequestRoutes
{
    public static IEndpointRouteBuilder MapGitHubPullRequestRoutes(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/github");

        api.MapGet("pulls", async (
            [FromQuery] string? repo,
            [FromQuery] string? state,
            GitHubClient gitHub,
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

            var pulls = await gitHub.GetPullRequestsAsync(repositoryName, normalizedState, cancellationToken);
            return Results.Ok(new PullRequestListResponse(repositoryName.ToString(), pulls));
        });

        api.MapGet("pulls/{number:int}/timeline", async (
            int number,
            [FromQuery] string? repo,
            GitHubClient gitHub,
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

            var pullRequest = await gitHub.GetPullRequestDetailsAsync(repositoryName, number, cancellationToken);
            var timeline = await gitHub.GetPullRequestTimelineAsync(repositoryName, number, cancellationToken);
            var stats = TimelineStats.Create(pullRequest, timeline);

            return Results.Ok(new TimelineResponse(repositoryName.ToString(), number, stats, timeline));
        });

        return endpoints;
    }
}
