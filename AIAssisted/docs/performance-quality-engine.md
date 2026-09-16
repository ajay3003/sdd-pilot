# BirkNext Performance Quality — native performance engine

BirkNext Performance Quality is a Frontend Quality Review engine that answers *"what is slow on this page, why, and what should the
team fix?"* for authenticated enterprise Blazor WebAssembly applications. It is independent of Lighthouse, Playwright and CDP.

## Architecture (unchanged principles)

| Component | Role |
|---|---|
| Normal managed Edge | The user signs in normally; no token injection, no MSAL cache manipulation, no second browser. |
| BirkNext Browser Companion (MV3 extension) | Page/runtime/resource/Blazor evidence per visit from PerformanceObserver, Resource Timing, Event Timing, Long Tasks, MutationObserver. |
| Local HTTPS proxy | REST / GraphQL / WebSocket evidence on approved hosts: per-request duration, status, allow-listed cache directives, header presence flags, declared size. No HTML injection. |
| Endpoint Discovery (page-oriented store) | Both sources converge on one `PageAnalysis` per page identity (origin + normalized path) and per analysis generation. |
| `PerformanceQualityRules` (frontend, pure) | Deterministic evaluation into metrics, findings, coverage, API summary, timeline and generation comparison. |

Engine id `FrontendQualityEngineId.PerformanceQuality`; toggle `Features.EnablePerformanceQualityEngine` (default **off**, opt-in per
Target Environment); policy Optional. Access decision is always Ready (it never contacts the target); coverage is reported per layer.

## Phases

* **Initial load** — full document navigation: Navigation Timing (TTFB, DOMContentLoaded, load), FCP, LCP, CLS, framework bootstrap.
* **SPA navigation** — client-side route change: LCP/CLS/Navigation Timing are *not measured* (Web Vitals definition); BirkNext Page
  Stabilization Time, incremental resources, API traffic are.
* **Runtime** — long tasks after stabilization, interactions, DOM churn, memory snapshot.
* **Background** — traffic that could not be correlated to a page (Endpoint Discovery "Shared").

The two navigation phases are never mixed in one snapshot. Refresh analysis on an already-loaded SPA page cannot recreate a cold
start; the UI says so and asks for a reload of the target page when an initial-load measurement is needed.

## Metric status

`Good` / `Needs improvement` / `Poor` / `Informational` / `Not measured`. Every threshold carries a source:
`Default` (documented default), `Target Environment` (saved value differs from the default) or `Policy` (Strict preset).

### Defaults (Target Environment → Performance thresholds / Core Web Vitals)

| Threshold | Default | Strict | Setting |
|---|---|---|---|
| LCP good / poor | 2 500 / 4 000 ms | — | `CoreWebVitals.LcpGoodMs/LcpPoorMs` |
| CLS good / poor | 0.1 / 0.25 | — | `CoreWebVitals.ClsGood/ClsPoor` |
| INP good / poor | 200 / 500 ms | — | `CoreWebVitals.InpGoodMs/InpPoorMs` |
| TTFB good / poor | 800 / 1 800 ms | — | constant (web.dev) |
| FCP good / poor | 1 800 / 3 000 ms | — | constant (Web Vitals) |
| Page stabilization good / poor | 2 000 / 5 000 ms | 1 500 / 4 000 | `PageStabilizationGoodMs/PoorMs` |
| API response warning / poor | 500 / 1 000 ms | 300 / 800 | `ApiResponseWarningMs/PoorMs` |
| Identical API calls | 2 | 1 | `MaxIdenticalApiCalls` |
| Long tasks | 3 (poor > 6) | 2 | `MaxLongTasks` |
| Main-thread blocking | 300 ms (poor > 600) | 200 | `MainThreadBlockingWarningMs` |
| Initial requests / transfer | 30 / 8 MB | 20 / 5 MB | `MaxStartupRequests`, `MaxStartupSizeBytes` |
| JavaScript transfer | 2 MB | 1.5 MB | `MaxJsTransferBytes` |
| Single resource | 2 MB | 1 MB | `MaxIndividualAssetSizeBytes` |
| Slow resource | 2 000 ms | 1 500 | `SlowResourceMs` |
| WASM / framework payload | 3 MB / 5 MB | 2 / 3 MB | `MaxWasmRuntimeSizeBytes`, `MaxFrameworkSizeBytes` |
| Assemblies | 100 | — | constant |
| Network burst | ≥ 15 requests in 1 500 ms | — | constant |
| Sequential pattern | next request starts ≤ 150 ms after previous completes, chain ≥ 3 | — | constant |
| Percentiles | published only with ≥ 5 samples (p50/p95 nearest rank); otherwise "observed" (max) | — | constant |

## BirkNext-specific metrics (named explicitly, never presented as Web Vitals)

* **BirkNext Page Stabilization Time** — route change → DOM quiet (no mutation batch for 800 ms) and network quiet (no *new* relevant
  request; a URL repeated more than twice within the visit is treated as polling-like and no longer resets the window), bounded by a
  4 s maximum wait. `stabilizedBy = quiet | max-wait`; a bounded observation lowers confidence to Medium.
* **Main-thread blocking time** — Σ max(0, duration − 50 ms) over observed long tasks. Not Lighthouse TBT (different window).
* **INP** — web-vitals methodology over Event Timing (`durationThreshold` 40 ms, longest duration per `interactionId`, worst interaction
  below 50 interactions, (n/50)th worst above). Published only after **3** distinct interactions; otherwise `insufficient-samples`,
  `not-measured` or `not-supported`. Never approximated from long tasks.

## API / network

Per page and generation, per REST endpoint (method + host + path) or GraphQL operation (endpoint + type + name): count, latency
statistics (min / p50 / p95 / max / total), statuses (errors, 401/403 kept distinct, 304), cache directives, duplicate flag,
polling-like flag (regular cadence over ≥ 4 samples spanning ≥ 10 s; heuristic only — it lowers confidence and never hides evidence,
because no configured polling classification exists), bursts, observed sequential patterns (worded as observations, not dependencies).

Proxy duration = request head received → last response byte relayed. Samples are bounded (50 per endpoint, most recent first) and,
after "Refresh analysis", restricted to the new generation's boundary so the cumulative live registry never inflates a refreshed page.

## Resources / cache

Resource Timing classification (js, css, wasm, image, font, api, document, framework-data, other), per-category counts/bytes/largest/
slowest, cache inference (`deliveryType === 'cache'` or zero transfer with a body; opaque entries stay unknown), duplicates split into
network repeats vs cache hits, failures, bounded chronological timeline. Proxy cache metadata (allow-listed Cache-Control directives,
ETag/Last-Modified presence, 304 count) drives "static asset repeatedly transferred without cache policy" and "_framework without
max-age/immutable".

## Blazor WASM

Framework payload and count, WASM bytes, runtime resources, assemblies, culture/timezone data, framework JS, boot manifest duration/
failure, framework load window (first framework request → last framework response), load kind (cold / warm / mixed) from cache
inference, framework downloads after SPA navigation. Bootstrap-to-first-render is not directly observable; page stabilization is used.

## Coverage

Per page and overall: Browser, Runtime, Resources, API, Blazor each `Complete | Partial | Not available`; overall
`Complete assessment | Partial assessment | Not assessed`. No evidence → the engine is **not assessed** with the reason
`PerformanceEvidenceUnavailable`, never "completed with no findings".

## Security

Only sanitized metadata leaves the browser or the proxy: scheme/host/path (query and fragment stripped), counts, durations, statuses,
allow-listed cache directives, operation names, structural selectors. Never Authorization/Bearer, cookies, MSAL cache, request or
response bodies, GraphQL query text, header values. Persistence (`birknext:endpoint-discovery`) holds the same data; live session ids
are never persisted.

## Lighthouse

Lighthouse stays available as a lab comparison. Where both exist for the initial load of the target page, LCP, CLS, FCP, TTFB and
transfer are shown side by side with the methodology note. Values are not expected to match (field vs synthetic lab, authenticated vs
anonymous, cache state, throttling), and the native engine is never adjusted to match Lighthouse.

## Collector overhead

The companion batches evidence (one snapshot at stabilization, ≤ 6 passive updates every 4 s, coalesced per page in the service worker,
flushed every 1.5 s), avoids continuous DOM scans (MutationObserver counts only), and reports its own cost per snapshot
(`collector.snapshotBuildMs`, observer callbacks, snapshots sent, payload bytes, entries examined).
