# BirkNext — API & GraphQL Execution Evidence: capability audit (2026-11-07)

Scope: how BirkNext sent REST and GraphQL requests, and what it kept of them, **before** the shared Execution Evidence capability was
added (baseline `92bef4b4`, branch `008-traceability-first`). Read-only code audit; no external system was contacted.

## 1. Where requests were sent

| Path | Component | Purpose | What was kept |
|---|---|---|---|
| API Quality Review | `Services/ApiQuality/ApiReviewEngine.cs` (`publicClient.SendAsync`, `ResponseBodyReader`) | Preflight, contract-driven REST/GraphQL checks, latency | Aggregated review result (status, shape paths, GraphQL error counts, latency tier). No per-request record. |
| AQR safe fuzzing | `Services/ApiQuality/Fuzzing/ApiFuzzingService.cs` | Bounded negative requests | Fuzz findings only. |
| Authenticated execution | `Services/LocalHttpsProxy/AuthenticatedApiExecutionService.cs` (+ `TransientAuthenticatedApiContextStore`) | REST/GraphQL with the proxy-captured session | Result object returned to the caller; credentials transient, never persisted. |
| Performance trials | `PerformanceTrialService` named client | Bounded trial requests | Trial metrics. |
| Altinn testing | `AltinnTestingRequestGuard` named client | Guarded Altinn calls | Altinn-specific evidence. |
| Topology/contract fetch | `OpenApiTopologyProvider` named client | Fetch OpenAPI documents | Contract, not an exchange. |

Result history for API Quality Review lived in the browser (`frontend/.../Services/ApiReviewService.cs`, `ApiReviewHistoryService`, per
profile), as summaries.

## 2. Findings

| ID | Finding | Consequence |
|---|---|---|
| EEA-1 | No run → logical call → physical attempt model anywhere. | A retry, a timeout or a transport error could not be told apart from "the call happened once". |
| EEA-2 | No correlation identifier on outgoing requests. | What BirkNext *believed* it sent could not be reconciled against what a target *received*. |
| EEA-3 | No persisted request/response evidence (headers, bodies, status, timing). | A tester could not show what was sent and received for a failed check, and nothing survived a restart. |
| EEA-4 | Redaction existed per feature (e.g. evidence sanitizers) but not as one rule applied **before** any execution evidence is stored. | Adding storage without a shared redaction step would risk persisting secrets. |
| EEA-5 | GraphQL "HTTP 200 with `errors`" was evaluated inside AQR only. | No reusable assertion model for GraphQL envelopes. |
| EEA-6 | Each executor had its own `HttpClient` setup. | Capture would have to be re-implemented per module (the brief's addendum explicitly asked for one shared capability). |
| EEA-7 | No export, compare or history of individual exchanges. | No audit trail from Test Data → run → request → response → assertion → result. |

Existing controls that the new capability **must reuse, not weaken**: `IApiEnvironmentSafetyPolicy` (AQR policy: Local / Development /
QA / Test only; production claims, production host markers and blocked hosts refused; loopback exempt), the local session / origin
checks on the API, and the rule that credentials are never persisted.

## 3. Decision

One shared platform capability (`BirkNext.ExecutionEvidence` contracts in `shared/`, tables in `AppDbContext`), with:

- capture at the transport boundary (one `DelegatingHandler` directly above the socket handler), so every consumer gets the same evidence;
- redaction (`ee-redaction-v1`) before persistence;
- Test Data owning inputs (`TestDataRefs` on each call), Execution Evidence owning the history of what was sent and received;
- no replay and no execution from opening a result.

Migrating the existing executors (§1) onto it is **not** part of this task; see the gap register in the implementation report.
