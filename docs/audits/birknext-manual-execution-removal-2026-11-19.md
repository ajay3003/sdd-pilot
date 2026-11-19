# Manual execution removed; Azure DevOps Test Plans owns manual execution — 2026-11-19

Branch `008-traceability-first`. Local only; not pushed.

**Product decision.** BirkNext prepares, reviews and exports manual test cases. Azure DevOps Test Plans executes them and records:

- results;
- evidence;
- defects;
- retests.

Manual Testing has two stages: **Discover & understand** and **Plan & prepare**. Plan & prepare ends with *Export to Azure DevOps*.

## Ownership map (audited)

| Capability | Before | Now |
| --- | --- | --- |
| Execute & document page (`/manual-testing/execute`), run history, step results, evidence files, defects, retests | `ManualTestingExecute.razor`, `ManualTestRunsApiService`, `ManualTestRunsController`, `ManualTestRunService`, `EvidenceFilePolicy` | **Removed.** The route redirects to the saved case in Plan & prepare (`draft` and `case` kept), else to Manual Testing |
| Case fingerprint and origin, used by the Azure DevOps export | Static members of the run service | `ManualCaseIdentity` (same code; the export is unchanged) |
| Saved cases, plan revisions, requirement links, discovery results | `test_plan_drafts`, `test_plan_draft_revisions`, `scenario_discovery_results` | Unchanged |
| Azure DevOps export (XLSX/CSV, history, known ids) | `AdoTestPlanExportService` | Unchanged. It never infers execution |
| Requirements Traceability, Test Coverage, Discover | Prepared cases plus manual execution counts | **Test design only.** The execution column, counts and chips are removed; the panel says execution is in Azure DevOps |
| `manual_test_runs`, `manual_run_attachments` tables | Written by the run service | **Dormant** (see below) |
| `WorkspaceStage`, `ManualTestingWorkspace.ResolveSnapshot`/`EvidenceAsync`, `manual-testing.css` | Shared with Technical Testing | Kept |
| Integration "Execute & Evidence", Critical E2E, technical runs and retests | Technical Testing | Untouched |

## Storage

The two run tables stay in the EF model as dormant compatibility storage:

- nothing reads or writes them;
- existing databases keep their migration history;
- Local Data Reset (model-driven) keeps clearing them.

Dropping them needs a new forward migration, generated with the EF tooling. That cleanup is left for a later change. The earlier migrations
also create tables that are still in use, so they must never be edited.

## UI

- **Home:** no runs card. The next step ends with "Export to Azure DevOps" once cases are approved. The boundary text says a prepared,
  approved or exported case is not an executed test.
- **Navigation:** the Execute & document module and its entries in the Manual Testing and Test Lead views are gone.
- **Plan & prepare:** the "Run this test case" link is removed.
- **User Guide:** describes two stages and the Azure DevOps handoff.

## Tests

- **Removed (execution-only):**
  - `ManualTestRunServiceTests`, `ManualRunPersistencePostgresTests`;
  - the attachment test and `EvidenceFilePolicyTests` in `EvidenceIntegrityDatabaseTests`;
  - the run-only test in `ManualRunOriginTests`;
  - `ManualRunHistoryPagingTests`, `ManualRunReportTests`;
  - the runner tests in `ManualTestingWorkspaceTests`.
- **Moved:** plan-revision history and discovery persistence moved to `TestPlanPersistencePostgresTests`.
- **Updated:**
  - origin and fingerprint tests now use `ManualCaseIdentity`;
  - the Manual Testing home and next-step tests use two stages and the export;
  - a new test covers the legacy redirect;
  - traceability tests assert no execution display.

Results: see the commit message.
