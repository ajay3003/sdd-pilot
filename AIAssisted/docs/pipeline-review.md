# Pipeline Review

Pipeline Review explains how changes reach environments. It covers:

- what runs first;
- what gates each deployment;
- which artifact is promoted;
- where validation can be skipped;
- which relationships are confirmed, inferred or unresolved.

The input is the pipeline definitions in one Source Analysis snapshot. It is not a YAML linter, a pipeline editor or a run-history dashboard.

Its basic rule is that a definition is not an execution:

- pipeline defined ≠ executed;
- test step ≠ test passed;
- deployment stage ≠ successful deployment;
- approval configured ≠ approval occurred;
- artifact name ≠ same artifact;
- pipeline resource declared ≠ downstream pipeline ran.

Two more rules hold throughout:

- Severity (how much a gap matters) and evidence state (Confirmed / Strongly supported / Inferred / Unresolved) are separate.
- There is no pipeline score.

## Data flow

```
Source Analysis upload ─► PipelineAnalyzer (CI/CD evidence v2, the only YAML reader)
                        ─► SourceArchitecture / Contracts / Infrastructure evidence
                                   │  (stored on the immutable IqrSourceSnapshot)
                                   ▼
            PipelineReviewBuilder (Services/PipelineReview) — deterministic, no parsing
                                   │   + optional AzureDevOpsPipelineMetadataSource (GET only)
                                   ▼
            GET api/pipeline-review[?snapshotId][&metadata]  /probe  /compare  /sources
                                   ▼
            /pipeline-review (QUALITY): Overview · Flow · Gaps · Tests · Environments · Dependencies · Changes
```

A guard test, `The_review_never_parses_YAML_or_reads_source_files`, keeps YAML/HCL readers, archives and file reads out of `Services/PipelineReview`.

Results are cached by `snapshotId + RulesVersion + metadata state`; the cache is never keyed by "latest". `PipelineReviewText.RulesVersion` versions the rules, and each result records the rules version and the CI/CD analyzer version it used.

## Readiness states (page and sidebar)

Source Analysis owns the CI/CD evidence. Pipeline Review only interprets it and never re-parses or regenerates it. Re-analysis always goes through Source Analysis.

The page status comes from `PipelineReviewDashboard.PageStatus`. The sidebar status comes from the shared `ApplicabilityEvaluator`. They use one vocabulary:

| State | When | Tone |
| --- | --- | --- |
| **Needs refresh** | A snapshot whose CI/CD evidence is older than `PipelineReviewText.RequiredCiCdVersion` (v2), or has no CI/CD domain at all. Sidebar: `ApplicabilityStatus.NeedsRefresh`, set from `ProjectTechnologyCoverage.CiCdEvidenceOutdated`. | amber |
| **N/A** | Current evidence with no pipeline definition. Exception: v1 evidence that found no pipelines is also N/A, because v1 already detected pipeline files. | grey |
| **Unsupported** | No reviewable pipeline, and the CI/CD domain reports `Unsupported`. A tool limitation, not a project finding. | grey |
| **Analysis failed** | The CI/CD domain reports `FailedAnalysis`. The only red state. | red |
| **Partial** | Reviewed, but a pattern-based provider is involved, templates are unresolved, or there are assessment gaps. | amber |
| **Ready** | Reviewed with none of the above. Ready says nothing about pipeline quality: findings are listed under Gaps. | green |

Notes on the rules:

- Outdated evidence is never shown as N/A. A snapshot analyzed before CI/CD evidence existed is the main case: its technology inventory has no pipeline entries, so before this rule the sidebar showed N/A for it.
- The CI/CD domain's own "Partial" status is not used for Partial. It is a disclaimer every pipeline carries (expressions are never evaluated), and the limitations already state it.

**Page layout.** The page always shows the full structure, including when evidence is stale:

- header status pill;
- summary bar (provider, CI/CD evidence, Source Analysis, metadata);
- source card (project name; the raw archive name is in Technical details);
- readiness checklist;
- optional enrichment card;
- summary tiles;
- the seven views.

A view without evidence shows a placeholder that says what will populate it. Unknown counts are shown as "—", never 0. The snapshot's own pipeline-file count from older evidence is shown, labelled historical.

**Azure DevOps metadata** is optional enrichment: definition names, environments and checks. The source review never needs it. Its states are Not included, Not configured, Not authorized, Unavailable and Included.

**Flow types** (Build / Test / Security / Package / Deploy / Other) are derived from the evidence attached to a stage, never from its name alone. The order of checks is: deployments, then validation categories, then published artifacts. Single-job pipelines show their validation steps in definition order instead. The backend lists a step once per category it matches, so the page merges these into one node.

**Conditions** are translated into plain language only for `succeeded`, `failed`, `succeededOrFailed`, `always`, `canceled`, `and`, `or`, `not`, `eq` and `ne` over `variables`. Anything else is shown raw.

**Tests** read as *Defined*. Whether a test ran or passed needs run evidence, which a definition cannot show.

## CI/CD evidence v2 (Source Analysis, additive)

All v2 additions are additive; v1 fields are unchanged. The review needs v2, and a snapshot analyzed by v1 asks the person to re-analyze.

**Stages and jobs**
- Stages: `PipelineStage.DependsOnDeclared` (whether a `dependsOn` key is present, so the implicit previous-stage dependency can be applied), plus `Condition` and `Order`.
- Jobs (`PipelineJob`): stage, `dependsOn`, condition, deployment flag, environment, strategy, `continueOnError` and order.

**Steps**
- `Condition`, `ContinueOnError`, `StrategyPhase` and `Order`.
- `ArtifactsPublished` and `ArtifactsConsumed`. Sources: `current`, a pipeline-resource alias, or `pipeline:{name}`.

**Pipelines, templates and triggers**
- `PipelineResource`: pipeline and repository resources, and whether a trigger is declared.
- `PipelineTemplateUse`: level, including stage/job, resolved path, repository alias and literal parameters.
- Template files: `Parameters` and `ParameterDefaults`.
- Triggers: `PipelineTrigger.Disables` records which key `none` switches off.

**New step kinds**
- `SmokeTest`, `ApiTest`, `ContractTest`, `PerformanceTest`, `HealthCheck`, `ArtifactDownload`, `Rollback`, `ManualApproval`.
- Generating migrations (`dotnet ef migrations bundle|script`) is classified as build work. Applying them (`database update`, `sqlcmd`, running the bundle) is a deployment.
- Build steps record project paths and the container build context as targets (paths only).

## Semantics (Azure Pipelines)

**Ordering and conditions**
- A stage without `dependsOn` runs after the previous stage. `dependsOn: []` runs in parallel.
- Jobs in a stage run in parallel unless they declare `dependsOn`.
- Steps in a job run in order.
- A custom condition without `succeeded()` replaces the default success check. The deployment can then run after failures, which is reported as a bypass.

**Soft and conditional gates**
- A test step with `continueOnError`, or a tolerant downstream condition (`always()`, `succeededOrFailed()`), only soft-gates the deployment: the deployment may proceed after a failure.
- A step-level condition makes a test **Conditional**: when the condition is false the step is skipped and the job still succeeds.

**Templates**
- Local templates are composed (`EffectivePipeline`) from evidence. Literal parameters are substituted, template defaults apply, and each item keeps its template origin.
- External (`@repo`) and unresolved templates stay opaque. Anything hidden behind them is **Not assessable**, never reported as "missing".

**Artifacts and cross-pipeline links**
- Deployment jobs implicitly download the current run's artifacts.
- Container deployments use the image pushed earlier on their path. The tag cannot be derived statically.
- A pipeline resource name maps to a file:
  - **Confirmed** through Azure DevOps metadata;
  - **Strongly supported** by an exact file-name match;
  - **Inferred** by a normalized name match;
  - otherwise **Unresolved**.

  Name similarity alone never creates a trigger.

## Rules (rules v1)

| Category | Rule (severity) |
|---|---|
| Test gating | A deployment is reachable without any test. High for QA/Staging/Prod, Medium otherwise. |
| Test gating | Tests exist but none gate the deployment. Reported separately from "no tests". |
| Test gating | A test category in the pipeline does not gate this deployment, e.g. "Integration tests do not gate QA". Medium or Low. |
| Conditions | Soft gate (`continueOnError` or a tolerant condition), conditional test, or deployment condition without `succeeded()` (bypass). |
| Post-deployment validation | No test or health check after the deployment. Not reported for infrastructure or migrations that gate an app deployment. Medium for QA+, Low otherwise. |
| Environment progression | Prod does not depend on QA in the same pipeline (High). No detectable QA→Prod link (Medium, Unresolved). Prod without pre-prod (Medium). Several pipelines deploy one environment differently (Low). |
| Artifact lineage | Prod and QA use different builds (Medium). Lineage to Prod unresolved (Medium, Unresolved). A higher environment rebuilds the same source instead of promoting the closest lower environment's build. That rule pairs deployments by build-target overlap, and ties are marked Inferred. |
| Infrastructure sequence | App before infrastructure, or not ordered, in one pipeline (Medium). Separate pipelines without orchestration (Low). `terraform apply` without a plan (Low). |
| Trigger coverage | Path filters of a pipeline that validates component C exclude a shared library C uses. IaC folder excluded from the pipeline that deploys it. Component changes start no pipeline. IaC deployed by no pipeline (Info). |
| Contract validation | A consumer's pipeline does not start when the producer contract it references changes (OpenApiReference or client-side copy). |
| Security validation | No scan on delivery paths, or scans only scheduled, conditional or soft. Low. |
| Template resolution, cross-pipeline, rollback | Unresolved/external template (Info). Unreferenced template (Info). Unmatched pipeline resource (Info). No rollback evidence for Prod (Info: "does not mean rollback is impossible"). |

**Approvals** may be configured in Azure DevOps outside YAML. Without Azure DevOps metadata they are "not assessable from YAML alone", never "no approval". With metadata, configured checks are shown as configuration, not as having occurred.

## Path probe and changes

**Path probe.** `GET probe?path=` answers "will a change to this path start the right pipelines, and what gates each environment?"

- It evaluates Azure path-filter semantics: prefix or wildcard matching, exclusions winning, and coverage inherited through pipeline triggers.
- The archive's top folder is stripped automatically.
- Branch filters are not evaluated.

**Changes.** `GET compare` compares the reviews of two snapshots. It reports:

- added/removed pipelines;
- trigger and path-filter changes, where narrowing is Medium;
- tests added/removed;
- deployments added/removed;
- validations leaving a deployment path (High for QA+);
- condition and artifact-flow changes;
- template changes.

These are source changes, not runtime results.

## Impact Analysis readiness

`PipelineReviewResult` is the API Impact Analysis can consume later:

- `PathCoverage`;
- `Deployments[].Before/After`;
- `Findings`;
- `Dependencies`;
- the path probe.

Pipeline Review does not duplicate Impact Analysis.

## Limitations

- Conditions and expressions are classified as written, never evaluated. Variables are not resolved.
- PR triggers in YAML apply to GitHub and Bitbucket only. Azure Repos uses branch policies, which are not in YAML.
- Job ordering is analyzed for Azure Pipelines only. GitHub, GitLab and Jenkins pipelines are listed with their tests.
- Container tags and build numbers cannot be derived statically.
- Service connections are names only.
- External templates require a Source Analysis snapshot of their repository. That snapshot is not yet composed into the review; the template is reported as not analyzed.
