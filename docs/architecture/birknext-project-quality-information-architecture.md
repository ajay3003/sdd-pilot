# BirkNext — Project Quality information architecture (2026-11-11)

## 1. Three pages, three questions

| Page | Question | Route |
|---|---|---|
| **Project Quality** (Dashboard) | What do we know about this project's evidence, reviews, tests and risks? | `/dashboard` (unchanged) |
| **Recommended Workflow** | What should I do next, why, and what must happen first? | `/getting-started` (unchanged; `?path=` and `?execution=` kept) |
| **Help Center** | How do I do it? | `/user-guide#topic` |

- No route was removed or renamed.
- Both pages keep their navigation entries.
- Each links to the other ("Why and what comes next" ↔ "Review overview") and to the Help Center.

## 2. Shared context

Both pages read the same scoped `ICurrentWorkspaceProjection` (one project and source context). A project switch updates both; existing cross-page tests guard this, and so does browser check PQ-14.

Recorded test runs are scoped to the **active Target Environment** (`ITargetEnvironmentService.ActiveEnvironment`). Another environment's runs never appear.

## 3. One recommendation, one wording

```
WorkflowReadinessService.NextAction   (the only engine; ordering unchanged)
        │
        └── DashboardPresentation.NextStep(workspace, readiness)   (the only presentation: key, title, why, outcome, action)
                 ├── Project Quality: "Next recommended action" card
                 └── Recommended Workflow: "Recommended next action" card (same key, title, why; plus the engine's Unlocks)
        RecommendedActionHelp.TopicFor(key, route)  → the same "Learn how" article on both pages
```

- Before this work, Recommended Workflow showed the raw step title and description, so the two pages worded the same action differently.
- The `?path=` chooser on Recommended Workflow remains an explicit choice the tester makes, and is labelled as such.
- "Top Recommendations" from document review reports stays in the collapsed detailed analysis, as report content rather than the workflow's next action (gap PQ-G3).

## 4. Workflow stages (Recommended Workflow)

`ProjectQualityStages.For(readiness, execution)` defines five stages:

| Stage | How its state is decided |
|---|---|
| 1. Understand the project | The next action is a setup step → In progress; otherwise Evidence in place (not a quality result) |
| 2. Review requirements & traceability | Recorded review decisions on the current documents. Recorded only when all are decided with no gaps and nothing out of date. |
| 3. Assess technical quality | Available vs needs-setup source, runtime and change reviews. Completion is not tracked for these, and the stage says so. |
| 4. Prepare & run tests | Recorded runs only: Not read / Unavailable / No recorded runs / N runs. A generated test is not an execution. |
| 5. Review evidence & release readiness | Always "Not assessed here": owned by Release Readiness |

- Opening a page or clicking never moves a stage.
- Stages are expandable (`<details>`), keyboard-operable, and carry their state in words, with colour as a secondary cue.

## 5. Metric semantics (Project Quality)

| Metric | Rule |
|---|---|
| SDD evidence | `available of applicable` core document types. Not-applicable types (for example no Constitution) are not in the denominator. |
| Specification quality | No findings → Available. Findings without severe ones → Partial. Severe findings → Attention. Never "Passed". |
| **Execution evidence** (new; replaces "QA verification", which counted Document Quality Review) | Recorded runs of the active environment: N runs with passed / failed-or-error / other. "No recorded runs", "Not read" and "Unavailable" are distinct, and unreadable is never zero. |
| Scores (compliance, QA audit, frontend) | Shown as bands ("Score 80+", "Score 50–79", "Score below 50"). No project policy turns a score into Healthy or Passed. |
| Traceability | Passed only when every transition was assessed; otherwise Partial |
| Delivery (documents) | Ready → Available, labelled "Based on documents, not on test execution" |
| Required review decisions | Not Passed while any decision is "reviewed with gaps" |
| API Quality Review card | "All API targets reviewed" only when every target completed (and there is at least one) |
| Import confirmation | Small and status-only: "Project imported. … importing is not a quality result." |
