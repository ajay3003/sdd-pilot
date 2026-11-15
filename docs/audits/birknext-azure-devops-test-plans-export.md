# BirkNext — Azure DevOps Test Plans export (XLSX / CSV)

Date: 2026-11-15 · Branch: `008-traceability-first` · Starting HEAD: `0f3ca7dc` (Manual Testing workspace).

## 1. Existing export functionality audited

| Item | Found | Decision |
|---|---|---|
| "Prepare for Azure DevOps" (Test Plan Suggestions, area `export`) | `TestPlanSuggestionEngine.Export` → BirkNext JSON + copyable text of one **snapshot**; states "import compatibility is not verified" | Kept (renamed section "BirkNext JSON of a saved snapshot"); not an Azure Test Plans import format |
| CSV / XLSX generators | none in the solution; no spreadsheet library referenced | New single exporter (`AdoTestPlanExporter`), no new dependency |
| Download mechanism | `wwwroot/js/reportExport.js` `downloadBase64File` | Reused |
| Saved cases, revisions, steps, step expectations, preconditions, priority, requirement links, Test Data links, review state | `test_plan_drafts` via `TestPlanSuggestionService` | Export source (read-only) |
| Azure DevOps contracts | `TestCaseDraft.VerifiedAzureDevOpsTestCaseId` (never written by BirkNext) | Used only as a stored, verified id in update mode |

## 2. Architecture

- `shared/AdoTestPlanExportContracts.cs` — request, profile, preview, rows, file, history.
- `AdoTestPlanExporter` (pure) — the one rule: saved cases → validated `AdoExportRow`s → **the same rows** serialised as XLSX (`Xlsx`) or CSV (`Csv`), plus the mapping report. No field logic is duplicated between formats.
- `AdoTestPlanExportService` — reads the saved plan through `TestPlanSuggestionService` at the reviewed revision (newer save → 409), project-bound (404 across projects), never saves the plan; stores export settings per project and a history record per generated file.
- `api/test-plan-suggestions/ado-export` — `profile` (GET/PUT), `preview`, `file`, `report`, `history`.
- UI: `Components/AdoExportWizard.razor`, the only export interface, in **Manual Testing → Plan & prepare → Export to Azure DevOps** (the existing Test Plan Suggestions route `?area=export`; also linked per plan from the Manual Testing home).
- Persistence: migration `AddAdoExportProfilesAndRecords` (additive): `ado_export_profiles` (per project, JSON, no credentials) and `ado_export_records` (file metadata, case revisions; the file is not stored).

## 3. Microsoft import requirements (Microsoft Learn "Bulk import or export test cases", last updated 2026-08-10)

- CSV and XLSX import/export in Azure Test Plans; same page for Services, 2022 and Server (no 2020-specific content). Import: Test Plans → select test plan → select test suite → *Import test cases from CSV/XLSX*.
- Services required columns: ID, Work Item Type, Title, Test Step, Step Action, Step Expected, Area Path, Assigned To, State. Server: Area Path not required and not modified by import.
- "Each test step is a separate row. Repeat the ID, Title, and other fields on each row, and increment the Test Step number." Blank ID creates new test cases.
- "Reimporting a test case with a matching ID replaces all existing test steps with the steps in your file. Missing steps are removed."
- New test case State: a state in the Proposed category of the process (default Design). Area Path must exist; Assigned To must be a valid user.
- CSV: UTF-8; quote values with commas or line breaks. Not stated on the page: shared steps, parameters, attachments, links, size limits, whether header order is mandatory.

## 4–5. XLSX and CSV formats

- **XLSX**: Office Open XML package (`[Content_Types].xml`, workbook, one worksheet `Test Cases`, styles). Header row (bold, frozen), then one row per step; text as inline strings (never formulas — no `<f>` element is written), Test Step as a number, wrap for step cells, column widths; no merged cells, no blank or title rows, no hidden values, no extra sheets.
- **CSV**: RFC 4180, UTF-8 with BOM, every field quoted, quotes doubled, line breaks kept inside quoted fields, CRLF rows. Optional spreadsheet protection (default on): a text cell starting with `= + - @` or tab gets a leading apostrophe (Test Step never), with a warning that Azure DevOps keeps it as text; turning it off keeps text exactly. XLSX needs no prefixing.

## 6. Supported Azure DevOps versions

Azure DevOps Services and Azure DevOps Server 2022 or later, per the page. 2020 or earlier: not stated, not claimed.

## 7. Field mapping

| Column | Value |
|---|---|
| ID | empty (create); the user-confirmed Azure DevOps id (update) — never a BirkNext plan, case, scenario or requirement id |
| Work Item Type | `Test Case` |
| Title | saved title, Markdown emphasis/list markers and a repeated requirement id removed (`FR-001: - **FR-001**: …` → `FR-001: …`), ≤ 255 characters |
| Test Step | 1, 2, 3 … per case, in saved order |
| Step Action / Step Expected | the saved step and its own expected result (edits included); multi-line kept |
| Area Path / Assigned To / State | from the export settings (form-checked only) |
| Priority (optional column) | Critical/1 → 1, High/2 → 2, Medium/Normal/3 → 3, Low/4 → 4; anything else empty with a warning |

## 8. New cases versus updates

Create (default): blank ID. Update: an explicit mode; each case needs the id shown in Azure DevOps (typed by the user, or the stored verified id), digits only, unique, plus an explicit confirmation; a warning states that steps are replaced.

## 9–12. Preconditions, priority, requirements, Test Data

- Preconditions: not an import column → listed in the mapping report by default; optional explicit "first step" ("Check the preconditions: …" / "The preconditions are met").
- Priority: optional column with the mapping above; not in the required column list, so it must be mapped in the import wizard.
- Requirements: the file does not create Azure DevOps links (said in the wizard); the mapping report lists BirkNext requirement ids per case. No Azure DevOps requirement id is invented.
- Test Data: names and asset ids in the mapping report only; no values.
- Evidence, attachments and run results: not exported (stated).

## 13. Validation rules

Blocking: no case ready; a selected case without title, steps, step action or step expected result; title > 255; Area Path missing (Services) or malformed; State missing/invalid; Assigned To invalid; update mode without confirmation, a missing, non-numeric or duplicate id. Warning: duplicate titles, non-default state, unmapped priority, CSV apostrophe protection, update replaces steps. Info: requirements, preconditions, Test Data are in the mapping report. Field checks: *Valid*, *Confirm in Azure DevOps*, *Invalid*, *Missing*, *Not required* — remote values are never labelled verified.

## 14. Export profiles

Per BirkNext project: destination label, target version, Area Path, Assigned To, State, format, preconditions mode, priority column, CSV protection. No credentials or tokens; never shared across projects.

## 15. Security

No Azure DevOps connection or write; formula-safe XLSX; CSV protection; XML-invalid characters removed; control characters stripped; file names derived from the plan name (letters/digits only); project-bound API; export reads only.

## 16. Generated sample files

Produced in browser acceptance (`scratchpad/acc/downloads-adx/`, not committed): XLSX and CSV of the LendoPlus plan before and after an edit, and the mapping report.

## 17. Automated tests

New tests — backend `AdoTestPlanExporterTests` (10, pure; files read back with `Microsoft.VisualBasic.FileIO.TextFieldParser` and as a raw OOXML ZIP/XML package): one row per step with repeated fields and blank ID, CSV/XLSX parity with Norwegian text, quotes, commas and line breaks, formula safety (XLSX inline strings, CSV apostrophe on/off, Test Step never prefixed), blocking rules (title, steps, action, expected, 255 characters), selection/approval/excluded explanations, update mode (confirmed, invalid, duplicate, stored verified id; create mode never writes an id), destination form checks (Services/Server), title cleanup and duplicate warning, preconditions/priority/requirements/Test Data and the mapping report, no mutation of the plan. Backend `AdoTestPlanExportServiceTests` (3, real PostgreSQL): saved revision only, unchanged plan row after export, history with case revisions, stale revision 409, other project 404, blocked file 409 and not recorded, per-project settings without credentials. Frontend `AdoExportWizardTests` (8): Excel default, per-case states and reasons, "Confirm in Azure DevOps", blocking disables download with a link to the case, browser download of the server's file, re-check on changes and selection, update mode ids + confirmation, settings saved only with write permission, unsaved-edit warning, error/retry and labelled fields.

Changed expectation (with justification, not weakened): `SourceIngestionOwnershipTests` allow-list gains `AdoTestPlanExporter.cs` — "writes the Azure Test Plans XLSX import workbook (ZipArchiveMode.Create, in memory); never reads an archive" — the same category as the existing execution-evidence export entry.

| Suite (final code) | Before (`0f3ca7dc`) | After |
|---|---|---|
| Focused backend (TestPlanning, Migration) | — | 166 / 166 |
| Focused frontend (export wizard, TPS, Manual Testing, documentation, user guide) | — | 291 / 291 |
| Full frontend | 9,049 | **9,057 passed, 0 failed** (before the lost-click fix; afterwards the wizard and user-guide tests were re-run: wizard 9 / 9) |
| Backend partitions 1–6 | 6,804 passed, 19 skipped | **6,817 passed, 19 skipped, 0 failed** (1,052 / 1,099 / 888 / 676 / 539 / 2,563; partition 1 re-run after the allow-list entry) |
| API / Web Release publish (clean copy) | 0 errors | 0 errors; WebAssembly hashes: 86 assets verified |

## 18. Browser acceptance

Release build containing exactly this commit (the uncommitted navigation work was excluded from the build copy), web :5197, API :5097, fresh disposable database `birknext_adox_final3_20261115`, guarded network (5000/5173 and external hosts blocked). Downloaded files were read back by `check_export.py` (Python standard library `zipfile`, `xml.etree`, `csv` — independent of BirkNext) and the workbook was opened by Microsoft Excel (local, read-only, separate hidden instance).

| Run | Result |
|---|---|
| Existing acceptance baseline (7 project categories) | **106 / 106** |
| Export journey (X0–X33 + viewports): Manual Testing home → Export link → Plan & prepare export; Excel default; counts; every case says why it is or is not exported; destination fields "Confirm in Azure DevOps"; a case needing attention blocks download with a link; preview; real `.xlsx` download (valid package, one "Test Cases" sheet, documented headers in order, one row per step numbered 1…n, blank ID, Test Case type, no formulas, no merged cells); `.csv` download (UTF-8 BOM, same rows as XLSX except the documented apostrophe); mapping report (BirkNext identity, requirements, reasons); history; case edit with æ ø å, quotes, commas and a formula-like step → new revision in the file name and content, literal in XLSX, apostrophe in protected CSV, exact with protection off; update mode warning and block; missing Area Path blocks; Server needs no Area Path; saved export settings come back after reload; another project shows no LendoPlus cases or settings; the Test Plan Suggestions route opens the same workflow; no horizontal overflow at 1440 / 1280 / 1024 / 768 px; keyboard reachable | **38 / 38** |
| Excel read-back of the downloaded workbook | opened; sheet "Test Cases"; documented headers; 0 formulas; Test Step numeric; title "Låne bok – kontroll av æ, ø og å" intact |
| Browser page errors, console errors, HTTP 5xx, network-guard violations | 0 / 0 / 0 / 0 |

Found and fixed during acceptance: a click on *Save settings* or *Download* right after editing a field was lost (the field's change started a re-check that disabled the buttons before the click landed). Buttons now stay enabled during a re-check and the action waits for it; covered by a new component test. Earlier runs also exposed script-side issues only (console encoding of the independent reader, a reused database), which were corrected before this final run.

**Live Azure DevOps import: not run** (no authorised Azure DevOps test organisation). Verified: file generation, the documented structure, independent parsing and Excel opening.

## 19. Known limitations

- Live import into Azure DevOps was **not run** (no authorised Azure DevOps test organisation); conformance is to the documented structure only.
- Area Path, Assigned To and State cannot be verified offline.
- Requirement links, attachments, shared steps and parameters are not carried by the file.
- Update mode relies on ids the user types (BirkNext stores no Azure DevOps ids of its own).

## 20. Git

- Starting HEAD `0f3ca7dc`; this work is one local commit on top (see `git log`). Nothing pushed.
- Not committed (other work, untouched): `appsettings.Local.json`, the Kode67 gap register, older audit drafts, `AIAssisted/testdata/`, and the in-progress workspace-navigation changes (separate commit).
- Temporary databases (template copies, not dropped): `birknext_adox_acc_20261115`, `birknext_adox_final_20261115`, `birknext_adox_final2_20261115`, `birknext_adox_final3_20261115`.
