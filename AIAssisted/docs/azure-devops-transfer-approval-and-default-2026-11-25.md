# Overfør testplan: file by default, direct transfer with test lead approval (2026-11-25)

Branch `008-traceability-first`. Implementation commit `c6ecf05b` (no push). The settings side (connection, new test plan and suite, saved destination, Git Sources design) is in `azure-devops-manual-testing-git-source-ux-2026-11-25.md`. This report also records phase 1 (optional execution in BirkNext) and phase 2 (results fetched from Azure DevOps) of `manual-test-execution-plan-2026-11-25.md`.

Status words: **ALREADY IMPLEMENTED**, **IMPLEMENTED**, **VERIFIED**, **PARTIAL**, **BLOCKED**, **NOT VERIFIED**.

## A. Existing transfer functionality (audit)

| Capability | Before this task |
|---|---|
| Offline import file in Microsoft's «Bulk import or export test cases» layout (XLSX/CSV, one row per step, blank ID = new), mapping report, per-project export settings, export history | ALREADY IMPLEMENTED (`AdoTestPlanExporter`, `AdoTestPlanExportService`, `AdoExportWizard`) |
| Update mode with ids the tester confirmed (`KnownTestCaseIds`) | ALREADY IMPLEMENTED |
| Any write to Azure DevOps | None |
| Mapping a case to an Azure DevOps test case with its revision | Only confirmed ids, with no revision |

## B. Existing approval workflow (audit)

- BirkNext has **per-case review approval**: `TestCaseReviewState.Accepted`, «Godkjent», a reviewer's decision on each case. The export already respects it: unapproved cases are left out unless «Ta med testtilfeller som ikke er godkjent» is ticked.
- BirkNext has **no plan-level test lead approval**: no approver identity, no approval of a plan revision, nothing to verify against.
- Therefore direct transfer requires an explicit **declaration** (J). It is enforced on the server, recorded with the initiating user, and never turned into an approval.

## C. Reconciled in-progress changes

- No other session was running.
- This session's own phase 1 (optional BirkNext execution and its switch) was uncommitted. It was finished and committed together with this work in `c6ecf05b`.
- Stashes were not touched.

## D. Actual System Settings navigation

System Settings → Analysis → **Azure DevOps – Test Plans** (after Git Sources), `?section=azure-devops-test-plans`. Details in the companion report, section A.

## E. File export as the default

- **Entry points.** Planlegg og klargjør has the primary button **«Overfør testplan»** in the plan header and the tab «Overfør testplan» (it was «Eksporter»). Both open `Components/ManualTesting/AdoTransferPanel.razor`.
- **The default is a field initialiser.** The method is the component field `_direct = false`; it is never stored and never read from history. Every time the panel opens, «Eksporter til fil» is selected.
  - This is VERIFIED in bUnit: reopening after choosing direct, and with a successful direct transfer in the history.
  - It is VERIFIED in the browser: another tab and back.
- **File mode shows:**
  - «Last ned testplanen som en fil. Du kan kontrollere innholdet før du importerer den i Azure DevOps.»
  - **«Ingenting blir endret i Azure DevOps.»**
  - the reminder «Testplanen bør være godkjent av testleder før den tas i bruk.» (no checkbox);
  - the existing wizard, now in Norwegian.
- **No automatic download.** The panel does nothing until «Last ned Excel (.xlsx)» or «Last ned CSV» is chosen.
- **No connection needed.** The file needs no Azure DevOps connection; the export service has no Azure DevOps client at all. VERIFIED in the backend test (file export → 0 calls to the fake) and in the browser (download → write count unchanged).

## F. File format compatibility

- The file uses the columns Microsoft documents for **Test Plans → Import test cases from CSV/XLSX**:
  - ID, Work Item Type, Title, Test Step, Step Action, Step Expected, Area Path, Assigned To, State;
  - optionally Priority and Description;
  - one row per step;
  - a blank ID creates a new test case.
- This was ALREADY IMPLEMENTED and is proven by `AdoTestPlanExporterTests` (header and rows read back by independent readers).
- The file is **imported into an existing test suite**. It does not create a test plan or a suite. The panel says so («filen oppretter ikke testplanen eller testgruppen») and the steps say where to create them (in Azure DevOps or in System Settings).
- Import steps are in Norwegian («Slik importerer du filen»).
- **NOT VERIFIED:** an actual import into a real Azure DevOps organization (not attempted, by design).

## G. Direct transfer

`Components/ManualTesting/AdoDirectTransfer.razor` runs four stages. Nothing is written before the last.

1. **Approval warning** (H–J). The declaration starts unchecked. «Fortsett» stays disabled until it is checked; checking it sends and reads nothing.
2. **Destination and cases.**
   - The saved destination is prefilled, with its source shown («Prosjektets standard», «Systemstandard» or «Valgt for denne overføringen»). «Endre» opens the same picker as the settings, where a new plan or suite can be created after its own review.
   - Every ready case shows «Opprett nytt», «Oppdater eksisterende» or «Hopp over» (whichever apply), its link («Koblet til testtilfelle 4711 · endret i BirkNext») and notes.
   - Possible duplicates and «Må kontrolleres» are shown in their own groups; cases that are not ready are under «Ikke med».
   - This stage only reads.
3. **Final review** («Se over før du sender»):
   - organization, project, test plan, test suite;
   - selected cases, Opprettes, Oppdateres, Hoppes over, «Må kontrolleres først (sendes ikke)»;
   - «Hvilke endringer blir gjort?», listing each actual operation;
   - content warnings;
   - the repeated declaration;
   - **«Bekreft og send til Azure DevOps»**, «Tilbake», «Avbryt».
4. **Result.** The headline is «Overføring fullført», «Delvis overført», «Må kontrolleres» or «Kunne ikke overføre». Successes and «Må følges opp» are listed separately, per case, with links, plus «Åpne testgruppen i Azure DevOps».

**The server** (`AdoTestPlanTransferService`) works like this:

- It rebuilds the preview from the **saved plan at the reviewed revision**; a newer save is refused.
- It uses the same rows as the file export, so steps, expected results, language, priority and the source Description are the same.
- It writes each case through the stored connection:
  - `Microsoft.VSTS.TCM.Steps` as Azure DevOps step XML;
  - Title, AreaPath, AssignedTo, Priority, Description;
  - the tag `BirkNext`.
- It adds the cases that are not already in the suite with one request.
- It never creates a test plan or a suite. Those exist only through their own reviewed create.

## H. Why test lead approval is required (UI text)

Heading **GODKJENNING FRA TESTLEDER KREVES** with a large warning icon on a calm amber card (not a red screen):

> Du er i ferd med å sende testplanen «…» fra BirkNext til Azure DevOps.
> **Hvorfor er godkjenning nødvendig?** Testplanen må være gjennomgått før andre testere tar den i bruk. Overføringen kan opprette nye testtilfeller eller endre testtilfeller som allerede finnes i Azure DevOps. Hvis planen har feil eller mangler, kan andre teste med feil steg, forventede resultater eller testdata. Derfor skal testleder godkjenne overføringen før du fortsetter.

## I. Consequences (progressive disclosure)

**«Hva kan skje når du overfører?»** (collapsible, 8 points):

1. new test cases;
2. existing ones updated if chosen;
3. other testers *with access to the project in Azure DevOps* can see them;
4. the wrong destination;
5. duplicates without the right link (BirkNext remembers the link and suggests updating);
6. unfinished tests used by others;
7. conflicts with edits made in Azure DevOps (never overwritten; shown as «Må kontrolleres»);
8. a partial transfer (the user sees exactly which cases).

Then «Hva som faktisk gjøres for hvert testtilfelle, ser du før du sender.»

**«Hva skjer ikke?»**:

- the BirkNext plan is not deleted or changed;
- saving a case never sends it;
- the transfer runs no tests and does not mean they passed;
- nothing is sent before «Bekreft og send til Azure DevOps».

Each statement is backed by the implementation and its tests (plan revision unchanged; no write before the final step).

## J. Verified approval vs. declaration

- **Checkbox:** «Jeg bekrefter at testleder har godkjent denne overføringen.» It starts unchecked. It is reset on cancel, on «Ferdig», and after each transfer. It is never stored as checked.
- **Note under it:** «Dette er din bekreftelse. BirkNext lagrer den med navnet ditt, men kan ikke selv kontrollere at testleder har godkjent.»
- **Server:** `TransferAsync` refuses with 400 `lead-approval-required` without `LeadApprovalDeclared`, and with 400 `confirmation-required` without `Confirmed`. A direct API call cannot bypass this.
- **Recorded** in the transfer history: `declaration = { declared, text, by: <initiating user>, verifiedApproval: false }`. No approver identity is invented, and the plan is not marked approved.
- **Per-case approval** (B) still applies: cases that are not approved are left out unless explicitly included.

## K. Preview and confirmation workflow

These are not writes:

- opening the panel;
- choosing direct;
- checking the box;
- reading the preview;
- changing choices.

The preview requests carry `Confirmed=false`, which bUnit verifies. Only «Bekreft og send til Azure DevOps» sends `Confirmed=true` and `LeadApprovalDeclared=true`, once. During sending the button is disabled («Sender …»); a double click sends once (bUnit). On the server, one transfer per project and destination runs at a time (409 `transfer-running`; backend test).

## L. Duplicate and conflict protection

- **Mapping table** `ado_test_case_links` (additive migration). Each link holds:
  - project, case key `{plan}/{case}`, organization, Azure DevOps project;
  - the test case id, the remote `rev`, the case text revision (fingerprint) at transfer;
  - the plan revision, plan id and suite id;
  - the state, who and when.
- Unique per case and destination project. It is kept with the test plans by every reset.
- **Suggestions:**

  | Case state | Suggested | Note |
  |---|---|---|
  | Not linked | Opprett nytt | |
  | Not linked, but its title is already in the suite | Hopp over | «Mulig duplikat…», shown separately |
  | Linked and changed in BirkNext | Oppdater eksisterende | |
  | Linked and unchanged | Hopp over | «Opprett nytt» is always an explicit choice |

- **Remote edits.** Before an update, the work item is read:
  - deleted, or not a Test Case in the project → «Må kontrolleres»;
  - its `rev` differs from the one BirkNext transferred → «Må kontrolleres», not overwritten.
  The PATCH itself carries JSON Patch `test /rev` (409/412 → not overwritten).
- **Uncertain create.** The link is stored as `Uncertain` *before* the create is sent. A lost answer leaves it uncertain:
  - the case is never sent again until «Kontroller» runs;
  - «Kontroller» finds the test case by the BirkNext tag + exact title + created since the attempt (read-only WIQL), or by a test case id the user gives;
  - 1 match → adopted (an update is suggested); 0 → cleared (can be sent again); several → listed for the user.
- **Certain failure.** A refused create leaves no link.
- **Suite add failure.** «Ikke lagt i testgruppen»: the link is kept, and the next transfer adds the case without creating it again.
- **Status.** The result and history say «Overført» **only** when every requested operation succeeded. Otherwise «Delvis overført», «Må kontrolleres» or «Kunne ikke overføre».

## M. Transfer history

- **Storage.** The existing history (`ado_export_records`) is reused for both methods. A direct transfer records:
  - the initiating user;
  - start and completion times;
  - plan id and revision, method, destination;
  - the selected cases, the per-case outcome and test case id;
  - counts, status, the declaration (`verifiedApproval:false`), and partial failures.
- **A refused transfer** (destination missing, nothing to send, plan saved again) is recorded as «Kunne ikke overføre» with its reason.
- **«Tidligere overføringer»** in the panel shows:
  - status: «Overført», «Delvis overført», «Kunne ikke overføre», «Må kontrolleres» or «Fil laget»;
  - date «25.11.2026 kl. 13:40» and the start–end time;
  - destination, counts, who, and «bekreftet testleders godkjenning (egen erklæring)»;
  - for a file: «en fil er ikke importert før du importerer den i Azure DevOps».
- A transfer is never shown as a test run or a pass. Results from Azure DevOps are a separate section (phase 2).

## N. Azure DevOps destination management

See the companion report, sections G–I:

- new or existing plan and suite, each create reviewed;
- the same name never created twice;
- the system or project default (local);
- removed only by Reset data → Reset everything;
- no example ids.

## O. Git Sources UX reuse

See the companion report, sections B–C (shared `.gsx/.gar` stylesheet moved out of Git Sources; same card, pills, segmented controls, pick lists, notices).

## P. Frontend tests

`AdoTransferTests` (bUnit) passes 12/12. The transfer-panel tests, keyed to the brief's numbers:

- T01–T04: always opens on file, also after a direct transfer.
- T05–T07: file mode has no checkbox and no remote call, and states what the file is.
- T08–T15: warning text, 8 consequences, «Hva skjer ikke?»; the declaration starts unchecked; «Fortsett» is disabled; the box alone reads nothing; cancel writes nothing and renews the declaration.
- T16–T21: the review shows the destination and counts; preview requests carry no confirmation; one transfer with confirmation, declaration and the reviewed destination; «Delvis overført»; «ikke at testene er kjørt eller bestått»; problems listed separately.
- T22: a double click sends once.
- T23: history labels, date and time, declaration note, file ≠ import.
- No write permission: nothing to send.

The other new tests (settings and picker) are in the companion report.

Full suite: **9693/0** (latest run).

## Q. Backend tests

`AdoTestPlanTransferTests` (fake Azure DevOps, temporary PostgreSQL) passes 17/17. The transfer tests, keyed to the brief's numbers:

- 27, 31: no confirmation → 400; no declaration → 400.
- 32, 46: a file export makes no Azure DevOps call; the preview writes nothing.
- 35, 36: a running transfer blocks a second; plan creation is locked to one.
- 37, 38: a lost create is uncertain, never resent, then reconciled and adopted.
- 39: a remote edit → «Må kontrolleres», no PATCH.
- 40, 41, 42: partial results per case; history «Transferred», «Partial», «NeedsReview», «Failed» (including a refusal before sending).
- 43: plan revision unchanged.
- 44: `verifiedApproval:false`; no «passed».
- 45: the token is absent from logs, results, settings, history and the base64 header form.
- Phase 2: results are read-only and mapped by verified link only (a same-title test case is not mapped); stored once; current only for the text that was sent; unknown outcomes are never «Bestått»; kept apart from BirkNext runs.
- No link → nothing fetched, with a Norwegian reason.

**28 and 29 (missing identity or authorization).** Writes use the existing `IntegrationConfigurationWritePolicy` on every write endpoint. In Local Tester Mode the policy follows the existing local-session rules (the initiating user is «Local user (this PC)» when there is no sign-in). No separate test was added for the policy itself; it is the existing, already-tested authorization. **PARTIAL.**

## R. Browser verification

The setup is isolated, as in the companion report (section N): a fresh Release publish, its own App_Data and database, ports 5097 and 5197, and a loopback Azure DevOps stand-in. The test is `AzureDevOpsTransferWorkflowTests`.

**Run 4 passed** and proved these steps after the settings part (each with a screenshot in `scratchpad/adoverify/shots`):

- «Overfør testplan» opens on file; «Ingenting blir endret i Azure DevOps.»
- An XLSX is downloaded; the write count is unchanged.
- Direct shows the warning (desktop and 390 px, no horizontal scroll); the box is unchecked and «Fortsett» disabled.
- Cancel returns to file with no write; reopening starts on file again.
- Declaration, then «Fortsett»: the destination is prefilled («Systemstandard»), 2 cases.
- The review shows Opprettes 2; no write before the button.
- «Bekreft og send…» gives «Overføring fullført», 2 test cases in the new suite.
- A second transfer:
  - the declaration is unchecked again;
  - both cases are linked and nothing is suggested (review disabled);
  - one «Oppdater eksisterende» and one «Opprett nytt», with the fake refusing the create → «Delvis overført» (1 updated, 1 failed); no extra test case.
- The history shows «Delvis overført», «Overført», «Fil laget» with «kl.».
- «Hent resultater fra Azure DevOps» gives «2 nye resultater», «1 bestått · 1 feilet»; no write.
- The plan revision is unchanged; no page errors, no API 5xx.

**Run history:**

- Runs 1–2 were setup problems: the database must exist before the API starts, and the route was stale.
- Run 3 timed out in the file wizard («klar for eksport» never appeared). Run 4 passed, but a stability rerun failed the same way. Its screenshot showed the cause, a **real, pre-existing defect** in the file wizard:
  - ticking «Ta med … ikke godkjent» and then entering the Area Path started two checks;
  - the older answer arrived last and replaced the newer one;
  - so the page showed «Area Path mangler» with the path filled in, and the download stayed blocked.
- **Fix:** each check is numbered, and only the newest answer is shown (`AdoExportWizard.CheckAsync`).
- **Regression test:** `An_older_check_that_answers_last_never_replaces_the_newer_one` sends the answers in reverse order. It fails without the fix and passes with it.
- **After the fix**, a fresh publish ran the walkthrough again: **passed** (32 s).
- The run after that never reached its tests: its test build compiled while the plan page was mid-edit in the same working tree (a compile error fixed minutes later). That is not a walkthrough failure.
- A later fresh publish (including the US + AC badge and «Kildegrunnlag» changes) **passed again**. That makes two clean passes after the fix.

## S. Modified files

See the companion report, section P.

## T. Remaining limitations

- **Approval:** a declaration only. BirkNext has no plan-level test lead approval to verify (B).
- **Language:** server messages from the export checks are English in the Norwegian panel.
- **File wizard:** it keeps its older four-step layout.
- **Phase 2:** importing results from an Azure DevOps file is not implemented (API fetch only). Results are fetched from the suites the plan was sent to, plus ids confirmed in an update export (default destination only). The last result per test point is stored at each fetch, so history builds up from repeated fetches.
- **Uncertain create:** reconciliation by tag + title + time is a heuristic. Several matches are listed, never guessed.
- **Not verified:** a real Azure DevOps organization (intended); the full backend suite (memory); keyboard-only and contrast checks.
- **Walkthrough evidence:** one pass before the wizard race fix and two after (separate fresh publishes).
- **Pre-existing, not changed:** `ScenarioCentricManualTestingWorkflowTests` opens `/test-plan-suggestions` without `?advanced=1`, so it would be redirected (only its wizard text assertions were updated here).

## U. Acceptance criteria (this brief)

| AC | Status |
|---|---|
| AC01 Default is «Eksporter til fil» every session | VERIFIED (bUnit and browser) |
| AC02 Direct requires explicit selection | VERIFIED |
| AC03 File without Azure DevOps login | VERIFIED |
| AC04 File makes no remote write | VERIFIED |
| AC05 Prominent warning | VERIFIED |
| AC06 Explains why | VERIFIED |
| AC07 Concrete consequences | VERIFIED |
| AC08 Actual intended operations | VERIFIED (review lists each operation) |
| AC09 What does not happen | VERIFIED |
| AC10 Acknowledgement starts unchecked | VERIFIED |
| AC11 Required for direct | VERIFIED (UI and server) |
| AC12 Backend enforces confirmation | VERIFIED |
| AC13 Approval vs declaration kept apart | VERIFIED (`verifiedApproval:false`, UI note) |
| AC14 Destination reviewed before transfer | VERIFIED |
| AC15 New or existing plan and suite | VERIFIED |
| AC16 No example ids | VERIFIED |
| AC17 Create/update/skip visible | VERIFIED |
| AC18 Duplicates prevented, uncertain reconciled | VERIFIED (backend); browser covers duplicate-suggestion and update |
| AC19 Remote revision conflicts | VERIFIED (backend) |
| AC20 Partial transfers accurate | VERIFIED (backend and browser) |
| AC21 Audit history | VERIFIED |
| AC22 Local plans and cases unchanged | VERIFIED |
| AC23 No false Passed or Ready | VERIFIED |
| AC24 Git Sources quality | VERIFIED (screenshots); the file wizard is PARTIAL (older layout) |
| AC25 Clear Norwegian | IMPLEMENTED; PARTIAL (English server issue messages) |
| AC26 Tests done or blockers named | VERIFIED (targeted backend, full frontend); full backend NOT VERIFIED |
| AC27 Browser verification safe and recorded | VERIFIED |
| AC28 No unauthorized external writes | VERIFIED |
| AC29 No destructive database operations | VERIFIED (additive migrations; disposable verification databases kept) |
| AC30 No commits or pushes | Commit made at the user's later request (`c6ecf05b`); no push |
| AC31 Evidence-backed report | This file |

## Phase 1 — optional execution in BirkNext

- **Switch:** System Settings → Feature Visibility → «Manual test execution» → «Execute manual tests in BirkNext» (`ManualExecution:BirkNextEnabled`, off by default). It applies at once.
- **While off:** every recording endpoint answers 409 `manual-execution-off` (`ManualExecutionGateTests`); recorded runs stay readable.
- **The run page** (`/manual-testing/run`) is now Norwegian throughout, including step and outcome labels. Its runs are labelled «Gjennomført i BirkNext»; a run of an earlier case version is «Fra en tidligere versjon».
- **Tests:** backend run, persistence and gate tests pass; frontend 9693/0.
- **NOT VERIFIED:** browser verification of the run page with execution on. Turning it on would change your local settings, and the isolated walkthrough covered Azure DevOps only.

## Phase 2 — results fetched from Azure DevOps

- **What it reads:** «Hent resultater fra Azure DevOps» (in «Overfør testplan») reads the last result of each test point in the suites the plan was sent to. It is read-only and needs the plan-editing permission because it stores results locally.
- **Storage:** results go to the new table `external_test_results` (additive migration; unique per run and result; kept with the plans by every reset). They are mapped only through verified links.
- **Display:**
  - Per case, under «Testgjennomføring», results appear separately from BirkNext runs as «Gjennomført i Azure DevOps», with outcome, time, tester and configuration, plus a link.
  - A summary counts only the **current version** («Gjeldende versjon: 1 bestått · 1 feilet …»). Results for an earlier text are «Fra en tidligere versjon» and not counted.
  - Outcomes other than Passed are shown as recorded («Ikke relevant», «Satt på pause» …), never as «Bestått».
- **Tests:** backend tests pass; VERIFIED in the browser (run 4).
