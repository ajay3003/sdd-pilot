# Functional gap review

Static implementation review; a route or service is not treated as proof of complete user value.

| Workflow | Evidence | Open verification |
|---|---|---|
| Import/source analysis | ProjectImportController, ProjectImportService, shared validated archive reader, durable staging and snapshots. | Large archive memory profile and malformed archive fuzzing not measured. |
| Discovery/planning | TestPlanSuggestionsController includes explicit discovery, durable last result, project plans, edits, reviews, snapshots, compare and export. | Verify freshness and identity after source refresh across modules. |
| Manual Testing | Dedicated routes; saved plan cases feed durable pinned runs; step outcomes, evidence references, defects and completion history exist. | No interactive usability run; history aggregation risk PERF-01. |
| Requirements/coverage | Project traceability and requirement coverage APIs; source requirements are distinguished from guide business rules. | Independently reconcile metrics against canonical requirements and execution fixtures. |
| Test Data | Dedicated UI/API and reset lifecycle components. | Reset/default fixture behavior needs disposable-store acceptance. |
| Technical Testing | Frontend Quality, API Quality, Integration Quality, Performance and Critical E2E modules appear in routes, controllers, services and test folders. | Verify every status clearly distinguishes static review, configured checks and runtime evidence. |
| Delivery/release | Delivery Reporting and Canonical Interpretation own separate schemas; release-readiness services/tests exist. | Exercise stale/missing evidence, scope revision and policy version conditions. |
| Navigation | Shared workspace tabs, feature visibility inventory and dedicated Manual Testing routes exist. | Role-based deep-link and first-action usability not browser-tested. |
| Export | At audited HEAD, test-plan JSON/text export is read-only. | Uncommitted Azure DevOps export work is concurrent and excluded. |

No feature was declared incomplete simply because every path was not exhausted. Main concern: evidence freshness and semantic consistency across independently owned modules.
