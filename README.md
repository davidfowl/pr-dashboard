# PR Timeline App

Aspire-powered GitHub PR dashboard for the Aspire team to triage pull requests across the Aspire repos. The dashboard loads one or more repositories, ranks review work by urgency, personalizes a "For me" queue when signed in, and shows timeline details that are easier to scan than a raw GitHub comment stream.

By default the UI watches:

- `microsoft/aspire`
- `microsoft/aspire.dev`
- `microsoft/dcp`
- `CommunityToolkit/Aspire`

You can replace those with any comma-separated `owner/repo` list in the dashboard.

## Projects

- `pr-timeline-app.AppHost/` - Aspire AppHost that coordinates the backend and Vite frontend for development, publishes the frontend into the backend for deployment, and defines the Azure Container Apps environment.
- `pr-timeline-app.Server/` - ASP.NET Core API for GitHub pull requests, review state, timelines, OAuth login, development token fallback, and published static files.
- `frontend/` - React/Vite dashboard UI.
- `pr-timeline-app.Tests/` - Aspire-backed smoke tests for authentication and API validation.

## Prerequisites

- .NET 10 SDK preview
- Aspire CLI from the dev channel
- Node.js `20.19+`, `22.12+`, or newer
- Optional for local development: GitHub CLI (`gh`) authenticated with `gh auth login`
- Required for manual Azure deployment: Azure CLI

## Run locally

Install frontend dependencies once:

```bash
npm --prefix frontend ci
```

Start the app with Aspire:

```bash
aspire start
```

The AppHost path is configured in `aspire.config.json`. The Vite frontend is served at <http://localhost:5173/>. API requests under `/api` are proxied to the ASP.NET Core server through the Aspire-provided `SERVER_HTTPS` or `SERVER_HTTP` environment variable. The server endpoint is also shown in the Aspire dashboard.

## GitHub authentication

In development, the backend can call GitHub with:

1. An in-app OAuth session, when `GITHUB_CLIENT_ID` and `GITHUB_CLIENT_SECRET` are set.
2. `GITHUB_TOKEN`.
3. `GH_TOKEN`.
4. `gh auth token`.

Outside Development, OAuth is required and the server fails startup unless `GITHUB_CLIENT_ID` and `GITHUB_CLIENT_SECRET` are configured. The OAuth callback path is `/signin-github`, for example `https://<app-host>/signin-github`.

The app requests the `repo` and `read:org` scopes.

## API endpoints

- `GET /api/github/auth-status`
- `GET /api/github/login?returnUrl=/...`
- `POST /api/github/logout`
- `GET /api/github/pulls?repo=owner/repo&state=open|closed|all`
- `POST /api/github/pulls/checks?repo=owner/repo`
- `GET /api/github/pulls/{number}/timeline?repo=owner/repo`

If `repo` is omitted, the backend defaults to `microsoft/aspire`.

Each pull request in `/api/github/pulls` and the `/timeline` response carries a `checks` object that rolls up GitHub's Check Runs and legacy combined-statuses for the PR's head commit:

```jsonc
{
  "checks": {
    "state": "unknown | success | failure | pending | none",
    "totalCount": 0,
    "successCount": 0,
    "failureCount": 0,
    "pendingCount": 0,
    "neutralCount": 0,
    "skippedCount": 0,
    "completedAt": "2026-05-26T15:00:00Z",
    "failingChecks": [
      { "name": "tests", "conclusion": "failure", "htmlUrl": "https://..." }
    ]
  }
}
```

PR list responses mark open PR checks as `unknown` initially so the dashboard can render without waiting for every PR's CI. The browser asks `POST /api/github/pulls/checks` only for open PRs that become visible, and the server enriches those requested head SHAs with bounded concurrency. Closed/merged PRs are skipped. The `/timeline` response still includes checks for the selected PR plus `mergeableState` (`clean | dirty | blocked | behind | unstable | unknown`) so the detail view can surface merge-conflict / branch-protection blockers.

## Build and lint

```bash
npm --prefix frontend ci
npm --prefix frontend run lint
npm --prefix frontend run build

dotnet restore pr-timeline-app.slnx
dotnet build pr-timeline-app.slnx --no-restore
```

## Aspire smoke tests

The test project contains Aspire-backed smoke tests for authentication and API validation. They start the AppHost with the frontend disabled.

```bash
dotnet test pr-timeline-app.slnx --no-build
```

## Deploy to Azure Container Apps

The AppHost is configured with an Azure Container Apps environment named `aca`. In publish/deploy mode, the ASP.NET Core server is the public entrypoint and serves the built Vite frontend from `wwwroot`.

The generated Container Apps deployment uses the Consumption workload profile and allows the server app to scale to zero replicas when idle. This keeps idle compute cost low while preserving the same public endpoint, with the tradeoff that the first request after an idle period can wait for a cold start.

Create a GitHub OAuth App before deploying. For a manual deployment, authenticate with Azure CLI and provide the Azure target plus GitHub OAuth parameters:

```bash
az login

export Azure__SubscriptionId=<subscription-id>
export Azure__Location=<azure-region>
export Azure__ResourceGroup=<resource-group>
export Parameters__github_client_id=<oauth-app-client-id>
export Parameters__github_client_secret=<oauth-app-client-secret>

aspire deploy
```

After the first deploy prints the Container App URL, update the GitHub OAuth App callback URL to `https://<aca-fqdn>/signin-github`, then redeploy or restart the app and test sign-in.

## Automated deployment

The `.github/workflows/deploy.yml` workflow deploys to Azure Container Apps after the `CI` workflow succeeds on `main`. It can also be run manually.

Configure a GitHub Environment named `production` with these values:

| Type | Name | Value |
|---|---|---|
| Secret | `AZURE_CLIENT_ID` | Entra app registration client ID used by GitHub Actions OIDC |
| Secret | `AZURE_TENANT_ID` | Azure tenant ID |
| Secret | `AZURE_SUBSCRIPTION_ID` | Azure subscription ID |
| Secret | `OAUTH_GITHUB_CLIENT_SECRET` | GitHub OAuth App client secret |
| Variable | `AZURE_LOCATION` | Azure region, for example `westus2` |
| Variable | `AZURE_RESOURCE_GROUP` | Azure resource group, for example `rg-pr-timeline-app-aca` |
| Variable | `OAUTH_GITHUB_CLIENT_ID` | GitHub OAuth App client ID |

Create a federated credential on the Entra app registration with:

```text
issuer: https://token.actions.githubusercontent.com
subject: repo:davidfowl/pr-dashboard:environment:production
audience: api://AzureADTokenExchange
```

Grant that identity `Contributor` and `User Access Administrator` on the deployment resource group so Aspire can provision resources and role assignments.
