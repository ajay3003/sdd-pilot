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

**Limitation, not hidden:** Recommended Workflow showed **0 next actions in every view** within the 3.5 s the harness waited. The per-view check therefore proves the projection, not the action content. The audience risk wording is covered by the automated test only, because the real workspace has no open baseline risk. **Responsive phase: NOT RUN.**

## Regression

The full frontend and backend partition-6c rerun was stopped before it started: it would have shared build outputs with this task's build. It must be run on the final tree. Its status is **NOT RUN** at this commit.
