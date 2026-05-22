# PR Timeline App

Local Aspire app for triaging GitHub pull requests across the Aspire repos. The dashboard highlights the PRs that need attention, prioritizes older review debt, and provides signal-focused timeline details instead of a raw comment reader.

## Projects

- `apphost.cs` - Aspire AppHost that runs the backend and Vite frontend.
- `pr-timeline-app.Server/` - ASP.NET Core API proxy for GitHub PR data, review state, timelines, and GitHub Device Flow login.
- `frontend/` - React/Vite dashboard UI.

## Run locally

```bash
aspire run --apphost apphost.cs
```

The frontend is served at <http://localhost:5173/>. API requests are proxied to the server through the Aspire-provided `SERVER_HTTP` or `SERVER_HTTPS` environment variable.

## GitHub authentication

The app can use an existing local token from `GITHUB_TOKEN`, `GH_TOKEN`, or `gh auth token`.

To enable in-app GitHub login, set `GITHUB_CLIENT_ID` to a GitHub OAuth App client ID with Device Flow enabled before starting the AppHost.

## Build

```bash
dotnet build pr-timeline-app.slnx
npm --prefix frontend run build -- --mode development
```

## Verify

Run the full local regression check before and after backend refactors:

```bash
./scripts/verify.sh
```
