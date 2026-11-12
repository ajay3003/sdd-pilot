# Manual testing — capability audit (tasks 16–19, one report set)

This is the canonical audit for the Manual Testing view (task 16), the view-aware Recommended Workflow (task 17), the role-aware workspace
(task 18) and the manual-testing workspace (task 19). The task-specific report names in those briefs map to this set:
- this audit;
- `birknext-manual-testing-view-gap-register.md`;
- `birknext-manual-testing-acceptance.md`;
- `docs/hardening/birknext-manual-testing-implementation.md`.

The scenario inventory and the Test Data audit are sections below, not separate files.

- **Baseline:** `618594fa` on `008-traceability-first`.
- **Untouched unrelated work:** other sessions' untracked audits, `AIAssisted/testdata/`, and the modified `appsettings.Local.json`.
- **Method:** read-only code audit with file:line evidence. Paths are relative to `AIAssisted/frontend/BirkNext.Web` unless noted.

## 1. Workspace views (the existing "Menu Views")

| Fact | Evidence |
|---|---|
| Built-in views only, as an enum: AllModules, ProjectLeader, ScrumMaster, TestLead, Developer, Architect (numbers stored). No custom views, no editor, no "restore default". | `Services/NavigationCatalog.cs` (`WorkspaceView`, `WorkspaceViews.All`) |
| A view chooses modules and puts its focus groups first. It could not regroup or reorder items, and cannot rename labels. | `WorkspaceViews.Ordered`, `NavMenu.razor` `Rows` |
| Every view always keeps Project Quality, User Guide and System Settings. | `WorkspaceViews.EveryView` |
| Stored in this browser only (`localStorage` key `birknext.navigation.v1`). An unknown value falls back to All Modules. Former "TechnicalReviewer" maps to Developer. | `NavigationSectionState.Restore` |
| Per-view personal additions ("Add X to <view>"); "Show other modules"; search groups results as current view, then other views, then disabled. | `NavigationSectionState`, `NavSearch.razor`, `WorkspaceScopeNotice.razor` |
| Resolution order: Feature Visibility (a disabled module is no row in any view), then the view, then the person's additions. There is no per-module authorization, and a view is never a permission. | `NavigationModules.Resolve` |
| Switching never navigates and never touches the project, running work or the source baseline. | `NavMenu.razor` `OnViewChanged`; `NavigationWorkspaceTests` |
| Help text about views was outdated: it listed "Technical Reviewer", omitted Developer and Architect, and said a view only changes group order. | `Components/Guide/GuideGovernance.razor` |

## 2. Capability inventory for manual testers

Classification is based on what each module does, not its name. Key: CORE = primary Manual Testing entry; OPTIONAL = available, not in the
primary menu; TECHNICAL = Technical QA / Developer.

| Module (route) | What a manual tester can do | Technical knowledge needed | Classification |
|---|---|---|---|
| Import Project (`project-import`) | See where project material comes from; import a package | No | CORE (Project setup) |
| Source Management (`sources`) | See which version of a source is analysed; choose the analysis baseline | No (details on demand) | CORE (Project setup) |
| System Understanding (`system-understanding`) | Flow, steps, rules, data, and what is source-backed versus documented | No | CORE |
| Functional scenarios: System Understanding → What to test (`system-understanding?tab=tests`) | `GuideTestScenario` gives preconditions, test data, expected outcome, observable evidence, failure cases and existing tests, each with a state (Suggested, NeedsClarification, ExistingDefinition, Executed, Verified); guide revisions are reviewed | No | CORE (scenario destination) |
| Specification Explorer (`specification-explorer`) | Requirements, user stories, Given/When/Then acceptance scenarios with linked requirements | No | CORE |
| Document Quality Review (`quality/document`) | Review the clarity, completeness and testability of requirements (the actual "specification review"; no separate Specification Review route exists) | No | CORE |
| Documentation Drift (`documentation-drift`) | Documents that no longer match: changed or outdated requirements | No | CORE (Requirements) |
| Test Data (`test-data`) | "Ready for testing?" readiness, the asset catalog with status (Draft, NeedsReview, Approved, Deprecated), "Used by" consumers. XML fixtures are one section, not the primary view. | No for the overview | CORE |
| Requirements Traceability (`artifact-traceability`) | Designed, referenced and executed coverage kept apart; candidate links need confirmation; current vs historical | No | CORE |
| Test Coverage & Overlap Review (`test-coverage-review`) | Requirements without tests, overlapping tests, tester-review items (`NeedsTesterReview`) | No | CORE |
| Project Quality (`dashboard`), Recommended Workflow (`getting-started`), User Guide | Status, next steps, help | No | CORE |
| Critical E2E Regression (`critical-e2e-regression`) | Versioned flows with steps, expected values and requirement refs; suggestions with states (NeedsTestData, NeedsExpectedResult, …). Execution is browser automation. | Partly (flow editor, runner) | OPTIONAL (Test Lead / Technical QA) |
| Skolenærvær Testing (`extensions/skolenaervaer`) | Static, code-defined event scenarios for Active Event Tests; "nothing runs from this page" | Yes (events, IQR) | OPTIONAL (project extension) |
| Manual integration test cases (IQR message-flow review) | Preconditions, action, expected flow and checkpoints for integrations | Yes (IQR) | TECHNICAL |
| Task Alignment Review, Relationship Review, Snapshots, Release Readiness | Implementation alignment, association review, history, release governance | Mixed | OPTIONAL (Test Lead / Project Leader views) |
| API/Frontend/Integration Quality, Contract Testing, Performance, Schema Studio, Source Analysis, Pipeline, Dependency | Technical reviews | Yes | TECHNICAL |

## 3. Scenario inventory and ownership

| Representation | Owner and storage | Kind (see the brief's categories) |
|---|---|---|
| `GuideTestScenario` (System Understanding) | `SystemGuideService`; DB `system_guide_revisions` (immutable beyond Draft) | SUGGESTED / REVIEWED scenario (per scenario state; per guide revision) |
| `SemanticAcceptanceScenario` (Specification Explorer) | Built in memory from `spec.md` | SPECIFICATION_SCENARIO |
| `CriticalE2EFlowDefinition` + suggestions | `CriticalE2EStore` (JSON files in App_Data) | EXECUTABLE_TEST_DEFINITION; suggestions are SUGGESTED until Approved |
| `QaTestCandidate` (Test Coverage & Overlap) | Persisted run results | Derived candidates (from spec scenarios and E2E flows) |
| Skolenærvær scenarios | Static code (`SkoleNaervaerScenarioProvider`) | Technical event scenarios |
| `ManualIntegrationTestCase` | Message-flow review store | MANUAL_TEST_CASE_DRAFT (technical, integration) |
| Functional Quality Scope (Delivery Reporting) | `functional_scope`, `test_design_assessment` tables | Assessment of user stories, scenarios and test cases |
| Legacy `Scenario` entity | `scenarios` table; its only writer is not rendered | Orphaned; not used |

**Decision:** the Manual Testing scenario destination is System Understanding → What to test. It is the only representation with preconditions,
test data, expected outcome, observable evidence and an explicit review state, written for testers. No new scenario store is created.
Critical E2E stays the executable layer, and Specification Explorer stays the requirement-side source.

## 4. Test Data (audit)

- **Route and sections:** `/test-data` has four sections: Overview, assets, XML fixtures, and requests & configuration.
- **What a non-technical tester sees:** the Overview gives "Ready for testing?" readiness and the next action; the assets section shows status and
  consumers. The tester does not need XML.
- **Lifecycle:** `TestDataLifecycleState.ReviewRequired` exists in contracts and is set by Source Change Impact, but **no page renders it**
  (gap MT-6).
- **Scenario links:** links exist to consumers (flows, journeys, packs). There are **no links between guide or spec scenarios and test data
  assets** (gap MT-5).

## 5. Recommended Workflow and Dashboard (audit)

- **Shared capability model:** both pages share `ReviewCapability`, built by `WorkflowReadinessService` and `ReviewNavigator`. Each capability has
  a stable `Id`, `Route`, readiness, requiredness and a human decision. It has no severity and no audience.
- **Next action:** Recommended Workflow's next action is `WorkflowRecommendation`, with a reason code. Dashboard "Top Recommendations" are the
  first 3 audit recommendations, all linking to `quality/document`.
- **No view concept:** neither page knew the workspace view. That was the root cause of "everything for everyone" on both pages (tasks 17/18).

## 6. Azure DevOps boundary

- **Read:** projects, repositories, commits, items, and one work item with relations.
- **Test Plans, Test Suites, Test Cases:** no read and no write exist. Nothing in BirkNext creates or links Test Cases.
- **Consequence:** the Manual Testing help states the boundary. BirkNext never claims a scenario is a Test Case or an executed test.

## 7. Source context

The view and the focus panel read the shared source context; neither changes it. With alternative versions and no baseline
(`BASELINE_NOT_SELECTED`), the risk is shown in every view. See `birknext-canonical-source-context.md`.
