# BirkNext — attended-authentication acceptance harness: what belongs in the permanent suite (2026-11-09)

Branch `008-traceability-first` (local only). Subject: the scratch harness used for the execution-lifecycle browser acceptance
(`docs/audits/birknext-rest-graphql-execution-lifecycle-acceptance-2026-11-08.md`), kept in the session scratchpad as `lcval/`. It is **not**
deleted and not committed.

## What the harness is

| Part | What it does | Depends on |
|---|---|---|
| `harness/` (C#) | Hosts the real API (`Program`) with `WebApplicationFactory`, replacing three seams: proxy certificate store → in-memory store; upstream connector and execution HttpClient handlers → synthetic hosts `*.lifecycle.test` pinned to a synthetic CA; a loopback forwarder on `:5099` | A built scratch worktree, a dedicated PostgreSQL database, fixed ports |
| `world.cjs` (Node) | Synthetic IdP (authorization code + PKCE, refresh, client credentials, logout), app and API (REST + GraphQL validating JWTs), control port and the **independent request ledger** | Node, ports 5443/5289, synthetic certificate files |
| `accept.cjs` (Node + Playwright) | The 44 browser checks: attended sign-in through the proxy, Test Data, runs, restart, history, export, deletion, retention | Microsoft Edge, Playwright, published frontend on `:5199` |
| `certs/`, `api-root/`, `results-*.json`, `shots/`, `export-selected.zip`, logs | Generated certificates (with a private key), content root with runtime data, outputs | — |

## What the permanent suite already covered, and the gap

- Proxy → store: `LocalHttpsProxyServiceTests` (promotion only on a 2xx from an approved host, tenant/expiry checks, wipe on stop, LC-DEF-1
  keep-alive regression).
- Store → run: `ExecutionAuthenticationLifecycleTests` (one session for every call, expiry mid-run, renewal of the same identity, identity
  change refused, cancellation while waiting, ended session, per-call outcomes) — with a credential the test places in the store.
- **Gap:** nothing joined the two. The only proof that the credential a run sends is the one the real proxy observed — and that the target
  received exactly what the evidence records — was the scratch browser acceptance (LC-21, LC-23, LC-24, LC-29).

## Decision

**Incorporated (committed):** `LocalHttpsProxyServiceTests.ACredentialObservedByTheProxyAuthenticatesEveryCallOfARunAndTheTargetReceivedExactlyWhatTheEvidenceRecords`.
The real proxy observes a bearer token on a successful approved request; the production `ExecutionRunService` (with its production primary
handler, so wire transmissions are counted on real sockets, and the real capture pipeline) runs 12 calls against the fixture's TLS origin, which
is the oracle. Asserts: 12 requests received, each with exactly the observed credential; received = evidence wire transmissions; one session
(`attended-session:dev#g1`); every call `Authenticated`; no token or JWT fragment in the stored evidence; after the proxy session ends the next run
is Blocked and nothing more reaches the target. No browser, Node, fixed port, certificate file, database or external host; runs in the normal
backend suite (partition 2) in about a second.

**Not incorporated (stays in the scratchpad, reusable):**

| Part | Why not now | What it would take |
|---|---|---|
| Browser sign-in (`accept.cjs`) and the synthetic IdP/app (`world.cjs`) | The authorization-code/PKCE flow, redirect URI, state and nonce belong to the *target application* and its IdP; BirkNext only observes the resulting traffic, which the new test covers. The browser run needs Edge, Node, fixed ports and ~5 minutes, which does not fit the memory-guarded backend suite. | An opt-in acceptance next to the existing loopback fixtures in `BirkNext.Web.PlaywrightTests` (precedent: `security-runtime-fixture.cjs`): ports as arguments, certificates generated at run time in a temporary directory, no scratch paths, results only to a temporary directory. |
| The in-process API harness | Duplicates `WebApplicationFactory` usage the suite already has; useful only together with the browser run. | Same as above. |
| Expiry → renewal observed *through the proxy* mid-run (LC-27) | Covered in two halves (store-level renewal in `ExecutionAuthenticationLifecycleTests`, the proxy keep-alive defect in `WhenTheUpstreamClosesAnIdleKeepAliveConnection…`); a joined version needs controlled clocks in both the proxy and the run. | A second joined test with a shared fake clock. Open. |
| Certificates, results, screenshots, logs, `api-root` runtime data | Generated outputs and a synthetic private key — never committed. | — |

Remaining open (not marked done by this test): a real Edge launched by BirkNext with a Windows-trusted proxy CA; expected-audience validation;
the other items of the execution-lifecycle gap register.
