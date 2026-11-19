# Release Readiness removed — 2026-11-19

Branch `008-traceability-first`. Local only; not pushed.

**Product decision.** There is no separate release-decision page:

- **Project Dashboard** shows testing status and evidence gaps;
- **Critical E2E Regression** keeps its build-specific release gate and policy, untouched;
- formal release approval happens outside BirkNext.

## Dependency map (audited)

| Component | Other consumers | Action |
| --- | --- | --- |
| `Pages/ReleaseReadiness.razor` (`/release-readiness`) | — | **Removed.** The route redirects to the Dashboard |
| `CriticalE2EReleaseReadinessView` (frontend view-model) | Only the page | **Removed** |
| `ReleaseReadinessPresentation`, `GovernanceGatePanel` | Correlation Review | Kept |
| Backend release assessment and frozen reports (`CanonicalInterpretationService.Release`, the `release_readiness_reports` table) | Snapshot Registry lists the frozen reports | Kept (frozen evidence; no new writes from the UI) |
| `CriticalE2EReleaseReadiness` (backend gate evaluation) | Critical E2E Regression | Kept, unchanged |
| Navigation (Traceability section, Project Leader, Scrum Master and Test Lead views), Documentation catalog, feature taxonomy | — | Entry removed. The Project Leader's "Release decision" group now lists Critical E2E Regression, Snapshots and Relationship Review. "Release Readiness" is a search alias of the Dashboard |
| Dashboard stage 5 ("Review evidence & release readiness") | — | Now "Review release evidence", linking to Critical E2E Regression |
| User Guide (Critical E2E gate text) | — | Rewritten: the gate is evaluated in Critical E2E Regression; approval happens outside BirkNext |

## Tests

- `ReleaseReadinessPageTests` and `ReleaseReadinessCriticalE2ETests` (page tests) were removed.
- The Critical E2E gate's backend tests are unchanged.

Results: see the commit message.
