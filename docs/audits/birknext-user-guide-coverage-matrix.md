# User Guide — coverage matrix (2026-11-13)

Generated from `NavigationCatalog` (every registered module route) against the guide sources (`Components/Guide/*.razor` + `Services/UserGuideContent.cs`).

**Covered** means the guide links the route or names the module's label. It shows the module is mentioned, not that the text is complete; depth per area is in the content audit.

Workflow-group ids of the Workspace Views (`pm-status`, `tl-prepare` …) are navigation groups, not modules; they are documented collectively under Workspace Views and are left out here.

| Module | Route | In guide |
|---|---|---|
| Workspace | `getting-started` | Covered |
| Project Quality | `dashboard` | Covered |
| System Understanding | `system-understanding` | Covered |
| Import Project | `project-import` | Covered |
| Source Management | `sources` | Covered |
| Demo Projects | `demo-projects` | Covered |
| Target Environments | `admin/system-settings?section=target-environments` | Covered |
| Delivery Management | `delivery-reporting` | Covered |
| Delivery Overview | `delivery-overview` | Covered |
| Specification Explorer | `specification-explorer` | Covered |
| Constitution Explorer | `constitution-explorer` | Covered |
| Data Model Explorer | `data-model-explorer` | Covered |
| Plan Explorer | `plan-explorer` | Covered |
| Task Explorer | `task-explorer` | Covered |
| Document Quality Review | `quality/document` | Covered |
| Task Alignment Review | `task-alignment` | Covered |
| Documentation Drift | `documentation-drift` | Covered |
| Requirements Traceability | `artifact-traceability` | Covered |
| Change Traceability | `implementation-traceability` | Covered |
| Impact Analysis | `impact-analysis` | Covered |
| Relationship Review | `correlation-review` | Covered |
| Release Readiness | `release-readiness` | Covered |
| Snapshots | `snapshots` | Covered |
| Traceability Suggestions | `traceability/suggestions` | Not covered |
| Code Traceability | `code-traceability` | Not covered |
| Source Analysis | `source-analysis` | Covered |
| Schema Studio | `schema-studio` | Covered |
| Generated Documentation Explorer | `generated-documentation-explorer` | Covered |
| Generated Documentation Health | `generated-documentation-health` | Covered |
| Technology Coverage | `technology-coverage` | Covered |
| Dependency Review | `dependency-review` | Covered |
| Pipeline Review | `pipeline-review` | Covered |
| Environment Analysis | `azure-environment` | Covered |
| AI Change Review | `ai-change-auditor` | Covered |
| Test Coverage & Overlap Review | `test-coverage-review` | Covered |
| Code Risk Review | `code-risk-review` | Covered |
| Test Data | `test-data` | Covered |
| Test Plan Suggestions | `test-plan-suggestions` | Covered |
| Contract Testing | `api-quality-review/contracts` | Covered |
| Integration Test Suites | `integration-suites` | Covered |
| Test Execution & Evidence | `execution-results` | Covered |
| Parquet Quality Review | `parquet-viewer` | Covered |
| Frontend Quality Review | `frontend-quality-review` | Covered |
| API Quality Review | `api-quality-review` | Covered |
| Integration Topology | `integration-topology` | Covered |
| Integration Quality Review | `integration-quality-review` | Covered |
| Performance Test Review | `performance-test-review` | Covered |
| Critical E2E Regression | `critical-e2e-regression` | Covered |
| Project Extensions | `extensions` | Covered |
| M01 – Gradert tilgang (Kode 6/7) | `security-classification-review` | Covered |
| Skolenærvær Testing | `extensions/skolenaervaer` | Covered |
| Help & Settings | `admin` | Covered |
| User Guide | `user-guide` | Covered |
| System Settings | `admin/system-settings` | Covered |
| Backup & Transfer | `workspace-transfer` | Covered |
| Delivery Management | `delivery` | Covered |
| Generated Documentation | `generated-documentation` | Covered |

The two **Not covered** rows, and the legacy Traceability & Coverage page (`traceability`, which the matrix lists under its group id), are legacy modules that are hidden by default (Feature Visibility → legacy traceability navigation). They get no article; Requirements Traceability and Relationship Review replace them.
