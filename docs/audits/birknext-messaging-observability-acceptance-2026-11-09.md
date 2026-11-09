# BirkNext — Messaging observability — acceptance (2026-11-09)

Branch `008-traceability-first`. Source: `d440e37d` + this change (committed with these reports).

**Result: 31/31 PASS** — API 17/17, after restart 3/3, browser 11/11. Synthetic and local only: **no real Service Bus, Wolverine database or Azure
behaviour is verified by these checks** (none was contacted; live observation needs separate authorization).

## Environment (isolated, synthetic)

| Item | Value |
|---|---|
| API | The real BirkNext API (clean build) on `localhost:5098`, content root without a Local settings file, outbound proxies to a dead port |
| Database | `birknext_mo_20261109` (new, disposable) |
| Synthetic application | Node server in a local container (`node:20-bookworm`, present) on `:18091`: `POST /children` registers a child and records the `X-BirkNext-Execution-Id` it received — the **independent ledger** |
| Message evidence | Files written by the acceptance script playing the application's publisher: each message carries the execution id the application saw; one Service Bus active-queue peek export, one dead-letter peek export, one Wolverine envelope export (inbox, outbox, dead letters). Synthetic identity number `01010112345` and e-mail addresses are planted to test redaction |
| Web / browser | Published frontend on `localhost:5198`; Edge (Playwright, headless); non-localhost requests aborted (0) |

## API — `movalidation/accept.cjs main`

| ID | Check | Result |
|---|---|---|
| MO-01…03 | A real execution run registers three children; the application's ledger received three execution ids of that run | PASS |
| MO-10 | Three observation files imported | PASS |
| MO-11 | Redaction counted at import | PASS |
| MO-12 | Message 1: Confirmed to call 1 of the run; Wolverine inbox `Handled` → Succeeded | PASS |
| MO-13 | Message 2: Confirmed; in the inbox after 4 deliveries → Pending ("the count does not say why") | PASS |
| MO-14 | An unrelated message: Not correlated (never by time) | PASS |
| MO-15 | Metadata only: no body text, size + SHA-256 kept, sensitive property masked | PASS |
| MO-16 | Envelope evidence is joined by exact id from the Wolverine set | PASS |
| MO-17 | DLQ message: Dead-lettered with reason, Confirmed to call 3, Wolverine exception in the timeline | PASS |
| MO-18 | Sanitized preview keeps the shape, masks identity number and e-mail (also in the dead-letter description) | PASS |
| MO-19 | Wolverine summary: inbox pending 1, handled 1, outbox pending 1, dead letters 1 | PASS |
| MO-20 | The stored documents (read directly from PostgreSQL) contain no identity number, e-mail or envelope body | PASS |
| MO-21 | Unknown file kind refused | PASS |
| MO-22 | A set is not readable from another environment | PASS |
| MO-23 | No message operation exists: complete, abandon, dead-letter, resubmit, replay, purge, receive → 404/405 | PASS |

## After restart — API stopped by its PID and started again

| ID | Check | Result |
|---|---|---|
| MO-30 | The three sets are listed | PASS |
| MO-31 | Correlation and processing are reassessed identically | PASS |
| MO-32 | Each set keeps the observation time its export declared | PASS |

## Browser — Integration Topology → Messages

| ID | Check | Result |
|---|---|---|
| MO-B1 | The three imported sets are listed | PASS |
| MO-B2 | Succeeded / Pending / not correlated as assessed by the backend | PASS |
| MO-B3 | Detail: processing reason, correlation rationale, metadata-only note, no identity number | PASS |
| MO-B4 | Filter "Correlated" | PASS |
| MO-B5 | DLQ: reason and sanitized preview | PASS |
| MO-B6/B7 | The correlated message links to its run; Execution Results opens it | PASS |
| MO-B8 | Wolverine inbox / outbox / dead-letter counts | PASS |
| MO-B9 | No message operation offered on the page | PASS |
| MO-B10 | No horizontal page scroll at 1440 / 1280 / 1024 / 768 / 390 | PASS (after a fix, below) |
| MO-Z1 | No page errors, console errors or external requests | PASS |

**Found and fixed by the browser run:** the message tables widened the page at 768 and 390 px (grid items default to `min-width: auto`); the panel's
grid children are now shrinkable and the tables scroll inside their region. The first runs also logged console errors caused by the acceptance
script itself (navigating while the app was still downloading, and calling the API before the launcher's session exchange); the script now
exchanges the session first and waits for the app — the final run has zero console errors.

Screenshots (synthetic, scratch, not committed): `movalidation/shots/mo-01-messages.png`, `mo-02-messages-390.png`.
