# Final report — UI redesign of NEXT48–52 remaining scope (NEXT53–NEXT57, 2026-11-06)

Branch `008-traceability-first`. **Nothing pushed or deployed.** One commit and one report per task under `docs/hardening/`. The correctness fixes of NEXT48–52 were kept, and no behaviour they introduced was undone.

Constraints held throughout:

- `appsettings.Local.json` (pre-existing EOL diff) and `testdata/` were never modified or committed.
- No reset, clean or stash.
- No live Azure DevOps, TT02, m2lbdev, Altinn or Azure calls. Every browser check aborted any non-localhost request and recorded none.
- Only the throwaway database `birknext_gaps` served the local stack. No write was made in this pass: every browser check was read-only. The only POST seen was Performance Test Review's stateless `runtime-authentication/resolve`.
- No load test, readiness check, validation request, freeze, sign-off or review decision was started.
- **Utdanning ↔ SkoleAdapter was not resolved.**

## Baseline (before any change)

- Backend full suite: **5917 passed, 13 skipped, 0 failed**. This had not been run since NEXT49.
- Frontend full suite on a clean export of HEAD b8fa2fd6: **8232 passed, 1 failed**. The failure was environmental: the export did not include `AIAssisted/docs/status-vocabulary.md`, which the vocabulary test reads. The same test passes in the working tree, and earlier runs there passed 8233/8233.

## Status per task

| Task | Commit | Status | Headline |
|---|---|---|---|
| NEXT53 Delivery Overview + Reports | 5b3f1470 | **Partial** | Shared Delivery Intelligence (one calculation layer + KPI, chart, attention, feature list, detail, change-board components). Overview is a Project Leader dashboard (Overview / Features / Risks & quality / Technical details); there is no default task hierarchy or split panel. Reports has a Scrum Master summary with baseline → current figures, a new Change board, and feature cards by default. |
| NEXT54 Test Data + XML fixtures | 3192a9c0 | **Partial** | Four sections with one next step from the shared readiness. Fixture builder: Configure → Review & validate → Save; XSD and mapping status; well-formed / XSD / business / runtime kept apart; preview with line numbers; persisted identity confirmed from the reloaded catalog. **Tiltak 2 automatic creation not verified live** (package not available locally). |
| NEXT55 Performance scenario editor | a23e2265 | **Partial** | Compact operation rows (Defined / Configured / Executable, issue count). One focused editor with five keyboard tabs: Authentication (required by the evidence beside the effective configuration), Parameters & Test Data, Request, Response & extraction, Advanced. Unloaded evidence is "not fully known". |
| NEXT56 Relationship Review | a774c0ea | **Partial** | Grouped review queue beside one inspector with focused decisions. The identity conflict shows each entity's aliases (and who declared them) and occurrences side by side. Master–detail explorer: paged list, inspector with established apart from candidates. Not auto-resolved. |
| NEXT57 Release Readiness | 4e465fff | **Partial** | Executive summary figures, dimensions grouped Required / Optional with a state legend, blockers once, pre-freeze summary, history inspector comparing a frozen assessment with the draft. Shared rule refuses a vacuous Ready when there is no required dimension. |

Each task is marked **Partial** because its brief contained more than this pass delivered. Each report lists exactly which acceptance criteria remain unmet; they are summarised below.

## Functional changes (beyond presentation)

1. **NEXT57**: `ReleaseReadinessRules.Decide` returned Ready when no dimension was Required. It now returns **Not assessed**. This is a shared-rule change, and the full backend suite was re-run.
2. **NEXT55**: the editor's default-open step is now stable while steps are edited or moved. During the work, moving a step had closed the editor in use; this was found and fixed.
3. **NEXT54**: a saved fixture is reported as stored only once the reloaded catalog lists it, with its alias, Test Data id and version. The editor's own state is never taken as confirmation.
4. **NEXT56**: after a decision the next question is selected. The decided relation is exactly the one in the inspector, which a test locks.
5. **NEXT53**: a single definition of "Completed with verified evidence" is shared by both Delivery pages (a test locks it). Change Board "Completed with evidence" is Not assessed without verification. "Checklist completed this period" only counts transitions within the period.

## Visual changes (summary)

- **Delivery**: full-width feature cards with in-place detail. Tasks only appear under Technical details. Charts: feature status, checklist by module and blockers by module. Text-valued KPIs are shown smaller.
- **Test Data**: section navigation with an advanced Requests & configuration section. A next-step card. A builder step indicator, status strip, validation outcome tiers and a dark code preview with diagnostic lines.
- **Performance**: one-line operation rows with dimension chips, plus a tabbed editor. Request and response panels appear only on request.
- **Relationship Review**: list and sticky inspector, grouping, side-by-side evidence cards, and an explorer list/inspector.
- **Release Readiness**: figure tiles, dimension groups with tallies and state chips, a freeze summary and a history inspector.
- **Mobile 390 px**: no horizontal overflow on any redesigned page, after fixes for fieldsets, long identifiers, selects and tables.

## Tests

| Run | Result |
|---|---|
| Frontend full suite after NEXT53 | 8243 passed, 0 failed |
| Frontend full suite after NEXT54 | 8250 passed, 1 failed: a timing test (243 ms against 200 ms under load), unrelated; passed 4/4 when run alone |
| Frontend full suite after NEXT55 | 8255 passed, 0 failed |
| Frontend full suite after NEXT56 | 8259 passed, 0 failed |
| Frontend after NEXT57 | Release subset 64 passed, 0 failed. The full frontend suite was **not re-run** after NEXT57 (memory pressure; see below). |
| Backend full suite after NEXT57 (shared rule change) | **Not completed**: stopped by Claude Code for critically low memory and not restarted, per the house rule. The backend `ReleaseReadinessTests` addition is therefore unrun. |

New tests:

- NEXT53: `DeliveryIntelligenceTests` (11).
- NEXT54: 6 in the tester-view and XML fixture panel tests.
- NEXT55: 4 in `PerformanceScenarioStepsTests`.
- NEXT56: 4 in `RelationshipReviewTests`.
- NEXT57: 4 presentation tests, 2 page tests and backend `ReleaseReadinessTests` additions.

Existing tests were updated only where the layout moved content (sections, parts, inspector). No assertion about values, sensitivity, approval, counting or readiness rules was relaxed.

## Browser checks (M2LB copy, Edge, localhost only)

| Task | Result | Key observations |
|---|---|---|
| NEXT53 | 18/18 | Features 24 · **Completed with verified evidence: Not assessed** · checklist 95%. Reports: scope 1 → 10, verified Not assessed. Change board: New 9 · Changed 1 · Checklist completed 2 · Needs review 1. |
| NEXT54 | 13/13 | Next step "Generate missing XML fixture — XML fixture: Missing" opens the builder. The XSD is "Not chosen yet · No matching XSD found …"; generate is disabled and nothing is guessed. |
| NEXT55 | 13/13 | `getchildrenforuser`: Defined ✓ · Configured ! · Executable ✕, 1 issue. Editor opens on Authentication: "Authenticated requests observed by Endpoint Discovery" vs "Override · Target Environment authentication", execution credential Missing. |
| NEXT56 | 14/14 | Utdanning ↔ SkoleAdapter with real source evidence: alias `M2LB.SkoleAdapter.Worker` Proposed, "declared by test pack expected aliases (conflicting declaration)" vs "declared by domain profile skolenaervaer; test pack expected aliases". Explorer "FR-001": 25 of 40 entities; Verifies 0 established · 6 candidate. |
| NEXT57 | **Not run** | The script is ready (`next57-e2e.cjs`). The stack was stopped to free memory after the backend run was stopped, so the page is verified by bUnit tests only. |

## Known limitations and remaining acceptance criteria (per task)

- **NEXT53**:
  - milestones / phase timeline (no authoritative evidence);
  - Functional Quality Scope restructuring;
  - report history navigation and frozen-report comparison;
  - one shared FilterBar component;
  - per-section skeletons;
  - virtualization (paging is used instead);
  - role defaults;
  - tests for renames, report immutability and export consistency.
- **NEXT54**:
  - the eight-dimension readiness model (consumer binding, environment, runtime);
  - a mapping table with Required and filters (the XSD parameter model has no required flag);
  - syntax highlighting, undo/redo and a navigation guard;
  - scenario variants;
  - multi-source contract-selection UX;
  - **live Tiltak 2 import → automatic fixture → save → reload** (manual checklist in the report).
- **NEXT55**:
  - discovery/acceptance state view;
  - status chips on Overview, Readiness and Run;
  - simplification of the remaining tabs;
  - explicit draft → v1 save sequence.
- **NEXT56**:
  - outcome preview per identity-conflict option;
  - snapshot revalidation UX;
  - server-side grouping and virtualization;
  - recent activity on the Overview;
  - history inspector.
- **NEXT57**:
  - findings table with filters and grouping;
  - TRX mapped per requirement;
  - comparing two frozen assessments with each other;
  - per-source "why is evidence missing".

## Interrupted by memory pressure

While waiting on the full backend suite after NEXT57, Claude Code stopped the background run (and its waiter) because the system was critically low on memory. Per the house rule they were not restarted. To free memory, the local stack was then stopped by port-owned PIDs (5099, 5199).

Outstanding verification:

- the full backend suite;
- the full frontend suite after NEXT57;
- the NEXT57 browser check.

Commands:

```
cd <scratch worktree>/AIAssisted
dotnet test backend/BirkNext.Api.Tests
dotnet test frontend/BirkNext.Web.Tests
# publish web + api, start on birknext_gaps, then: node next57-e2e.cjs <shots>
```

## Manual checks for you

1. **Decide Utdanning ↔ SkoleAdapter** in Relationship Review (the review queue → Identity conflict), with a rationale.
   - The inspector now shows that `M2LB.SkoleAdapter.Worker` is declared for SkoleAdapter by the Skolenærvær domain profile and the test pack's expected aliases.
   - The declaration for Utdanning is flagged as conflicting.
   - Release readiness stays Blocked until this is decided.
2. **Run the outstanding verification** listed under "Interrupted by memory pressure" (backend suite, frontend suite, NEXT57 browser check).
3. **Run the NEXT54 checklist** where the Tiltak 2 package and the Skolenærvær pack are available: import → automatic fixture → Stored in Test Data (alias, id, v1) → reload → still listed → approve.
4. **Review the commits** before pushing: `git log b8fa2fd6..HEAD`.
5. Optional: judge the Delivery Overview and Reports layouts against your Project Leader and Scrum Master expectations. Role defaults (G12) were not implemented.
