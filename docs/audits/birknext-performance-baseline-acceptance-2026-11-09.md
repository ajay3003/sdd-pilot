# BirkNext — Performance Test Review: baselines & regression — acceptance (2026-11-09)

Branch `008-traceability-first`. Source: `190ce1b0` + this change (committed with these reports).

**Result: 40/40 PASS** — REST API 21/21, GraphQL 4/4, after restart 4/4, browser 11/11 — with **real k6 runs** (no synthetic metrics).
One script error (a wrong GraphQL target-type name) failed the first GraphQL attempt inside the main phase; the GraphQL phase was rerun on its
own and passed. One product finding (GraphQL error rate of a clean baseline) was fixed and verified.

## Environment (isolated, synthetic)

| Item | Value |
|---|---|
| API | The real BirkNext API (clean build of the commit) on `localhost:5098`, content root without a Local settings file, outbound proxies to a dead port |
| Database | `birknext_pb_20261109` (new, disposable) |
| Load generator | k6 1.0.0 (`docker.io/grafana/k6:1.0.0`, already present; no pull) in Podman 5.4.2 |
| Target | Synthetic Node server in a local container (`node:20-bookworm`, already present) on `:18090`: `GET /children`, `POST /graphql`, latency and GraphQL errors controlled by the script |
| Intensity | 1 virtual user (2 for the not-comparable case), 2 s warm-up + 10 s steady; ≈ 560 requests per run |
| Web | Published frontend on `localhost:5198`; Edge (Playwright, headless); non-localhost requests aborted (0), the frontend's default API origin rerouted inside the browser to `:5098` |

Your stack on 5000/5173 and your databases were not touched; nothing outside this machine was contacted.

## REST — `pbval/accept.cjs main`

| ID | Check | Result |
|---|---|---|
| PB-01 | Definition saved; readiness Ready (runtime, image, target reachable from the k6 container) | PASS |
| PB-02 | 3 reference runs at 20 ms target latency completed (P95 22.19 / 22.31 / 22.08 ms; 559 / 557 / 560 requests) | PASS |
| PB-03 | Proposal recorded with its eligibility review | PASS |
| PB-04 | A proposed baseline is refused as a reference | PASS |
| PB-05 | Approved → Active, approver recorded | PASS |
| PB-06 | An approved baseline cannot be decided again | PASS |
| PB-07 | Same latency, two runs → Comparable, Stable (P95 Δ +0.002 ms) | PASS |
| PB-08 | Target slowed to 80 ms; one run → P95 **Potential** regression | PASS |
| PB-09 | Different application version → Comparable with warnings, named | PASS |
| PB-10 | Three slower runs → P95 **Confirmed** regression (22.19 → median 82.24 ms, +270.5 %, 3 of 3 beyond +20 %) | PASS |
| PB-11 | Thresholds (P95 < 1000 ms) Pass beside the Confirmed regression | PASS |
| PB-12 | P99 without a tolerance: shown, Not assessed | PASS |
| PB-13 | Workload changed → definition v2 with a new comparison fingerprint | PASS |
| PB-14 | 2 VUs vs 1 → Not comparable, exploratory, Inconclusive; "Virtual users" named as blocking | PASS |
| PB-15 | Rejection needs a reason; the rejected proposal is kept | PASS |
| PB-16 | The baseline's run cannot be deleted | PASS |
| PB-17 | An unreferenced run is deleted only with confirmation | PASS |
| PB-18 | Trend: 6 runs, baseline marked, the 2-user run off the line (1 excluded) | PASS |
| PB-19 | Export: baseline identity with approver, 3 runs, workload, units, derivation, Confirmed verdict | PASS |
| PB-20 | Export contains no credential, cookie or request content | PASS |
| PB-21 | Ledger: the target received 2274 requests; the runs report 2267 (the 7 extra are readiness probes; never fewer) | PASS |

## GraphQL — `accept.cjs graphql`

| ID | Check | Result |
|---|---|---|
| PB-30 | GraphQL definition saved (`query Children { children { id name } }`) | PASS |
| PB-31 | Readiness Ready | PASS |
| PB-32 | Run completed; one-step promotion records the approver | PASS |
| PB-33 | Target returning HTTP 200 + `errors`: HTTP error rate 0 %, GraphQL errors 559 of 559 → GraphQL error rate 0 % → 100 % visible | PASS |

Finding fixed by PB-33: the clean baseline stored no GraphQL-error count (k6 omits counters without samples), so the GraphQL error rate had no
baseline. Rule fix + test; after the fix the comparison shows baseline 0 %, current 100 %, direction Worse. It stays **Not assessed**: tolerances
can only be configured for the core metrics (gap register).

## After restart — API stopped by its PID, started again — `accept.cjs after-restart`

| ID | Check | Result |
|---|---|---|
| PB-40 | Approved and rejected baselines persisted with their decisions | PASS |
| PB-41 | The same regression (Confirmed) reproduced from persisted runs | PASS |
| PB-42 | Trend unchanged | PASS |
| PB-43 | The deleted run stays deleted; the baseline's run is still there | PASS |

## Browser — `pbval/browser.cjs` (Edge)

| ID | Check | Result |
|---|---|---|
| PB-B1 | History shows "Approved by …" and "Rejected by …" | PASS |
| PB-B2/B3 | Delete asks first; the baseline's run is protected (message shown) | PASS |
| PB-B4 | Three slower runs selected in the UI → Confirmed, "+20 %", "3 of 3" | PASS |
| PB-B5 | Thresholds listed beside the verdict | PASS |
| PB-B6 | Export downloaded from the UI with the Confirmed verdict | PASS |
| PB-B7 | The 2-user run shown as an exploratory, not comparable comparison | PASS |
| PB-B8 | Trend hides the not-comparable run until asked; baseline run marked | PASS |
| PB-B9 | No horizontal page scroll at 1440 / 1280 / 1024 / 768 / 390 | PASS |
| PB-B10 | Keyboard: baseline selector → run checkboxes | PASS |
| PB-Z1 | No page errors, no external request (the one console line is the expected 422 of PB-B3) | PASS |

Screenshots (synthetic, scratch, not committed): `pbval/shots/pb-01-history.png`, `pb-02-regression.png`, `pb-03-compare-390.png`.

## Regression

See the gap register (final suite numbers).
