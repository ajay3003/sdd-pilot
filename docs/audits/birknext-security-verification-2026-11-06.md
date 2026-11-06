# BirkNext Security Verification

**Execution baseline:** `008-traceability-first` / `546584f1`; **final observed HEAD:** `63ee0980`. The intervening topology worklist commit was not verified. Static code inspection plus a current-source focused test run; no live external targets, probes, or non-loopback listener were used.

## Security observations

| Surface | Source evidence | Verification result |
|---|---|---|
| Listener configuration | `AIAssisted/backend/BirkNext.Api/Program.cs:84-94` gathers `urls` and Kestrel endpoint URLs and rejects detected widened addresses absent the explicit `Hosting:AllowNonLoopbackBinding` setting. | Test source includes loopback/widened cases (`AIAssisted/backend/BirkNext.Api.Tests/Controllers/LocalSessionSecurityTests.cs:39-57`); these tests ran within the focused 90/90 pass. Effective socket and all host configuration precedence remain unverified. |
| Authentication registration | JWT bearer plus local-session scheme; authority/audience are configuration-driven and HTTPS metadata is required (`Program.cs:104-113`). Named permission policies begin at `117-124`. | Configured; no live identity provider or token was used. |
| API/GraphQL request guard | Middleware is ordered after authentication and before authorization; mapped controllers and GraphQL follow (`Program.cs:917-925`). Guard checks `/api` and `/graphql`, Host loopback, authenticated local session/bearer, and cookie-authenticated unsafe request Origin/client marker (`AIAssisted/backend/BirkNext.Api/Services/LocalSecurity/LocalRequestGuard.cs:35-72`). | Source path confirmed. The middleware is a local request/session boundary, not per-resource project authorization. Route-level policies and service checks were not exhaustively inventoried in this verification pass. |
| Bootstrap/extension exceptions | Session bootstrap and Browser Companion extension routes bypass the central session requirement but are delegated to their own checks (`LocalRequestGuard.cs:32-47,75-87`). | Code path identified; no browser extension pairing/session runtime test in this pass. |
| Local launch-code storage | Directory/path initialization and read/write path in `AIAssisted/backend/BirkNext.Api/Services/LocalSecurity/LocalSessionService.cs:33-45,109-126`. | No explicit ACL setting located in service; actual inherited Windows ACL/package permissions not checked. No actual code read. |
| Workspace identity | `AIAssisted/backend/BirkNext.Api/Services/WorkspacePersistenceService.cs:14-15`; list endpoint fixed owner at `AIAssisted/backend/BirkNext.Api/Controllers/WorkspacePersistenceController.cs:127-142`; ID-based mutations at `151-205`. | Multi-user isolation is unverified; conditional risk, not demonstrated exploit. |

## Focused test outcome

Current-source test project compilation followed by selected tests yielded **90 passed, 0 failed, 0 skipped**. Filter included `LocalSessionSecurityTests`, `ActiveEventAuthorizationTests`, `AltinnTestingAuthorizationTests`, `LocalTrustedExecutionTests`, and `ProjectImportServiceTests`. It did not test every API endpoint, browser-origin scenario, actual listener socket, packaged ACL, or real Entra identity.

## Security disposition

- No new confirmed security defect was reproduced in this bounded pass.
- BQA-001 remains a conditional configuration-coverage question.
- BQA-002 remains a package/ACL question.
- BQA-008 remains a conditional shared-deployment identity-isolation risk.
- The prior authorization gap must not be treated as closed globally from these focused suites; only selected capability policy/test areas were exercised.
- CORS is not treated as authentication; this pass relies on the central session/bearer guard evidence above and makes no assertion that every route has resource ownership enforcement.

Further verification should use an isolated local host and synthetic identity/workspace fixtures after the current UI/worktree changes stabilize.

## Verification completion — current source `7410e179` (2026-11-06)

The sections above are the **historical** audit at `546584f1` (final observed HEAD `63ee0980`). They are kept unchanged. This section records the
completion run against the current source. Source state verified: branch `008-traceability-first`, HEAD `7410e1793be162bd589762c9e65749d0f8621c46`; the isolated build worktree's
3,541 source files (`AIAssisted/`, `scripts/`) are byte-identical to that commit (blob hashes compared; only 7 `AIAssisted/docs/*.md` reports absent).
Uncommitted user files left untouched: `AIAssisted/backend/BirkNext.Api/appsettings.Local.json`, `AIAssisted/testdata/`. No push, no deployment,
no external system (M2LB, TT02, Altinn, MU, MM, Bufdata, Azure DevOps) contacted; browser runs abort every non-localhost request.

### Security results

- **Launch-code storage (BQA-002): confirmed and fixed** (`37b918f7`). Observed before the fix: `App_Data\local-session` in the dev install grants Modify to
  five foreign SIDs and `CodexSandboxUsers` by inheritance; `C:\` grants *Authenticated Users* Modify to new subfolders (the package README's
  `C:\BirkNext`). After: protected DACL with only the current user and SYSTEM; existing installs repaired on start (observed on the isolated API).
  The code value was never read or printed.
- **Listener binding (BQA-001): partial.** Startup refuses widened `urls`/Kestrel URLs unless explicitly allowed (unit tests pass). All four running
  listeners are loopback-only. A live start with `0.0.0.0` was **not run** — the permission policy denied it.
- **Session/CSRF/origin:** `sec-e2e` 12/12 (unconnected browser asked for the code, wrong code refused, right code connects, CSRF/origin checks).
- **Focused security/import tests:** 92/92 (the audit's 90 + 2 ACL tests). Full backend run includes all security suites (see test report).
- **No secrets:** fixtures and browser checks assert no credential markers; no credential was created, reused or transferred between engines.
- **Warnings with a security reading:** `EF1002` (2) interpolates only EF-model table names delimited by `ISqlGenerationHelper` — no user input,
  false positive; `CA1416` (2) is guarded by `OperatingSystem.IsWindows()`; `CS0612` (8) are obsolete Playwright options in browser-auth helpers.
