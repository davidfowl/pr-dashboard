public static class AgentSchemaRoutes
{
    private const string AgentSchemaContentType = "application/pr-dashboard-agent-schema+json";

    public static IEndpointRouteBuilder MapAgentSchemaRoutes(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/agents/schema", () => Results.Json(CreateSchema(), contentType: AgentSchemaContentType));
        endpoints.MapGet("/api/agents/schema.json", () => Results.Json(CreateSchema(), contentType: AgentSchemaContentType));
        endpoints.MapGet("/.well-known/pr-dashboard-agent-schema", () => Results.Json(CreateSchema(), contentType: AgentSchemaContentType));
        endpoints.MapAgentReviewQueueRoutes();

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
                        "Find the same most-ready pull requests shown in the homepage review queue.",
                        "Find pull requests that need review.",
                        "Find pull requests ready to merge.",
                        "Find pull requests with failing CI, requested changes, stale review activity, or author-response work.",
                        "Find pull requests assigned to the signed-in user through the personal queue."
                    ],
                    ApiEndpoints:
                    [
                        new("GET", "/api/agents/review-queue?repo={owner}/{repo}&refresh=true&limit=10", "Programmatic endpoint for the same most-ready focus queue shown on the homepage review mode. Omit repo to use the dashboard-configured repositories; pass comma-separated repo values to constrain the queue."),
                        new("GET", "/api/dashboard/config", "Return the dashboard configuration used by the homepage. For the homepage review queue, read repositories from repositories or repositoryInput, then use doNotMergeLabels, botAuthors, communityRepositories, coreTeamMembers, and nonBlockingCheckFailureRules when applying queue rules."),
                        new("GET", "/api/github/pulls/graphql?repo={owner}/{repo}&state=open&refresh=true", "Return pull request summaries from the same endpoint the homepage uses for review mode. Use this per repository when you need raw pull request data instead of the aggregated review queue. Set refresh=true to force live GitHub refresh when authenticated; unauthenticated callers may receive a stale/empty shared-cache error until the public cache is warm."),
                        new("GET", "/api/github/pulls/stream?repo={owner}/{repo}&state=open&refresh=true", "Stream pull request summaries for one repository. Set refresh=true to force live GitHub refresh when authenticated. Items with isStale=true are cached overlays and should not be treated as final. A stream is complete only after an item with isComplete=true; if the stream ends without isComplete, keep prior/live data marked incomplete."),
                        new("GET", "/api/github/pulls?repo={owner}/{repo}&state=open", "Return pull request summaries for one repository."),
                        new("GET", "/api/github/pulls/{number}/timeline?repo={owner}/{repo}", "Return activity, checks, mergeability, and triage detail for one pull request.")
                    ],
                    RequiredInputs:
                    [
                        new("repo", "Optional GitHub owner/repo filter. Omit it to use configured repositories, or pass comma-separated owner/repo values for a multi-repo queue."),
                        new("state", "Optional pull request state: open, closed, or all. Defaults to open.")
                    ],
                    HomepageFocusQueue: new(
                        Description: "Instructions for reproducing the same focus queue shown on /?mode=review, including the most-ready pull requests on the homepage.",
                        ConfigEndpoint: "/api/dashboard/config",
                        Steps:
                        [
                            "Use repositories from dashboard config repositories or repositoryInput unless the user supplied an explicit repo list.",
                            "Fetch /api/github/pulls/graphql?repo={owner}/{repo}&state=open&refresh=true for each repository; omit refresh=true when you intentionally want cached data.",
                            "Build attention buckets from the returned pull request summaries using the configured labels, bot authors, community repositories, core team, and non-blocking check rules.",
                            "Rank each pull request by its highest-priority eligible bucket, dedupe by repository and number, then keep only pull requests with recent action-relevant activity.",
                            "Exclude author-blocked or specialized work from the focus queue; surface it in blocker/specialized lanes instead of the most-ready review list."
                        ],
                        BucketPriority:
                        [
                            "Regression",
                            "Approved but aging",
                            "Re-review needed",
                            "Ready to merge",
                            "Needs review",
                            "Quick wins",
                            "Review started"
                        ],
                        ExcludedBuckets:
                        [
                            "Draft",
                            "My draft PRs",
                            "Docs",
                            "Community Toolkit",
                            "Bots / automation",
                            "Community",
                            "Aged out community",
                            "Unresolved feedback",
                            "Merge conflicts",
                            "CI failing",
                            "Author response",
                            "Stalled"
                        ])
                    ),
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
    IReadOnlyList<AgentInputSchema> RequiredInputs,
    AgentHomepageFocusQueueSchema? HomepageFocusQueue = null);

record AgentApiEndpointSchema(string Method, string Path, string Description);

record AgentInputSchema(string Name, string Description);

record AgentHomepageFocusQueueSchema(
   string Description,
   string ConfigEndpoint,
   IReadOnlyList<string> Steps,
   IReadOnlyList<string> BucketPriority,
   IReadOnlyList<string> ExcludedBuckets);
