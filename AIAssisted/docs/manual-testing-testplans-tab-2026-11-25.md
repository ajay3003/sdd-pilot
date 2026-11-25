# Manual Testing — «3. Testplaner» tab (2026-11-25)

## Goal

Test plan management gets its own tab, the last one in Manual Testing. «Finn og forstå» stays focused on scenarios, and «Planlegg og klargjør» stays focused on test cases. The project is the scope for test plans: choosing a module or specification never hides a test plan.

## Result

| Tab | Route | Owns |
|---|---|---|
| 1. Finn og forstå | `/manual-testing/discover` | Requirements, scenario suggestions, choosing scenarios (unchanged) |
| 2. Planlegg og klargjør | `/manual-testing/plan` | Test cases of the current test plan, with a link to «Administrer testplaner» |
| **3. Testplaner** | `/manual-testing/plans` (also `/manual-testing`) | «+ Ny testplan», saved test plans (open, archive, restore, delete), «Overfør testplan», Neste steg, counters |

- `ManualTestingPresentation.Stages` gained `("plans", "Testplaner", "Hvilke testplaner har vi?", PlansRoute)`. `TestingWorkspaceHeader` therefore shows three numbered tabs, and Testplaner is the last one.
- `Pages/ManualTesting.razor` is the Testplaner tab, reached through both routes. The old home content (Neste steg, counters, readiness) is kept below the saved test plans.
- **Transfer moved:**
  - «Overfør testplan» is no longer a tool on the plan page. The plan page's «Overfør testplan» button navigates to `manual-testing/plans?transfer={id}`.
  - The SavedPlans «Overfør» link, Neste steg and the old links (`?panel=export`, `test-plan-suggestions?…&area=export`) all go to the same place.
  - The same `AdoTransferPanel` is reused, so there is no second transfer system.
- **Plan page:**
  - The tools are now «Testtilfeller», «Versjoner» and «Kildeendringer».
  - The new link «Administrer testplaner» (`mtp-manage-plans`) goes to the Testplaner tab.
  - With unsaved edits, «Overfør testplan» asks first, like every other switch.
- **Navigation:**
  - `FeatureVisibilityTaxonomy` has `manual-testing/plans` as an alias of `manual-testing`.
  - The Manual Testing nav entry has the aliases «Testplaner», «Test plans», «Ny testplan» and «Overfør testplan».
  - `DocumentationCatalog` describes the tab.
- **Terminology:** «testplan/testplaner» everywhere in Manual Testing (Lagrede testplaner, Arkiver testplan, Testplanen er arkivert, Ny testplan med disse, …).

## Not changed

- Plan identity, cases and Azure DevOps links stay keyed by the plan id on the server.
- Discovery scope still follows module and specification. Test plans do not.
- No data was reset, migrated or deleted.

## Tests

- **bUnit** `ManualTestingTestplanerTabTests`:
  - three tabs, with Testplaner last;
  - the name format;
  - the visible primary button, and a disabled button that says why;
  - a clean create (AllowEmpty, no candidates, no DraftId, its own request id);
  - a second start gets a new request id;
  - cancel creates nothing;
  - a failed save keeps the draft, and the retry reuses the id;
  - a double click sends one request;
  - `?create=1`;
  - the transfer opens on file;
  - old export links.
- **Updated:** ManualTestingWorkspaceTests, ManualTestingPlanTests, LegacyPlanLinkRedirectTests.
- **Full frontend suite:** 9708/9708 passed before the settings tests were added. The new settings tests pass on their own.
- **Playwright** `AzureDevOpsTransferWorkflowTests`:
  - opens the transfer from the plan page and checks that it lands on the Testplaner tab;
  - closes and reopens it from «Lagrede testplaner».
