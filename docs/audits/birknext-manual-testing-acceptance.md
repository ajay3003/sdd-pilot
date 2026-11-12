# Manual testing — acceptance (tasks 16–19, first increment)

**Status:** the Manual Testing view, the shortcut to the scenarios ("Functional Test Scenarios", pointing at System Understanding → What to test),
the view-aware next-steps panel and the help article are VERIFIED in a real browser (40/40). The tasks' further items are open; see the gap register.

## Environment

- **Code under test:** the working tree on top of `075c006c`, committed together with this report. The scratch build tree was synced from it, and
  both test projects built successfully before every run.
- **Isolated install:**
  - Release web on :5497 and Release API on :5496;
  - database `birknext_mb` in the disposable PostgreSQL 16 cluster: the real M2LB snapshots, analysis baseline M2LB (7), 6 recorded decisions.
- **Browser:** Edge driven by Playwright. No request left localhost.
- **Afterwards:** API, web server and cluster were stopped; the data is kept.
- **Harness:** `scratchpad/mb/mt-accept.cjs`; results in `scratchpad/mb/out/mt-accept.json`; screenshots `mt-*.png`.

## Browser acceptance (40/40)

| Id | Case (brief) | Result |
|---|---|---|
| MT-A | Select Manual Testing (A); six groups in workflow order | PASS: Project setup, Understand the system, Requirements, Test preparation, Coverage & traceability, Quality & follow-up (then Help & Settings) |
| MT-A2 | First-time explanation (short description; "not a permission") | PASS |
| MT-B | Rows are exactly the workflow modules, in order | PASS |
| MT-R (13) | Every destination opens a real page (Import Project, Source Management, System Understanding, Specification Explorer, Document Quality Review, Documentation Drift, Functional Test Scenarios, Test Data, Requirements Traceability, Test Coverage & Overlap Review, Project Quality, Recommended Workflow, User Guide) | PASS |
| MT-L | Scenario destination (L/M): System Understanding with "What to test" selected; exactly one current row | PASS |
| MT-Y | Help Center article (Y): view ≠ permission, Azure DevOps Test Plans boundary, no-frontend testing, XSD | PASS |
| MT-W | Recommended Workflow: at most 3 next steps, each with a real destination | PASS: Document Quality Review, Documentation Drift (ready), Specification Explorer (blocked: "Needs setup: Choose the Specification artifact to review first") |
| MT-D | Dashboard preview shows the same capability ids and links to Recommended Workflow | PASS |
| MT-R2 | Baseline risk only when real (M2LB (7) is selected) | PASS: none shown. The risk itself is unit-tested in every view. |
| MT-C | Switch to Developer (C): technical relevance, no reload | PASS: source-analysis, dependency-review, pipeline-review |
| MT-Bb | Return to All Modules (B): panel steps aside, catalog groups back | PASS |
| MT-I | Back to Manual Testing: same recommendation identities | PASS |
| MT-O | Baseline preserved (O): used snapshot unchanged, no decision recorded by view switches | PASS: 46d8661d…, history still 6 |
| MT-F | Browser refresh (D/F) | PASS |
| MT-S | New tab (S) | PASS |
| MT-K | Hidden technical modules stay reachable (K): search lists Source Analysis under "Available in other workspaces" | PASS |
| MT-X | Keyboard-operable view selector (X) | PASS |
| MT-V (10) | No horizontal overflow at 1440/1280/1024/768/390 on Recommended Workflow and Dashboard, with the mobile drawer open at ≤768 (W) | PASS; screenshots inspected |
| Z | No console or page error; nothing left localhost | PASS |

**Found by screenshot inspection and fixed:** a blocked step repeated its reason with broken grammar ("Needs Choose … first first"). It now shows
"Blocked" and the capability's own status text, once. The fix was re-verified in the browser.

## Covered by tests only (not by the browser)

| Case | Test |
|---|---|
| Invalid or removed stored view (G/H) | `ManualTestingViewTests.The_stored_view_restores_and_an_unknown_stored_view_falls_back_to_All_Modules` |
| Disabled feature (I) | `ManualTestingViewTests.Feature_Visibility_outranks_the_view…`; changing System Settings in the acceptance install was avoided |
| Every enabled module is reachable in Manual Testing | `ManualTestingViewTests.Every_enabled_module_is_reachable…` |
| Baseline risk in every view | `WorkspaceFocusTests.The_source_baseline_risk_is_visible_in_every_view` (4 views) |

**Not applicable:** retired features (J) can never appear, because no catalog row reads a retired key. Per-module authorization (K) does not exist
in BirkNext; a view is not a permission.

**NOT RUN:** API restart (E) for the view. It is stored in the browser only, so an API restart cannot affect it.

## Focused regression (on this tree)

- **Frontend:** navigation, NavMenu, Manual Testing, WorkspaceFocus, guide, documentation catalog, Feature Visibility, Dashboard, Recommended
  Workflow and System Settings: 518/518. After the wording fix, focus and navigation: 106/106.
- **Backend:** not affected by this change. Contract fix: 16/16.
- **Release build** of API and web: succeeded (0 errors), on the tree before the wording fix (presentation code only).
- **Full regression:** the last full run is `22710749` (see the multi-branch implementation report). It has not been repeated on this tree.
