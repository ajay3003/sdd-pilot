# Deep Frontend Functional Test Discovery — implementation report

**Date:** 2026-11-13 · branch `008-traceability-first` · local commit only (no push, no deployment, no remote calls, no QA/TT02 traffic).

## Acceptance criterion

If the M2LB Frontend contains search, forms, validation and user actions, BirkNext must discover them and suggest relevant functional tests, not just "BarnSearchPage opens". A manual tester must be able to review and improve the suggestions without the Edge Companion and without understanding the source.

## Root cause (confirmed in code)

`SourceUiExtractor.Page()` recognised only built-in Blazor inputs, `<button>`/`<a>` and `@onclick`. Every other uppercase tag was an opaque "component", and only inside a form. M2LB's search page renders `<PersonSearchComponent />`. Its behaviour is a plain `<input @oninput @onkeyup>` plus `RadzenButton Click="SearchAsync"`, so the page contributed only a heading, and the engine could only produce "page opens".

## What changed

| Area | Change |
|---|---|
| Composition | A routed page takes the controls, forms and messages of every component it uses whose `.razor` file is in the archive. Depth ≤ 3, ≤ 12 components per page, each component once (cycle-safe). The controls keep their declaring file (`SourceFile`), and a component used inside `@if`/`@foreach` makes them conditional/repeated. A component name that matches two files is ambiguous and is not composed. |
| Component libraries | Radzen, MudBlazor, FluentUI, Telerik and Syncfusion buttons, fields, selects, date/number pickers, checkboxes, grids, links and forms are recognised by name and parameters (`Text`, `Click`, `Change`, `@bind-Value`, `Disabled`, `ButtonType`, `Placeholder`, `aria-label`). Radzen validators mark their control as required. |
| Handlers | `@oninput`/`@onchange`/`ValueChanged` on inputs, and key handlers (`@onkeyup`, …). A lambda is reduced to its method name, and an inline assignment becomes `set _field`. An icon ligature ("close", "arrow_back") yields to the aria-label as the button's name. |
| Intent | Each control gets a hint from its own words: Search, Clear, Filter, Save, Delete or Open (Norwegian and English). |
| Messages | Static empty-result, error and loading texts ("Ingen treff", a grid's `EmptyText`) are kept as text, file:line and whether they are conditional. |
| Suggestions (`suggest-v2`) | A search pair (input + search button in the same file, or a search-as-you-type input) yields **Search** (a known record from approved Test Data `${data:search.known-<noun>}`), **SearchNoMatch** (synthetic, or Test Data when the field is identifier-like), **SearchEmpty** and **ClearSearch**. Buttons with an action yield **UserAction**, except buttons inside `@foreach` or dialog/modal components and retry/cancel/close buttons. |
| Expected results | The source rarely states what a hit shows, so there is no assertion. Instead an **ExpectedHint** (with its basis) is offered for a person to confirm. An empty-result text counts only when it is declared in the search's own file or names a search, so the "no children in this department" text is not used. |
| Classification | `Category`: UI Smoke (page opens, navigation) or UI Functional (everything else). UI source alone never yields Business E2E. `TestDataState`: NotNeeded, SyntheticGenerated, NeedsApprovedTestData or ProvidedByReviewer. |
| Manual review | `POST api/critical-e2e/suggestions/manual-review` takes the expected result in the tester's words, the Test Data reference, notes, and a decision (Draft, ReadyForManualTest or Rejected). Ready needs an expected result, plus a Test Data reference when one is needed. Text that looks like a national identity number is refused. The review is human content, so it is kept across snapshots, unlike locator verification. It is never an automation approval. |
| UI | Suggested tests shows UI Functional / UI Smoke filter chips (functional listed first) and a "Prepare manual test case" panel. The panel has plain-language steps with Test Data named, never shown as a placeholder, plus "Use this" on the hint, expected/Test Data/notes fields, Save draft / Ready for manual test / Reject, and "Add to a test plan" (→ Test Plan Suggestions). The Test Plan Suggestions lead links back to Suggested tests. |

## Verification

- **Synthetic fixture** (`DeepFrontendDiscoveryTests`, hand-written in M2LB's shape): composition, library controls, intent, messages, the four search suggestions, hints never asserted, no-component fallback, a bounded/cycle-safe/ambiguous composition, and manual review without the companion (a strict mock that is never called). Backend Critical E2E tests: 190/190 passed.
- **Frontend**: `CriticalE2ESuggestionsTests` adds a manual-test-case test (filter, order, plain steps without `${data:`, use hint, ready request, no Verify call).
- **Real M2LB archive (offline, read-only)**: `testdata/M2LB.zip` was run through the extractor and engine by a scratch-only probe that is not committed. Result: 59 UI files, 13 routed pages, **45 suggestions = 11 page checks + 34 UI Functional**. That covers search on `/person` and `/` (known / no match / empty / clear), search-as-you-type on `/admin/child-specific-roles`, `/admin/general-roles` (two searches, told apart by field) and `/admin/user-access`, and 14 user actions (Opprett, Tildel rolle, Lagre, Neste side, …). Before this change the same archive yielded page checks only.

## Not done (remaining)

- No browser acceptance against the running M2LB frontend, and no Edge Companion verification of the new locators. Every suggestion stays NeedsDomVerification until verified.
- Filter panels (e.g. audit-log filters with `EmitChange`) are recognised as controls but get no filter scenario yet.
- An automation approval of a search still needs Test Data **values**. The `${data:…}` alias is named, but approval is refused while Test Data needs are open, and wiring the central Test Data resolver into suggestion approval is not done.
- No key action in the browser vocabulary: "Enter starts the search" is a manual variant, stated as a limitation.
- Component parameters are not evaluated: a component that renders differently per use is described once.
- Multi-step flows across pages (search → open detail) are not chained. Business E2E needs evidence beyond UI source.
