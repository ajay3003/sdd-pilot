# SDD cross-artifact traceability audit (2026-11-25)

Read-only audit. Nothing was built, run, tested or changed. Real data: `C:\Users\ajaan\Downloads\M2LB (8)\` (untrusted input, read as data).
BirkNext line numbers are as read on 2026-11-25 **while another session was editing the same files**; `SpecExplorerService.cs` changed
during the audit (it grew from 1961 to 2056 lines). Where that matters, the text says "originally" and "working tree now".

Legend for relationship types used throughout:

- **A – direct**: the same artifact names both ends with an exact, local identifier (story/scenario text says `FR-009`).
- **B – cross-artifact**: another artifact of the same feature names both ends (a `[US3]` task line naming `FR-024`; a phase Goal; a quickstart row; a contract cited by a tagged task; an upstream reference doc cited by both ends).
- **C – behavioural suggestion**: wording overlap only. Never evidence. Always shown as a suggestion and always needs a person.

A task is **not** implementation proof and a test task is **not** test proof. Both only say "the plan intended this work for this story".

---

## A. Conventions actually used

| Convention | Source evidence | Observed in M2LB |
|---|---|---|
| `### User Story N - Title (Priority: Pn)` | `.specify/templates/spec-template.md` ("### User Story 1 - [Brief Title] (Priority: P1)") | 35 of 39 specs. Em dash variant in Person (`### User Story 1 — Search for Children (Priority: P1)`, PersonService/specs/001-person-module/spec.md:40). Norwegian titles but English heading in Tjeneste 001. 4 specs have no story headings (Frontend 001/006, HendelseService 001, RevisjonService 002). |
| `**Why this priority**`, `**Independent Test**`, `**Acceptance Scenarios**:` | spec-template.md | Present in every story of the 5 focus features. |
| Numbered `1. **Given** … **When** … **Then** …` scenarios, no scenario id | spec-template.md | Everywhere. Scenarios carry **no id**. Some scenarios contain two Given/Then pairs (SkoleService US2 sc 5, spec.md:134-138). |
| `### Functional Requirements` with `- **FR-001**: System MUST …` | spec-template.md | SkoleService, SkoleAdapter, MR, Tjeneste use list form. Person 001 uses **paragraph** form `**FR-001**: …` (spec.md:268). Lettered sub-requirements `FR-008a`, `FR-040e` are a project convention (template shows only `FR-001`). |
| Bold-only area lines inside Functional Requirements (`**Intake and identity**`) | **not in template** | SkoleService (11 areas, spec.md:554-888), SkoleAdapter (7, spec.md:276-406), Tjeneste 001 (6), MR 001 (7). Person 001 has **none**. |
| `**SC-001**` under `### Measurable Outcomes` | spec-template.md | Everywhere with SC. |
| `## Dependencies & Open Items` table, `## Out of Scope`, `## Context` | **not in template** (template has only `## Assumptions`) | SkoleService spec.md:22, 1038, 1056. The dependency table's "Blocks" column names stories and FRs (spec.md:1042-1050). |
| Story → FR mapping in spec | **no template slot** | Not written as a mapping anywhere. FR ids appear only incidentally inside scenarios (4 places in SkoleService). |
| Task line `- [ ] T012 [P] [US1] Description with path` | tasks-template.md "Format: `[ID] [P?] [Story] Description`"; BirkNext `.claude/skills/speckit-tasks/SKILL.md:140-168` ("[Story] label: REQUIRED for user story phase tasks only") | Everywhere. SkoleService tasks.md:19 restates it: "[USn] maps a task to its story" (tasks.md:913). |
| `## Phase N: User Story K - Title (Priority: Pn)` + `**Goal**:` + `**Independent Test**:` | tasks-template.md | Everywhere. SkoleService orders phases by dependency, not number (Phase 5 = US7, Phase 6 = US6, Phase 7 = US3; tasks.md:354, 448, 514). |
| Story dependencies | tasks-template.md "### User Story Dependencies" | SkoleService has an explicit graph (tasks.md:836-843) and per-phase prose (tasks.md:824-833). |
| FR ids inside tasks | **no template rule**; the tasks skill never asks for FR ids | Ad hoc: in task text, in parentheses, in indented sub-bullets and `*As built: …*` notes. Ranges written as "FR-014 to FR-016" (tasks.md:300), "FR-040b to FR-040d" (tasks.md:405). |
| Scenario references in tasks | **no template rule** | SkoleAdapter and MR: `US1-1, US1-2` (SkoleAdapter tasks.md:131; 30 and 47 hits). SkoleService: "scenario 11", "US2 scenarios 2–3" (tasks.md:259, 738; 34 hits) — ambiguous between acceptance and quickstart scenarios. |
| `per FR-003` / `US1/AC2` source refs | `speckit-converge` skill (M2LB `.claude/skills/speckit-converge/SKILL.md:223`, `author: github-spec-kit`) | Convergence phase exists (SkoleService tasks.md:920) but its tasks do **not** use `per <source-ref>`; FR ids are inline. |
| Requirement ↔ task coverage | `speckit-analyze` SKILL.md:117: "Map each task to one or more requirements or stories (inference by keyword / explicit reference patterns…)" | Spec Kit itself relies on inference here — it does not guarantee explicit links. |
| External id namespaces | docs/analysis/skolenaervaer-reference.md:48 ("ID prefixes: `DEC-` … `CA-` SkoleAdapter, `UT-` Utdanning context, `SR-` Skolenærværsrapport context … User story IDs (`US-A1` …) are kept from [C]") | SkoleService spec.md:19-20 states bracketed `[SR-FR-013]`/`[DEC-25]` point to that document. |

Spec Kit versions: M2LB `.specify/init-options.json` `speckit_version 1.0.13`; BirkNext `.specify/init-options.json` `0.7.6.dev0`. The spec templates differ only in whitespace.

## B. Artifact inventory

All 39 spec.md folders (Y = present). Counts: `US` story headings, `FR` FR definitions, `SC`, `AS` `**Given**` lines, `T` task lines, `T[US]` task lines with a `[USn]` tag.

| Feature folder | plan | tasks | research | data-model | quickstart | contracts/ | checklists/ | US | FR | SC | AS | T | T[US] |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| AutorisasjonService/001-translate-source-english | Y | Y | Y | - | Y | - | Y | 3 | 9 | 7 | 9 | 49 | 43 |
| AutorisasjonService/004-scim-user-sync | Y | Y | Y | Y | Y | Y | Y | 4 | 22 | 6 | 12 | 38 | 19 |
| AutorisasjonService/005-translate-adr-029 | Y | Y | Y | Y | Y | Y | Y | 4 | 22 | 8 | 19 | 89 | 59 |
| CdcReplay/001-translate-adr-029 | Y | Y | Y | Y | Y | Y | Y | 3 | 11 | 7 | 7 | 29 | 20 |
| Frontend/001-initial-dashboard | Y | Y | Y | Y | Y | Y | Y | 0 | 11 | 7 | 0 | 27 | 13 |
| Frontend/002-child-search-profile | Y | Y | Y | Y | Y | Y | Y | 3 | 19 | 6 | 17 | 50 | 26 |
| Frontend/003-designer-polish | Y | Y | Y | Y | Y | - | Y | 5 | 13 | 8 | 15 | 27 | 19 |
| Frontend/004-settings-devmode | Y | - | - | - | - | - | - | 2 | 0 | 0 | 8 | – | – |
| Frontend/005-access-admin-panel | Y | Y | Y | Y | Y | Y | Y | 7 | 43 | 10 | 45 | 54 | 31 |
| Frontend/006-admin-module-alignment | Y | - | - | - | - | - | - | 0 | 0 | 0 | 0 | – | – |
| Frontend/007-translate-adr-029 | Y | Y | Y | Y | Y | Y | Y | 4 | 18 | 7 | 16 | 45 | 34 |
| HendelseAdapter/001-birk-hendelse-adapter | Y | Y | Y | Y | Y | Y | Y | 5 | 18 | 7 | 18 | 70 | 42 |
| HendelseAdapter/002-testcontainers-wolverine | Y | Y | Y | Y | Y | Y | Y | 3 | 14 | 6 | 11 | 27 | 20 |
| HendelseAdapter/003-multi-entity-debezium | Y | Y | Y | Y | - | - | Y | 4 | 12 | 5 | 15 | 34 | 20 |
| HendelseAdapter/004-translate-adr-029 | Y | Y | Y | Y | Y | Y | Y | 4 | 28 | 10 | 17 | 99 | 62 |
| HendelseService/001-hendelsestjenesten | Y | Y | Y | Y | Y | Y | Y | 0 | 0 | 0 | 0 | 48 | 26 |
| HendelseService/002-fix-romning-refdata | Y | Y | Y | Y | - | Y | Y | 3 | 7 | 6 | 8 | 29 | 12 |
| HendelseService/003-translate-adr-029 | Y | Y | Y | Y | Y | Y | Y | 4 | 38 | 13 | 23 | 84 | 72 |
| **MeldingsutvekslerReceiver/001-meldingsutveksler-receiver** | Y | Y | Y | Y | Y | Y (5 + `avhengigheter/`) | Y (2) | 5 | 41 | 8 | 40 | 138 | 55 |
| PersonAdapter/001-birk-person-adapter | Y | Y | Y | Y | Y | Y | Y | 5 | 22 | 9 | 21 | 58 | 38 |
| PersonAdapter/002-batch-cdc-self-heal | Y | Y | Y | Y | Y | Y | Y | 3 | 11 | 4 | 8 | 23 | 17 |
| **PersonService/001-person-module** | Y | Y | Y | Y | Y | Y (3) | Y | 6 | 33 (paragraph form) | 0 bullets | 33 | 64 | 28 |
| PersonService/002-barn-parent-feilkode | Y | Y | Y | Y | Y | Y | Y | 2 | 7 | 3 | 5 | 12 | 8 |
| PersonService/003-translate-adr-029 | Y | Y | Y | Y | Y | Y (4) | Y | 5 | 25 | 8 | 18 | 95 | 66 |
| Proxy/001-proxy-initial-setup | Y | Y | Y | Y | Y | Y | Y | 3 | 9 | 5 | 8 | 29 | 17 |
| Proxy/002-adr026-health-checks | Y | Y | Y | Y | Y | Y | Y | 3 | 10 | 6 | 11 | 12 | 8 |
| RevisjonService/001-revisjonstjenesten-m01 | Y | Y | Y | Y | Y | Y | Y | 5 | 14 | 8 | 14 | 43 | 31 |
| RevisjonService/002-translate-adr-029 | - | Y | - | - | - | Y | - | 0 | 6 | 0 | 0 | 20 | 0 |
| **SkoleAdapter/001-skoleadapter** | Y | Y | Y | Y | Y | Y (3) | Y | 5 | 27 | 9 | 23 | 105 | 41 |
| **SkoleService/001-skoleservice** | Y | Y | Y | Y | Y | Y (7 + `avhengigheter/` 2) | Y (2) | 8 | 65 | 11 | 65 numbered (72 `**Given**`) | 175 | 95 |
| **TjenesteService/001-tjenestemodul-m01** | Y | Y | Y | Y | Y | Y (2) | Y | 5 | 31 | 10 | 20 | 58 | 28 |
| TjenesteService/002-birk-cdc-alignment | Y | Y | Y | Y | Y | Y | Y | 4 | 12 | 6 | 13 | 43 | 33 |
| TjenesteService/003-domain-sync-self-heal | Y | Y | Y | Y | Y | - | Y | 3 | 8 | 4 | 7 | 19 | 8 |
| TjenesteService/004-child-placements-period | Y | Y | Y | Y | Y | Y | Y | 4 | 16 | 6 | 27 | 84 | 55 |
| TjenesteService/005-translate-source-english | Y | Y | Y | - | Y | - | Y | 5 | 19 | 7 | 17 | 53 | 41 |
| specs/001-platform-contract-rename | Y | Y | Y | Y | Y | Y | Y | 6 | 22 | 8 | 20 | 157 | 119 |
| specs/002-operation-display-name | Y | Y | Y | Y | Y | Y | Y | 5 | 22 | 8 | 15 | 180 | 104 |
| specs/003-birkid-partial-search | Y | Y | Y | Y | Y | Y | Y | 3 | 8 | 4 | 12 | 25 | 15 |
| specs/003-cross-service-contract-translation | Y | Y | Y | Y | Y | Y | Y | 6 | 22 | 8 | 16 | 125 | 111 |

Notes:
- `SkoleService/specs/002-translate-source-english/` exists **without spec.md** (only `domain-term-inventory.md`, `name-map-review.md`, `wire-changes.md`). Its work is tracked as Phase 13 of 001's tasks.md (tasks.md:937-951).
- Spec Kit constitutions: root `.specify/memory/constitution.md` plus a module constitution in 12 modules (SkoleService, SkoleAdapter, TjenesteService included; their `.specify` has `memory/` only). Templates exist only at the root `.specify/templates/`.
- SkoleService artifacts that name FR ids (distinct ids): tasks 37, research 45, data-model 38, quickstart 13, plan 7, contracts 2–14 each. Only tasks.md, plan.md, quickstart.md and checklists/quickstart-validering.md name stories. research.md and data-model.md name **no** story.

## C. Current BirkNext interpretation architecture (file-specific)

| Step | File | What it does with stories/FRs |
|---|---|---|
| Spec parse | `frontend/BirkNext.Web/Services/SpecExplorerService.cs` `Parse` | Builds a `SpecTree`. Numbered Given/When/Then items become `BddScenario` nodes **without `SpecItemId`** (inline flush ~line 247-257; block flush ~318-328). |
| Semantic model | `SpecExplorerService.BuildSemanticModel` (originally :1390; now shifted) → `frontend/BirkNext.Web/Models/SpecificationSemanticModel.cs` | UserStories, Requirements, SuccessCriteria, AcceptanceScenarios, Clarifications, EdgeCases, Assumptions, SecurityConsiderations, KeyEntities. Working tree now also adds `ExtractBoundaries` (Dependencies & Open Items, Out of Scope, :1484-1525) — being added concurrently. |
| Story → FR | `LinkUserStoriesToRequirements` | **Originally** (:1781-1794): `CanLinkByReference(us.Description, r.Id) \|\| CanLinkSemantically(us.Title, r.Text)`. `CanLinkSemantically` = ≥2 shared tokens incl. stop words; the working-tree comment (:1869) records "linked SkoleService US1 to 58 of 65 requirements". **Working tree now** (:1865-1879): explicit references only, over Title+Description+Why+IndependentTest. Scenarios are not part of that text. |
| Scenario → FR | `LinkRequirementsToAcceptanceScenarios` | **Originally** (:1822-1833): only `requirement text contains scenario.Id`, and `s.Id` is always null for numbered scenarios → **no SkoleService scenario ever got a requirement**. **Working tree now** (:1907-1921): also `scenario Title/Given/When/Then names req.Id`. |
| Id matching | `CanLinkByReference` | **Originally** (:1883-1888): hyphen-stripped `Contains` → `FR-008` matched `FR-008a`, `FR-013` matched `SR-FR-013`, `FR-001` matched `UT-FR-001`. **Working tree now** (:1975-1983): whole-id regex with `(?<![\w-])` and `(?![A-Za-z0-9])`. |
| Still keyword-based | `LinkSecurityToRequirements` (:1945-1956) | Uses `CanLinkSemantically` (2 shared tokens) → `SemanticSecurity.AffectedRequirementIds` / `req.LinkedSecurityConsiderations` are behavioural, not marked as suggestions. |
| Evidence (browser) | `frontend/BirkNext.Web/Services/TestCoveragePresentation.cs:18-115` | `CoverageUserStory.NamedRequirements` = FR ids in Title+Description+Why+IndependentTest (:34, :60-62). `TaskRequirements` = tagged task lines via `TaskExplorerService` (:68-83) + `StoryTaskContext` + plan.md/quickstart.md lines naming the story (:93-115, Kind = null). Scenario `RequirementIds` = `s.LinkedRequirements` (:42). |
| Evidence (API) | `backend/BirkNext.Api/Services/Specifications/SpecificationEvidenceBuilder.cs:24-92` | Same mapping; tasks.md via `StoryTaskContext.Read(..., taskLines: true)` (:82); plan/quickstart lines with `StoryTaskRequirement.TaskLine` kind (:88). |
| Task parse | `frontend/BirkNext.Web/Services/TaskExplorerService.cs` | `UserStoryTagRe` `\[US(\d+(?:[–\-]\d+)?)\]` (:26-27); `FrRefRe` `(?<![\w-])(FR)-?(\d{1,4})([a-z])?\b` (:30-31); `ReferencedFrIds` from the task body (:472-473). Indented sub-bullets are not tasks. |
| FR areas | `shared/RequirementSections.cs` `RequirementSections.Read` (:62-123) | Bold-only line or sub-heading under the requirements heading = area; `### Key Entities` ends the region (:88-90); `SR-` definitions skipped (:116). |
| Phase goal / task details | `shared/RequirementSections.cs` `StoryTaskContext.Read` (:159-196) | Goal line inside the story's phase → `phase-goal`; indented lines under a tagged task (sub-bullets, *As built*) → `task-detail`. Ranges not expanded. |
| Grouping | `shared/StoryGrouping.cs` `StoryGroupingEngine.Relations/Profiles` (:224-379) | Basis order SourceExplicit < HumanConfirmed < ScenarioExplicit < Structural < Suggested (:134-146). `SupportingOnly` = all evidence task-detail (:160). Deciding = established, not cross-cutting, not supporting-only, known area (:262). Cross-cutting areas by breadth (:270-277). |
| Grouping inputs | `frontend/BirkNext.Web/Services/StoryGroupingPresentation.cs:32-55` | ScenarioLinks = scenario `RequirementIds` (:46); Suggested = `ScenarioPlanning.SuggestRequirements` over story text, Structural+Semantic levels, `maxSemantic: 3` (:48-51). |
| Suggestion engine | `shared/ScenarioCentricPlanning.cs:310-345` `ScenarioPlanning.SuggestRequirements` | Levels Explicit / Structural / Semantic / Weak (:90); id regex `(?<![\w-])[A-Z]{1,6}-\d+[a-z]?\b`; scoped by `scopePath` so another spec's FR-001 never matches. This is the one place where behavioural similarity belongs. |
| Legacy traceability view | `frontend/BirkNext.Web/Services/TraceabilityModelBuilder.cs` | **Hard-coded** FR-number → story ranges (`InferUserStoryId`, :379-398: "1..7 → US1, 8..12 → US2 …", source "deterministic FR range") and hard-coded FR → SC links (`AddDeterministicScLinks`, :401-410: SC-008 ← FR-001..033). Used by `Components/TraceabilityView.razor:508`, rendered only from `Components/ExtractionReviewList.razor:499`, which no page renders (only tests). Latent, but invented relationships. |
| Other id regexes | `TraceabilityModelBuilder.cs:9` and `SpecExplorerService.cs:90` `\bFR-\d{3,4}\b`; `backend/BirkNext.Api/Services/TraceabilitySuggestionService.cs:42` `\b(FR\|SC\|US\|TC\|TEST\|TASK\|T)[-]?\s*(\d+)\b` | Match inside `UT-FR-001`/`SR-FR-013`; the first two never match suffixed ids (`FR-008a`). |
| Canonical interpretation | `backend/BirkNext.Api/Services/CanonicalInterpretation/*` | Reads stored Source Analysis / Delivery snapshots (CanonicalInterpretationService.cs:11-49, `WorkItemInterpreter.Classify`). It does not read spec.md story ↔ FR relations; not on the story-grouping path. |

## D. Source identity and namespace analysis

| Id form | Meaning | Evidence | Risk |
|---|---|---|---|
| `FR-001` in SkoleService | local requirement | spec.md:556 | Collides with `FR-001` of every other feature (Person spec.md:268, SkoleAdapter, …). Identity must be (spec path, id). `ScenarioPlanning.SuggestRequirements` already scopes by `scopePath`; `CoverageUserStory.KeyOf(specPath, …)` scopes stories. |
| `US1` / "User Story 1" | local story | spec.md:50; tasks `[US1]` | Same collision across features. `StoryTaskContext`/`DocumentRequirements` only read files in the spec's own folder (TestCoveragePresentation.cs:73, :104). |
| `US-B1`, `US-C1`, `US-D4` … | stories of the upstream user-story doc [C] | spec.md:56, 109, 316, 375; reference.md:48, 520-526 | Not local stories. `TryExtractUserStoryId`/`UsIdRe \bUS\d+\b` do not match them (good). |
| `SR-FR-013`, `UT-FR-001..005`, `CA-FR-005` | upstream requirements of the reference doc | spec.md:19-20, 316; SkoleAdapter spec.md:15 | Before today's guards they matched local `FR-013`/`FR-001`. Range `UT-FR-001..005` is not expandable locally. |
| `FR-038..FR-040e` in SkoleAdapter | **SkoleService's** FRs, unprefixed | SkoleAdapter spec.md:17 "`SkoleService/specs/001-skoleservice` (FR-038..FR-040e)" | An unprefixed foreign FR. SkoleAdapter defines only FR-001..FR-024, so FR-038 does not resolve locally — it must be reported as "foreign/unresolved", never linked. |
| SkoleService clarifications "(from the SkoleAdapter clarification session)" | answers imported from another spec | spec.md:525-534 | FR ids in those answers are SkoleService's (they were re-written into this spec). |
| `FR-041 (a)`, `FR-026a (b)` | sub-clause of a requirement | spec.md:305; tasks.md:329 | Clause letters are not separate ids; `FR-026a (b)` must stay FR-026A. |
| `DEP-07`, `OQ-03`, `DEC-122`, `EXC-05`, `PS-06` | decisions/dependencies | spec.md:1042-1052 | Not requirements. `RequirementSections.Definition` accepts only FR/NFR/REQ/SR-FR (SR- skipped). |
| `T071` | local task | tasks.md:343 | Unique per tasks.md only. |
| "scenario 11", "US2 scenarios 2–3", `US1-1` | scenario references | SkoleService tasks.md:259, 738; SkoleAdapter tasks.md:131 | No scenario ids exist in spec.md; references are positional (story + number). "quickstart scenario N" is a different numbering (quickstart.md:72-83). |

## E. US ↔ FR evidence correlation (SkoleService)

Facts (verified):
- Story text (title, description, why, independent test) names **no** local FR in any of US1–US8. US7's `UT-FR-001..005` (spec.md:316) is upstream.
- Acceptance scenarios name local FRs in exactly four places: US3 sc 4 `FR-009` (spec.md:177), US6 sc 10 `FR-041 (b)` (spec.md:305), US7 sc 4 `FR-039` (spec.md:336), US8 sc 3 `FR-009` (spec.md:393). Three of them are cross-references ("unless the counting option in FR-009 is on", "appears on the clean-up list (FR-041 (b))"), not a statement that the story implements the FR.
- Scenarios name other stories: US2 sc 8 "(User Story 4)" (spec.md:146), US3 sc 1 "(User Story 2)" (spec.md:170), US4 sc 2 "as in User Stories 1 and 2" (spec.md:210). Story → story, not story → FR.
- Edge cases name FRs and once a story: spec.md:436-439 "(User Story 4) … (FR-040c)". FR-040c belongs to Education master data (US7's area); co-mention is not a link.
- Dependencies table names both: DEP-07 "User Story 6, FR-027" (spec.md:1046), DEP-08 "User Story 7, FR-038" (:1049), DEP-09 "User Story 2, FR-009a, SC-004" (:1050), DEP-06 "User Stories 4, 7; FR-040c" (:1048). This is "blocked by the same dependency", a weak B relation — context, not traceability.
- Upstream provenance chain (B, two hops, cross-document). Each SkoleService story cites upstream stories (`[US-B4]`); each SkoleService FR cites upstream requirements (`[SR-FR-007]`); the reference doc tags upstream requirements with the story they came from (`[C] US-B4`):

| SkoleService story (tag line) | Upstream story | Upstream requirement (reference.md line) | SkoleService FR citing it (spec.md line) |
|---|---|---|---|
| US1 (:56) | US-B1 | SR-FR-002 (:566), SR-FR-004 (:568) | FR-002 (:564), FR-004 (:569) |
| US2 (:109) | US-B4 | SR-FR-007 (:575), SR-FR-008 (:577), SR-FR-009 (:580) | FR-008 (:601), FR-009 (:627) |
| US5 (:225) | US-B5 | SR-FR-023 (:609) | FR-022 (:711) |
| US7 (:316, cites UT-FR-001..005 directly) | US-C1 | UT-FR-001 (:507), UT-FR-004 (:510) | FR-038 (:823) |
| US3, US4, US6, US8 | US-D2/E1/E3, US-B6, US-D1/D3, US-D4 | no tagged upstream requirement | — |

Only 7 of 59 `SR-FR` lines carry a `[C] US-…` tag, so the chain is sparse. It is still the only **source-authored** evidence that ties US2 to its Classification requirements.

## F. Task-to-story analysis

Three different relations — kept apart:

1. **Task belongs to story** — `[USn]` tag (framework requirement for story-phase tasks). Strong.
2. **Task references FR** — FR id in task text (project convention). Means "this task's work concerns FR-x", not "FR-x is implemented".
3. **Shared component / cross-story work** — a task in story X that builds or verifies something of story Y. Explicit in SkoleService prose:
   - tasks.md:337 US2 Goal: "Processing is US1's pipeline, so this story is mostly tests; enforcement on read is built in US8 (T146) and withholding at lock in US3."
   - tasks.md:339 US2 Independent Test: "The read scenarios (2, 3) are verified in US8 (T141). The lock scenarios (4, 5) are verified in US3 (T106), and scenario 6 in US4 (T123)."
   - tasks.md:738 `T141 [P] [US8] Authorisation scenario tests (SC-004, US2 scenarios 2–3)`.
   - tasks.md:452 US6 Independent Test: "The end-to-end check through a real lock is in US3 (T106)."
   - tasks.md:274 `T057 [P] [US1]` tests FR-022 (current-version rule of Corrections, US5's area).

SkoleService per-story FR mentions in tasks.md (tagged = on the `- [X] Tnnn [USn]` line; detail = indented sub-bullet or *As built*; goal = phase Goal line):

| Story | Tagged task lines | Phase goal | Task details | Not attributed (correctly) |
|---|---|---|---|---|
| US1 | T057 FR-022 (:274), T059 FR-018 (:283), T062 FR-007 (:299), T063 FR-014, FR-015c, FR-016 (:300), T070 FR-026a (:329) | — | T055 FR-011 (:265), T064 FR-022 (:305), T068 FR-013 (:323), FR-004 (:327) | FR-045b in phase prose (:248) |
| US2 | none (T071 :343, T072 :344 name no FR) | FR-008a (:337) | — | — |
| US3 | T108 FR-008a, FR-028 (:558), T110 FR-024, FR-026 (:569), T112 FR-019 (:574), T116 FR-008a, FR-028 (:600) | — | T107 FR-026 (:553), FR-025 (:555), T115 FR-024, FR-026 (:597), T120 FR-035 (:617), T122 FR-037 (:627) | — |
| US4 | T132 FR-026a (:681) — a metrics-gauge task | — | T123 FR-046 (:646), T128 FR-007 (:674 As built), T131 FR-006, FR-009 (:680) | — |
| US5 | T135 FR-022, FR-045b (:708) | — | T134 FR-006 (:707), T138 FR-021 (:718), T139 FR-020 (:722 As built) | — |
| US6 | T094 FR-027a (:461) | — | T096 FR-009 (:476), T099 FR-027 (:484) | — |
| US7 | T077 FR-039 (:392), T078 FR-040c (:394), T082 FR-040b, FR-040d (:405, written "FR-040b to FR-040d") | — | T085 FR-040c (:419) | — |
| US8 | T144 FR-008a (:762) | — (Goal :730 names none) | T141 FR-008a (:743), T142 FR-011 (:756 As built), T146 FR-011 (:774 As built) | Convergence T165 FR-041 (:929, untagged) |

Coverage gap: tasks.md names only 37 of 65 FRs at all. FR-041/FR-042 (Follow-up lists) are never named by a US8 task. FR-032..FR-036 (Delivery) only in US3 details. Ranges hide ids: "FR-014 to FR-016" (:300) implies FR-015/FR-015a/FR-015b, "FR-040b to FR-040d" (:405) implies FR-040c — BirkNext extracts only the endpoints.

Cross-feature examples:
- Person 001: story text names no FR; tagged tasks do — US1 `T033` FR-031 (tasks.md:108), `T034` FR-004/005/007 (:109), `T035` FR-006 (:110); US3 `T044` FR-015/016/028 (:145). Goals name no FR (tasks.md:97, 121, 138, 155, 170). FRs are paragraphs and have no area lines → every FR is "Ukjent funksjonsområde" in grouping; functional mode is not available for Person.
- SkoleAdapter 001: story text names no FR; US2 `T046` FR-020/021 (tasks.md:155), `T047` FR-010/019 (:159); US5 tasks name **no** FR (tasks.md:230-237). Tasks reference scenarios explicitly: `T038 [US1] … quickstart scenario 1 (US1-1, US1-2, US1-3 …)` (tasks.md:131) — a test task → acceptance scenario link that BirkNext does not parse.
- Tjeneste 001 and MR 001: same pattern (tagged tasks carry FRs; MR uses `USn-k` 47 times).

## G. Contracts and data-model evidence

- Contracts cite FRs, never stories (SkoleService): `rapportinntak.md` FR-003/006/009/010/011/012/013/020/021/045/045a; `rapportinntak.openapi.yaml` 14 FRs; `utdanning-innmating.md` FR-003/017/018/038/039/040a–e; `bufdata-leveranse.md` FR-008b/009/015/015a–c/016/019/029/030/033–036; `graphql-skole.graphql` FR-008a/009/027/027a/029/041 (FR-041 (a) :22, (b) :31); `hendelser.md` FR-009/035/037/047; `drift.md` FR-023a/046; `avhengigheter/person-maskinoppslag.md` FR-002/008a/024a/028/038/041; `avhengigheter/tjeneste-plasseringer.md` FR-027/027a.
- Story ↔ contract exists only through tagged tasks that name the contract file: `[US1]` → `rapportinntak.openapi.yaml` (tasks.md:318); `[US3]` → `bufdata-leveranse.md` (:613), `drift.md` (:601); `[US8]` → `graphql-skole.graphql` (T140, :736). So US8 → T140 → graphql contract → FR-041 is a real B chain (2 hops) — the only source path from US8 to the Follow-up lists area. Contracts cite many FRs, so this chain must be supporting-only and reviewed.
- data-model.md names 38 FRs and no story; research.md 45 FRs and no story. Useful for "requirement has design evidence", not for story grouping.
- SkoleAdapter `contracts/skoleservice-utdanning-mapping.md:4` declares `SkoleService/specs/001-skoleservice/contracts/utdanning-innmating.md` the authoritative target — a cross-module contract dependency.

## H. Source precedence and historical naming

- 22 spec.md files carry the banner "Superseded in part 2026-11-09 (cross-service contract translation)" (e.g. SkoleService spec.md:3): Norwegian exchanged names in these folders "describe the state before that feature and are kept as history. The current names are in the name map (`specs/003-cross-service-contract-translation/contracts/name-map/`)". It lists the files that quote old names (spec, plan, tasks, research, data-model, quickstart, `contracts/hendelser.md`, `contracts/utdanning-innmating.md`, …).
- Evidence of drift inside one feature: spec says `SkolenaervaerPeriodClosed` (spec.md:175, 811); tasks.md says `SkolenaervaersperiodeLukket` 6 times (:516, 543, 545, 627, 629, 815); `SkoleService/specs/002-translate-source-english/wire-changes.md:76` records `SkolenaervaersperiodeLukket` as the old event type; name map `skole.md:41` (SKO-021) lists `SkolenaervaerPeriodClosed` as current. `contracts/hendelser.md` still shows the Norwegian envelope (`hendelsesId`, `korrelasjonId`, :7-10); `person.md:92` maps `BarnRegistrert` → `BarnRegistered`, while SkoleService spec.md:349 still says `BarnRegistrert`.
- Supersession inside FR text: Person FR-001 "*Superseded for name and BirkID search … see specs/003-birkid-partial-search*" (Person spec.md:268-272).
- Module rename: spec.md:11-13 "called Skoletjenesten until 2026-10-20".
- Precedence rule to adopt (BirkNext recommendation, based on the banners): for **names**, the name map wins over a feature folder; for **requirement ids and story ids**, the feature's own spec.md is authoritative and is not renamed. Name drift must never break an id link (ids are stable; names are not), and must never create one (matching on `BarnRegistrert` vs `BarnRegistered` is behavioural at best).

## I. SkoleService US1–US8 relationship matrix

Status columns: **Current** = what BirkNext produces today as read (working tree, uncommitted, mid-edit); **Proposed** = after section L. Strength: High / Medium / Low / Suggestion.

| User Story | Acceptance Scenario | FR | Evidence Artifact | Evidence Location | Type | Strength | Current BirkNext Status | Proposed Status | Human Review |
|---|---|---|---|---|---|---|---|---|---|
| US1 | — | FR-007 | tasks.md T062 [US1] | tasks.md:299 | B | High | Structural (task) | Structural (task) | No |
| US1 | — | FR-014, FR-015c, FR-016 | tasks.md T063 [US1] | tasks.md:300 | B | High | Structural (task) | Structural (task); FR-015/015a/015b only as "range-implied" suggestion | Range: yes |
| US1 | — | FR-018 | tasks.md T059 [US1]; quickstart row 3 | tasks.md:283; quickstart.md:74 | B | High | Structural (task + quickstart, kind null) | Structural (task) + quickstart-row (supporting) | No |
| US1 | — | FR-022 | tasks.md T057 [US1] (test of version rule) | tasks.md:274 | B | Medium (other story's area) | Structural, deciding | Structural; mark cross-area | Yes |
| US1 | — | FR-026a | tasks.md T070 [US1] (metric) | tasks.md:329 | B | Low | Structural, deciding | Structural, deciding (unchanged) | Yes |
| US1 | — | FR-011, FR-013, FR-004, FR-022 | task details T055/T068/T064 | tasks.md:265, 323, 327, 305 | B | Low | task-detail (supporting) | unchanged | No |
| US1 | — | FR-002, FR-004 | [US-B1] → SR-FR-002/004 → FR-002/004 | spec.md:56; reference.md:566, 568; spec.md:564, 569 | B (2-hop, external doc) | Medium | not read | upstream-provenance (supporting) | Yes |
| US1 | — | FR-001..FR-017 | wording overlap | — | C | Suggestion | Suggested (≤3 semantic) | Suggested | Yes |
| US2 | — | FR-008a | tasks.md Phase 4 Goal | tasks.md:337 | B | Medium-High | Structural (phase-goal), deciding | unchanged | No |
| US2 | sc 2, 3 | — (SC-004) | tasks.md T141 [US8] "US2 scenarios 2–3" | tasks.md:738, 339 | B (scenario → task of other story) | High for test intent | not read | scenario-task reference | No |
| US2 | sc 4, 5 / sc 6 | — | tasks.md "verified in US3 (T106) … US4 (T123)" | tasks.md:339 | B | High for test intent | not read | scenario-task reference | No |
| US2 | sc 8 | — (User Story 4) | spec scenario text | spec.md:144-146 | A (story→story) | High | not read | story reference (context) | No |
| US2 | — | FR-008, FR-009 | [US-B4] → SR-FR-007/008/009 | spec.md:109; reference.md:575, 577, 580; spec.md:601, 627 | B (2-hop, external doc) | Medium | not read | upstream-provenance (supporting) | Yes |
| US2 | — | FR-009a (+SC-004) | Dependencies DEP-09 "User Story 2, FR-009a" | spec.md:1050 | B (shared blocker) | Low | being added (`SemanticDependency.RequirementIds`, no story ids) | dependency context only | Yes |
| US2 | — | FR-008, 008a, 008b, 009, 009a, FR-042 | wording ("Kode 6/7", "gradering") | — | C | Suggestion | Suggested | Suggested | Yes |
| US3 | sc 4 | FR-009 | spec scenario text "unless the counting option in FR-009 is on" | spec.md:177 | A | Medium (conditional reference) | **originally none**; working tree: ScenarioExplicit | ScenarioExplicit, flagged "reference in scenario" | Yes |
| US3 | — | FR-024, FR-026 | T110 [US3] | tasks.md:569 | B | High | Structural | Structural | No |
| US3 | — | FR-008a, FR-028 | T108, T116 [US3] | tasks.md:558, 600 | B | Medium | Structural (FR-008a in cross-cutting area) | unchanged | No |
| US3 | — | FR-019 | T112 [US3] | tasks.md:574 | B | Medium | Structural | Structural | No |
| US3 | — | FR-035 | plan.md "Coverage (US6) … (FR-035), so it ships with US3"; detail T120 | plan.md:290; tasks.md:617 | B | Low (plan co-mention) | **plan line treated as task line (deciding)** | plan-mention (supporting) | Yes |
| US3 | — | FR-025, FR-026, FR-037 | details T107/T115/T122 | tasks.md:553, 555, 597, 627 | B | Low | task-detail | unchanged | No |
| US3 | — | FR-032..FR-037 | contract `bufdata-leveranse.md` via `[US3]` task | tasks.md:613; contract | B (2-hop) | Low | not read | contract-mediated (supporting) | Yes |
| US4 | — | FR-026a | T132 [US4] gauge | tasks.md:681 | B | Low | Structural, the only deciding link → "Trenger gjennomgang" | unchanged (stays review) | Yes |
| US4 | — | FR-006, FR-009, FR-007, FR-046 | details T131/T128/T123 | tasks.md:680, 674, 646 | B | Low | task-detail | unchanged | No |
| US4 | — | FR-040c | edge case co-mention "(User Story 4) … (FR-040c)" | spec.md:436-439 | — | **None** (wrong if used) | not read | must stay unlinked | — |
| US4 | — | FR-006, FR-045b | wording ("held", "unknown child") | — | C | Suggestion | Suggested | Suggested | Yes |
| US5 | — | FR-022, FR-045b | T135 [US5] | tasks.md:708 | B | Medium | Structural (tie → «Flere mulige grupper») | unchanged | Yes |
| US5 | — | FR-022 | [US-B5] → SR-FR-023 | spec.md:225; reference.md:609; spec.md:711 | B (2-hop) | Medium | not read | upstream-provenance (supporting) | Yes |
| US5 | — | FR-006, FR-020, FR-021 | details T134/T139/T138 | tasks.md:707, 722, 718 | B | Low | task-detail | unchanged | No |
| US6 | sc 10 | FR-041 | spec "clean-up list (FR-041 (b))" | spec.md:305 | A | Low as primary (points to US8's area) | **originally none**; working tree: ScenarioExplicit (deciding) | ScenarioExplicit, but "reference in scenario" must not decide alone | Yes |
| US6 | — | FR-027a | T094 [US6] | tasks.md:461 | B | High | Structural | Structural | No |
| US6 | — | FR-027, FR-009 | details T099/T096 | tasks.md:484, 476 | B | Low | task-detail | unchanged | No |
| US6 | — | FR-035 | plan.md co-mention | plan.md:290 | B | Low | treated as task line | plan-mention (supporting) | Yes |
| US6 | — | FR-027 | DEP-07 "User Story 6, FR-027" | spec.md:1046 | B (shared blocker) | Low | being added (no story ids) | dependency context | Yes |
| US7 | sc 4 | FR-039 | spec scenario "(FR-039)" | spec.md:336 | A | High | **originally none**; working tree: ScenarioExplicit | ScenarioExplicit | No |
| US7 | — | FR-039, FR-040c, FR-040b, FR-040d | T077, T078, T082 [US7] | tasks.md:392, 394, 405 | B | High | Structural | Structural (+FR-040c range-implied) | No |
| US7 | — | FR-038 | US7 cites UT-FR-001..005; FR-038 cites UT-FR-001/004; DEP-08 "User Story 7, FR-038" | spec.md:316, 823, 1049 | B | Medium | not read | upstream-provenance (supporting) | Yes |
| US8 | sc 3 | FR-009 | spec "whatever the counting option in FR-009" | spec.md:393 | A | Low as primary (cross-cutting control) | **originally none**; working tree: ScenarioExplicit (Classification = cross-cutting area) | ScenarioExplicit, cross-cutting | Yes |
| US8 | — | FR-008a | T144 [US8]; detail T141 | tasks.md:762, 743 | B | Medium | Structural (cross-cutting area) | unchanged | No |
| US8 | — | FR-011 | As built notes T142/T146 | tasks.md:756, 774 | B | Low | task-detail | unchanged | No |
| US8 | — | FR-041 | T140 [US8] → `graphql-skole.graphql` → FR-041 (a)/(b) | tasks.md:736; graphql-skole.graphql:22, 31 | B (2-hop) | Medium | not read | contract-mediated (supporting) | Yes |
| US8 | — | FR-041, FR-042 | wording ("follow-up list", "clean-up list") | — | C | Suggestion | Suggested | Suggested | Yes |

Cross-feature rows:

| Story | Scenario | FR | Artifact | Location | Type | Strength | Current | Proposed | Review |
|---|---|---|---|---|---|---|---|---|---|
| Person US1 | — | FR-004, FR-005, FR-006, FR-007, FR-031 | tasks.md T033–T035 [US1] | Person tasks.md:108-110 | B | High | Structural; area "Ukjent" (no FR areas) | unchanged; grouping mode = stories | No |
| Person US3 | — | FR-015, FR-016, FR-028 | T044 [US3] | Person tasks.md:145 | B | High | Structural, "Ukjent" area | unchanged | No |
| SkoleAdapter US1 | US1-1..US1-3 | — | T038 [US1] "quickstart scenario 1 (US1-1, US1-2, US1-3 …)" | SkoleAdapter tasks.md:131 | B (test task → scenario) | High | not read | scenario-task reference | No |
| SkoleAdapter US2 | — | FR-010, FR-019, FR-020, FR-021 | T046, T047 [US2] | SkoleAdapter tasks.md:155, 159 | B | High | Structural | Structural | No |
| SkoleAdapter (header) | — | SkoleService FR-038..FR-040e | spec header | SkoleAdapter spec.md:17 | foreign | — | ignored (ids not defined locally) | report as foreign reference | No |

## J. Incorrect or ambiguous existing associations

1. **Keyword story → FR links** (fixed in working tree): original `LinkUserStoriesToRequirements` used `CanLinkSemantically(us.Title, r.Text)` (2 shared tokens incl. "the"/"a"); the new comment records 58 of 65 FRs for US1. Consumers that read `LinkedUserStories`/`LinkedRequirements` were affected: `ReviewContext.cs:59-134, 271-273`, `ImplementationReview.razor:325-327`, `SddEvidenceGraphService.cs:247`, `SpecExplorerPanel.razor:825, 866`, `SpecificationSemanticModel.cs:42, 44`. Any stored or snapshotted data built from them (e.g. evidence fingerprints) is suspect.
2. **Substring id matching** (fixed in working tree): `FR-008` ⊂ `FR-008a`, `FR-013` ⊂ `SR-FR-013`, `FR-001` ⊂ `UT-FR-001`. It also hit `LinkClarifications` and `LinkEdgeCasesToRequirements`.
3. **Still keyword-based**: `LinkSecurityToRequirements` (SpecExplorerService.cs:1951) fills `req.LinkedSecurityConsiderations` from 2 shared tokens, with no "suggestion" marker.
4. **Hard-coded relations**: `TraceabilityModelBuilder.InferUserStoryId` (:379-398) and `AddDeterministicScLinks` (:401-410) apply one old sample's FR→US and FR→SC numbering to any spec. For SkoleService it would put FR-013 (Validity) under "US3", FR-017..019 under "US4", FR-029..031 under "Cross-cutting / Platform", and link SC-008 to FR-001..033. Not rendered by any page today, but tested and one `<TraceabilityView>` away from being visible.
5. **Plan/quickstart co-mentions treated as task lines**: `DocumentRequirements` gives them `Kind = null` (browser, TestCoveragePresentation.cs:111) or `TaskLine` (API, SpecificationEvidenceBuilder.cs:88); `StoryGrouping.cs:239` maps null to `TaskLine`, so plan.md:290 makes FR-035 **deciding** for both US3 and US6. A plan sentence that names two stories and one FR is not a task assignment.
6. **Scenario cross-references treated as ownership** (new with the working-tree fix): US6 sc 10 `(FR-041 (b))` and US3/US8 `FR-009` become `ScenarioExplicit`, which outranks every task line (`StoryLinkBasis` order). US6 would get a deciding Follow-up-lists link from a parenthetical pointer.
7. **Ranges**: "FR-014 to FR-016", "FR-040b to FR-040d", "UT-FR-001..005", "FR-038..FR-040e" — only endpoints are extracted; middle ids are silently missing (no false link, but false absence).
8. **API vs browser drift**: the API passes `taskLines: true` to `StoryTaskContext` (SpecificationEvidenceBuilder.cs:82); the browser reads tagged lines through `TaskExplorerService` and dedups by `(RequirementId, TaskId)` (TestCoveragePresentation.cs:81). Same facts, two code paths; counts can differ for a task line naming one FR twice or across lines.
9. **Other regexes still unguarded**: `TraceabilityModelBuilder.cs:9`, `SpecExplorerService.cs:90` (`\bFR-\d{3,4}\b`) and `TraceabilitySuggestionService.cs:42` match inside `UT-FR-001`/`SR-FR-013`; the first two never match `FR-008a`.
10. **Ambiguous scenario numbers**: "scenario 11" (tasks.md:259) = US1 acceptance scenario 11; "quickstart scenarios 1–3" (:246) = quickstart table rows. Both say "scenario N"; resolution needs the word "quickstart" or a story context.

## K. Impact on functional grouping

- Primary business group ≠ every FR area a story touches. The source never states a story's primary area; BirkNext derives it by counting deciding FRs per area (StoryGrouping.cs:310-363). That is a BirkNext rule, not a Spec Kit rule.
- **Why US2 is weak**: by design. tasks.md:337 says US2 is "mostly tests", built in US1/US3/US8. Its only own-artifact FR is the phase Goal's FR-008a (Classification), and Classification is cross-cutting (US1/US2/US3/US8). The real relations exist but sit elsewhere: the upstream chain US-B4 → FR-008/FR-009 (section E), the T141/T106/T123 scenario verifications (tasks.md:339, 738), and DEP-09 (spec.md:1050). Expected result: «Trenger gjennomgang» with Classification as the one candidate — correct behaviour; a person confirms.
- **Why US8 is weak/wrong**: none of US8's own artifacts names its natural area (Follow-up lists, FR-041/FR-042). Its evidence points to Classification (FR-008a tagged, FR-009 scenario — cross-cutting) and Validity (FR-011, As built only). The only path to FR-041 is US8 → T140 → `graphql-skole.graphql` → FR-041 (2 hops). Expected result: no deciding area; candidates Classification/Validity; a person picks Follow-up lists. A behavioural suggestion for FR-041/042 is appropriate and must stay a suggestion.
- **US4**: single deciding link FR-026a from a metrics task (T132) while details touch Intake (FR-006/007) → «Trenger gjennomgang» (StoryGrouping.cs:351-356). Correct.
- **US5**: FR-022 (Corrections) vs FR-045b (Access, audit) tie → «Flere mulige grupper». The upstream chain US-B5 → FR-022 would break the tie toward Corrections if it were allowed to count — it should only be shown as supporting.
- **US6**: with the working-tree scenario fix it gets FR-027a (Expected population, task), FR-041 (Follow-up, scenario-explicit) and FR-035 (Delivery, plan co-mention) — one each, three-way tie, although the source clearly places US6 in "Expected population and coverage". Items 5 and 6 of section J cause this.
- **US1**: Quality control (FR-014, FR-015c, FR-016) beats Intake (FR-007) 3:1 though the story's title is about intake. A reviewer may prefer Intake; the count rule is a suggestion, so a person decides.
- **US3, US7**: clear (Period lock; Education master data).

## L. Proposed minimal architecture changes (do not implement here)

Reuse existing parsers and the existing `StoryLinkBasis`/`StoryTaskRequirement.Kind` model. No new parser, graph, AI or vector store.

1. **`shared/TestCoverageContracts.cs` (`StoryTaskRequirement`, :410-412)** — add kind constants `PlanMention = "plan-mention"`, `QuickstartRow = "quickstart-row"`, `ScenarioReference = "scenario-ref"`, `UpstreamProvenance = "upstream"`, `ContractMediated = "contract"`. Strings only; no schema change.
2. **`frontend/.../TestCoveragePresentation.cs:111` and `backend/.../SpecificationEvidenceBuilder.cs:88`** — set `PlanMention`/`QuickstartRow` instead of null/TaskLine. Move the shared plan/quickstart loop into `shared/RequirementSections.cs` (next to `StoryTaskContext`) so browser and API call one function (removes the duplicated "browser's own rules" copy).
3. **`shared/StoryGrouping.cs`** — extend `SupportingOnly` (:160) to "all evidence kinds are supporting" where supporting = {task-detail, plan-mention, quickstart-row, upstream, contract}. Keep `Established` unchanged so these still show as related areas, never as the primary one.
4. **Scenario references are not ownership** — in `StoryGroupingPresentation.cs:46` (and the API grouping service), pass the scenario links as before but let `Relations` mark a scenario-only relation whose FR sits in an area different from all of the story's task-line areas as supporting (flag `ReferenceOnly`). Smallest variant: treat `ScenarioExplicit` like `Structural` for deciding (same count weight), not as an outranking basis. Needs a product decision (O-2).
5. **Ranges** — in `StoryTaskContext.Read` and `TaskExplorerService` FR extraction, recognise `FR-x to FR-y` / `FR-x..FR-y` only when both ends exist in the same spec's `RequirementSectionMap` and the middle ids are contiguous in definition order; emit middle ids with kind `range-implied` (supporting). Never expand foreign-prefixed ranges.
6. **Scenario-task references** — in `StoryTaskContext.Read`, additionally read `USn-k` (SkoleAdapter/MR) and "US<n> scenario(s) <a>[–<b>]" (SkoleService tasks.md:738) inside tagged task lines/details → `(StoryN, scenario k, TaskId)`. Bare "scenario N" only when the line has no "quickstart" word and the task is tagged; otherwise ignore. This is test-intent evidence for scenarios, not FR evidence.
7. **Upstream provenance (optional, opt-in)** — when the spec's governing-documents line names a reference doc that is present in the import (SkoleService spec.md:17-20), read its `- **SR-FR-…**: … [C] US-B4` lines with the same `RequirementSections` definition regex (`SR-FR` is already recognised at :53) and join: story tag `[US-B4]` ↔ local FR citing `[SR-FR-007]`. Emit `UpstreamProvenance` (supporting). Never across modules, never without both citations.
8. **Dependencies table** (being added in `ExtractBoundaries`, SpecExplorerService.cs:1488-1525) — also parse story ids from the Blocks cell ("User Stories 1, 2, 4, 6, 8") into `SemanticDependency.StoryIds`. Show as preparation context; do not feed grouping.
9. **Contract-mediated (optional)** — a tagged task naming a `contracts/<file>` of the same feature (tasks.md:736) + that contract's FR ids → `ContractMediated` (supporting). Cap: only FRs whose area already appears among the story's own evidence, else list as "contract mentions" without linking.
10. **Remove or quarantine invented relations** — delete `InferUserStoryId`'s numeric switch and `AddDeterministicScLinks` from `TraceabilityModelBuilder.cs` (keep only the `UsIdRe` "context heading" branch), or gate the component behind a feature flag that can never reach a real spec. Replace `\bFR-\d{3,4}\b` at TraceabilityModelBuilder.cs:9 / SpecExplorerService.cs:90 and the `IdPattern` at TraceabilitySuggestionService.cs:42 with the shared guarded grammar (`(?<![\w-])FR-?\d{1,4}[a-z]?\b`, already in TaskExplorerService.cs:30).
11. **`LinkSecurityToRequirements`** — switch to `CanLinkByReference`; if behavioural matching is wanted there, route it through `ScenarioPlanning.SuggestRequirements` and label it a suggestion.
12. **Foreign ids** — `RequirementSections`/evidence builders: an FR id named in a spec but not defined by it (SkoleAdapter spec.md:17 FR-038) → report "not defined in this specification" (already the `Find` = null path in `RequirementSectionMap.Find`, :27-31); never auto-resolve to another spec.

## M. Suggested regression tests

All with real excerpts (fixtures already referenced via `RequirementSectionsTests.Spec("SkoleService/specs/001-skoleservice/spec.md")`):

1. `BuildSemanticModel` on SkoleService: every story's `LinkedRequirements` is empty (no local FR in story text); `UT-FR-001..005` in US7 links nothing.
2. Scenario links: exactly US3#4→FR-009, US6#10→FR-041, US7#4→FR-039, US8#3→FR-009; no other scenario has a requirement; `FR-041 (b)` normalises to FR-041.
3. `CanLinkByReference`: FR-008 ∉ "FR-008a"; FR-013 ∉ "[SR-FR-013]"; FR-001 ∉ "UT-FR-001"; FR-026A ∈ "FR-026a (b)"; FR-008 ∈ "FR008".
4. `StoryTaskContext.Read` US2: one `phase-goal` FR-008A at tasks.md:337; no US1 relation from "Processing is US1's".
5. Plan line plan.md:290 yields `plan-mention` for US3 and US6 and is `SupportingOnly`.
6. Grouping on the real fixture: US7 → Education master data (Suggested); US3 → Period lock; US2 → NeedsReview with Classification candidate; US8 → no Follow-up area unless a person confirms; US6 → Expected population once J5/J6 are addressed.
7. Ranges: T063 (:300) yields FR-015, FR-015A, FR-015B as `range-implied` only; "UT-FR-001..005" yields nothing.
8. Scenario-task refs: SkoleAdapter T038 (:131) → US1 scenarios 1–3; SkoleService T141 (:738) → US2 scenarios 2–3 for task T141 tagged US8; "quickstart scenarios 1–3" (:246) → none.
9. Namespace: SkoleAdapter spec with "FR-038..FR-040e" (:17) → no relation, one "not defined here" note.
10. Person 001: paragraph `**FR-001**:` definitions read (33 FRs), zero areas → default grouping mode is Stories (`StoryGroupingPresentation.DefaultMode`).
11. `TraceabilityModelBuilder` (if kept): building from SkoleService produces **no** "deterministic FR range" story ids and no SC-008→FR-001..033 links.
12. API/browser parity: `SpecificationEvidenceBuilder.Build` and `TestCoveragePresentation.WorkspaceEvidence` give identical `TaskRequirements` (ids, kinds, lines) for the same folder.

## N. Safe integration order with the ongoing structural-compliance work

Your files in flight: `shared/StoryGrouping.cs`, `shared/RequirementSections.cs`, `SpecificationEvidenceBuilder.cs`, `TestCoveragePresentation.cs`, `TaskExplorerService.cs`, Discover grouping UI, and (observed during this audit) `SpecExplorerService.cs` linking + `ExtractBoundaries`.

1. Finish and commit the current set first (guarded regexes, explicit-only story links, scenario-text links, `StoryTaskContext`, boundaries). Run M1–M4 against it.
2. Then L1 + L2 (kinds for plan/quickstart, one shared function) — touches the same two builders you just changed; do it right after, in one commit, so the two builders do not drift again.
3. Then L3 + L4 (`StoryGrouping.cs` supporting/ReferenceOnly) — after O-2 is decided; re-run M6 and the grouping fixture `story-grouping-report.md`.
4. Then L10/L11/L12 (independent files: `TraceabilityModelBuilder.cs`, `TraceabilitySuggestionService.cs`, `SpecExplorerService.LinkSecurityToRequirements`). Can go in parallel with 3; no shared state.
5. Then L5/L6 (ranges, scenario-task refs) in `StoryTaskContext` — additive.
6. Last, optional L7/L8/L9 (upstream provenance, dependency story ids, contract-mediated) behind a visible "supporting" label.
Each step changes evidence fingerprints (`SpecificationEvidenceBuilder.SemanticFingerprint`, :121-126, includes scenario RequirementIds) → expect stored discovery evidence to be rebuilt on next import; mention it in the commit.

## O. Open questions and limitations

1. I did not run BirkNext. The 58/65 figure comes from your working-tree comment (SpecExplorerService.cs:1869); the "current status" column is from code reading of files that changed during the audit.
2. Should a scenario's parenthetical FR reference decide a primary area (J6/L4)? Spec Kit says nothing; this is a BirkNext policy choice.
3. Is the upstream reference doc in scope for BirkNext imports? It lives at `docs/analysis/` outside the feature folder; only 7/59 SR-FR lines carry a story tag.
4. "scenario N" in SkoleService tasks is ambiguous (acceptance vs quickstart); only story-qualified forms are safe.
5. Person 001 has no FR areas; functional grouping cannot work there without a person.
6. Translate features (`*-translate-adr-029`, `*-translate-source-english`) have stories about renaming, not business behaviour; their FRs must not be mixed with the business feature's FRs of the same module.
7. `SkoleService/specs/002-translate-source-english/` has no spec.md; BirkNext's role classifier will not see it as a specification (expected).
8. Spec Kit version mismatch (BirkNext 0.7.6.dev0 vs M2LB 1.0.13) — templates equivalent for these conventions; newer Spec Kit features (converge) are present in M2LB only.

## P. Framework requirement vs project convention vs BirkNext recommendation

| Item | Classification | Evidence |
|---|---|---|
| Story heading `### User Story N - Title (Priority: Pn)` | FRAMEWORK REQUIREMENT (template) | spec-template.md |
| Why this priority / Independent Test / Acceptance Scenarios | FRAMEWORK REQUIREMENT (template, "mandatory" section) | spec-template.md |
| Given/When/Then numbered scenarios | FRAMEWORK REQUIREMENT (template format) | spec-template.md |
| Scenario ids | Not in framework; none in M2LB | — |
| `FR-###`, `SC-###` ids | FRAMEWORK REQUIREMENT (template) | spec-template.md; speckit-analyze SKILL.md:115 |
| Lettered sub-ids (FR-008a) | PROJECT CONVENTION | SkoleService, SkoleAdapter, MR, Tjeneste |
| FR area bold lines | PROJECT CONVENTION | SkoleService spec.md:554 etc.; not in template |
| Story → FR mapping in spec | Neither; not defined by framework or project | — |
| `T###` ids, checkbox, `[P]` | FRAMEWORK REQUIREMENT | tasks-template.md; speckit-tasks SKILL.md:140-168 |
| `[USn]` on story-phase tasks | FRAMEWORK REQUIREMENT ("REQUIRED for user story phase tasks only") | speckit-tasks SKILL.md:151-156 |
| Phase per story with Goal + Independent Test | FRAMEWORK REQUIREMENT (template) | tasks-template.md |
| Story dependency section | FRAMEWORK (template section; content optional) | tasks-template.md "User Story Dependencies" |
| FR ids in task text | PROJECT CONVENTION (ad hoc) | SkoleService tasks.md; not required by template |
| `*As built:*` notes | PROJECT CONVENTION | SkoleService tasks.md:674, 722, 756, 774 |
| `USn-k` scenario refs in tasks | PROJECT CONVENTION (SkoleAdapter, MR) | SkoleAdapter tasks.md:131 |
| `per FR-003` / `US1/AC2` in convergence tasks | FRAMEWORK (converge command template), not used in practice | M2LB speckit-converge SKILL.md:223 |
| Task ↔ requirement coverage by keyword inference | FRAMEWORK behaviour of `/speckit-analyze` (advisory) | speckit-analyze SKILL.md:117 |
| Dependencies & Open Items table, Context, Out of Scope | PROJECT CONVENTION | SkoleService spec.md:22, 1038, 1056 |
| External id namespaces (SR-, UT-, US-B…) | PROJECT CONVENTION, documented | reference.md:48 |
| "Superseded in part" banners + name map precedence | PROJECT CONVENTION | spec.md:3 (22 specs) |
| Identity = (spec path, id) | BIRKNEXT RECOMMENDATION | section D |
| Basis order and "suggestion never decides" | BIRKNEXT RECOMMENDATION (already in StoryGrouping.cs) | StoryGrouping.cs:134-155 |
| Plan/quickstart/details/upstream/contract = supporting only | BIRKNEXT RECOMMENDATION | section L |
| Scenario parenthetical FR = reference, not ownership | BIRKNEXT RECOMMENDATION (needs decision O-2) | section J6 |
| No hard-coded FR→US/SC relations | BIRKNEXT RECOMMENDATION | section J4 |
| Behavioural similarity only via `ScenarioPlanning.SuggestRequirements`, always "Suggested" | BIRKNEXT RECOMMENDATION | ScenarioCentricPlanning.cs:310-345 |

## Final verification (2026-11-25, 09:00–09:20 local)

This audit was read-only. The section records what happened after it: the reviewer decisions for US4, US5 and US8 from section J/K (FV7), and the verification of the code that came out of the follow-up tasks.

All steps below ran after the user's go-ahead. The app was not running when this verification started, so nothing was stopped. Nothing was reset, no database was dropped, both Git stashes and all saved plans are untouched, and nothing was pushed.

### FV1. Full backend regression (isolated run database, memory sampled)

| Run | Result | Duration | Peak working set / min free RAM | Leftover processes |
|---|---|---|---|---|
| final3 (08:30, committed code) | 7,431 passed / **1 failed** (`CI_DEF_3`) / 19 skipped | 7 m 39 s | 6.6 GB / 2.07 GB | 0 |
| final4 (09:00, current tree) | **7,432 passed / 0 failed** / 19 skipped | 6 m 44 s | 7.3 GB / 3.24 GB | 0 |

`CI_DEF_3_the_connect_callback_lets_an_allowed_host_through`:

- In final3 it took 29.4 s and returned Unreachable, because the fetcher's 20 s timeout fired.
- In final4 it passed but took **9.3 s**; run alone it takes milliseconds.
- Both runs show heavy GC pressure (187–233 s of total pause, wake-ups up to 3.6–4.8 s late).

The pattern fits thread-pool/GC starvation of the in-test loopback server under full-suite load, not a policy or production defect. This is **not proven**. The test assertion now reports the fetcher's problem text, the elapsed time, whether the loopback server answered, thread-pool counts and the default proxy, so the next failure names its cause. The assertion itself is unchanged in strength. **This is not a claim that CI_DEF_3 is fixed.** It passed in one of the last two full runs.

### FV2. Full frontend regression

**9,639 passed / 0 failed.** This includes 7 new tests for:

- AC titles;
- the story shown in Norwegian, untranslated and stale states;
- «Sammenlign» showing every story field in both languages;
- the detail title.

### FV3. The 07:55:20 FullIncludingProtected reset

- **What happened:** `POST /api/admin/data-reset` (correlation `48705cc1…`, mode `FullIncludingProtected`) arrived through a browser session of the running app, after that session opened System settings and loaded the reset plan.
- **What it removed:** 416 rows across 88 main tables and the dependent schemas (canonical_interpretation, delivery_reporting, integration_journeys, scope_registry, snapshot_registry, task_alignment).
- **What it kept:** test plans and revisions; the fingerprints of the original plans are identical before and after.
- **Afterwards:** a new project was imported (`import:import-a5c083c81e63c1a2`), and plan `369a1a8b…` was created at 08:08:09 through the app.
- **Who initiated it is not established.** The request shows only that it came through the app's own UI session. Nothing in the logs identifies a person, and none is inferred.

### FV4. Temporary test databases (read-only inventory; nothing dropped)

The inventory found **368 databases, 6.6 GB in total**:

| Kind | Count | Size |
|---|---|---|
| Random-suffix test databases | 230 | 2.1 GB |
| Dated acceptance databases | 118 | 4.0 GB |
| Other | 20 | 0.6 GB |

- None has an open connection.
- None carries the ownership marker that today's isolation writes (`birknext_testrun_*` plus a comment), so none can be proven test-only.

Recommended criteria for a cleanup, which runs only after your confirmation:

- The name matches a known test pattern exactly.
- There are no connections.
- The database is not `birknext` and not a template.
- The database is older than the newest run.
- Each one is listed and confirmed before it is dropped.

New runs drop their own marked database, and the full runs above left none behind.

### FV5. The 57 committed files

The 57 files in commit `e4d6c16d` were reviewed file by file, and every one is intentional. Seven of them had their line endings flipped from LF to CRLF on commit. Commit `2b3af1a4` restored LF in those seven with no content change. The repository uses `core.autocrlf=false`, and each file keeps its own line endings.

### FV6. Browser walkthrough with disposable records (`scratchpad/grp_walk3.py`)

- **Setup:** the real app (signed `dotnet run`, API :5000, frontend :5173), project `import:import-a5c083c81e63c1a2`, SkoleService `spec.md`.
- **Safety gate:** before running, the script checked that no grouping row existed for the specification, and it refuses to run if one exists.
- **What it writes:** only grouping metadata. Every group it creates is named `TEMP gjennomgang …` and deleted by the script itself.
- **Before:** plan fingerprint `e20566b3…`, zero grouping rows.
- **After:** the same fingerprint, and zero rows after cleanup.

**Result: 42 checks, 41 PASS, 1 FAIL.**

- **The failure (F45, "no console errors"):** the browser logs the two *deliberate* negative tests as console errors: a duplicate name refused with 400 and a stale revision refused with 409. F46 already excludes those two expected responses and passes. No other console errors occurred.
- **Drill-down (G01–G07):** groups → stories → AC, with a breadcrumb and «‹ group» back link. «Alle scenarioer (218)» stays reachable.
- **Titles (G04, E42):** the detail title and the Planlegg heading are «AC n — …», never «US… —».
- **Level colours (G05):** group = blue `rgb(239,246,255)`, user story = green `rgb(240,253,244)`, scenario/AC = purple `rgb(237,233,254)`.
- **Grouping (B11–B21, T-US3/US6/US8):** every story shows a status and its source-based reasons, and incidental references (FR-009, FR-041) stay visible with a role without deciding the primary area.
- **Disposable round trip (C22–C30):** create, rename, duplicate-name refusal, reload persistence, move, rationale, leave ungrouped, delete. US6 was used because it needs review and is not one of the reviewer decisions below.
- **Server failure paths (D31–D35):** a server-side 400 with its own message, a 409 on a stale revision, and a retry stored once.
- **Testability (E36–E43):** an ungrouped story's scenario shows Given/When/Then and its requirements, and can be prepared. Planlegg opens and shows the story card and the Norsk/Original/Sammenlign controls.
- **Layout (F47/F48):** keyboard focus moves; there is no horizontal scroll at 900, 1280 or 1920 px.

**Timings (ms):** initial workspace load 22,606 (discovery of 655 scenarios), open group 52, open story 173, select a story 382–426, search 857, a grouping save 115–991, open Planlegg 2,726.

**Earlier attempts.** Two earlier attempts stopped on problems in the script, not the product:

- US2 no longer needs review.
- The organizer's name is a DOM property, not an attribute.
- The script's direct API calls lacked the `X-BirkNext-Client` header, which the API correctly refused with 403.

Each attempt's own row (`b7407133…`, `0b0db545…`, and the final run's `dcbc8f25…`) was deleted by id, project and specification together. Nothing else was touched.

### FV7. Reviewer decisions applied (US4, US5, US8)

These were applied through the story card's existing reviewer UI («Endre plassering» and «Administrer kravkoblinger»), with a preview before each save. No parser rule and no direct database write was involved. The result is stored in grouping row `48db6a24…`, revision 5, with 7 history entries by `local-user`.

| Story | Decision | Result after reload |
|---|---|---|
| US4 | Primary area «Intake and identity» | Gruppert, «Intake and identity (bekreftet av en person)». The Period lock link FR-026a is unchanged, so Period lock stays a related area. |
| US5 | Primary area «Corrections, chains and idempotency» | Gruppert, confirmed by a person. All six links are unchanged. |
| US8 | Primary area «Follow-up lists»; FR-041 and FR-042 confirmed; the Classification links FR-009 and FR-008a given the role «Støttende» | Gruppert, «Follow-up lists (bekreftet av en person)». FR-041/042 changed from Suggested to HumanConfirmed. FR-009 (ScenarioExplicit) and FR-008a (Structural) keep their source basis and now have the role Supporting. |

All original source relationships are kept: no link was removed or replaced, and roles never change a link or its evidence. Plan fingerprints are unchanged.

### FV8. Manual testing UI changes in the same verification

- **Story card:** the user story card in Finn og forstå and Planlegg is a prominent light-green card with its details one click away.
- **Story language:** the story follows the editor language. Norsk shows the plan's stored translation, Original shows the source, and Sammenlign shows title, description, «Hvorfor denne prioriteten» and «Uavhengig test» side by side. Untranslated and stale states are labelled.
- **Scenario titles:** a scenario is named «AC n — …», never «US1 — …».
- **Drill-down:** groups → user stories → AC, with one colour per level.
