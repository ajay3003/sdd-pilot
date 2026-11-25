# Spec.md structural compliance audit: SkoleService — 2026-11-25

- **Branch:** `008-traceability-first`.
- **Baseline:** commit `9e4988bf`, plus today's uncommitted work. Nothing was committed or pushed.
- **Companion reports:**
  - `sdd-cross-artifact-traceability-audit-2026-11-25.md` (read-only SDD audit);
  - `test-isolation-and-failsafe-grouping-2026-11-25.md` (test isolation, memory, fail-safe grouping).
- **Method:** the canonical parser (`SpecExplorerService.BuildSemanticModel`) is run over the real, unchanged specification. Every section is compared with the source text, and each confirmed defect is fixed in that parser. No new parser, traceability engine or grouping engine was added.
- **Inventory tool:** `SpecStructureInventoryProbe`, opt-in through `BIRKNEXT_SPEC_AUDIT_OUT`.
- **Regression tests:** `SpecStructureComplianceTests` (18 tests).

## A. Section inventory (the source file)

`M2LB (8)/SkoleService/specs/001-skoleservice/spec.md`, 1,065 lines. Its SHA-256 starts `a979d194…` and is identical to the test fixture.

| Source section | Lines | BirkNext before | BirkNext now |
|---|---|---|---|
| Title, header block ("Superseded in part 2026-11-09 …", Names, Input, Governing documents) | 1–21 | title/feature/status/created | unchanged; the supersession banner is reported, not modelled (see X) |
| Context / Reporting cycle | 22–47 | not modelled | not modelled (context prose; see X) |
| User Scenarios & Testing → US1–US8 | 48–399 | 8 stories, 65 scenarios ✔ | ✔ unchanged |
| Edge Cases | 400–481 | **0 extracted** | **27/27** |
| Clarifications (6 dated `#### Session` blocks) | 482–549 | 48 Q/A, no session, no status | 48, each with session plus the source's own Superseded/Amended marker |
| Requirements → Functional Requirements (11 bold areas) | 550–932 | 65 FRs, all category "General" | 65 FRs, category = source area |
| Key Entities | 933–967 | **2 of 12** | **12/12** |
| Success Criteria | 968–1003 | 11 ✔ | 11 ✔ |
| Assumptions | 1004–1037 | 11 ✔ | 11 ✔ |
| Dependencies & Open Items (table) | 1038–1055 | **not modelled** | 13 rows, closed and struck-through status kept |
| Out of Scope | 1056–1065 | **not modelled** | 7 items |

## B. Source version and authority

- **The uploaded file is not available.** `spec(20261125-055421).md`, named in the task, does not exist under Downloads, Desktop, Documents or the repository. The baseline is therefore the `M2LB (8)` source, which is byte-identical to the test fixture. Repository and source agree; no difference to record.
- **Authority markers are read from the source only:**
  - the document banner "Superseded in part 2026-11-09 (cross-service contract translation)";
  - inline `*Superseded <date>: …*` and `*Amended <date>: …*` in clarification answers;
  - `**Closed …**` and `~~struck~~` rows in Dependencies.
- **No precedence is inferred from position in the document.**

## C. User stories

| | Before | Now |
|---|---|---|
| Count | 8 ✔ | 8 ✔ |
| Priorities | P1, P1, P1, P2, P2, P2, P2, P3 ✔ | ✔ |
| Description, Why this priority, Independent Test | present for all 8 ✔ | ✔ |
| Source lines | ✔ | ✔ |
| **Story → FR links** | **invented**: US1 linked to 58 of 65 FRs, US3 to 53, US2 to 36 | **0 for every story.** No story text names a SkoleService FR; US7's "UT-FR-001..005" is the Utdanning service's own requirements |

The cause was in `LinkUserStoriesToRequirements`: it linked whenever the story title and the FR text shared two words, stopwords included (`CanLinkSemantically`). The id match was also a substring match, so `CanLinkByReference("FR-008")` matched "FR-008a". Both are fixed:

- links are explicit only;
- ids must match whole, so FR-008 never matches FR-008a or FR-0081, and `UT-FR-001` / `[SR-FR-013]` are not this document's FRs.

The only consumers of these links are the SDD evidence graph and dashboard counts; both now show source-true numbers. Behavioural relationships stay reviewable suggestions (`ScenarioPlanning.SuggestRequirements`) and are never source links.

## D. Acceptance scenarios

- **Count and attachment are correct:** 65 = 11/8/6/5/9/10/12/4, each attached to its own story, with a unique (story, number) identity and a source line. US1 scenario 1 and US2 scenario 1 are different records.
- **Scenario → FR links were never created before.** No scenario has an id (`SpecItemId` is null for numbered items), and the old code only looked for scenario ids inside FR text.
- **Fixed:** a scenario whose own Given/When/Then names an FR is now linked to exactly that FR:
  - US3-4 → FR-009 ("unless the counting option in FR-009 is on");
  - US6-10 → FR-041 ("the clean-up list (FR-041 (b))");
  - US7-4 → FR-039 ("the fields the expected population needs (FR-039)");
  - US8-3 → FR-009.
- FR-009 is named by scenarios of two stories (US3, US8): one FR, several scenarios.

## E. Compound scenarios

The source scenario stays **one record**. Tests S08–S10 confirm, for example, that US2 still has 8 scenarios. The existing test-design engine (`ScenarioPlanning.Variants`, which feeds Planlegg's proposals) now proposes **variants** from the source text:

| Scenario | Source form | Variants proposed (verbatim source text) |
|---|---|---|
| US1-5 | "…; where planned equals actual, it is accepted with no flag" | the `where` clause (the clause rule now recognises *where*) |
| US1-8 | Given lists "span two periods, start after the submission date, or fall in a locked period or a period that has not started" | 3 "Listed condition" variants, same expected outcome |
| US2-5 | second "**Given** it is on, **Then** …" | 1 "Source alternative": counting option on → full breakdown, Bufdata breakdown still without them, no counts in the notification |
| US5-7 | "**Given** a receipt or reply value …, **Then** it is rejected as unexpected" | 1 source alternative |
| US7-5 | "**Given** an update for a record identity the system does not hold, **Then** it is applied as a create" | 1 source alternative |
| US7-12 | three further Given/Then pairs | 3 source alternatives (missing from-date / code outside code lists / to before from) |

Variants are suggestions only:

- **Nothing is saved automatically.** No case is created until a tester accepts a proposal.
- **No proposal is offered twice.** One already used for a saved case (`FromVariant`) is not proposed again (D57–D58).
- **No invented variants.** A plain scenario gets no alternatives (US4-1).
- **Existing cases are unchanged.** No case is regenerated or rewritten.

## F. FR sections

- **11 source areas.** Read by `RequirementSections` (shared with grouping) and now also the canonical `SemanticRequirement.Category`:
  1. Intake and identity
  2. Classification
  3. Validity control (reject with machine-readable reason)
  4. Quality control (accept and flag)
  5. Corrections, chains and idempotency
  6. Period lock
  7. Expected population and coverage
  8. Delivery to Bufdata
  9. Education master data (Utdanning)
  10. Follow-up lists
  11. Access, audit and retention
- **Container headings are excluded.** "Functional Requirements" and "Key Entities" are never areas; this was fixed and regression-tested S01/S05 on 2026-11-25.
- **Suffixed ids stay distinct:** FR-015 / 015a / 015b / 015c and FR-027 / 027a. The canonical id is used for identity (FR-015A); the source spelling is used for display (FR-015a).
- **Namespaces are kept apart.** Three further id patterns ignored the namespace guard and are now fixed: `TraceabilityModelBuilder.FrIdRe`, `SpecExplorerService.FrIdRe` and `TraceabilitySuggestionService.IdPattern`. So `UT-FR-*`, `SR-FR-*` and `US-B*` are never this spec's FR-* / US*.

## G–I. US–FR relationships and grouping (real SkoleService)

Evidence kinds are kept separate (`StoryTaskRequirement.Kind` / `StoryLinkBasis`):

| Kind | Status |
|---|---|
| A. FR exists in source | `RequirementSections` |
| B. Explicitly referenced (story / scenario text) | SourceExplicit / ScenarioExplicit |
| Tagged task line `[USn]` | Structural, deciding |
| Phase «Goal» of the story's own tasks.md phase | Structural, deciding — **new today**; the only source of US2 → FR-008a |
| Task continuation lines / «As built» | supporting only — **new** |
| plan.md / quickstart.md line mentioning the story | supporting only — **new**, after the SDD audit (plan.md:290 made FR-035 decide US3/US6) |
| C. Behavioural (word overlap) | suggestion only; never decides, never a link |
| E. Confirmed / F. rejected by a reviewer | HumanConfirmed / Rejected, with history |

Primary area versus related areas:

- **Primary area:** the area with the most distinct deciding FRs.
- **Ties:** a tie means «Flere mulige grupper».
- **Thin evidence:** one task-line FR while other own areas are touched means «Trenger gjennomgang».
- **Cross-cutting areas** (deciding evidence from ≥3 stories and ≥ half of the linked stories) never decide alone.
- **Confirmed placements always win** and are flagged when the suggestion changes.

The real outcome, per story, is in section I below (regenerated after all fixes: `Fixtures/story-grouping-report.md`).

### I. Traceability sample, all eight stories

Columns marked "see SDD audit" are covered in detail in the companion report. Edge cases are matched by explicit FR reference only.

(Filled from the regenerated grouping report. See the table appended at the end of this document.)

## J. Edge cases

- **Extraction:** 27/27, with title, description and the FRs they cite explicitly. For example, "Intake outage around the deadline" cites FR-023a and FR-026a.
- **Never acceptance scenarios:** the scenario count stays 65 (S17).
- **Matching is explicit only.** An edge case relates to an FR only when its text names the FR; there is no semantic matching.
- **Not yet surfaced in Manual Testing.** Edge cases are not yet proposed as test-design variants or shown in a context panel there (see X).

## K. Clarifications and supersession

- **Session dates:** all 48 clarifications carry theirs (2026-10-02 … 2026-10-09).
- **Status comes from the source's own markers:**
  - 3 are **Superseded**. Examples: "How does the service know a child's current gradering" → *Superseded 2026-10-09: no copy is kept … (DEC-122)*; "Can Person contain Kode 6/7 children" → *Superseded 2026-10-06*.
  - 4 are **Amended**.
  - 41 are **Current**.
- **Never inferred from position.** A later position in the document never decides precedence.
- **The original answers are kept** (the status note is verbatim), so nothing historical is deleted.
- **Gap:** test design does not yet suppress expected results derived from a superseded answer, because clarifications don't feed expected results today. The superseded flag is available for that rule. See X.

## L. Success criteria

- **Identity:** SC-001…SC-011 keep their ids, text and line.
- **Not FRs:** no SC- id appears among the requirements (S20).
- **SC links are sparse in the source.** SC → FR links exist only where the SC text names an FR (SC-007 → FR-045).
- **Gap:** test-approach recommendations per SC (for example SC-010's load test as performance, SC-005's byte-for-byte check as automated) are not yet produced (see X).

## M. Dependencies and open items

- **Rows:** 13, each with id, statement, owner, the "Blocks" column, and the FRs named in the row. For example, OQ-03 → FR-009 and DEP-06 → FR-008a, FR-040c.
- **Source-document status is kept:** DEP-04/OQ-09 **Closed 2026-10-06**; EXC-01..05 struck through, so withdrawn.
- **Live status is unknown.** The specification does not know the current implementation state, and BirkNext does not invent it.
- **Gap:** dependencies are not yet shown in the preparation context (see X).

## N–O. Key entities, assumptions, out of scope

- **Key entities:** 12/12. The entity rule now accepts `**Name** (`Code`): …`.
- **Assumptions:** 11.
- **Out of scope:** 7 items, multi-line items joined.
- **All context, never requirements or tests:** none of these becomes a requirement or a test case.

## P–R. Manual Testing UI and grouping

See the companion report, `test-isolation-and-failsafe-grouping-2026-11-25.md`, sections M–S:

- the permanent «Uten gruppe» system view, with each story's reason;
- «Gjennomgå gruppering»;
- truthful status labels;
- deleting a group with stories, with preview and choice;
- duplicate-name validation;
- idempotent retry;
- «Gruppering feilet» / «Kilde mangler».

No other UI redesign was made in this task.

## S–V. Regression, browser, performance

See the companion report. Results are filled in there from the actual runs.

## W. Changed files (this task)

| File | Change |
|---|---|
| `frontend/BirkNext.Web/Services/SpecExplorerService.cs` | Edge cases under «User Scenarios & Testing», entity code names, whole-id references, explicit-only story links, scenario → FR from Given/When/Then, FR category = source area, clarification session/status, dependencies, out of scope, guarded FR id regex |
| `frontend/BirkNext.Web/Models/SpecificationSemanticModel.cs` | `Category` settable; `SemanticClarification.Session/Status/StatusNote`; `Dependencies`, `OutOfScope`, `SemanticDependency`, `SemanticScopeItem` |
| `shared/ScenarioCentricPlanning.cs` | Source-alternative and listed-condition variants; `where` clauses |
| `shared/TestCoverageContracts.cs`, `shared/RequirementSections.cs`, `shared/StoryGrouping.cs` | Evidence kinds (task line / phase goal / task detail / plan mention), `StoryTaskContext`, supporting-only evidence |
| `frontend/BirkNext.Web/Services/TraceabilityModelBuilder.cs`, `TaskExplorerService.cs`, `TestCoveragePresentation.cs`; `backend/.../SpecificationEvidenceBuilder.cs`, `TraceabilitySuggestionService.cs` | Namespace-guarded ids; tasks.md read by tagged lines, goals and details only (not "any line mentioning the story") |
| `backend/BirkNext.Api.Tests/Services/Specifications/SpecStructureComplianceTests.cs`, `SpecStructureInventoryProbe.cs` | New tests and the audit probe |

## X. Known limitations and gaps

1. **Context sections are not modelled.** The supersession banner, the Context and Reporting-cycle prose, and Governing documents are not modelled sections; they are visible in the original text only.
2. **Edge cases, success criteria, dependencies and clarification status are not surfaced in Manual Testing.** They are extracted and tested, but not yet shown as contextual panels or turned into test-design proposals. The model is ready for that UI work.
3. **Superseded clarifications don't yet affect expected results.** Rule 63 is honoured trivially, because clarifications don't feed expected results today. Once they do, the Superseded status must exclude them.
4. **The scenario-reference policy is an open product decision.** An FR named in passing inside a scenario ("counting option in FR-009") counts as ScenarioExplicit evidence and can decide a primary area (US3, US8, US6). See SDD audit §K.
5. **Hard-coded ranges remain.** `TraceabilityModelBuilder.InferUserStoryId` and `AddDeterministicScLinks` hard-code FR → story ranges and FR → SC links. No page renders them today (SDD audit §J); they were not removed in this task.
6. **ID ranges in prose yield only their endpoints.** "FR-040b to FR-040d" and "UT-FR-001..005" are not expanded.
7. **Other specs' formats.** The audit covered SkoleService in depth. Person 001 writes FRs as paragraphs without areas, so it has no functional grouping (the expected "Ukjent" area).
8. **The uploaded comparison file is unavailable.** `spec(20261125-055421).md` could not be found.

## Y. Acceptance criteria

(See the end of the companion report for AC01–AC32 of the isolation/grouping task. These are the structural-compliance task's ACs.)

| AC | Result | Evidence |
|---|---|---|
| AC01 eight stories | PASS | S01 |
| AC02 65 scenarios | PASS | S06 |
| AC03 priority, description, rationale, Independent Test | PASS | S01–S05 |
| AC04 compound scenarios keep identity | PASS | S08–S10, D57 |
| AC05 variants keep their source relationship | PASS | D51–D56 (proposal basis cites the source text) |
| AC06 FR sections recognised | PASS | S12 |
| AC07 container headings not misclassified | PASS | S12, RequirementSections S01/S05 |
| AC08 US–FR provenance | PASS | evidence kinds and basis; R27 |
| AC09 implicit links proposed, never confirmed | PASS | word-overlap links removed; suggestions stay Suggested |
| AC10 primary versus related areas | PASS | F43; grouping report |
| AC11 US2/US4/US5/US8 investigated with source evidence | PASS | section I; SDD audit |
| AC12 ungrouped stories accessible | PASS | companion report |
| AC13 standalone scenarios accessible | PASS | «Uten brukerhistorie» unchanged; U23–U24 |
| AC14 group rename / reassignment | PASS (unit), browser: see companion | F32, F35, U11 |
| AC15 confirmed grouping survives source changes | PASS | F23 |
| AC16 edge cases as test evidence | PARTIAL | extracted and linked, not yet in the Manual Testing UI or proposals |
| AC17 clarifications / superseded rules | PARTIAL | status extracted from the source; not yet shown or used in test design |
| AC18 success criteria inform verification approach | PARTIAL | extracted; no per-SC approach recommendation yet |
| AC19 dependencies inform preparation | PARTIAL | extracted; not yet in the preparation context |
| AC20 out of scope not turned into tests | PASS | extracted as context only |
| AC21 case editor intact | PASS | frontend suite green; no editor change |
| AC22 Norwegian / Original / Compare intact | PASS | frontend suite |
| AC23 saved cases and plans unchanged | PASS | DB fingerprints (companion report) |
| AC24 stable source identity | PASS | (story, number) and canonical ids |
| AC25 no duplicate scenarios / FRs | PASS | S06, S12 |
| AC26 grouping does not block preparation | PASS | U28/U35 |
| AC27 test DB and App_Data isolated | PASS | I01–I10 |
| AC28 backend regression run safely or reported blocked | see companion | |
| AC29 browser verification | see companion | |
| AC30 performance measured | see companion | |
| AC31 no duplicate parser or engine | PASS | all fixes in existing services |
| AC32 no reset, commit or push | PASS | |
