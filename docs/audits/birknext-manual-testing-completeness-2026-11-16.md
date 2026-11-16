# Manual Testing completeness — gap matrix (2026-11-16)

Base: `02a6c0bd`. Primary user: a manual tester, not a developer. Scope: the manual testing lifecycle from scenario selection to reporting, with
traceability. No new engine or module was added. Each fix extends the owner that already existed:

| Concern | Owner (unchanged) |
|---|---|
| Run results, evidence, defects | `ManualTestRunService` + `manual_test_runs` (finality enforced by the DB trigger) |
| Saved test plans | `TestPlanSuggestionService` + `test_plan_drafts` |
| Azure DevOps file export | `AdoTestPlanExporter` / `AdoTestPlanExportService` (offline files only; nothing is written to Azure DevOps) |
| Integration suites | `IntegrationSuiteService` (`SuiteKey` and fingerprint unchanged) |
| Execution shown in Test Coverage / Requirements Traceability / plan | `PreparedCaseTraceability` + `ManualRunRules.Summary` (one source) |

## Gap matrix

| # | Lifecycle area | Finding (before) | Fix | Evidence | Residual risk |
|---|---|---|---|---|---|
| 1 | Defects after execution | A defect's status could only change while the run was in progress. After completion the run and its defects were frozen, so a fixed defect stayed "Open" forever. | **Follow up** on a completed or stopped run: set the status (Open / Ready for retest / Closed), add a note and a work item, and link the retest run. Each change appends a `ManualDefectEvent` (who, when, from → to). The DB trigger was replaced with a narrow rule: a final run may change only by +1 revision, with state, outcome, tester, timestamps, steps and outcome unchanged, evidence only added, and the defect count unchanged. | Backend: follow-up / stale revision / in-progress refusal tests. A raw SQL rewrite of a final run's results is refused by the DB. bUnit: follow-up form and history. Browser L1–L5, L20–L22, M1. | Defects stay local observations: closing one never touches Azure DevOps. |
| 2 | Retesting | Nothing linked a passing retest to the defect it verified. | The follow-up offers completed later runs of the same case as "Verified by retest run". The history entry links to that run. | Browser L14–L21 (retest run passes, defect closed with a link). | Not enforced: a defect can be closed without a retest run. That is deliberate, because some fixes are verified elsewhere. |
| 3 | Evidence | Only external URLs and notes were possible, so screenshots had to live on a share. | **Attachments** are stored in the database (`manual_run_attachments`) and covered by the existing backups. Limits: an allow-list of types (png jpg gif webp pdf txt log csv json xml har), at most 10 MB each and 20 per run. The content must match its type (magic bytes for images and PDF; no NUL bytes for text). The file name is reduced to a bare name. Downloads are project-scoped with `nosniff`. Files can be added to a completed run and are marked "added after completion", without changing the results. During a run, attaching keeps unsaved step results. A file cannot be faked by a save request (`attachment-unknown`). | Backend: refused html, svg, fake png, binary txt, empty, oversize, forged reference; cross-project 404. Browser L6–L19: real PNG and log downloaded byte-identical, refusals shown, interrupted session, M2–M4 after an API restart. | No virus scanning: files are served as downloads with `nosniff` and are never rendered inline. Removing an attachment from a run's evidence keeps the stored bytes, which preserves the audit trail but uses space. |
| 4 | Saved test plans | A save overwrote the plan, so an earlier version could not be seen or recovered. | **Version history**: every create and save appends a row to `test_plan_draft_revisions` in the same transaction. Older versions can be viewed. **Restore** saves the chosen version as the *next* revision (nothing is deleted); it is refused over unsaved edits (`draft-changed`) and asks for confirmation first. | Backend: history, restore, stale and current refusals. bUnit: list, view, restore, unsaved block. Browser L31–L37 and M6–M7 after a restart. | Versions saved before this migration were never recorded and cannot be restored. History is shown only from the first save after the upgrade. |
| 5 | Azure DevOps export | The source specification, scenario and requirements did not travel into Azure DevOps, and update exports needed the same work-item IDs typed every time. | The **Description column** (on by default; can be switched off) says where each case came from: specification file, acceptance scenario, requirements with their specification, and the BirkNext case. The mapping report gains "Source specification", "Acceptance scenario" and "Requirements with their specification" (appended, so existing columns keep their positions). IDs confirmed in an update export are **remembered** per project and case and prefilled with a "Remembered" marker; a typed ID wins. | Backend: remembered-ID and origin tests. bUnit: prefill, marker, typed ID wins, reference toggle. Browser X12, X18a–b, X28a–c (the update file carries the IDs; after a reload they are prefilled). | Checked against an independent reader of the files only. It was **not imported into a live Azure DevOps** (not permitted); whether Description is mapped in the import wizard is the tester's choice. |
| 6 | Integration suite traceability | A suite generated while preparing a journey did not record the journey. Switching area on Integration Test Suites dropped `journey=` from the address, so after a reload a suite was generated without the journey context. | The generate request carries `PreparedForJourneyId`. The backend checks the journey exists and is not retired, then records `{journeyId, revision, name}` as metadata outside the fingerprint. A reused identical suite adds the journey without a new revision. Integration Test Suites shows "Prepared for", and Integration Testing shows a chip that warns when the journey has a newer revision. Area links keep the journey. | Backend: `SuiteJourneyReferenceTests`. bUnit: the generate request carries the journey and area links keep it. Browser itx P3a–P3d. | Suites generated before this change have no journey reference. |
| 7 | Consistency across views | One source already: Test Coverage, Requirements Traceability and the plan read `PreparedCaseTraceability`, and the run tables, home and results CSV read `ManualRunRules.Summary`. "Open defects" now means *not closed* (Open + Ready for retest) everywhere. | No duplicate counting was added. **Defect report CSV** (one row per defect, with status and last follow-up), built with the same spreadsheet-safe CSV rules as the results report. | Browser L23–L30: the defect list, history row, results CSV and defect CSV agree; Test Coverage and Requirements Traceability show identical execution text (latest completed run = the retest). | The "Open defects" column header is kept for compatibility; it counts *not closed*. |
| 8 | Workflow, navigation, validation | See the first three rows above. Also checked: a stale tab's follow-up is refused (409, nothing overwritten); attaching is refused on a stopped run or from a stale revision, and nothing is stored; reloads, Back, an API restart and project isolation were exercised. | — | Backend: stopped-run / stale-revision attach test. Browser L5, L17, M1–M12; existing mt a/b/c and link/ado journeys re-run. | Refusing an unknown or retired journey at suite generation is in code but has **no automated test**: no service-level fixture for `IntegrationSuiteService` exists. The backend does not check that the journey belongs to the caller's project; only the page does. Suite generation as a whole is not project-scoped yet (snapshots, contexts and suites), so this is fixed with that scoping in the journey audit. Remaining gaps are listed below. |

### Found during browser verification

- **Stale explanation on Integration Testing**: the prepare stage still said the journey "is not bound to the suite". It now says that a suite generated with
  the journey as context records the journey and its revision ("Prepared for").
- **Integration Test Suites snapshot list is not project-scoped**: it lists every imported snapshot and preselects the first, which can belong to another
  project (seen: `tps-empty.zip` preselected while preparing an M2LB journey). Not fixed here, because it belongs to the journey audit that follows.
  The acceptance script now picks the project's own snapshot explicitly.
- **Architecture guard**: `SourceIngestionOwnershipTests` lists every multipart endpoint. The attachment upload was added to it with its reason
  (allow-listed evidence types, no archives, never source).
- **Acceptance scripts** updated for the intended new export shape: the ADO acceptance pinned 9 columns, and the Description column makes 10.

## Verification (this revision, sequential)

Browser runs used Edge with a guarded network, disposable databases copied from `birknext_pts_acc_20261115`, and isolated ports 5097/5197.

**Run 2: final committed revision.**
- Frontend bUnit: 9179/9179.
- Backend partitions, failed counts all 0:

  | Partition | Passed / total |
  |---|---|
  | 1 | 1052/1054 |
  | 2 | 1101/1106 |
  | 3 | 888/888 |
  | 4 | 682/683 |
  | 5 | 541/544 |
  | 6 | 2606/2614 |

  The remainder in each partition is skipped tests.
- Release publish: 0 errors.
- Browser:
  - ADO (fresh DB after utps): 43/43.
  - utps: 106/106.
  - itx on the prepared DB: 46/46. The suite records "Prepared for … (revision 2)" and the Integration Testing chip shows it.
  - p2 report: 5/5 once a project is selected.

**Run 1: the same change before the late fixes.** The late fixes were the suite-journey wording, the ownership-guard allow-list and the acceptance scripts.
- Browser:

  | Suite | Result |
  |---|---|
  | lc l1 | 38/38 |
  | lc l2 (after an API restart) | 13/13 |
  | mt a | 23/23 |
  | mt b (after an API restart) | 48/48 |
  | mt c | 7/7 |
  | cx | 6/6 |
  | utps | 106/106 |
  | link | 12/12 |
  | link verify (after a restart) | 26/26 |
  | tt | 76/76 |
  | ttnav | 48/48 |
  | nav | 41/41 |
  | nav2 | 32/32 |
  | brand | 17/17 |

- Failures in this run:
  - Backend partition 1 had one failure: the ownership guard.
  - ADO X17 hard-coded 9 columns.
  - itx P3d picked another project's snapshot.
  - p2 R: see below.
  - All four are fixed and re-run in run 2.

**p2 R diagnosis.**
- p2 does not select a project.
- lc l2's last step selected a project and closed the browser about 3 s later, before the fire-and-forget workspace save finished, so the next session showed an older project.
- Selection does persist across sessions and API restarts once saved; checked with a probe.
- Awaiting that save and saying when it fails is fixed in the follow-up journey audit.

**Not re-run on the final revision:** lc, mt, link, tt, ttnav, nav, nav2 and brand. The only product change between the runs is the
explanatory text on Integration Testing's prepare stage, which is covered by the itx run above.

**Pre-existing, unchanged:** the SRI check reports one entry, the boot manifest's own `hash` field. nav logs a FrontendQualityEngineStatus console line on
`/frontend-quality-review` (known; R13).

## What remains before Manual Testing is production-ready

1. **Live Azure DevOps import** of the Description column and of update files with remembered IDs: verify once in a sandbox project.
2. **Prerequisites are not enforced at run start.** Case prerequisites ("blocked by", required environment) are shown but not pinned on the
   run, and starting is not refused when they are unmet.
3. **Test Data changes do not mark runs stale.** The case fingerprint covers steps and expected results, not the Test Data asset's content.
4. **Plan Refresh** regenerates generated text and can replace generated evidence on cases the tester did not edit. Version history makes this
   recoverable now, but nothing warns before it happens.
5. **Discover → case links.** Discover rows (requirements, suggestions) do not link to the prepared case that covers them.
6. **Internal identifiers** (`file#id`, fingerprints, Test Data asset IDs) are still visible in some details for non-technical testers.
7. **Removed cases.** When a case leaves the plan (an edit or a restore), its runs stay in run history and Test Coverage, but the plan no longer
   links to them. They are reachable only from history and the defect list.
8. **Attachment governance**: no malware scanning, no retention policy, and no delete for stored bytes (audit-first by design). Decide this
   before handling real personal data in screenshots.
9. **Accessibility** was checked by keyboard reachability and layout at 768–1440 px only. A screen-reader pass of the new follow-up and
   history panels is still to do.
