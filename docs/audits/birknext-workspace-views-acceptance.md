# Workspace Views — acceptance

## Automated (independent expectations from the brief)

Frontend focused run on the final tree: **431/431 passed** (navigation, layout, workspace views, focus panel, Dashboard, Recommended Workflow, Feature Visibility, Test Plan Suggestions).

| Fixture | Test |
|---|---|
| A–H eight views (ids, labels, order, descriptions, memberships) | `Eight_built_in_views_in_selector_order…`, `Presets_HoldTheirRolesModules` (TL, TT, MT, SM, PM, Dev, Arch), `EveryPreset_NamesRealModules_AndRealGroups` (all focused views) |
| I view switching (never navigates, never changes the project) | `SwitchingTheView_StaysOnTheCurrentPage_AndNeverChangesTheSelectedProject`, `SelectingAnotherProject_NeverChangesTheView` |
| K renamed-view persistence, L invalid saved view, M customization (additions per view) | `A_saved_preference_keeps_its_view_after_the_label_changes…`, `EarlierPreferences_StayValid…`, `CollapseChoices_AreRememberedPerView…`, `ResolutionModel_…AdditionsNeverShowADisabledModule` |
| N hidden destination, O retired feature, P Feature Visibility over the view | `EveryVisibleModule_IsReachableInEveryView…` (8 views), `ResolutionModel_FeatureVisibilityOutranksTheView…` |
| T critical blocker in every view (same identity, audience wording) | `The_baseline_risk_has_one_identity_in_every_view_and_audience_wording` |
| Distinct views (TT ≠ Dev ≠ MT; TL functional + technical; PM without runners) | `Specialized_views_are_distinct_and_reduce_navigation` |
| Y active page outside the view | `OutsideViewNotice_KeepsThePageOpen_OffersAddAndSwitch` |
| U manual test-plan suggestion facts (MT and TL) | `ReviewedRequirementCoverageTests.S_T_…`, `TestPlanSuggestionsPageTests.Dashboard_and_Recommended_Workflow_focus…` |

**NOT RUN as separate fixtures:**
- Q (runtime unavailable): covered by the existing availability tests, not re-run per view;
- R/S (missing baseline / alternative snapshots): the shared risk test above;
- V/W (integration failure, performance finding);
- X (project switch: the existing test only);
- Z (API restart).

## Browser (isolated Release install, real workspace, Edge)

Harness `scratchpad/mb/wv-accept.cjs`:
- **main:** the selector lists the eight views in order; each view switch keeps the page open and writes nothing to the API; every sidebar destination opens a real page; Dashboard and Workflow focus are shown per view; baseline risks are the same in every view; the view survives a refresh; the source context fingerprint is unchanged;
- **responsive:** six focused views at five widths.

**Result (main phase, completed after the first commit): 28/28 PASS.** 0 console errors; 0 requests outside localhost.

| Check | Result |
|---|---|
| WV-01: the selector lists the eight views in order (All Modules, Project Manager, Scrum Master, Test Lead, Manual Tester, Technical Tester, Developer, Architect) | PASS |
| Switch to each view from an open Test Data page: the page stays, 0 API writes (8 transitions) | PASS ×8 |
| Every sidebar destination opens a real page: All Modules 48, PM 13, SM 11, TL 22, MT 14, TT 20, Dev 18, Arch 20 | PASS ×8 |
| Dashboard renders the focus panel of the selected view (`data-view`) | PASS ×8 |
| WV-02: baseline risks identical in every view (none on this workspace: the baseline is selected) | PASS |
| WV-03: Technical Tester survives a browser refresh | PASS |
| WV-04: the source-context fingerprint is unchanged after all switches (`b89ea3d72f2f7566`) | PASS |

**Earlier limitation (superseded below):** the first run showed 0 next actions in every view within a fixed 3.5 s wait. It proved the projection, not the action content.

### Final release acceptance (2026-11-13, isolated Release API with `bc8a893b`)

Setup:
- harness `scratchpad/mb/rr-accept.cjs workflow`: a fresh Edge profile, non-localhost requests aborted;
- wiring guard `probe-net.cjs` first: 5 calls to :5496, 0 failed, session connected. An earlier run had silently used the web app's default backend :5000 after a republish; its results were discarded;
- each view waits for a terminal state (actions or a stated empty message), never a fixed delay.

**Result: 8/8 PASS**, 0 console errors, 0 blocked requests.

| View | Next steps (ready / blocked) | First actions shown |
|---|---|---|
| All Modules | no focus panel (by design) | — |
| Project Manager | 0 / 1 | Requirements Traceability (blocked) |
| Scrum Master | 0 / 4 | Specification, Plan, Task Explorer (blocked) |
| Test Lead | 1 / 7 | Document Quality Review (ready now) |
| Manual Tester | 2 / 2, plus 6 Test Plan proposal steps | Document Quality Review, Documentation Drift (ready now) |
| Technical Tester | 1 / 5 | Code Risk Review (ready); API Quality Review and Critical E2E (blocked) |
| Developer | 4 / 4 | Source Analysis, Code Risk Review, Dependency Review (ready) |
| Architect | 2 / 5 | Source Analysis, Dependency Review (ready); Integration Quality Review (blocked) |

- The first view needed 17 s: its data loads cold. The other views switch without new requests.
- Screenshots are in `scratchpad/mb/out/rr-workflow-<view>.png`. The Manual Tester one shows "Your next steps · Manual Tester" and the Test Plan proposals.
- All Modules (`rr-accept.cjs all`) is 7/7 PASS: 48 destinations, no duplicates; the specialized menus have PM 13, SM 11, TL 22, MT 14, TT 20, Dev 18 and Arch 20 entries; it survives a refresh. Scaling is 1/1 PASS.
- **Memory during this run:** the isolated API peaked at 2.9 GB private (Workstation GC) and settled to 1.27 GB. See the performance report, gap TPS-P2.

### Responsive (`scratchpad/mb/resp.cjs`, `resp-toggle.cjs`)

Each page was loaded once and then resized to 1440, 1280, 1024, 768 and 390 px.

| Page | Horizontal overflow | MetaBadge / SemanticStatusBadge | Workspace selector |
|---|---|---|---|
| Recommended Workflow | none at any width | none on this page | visible ≥ 768 px; behind the menu button at 390 px |
| Dashboard | none | 13–14 badges, minimum 12 px, 0 clipped at all widths | same |
| Test Plan Suggestions | none | none on this page | same |
| Relationship Review | none | none: the workspace's relationships are **not analyzed** ("Not analyzed — analyze relationships on the Overview tab"). The earlier `rr-accept badges` check counted 0 badges there and **FAILED as a harness/data issue**, not a product result | same |
| Source Analysis | none | 13 badges, minimum 12 px, 0 clipped | same |
| Requirements Traceability | none | none | same |

- At 390 px the menu button opens the navigation. The selector is then visible, and switching to Developer works without horizontal scroll.
- Screenshots are in `scratchpad/mb/out/resp-<page>-<width>.png`.
- The 338 existing sub-12 px rules were not sweep-fixed, as instructed.
- **Not verified:** badges inside Relationship Review. Analyzing relationships would write to the workspace, and that wasn't run.

## Regression

Final release acceptance (`a6598fb7`): frontend **8 735/8 735 passed**. Backend: 6 322 passed, 0 failed, 13 skipped; partition 6c3 is BLOCKED by system memory (see `birknext-release-readiness-final.md`).
