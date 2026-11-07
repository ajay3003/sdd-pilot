# BirkNext UI and Browser Verification

**Status:** NOT RUN for this pass. No application was launched and no browser automation, screenshots, browser logs, viewport checks, keyboard walkthrough, accessibility scan, or screen-reader test was performed.

The Integration Topology worklist change was committed as `63ee0980` during this audit. That commit was not built or browser-verified.

## Reason and evidence

- Worktree edits appeared during the audit in `AIAssisted/frontend/BirkNext.Web/Pages/IntegrationTopologyPage.razor`, its `.razor.css`, and `AIAssisted/frontend/BirkNext.Web.Tests/Pages/IntegrationTopologyPageTests.cs`. They were preserved and not exercised to avoid racing concurrent edits.
- The normal frontend project build is guarded by `FailIfStaleScopedCssBundleExists`; it failed when `wwwroot/BirkNext.Web.styles.css` was present (`AIAssisted/frontend/BirkNext.Web/BirkNext.Web.csproj:17-23`). No file was moved or deleted.
- An API-only Release build succeeded, but this does not compile current Razor UI.

## Coverage matrix

| Journey / area | Browser result | Screenshots/logs | Disposition |
|---|---|---|---|
| Project Import / Source Analysis / Traceability | Not run | None | Not tested |
| Relationship Review / Integration Topology / Catalog / IQR | Not run | None | Not tested; topology files changed concurrently |
| Test Data / Schema Studio | Not run | None | Not tested |
| AQR / FQR / Performance / M01 / Skolenærvær | Not run | None | Not tested |
| Delivery Readiness | Not run | None | Not tested |
| 390px / 768px / 1440px layout | Not run | None | Not tested |
| Keyboard/focus/labels/dialogs | Not run | None | Not tested |

No usability or accessibility defect is asserted from this report. Prior reports are historical context and do not substitute for browser verification. BQA-005 remains open as a coverage limitation. The next safe step is to wait for the concurrent UI edits to settle, build the frontend in a clean isolated workspace, then run local synthetic browser flows with all network destinations restricted to localhost.

## Verification completion — current source `7410e179` (2026-11-06)

The sections above are the **historical** audit at `546584f1` (final observed HEAD `63ee0980`). They are kept unchanged. This section records the
completion run against the current source. Source state verified: branch `008-traceability-first`, HEAD `7410e1793be162bd589762c9e65749d0f8621c46`; the isolated build worktree's
3,541 source files (`AIAssisted/`, `scripts/`) are byte-identical to that commit (blob hashes compared; only 7 `AIAssisted/docs/*.md` reports absent).
Uncommitted user files left untouched: `AIAssisted/backend/BirkNext.Api/appsettings.Local.json`, `AIAssisted/testdata/`. No push, no deployment,
no external system (M2LB, TT02, Altinn, MU, MM, Bufdata, Azure DevOps) contacted; browser runs abort every non-localhost request.

### Browser verification (published Release build, localhost :5199 → :5099, `birknext_gaps`)

Every script aborts non-localhost requests and records any state-changing request as a failure (only listed POST-for-query status endpoints are
allowed, each checked to persist nothing). Screenshots are in the session scratchpad (`shots-verify/`, `shots-rw/`).

| Journey (mandatory list) | Script → result |
|---|---|
| 1 Startup and navigation | `nav-e2e` 39/39; `verify-journeys` 18 routes load, no page error, no 5xx, no raw expression text |
| 2 Target Environment selection | `verify-journeys` TE ✓; `auth-e2e` 25/25 |
| 3 Authentication guided workflow | `auth-e2e` 25/25 (controlled `.invalid` detection) |
| 4 Connected Systems / 5 Integration Catalog | `cs-catalog-e2e` 15/15 |
| 6 Integration Quality Review | `iqr-e2e` 10/10 |
| 7 Integration Topology | `topo-e2e` 14/14, `rw-topo-e2e` 11/11 |
| 8 API Quality Review | `aqr-e2e` 8/8, `rw-aqr-e2e` 46/46 (synthetic stored results, all 8 tabs) |
| 9 Frontend Quality Review | `fqr-trace-e2e` 12/12, `rw-fqr-e2e` 39/39 (synthetic stored runs, all 5 tabs) |
| 10 Requirements Traceability | `fqr-trace-e2e` 12/12 |
| 11 Skolenærvær / 12 M01 | `m01-snt-e2e` 11/11, `mh-e2e` 11/11 |
| 13 Test Data Overview | `verify-journeys` TD ✓, `next58-e2e` 25/25 |
| 14 XML fixture | `verify-journeys` XF ✓ (open, steps, states) — Save/Validate-record **not executed** (writes) |
| 15 Performance Test Review | `next58-e2e` (configuration/readiness, no run) |
| 16 Snapshots | `snap-e2e` 18/18 |
| Localhost security | `sec-e2e` 12/12 |

**Accessibility (axe-core 4.13 from the repository):** 18 pages; before fixes 12 serious `color-contrast` nodes on 4 pages, after fixes **0
serious/critical**. Keyboard focus visible on each page's first control; no horizontal scroll at 390 px on any page or result tab.

**Script maintenance (not product changes, documented):** `nav-e2e`, `auth-e2e`, `snap-e2e` predated the launch-code gate and now connect a local
session like every other script; `next58-e2e` waits for the readiness dimensions instead of a fixed 2.5 s; two `snap-e2e` expectations were
updated to intentional product changes (`e536b241` added the attention KPI; Source Analysis snapshots say "None recorded · not all stores
checked" instead of claiming none).

**Observation:** one run reported a console error from an aborted engine-status fetch on Performance Test Review — caused by the harness's
full-page navigation while the query was still in flight after 45 s (BQA-013).

## Gap closure — 2026-11-07: writable browser journeys

The sections above are kept unchanged. Published Release web app (rebuilt after `04845c9b`) on :5199 → isolated API :5099, Edge via
Playwright, 1440×1000, every non-localhost request aborted.

| Journey | Result | Notes |
|---|---|---|
| XML Fixture generate → map → edit → validate → save → reload (G6) | **PASS** 22/22 | Includes invalid edits invalidating validation, invalid XML not saveable, not-approved after reload, duplicate alias kept separate. Details in the verification audit. |
| System Settings → Git Sources → Microsoft sign-in setup | **PASS** 18/18 + 10/10 after an API restart | Before setup: "Not configured", never "All clear", missing items say "Set up here", Test connection disabled with a reason. An invalid client ID is refused with a reason and nothing is written; the backend refuses an invalid tenant (400). After save: restart notice, values written to the isolated content root's settings file with its other sections kept, running sign-in unchanged until restart, values persist across a reload. After restart: Ready to connect, Sign in offered, no restart notice, Azure DevOps access "Not tested". Microsoft was never called. "Client ID required to enable" is covered by backend unit tests. |
| Workflows A–F (G7) | see cross-module report | All ran with writes; no page errors in any run. |
| M01 static review reload in a fresh browser | **PASS** after `04845c9b` | Failed before the fix (BQA-014). |

**Accessibility (changed UI only):** axe-core on System Settings with the Git Sources sign-in setup expanded: 0 serious/critical; the client ID
field shows a visible keyboard focus indicator (other new controls use the same input/button styles; not individually checked). Error and
restart messages are text, not colour only. BQA-014 and the launcher fix changed no markup. No screen-reader session.
