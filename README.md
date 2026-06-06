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
