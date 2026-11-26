# Manual Testing translation consistency — audit and fix — 2026-11-26

**Safety:**
- no database change or migration;
- no source text changed;
- no translation created, overwritten or retranslated;
- no revision created by reading or by switching language;
- no network translation.

**Status keys used:** IMPLEMENTED · VERIFIED (tests) · NOT VERIFIED (browser) · NOT IMPLEMENTED.

## A–B. Translation architecture (existing, reused)

| Part | Where | Notes |
|---|---|---|
| Case translation | `TestCaseDraft.Translations["nb"]` → `TestCaseTranslation` (`shared/ScenarioCentricPlanning.cs`) | title, purpose, preconditions, steps, step results, expected result, the scenario's **Given/When/Then**; `Origin` (Machine / MachineEdited / Manual); `SourceFingerprint` binds it to the original text |
| User story translation | `TestPlanDraft.StoryTranslations` → `StoryTranslation` | title, description, why this priority, independent test; fingerprint via `ScenarioPlanning.StoryFingerprint` |
| Requirement wording | `TestPlanDraft.RequirementTranslations` | display only |
| Producing translations | the existing «Oversett» / «Oversett alle» actions (offline embedded translator by default) | explicit user action only; nothing here calls them |

**What exists:**
- translations live **inside plans** and are linked to stable identities (case id, story key);
- staleness is detectable by fingerprint.

**There is no second translation store, and none was added.**

## C–F. Audit by view: root causes

| View / field | Before | Root cause | Fix |
|---|---|---|---|
| **Planlegg** — the language choice | reset on every case: `Open()` set `_lang = DefaultLanguage(form)` (Norsk only if *that* case had a translation) | no preference; a case without a translation always opened in «Original», and the choice was lost on switching case, tab or reload | **IMPLEMENTED:** one preference (`ManualTestingLanguage`, `localStorage` key `birknext.manualTesting.language`, per browser, so per tester), read on load and written on click; `Open()` uses it |
| **Planlegg** — the scenario heading (AC title) | always the original (`CaseEditorPresentation.ScenarioHeading` read `Generated.Title`) | ignored the case's stored translation | **IMPLEMENTED:** `ScenarioHeading(c, language)` uses the stored Norwegian title when it is current and no person rewrote the title |
| **Planlegg** — the user story card | already used the plan's `StoryTranslations` with a stale/untranslated note | — | unchanged; now follows the kept preference |
| **Planlegg** — case fields (title, purpose, preconditions, steps, results) | Original/Norsk/Sammenlign editor with empty-translation and stale notices | worked, but reset per case | now follows the kept preference |
| **Finn og forstå** — US title, AC titles, expected result | **always original**: no translation was read at all | translations live in plans; Finn og forstå had no access to them | **IMPLEMENTED:** the work view (`WorkQueuesAsync`, which already reads every plan once) returns the newest stored **story** translations and, per scenario, the newest stored **case translation of that scenario** (a case prepared from it whose title no person rewrote), with `Current` (fingerprint match). Finn og forstå has its own Norsk/Original/Sammenlign control using the same preference. |
| **Finn og forstå** — classifications, technical evidence | Norwegian UI labels; code identifiers in the original | correct | unchanged (identifiers are never translated) |
| **Testoversikt / Testplaner** | Norwegian chrome; plan names are user data | correct | unchanged (plan names are not translated) |

## G–J. Coverage

| Content | Status |
|---|---|
| US title | **IMPLEMENTED** in Finn og forstå (card) and Planlegg (card): one stored translation, current only |
| US description | **IMPLEMENTED** in Planlegg; **NOT IMPLEMENTED** in the Finn og forstå card body (the title only) |
| AC title | **IMPLEMENTED** in Finn og forstå (rows and drawer title) and the Planlegg heading |
| Expected result | **IMPLEMENTED** in the Finn og forstå drawer (`Then`/`ExpectedResult` of the stored translation) |
| Given/When (drawer detail lists) | **NOT IMPLEMENTED**: still original in the collapsed «Mer om scenarioet» |
| E2E/standalone scenarios | same path (by scenario identity; no US needed); shown when a case of the scenario holds a translation |
| Case fields | existing editor (title, purpose, preconditions, steps, results) |

## K. Original / Norsk / Sammenlign

- **Norsk:**
  - shows a stored translation that is **current** (its fingerprint matches its source);
  - otherwise the original, with «Norsk oversettelse mangler – viser originalen» or «Norsk oversettelse er utdatert – viser originalen».
  - A stale translation is never shown as current.
  - The origin is stated («Maskinoversatt – ikke gjennomgått» / «Maskinoversatt, redigert» / «Oversatt av en person»).
- **Sammenlign:** the translated text, plus the original beside it (title and expected result in the drawer).
- **Original:** source text only.

## L–N. Persistence, snapshots, overrides

- **Read-only:** the language choice and display only read.
- **Shown only from its own source:**
  - a case translation is used for its own scenario only (identity match, never a title);
  - a translation whose case title was rewritten by a person is not used as the scenario title.

## O. Cross-tab

The same preference is read by Finn og forstå and Planlegg og klargjør and survives reload, back/forward and case or plan switching (it lives in `localStorage`, not in component state).

## P. Offline

No change: nothing calls a translator.

## Q. Loading

While the work view loads, Finn og forstå shows originals (there is no translation to show yet). Titles switch to Norwegian when the stored translations arrive; no request is repeated.

## R. Performance

There are no new requests. Translations come with the existing single work-view request, and the client does an in-memory lookup per row.

## S–T. Tests

- **Backend:** `ScenarioWorkQueueTests.The_work_view_reuses_a_stored_case_translation_for_its_scenario_and_translates_nothing_itself`:
  - nothing stored → none;
  - a stored Norwegian title and «Then» → returned as current, for that scenario only;
  - reading writes no revision.
  - The result was running at commit time.
- **Frontend:**
  - `ManualTestingLanguageTests` (current translation with origin, Sammenlign keeps the original, a stale one is never current, a missing one is labelled, the expected result, story title currency) — **2/2**;
  - all Discover, Planlegg and work-queue tests — **538/538**.

## U. Browser

**NOT VERIFIED** in the browser in this commit.

## W. Remaining gaps

- **The 60 listed frontend checks** (sections 17–18 of the brief) are covered only in part, by the tests above.
- **Finn og forstå:** the US description, Given/When, requirement wording and the «Uavhengig test» texts stay original.
- **Testplaner overview and transfer previews** show case titles in the original.
- **The language control** is per page (two controls sharing one preference), not a global header switch.
