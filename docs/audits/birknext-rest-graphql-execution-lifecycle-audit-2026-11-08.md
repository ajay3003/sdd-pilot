# BirkNext — REST & GraphQL complete execution lifecycle: baseline and architecture audit (2026-11-08)

Stage A of the execution-lifecycle brief. Read-only audit of the real code before any change. Statuses: IMPLEMENTED / PARTIAL / MISSING /
UNVERIFIED / NOT SUPPORTED. Implementation, acceptance and gap register follow in the companion reports
(`docs/hardening/birknext-rest-graphql-execution-lifecycle-implementation-2026-11-08.md`, `docs/audits/…-acceptance-…`, `docs/audits/…-gap-register-…`).

## 1. Baseline

| Item | Value |
|---|---|
| Branch | `008-traceability-first` (local only, nothing pushed) |
| Baseline HEAD | `d0a57a2e` (Contract-Driven Testing phase 2 stage 10) |
| Working tree | clean apart from user files that are never staged: `appsettings.Local.json` (modified by the user), `AIAssisted/testdata/`, three `docs/audits/birknext-final-*` / release-assurance files of another session |
| Ports | the user's stack on 5000 (`BirkNext.Api.exe` from the repo `bin`) and 5173 (Blazor dev server) — not touched. 5099/5199/5288 free |
| Databases | 27 `birknext*` databases. Phase-2 `birknext_cdt2_20261108` dropped with the user's approval (0 connections, created by the acceptance reset script). Everything else untouched |
| Scratch | phase-2 folders `pub-api-cdt2`, `api-cdt2`, `webpub-cdt2`, `cdtval2` deleted with approval; scripts, results and screenshots kept in `scratchpad/kept-cdt2-acceptance/`. `api-gaps`, `webpub` untouched |
| Configuration | `appsettings.Development.json`: `Authorization:LocalConfigurationWrites=true`, `Authorization:LocalTrustedExecution=true` (Development + loopback + configured frontend origin only) |

## 2. Where a REST/GraphQL execution lives today (traced through code)

| Step | Owner (code) | Persistence | Verified before this task? |
|---|---|---|---|
| Contract | `ContractRegistryService` (api_contracts, api_contract_versions; immutable sha256 versions) | PostgreSQL (AppDbContext) | yes (CDT phase 1/2 browser) |
| Test definition | `ContractTestService` (contract_tests + revisions, `DefinitionRef = contract-test:{id}@r{rev}`) | PostgreSQL | yes |
| Dataset binding | `ContractTestService.BindAsync` → one Test Data dataset / key-value collection pinned `alias@vN` | test document | yes (single record) |
| Variable resolution | `ContractTestRules.Materialize`: `{{dataset.name}}` placeholders; JSON whole-placeholders keep the Test Data type (objects at any depth) | — | yes for objects; **arrays are not filled** |
| Target Environment | frontend-owned; the run request carries `TargetUrl`, `EnvironmentType`, `IsProduction`, `ProfileId`, `ContextFingerprint` | browser | yes |
| Safety | `IApiEnvironmentSafetyPolicy` (Local/Development/QA/Test only, production markers refused) + `ExecutionRunService.CallBlock` (relative paths, no credential headers, mutations need confirmation, no subscriptions) | — | yes |
| Authentication | `ExecutionCredentialResolver`: AttendedSession (Local HTTPS proxy credential, memory only), ClientCredentials / ApiKey (DataProtection-encrypted secret in `App_Data/api-service-credentials`) | memory / App_Data | unattended: browser PASS; attended: backend tests only |
| Readiness | `ContractTestService.Readiness` | — | yes |
| Serialized request | `ExecutionRunService.Build` | — | yes |
| Transport | named client `execution-evidence` (no redirects, no cookies) + `EvidenceCaptureHandler` + `TransmissionCountingStream` (wire sends) | — | yes (EE-DEF-1) |
| Response + contract validation | `ContractCallValidator` (OpenAPI schema / GraphQL shape) | call document | yes |
| Assertions | `ExecutionAssertions` (status, header, JSON path, GraphQL data/errors, XPath, body, contract schema) | call document | yes |
| Execution Evidence | `ExecutionEvidenceStore`: tables `execution_runs` (summary JSON) and `execution_calls` (one redacted JSON document per logical call, attempts inside) | PostgreSQL only — **no evidence files on disk** | yes |
| Results Explorer | `Pages/ExecutionResults.razor` (runs table, run KPIs, calls table, call detail with request/response/assertions/attempts/compare) | — | yes |
| Export | `GET runs/{id}/export` (ZIP built from stored documents; manifest + calls + hashes) — single run | generated on demand, never stored | yes |
| Deletion | `DELETE runs/{id}` (refuses Running) | — | **no authorization, no confirmation, no protection** |
| Retention | — | — | **MISSING** |
| Restart | `ExecutionEvidenceRecoveryService`: Running → Interrupted, nothing resent | — | yes |

Correlation: `X-BirkNext-Execution-Id: {run:N}.{sequence}.{attempt}` on every attempt. Version references on every call: `OperationRef key@vN#op`,
`DefinitionVersion contract-test:{id}@r{rev}`, `TestDataRefs alias@vN`, run `ContractRefs key@vN`, `AuthenticationReference` (configuration identity, never a value).

## 3. Capability audit

| # | Capability | Current state | Owner | Gap | Risk | Required fix | Status |
|---|---|---|---|---|---|---|---|
| 1 | Authorization to **execute** (C) | `POST api/execution-evidence/runs` and `POST api/contract-tests/run` have no policy; only the environment safety policy | ExecutionEvidenceController / ContractTestingController | Any caller reaching the API can start runs against any non-production-classified URL it names | High (local tool, but it is the one outbound execution path) | Require the existing `ActiveEventExecute` policy (permission claim, or the Development + loopback + frontend-origin local grant). No new mechanism | MISSING |
| 2 | Authentication to BirkNext (A) vs target (B) | A: Entra JWT bearer / local grants. B: run credential. Never mixed (the BirkNext token is not forwarded) | Program.cs / ExecutionCredentialResolver | — | — | keep | IMPLEMENTED |
| 3 | Attended session start / observe | Local HTTPS proxy promotes an observed bearer token (2xx, approved host, unexpired, expected tenant), memory only, bounded by token `exp` and `CredentialLifetimeMinutes` | LocalHttpsProxyService / TransientAuthenticatedApiContextStore | No session **generation** identity (renewal cannot be shown in evidence); no expected-**audience** check | Medium | Generation counter + non-secret session reference; optional expected audience | PARTIAL |
| 4 | Attended session expiry mid-run | Each later attempt is refused "expired" and the call becomes Blocked; the run continues and every remaining call fails the same way | ExecutionRunService | No pause for re-authentication; no "Blocked for authentication" outcome; identity change across renewal undetected | Medium | Bounded wait for a renewed session of the **same identity**; otherwise remaining calls Blocked for authentication; never anonymous | PARTIAL |
| 5 | Attended logout / cancellation | Proxy stop wipes the credential; no explicit "end attended session" without stopping the proxy | LocalHttpsProxyService | Logout = proxy stop only | Low | End-session endpoint (Invalidate) | PARTIAL |
| 6 | Unattended token reuse / renewal | Memory cache per credential reference, renewed 30 s before expiry | ExecutionCredentialResolver | Concurrent callers can fetch twice (refresh race); acquisitions not counted in evidence | Medium | Single-flight per reference; count acquisitions per run | PARTIAL |
| 7 | Per-call authentication outcome | Only the run label + reference; a 401/403 is just a status | — | No Authenticated / Anonymous by design / Blocked for authentication / Failed authentication / Authorization denied per call | Medium | `AuthenticationOutcome` on every call | MISSING |
| 8 | Dataset-driven execution (N rows) | A dataset is one record | Test Data / ContractTestService | One test × N rows → N logical executions is impossible | High (brief core) | Dataset **rows** in the existing Test Data `Dataset` kind (versioned with it); expansion with row identity; policies all/selected/stop-on-first-failure/max | MISSING |
| 9 | Typed variable resolution | Whole placeholders keep type in JSON objects; arrays skipped; no precedence model; no scenario/step/env sources | ContractTestRules | Arrays, list values, row > dataset precedence, step outputs, missing-value errors per source | Medium | Extend `Materialize` (one engine): arrays, explicit precedence row → dataset member → step output reference; never empty-string substitution | PARTIAL |
| 10 | Chained calls | none | — | No steps, extraction, dependencies, upstream-failure blocking | High (brief core) | Chain = ordered steps over existing contract tests; extraction by the existing JSON path engine (`ExecutionAssertions.Select`) / GraphQL data path / header; resolved inside the run at send time; dependents Blocked when upstream fails; cycles refused | MISSING |
| 11 | 100+ execution control | Max 500 calls, sequential, per-call timeout, bounded retry (≤ 5), cancel | ExecutionRunService | No stop-on-first-failure, no progress endpoint beyond reading the run, no bounded concurrency | Low | Run policies; progress = stored counts | PARTIAL |
| 12 | Request/response capture | Transport-boundary capture, redaction `ee-redaction-v1`, wire-send counting, unsent → no response, GraphQL 200+errors | EvidenceCaptureHandler | Auth outcome and dataset row not recorded per call | Low | add fields | IMPLEMENTED (fields PARTIAL) |
| 13 | Local storage | PostgreSQL only: `execution_runs.SummaryJson`, `execution_calls.DocumentJson` (bodies inside, bounded by capture policy 64 KB per body). No files, no exports stored | ExecutionEvidenceStore | Not documented for testers; no size reporting | Low | Storage usage endpoint (measured `octet_length`) | PARTIAL |
| 14 | Evidence deletion | Single run delete, no auth, no confirmation, no protection, no bulk, no body removal, no audit trail | ExecutionEvidenceStore | — | High | Authorized (write policy), confirmed, protection-aware single/bulk/scope delete; body removal keeping metadata + hashes; audit trail; one DB transaction (no files are owned, so deletion is atomic) | PARTIAL |
| 15 | Retention | none | — | — | Medium | Explicit setting (default **Disabled**), 7/30/90/custom days, dry-run preview (count + reclaimable bytes), manual apply only, protected runs never removed, audit | MISSING |
| 16 | Storage usage | none | — | — | Low | Runs, logical calls, attempts, measured DB bytes, protected, oldest/newest, retention candidates | MISSING |
| 17 | Export | single run ZIP, hashes, redaction notice | ExecutionEvidenceStore | No multi-run export; manifest lacks Test Data/contract/auth metadata summary | Low | Selected-runs export with count/size limits; richer manifest | PARTIAL |
| 18 | Results Explorer | runs table, run detail, calls, call detail | ExecutionResults.razor | No history filters, multi-select, delete, protect, retention, storage, auth outcome, dataset row, chain view | Medium | Extend the existing page (same design) | PARTIAL |
| 19 | Data isolation | Runs are per environment id; local single-user tool; no project column | ExecutionEvidenceStore | Cross-project scoping limited to environment | Low | Filter by environment; document | PARTIAL |
| 20 | Revision drift | Calls keep exact contract version, test revision, dataset version; history never re-resolved | — | — | — | keep; add dataset row ids | IMPLEMENTED |
| 21 | Failure / recovery | Restart → Interrupted; cancel; timeouts; transport failures; evidence write failure → run recovery | ExecutionRunService | Auth expiry mid-run (see 4); chain upstream failure (see 10) | Medium | as above | PARTIAL |

## 4. Attended authentication: what exists and what this task can verify

- **Mechanism (kept, no second one):** the tester signs in to the target application in a dedicated Edge profile routed through the BirkNext Local HTTPS
  proxy; the proxy observes the delegated bearer token on an approved host and holds it in memory (`TransientAuthenticatedApiContextStore`) for the
  Target Environment profile + fingerprint. BirkNext never shows a token form, never stores the token, never reuses its own sign-in token.
  Authorization code + PKCE, redirect URI validation, state and nonce belong to the **target application and its IdP** in this model; BirkNext
  observes the result. A BirkNext-owned PKCE client would be a second mechanism and is not added.
- **Constraint for a local E2E:** the proxy intercepts only HTTPS DNS hosts (`ApprovedHostSet` refuses loopback, `.localhost`, `.local`, IP literals),
  and both the proxy's upstream connection and the backend's execution client validate TLS through the OS trust store. A synthetic HTTPS target on
  loopback therefore needs (a) a host name mapping and (b) trust in a synthetic certificate — without touching the Windows certificate store or the
  hosts file. Plan: an **acceptance harness** that hosts the real `Program` (WebApplicationFactory + Kestrel) and replaces only three seams —
  `IProxyCertificateStore` (→ the existing in-memory `EphemeralCertificateStore`), `IUpstreamConnector` (synthetic host → loopback, trust pinned to
  the synthetic CA) and the `execution-evidence` primary handler's TLS trust/connect (same mapping and pin). Product code and policies stay unchanged;
  the harness is acceptance tooling and is reported as such.

## 5. Reconciliation with Contract-Driven Testing phase 2 gaps

| Phase-2 item | Handling in this task |
|---|---|
| Attended proxy sign-in not browser-verified | In scope (stage B + J) through the harness above. Stays NOT RUN unless executed. |
| AQR stored-contract selection not browser-verified | Attempt only if a discovered AQR target can be produced safely in the isolated stack; otherwise stays PARTIAL. |
| YAML / Swagger 2 structural parsing partial | **Stays open and visible.** Not part of this task; not marked resolved. |
| Basic authentication | NOT SUPPORTED (no policy). |
| Partition-6 one-off timeout (`Negative_proposals_cover_many_variants…`) | Recorded failure: the GraphQL call's transport hit its 15 s timeout at the partition's memory peak; passed on rerun. Investigated in stage K (isolated loop, repeated partition runs, thread-pool starvation probe). |

## 6. Plan (stages)

A audit (this report) · B authentication lifecycle (execute authorization, per-call auth outcome, session generation, wait-for-renewal, identity pin,
single-flight token, end session) · C Test Data rows + typed resolution + dataset-driven runs · D chains · E storage integrity · F delete / protect /
retention · G storage usage + export · H Results Explorer · I security / recovery · J isolated acceptance ≥ 100 calls incl. attended browser E2E ·
K full regression, timeout investigation, reports. Each verified stage is committed locally.
