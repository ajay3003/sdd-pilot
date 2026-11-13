# Pipeline Review — Test Lead audit (2026-11-13)

Branch `008-traceability-first`. The starting HEAD was 7b29fcfb.

## Data flow (verified)

The flow from source to page is:

1. An imported source snapshot is read by **`PipelineAnalyzer`** (Source Analysis → CI/CD evidence).
2. **`Services/PipelineReview/*`** turns that evidence into the review: `PipelineReviewBuilder`, `EffectivePipeline`,
   `PipelineInfrastructureChecks`, `PipelinePathFilters` and `PipelineStoryWriter`.
3. The result is a `PipelineReviewResult` (shared contract).
4. **`Pages/PipelineReview.razor`** shows it in seven tabs: Overview, Flow, Findings, Tests, Environments, Dependencies, Changes. The
   page status comes from `PipelineReviewDashboard`.

There is one analyzer, and no second pipeline engine.

## What already works (no change needed)

- **Provider discovery.** `PipelineAnalyzer` detects Azure Pipelines, GitHub Actions and GitLab CI, and Jenkinsfiles by pattern. Each
  provider has its own `DomainSupport`.
  - GitHub Actions is **Partial**, and the analyzer says so: "on: triggers, jobs, environments, actions and run commands by pattern;
    reusable workflows are listed, not expanded". It has its own `GitHub(...)` reader, separate from Azure rules.
- **Findings are already grouped** by rule (`G.Group`), with a per-severity group count.
  - "Why it matters" and "Suggested review" appear once per group, and every affected item is listed. Different conditions are not
    merged.
  - Filters cover severity, category, environment, pipeline and search.
  - Unresolved evidence is counted separately.
- **Findings are stated as source-based observations.** The page says "not proof that a pipeline execution failed".

## Confirmed defect fixed

- **The page status "Review ready" used the positive (green ✓) tone.** That reads as pipeline health, which the source cannot establish.
  - It now reads **"Source analysis complete"** in the **neutral** tone. The explanation line uses the same words, and says the status does
    not mean the pipelines ran, passed or deployed.
  - Partial, Needs refresh, Unsupported, N/A and Analysis failed are unchanged.
  - Tests updated: `PipelineReviewDashboardTests`, `PipelineReviewTestLeadTests`.

## Must stay "Not assessed"

The following are not observable from repository YAML and must not be marked absent or passed:

- pipeline run history;
- whether tests passed;
- Azure DevOps approvals and environment checks configured outside YAML;
- deployed artifacts;
- runtime post-deployment verification.

## Not done (remaining scope of the brief)

- A compact Test Lead overview with a release-path strip and "relationship not established from source".
- A single-pipeline flow explorer.
- A tests gating matrix: PR / DEV / QA × required / conditional / advisory.
- DEV-versus-QA comparison.
- A grouped trigger explorer.
- Changes empty-state wording.
- Synthetic `dependsOn` / `condition` / `continueOnError` gate fixtures.
- Real M2LB and browser acceptance.
- The User Guide update.
