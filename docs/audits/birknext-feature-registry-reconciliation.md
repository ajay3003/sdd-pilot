# Feature registry reconciliation (task 13)

## One registry per concern (no second registry)

| Concern | Authority | Notes |
|---|---|---|
| What a user can navigate to | `NavigationCatalog.Sections` (53 entries) | unchanged |
| Which switches exist and their single default | backend `AdminService` (Platform 4, Core 22, Advanced 8; `FeatureDefault`) | the sidebar flags and Settings now share one default |
| Which switch gates which entry | **derived**: `FeatureVisibilityInventory` evaluates each row's own rule with each switch off and on | replaces the hand-written `MenuItems` list |
| What code cannot say (lifecycle, no-effect reasons, retired keys, pages reached from elsewhere) | `FeatureVisibilityTaxonomy` declarations | checked against the code at runtime: a stale declaration becomes a navigation issue |
| Product availability | `FeatureAvailabilityCatalog` | unchanged |
| Help anchors and routable pages | `DocumentationCatalog` (`Features`, `DiscoverRoutes`) | reused, not duplicated |

## Counts (shipped configuration) and their units

| Count | Value | Unit |
|---|---|---|
| Switches | 30 | configurable keys (core + advanced); **25 with an effect, 5 without** |
| Platform switches | 4 | always on: Dashboard, User Guide, Recommended Workflow, System Settings |
| Sidebar entries | 53 | rows, including the Target Environments deep link |
| Shown | 47 | entries visible now (independently matched against the rendered sidebar links: 47 = 47) |
| Hidden | 6 | entries hidden by a switch: Change Traceability, Impact Analysis, AI Change Review, and three legacy traceability entries |
| Always shown | 20 | entries with no switch |
| Pages reached from other pages | 16 | aliases, redirects, contextual detail pages and the sign-in callback |
| Retired keys | 3 | Spec Comparison, Specification Deltas, Task Deltas |

## Switch → entry mapping (derived; selection)

| Switch | Entry | Rule |
|---|---|---|
| SampleProjects | Demo Projects | required |
| SampleProjects **or** SourceAnalysis | Import Project, Source Management | one of them is enough |
| RecommendedWorkflow (platform) | Recommended Workflow, System Understanding | always on |
| AdminSystemSettings (platform) | System Settings, Target Environments, Backup & Transfer | always on |
| LegacyTraceabilityNavigationEnabled **and** TraceabilityCoverage / TraceabilitySuggestions / CodeTraceability | Traceability & Coverage / Suggestions / Code Traceability | master switch plus each entry's own switch |
| ImpactAnalysis, ImplementationTraceability, AiChangeReview | Impact Analysis, Change Traceability, AI Change Review | required (optional, off by default) |

Every other switch with an effect gates exactly its own entry; the bUnit and browser checks confirm this.

## Switches without an entry

| Key | Lifecycle | Decision |
|---|---|---|
| QaArtifactLibrary | No effect | kept (configuration compatibility); shown as "No effect", no toggle |
| BlazorWasmSecurityReview | No effect | its page was retired |
| BlazorWasmPerformanceReview | No effect | Frontend Quality Review runs the checks |
| EnableExtractionReview, EnableArchitectureView | No effect | they switch a mode of a component no page renders |
| LegacyTraceabilityNavigationEnabled | Legacy, master switch | toggle kept |

## Pages without a sidebar entry (classified)

- **Redirect:** `/` → Project Quality.
- **Infrastructure:** `/authentication/{action}`.
- **Aliases:**
  - `ai-generated-code-review`
  - `change-traceability`
  - `quality-review`
  - `sample-projects`
  - `spec-drift`
  - `task-alignment-review`
  - `traceability-suggestions`
- **Redirects:**
  - `compare`
  - `compare/reviews`
  - `task-deltas`
  - `quality/source`
- **Contextual:**
  - `compare/reviews/{id}`
  - `source-analysis/application-messaging`
  - `implementation-review` (Implementation Evidence Review, opened from Task Explorer and requirement evidence)

A new page that is neither a sidebar entry nor in this list is reported as "Page without a sidebar entry".

## Disabled features (not enabled by this task)

| Feature | Why off | Successor |
|---|---|---|
| Change Traceability | optional Azure DevOps provenance | — |
| Impact Analysis | optional; legacy-removal debt recorded | Release Readiness / Relationship Review |
| AI Change Review | off in every shipped configuration | — |
| Traceability & Coverage / Suggestions / Code Traceability | legacy, behind the master switch | Requirements Traceability / Relationship Review / Change Traceability |

## Removed or corrected

- Orphan configuration keys `CreateTestScenario` and `QaReadiness` removed from `appsettings.json`.
- Dead route and link lists removed from `SystemSettings.razor`.
- Broken `/explorers` link fixed.
- The Dashboard's AI Change Review quick action now follows its switch.
