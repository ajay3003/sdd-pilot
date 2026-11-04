# Delivery Overview — Work type + Quality category (2026-11-04)

Branch `008-traceability-first`. Nothing was pushed.

## What and why

Delivery Overview mixed technical work (ADR-029 translations, contract renames, Developer Mode Settings Panel, test tooling) with business and quality delivery. A test lead wants "what functional and quality value do we deliver?" first.

## Design

- **Two levels**
  - **Work type:** Non-technical · Technical · All work.
  - **Quality category** (only under non-technical work): Functional · Security · Privacy · Performance · Reliability · Data quality · Compliance · Availability.
  - The **default is Non-technical**. It is not written to the URL; `work=Technical|All` and `quality=<category>` are.
- **One classification, not a Delivery rule**
  - The shared `FunctionalQualityClassifier` (rule set `functional-quality/2`, the same classifier Functional Quality Scope uses) gains `ClassifyFeature(title, storyTitles)`.
    - **Technical:** wording first (migration, refactoring, infrastructure, pipelines, decision-record work, source translation). The vocabulary now also covers developer tooling (developer mode, devmode, utviklermodus) and `WolverineFX` / Testcontainers.
    - **Quality category:** otherwise the title's quality wording, else the category more than half of its user stories state, else Functional.
  - **Canonical evidence:** every established member of a change intent, most of whose members are technical, is technical too (rule `fq.technical.change-intent`).
    - Example: "Translate the Tjeneste module to English" has no technical wording.
    - It becomes technical because a person confirmed it into the ADR-029 change intent.
- **Server-side, same projection**
  - `DeliveryScopeService` applies the work filter together with Source and Functional scope (endpoint `…/scope?work=&quality=`).
  - The page renders `DeliveryReportScoping.Project` of the frozen report. Every KPI, the checklist donut, feature states, change this period, quality and readiness, and attention follow the selection.
  - The response also carries `WorkSummary` (total, non-technical, technical, by category, shown) and the per-feature classification with its rule.
- **Indicator:** "Showing: Non-technical delivery · 24 of 38 features · Functional 21 · Security 2 · Reliability 1 · 14 technical features hidden".
- **Empty state:** choosing a technical scope (e.g. ADR-029) under the default gives an explicit empty state with **Show all work (N technical)**. It never switches silently.
- **Scoped reports:** report generation accepts `WorkType` / `Quality`. A frozen report keeps them plus the classification rule set in `Scope`, and the label shows "Non-technical" / the category.

## Real data (throwaway `birknext_ci2`, real M2LB-language, browser 1440/390 × light/dark, 0 external requests, no overflow, no errors)

| View | Features | Checklist |
|---|---|---|
| Non-technical (default) | 24 of 38 (Functional 21 · Security 2 · Reliability 1) | 1248 / 1306 (95 %) |
| Technical | 14 (9 translations, contract rename, Developer Mode, Wolverine shouldly, Testcontainers/WolverineFX, ADR-026 health checks) | — |
| Non-technical · Security | 2 (Autorisasjon initial, Access Administration Panel) | — |

## Tests

- **Backend** (`DeliveryScopeTests`, +2):
  - Shared feature classification (technical first, developer tooling, security wording, story majority; English wording is not translation).
  - Non-technical hides technical work, including a confirmed change-intent member (`fq.technical.change-intent`); every metric follows.
  - Technical, and Non-technical + Functional.
  - All work with no scope returns the frozen report unchanged.
- **Frontend** (`DeliveryOverviewScopeTests`, +4; existing scope tests now state `work=All`):
  - Non-technical default with recalculated KPIs and checklist, and the indicator.
  - Technical with no quality level.
  - Quality kept in the URL (and dropped for Technical).
  - Technical scope under the default gives the "Show all work" action.
  - The Clear filters test now expects the default (Non-technical) view.
- **Suites:** frontend 8006/8006; backend 5807 passed, 0 failed, 13 skipped.

## Not done / follow-ups

- **FQS inheritance:** Functional Quality Scope uses the same classifier and rule set but does not yet apply the canonical change-intent inheritance. Its items are classified per item with the feature-title technical rule, as before.
- **Same rule in other views:** Test Coverage and Traceability should read `DeliveryFeatureWork` / `ClassifyFeature` when they get work filters.
- **Delivery Reports Stories tab:** gets the Work type / Quality toolbar in the queued Delivery Change Report redesign.
