# Feature → User Story → Acceptance Scenario → Manual Test Cases (2026-11-20)

Branch `008-traceability-first`. Builds on the scenario-centric Plan & prepare (`26fed394`, `7d9a34b7`) and System Settings → Translation
(`073725b5`) — same session, same model; no second parser, planner or UI.

Principle: **BirkNext preserves the structure the specification already provides instead of flattening it or inventing another hierarchy.**

## 1. Actual M2LB `spec.md` structure (imported archive, read-only)

26 module specifications under `<Module>/specs/<NNN-feature>/spec.md`. Three formats:

| Format | Example | Where |
|---|---|---|
| Spec Kit (English) | `### User Story 6 - Expected population and coverage breakdown (Priority: P2)`, description, `**Why this priority**:`, `**Independent Test**:`, `**Acceptance Scenarios**:` with numbered `**Given** … **When** … **Then** …`; upstream references in brackets (`[US-D1, US-D3]`, `[US-A1, US-A3]`) | SkoleService, SkoleAdapter, MeldingsutvekslerReceiver, Person, Tjeneste, … |
| Spec Kit (Norwegian) | `### Brukerhistorie 1 — Overblikk ved innlogging (Prioritet: P1)`, `**Hvorfor denne prioriteten**`, `**Selvstendig test**`, `**Akseptansekriterier**` with `**Gitt** … **naar** … **saa** …` | Frontend/001-initial-dashboard |
| Scenario headings directly under the feature | `### Scenario 1: Saksbehandler ser hendelsestidslinje` with `**Gitt**` / `**Når**` / `**Så**` lines | Hendelse/001-hendelsestjenesten |

Functional requirements are in their own section (`## Requirements → ### Functional Requirements`), not under stories. Story → requirement
relationships are written in `tasks.md` (`- [X] T099 [US6] … (FR-027a)`), rarely in the story text. Scenario 42 of SkoleService ("Given a
child past compulsory school age, When the period locks, …") is **acceptance scenario 3 of User Story 6** (priority P2) — verified against
the heading above it in the raw file by the opt-in test, not assumed.

## 2. What the shared parser did before

`SpecExplorerService` (the single specification parser, used by the browser and the server) already nested scenarios under their story heading
in its tree, but the semantic model linked **0** scenarios to a story (links only by text reference to a scenario id that M2LB never writes),
filled no priority / why / Independent Test, treated in-text references (`[US-A3]`) as stories, read no Norwegian inline scenarios and turned
"Scenario N:" headings into stories with no scenarios. Discovery numbered scenarios across the whole file (`scenario-42`) with no story.

## 3. Parser changes (additive, in the shared parser)

- `SemanticUserStory`: `AuthoredId` ("US6" from "User Story 6" / "Brukerhistorie 6" / an authored id), `Ordinal`, `IsStoryHeading` (false for
  in-text references and "Scenario N:" headings), `StableKey` (= authored number, else `story-<position>`; never the title), `Priority`
  ("(Priority: P2)" / "(Prioritet: P2)"), `Description`, `Why`, `IndependentTest` (English and Norwegian labels; source text unchanged),
  `LineStart`/`LineEnd`.
- `SemanticAcceptanceScenario`: `ParentStoryKey` (nearest story heading above it in the heading hierarchy — never textual proximity; null
  when none), `NumberInStory`, `LineStart`. The text-reference links are kept; structural links are added.
- Inline scenarios accept Norwegian keywords (**Gitt** / **Når**/**Naar** / **Så**/**Saa**); a `### Scenario N:` heading followed by step
  lines becomes that scenario (a heading without steps never creates an empty scenario).
- `tasks.md`: the shared task parser kept `FR-027a` as `FR-027` — fixed to keep the suffix (`FR-027A`, as the specification normalises it).

## 4. Identity, provenance, re-import

- **Story key** = specification file + the story's own number (`SkoleService/specs/001-skoleservice/spec.md#US6`); unnumbered stories by
  position (`#story-2`). The same number in another feature's file is another story.
- **Scenario identity is unchanged** (`scenario-N` → candidate id), so prepared cases keep their identity; the story and the number within
  it are added beside it (`DiscoveredScenario.Story`, `TestCaseContent.Story`, evidence `story → <key>`), **outside the fingerprint** —
  identifying a story is not a change of the scenario.
- **Moves are flagged, never silent**: when a newer discovery puts the same scenario under another story, the case is flagged "changed in the
  evidence"; nothing is rebound until the tester chooses Update, which records "User story: US6 → US7 (…)" in the case's change history.
  Historical revisions keep their story.

## 5. Independent Test

Carried with the story into every case prepared from its scenarios and shown in the scenario overview and the story card as **evidence for
preparing the case** (setup, observable outcome). It is never turned into steps by itself.

## 6. Requirements of a user story

Story card → **Requirements** tab:
1. **Named in the story** (A) — FR ids in the story's own text.
2. **From tasks.md** (B, structural) — FR ids in tasks tagged with the story (`[US6]`) in the **same feature folder's** tasks.md, with the task
   id, line and text as evidence (tasks of another feature never count).
3. **Suggested** (C/D) — the existing suggester on the story's title, description, why and Independent Test.
4. **Linked to the story's test cases** — read-only roll-up of the cases' own links.

Confirm / Not related is the tester's decision, saved per story key with who and when (`TestPlanDraft.StoryRequirementLinks`,
`StoryRejectedSuggestions`), checked against the project's catalog exactly like case links. **A story-level requirement is an association,
never inherited by its scenarios or cases, never counted as coverage** (verified: traceability does not count it).

## 7. UI (Plan & prepare)

Feature line → **user story cards** (number, priority badge, Independent Test chip, "n of m scenarios in the plan", tabs *Story* /
*Requirements*) → the existing scenario accordion (scenarios in their story order) → test cases. Scenarios with no story: "Scenarios not under a
user story — Parent user story: not identified"; other sources (UI, contracts) and hand-written cases without a scenario are listed apart.
Without any story the plain scenario list is shown (non-SDD projects unchanged). A sticky language toolbar (segmented Original | Norwegian
switch, **Translate all** with a preview popover) sits above. Headings keep their levels (story `h3`, scenario `aria-level` 4).

## 8. Translation

Translating a scenario (or **Translate all**) now covers the scenario's Given/When/Then, the user story (title, description, why, Independent
Test — `TestPlanDraft.StoryTranslations`), every case field a tester reads (title, purpose, preconditions, steps and results, expected result,
how to verify, environment, roles, dependencies) and the linked requirement wording for display (`RequirementTranslations`). Identifiers stay
as written; a person's text (case or story) is never overwritten unasked; it is saved as one revision. Not translated on purpose: priority
values (export mapping), identifiers and names, review rationales and history, BirkNext's own labels.

## 9. Test coverage semantics

No new coverage figure. Discovered scenario ≠ prepared case ≠ approved ≠ exported ≠ executed; suggested ≠ confirmed; a story or scenario
association is never a case's coverage; a story's requirements are never inherited.

## 10. Other formats

Specifications without stories (scenarios directly under the feature), stories without scenarios, unnumbered stories, Norwegian headings,
different heading depths and duplicate story numbers across features are handled (tests). OpenAPI/UI-derived scenarios have no story and are
listed under "other sources". A project without a specification works as before.

## 11. Tests

- Backend: `UserStoryHierarchyTests` (English, Norwegian, scenario headings, unnumbered, in-text references, duplicate numbers across features,
  discovery carries the story outside the fingerprint, structural references), `ScenarioCentricPlanningServiceTests` (+3 on PostgreSQL: story
  kept and a move flagged/recorded, story-level link saved with who and never counted, Translate all with stories and requirement wording; a
  person's story text kept), `ScenarioPlanningTests` (scenario Given/When/Then translated; incomplete field sets refused).
- Opt-in on the real archive: `RealM2lbUserStoryHierarchyTests` (all 26 specs: every parent is one of the document's own story headings;
  Scenario 42 → US6 / P2 / scenario 3, verified against the raw heading; Norwegian dashboard 11 scenarios under stories; Hendelse 6 scenarios
  without a story; `[US-A3]` never a parent).
- Frontend: `UserStoryPrepareTests` (grouping and order, story card, tasks.md B suggestion with `FR-027a` kept, confirmation saved for the story
  only, Translate all preview and no-service help, same-folder rule), `ScenarioCentricPrepareTests` updated.
- Results of this run: see the commit and the handoff.

## 12. Real M2LB numbers (parser)

24 of 26 specifications: all scenarios under a story (before: 0 linked). Frontend/001 (Norwegian): 11 scenarios (before 0). Hendelse/001:
6 scenarios without a story (before 0). Frontend/004 has stories without an Independent Test section; Frontend/006 has neither stories nor
scenarios.

## 13. Order, remove and exclude (follow-up)

- One order for everything: the plan's case list (already what the save keeps and the export reads). `PlanOrdering` moves a case among its own
  scenario's cases, a scenario as one block among the scenarios of its group (same story, or the same "other" group) — nothing outside the group
  moves — and a step with its expected result (the evidence is kept apart). User stories keep the specification's order; scenarios within a
  story follow the plan's order, which is the specification's until a tester moves them.
- UI: a ⋮⋮ drag handle (HTML5 drag and drop; one global `dragstart` line in `pageFocus.js` sets the drag data browsers such as Firefox need)
  and ↑ / ↓ buttons with accessible names for keyboard and screen-reader users; a dashed drop target; a move never collapses the open scenario.
- Remove a scenario only when none of its cases was reviewed and none has an Azure DevOps id (earlier versions keep it); otherwise **Exclude
  scenario** with a reason: `POST drafts/{id}/exclude-scenario` excludes every case under it in one revision ("Scenario excluded (n test
  cases)"); nothing is deleted and excluded cases are never exported.
- Tests: `PlanOrderingTests` (4), `PrepareReorderTests` (4 bUnit: keyboard, drag of scenarios / cases / steps, remove vs exclude), a PostgreSQL
  service test (order kept and followed by the export; exclusion needs a reason, keeps every case, excluded cases not exported), and the browser
  workflow (keyboard reorder within a story, step drag, reload).

## 14. Remaining limitations

- Scenario identity stays positional within the file (`scenario-N`, the pre-existing scheme) so prepared cases keep their identity; inserting a
  scenario earlier in the file flags later scenarios as changed (never silently rebound).
- Story-level requirement links are shown in Plan & prepare only; Requirements Traceability does not list them yet (by design they are not
  coverage).
- tasks.md is read only when it sits in the specification's folder; plan.md/quickstart.md are not used for story relationships.
- Translation providers are verified against stand-ins only (see the scenario-centric audit, §16/§18).
