# BirkNext — Azure Blob Storage Explorer — acceptance (2026-11-09)

Branch `008-traceability-first`. Source: `5e30f803` + this change (committed with these reports).

**Result: 44/44 PASS** — API 31/31, after restart 2/2, browser 11/11. **Synthetic and local only.** The store was a SYNTHETIC LOCAL folder
(labelled "not Azure" in API and UI); the Azure-kind store had no identity and stayed Disabled — **no Azure account was contacted, so
real Azure behaviour is not verified by these checks.**

## Setup (scratch only, not committed)

- Real BirkNext API built from a clean tree (`5e30f803` + this change), port 5098, dedicated database `birknext_bs_20261109`, content
  root without `appsettings.Local.json`, outbound proxies pointed at a dead port.
- Stores for environment `bs-local`: `synthetic` (folder, allowed container `skolenaervaer-out`) and `azure` (Azure kind, no identity).
- A synthetic application (:18092) plays an integration that writes files into the store when called, and keeps an **independent ledger**
  of the `X-BirkNext-Execution-Id` it received. A BirkNext execution run called it three times. Decoys were written before the run: an old
  report carrying another run's id and a container outside the allow-list.
- Published frontend served on 5198; Edge headless with every non-localhost request blocked.

## Results

| Id | Result | Check |
|---|---|---|
| BS-01–03 | PASS | run started; 3 calls passed; ledger has 3 writes, each with an execution id of this run |
| BS-10–15 | PASS | readiness and "not Azure" label; environment scoping; allow-list (hidden + refused); virtual folders; pagination with continuation, no sidecars |
| BS-20–26 | PASS | properties with masked secret metadata; JSON/XML/CSV/NDJSON/Parquet previews (identity number masked, formula neutralised, malformed NDJSON line reported, Parquet rows = independent oracle); traversal refused |
| BS-30–31 | PASS | correlation **Confirmed** by execution id in metadata (ledger call 1) and in the blob name (ledger call 3) |
| BS-32–34 | PASS | a blob written during the run without an id is **Not correlated** (time is not proof); an old blob with another run's id is Not correlated and outside the window |
| BS-35 | PASS | a folder expectation finds the newest blob below it — **failed first** (defect fixed, see implementation report) |
| BS-36–40 | PASS | Parquet expectation via the shared engine; missing blob stops at Exists; missing field named; other environment refused; Azure store refused with no network call |
| BS-50–53 | PASS | observation saved with checks + SHA-256; no content, identity number or secret stored; per-environment list; store bytes unchanged |
| BS-60–61 | PASS | after API restart the observation is still there; deleting it leaves the blob unchanged |
| BS-B1–B10 | PASS | browser: stores and labels, allow-list, folders, NDJSON and Parquet previews, folder check correlated with the run + link to Execution Results, save, 390 px without page scroll, keyboard order, store unchanged |
| BS-Z1 | PASS | no external request, no page error, no console error |

## Harness corrections made during this run (scratch, not product)

The harness was written in the previous session just before it ended. Its first runs failed on harness defects: missing oracle file,
the oracle key (`children_v1`, `row1`), and a Playwright wait on a never-visible `<option>`. These were fixed in the harness; product code
changed only for BS-35.
