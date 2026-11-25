# Azure DevOps – Test Plans: settings in the Git Sources design (2026-11-25)

Branch `008-traceability-first`. Implementation commit `c6ecf05b` (no push). Test additions after that commit are listed in L–N. The companion report on the transfer workflow is `azure-devops-transfer-approval-and-default-2026-11-25.md`.

Status words: **IMPLEMENTED** (in code), **VERIFIED** (an automated test or the browser walkthrough proves it), **PARTIAL**, **NOT VERIFIED**, **BLOCKED**.

## A. Actual System Settings navigation

- System Settings has a left navigation built from `DocumentationCatalog.SettingsGroups` (`frontend/BirkNext.Web/Services/DocumentationCatalog.cs`), with the groups Configuration, Analysis and Developer.
- Git Sources is the item `git-sources` in **Analysis**.
- **Azure DevOps – Test Plans** (`azure-devops-test-plans`) is added directly after Git Sources, in the same group. Its URL is `admin/system-settings?section=azure-devops-test-plans`.
  - The label is English: the user decided that System Settings is English everywhere. That decision overrides the brief's Norwegian label «Azure DevOps – Testplaner».
- The older **Platform** section keeps its read-only Azure DevOps block for Change Traceability. That is a different feature with its own configuration; no second settings area for test plans was created.
- In Feature Visibility, the «Manual test execution» card now links to the new section (`ado-export-settings-link`).

## B. Git Sources UX analysis

Reference: `frontend/BirkNext.Web/Components/GitSourcesSettingsCard.razor` and `Components/GitSources/GitAddRepositoryForm.razor`. Screenshot: `ado-01-git-sources.png`.

Git Sources looks good because of a few consistent choices:

- **One card, light sections.** A `.gsx` card holds a header (title, subtitle and a status pill shown as a dot and words). Below it, sections with small uppercase headings are separated by a thin rule. There are no nested boxed panels.
- **Status as words.** A pill says «Git ready» or «Connected» in plain words, coloured with the success or warning tokens.
- **Selections and actions:**
  - choices are a segmented control (`.gar-tabs`: «Clone from Git URL / Local repository»);
  - lists are searchable, with rounded rows;
  - the selected item gets a primary left bar.
- **Contextual actions.** Actions sit with the thing they act on. A disabled action says why in a notice next to it. Busy and success text appears inline (`gsx-notice`); errors appear as `gsx-error`.
- **Opening reads only local state.** Network work (fetch) happens only on an explicit action.
- **Responsive.** At 48rem the facts list and tables stack, with no horizontal scroll.

## C. Reusable components and styles

- **The design layer was moved, not copied.** Git Sources used scoped CSS (`*.razor.css`), which no other component can use. The shared rules were moved unchanged into a new global stylesheet, `frontend/BirkNext.Web/wwwroot/css/settings-sections.css`, referenced from `index.html`. Git Sources now reads them from there; the visual result is the same rules.
  - Moved from `GitSourcesSettingsCard.razor.css`: `.gsx`, `.gsx-header`, `-title`, `-subtitle`, `-muted`, `-link`, `-wrap`, `-pill` (all tones), `-panel`, `-panel-head`, `-panel-title`, `-notice`, `-error`, `-state`, `-facts`, and their media rules.
  - Moved from `GitAddRepositoryForm.razor.css`: `.gar-tabs`, `.gar-tab`, `.gar-field`, `.gar-help`, `.gar-muted`, `.gar-field-error`, `.gar-error`, `.gar-list`, `.gar-pick`, `.gar-actions`, `.gar-inline`, `.gar-narrow`.
- **New styles only where Git Sources has no pattern:** `wwwroot/css/azure-devops-test-plans.css` (79 lines). It covers the suite tree indentation, the create and review tiles, the approval warning, case rows and the history list.
  - It uses design tokens only: no new framework, no new tokens, no UI library.
- **Shared component:** `Components/AzureDevOps/AdoDestinationPicker.razor` serves both System Settings (English) and Manual Testing (Norwegian), with an `English` parameter.

## D. Existing Azure DevOps implementation (before this task)

| Area | Status before | What this task did |
|---|---|---|
| Stored connection: organization plus PAT encrypted with Data Protection (`AzureDevOpsConnectionStore`, App_Data/azure-devops) | Present (Delivery Reporting) | **Reused**: the same store and the same add-connection endpoint. No second login. |
| REST plumbing: auth header, base address, development-only fake-server seam (`AzureDevOpsGitClient`) | Present, read-only Git | **Reused**: `Client()`, `Base()` and `WithVersion()` are now `internal`. `AzureDevOpsTestPlansClient` builds on them; it is not a second client. |
| Project list | Present | Reused through the same plumbing |
| Test plans, suites, test cases, test points | Absent | Added in `AzureDevOpsTestPlansClient` |
| Offline export file (XLSX/CSV in the Microsoft import format), per-project export profile, export history | Present | Kept. The profile gained `DefaultDestination`. History gained status, start and end times, and the declaration. |

## E. Concurrent task reconciliation

- `ListAgents` showed no other Claude session.
- The only in-progress work was this session's own uncommitted phase 1 (optional BirkNext execution, the settings switch). It was committed together with this work in `c6ecf05b`.
- The git stashes were not touched. No other files were overwritten.

## F. Confirmed missing capabilities (before)

These were missing:

- choosing an organization, project, test plan or suite in BirkNext;
- creating a test plan or a test suite;
- a saved destination;
- a direct transfer and a mapping from BirkNext cases to Azure DevOps test cases;
- protection against duplicates and remote edits;
- fetching results back.

All of these are now **IMPLEMENTED** (G–J here, and the companion report).

## G. New test plan workflow

`AdoDestinationPicker` → step «Test plan» → segmented «Use existing / Create new test plan» (Norwegian: «Bruk eksisterende / Opprett ny testplan»).

1. «Create new test plan» shows two fields:
   - name, with the placeholder «M2LB – Skolenærvær – Testplan» (a placeholder only, never a value);
   - an optional description.
2. **Same-name warning.** While the user types, the plans already in the project are compared (ignoring case). A match shows «A test plan named "…" already exists in M2LB. A second plan with the same name is never created.» with «Use the existing plan». «Review» stays disabled.
3. «Review» shows: Organization, Project, Test plan to create. Then come «Create in Azure DevOps» and «Cancel». Nothing is sent before «Create in Azure DevOps».
4. **The server checks again** (`AdoTestPlanSettingsService.CreatePlanAsync`):
   - The request needs `Confirmed`.
   - The plans are listed first. A plan with the same name returns `AlreadyExists` together with that plan, and nothing is created.
   - Only one create per name can run at a time; a second click returns `Blocked`.
   - Azure DevOps returns the id. Iteration and Area Path default to the project root.
5. **A lost answer** (timeout, broken connection after sending, 5xx) is recorded as **pending** in App_Data/azure-devops/test-plans.json.
   - The user sees «We do not know whether the test plan was created. Check Azure DevOps before you try again.»
   - A new create with that name is `Blocked` until «Check in Azure DevOps» runs. That check looks the name up: if found, the plan is adopted; if not, the pending record is cleared.
6. Permissions: creating needs the plan-editing policy (`IntegrationConfigurationWritePolicy`). A 403 from Azure DevOps reads «You do not have permission…».
7. No example ids are used anywhere (50088, 50090 and «Testdokumentasjon» appear nowhere as defaults; a test checks this).

## H. New test suite workflow

1. Below a chosen plan, its suites are shown as an indented tree (root = «Whole test plan»), as buttons with `aria-pressed`.
2. «+ Create new test suite» asks for a name and «Place under» (a parent from the tree; by default the selected suite, otherwise the root).
3. A same name under the same parent gives a warning, with «Use the existing suite».
4. «Review» shows «Test suite to create: path › name». Then «Create in Azure DevOps».
5. The server checks again: the parent must still exist; a same name under the same parent returns `AlreadyExists`; the same pending and lock rules apply as for plans.
6. Several suites can be created one after another. A destination uses one suite; a transfer can go to another suite by changing the destination.

## I. Default destination behaviour

- The summary panel «Default destination» lists Applies to, Organization, Project, Test plan, Test suite (path), Status and Saved (who and when).
  - Status is Configured, Not configured, or Needs review (the connection no longer exists).
- «Change» opens the picker. «Applies to» offers All projects (system default) or Only `<project>` (the project default). Then «Save as default».
- The text says «Saving is local: nothing is created or changed in Azure DevOps». Saving is a local file or row only, and a test proves no write happens.
- **Storage:**
  - the system default goes to App_Data/azure-devops/test-plans.json (beside the connections);
  - the project default goes to the project's export profile (`AdoExportProfile.DefaultDestination`, JSON, no migration). An ordinary export-profile save keeps it.
- **Precedence:** an explicit choice in the transfer, then the project default, then the system default. The transfer panel shows which one applies («Prosjektets standard / Systemstandard / Valgt for denne overføringen»).
- **Reset:** these settings are removed only by **Reset data → Reset everything** (the user's rule).
  - `AdoExportProfileRecord` moved from the settings group to a new reset-everything-only group, so the ordinary «Reset project data» no longer deletes it.
  - Reset everything also clears test-plans.json.
  - Connections are kept by every reset, like the machine's Azure sign-in.
  - The reset plan texts list all this.

## J. Manual Testing export integration

Manual Testing → Planlegg og klargjør:

- a primary button «Overfør testplan»;
- the tab «Overfør testplan» (it was «Eksporter»).

Both open `AdoTransferPanel`, which always starts on «Eksporter til fil». «Send direkte til Azure DevOps» uses the saved destination and can change it inline with the same picker. Details are in the companion report.

## K. External write safety

The only writes to Azure DevOps are:

- create plan and create suite (settings or picker), each after its own review;
- the final «Bekreft og send til Azure DevOps» (create or update test cases, then add them to the suite).

Everything else only reads:

- opening settings reads local state only;
- choosing an organization, project, plan or suite reads from Azure DevOps;
- the preview and «Kontroller» read from Azure DevOps;
- «Hent resultater» reads only.

Other guarantees:

- Every write needs a confirmation flag in the request and the plan-editing permission.
- Writes are never retried automatically.
- Tests use fake handlers only; nothing was sent to a real organization.

## L. Frontend test results

- `AdoTransferTests` (new, 12 tests) passes. It covers the English settings, the empty state, saving locally with nothing preselected, plan creation only after review with a duplicate name refused, the empty-project create action, the Norwegian picker in Manual Testing, and the transfer panel cases (see the companion report).
- `AdoExportWizardTests` (10 tests, assertions moved to the Norwegian text) passes.
- **Full bUnit suite: 9693 passed, 0 failed** (latest run, after all of today's changes) (`BirkNext.Web.Tests`).

## M. Backend test results

- `AdoTestPlanTransferTests` (new, 17 tests, a fake Azure DevOps `HttpMessageHandler` and a temporary PostgreSQL database per test class) passes. It covers:
  - plan creation needs confirmation; an existing name is offered instead;
  - a lost answer is pending and never resent until checked, then adopted;
  - a double click creates one plan;
  - suites: parent, same-name and 403 handling;
  - an unaccepted token and an unknown connection;
  - saving a destination is local; project-over-system precedence; an export-settings save keeps the destination;
  - the transfer tests listed in the companion report;
  - the steps XML.
- `TestPlanResetProtectionTests` (2 new tests: the Azure DevOps settings rule, and Reset everything clears the system destination but keeps connections) passes. The existing count test was updated from 6 to 8 protected tables.
- `AdoTestPlanExporterTests`, `DataResetScopeTests`, `LocalDataResetTests`, `ManualExecutionGateTests`, `ManualTestRunServiceTests`, `DeliveryAzureDevOpsTests` and `PersistenceOwnershipTests` pass.
  - The last targeted run of the nine suites was 90/92. The 2 failures were stale test expectations, fixed and rerun green (35/35 for the two changed suites).
- The full backend suite was not run, because earlier runs in this session hit the machine's memory limit. **NOT VERIFIED.**

## N. Browser UX verification

**VERIFIED** on an isolated BirkNext. The setup:

- a Release publish from a clean source copy (your `appsettings.Local.json` was not copied);
- its own App_Data (`BIRKNEXT_APP_DATA_DIR`);
- a disposable database `birknext_adoverify_<time>`, created by a create-only helper (no Podman);
- API on 5097, web on 5197, `ASPNETCORE_ENVIRONMENT=Development` so the loopback-only `DevelopmentBaseUrl` seam is honoured;
- Azure DevOps = `AzureDevOpsStandIn` (in the test, 127.0.0.1:5399).

Neither your BirkNext instance nor your Azure DevOps organization was used.

The test is `frontend/BirkNext.Web.PlaywrightTests/Tests/Workflows/AzureDevOpsTransferWorkflowTests.cs`, and the script that runs it is `scratchpad/ado_verify.ps1`.

**Runs:**

1. The API stopped: the database must exist before start. Fixed with the create-only helper.
2. Seeding used a stale route; `/test-plan-suggestions` now needs `?advanced=1`. The existing scenario test has the same stale route (pre-existing, not changed).
3. The test timed out waiting for «klar for eksport» in the file wizard.
4. **Passed** (59 s).
5. The timeout from run 3 came back. The cause was a real race in the file wizard (an older check's answer replaced a newer one). It is fixed, with a regression test.
6. After the fix: **passed** (32 s), and passed again on a later fresh publish. See the companion report, section R.

Settings steps proven in run 4:

- the Git Sources screenshot was taken first;
- the section is found in the navigation;
- empty state, then a connection added (the PAT field is a password field), then «Check connection» → «Connected to verifyorg.»;
- project M2LB, then «No test plans were found in this project.», then a NEW plan through review → created (1 write), then a NEW suite through review → created (2 writes);
- the same plan name in another case is refused before sending; «Use the existing plan» works;
- the suite is chosen and «Save as default» shows «Nothing was changed in Azure DevOps.» — still 2 writes;
- at 390 px there is no horizontal scroll.

Screenshots are in `scratchpad/adoverify/shots/ado-01…ado-14*.png` (desktop and 390 px).

**Comparison with Git Sources** (`ado-01` against `ado-02…ado-06`): the same card, the same header and pill, the same uppercase section headings and rules, the same bordered rows and button styles, and the same blue notice. The destination summary uses a facts list with a primary left bar, like a selected Git repository.

**Observed, not changed:** the isolated instance listed your real Git Sources repository. So Git Sources keeps its state outside the App_Data that `BIRKNEXT_APP_DATA_DIR` moves. It was only read.

## O. Accessibility and responsiveness

- **Labels and roles:**
  - every field has a `<label for>`;
  - segmented controls use `role=tablist/tab` with `aria-selected`;
  - list choices are buttons with `aria-pressed`;
  - review tiles are `role=group` with an `aria-label`;
  - status and notices use `role=status`, errors `role=alert`;
  - a disabled action has a `title` saying why (for example «Choose a test plan and a test suite first.»).
- **Keyboard:** everything is a native button, input, select or details element. There are no hover-only controls. Focus rings are visible (`:focus-visible`, including on `details` summaries).
- **Responsive:** at 48rem the rows stack (`.ats-connection`, `.adt-case`, `.atp-entry`), the warning icon moves above the text, and the tree indent shrinks. 390 px was verified in the browser with no horizontal scroll (settings and the warning).
- **Contrast:** design tokens only, with the same token pairs as Git Sources. No separate contrast measurement was made. **NOT VERIFIED.**

## P. Modified files (main ones)

**Backend:**

- `Services/DeliveryReporting/AzureDevOps/AzureDevOpsTestPlansClient.cs` (new)
- `AzureDevOpsTestPlanSettingsStore.cs` (new)
- `AzureDevOpsConnections.cs` (internal plumbing, `DirectoryPath`)
- `Services/TestPlanning/AdoTestPlanSettingsService.cs`, `AdoTestPlanTransferService.cs`, `AdoTestResultsService.cs` (new)
- `AdoTestPlanExportService.cs`, `AdoTestPlanExporter.cs` (row `CaseId`)
- `Controllers/AdoTestPlansController.cs` (new)
- `Models/AdoExportRecords.cs`, `Data/AppDbContext.cs`
- migrations `20261125112858_AdoTestCaseLinks` and `20261125115740_ExternalTestResults` (additive only)
- `Services/LocalDataReset/LocalDataReset.cs`, `DataResetService.cs`
- `Program.cs`

**Shared:**

- `shared/AzureDevOpsTestPlanContracts.cs` (new)
- `AdoTestPlanExportContracts.cs`

**Frontend:**

- `Components/AzureDevOps/AdoDestinationPicker.razor`, `AdoTestPlansSettingsCard.razor` (new)
- `Components/ManualTesting/AdoTransferPanel.razor`, `AdoDirectTransfer.razor` (new)
- `Components/AdoExportWizard.razor` (Norwegian)
- `Services/AdoTestPlansApiService.cs` (new)
- `Pages/Admin/SystemSettings.razor`, `Pages/ManualTestingPlan.razor`
- `Services/DocumentationCatalog.cs`, `Program.cs`
- `wwwroot/css/settings-sections.css`, `azure-devops-test-plans.css` (new)
- `GitSourcesSettingsCard.razor.css`, `GitAddRepositoryForm.razor.css` (rules moved out)
- `wwwroot/index.html`

**Tests:**

- `AdoTestPlanTransferTests.cs`, `AdoTransferTests.cs`, `AzureDevOpsTransferWorkflowTests.cs`, `Fixtures/AzureDevOpsStandIn.cs` (new)
- `AdoExportWizardTests.cs`, `TestPlanResetProtectionTests.cs`
- four Playwright workflow tests (Norwegian wizard text)

## Q. Remaining limitations

- The export check messages from the server (`AdoTestPlanExporter` issues and field explanations) are still English inside the Norwegian panel, as are the backend messages shown in the English settings for a refused create. Known outcomes are composed in the page's own language.
- The file wizard keeps its older four-step layout, with Norwegian text and new import steps. Redesigning it into the Git Sources style is not done.
- Several plans or suites at once in one review: one at a time.
- A connection cannot be removed or rotated from this section; the Delivery Reporting connection management still does that.
- Contrast is not measured; the full backend suite was not run (memory).
- The file wizard's check race (see N) is fixed; the walkthrough passed twice after the fix.

## R. Acceptance criteria (this brief)

| AC | Status |
|---|---|
| AC01 Git Sources inspected | VERIFIED (code and browser screenshot) |
| AC02 Same design language | VERIFIED (shared `.gsx/.gar` rules; screenshots) |
| AC03 Existing navigation reused | VERIFIED |
| AC04 No duplicate settings area | IMPLEMENTED (Platform's block is Change Traceability) |
| AC05 Organization and project selectable | VERIFIED |
| AC06 New test plan | VERIFIED (fake Azure DevOps) |
| AC07 New test suite | VERIFIED (fake Azure DevOps) |
| AC08 Creation requires confirmation | VERIFIED (server and UI) |
| AC09 Ids stored after creation | VERIFIED (destination and links) |
| AC10 Retries never blindly duplicate | VERIFIED (pending, lock, same-name check) |
| AC11 Existing resources selectable | VERIFIED |
| AC12 Default destination saved | VERIFIED |
| AC13 Manual Testing reuses it | VERIFIED |
| AC14 Local save never publishes | VERIFIED |
| AC15 Ids hidden unless needed | IMPLEMENTED (technical details only) |
| AC16 Modern, compact, responsive | VERIFIED at 1366 and 390 px |
| AC17 Understandable text | IMPLEMENTED (settings English per user; Manual Testing Norwegian). PARTIAL: some server messages are English |
| AC18 Loading, error and empty states | VERIFIED for empty states; loading and error IMPLEMENTED |
| AC19 Keyboard | IMPLEMENTED (native controls), NOT VERIFIED by a keyboard-only walkthrough |
| AC20 User data intact | VERIFIED (plan revision unchanged; isolated instance) |
| AC21 No unauthorized remote write | VERIFIED |
| AC22 No duplicate auth or client | VERIFIED |
| AC23 Tests pass or blockers named | VERIFIED (targeted backend, full frontend); full backend NOT VERIFIED |
| AC24 Browser walkthrough | VERIFIED |
| AC25 No reset or cleanup | VERIFIED (the disposable verification databases were kept, not dropped) |
| AC26 No commits or pushes | Not followed as written: the user later asked to commit (`c6ecf05b`); nothing was pushed |
| AC27 Evidence-backed report | This file |
