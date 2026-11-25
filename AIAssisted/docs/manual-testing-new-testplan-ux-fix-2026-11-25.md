# Manual Testing — new test plan, default name and a simpler transfer (2026-11-25)

## 1. «+ Ny testplan» creates a clean test plan

**Root cause:**
- There was no real «Ny testplan» path. A test plan could only be made from a scenario selection (with a date-only name), and the server refused an empty one ("no-scenarios").
- The visible «Lag ny plan» was a faint `btn-secondary`.

**Fix:**
- **Testplaner tab:** «+ Ny testplan» is `btn-primary`. Without write access it is disabled and says «Du har ikke tilgang til å opprette testplaner.»
- **The form:**
  - The name is prefilled and editable.
  - An optional «Ta med de N valgte scenarioene» is unchecked by default.
  - The buttons are «Opprett testplan» («Oppretter …» while busy) and «Avbryt».
  - On success: «Testplan opprettet: «name».» with «Åpne i Planlegg og klargjør».
- **Clean draft:**
  - Every start makes a new `NewPlanDraft`: a new suggested name and a new `CreationRequestId` (Guid "N").
  - Nothing is carried over from another test plan: no cases, revision, approvals or Azure DevOps ids.
- **Server** (`TestPlanSuggestionService.CreateProjectAsync`):
  - `CreateProjectTestPlanRequest.AllowEmpty` allows a test plan without cases; its name is required (400 «Gi testplanen et navn.»).
  - `CreationRequestId` is stored on the test plan (`TestPlanDraft.CreationRequestId`). The same request again returns the test plan already created (`CreatedFromAsync`).
  - A static in-flight guard answers 409 «Testplanen opprettes allerede. Vent litt.» to a simultaneous duplicate.
- **Duplicates and cancel:**
  - The button is disabled while creating.
  - A failed save keeps the draft (same name and request id), so a retry is deduplicated.
  - Cancel sends nothing.

## 2. Default name «Testplan yyyy-MM-dd HH-mm-ss»

- `ManualTestingPresentation.SuggestedPlanName(DateTime local)` gives the name in local time, taken when the create starts. It has no colons, so it also works as a file name.
- It is used by «+ Ny testplan» and by «Ny testplan med disse» on the plan page, which is the same convention in every create entry.
- Two test plans created in the same second keep distinct ids, because the id is a Guid and never derived from the name.

## 3. A simpler Azure DevOps transfer

**System Settings owns the configuration, in English:**
- Azure DevOps – Test Plans already holds the connection and the default destination (organization, project, test plan, test suite).
- **New: «Field defaults»** for the selected project:
  - Azure DevOps version, Area Path, Assigned To, State, Preconditions, File format, Priority and Source references.
  - Saved with `IAdoExportApiService.SaveProfileAsync`. Remembered Azure DevOps ids are kept.
  - Saving changes nothing in Azure DevOps.

**Manual Testing (Norwegian) keeps only the essentials:**
- **«Eksporter til fil»** (the default every time):
  - A short intro: «Ingenting blir endret i Azure DevOps.»
  - Choosing the test cases.
  - A one-line field summary with a link to «Standardverdier i Systeminnstillinger».
  - **«Endre felter for denne eksporten»** is closed by default. It opens on its own when a required field is missing or invalid, when update mode is on, or when values were changed for this export. Changes apply to this export only; the «Lagre som prosjektets eksportinnstillinger» button is gone.
  - **Kontroll:** a compact «Klar for eksport.» or «N ting må rettes». Blocking problems are always visible. The field table, warnings and notes are in **«Kontroller før overføring»** (closed).
  - Download, with **«Vis importveiledning»** (closed) holding the format note, the import steps and the boundary text.
  - No connection, project, test plan or test suite is shown in file mode.
- **«Send direkte til Azure DevOps»:**
  - The short warning «Godkjenning fra testleder kreves» and the declaration checkbox are always visible.
  - The reasons are in **«Hvorfor kreves godkjenning?»** (closed).
  - «Endre» is now **«Endre mål i Azure DevOps»**.
  - «Hvilke endringer blir gjort?» is closed by default; the counts stay visible.
  - The server still enforces the declaration and the final confirmation.

## Tests

- **Backend** `NewTestPlanCreationTests` (PostgreSQL, a disposable database per class), 7/7:
  - an empty test plan;
  - a name is required;
  - scenarios are still required without AllowEmpty;
  - same name in the same second gives two test plans;
  - nothing inherited;
  - a sequential retry is deduplicated;
  - four concurrent requests create one test plan.
- **bUnit:**
  - `ManualTestingTestplanerTabTests`;
  - `AdoExportWizardTests`: field ownership, the compact check, closed sections;
  - `AdoTransferTests`: the warning stays visible and the reasons are collapsed;
  - `AdoFieldDefaultsSettingsTests`: edit and save, no project, no write access.
- **Playwright** `AzureDevOpsTransferWorkflowTests` / `ManualTestingReloadTests`: the Area Path comes from System Settings → Field defaults, and the sections are checked closed. The other workflow tests now expect «Klar for eksport».
