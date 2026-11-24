# Manual Testing — interpretation, FR traceability and test design (2026-11-24)

Branch `008-traceability-first`. Three queued tasks, built on each other and on existing engines (no second parser, traceability engine,
discovery engine, test-plan system, translation provider or case editor):

1. **Blocker — Complete manual-testing interpretation & UX** (shared interpretation engine + one Norsk / Original / Sammenlign editor)
2. **FR Traceability Integration** (explicit / suggested / confirmed FR links, coverage levels, change impact, «Tilknyttede krav»)
3. **Intelligent Test Design & Execution Readiness** (execution suitability, test oracle, test data, readiness, design techniques, benchmark)

Also contains earlier uncommitted Manual Testing work from the same session: the «Utgått scenario» correctness fix and the Finn og forstå
specification/discovery UX refinements (scope picker actions slot, filters, counts, priority filter, queue hints).

Test results are at the end; the limitations are listed with what each means.

---

## 1. Blocker — interpretation & unified editor

### «Utgått scenario» (correctness first)
Root cause: the current discovery had been run with *another* specification selected, so the plan's scenarios were simply absent — and absence
was treated as confirmed removal. `TestPlanSuggestionService.Sources.Absence()` now returns **Kilde mangler** (SourceUnavailable) when the
scenario's own specification was not read in the latest discovery. Verified on the real plan «Testplan 2026-11-24»: 2 removed → 0 removed,
2 source unavailable, plan revision unchanged.

### Shared interpretation (`AcceptanceScenarioInterpreter`, `StoryInterpretation`)
* Given → preconditions and test-data requirement, **verbatim** (values, inequalities, roles kept).
* When → one action; "it"/"they" resolved from the Given or the story. Classified as arrival / person acts / system processing / as written.
  *How* something is sent or triggered is never invented — it becomes an open question.
* Then → verification points split only at clause boundaries; negative assertions kept and flagged for access; "; where …" becomes a variant.
* User story: linked refs (`[US-B1, US-B2]`) kept apart from the story id (US1 is never an alias of US-B1); Independent Test split into
  conditions and verifications (interpretations marked for review). New drafts only — saved cases are never regenerated.

Before → after on real wording:

| Service | Before (generic steps) | After |
|---|---|---|
| SkoleService US1 | "Prepare the starting situation…" + "Carry out or trigger: it arrives" | "Send the report in as a private institution or a foster home." |
| SkoleAdapter | "Carry out or trigger: the change arrives" | "Make the change arrive (a new utdanningsforhold in BiRK)." |
| Person | "Carry out or trigger: they search by partial name" | "As the caseworker: search by partial name." |
| MeldingsutvekslerReceiver | "Carry out or trigger: the intake processes it" | "Trigger and observe: the intake processes the valid skolenærvær v2.0.1 message." |

### One editor, three views
Segmented **Norsk | Original | Sammenlign** over the *same* form: identical fields (title, purpose, preconditions, test data, steps with
per-step expected result, overall expected result), one step list aligned by identity (move/add/remove applies to both languages). Default
Norsk when a translation exists, else Original; never translates on open. Empty state «Ingen norsk oversettelse finnes ennå.» with the
existing Translate action. Partial translations are flagged per field («Ikke oversatt»), stale ones get a banner with a Compare shortcut,
reviewed ones say they are kept. Unsaved edits survive view switches. Collapsible «Brukerhistorie og testmål» (priority, why, linked
stories, Independent Test verbatim, objectives with which cases appear to cover them — a word-match suggestion, labelled as such).
Translated purpose is now editable and saved. **Walkthrough finding fixed:** the translator turned the route `/admin/child-specific-roles`
into "/admin/barnespesifikke roller"; app routes are now protected tokens.

## 2. FR traceability

Built on the existing `ScenarioPlanning.SuggestRequirements` (A explicit / B structural / C semantic / D weak), `RequirementLinkPresentation.Rows`,
tester-confirmed `ManualRequirementLink`s and the server's `PreparedCaseTraceView`.

* **Ids**: an id inside another id is not that id (`SR-FR-017a` is no longer read as `FR-017a`); suffixes are kept (FR-015 ≠ FR-015a).
* **Scope**: an explicit or structural match only counts inside the scenario's own specification — FR-001 of Frontend is never borrowed for
  SkoleService (the link-row lookup had the same cross-service collision and is fixed too).
* **Rule level, not words**: when both texts state a quoted outcome, the same outcome is the same rule and a different outcome is a different
  rule. SkoleService US1 scenario 2 («attendance above plan») suggests FR-015a and never FR-015 («inconsistent hour totals»), although both talk
  about absence sums; scenario 4 suggests FR-015, not FR-015a. A requirement threshold the scenario contradicts (24 vs 30) is not "same behaviour".
* **Relations stay apart**: Nevnt i kilden (explicit) / Bekreftet (by whom, when) / Bekreftet for scenarioet (never counted as case coverage) /
  Foreslått (A–D, with reason). A suggestion is never a link until a person confirms; rejecting hides it for that case only.
* **Coverage levels, separately**: in the specification · acceptance scenarios · drafts in the plan · approved in the plan · executed (passed)
  from completed manual runs across plans — "Utførelse ikke kjent her" when the trace is not available, never "not executed".
* **Change impact**: a requirement whose text changed since a link was made is classified **wording only** or **behavioural** (outcome,
  numbers, negation, MUST/SHOULD/MAY, outcome verbs), with each difference named. Missing requirements are kept, flagged, not counted.
* **UI**: «Tilknyttede krav» in the case's Kildegrunnlag (ids link to Requirements Traceability, `translate="no"`); confirm / reject / remove
  save only that change and send every other part of the plan as stored. Technical suggestions are hidden; technical links stay visible, tagged.

## 3. Intelligent test design & execution readiness

One deterministic, bounded advisor (`shared/TestDesignAdvice.cs`) over the text BirkNext already holds. Additional test designs are
`TestCaseVariantProposal`s inside the existing `ScenarioPlanning.Variants`, so **accepting one uses the existing add-from-variant endpoint**
(the server re-derives the proposal from the evidence and refuses it when it no longer applies).

* **Execution suitability** (several may apply, each with a reason): Manuell · Manuell + teknisk · Integrasjon / E2E · Automatisering anbefalt ·
  Teknisk · Trenger vurdering — from actor, trigger, observable result, precision, timing, repetition and the systems the text names (never
  from a service name, path or test name alone). Manual questions A–H answered Yes / No / Unknown.
* **Test oracle**: each assertion → what to verify and *where* (category: screen, processing outcome, persisted state, delivered output,
  flag/reason, absence across surfaces, timing). "Verifisering kreves" is kept apart from "Metode bekreftet", which needs a person-written
  observation method. No query, queue, path, endpoint or tool is named.
* **Test data**: the Given's requirements vs availability from linked Test Data assets (Confirmed / Må lages / Ukjent / Blokkert). A
  specification describing data never makes it available.
* **Readiness** mapped to the existing review states: Klar til forberedelse · Trenger klargjøring · Klar til gjennomgang · Klar til kjøring ·
  Blokkert. Unknown is never satisfied; "Klar til kjøring" is not an execution result; saving a draft is never blocked.
* **Techniques**: boundary values (inclusive/exclusive from the comparison words; expressions such as "24 × the number of days in its date
  range" kept whole; only the side the source states gets an expected result; values left to the contract), idempotency / order / concurrency
  (identity, version and arrival order kept apart; persisted state verified), access and confidentiality variants (only permissions the text
  names; "absent from one list is not enough"), state sequences (only states the text names), a bounded decision table (precedence from
  "MUST NOT be applied when FR-x applies", contradictory combinations excluded, unresolved pairs get no outcome, a rejection is never turned
  into acceptance by a flag) and cross-service E2E proposals (only named services; every checkpoint without a confirmed way to observe it is a
  visible gap).
* **Human review**: accept (adds a draft case via the existing path), reject or defer (saved with the plan — server records who and when),
  inspect the basis. The created case remembers its proposal key, so the proposal does not come back after the title is edited (walkthrough
  finding, fixed).
* **UI**: Finn og forstå shows the approach compactly on each row, an optional «Testtilnærming» filter (existing queues unchanged) and the
  panel in the scenario detail (read-only). Planlegg og klargjør shows «Testdesign og klargjøring» under the editor with readiness, verification,
  data, proposals and collapsed «Teknisk testdesign». Identical in all three language views; no source refetch on a view switch.

### Benchmark (real data)
`backend/BirkNext.Api.Tests/Services/TestPlanning/Fixtures/test-design-benchmark.json`: **57 acceptance scenarios quoted verbatim from
M2LB.zip, 11 services** (SkoleService US1–US7 incl. US1 scenarios 1, 2, 4, 9, 11; SkoleAdapter, Person, PersonAdapter, Tjeneste, Autorisasjon,
MeldingsutvekslerReceiver, Frontend, Revisjon, HendelseAdapter, Hendelse). Provisional output: `test-design-benchmark-results.md`.

**Human review: 0 of 57 reviewed — classification accuracy is not established.** What *is* measured, against the source text:

| Gate (checked on every scenario) | Result |
|---|---|
| Invented requirement references | 0 |
| Definitive expected results not stated by the source | 0 (7 proposals carry the source's own outcome) |
| Lost negative assertions | 0 (27 negative verification points) |
| Duplicate proposals | 0 |
| Techniques without their trigger words in the source | 0 |
| E2E services not named by the source | 0 |
| Verification methods claimed as confirmed | 0 (no case has a person-written method) |

Provisional distribution: Manuell 18 · Manuell + teknisk 11 · Integrasjon / E2E 16 · Automatisering anbefalt 6 · Teknisk 15 · Trenger vurdering 0.
27 proposals (Konfidensialitet 9, Grenseverdi 6, Tilgang 5, Idempotens 3, regelkombinasjon 2, Ugyldig inndata 1, Rekkefølge 1).
**Performance**: all 57 through interpreter + advisor + variants in ~250 ms / ~10 MB allocated; 600 discovered scenarios through the approach
filter well under the test budget (classification once per scenario, reused by later filters).

## Browser verification (real M2LB, disposable plans)

Blocker editor walkthrough: 10/10 (default Original, empty state, existing Translate, Norsk view, Compare aligns 3/3 steps, Norwegian edit kept
across views and after save + reload, reported as edited/protected, no horizontal scroll at 390 px).

ITD + FR walkthrough (specification temporarily switched to SkoleService 001 and restored afterwards with a re-discovery):
Finn og forstå on SkoleService 001 · threshold scenario → «Manuell + teknisk» with reasons · verification "metode ikke bekreftet" · three
boundary proposals from «more than 0.5» · idempotency («twice») → automation · Kode 6/7 → access variants · period lock → Teknisk · technical
design collapsed · «Tilknyttede krav»: FR-015 «Samme regel (samme utfall)», FR-015a not suggested · readiness «Klar til forberedelse» with the
trigger unknown · accept a proposal → new draft opens · edit + save + reload · language views keep the panel · no duplicate cases · renamed case
does not bring its proposal back · reject saved and still hidden after reload · 0 console errors, 0 API 5xx. Existing plan «Testplan 2026-11-24»
unchanged (revision/time 08:32); temporary plans deleted (archived then deleted by exact stamp).

Walkthrough findings fixed in this work: translated route; per-render recomputation of requirement suggestions froze the plan page (now
computed once per saved case revision); readiness reported a flag assertion as "confirmed"; rejecting the last proposal hid the rejected count;
an edited accepted case brought its proposal back; «Samme regel» label used for word overlaps.

## Limitations

1. **FR id casing**: the existing shared requirement parser (`RequirementReferenceParser.Normalize`) upper-cases ids, so FR-027a is shown as
   FR-027A. Identity is preserved (FR-015 ≠ FR-015a; all matching is case-insensitive), but the displayed casing differs from the source.
   Changing the parser would affect stored link matching across BirkNext and was not done here.
2. **Classification accuracy is not established**: no benchmark label has been reviewed by a person. The gates above are source checks, not
   accuracy. An expert should fill `humanReview` in the fixture.
3. **The interpreter and advisor are English-rule based**: Norwegian-only specifications get "as written" actions and fewer techniques.
4. **Verification surfaces are categories, by design**: "how" is only confirmed when a tester writes an observation method on the case.
5. **E2E proposals** read services from the scenario text; actual contracts/architecture are not traversed yet, so checkpoints are the
   scenario's own sentences.
6. **Decision tables** only read requirements written as "When/If …, … MUST be …"; requirements that state the outcome first (e.g. FR-016) are
   left out rather than misread.
7. **Existing cases accepted before `FromVariant`** (none in user plans) can bring their proposal back after a title edit.
8. **Objective coverage** in «Brukerhistorie og testmål» is a word-match suggestion, labelled as such.
9. **Browser repeat for Person/SkoleAdapter** was not done in the UI (it would require switching the module selection); those services are
   covered by the 57-scenario benchmark and unit tests. `RealM2lbManualTestingTests` (Playwright) was not run.
10. **Full backend regression incomplete** (stopped by the system for low memory; see Test results).
11. **Still open from before**: the ~14 s blocking review-context rebuild when the specification changes, and the Local Data Reset safety
    review.

## Test results

Run sequentially with `xUnit.MaxParallelThreads=4`, on the final code:

| Suite | Result |
|---|---|
| Frontend — full suite | **9551 passed, 0 failed, 0 skipped** |
| Backend — `TestPlanning` (all planning, interpreter, FR traceability, test-design, benchmark and service tests) | **350 passed, 0 failed, 0 skipped** |
| Backend — full suite | **Incomplete.** The run was stopped by the system for low memory before it finished. Its partial log had 2 failures (`ApiReviewEngineTests.CI_DEF_3_…`, `TlsChainInspectorTests.IncompleteChain_…`, both loopback/TLS timing tests outside this work); both pass when run on their own. The last *complete* full backend run in this session (before the FR and test-design tasks) was 7237 passed, 0 failed, 19 skipped. |

The full backend suite must be re-run before relying on it.
