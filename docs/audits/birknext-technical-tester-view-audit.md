# Technical Tester view — audit

**Purpose:** technical verification of APIs, contracts, integrations, E2E flows, test execution, technical test data, performance and observable outcomes. It sits between Test Lead (oversight, functional and technical) and Developer (implementation).

| Group | Modules (route) | Why |
|---|---|---|
| Project context | Source Management (`sources`), System Understanding | Which version is tested; what the flow should do |
| Contracts & APIs | Contract Testing (`api-quality-review/contracts`), API Quality Review, Schema Studio | Stored REST/GraphQL contracts and their tests; API findings; XSD validation |
| Integration & E2E | Integration Test Suites (`integration-suites`), Critical E2E Regression, Integration Quality Review, Integration Topology | Suites and flows to verify; integration risks; the interfaces they cross |
| Test data & execution | Test Data, Test Execution & Evidence (`execution-results`), Parquet Quality Review | Fixtures and prerequisites; actual run outcomes; delivered file contracts |
| Performance & risk | Performance Test Review, Test Coverage & Overlap Review, Code Risk Review | Performance findings; coverage gaps; risky code to target |
| Quality & follow-up | Project Quality, Recommended Workflow, Requirements Traceability, User Guide | Shared status, next actions, requirement links |

**Considered and left out:**
- Source Analysis, Dependency and Pipeline Review: implementation concerns, kept for the Developer.
- Snapshots and Release Readiness: decisions for the Test Lead and Project Manager.
- Target Environments: configuration lives in System Settings, and the Test Lead and Project Manager hold the row.

All of them stay one search away.

**Not a permission:** selecting Technical Tester grants nothing. Running Contract Tests, Integration Suites, Critical E2E or performance tests stays gated by the existing backend policies and by Target Environment safety (active testing allowed only where the environment permits it). The view only changes which rows the sidebar shows first.

**Dashboard / Recommended Workflow:** `WorkspaceFocus` keeps the shared capabilities whose route is in this view. In practice these are contract, API, integration, E2E, performance, test data and coverage reviews, with their real state (ready now / blocked with the reason / done). Baseline risks appear with the lead "Confirm the source and environment baseline before integration verification." No view-specific metric is invented.

**Difference from Developer:** the views share 7 modules (Source Management, Contract Testing, API Quality Review, Schema Studio, Test Execution & Evidence, Code Risk Review, Recommended Workflow).
- Only Technical Tester has integration suites, E2E, integration quality and topology, test data, parquet, performance and coverage.
- Only Developer has source analysis, tasks, task alignment, change traceability, pipeline, dependency, technology and impact.
