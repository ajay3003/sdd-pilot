# BirkNext — Project Quality & Recommended Workflow: audit (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `d86bf326`.
- **Method:** read-only code audit of HEAD. Paths are relative to `AIAssisted/frontend/BirkNext.Web`.
- **Related documents:** information architecture `../architecture/birknext-project-quality-information-architecture.md`.

## 1. Page architecture (baseline)

| Page | Route | Size | Data |
|---|---|---|---|
| Project Quality (`Pages/Dashboard.razor`) | `/dashboard` (`?reset=`) | 1,435 lines + 1,004 CSS | `ICurrentWorkspaceProjection`, `IWorkflowReadinessService`, session review services, `IDashboardSnapshotService`, activity API, project evidence; Canonical summary scoped to the active Target Environment |
| Recommended Workflow (`Pages/RecommendedWorkflow.razor`) | `/getting-started` (`?path=`, `?execution=`) | 1,065 lines + 1,517 CSS | `IWorkflowReadinessService` (one call), workspace persistence |

- **Shared context:** both pages read the same scoped `ICurrentWorkspaceProjection`. The project is not in the URL.
- **Extra scopes:** the Dashboard also mixes browser-session, logical-application and environment scopes.

## 2. Recommendation owners

- **One engine:** `WorkflowReadinessService.NextAction` is the next-action engine. Its ordered rule chain covers project, evidence, module, pack, source, binding, review gates, applicable reviews and target.
- **Presented twice, in different words:**
  - the Dashboard used `DashboardPresentation.NextStep`, which rewords per key;
  - Recommended Workflow used the raw step title and description plus `ReviewNavigator.Explain`.
  - Same key, different title and reason. For example, for the import step the Dashboard said "Import your project" and Recommended Workflow said "Load project artifacts".
- **Other producers on the same screens:**
  - `WorkflowPaths.NextStepOf` (`?path=`, Recommended Workflow only, an explicit user choice);
  - "Top Recommendations" from document-review reports (Dashboard, collapsed);
  - capability lists (`OtherActions` 3, `AvailableNextReviews` 5, `Then` 3).
- **No test compared the two pages' next action.**

## 3. Metric audit: confirmed misleading semantics

| Metric | Problem | Class |
|---|---|---|
| SDD evidence "x of 4" | Denominator included the optional Constitution even when not applicable | Wrong denominator |
| Specification quality | "Passed" whenever severe = 0, even with findings | False pass |
| QA verification | "Results this session" from Document Quality Review (a document score), contradicting its own text | Not execution |
| Health compliance / QA | `ByScore` ≥ 80 → Passed; "Healthy" labels on analysis cards | Unsupported verdict |
| Health traceability | Passed when only some transitions were assessed | False pass |
| Health delivery | "Release" Passed from document review packs | Not runtime |
| Health workflow | Passed at 100% even when decisions were "reviewed with gaps" | False pass |
| API Quality card | "API targets reviewed" with 0 completed | Wrong text |
| Import banner | "Project imported successfully" on every visit, as the most prominent element | Import ≠ quality |
| Execution evidence | Neither page read Test Execution & Evidence | Missing |

## 4. Other findings

| Finding | Status in this work |
|---|---|
| No dismiss / defer / not-applicable state for recommendations; only document review decisions are persisted (`WorkspaceReviewProgress`) | Not built (PQ-G1) |
| Approve unreachable (`RequiresApproval = false` for every step) | Not changed: governance is never shown as Passed because of it (PQ-G4) |
| Needs-attention items are key-grouped aggregates without finding ids; source binding appears in up to five places | Not consolidated (PQ-G2) |
| No help link on either page; no Project Quality guide article | Fixed |
| Dead code: `DashboardMetricsService` / `DashboardMetricsCards` (unused); release-readiness helpers in Recommended Workflow never rendered | Not removed (PQ-G6) |
