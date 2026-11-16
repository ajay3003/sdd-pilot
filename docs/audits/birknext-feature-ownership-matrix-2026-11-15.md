# BirkNext — Feature ownership matrix (2026-11-15)

The durable reference for **where BirkNext functionality belongs**. Use it before adding a module, a tab or a calculation: find the entity's authoritative owner below, extend that owner, and let other modules read it or link to it by identity. Companion documents: portfolio audit (`birknext-feature-portfolio-consolidation-2026-11-15.md`), overlap register, consolidation roadmap.

## How the table was produced

Not hand-written. The module list, area, enabled state and workspace memberships are read from the running Release build: browser acceptance (`nav-accept`) opens the **All modules** page (rendered from `NavigationCatalog` / `NavigationModules`, the single module registry), opens every enabled module at its route, and records which workspace sidebars list it. The raw inventory is `birknext-feature-inventory-2026-11-15.json` (66 modules, 60 enabled by default). Each row is joined with a curated entry (primary user, purpose, authoritative owner, disposition); the generator fails if any registry module lacks a curated entry, so the matrix cannot silently omit a module. Contextual routes that are not modules (detail pages, deep links) are listed in `FeatureVisibilityTaxonomy.ContextualRoutes` and enforced by `FeatureVisibilityInventoryTests`.

## Placement rules (for future functionality)

1. **One owner per entity.** Every stored entity has exactly one writer: the module named in "Authoritative owner". Other modules read it through that owner's API or link to it by id (`?journey=`, `?integration=`, `?scope=`, `?asset=`). A second writer or a copied table is a defect (register rows 17–18).
2. **One grammar per concept.** Requirement identifiers use `RequirementReferenceParser` (shared); prepared-case counting uses `PreparedCaseTraceability`; requirement→implementation rows use `SddEvidenceGraphService`. A new consumer reuses these instead of writing its own pattern (register rows 7–8).
3. **A link sends only what its target reads.** Before adding `?param=` to a link, confirm the target page (or a component it embeds, such as `ScopeFilter`) reads it; otherwise link without it and say so (register rows 4–6).
4. **Workspaces are navigation, never authorization.** A workspace lists modules for a role; every enabled module stays reachable from All modules and by search. A new module is placed in at least one specialised workspace, or is added — with a reason — to the All-modules-only list pinned by `NavigationDispositionTests` (frontend) and checked in the browser (`nav-accept` P3).
5. **Workflow entries read, specialists act.** Manual Testing, Technical Testing and Integration Testing summarise and hand off; they do not run engines or store results of their own. New checks belong in the specialist module that owns the result.
6. **Off-by-default modules stay off until consolidated.** Impact Analysis and AI Change Review read the legacy traceability store; do not build on them — use canonical relations (roadmap R2).

## Workspaces

| Workspace | Modules listed (enabled, default visibility) |
|---|---:|
| Manual Testing (default) | 20 |
| Technical Testing | 30 |
| Test Management | 27 |
| Project Manager | 15 |
| Scrum Master | 13 |
| Developer | 21 |
| Architect | 22 |
| All Modules | 60 (every enabled module) |

All-modules-only by design: All modules and System Settings (always shown), Backup & Transfer (administration), Security Classification Review (M2LB Kode 6/7 extension), Skolenærvær (project extension); and the six modules off by default (Change Traceability, Impact Analysis, legacy Traceability & Coverage, Traceability Suggestions, Code Traceability, AI Change Review) — where an off module is listed in a workspace it appears only when switched on.

## Dispositions

**KEEP** distinct workflow · **REUSE** shared capability read by several modules · **INTEGRATE** owned entity joined into one workflow entry by identity · **MOVE** given a (further) workspace home · **INVESTIGATE** evidence of a second store or verdict whose consolidation needs its own migration (roadmap). No module was retired.

## Matrix

| Area | Module | Route | Enabled | Workspaces (besides All Modules) | Primary user | Purpose | Authoritative owner | Disposition |
|---|---|---|---|---|---|---|---|---|
| getting-started | Project Quality | `/dashboard` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Scrum Master, Developer, Architect | Everyone | Project quality overview and next steps | Dashboard projections over module results (no store) | KEEP |
| getting-started | All modules | `/all-modules` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Scrum Master, Developer, Architect | Everyone | Complete searchable module catalog | NavigationCatalog / NavigationModules (registry) | KEEP |
| getting-started | Recommended Workflow | `/getting-started` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Scrum Master, Developer, Architect | Everyone | Recommended Workflow: input readiness and review gates | RecommendedWorkflowService (workspace_review_progress) | KEEP |
| getting-started | System Understanding | `/system-understanding` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Architect | Manual tester, architect | Business flow, data, rules and what to test from evidence | System guide services (system_guide_revisions) | KEEP — evidence for Manual Testing Discover |
| getting-started | Import Project | `/project-import` | yes | Manual Testing, Developer | Everyone | One ZIP → project artifacts and source snapshot | ProjectImportService / ProjectImportStagingStore | KEEP |
| getting-started | Source Management | `/sources` | yes | Manual Testing, Technical Testing, Project Manager, Developer, Architect | Developer, tester | Source versions and analysis baseline | SourceBaselineService (SourceDecisions, append-only) | KEEP |
| getting-started | Demo Projects | `/demo-projects` | yes | Manual Testing, Developer | New users | Sample projects to try BirkNext | SampleProjects catalog files | MOVE — placed in Manual Testing (Project setup) and Developer (Source) |
| getting-started | Project Test Setup | `/project-scopes` | yes | Manual Testing, Technical Testing, Test Management | Test lead, testers | Shared test areas, systems and connections | ScopeRegistryService (scope_registry) | REUSE — Manual, Technical, Test Management |
| getting-started | Target Environments | `/admin/system-settings?section=target-environments` | yes | Technical Testing, Test Management, Project Manager | Technical tester | Target applications, authentication, discovery | Frontend analysis settings (browser storage) + backend profiles | REUSE |
| delivery-reporting | Delivery Snapshots | `/delivery-snapshots` | yes | Project Manager, Scrum Master | Project manager, scrum master | Frozen delivery snapshots | DeliverySnapshotStore (delivery_reporting) | KEEP — same page as Delivery Reports (two entries) |
| delivery-reporting | Delivery Overview | `/delivery-overview` | yes | Project Manager, Scrum Master | Project manager, scrum master | Delivery progress for a scope | DeliveryReporting scope view | KEEP |
| delivery-reporting | Delivery Reports | `/delivery-reporting` | yes | Project Manager, Scrum Master | Project manager | Delivery reports and functional quality scope | WeeklyReportService / DeliveryReportingImportService | KEEP |
| document-review | Specification Explorer | `/specification-explorer` | yes | Manual Testing, Test Management, Scrum Master, Developer | Manual tester, developer | Requirements, stories, acceptance scenarios | Shared spec grammar (RequirementReferenceParser) over workspace artifacts | KEEP |
| document-review | Constitution Explorer | `/constitution-explorer` | yes | Architect | Architect | Project principles | Workspace artifacts | KEEP |
| document-review | Data Model Explorer | `/data-model-explorer` | yes | Architect | Architect | Data model from documents and source | Data model evidence services | KEEP |
| document-review | Plan Explorer | `/plan-explorer` | yes | Scrum Master | Scrum master | Implementation plan phases | Workspace artifacts | KEEP |
| document-review | Task Explorer | `/task-explorer` | yes | Scrum Master, Developer | Scrum master, developer | Tasks and their status | Workspace artifacts | KEEP |
| document-review | Document Quality Review | `/quality/document` | yes | Manual Testing, Test Management | Manual tester, test lead | Requirement clarity, completeness, testability | DocumentationGatePolicy / document quality review | KEEP |
| document-review | Task Alignment Review | `/task-alignment` | yes | Scrum Master, Developer | Developer, scrum master | Tasks vs specification (not implementation) | TaskAlignmentRunService (task_alignment) | INVESTIGATE — internal names say "ImplementationReview"; rename (backlog) |
| document-review | Documentation Drift | `/documentation-drift` | yes | Manual Testing | Manual tester, architect | Documents that no longer match | SpecComparisonService (requirement id: shared grammar after this change) | KEEP |
| traceability | Requirements Traceability | `/artifact-traceability` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Architect | Manual tester, test lead | Requirements Traceability: design vs execution | PreparedCaseTraceability (shared with Test Coverage) | KEEP — reuse |
| traceability | Change Traceability | `/implementation-traceability` | off | — (All modules only) | Developer | Change Traceability (off by default) | Change provenance services | KEEP (feature-flagged) |
| traceability | Impact Analysis | `/impact-analysis` | off | — (All modules only) | Developer, test lead | Impact of changes (off by default) | ImpactAnalysisService (legacy Scenarios/TraceLinks) | INVESTIGATE — second traceability store; consolidate onto canonical relations (backlog) |
| traceability | Relationship Review | `/correlation-review` | yes | Test Management, Project Manager | Test lead, architect | Relationship Review: confirm relationships | CanonicalInterpretationService (decisions) | KEEP |
| traceability | Release Readiness | `/release-readiness` | yes | Test Management, Project Manager, Scrum Master | Test lead, project manager | Release readiness incl. Critical E2E gate | CanonicalInterpretationService.Release + CriticalE2EReleaseReadiness | KEEP |
| traceability | Snapshots | `/snapshots` | yes | Project Manager, Architect | Architect, project manager | Snapshot registry: lineage, retention | SnapshotRegistryService (snapshot_registry) | KEEP |
| traceability | Traceability & Coverage | `/traceability` | off | — (All modules only) | Test lead | Legacy Traceability & Coverage (off by default) | SddEvidenceGraphService | KEEP (feature-flagged) |
| traceability | Traceability Suggestions | `/traceability/suggestions` | off | — (All modules only) | Test lead | Traceability suggestions (off by default) | SddEvidenceGraphService | KEEP (feature-flagged) |
| traceability | Code Traceability | `/code-traceability` | off | — (All modules only) | Developer | Code traceability (off by default) | SddEvidenceGraphService | KEEP (feature-flagged) |
| source-review | Source Analysis | `/source-analysis` | yes | Developer, Architect | Developer, architect | Analysed source: domains, contracts, infrastructure | IqrSourceStore (iqr_source_snapshots) + domain analyzers | KEEP |
| source-review | Schema Studio | `/schema-studio` | yes | Technical Testing, Developer, Architect | Technical tester, architect | XSD/schema evidence, validate and test | Shared XSD capability | KEEP |
| source-review | Generated Documentation Explorer | `/generated-documentation-explorer` | yes | Architect | Architect | Generated documentation of a snapshot | Generated documentation diagnostics (per request) | KEEP |
| source-review | Generated Documentation Health | `/generated-documentation-health` | yes | Developer, Architect | Architect, developer | Health of generated documentation | Same diagnostic run as the explorer (re-run per page) | MOVE — placed in Architect (Source & structure) and Developer (Code quality & build); cache the run (R10) |
| source-review | Technology Coverage | `/technology-coverage` | yes | Developer, Architect | Developer, architect | Which technologies BirkNext can analyse; review applicability | ApplicabilityEvaluator (shared) | REUSE — Technical Testing reads it |
| source-review | Dependency Review | `/dependency-review` | yes | Developer, Architect | Developer, architect | Dependencies, SBOM, health | Dependency evidence providers | KEEP |
| source-review | Pipeline Review | `/pipeline-review` | yes | Developer | Developer | CI/CD delivery flow | Delivery evidence projection | KEEP |
| source-review | Environment Analysis | `/azure-environment` | yes | Architect | Architect | Observed cloud environment (read-only) | Azure environment analysis | KEEP |
| source-review | AI Change Review | `/ai-change-auditor` | off | — (All modules only) | Developer | AI change review (off by default) | AIChangeAuditService (legacy Scenarios/TraceLinks) | INVESTIGATE — same legacy store as Impact Analysis |
| manual-testing-workspace | Manual Testing | `/manual-testing` | yes | Manual Testing, Test Management | Manual tester | Manual Testing home | ManualTestingPresentation over plans and runs | KEEP — primary entry |
| manual-testing-workspace | Discover & understand | `/manual-testing/discover` | yes | Manual Testing | Manual tester | What needs testing | Scenario discovery store + specification | KEEP (stage) |
| manual-testing-workspace | Plan & prepare | `/test-plan-suggestions` | yes | Manual Testing, Test Management | Manual tester | Plan & prepare: saved cases, links, export | TestPlanSuggestionService (test_plan_drafts/snapshots) | KEEP (stage; one planning page) |
| manual-testing-workspace | Execute & document | `/manual-testing/execute` | yes | Manual Testing, Test Management | Manual tester | Execute & document runs | ManualTestRunService (manual_test_runs) | KEEP (stage) |
| technical-testing-workspace | Technical Testing | `/technical-testing` | yes | Technical Testing, Test Management | Technical tester | Technical Testing home | TechnicalTestingPresentation over specialist results | KEEP — primary entry |
| technical-testing-workspace | Discover & assess | `/technical-testing/assess` | yes | Technical Testing | Technical tester | Which areas apply, static evidence | Technology Coverage applicability + module stores | KEEP (stage) |
| technical-testing-workspace | Configure & prepare | `/technical-testing/configure` | yes | Technical Testing | Technical tester | What checks still need | Specialist modules (links only) | KEEP (stage) |
| technical-testing-workspace | Verify & investigate | `/technical-testing/verify` | yes | Technical Testing | Technical tester | Recorded runtime results | Specialist result stores (read) | KEEP (stage) |
| technical-testing-workspace | Integration Testing | `/technical-testing/integrations` | yes | Technical Testing | Technical tester | Integration Testing: discover → journey → suite → verify | Topology, catalog, journeys, suites, IQR (read; handoffs by id) | INTEGRATE — one entry over the integration modules |
| quality-testing | Test Coverage & Overlap Review | `/test-coverage-review` | yes | Manual Testing, Technical Testing, Test Management | Test lead, testers | Tested vs untested, overlap, journey matches | TestCoverageReviewEngine (+ PreparedCaseTraceability) | KEEP — requirement population differs from RequirementCoverageService (labelled, backlog) |
| quality-testing | Code Risk Review | `/code-risk-review` | yes | Technical Testing, Developer | Developer, technical tester | Change and code risk | Code risk services | KEEP |
| quality-testing | Test Data | `/test-data` | yes | Manual Testing, Technical Testing, Test Management | Testers | Approved test data assets | TestDataRegistryStore / AuthoredTestDataStore (files) | REUSE — every test module links by asset id |
| quality-testing | Contract Testing | `/api-quality-review/contracts` | yes | Technical Testing, Test Management, Developer, Architect | Technical tester | Contract Testing: versions, tests, coverage | Contract store (ApiContracts) | KEEP |
| quality-testing | Integration Test Suites | `/integration-suites` | yes | Technical Testing, Test Management | Technical tester | Integration Test Suites from source snapshots | IntegrationSuiteService (integration_suites) | INTEGRATE — receives journey context |
| quality-testing | Test Execution & Evidence | `/execution-results` | yes | Technical Testing, Test Management, Developer | Technical tester, test lead | Actual results of every test module | Execution evidence store | KEEP |
| quality-testing | Parquet Quality Review | `/parquet-viewer` | yes | Technical Testing | Technical tester | Parquet file quality | Parquet inspection store | KEEP |
| quality-testing | Frontend Quality Review | `/frontend-quality-review` | yes | Technical Testing, Test Management | Technical tester | Runtime frontend review | FQR engines; history in browser storage | KEEP |
| quality-testing | API Quality Review | `/api-quality-review` | yes | Technical Testing, Test Management, Developer, Architect | Technical tester | Runtime API review | AQR engines; history in browser storage | KEEP |
| quality-testing | Integration Topology | `/integration-topology` | yes | Technical Testing, Architect | Technical tester, architect | Components and connections (derived) | TopologyBuilder (computed, no store) | INTEGRATE — read by Integration Testing |
| quality-testing | Integration Quality Review | `/integration-quality-review` | yes | Technical Testing, Test Management, Architect | Technical tester | Static integration review; test packs | IQR (integration_definitions, review/journey runs) | INTEGRATE — opened on exact integration |
| quality-testing | Integration Journeys | `/integration-journeys` | yes | Technical Testing | Technical tester | Declared journeys and revisions | DeclaredIntegrationJourneyStore (integration_journeys) | INTEGRATE — systems & steps editor, suite handoff |
| quality-testing | Performance Test Review | `/performance-test-review` | yes | Technical Testing, Test Management | Technical tester | Performance scenarios and runs | PerformanceTestApi stores (runs with snapshot) | KEEP |
| quality-testing | Critical E2E Regression | `/critical-e2e-regression` | yes | Technical Testing, Test Management | Technical tester, test lead | Critical flows and release gate (Policy 2) | CriticalE2EStore (files) | KEEP — release gate semantics unchanged |
| extensions | M01 – Gradert tilgang (Kode 6/7) | `/security-classification-review` | yes | — (All modules only) | Technical tester (M2LB) | M2LB Kode 6/7 classification extension | Security classification services | KEEP — domain extension, All modules only by design |
| extensions | Skolenærvær Testing | `/extensions/skolenaervaer` | yes | — (All modules only) | Technical tester (M2LB) | Skolenærvær project extension | Skolenærvær services | KEEP — project extension, All modules only by design |
| admin | User Guide | `/user-guide` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Scrum Master, Developer, Architect | Everyone | Help Center / User Guide | UserGuideContent / DocumentationCatalog | KEEP |
| admin | System Settings | `/admin/system-settings` | yes | Manual Testing, Technical Testing, Test Management, Project Manager, Scrum Master, Developer, Architect | Administrator | Settings, feature visibility, diagnostics | System configuration services | KEEP |
| admin | Backup & Transfer | `/workspace-transfer` | yes | — (All modules only) | Administrator | Backup, restore and transfer | FullBackupService / ManagedFileStores | KEEP — administration, All modules only by design |
