# Aspire Team App

Aspire Team App is a team dashboard for the Aspire team, built with Aspire.

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

## Unresolved feedback

When a PR has open (unresolved) review threads, the author has feedback to address, so it is not reviewer-ready. The dashboard fetches unresolved review-thread counts for PRs that have actually been reviewed — approved or commented — plus **waiting** PRs the Copilot review bot has reviewed (its reviews are filtered out of the human review state, so the PR still reads as "waiting"). It skips plain awaiting-review PRs (no threads yet) and changes-requested PRs (already author-blocked in the **Author response** lane), keeping the extra GraphQL calls bounded.

Human and Copilot threads are treated identically. Any such PR shows a `resolve feedback` action and an `N unresolved` pill, moves to the **Unresolved feedback** bucket, and is kept out of the **Needs attention** focus queue.

Merge-blocking is policy-gated. Some repositories require all review conversations to be resolved before an approved PR can merge (GitHub's "require conversation resolution" branch protection). Only for repositories listed in `GitHubReviewPolicy:RequireConversationResolution` (`owner/repo`, case-insensitive) does an approved PR with open threads get pulled out of **Ready to merge**; elsewhere the unresolved-feedback signal is informational and does not gate merging. This is opt-in per repo because the branch-protection setting is only readable through GitHub's admin-scoped branch-protection API, which this app's tokens do not have.

Any PR whose head commit has failing checks is also kept out of the **Needs attention** focus queue (pending checks are fine). The author still sees it in the standalone **CI failing** bucket, and it reappears in Needs attention once its checks are green — nudging the team to keep CI passing.

## Production public cache

Logged-out users read pull request data only from the shared public cache for repositories in `GitHubCacheWarmup:Repositories`. Configure `GITHUB_PUBLIC_CACHE_TOKEN` or `GitHubCacheWarmup:PublicCacheToken` with a server-owned fine-grained PAT or GitHub App token so the backend can verify allowlisted public visibility and refresh that cache without using anonymous quota or user tokens. Public cache entries and last-good snapshots are persisted in the Aspire-managed `github-cache` Blob container, which runs as Azurite locally and Azure Blob Storage when published. Local Azurite uses an Aspire data volume so cache snapshots survive container recreation; use the `clear-cache` resource command when you need to reset the local public cache.

## Notifications (PWA + Web Push)

The dashboard is an installable PWA that can deliver Web Push notifications when you are
added as a requested reviewer on an open PR in a watched repository — even when the app is
closed.

- **Install**: in a desktop Chromium browser use the install icon in the address bar; on
  Android use the browser "Install app" prompt. On **iOS/Safari, Web Push only works after
  you add the app to the Home Screen** ("Share → Add to Home Screen"); the in-app panel
  detects this and shows install guidance until then.
- **Enable**: sign in with GitHub, then use the **Notifications** panel in the footer to
  grant permission and subscribe. Toggle the `review_requested` preference or send a test
  push from there. v1 only fires for `review_requested`; additional triggers are planned.
- **Single replica**: while notifications are enabled the server runs as exactly one replica
  (`MinReplicas = MaxReplicas = 1`) so the in-process detector is a single writer for the
  per-user dedupe state. Horizontal scaling needs single-leader election (e.g. an Azure Blob
  lease) and is intentionally out of scope for v1.

### VAPID keys

Web Push is signed with a one-time VAPID key pair and is **opt-in**: if keys are absent the
server treats push as disabled and every push path no-ops, so local development without keys
keeps working. Generate a key pair once (for example with `npx web-push generate-vapid-keys`,
which emits base64url `publicKey`/`privateKey` values), then configure:

- `WebPush:Enabled` = `true`
- `WebPush:PublicKey` = the base64url public key (shipped to the browser)
- `WebPush:PrivateKey` = the base64url private key (**secret**)
- `WebPush:Subject` = a `mailto:` or `https` contact URL
- `WebPush:KeyId` = optional key id/version; lets the client detect a rotation and re-subscribe

For local development, store these with user-secrets on the **Server** project:

```bash
dotnet user-secrets --project pr-timeline-app.Server set "WebPush:Enabled" "true"
dotnet user-secrets --project pr-timeline-app.Server set "WebPush:PublicKey" "<public-key>"
dotnet user-secrets --project pr-timeline-app.Server set "WebPush:PrivateKey" "<private-key>"
dotnet user-secrets --project pr-timeline-app.Server set "WebPush:Subject" "mailto:you@example.com"
```

Regenerating the dev key pair invalidates existing local subscriptions; re-subscribe from
the Notifications panel afterward.

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
export Parameters__web_push_public_key=<vapid-public-key>
export Parameters__web_push_private_key=<vapid-private-key>
export Parameters__web_push_subject=mailto:you@example.com
export Parameters__web_push_key_id=<vapid-key-id>

aspire deploy
```

After the first deploy, set the GitHub OAuth callback URL to `https://<aca-fqdn>/signin-github`.
