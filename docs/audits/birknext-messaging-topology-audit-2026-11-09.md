# BirkNext — Messaging topology & observability — audit (2026-11-09)

Branch `008-traceability-first` (local only). Baseline `d440e37d`. Brief: "Messaging topology & observability" (Terraform · Service Bus ·
Wolverine · inbox/outbox · DLQ · retries · message inspection · E2E traceability). This audit records what already existed, so the
implementation adds only what is missing and no second topology engine.

## Capability matrix

| Capability | Existing / Partial / Missing | Owning module | Decision |
|---|---|---|---|
| Terraform parsing incl. `azurerm_servicebus_*` attributes (TTL, lock duration, max delivery count, dead-lettering on expiry, sessions, partitioning) | **Existing** | `SourceAnalysis/Evidence` (`TerraformEvidenceProvider`, `InfrastructureCatalog` "Messaging" attributes), `AzureEnvironment/DeclaredObservedComparer` | reused, unchanged |
| Service Bus platform topology (namespace, queues, topics, subscriptions) with provenance | **Existing** (Terraform-audited seed v2; `ServiceBusTopology`) | `Integrations` (`M2lbDevServiceBusSeed`, `ServiceBusPlatformPanel`) | reused |
| Runtime infrastructure: entity metadata (ARM GET only) and counts incl. dead-lettered (Azure Monitor) | **Existing**, off by default, needs an authorized identity | `Integrations/ServiceBus` (`ArmServiceBusMetadataSource`, `AzureMonitorServiceBusMetricsSource`) | reused; not run (no Azure in this task) |
| Wolverine source analysis: publishers, listeners, handlers, routes, retry/error policies, outbox/inbox, DLQ (`MoveToErrorQueue`) | **Existing** (Roslyn, syntax only, file:line facts) | `Integrations/ApplicationMessaging/WolverineSourceAnalyzer` | reused |
| Wolverine handler log telemetry (Log Analytics / App Insights) | **Existing**, off by default | `ApplicationMessagingTelemetry` | reused; not run |
| Integration Topology (canonical, providers, merge) | **Existing** | `IntegrationTopology` | the new view lives here (Messages tab) |
| Architecture guard: no data-plane clients, no message operations, GET-only HTTP for Azure | **Existing** (`AzureReadOnlyBoundaryTests` and the Service Bus regex test) | tests | kept; extended by a test on the new code |
| Message content (peek / DLQ peek) | **Missing** | — | **added as imported, sanitized observations** (no connection: live Peek is not authorized in this task and would need the data-plane client the guard bans) |
| Wolverine runtime state (inbox / outbox pending, dead letters, attempts) | **Missing** | — | **added as imported envelope-store rows** (no database connection, no SQL) |
| Per-message processing state and timeline | **Missing** | — | added (evidence-based; vanished ≠ success) |
| Correlation message ↔ test execution | **Missing** | — | added: exact Execution Evidence ids (`X-BirkNext-Execution-Id` = `{run}.{sequence}.{attempt}`), never time |
| Contract validation of message bodies via Schema Studio | Partial (Schema Studio exists; not wired to messages) | — | not done (gap) |
| IQR consumption of message evidence | Partial (IQR consumes Service Bus/Wolverine *configuration* and counts) | — | not done (gap) |

## Layers kept apart

STATIC CONFIGURATION (Terraform, Wolverine source) · RUNTIME INFRASTRUCTURE (ARM metadata, Azure Monitor counts) · MESSAGE CONTENT (imported,
redacted) · APPLICATION PROCESSING (Wolverine envelope rows) · OBSERVED END-TO-END EVIDENCE (correlation with Execution Evidence runs). The new
code only adds the last three, and only from files.
