# BirkNext V12 — major UX audit (2026-11-27)

**Status: AUDIT COMPLETE (read-only).** No code was changed for this audit.
- **Baseline:** branch `008-traceability-first` at `9af21c93` (V10 stabilization).
- **Uncommitted work present at the time** (other tasks): System Settings → Data Quality, the Testoversikt twin-scenario fix, SRC-001.
- **Other sessions:** none running. Two untracked openspec documents from an earlier session were not touched.

**Method.** Two code-level audits, with file:line evidence in their reports:
- the four Manual Testing pages;
- every other module.

They were combined with the V10 browser evidence (isolated M2LB runs) and the manual test guide's findings.

**Severity:**
- **P1:** blocks or misleads a daily task.
- **P2:** costs time or causes errors.
- **P3:** consistency and polish.

## 1. Module inventory
- **Navigation:** about 62 sidebar rows in 10 sections (`Services/NavigationCatalog.cs:96-262`); 75 routed pages; role «Workspace Views» regroup the rows.
- **Manual Testing** (Testoversikt, Finn og forstå, Planlegg og klargjør, Testplaner, Testgjennomføring) is the highest-frequency workspace.
- **Other large-list, high-frequency pages:** Specification Explorer, Requirements Traceability, Test Coverage Review, Source Management/Analysis, Release Readiness (Dashboard), Test Data, Delivery Reporting, Project Import, System Settings.

## 2. Prioritized UX inventory

### Manual Testing (reference workspace)
| # | Sev | Finding | Evidence |
|---|---|---|---|
| MT-1 | P1 | **Finn og forstå details are hidden below a 1,344 px page width**, and a row click only highlights the row; «Vis detaljer» is needed. Two breakpoint systems: container queries plus a JS pixel threshold. | `ManualTestingDiscover.razor:747,2225,2246`; css:517-535,812 |
| MT-2 | P1 | **Two primary «Forbered testtilfelle» buttons in one detail panel** (drawer header + plan block), plus outline-primary buttons per readiness criterion. | `:789`, `:2536-2544` |
| MT-3 | P1 | **Three readiness models with different wording on one scenario:** «Kildegrunnlag n/6», «Klargjør AC x av y påkrevde», «Før godkjenning», and «Klar til testing / Klar til kjøring» elsewhere. | Discover `:1026`; `ScenarioPreparationPanel:15`; Plan `:993`; `ScenarioPreparation.cs:148` |
| MT-4 | P1 | **Testplaner shows no Plan → Suite → Occurrence → Case hierarchy:** a flat plan list; execution flattens suites to a path string. | `SavedPlans.razor:33-108`; `PlanExecution.razor:37` |
| MT-5 | P2 | **Filters are spread across six places:** work queues, toolbar, 10 «Flere filtre», the Manuell/Teknisk switch (twice), list grouping, and rail mode. «Alle typer» is a duplicated label. | `:534,574,618,398,592,2418,132`; `:624/639` |
| MT-6 | P2 | **Three competing grouping systems:** rail mode, overview cards and the list's «Grupper etter». Default groups start collapsed (the V10 P1 root cause). | `:132,379,2419` |
| MT-7 | P2 | **Overloaded scenario row** (meta line, tags, requirement chips, plan line, count, status, button). Bulk-selected is shown by **bold only**; in-plan is shown by **colour only**. | `:2613-2637`; css:169,1072 |
| MT-8 | P2 | **Plan editor shows the create form and the edit form together**, plus a long scroll of context duplicated from Finn og forstå (story card, «Fra spesifikasjonen», «Kildegrunnlag»). | Plan `:309`, `:402`, `:461-602`, `:1008-1098` |
| MT-9 | P2 | **No direct «Legg til i testplan» in Finn og forstå:** the bulk action only navigates to Plan. | `:1140,2652` |
| MT-10 | P2 | **Execution page has no workspace header** (own h1 and breadcrumb). The completion figure «Gjennomført: x %» counts blocked as done. | `ManualTestingPlanExecution.razor:14-17`; `PlanExecution.razor:20-26` |
| MT-11 | P3 | **Inconsistent wording** for one concept: scenario / AC / akseptansescenario / forslag / kandidat. «Bekreftet av tester» vs «Vurdert av …». «Testgruppe» for suite. | see audit §5 |
| MT-12 | P3 | **Loading and empty states differ by page:** `InlineLoading`, raw `msb-skeleton`, plain `role=status` paragraphs, ~15 ad-hoc `msb-empty`. Primary buttons are sometimes `-sm` and sometimes not. | audit §5 |
| MT-13 | P2 (a11y) | **Interactive controls inside `role=option` rows.** Enter is not handled. The detail drawer has no focus trap and does not return focus. The Plan preview closes on a backdrop click. | `:2613-2637,2666`; `:2218-2220`; Plan `:1566` |

### Application-wide
| # | Sev | Finding | Evidence |
|---|---|---|---|
| APP-1 | P1 | **No application-level scope bar.** Five or more scope controls (ProjectSelector in about 27 page headers, SpecificationScopePicker, ProjectContextStrip, SourceSnapshotSelector, FrontendQualityScopePicker). 23 pages skip `PageHeader` and so have no project selector. | audit §2 |
| APP-2 | P2 | **337 status/badge class families.** The shared `StatusChip` and `SemanticStatusBadge` are used in fewer than 20 files. | audit §3 |
| APP-3 | P2 | **No shared table/grid:** 369 tables. `Pager` is used once outside Manual Testing. Silent `.Take(n)` truncation on Test Coverage, Delivery Reports and Spec Explorer. | audit §3 |
| APP-4 | P2 (a11y) | **About 8 modal families.** Focus is trapped in 1 of 18 dialog files, and 11 have no Escape handling. Native `confirm()` is used 9×. | audit §3 |
| APP-5 | P2 | **Mixed Norwegian and English inside shared controls** (ShowMore and Pager are Norwegian; ProjectSelector is mixed; System Settings is English). | audit §5 |
| APP-6 | P3 | **About 7,200 hard-coded hex colours.** No semantic tokens for User Story / AC / Test Case / Readiness / Source evidence. `--radius-default` is used but undefined. | audit §4 |
| APP-7 | P3 | **Four loading patterns.** Shared `EmptyState` is used in 5 files vs 79 ad hoc. | audit §3 |
| APP-8 | P2 (a11y) | **12 clickable span/div/tr without keyboard support**, including every search-clear «×». | audit §6 |
| APP-9 | P3 | **Wide tables with fixed min-widths** (48–56 rem, 700 px). Only half the CSS files have breakpoints. | audit §7 |

## 3. Existing building blocks to reuse (not duplicate)
- **Shared components:** `ShowMore`, `Pager`, `InlineLoading`, `LoadingSkeleton`, `EmptyState`, `StatusChip`, `SemanticStatusBadge`, `TestingWorkspaceHeader`, `SpecificationScopePicker`, `NavigationLock`.
- **Dialog pattern:** AddScenariosToPlan (focus in/out, Escape, unsaved-changes guard).
- **Inline patterns:** Discover `ActiveChips()`, `.msb-selection` bulk bar, `ScenarioPreparationPanel` (`CountText`).
- **Tokens:** `wwwroot/css/design-tokens.css`.

## 4. Order of work (V12 scope)
1. **Finn og forstå** (MT-1, 2, 5, 6, 7, 9, 13): a responsive master–detail with one primary action, a filter toolbar with chips, direct «Legg til i testplan», and independent row states that do not rely on colour alone.
2. **Shared pieces on the way**, reused, not reinvented: StatusBadge, ReadinessSummary, EmptyState, Dialog behaviour.
3. **Planlegg og klargjør** (MT-3, MT-8): one readiness summary, create and edit separated, source context collapsed.
4. **Testplaner and execution** (MT-4, MT-10): the visible hierarchy, «x av y behandlet».
5. **Testoversikt and the rest of BirkNext** (APP-*): shared patterns only where the benefit is clear. Not a blind redesign.

V11 runs after V12 (user decision 2026-11-27).
