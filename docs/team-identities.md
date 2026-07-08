# Team identities: multi-identity per member

## Problem

Two things are conflated in the current dashboard:

1. **Repo → identity (token routing).** Which GitHub credential can *read* a
   repository. EMU-only repos such as `devdiv-microsoft/aspire-1p` are invisible
   to a public identity's token (`404 Not Found`), so their PRs/issues are
   skipped.
2. **Login → person (attribution/grouping).** The dashboard groups work by
   person — "your PRs", "needs your review", core-team ownership. A team member
   who acts under multiple logins (e.g. `radical` publicly and `ankj_microsoft`
   in EMU repos) is currently treated as two different people.

Today's mechanisms are insufficient for both:

- Token routing was a single global identity (one `gh` account or one OAuth
  session applied to every repo). A per-repo override map exists but is keyed to
  raw logins and does not model identity *kinds*.
- Attribution uses `CoreTeamMembers` plus a suffix heuristic
  (`CoreTeamMemberAliasSuffixes: ["_microsoft"]`) that only folds
  `<primary>_microsoft` into `<primary>`. It cannot express an alias whose base
  handle differs from the primary — e.g. `ankj_microsoft` ↔ `radical`.

## Model

A team member is the source of truth and owns multiple identities, each tagged
with a **kind**. Repositories are routed by kind, not by a specific login, so the
same mapping works for every developer (each supplies their own login per kind).

```jsonc
"TeamIdentity": {
  "DefaultKind": "public",
  "RepositoryKinds": { "devdiv-microsoft/aspire-1p": "microsoft" },
  "Members": [
    { "name": "radical",
      "identities": [
        { "login": "radical",        "kind": "public"    },
        { "login": "ankj_microsoft", "kind": "microsoft" }
      ] }
  ],
  "CurrentDeveloper": "radical"   // development-only; the member "I" am
}
```

Two derived structures feed the two concerns:

- **Alias → member** (attribution): every `identities[].login` maps to its
  member. `ankj_microsoft` → member `radical`. Grouping/dedupe canonicalize a
  login through this map before keying, so all of a person's logins collapse to
  one identity.
- **Kind → login for the current developer** (token routing): a repo's kind
  (`RepositoryKinds[repo]` or `DefaultKind`) selects which of the current
  developer's identities supplies the token.

## Token routing (development)

`GitHubTokenProvider.GetTokenAsync(RepositoryName?, ct)` resolves, in order:

1. OAuth cookie token (unchanged).
2. Suppressed (post-logout) or non-Development → no fallback (unchanged).
3. **Dev-account dropdown pick** (`selectedDevelopmentGitHubUser`): use that exact
   `gh` login for every repo. Preserved as a raw "act as this account" debug
   override.
4. **Unified model** (dropdown on "Default token source"): current developer =
   `CurrentDeveloper` (or the sole configured member). `kind =
   RepositoryKinds[repo] ?? DefaultKind`; `login = member.identity(kind) ??
   member.identity(DefaultKind)`; return that login's `gh` token. A configured
   kind with no matching identity logs a warning and falls back to the default
   kind.
5. Env token (`GITHUB_TOKEN`/`GH_TOKEN`), then the active `gh` account
   (unchanged) when the model is not configured.

This subsumes the earlier standalone "persistent default identity" idea: the
default identity is the current developer's `DefaultKind` login, declared once in
config.

Identities are local `gh` accounts, so `TeamIdentity:CurrentDeveloper` and any
personal identity logins live in **user-secrets**, not committed config.

## Attribution (server + frontend)

Attribution is computed in two mirrored places, both fed by the dashboard-config
payload (`DashboardConfigRoutes` → frontend `dashboardConfig`):

- Server: `AgentReviewQueue.MatchingCoreTeamMember` / `ActorIdentityKey`.
- Frontend: `models.ts` `matchingCoreTeamMember` / `actorIdentityKey`, and
  ownership/focus grouping in `focusQueue.ts`.

The config payload gains an **alias map** (`aliasLogin → primaryLogin`) derived
from `TeamIdentity.Members`. Both sides canonicalize a login through the alias map
before computing the identity key, so every login of a person collapses to one
key everywhere grouping and dedupe happen. The existing `_microsoft` suffix
heuristic stays as a fallback for members without an explicit mapping.

## Delivery

- **P1a — token routing (server):** introduce `TeamIdentity` options; rework
  `GitHubTokenProvider` to kind-based routing with a persistent default; replace
  the standalone per-repo login map. Tests + Playwright validation.
- **P1b — attribution (server + frontend):** surface the alias map in the config
  payload; canonicalize logins in server and frontend matching so multi-identity
  members group as one person. Tests.

## Out of scope (future)

- **P2 — production multi-login.** Holding two live GitHub identities per session
  needs a per-session multi-token store, a second OAuth "connect account" flow
  with per-identity connect/disconnect, and a decision on OAuth `repo` scope and
  EMU SSO for reading private/internal repos. The team-wide `TeamIdentity` model
  (members, kinds, repo→kind) is the config foundation for it; only the *token
  acquisition and storage* is deferred.
