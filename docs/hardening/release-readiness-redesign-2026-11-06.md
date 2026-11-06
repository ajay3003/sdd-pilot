# NEXT57 — Release Readiness: executive summary, evidence dimensions, blockers, scope and history (2026-11-06)

Branch `008-traceability-first`. Builds on NEXT49 (525418b4): the Delivery snapshot is kept so the scope resolves; there is no vacuous truth for empty requirements or tasks; root causes are listed once; next steps follow prerequisites. Mostly frontend, plus **one shared-rule hardening** (below). Nothing pushed.

**Status: partially completed.**

- Done:
  - The executive summary with release figures.
  - Evidence dimensions grouped **Required / Optional / Not applicable** with a state legend.
  - Blockers, listed once per root cause.
  - Scope selection (unchanged behaviour).
  - A **pre-freeze summary**.
  - A **history inspector** that compares a frozen assessment with the current draft.
- The release decision rule now also refuses a vacuous Ready when there is no required dimension.
- Remaining items are listed under "Not done".

## Audit — what NEXT49 left (P7–P12)

| # | Gap | Before |
|---|---|---|
| X1 | Executive summary (P7) | Six plain `dt/dd` facts. The scope size (feature packs) and the required gaps were missing, so "how much is in this release" was not answered at the top. |
| X2 | Dimensions (P8) | One flat table mixing Required, Optional and Not applicable. Applicability was a small badge beside the title, so Required vs Optional and Ready vs Ready with limitations vs Not assessed had to be read row by row. |
| X3 | Freeze (P10) | "Freeze this assessment" took a name only; what would be frozen was not shown first. |
| X4 | History (P12) | A table of frozen assessments that could only be opened as a page. There was no inspector and no comparison with the current draft. |
| X5 | Vacuous Ready (P3) | `ReleaseReadinessRules.Decide` returned **Ready, "Every applicable dimension is ready."** when no dimension was Required (for example only optional dimensions, or none). The backend always emits required dimensions today, so this was latent; it is now refused. |

## Changes

### Shared rule (`shared/ReleaseReadiness.cs`)

`Decide` returns **Not assessed — "No required dimension was assessed."** when there is no required dimension. The other rules are unchanged:

| Situation | Result |
|---|---|
| A required dimension is Blocked | Blocked |
| A required dimension is Not assessed (including an unresolved scope) | Not assessed |
| An optional dimension is anything other than Ready, or a required one has limitations or an exception | Ready with limitations |
| A dimension is Not applicable | Never counts |

### Release Readiness page (`Pages/ReleaseReadiness.razor`, `Services/ReleaseReadinessPresentation.cs`)

- **Executive summary**: the decision with icon, label and reason, then seven figures. Each figure is "Not assessed" when unknown, never 0.
  - **Feature packs in scope**, with the task count, or "Release scope not resolved".
  - **Requirements assessed**, with how many have no test link.
  - **With a confirmed test**, with "N more with a suggested test only — not counted".
  - **Tests executed for this scope**: always Not assessed. Imported TRX results are named as found, never mapped to the scope.
  - **Blocking findings**, with the root-cause count.
  - **Decisions pending**.
  - **Required evidence gaps**.
- **What blocks release?**, **Required evidence not assessed** and **What should I do next?** are unchanged from NEXT49: one root cause once, prerequisite order, and a deep link to the exact review item.
- **Evidence dimensions**:
  - A state legend (✓ Ready · ! Ready with limitations · ✕ Blocked · ? Not assessed) and what Required vs Optional means for the result.
  - Then the **Required**, **Optional** and **Not applicable** groups. Each has a tally (for example "1 Blocked · 1 Not assessed · 2 Ready with limitations · 2 Ready") and rows ordered worst first.
  - Each row shows its state chip, title, summary, expandable evidence, next action (unless Ready) and a link to its page.
- **Scope & Evidence**: unchanged (entire project or selected modules; the features in scope; the evidence used).
- **Reports & History**:
  - **What a freeze records**, shown before freezing: scope, relationship analysis, feature packs and requirements, result, blocking causes, required gaps and rules. The freeze button itself is unchanged; freezing is still not approval.
  - **Frozen assessments** table, with **Inspect** per row.
  - **History inspector**:
    - the frozen assessment's own summary, never recomputed, with its latest release decision;
    - "Compared with the current draft": how many dimensions changed, the result then and now, and per dimension its frozen state → current state, changed ones first;
    - a link to open the frozen assessment.
  - Inspecting changes nothing.

## Regression fixture — the corrected M2LB scope

`ReleaseReadinessPresentationTests.M2lbCorrected()` encodes the NEXT49 result on the copy:

- 38 feature packs and 676 requirements;
- 0 with a confirmed test and 31 with a suggested test only;
- 1 blocking finding and 14 pending decisions.

Test design is not assessed, and Governance is Blocked by the undecided identity conflict. The rule gives **Blocked**, and the summary shows 38 / 676 with executed **Not assessed**.

## Tests

- Frontend (new, 7):
  1. The corrected M2LB scope shows 38 packs and 676 requirements and stays **Blocked**.
  2. Dimensions grouped Required → Optional, worst first, with tallies.
  3. **Missing required scope or evidence is never Ready**; a blocking governance decision blocks; an optional gap never blocks; no required dimension gives Not assessed.
  4. A frozen assessment compared per dimension with the draft, plus the freeze summary.
  5. Page: the pre-freeze summary, and the inspector comparing Blocked then with Ready with limitations now (no freeze call).
  6. Page: the grouped dimensions, the legend and the seven figures.
  7. The updated decision-and-dimensions test asserts Performance's Optional group.
- Backend: `ReleaseReadinessTests` now also asserts that no required dimension, and an empty list, are Not assessed.
- Release subset (frontend): **64 passed, 0 failed**, including the shared-rule tests above, which run against `shared/ReleaseReadiness.cs`.
- **The full backend suite after the shared-rule change did not complete.** Claude Code stopped the run because the machine was critically low on memory. Per the house rule it was not restarted. The backend `ReleaseReadinessTests` addition has therefore **not been run**.
  - The change only affects an assessment with no Required dimension. The backend always emits Required dimensions (Scope, Requirements, Traceability, Test design, Governance, Open decisions), so no existing backend path changes outcome.
  - This still needs confirmation: run `dotnet test backend/BirkNext.Api.Tests`.

## Browser verification — not run

The NEXT57 check (`next57-e2e.cjs`: figures 38 / 676, grouped dimensions, legend, blockers, freeze summary, history inspector, mobile, read-only) was written but **not run**.

- The memory shortage that stopped the backend suite came first. The local stack was then stopped to free memory instead of publishing and starting it again.
- The page is covered by bUnit tests only.
- To run it, publish the web app and the API (the shared rule is in both), start the stack on `birknext_gaps`, then run `node next57-e2e.cjs <shots dir>`.

## Not done (remaining acceptance criteria)

- **P9 findings table** with search, filter and grouping by root cause. Findings stay on the shared governance panel, with blockers listed once on the Overview.
- **TRX results mapped per requirement in scope.** Imported results are reported as found; execution stays Not assessed, deliberately.
- **Comparing two frozen assessments with each other.** The inspector compares a frozen assessment with the current draft only.
- **P8 "Why is evidence missing?" deep explanations per evidence source** beyond each dimension's summary, next action and link.
