# SDD traceability semantics hardening: FR relationship roles and primary grouping — 2026-11-25

- **Branch:** `008-traceability-first`, uncommitted on top of `9e4988bf`. No commit or push.
- **Inputs:**
  - `sdd-cross-artifact-traceability-audit-2026-11-25.md` (read-only audit);
  - `spec-structure-compliance-audit-2026-11-25.md`;
  - `test-isolation-and-failsafe-grouping-2026-11-25.md`.

**The rule implemented:** a source reference proves that a relationship exists. It does **not** prove primary business ownership, functional responsibility, implementation, test coverage, test execution or test success.

## A. Existing architecture reused

| Concern | Existing component |
|---|---|
| Spec parsing | `SpecExplorerService` (canonical) |
| tasks.md | `TaskExplorerService` (browser) and `StoryTaskContext` (shared reader) |
| Story → FR evidence | `CoverageUserStory.NamedRequirements` / `TaskRequirements`, `StoryTaskRequirement.Kind` |
| Relations, provenance, grouping, reviewer decisions | `StoryGroupingEngine` (`StoryLinkBasis`), the `story_groupings` document |
| UI | «Tilknyttede krav» / «Administrer kravkoblinger» (`StoryRequirementLinks`), «Administrer grupper», «Gjennomgå gruppering» |

No new parser, traceability engine, graph or page was added.

## B. Hard-coded inference removed

`frontend/BirkNext.Web/Services/TraceabilityModelBuilder.cs`:

- **`InferUserStoryId`** mapped FR numbers to stories for *every* specification: FR-001..007 → US1, 008..012 → US2, 013..016 and 033 → US3, 017..019 → US4, 020..024 and 032 → US5, 025..028 → US6, and 029..031 → "Cross-cutting / Platform".
  - **Removed.** A story now comes only from an explicit story id in the item's context.
- **`AddDeterministicScLinks`** linked fixed FR ranges to SC-001…SC-008.
  - **Removed.** Requirement → SC links now exist only where the success criterion names the requirement.
- **Earlier today:**
  - `SpecExplorerService.LinkUserStoriesToRequirements` stopped linking on two shared title words (US1 had 58 of 65 FRs); links are explicit only.
  - The backend `tasks.md` reader stopped treating any line that mentions a story as a task of that story.
- **Persisted data:** these were computed views, never stored, so no saved links needed recomputation. No reviewer decision was touched.
- **Tests:**
  - T02 (the source contains neither path any more);
  - `TraceabilityModelBuilderTests` rewritten: the story column is only explicit ids, and SC links are only what the source states. The old tests asserted the fabricated table.

## C. Strict identifier parsing

Whole identifiers, suffix-aware, namespace-safe: `(?<![\w-])FR-?\d+[a-z]?\b`.

- **FR-015, FR-015a and FR-015b** are distinct. FR-0151 is not FR-015.
- **Prefixed references are not this document's FRs:** `UT-FR-001`, `SR-FR-017a`, and `US-B1` for stories.
- **References inside code spans and Markdown links** are read.
- **Several ids in one paragraph** are all found.
- **Casing:** the canonical id (FR-027A) is used for identity; the source spelling (FR-027a) for display.

Applied in:

- `SpecificationEvidenceBuilder.NamedRequirements`
- `TestCoveragePresentation.NamedRequirements`
- `TaskExplorerService` FR references
- `SpecExplorerService.CanLinkByReference` and `FrIdRe`
- `TraceabilityModelBuilder.FrIdRe`
- `TraceabilitySuggestionService.IdPattern`
- `StoryTaskContext`

Tests: T03–T06, S16, R-whole-identifier.

**Not changed:** persisted canonical keys. Cross-service collisions (SkoleService FR-001 versus Person FR-001) are already prevented by the catalog scoping per specification path in the grouping service (V03 from 24 November). Validation uses only the specification's own catalog.

## D. Provenance model (how a link is known)

`StoryLinkBasis`: SourceExplicit, HumanConfirmed, ScenarioExplicit, Structural, Suggested. Each relation keeps every piece of evidence with its **kind**:

| Kind | Meaning |
|---|---|
| `story` | the story's own text names it |
| `scenario` | an acceptance scenario's own text names it |
| `task` | a tagged task line (`[USn]`) |
| `phase-goal` | the «Goal» of the story's own tasks.md phase |
| `task-detail` | an indented sub-bullet or «As built» note under a tagged task |
| `plan-mention` | a plan.md / quickstart.md line naming the story and the FR together (added today, from audit finding 3) |
| `manual` / `confirmed` / `plan` | a person added or confirmed it, here or on a test plan |
| `suggestion` | the existing rule comparison; never a link |

## E. Role semantics (what a link is for), independent of provenance

`StoryLinkRole`:

| Role | Meaning |
|---|---|
| **Unknown** | the relationship is real, but its role is not established |
| **Primary** | the FR defines the story's central behaviour; reviewer only |
| **Supporting** | a prerequisite, constraint or exception |
| **CrossCutting** | a control many stories pass through |
| **Contextual** | comparison or background; reviewer only |

- **Derived role, where the evidence shows it:**
  - CrossCutting from breadth (FR or area).
  - Supporting when the only evidence is task details or plan mentions.
  - Otherwise **Unknown**. Nothing is guessed from words.
- **Reviewer role:**
  - set through the new `SetLinkRole` operation, stored in `StoryGroupingDocument.Roles` (latest per story and requirement), with history ("Endret kravrolle");
  - "Uavklart rolle" returns the relationship to derived;
  - a role never changes the link, its decision or its source evidence (T18/T29).
- **Shown in the UI:** in «Tilknyttede krav» each link shows its role ("Uavklart rolle", "Støttende", "Tverrgående kontroll", "Kontekst", "Primær atferd"), marked "(vurdert)" when a reviewer set it. «Administrer kravkoblinger» has a role selector per link; previews state "rollen til FR-x blir …".
- **Independence:** provenance and role are separate dimensions (T07/T08/T13). For example, a SourceExplicit link can be reviewed as Supporting.

## F. Incidental references

**Generic rule:** a requirement named **only** inside an acceptance scenario's text (or only in supporting lines) has *no responsibility evidence*.

- It stays visible as a related area, with role Unknown.
- It **never decides** a primary area.
- It **never makes another suggestion "thin"**.
- A reviewer can mark it Primary, and then it decides (T-reviewer test).

Responsibility evidence is: the story's own text, a tagged task, the story's phase goal, or a person's link. There are no story-specific rules.

| Real case | Before (structural fixes only) | Now |
|---|---|---|
| US3 → FR-009 ("counting option in FR-009") | could tie with Period lock | visible (tverrgående); **Period lock** decided by FR-024/FR-026 tasks (T15) |
| US6 → FR-041 ("clean-up list (FR-041 (b))") | Expected population was "thin" because of it | visible; **Expected population and coverage** decided by FR-027a (T16) |
| US8 → FR-009 | could co-decide Classification | visible; does not decide. US8 stays **Trenger gjennomgang** (T17) |

## G. Primary grouping decision rules (in order)

1. A person's placement (confirmed group or area, or «bevisst uten gruppe») is never moved silently. A changed suggestion is flagged «Trenger gjennomgang».
2. A reviewer's **Primary** role decides; Supporting, CrossCutting and Contextual never decide.
3. Unreviewed relationships decide only with responsibility evidence, and not when they are cross-cutting or supporting-only.
4. The strictly most distinct deciding FRs in one known area gives a suggestion. A tie gives «Flere mulige grupper». A single task-line FR while other own (deciding or supporting) areas exist gives «Trenger gjennomgang». No deciding evidence gives «Uten gruppe» with candidates.
5. **Never decides:** raw link count from incidental references, shared security FRs (cross-cutting), dependencies, nearby contracts, plan co-mentions, similar words, or numeric ranges.

## H. Cross-artifact evidence

**Implemented:** `tasks.md` tagged lines, phase goals and task details; `plan.md` and `quickstart.md` co-mentions as supporting evidence. Each piece keeps its file, line and text, shown under «Se kilde».

**Not implemented (recommendations from the audit, unchanged):**

- **The upstream reference document as supporting evidence.** `docs/analysis/skolenaervaer-reference.md` ties US2 [US-B4] → SR-FR-007/008/009 → FR-008/009.
- **Contract-mediated two-hop evidence.** US8 T140 → `graphql-skole.graphql` → FR-041.

Both would be *paths* of supporting evidence, never direct links. Neither is created today, and no transitive inference exists.

## I. `USn-k` scenario references

The convention is confirmed in the source:

- `US5-2`, `US4-7` … in MeldingsutvekslerReceiver and SkoleAdapter artifacts;
- "US2 scenarios 2–3" and "User Story 3 scenario 3" in SkoleService `tasks.md`.

`SourceScenarioReferences.Parse` reads them verbatim and expands ranges. `TT-US1-2` style ids are ignored. `Resolve` resolves them **only within one specification**: "US3-14" from another feature does not resolve to SkoleService US3, which has 6 scenarios. Unresolved references keep their reason (T25/T26).

**Gap:** the parser and resolver exist and are tested, but are not yet shown in the UI or used as evidence.

## J. tasks.md relationships

| Evidence | Effect |
|---|---|
| Task tagged with the story | evidence that the story is responsible (Structural) |
| Phase goal | the same |
| Detail line | supporting only |
| Task in another story's phase that mentions this story ("Processing is US1's", "US2 scenarios 2–3") | not a task of this story (T21–T23) |

**A task link is traceability only.** Relations carry no implemented, covered, tested or passed state (T24). Neither task existence nor task completion is treated as implementation or test evidence anywhere in the grouping or traceability path.

## K. Historical contract names

**Verified, not changed:**

- Identity never depends on names. Story identity is file plus story number; scenario identity is positional within a file; FRs use their canonical id.
- Renaming `SkolenaervaersperiodeLukket` to `SkolenaervaerPeriodClosed` in a scenario changes neither its story, its number nor its links (T27/T28).

**Across imports:** scenarios are matched by file plus Given and When text. A renamed contract in Given or When would therefore show as a changed scenario in source-change analysis, never as a silent relink.

**No alias table is implemented:** the authoritative mapping (SKO-021, `wire-changes.md`) is not read yet. That remains a recommendation.

## L. SkoleService US1–US8 evidence matrix (no reviewed decisions applied)

| Story | Suggested primary | Deciding evidence | Related (role) | State |
|---|---|---|---|---|
| US1 | Quality control (accept and flag) | FR-014, FR-015c, FR-016 tasks | Corrections FR-018/022 (Unknown), Intake FR-004/007, Validity FR-011/013, Period lock FR-026a | Gruppert (foreslått) |
| US2 | Classification | FR-008a phase goal (tasks.md US2 phase) | — | Gruppert (foreslått) |
| US3 | Period lock | FR-024, FR-026 tasks | FR-009 scenario 4 (CrossCutting); Delivery, Corrections, Expected | Gruppert (foreslått) |
| US4 | — (candidate Period lock) | FR-026a, one metrics task | Intake FR-006/007, Access FR-046, Classification FR-009 (Supporting, task details) | Trenger gjennomgang |
| US5 | — (Corrections / Access) | FR-022 and FR-045b (one task each) | FR-020/021 (Supporting, task details) | Flere mulige grupper |
| US6 | Expected population and coverage | FR-027a task | FR-041 scenario 10 (Unknown, incidental); FR-027 and FR-035 plan mentions (Supporting); FR-009 (CrossCutting) | Gruppert (foreslått) |
| US7 | Education master data (Utdanning) | FR-039, FR-040b/c/d | — | Gruppert (foreslått) |
| US8 | — (candidate Classification) | FR-008a, one task | FR-009 scenario 3 (CrossCutting); FR-011 (Supporting, As-built) | Trenger gjennomgang |

**Human confirmation state:** none. The user database has 0 grouping documents.

## M. Existing decisions preserved

No grouping document exists in the user database, and the walkthrough creates and removes only its own row. Roles are additive, with an empty list by default. Old documents deserialize unchanged.

## N. Regression results

| Suite | Result |
|---|---|
| `TraceabilitySemanticsTests` (new) | 24/24 |
| Grouping, structure and sections targeted | 87/87 |
| Full frontend | **9,631 / 0** |
| Full backend after these changes | **7,424 passed / 1 failed / 19 skipped**; the failure is the intermittent `CI_DEF_3` loopback timeout, unrelated, which passes 3/3 alone (see the isolation report, G) |

## O. Browser verification

**Blocked.** A Local Data Reset and a new project import were performed in the running app by a browser session at 07:55–07:56. The import was still committing, so a fresh browser had no project. No disposable records were written. See the isolation report, section U.

## P. Changed files

- `shared/StoryGrouping.cs`: roles, `SetLinkRole`, `Roles`, responsibility evidence, deciding rule, thin rule.
- `shared/TestCoverageContracts.cs`: `PlanMention`.
- `shared/RequirementSections.cs`: `SourceScenarioReferences`.
- `frontend/BirkNext.Web/Services/TraceabilityModelBuilder.cs`: ranges and SC table removed, guarded regex.
- `TestCoveragePresentation.cs`, `SpecExplorerService.cs`, `StoryGroupingPresentation.cs`.
- `Components/ManualTesting/StoryRequirementLinks.razor`: role display and selector, evidence kinds.
- `backend/BirkNext.Api/Services/Specifications/SpecificationEvidenceBuilder.cs`, `TraceabilitySuggestionService.cs`.
- Tests:
  - `backend/BirkNext.Api.Tests/Services/TestPlanning/TraceabilitySemanticsTests.cs` (new);
  - `frontend/BirkNext.Web.Tests/Services/TraceabilityModelBuilderTests.cs` and `StoryGroupingPresentationTests.cs` (expectations of removed fabrication updated).

## Q. Remaining limitations

1. **Not yet surfaced:** `USn-k` references, upstream-document chains and contract two-hop paths (H, I).
2. **No historical contract-name alias table** (K).
3. **Primary and Contextual roles are reviewer-only.** The engine never claims them.
4. **US1's plurality area** (Quality control) may not match a tester's view of its business purpose. The engine reports the evidence; confirming a business group is a human decision.

## R. Unresolved review decisions (for a test lead)

- **US4:** Period lock (one metrics task) or Intake (task details), or keep it ungrouped?
- **US5:** Corrections or Access?
- **US8:** Classification, or confirm FR-041/FR-042 as Follow-up lists links (not stated by US8's own artifacts)?
- **Optional:** mark US3/US8 → FR-009 and US6 → FR-041 as **Contextual**, to record the judgement explicitly.

## AC01–AC20

| AC | Result |
|---|---|
| 01 FR-range inference removed | PASS (B, T02) |
| 02 no keyword-overlap links | PASS (T01, R27) |
| 03 namespace-safe ids | PASS (C) |
| 04 provenance and role distinct | PASS (E, T13) |
| 05 incidental references never decide | PASS (F, T14–T17) |
| 06 supporting and cross-cutting stay visible | PASS (T14) |
| 07 ambiguous roles require review | PASS (T11/T12) |
| 08 confirmed grouping stable | PASS (T18) |
| 09 cross-artifact evidence keeps its path | PARTIAL (tasks/plan yes; upstream and contract paths not implemented) |
| 10 two-hop evidence not shown as direct | PASS (not created) |
| 11 `USn-k` with namespace | PARTIAL (parser and resolver tested; not wired into the UI) |
| 12 task ≠ implementation or test success | PASS (T24) |
| 13 names don't change identity | PASS (T27/T28) |
| 14 SkoleService reviewed with evidence | PASS (L) |
| 15 ungrouped stories accessible | PASS |
| 16 plans and cases unchanged | PASS (fingerprints) |
| 17 targeted tests | PASS |
| 18 browser verification | BLOCKED (O) |
| 19 no duplicate architecture | PASS |
| 20 no destructive operations, commits or pushes by this work | PASS |
