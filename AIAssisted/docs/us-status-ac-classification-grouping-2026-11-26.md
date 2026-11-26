# User story implementation status and acceptance scenario classification (2026-11-26)

This covers two briefs, implemented together as one shared capability:
- «US Implementation Status Engine»;
- its revision «US Status Engine + AC Classification & Grouping».

Branch `008-traceability-first`, committed locally (nothing pushed). There was no data reset, database cleanup or destructive migration, and no Azure DevOps call. Both stashes are kept.

**Status words used:**
- **CONFIRMED** — checked against the real source or a test run.
- **INFERRED** — follows from a documented rule.
- **AMBIGUOUS** — the source does not settle it.
- **MISSING** — not built or not in the source.

## A. Repository state and what already existed (audit)

| Capability | State before | Used how |
|---|---|---|
| tasks.md parsing | ALREADY IMPLEMENTED: `TaskExplorerService.Parse` (shared), `DeliverySnapshotParser`, and `ImplementationGapExtractor.Tasks`. Implementation Gaps persists the extracted task lines per project import (`implementation_gap_evaluations.InputsJson`, scope `import:{id}` = the project key) | **Reused.** The engine reads those persisted lines; there is no new parser and no re-reading of the ZIP |
| Task → story ownership | ALREADY IMPLEMENTED: `WorkItemInterpreter` (`[USn]`, story phases, shared/foundational) | Its `StoryKey` / `PhaseStoryKey` rules are reused |
| Raw checkbox marker, file legend, duplicate ids, `[X/]` | MISSING: `[~]`, `[-]`, `[/]` became «in progress», and `[X/]` was not read as a task at all | Added to the extraction (additive fields; see C) |
| Per-story status | PARTIAL: Delivery Reporting counts per story, but has no status for a single story | New engine (one, server-side) |
| Confirmation workflow | ALREADY IMPLEMENTED: `ManualReviewDecisionStore`, which is append-only, with reviewer, time, note, evidence and «applies to» | **Reused** (module `user-story-implementation`) |
| Scenario classification | PARTIAL: `FunctionalQualityClassifier` classifies feature and story text by keywords (categories, no Mixed or Non-functional); Manual/Technical exists separately | A new AC-level rule set (`scenario-type/1`) based on what the scenario asserts; categories align with the existing ones |
| Per-scenario reviewer decisions | ALREADY IMPLEMENTED: `story_groupings` document, with Manual/Technical decisions (append-only, actor, time) | **Reused**: `SetScenarioType` operation and `ScenarioTypes` list |
| Dashboard registry and settings | ALREADY IMPLEMENTED (2026-11-25) | 4 new figures and 4 new charts, all off by default |

**Source used:** `M2LB-language(3).zip` is not on this PC. The real **M2LB.zip** (Test-packages) is used instead: 35 specifications, all with spec.md and tasks.md. Its markers across all tasks.md files:
- 1,643 `[X]`, 252 `[x]` and 90 `[ ]`;
- 29 `[~]`;
- 1 `[X/]`.

## B. Status model

There are four separate dimensions:
1. **Task status** (from tasks.md).
2. **Evidence quality.**
3. **Implementation confirmation** (a person).
4. **Scenario type** (per AC).

Test preparation and approval are untouched, and no execution figure exists anywhere.

| Task status | Rule |
|---|---|
| Ukjent | No tasks.md beside the specification, no task linked to the story, or no task extraction for the import |
| Ikke startet iht. plan | A reliable direct task set, none complete |
| Under utvikling | Some complete, some not |
| US-oppgaver fullført | Every eligible direct task is checked and no blocking dependency is open. **Never «Implementert»**: the explanation says «Dette bekrefter ikke alene at funksjonaliteten er ferdig implementert.» |
| Avhengighet gjenstår | All direct tasks are checked, but a task they explicitly depend on is not |
| Må avklares | A marker the source does not explain (progress is then «kan ikke beregnes sikkert») |

### Ownership

The order of precedence:
1. The task's own `[USn]` tag.
2. Otherwise, a phase titled for one story.
3. Otherwise, shared/foundational work. It is listed as «Fellesoppgaver … mulig avhengighet, ikke bekreftet», never counted.

A tag naming another story than its phase is counted for its tag and reported as **«Oppgavekoblingen er motstridende»**. A tag that is not one story (`[US1-2]`) is never counted and is reported. Titles and wording never link a task.

### Markers

`[x]`/`[X]` mean complete and `[ ]` means open. Any other marker is interpreted only by:
- **the task's own words** («**Superseded**», «**Partially done**»);
- **or the tasks document's own legend**.

Otherwise it is «Ukjent markering» and the percentage is unavailable.

The real legend, in `TjenesteService/specs/004-child-placements-period/tasks.md`:
> «A task marked `[~]` was superseded: its original text is kept after "Superseded", and nothing is built for it today.»

Superseded tasks are listed as «Utgått» and left out of the count. A deferred task that is not checked stays open work («Utsatt»).

### Dependencies

Only explicit wording («depends on», «requires», «after», «avhenger av», «krever», «etter») is a **Bekreftet avhengighet**. A task that is only mentioned is a **Relatert oppgave**, and a task id that doesn't exist is **Uløst**. Every task id is scoped to its own tasks.md, and cycles are reported.

### Confirmation

«Bekreft implementert» records a manual-review decision with these properties:
- It records the reviewer, the time, an optional reason and the tasks.md path with its source version.
- It applies to the **fingerprint of the story's task evidence**. When the tasks change, it shows «Må bekreftes på nytt»; the history is kept.
- It needs write permission and a signed-in person, and is refused when the story has no task evidence.

## C. Changed import extraction (additive)

`GapTaskInput.Marker` (raw marker), `GapDocumentSet.MarkerConventions` (the document's own marker definitions) and `GapDocumentSet.DuplicateTaskIds`. A 2–3-character marker (`[X/]`) is now a task of Unknown state instead of being skipped.

Imports made before this change keep their inputs:
- `[x]` and `[ ]` are read as before;
- `[~]`, which they stored without its marker, shows «Markeringen er ikke lagret for denne importen; importer prosjektet på nytt».

Implementation Gaps' own results are unchanged (its 112 tests pass).

## D. Real M2LB examples (CONFIRMED in the isolated browser run and in tests)

| Example | Source | Result |
|---|---|---|
| A. Frontend Child Search US1 | `Frontend/specs/002-child-search-profile/tasks.md` T022–T029 `[X]`, T050 `[ ]` | **Under utvikling, 8 av 9, 89 %, 1 oppgave gjenstår (T050)**. «depends on T003» is a confirmed, completed dependency |
| B. US2 | Same file. T042 `[ ]` «— **deferred**: requires backend GraphQL schema extension» | **Under utvikling, 13 av 14, 93 %**. T042 is shown as open «Utsatt», never removed |
| C. US3 | T044–T046 `[X]` | **US-oppgaver fullført, 3 av 3, Ikke bekreftet** |
| F. Proxy Initial Setup US1 | `Proxy/specs/001-proxy-initial-setup/tasks.md` T010–T015 `[ ]` | **Ikke startet iht. plan** (test) |
| J. `[X/]` | `AutorisasjonService/specs/001-translate-source-english/tasks.md` line 49 | **Må avklares**: «Markeringen [X/] er ikke forklart i tasks.md eller i oppgaven» (test) |
| `[~]` by legend | TjenesteService 004 (above) | «Utgått», not counted (test) |
| `[~]` by own words | TjenesteService 003 T017 «**Partially done**» | Delvis (test) |
| H. Missing tasks.md | — | Ukjent, «tasks.md mangler for spesifikasjonen.», no 0 % (test) |
| I. Tasks without spec | — | No story is created from tasks.md (test) |

**Not verified separately:**
- **D. SkoleService US1 and E. SkoleService US8** (cross-cutting tasks): the rules are the same. Open polish tasks appear under «Fellesoppgaver», never counted.
- **G. TjenesteService US4:** not checked in the browser.

## E. Acceptance scenario classification

There is one rule set (`shared/ScenarioTypeClassification.cs`, `scenario-type/1`). It is deterministic and used by the server (Testoversikt) and the browser (Finn og forstå). It applies only to **acceptance scenarios**.

Two kinds of item are kept apart:
- requirements without one become «Krav uten akseptansescenario»;
- proposals from contracts or code become «Andre forslag».

### Evidence order

1. **Explicit marker:** the scenario names its own type («(NFR)»).
2. **Requirement links:** every linked requirement is an NFR-… (an FR link never decides).
3. **The scenario itself:**
   - its outcome (Then), clause by clause: a quality clause, such as a threshold, WCAG, keyboard or focus, colour or visual consistency, readiness or health, retry or not-lost, encryption or never-logged, or telemetry, versus an observable business outcome;
   - plus the action (When).
   - Rules for combining them:
     - a quality clause that also has a concrete outcome (HTTP status, «returns», a quoted message), or a quality action with a functional outcome, makes the scenario **Blandet**;
     - universal design stays **Ikke-funksjonell** (its observable result is the quality);
     - nothing clear means **Uavklart**, never functional by default.

Titles, folder names, technology names and security vocabulary on their own never decide: «Kode 6/7» business rules stay functional.

**Real results** (backend tests on verbatim scenarios, and the browser):

| Case | Source | Result |
|---|---|---|
| A | Frontend 002 US1 scenarios 1–6 | Funksjonell (all six), no invented performance |
| B | Frontend 003 US2 (colour #212121, visually consistent) | Ikke-funksjonell · Brukervennlighet |
| C | Frontend 003 US5 (skip link, focus, screen reader) | Ikke-funksjonell · Universell utforming (all four, browser D1) |
| D | Proxy 002 liveness/readiness (returns 200/503) | Blandet · Tilgjengelighet / oppetid. «no authentication is required» is Funksjonell |
| E | PersonAdapter Kode 6 (not forwarded, logged, alert) | Funksjonell |
| F | SkoleService Kode 6/7 (does not appear, not counted) | Funksjonell |
| G | A functional AC linked to an FR whose text mentions WCAG | Funksjonell |
| H | Frontend 002 US2 #5 («OBS! …» in a visually distinct style) | Blandet |
| I | «it works as expected» / no Then | Uavklart («Trenger avklaring») |

**Reviewer decisions:**
- **«Bekreft forslaget» / «Lagre vurdering»** in the drawer records the decision with the scenario's content fingerprint. The type, qualities and reason are stored in the `story_groupings` document, append-only and with history.
- **When the scenario text changes**, the decision is not applied silently: «må gjøres på nytt».
- **Independence:** test kinds, cases and plans are never changed.

**Labels:** «Universell utforming» (accessibility) and «Tilgjengelighet / oppetid» (availability) are always distinct.

## F. Finn og forstå

- **Every user story card** (the selected story, the story overview cards and the rail) shows:
  - the task status badge;
  - «8 av 9 oppgaver fullført», a thin progress bar and «89 %»;
  - «N oppgaver gjenstår»;
  - the confirmation state;
  - **«Akseptansescenarioer: 5 funksjonelle · 1 blandet»** (the distribution of its own ACs, each counted once).

  This is shown whatever the dashboard settings are. With no task evidence it shows «Oppgavestatus er ikke tilgjengelig.»: no bar, no 0 %, no «?». «Kilde: linje ?» is now «linjenummer er ikke registrert».
- **«Se statusgrunnlag»** (accordion, closed by default) shows two sections:
  - **Implementeringsstatus:** explanation, counts, evidence quality, source version, confirmation, remaining tasks, completed tasks (collapsed), superseded tasks, dependencies (confirmed / related / unresolved, explained), shared tasks, notes and the source;
  - **Scenarioklassifisering:** distribution, quality attributes and an explanation.
- **Rows:** one short label («Ikke-funksjonell · Universell utforming»; functional rows need none). The purple AC badge is unchanged.
- **Drawer:** a «Scenariotype» section with the type, qualities, «Foreslått fra kildegrunnlag» / «Bekreftet av gjennomgang» / «Trenger avklaring», the explanation, the evidence and the review form.
- **Grouping:** «Brukerhistorier» stays the default; «Scenariotype» comes next, with headers and counts.
  - A mixed scenario is listed once, under «Blandede».
  - Then come «Krav uten akseptansescenario» and «Andre forslag».
- **Filters** (in «Flere filtre», combined with search, priority and the rest):
  - «Scenariotype» (exact type);
  - «Implementeringsstatus».
- **Status loading:** the story status is loaded once per selected specification in the background. A newer selection makes an older answer irrelevant. Failures show «Prøv igjen».
- **Drawer defect fixed:** the drawer started at the top of the window, under the layout's sticky Activity row, which covered «Lukk». It now starts below that row (found by the isolated walkthrough).

## G. Testoversikt and System Settings

All of these are optional and off by default; they are switched in System Settings → Manual Testing, using the existing registry and storage.

| Group | Parts | Counted as |
|---|---|---|
| Figures | «US under utvikling», «US med uferdige oppgaver», «US-oppgaver fullført», «US-status må avklares», «Funksjonelle scenarioer», «Ikke-funksjonelle scenarioer» | Unique stories and unique acceptance scenarios; a mixed scenario only as mixed |
| Charts | «Implementeringsframdrift for brukerhistorier» | Mutually exclusive status bars, every story once |
| | «Scenarioer etter type» | |
| | «Ikke-funksjonelle scenarioer per kategori» | Overlap explained |
| | «Manglende dekning etter type» | |

**Scope and drill-down:**
- Every part follows Project → Module → Specification.
- A story-status bar lists its stories. «Åpne i Finn og forstå» chooses the specification and filters on that status.
- Type and category bars open their exact scenarios.

**Settings:** existing settings are not changed (only explicit choices are stored).

**Not built:** «Implementeringsframdrift» is not a stacked chart per module; it is the status distribution of the scope.

## H. Tests

- **Backend:**
  - `UserStoryImplementationEngineTests` (13, real M2LB excerpts through the real import extraction);
  - `ScenarioTypeClassifierTests` (25 cases, verbatim M2LB scenarios);
  - `UserStoryStatusServiceTests` (2, temporary database: read writes nothing; the confirmation is kept, with history; «Må bekreftes på nytt» after a new import; no confirmation without task evidence or without a person).
  - With Implementation Gaps, story grouping and manual review: **149/149**, then 2/2.
- **Frontend:** **9,750 passed / 0 failed** (full suite). New:
  - `DiscoverStoryStatusAndTypeTests` (6);
  - two Testoversikt tests (unique counts, mixed counted once, scope, unavailable is «—», story drill-down).

## I. Isolated browser walkthrough

The isolated copy had its own database and App_Data, with M2LB.zip imported (116 s).

**First run:**
- U1 (241 ms), U1b, U1c, U1d, U2, U3, U4, T1, T2, T3, D1 and D2 passed. Their results are in sections D and F above.
- T1: Frontend 002 grouped as FUNKSJONELLE (15), BLANDEDE (2), KRAV UTEN AKSEPTANSESCENARIO (19), ANDRE FORSLAG (91).
- The run then stopped on the drawer's «Lukk», which was covered by the Activity row (fixed, see F).

**Second run** (the drawer fix applied to the isolated copy's stylesheet):
- U1–U4, T1–T3, D1 and D2 pass again, and closing the drawer with «Lukk» works.
- The run then **timed out waiting for Proxy 002's scenario list** (10 minutes).

**NOT VERIFIED in the browser:**
- P1/P2: Proxy health checks; covered by the backend tests in E;
- O1–O4: Testoversikt figures, chart and story drill-down; covered by the frontend tests;
- R1: the 390 px layout;
- Q/R: the console and API checks for this run.

They will be run in the next isolated walkthrough.

## J. Remaining limitations

- **Not built:**
  - **Cross-story dependencies stated in spec.md** («US2 depends on US1»): not read; only task lines are.
  - **Dependency wording such as «blocks T022»:** recorded only as related work, not as a reverse dependency.
- **Partly built:**
  - **Implementeringsframdrift per module:** shown as the scope's status distribution, not stacked per module.
- **Behaviour of existing data:**
  - **Older imports:** their `[~]` tasks show «importer på nytt» until the project is imported again.
- **Not verified:**
  - **SkoleService US8 and TjenesteService US4:** not checked in the browser.
- **Open observation:**
  - **«Anbefalt å forberede» queue:** on a second visit to Frontend 002 it showed 0 (91 «Trenger avklaring»), while the first visit found rows. Not investigated; this change does not touch the work queues.
