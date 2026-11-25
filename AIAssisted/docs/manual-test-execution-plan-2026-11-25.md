# Manual test execution: BirkNext and Azure DevOps — implementation plan (2026-11-25)

**Status:** plan only. Nothing in this document is implemented yet.

**Decision (product owner, 2026-11-25).** Both execution options become part of BirkNext's development:

1. **Testgjennomføring i BirkNext.** The tester opens a case, follows its steps and records Bestått, Feilet or Blokkert, with comments and documentation. History is kept.
2. **Testgjennomføring i Azure DevOps.** A case is linked to Azure DevOps Test Plans, and BirkNext fetches the results back.

**Choice and defaults**
- The user chooses where a test is executed.
- **Option 1 is optional and off by default.** Azure DevOps Test Plans stays the main place for execution for some time.

**What is shared and what is kept apart**
- Preparation is shared: the same plans, cases, revisions and readiness.
- Results are kept apart, each labelled with where it was executed.
- **A case is never «Bestått» because of a result from an earlier version.**

**Order**
- **Phase 1:** execution in BirkNext, with safe storage and history.
- **Phase 2:** connection to Azure DevOps.

---

## 1. Principles (apply to both phases)

1. **One preparation.**
   - Both options read the same test plan, case, revision, readiness, test data and «Før du starter» fields.
   - No second editor and no copy of a case.
2. **A result belongs to one version of one case.**
   - Every result records:
     - the plan id, the case id and the plan revision;
     - the case fingerprint at execution (`ManualCaseIdentity`);
     - where it was executed;
     - who executed it and when.
3. **Current versus historical.**
   - A result is **current** only when its case fingerprint equals the case's fingerprint now, and the case's source state is current.
   - Otherwise it is shown as «Fra en tidligere versjon» and never counts as passed for the current version.
4. **Results stay separate.**
   - BirkNext runs and Azure DevOps results are stored and shown apart, each with its source («Gjennomført i BirkNext» / «Gjennomført i Azure DevOps»).
   - They are never merged into one outcome without showing both.
5. **Never from design or code.**
   - A prepared, approved or exported case is not an executed test.
   - A linked automated test is not an executed manual test.
6. **Append-only history.**
   - A rerun is a new run; a completed run is never edited.
   - Corrections are new events.
   - Abandoning keeps the run with its reason.
7. **No secrets in results or logs.** Evidence follows the existing evidence policy, with bounded size.

## 2. What exists and can be reused

| Asset | Where | State | Reuse |
|---|---|---|---|
| Run contracts: `ManualTestRun`, `ManualRunStep`, `ManualStepOutcome` (Pass/Fail/Blocked/NotRun), `ManualRunOutcome`, `ManualRunEvidence` (link/note/attachment), `ManualRunDefect` with events, `ManualRunOrigin`, `ManualRunRules`, requests and pages | `shared/ManualTestRunContracts.cs` (243 lines) | **Present**, compiled, used only by origin tests | As is (phase 1) |
| Tables `manual_test_runs`, `manual_run_attachments` (revision concurrency token, one-in-progress unique index per case) | `Models/ManualTestRunRecords.cs`, `AppDbContext.cs`, migrations 20261115–16 | **Dormant, kept** (`birknext-manual-execution-removal-2026-11-19.md`) | As is; no migration needed for phase 1 |
| `ManualCaseIdentity` (case fingerprint and origin) | `Services/TestPlanning/ManualCaseIdentity.cs` | **Present**, used by the Azure DevOps export | Fingerprint for currency (both phases) |
| Retired service `ManualTestRunService` (463 lines), `ManualTestRunsController` (114), `ManualTestRunsApiService` (123), `ManualTestingExecute.razor` (855), `EvidenceFilePolicy` | Git: parent of `d1d174ea` | **Removed** 2026-11-19, recoverable | Restore and adapt (phase 1) |
| Retired tests: `ManualTestRunServiceTests` (483), `ManualRunPersistencePostgresTests` (325), `ManualRunHistoryPagingTests` (86), `ManualRunReportTests` (59), the attachment tests | Git: parent of `d1d174ea` | Removed | Restore as the regression base |
| Readiness (`TestDesignAdvisor.Readiness`, the checklist, «Før du starter», hindrance → «Blokkert») | Shared + Planlegg | **Present**, browser-verified | Gate and warning before «Start test» |
| Azure DevOps export (XLSX/CSV, `AdoExportProfile.KnownTestCaseIds`, export history), `TestCaseDraft.VerifiedAzureDevOpsTestCaseId` | `AdoTestPlanExporter`, `AdoTestPlanExportService`, contracts | **Present** | The link between a case and its Azure DevOps Test Case (phase 2) |
| Azure DevOps connection settings and HTTP clients (`AzureDevOpsConnections`, connection tester, read-only providers) | `Services/DeliveryReporting/AzureDevOps`, `ImplementationTraceability` | **Present** (delivery and traceability) | Connection, credentials and test-connection for result import (phase 2) |
| Case history, plan revisions | `EditHistory`/`ReviewHistory` shown in Planlegg, `PlanVersionsPanel` | **Present** | A run reference is shown in the same history |

**Not reused:** nothing from Technical Testing's runs (integration/E2E). Those are technical runs with their own owner.

## 3. Choosing where a test is executed

- **System setting** `ManualExecution:BirkNextEnabled`, **default `false`**.
  - Off: Manual Testing looks as it does today: execution happens in Azure DevOps and nothing changes for users.
- **Per project, when enabled:** «Hvor gjennomføres manuelle tester?»
  - **Azure DevOps** (default), **BirkNext**, or **Begge** (the tester chooses per run).
  - Stored with the project's existing test settings; no new module.
- **The UI follows the choice:**
  - «Start test» in Planlegg appears only when BirkNext execution is enabled for the project.
  - «Hent resultater fra Azure DevOps» appears only when Azure DevOps is chosen and a connection exists.

## 4. Phase 1 — execution in BirkNext

### 4.1 Backend

1. **Restore `ManualTestRunService` and `ManualTestRunsController`** from `d1d174ea^` and adapt them to today's model:
   - Snapshot the case's **effective content** when the run starts: title, preconditions, steps, expected results per step, test data, «Før du starter» fields and Testtype. A run therefore shows what was executed, even after the case changes.
   - Record `CaseFingerprint` (`ManualCaseIdentity`), plan revision, environment and executor.
   - **Gate:** refuse to start when `ManualExecution:BirkNextEnabled` is false, or the project's choice excludes BirkNext (403/409 with a clear message).
   - **Readiness:** starting is allowed when the case is not «Klar til kjøring», but the run records the readiness at start. A recorded hindrance shows as a warning before starting.
   - **One in-progress run per case:** the existing unique index; `Start` resumes it.
   - **Step outcomes:** Pass/Fail/Blocked/NotRun plus an actual result per step. The run outcome is derived by `ManualRunRules` (any Fail → Feilet; any Blocked → Blokkert; all Pass → Bestått).
   - **Save, complete and abandon** use revision checks (409 on stale) and request-id idempotency (the same pattern as grouping saves), so a retry never creates a second run.
   - **Evidence:** links and notes; attachments through the existing `EvidenceFilePolicy` (type allow-list, size limit, SHA-256, no executable content), stored in `manual_run_attachments`. Withdrawal keeps the record.
   - **Defects:** a reference (external id or link) and a status (Open, Closed, ReadyForRetest) with events. No defect tracker is built.
2. **Write permission:** the existing `IntegrationConfigurationWritePolicy`, the same as for plan writes. Reading is open to the project's readers.
3. **Currency:** `ManualRunRules.IsCurrent(run, case)` compares fingerprints. It is computed on read and never stored as "passed".
4. **No migration.** The tables exist with the columns the contracts use. If a field is missing (for example a run snapshot of «Før du starter»), add it to the run's JSON content, not as a column.

### 4.2 Frontend (inside the existing workspace)

- **Planlegg (case editor)**, when enabled:
  - «Start test», with a readiness warning;
  - «Testhistorikk» for the case: runs newest first, outcome, who, when, current or «fra en tidligere versjon», and source «BirkNext».
- **Run view** (`/manual-testing/run/{runId}`, restored and simplified from `ManualTestingExecute.razor`):
  - one step at a time or as a list;
  - Bestått / Feilet / Blokkert per step, plus «Faktisk resultat»;
  - «Legg til dokumentasjon»;
  - «Registrer feil»;
  - «Fullfør» or «Avbryt med begrunnelse».
  - Norwegian labels; the expected result is always shown beside the actual result.
- **Landing page:** «Gjennomført / bestått» shows real counts per source when BirkNext execution is enabled. It counts current results only, with the denominator, and shows earlier-version results separately. With execution off, it keeps «Registreres i Azure DevOps».
- **Finn og forstå:** a scenario shows «Siste kjøring» only from current results of its cases; never from automated tests.

### 4.3 Tests (phase 1)

- **Restored and adapted:** `ManualTestRunServiceTests`, `ManualRunPersistencePostgresTests` (temporary database), paging and report tests, attachment policy tests.
- **New:**
  - start refused when disabled;
  - the run snapshot keeps the executed text after the case changes;
  - a rerun after a case change makes the earlier result historical;
  - retry or idempotent complete;
  - stale revision 409;
  - one in-progress run;
  - outcome derivation;
  - the landing counts separate current from earlier-version results;
  - a hindrance warning is shown at start;
  - Azure DevOps-only projects show no «Start test».
- **Browser:** the full journey on a disposable plan (start → steps → evidence → Feilet → rerun → Bestått → history), with the scoped cleanup pattern already used in this task.

## 5. Phase 2 — Azure DevOps

### 5.1 Linking a case to its Azure DevOps Test Case

- **The link** is `VerifiedAzureDevOpsTestCaseId` on the case and `KnownTestCaseIds` in the export profile:
  - set by the tester, or confirmed after import with the Azure DevOps id from the export round trip;
  - never guessed from a title.
- **A case that is not linked** shows «Ikke koblet til Azure DevOps».

### 5.2 Fetching results

- **Read-only.** BirkNext never writes to Azure DevOps.
- **Two ways:**
  1. **File import (offline):** results exported from Azure DevOps Test Plans (CSV/XLSX), parsed into results keyed by Test Case id.
  2. **API (optional):** the existing Azure DevOps connection, using test points and results for a plan/suite, with a read-only PAT kept in the existing credential handling.
- **Each imported result records:**
  - the Azure DevOps ids (plan, suite, point, test case, run, result);
  - outcome, tester, completed date, configuration and comment;
  - the import time;
  - **the BirkNext case fingerprint at its last export.** A result is current only when that export matches the case's current fingerprint. An Azure DevOps result for a case changed since its export is «fra en tidligere versjon».
- **Storage:** a separate, additive table `external_test_results` (source = AzureDevOps; immutable rows; unique per Azure DevOps result id, so a re-import never duplicates). This keeps imported results apart from BirkNext runs; `manual_test_runs` keeps in-progress semantics and attachments of its own.
- **Outcome mapping:**
  - Passed → Bestått, Failed → Feilet, Blocked → Blokkert;
  - NotApplicable, Paused and others are shown as recorded, never mapped to Bestått.

### 5.3 Display

- **Testhistorikk:** runs from both sources in one list, each labelled «Gjennomført i BirkNext» or «Gjennomført i Azure DevOps», with its own link (run view, or the Azure DevOps result URL).
- **No combined outcome:** when the two sources disagree for the current version, both are shown.
- **Counts:** separate rows per source.

### 5.4 Tests (phase 2)

- Parser tests on synthetic exports.
- API client tests with a fake HTTP handler (no network in tests).
- Mapping by verified id only.
- Currency through the export fingerprint.
- Re-import idempotency.
- Separation from BirkNext runs.
- Unknown outcomes are never «Bestått».
- Permissions (import needs write access).

## 6. Acceptance criteria

| # | Criterion |
|---|---|
| E01 | With BirkNext execution off, nothing changes for users (Azure DevOps stays the documented place) |
| E02 | When enabled, a tester can start, execute step by step, record outcome, actual result, evidence and a defect reference, and complete |
| E03 | Runs are append-only; a rerun never overwrites history |
| E04 | A run keeps a snapshot of what was executed |
| E05 | A result is current only for the case's current fingerprint; older ones are labelled and never count as passed |
| E06 | BirkNext and Azure DevOps results are stored and shown apart, with their source |
| E07 | Retries never duplicate a run or an imported result; stale saves are refused |
| E08 | One preparation: no second editor, no copied cases |
| E09 | Azure DevOps results are mapped only through a verified Test Case id |
| E10 | BirkNext never writes to Azure DevOps |
| E11 | Counts separate design, readiness and execution, per source, with denominators |
| E12 | Evidence follows the existing file policy; no secrets in logs |

## 7. Risks and open questions

- **Azure DevOps result export formats** differ between Test Plans views. The file import starts with the format the team actually exports; a sample is needed before implementation.
- **The PAT scope** for reading test results (Test Management, read) must be approved by whoever owns the Azure DevOps organisation.
- **Attachments** use local storage under the existing policy. Larger evidence (video) stays a link.
- **Restoring retired code** brings back code from 2026-11-19. It must be reviewed against everything changed since (Testtype, readiness, «Før du starter», technical coverage), not restored blindly.

## 8. Rough size

- **Phase 1:** restore and adapt about 1,500 lines of service, controller, client and page, plus about 950 lines of tests; new UI parts in Planlegg and the landing page; one disposable-data browser journey.
- **Phase 2:** a parser and an optional API client; one additive table; history and count display; tests with fake HTTP.
