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

**Status at commit time: RUNNING.** The user asked not to wait. **No browser result is claimed here.** The result is added in a follow-up commit when the run completes.

## Regression

The full frontend and backend partition-6c rerun was stopped before it started: it would have shared build outputs with this task's build. It must be run on the final tree. Its status is **NOT RUN** at this commit.
