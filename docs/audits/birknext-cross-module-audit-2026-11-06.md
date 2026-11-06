# BirkNext Independent Audit â€” Cross-Module Data and Workflow Review

## Result

The following is a **bounded static trace**, not a complete verification of every application module. It records the currently demonstrated ownership edges and flags untraced consumer behavior. The historical hardening reports were used to identify claims to recheck, not to establish current correctness.

## Producer/consumer matrix

| Workflow edge | Producer / owner | Consumer | Current-source evidence | Assessment |
|---|---|---|---|---|
| Project archive â†’ staged import | API `ProjectImportService.Preview`; validates through the shared Source Analysis archive reader and stages archive/workspace. | Project Import UI and `CommitAsync`. | [ProjectImportService.cs:20â€“60, 95â€“105](../../AIAssisted/backend/BirkNext.Api/Services/ProjectImport/ProjectImportService.cs) | **Source confirmed.** Preview is not persistence of final project evidence. UI activation and backend source snapshot are separate owners. |
| Commit â†’ Source Analysis snapshot | `ProjectImportService.SourceAsync` and `IqrSourceStore`; source evidence is stored under Source Analysis ownership, with import provenance. | Source Analysis, snapshot registry projections, schema/test-data evidence consumers. | [ProjectImportService.cs:127â€“173](../../AIAssisted/backend/BirkNext.Api/Services/ProjectImport/ProjectImportService.cs) | **Source confirmed** for snapshot creation/reuse. Completeness/freshness of each downstream consumer was not exhaustively traced. |
| Same archive retry â†’ current snapshot | Import identity from archive SHA; staged commit lock; current snapshot reuse by import ID and archive hash. | Import caller retry/double-submit. | [ProjectImportService.cs:29, 95â€“105, 135â€“143](../../AIAssisted/backend/BirkNext.Api/Services/ProjectImport/ProjectImportService.cs); tests [212â€“240](../../AIAssisted/backend/BirkNext.Api.Tests/ProjectImport/ProjectImportServiceTests.cs) | **Implemented and reachable in source; test source exists; tests not run here.** |
| Source/snapshot â†’ Traceability / canonical relationships | Existing architecture separates artifact trace links and canonical relation records. | Traceability and release readiness. | Current source inventory and historical ownership reports; full request-to-storage trace was not repeated for every page/controller in this pass. | **Partial.** Do not combine counts or declare a single source of truth without examining exact relationship type and owner. |
| Integration discovery â†’ Integration Catalog â†’ IQR | Integration topology/catalog and quality review have separate UI/API services and domain models. | Readiness and run actions. | Service registrations in [Program.cs:154â€“177, 700â€“786](../../AIAssisted/backend/BirkNext.Api/Program.cs); frontend clients in [Program.cs:273â€“367](../../AIAssisted/frontend/BirkNext.Web/Program.cs) | **Registration confirmed; all consumer data semantics not revalidated.** A registration does not prove a given page uses the latest source revision. |
| Test Data â†’ XML fixture â†’ consumer readiness | Test Data catalog/fixture services and schema validation are registered; separate backend and frontend interfaces exist. | SkolenÃ¦rvÃ¦r and other consumers. | [Program.cs:700â€“735](../../AIAssisted/backend/BirkNext.Api/Program.cs), Test Data UI route `/test-data`; no live generation/reload was performed. | **Partial.** Persistence, duplicate generation, readiness invalidation and actual file reload are not reverified here. |
| API/IQR/Frontend/Performance/M01 reviews â†’ delivery/release | Multiple review engines and release readiness exist; source model and snapshot identifiers cross boundaries. | Delivery reporting and frozen assessments. | Current route inventory and registrations; full run-ID/snapshot freshness matrix not traced end to end. | **Not fully verified.** Historical summaries are not current proof. |

## Cross-module consistency observations

1. **Different owners are intentional at import.** The API owns source-analysis snapshots; the browser workspace/artifact repository owns activated document artifacts. The import serviceâ€™s own contract says these stores are not merged. A complete consistency assertion therefore needs matching import provenance and explicit artifact/snapshot references, not equal row counts.
2. **Currentness must be scope-specific.** Project Import reuses an identical snapshot only when it is still the current Source Analysis snapshot. This avoids treating an old identical revision as current after a later revision has become current. It also means â€œsame bytes exist historicallyâ€ and â€œcurrent snapshotâ€ are not interchangeable.
3. **Readiness claims require evidence scope.** The route/service inventory shows many independent review modules. This pass did not prove that all page-level summary cards use the same scope, timestamp, snapshot or run ID. Any dashboard aggregation should be verified against the underlying result models before being described as a common release verdict.
4. **Manual Review and Security Classification changes landed during this audit.** Commits made outside this audit added or updated these areas. Their full workflow, persistence, API contract and readiness propagation were not reverified here.

## Workflow review coverage

| Workflow | Source path inspected | Consumer/state inspected | Runtime verified |
|---|---|---|---|
| Project Import / repeat commit | Yes, API service and focused test source | Partial; commit idempotency and snapshot reuse logic | No |
| Source Analysis â†’ explorers | Registration/route inventory only | Partial | No |
| Requirements Traceability â†” canonical relations | Historical ownership claim used as lead; no full current trace | Partial | No |
| Integration Topology â†’ catalog â†’ IQR | DI registration and frontend client registration | Partial | No |
| Test Data/XML â†’ consumer | DI registration and relevant route inventory | Partial | No |
| API/Frontend/Performance/Security reviews â†’ release | Routes and relevant current API surface inventory | Not complete | No |
| Delivery Overview/Reports â†’ release | Registrations/route inventory only | Not complete | No |

For remaining confidence requirements see [BQA-004](birknext-gap-register-2026-11-06.md#gap-register).
