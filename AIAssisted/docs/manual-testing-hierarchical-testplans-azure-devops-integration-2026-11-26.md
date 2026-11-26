# Hierarchical test plans, test levels and Azure DevOps test suites (2026-11-26)

This is the brief «Unified scenarios, hierarchical test plans and Azure DevOps test suite integration». Branch `008-traceability-first`, committed locally (nothing pushed). There was no data reset, database cleanup or destructive migration (no schema change at all), and no real Azure DevOps call: transfers were tested against the existing Azure DevOps stand-in. Both stashes are kept.

**Status words used:**
- **IMPLEMENTED** — built in this delivery.
- **VERIFIED** — checked by tests.
- **PARTIAL** — some of it is built.
- **NOT IMPLEMENTED** — not built.

## A. What existed (audit)

| Capability | Before | Now |
|---|---|---|
| Plan suites | A flat list: `TestSuiteDraft(Id, Name, Origin, StageId)`, one `SuiteId` per case, suites proposed per area | The same record with `ParentId`, `Order` and `TestLevel` (additive JSON fields). A flat plan is a tree without parents: **no migration**, and old plans read and save unchanged |
| Saving suites | The save accepted the client's suite list (max 50) | Hierarchy validated (unknown parent, cycle, duplicate id → 400 `invalid-suites`, never repaired silently); max 200 |
| Test level | MISSING (only the candidate type existed) | `TestLevelClassifier` (`test-level/1`): from the candidate type only. BusinessE2E is «Ende til ende» (**candidate**), Integration is «Integrasjon», API contract and UI are «Komponent», anything else is «Uavklart». Never from titles, folders or service counts |
| Plan creation from a selection | Flat area suites | Optional `Structure` template; the suites are built by `PlanStructure.Build` — **the same code** as the browser preview |
| File export | One XLSX/CSV in the Azure DevOps import format | Also an **export package (ZIP)**, made with the same exporter per suite |
| Direct transfer | Every case into one destination suite; per-case links; safe suite creation (reuse by name under a parent, uncertain creates recorded) | **Suite placement (opt-in):** the static suite tree is created or reused under the destination, parent first, with each case in its own suite |

## B. Structure templates (System Settings → Manual Testing → Test plan structure)

| Template | Suites |
|---|---|
| **Testnivå → Funksjonelt område** (default, recommended) | Ende til ende / Integrasjon / Komponent / Uavklart, with the scenario's area under each («Uten angitt område» when none) |
| Modul → Brukerhistorie | Module (from the specification path), then «USn title» («Uten modul» / «Uten brukerhistorie») |
| Scenariotype → Testnivå | The scenario's type (delivery 2's classifier), then the test level |
| Ingen automatisk gruppering | Flat area suites, as before |

**Rules:**
- **No empty suites, stable ids:** a suite is created only for groups that hold cases. Ids come from the group path, so the same selection gives the same ids, and two «Rapportering» under different parents are different suites.
- **E2E-only plans:** when every case has the same level, the level layer is left out (an E2E-only plan becomes «Rapportering», «CDC» …). The level is kept on the suites, and every case keeps its candidate type, so its level is never lost.
- **Choice and preview:** the tester chooses the template in «Ny testplan» and sees **the exact tree** before creating, with counts. E2E candidates are flagged («kandidater fra dokumenterte forretningsflyter … må gjennomgås»).
- **Scope of settings:** settings apply to new plans only. An existing plan is never reorganised; adding scenarios to it later adds flat area suites as before.

## C. Planlegg og klargjør

- **Suite tree:** a plan with a suite tree shows it as the left list:
  - collapsible suites with case counts;
  - empty suites shown with «0 testtilfeller»;
  - the open case's suite always open;
  - «Vis alle grupper» / «Skjul alle».

  Folding is view state only and is never saved. A flat plan looks as before.
- **«Testgrupper» panel** (write permission):
  - **Ny gruppe:** names are unique among siblings;
  - **Gi nytt navn**;
  - **Flytt:** a suite can't be moved into itself or a descendant;
  - **Fjern gruppe:** a suite with cases needs a target suite («ingen testtilfeller slettes»), and its child suites move up.
- **Moving a case:** a «Testgruppe» choice on the open case. It changes **only** the case's suite; identity, source, steps, approval and translation are unchanged (tested).
- **Saving:** every change is one saved revision through the normal save, and is refused while the case form has unsaved edits.
- **«Se endringer»** names suites added, renamed, moved and removed, and cases moved between suites.

## D. Finn og forstå

- **Grouping and filter:** «Testnivå» is a grouping (after «Brukerhistorier», which stays the default) and a filter. Each scenario is under one level.
- **Drawer:** shows «Testnivå: Ende til ende (kandidat) — …» with the reason.
- **Independence:** the type and Manual/Technical classifications are unchanged and independent.

## E. File export package (Overfør testplan → Eksporter til fil → «Last ned eksportpakke (.zip)»)

This is offered when the plan has a suite tree. It contains:
- `TestSuite-struktur.csv` — the **manifest**: suite id, parent id, order, path, test level, case count and import file. Empty suites are listed, with no file. It is not an Azure DevOps import file.
- One import file per suite that holds ready cases, in a folder per parent («Ende-til-ende/Rapportering.xlsx»). These are made by the **existing exporter**, with the same columns, step serialisation and validation (Area Path etc.) as the single file. Safe names, and no collisions.
- `Testplanoversikt.csv` — a working overview (suite path, case, level, status, source identity).
- `Importveiledning.txt` — **generated from the manifest**:
  - «Import fra fil oppretter IKKE testgrupper»;
  - create the Static Test Suites parent before child, listing this plan's actual suites;
  - import each file into its suite;
  - check the counts.

The export is refused (409) when anything blocks it, exactly as the single file. The page says that one single Excel file puts all cases in the same suite. Nothing is sent anywhere.

## F. Direct transfer with suite placement («Behold testgruppene», on by default for a plan with a suite tree)

- **Preview** (read-only; no remote write — tested): the suite tree under the destination suite. Each suite shows «finnes» (an existing static suite with the same name under the **matched parent**; never matched by name alone) or «opprettes», with case counts.
- **Confirmed transfer** (the existing declaration and confirmation are unchanged):
  - suites are created or reused **parent before child** through the existing safe creation: reuse by name under the same parent, and an uncertain create blocks a blind second one;
  - each case is then added to its own suite, only if it is not there already.
- **Retry:** reuses every suite («gjenbrukt») and adds no duplicate suite or membership (tested).
- **Partial failure:**
  - a refused suite gives «Feilet»;
  - its children are not created elsewhere;
  - its cases are «Ikke i testgruppen» (the test case exists and is linked; transfer again);
  - the result says «Delvis overført», and the suite outcomes are listed and recorded in the transfer history.
- **Never done:** no Azure DevOps user story or requirement-based suite is created.

**Remote id mapping:** cases keep their existing per-case link (with the remote suite). Suites are mapped by parent-aware name matching at every transfer, and no new table was added.

**Limitations:**
- a suite renamed in BirkNext after a transfer creates a new remote suite on the next transfer (the old one is kept; nothing is deleted);
- empty suites are not created remotely;
- an «Opprett ny testplan i Azure DevOps» choice inside this flow is not added: the existing plan creation in System Settings is used.

## G. Tests

- **Backend:**
  - `HierarchicalTestPlanTests` (8): test level, default structure, no empty suites, stable parent-aware ids, E2E-only, other templates, invalid hierarchies, export package manifest, files and guide, remote matching;
  - in `SourceDrivenPlanningTests` (temporary database): create with a structure, add a child suite, move a case (identity, source and review unchanged), revision per change, cycle refused;
  - in `AdoTestPlanTransferTests` (Azure DevOps stand-in): preview writes nothing; the tree is created parent first under the destination; cases land in their own suites; a retry reuses suites without duplicates; a refused suite gives a partial result.
  - All 19 transfer tests and all 44 related tests pass. The wider test-planning suite was still running at commit time; its result follows in a later commit.
- **Frontend:**
  - `ManualTestingPlanSuiteTreeTests` (tree, counts, empty suite, folding never saved, case move saves only the suite, flat plan unchanged, suite edit rules, «Se endringer»);
  - the «Ny testplan» preview, with the structure sent;
  - the System Settings structure choice. **Full frontend suite: 9,757 passed / 0 failed.**

## H. Not done in this delivery

- **NOT IMPLEMENTED:**
  - **«Documented E2E tests» (TEST-E-01 …) as a new scenario source:** the test level of today's discovered scenarios is used; parsing explicit test specifications into scenarios is the developer-test delivery's (5) territory.
  - **Multiple suite membership:** a case belongs to one suite. The current model supports one, and the brief allows it.
  - **Drag-and-drop:** the panel's menus are the (accessible) way to move suites and cases.
  - **Testoversikt hierarchy KPIs and charts.**
- **PARTIAL:**
  - **Testplaner** does not yet show a hierarchy preview per plan; the tree is visible in Planlegg and in the transfer.
- **Not checked in the browser:** no isolated browser walkthrough was run for this delivery; it is covered by the tests above.
