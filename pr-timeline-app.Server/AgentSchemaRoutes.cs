public static class AgentSchemaRoutes
{
    private const string AgentSchemaContentType = "application/pr-dashboard-agent-schema+json";

    public static IEndpointRouteBuilder MapAgentSchemaRoutes(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/agents/schema", () => Results.Json(CreateSchema(), contentType: AgentSchemaContentType));
        endpoints.MapGet("/api/agents/schema.json", () => Results.Json(CreateSchema(), contentType: AgentSchemaContentType));
        endpoints.MapGet("/.well-known/pr-dashboard-agent-schema", () => Results.Json(CreateSchema(), contentType: AgentSchemaContentType));

        return endpoints;
    }

    private static AgentUseCaseSchemaResponse CreateSchema() =>
        new(
            SchemaVersion: 1,
            Name: "pr-dashboard",
            Description: "Use this schema to choose the dashboard mode and API endpoint for a PR-dashboard automation use case.",
            Discovery: new(
                SchemaUrls:
                [
                    "/api/agents/schema",
                    "/api/agents/schema.json",
                    "/.well-known/pr-dashboard-agent-schema"
                ],
                HtmlSelectors:
                [
                    "link[rel=\"service-desc\"][type=\"application/pr-dashboard-agent-schema+json\"]",
                    "meta[name=\"pr-dashboard-agent-schema\"]"
                ]),
            Modes:
            [
                new(
                    Id: "review",
                    Label: "Review mode",
                    DashboardUrl: "/?mode=review",
                    UseCases:
                    [
                        "Find pull requests that need review.",
                        "Find pull requests ready to merge.",
                        "Find pull requests with failing CI, requested changes, stale review activity, or author-response work.",
                        "Find pull requests assigned to the signed-in user through the personal queue."
                    ],
                    ApiEndpoints:
                    [
                        new("GET", "/api/github/pulls/stream?repo={owner}/{repo}&state=open&refresh=true", "Stream pull request summaries for one repository. Set refresh=true to force live GitHub refresh when authenticated. Items with isStale=true are cached overlays and should not be treated as final. A stream is complete only after an item with isComplete=true; if the stream ends without isComplete, keep prior/live data marked incomplete."),
                        new("GET", "/api/github/pulls?repo={owner}/{repo}&state=open", "Return pull request summaries for one repository."),
                        new("GET", "/api/github/pulls/{number}/timeline?repo={owner}/{repo}", "Return activity, checks, mergeability, and triage detail for one pull request.")
                    ],
                    RequiredInputs:
                    [
                        new("repo", "GitHub owner/repo. Repeat the endpoint for each repository when you need a multi-repo queue."),
                        new("state", "Optional pull request state: open, closed, or all. Defaults to open.")
                    ]),
                new(
                    Id: "issues",
                    Label: "Issues mode",
                    DashboardUrl: "/?mode=issues",
                    UseCases:
                    [
                        "Find focused issues that need follow-up without mixing them into PR review work.",
                        "Track regressions, release-blocking issues, and manual validation issues."
                    ],
                    ApiEndpoints:
                    [
                        new("GET", "/api/github/issues/focus?repo={owner}/{repo}&state=open", "Return focused issue summaries for one repository.")
                    ],
                    RequiredInputs:
                    [
                        new("repo", "GitHub owner/repo. Repeat the endpoint for each repository when you need a multi-repo issue view."),
                        new("state", "Optional issue state: open, closed, or all. Defaults to open.")
                    ]),
                new(
                    Id: "ship",
                    Label: "Ship mode",
                    DashboardUrl: "/?mode=ship&milestone={milestone}&releaseBranch={branch}",
                    UseCases:
                    [
                        "Prepare or inspect release/ship-week work for a milestone.",
                        "Compare milestone pull requests, release-branch pull requests, and release-branch watchlist items.",
                        "Create a shareable ship-week snapshot for status reporting."
                    ],
                    ApiEndpoints:
                    [
                        new("GET", "/api/github/ship-week?repo={owner}/{repo}&milestone={milestone}&releaseBranch={branch}", "Return milestone pull requests, linked issues, and release scope signals.")
                    ],
                    RequiredInputs:
                    [
                        new("repo", "GitHub owner/repo."),
                        new("milestone", "Required milestone title, for example 13.4."),
                        new("releaseBranch", "Optional release branch. Defaults to the app's current release branch when omitted.")
                    ])
            ]);
}

record AgentUseCaseSchemaResponse(
    int SchemaVersion,
    string Name,
    string Description,
    AgentDiscoverySchema Discovery,
    IReadOnlyList<AgentModeSchema> Modes);

record AgentDiscoverySchema(
    IReadOnlyList<string> SchemaUrls,
    IReadOnlyList<string> HtmlSelectors);

record AgentModeSchema(
    string Id,
    string Label,
    string DashboardUrl,
    IReadOnlyList<string> UseCases,
    IReadOnlyList<AgentApiEndpointSchema> ApiEndpoints,
    IReadOnlyList<AgentInputSchema> RequiredInputs);

record AgentApiEndpointSchema(string Method, string Path, string Description);

record AgentInputSchema(string Name, string Description);
