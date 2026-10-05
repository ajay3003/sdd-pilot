# BirkNext documentation audit

Audit baseline: branch `008-traceability-first`, starting commit `397b5553a254f46c212f063ae2e22fc9099f038c`. The working tree already contained unrelated reset/runtime/dashboard/settings changes when this work began; those changes were preserved.

## Product inventory

The sidebar is defined by `NavigationCatalog`, not by old documentation or the `MenuItemRegistry` label list. It currently contains 31 rows: Recommended Workflow and User Guide (2), Dashboard plus five other explorers (6), Sample Projects (1), three legacy/optional Traceability entries (3), eight Analysis entries, nine Quality entries, optional AI Change Review (1), and System Settings (1).

System Settings contains configuration, analysis capability and developer diagnostics areas. The documented inventory covers General, Configuration Health, Feature Visibility, Platform, Target Environments, Frontend Engine Capabilities, Performance Test Engines, AI, Environment Diagnostics, System Diagnostics, Documentation Health, ReviewContext Validation, Runtime Diagnostics and Maintenance. Target Environments currently has 11 tabs. Source Analysis currently has eight areas: Overview, Architecture, Database, Observability, Infrastructure, Configuration, CI/CD and Contracts.

The current quality/review rows are Quality Review, Frontend Quality Review, API Quality Review, Integration Quality Review, Performance Test Review, Security Classification, Dependency Review, Pipeline Review and Critical E2E Regression. Environment Analysis is an Azure provider page with a generic navigation label. Security Classification is extension-specific (M2LB child-security-classification).

## Gaps and stale framing found

The previous guide led with SDD governance, contained legacy readiness/compliance pack names, presented optional AI as a core concept, and omitted or materially under-described shared Source Analysis ownership, technology support, environment/provider semantics, current review pages, pipeline, performance, security expectation ownership, administration and evidence maturity. Its feature matrix was manually maintained and did not represent the current sidebar. Workflow copy described manual approval/readiness semantics as if universally current.

The previous Documentation Health page held a hand-maintained route list that omitted current routes including `/source-analysis`. The route itself exists in `SourceAnalysis.razor`; the diagnostic falsely rejected it. The menu warning came from comparing display labels against a stale `MenuItemRegistry`, including “Azure Environment Analysis”, while the current `NavigationCatalog` row is “Environment Analysis” at `/azure-environment` and supplies provider metadata. These checks were removed from the active diagnostic path.

Before rebuild the diagnostic had three effective validations grouped into route/link/menu checks and a no-op orphan check; it did not inspect actual routable components or current User Guide coverage. A numeric before-count of documented current capabilities was not reliable because the former matrix mixed legacy/embedded entries and lacked feature identity. The former guide and metadata did not contain complete Settings, Target Environment or Source Analysis coverage inventories.

## Current documentation ownership

`NavigationCatalog` is the source for current navigation identity and labels. `DocumentationCatalog.Features` is the explicit documentation registry keyed by stable route-derived FeatureIds; display text can change without changing the ID. `DocumentationCatalog.DiscoverRoutes` reads Razor `RouteAttribute` metadata from the application assembly. The User Guide matrix renders from the documentation registry. Documentation Health compares current navigation rows against that registry, actual routes, declared anchors, system settings, environment tabs, source-analysis domains, provider scope and extension scope.

The guide now explains:

- source snapshot provenance/currentness and the shared source-evidence owner;
- support versus applicability, coverage versus quality, and distinct evidence maturity states;
- requirements/implementation/test relationships without equating designed and executed tests;
- the boundary between source analysis, runtime evidence, pipeline declarations and successful execution;
- provider and extension scope, including Azure Environment Analysis and M2LB Security Classification;
- Performance Test Review versus host-level Performance Test Engines, explicit baselines, comparability and Resource Stability limitations;
- Target Environment ownership, administrative settings, diagnostics, workflows, scenarios and current navigation.

## Documentation Health coverage

The deterministic checker currently checks duplicate feature IDs, current navigation coverage by ID, documented and current routes, route mismatch, guide anchors, orphan documentation entries, settings/tab/domain coverage, provider/extension scope metadata, and known legacy terms when supplied to the checker. It makes no network or AI calls. Fixture tests cover a missing feature, invalid route, label rename under stable ID, legacy term, missing anchor, duplicate ID, orphan document and missing settings/tab/domain entries.

The System Settings UI summarizes findings by category with failure/warning status and a suggested correction. Source Analysis now validates as a real route; menu naming is sourced from `NavigationCatalog` stable route IDs rather than matching labels.

## Intentional limits and remaining work

Deterministic checks establish structured consistency, not prose truth or completeness. The Settings, environment-tab and source-domain inventories are explicit metadata and must be updated alongside those UI registries; deeper consolidation into a single product registry is a future improvement. The current guide describes the main ownership/evidence boundaries but does not replace feature-specific operational instructions or current Technology Coverage output. Legacy Traceability and Spec Drift/Impact Analysis remain in the actual navigation catalog and are documented as such; this audit does not silently remove them.

Verification completed for this change: the full frontend suite passed (4,899 passed, 0 failed), and the User Guide browser check passed at 1440, 1100, 768 and 390 pixels with no horizontal overflow and no axe A/AA findings in those views. Debug and Release application builds passed during implementation; `git diff --check` passed. Backend tests and an axe scan of the Documentation Health settings view were not run. No commit was created because this working tree also contains unrelated in-progress work, including overlapping settings changes; preserving that work is safer than committing a mixed change.
