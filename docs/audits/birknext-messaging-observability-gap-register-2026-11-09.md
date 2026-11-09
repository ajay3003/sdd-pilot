# BirkNext — Messaging observability — gap register (2026-11-09)

Branch `008-traceability-first` (local only). Initial SHA `d440e37d`; final SHA = the commit carrying this file.
Statuses: PASS / FAIL / PARTIAL / BLOCKED / NOT RUN / NOT SUPPORTED. **Nothing here verifies real Azure Service Bus or Wolverine behaviour**:
every check used synthetic files and a local synthetic application.

**Overall: PARTIAL.** Message content, Wolverine processing state, per-message timelines and exact-id correlation with test executions are
implemented and verified (31/31); live runtime connections, contract validation of message bodies, IQR consumption and the Skolenærvær messaging
segments are not done.

| § | Capability | Status | Notes |
|---|---|---|---|
| 1 | Baseline audit and capability matrix | PASS | audit report |
| 2 | Terraform `azurerm_servicebus_*` discovery | PASS (existing) | not changed; no `terraform plan/apply/state` anywhere |
| 3 | Wolverine source analysis (handlers, publishers, routes, retry, outbox) | PASS (existing) | not changed |
| 4 | Shared topology model extension (Messaging* entities as Integration Topology nodes) | **NOT RUN** | observations live beside the topology (Messages tab); entities are not yet topology nodes |
| 5 | Static topology explorer for messaging | PARTIAL (existing) | Service Bus platform panel + topology; no dedicated messaging tree |
| 6 | Runtime connection design (modes A–D) | PARTIAL | mode A (static) existing; modes B/C/D replaced by *imported* observations; live modes need separate authorization |
| 7 | Entity inventory with counts | PASS (existing, off by default) | ARM GET + Azure Monitor; not run here |
| 8 | Safe Peek (non-settling) | **NOT SUPPORTED live**; PASS as import | BirkNext keeps no data-plane client (architecture guard unchanged); a peek export made with the tester's own access is imported |
| 9 | DLQ view (reason, description, delivery count, related run; no resubmit/replay/purge) | PASS | MO-17, MO-B5, MO-23 |
| 10 | Wolverine runtime (inbox / outbox pending, dead letters, attempts) | PASS as import | rows of the envelope tables; no database connection, no SQL |
| 10 | Wolverine telemetry (App Insights / OpenTelemetry) | PASS (existing, off by default) | not run here |
| 11 | Correlation with Execution Evidence (Confirmed / Ambiguous / Not correlated; never by time; no cross-environment merge) | PASS | MO-12…MO-14, tests |
| 11 | Trace/span-id and causation-id correlation | **NOT SUPPORTED** | only the execution id (correlation id or `X-BirkNext-Execution-Id` property) |
| 12 | IQR integration of message evidence | **NOT RUN** | IQR still reads configuration and counts only |
| 13 | Skolenærvær messaging segments | **NOT RUN** | |
| 14 | Message contract validation via Schema Studio | **NOT RUN** | |
| 15 | Error / retry / DLQ timeline; configured ≠ observed retries; vanished ≠ success | PASS | timeline + processing rules, tests |
| 16 | Persistence / retention / deletion | PASS (manual deletion) | table `message_observation_sets`; no automatic retention |
| 17 | UI inside Integration Topology (Messages tab), master–detail, responsive | PASS | MO-B1…MO-B10 |
| 18 | Security: redaction before persistence, no destructive operations, environment scoping, write permission for import/delete, size limits | PASS | MO-20…MO-23, tests |
| 20–21 | Isolated acceptance with independent ledger; browser | PASS | 31/31 |

## Defects found and fixed

| ID | Found by | Defect | Fix |
|---|---|---|---|
| MO-DEF-1 | Browser MO-B10 | Message tables widened the page at 768 / 390 px | shrinkable grid children; tables scroll in their region |

## Regression

| Suite (clean build of the final source, memory-guarded, sequential partitions) | Result |
|---|---|
| Backend P1 · P2 · P3 · P4 · P5 · P6 | 1031/0/2 · 1098/0/5 · 830/0/0 · 652/0/1 · 520/0/3 · 2108/0/8 |
| **Backend total** | **6239 passed, 0 failed, 19 skipped** |
| **Frontend** | **8503 passed, 0 failed** |

Scratch resources (not committed): `scratchpad/movalidation/`, DB `birknext_mo_20261109`, container `bn-mo-target` (:18091).
