# Manual Testing and source-driven test planning: report (2026-11-23)

This report covers all the work in one session on branch `008-traceability-first` (not pushed):

1. **Audit:** where Test Plan Suggestions gets its scenarios (read-only).
2. **Source-driven test planning hardening**, plus the extra criterion: no dependency on a static guide.
3. **Manual Testing UX redesign:** Finn og forstå and the landing page.
4. **Independent review** and the fixes it led to.
5. **Verification of commits `514e3927` and `546e6056`:**
   - full suites at the normal build location;
   - migrations and guide jobs after a restart;
   - real imports;
   - a headless UI walkthrough.
6. **Step 5:** Planlegg og klargjør, the master-detail case editor.

## Commits

| Commit | Content |
|---|---|
| `514e3927` | Source-driven test planning. A consistent source basis, snapshot-safe discovery, durable guide-generation jobs, change-set proposals, consolidation and conflicts, plan freshness and impact. Two migrations. |
| `546e6056` | Manual Testing. A scenario-first Finn og forstå and a compact Norwegian landing page. |
| (this commit) | Fixes found with real data: specification evidence stored beside the append-only catalog, and the discovery pointer after a restart. Also Planlegg og klargjør (the case editor) and this report. One migration. |

---

## 1. Audit: where scenarios came from (before the work)

**Finding:** BirkNext could not produce one reliable, source-traceable and snapshot-aware manual test plan from all the imported sources.

- **Project mode** already ran seven providers: specification, System Understanding guide, UI source, API contracts, integration journeys, developer tests, and existing xUnit tests (association only). The guide was one source among several.
- But each run read **one** snapshot chosen in the browser, plus specification text **parsed in the browser**, not the import's own copy.
- There was no combined multi-package baseline, and nothing reacted to a newer import.
- **Guide mode** (`?guide=`) used only the guide, and the only guide registered was the static `skolenaervaer` guide.
- When no guide revision was published, an unreviewed draft guide was shown and used.
- Importing a newer source did not invalidate suggestions; it only added a "may need review" warning on the guide.

## 2. Source-driven test planning hardening (commit `514e3927`)

**Source basis**
- Discovery reads one consistent set of sources (`ScenarioSourceBasisResolver`) through the existing baseline service.
- Several different versions with no selected baseline are reported as unresolved, never guessed.
- The specification comes from the server copy captured at import. A different browser copy is reported and used only when explicitly chosen.

**Plans follow the application, not one import**
- Plans carry an application key and compare against that application's newest import.
- Specification scenarios are matched by situation (file, Given and When), because parser ids are positional.

**Historical vs current**
- Inspecting an older version is Historical and never moves the current pointers.
- Refresh refuses a rollback (`basis-older`).
- A historical discovery cannot extend a plan (`historical-discovery`).

**Case source state**
- Current, Changed, Removed ("Utgått"), or SourceUnavailable ("Kilde mangler").
- Removed is set only after a complete comparison.

**Guides without the static JSON**
- Each application gets its own guide (`app-<sha24>`). `skolenaervaer.guide.json` is a legacy seed, used only when its roles recognise the snapshot.
- Generation is a database-backed job (`system_guide_generation_jobs`, `SystemGuideGenerationWorker`), with retry and reclaim of abandoned jobs.
- Proposals are change sets (new, changed expected result, conflicts with reviewed, evidence removed, ambiguous, linked), each decided on its own.
- Apply changes only source-owned fields and needs the draft's base fingerprint.

**Consolidation**
- Only exact duplicates are merged. Equivalent, related, variant and conflicting scenarios are labelled.
- Contradicting expected results keep every claim. An unresolved conflict blocks approval and readiness.

**Freshness and impact**
- Plan status: "Klar", "Ny kildeversjon", "Må gjennomgås", "Kilde mangler" or "Ikke sammenlignet".
- A read-only impact analysis.

**Translation and export**
- A Norwegian export that falls back to English for some cases warns, and says the file is not fully in Norwegian.

## 3. Manual Testing UX: Finn og forstå and landing page (commit `546e6056`)

**Finn og forstå (scenario first)**
- Source status bar.
- Search plus area, status, type, story and requirement filters.
- "Vis tekniske scenarioer" with a hidden count.
- Paging, multi-select that survives filtering, and keyboard navigation.
- A detail panel with preconditions, steps, expected result and its basis, story, requirements, open questions, and evidence collapsed by default.

**Landing page**
- One next step, real counts (suggestions are never counted as test cases), and saved plans with their source status.
- Stages appear only in the header, in Norwegian.

## 4. Review and fixes (before `514e3927`)

An independent review of the backend, plus my own review of the frontend, found the following. All are fixed and have regression tests.

- Refresh marked cases "Utgått" when their source was unknown. They are now "Kilde mangler".
- Two cases could end up with the same scenario id after a merge, which crashed the save.
- Applying a guide proposal could overwrite reviewer edits, or be applied twice.
- A new module scope, or a second spec file, made every scenario look removed. Identity no longer depends on scope; twin scenarios are told apart.
- Duplicate merging was too loose (numbers and "no" were ignored), and a merge lost information.
- Smaller fixes:
  - a historical discovery could extend a plan;
  - EvidenceRemoved was proposed from an incomplete read;
  - an older retry made the newer proposal stale;
  - a finished job overwrote Stale;
  - identities were read once per plan instead of once per request;
  - the "Vis alle" toggle state was wrong.
- **Not fixed (low risk, single-instance app):**
  - job claiming across several app instances;
  - two simultaneous decisions on the same proposal.

---

## 5. Verification of `514e3927` and `546e6056`

**Process control.** Before any stop, the exact process was identified by port, image path and parent command line. Only BirkNext's own processes were stopped:
- the API on :5000, and its `dotnet run` parent;
- the Blazor dev server on :5173, and its parent;
- the launcher.

The user's stashes (`stash@{0}`, `stash@{1}`) are untouched.

### Full suites (normal build location)

| Suite | Result |
|---|---|
| Backend, after the two commits | 7,165 passed, 1 failed, 19 skipped. The failure, `ApiReviewEngineTests.CI_DEF_3`, passes alone in 130 ms: a load flake. |
| Frontend, after the two commits | 9,411 of 9,411 |
| Backend, final (this commit) | **7,168 passed, 0 failed, 19 skipped** |
| Frontend, final (this commit) | **9,425 of 9,425**. An earlier run had one Project Import timing failure (a different test each run; 141 of 141 pass three times alone): a load flake. |

### Migrations and guide jobs after a restart

- The new API started against the developer database with 0 errors. Migrations up to `20261123150000_GuideGenerationJobsAndDecisions` were applied.
- `20261123170926_SpecificationCatalogEvidence` (this commit) applied at the next start and created `specification_catalog_evidence`.
- **Guide-job recovery:**
  1. Imported `M2LB.zip` (about 271 s).
  2. Simulated an abandoned job: set it to Running, with UpdatedAt 20 minutes ago.
  3. Restarted the API.
  - **Result:** the worker reclaimed the job and it Succeeded on attempt 2, with no duplicate candidate. All 5 jobs of the session finished as Succeeded.

### Real imports and discovery

| Package | Application key | Discovery |
|---|---|---|
| `M2LB.zip` | `m2lb` | Basis Current, specification ImportedSnapshot. 1,533 candidates: specification 1,013, API 419, UI 58, guide 24, journeys 19. Identities unique; 350 Related; 0 merged; 0 conflicts; 109 without expected result. |
| `M2LB-language.zip` | `m2lb-language` | **Defect 1 (fixed, below):** specification NoEvidence. After the fix: 1,752 candidates, including 1,228 specification. Basis Current, ImportedSnapshot, `IDENTICAL_CONTENT`. |
| `Terraform-iac-m2lb.zip` | `terraform-iac-m2lb` | 0 candidates, correctly. No specification, contract or test; an infrastructure-only package. |
| `M2LB.Common.zip` | `m2lb.common` | 0 candidates. 113 tests discovered, none referencing a requirement (association only, correctly). |
| `Tiltak 2 – Sikkert digitalt økosystem.zip` | `buf.meldingsutveksler` | 16 candidates: 12 journeys and 4 guide step checks. Only the scenarios the Skolenærvær guide maps to this source apply (0 of 22 guide scenarios). |

**Isolation.** Inspecting the Tiltak 2 snapshot from project `m2lb`, with m2lb's own source as member, is refused (`400 snapshot-not-in-project`).

> **Note:** project membership is declared by the client. A request that names a foreign snapshot as the project's *own member* is accepted, and moves that project's pointer. That is how the verification script first did it; the pointer was restored by discovering m2lb again.

**Limitation (unchanged, by design):**
- `M2LB-language.zip` is a newer version of M2LB with renamed projects (project-name Jaccard 0.56).
- Its identity comes from the archive name, so it is a **different application** (`m2lb-language`), and an M2LB plan is not compared with it.
- A future "new version of …" decision at import time would join them.

### Defect 1: specification evidence missing for a re-imported archive (fixed)

**Cause:**
- `specification_catalogs` is append-only (a database trigger).
- `M2LB-language` was first imported by a build that stored no specification evidence. The re-import tried to add the evidence by updating that row.
- The trigger refused, which poisoned the shared DbContext, and the guide-job enqueue then failed with `InvalidOperationException`.

**Fix:**
- New insert-only table `specification_catalog_evidence` (one row per catalog, unique `CatalogId`). The row is never rewritten.
- `CaptureAsync` reads the catalog `AsNoTracking`, adds the evidence row, and detaches it on failure.
- `EvidenceAsync` finds the catalog by snapshot id, then by the snapshot's own import (`ProjectImport.ImportId`), then by project key.
- The guide-job enqueue now rethrows the real error instead of hiding it.

**Test:** `A_re_imported_archive_whose_catalog_predates_the_evidence_gets_it_beside_the_append_only_row_and_discovery_finds_it`.

### Defect 2: "Kilde mangler" after a restart (fixed)

Found by the headless UI walkthrough.

**What happened:**
1. The launcher restarted the API.
2. The import's own project (`import:import-a5c0…`) restored its older stored discovery first, from before the specification evidence existed.
3. That set the application pointer. `RestoreApplicationAsync` returned early because a pointer existed.
4. The browser's project request did not restore the plan's own project.

**Result:** the plan showed "Kilde mangler" for its 4 specification cases until the project was opened through the API.

**Fix:**
- Each application's stored results are restored once per process, and the cache only moves the pointer forward.
- `StatusAsync` restores the plan's own project and application first.

**Test:** `After_a_restart_an_older_result_restored_first_never_hides_the_applications_newest_discovery`.

### Plan, edit, approve, translate, export and re-discover on real data (M2LB-language)

Run through the API with the session client; the launch code was never printed.

| Step | Result |
|---|---|
| Plan from 4 specification and 1 API candidate | Created, 5 cases |
| Tester edit (title, 2 steps with expected results, expected result) | Saved |
| Approve a second case | 200 |
| Save against an old revision | **409 `draft-changed`**: no silent overwrite |
| Translate (offline HPLT en→nb) | 1 case translated; "translating approves nothing" |
| Snapshot and export | 200; export has 5,106 characters of text plus JSON |
| Re-discover, impact, refresh | 200; **edit kept: True; approval kept: Accepted**; all cases Current |

The verification left plans named **"Verifisering m2lb-language"** in the developer database. They are labelled and can be ignored or excluded; BirkNext has no delete for plans.

### Headless UI walkthrough (real app, Chromium headless, Playwright)

Steps run by the script:
1. Connected the browser with the launch code (read from file, never shown).
2. Imported `M2LB-language.zip` through Project Import (outcome `ImportedWithNotes`; reused on the server).
3. Opened the landing page, Planlegg og klargjør and Finn og forstå.

| Check | Result |
|---|---|
| Header stages | "Manuell testing · 1 Finn og forstå · 2 Planlegg og klargjør" |
| Landing page next step | "Eksporter til Azure DevOps", pointing to the existing export |
| Plan page | 2 scenarios and 2 cases shown; "3 tekniske testtilfeller skjult". Vis teknisk innhold shows 5. |
| Unsaved edits, then picking another case | Prompt shown; Cancel keeps the edits |
| Add step and save | "Lagret.", revision 5 → 6. A reload shows the step and title; restored afterwards (revision 7). |
| Source banner | Showed "Kildegrunnlaget er endret … 4 testtilfeller kunne ikke sammenlignes" → **Defect 2** (fixed) |
| Console errors | Only 401s from before the browser connected |

**After the fixes (final launcher restart):**
- Migrations are current, up to `20261123170926_SpecificationCatalogEvidence`; 0 errors in the backend log.
- A simulated abandoned guide job was reclaimed and Succeeded on attempt 2, with no duplicate candidates.
- The same walkthrough passes again, and the plan shows **no** source banner (status "Klar"): Defect 2 is confirmed fixed in the real app.
- Save and reload give revisions 8 → 9, and the plan was restored afterwards.

Screenshots were kept in the session's scratchpad; they are not committed.

---

## 6. Step 5: Planlegg og klargjør (this commit)

A new page, `/manual-testing/plan` (`ManualTestingPlan.razor`), is the stage "Planlegg og klargjør". Pure logic is in `PlanPreparePresentation`.

**Master (left)**
- The plan's scenarios with their test cases, several per scenario.
- Story id, status chip ("Utkast", "Klar for gjennomgang", "Trenger avklaring", "Godkjent") and source chip ("Kildegrunnlaget er endret", "Utgått scenario", "Kilde mangler").
- Search by title, story or requirement. "+ Testtilfelle" adds a case under a scenario.

**Detail (right)**
- Fields: Tittel, Formål, Forutsetninger, and Testdata (the need, what the source says, linked assets).
- **Teststeg**: each step has a Handling and a Forventet resultat. Steps can be added, edited, removed and moved up or down; empty rows are never saved.
- The overall Forventet resultat.

**Saving**
- Only human-owned edits are sent; a value equal to the generated one is "no edit". Fields the form does not show (type, risk, template values) are kept.
- The plan's revision is sent. A stale save is refused, and the form keeps the tester's text.

**Unsaved-edit protection**
- Switching case, changing plan, adding a case or navigating away (`NavigationLock`) asks first: "Lagre og fortsett", "Forkast endringene" or "Avbryt".
- Leaving the browser tab is guarded by the browser's own confirm.

**Review**
- "Klar for gjennomgang" and "Godkjenn" are enabled only for a saved, complete case (title, at least one step, an expected result) with no unresolved source conflict. The missing items are listed.
- "Trenger avklaring" is always possible.

**Context (Kildegrunnlag)**
- User story (marked technical when it is), requirements (FR ids from the case's own evidence plus manual links; never inherited from the story), evidence, open questions, and the source's expected result when the tester changed it.

**Vis teknisk innhold**
- Hidden by default:
  - API operations; authorization checks no requirement names;
  - page smoke checks, integration journeys, performance;
  - cases whose **user story or title is about the code base**, e.g. "Translate Non-Domain Identifiers to English", "All Tests Pass After Translation", "Developers read the code without Norwegian knowledge".
- Never hidden:
  - cases written by a person, edited, approved, needing clarification or ready for review;
  - cases with a source conflict.

**Translate and export are the existing ones**
- "Oversett" calls the existing per-scenario translation (Norwegian, offline translator) and is disabled while edits are unsaved.
- "Eksporter" opens the existing export of the plan (`test-plan-suggestions?…&area=export`).
- The full page stays reachable as "Avansert visning" / sidebar "Plans & export".

**Integration with Finn og forstå**
- "Forbered testtilfeller" now opens Planlegg og klargjør.
- The tab's selection is offered as "Legg til i «plan»" or "Lag ny plan", and is cleared after use.

**Navigation**
- Sidebar: "Plan & prepare" points to the new page; "Plans & export" points to the existing page. Both are registered in the documentation catalog.
- The stage titles are now "Finn og forstå" and "Planlegg og klargjør".

### Tests A–G (`ManualTestingPlanTests`, 16 tests)

- **A.** An unchanged form produces no edits, and unshown fields are kept. Steps can be added, edited, removed and reordered; empty rows are dropped; the result reads back.
- **B.** Technical scenarios and stories are hidden; what needs a person is never hidden. Search works by title, story and requirement.
- **C.** Master-detail rendering in Norwegian, several cases per scenario, the context panel and the source banner.
- **D.** Save sends the revision and only this case's edits. A stale save keeps the form. A saved case reads back.
- **E.** Unsaved edits are protected when switching case and when navigating.
- **F.** Ready and approve work; an incomplete or conflicting case cannot be approved; a reader without write access cannot edit.
- **G.** The selection is added to the plan and cleared. Translate uses the existing endpoint and export opens the existing export. Adding a case opens it in the editor.

Existing navigation, feature-visibility and Manual Testing tests were updated for the new route (+1 sidebar row).

### Acceptance criteria (step 5)

| AC | Result |
|---|---|
| Modern master-detail case editor | PASS |
| Steps with action and expected result: add, edit, remove, reorder | PASS |
| Preconditions and test data | PASS. The test-data *need* is editable; asset links stay in Test Data / Avansert visning. |
| Several cases per scenario | PASS |
| Integration with Finn og forstå | PASS |
| Unsaved-edit protection | PASS |
| "Vis teknisk innhold" for technical US, FR and scenarios | PARTIAL. Stories and scenarios are classified by a documented word list. A technical FR is not hidden by itself: FR ids are shown as context on their case. Finn og forstå keeps its own capability-based filter. |
| US, FR and evidence as context | PASS |
| Translate kept | PASS (per scenario; "Oversett alle" is still in Avansert visning) |
| Export kept | PASS (the existing export) |
| Norwegian labels | PASS on the new page. The sidebar labels stay English, as the rest of the sidebar is English. |
| Tests A–G | PASS (16 of 16) |
| Real M2LB workflow | PASS (API flow and headless UI walkthrough above) |
| Full regression | PASS: backend 7,168/0/19 skipped, frontend 9,425/9,425 |

## Known limitations and follow-ups

- **"New version of" decision:** an archive with a different name is a different application (M2LB vs M2LB-language).
- **Client-declared membership:** a request that names a foreign snapshot as the project's own member is accepted.
- **Test isolation (pre-existing):** the default `WebApplicationFactory` test host uses the app's real connection string, so tests migrate the developer database.
- **Low-risk concurrency items** from the review (section 4).
- **Technical classification** uses a narrow, documented word list. A business story that happens to mention "developer" is hidden only until the tester turns on "Vis teknisk innhold". It is never hidden once someone has worked on it.
