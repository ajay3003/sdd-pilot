# Scenario workflow UX — audit (System Understanding vs Test Plan Suggestions)

**Baseline:** `5a4f7605`. This follows the Manual Testing UX consolidation `0752a87d`; see `birknext-manual-testing-ux-consolidation.md` for the earlier part.

## Canonical ownership (verified in code)

- **One scenario source:** the System Understanding guide (`SystemGuideService`; curated, versioned revisions). Test Plan Suggestions reads candidates from the guide revision readers see, and stores only `ScenarioReference` (guide, scenario id, revision, fingerprint) on its drafts.
- **No second scenario store or generator.** Drafts, review events, snapshots, Test Data links and MT-7 associations each live in their single existing store.

## What each page did before this task

| | System Understanding → What to test | Test Plan Suggestions → Discover |
|---|---|---|
| Content | 21 cards, each expandable: objective, preconditions, test data, expected outcome, observable evidence, failure cases, rules, evidence links, state | Compact rows grouped by the guide's stages (since `0752a87d`): title, two-line purpose, state, readiness chips, one Evidence link |
| Selection | Checkbox per card + "Prepare in Test Plan Suggestions" (selected set and per scenario, the same `PrepareLink`) | Selection bar (select visible, clear, hidden-by-filter count) + one primary "Prepare Test Plan" |
| Said what it is for | Only "Test guidance, not test coverage" | Only the section title |

**Confirmed overlap:** a reader saw two scenario lists with selection and could not tell which page to use for what. The data, engine and state were never duplicated.

## Decisions (this task)

1. **What to test explains and hands off.** Its note now says what the tab is for (situations, why they matter, expected behaviour, rules, evidence) and that test plans, suites and Test Case drafts are made in Test Plan Suggestions, with a link. Selection and "Prepare in Test Plan Suggestions" stay as the lightweight handoff. No planning features were added.
2. **Test Plan Suggestions states each step's purpose.** Under the area bar: "Step N of 7 · …", in order:
   - Discover: choose the scenarios for a proposed test plan;
   - Suggested Test Plan: organise them into a plan and suites;
   - Review Test Cases: preconditions, steps, expected results and your decision;
   - Test Data & Testability: link data and see what still blocks execution;
   - Saved Snapshots: freeze an immutable proposal;
   - Compare Deliveries: what changed and what to retest;
   - Prepare for Azure DevOps: a reviewable export, nothing written.
3. **Language:** the product UI is English throughout; only the guide content is Norwegian. The purpose lines follow the product convention, in English. Mixing in the suggested Norwegian sentences would contradict "do not mix Norwegian and English arbitrarily". A Norwegian UI would be a separate localization task.
4. **States stay distinct** (unchanged):
   - a suggested scenario, a selected candidate, a prepared draft, a reviewed draft, an accepted draft and a saved snapshot are separate states;
   - "not an Azure DevOps Test Case" and "not executed" are stated on both pages;
   - MT-7: "Accepting this Test Case for preparation does not approve any requirement association".

## Visual scaling (code check)

Both pages use the same container (`max-width: 88rem`) and title size (1.75rem). Test Plan Suggestions sets base text to 0.95rem (15.2 px) against 16 px on System Understanding. Its row titles (15.2 px) are slightly larger than System Understanding's card titles (14.7 px). Nothing in the CSS makes one page "much smaller". The measurement at equal viewport and zoom is in the browser acceptance report; no CSS is changed on a screenshot impression.
