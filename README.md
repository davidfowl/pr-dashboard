# Team Dashboard

A team dashboard for the Aspire team, built with Aspire.

It helps the team prioritize GitHub pull request work, focus on urgent reviews, and scan PR timeline details quickly.

## Prerequisites

- .NET 10 SDK
- Aspire CLI from the dev channel
- Docker or another Aspire-supported container runtime for the local Azurite storage emulator
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
- `microsoft/aspire-skills`
- `microsoft/dcp`
- `CommunityToolkit/Aspire`

You can replace that list in the dashboard with any comma-separated `owner/repo` values.

## GitHub auth

In development, the server can use an OAuth session, `GITHUB_TOKEN`, `GH_TOKEN`, or `gh auth token`. Outside development, configure `GITHUB_CLIENT_ID` and `GITHUB_CLIENT_SECRET`; the callback path is `/signin-github`. The OAuth flow requests no GitHub scopes, so it supports public repository API reads without requesting repository or organization permissions.

## Conversation-resolution policy

Some repositories require all review conversations to be resolved before an approved PR can merge (GitHub's "require conversation resolution" branch protection). For repositories listed in `GitHubReviewPolicy:RequireConversationResolution` (`owner/repo`, case-insensitive), the dashboard fetches unresolved review-thread counts for approved PRs and surfaces them: an approved PR with open threads shows a `resolve feedback` action and a `N unresolved` pill, moves to an **Unresolved feedback** bucket, and is kept out of **Ready to merge**.

This is opt-in per repo because the branch-protection setting is only readable through GitHub's admin-scoped branch-protection API, which this app's tokens do not have. Repositories not in the list are unaffected, and no extra GitHub calls are made for them.

## Copilot review feedback

The Copilot review bot's reviews are filtered out of a PR's human review state, so a PR that only Copilot has commented on otherwise looks like it still "needs a reviewer". To avoid surfacing those as actionable review work, the dashboard also fetches unresolved review-thread counts for **waiting** PRs that the Copilot reviewer has reviewed (this is independent of the conversation-resolution policy above, since it is a triage signal rather than a merge gate).

A waiting PR with unresolved threads is treated as waiting on the author: it shows an `address feedback` action, moves to a **Copilot feedback** bucket, and is kept out of the **Needs attention** focus queue. The extra GraphQL call is bounded to waiting PRs the bot actually reviewed.

Any PR whose head commit has failing checks is also kept out of the **Needs attention** focus queue (pending checks are fine). The author still sees it in the standalone **CI failing** bucket, and it reappears in Needs attention once its checks are green — nudging the team to keep CI passing.

## Production public cache

Logged-out users read pull request data only from the shared public cache for repositories in `GitHubCacheWarmup:Repositories`. Configure `GITHUB_PUBLIC_CACHE_TOKEN` or `GitHubCacheWarmup:PublicCacheToken` with a server-owned fine-grained PAT or GitHub App token so the backend can verify allowlisted public visibility and refresh that cache without using anonymous quota or user tokens. Public cache entries and last-good snapshots are persisted in the Aspire-managed `github-cache` Blob container, which runs as Azurite locally and Azure Blob Storage when published. Local Azurite uses an Aspire data volume so cache snapshots survive container recreation; use the `clear-cache` resource command when you need to reset the local public cache.

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
