# Release Readiness: report (2026-11-05)

Branch `008-traceability-first`, not pushed. NEXT40.

## A. Audit (capability matrix)

| Capability | Existing | Status | Action |
|---|---|---|---|
| Release decision engine (release/1: Ready / Ready with limitations / Blocked / Not assessed from Required / Optional dimensions) | `ReleaseReadinessRules.Decide` (shared) | Used, correct (never Ready with an unassessed Required dimension) | Kept |
| Dimensions from the canonical interpretation + shared governance gate | `CanonicalInterpretationService.Release` | Used | Kept; wording aligned; counts exposed as `Facts` |
| Governance policies / gate | `GovernancePolicies`, `GovernanceAsync(scope, action, modules)` | Used; the frontend panel ignored modules | `GovernanceGatePanel.Modules` added |
| Frozen reports + sign-off | `ReleaseReports` / `ReleaseSignOffs` tables, immutable `AssessmentJson` | Used | Duplicate freeze prevented; UI separates draft / frozen / release decision |
| Project identity | `profile.Name` shown as "Project" | **Needs correction** (same root cause as NEXT39) | Target Application read from the saved profile |
| Scope selector | Module checkboxes rendered only when `AvailableModules` > 0 | **Defect**: the text promised a selector that never appeared without an analysis or without feature packs (the M2LB case) | Explicit Entire project / Selected modules choice; searchable list; the reason when there are no modules |
| Test execution (Requirement → VERIFIES → TRX run) | `RequirementTestExecutions` (frontend, used by Functional Quality Scope) | Not read into readiness | **Not implemented** here: shown as "Not assessed" with a link (see H) |
| AQR / IQR / FQR / Performance / Critical E2E results | Each module's own readiness | Not read into readiness | Unchanged: Optional dimensions, Not assessed, linked |
| StatusVocabulary / loading | `GovernancePolicies.DecisionLabel`, `InlineLoading` | Used | Kept |

## B. Project context

- **Before:** the context read "Project: Development", the environment name.
- **Root cause:** besides the page reading `profile.Name`, the analysis context's profile is a data-minimized copy without the Target Application.
- **Now:** "Target Application: M2LB · Environment: QA/Development · Scope: Entire project". The canonical scope stays the Target Environment id, so each environment is isolated.
- **Verified in the browser:** two environments of the same application, one without an analysis, are never mixed.

## C. Scope

- **Scope & Evidence tab:**
  - an explicit radio "Entire project" / "Selected modules";
  - a module filter (search) over the modules of the latest analysis that have feature packs;
  - "Entire project" clears the choice.
- **Without modules, the reason is given:**
  - with an analysis: "No module of the latest relationship analysis has a feature pack (a Delivery snapshot with specifications), so only the entire project can be assessed";
  - without one: "…has not been prepared yet. Analyze relationships first."
- **In scope:** the Scope dimension's summary and feature packs.
- **Findings:** the governance gate is evaluated for the chosen modules; other modules' findings never weigh on them.
- Features, capabilities, services and saved release definitions as scope types are **not implemented** (the canonical model only exposes modules with feature packs to this view).

## D. Evidence and readiness

- **No analysis:** Overview reads "Release Readiness — Not assessed — Required relationship evidence has not been prepared."
  - Primary action **Analyze relationships**: opens Relationship Review in the same context.
  - Secondary **View missing prerequisites**: Scope & Evidence lists every Not assessed dimension with its owning page.
  - No counts are shown, and Findings reads "Governance not assessed — …".
- **With an analysis:** decision, reason and the counts from `ReleaseReadinessFacts`:
  - requirements assessed and requirements with a confirmed test;
  - **tests executed: "Not assessed"** (never inferred from a link);
  - blocking findings and open review decisions;
  - evidence not assessed.

  Counts are null, and shown as "Not assessed", when unknown, including in reports frozen before this change.
- **Dimensions:** one table, Dimension | Status | Evidence | Action. Every action links to the owning page.
- **Functional test execution:** reads "Test execution evidence is not read into release readiness: a linked test is not an executed or passed test." It links to Functional Quality Scope, which shows each requirement's imported results.

## E. UI

- Header with the intro texts from the brief (verbatim) and the context line.
- Tabs: **Overview | Scope & Evidence | Findings | Reports & History**; `?tab=scope|findings|history`.
- Responsive: the dimension and report tables collapse to stacked rows under 40rem; no horizontal overflow at 390 px.

## F. Lifecycle

- **Draft assessment:** the live view, recomputed on every change ("draft — recomputed on every change").
- **Frozen assessment:** "Freeze assessment" moved from the top of the page to Reports & History, with an explanation.
  - A Not assessed result freezes only as **"Freeze incomplete assessment"**, labelled "Frozen assessment · incomplete" (and "· incomplete" in the history).
  - A frozen assessment keeps scope, environment, relationship analysis, rule and policy versions, dimensions and findings, and never changes.
- **Duplicate prevention (new, backend):** freezing the same name over the same scope, analysis, policy version and evaluated dimensions again is refused with a message. Changed evidence (for example a review decision since) or another scope is a new report.
- **Release decision:** the existing sign-off, now titled "Release decision". It is a person's go/no-go beside the frozen assessment, never changes it and deploys nothing. No new deploy action.
- **History table:** name, frozen time, scope, result, analysis, release decision ("Not recorded" when none).

## G. Testing

| Suite | Test | What it covers |
|---|---|---|
| Backend `ReleaseReadinessTests` | Facts | Counts for a module scope (2 requirements: 1 confirmed test, 1 none; 2 tasks: 1 names code; blocking findings > 0) |
| Backend `ReleaseReadinessTests` | No analysis | Facts are null and the "Required relationship evidence has not been prepared" wording is used |
| Backend `ReleaseReadinessTests` | Execution wording | The execution dimension says a linked test is not executed |
| Backend `ReleaseReadinessTests` | Duplicate freeze | Same evidence and name refused; changed evidence and another scope accepted |
| Frontend `ReleaseReadinessPageTests` | Counts | Shown; executed is Not assessed |
| Frontend `ReleaseReadinessPageTests` | Scope choice | Explicit; filter; module sent; entire clears |
| Frontend `ReleaseReadinessPageTests` | Freeze | Moved to Reports & History |
| Frontend `ReleaseReadinessPageTests` | Not prepared | No counts; Target Application; Analyze link; prerequisites; no-modules reason; governance not evaluated; "Freeze incomplete assessment" |
| Frontend `ReleaseReadinessPageTests` | Findings | Follow the chosen modules |

| Run | Result |
|---|---|
| Full frontend suite | **8162 passed, 0 failed** |
| Full backend suite | 5905 passed, 13 skipped, 1 failed: `RuntimeAuthenticationTests.Accessibility_DependencyInjectionSuppliesTheAttendedSessionManager` (TaskCanceled during test-host start-up under the full parallel run; unrelated to this change; 3/3 green alone) |

**Real browser** (`scratchpad/rel-e2e.cjs`):

- **Setup:** Edge on the M2LB copy (DB `birknext_gaps`). Environment `ci-live` "Development" has the NEXT39 analysis; `rr-fresh` "QA" has none. Both are Target Application M2LB.
- **Result: 22/23 checks passed.** The only failure, Z3, was a check that was too strict: navigating to Functional Quality Scope (D5) issued `POST …/scope/resolve`, a read with a request body that saves nothing. The criterion was corrected; the script was not rerun.
- **Writes:** one frozen assessment (plus the refused duplicate). Sign-off writes were blocked, and none was attempted.

| # | Scenario | Result |
|---|---|---|
| A1–A2 | QA, no analysis | Context "Target Application: M2LB · Environment: QA · Scope: Entire project"; intro texts |
| A3–A5 | Not assessed | "Release Readiness — Not assessed — Required relationship evidence has not been prepared." with no counts; prerequisites list; governance not assessed |
| C1–C2 | Analyze relationships | Opens Relationship Review with "Target Application: M2LB · Environment: QA"; back still reads Not assessed (nothing was analyzed there) |
| D1–D3 | Development, with analysis | Blocked ("Blocked by governance."); counts: requirements 0, confirmed tests 0, tests executed Not assessed, blocking 1, open decisions 1, 9 dimensions not assessed; the dimensions table; never Ready with unassessed required evidence |
| B1, B3 | Scope | Selected modules gives the reason (M2LB has no feature packs); evidence names analysis 4ece441f and release/1 · governance/1 |
| D4–D5 | Findings and actions | The shared gate (Blocked, 1 finding); the Test design action opens Functional Quality Scope |
| E1–E5 | Freeze | Opens by id with analysis and versions and "never changes"; release decision separate, none recorded; history row; the duplicate freeze is refused with a message; reopening is identical |
| F1 | Phone width | 390 px: no overflow |
| Z1–Z2 | Safety | No external host; no page errors |

## H. Gaps

| Item | Status |
|---|---|
| Identity, scope choice, Not assessed overview, counts, dimensions table, tabs, draft/frozen/decision separation, duplicate freeze | Implemented · Unit · Browser |
| Test execution in readiness (`RequirementTestExecutions` per requirement and environment) | **Not implemented**: the rule lives in the frontend and needs the Functional Quality Scope test links and the workspace TRX evidence; shown as Not assessed with a link |
| AQR / IQR / FQR / Performance / Critical E2E results and quality-finding de-duplication across modules | Not implemented (Optional, Not assessed, linked) |
| Scope by features, capabilities, services or saved release definitions; cross-module dependency flags | Not implemented |
| Back navigation from a dimension's page keeps the chosen modules | Not implemented (the scope is page state; back returns to Entire project) |
| Real M2LB readiness beyond Blocked / Not assessed | Not verifiable: the copy has no Delivery snapshot, so no requirements are in scope; no positive readiness was fabricated |
| Concurrent freezes by two reviewers | Duplicate rule only (no lock); not browser-tested |
