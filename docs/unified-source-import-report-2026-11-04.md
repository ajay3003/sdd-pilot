# Unified Source Import & Git Connection Manager — report (2026-11-04)

Branch `008-traceability-first`. Builds on #26 Git Sources (65101faf) and #27 hardening (37b027f7). Nothing was pushed.

Status legend:
- **Verified**: implemented and verified by automated tests against fakes.
- **Not live-tested**: implemented, but not exercised against real Microsoft or Azure DevOps.
- **Partial**
- **Not implemented**
- **Blocked**: blocked by external configuration.

> **Live verification:** none. No real Microsoft sign-in and no real Azure DevOps call was made in this work. Every Git path is tested against HTTP and MSAL fakes only. Until an administrator configures the Entra app registration and a person signs in, everything that talks to Microsoft or Azure DevOps is at most **Not live-tested**.

## A. Audit — what existed and what was missing

**Already existed**
- The ZIP Project Import pipeline: preview → staging → commit.
- The Shared Snapshot Registry (`IqrSourceSnapshot`).
- Multi-role Project Evidence bindings.
- #26 Git Sources:
  - one organization URL;
  - interactive Entra sign-in through the dedicated Edge profile;
  - a flat repository list and latest-first branches;
  - an explicit Test;
  - a branch-head import through the same Project Import pipeline, with `ProjectImportProvenance.Git` stamped.

**Gaps found**
- Configuration diagnostics: there was only a generic "not configured".
- Sign-in failure classification and device-code fallback.
- Discovery of organizations and projects, with paging.
- Selecting several repositories.
- Importing a pinned commit.
- Links between a repository and a BirkNext project, with several roles.
- Import history per repository.
- Update checks.
- Matching earlier ZIP snapshots to a repository.
- A two-method Import UI.
- A Source Management overview.

## B. Connection & configuration

| Capability | Status |
|---|---|
| Diagnostics: enabled, client id (GUID), tenant/authority, redirect URI, sign-in method, delegated consent, last sign-in, Azure DevOps access. Each check has a state and an administrator action; no secrets are shown. | Verified |
| Sign-in failures classified from AADSTS codes:<br>• consent (65001/90094/90095)<br>• redirect (50011/500113)<br>• app not found (700016)<br>• tenant (90002/50020)<br>• Conditional Access (53003/50076)<br>• cancellation | Verified |
| Device-code sign-in: the code is shown in Settings while waiting (`AwaitingDeviceCode`) | Verified with a fake broker; Not live-tested |
| Connect / reconnect / refresh / disconnect; `AccessDenied` state when Azure DevOps refuses access | Verified with fakes; Not live-tested |
| Tokens are held in memory only. No PAT. No passwords stored. Nothing is called just because a page opened. | Verified (status reads local state only) |
| In-browser MSAL popup/redirect | **Not implemented.** By design, sign-in uses the backend MSAL with the dedicated Edge profile, and device code is the fallback. |
| A real Entra app registration, consent and redirect URI | **Blocked** until an administrator configures them |

## C. Discovery & selection

| Capability | Status |
|---|---|
| Organizations discovered from the account's membership, merged with entered ones. When discovery fails, the entered organization is still offered and the reason is shown. | Verified with fakes; Not live-tested |
| Projects paged with the continuation token ("More projects"); repositories listed per project | Verified with fakes; Not live-tested |
| Several repositories at once; a branch per repository (latest first) or a specific 40-character commit, checked to exist | Verified (both UI and service) |
| Several roles per repository (`PackageEvidenceRole`), saved on the link | Verified |
| Stale-response protection in the wizard (generation counter); Retry on error; loading via `ReviewStageList` | Implemented. The generation counter is not separately tested. |

## D. Links, provenance & history

- A link is identified by `organization|repositoryId`. It is never identified by name.
- It is scoped to the BirkNext project (Target Environment).
- Repository metadata comes from Azure DevOps, not from the request.
- Removing a link never deletes snapshots or reports. **Verified.**
- When a Git import is committed, the commit SHA, branch, snapshot id and import id are appended to the link's history (`IGitImportRecorder`, called from `ProjectImportService`). The same commit twice is recorded once. **Verified end to end against the real `ProjectImportService` + in-memory DB.**
- The frontend import record keeps `Git` provenance. A ZIP never becomes Git.

## E. Updates

- **Check for updates** compares the branch head, or the pinned commit, with the last imported revision.
  - It never downloads or imports. **Verified:** the archive call count is unchanged.
  - It never switches the branch.
- **Import revision** starts the normal Project Import preview for the newer revision. ZIP rows offer **Upload new version**.
- Unattended or scheduled sync: **Not implemented** (out of scope; every action is explicit).

## F. ZIP ↔ Git matching

- Earlier snapshots without Git provenance are offered as candidates, in this order:
  1. **Same content** (archive SHA-256);
  2. **Same commit** (commit recorded in the archive);
  3. **Possible same structure** (≥ 80 % Jaccard of project-file paths below the root folder, at least two projects).
- A snapshot that matches only by name is never offered. **Verified.**
- Nothing is merged. The person chooses **Associate** or **Keep separate**; the decision is stored on the link and shown. **Verified.**
- A test found a crash: a SHA shorter than 12 characters made the evidence text throw. **Fixed.**

## G. UI

- **Import Project** offers two methods:
  - **Manual ZIP import**: unchanged, needs no sign-in.
  - **Azure DevOps Git**: wizard with organization → project → repositories → review.
  - Selected repositories become a queue, imported one at a time through the same review, with "Next repository" on the result.
  - The page links to **Manage sources**.
- **System Settings → Git Sources** adds:
  - a configuration-health panel and the device-code block;
  - an organization picker and save;
  - a list of linked sources.
- **Source Management** (`/sources`, new sidebar entry under Workspace) shows one row per imported package or linked repository:
  - origin, roles, snapshot, last import, update state;
  - actions: check, import revision, earlier imports, roles editor, upload new version, view evidence.
  - A test found a bug: the roles editor lost a ticked role on re-render. **Fixed.**
- **Activity Center:** Git imports appear through the existing Project Import operations. There are no extra Git-specific events: **Partial**.

## H. Security

- Delegated Microsoft Entra scope for Azure DevOps only (`499b84ac-…/.default`).
- Tokens stay in memory; no tokens, passwords or PATs are persisted, and none are rendered (tests assert no "Bearer" in markup).
- Read-only Azure DevOps APIs.
- Organization URLs are canonicalised and non-Azure-DevOps hosts are rejected.
- A pinned commit must be a full SHA that exists.
- Failures map to 401/403/404/429/501 without leaking the response body.

## I. Tests

**New backend tests:** `GitSourceLinksTests`, 8 tests.
- diagnostics;
- failure classification (theory);
- consent failure after interactive sign-in;
- device code;
- organizations discovery and fallback;
- projects paging;
- pinned commit / RevisionNotFound;
- link → record at commit → update check → re-link → unlink;
- matching rules and decisions.

**New frontend tests:**
- `GitSourcesUiTests`: wizard full flow, short-commit block, not connected, diagnostics + device code. The old picker tests were replaced.
- `SourceManagementTests`, 3 tests.

**Targeted runs:** Git backend 32/32 passed; Git UI + Source Management frontend 10/10 passed.

Full suites: backend 5877 passed, 13 skipped, 0 failed; frontend 8032 passed, 0 failed.

Remaining work, not verified live: the real Entra sign-in, Azure DevOps membership discovery and archive download against bufdir's organization.
