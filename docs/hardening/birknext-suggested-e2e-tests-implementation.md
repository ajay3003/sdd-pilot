# BirkNext — Intelligent suggested E2E test generation: implementation (2026-11-10)

Branch `008-traceability-first`. Baseline `0fca2f6f`. Audit: `../audits/birknext-suggested-e2e-tests-audit.md`. No push, no deployment.

This extends the existing Critical E2E Regression. There is no second engine and no second test format: a suggestion becomes an ordinary `CriticalE2EFlowDefinition`, and only that flow is ever executed.

## 1. UI facts from source (offline)

| Element | Implementation |
|---|---|
| Reading | `IqrSourceArchiveReader` keeps `.razor` / `.cshtml` / `.html` / `.htm` text in memory for the analysis only. **Bounds:** ≤ 600 files, ≤ 512 KB each, ≤ 16 MB in total. **Skipped:** bin, obj, node_modules, lib, dist. **Never stored.** |
| Extraction | `Services/CriticalE2E/Suggestions/SourceUiExtractor.cs`, regex-based and bounded (≤ 400 files, ≤ 200 controls per file); Razor is not executed. Per page it records: `@page` routes; `PageTitle` / first `h1`; `[Authorize(Roles)]`; requirement ids from comments (built-in prefixes only); forms (`EditForm` / `form`, model type, submit handler, validator); controls. Each control has kind, role, test id, static id, label (`label for`, wrapping label, aria-label, placeholder as last resort), bound member, input type, required, min/max length, range, pattern, **required message** and **constraint message** kept apart, options, handler, line, and conditional (`@if`/`else`) / repeated (`@foreach`/`@for`/`@while`) flags. DataAnnotations come from the bound model class in the `.cs` files; the field → type map comes from `@code` and code-behind. A custom component is listed as a component with a limitation, never as a DOM element. |
| Storage | `IqrSourceSnapshot.UiSurface` (facts only). Snapshots analysed before this have none, and the page says to re-import. |

## 2. Suggestions (offline, deterministic)

`SuggestionEngine` (rule set `suggest-v1`). The suggestion id is a hash of the rule version, project, route, kind and controls, so it is stable across snapshots.

| Kind | Steps | Expected result: only from explicit evidence |
|---|---|---|
| Page opens | Navigate → AssertVisible on the heading | the page's own heading in source; otherwise the route itself |
| Navigation | Navigate → Click link → AssertRoute | the link's href plus the target page's `@page` route in the same snapshot |
| Required fields | Navigate → Click submit → validation assertion | with one required field and a declared `ErrorMessage`, its text (`ul.validation-errors` contains it); otherwise only the summary's presence; with neither a ValidationSummary nor exactly one ValidationMessage, **none** |
| Invalid input | Navigate → valid values for the other required fields → one value violating exactly one constraint (max length + 1, range max + 1) → submit → validation assertion | that constraint's own declared message; otherwise presence only. The required message is never reused. |
| Valid submission | Navigate → valid values → submit | **none**: NEEDS_EXPECTED_RESULT. A reviewer may state the expected route at approval. |

- **Values.**
  - Synthetic values are generated from the declared constraints (text fitted to min/max, number = range min, a fixed date, `synthetic@example.invalid`, the first static option).
  - **Never generated:** identifier-like fields (id, nr, nummer, fnr, fødselsnummer, personnummer, ssn, orgnr, reference, token), passwords, pattern-constrained fields and files. These become NEEDS_TEST_DATA.
  - Conditional or repeated fields are never filled.
  - A form with several submit buttons is not suggested.
- **Locators**, in priority order: test id → role + accessible name → label → static `#id` → scoped CSS fallback, which is used only for the stated Blazor validation conventions. Never positional, never an expression id.
- **Coverage** against existing flows of the environment:
  - same route and every control's selector → ALREADY_COVERED;
  - same route with some selectors → PARTIALLY_COVERED ("extend it rather than adding a copy");
  - same requirement on another route → OVERLAP_REQUIRES_REVIEW;
  - otherwise NEW_SUGGESTION.
- **Readiness states** (separate from execution): NEEDS_DOM_VERIFICATION, NEEDS_TEST_DATA, NEEDS_EXPECTED_RESULT, READY_FOR_REVIEW, APPROVED, EXECUTABLE / EXECUTED (from the approved flow's run history). Validation output is excluded from DOM verification: it exists only after a submission.

## 3. DOM verification through the real Edge Companion

- **Contract.** `CompanionActionKind.VerifySelectors` with ≤ 16 `Selectors`. The result carries `SelectorChecks` (`CompanionSelectorCheck`: match count, visible, enabled, tag, role, accessible name, limitation). Capability `selector-verify`. It is an authoring action: `ConfigurationProblem()` refuses it as a flow step.
- **Backend gate** (`BrowserCompanionService.Dispatch`): capability required, 1–16 selectors, plus every existing gate (session, non-production, approved origin, the bound live page, one command in flight). The result is sanitised: capped fields, ≤ 16 checks.
- **Extension** (`lib/automation.js` `verify`, `content.js`):
  - uses the replay resolver itself (`matches`), so "unique" means what a run will resolve;
  - identity only: no values, no HTML, no text beyond an 80-character accessible name, and none for password fields;
  - frames are reported as not searched;
  - bound to the content-script instance;
  - no manifest change.
- **Password guard** (security fix): `ReadValue` / `AssertValue` refuse password fields; `Fill` of a password never echoes the value.
- **Service** (`CriticalE2ESuggestionService.VerifyAsync`): needs a connected extension with the capability, exactly one approved live page, and that page on the suggestion's route (otherwise "open {route}", and nothing is dispatched). Each step's evidence records the state, the chosen selector, all checks, the observed origin and route, the time and **the source snapshot**. A suggestion regenerated from another snapshot shows those verifications as STALE.

## 4. Review, approval, execution, impact

- **Decisions** (`DecideAsync`; who and when kept; history in `App_Data/critical-e2e/suggestion-reviews.json`):
  - Accepted: only the locator verified unique.
  - AlternativeChosen: a unique checked alternative, or a manual locator with a written reason. A locator matching several elements is refused.
  - Rejected / Unresolved.
- **Approval** (`ApproveAsync`) refuses when: not every control step is mapped; Test Data is missing; no expected result exists (unless the reviewer states a route); or the suggestion is already covered.
  - **What approval saves:**
    - a **Diagnostic** flow through `ICriticalE2EService.SaveFlow`, with `Source = SuggestedFromSource` (additive enum value) and `RequirementRef`;
    - tags `suggestion:`, `snapshot:`, `project:` and the rule version;
    - an automation boundary, "Browser UI only: what the application does after the submission is not verified";
    - a final outcome derived from the final step.
  - Approval never executes. The flow runs only from Critical E2E Regression (Run), through the existing runner and Companion, and its results appear in Test Execution & Evidence as Browser E2E.
- **Impact** (`GenerateAsync` with a newer snapshot): approved suggested flows of the project whose snapshot tag differs are checked against the new facts. The possible proposals are a removed route (TEST_MAY_BE_OBSOLETE / ASSERTION_REVIEW_NEEDED), a selector no longer declared (LOCATOR_REVALIDATION_NEEDED), a length constraint that now rejects the flow's value (TEST_DATA_REVIEW_NEEDED) and new uncovered behaviour (NEW_TEST_SUGGESTED). Flows are never rewritten.

## 5. API and UI

- **API:** `GET api/critical-e2e/suggestions/snapshots`; `POST …/generate`, `…/verify`, `…/decide`, `…/approve`. Same local-caller filter as the rest of Critical E2E.
- **UI:** `Pages/CriticalE2ESuggestions.razor`, embedded in Critical E2E Regression.
  - Snapshot selector and Generate suggestions.
  - Counts: suggestions, existing tests reused, new, missing DOM mappings, missing expected results, missing Test Data, ready for approval.
  - Four areas: Test Suggestions, DOM Mapping, Test Data & Readiness, Execution Results.
  - Detail panel (no permanent split): expected result and its basis, origin, source file:line, preconditions, Test Data needs, steps with locator candidates and what the attended page showed, plus Verify DOM in Edge / Accept / Use this / Reject / Mark unresolved / Approve / Open existing test.
  - Regression impact, collapsible.

## 6. Tests

- **`SuggestedTestsTests` (6, backend):** UI facts without markup; expected results only from evidence (required vs constraint message, conditional field never filled, identifier and credential never generated); locator priority; duplicate detection; verification states; the service flow with a mocked Companion (route and capability gates, ambiguous alternative refused, Accept, Approve to Diagnostic, covered after approval, stale across snapshots, impact without rewriting).
- **`CriticalE2ESuggestionsTests` (5, bUnit).**
- **Extension `tests/verify.test.mjs` (6):** in a real browser fixture — identity only, ambiguity reported, bounds, frames, password guard. The full extension suite passes: 149, 1 skipped.
