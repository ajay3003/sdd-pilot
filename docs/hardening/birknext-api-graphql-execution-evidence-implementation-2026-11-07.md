# BirkNext — Unified API & GraphQL Execution Evidence: implementation (2026-11-07)

Branch `008-traceability-first`, local commits only (nothing pushed):

| Commit | Content |
|---|---|
| `bd36b034` | Shared contracts, EF tables + migration, capture handler, redaction, assertions, store, run service, recovery, controller, tests |
| `563d91df` | Execution Results page (`/execution-results`), navigation, user guide, bUnit tests |
| `65bea9c1` | EE-DEF-1: every wire transmission recorded per attempt (regression test red → green) |
| `230cfed2` | Results KPI shows "N sent on the wire" when the HTTP stack resent a request |
| `f074b398` | Culture-independent `tabindex` on the call tabs (found in browser acceptance; regression test red → green) |
| `a7202385` | KPI notes separated from their values |

## 1. Model

`Run → Logical call → Physical attempt → captured Request / Response → Assertion results` (`shared/ExecutionEvidenceContracts.cs`).

- **States** (`ExecutionState`): Planned, Queued, Running, Passed, Failed, Error, Skipped, Blocked, Cancelled, TimedOut, Interrupted,
  NotAssessed. *Failed* = the exchange happened and an assertion failed; *Error* = a technical problem prevented a normal exchange.
  No assertions → NotAssessed (never a pass by default).
- **Transport** (`TransportResult`): ResponseReceived, ConnectionFailed, TimedOut, Cancelled, NotSent. A response is never invented
  for an attempt that did not receive one.
- **Attempt** records request, response (when received), error, retry reason and **`Transmissions`** — how many times the request
  actually went on the wire (see EE-DEF-1). The run summary records **`WireTransmissions`**.
- **Assertions**: StatusCode, Header, JsonPath, ResponseTime, GraphQlErrorCount / ErrorCode / ErrorPath / DataPath / HasData.
  GraphQL HTTP 200 with `errors` fails unless the test expects the error.
- **Traceability fields** on each call: `TestDataRefs`, `OperationRef`, `DefinitionVersion`; on the run: `SourceSnapshotId`,
  `ContractRefs`, environment, capture policy, redaction version.

## 2. Components

| Component | File | Notes |
|---|---|---|
| Storage | `Models/ExecutionEvidenceRecords.cs`, `Data/AppDbContext.cs`, migration `20261107201943_AddExecutionEvidence` | Tables `execution_runs`, `execution_calls`; unique (RunId, Sequence). Reuses the existing database; no new store. |
| Capture | `Services/ExecutionEvidence/EvidenceCaptureHandler.cs` | Last handler before the socket handler; request headers + body bytes, bounded response read (1 MB read, 64 KB stored text). |
| Wire counting | `Services/ExecutionEvidence/TransmissionCountingStream.cs` | Installed through `SocketsHttpHandler.PlaintextStreamFilter` (after TLS); counts request heads by the `X-BirkNext-Execution-Id` correlation header. |
| Redaction | `Services/ExecutionEvidence/ExecutionEvidenceRedaction.cs` | `ee-redaction-v1`, applied before persistence: sensitive headers, URL query values, JSON / XML / form fields by name and value pattern, 11-digit identifiers. Binary and truncated JSON → metadata only. |
| Assertions | `Services/ExecutionEvidence/ExecutionAssertions.cs` | Dotted JSON paths with indexes; GraphQL envelope parsing. |
| Store | `Services/ExecutionEvidence/ExecutionEvidenceStore.cs` | Create, per-call save, complete, recover, list, filtered/paged detail, compare, export (ZIP: `manifest.json`, `calls/NNNN.json`, `hashes.json`), delete (not while running). |
| Execution | `Services/ExecutionEvidence/ExecutionRunService.cs` | Background, sequential, per-call persistence, retry policy, timeout, cancel; correlation `"{runId:N}.{sequence}.{attempt}"`; max 500 calls. |
| Recovery | `ExecutionEvidenceRecoveryService` | On start-up, runs left Running are marked Interrupted; nothing is re-sent. |
| API | `Controllers/ExecutionEvidenceController.cs` (`api/execution-evidence`) | Start, cancel, list, detail, call, compare, export, delete. |
| UI | `frontend/.../Pages/ExecutionResults.razor` | Run history, KPIs, filters/search/sort/paging in the URL, call tabs Request / Response / Assertions / Attempts / Evidence, attempt compare, export link. Never starts or replays a run. |

## 3. Safety (unchanged rules, reused)

- Target = the selected environment's origin; calls carry **relative paths only** (no free URL, so no generic HTTP executor).
- `IApiEnvironmentSafetyPolicy` evaluated before anything is sent; Production refused.
- Blocked before sending: non-relative paths, credential headers in the definition (Authorization, Cookie, API keys), unsupported
  methods, GraphQL without a query, subscriptions, non-object variables, mutating calls without explicit confirmation
  (`ConfirmMutatingCalls`), and `UseTargetAuthentication` (authenticated execution is not wired to this path yet → Blocked with reason).
- No redirects, no cookies; the definitions persisted are the sanitized ones, the originals are only sent.
- No replay endpoint; opening a run or a call is read-only (verified: target observation count unchanged, XR-23 / EE-21).

## 4. Defects found and fixed during this task

| ID | Defect | Root cause | Fix | Regression |
|---|---|---|---|---|
| EE-DEF-1 | The 100-call acceptance target received each dropped call **4 times** while BirkNext stored 1 attempt. | .NET `SocketsHttpHandler` (HTTP/1.1) resends a request **without a body** (GET/DELETE) when the connection closes before any response byte. It happens below every `DelegatingHandler`; disabling pooling, `Connection: close`, `MaxConnectionsPerServer=1`, no warm-up — all still 4 transmissions (scratch probe). POST with content: 1. | The connection's plaintext stream counts every request head by correlation id; each attempt records `Transmissions` and an explanatory error when > 1; run records `WireTransmissions`; UI shows "N sent on the wire". The resend itself cannot be disabled, so the evidence now reports it instead of hiding it. | `A_dropped_connection_records_every_wire_transmission` (raw keep-alive TCP server): red without the stream filter, green with it. |
| EE-DEF-2 | On the call tabs every tab was a tab stop. | `tabindex="@(cond ? 0 : -1)"` is culture-formatted; in the browser (ICU) nb-NO writes `−1` (U+2212), which browsers ignore. | Literal `"0"` / `"-1"`. | `Only_the_selected_tab_is_in_the_tab_order_under_a_Norwegian_culture` (pins the ICU negative sign; NLS on Windows uses a hyphen, so `nb-NO` alone does not reproduce): red → green. Browser XR-19. |
| EE-DEF-3 | KPI read "110119 sent on the wire" / "100of 100". | Razor dropped the leading space in the note span. | Separate `xr-kpi-note` element with margin. | bUnit asserts the note element; browser XR-02 checks note text and margin. |

Earlier fixes inside `bd36b034` (before the first commit): the executor initially sent the sanitized definitions (now sends originals,
persists sanitized); a duplicate create with an empty run id; `EF.Functions.ILike` unsupported by the in-memory provider (search uses a
provider-neutral lower-case contains).

## 5. Tests

- Backend `ExecutionEvidence*`: 30/30 (mixed 10-call run with server-side oracle: 12 attempts = 12 wire transmissions = 12 server
  receipts, redaction, GraphQL negative/partial, transport failure, timeout, blocked cases, recovery, export hashes, wire transmissions).
- Frontend `ExecutionResultsPageTests`: 5/5.
- Full regression on the final commit: see the acceptance report §4.

## 6. Remaining gaps (registered, not done)

| ID | Gap |
|---|---|
| GAP-EE-1 | Existing executors (AQR `ApiReviewEngine`, safe fuzzing, `AuthenticatedApiExecutionService`, performance trials, Altinn) do not yet write Execution Evidence; Contract-Driven Testing is the first planned consumer. |
| GAP-EE-2 | `UseTargetAuthentication` is Blocked: the authenticated path (transient proxy session) is not connected to the run service. |
| GAP-EE-3 | Timing is client elapsed time only; DNS / connect / TLS / first byte are not captured (stated in the UI). |
| GAP-EE-4 | The same culture-formatted int `tabindex` exists in 7 other components (10 sites): `BrowserDiscoveryTab.razor` 120, 349; `BrowserQualityWorkspace.razor` 11, 15, 18; `TestCoverageReview.razor` 128; `EndpointDiscoveryTab.razor` 56; `FrontendAnalysisSettings.razor` 213; `PerformanceScenarioSteps.razor` 468; `SpecExplorerPanel.razor` 122. Not changed here (scope). |
| GAP-EE-5 | No automatic retention policy; runs are deleted explicitly (`DELETE api/execution-evidence/runs/{id}`). |
| GAP-EE-6 | No Results-page entry from AQR / Test Data yet (deep links exist via `?run=&call=`). |
| GAP-EE-7 | Replay is intentionally absent; a safe, explicit re-run design is left to Contract-Driven Testing. |
