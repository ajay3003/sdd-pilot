# BirkNext — REST & GraphQL complete execution lifecycle: implementation (2026-11-08)

Branch `008-traceability-first` (local only). Baseline HEAD `d0a57a2e`. Audit: `docs/audits/birknext-rest-graphql-execution-lifecycle-audit-2026-11-08.md`.
Acceptance and gap register: `docs/audits/birknext-rest-graphql-execution-lifecycle-{acceptance,gap-register}-2026-11-08.md`.
No new engine, authentication mechanism or result store: everything extends Contract Testing (`ContractTestService`), Test Data
(`TestDataResourceContent`), the attended Local HTTPS proxy session and Execution Evidence (`ExecutionRunService`, `ExecutionEvidenceStore`).

## Commits

| Stage | Commit | Scope |
|---|---|---|
| A | `1806f9d0` | Baseline and architecture audit |
| B | `110afb72` | Authentication lifecycle |
| C | `d27cbbd9` | Dataset rows, typed resolution, data-driven runs |
| D | `a918cd70` | Chained REST/GraphQL calls |
| E–G | `5f6fa56e` | Storage, integrity, protection, deletion, retention, usage, audit, export |
| H | `cae1e21d` | Execution Results management UI, attended session status |
| I | `e79cecd6` | No automatic resend of data-changing calls; unknown outcome |
| J | `77bdcd8a` | LC-DEF-1 found by the browser acceptance: Local HTTPS proxy closes the browser side when the target closes an idle keep-alive connection |
| K | (reports commit) | Source-ownership allow-list follows the export, regression, timeout investigation, reports |

## B — Authentication lifecycle

- **Authorization to execute (C).** `POST api/execution-evidence/runs`, `…/runs/{id}/cancel`, `POST api/contract-testing/runs` and
  `POST api/contract-testing/chains/{id}/runs` require the existing `ActiveEventExecute` policy (a permission claim, or the Development +
  loopback + configured-frontend-origin local grant). Previously only the environment safety policy applied.
- **Attended session (B), same mechanism.** `TransientAuthenticatedApiContextStore` now keeps a per-profile **generation** (a different token
  increments it; re-observing the same token keeps it) and a non-reversible **identity reference** (SHA-256 of issuer + `sub`/`oid`, 12 hex;
  `BearerTokenInspector`). `TryApply` returns an `AttendedSessionStamp` (generation, identity, expiry) — never the value.
- **Per-call evidence.** `ExecutionAttempt.AuthenticationSession` names the credential instance (`attended-session:{env}#g2`,
  `api-credential:{env}:ClientCredentials:v1#t3`); `ExecutionCallRecord.Authentication` is `NotApplicable`, `Authenticated`,
  `AnonymousByDesign`, `BlockedForAuthentication`, `FailedAuthentication` (401 / GraphQL `UNAUTHENTICATED`) or `AuthorizationDenied`
  (403 / `FORBIDDEN`). The run summary lists `AuthenticationSessions` in first-use order.
- **Expiry mid-run.** Opt-in `ReauthenticationWaitSeconds` (0–900, once per run): the waiting call shows "Waiting for the attended session to be
  renewed…"; a renewed session of the **same identity** continues the run; a different identity is refused; if nothing arrives in time the
  remaining authenticated calls are Blocked for authentication. Cancellation during the wait sends nothing. Never a fallback to anonymous.
- **Unattended.** Client-credentials token requests are single-flight per credential (no refresh race); acquisitions are numbered (`#tN`) and
  reset when the credential changes.
- **Session status and end ("logout").** `POST api/local-https-proxy/attended-session` (metadata) and `…/attended-session/end` (wipes the
  in-memory credential; the proxy keeps running). Contract Testing shows the session (generation, expiry) and an "End attended session" button.
- Not added on purpose: a BirkNext-owned authorization-code/PKCE client (a second mechanism) and an expected-audience setting (would need new
  Target Environment settings; the target API enforces its audience). See the gap register.

## C — Test Data rows and typed resolution

- `TestDataResourceContent.Rows` (`TestDataDatasetRow(Id, Values)`) in the existing Dataset kind: stable row ids, ≤ 1000 rows, one type per
  column (Null allowed), the same type/credential/identity-number checks as members. Versioned and approved like every Test Data resource.
- Editor: a rows table (typed columns, row ids, add/remove) and a CSV paste import with a type guess to review; an untouched empty member line is
  no longer saved.
- `ContractTestService.BoundVariants`: one execution per row; members are shared and a row value wins. Readiness and the picker count values
  provided by every row ("… in each of its 50 rows"). A row missing a required value makes the test Not ready — never a default.
- Each logical call records `DatasetRow` (`alias@vN#row`) and `ResolvedValues` (name, exact type, source "row r-007 of children@v3" /
  "member of …" / "step create → childId", redacted preview ≤ 120 characters).
- Run options: selected rows, max executions (the rest listed as not run), stop on first failure (remaining calls Skipped, never sent).
- `ContractTestRules.Materialize` fills JSON arrays (typed elements; an unresolved optional element is left out; a required one is reported).

## D — Chains

- Table `contract_test_chains` (immutable revisions; migration `AddContractTestChains`), API `api/contract-testing/chains` (list, get, create,
  save revision with conflict check, delete with write permission, run with execute permission).
- A step = existing contract test (+ optional dataset row) with outputs (`OutputExtraction`: JSON path / GraphQL data path / safe response
  header, expected kind) and inputs from earlier steps. Saving refuses unknown tests, inputs, outputs, self-references, credential-like outputs
  (names, paths or `Authorization`/cookie headers) and cycles (`a → b → a`).
- The run resolves `{{steps.<step>.<output>}}` at send time (`ExecutionOutputs.Resolve`: typed in JSON bodies/variables, URL-escaped in paths).
  A dependent of a step that did not pass is Blocked ("depends on step create, which ended Failed"); a missing output fails the step; a value
  that looks like a credential is never extracted. The run records `ChainRef` and each call's `StepId`, `DependsOn`, `Outputs`.
- Contract Testing → Chains view (editor, run, delete).

## E–G — Storage, deletion, retention, usage, export (`ExecutionEvidenceManagement`)

- **Where evidence lives:** PostgreSQL only — `execution_runs` (summary) and `execution_calls` (one redacted document per logical call with
  attempts, request/response bodies bounded by the capture policy, assertions, outputs). No evidence files; exports are generated on demand.
- **Usage:** runs, logical calls, attempts, measured stored bytes (UTF-8 bytes of the stored JSON; `octet_length` on PostgreSQL), table size
  (`pg_total_relation_size`, or "Not available"), protected runs, oldest/newest, retention candidates.
- **Integrity:** stored bodies re-hashed against the SHA-256 recorded at capture; removed bodies counted.
- **Deletion:** one run, selected runs (≤ 500), every eligible run of an environment; `Confirm` must equal the number shown; protected and
  running runs are skipped with a reason; one transaction (`ExecuteDelete` inside a transaction on PostgreSQL); audit entry.
- **Body removal:** a call's stored bodies → `CaptureCompleteness.Removed`, size and SHA-256 kept; refused for protected runs.
- **Retention:** explicit setting (table `execution_retention_settings`), default **Disabled**; 1–3650 days (7/30/90 presets); dry-run preview
  (runs, calls, bytes, protected and running kept); applied only by a person with the previewed count; no background deletion.
- **Audit:** table `execution_evidence_audit` (action, scope, counts, bytes, initiator; never content).
- **Export:** 1–20 runs, ≤ 50 MB stored evidence: `manifest.json`, `runs/{id}/manifest.json` (summary, contract / operation / definition /
  Test Data / dataset-row / chain references, authentication mode, reference, sessions and outcomes, correlation scheme, body completeness),
  `runs/{id}/calls/NNNN.json`, `hashes.json`. The old single-run export uses the same builder.
- Write permission (`IntegrationConfigurationWrite`) on delete, bulk delete, protect, body removal, retention change and apply; reads and exports
  need none. Migration `AddExecutionEvidenceRetention` (runs `Protected`/`ProtectedAt`, audit and retention tables, index on `StartedAt`).

## H — Execution Results UI (same design)

Stored-evidence panel, retention (preset/custom, preview, typed-count apply), audit trail, history filters in the URL (`hq`, `hstate`, `hfrom`,
`hto`, `hprot`), multi-select export (download) and delete, delete all eligible of an environment, per-run protect / verify / delete, call
columns for authentication outcome and row/step, Evidence tab with session, dataset row, values used, outputs and body removal (second click
confirms). Contract Testing: run options and the attended-session line.

## I — Security and recovery

- Retries only for idempotent calls (GET, HEAD, OPTIONS, PUT, DELETE, GraphQL queries). POST, PATCH and GraphQL mutations are never resent
  automatically, whatever the policy says ("Not retried: POST can change data…").
- A data-changing request that was transmitted but got no response is recorded as **outcome unknown** (not a plain transport error).
- Kept and re-verified: redirects never followed, no credential headers in definitions, restart → Interrupted without resending, unsent calls
  have no response, GraphQL 200 + errors handled, wire transmissions counted.

## J — Defect found by acceptance

LC-DEF-1 (`77bdcd8a`): `LocalHttpsProxyServer.RelayAsync` now cancels the relay when the response pump ends (upstream closed an idle keep-alive
connection, `Connection: close`, or an error), so the browser's side is closed and the browser reconnects instead of hanging. Regression test
`WhenTheUpstreamClosesAnIdleKeepAliveConnectionTheBrowserSideIsClosedTooInsteadOfHanging` (failed before the fix).

## Tests added (backend / frontend)

`ExecutionAuthenticationLifecycleTests` (8), `ExecutionAuthenticationTests` (+1 single-flight), `ContractTestingEndpointSecurityTests`
(execute / write policies), `DatasetRowResolutionTests` (4), `ContractTestingTests` (+2 data-driven, +4 chains), `ExecutionOutputsTests` (4),
`ExecutionEvidenceManagementInMemoryTests` + `ExecutionEvidenceManagementPostgresTests` (6 scenarios each), `ExecutionEvidenceTests`
(+2: no resend of data-changing calls, redirect not followed); frontend `TestDataResourceEditorTests` (+1), `ContractTestingPageTests` (+3),
`ExecutionResultsPageTests` (+4).
