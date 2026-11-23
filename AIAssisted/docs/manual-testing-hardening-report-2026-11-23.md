# Manual Testing hardening — Step 6 report (2026-11-23)

Branch `008-traceability-first`, not pushed. Commits, in order:

| Commit | Content |
|---|---|
| `b1365f9a` | Plan lifecycle (archive, restore, logical delete), explicit application identity at import with a change summary, the shared technical-relevance rule, one primary workflow (Planlegg og klargjør gains the legacy page's daily functions), legacy redirects. Migration `TestPlanLifecycle`. |
| `6e39f632` | One scenario resolution for status, impact, refresh and the change summary (a real-data defect, see L). |
| `07a1f12a` | Change summary compares with the previous *content* version; UI scenarios get a project-independent identity (former identity kept as an alias). |
| `2a21bf0a` | Legacy-link redirects after the first render (a real-browser defect); narrow-width fix for the Test Data picker. |
| (this commit) | This report. |

The user's instruction during the task changed from "do not commit" to "commit ALL and continue"; everything above was committed after that instruction.

## A. Initial repository state

- Clean tree at `42bae213` on `008-traceability-first`.
- `stash@{0}` and `stash@{1}` were present. They were never applied or changed.
- BirkNext was running on :5000 (API) and :5173 (web).
- Database: the local developer PostgreSQL (`birknext`). Backend tests use their own temporary databases or in-memory contexts.
- Test data: `AIAssisted/testdata` contains M2LB, M2LB-language, M2LB.Common, Terraform (×4) and Tiltak 2.

## B. Existing architecture reused (nothing duplicated)

- **Plan persistence:** `TestPlanSuggestionService` with the revision-checked `WriteAsync`. Every lifecycle change is a revision.
- **Concurrency:** the existing row-version check. Stale requests get 409 `draft-changed`.
- **Authorization:** the existing `IntegrationConfigurationWritePolicy`.
- **Source identity:** the snapshot's `Repository` identity, read everywhere through `SourceBaselineService.KeyOf`. A declared import identity is stored there, so baselines, discovery and plans follow it with no second identity engine.
- **Baselines:** the existing `SelectBaselineAsync` decision ("Kildeversjoner").
- **Scenario matching:** `CandidateIndex` plus `Renumbered` (now one shared order).
- **Translation and export:** the existing per-scenario translation, translate-all and `AdoExportWizard` (with its language choice). No new engines.
- **Classification:** next to the existing `ScenarioOwnership` rule, in the same shared file.

## C. Confirmed gaps (before this step)

1. There was no way to archive or delete a plan.
2. Application identity came from the archive name only (M2LB vs M2LB-language).
3. Technical requirements (FR) were not classified.
4. Two competing plan editors existed. The legacy page alone held export, versions, source changes and refresh, translate all, conflict resolution, exclusion and Test Data linking, and running discovery from Finn og forstå.
5. Found during real-data verification:
   - status, impact and refresh disagreed for a moved specification;
   - the change summary could compare a version with itself;
   - UI scenario identities depended on the project key;
   - legacy redirects did not complete in a real browser;
   - the Test Data picker overflowed at phone width.

## D. Plan management

**Archive**
- Active → Archived, at the revision the tester saw.
- The plan becomes read-only: every content mutation returns 409 `plan-archived`.
- It is listed apart (`ArchivedDrafts`) and excluded from active lists, counts and prepared-case traceability.

**Restore**
- Archived → Active, at the revision shown.

**Delete**
- Logical, and only for an archived plan.
- The tester must type the plan's exact name; a wrong name returns 400 `confirm-name`, and a stale revision returns 409.
- A global EF query filter hides the deleted plan from every query, and opening it returns 404.
- The row, every revision and every frozen snapshot are kept. Snapshots are immutable by database trigger and reference the plan, so a permanent delete would orphan the audit trail.
- There is no purge. Retention is "kept".

**Auditing and API**
- Each change writes a revision with its reason ("Arkivert: …", "Gjenopprettet fra arkivet", "Slettet") and records who and when (`LifecycleChangedAt` / `LifecycleChangedBy` / `LifecycleReason`).
- The endpoints are POST `drafts/{id}/archive`, `/unarchive` and `/delete`, all with the write policy. There is no DELETE verb and no anonymous access; a test asserts the attributes.

**UI**
- **Manuell testing → Lagrede planer:** Aktive and Arkiverte views. Each plan shows its name, application, last change, scenario and test-case counts, approvals, clarifications and source status, with Åpne, Eksporter and Arkiver (or Vis, Gjenopprett and Slett, with an inline typed confirmation).
- **Planlegg og klargjør:** an «Arkiver plan» confirmation, and a read-only banner with «Gjenopprett» when an archived plan is opened.

**The four "Verifisering m2lb-language" plans were not touched.** They can now be archived or deleted by a person; nothing was deleted by name.

## E. Application import and version identity

- **The choice.** After the archive is chosen, the import page asks «Ny applikasjon» or «Ny versjon av eksisterende applikasjon» (pick the application, with «Bruk denne versjonen som gjeldende kildegrunnlag», default on).
  - Nothing is preselected, and Import waits for the answer whenever applications already exist.
  - With no applications yet, no choice is needed.
- **New version.** The new snapshot gets the chosen application's identity, and the archive's own identity is kept in `ArchiveRepository` for lineage. Both imports keep their own snapshots, and existing snapshots are never rewritten.
  - With "use as current", the import is recorded as the analysis baseline through the existing decision log.
- **New application when its name is taken.** It gets its own identity (`M2LB (2)`), so it never joins an application silently.
- **Unrelated archive.** A warning appears, never a refusal, when the archive shares no .NET project with the chosen application's latest import.
- **No choice** (older clients, Source Analysis uploads): the archive's own identity, as before.
- **After import:**
  - the result card shows the application, mode, archive name and baseline;
  - «Sammenlign scenarioer med forrige versjon» runs discovery on request and shows **computed** counts: new, changed, removed, unchanged, moved (same text), not compared, conflicts and plans to review.
  - It compares with the newest earlier discovery whose *source content* differs, so a re-import of identical content never compares a version with itself.
- **Failure safety.** A choice cannot change after the import has settled (`IMPORT_ALREADY_COMMITTED`), and a staged upload that expires keeps nothing.

## F. Technical user stories and requirements

**The rule**
- One shared rule (`ManualTestingRelevance`) applies to scenarios, user stories and requirements.
- Text is technical only when it is about the code base or its operation (renaming identifiers, refactoring, builds and test suites, telemetry and configuration names, infrastructure, governing documents, ADRs) **and** describes nothing a person observes (a user, screen, message, status code, rejection, access).
- Uncertain text is never technical. Document numbering ("User Story 2 –", "(Priority: P1)") is ignored. Words like "API", "HTTP" or "database" alone never decide.

**Where it applies**
- **Finn og forstå:** «Vis teknisk innhold» hides technical scenarios (including those under technical stories) and technical requirements, with hidden counts.
- **Planlegg og klargjør:** the same control applies; a technical FR linked to a case stays visible in the case context, marked «teknisk».
- **Never hidden:** conflicts, cases a person wrote, edited, approved or marked for clarification, and authorization checks a requirement names.
- **Not filtered elsewhere:** filtering is presentation only. Nothing changes in Source Analysis, traceability, coverage, persistence or exports.

**Real data (M2LB)**
- 681 of 1,752 scenarios and 3 requirements are hidden by default.
- Business stories ("User Activated When Assigned to M2LB in Entra", "Overblikk ved innlogging") stay visible.

## G. Legacy UI consolidation

**Planlegg og klargjør is the one primary workflow.** Its tabs:
- **Testtilfeller:** the editor.
- **Eksporter:** the existing Azure DevOps wizard, with language choice and unsaved-edit guard.
- **Versjoner:** revision history and restore.
- **Kildeendringer:** impact and safe refresh.

Also on the page:
- «Oversett alle», and «Oversett» per scenario;
- translation review and editing;
- Test Data linking;
- conflict resolution with a chosen claim and a reason;
- «Utelat» (exclude) with a reason.

**Finn og forstå** now runs discovery itself.

**Legacy page**
- Ordinary links to `/test-plan-suggestions` (bookmarks, Test Coverage / Traceability drill-downs, retired Execute links) redirect: `?area=choose` goes to Finn og forstå, anything else goes to `/manual-testing/plan` (with `draft`, `case` and `panel=export` kept).
- The page remains only as «Avanserte verktøy» (`?advanced=1`) for guide-mode plans, variant proposals, JSON snapshots and delivery comparison. It is no longer a sidebar entry and is classified as a contextual route.
- **Not deleted.** The legacy page code was not removed: guide-mode plans, variants and delivery comparison still live only there (see Q).

## H. Manual-tester UX improvements

- Norwegian labels throughout the Manual Testing pages.
- Selected state and «●» on the case being edited.
- Progressive disclosure: evidence collapsed by default; «Hvorfor dette forventede resultatet?» answered in plain words (the source that states it, and whether a person confirmed it).
- Unsaved-edit protection across case, plan, tool and navigation changes.
- Responsive at 390 px with no horizontal page scroll (verified in Chromium).
- Keyboard-reachable controls with labels.
- No modal dialogs: confirmations are inline.

## I. Translation and export

**Reused, not rebuilt**
- Per-scenario translation, translate-all and the export wizard are reused unchanged, including its language selection and mixed-language safeguards.
- The nested manual-case export regression test (`ScenarioCentricPlanningServiceTests`) runs in the backend suite.

**Translation review**
- The case shows the Norwegian title, preconditions, steps (action and expected) and expected result, with a status:
  - «Maskinoversatt — ikke gjennomgått»;
  - «Maskinoversatt og redigert»;
  - «Skrevet av en person»;
  - «Oversatt fra en eldre tekst — gjennomgå».
- Reviewer edits are saved through the normal save. The server marks them edited and binds them to the original, and the original stays authoritative.

## J. Source and traceability safety

**Verified on real data**
- Edits and approvals survive a new version and refresh.
- A stale save returns 409 `draft-changed`.
- A historical inspection cannot extend a plan.
- "Kilde mangler" is reported separately from "Utgått".

**Fixed in this step**
- **One locate order** for status, impact, refresh and the change summary: identity, then an unambiguous same-text move. A case without an identity follows its move first.
- **Impact** reports a move as a change of place, never a removal.

## K. Database and migration

- **New migration:** `20261123190604_TestPlanLifecycle`, generated by `dotnet ef` (context `AppDbContext`) with its Designer and an updated model snapshot (+18 lines).
- **Purely additive:** four columns on `test_plan_drafts` (`Lifecycle` not null, default `'Active'`; three nullable). No drops, and existing plans become Active.
- **Integrity tests:** pass. One run failed against a stale build and passed after the rebuild.
- **Applied at restart:** confirmed. The lifecycle endpoints read and write the new columns on the real database (workflow C).

## L. Real M2LB results (API, local app)

| Workflow | Result |
|---|---|
| A — new plan on M2LB | Discovery Current (1,533 candidates). The plan has 4 specification cases, a tester edit and one approval. |
| B — M2LB-language as a new version of M2LB | Options listed known applications; choice NewVersion m2lb; no warning (shared projects). Commit after 315 s created snapshot `9e53454b…`, Application = M2LB (archive M2LB-language), baseline selected. The M2LB project's basis became M2LB-language.zip with `BASELINE_SELECTED`. |
| B — change summary (after fixes) | Compared with M2LB.zip: 435 new, 6 changed, 216 removed, 1,038 unchanged, 273 moved (same text), 0 conflicts, 1 plan to review. |
| B — plan freshness | **Defect found and fixed:** status said "4 endret", impact "4 utgått" and refresh kept the cases (the specification folder was renamed). After the fix, status and impact both say "4 endret, 0 utgått", each explained as "same text, new place". Refresh kept the tester's edited title and the approval (revision 4); nothing was superseded. |
| C — temporary plan lifecycle | Delete while active → 409 `plan-not-archived`; archive with a stale revision → 409; archived plan in the archived list only; save on archived → 409 `plan-archived`; restore keeps 2 of 2 cases; wrong confirmation name → 400; delete → 204; opening → 404; in no list; other plans untouched. |
| D — technical filters | Finn og forstå: 1,071 of 1,752 shown, 681 technical hidden, 3 technical requirements hidden. The open plan's 4 cases (a story about translating identifiers) are hidden by default and shown with «Vis teknisk innhold». |
| E — legacy compatibility | `/test-plan-suggestions?area=prepare` goes to Planlegg og klargjør; `?area=choose` goes to Finn og forstå (real Chromium, after the fix). |

**Packages not re-imported in this step:** Terraform, M2LB.Common and Tiltak 2. They were imported earlier in the session; their identities (`terraform-iac-m2lb`, `m2lb.common`, `buf.meldingsutveksler`) appear as separate known applications in the import choice, and isolation was verified earlier.

## M. UI walkthrough (headless Chromium against the real app)

**Import**
- The choice card is shown, and Import is disabled until a choice is made (with an explanation).
- «Ny versjon av M2LB» was chosen with no warning; the result shows "M2LB (ny versjon)", the archive name and the baseline.
- The comparison ran on request.

**Other pages**
- **Home:** Aktive and Arkiverte, with the plan facts and the source status «Må gjennomgås».
- **Finn og forstå:** the counts above.
- **Planlegg og klargjør:** all four tools render (the export wizard, versions, impact), and «Hvorfor dette forventede resultatet?» is shown.
- **Narrow width:** at 390 px, home and Finn og forstå have no overflow; the plan page had overflow from the Test Data picker, now fixed.
- **Console:** 0 errors after connecting.

**Limits**
- These walkthroughs are automated and are **not** equivalent to acceptance by human testers.
- In this environment the app shell can wait about 28 s for a technology-coverage request before rendering pages. That is pre-existing and noted in Q.

## N. Backend tests

| Run | Result |
|---|---|
| Full suite, normal build location, at `b1365f9a` (rerun after a stale-build artefact) | **7,180 passed, 0 failed, 19 skipped** |
| Focused after `6e39f632` / `07a1f12a` (planning, discovery, Critical E2E suggestions, coverage) | 536 of 536 |
| Final full suite after `2a21bf0a` | **Not completed.** Claude Code stopped it because the machine ran low on memory. It was not restarted, as instructed by the tool. `2a21bf0a` changes only frontend files, so the backend code tested at `07a1f12a` is unchanged. |

New backend tests:
- lifecycle archive/restore and logical delete;
- application identity (6 tests);
- the change summary;
- the moved specification;
- the restart pointer;
- lifecycle authorization;
- UI identity.

## O. Frontend tests

| Run | Result |
|---|---|
| Full suite, normal build location, at `b1365f9a` | **9,464 of 9,464** |
| Focused after `2a21bf0a` (redirects, Manual Testing, legacy page) | 71 of 71 |
| Final full suite after `2a21bf0a` | Built concurrently with the backend run, so the web project could not be rebuilt and **ran stale**: 9,462 passed, 2 failed (an authentication-method test and an active-target timing test, both outside Manual Testing). It needs a clean rerun. |

New frontend tests:
- the plan editor tools, lifecycle, translation review, Test Data, conflicts, exclusion and deep links (H–N);
- saved plans;
- the import choice and summary;
- relevance classification;
- legacy redirects.

## P. Files changed

**Backend**
- `Data/AppDbContext.cs`, `Models/TestPlanRecords.cs`, and migration `20261123190604_TestPlanLifecycle` with its Designer and snapshot.
- `Services/TestPlanning/`: `TestPlanSuggestionService.cs`, `.Lifecycle.cs`, `.Sources.cs`; `TestScenarioDiscoveryService.Changes.cs`; `ScenarioDiscoveryStore.cs`; `TestScenarioDiscoveryEngine.cs`.
- `Services/ProjectImport/`: `ProjectImportService.cs`, `.Application.cs`, `ProjectImportStagingStore.cs`.
- `Services/Integrations/SourceEvidence/IqrSourceStore.cs`, `Services/TestCoverage/TestCoverageReviewService.cs`.
- Controllers: `TestPlanSuggestionsController.cs`, `ProjectImportController.cs`.

**Shared**
- `TestPlanSuggestionContracts.cs`, `ProjectImportContracts.cs`, `TestScenarioDiscoveryContracts.cs`, `IqrSourceContracts.cs`, `ScenarioOwnershipContracts.cs`.

**Frontend**
- Pages: `ManualTesting.razor`, `ManualTestingDiscover.razor`, `ManualTestingPlan.razor`, `ManualTestingExecuteRedirect.razor`, `ProjectImport.razor`, `TestPlanSuggestions.razor` (redirect only).
- New components: `Components/ManualTesting/{SavedPlans, PlanVersionsPanel, PlanSourceChangesPanel}.razor`, `Components/ProjectImport/{ImportApplicationChoice, ImportApplicationSummary}.razor`.
- Services: `PlanPreparePresentation.cs`, `ManualTestingPresentation.cs`, `ScenarioBrowserPresentation.cs`, `NavigationCatalog.cs`, `FeatureVisibilityTaxonomy.cs`, `TestPlanSuggestionsApiService.cs`, `ProjectImport/ProjectImportApiService.cs`.
- `wwwroot/css/manual-testing.css`.

**Tests and fixtures**
- Backend and frontend test files listed in N and O.
- Playwright fixtures answer the new import question («Ny applikasjon»).

## Q. Known limitations

- **Full regression needs one rerun.** The final full backend run was stopped for low memory, and the final frontend run used a stale build. Rerun both with the app stopped; see O for the exact state.
- **Legacy page not deleted.** It keeps guide-mode plans, variant proposals, JSON snapshots and delivery comparison. Removing it needs those moved first (the smallest next item: variants into the case editor).
- **Requirement texts on the plan page** come from the browser's selected specification. If no specification is selected, linked FR ids are shown without text and without the «teknisk» mark.
- **Classification is a word rule.** A business story using code-base words *and* nothing observable is hidden until «Vis teknisk innhold»; it is never hidden once someone has worked on the case.
- **The unrelated-archive warning** compares .NET project names only. Archives without project files get no warning.
- **Slow workspace start (pre-existing):** the technology-coverage request took about 28 s here, which delays page rendering after a full reload.
- **Single-instance assumptions** from the earlier review (job claiming, concurrent decisions) are unchanged.

## R. Acceptance criteria

| AC | Result | Evidence |
|---|---|---|
| 01 One primary workflow | PASS | Sidebar has one Plan & prepare; legacy links redirect (G, L-E) |
| 02 Scenarios primary in Finn og forstå | PASS | Scenario list first; discovery runs on the page |
| 03 Test cases primary in Planlegg og klargjør | PASS | Master-detail editor is the default tab |
| 04 Complete discovery → export | PASS | Walkthrough and workflow A; export tab with the existing wizard |
| 05 Archive | PASS | Workflow C, tests |
| 06 Restore | PASS | Workflow C, tests |
| 07 Safe, authorized, auditable delete | PASS | Logical delete, typed name, revision, write policy (D) |
| 08 Plans and revisions preserved | PASS | Revisions and snapshots kept after delete (test, C) |
| 09 Explicit new application | PASS | Choice card; taken name gets its own identity |
| 10 Explicit new version | PASS | Workflow B |
| 11 Different file name joins when chosen | PASS | M2LB-language → M2LB (B) |
| 12 History and lineage preserved | PASS | Both snapshots kept; `ArchiveRepository` |
| 13 Technical scenarios hidden | PASS | 681 hidden (D) |
| 14 Technical stories hidden | PASS | Story rule, tests, real data |
| 15 Technical FR hidden but contextual | PASS | 3 hidden in Finn og forstå; marked in case context |
| 16 Security, functional and E2E visible | PASS | Conflicts, named authorization and integration stay visible (tests) |
| 17 Legacy functions in the canonical workflow | PARTIAL | Daily functions moved; guide mode, variants and JSON snapshots remain in Avanserte verktøy |
| 18 No competing primary editor | PASS | The legacy page is advanced tools, not a sidebar entry |
| 19 Translation in Plan & Prepare | PASS | Oversett / Oversett alle (tests N, G) |
| 20 Language selection and safeguards | PASS | Existing wizard and validators unchanged |
| 21 Manual and nested cases translate | PASS | Existing backend regression test in the suite at `b1365f9a` |
| 22 Export respects language | PASS | Existing wizard and tests |
| 23 Edits and approvals survive | PASS | Workflow B refresh; moved-spec test |
| 24 Conflicts and stale cases visible | PASS | Case chips, banner, conflict form |
| 25 Large datasets usable | PASS | 1,752 candidates, paging, filters; existing 5,000-candidate test |
| 26 Norwegian UI | PASS (Manual Testing) | The Project Import page is otherwise English; only its new choice card is Norwegian |
| 27 Backend regressions pass | PARTIAL | 7,180/0/19 at `b1365f9a`; final rerun stopped for memory |
| 28 Frontend regressions pass | PARTIAL | 9,464/9,464 at `b1365f9a`; final run stale (O) |
| 29 Real-data workflows verified | PASS | L |
| 30 No unauthorized destructive changes, no push | PASS | Only verified BirkNext processes stopped; nothing deleted except the temporary plan created in workflow C (logically); not pushed; stashes untouched |

## Human manual-tester acceptance checklist

1. **Import a new version.** Import a newer ZIP of an existing application and choose «Ny versjon av eksisterende applikasjon». Check that the result shows the right application and that «Sammenlign scenarioer» gives believable counts.
2. **Finn og forstå.** Check that business scenarios come first and that «Vis teknisk innhold» reveals only what you consider technical. Report anything hidden that you need.
3. **Prepare cases.** Select 3–5 scenarios, open Planlegg og klargjør and add a case under a scenario. Write preconditions, steps (action and expected result) and the expected result, link Test Data and save.
4. **Unsaved changes.** Switch case with unsaved edits and confirm you are asked. Reload the page and confirm the saved text is there.
5. **Translation.** Press «Oversett», review the Norwegian text, correct one step and save. Check the status says it was edited.
6. **Export.** Open «Eksporter», choose Norwegian and export. Open the file in Azure DevOps Test Plans (import) and check one case row by row.
7. **Archive and delete.** Archive a plan you no longer need, find it under «Arkiverte», restore it, archive it again and delete it by typing its name.
8. **Source changes.** After a new version, open an older plan, read «Kildeendringer», press «Oppdater planen» and confirm your edits and approvals remain.
