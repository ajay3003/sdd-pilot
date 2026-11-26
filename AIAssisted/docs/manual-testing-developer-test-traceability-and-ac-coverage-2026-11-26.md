# Developer test discovery and AC coverage — 2026-11-26

**Delivery 5.**

**Safety:**
- no database reset, cleanup or destructive migration;
- no Azure DevOps write;
- no internet, AI, Docker or Podman.

**The key rule:** «utviklertesten finnes» is never «akseptansescenarioet er tilstrekkelig testet».

## A. What already existed and is reused

The existing test discovery already does three things, and this delivery reuses all of it:
- **Reading tests:** it reads the source tests for references to scenarios and user stories (`DotNetXunitTestDiscoveryProvider`, `ScenarioTestLink`);
- **Attaching tests:** it attaches them to each acceptance scenario (`DiscoveredScenario.ExistingTests`);
- **Recording decisions:** a reviewer can confirm or reject a tie and assess its extent (Full/Partial). These decisions are stored as `ScenarioTestLinkDecision` in the specification's grouping document, with the test and scenario fingerprints (a changed test or scenario → «Kontroller koblingen»).

`TechnicalCoveragePresentation` keeps four facts apart: tie, extent, currency and result. Manual Testing never shows a test as run or passed.

No new discovery engine was built: duplicating the inventory would create a second truth.

## B. New in this delivery

The new logic lives in `DeveloperTestCoveragePresentation` (pure; it reads the same facts).

- **US card — «Utviklertester»:**
  - an example line: «Utviklertester: 3 av 9 AC har koblet test (1 vurdert som full, 1 delvis) · 1 med mulig test · 5 uten kjent test»;
  - the tone is never a success tone for the story, not even when every AC is assessed as full;
  - the words «dekket» or «testet» are never used;
  - a tooltip carries the key-rule caveat;
  - a link, «Vis AC uten koblet utviklertest», sets the filter.
- **AC details («Hva er allerede kontrollert?»):**
  - a plain-language meaning of the state;
  - the caveat whenever tests exist;
  - **the expected results as checkpoints:**
    - a checkpoint is «Kontrollert av utviklertest (vurdert)» only when a reviewer assessed a confirmed test as covering the whole scenario;
    - for partial coverage, every checkpoint stays «Ikke bekreftet», with the reason (the assessment says *some*, not *which*);
    - an explicit or candidate tie checks nothing until it is assessed.
  - The concrete tests (layer, readable name, class, method, file, basis) were already shown, so the tester never has to read C#.
- **Filter:** «Uten koblet utviklertest (prioriter manuelt)» = no known test, same requirement only, or an unconfirmed candidate.
- **Planlegg:**
  - the scenario's section shows the meaning and a «Prioriter manuell test» badge for scenarios without a tied test;
  - developer tests never remove, replace or hide a manual case.

## C. Tests

- **`DeveloperTestCoveragePresentationTests`:**
  - the summary counts, text and tone (no success tone even for all-full);
  - checkpoints are checked only behind a full assessment, and partial leaves all unconfirmed;
  - the priority, meaning and filter rules.
- **`DiscoverWorkspaceTests`:** the US card line (no «dekket»; the caveat), the AC meaning, caveat and unconfirmed checkpoints, and the filter link showing only the AC without a test.
- **Results:**
  - the targeted runs pass: 131 tests, including the existing technical coverage, Discover and Planlegg tests;
  - the full frontend suite was started at commit time; its result follows in a later commit.

## D. Not done

- **NOT IMPLEMENTED:**
  - per-checkpoint assessment (marking *which* expected results a test checks). It needs a new decision field; today «Delvis» is honest but coarse;
  - Testoversikt widgets for developer-test coverage (optional in the brief);
  - running tests or reading test results: Manual Testing still holds no results («Testresultat ikke tilgjengelig»).
- **Not checked in the browser:** no isolated browser walkthrough was run; the flows are covered by bUnit tests.
