# Scenario checkpoints and developer-test coverage — audit and redesign — 2026-11-26

**Safety:**
- no database change;
- no saved case, approval, revision or translation changed;
- the extraction is display-only (nothing persisted).

**Status keys used:** IMPLEMENTED · VERIFIED (tests) · NOT VERIFIED (browser) · NOT IMPLEMENTED.

## A–C. The checkpoint pipeline (audit)

| Stage | Where | Behaviour before |
|---|---|---|
| Source text | the user story's «Independent Test» (`CoverageUserStory.IndependentTest`, read from spec.md) | unchanged, kept verbatim |
| Extraction | `StoryInterpretation.Objectives` (`shared/TestCoverageContracts.cs`) — deterministic, not persisted, not overridable | split into sentences; «confirm/verify …» lists split at **every comma**; a sentence without «confirm/verify» became an **expected result** (Verification, «tolket») |
| «Coverage» | `PlanPreparePresentation.ObjectiveCoverageOf` (`Services/CaseEditorPresentation.cs`) | **at least 70 % of the fragment's content words found in the text of a manual case** of the same story in the plan (Planlegg), or of a discovered scenario (Finn og forstå) |
| UI | Planlegg → user story → «Uavhengig test» (`mtp-objective`); Finn og forstå → story details | each fragment a row: «tolket» (split by BirkNext), **«dekket»** (green) or **«mangler»** (red); a chip «x/y testmål» |

## B, D. The reproduced example and the root cause

**The source:** «Submit a Barn (child registration) record whose parent Person does not exist yet. Confirm it is recorded as a distinct, retryable failure with its original data preserved. Then submit the parent Person. Confirm the Barn record is automatically delivered on the next retry cycle, with no manual action taken.»

**Before:** 6 rows (the fragments in the brief).

**Earliest faulty stage: extraction.**
- **Sentence 1 («Submit …») and sentence 3 («Then submit …»):** they had no «confirm», so they fell to the last branch and were recorded as **expected results**. An action became a coverage gap («mangler»).
- **The comma split:** it cut «a distinct, retryable failure» into «it is recorded as a distinct» plus «retryable failure with its original data preserved». It also cut «…next retry cycle, with no manual action taken», separating the negation and the timing condition from their result.

## E–F. «tolket», «mangler», «dekket» — and the green example

- **«tolket»** = BirkNext split the sentence. It is shown now as «tolket av BirkNext», with the original sentence as a tooltip.
- **«mangler»** meant *the fragment's words were not found in the plan's manual cases*. It did **not** mean a missing feature, a missing developer test or a missing case field.
- **«dekket»** for «retryable failure with its original data preserved» was **not developer-test coverage**. It was a word match against a *manual case* in the same plan.
  - No test class, method, assertion or relationship was involved, so **it could not be explained with test evidence**.
  - It is therefore no longer shown as green or «dekket». It reads «Nevnt i et testtilfelle» (neutral), with the case titles as a tooltip.

## G. Actions vs expected results (IMPLEMENTED)

**Changes to `StoryInterpretation.Objectives`:**
- **Actions:** a sentence without «confirm/verify» that starts with an action verb («submit», «send», «register», «wait», «allow», «trigger» …, also after «then/first/next/finally») is a **Condition (action)**, not an expectation. An explicit action is not marked as interpreted.
- **The list split:** a «confirm/verify» list is split at a comma **only where a new item begins** (the/a/an/no/its/their/that/whether/all/each/every, optionally after «and»). Adjective lists, negations and timing clauses stay whole.
- **Unchanged:**
  - genuine lists still split («the stored outcome, the rejection reasons, and that no duplicate exists» → 3);
  - unknown sentences stay «Verification, needs review».

**The example now reads:** 2 actions, each followed by one whole expected result, in source order.

## H, J. Tester-first UI (IMPLEMENTED)

**«Uavhengig test» in Planlegg (and «Slik testes brukerhistorien alene» in Finn og forstå):**
- a **numbered list of steps**: the action, then «Forventet: …» for each expected result after it (`PlanPreparePresentation.PreparationSteps`);
- only an expected result gets a neutral mention state: «Nevnt i et testtilfelle» or «Ikke nevnt i planens testtilfeller» (in Finn og forstå «Nevnt i et scenario» / «Ikke nevnt i scenarioene»);
- **an action is never a gap;**
- the chip «x/y forventede resultater nevnt i testtilfeller» counts **expected results only** (there is no «1 of 6» over actions), in a muted tone;
- a fixed note: «Dette er testforberedelse, ikke utviklertestdekning. Utviklertester vurderes per akseptansescenario («Hva er allerede kontrollert?»).»

**Developer-test coverage** stays where the evidence is: per acceptance scenario (`TechnicalCoveragePresentation`, plus the delivery 5 checkpoints that count as checked only behind a reviewer's full-coverage assessment).

## I. Test boundaries

- **Unchanged:** coverage is never inferred from a fragment. «Delvis» and «Full» remain reviewer assessments.
- **NOT IMPLEMENTED:** a mock/emulator boundary analysis of developer tests.

## K. Translation

- **Shown in the chosen language:** the steps follow the story card's language (Norsk shows the stored translated Independent Test above the list).
- **Still in the original:** the extracted fragments come from the original text, because the extraction runs on the source.
- **NOT IMPLEMENTED:** extraction from the translated text.

## M. Preservation

- **Read-only:** the presentation only reads.
- **Unaffected:** no saved case or step is touched; plans, approvals and revisions are unchanged.

## O–P. Tests

- **Frontend:**
  - `PreparationStepsTests` (new, 3): the Barn example → exactly two actions, each with its whole expected result, in order, and explicit actions not marked as interpreted; a real list still split at new items; only an expected result can be «mentioned», and actions are never coverage;
  - updated: `ManualTestingPlanEditorTests.L14` (4 expected results; conditions are actions with «tolket» kept; the chip counts expected results) and `DiscoverWorkspaceTests` (the mention wording, never «Dekket»).
  - Discover/Planlegg/workspace tests **542/542**.
- **Backend:** the interpreter tests were running at commit time.

## Q. Browser

**NOT VERIFIED** in the browser in this commit.

## S. Remaining limitations

- **No per-checkpoint link to a developer test assertion:** there is no such relation in the model, so none is shown.
- **The action-verb list is a fixed vocabulary:** an action phrased otherwise stays «Verification, needs review» (honest, not hidden).
