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

## Build

```bash
dotnet build pr-timeline-app.slnx
npm --prefix frontend run build -- --mode development
```
