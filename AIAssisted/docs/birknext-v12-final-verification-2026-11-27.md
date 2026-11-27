# BirkNext V12 — final UX decisions: implementation and verification (2026-11-27)

Scope: «V12 — Final UX design decisions» (items 1–6). These stay out of scope and are tracked separately: BUG-005, the persistent identity migration, the test-method filter, and acceptance testing with real testers.

## 1. What was implemented
| Decision | Implementation | Commit |
|---|---|---|
| 1. Grouping in Finn og forstå | **One grouping state**: Brukerhistorie / Spesifikasjon (new axis) / Modul / Ingen gruppering. Details below the table. | 3162cc46 |
| 2. Glossary | «Kandidat» is no longer shown to testers. Details below the table. | 3162cc46 |
| 3. Readiness views | Analysis only. `birknext-v12-readiness-views-analysis-2026-11-27.md` recommends a common *presentation*, not a common model. No semantics were changed. | 3162cc46 |
| 4a. Accessible dialog | `Shared/Components/Dialog/AccessibleDialog.razor` + `birknextDialog` (pageFocus.js). Details below the table. | 3162cc46 |
| 4b. Badges and empty states | Manual Testing badges share one shape and one palette per tone; empty states share one box. Details below the table. | ed472b7a |
| 4c. Application scope bar | The shared `SpecificationScopePicker` is the scope bar where scope drives the page. Details below the table. | — |
| 4d. Silent `.Take(n)` | Each capped list now states the total and has a «Show all» / «Show fewer» toggle. Details below the table. | 3162cc46 |
| Earlier V12 bundle items | Two items, details below the table. | 36075dbb, 6fe0f156, ea835190 |

**1. Grouping in Finn og forstå**
- The rail, the area cards and the list all follow the one grouping state.
- «Ingen gruppering» removes area groups and cards. The stories stay in the rail as navigation, because they work as a filter, not a grouping.
- Testnivå, scenariotype, funksjonsområde and testmetode remain available as second-level breakdowns.
- A grouping saved before V12 is normalised into the new model; the tester's breakdown is kept.
- The default remains Brukerhistorie. Scenarios without a story keep their own group.
- The specification grouping uses only the story's documented source path. Nothing is inferred, and plans are never structured by it.

**2. Glossary**
- Testers now see «scenarioforslag» / «foreslått» instead of «kandidat».
- Unchanged: identities, contracts, the backend Data Quality labels (a technical view), and source-authored text.

**4a. Accessible dialog**
- Panel-level `role="dialog"` with `aria-modal`.
- Escape closes the dialog, unless the owner says it can't close yet (save or reset running).
- Tab and Shift+Tab are trapped inside the dialog.
- Focus moves in when the dialog opens, and returns to the opener when it closes.
- Clicking beside the dialog closes it only when allowed. It never closes an edit dialog (BUG-001 policy).
- Migrated: Data reset, the three Frontend Analysis modals, the KPI tile editor and the Planlegg preview.
- LocalSessionGate is deliberately unchanged: it can't be dismissed, and the page behind it is already inert.
- A race in «Legg til scenarioer» was fixed. `Task.Yield` inside the Escape handler had made BUG001 flaky (1 failure in 5 runs).

**4b. Badges and empty states**
- `.mt-chip` and `.msb-status` now share one shape and one palette per tone.
- Empty states share one dashed, muted box.
- This is a CSS-only change: tones and texts are unchanged.

**4c. Application scope bar**
- The shared `SpecificationScopePicker` is the scope bar on Testoversikt, Finn og forstå and Test plan suggestions, where scope drives the page.
- The plan pages keep their plan context; the brief says that context must not be broken.

**4d. Silent `.Take(n)` caps fixed**
- Test Coverage: execution-only results (50) and developer tests (8).
- Delivery Reporting: «Features that moved» (8).
- Spec Explorer: linked tests and success criteria (5 each).

**Earlier V12 bundle items**
- Add selected scenarios from Finn og forstå to a chosen test plan and test group.
- Plan structure on demand in Testplaner.

## 2. Verification
**Status at commit (2026-11-27, worktree audit): NOT COMPLETED.** This record was committed as found; none of the rows below was filled in, so none of them is evidence. Testoversikt-specific browser verification is in `birknext-testoversikt-complete-navigation-scope-regression-2026-11-27.md`. PENDING — filled in from the runs below. A check is reported as PASS only if it was executed.

| Check | Result |
|---|---|
| Affected frontend tests | PENDING |
| Full frontend regression (merged 008) | PENDING |
| Flaky BUG001 test ×15 | PENDING |
| Relevant backend tests (partitioned) | PENDING |
| Browser: grouping and filters, same population per grouping | PENDING |
| Browser: exact-scenario navigation (V10 regression) | PENDING |
| Browser: dialog keyboard (KPI editor, Planlegg preview) | PENDING |
| Browser: application switching | PENDING |
| Browser: pagination beyond limits | PENDING |
| Browser: test planning and saved cases, plan structure | PENDING |
| Browser: dashboard regression | PENDING |
