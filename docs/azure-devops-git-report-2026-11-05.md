# Unified Source Management & Azure DevOps Git: report (2026-11-05)

Branch `008-traceability-first`, not pushed.

This builds on b3522e92; see `docs/unified-source-import-report-2026-11-04.md`, whose capabilities are not repeated here.

> **Live verification: none.** No Microsoft sign-in and no Azure DevOps call was made. Real access requires an explicit action by you, and the M2LB copy has `AzureEnvironment:Enabled=false` and no client id. Everything that talks to Microsoft or Azure DevOps remains **Not live-tested** and **Externally blocked** until an administrator configures the Entra app registration and a person signs in.

## A. Audit (implementation matrix)

| Capability (brief) | Existing (b3522e92) | Gap verified | Action |
|---|---|---|---|
| One organization, delegated Entra sign-in (MFA/CA), device-code fallback, diagnostics | Yes: backend MSAL with the dedicated Edge profile (auth code + PKCE through the system browser), AADSTS classification | In-browser MSAL popup | Kept by design; no change |
| Connection states | Yes (`GitConnectionState`) | Labels differed from the brief's vocabulary | Relabelled: Ready to connect, Connecting, Authentication expired |
| Signed in vs Azure DevOps access | `LastTest` existed | The row read "Last Git test · Not tested" even when signed in | "Azure DevOps access": "Verified by the read-only Git test…" or "Not verified yet — a Microsoft sign-in alone does not prove repository access" |
| Discovery (organizations, projects with paging, several repositories, a branch per repository, pinned commit) | Yes | — | Kept |
| Update states | Free text ("Update available" / "Up to date" / problem) | No taxonomy. A never-imported repository read "Update available" | `GitUpdateState` (Not checked, Up to date, Update available, Not imported, Unable to compare, Branch unavailable, Authentication required, Check failed) via `GitUpdateStates.Of`. The failure kind is recorded on the check |
| Import exactly the checked revision | **No.** "Import revision" imported the branch head at import time, so a branch that moved after the check was imported silently | Defect | `GitUpdateStates.CommitToImport`: the checked commit (or the pinned one); the button names it ("Import revision 01234567", or "Import branch head" when unchecked) |
| Bulk check | No | — | "Check all linked repositories (N)": read-only, at most 3 at a time. A results table (Azure DevOps project, repository, branch, imported revision, remote revision, status); partial success is normal; nothing is imported |
| ZIP + Git in one project, ZIP↔Git matching (same content / commit / structure; never by name), links per BirkNext project, history | Yes | — | Kept. Source Management (NEXT35) shows both origins |
| Snapshots per revision (shared registry), immutable | Yes, via the shared Project Import pipeline | — | Kept |
| Unattended sync, PAT, service principal | Not implemented (out of scope) | — | — |

## B. Authentication

- Unchanged: delegated, interactive, tokens in memory only, no PAT, nothing contacted when a page opens.
- The card now separates **Status** (the Microsoft sign-in) from **Azure DevOps access**, which only the explicit read-only test verifies.

## C. Discovery

Unchanged from b3522e92: organizations, projects with paging, repositories, branches latest first, pinned commit.

## D. Updates

- **Classification:** compared by commit, never by timestamp. The failure kind (`BranchNotFound`/`RevisionNotFound` → Branch unavailable; `SignInRequired`/`AccessDenied` → Authentication required; any other → Check failed) is stored on the check (`GitUpdateCheck.Failure`, optional, so stored JSON stays compatible).
- **Import pinning:** an import after a check takes exactly the checked commit, pinned by the existing `GitImportRequest.Commit`, so a moved branch is never imported silently.
- **Bulk check:** bounded concurrency (3), each row keeps its own outcome, and a check never imports, creates a snapshot or changes a pointer or association (asserted).

## E. Pipeline

Unchanged:

- **Acquisition:** ZIP adapter or Git adapter, then the same Project Import pipeline.
- **Downstream:** Source Analysis snapshot, Snapshot Registry, Project Evidence.

## F. UI

- **Git Sources:** state labels; Azure DevOps access row; configuration health per prerequisite. On the copy: enabled Missing, client id Missing, tenant Ok, redirect Info, method Ok, consent Info, Azure DevOps NotChecked.
- **Source Management:**
  - the update column shows the shared state;
  - the import button names the revision it takes;
  - "Check all linked repositories" with its results table.

## G. Security

- No token, password or PAT is stored or shown; markup is checked for "Bearer" and "eyJ".
- Read-only Azure DevOps APIs.
- Nothing is called when a page opens: in the browser, zero Git check, repository, branch or test requests on opening Source Management.

## H. Tests

| Suite | Test | What it covers |
|---|---|---|
| Frontend `SourceManagementTests` | Update-state classification (8 cases) | Not checked … Check failed |
| Frontend `SourceManagementTests` | Import takes the checked commit | Checked → that commit; unchecked → branch head; failed check → none; pinned wins; the button names the revision |
| Frontend `SourceManagementTests` | Check all | Every link checked; outcomes Not imported and Branch unavailable; nothing imported |
| Frontend `SourceManagementTests` | Existing single-check test | Now expects "Not imported — remote 01234567" (never imported is not an update) |
| Frontend `GitSourcesUiTests` | Azure DevOps access row | When signed in but untested: "Not verified yet — a Microsoft sign-in alone does not prove repository access" |
| Backend `GitSourceLinksTests` | Real service against fakes | A check yields Up to date, no failure, and the import commit is the checked head |

**Flaky test hardened:** `AuthenticationPaneSemanticsTests` clicked a profile chip found just before an asynchronous re-render (stale event handler). It now re-finds the chip and retries only on that exception (3/3 green alone, full suite green).

| Run | Result |
|---|---|
| Full frontend suite | **8155 passed, 0 failed** |
| Full backend suite | **5906 passed, 13 skipped, 0 failed** |

**Real browser** (`scratchpad/git-e2e.cjs`, M2LB copy): **6/6 checks passed.**

- Git Sources shows "Not configured — AzureEnvironment:Enabled is false…".
- Azure DevOps access is shown apart from the sign-in and is never assumed.
- Each configuration prerequisite is named, with no fake client id and no secret.
- Zero Git requests on opening Source Management.
- No external host; no page errors.

## I. Gaps

| Item | Status |
|---|---|
| Live Microsoft sign-in, Azure DevOps discovery, update check and import against a real organization | Externally blocked: needs the Entra registration and your explicit sign-in |
| In-browser MSAL popup/redirect | Not implemented (backend MSAL with system browser plus device code, by design) |
| "Refresh branches" with remote HEAD per branch inside Source Management; renamed or deleted branch reconciliation beyond "Branch unavailable" | Not implemented |
| Import summary with confirmation when HEAD moved after the check | Partial: the checked commit is imported; there is no separate re-confirm dialog |
| Multi-branch per repository as separate link histories | Not implemented (one branch per link, as before) |
