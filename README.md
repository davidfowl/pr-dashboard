# Team Dashboard

A team dashboard for the Aspire team, built with Aspire.

It helps the team prioritize GitHub pull request work, focus on urgent reviews, and scan PR timeline details quickly.

## Prerequisites

- .NET 10 SDK
- Aspire CLI from the dev channel
- Node.js `20.19+`, `22.12+`, or newer
- Optional: GitHub CLI (`gh`) for local development auth
- Optional: Azure CLI for manual Azure Container Apps deployment

## Run locally

```bash
aspire start
```

Open the Vite frontend at <http://localhost:5173/>.

By default, the dashboard watches:

- `microsoft/aspire`
- `microsoft/aspire.dev`
- `microsoft/dcp`
- `CommunityToolkit/Aspire`

You can replace that list in the dashboard with any comma-separated `owner/repo` values.

## GitHub auth

In development, the server can use an OAuth session, `GITHUB_TOKEN`, `GH_TOKEN`, or `gh auth token`. Outside development, configure `GITHUB_CLIENT_ID` and `GITHUB_CLIENT_SECRET`; the callback path is `/signin-github`. The OAuth flow requests no GitHub scopes, so it supports public repository API reads without requesting repository or organization permissions.

## Conversation-resolution policy

Some repositories require all review conversations to be resolved before an approved PR can merge (GitHub's "require conversation resolution" branch protection). For repositories listed in `GitHubReviewPolicy:RequireConversationResolution` (`owner/repo`, case-insensitive), the dashboard fetches unresolved review-thread counts for approved PRs and surfaces them: an approved PR with open threads shows a `resolve feedback` action and a `N unresolved` pill, moves to an **Unresolved feedback** bucket, and is kept out of **Ready to merge**.

This is opt-in per repo because the branch-protection setting is only readable through GitHub's admin-scoped branch-protection API, which this app's tokens do not have. Repositories not in the list are unaffected, and no extra GitHub calls are made for them.

## Production public cache

Logged-out users read pull request data only from the shared public cache for repositories in `GitHubCacheWarmup:Repositories`. Configure `GITHUB_PUBLIC_CACHE_TOKEN` or `GitHubCacheWarmup:PublicCacheToken` with a server-owned fine-grained PAT or GitHub App token so the backend can verify allowlisted public visibility and refresh that cache without using anonymous quota or user tokens. Visibility verification uses the server token and is cached separately from PR data. The current last-good fallback uses the server memory cache; replace it with durable storage before relying on cache continuity across restarts or multiple backend instances.

## Build and lint

```bash
npm --prefix frontend ci
npm --prefix frontend run lint
npm --prefix frontend run build

dotnet restore pr-timeline-app.slnx
dotnet build pr-timeline-app.slnx --no-restore
```

## Test

```bash
dotnet test pr-timeline-app.slnx --no-build
```

## Project layout

- `pr-timeline-app.AppHost/` - Aspire AppHost for local orchestration and Azure deployment.
- `pr-timeline-app.Server/` - ASP.NET Core API and production static-file host.
- `frontend/` - React/Vite dashboard UI.
- `pr-timeline-app.Tests/` - Aspire-backed smoke tests.

## Deploy

```bash
az login
export Azure__SubscriptionId=<subscription-id>
export Azure__Location=<azure-region>
export Azure__ResourceGroup=<resource-group>
export Parameters__github_client_id=<oauth-app-client-id>
export Parameters__github_client_secret=<oauth-app-client-secret>

aspire deploy
```

After the first deploy, set the GitHub OAuth callback URL to `https://<aca-fqdn>/signin-github`.
