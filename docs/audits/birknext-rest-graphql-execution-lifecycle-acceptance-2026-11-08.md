# BirkNext — REST & GraphQL complete execution lifecycle: acceptance (2026-11-08)

Branch `008-traceability-first`. Baseline `d0a57a2e`; acceptance run against source `77bdcd8a` (after the fix of LC-DEF-1).
Statuses: PASS / FAIL / PARTIAL / BLOCKED / NOT RUN / NOT SUPPORTED.

**Result: main phase 26/26 PASS, restart phase 18/18 PASS (44/44), 172 logical calls in 8 runs, reconciled call by call with an
independent server-side ledger.** The user's requested demonstration — attended login → choose Test Data → run REST/GraphQL → inspect
request/response → restart → open history → export → delete one run → the other evidence is intact — was executed end to end in the browser
against isolated local services.

## Environment (isolated, synthetic)

| Item | Value |
|---|---|
| API | The real BirkNext API (`Program`, unchanged production code) hosted in-process by an acceptance harness (`scratchpad/lcval/harness`, not committed) on `localhost:5099`. The harness replaces exactly three seams: `IProxyCertificateStore` → the product's `EphemeralCertificateStore` (CA and its "trust" in process memory only; the Windows certificate store was never touched); `IUpstreamConnector` and the `execution-evidence` / `api-service-token` HttpClient handlers → the three synthetic hosts `*.lifecycle.test:443` mapped to `127.0.0.1:5443` with TLS pinned to a synthetic CA, **every other host refused**. A loopback-only forwarder fronts the in-memory server and records requests as loopback connections. Content root `lcval/api-root` (appsettings.json + Development only; no Local file). `ASPNETCORE_ENVIRONMENT=Development`, `AuthenticatedReview` LocalWorkstation, proxy on 8898, outbound proxy variables pointed at a dead port. |
| Database | `birknext_lc_20261108` (new, disposable; recreated before the final run) |
| Web | Published frontend `webpub-lc` served on `localhost:5199` (`serve-web-lc.cjs`). `webpub` and `api-gaps` were not written. |
| Synthetic world | `lcval/world.cjs` on `127.0.0.1:5443` (HTTPS): `idp.lifecycle.test` (authorization code + PKCE S256, state, nonce, exact redirect URI, refresh tokens, client credentials, logout; HS256 JWT with `exp`, `aud`, `jti`), `app.lifecycle.test` (SPA signing in and calling the API), `api.lifecycle.test` (REST + GraphQL validating the JWT, slow/flaky/drop/forbidden routes). Control/ledger on `127.0.0.1:5289`. |
| Oracle | The world's ledger: every request the API received (method, path, query, body, content type, `X-BirkNext-Execution-Id`, token valid/expired/none, `sub`, `jti`, a 12-hex token hash — never a token). |
| Browser | Playwright, Microsoft Edge, headless. BirkNext pages: every non-localhost request aborted and counted. The tester's target-application browser routed through the BirkNext Local HTTPS proxy with certificate errors ignored (the proxy CA was "trusted" in BirkNext's in-memory store only, never in Windows). |
| Credentials / data | Synthetic only: users `tester-a`, `tester-b`; service client `lifecycle-service` / `synthetic-service-secret-5150`; Test Data datasets `lc-children-rows` (50 rows), `lc-read-ids` (10 rows), `lc-one-id`, `lc-list`, `lc-gql-child`. |

Your stack on 5000/5173 and your databases were not touched. No external system was contacted (LC-Z2, LC-Z4: 0 external requests from BirkNext pages;
the harness refuses every non-synthetic host).

## Main phase — `lcval/accept.cjs main`: 26/26 PASS

| ID | Check | Result |
|---|---|---|
| LC-01 | OpenAPI uploaded in the UI, GraphQL SDL imported | PASS |
| LC-02 | Test Data: 50 typed rows, 10 id rows, record datasets created and approved (Test Data API) | PASS |
| LC-03 | Draft tests generated from both contracts (REST incl. negatives, GraphQL) | PASS |
| LC-04 | Test Data **picked in the UI** and pinned; readiness "every required value … in each of its 50 rows" | PASS |
| LC-05 | Both contracts bound to the attended session for the Target Environment | PASS |
| **Attended authentication** | | |
| LC-10 | No attended session → the run is refused (Blocked) before anything is sent | PASS |
| LC-11 | Attended setup in the UI: certificate (memory) and proxy started for the Target Environment | PASS |
| LC-12 | Cancelled sign-in at the IdP → no attended session ("none") | PASS |
| LC-13 | Sign-in with authorization code + PKCE through the proxy → session active, generation 1, no token on any page | PASS |
| LC-14 | IdP refuses an unregistered redirect URI (target-side control, observed) | PASS |
| **REST data-driven run (62 logical calls)** | | |
| LC-20 | 60 dataset rows + 2 negatives; the oracle received each row's own `name` (string) and `age` (JSON number) with the row identity in evidence (`lc-children-rows@v1#r-…`) | PASS |
| LC-21 | One attended session for every authenticated call (one `jti`, one `attended-session:…#g1`); the missing-authentication negative was sent without a credential by design (`AnonymousByDesign`) | PASS |
| LC-22 | Unknown ids fail on their assertion (404 × 3); missing `status` passes on 400; no credential passes on 401; 59 Passed | PASS |
| LC-23 | 62 calls → 62 requests at the oracle → 62 wire sends in evidence; one attempt each | PASS |
| LC-24 | No token, secret or JWT fragment in the stored evidence; Authorization recorded as redacted | PASS |
| **GraphQL and chain** | | |
| LC-25 | Confirmed mutation sent typed variables (`age: 6`); negative got HTTP 200 + GraphQL errors and passed | PASS |
| LC-26 | Chain create → read → GraphQL read: the id returned by step 1 was sent in step 2's path and step 3's variable, in dependency order; run names its chain revision | PASS |
| **Session lifecycle across many calls** | | |
| LC-27 | Token lifetime 25 s, 1.2 s per call: the session expired mid-run, the call showed "Waiting for the attended session…", the app renewed, the run continued with generation 2 of the **same identity**; 30/30 Passed, every request valid and from `tester-a`, two `jti` | PASS |
| LC-28 | Expiry, then **tester-b** signed in: remaining calls Blocked for authentication ("different account"); the oracle never received a request of the run as tester-b | PASS |
| LC-29 | "End attended session" in the UI → the next run is Blocked before sending; the oracle received nothing | PASS |
| LC-30 | Cancellation mid-run: some Passed, the rest Cancelled with no attempt; oracle requests = evidence wire sends | PASS |
| **Failures and unattended** | | |
| LC-31 | Timeout (15 s), dropped connection, HTTP 503, HTTP 403 → TimedOut (no invented response), Error, Failed, Authorization denied | PASS |
| LC-32 | Unattended client credentials: 15 calls, **1** token request; scope-limited POSTs → Authorization denied; the client secret nowhere in evidence | PASS |
| LC-33 | 172 logical calls in 8 runs; for every run the oracle's received requests equal the evidence's wire transmissions | PASS |
| LC-Z1/Z2 | No page/console errors; no external request | PASS |

The target browser recorded five `ERR_ABORTED` requests of the synthetic app (`/api/me` fetches abandoned when the script navigated the page during
a sign-in); the app never needed a retry (`appRetries = 0`).

## Restart phase — `accept.cjs interrupt` → API stopped by its PID mid-run → restarted → `accept.cjs after-restart`: 18/18 PASS

| ID | Check | Result |
|---|---|---|
| LC-40 | API stopped after 5 of 20 calls were received: after the restart the run is Interrupted, unfinished calls Interrupted, nothing sent again (oracle count unchanged) | PASS |
| LC-41 | The attended session did not survive the restart (memory only) | PASS |
| LC-42 | Run history after the restart lists every run (PostgreSQL) | PASS |
| LC-43 | Storage: measured stored bytes, database table size, location "PostgreSQL … execution_runs … execution_calls" | PASS |
| LC-44 | Call inspection: request body sent for row r-007 (`"Barn 7"`, `"age": 7`), the row, typed values with source, the attended session — no token on the page | PASS |
| LC-45 | "Verify stored evidence": every stored body matches its recorded SHA-256 | PASS |
| LC-46 | Export of two selected runs downloaded from the UI; an independent Python check: every file matches `hashes.json`, one manifest per run with references/authentication/correlation, no token or secret | PASS |
| LC-47 | A protected run cannot be deleted from its page | PASS |
| LC-48 | Deleting one run in the UI (typed "1") removed exactly its calls in the database; every other run's call count unchanged | PASS |
| LC-49 | Retention 30 days, dry run: exactly the one old unprotected run is a candidate; the old protected run is kept; nothing deleted | PASS |
| LC-50 | Applying retention (typed count) deleted that run only | PASS |
| LC-51 | Measured stored bytes dropped after deletion and retention | PASS |
| LC-52 | Audit trail lists Exported, Deleted, Protected, RetentionApplied | PASS |
| LC-53 | Removing one call's bodies keeps status, size and hash; integrity still verifies (removed counted) | PASS |
| LC-54 | No horizontal page scroll at 1440/1280/1024/768/390 (history, run, call evidence, chains) | PASS |
| LC-55 | Keyboard: only the selected call tab is in the tab order | PASS |
| LC-Z3/Z4 | No page/console errors; no external request | PASS |

Test setup note: to exercise retention without waiting 30 days, the `StartedAt` of two runs was moved 40 days back with SQL in the disposable
database (one protected, one not). Nothing else was changed outside the product.

Screenshots (synthetic data, `scratchpad/lcval/shots/`, not committed): `lc-01-attended-setup.png`, `lc-02-contract-testing.png`,
`lc-03-history-storage.png`, `lc-04-call-evidence.png`, `lc-05-chains-390.png`.

## Defect found by acceptance

**LC-DEF-1 — Local HTTPS proxy left the browser's side open after the target closed an idle keep-alive connection** (`77bdcd8a`). The first full
runs failed LC-27: after the app refreshed its token, its API call never arrived, so the renewed session was never observed. Reproduced in isolation
(API call after 2 s idle: 53 ms; after 12 s idle: no answer within 25 s); cause: when the upstream ended an intercepted keep-alive connection, the
relay kept reading the browser's side and forwarded its next request into the dead upstream. Fix: the browser's side is closed whenever the upstream
side ends, so the browser reconnects. A regression test reproduced the hang first (`WhenTheUpstreamClosesAnIdleKeepAliveConnection…`). This affects
real attended use with any server whose keep-alive timeout is shorter than the tester's pause.

Script corrections during the acceptance (not product defects): a GraphQL negative that needs no input was given a dataset (no picker exists for it);
the IdP redirect check used a cross-origin `fetch` (blocked by CORS) instead of a navigation; a missing-authentication negative bound to a 10-row
dataset would have sent 10 anonymous calls (rebound to one record).

## Authentication modes

| Mode | Backend tests | Browser (this acceptance) |
|---|---|---|
| None (anonymous) | PASS | PASS (negatives, LC-21/22) |
| Attended session (Local HTTPS proxy; target app uses authorization code + PKCE) | PASS | **PASS** (LC-11…LC-14, LC-20…LC-30) — previously NOT RUN |
| OAuth2 client credentials (unattended) | PASS | PASS (LC-32) |
| API key (unattended) | PASS | PASS in CDT phase 2 (not repeated) |
| Missing authentication by design | PASS | PASS (LC-21) |
| Basic | — | NOT SUPPORTED |

## Regression

See the gap register for the final backend/frontend numbers and the partition-6 timeout investigation (recorded with the final SHA).
