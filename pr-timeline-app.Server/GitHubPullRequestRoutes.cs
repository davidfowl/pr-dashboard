using Microsoft.AspNetCore.Mvc;

public static class GitHubPullRequestRoutes
{
    public static IEndpointRouteBuilder MapGitHubPullRequestRoutes(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/github");

        api.MapGet("pulls", async (
            [FromQuery] string? repo,
            [FromQuery] string? state,
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

            var pulls = await pullRequests.GetPullRequestsAsync(repositoryName, normalizedState, cancellationToken);
            return Results.Ok(new PullRequestListResponse(repositoryName.ToString(), pulls));
        });

        api.MapPost("pulls/checks", async (
            [FromQuery] string? repo,
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
                cancellationToken);
            return Results.Ok(new PullRequestChecksResponse(repositoryName.ToString(), checks));
        });

        api.MapGet("pulls/{number:int}/timeline", async (
            int number,
            [FromQuery] string? repo,
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

            return Results.Ok(await pullRequests.GetTimelineAsync(repositoryName, number, cancellationToken));
        });

        return endpoints;
    }
}
