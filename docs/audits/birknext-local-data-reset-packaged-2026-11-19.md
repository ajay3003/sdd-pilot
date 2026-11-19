# Local Data Reset in the packaged app — 2026-11-19

Fix: `daa84ff7` on `008-traceability-first`. Local only; not pushed.

## Reported

After Local Data Reset in the downloaded Windows package (1.0.0-ci.416921), the Dashboard confirmed that no project was loaded but still
showed:

- 21 scenario candidates;
- 4 reviews ready to run;
- the previously named workspace;
- conflicting next actions.

None of this reproduced in Development.

## Each value traced to its source

| Value | Source | Verdict |
| --- | --- | --- |
| Workspace name `Auto_…` and "Auto-saved" | `CurrentWorkspaceProjection` snapshot cached during the frontend reset, built from the restored workspace metadata | **Reset defect: fixed.** |
| Readiness hero, "Reviews ready to run" | Rendered only while the stale snapshot says a workspace is loaded | Consequence of the same defect. Gone with the fix. |
| "21 scenario candidates" | The built-in Skolenærvær System Guide (21 curated scenarios), shown by "Your next steps" when no project is loaded | **Platform content, not retained data.** Also shown on a fresh install (verified on the 416921 package). |
| "No project is loaded" banner | Fixed text for `?reset=done` | Now consistent with the page. |
| Conflicting next actions | Right after reset, "Import your project" (stale loaded snapshot) against the next-steps panel. After a reload, "Finish setting up the Target Environment" against "Review Test Plan Suggestions: Ready now" | The first pair came from the defect. The second pair also appears on a fresh install: existing Dashboard wording, not a reset defect, and not changed here. |

Backend, browser storage and database were clean in every phase:

- `current-state` returned `currentWorkspaceId:null` and `resetEpoch:1` from 0.5 s after the reset.
- The project localStorage keys were removed. Only the reseeded `birknext:frontend-analysis-settings` and the `birknext.navigation.v1` UI
  preference remained.

## Root cause

`ApplicationRuntimeResetService.ClearFrontendRuntimeStateAsync` cleared the stores in this order:

1. `NotifyWorkspaceChanged(null)`, then `ClearAll()`. Each change invalidates the projection, and the Dashboard and sidebar recompute it at
   once.
2. Only then `WorkspaceSessionRestoreService.ClearWorkspaceAsync()` nulled the restored workspace metadata (the auto-saved workspace that the
   app restores at start).

A snapshot computed between those steps had no artifacts and no project, but still had the metadata. It was therefore "loaded", named
`Auto_…`, with save status AutoSaved.

`ClearWorkspaceAsync`'s own `NotifyWorkspaceChanged(null)` was deduplicated because the id was already null, so nothing invalidated the
snapshot afterwards. A reload fixed it.

**Why Development hid it.** A development Target Environment usually has a bound project. Resetting Target Environments then changes the
project key and raises one more invalidation. A fresh packaged install has no bound project, so the stale snapshot stays.

## Fix

- `WorkspaceSessionRestoreService.ClearWorkspaceAsync` forgets the metadata first and always raises a repository change at the end.
- `ApplicationRuntimeResetService` clears the restore metadata first and invalidates `ICurrentWorkspaceProjection` as its last step.
- `docs/local-data-reset.md` documents the order.

Zero counts are not hard-coded and no panel is hidden.

## Verification

- **Unit and integration.** `LocalDataResetWorkspaceProjectionTests` (3 tests) wire the real restore service, explorer context, projection
  and reset coordinator.
  - Before the fix: 2 failed with `Loaded`, expected `NoWorkspace`.
  - After the fix, together with the related suites (LocalDataReset, ResetLocalDatabase, CurrentWorkspaceProjection, DashboardPage,
    WorkspaceRestorePolicy, SampleProjectSelectionPersistence, ReviewContextLifecycle, WorkspaceSession): **72 passed, 0 failed**.
  - Built in a clean worktree at `f3f16650` plus the change, because the main tree had unrelated uncommitted work.
- **Packaged.** A tester package was built from `daa84ff7`:
  - `BirkNext-Tester-Windows-x64-1.0.0-local.zip`, SHA-256 `e68b7f0f…a2bf1d6`.
  - The build manifest records `sourceCommit daa84ff7`. "dirty" refers only to the untracked package-script variant that reuses the pinned
    PostgreSQL/k6 files from the verified 416921 package, checked file by file against its SHA-256 manifest, with no download.
  - The real `Start-BirkNext.exe` was run with a fresh data folder and Edge profile: import a synthetic LendoPlus project → reset → check.

| Phase | Workspace name | Save status | Hero | API current workspace |
| --- | --- | --- | --- | --- |
| After import | LendoPlus | Auto-saved | 4 of 7 inputs, 10 ready | `Auto_20261119_104521`, epoch 0 |
| 0.5 s after reset | absent | absent | absent | null, epoch 1 |
| 8 s, no reload | absent | absent | absent | null, epoch 1 |
| After reload | absent | absent | absent | null, epoch 1 |
| After application restart | absent | absent | absent | null, epoch 1 |

On the unmodified 416921 package, the same sequence showed `Auto_20261119_094015` and "Auto-saved" at 0.5 s and 8 s, gone only after a
reload.

**Not run.**

- A published Development-vs-Release browser comparison. The root cause is in shared frontend code, identical in all three modes.
- The full frontend and backend suites for this change. They are run with the next regression.
- A pipeline (CI) package build. Required before distributing.
