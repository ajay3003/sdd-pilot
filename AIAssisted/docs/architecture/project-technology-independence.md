# Project and technology independence

BirkNext must be able to onboard a project whose stack it only partly understands without presenting its own limitations as the project's quality problems. This document describes the mechanisms that enforce that and how to extend them. The baseline findings are in [`../birknext-project-technology-independence-audit.md`](../birknext-project-technology-independence-audit.md).

## Invariants

1. **Tool coverage ≠ project quality.** If BirkNext cannot analyze a technology, that is a coverage gap. It is never a failure and never a 0.
2. **A quality denominator holds only applicable, assessed checks.** `Unsupported` and `NotApplicable` checks are excluded. `NotTested`, `NotAssessed` and `Unavailable` lower *assessment coverage*, not quality. When nothing was assessed, there is no quality value (`null`), not 0.
3. **There is no global fake quality score.** The dashboard reports *quality among assessed areas* next to *n of m areas assessed*.
4. **No project-specific behavior from a hostname.** Template records reach an environment only through an explicit "Apply template" action. A known host can at most produce a suggestion.
5. **Technology ≠ capability.** Kafka, ASP.NET or App Insights are evidence *for* a capability (EventStreaming, BackendApplication, Observability). Capabilities can be scoped to a component.
6. **Planned is not support.** The UI and the evaluator show a roadmap item as not supported.

## Vocabulary — `shared/ApplicabilityContracts.cs` (`BirkNext.Applicability`)

| Concept | Values | Meaning |
|---|---|---|
| `ApplicabilityStatus` | Applicable, PartiallyApplicable, NotApplicable, Unsupported, NotEnoughEvidence, NeedsConfiguration | Does a review apply, and can BirkNext assess it? There is no `Failed` value. |
| `ReviewExecutionState` | NotStarted, Blocked, Completed, PartiallyCompleted, FailedToExecute | Did it run? `FailedToExecute` is a tool error, not a project result. |
| `CheckOutcome` | Pass, Fail, Warning, NeedsReview, Partial (assessed) · NotTested, NotAssessed, Unavailable, Unsupported, NotApplicable (neutral) | What one check found. |
| `Capability` | SourceCode, BackendApplication, FrontendApplication, BrowserTarget, RestApi, GraphQlApi, SoapApi, GrpcApi, RelationalDatabase, DocumentDatabase, Messaging, EventStreaming, FileIntegration, InfrastructureAsCode, Pipeline, Authentication, Authorization, Observability, Containerized, Kubernetes, CloudHosted, PackageInventory, ApiTarget, Requirements, Documentation, IntegrationCatalog, DomainExtension | What a project or component provides. |
| `CapabilityEvidence` | capability, `DetectionConfidence` (Confirmed / StronglySupported / Inferred / Unresolved), basis, technology, component, provider id | Why the capability is believed. |

`ScoreSemantics.Compute(outcomes)` is the reference scoring function. Warning, NeedsReview and Partial count as half. Example: 10 checks, 4 Unsupported, 2 NotApplicable, 4 executed → denominator 4. `ScoreSemantics.ExcludedFromQuality(status)` keeps every non-applicable review out of every aggregate.

## Honest technology support — `shared/TechnologyCoverageContracts.cs` (`BirkNext.Technology`)

`TechnologySupportRegistry` is the **single** registry, as pure data shared by the backend, the UI, the evaluator and the tests.

- **`TechnologySupportDescriptor`** is one technology with a level per dimension.
  - Dimensions: **Configuration / SourceAnalysis / Contract / RuntimeObservation / ActiveTest / TestPlan**.
  - Levels: **Full / Partial / Planned / Unsupported / NotApplicable**.
  - Each descriptor also carries its provider ids, its limitations and the capabilities it provides.
- **`AnalysisProviderDescriptor`** is one analyzer or provider with a stable id and a maturity level. Stable ids include:
  - `source.architecture.dotnet`, `source.database.efcore`, `source.database.sql-ddl`, `source.observability.dotnet`
  - `source.iac.terraform`, `source.iac.bicep-arm`, `source.iac.kubernetes`
  - `contract.openapi`, `contract.graphql`, `contract.asyncapi`, `contract.xsd`, `contract.protobuf`
  - `pipeline.azuredevops`, `pipeline.github-actions`, `pipeline.gitlab`, `pipeline.jenkins`
  - `dependency.nuget`, `dependency.docker`, `dependency.sbom`
  - `runtime.eventhub.azure`, `runtime.servicebus.azure`, `runtime.http`, `runtime.browser`, `cloud.azure`
- **Matrices** are filtered by area: Language, Framework, Integration, Database, Dependency, Pipeline, Cloud, Contract. A unit test guarantees unique ids and that every provider/technology reference resolves.

Examples of what the registry states (verified against the code):

| Technology | Configuration | Source | Contract | Runtime | Active test |
|---|---|---|---|---|---|
| Azure Event Hubs | Full | Partial | — | Partial (opt-in, Azure off by default) | Partial (DEV/QA send only) |
| Azure Service Bus | Full | Partial | — | Partial (ARM GET metadata) | Not supported |
| Apache Kafka | Partial (catalog type Other) | Partial (C# Confluent.Kafka correlation, Kafka Connect detection) | Not supported | Not supported | Not supported |
| SOAP / WSDL | Partial | Not supported | Not supported (no WSDL analyzer) | Not supported | Not supported |
| Oracle | — | Partial (basic DDL; no PL/SQL) | — | — | — |
| Maven / npm / pip | — | Not supported (SBOM import is the path) | — | — | — |
| GitHub Actions / GitLab / Jenkins | — | Partial (pattern-based) | — | Not supported | — |
| AWS / GCP | — | Partial (Terraform inventory) | — | Not supported | — |

`IntegrationTechnology.Map(kind, names…)` maps a configured catalog entry onto a registry technology. Kafka, RabbitMQ, ActiveMQ and SOAP are catalogued as Other or HttpApi today, so their names decide the mapping. A SOAP service catalogued as an HTTP API is therefore never credited with REST runtime support.

## Technology inventory (Source Analysis)

`Services/SourceAnalysis/Technology/TechnologyInventory.Detect` runs once at upload and stores the result in `IqrSourceSnapshot.TechnologyCoverage`. That field is null in older snapshots; analyze the source again to populate it. Its inputs:

- **`IqrSourceArchiveReader.Workspace.AllPaths`**: every non-ignored archive path, names only, held in memory. Before this, files that no analyzer reads (`pom.xml`, `.java`, `requirements.txt`) were dropped without trace.
- **Content markers** in project, configuration and evidence files already held in memory (package ids, connection-string schemes, config sections). These yield `Inferred` confidence only.
- **Evidence the analyzers already produced**: pipeline platforms, Terraform providers, IaC formats, contract types, architecture interfaces.

The output lists detected technologies, each with a confidence and up to 5 redacted file names, never content. It also lists the implied capabilities, file counts (total, analyzed, source files not analyzed) and one neutral limitation per unsupported technology ("… detected; BirkNext does not analyze it … This is a tool limitation, not a project finding.").

`SourceArchitectureAnalyzer` is the `source.architecture.dotnet` provider. Without `.csproj` files its status is `Unsupported`, and its first limitation names the provider. It never invents components for another language.

## Applicability evaluation

- **`ReviewCatalog`** lists each review with its route, required capabilities and an optional domain extension id.
- **`ApplicabilityEvaluator.Evaluate(reviewId, ProjectApplicabilityInput)`** is pure.
  - Its inputs are source technologies and capabilities, target URL configured or not, configured integrations as technology ids, requirements and documentation loaded, SBOM, and enabled domain extensions.
  - It returns a `ReviewApplicability`: status, reason, required, available and missing capabilities, evidence, and an action.

Main rules:

| Review | Rule |
|---|---|
| Source Analysis | Applicable when every detected language is supported. Partial when some are. Unsupported when none are (configuration, IaC, contracts and pipelines are still read) or when nothing is recognised. NotEnoughEvidence without a snapshot. |
| Frontend QR / Critical E2E | Applicable with a target. NeedsConfiguration when the source shows a frontend but there is no target. NotApplicable when a snapshot shows no frontend and there is no target. |
| API QR | Applicable with a target. Partial or Unsupported when only SOAP/gRPC is detected. NeedsConfiguration when an HTTP API is detected without a target. NotApplicable without a backend. |
| Integration QR | Applicable when every configured integration has a runtime provider. Partial when some are configuration-only (Kafka, RabbitMQ, SOAP, File …). NeedsConfiguration when the source shows integrations but none are configured. |
| Dependency Review | Applicable for NuGet or an SBOM. Partial for mixed ecosystems. Unsupported for Maven/npm/pip only, with the action "Import a CycloneDX or SPDX SBOM". |
| Pipeline Review | Applicable for Azure Pipelines. Partial for GitHub Actions, GitLab or Jenkins. NotApplicable when no definitions are found. |
| Azure Environment | Applicable with Azure evidence. Unsupported for AWS/GCP only. |
| Security Classification | Applicable only when the domain extension `m2lb.child-security-classification` is enabled. Otherwise NotApplicable. |
| Quality Review | Applicable with requirements. NotEnoughEvidence otherwise. |

## Endpoint and UI

- **`GET api/technology-coverage?environmentId=`** returns `ProjectTechnologyCoverage`.
  - Contents: the latest Source Analysis snapshot's `TechnologyCoverage`, configured integration technology ids and enabled domain extensions.
  - It is read-only: it never applies or upgrades a template and never rescans an archive.
- **`ProjectApplicabilityState`** is scoped to the frontend session. It combines the endpoint, the active Target Environment (target URL) and workspace documents, then runs the shared evaluator.
- **Navigation** labels reviews with applicability badges — Partial, N/A, Unsupported, Setup, No evidence — and never hides them.
  - Security Classification carries an "M2LB extension" label.
  - Badges are optional: a host without the state service renders the menu as before.
- **Technology Coverage** (`/technology-coverage`) shows:
  - the review applicability table, with reason and next step;
  - the detected technologies, with confidence, file evidence, per-dimension support pills and limitations;
  - the full support matrix, collapsed.
  - Neutral states use the muted tone, never the attention/failure tone.
- **Dashboard** averages assessed areas only.
  - A QA report built without a specification, which scores 0 by construction, is not assessed.
  - "n of m areas assessed" is shown next to the quality chip.

## M2LB as an explicit template and domain extension

- `IntegrationCatalogService.GetAsync` **never applies** the M2LB DEV template.
  - Environment type and target URL only make `IntegrationCatalog.Templates` mark the template as `Suggested`.
  - Read-only checks (no environment type) never write.
- `ApplyTemplateAsync(environmentId, "m2lb-dev-eventhub")`, exposed as `POST api/integrations/templates/{templateId}/apply`, is the only path that writes template records. It is add-missing only: edited and deleted records are respected.
- **Persisted workspaces keep working.** An environment whose state record already carries the seed name — every workspace seeded by an earlier version — still loads and still receives add-missing upgrades (v1→v4).
- `IntegrationCatalog.AppliedTemplateId` and `DomainExtensions` say what is applied. M2LB enables `m2lb.child-security-classification`.
- The Integrations pane shows the suggestion with an explicit **Apply** button. For other environments the template sits in a collapsed "Project templates" list.
- Security Classification is a domain extension. Its page says so, and its M2LB defaults (target system `M2LB`, BiRK, Kode 6/7, BirkId) stay inside it. No generic review descriptor mentions BirkId, Kode 6/7, BarnRegistreringId or BiRK (enforced by a test).

## Extending

- **New technology:** add a `TechnologySupportDescriptor` with honest levels and limitations. Add a detection rule in `TechnologyInventory` (path, content marker, or analyzer evidence). Add a fixture test.
- **New analyzer or provider:** give it a stable id in `TechnologySupportRegistry.Providers`, reference it from the technologies it serves, and raise their levels only for what the code really does. Source-domain analyzers are still registered in `SourceEvidenceAnalyzerRegistry.Default`; architecture extractors are registered in `SourceArchitectureAnalyzer.Extractors`.
- **New review:** add a `ReviewDescriptor` and an evaluator rule. Expose a `ReviewExecutionResult` when the review has a check list, and score it with `ScoreSemantics`.
- **New project template or domain pack:** add a template offer and an explicit apply path. Never use a hostname predicate to apply it.

## Revised roadmap

| Priority | Item | Status |
|---|---|---|
| P0 | Shared applicability, execution, outcome and score semantics | **Done** (`ApplicabilityContracts.cs`, tests) |
| P0 | Dashboard: quality among assessed plus coverage; insufficient-data QA excluded | **Done** |
| P0 | M2LB template explicit; suggestion only; legacy workspaces upgrade; domain extension flag | **Done** |
| P0 | Navigation applicability badges; Security Classification labelled as an extension | **Done** |
| P1 | Technology support registry with stable provider ids and honest matrices | **Done** |
| P1 | Technology inventory on the snapshot (unsupported technologies reported, not dropped) | **Done** |
| P1 | Technology & Analysis Coverage page plus endpoint | **Done** |
| P1 | Per-review `ReviewExecutionResult` envelopes (AQR, IQR, FQR, Dependency, Pipeline) scored by `ScoreSemantics` | Next: each review keeps its own score today; AQR per-category ints default to 0 for unassessed categories in the API JSON (not rendered) |
| P1 | Evaluator inputs for Endpoint Discovery targets (AQR) and SBOM uploads (Dependency Review) | Next: `HasApiTarget` currently means "target URL configured" and `HasSbom` means "SBOM file in the archive" |
| P1 | Pipeline platform parser fallthrough (`_ => AzurePipelines`) made explicit | Next |
| P2 | Maven/Gradle, npm and pip manifest readers (inventory only) | Planned — today: SBOM import |
| P2 | WSDL contract analyzer | Planned |
| P2 | Kafka topology/runtime provider | Planned |
| P2 | Java/Spring architecture provider (`source.architecture.jvm`) | Planned — needs a non-`.csproj` project model |
| P2 | AWS environment provider | Planned |
| P3 | Component-scoped capability view (per project component, not per snapshot) | Planned |

## Review-local scoring on the shared semantics (follow-up)

Every review score now goes through `ScoreSemantics`. There is no second scoring abstraction.

- **Shared additions**
  - `CheckOutcome.Informational` (appended): the check executed and recorded evidence without judging it (Observed, Detected). It counts toward coverage, never toward quality.
  - `IsExecuted`.
  - `Credit`: one cross-review policy. Pass 1; Warning, NeedsReview and Partial ½; Fail 0.
  - `FromExecution`: a provider failure or block becomes Unavailable, never Fail.
  - `ComputeWeighted`: the denominator is the assessed weight only, plus a separate weighted coverage.
  - `AverageAssessed`: averages category scores that were assessed; null when none was.

### Per-review mapping

| Review | Before | Now |
|---|---|---|
| **API QR v2** (UI path) | No score; check results only | `ApiReviewScoring.Outcome` maps Blocked→Unavailable, NotTested→NotTested, ManualReview→NeedsReview. The report gains `Quality` and `QualityByArea`; each target gains `Quality`. These are additive, computed properties. An area without checks has no entry. |
| **API QR legacy** (`POST api/api-quality/analyze`) | Every unassessed category int defaulted to **0** | Category ints are nullable (`null` = not assessed). The overall score is `AverageAssessed`. `assessmentState` and `scoringModelVersion: 2` are added. A run with nothing assessed has no score and is not deployment-ready, which is an evidence gap. No frontend caller uses this endpoint. |
| **Integration QR** | Already neutral (assessed/total counts, `NothingAssessed` outcome) | `IntegrationReviewScoring` covers the check-status mapping (Observed/Detected→Informational, NotConfigured→NotAssessed) and splits the run into three properties (table below). |
| **Frontend QR** | Category scores already null when unassessed. **Performance scored 100 when only a security scan ran.** | Performance is scored only from performance-engine data. The overall score is `AverageAssessed`. `FrontendQualityScoring.EngineCoverage` maps engine states: Unavailable, timed out or engine error → Unavailable; Disabled or NotApplicable → excluded. |
| **WASM performance readiness** (dashboard input) | An unassessed category scored **100**. With nothing assessed: overall **0** with `HasData = true`, which the dashboard averaged in. | An unassessed category is null. The overall score is `AverageAssessed`. `HasData` is set only when something was assessed. |
| **WASM security** (dashboard and FQR input) | An unreachable target scored **100** (no findings) | Score is null when the target page could not be fetched. |
| **Dependency Review** | A Maven/npm/pip-only repository got Coverage **"Missing"** | Unreadable manifests are recorded at upload (`SourceDependencyEvidence.UnsupportedManifests`, names only). With nothing readable, Coverage is NotAssessed and names the ecosystems and the SBOM path. A partly readable repository names what was not reviewed. |
| **Pipeline Review** | Tool-limitation findings counted with defects | `PipelineFindingCategory` UnresolvedFlow, TemplateResolutionGap and CrossPipelineDependencyGap set `IsAssessmentGap`. These are counted apart ("N not assessable") and do not drive the "Needs attention" headline. "No test step was found" is still raised only when the job structure is known. |
| **Quality Review** (aggregate) | Standards pack with no summary scored **0**; Data Model with no entities scored **0**; both averaged in | `QualityReviewPackResult.Assessed` / `NotAssessedReason`; `QualityReviewReport.AssessedPacks`. The overall score averages assessed packs only. The UI shows "Not assessed" or "No score" and "n of m packs assessed". On the dashboard, a session result with 0 assessed packs is not an assessed area. |

Integration QR's run is split into three properties:

| Property | Contains | Never affects |
|---|---|---|
| `Quality` | Runtime, contract and source checks | — |
| `ConfigurationReadiness` | Configuration-provenance checks only | Quality |
| `RuntimeSupport` | Per system, from the registry. Kafka: Unsupported, labelled a tool limitation | Quality |

### Not changed, and why

- **Release readiness** (`DeliveryReadinessService`) and the **QA readiness / auditor** score from requirements artifacts and consume none of the five reviews. The requirements-free case was already fixed on the dashboard.
- **Persisted results stay readable.**
  - The new review properties are get-only and computed. Stored IQR runs and AQR history recompute them on read and are never rewritten.
  - Legacy AQR JSON now carries `null` instead of `0` for unassessed categories. `scoringModelVersion` tells the two shapes apart; a v1 `0` is ambiguous and is not reinterpreted.
- **Legacy penalty scores remain** (FQR / WASM / AQR legacy): 100 minus severity points per assessed category. Eligibility now follows the shared rules; the per-category penalty formula itself is review-local.
