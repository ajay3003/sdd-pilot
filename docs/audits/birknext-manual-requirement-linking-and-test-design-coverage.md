# BirkNext — Manual requirement linking and test-design coverage

Date: 2026-11-15 · Branch: `008-traceability-first` · Builds on `1acb8b58` (Test Plan Suggestions), `a1e4e977` (regression stabilization), `69d9528f` (test-design traceability), `96485cff` (navigation progress).

## 1. Executive summary

A manual tester can now link a saved test case to one or more requirements of the current project's specification from Test Plan Suggestions, save the links with the case, and see the same links in Test Coverage Review and Requirements Traceability. Both views read the links through the one existing traceability rule (`PreparedCaseTraceability`), so they show identical requirement rows, origins, states and gaps.

- Links are validated on the server against the project's own requirement catalog: an unknown requirement, a requirement of another specification file and another project's catalog are refused.
- Automatic links (the case's own discovery evidence) and manual links (a tester's confirmation) are stored and shown separately. Removing a manual link never removes automatic evidence; automatic evidence is never converted to a manual confirmation.
- A link whose requirement later leaves the specification is kept, flagged "no longer in the reviewed specification", and not counted.
- Test design (prepared / approved) is shown apart from execution. Every case and every requirement row says "Not executed". No execution or pass is inferred.
- No database schema change: links are part of the saved case JSON. Plans without links serialize exactly as before (snapshot identity unchanged).

Phase 0: the pending navigation-progress feature was committed together with its test fixes as `96485cff` (10 files, only those). The permission check allowed it this time.

## 2. Implemented functionality

| Area | What it does |
|---|---|
| Test Plan Suggestions — "Linked requirements" (per open project case) | Lists linked requirements with id, text, origin (Automatically identified / Manually confirmed / both), validity (In the current specification / Not found — removed or renamed / Not checked) and who linked it when. Search by id or words; Link and Remove link buttons; "Not saved yet" per new link; page-level "Unsaved changes"; explains an empty specification or no selected specification; no free-text requirements. Read-only users see links without controls. |
| Save (`POST api/test-plan-suggestions/{id}` existing endpoint) | Accepts `TestCaseHumanPart.RequirementLinks` (null = unchanged, older clients) and `SaveTestPlanDraftRequest.RequirementCatalog` (project key + requirement options). New links must be in the catalog of the plan's own project. Server records title (from the catalog), LinkedBy, LinkedAt. Edit history gains "Linked requirements". Max 50 links per case. |
| Traceability rule (`PreparedCaseTraceability.Build`) | Adds manual links as `ManualConfirmation` entries with `Validity`; counts them under the same conditions as evidence links (active case, reviewed specification file, requirement in the specification). Per requirement: title, automatic/manual case counts, Execution = "Not executed". View: requirement population of the reviewed specification, requirements without test design, stale manual links. |
| Test Coverage Review (Requirements tab) | Shared panel shows requirement title, test-design state, how linked, execution, cases; "Requirements without test design" list; a plain-language explanation of the numbers. Now receives requirement titles. |
| Requirements Traceability (Overview) | Now sends the analysed specification's requirement ids and texts, so stale links and gaps are evaluated exactly as in Test Coverage. |
| Snapshot comparison / export | "LinkedRequirements" change category in delivery comparison; `linkedRequirements` (origin "Manually confirmed") in the export JSON when present. |

## 3. User workflow

1. Select the project (Project Import → Use). Open Test Plan Suggestions → Prepare test cases, open a case.
2. In "Linked requirements", type an id (FR-002) or words ("member card"); choose **Link** (mouse or keyboard Enter). The row shows "Manually confirmed" and "Not saved yet"; the page shows "Unsaved changes".
3. **Save draft**. The row now shows "Linked by {tester}, {date}". Reload or restart: the links are still there.
4. Test Coverage Review → Requirements: the requirement shows "Prepared, not approved yet" (or "Approved test case" once the case is accepted), "Manually confirmed (1)", "Not executed", and a link back to the case.
5. Requirements Traceability → Overview: the same rows. Requirements with no case are listed under "Requirements without test design".
6. To remove a link: **Remove link**, save. An automatic link to the same requirement stays.

## 4. Domain and persistence decisions

- Distinctions (unchanged vocabulary, made explicit):

| Term | Meaning in BirkNext |
|---|---|
| Requirement | An id (FR-nnn …) in the selected specification, parsed in the browser by `SpecExplorerService`; identity = id + specification file. |
| Business rule | A System Understanding guide rule (MT-7); never a specification requirement. |
| Scenario | Acceptance scenario of the specification, or a guide scenario. |
| Suggested test case | A discovery candidate (not saved). |
| Manually created case | `ScenarioSourceKind.Manual` saved case. |
| Prepared case | A saved case not (yet) accepted. |
| Approved case | Review state Accepted ("accepted for preparation"). |
| Executed case | Not recorded by Test Plan Suggestions; never inferred. |
| Evidence-backed association | The case's own discovery evidence names the requirement in its specification file (automatic). |
| Manually confirmed association | A tester linked the case to the requirement (manual). |

- Case identity unchanged: plan id + case id (`{draft:N}/{caseId}`).
- `TestCaseDraft.RequirementLinks` is stored in the existing `ContentJson` as `requirementLinks`, omitted when empty, so existing plans and snapshot fingerprints are unchanged (tested). No migration.
- Why the catalog travels with the save: specification requirements are parsed in the browser (the same `CoverageWorkspaceEvidence` Test Coverage and discovery already trust); the server has no specification parser. The server still enforces project binding (catalog project = plan project), id + file existence in the catalog, and records the title from the catalog, never from the client's link.
- Held links are re-saved as recorded even when the current catalog no longer has them (the tester removes them explicitly); traceability flags them.

## 5. Coverage calculation rules (test-design coverage)

1. A requirement counts once per metric, however many cases cover it; one case may cover many requirements.
2. A case link counts only when: the case is active (not Excluded / Superseded), the link names the reviewed specification file, and the requirement is in that specification.
3. Automatic and manual links of one case to one requirement are both shown; the case counts once.
4. Unlinked cases are listed ("Not linked (not counted)") and never increase coverage.
5. Approved design = at least one counted approved case; otherwise "Prepared, not approved yet".
6. Execution: always "Not executed" for prepared/approved cases; executed and passed tests remain the review's own imported-execution evidence and are never attributed to cases.
7. Denominator = the reviewed specification's requirements only (shown as "Requirements in the specification"); without it no gap count is claimed.
8. Guide business rules (MT-7) and journey-declared ids are shown, never counted; MT-7 calculations are untouched. Critical E2E flows stay the flow's evidence.

## 6. Traceability integration

Both modules call `PreparedCaseTraceability.Build` with the same inputs (project key, specification path, requirement ids and texts): Test Coverage via `TestCoverageReviewService.PreparedCasesAsync`, Requirements Traceability via `POST api/test-plan-suggestions/project/traceability`. Browser acceptance verified the two render identical requirement rows (K10/K11).

## 7. Changed files

Backend: `shared/TestPlanSuggestionContracts.cs` (ManualRequirementLink, ProjectRequirementOption/Catalog, link validity, requirement trace fields), `Services/TestPlanning/PreparedCaseTraceability.cs`, `Services/TestPlanning/TestPlanSuggestionService.cs` (validation, persistence, history), `Services/TestPlanning/TestPlanSuggestionEngine.cs` (comparison, export), `Services/TestCoverage/TestCoverageReviewService.cs` (titles), `Controllers/TestPlanSuggestionsController.cs` (titles).
Frontend: `Services/RequirementLinkPresentation.cs` (new), `Services/TestPlanSuggestionsPresentation.cs`, `Pages/TestPlanSuggestions.razor` (+ `.css`), `Components/PreparedCaseTracePanel.razor` (+ `.css`), `Pages/ArtifactTraceability.razor`.
Tests: backend `PreparedCaseTraceabilityTests.cs` (+4), `TestPlanSuggestionServiceTests.cs` (+6, real PostgreSQL); frontend `RequirementLinkingTests.cs` (new, 10), `RequirementsTraceabilityWorkspaceTests.cs` (+2 assertions).

## 8. Test results (final code)

| Suite | Before (baseline) | After |
|---|---|---|
| Focused backend (TestPlanning, TestCoverage, RequirementCoverage, CriticalE2E) | — | 339 / 339 passed |
| Focused frontend (linking, TPS, Test Coverage, Traceability, MT-7, Critical E2E) | — | 205 / 205 passed |
| Full frontend | 9,030 passed | 9,040 passed, 0 failed (rerun after the final CSS fix) |
| Backend partitions 1–6 | 6,787 passed, 19 skipped | 6,797 passed, 19 skipped, 0 failed (1,052 / 1,099 / 888 / 676 / 539 / 2,543) |
| API / Web Release publish (clean copy) | 0 errors | 0 errors; WebAssembly hashes: 86 assets verified |

The navigation tests are now committed (`96485cff`); the full frontend result no longer depends on uncommitted work.

## 9. Browser acceptance

Fresh Release build (web :5197, API :5097), disposable database `birknext_reqlink_final_20261115` (template copy), guarded network (only the two test ports; 5000/5173 and external hosts blocked).

| Run | Result |
|---|---|
| Existing Test Plan Suggestions + integration acceptance (7 project categories: M2LB, Skolenærvær guide plan, Lendo/Blazor, Parcelo/API-only, Roomly/docs-only, LendoPlus/mixed, Opaque/insufficient evidence; viewports 1440–768) | **106 / 106 passed** |
| Linking phase (L1–L10): honest empty state, FR-999 offers nothing (no free text), search by words, Link via keyboard Enter, two manual links "Not saved yet" + "Unsaved changes", save, "Linked by", validity "In the current specification", reload keeps links, no unsaved indicator after reload | **12 / 12 passed** |
| Verify phase after a real API process restart (K0–K17 + viewports): links persisted; Test Coverage rows FR-001 "Automatically identified (1) · Manually confirmed (1)" counted once with 2 cases, FR-002 "Manually confirmed (1)", FR-003 "Approved test case"; all "Not executed", Executed 0; 3 requirements in the specification, 0 without test design; drill-down from FR-002 opens the case; Requirements Traceability rows identical to Test Coverage; unlink FR-002 → FR-002 listed under "Requirements without test design"; Opaque project shows none of LendoPlus's links; no horizontal overflow at 1440 / 1024 / 768 / 390 px in Test Plan Suggestions and the Test Coverage panel | **26 / 26 passed** |
| Browser page errors, console errors, HTTP 5xx, network-guard violations | 0 / 0 / 0 / 0 in every run |

All three runs used the same final build (after the last CSS change). An earlier run on the previous build found an overflow at 390 px: three pre-existing `nowrap` status labels in Test Plan Suggestions (439 px wide). It was fixed by letting labels wrap below 600 px. The full frontend suite was rerun (9,040 / 9,040), the build republished and every browser run repeated on a new database. In that earlier run a script expectation (K0, project already current) also failed; the script was corrected.

Screenshots: `scratchpad/acc/shots-utps/`, `scratchpad/acc/shots-link/` (not committed).

## 10. Backward compatibility

- Plans and snapshots without links: identical JSON and content fingerprint (backend test).
- Older clients (no `RequirementLinks` in a save): links unchanged (backend test).
- Older stored coverage results (links without validity/origin): render as automatic evidence, no gap claimed (frontend test).
- Guide (Skolenærvær) plans: no linking UI, no requirement links sent; MT-7 association display unchanged; guide plan intact in acceptance (R5/R6).

## 11. Known limitations

- The requirement catalog is the browser-parsed specification sent with the save; a modified client could send a fabricated catalog (same trust boundary as Test Coverage and discovery workspace evidence; local single-user tool).
- Linking is available for project plans only, not for historical guide plans (MT-7 owns guide associations).
- Test Plan Suggestions has no navigation guard for unsaved changes (only the visible indicator, as before).
- Execution remains out of scope: no manual run recording exists yet (queued: Unified Manual Testing workspace).
- Requirements in another module/pack are linked only when that specification is the selected one.

## 12. Remaining improvement backlog

- R1: server-side specification parsing (shared parser) to validate catalogs without trusting the browser.
- R2: a "link from the requirement" action in Requirements Traceability (choose cases for a requirement).
- R3: manual test execution records, then an "executed / passed" column fed by real runs.
- Earlier follow-ups F1–F3 (discovery cache durability, React/Angular UI analysis, duplicate hints) remain open.

## 13. Git status and commits

- `96485cff` Navigation progress (feature + its test fixes, committed first, separately).
- The requirement-linking commit contains only the files in section 7 and this report.
- Not committed (not owned by this task, left untouched): `appsettings.Local.json`, the Kode67 gap register, older untracked audit drafts, `AIAssisted/testdata/`.
- Nothing pushed.

Temporary databases created by this task (none dropped; all are template copies of `birknext_pts_acc_20261115`): `birknext_reqlink_acc_20261115` (first acceptance pass), `birknext_reqlink_final_20261115` (final acceptance). Backend test runs create and drop their own `birknext_tps_*` databases. Test servers on :5097 / :5197 were stopped after the runs.
