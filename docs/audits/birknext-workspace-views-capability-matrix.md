# Workspace Views — capability matrix

Source of truth: `WorkspaceViews.All` in `AIAssisted/frontend/BirkNext.Web/Services/NavigationCatalog.cs`.

**Legend:**
- **P** = in the view's preset (a sidebar row, in the view's workflow group);
- **·** = outside the view (still reachable by search, "Show other modules", a deep link or All Modules);
- **E** = in every view (`WorkspaceViews.EveryView`: Project Quality, User Guide, System Settings);
- All Modules = every module the Feature Visibility settings allow (**A**).

A view is **never** a permission: Feature Visibility, product availability and backend authorization decide what exists and what may be done.

Abbreviations: PM = Project Manager (id `ProjectLeader`), SM = Scrum Master, TL = Test Lead, MT = Manual Tester (id `ManualTesting`), TT = Technical Tester (new, id `TechnicalTester` = 7), Dev = Developer, Arch = Architect.

| Module (route) | All | PM | SM | TL | MT | TT | Dev | Arch | Rationale (short) |
|---|---|---|---|---|---|---|---|---|---|
| Project Quality (`dashboard`) | A | E | E | E | E | E | E | E | Shared evidence-based status; every view |
| Recommended Workflow (`getting-started`) | A | P | P | P | P | P | P | P | "What next" for every audience |
| System Understanding | A | P | · | P | P | P | · | P | Learning the system: PM scope, TL/MT/TT test design, Arch primary; Dev works from source |
| Import Project (`project-import`) | A | · | · | · | P | · | P | · | Setting up a project: testers and developers |
| Source Management (`sources`) | A | P | · | · | P | P | P | P | Baseline clarity (PM), test context (MT/TT), source (Dev/Arch) |
| Demo Projects | A | · | · | · | · | · | · | · | Evaluation only |
| Target Environments | A | P | · | P | · | · | · | · | Environment readiness (PM, TL); configuration stays in System Settings |
| Delivery Overview | A | P | P | · | · | · | · | · | Delivery status (PM, SM) |
| Delivery Reports | A | P | P | · | · | · | · | · | Weekly progress (SM primary, PM) |
| Specification Explorer | A | · | P | P | P | · | P | · | Requirements (TL, MT), stories (SM), implementation spec (Dev) |
| Constitution Explorer | A | · | · | · | · | · | · | P | Governance principles (Arch) |
| Data Model Explorer | A | · | · | · | · | · | · | P | Data architecture |
| Plan Explorer | A | · | P | · | · | · | · | · | Planning (SM) |
| Task Explorer | A | · | P | · | · | · | P | · | Work items (SM, Dev) |
| Document Quality Review (`quality/document`) | A | · | · | P | P | · | · | · | Requirement review (testers) |
| Task Alignment Review | A | · | P | · | · | · | P | · | Plan ↔ implementation (SM impediments, Dev) |
| Documentation Drift | A | · | · | · | P | · | · | · | Requirement freshness for functional testers |
| Requirements Traceability (`artifact-traceability`) | A | P | · | P | P | P | · | P | Coverage (TL, MT, TT), scope (PM), governance (Arch) |
| Change Traceability (`implementation-traceability`) | A | · | P | · | · | · | P | · | Change follow-up (SM, Dev) |
| Impact Analysis | A | · | · | · | · | · | P | · | Source change impact (Dev) |
| Relationship Review (`correlation-review`) | A | P | · | P | · | · | · | · | Shared decisions affecting release (PM) and coverage (TL) |
| Release Readiness | A | P | P | P | · | · | · | · | Release decision (PM), blockers (SM), test confidence (TL) |
| Snapshots | A | P | · | · | · | · | · | P | Evidence history (PM decisions, Arch versions) |
| Legacy traceability rows (3) | A* | · | · | · | · | · | · | · | Retired behind `LegacyTraceabilityNavigationEnabled`; never reactivated by a view |
| Source Analysis | A | · | · | · | · | · | P | P | Implementation (Dev), structure (Arch) |
| Schema Studio | A | · | · | · | · | P | P | P | Contracts (TT, Dev, Arch) |
| Generated Documentation Explorer | A | · | · | · | · | · | · | P | Implementation-derived docs (Arch) |
| Generated Documentation Health | A | · | · | · | · | · | · | · | Specialist; All Modules |
| Technology Coverage | A | · | · | · | · | · | P | P | What can be assessed (Dev, Arch) |
| Dependency Review | A | · | · | · | · | · | P | P | Dependencies (Dev, Arch) |
| Pipeline Review | A | · | · | · | · | · | P | · | Build/pipeline concerns (Dev) |
| Environment Analysis (`azure-environment`) | A | · | · | · | · | · | · | P | Environment architecture (Arch) |
| AI Change Review | A* | · | · | · | · | · | · | · | Optional by default (Feature Visibility) |
| Test Coverage & Overlap Review | A | · | · | P | P | P | · | · | Coverage (TL, MT), technical risk (TT) |
| Code Risk Review | A | · | · | · | · | P | P | · | Risk (TT secondary), code quality (Dev) |
| Test Data | A | · | · | P | P | P | · | · | Test data readiness (TL, MT, TT) |
| Test Plan Suggestions | A | · | · | P | P | · | · | · | Functional preparation (MT primary, TL oversight) |
| Contract Testing (`api-quality-review/contracts`) | A | · | · | P | · | P | P | P | Contract verification (TT), quality (TL), implementation (Dev), boundaries (Arch) |
| Integration Test Suites | A | · | · | P | · | P | · | · | Integration verification (TT, TL) |
| Test Execution & Evidence (`execution-results`) | A | · | · | P | · | P | P | · | Execution evidence (TT, TL), test results of changes (Dev) |
| Parquet Quality Review | A | · | · | · | · | P | · | · | Technical data verification (TT) |
| Frontend Quality Review | A | · | · | P | · | · | · | · | Runtime frontend quality (TL) |
| API Quality Review | A | · | · | P | · | P | P | P | API verification (TT), quality (TL), implementation (Dev), boundaries (Arch) |
| Integration Topology | A | · | · | · | · | P | · | P | Integration map (TT, Arch) |
| Integration Quality Review | A | · | · | P | · | P | · | P | Integration risk (TL, TT, Arch) |
| Performance Test Review | A | · | · | P | · | P | · | · | Performance (TT, TL) |
| Critical E2E Regression | A | · | · | P | · | P | · | · | E2E flows (TT, TL) |
| M01 — Gradert tilgang | A | · | · | · | · | · | · | · | Project extension; All Modules and search |
| Skolenærvær Testing | A | · | · | · | · | · | · | · | Project extension; All Modules and search |
| User Guide | A | E | E | E | E | E | E | E | Help, every view |
| System Settings | A | E | E | E | E | E | E | E | Settings, every view |
| Backup & Transfer | A | · | · | · | · | · | · | · | Administration |

**Sizes** (preset rows, excluding the three every-view rows; the catalogue has 50+ modules):
- PM 10, SM 9, TL 19, MT 11, TT 17, Dev 17, Arch 17.
- Test Lead is deliberately the broadest: it covers functional and technical testing.
- Technical Tester and Developer share 7 modules (Source Management, Contract Testing, API Quality Review, Schema Studio, Test Execution & Evidence, Code Risk Review, Recommended Workflow) but differ in emphasis:
  - TT only: integration suites, E2E, integration quality and topology, test data, parquet, performance, coverage;
  - Dev only: source analysis, tasks, task alignment, change traceability, pipeline, dependency, technology, impact.

**Workflow groups per view:** see the `Groups(...)` definitions. Every specialized view now uses audience groups (e.g. Technical Tester: Project context · Contracts & APIs · Integration & E2E · Test data & execution · Performance & risk · Quality & follow-up). All other modules follow in their catalogue groups, but only as "Show other modules" rows.
