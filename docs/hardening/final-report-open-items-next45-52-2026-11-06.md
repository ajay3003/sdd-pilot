# Final report — open items (Groups 1–4) and NEXT45–NEXT52 (2026-11-05/06)

Branch `008-traceability-first`. **Nothing pushed.** Every task has its own commit and its own report under `docs/hardening/`.

Constraints held throughout:

- `appsettings.Local.json` (pre-existing EOL diff) and `testdata/` were never committed or modified.
- No reset, clean or stash.
- No live Azure DevOps, TT02, m2lbdev, Altinn or Azure calls. Browser checks blocked every non-localhost request.
- Writes went only to the throwaway database `birknext_gaps`; the real `birknext` database was untouched.
- No performance test, readiness check, network check or run was started.

## Status per task

| Task | Commit | Status | Headline |
|---|---|---|---|
| Groups 1–2 | ee63607f | Completed | Task Alignment design links (0 → 11 on M2LB: context plus a normalization that dropped them), task text, scope labels |
| Group 3 | 263501df, 7ada547d | Completed | Remembered, revocable repository association for cross-repository baselines; diagram labels as plain text |
| Group 4 | b756743f (report) | Offline completed; live pending | ADO and TT02 fixture suites 582 + 201 passing; live validation needs user-authorised access |
| NEXT45 Global loading | 8b277882 | Completed | Dashboard no longer shows "Nothing needs attention" / "Not assessed" while loading; no stuck indicator on failure; stale-response guard; one spinner (Plan Explorer design); 45 ad-hoc loading texts → shared component; `LoadSequence`, `LoadError`, `BusyButton` |
| NEXT46 Source Management | 2a47992c | Completed | Summary tiles (filter), searchable card list, inspector, per-dimension analysis chips, keyboard tabs; false "no sources" while loading fixed; repository read errors with Retry |
| NEXT47 Snapshots | 15bbcdc0 | Completed (core) | **Same snapshot had two verdicts** (Source Management "Analyzed" vs registry "Partial") → one shared rule; KPI definitions, reference states, confirmed lifecycle actions, stale-selection guard, provenance chain |
| NEXT48 Relationship Review | c1642763 | Partially completed | **Overview Conflicts 0 while governance blocked** → counted with the queue's classifier; rejected candidates no longer counted; totals reconcile (3,802 = 2,439 + 1,361 + 2); rationale required for identity-conflict decisions (backend); governance dashboard; conflict workflow |
| NEXT49 Release Readiness | 525418b4 | Partially completed | **Re-analysis dropped the Delivery snapshot → 0 feature packs**; now kept, giving 38 packs and 676 requirements on M2LB. Empty scope is "Release scope not resolved" with its reason; no vacuous Ready; root causes listed once; prerequisite-ordered next steps; TRX shown as found, execution stays Not assessed |
| NEXT50 Test Data / XML fixtures | 799019a9 | Partially completed | **`ITestDataApiService` never registered** → "Fixtures cannot be saved in this host." and the automatic draft after import never ran; registered and resolved consistently |
| NEXT51 Performance Test Review | 101296b2 | Partially completed | "No step requires authentication" was shown while the endpoint history loaded or had failed; now loading / "not fully known" with Retry |
| NEXT52 Delivery Intelligence | 6e9580f0 | Partially completed | A 95% task checklist was the only completion-like KPI; **"Completed with verified evidence: Not assessed"** added; "All tasks completed" → "All tasks checked" |

## Confirmed defects fixed (root causes)

1. **Task Alignment** ignored design inputs, and `NormalizeReport` dropped the design links (G1).
2. **Dashboard** rendered readiness-derived results from a null readiness, swallowed failures (stuck "Working out…") and allowed stale overwrites (NEXT45).
3. **Source Management** showed "No source packages yet" while loading and ignored repository read errors (NEXT46).
4. **Two definitions of "analyzed"**: Source Management used Project Evidence capability dimensions, the registry used analysis areas (NEXT47).
5. **Snapshot selection race**: an older lineage response could replace a newer selection (NEXT47).
6. **Identity conflicts invisible on the Overview**: only `Conflict` review states were counted, not ambiguous alias-conflict relations (NEXT48).
7. **Rejected candidates counted as candidates** (NEXT48).
8. **Identity conflicts decidable without a rationale** (NEXT48).
9. **Candidate test links misexplained** as "by naming only"; their basis is an id mentioned in comments or strings (NEXT48).
10. **Relationship Review re-analysis omitted the Delivery snapshot**, emptying the release scope (NEXT49).
11. **Vacuous truth**: Requirements "Ready with limitations" with 0 requirements; Traceability "Ready" with 0 tasks (NEXT49).
12. **Test Data client not registered in the host**: fixture save refused, automatic draft never ran. Tests masked it with mocks (NEXT50).
13. **Authentication summary claimed "none required" without evidence** (NEXT51).
14. **Checklist percentage presented as the completion KPI** (NEXT52).
15. **Code Risk 500** on duplicate endpoint keys, and earlier NEXT39–43 items (see the earlier reports).

A host-composition sweep after NEXT50 found no other application service that is resolved but never registered.

## Tests (latest runs)

- Frontend full suite: **8233 passed, 0 failed** (after NEXT52).
- Backend full suite: **5914 passed, 13 skipped, 0 failed** (after NEXT48).
- After NEXT49 (release backend change), the release, governance and adoption subset passed 44/44. The full backend suite was not re-run after NEXT49.
- New test files:
  - `GlobalLoadingFeedbackTests`, `SourceManagementRedesignTests`, `RelationshipAccountingTests`, `ReleaseScopeResolutionTests`, `ReleaseReadinessPresentationTests`, `TestDataHostCompositionTests`;
  - additions to `SnapshotBrowserTests`, `RelationshipReviewTests`, `PerformanceTestReviewPageTests`, `DeliveryDashboardTests`, `ReleaseReadinessTests`.

## Real-browser verification (M2LB copy, Edge, nb-NO, localhost only)

| Area | Result | Notes |
|---|---|---|
| Groups 1–2 | 8/8 | |
| Association | 8/8 | |
| Traceability | 5/5 | |
| NEXT45 | 9/9 | |
| NEXT46 | 18/18 | Plus inspector focus on mobile |
| NEXT47 | 16/16 | |
| NEXT48 | 12/12 | |
| NEXT49 | 14/14 | Includes one analysis with Delivery in the throwaway DB |
| NEXT51 | 4/5 | The one "write" was a stateless resolver POST |
| NEXT52 | 6/6 | |
| NEXT50 | Not verified live | The copy has no Skolenærvær test pack bound to the environment; manual checklist in its report |

## Not done — remaining scope (honest list)

- **NEXT48**: explorer master–detail inspector; snapshot revalidation UX; virtualization; recent activity on the Overview.
- **NEXT49**: P7–P12 dashboard metrics row, grouped Scope & Evidence, findings table, pre-freeze summary, frozen-report comparison, history inspector; TRX-to-requirement mapping.
- **NEXT50**: page redesign (four sections, builder with editor); per-dimension readiness model; scenario variants; multi-source contract-selection UX; live save verification.
- **NEXT51**: compact Scenario list and focused step editor; shared-variable UX for `{brukerId}`; discovery/acceptance states; status chips across tabs; explicit draft → v1 save.
- **NEXT52**: shared component set; full-width feature cards and new navigation; Scrum Master dashboard, Change Board regrouping, milestones, FQS restructuring, history navigation; performance for 1.5–2k work items.
- **Group 4 live**: Entra/ADO sign-in, TT02/ID-porten and live submissions — only with explicit user authorisation.

## Manual steps for you

1. In Relationship Review, decide the **Utdanning ↔ SkoleAdapter** identity conflict (a rationale is now required). Release readiness stays Blocked until then.
2. Where the Skolenærvær pack is bound, run the NEXT50 checklist: import Tiltak 2 → automatic fixture → save → reload.
3. Review and push the commits when ready (`git log b756743f^..HEAD`).
