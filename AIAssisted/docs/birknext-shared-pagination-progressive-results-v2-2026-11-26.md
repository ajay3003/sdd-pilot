# BirkNext — shared pagination and progressive results V2 (2026-11-26)

Commits:
- `5314ad7b`: server search, window, pager, V9 Finn og forstå.
- `7b1eee79` and `956ce3d7`: fixes found by the browser journeys.

The user asked for commits («commit all before continuing»), overriding the brief's no-commit line; nothing was pushed.

## A. Existing implementation (audited)
- **Shared «Vis flere»:** `Shared/Components/Results/ShowMore.razor` (V1, nine lists), client-side.
  - It reveals rows already in memory; the list filters and searches its whole loaded population first.
  - **Kept unchanged.**
- **Before V2, the population limit:** discovery *truncated* the scenario population at 10,000 in `TestScenarioDiscoveryEngine.Finalize`. The rest was not stored, not searchable and not usable by the server (plans, work queues).
- **Requirements Traceability:** the full matrix rendered `VisibleMatrix.Take(500)` and only said «Showing 500 of N rows». Rows past 500 were reachable only by filtering.
- **Backend paging:** there was no shared paging convention in the plan/discovery API. No list endpoint had paging.

## B. Decision matrix (lists considered)

| List | Population | UX | Data strategy | Status |
|---|---|---|---|---|
| Finn og forstå scenarios | up to the whole project (M2LB 2,015; synthetic 12,000) | **Vis flere** (kept) | Window of 10,000 sent; beyond it, **server search** with paging | IMPLEMENTED |
| Finn og forstå groups / requirements | per group / specification | Vis flere (kept) | client | unchanged |
| Legg til scenarioer, Testplaner list, source changes, config rows, gaps, coverage journeys/behaviours | small to medium | Vis flere (V1) | client | unchanged |
| **Requirements Traceability – full matrix** | can exceed 500 | **numbered pages** (new) | client (the report is built in the page) | IMPLEMENTED |
| Execution history (per occurrence) | small | none | — | not needed |
| Source Management files, Delivery tables, audit lists | not measured in this pass | — | — | NOT CONVERTED (see P) |

## C. Shared frontend
- `ShowMore` (V1) is reused for the server search results, with real asynchronous loading: spinner, retry, no double click.
- **New `Shared/Components/Results/Pager.razor`:**
  - «Viser 21–40 av 248 · Side 2 av 13»;
  - Forrige/Neste;
  - a compact window (first, last, current ±1, ellipsis);
  - `aria-current="page"`;
  - nothing shown for one page or an unknown total (no invented last page).
- There is no generic data hook: the two consumers keep their own domain logic. A generic abstraction was not justified.

## D–E. Backend contract and strategy
- **`TestScenarioDiscoveryEngine.Finalize` keeps the COMPLETE population**, and the stored result holds every candidate.
- **`Window(result, 10,000)`** is applied only where a result is *sent* (`POST discover`, `GET discover/last`):
  - the first 10,000 in source-priority order (specification, documented tests, guide, journeys, UI, API, existing/developer tests), stable within a source;
  - plus `NotKept` and a limitation text.
- **`GET discover/search?projectKey&scope&q&skip&take`** → `ScenarioSearchPage(DiscoveryId, Query, Total, Skip, Items)`:
  - searches the candidates **beyond** the window (the client already holds the rest);
  - every word must match, case-insensitive, over title, purpose, expected result, area, requirement ids and user story;
  - the same order as the window;
  - `take` limited to 1–200; an empty query returns nothing, never everything.
- **Offset paging** (`skip`) is right here: the stored result is immutable per discovery id, so offsets are stable. There are no page jumps; it is a «Vis flere» continuation. A total is computed because filtering an in-memory result is cheap.
- **Scope:** the same `LastAsync(projectKey, scope)` lookup as `discover/last`. Only the project's result for that selection is searched, so there is no cross-project data.

## F. Search correctness — the acceptance criterion
«Scenario nr. 12 000 must be searchable directly, without loading the 11,999 before it.»
- **Backend:** `DiscoveryCapTests`:
  - 12,000 candidates are kept;
  - the sent window holds 10,000 with the source-backed ones first; `NotKept` is 2,000;
  - the search finds position 12,000 alone;
  - 10 pages of 200 give 2,000 unique matches with no gaps;
  - `HasMore` is false at the end.
- **Frontend:** `DiscoverScopeAndActionTests`:
  - with an incomplete population, a search also queries the server once per settled query (debounced, newer query wins);
  - shows 40 of 41 matches; «Vis flere» continues at 40;
  - the far match opens in the detail panel;
  - a complete population never calls the server.
- **Browser (synthetic 12,000):** see S.

## G. 10,000 limit analysis
| Question | Now |
|---|---|
| Limits source parsing? | No |
| Limits what is stored and searchable? | **No** (it did before V2) |
| Limits the API response? | Yes: the window of 10,000 sent at once |
| Affects coverage denominators? | No: server-side aggregates read the stored result, now complete |
| Affects grouping and counts in Finn og forstå? | Counts on the page are for the loaded window. The page says how many are not loaded («Ikke alle scenarioer er lastet inn … Søk finner dem i hele utvalget»). |

## H–I. Grouping and selection
- Grouping is unchanged; it works on the loaded window.
- A server match that is opened joins the page's scenarios, classified like the others.
- Selection is by canonical id, independent of batches (V1).

## J. Cache and concurrency
- Search requests carry a generation number, so a newer query drops older answers.
- «Vis flere» continues from the number of rows loaded, and duplicates are filtered by id.
- The discovery ETag/304 cache keys on the bytes actually sent, so the windowed and full forms never share one.

## K. Counts
No KPI formula changed. The incomplete-population notice is shown where the list is.

## L. Performance
- **Compact payload (V1):** M2LB 9.2 MB → 6.9 MB.
- **V2:** the window caps what is sent; a server search returns 40 rows per page.
- Browser timings: see S. No other before/after claims.

## M. Tests
| Suite | Result |
|---|---|
| Backend `DiscoveryCapTests` | 4 tests |
| Backend discovery and planning classes | 45 passed, 1 skipped |
| Backend `SourceDrivenPlanningTests` | 26/26 |
| Frontend `PagerTests` | 4 |
| Frontend Discover server-search, detail-panel and filter tests | pass |
| Frontend full suite | 9,872 / 9,872 (before the last additions, which pass in their classes) |

## N. Browser
See S below.

## O. Changed files
- Backend: `TestScenarioDiscoveryEngine.cs`, `TestScenarioDiscoveryService.cs`, `TestPlanSuggestionsController.cs`
- Contracts: `shared/TestScenarioDiscoveryContracts.cs` (`ScenarioSearchPage`)
- Frontend: `TestPlanSuggestionsApiService.cs`, `ManualTestingDiscover.razor`, `ArtifactTraceability.razor`, `Shared/Components/Results/Pager.razor`, `components.css`
- Tests above

## P. Not converted, and why
- **Source Management files, Delivery tables, audit/event lists:** not measured, and no evidence that they need paging in this pass.
- **Infrastructure dependencies list:** V1 pattern, English UI.
- **Server-side paging for the matrix:** the matrix is built in the browser from the report, so client paging is sufficient.

## Q. Risks
- Grouping and counts in Finn og forstå cover the loaded window when a project exceeds 10,000. The page says so, but group counts are not global.
- The server search covers what the page's own search reads, but not the page's filters (priority, type …). Those apply to loaded rows. A filtered server search is the next step if needed.

## R. Acceptance
| # | Criterion | Status |
|---|---|---|
| 1 | Vis flere preserved | VERIFIED (browser journeys, V1 tests) |
| 2 | Large tables paginated | IMPLEMENTED (traceability matrix) |
| 3 | Shared backend paging reused | PARTIAL (one endpoint; no other endpoint needed it yet) |
| 4 | Search covers all eligible data | IMPLEMENTED + VERIFIED (tests; browser in S) |
| 5 | Filter and sort before paging | VERIFIED (window/search order, tests) |
| 6 | Group and aggregate counts correct | PARTIAL (see Q) |
| 7 | No scenario silently hidden | IMPLEMENTED (complete storage, visible notice) |
| 8 | Selections survive | VERIFIED (journey E) |
| 9 | Canonical identities intact | VERIFIED (ids unchanged; tests) |
| 10 | Stale requests cannot overwrite | IMPLEMENTED (generation guard) |
| 11 | Truthful spinner | VERIFIED (tests) |
| 12 | Recoverable errors | IMPLEMENTED (ShowMore retry) |
| 13 | Performance measured | PARTIAL |
| 14 | Regression suites pass | VERIFIED (2026-11-27): frontend 9,876/9,876; backend 7,626/0 failed/20 skipped, partitioned (see the backend stability report). |
| 15 | Browser verification | see S |

## S. Browser — scenario beyond 10,000 (VERIFIED, 6/6)
Isolated v1env29; script `scratchpad/v1/v1_over10k.py`.

**Synthetic package:** one Spec-Kit specification with 120 user stories × 100 acceptance scenarios = 12,000.
- The first attempt (1.7 MB `spec.md`) was not read as a specification: per-document limit `ArtifactExplorerContext.MaxImportBytes` = 1 MB.
- The package was regenerated at 766 KB.

| Check | Result |
|---|---|
| Incomplete population said | «Ikke alle scenarioer er lastet inn: 2001 scenarioer utover de første 10000 vises ikke i listen (kildescenarioer først). Søk finner dem i hele utvalget.» (12,001 candidates: the 12,000 scenarios plus one requirement candidate) |
| List shown | 2.0 s after selecting the specification (the window of 10,000) |
| **Search «post 11999»** | **found in 0.66 s** among «Treff blant scenarioene som ikke er lastet inn»; **not** among the loaded rows, so it was beyond the window and never loaded |
| Opens | the detail panel shows «AC 99 — Post 11999 — sjekk» (US120) |
| Console | no errors |

**Measured cost:** the window of 10,000 is **15.7 MB** on the wire (compact form) for this synthetic specification. It is acceptable locally (2 s), but it is the next thing to reduce. Recommended: a smaller window (e.g. 2,000) now that server search reaches everything beyond it. NOT CHANGED in this pass.

Screenshots: `scratchpad/v1/v1env29/shots/over10k-notice.png`, `over10k-search.png`, `over10k-open.png`.
