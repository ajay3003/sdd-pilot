# BirkNext independent codebase audit

Audit baseline: 0f3ca7dc505f1389a16f9787ac5450ff58d39820, branch 008-traceability-first, 2026-11-15. Static audit of committed HEAD; the working tree contains unrelated/in-progress changes.

## Assessment

BirkNext is a broad, capable local QA workbench with real implementations for bounded ZIP import, source snapshots, scenario discovery, saved plans, manual execution, traceability, and delivery evidence. Strong points include explicit provenance, snapshot concepts, cancellation-aware import, bounded archive handling, local-session/CSRF protection, optimistic concurrency for manual runs, and focused domain tests.

It is not established as production-ready. The principal risk is missing project-level authorization binding: reviewed APIs authenticate a caller but commonly scope records using caller-supplied project keys. This is acceptable only under the intended single-user loopback model; it is a P1 risk for shared Entra deployments. The large number of domain contexts and feature engines also raises maintenance and cross-module consistency costs.

## Findings

| ID | Severity | Summary |
|---|---|---|
| SEC-01 | P1 conditional | No visible identity-to-project authorization in reviewed project-scoped API paths. |
| ARCH-01 | P2 | Many persistence contexts and reset owners create coordinated migration/reset complexity. |
| PERF-01 | P2 | Manual execution history is fully loaded and deserialized for project-level traceability. |
| TEST-01 | P2 | Current suite reliability is unverified; no tests/builds run in this audit. |

No P0 defect established. No additional confirmed P1 asserted.

## Readiness

| Use | Assessment |
|---|---|
| Local development / POC | Suitable. |
| Internal evaluation | Suitable with supervision; derived analysis should be treated as reviewable evidence. |
| Controlled pilot | Conditional on deployment boundary, migration, backup, and telemetry verification. |
| Broader production | Not established. Resolve SEC-01 for shared deployments; validate migration/restore, access controls, operations, accessibility, and performance. |

## Evidence-based scorecard

Scores are 0–5. Confidence refers to the static evidence reviewed; the dirty working tree and no runtime verification limit confidence.

| Dimension | Score | Reason / evidence | Main risk | Confidence |
|---|---:|---|---|---|
| Architecture | 3 | Clear domain services and separate reporting/interpretation schemas, alongside many contexts and a broad composition root. | Cross-domain ownership and migration/reset coordination. | Medium |
| Maintainability | 3 | Explicit boundaries in import and test planning; large repository and registration/navigation surface. | Change coordination and onboarding cost. | Medium |
| Code quality | 3 | Reviewed paths use cancellation, validation, bounded input, and error mapping. Audit was sampled, not line-by-line across all source. | Inconsistent conventions elsewhere remain possible. | Low-medium |
| Security | 2 | Strong local loopback/session/CSRF controls; project-level principal binding not visible in reviewed paths. | Shared deployment cross-project access. | Medium |
| Data integrity | 4 | Immutable snapshot concepts, pinned manual runs and EF concurrency token observed. | Cross-context operations and historical payload evolution. | Medium |
| Test quality | 3 | Multiple test layers and targeted invariants exist; none run and assertion/flake quality not audited exhaustively. | Overconfidence from test count or provider-specific tests. | Low-medium |
| Functional completeness | 3 | Import → discovery → plan → manual run and delivery surfaces have concrete persistence/API flows. | Cross-module freshness and execution evidence semantics need end-to-end confirmation. | Medium |
| Cross-module consistency | 2 | Separate domain evidence owners and snapshot sources are explicit. | Same project facts may drift across stores and reports. | Low-medium |
| Performance | 2 | Archive limits are explicit; history aggregation deserializes all runs. No measurements. | Growth-related memory and query cost. | Low-medium |
| UX | 3 | Manual workflow is task-sequenced and workspace navigation is shared. | Broad module catalog may obscure first action. | Low |
| Accessibility | 2 | Reusable layout exists; no keyboard/screen-reader assessment. | Unknown barriers in critical workflows. | Low |
| Operational readiness | 2 | Reset, backup/transfer and diagnostics implementations exist. | Migration, restore, telemetry and recovery were not exercised. | Low |

## Next actions

1. Declare single-user loopback versus shared deployment.
2. Implement project-level authorization or refuse shared hosting.
3. Add cross-project negative tests for every API family.
4. Exercise migrations, concurrency and reset on disposable PostgreSQL.
5. Bound/paginate history aggregation.
6. Add resolved dependency inventory and advisory scanning.

See the detailed review and roadmap reports in this directory.
