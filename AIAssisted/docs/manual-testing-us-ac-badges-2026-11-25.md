# Manual Testing: US + AC badges, «Kildegrunnlag» collapsed, functional areas vs user stories (2026-11-25)

Branch `008-traceability-first`. The badges and «Kildegrunnlag» are in commit `70456697` (the user asked to commit everything). The functional-area styling comes after that commit. Nothing was pushed.

## 1. US + AC badges in Planlegg og klargjør → Testtilfeller

### Existing list component

- The page is `frontend/BirkNext.Web/Pages/ManualTestingPlan.razor`, left list `nav.mtp-master`.
- Rows come from `PlanPreparePresentation.Rows`. There is one row per scenario (`ScenarioPlanning.Groups`: grouped by `ParentScenario` = the source scenario id; a hand-written case joins its scenario's row).
- The row header showed only the US badge (`.mtp-story`, `row.StoryId`). Search and the «Vis teknisk innhold» filter work on rows.

### Existing AC identity source

- The detail heading is `CaseEditorPresentation.ScenarioHeading(c)` → `ScenarioWorkspacePresentation.AcLabel(c.Generated.NumberInStory)` («AC 2»). It shows only when `c.Generated.Story` is set.
- Both values are saved with the case when it is prepared from the parsed acceptance scenario (`QaTestCandidate.NumberInStory`, `StoryKey`).
- No new parser was added. The list reads the same two saved fields.

### Changes implemented

- **`PlanPreparePresentation.AcOf(primary, cases, story)`** works per row:
  - It takes the scenario's own source case first, then its other source cases. Hand-written cases are ignored: they carry no number of their own.
  - From those it collects the distinct (story key, AC number) pairs.
  - Exactly one pair whose story is the row's story → «AC n».
  - None → no badge.
  - More than one, or a number under another story → `AcAmbiguous` (shown as «AC ?» with an explanation), never a guess.
- **`ScenarioRow`** gains `Ac` and `AcAmbiguous` (init properties; existing constructor unchanged).
- **Markup** (`ManualTestingPlan.razor`): `[US2] [AC 2] title` on one line.
  - The AC badge (`.mtp-ac`, `data-testid=mtp-row-ac`) sits directly after the US badge.
  - It has a `title` («Akseptansescenario AC 2 i US2»).
  - It is static text (a `span`).
- **Style** (`manual-testing.css` `.mtp-ac`): the US badge's size and shape, in the detail heading's accent tokens (violet), so list and detail match. «AC ?» is dashed and muted. `white-space: nowrap`, no extra row; the title still wraps as before.
- **Search** also matches the badge text, with or without the space («ac2», «AC 2»).

### Identity and scoping rules

| Rule | How |
|---|---|
| US and AC are separate | The badge is the pair (story key, number) |
| US1/AC 2 ≠ US2/AC 2 | Each is its own row and keeps its own pair |
| Several cases under one scenario | All share the row, so one US/AC |
| Hand-written case without a scenario | No AC (the «Uten scenario» row) |
| Number missing | No badge |
| Ambiguous saved data | «AC ?», nothing chosen |
| Never from | List position, ordering, title similarity, FR numbers or story ranges |

### Missing and stale AC

- Nothing is reassigned and no case is changed.
- A stale or changed source keeps its saved identity (the badge shows what the case was prepared from).
- The existing source state in the row («Kildekonflikt» / source label) and the plan's «Kildegrunnlaget er endret» banner stay as before.

### Navigation

- The badges are not interactive. «Vis scenarioet i Finn og forstå» in the detail (existing, canonical identity) opens the scenario.
- Each badge has a `title` saying what it is. There are no nested click targets inside the row header and no new URLs.

### Regression tests (bUnit, `ManualTestingPlanTests`)

- `O01_O05…`: US1/AC 1, US2/AC 2, US1/AC 2 as its own row, AC 3 under US1, a hand-written case sharing its scenario's row.
- `O06_O09…`: hand-written → no AC; no number → no badge; mixed numbers → ambiguous; a number under another story → ambiguous.
- `O10_O11_O18…`: a reimport title change, a translation or a manual edit keeps «AC 2»; an inserted scenario does not shift it; no rows merged or created.
- `O12_O16…`: the page renders `US1/AC 1`, `US2/AC 2`; the AC badge is a static span right after US and before the title; the `title` text; the selected case keeps `is-selected`; the detail heading starts with the same «AC 1 —»; search «ac2» and «US2».
- **Result:** plan page and editor tests 80/80. Full frontend suite: 9691/0 before the first commit, 9693/0 after the rail change.

## 2. «Kildegrunnlag» collapsed by default

- **Component:** the `aside.mtp-context` in the plan page's detail. Its content and actions (story, requirements with confirm, reject and unlink, why, source expected result, questions, evidence, translation) are **unchanged**.
- **Pattern:**
  - an accordion header: `<h3><button aria-expanded aria-controls="mtp-context-body">Kildegrunnlag</button></h3>`;
  - a rotating chevron;
  - the body stays in the page with `hidden` while collapsed.
- **Behaviour:**
  - The open state is kept for one case id only (`_contextOpenFor`), so switching case or scenario always shows it collapsed.
  - Keyboard: a native button (Enter or Space), `:focus-visible` outline.
  - No nested scroll.
- **Test** `P_kildegrunnlag_starts_collapsed…`:
  - collapsed with `aria-expanded=false`;
  - the content is still in the page;
  - click → expanded, click again → collapsed;
  - opened, then another case selected → collapsed.
- An earlier version used native `<details>`. It was replaced because its toggle cannot be tested in bUnit and does not reset on a case switch without extra keys.

## 3. Finn og forstå: functional areas vs user stories

- **Audit:** `ManualTestingDiscover.razor` rail `nav.mtw-stories`. Areas are `li.mtw-group > button.mtw-group-head` (from `StoryGroupingPresentation.Tree`); stories are `StoryButton` (`button.mtw-story`, `is-current` when chosen).
- **What was there:**
  - area headers were transparent and only grey on hover;
  - the selected story was `--msb-subtle` (grey) with a blue bar;
  - no rule coloured an area green.
  - The screenshot you described could not be reproduced from the code, so the target state was made explicit and is locked by tests.
- **Changes** (`manual-testing.css`, tokens only, light and dark themes follow):
  - every area header, open or closed and whether or not it holds the selected story: the same neutral surface (`--msb-subtle`) with a border;
  - unselected stories: neutral surface (`--msb-surface`) with a border;
  - the selected story: the middle column's soft-green story tint (`--clr-success-bg-light`, the same token as `.mtw-story-card.is-story`) with the blue selection border and bar (`--msb-accent`), and a heading-coloured title;
  - «Alle scenarioer» is not a story and keeps a neutral selected look.
- No grouping, relationship or plan data changed. No markup change.
- **Tests** (`DiscoverWorkspaceTests`):
  - only the clicked story is `is-current` and `aria-pressed=true`; the selection moves and never spreads; every story is a button;
  - stylesheet rules: one neutral token for every area header; no `:has()` colouring of an area; no success tint on an area; the selected story uses the success tint plus the accent bar; unselected stories are neutral; the same token as the middle card.
  - 15/15 pass.

## 4. Browser verification

**VERIFIED** on an isolated BirkNext, never your instance or data:

- a Release publish from a clean source copy, its own App_Data, and a disposable database `birknext_adoverify_<time>`;
- ports 5097 and 5197;
- the synthetic spec has three stories (US1 with AC 1 and AC 2, US2 AC 1, US3 AC 1) and three functional areas (bold section lines under Functional Requirements).

The test is `ManualTestingRailAndBadgesWorkflowTests`. It **passed** (46 s); screenshots `scratchpad/adoverify/shots/mt-00…mt-05`.

**«Finn og forstå»:**

- Areas are only *suggested* until a reviewer accepts them (existing behaviour: all three stories first sat in «Uten gruppe», with «Gjennomgå gruppering (3)»). The walkthrough accepts the suggestions as a tester would. Two areas were then shown («Quality control» and «Uten gruppe»; the other stories stayed ungrouped).
- The computed background of **every area header is identical**, including the one holding the selected story.
- The selected story's background **equals the middle column's story card** (soft green), with the blue bar. Every unselected story differs from it, and no area header is green.
- Collapsing the area that holds the selected story keeps its colour and the selection. Enter on the focused header also toggles it.

**Defect found and fixed during the check:** an area holding the selected story was forced open, so its toggle did nothing (`aria-expanded` stayed true, a dead control). Now the tester can collapse it, and choosing a story re-opens its own area (`ManualTestingDiscover.razor`, `IsOpen` / `ToggleGroup` / `SelectStory`).

**Planlegg og klargjør:**

- The rows show `US1/AC 1`, `US1/AC 2`, `US2/AC 1`, `US3/AC 1`: AC 1 under US1 and under US2 are different rows.
- Selecting the US2 case shows the detail heading «AC 1 — …» and keeps `is-selected`.
- «Kildegrunnlag» is collapsed with its body hidden; a click shows FR-010 and its content, a second click collapses it, and another case starts collapsed.
- At 390 px there is no horizontal page scroll.
- No page errors.

**Observed:** at 1366 px the page's existing container query lays the story rail out horizontally above the content (unchanged).

## 5. Modified files

- `frontend/BirkNext.Web/Services/PlanPreparePresentation.cs` (`AcOf`, `ScenarioRow.Ac/AcAmbiguous`, search)
- `frontend/BirkNext.Web/Pages/ManualTestingPlan.razor` (AC badge, «Kildegrunnlag» accordion)
- `frontend/BirkNext.Web/wwwroot/css/manual-testing.css` (`.mtp-ac`, accordion, areas and stories)
- `frontend/BirkNext.Web/Pages/ManualTestingDiscover.razor` (an area can be collapsed while it holds the selected story)
- `frontend/BirkNext.Web.Tests/Pages/ManualTestingPlanTests.cs`, `DiscoverWorkspaceTests.cs`
- `frontend/BirkNext.Web.PlaywrightTests/Tests/Workflows/ManualTestingRailAndBadgesWorkflowTests.cs` (new)

## 6. Remaining limitations

- The badges do not navigate on their own; the detail's existing «Vis scenarioet i Finn og forstå» does.
- A row's AC is the scenario's saved number. A case prepared before `NumberInStory` was stored shows no badge until it is prepared again; nothing is guessed.
- The green-area colouring you described was not found in the code (see 3). The target state is now explicit, and VERIFIED in the browser by computed colours.
- In the walkthrough, only two areas appeared after the review. Three areas side by side were not shown.

## 7. Acceptance criteria (US + AC brief)

| AC | Status |
|---|---|
| AC01 US and AC together in the list | VERIFIED (bUnit and browser) |
| AC02 AC from real source identity | VERIFIED (saved `NumberInStory`, the same as the detail) |
| AC03 Same AC under different stories | VERIFIED |
| AC04 No AC guessed | VERIFIED |
| AC05 Cases without AC accessible | VERIFIED |
| AC06 Case content unchanged | VERIFIED (presentation only) |
| AC07 Source associations preserved | VERIFIED (no data written) |
| AC08 Selected styling | VERIFIED |
| AC09 Long titles readable | VERIFIED (titles wrap beside the badges; 390 px without horizontal scroll) |
| AC10 Search and filter work | VERIFIED |
| AC11 Navigation uses real identity | IMPLEMENTED (existing detail link; badges static) |
| AC12 Norwegian/Original behaviour | VERIFIED (identity independent of text) |
| AC13 No duplicate parser | VERIFIED |
| AC14 Frontend tests pass | VERIFIED |
| AC15 Browser verification | VERIFIED (isolated; see 4) |
| AC16 No reset or destructive operations | VERIFIED |
| AC17 No commits or pushes | Commit made at the user's explicit request («commit ALL»); no push |
| AC18 Evidence-backed report | This file |
