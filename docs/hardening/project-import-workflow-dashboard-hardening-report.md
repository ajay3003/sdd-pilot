# Hardening Pass 1: Project Import, Recommended Workflow, Project Quality Dashboard (2026-11-05)

Branch `008-traceability-first`, not pushed. NEXT41.

> **Finding IDs.** The brief referenced 47 findings (IMP-01…19, WF-01…12, DASH-01…16), but the list itself was not included. The IDs below are therefore reconstructed from the brief's sections (§3–§24) and from what was reproduced on the M2LB copy. Unused IDs are not listed.

## A. Executive summary

The main root cause was shared: **a stored source snapshot was treated as an analyzed one, and as the project's source.**

1. **Snapshot existence counted as success.** `TechnologyCoverageController` returned the workspace's newest snapshot with no outcome. Recommended Workflow and the Dashboard therefore read "Analyzed" / "Source code analyzed" whatever the analysis result was.
2. **Wrong snapshot.** That newest snapshot was whichever package was imported last (`M2LB.Common.zip` on the copy), not the Target Application's own source (`M2LB.zip`).
3. **Divergent rules.** Import judged packages by the code analyzer's status first. A snapshot whose code analyzer said Ready while another area was partial read "Ready" on Import and "Partial" in the Snapshot Registry.

**Fixes:**

- One shared outcome rule (`SourceSetupOutcome.Overall`) now feeds the coverage read model, the source input, Import and the Dashboard.
- The coverage read takes the Target Application's main package snapshot from Project Evidence.
- The rule itself was corrected: a source with no usable area is Failed, never Partial.

The secondary defects were presentation and semantics:

| Defect | Fix |
|---|---|
| Ambiguity reported as "missing required evidence" | Grouped as "Ambiguous evidence" |
| A source binding demanded with no Artifact Pack chosen | New `NoScope` state; nothing to bind until a pack is chosen |
| "QA verification: Not verified" with no evidence | "No verification evidence" |
| "Overall readiness" (implies release) | "Review readiness" |
| Package "Associated" without saying with what | "Associated with application" |
| Import's context strip said "Not configured" while Workflow showed the project | The strip resolves Project Evidence once |
| Page name | Dashboard renamed Project Quality Dashboard |

**Blockers:** none. **Open:** see G.

## B. Finding register

**Classification:**

- **F** functional defect
- **P** presentation/semantic defect
- **E** expected (clarified)
- **D** data-dependent
- **N** not reproducible
- **O** open (not done)

**Test evidence:**

- **U** unit/component tests
- **B** browser check (`scratchpad/h41-e2e.cjs`, check id)

| ID | Observation (§) | Root cause | Class | Sev | Files | Evidence | Disposition |
|---|---|---|---|---|---|---|---|
| IMP-01 | Import "Project evidence: Not configured" while Workflow shows "M2LB · 1 of 2 roles" (§3 identity) | `ProjectContextStrip` read the cached `Peek` view only; Import never resolved Project Evidence | F | P0 | `ProjectContextStrip.razor` | U `ProjectContextStripTests`; B A1, C2 | Fixed, verified |
| IMP-02 | Package summary vs Snapshot Registry disagree (§4, §5) | Import used the code analyzer's Ready/Partial before the areas | F | P0 | `ImportProjectOverview.cs` | U `SourceOutcomeSemanticsTests`; B A2 (7/7 agree) | Fixed, verified |
| IMP-03 | Terraform-BasicOpsInfra_Meldingsutveksler "Failed" / no app project (§5) | Fixed in NEXT36 for the registry; Import now uses the same rule | F | P0 | shared `SourceSetupOutcome` | U; B A3 ("Infrastructure evidence · Partial · no application code") | Fixed, verified |
| IMP-04 | A source with nothing usable (CI/CD failed, architecture unsupported) | The shared rule returned Partial (Unsupported counted as present); Import said Failed | F | P1 | `IntegrationJourneyContracts.cs` | U `SnapshotRegistryTests` (+2 cases), `ImportProjectOverviewTests` | Fixed, unit only (no such package on the copy) |
| IMP-05 | Package roles across pages (§8) | Roles come from contents; after load: M2LB primary, M2LB.Common supporting, Terraform ×5 infrastructure | E | — | — | B A4 | Clarified (consistent) |
| IMP-06 | Terraform shown as "Application source" for a moment (§8) | Before Source Analysis setup loads, `ProjectEvidencePackages.Of` falls back to "snapshot ⇒ application source" (the older-backend path) | P | P2 | `ProjectEvidencePackages.cs` | Observed in the probe at 8 s; correct at 30 s | **Open**: the fallback also feeds the main-package lookup; changing it needs its own pass |
| IMP-07 | Skolenærvær "2 of 2 required sources available" + "2 required bindings not configured" (§6) | On the copy: "MU Not found · M2LB 2 possible sources found · Altinn built-in test reference · IaC optional". Availability and binding are separate texts | D | — | — | Probe | Not changed; binding reuse not browser-tested (needs binding writes) |
| IMP-08 | Altinn "Built-in OpenAPI reference" (§7) | Shown as "Built-in test reference available": a reference, not an imported source | E | — | — | Probe | Clarified |
| IMP-09 | Import actions keep context (§9) | Not audited beyond the strip | O | P2 | — | — | **Open** |
| WF-01 | Source "Ready / Analyzed" for a partial snapshot (§10) | Coverage carried no outcome | F | P0 | `TechnologyCoverageContracts.cs`, `TechnologyCoverageController.cs`, `ProjectInputs.cs` | U; B C1 | Fixed, verified |
| WF-02 | The source card showed the package imported last (`M2LB.Common.zip`) (§3, §10) | Coverage = workspace's newest snapshot | F | P0 | `TechnologyCoverageController.cs` (`snapshotId`), `ProjectApplicabilityState` | U; B C1 (M2LB.zip) | Fixed, verified |
| WF-03 | "Unavailable" over "Partially analyzed" (regression found during the fix) | Consumers treated only Ready as usable | F | P1 | `WorkflowPaths.cs`, `ReviewNavigator.cs`, `WorkflowReadinessService.cs`, `DashboardPresentation.cs`, `RecommendedWorkflow.razor` | U `ProjectInput.IsUsable`; B C1 | Fixed, verified |
| WF-04 | A failed analysis would read "needs refresh / out of date" | No distinct state | P | P1 | `ProjectInputStatus.Failed` (appended) | U | Fixed, unit only (no failed snapshot on the copy) |
| WF-05 | "Establish source binding …" with no pack chosen (§11, §20) | `SourceBindingOf` returned NotEstablished without an active requirement scope | F | P1 | `SourceBindingState.NoScope` (appended) | U `RecommendedWorkflowEvidenceTests` (+2); B C4, D6 | Fixed, verified |
| WF-06 | "0/6 core manual reviews recorded" (§12) | Counted from persisted decisions (`WorkflowReadinessService` core reviews) | O | P2 | — | — | **Open**: not re-audited |
| WF-07 | "No recommended packages are defined for M2LB" with imports present (§13) | Recommendations are an explicit list | P | P2 | `RecommendedPackagesCard.razor` | B C3 | Clarified text, verified |
| WF-08 | "2/2 required roles ready" (§10) | The copy shows "1 of 2" | D | — | — | Probe | Data-dependent; not changed |
| DASH-01 | Rename (§15) | — | P | P2 | `Dashboard.razor`, `NavigationCatalog.cs` (alias "Dashboard"), `DocumentationCatalog.cs` | U `NavMenuTests`; B D1 | Fixed, verified (route `/dashboard` kept) |
| DASH-02 | "Overall readiness" reads like release readiness (§17) | Label | P | P1 | `Dashboard.razor` | B D2 | Fixed, verified |
| DASH-03 | "Source code analyzed" while packages are Partial/Failed (§21) | Same as WF-01 | F | P0 | `DashboardPresentation.cs` | B D3 | Fixed, verified |
| DASH-04 | "QA verification: Not verified" with no results (§18) | Label | P | P1 | `DashboardQualitySummary.cs` | U; B D4 | Fixed, verified |
| DASH-05 | Artifact ambiguity under "Missing required evidence" (§23) | Group by key prefix merged `ambiguous-*` with `missing-*` | P | P1 | `DashboardQualitySummary.Group` | U; B D5 | Fixed, verified |
| DASH-06 | Binding item counted with no module chosen (§20, §23) | WF-05 | F | P1 | — | B D6 | Fixed, verified |
| DASH-07 | "8 imported packages Associated" (§20) | The association type was not named | P | P2 | `SddDashboard.cs` | U `SddDashboardTests`; B D7 | Fixed, verified |
| DASH-08 | "Shared identities and relations: Not interpreted yet" (§22) | The tile reads the canonical summary for the environment (correct). On the copy an analysis now exists (NEXT39) and is reused | E/D | — | `CanonicalSummaryTile.razor` (wording) | B D8 | Clarified; wording aligned with Relationship Review |
| DASH-09 | Metrics 8 / 6 / 1 / 0 / 1, 18 ready / 2 blocked / 20 (§16) | Copy: 8 inputs, 6 ready, 1 partial, 1 not assessed; 20 reviews: 11 ready, 9 blocked | O | P2 | — | — | **Open**: definitions not re-audited |
| DASH-10 | Document inventory reconciliation (§19) | **Observed mismatch:** Dashboard "39 plans" vs Import "28 plans across 12 modules". The Dashboard appears to include the 11 project-level plan candidates | F | P1 | — | Probe | **Open** |
| DASH-11 | Recent Activity (§24) | Not audited | O | P2 | — | — | **Open** |

## C. Shared foundations

**Reused:**

- Snapshot Registry outcome rule
- Project Evidence (main package, associations)
- Source Analysis setup areas
- `StatusVocabulary` / `SemanticStatusBadge`
- the workflow/readiness services

**Corrected:**

- `SourceSetupOutcome.Overall`: no usable area means Failed.
- `ProjectTechnologyCoverage` gained `SourceOutcome` and `SourceAreas` (optional, backward compatible).
- `ProjectInputStatus.Failed` and `SourceBindingState.NoScope` were appended, keeping stored numeric values stable.
- `ProjectInput.IsUsable` is the one definition of "usable source".

No new registry, engine, store or vocabulary.

## D. Data integrity

- Nothing was re-imported, re-analyzed, deleted or rewritten.
- The browser run compared every registry snapshot's id, status and content fingerprint before and after: identical (E2).
- No association was invented or auto-confirmed; the strip only triggers the existing, cached Project Evidence resolution.
- No API write was attempted during the browser run (Z3).

## E. UI changes

- **Names:**
  - sidebar "Project Quality" (search alias "Dashboard");
  - page title and heading "Project Quality Dashboard";
  - the intro from the brief, verbatim.
- **Source labels:** "Analyzed" / "Partially analyzed" / "Analysis failed" / "Needs refresh", all from the shared outcome.
- **Binding and association:** "No Artifact Pack chosen" for the source binding; "Associated with application" on packages.
- **Other labels:**
  - "Ambiguous evidence" attention group;
  - "No verification evidence";
  - "Review readiness";
  - the recommended-packages explanation;
  - Relationship Review wording on the identities tile.

## F. Commands and results

| Run | Result |
|---|---|
| Full frontend suite (scratch worktree) | **8170 passed, 0 failed** |
| Full backend suite | **5908 passed, 13 skipped, 0 failed** |
| New tests | `SourceOutcomeSemanticsTests` (4), `ProjectContextStripTests` (2), `RecommendedWorkflowEvidenceTests` (+2), `SnapshotRegistryTests` (+2 cases) |
| Updated assertions | Rename, QA label, tile wording, package association label |

**Real browser** (`scratchpad/h41-e2e.cjs`):

- **Setup:** Edge, DB `birknext_gaps`, environment `ci-live` "Development", Target Application M2LB. Localhost only; writes aborted.
- **Result: 25/25.** The first run was 23/25: two checks did not account for CSS uppercase text. They were made case-insensitive and the run repeated.

| Group | What was checked |
|---|---|
| A — Import | Strip shows M2LB on a fresh session; 7/7 package outcomes equal the registry; Terraform-BasicOpsInfra_Meldingsutveksler infrastructure · Partial · no application code; roles from contents |
| C — Workflow | M2LB.zip "Partially analyzed"; same strip; packages text; no binding recommendation without a module |
| D — Dashboard | Rename; Review readiness; "Source: partially analyzed"; "No verification evidence"; "Ambiguous evidence"; no binding item; "Associated with application"; tile wording; back keeps context |
| E — Persistence | Refresh gives identical counts (3, 0, 11, 0); registry unchanged |
| F — Phone width | 390 px on all three pages: no overflow |

**Not browser-tested:**

- §36 B: binding a Skolenærvær source and reusing confirmed bindings (needs binding writes);
- §36 C: resolving a binding with controlled data;
- choosing a module (would change the session's active scope).

## G. Status

| Group | Items |
|---|---|
| Fixed and browser-verified | IMP-01, IMP-02, IMP-03, WF-01, WF-02, WF-03, WF-05, WF-07, DASH-01 to DASH-07 |
| Fixed, not browser-verified | IMP-04, WF-04 (no matching data on the copy; unit-tested) |
| Clarified (expected) | IMP-05, IMP-08, DASH-08 |
| Data-dependent | IMP-07, WF-08 |
| Open | IMP-06 (transient role before load), IMP-09 (action navigation), WF-06 (core manual reviews), DASH-09 (metric definitions), DASH-10 (plan count 39 vs 28), DASH-11 (Recent Activity) |
| Blocked | None |
