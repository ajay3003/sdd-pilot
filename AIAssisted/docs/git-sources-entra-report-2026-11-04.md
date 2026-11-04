# Git Sources (Azure DevOps over Microsoft Entra ID) — report (2026-11-04)

Branch `008-traceability-first`. Nothing was pushed.

**No real Azure DevOps or Microsoft call was made** while building or testing this. Every test uses a fake HTTP handler and a fake token broker. The browser check ran against localhost with sign-in not configured.

## GIT SOURCE & AUTHENTICATION OWNERSHIP AUDIT MAP

| Component | Owner / persistence | Auth handling | Reuse? | Security / duplication risk | Decision |
|---|---|---|---|---|---|
| Azure Environment sign-in (`MsalAzureTokenBroker`, `AzureSignInService`, `DedicatedEdgeSignInBrowser`) | Azure Environment Analysis; tokens in MSAL memory only | Entra public client, interactive (dedicated Edge profile) or device code; no secret, no password | **Yes** | A second auth stack would duplicate it | **Extended**: `InteractiveForAsync` / `SilentForAsync(scopes)` on the same MSAL app and in-memory account cache, for the Azure DevOps resource |
| Frontend MSAL (`AddMsalAuthentication`) | Active Event / integration writes | SPA authorization code + PKCE for the BirkNext API | No (protects the BirkNext API, not Azure DevOps) | Tokens would reach the browser | Not used for Git |
| Change Traceability ADO (`ADO_PAT`, `appsettings.Local.json`) | Platform settings | PAT | No (PAT is not the primary workflow) | PAT in config | Left as is; not a fallback for Git Sources |
| Delivery Azure DevOps import | Delivery | Its own | No | — | Unchanged |
| Project Import (`ProjectImportService`, staging store, `IqrSourceArchiveReader`) | Project Import / Source Analysis | — | **Yes** | A separate Git pipeline would fork analysis | **Reused**: a Git commit archive goes through the same `Preview` → `Commit` |
| Source snapshot provenance (`IqrSourceSnapshot.Commit/Branch`, `ProjectImportProvenance`) | Source Analysis | — | **Yes** | — | **Extended**: `ProjectImportProvenance.Git` (repository, branch, **commit SHA**, retrieved at); the snapshot's Commit/Branch come from the provider |
| System Settings sections (`DocumentationCatalog.SettingsGroups`) | Admin | — | Yes | — | New section **Analysis → Git Sources** |
| Local settings | — | — | — | Must hold no secret | New `git-sources.json` (organization URL, tracked branches, last test summary) in `%LOCALAPPDATA%\BirkNext`, overridable with `GitSources:SettingsDirectory` |
| Browser / local storage | — | — | — | — | Nothing Git-related is stored in the browser |

## What was built

### Authentication

- **Flow:** Microsoft Entra ID **delegated**, public client, interactive sign-in in the dedicated BirkNext Edge profile (authorization code + PKCE handled by MSAL).
- **App registration:** the same app as Azure Environment Analysis (`AzureEnvironment:Enabled` + `ClientId`), with the delegated Azure DevOps permission (`499b84ac-…/user_impersonation`).
- **Tokens:** in MSAL's in-memory cache only (lost on restart). Never logged, persisted, put in URLs or returned to the browser.
- **Why this flow:** BirkNext.Api runs locally beside the person; a backend public client with the system browser is the supported MSAL pattern, needs no secret, and reuses the existing, reviewed sign-in.

**States**

| State | When |
|---|---|
| Not configured | No app registration; the requirements are listed |
| Not connected | Never signed in this session |
| Signing in | Interactive sign-in in progress |
| Connected | Account shown |
| **Reconnect required** | A silent token is no longer available after a connection; this is not configuration loss |
| Connection failed | Sign-in did not complete |

**Disconnect** removes the cached Microsoft account. That also signs out Azure Environment Analysis (one Microsoft session), and the UI says so. Settings, tracked branches, imported projects and snapshots stay.

### Azure DevOps (read-only, `AzureDevOpsGitClient`)

- **Calls:** repositories (`_apis/git/repositories`); branches with head commit, date, author and message (`stats/branches`), sorted **latest first**; the archive of **one commit by SHA** (`items?…versionType=commit&$format=zip`), bounded to 512 MB.
- **Transport:** no redirects followed, no cookies; the bearer token goes in a header, never in a URL.
- **Diagnostic failures** (`GitFailureKind`), never one "connection failed":
  - sign-in required (401, or the 203 HTML sign-in page);
  - access denied (403);
  - organization / repository / branch not found;
  - unreachable;
  - too large;
  - unexpected.

### System Settings → Git Sources

**Connection card:** provider, authentication, status, account, last Git test.

**Organization URL:** validated to `https://dev.azure.com/{org}` or `https://{org}.visualstudio.com`. A different organization clears the last test.

**Test Git connection** (explicit) runs these steps, each with a result, duration and safe message:
1. Microsoft Entra authentication;
2. organization and repository discovery (count);
3. branch discovery;
4. commit metadata;
5. local storage.

Overall: Ready / Not ready with the first failure. The test **never imports, creates a snapshot or downloads an archive**. Only the safe summary is kept.

**Repositories and branches:** loaded only on request; branches latest first; Track / Untrack branches.

### Import Project → "Import from an Azure DevOps Git repository"

- Opening the page reads nothing remote. Opening the section reads only the local sign-in state.
- Repositories and branches load on request (with a branch filter). Then: "Preview import of this branch's current commit".
- The backend resolves the branch to its **commit SHA**, downloads exactly that commit and stages it through the **same Project Import preview**.
- From there it is a ZIP import: document roles, duplicate check, commit, activation.
- The preview shows: "From Azure DevOps: project / repository · branch · commit `sha` · retrieved … The commit, not the branch, is what is imported."
- On commit, `ProjectImportProvenance.Git` is stored with the snapshot, and the snapshot's `Commit` / `Branch` are the provider's values (never guessed).
- The archive name (`repo@branch-sha8.zip`) is readable only; **the SHA is the identity**.

## Tests (no network)

**Backend** `GitSourcesTests` (9 tests, 20 cases):
- organization URL normalization (8 cases);
- branch name / latest-first / archive name / SHA rules;
- status makes **no HTTP call** and says Reconnect required after expiry;
- settings validated, a new organization clears the last test, and the settings file never contains the token;
- the Git test verifies every step, makes **GET only**, downloads no archive, puts the token only in the header and creates no snapshot;
- a failed sign-in skips the rest with "Sign-in required" and makes no HTTP call;
- 401 / 403 / 404 / 203 HTML / 500 map to their diagnostic kinds;
- import downloads by **SHA** (`versionType=commit`), stages through Project Import, then commit stamps `Provenance.Git` and snapshot Commit/Branch = SHA / branch;
- a deleted branch gives `BranchNotFound`;
- disconnect signs out and keeps settings and tracked branches.

**Frontend** `GitSourcesUiTests` (5):
- the card reads only status on open (no repositories, no test), shows "Not tested" (configured ≠ verified) and runs the test on request;
- not configured lists requirements and disables the test;
- Reconnect is offered;
- the picker reads nothing until opened, loads repositories / branches only on click, needs a chosen branch and hands over (request, label);
- not connected links to settings.

**Related suites:** Project Import / Azure Environment / Azure sign-in: 157 passed. Project Import / System Settings / docs catalog frontend: 420 passed.

**Full suites:** see the commit message and the final report.

## Not done (follow-ups)

- **Source Sets** (several repositories / branches as one baseline), update checks ("update available / unchanged") and **configured automatic import**.
- **Managed Git storage** (fetch / clone cache), the Git executable and credential helper. Today the provider's commit archive is used; nothing is cloned.
- **Device-code sign-in** for Git Sources (the broker supports it for ARM; interactive only here). Editing the app registration in the UI (it comes from configuration).
- **Durability:** the staged Git revision is memory-only. A stage restored after a backend restart commits as a plain archive.
- **History and housekeeping:** import history by SHA / re-import of the same SHA indicator, retention, and organization / project discovery across organizations.
- **A real end-to-end run against Azure DevOps** with your account. That needs your explicit sign-in on your machine; it was deliberately not done here.

## Confirmations

- Real Azure DevOps / Microsoft connectivity happens only through an explicit action (Sign in, Test Git connection, Load / Refresh repositories, branch choice, Preview import), never because a page opened.
- No Microsoft password, MFA code, PAT or token is stored. Tokens are MSAL in-memory only; responses and settings carry none.
- Read-only against the remote: no commit, push, branch or PR operations.
- Git and ZIP imports converge on the same Project Import / Source Analysis pipeline; there is no separate Git analysis.
- The commit SHA is the revision identity; a branch name is never treated as one.
- No real external traffic in tests or acceptance; `appsettings.Local.json` not intentionally modified; user Test Data preserved; nothing pushed.
