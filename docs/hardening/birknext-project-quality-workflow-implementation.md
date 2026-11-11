# BirkNext — Project Quality & Recommended Workflow: implementation (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `d86bf326`.
- **Related documents:** audit `../audits/birknext-project-quality-workflow-audit.md`; information architecture `../architecture/birknext-project-quality-information-architecture.md`.

No new engine, store or score was introduced. The changes reuse `WorkflowReadinessService`, `DashboardPresentation`, `ICurrentWorkspaceProjection`, Test Execution & Evidence and the Help Center.

| File | Change |
|---|---|
| `Services/DashboardPresentation.cs` | `ByScore` gives Available, never Passed. Traceability is Passed only when every transition was assessed, otherwise Partial. Delivery readiness from documents is Available and labelled document-based. The workflow area shows Attention, not Passed, while decisions are "reviewed with gaps". Specific outcomes for Configure Project and Bind Project Evidence. `DashboardReviewFacts.Execution` and `ExecutionEvidenceFacts` added. |
| `Services/DashboardQualitySummary.cs` | Not-applicable document types are left out of the denominator. Specification quality has no Passed: findings without severe ones → Partial. "QA verification" is replaced by **Execution evidence** (recorded runs; Not read / Unavailable / No recorded runs / N runs). |
| `Services/ProjectQualityGuidance.cs` (new) | `RecommendedActionHelp.TopicFor` (one "Learn how" mapping); `ProjectQualityStages` (five stages from recorded state only) |
| `Pages/Dashboard.razor` | Help link to `project-quality`; a compact import confirmation that says importing is not a quality result; "Learn how" on the next action; score bands instead of "Healthy"; corrected API card text; recorded runs read for the active Target Environment (optional services; unreadable is Unavailable) |
| `Pages/RecommendedWorkflow.razor(.css)` | The next action renders the same `DashboardPresentation.NextStep` as the Dashboard (same key, title and why), plus "Learn how" and the engine's Unlocks. The "nothing open" text is shared. Workflow stages are expandable and keyboard-operable. Recorded runs are read for stage 4. The footer link goes to the article. |
| `Components/Guide/GuideGettingStarted.razor`, `Services/UserGuideContent.cs`, `Services/DocumentationCatalog.cs` | New article `project-quality` (verified against 1.0.0); the Dashboard feature points to it |

## Tests

- **New:**
  - `ProjectQualityTests` (execution evidence semantics, specification quality, denominator, Learn-how targets, article);
  - `CrossPageWorkspaceContractTests.Both_pages_show_the_same_next_action_with_the_same_wording` (two projects);
  - `DashboardPresentationTests.Setup_steps_explain_what_they_produce…` (2).
- **Updated deliberately, with comments:**
  - `DashboardQualitySummaryTests`: denominator; execution card;
  - `DashboardTesterFirstPageTests`: import text; KPI key;
  - `RequirementsTraceabilitySemanticsTests`: partial assessment is Partial;
  - `RecommendedWorkflowStepOneTests`: shared title;
  - `HelpCenterTests`: dynamic topics.
