# BirkNext — User documentation & Help Center: audit (2026-11-11)

- **Branch and baseline:** `008-traceability-first`, baseline `4c424ca6`.
- **Method:** read-only code audit of HEAD. Paths are relative to `AIAssisted/frontend/BirkNext.Web` unless stated otherwise.
- **Related documents:** information architecture `../architecture/birknext-help-center-information-architecture.md`; implementation `../hardening/birknext-user-documentation-implementation.md`; acceptance `birknext-user-documentation-acceptance.md`; gaps `birknext-user-documentation-gap-register.md`.

## 1. Existing documentation architecture (baseline)

| Aspect | Finding | Evidence |
|---|---|---|
| Route | One page, `/user-guide` (nav label "User Guide", alias "Help"), visible by default and pinned in every Workspace View | `Pages/UserGuide.razor:1`; `NavigationCatalog.cs:211,379` |
| Storage | Hand-written Razor prose in six section components, plus structured lists in `UserGuideContent.cs`: 39 topics in 6 groups, 7 task cards, 16 status terms, 16 glossary entries, 17 troubleshooting entries. A feature registry (52 features) with per-feature details is in `DocumentationCatalog.cs`. No Markdown, no CMS. | `Components/Guide/*.razor`; `Services/UserGuideContent.cs`; `Services/DocumentationCatalog.cs` |
| Rendering | Razor-encoded, with no `MarkupString` in the guide, so it is safe by construction | — |
| Deep links | Fragments `/user-guide#id`; `userGuide.js` scrolls, focuses the heading and keeps `aria-current` (scroll spy) | `wwwroot/js/userGuide.js` |
| Search | Title, group and keywords only; no body text, excerpts or synonyms | `UserGuideContent.Search` |
| Navigation | Collapsible sidebar groups and task cards; one long page | `UserGuide.razor` |
| Contextual help | Plain "Learn more" links on 6 pages/components; no shared component; most features never link to the guide | Audit list (ExternalIntegrationsPane, TargetEnvironmentProjectPane, AzureEnvironmentAnalysis, PerformanceTestReview, SpecDrift, TestDataPage, RecommendedWorkflow) |
| Coverage guard | `DocumentationCatalog.Check` (every navigation feature documented, real routes, anchors present, no broken `href` in the guide sources, no legacy terms), in tests and in System Settings → Documentation Health | `DocumentationCatalogTests`, `UserGuidePageTests` |
| Version metadata | None per article | — |
| What's new | None; no release-notes source exists | — |
| Status vocabularies | Two that are not linked: the guide's 16 terms and the 48-value `StatusVocabulary`, the latter exported to `AIAssisted/docs/status-vocabulary.md` and not shown in the app | `StatusVocabulary.cs` |

## 2. Accuracy findings

Classification: ACCURATE / OUTDATED / INCOMPLETE / INCORRECT / UNVERIFIED.

| Area | Finding | Class | Resolution |
|---|---|---|---|
| Integration Test Suites, Parquet Quality Review | Articles exist, but they were not registered topics, so they were missing from navigation and search | INCOMPLETE | Registered as topics |
| Suggested E2E tests, Edge Companion DOM verification | No article. Only a general Browser Discovery mention. | INCOMPLETE | New article `suggested-e2e`, using the page's real action labels |
| MU → MM schema version | Not documented. The risk must not be stated as a confirmed incompatibility. | INCOMPLETE | `mu-mm-contract-risk`, worded to the gap report MM-G1 |
| Execution outcomes (Passed / Failed / Error / Timed out / Blocked / Not assessed / Skipped / Cancelled / Interrupted) | The status reference covered review and configuration words only. Execution outcomes were explained only in the Execution Results prose. | INCOMPLETE | Execution outcomes table, plus the "not review statuses" rule |
| Historical vs current results (Artifact Versioning, `5044ae3b`) | Not documented | INCOMPLETE (new capability) | Result currency table; Versions article |
| BirkNext build versioning (`edae017a`) | Not documented | INCOMPLETE (new capability) | Versions article; troubleshooting |
| Backup & Transfer | Accurate against the Backup & Transfer acceptance | ACCURATE | Verification recorded |
| Execution Results four areas | Accurate (Overview, Runs & Results, Evidence & Traceability, Storage & Retention) | ACCURATE | Verification recorded |
| What's new | No release-notes source; nothing claimed | ACCURATE (absence) | An explicit "not available" article; no commit messages used as release notes |
| Feature matrix: 7 features with generic detail text (impact-analysis, correlation-review, release-readiness, snapshots, generated-documentation-health, traceability/suggestions, code-traceability) | Fallback text, broad anchors | INCOMPLETE | Not changed; gap UD-G2 |
| Status vocabulary duplication | Two vocabularies | INCOMPLETE | Not merged; gap UD-G3 |
| Other articles (about 36) | Not re-verified in this task | UNVERIFIED | Each article now states "Verification against the current version not recorded"; none claims to be current |

## 3. Security finding outside the Help Center

`Services/MarkdownRenderingService.cs` (used by the Constitution, Data Model, Plan and Task explorers to render **imported project documents**):
- does not call Markdig's `DisableHtml()`, although its comment says HTML is escaped;
- removes only `<script>` and `on*=` afterwards.

So `<iframe>`, `<form>`, `<object>` and `<style>` could pass into `MarkupString`. Its tests assert only that some text exists.

The Help Center itself renders no Markdown, so this is **not** a Help Center defect. It is recorded as gap UD-G1 for a separate, reviewed fix, because escaping all HTML changes how existing documents render.

## 4. Feature-to-documentation coverage (52 navigation features)

Method: every row of `NavigationCatalog`, matched to its `DocumentationCatalog` anchor:
- **DOCUMENTED:** a dedicated article whose content names the feature's page and workflow.
- **PARTIALLY_DOCUMENTED:** a shared or broad article, or generic catalog detail text.
- **MISSING:** no article.

| Status | Count | Features |
|---|---|---|
| DOCUMENTED | 33 | Recommended Workflow, Import Project, Source Management (import article), Target Environments, Delivery Overview, Delivery Reports, Task Explorer, Documentation Drift, Source Analysis, Schema Studio, Generated Documentation Explorer, Technology Coverage, Pipeline Review, Environment Analysis (Azure), Test Coverage & Overlap, Code Risk Review, Test Data, Contract Testing, Integration Test Suites, Test Execution & Evidence, Parquet Quality Review, Performance Test Review, Critical E2E Regression (with Suggested tests), Integration Quality Review, Integration Topology (journeys), Skolenærvær Testing, User Guide, System Settings, Backup & Transfer, Demo Projects (project inputs), Project Quality (feature matrix + dashboard text), Specification Explorer and Plan Explorer (explorers article) |
| PARTIALLY_DOCUMENTED | 19 | Constitution Explorer, Data Model Explorer, Document Quality Review, Task Alignment Review, Requirements Traceability, Change Traceability, Impact Analysis, Relationship Review, Release Readiness, Snapshots, Traceability & Coverage, Traceability Suggestions, Code Traceability, Generated Documentation Health, Dependency Review, AI Change Review, Frontend Quality Review, API Quality Review, M01 Kode 6/7 (these share the broad `traceability`, `quality-reviews`, `explorers` or `optional-capabilities` articles, or have generic catalog detail) |
| MISSING | 0 | — |

Coverage is a judgement against the article content, not a count of titles. The structural guard (`DocumentationCatalog.Check`) still requires every feature to have an anchor.
