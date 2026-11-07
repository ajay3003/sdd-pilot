# BirkNext — API & GraphQL Execution Evidence: acceptance (2026-11-07)

Implementation: see `docs/hardening/birknext-api-graphql-execution-evidence-implementation-2026-11-07.md`.
Capability audit: `docs/audits/birknext-api-graphql-execution-evidence-audit-2026-11-07.md`.

## 1. Environment (isolated, local, synthetic)

| Item | Value |
|---|---|
| API | Release publish of the committed code, `http://localhost:5099`, its own content root, its own PostgreSQL database `birknext_ee_20261107` (created for this task), outbound HTTP(S) proxy set to a dead local port |
| Web | Release publish, `http://localhost:5199`; browser routes the default backend port to 5099 |
| Target | Synthetic Node mock (`localhost:5288`): REST `/children/*`, `/fail`, `/flaky/{n}` (503 twice), `/slow` (3 s), `/drop` (reads the request, destroys the socket), `/graphql` (query / mutation / FORBIDDEN error / partial data). It records every request it receives (method, path, correlation header, body): the **independent oracle** for what BirkNext transmitted. |
| Data | Synthetic only. Synthetic secrets planted in requests and responses (a password value and two token values) to test redaction. No real personal data, no credentials. |
| Not touched | The user's stack on ports 5000/5173, `appsettings.Local.json`, `testdata/`, existing databases. No external network calls (browser: 0 external requests). |

## 2. API acceptance: 100-call run (`eeval/acceptance.cjs`)

100 logical calls: 30 REST GET, 10 expected 404, 10 POST create, 5 PUT, 5 PATCH, 5 DELETE, 5 expected 400, 5 assertion
failures, 3 dropped connections, 2 timeouts, 5 retry sequences (503, 503, 200), 8 GraphQL queries, 3 GraphQL mutations, 2 expected GraphQL
FORBIDDEN (HTTP 200), 2 GraphQL partial data with errors. Mutating calls explicitly confirmed. Duration 3.7 s.

| ID | Check | Verdict | Evidence |
|---|---|---|---|
| EE-00 | 100 logical calls planned | PASS | 100 |
| EE-01 | Run accepted (user-initiated, Local target) | PASS | 100 sent, 0 blocked |
| EE-02 | Each call has an authoritative final state | PASS | Passed 88, Failed 7, Error 3, TimedOut 2 |
| EE-03 | Physical attempts counted separately | PASS | 110 attempts (5 × 3 retries) |
| EE-04 | Every request the target received is accounted for (see §2.1) | PASS | target 119 = recorded transmissions 119; 110 attempts; 3 attempts resent |
| EE-05 | No response for attempts that received none | PASS | 3 ConnectionFailed, 2 TimedOut, all without response |
| EE-06 | Every configured assertion has a persisted outcome | PASS | 208 / 208 |
| EE-07 | Expected 404 passes; GraphQL HTTP 200 with errors fails unless expected; expected FORBIDDEN passes | PASS | |
| EE-08 | Retry sequence: 3 attempts, reasons recorded, final Passed | PASS | |
| EE-09 | Stored request body equals the target's observation after redaction | PASS | password / token masked, other fields equal |
| EE-10 | Synthetic secrets absent from stored evidence | PASS | |
| EE-11 | Run statistics reconcile exactly with the calls | PASS | |
| EE-12 | Blocked calls never reach the target | PASS | target delta 0 |
| EE-13 | Production classification refused by the safety policy | PASS | |
| EE-14 | Attempt comparison reports the status change | PASS | HTTP 503 → 200 |
| EE-20 | After a BirkNext restart the saved run opens unchanged, no execution | PASS | same counts |
| EE-21 | Opening the run sends nothing to the target | PASS | |

Export ZIP: 115,660 bytes, manifest + 100 call documents + hashes.

### 2.1 EE-04: criterion changed, stated openly

The first run (build `563d91df`) **failed** EE-04 as originally written ("server observations = stored attempts"): the target received
119 requests against 110 stored attempts. Each of the 3 `/drop` GET calls arrived 4 times. Root cause (EE-DEF-1): the .NET HTTP/1.1
stack resends a request without a body when the connection closes before a response, and this cannot be switched off (see the
implementation report). "One attempt = exactly one wire transmission" is therefore not achievable for body-less requests. The criterion
is now: every observation matches a stored attempt with request evidence, **and** each attempt's recorded `Transmissions` equals the
number of times the target saw its correlation id, **and** the run's `WireTransmissions` equals the target's total, **and** each
resent attempt says so. Rerun on `65bea9c1`: PASS. Resent attempts: calls 76, 77, 78, attempt 1, GET, 4 transmissions each.

## 3. Browser acceptance: Execution Results (`eeval/xr-accept.cjs`, Edge, Playwright)

Opens the saved 100-call run; never starts one.

| ID | Check | Verdict |
|---|---|---|
| XR-01 | Run history lists the run with outcome, calls, attempts | PASS |
| XR-02 | KPIs 100 / 110 with a separate "119 sent on the wire" note / 88 / 7 / 5 | PASS |
| XR-03 | Paging: 25 per page; Next shows 26–50; page in the URL | PASS |
| XR-04 | Last page holds call 100; Next disabled | PASS |
| XR-05 | Call 1: request line actually sent | PASS |
| XR-06 | Call 1: HTTP 200, password redacted | PASS |
| XR-07 | Call 50 (POST): request body with secrets masked | PASS |
| XR-08 | Call 100: GraphQL HTTP 200 with errors is Failed; errors shown | PASS |
| XR-09 | Expected GraphQL FORBIDDEN passes | PASS |
| XR-10 | Expected 404 passes; response shows 404 | PASS |
| XR-11 | Retry: 3 attempts, 503 → 503 → 200, reasons | PASS |
| XR-12 | Compare attempt 1 with 3: status change | PASS |
| XR-13 | Dropped connection: ConnectionFailed and "transmitted this request 4 times" | PASS |
| XR-14 | Dropped connection: no invented response | PASS |
| XR-15 | Filter Failed: exactly 7 | PASS |
| XR-16 | Filter GraphQL: 15 | PASS |
| XR-17 | Filter DELETE: 5 | PASS |
| XR-18 | Reload restores call and tab from the URL | PASS |
| XR-19 | Keyboard: only the selected tab is a Tab stop | PASS (failed first: EE-DEF-2) |
| XR-20 | Keyboard: Enter opens a focused tab | PASS |
| XR-21 | Export downloads a ZIP with manifest, calls, hashes, no secrets | PASS |
| XR-22 | 390 px: no horizontal page scroll (run and call views) | PASS |
| XR-23 | Browsing sent nothing to the target | PASS (119 → 119) |
| XR-24 | No page or console errors | PASS |
| XR-25 | No external requests | PASS |

Result: **25 / 25** on web build `a7202385`. Earlier rounds found EE-DEF-2 (XR-19) and EE-DEF-3 (KPI text "110119"); both fixed with
regression tests. Screenshots (synthetic data only), kept outside the repository in the session scratchpad `eeval/shots/`:
`xr-01-history`, `xr-02-run`, `xr-07-call50-request`, `xr-08-graphql-errors`, `xr-12-retry-compare`, `xr-22-run-390`, `xr-22-call-390`.

## 4. Regression on the final code

Memory-guarded, sequential (1.5 GB floor).

| Suite | Passed | Failed | Skipped | Total |
|---|---|---|---|---|
| Backend partition 1 | 984 | 0 | 2 | 986 |
| Backend partition 2 | 1,096 | 0 | 5 | 1,101 |
| Backend partition 3 | 806 | 0 | 0 | 806 |
| Backend partition 4 | 649 | 0 | 1 | 650 |
| Backend partition 5 | 520 | 0 | 3 | 523 |
| Backend partition 6 | 2,007 | 0 | 8 | 2,015 |
| **Backend total** | **6,062** | **0** | **19** | **6,081** |
| **Frontend (full)** | **8,470** | **0** | **0** | **8,470** |

Notes, stated as they happened:

- The first full backend run found **1 failure** in partition 1: `SourceIngestionOwnershipTests.Archives_are_opened_only_by_the_canonical_reader_and_justified_specialized_readers`.
  The evidence export creates a ZIP (`ZipArchiveMode.Create`) and the guard allow-lists every `new ZipArchive(`. Fixed by adding the
  store to the allow-list with its justification (it never reads an archive), as done for the other ZIP writers. Partition 1 was rerun
  after the fix: the numbers above.
- That first run was stopped by the host for low memory during partition 6 (partitions 1–5 had completed). Partition 6 was rerun alone
  with the memory guard; the numbers above.
- Partitions 2–5 ran on the build before the allow-list line was added; that change touches only the test project.

## 5. Not verified / limits

- Authenticated execution (`UseTargetAuthentication`) is Blocked by design in this task; not exercised beyond the blocked case.
- TLS targets were not exercised (the counting stream sits after TLS, verified only for plain HTTP on loopback).
- HTTP/2 was not exercised; the execution client uses the default HTTP/1.1.
- No AQR, fuzzing or Test Data page writes runs yet (GAP-EE-1); runs are started through `POST api/execution-evidence/runs`.
- On Windows, one dropped attempt against an IPv4-only `localhost` server took about 8.5 s (4 transmissions) in a probe, with or without
  the counting stream. Likely cause, not verified: each reconnect tries `::1` first. Observed only, not changed.
