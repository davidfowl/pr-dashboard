# PR Timeline App

Local Aspire app for triaging GitHub pull requests across the Aspire repos. The dashboard highlights the PRs that need attention, prioritizes older review debt, and provides signal-focused timeline details instead of a raw comment reader.

## Projects

- `pr-timeline-app.AppHost/` - Aspire AppHost that runs the backend and Vite frontend.
- `pr-timeline-app.Server/` - ASP.NET Core API proxy for GitHub PR data, review state, timelines, and GitHub Device Flow login.
- `frontend/` - React/Vite dashboard UI.

## Run locally

```bash
aspire run --apphost pr-timeline-app.AppHost/pr-timeline-app.AppHost.csproj
```

The frontend is served at <http://localhost:5173/>. API requests are proxied to the server through the Aspire-provided `SERVER_HTTP` or `SERVER_HTTPS` environment variable.

## GitHub authentication

In development, the app can use an existing local token from `GITHUB_TOKEN`, `GH_TOKEN`, or `gh auth token`. To use in-app GitHub login locally, set `GITHUB_CLIENT_ID` and `GITHUB_CLIENT_SECRET` on the server.

Outside development, GitHub OAuth is required. Create a GitHub OAuth App and provide both publish/deploy parameters:

```bash
export Parameters__github_client_id=...
export Parameters__github_client_secret=...
```

Configure the OAuth App callback URL to the backend callback path, for example `https://<app-host>/signin-github`.

## Deploy to Azure Container Apps

The AppHost is configured with an Azure Container Apps environment named `aca`. The ASP.NET Core server is the public production entrypoint and serves the built Vite frontend from `wwwroot`.

For a manual deployment, authenticate with Azure CLI and provide the Azure target plus GitHub OAuth parameters:

```bash
az login

export Azure__SubscriptionId=<subscription-id>
export Azure__Location=<azure-region>
export Azure__ResourceGroup=<resource-group>
export Parameters__github_client_id=<oauth-app-client-id>
export Parameters__github_client_secret=<oauth-app-client-secret>

aspire deploy --apphost pr-timeline-app.AppHost/pr-timeline-app.AppHost.csproj
```

After the first deploy prints the Container App URL, update the GitHub OAuth App callback URL to `https://<aca-fqdn>/signin-github`, then test sign-in.

## Automated deployment

The `.github/workflows/deploy.yml` workflow deploys to Azure Container Apps on every push to `main` and can also be run manually.

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

## Build

```bash
dotnet build pr-timeline-app.slnx
npm --prefix frontend run build -- --mode development
```
