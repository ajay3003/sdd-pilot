# Browser Companion reporting audit — 2026-09-17

Scope: Browser Companion only. No proxy, authentication, discovery or backend production behavior changed.

## Finding and limits

The reported live failure was not reproduced in the user's authenticated Edge profile. A fresh isolated Chromium profile with
the original extension successfully reported a fixture served at `https://m2lbdev.bufetat.no`. Thus, opening this origin alone
is not a demonstrated root cause, and this audit does not claim a verified live M2LB fix.

Confirmed reporting defects, reproduced against the original worker:

- The heartbeat alarm was created only during pairing, not restored when missing at browser startup. Pairing updates backend
  LastSeenAt immediately, so the UI initially says Connected even without subsequent reports; it expires after 45 seconds.
- Current page existed only in a worker global. Worker termination erased it; subsequent alarm heartbeats sent a null page.
- Registration failures set Blocked but pairing immediately overwrote that with Connected. Popup validation also ignored
  registration and host-permission failures.

The alarm requested 15 seconds despite declaring support for Chromium 116. Packaged extensions before Chromium 120 have a
one-minute minimum, exceeding the backend's 45-second connected window. Chromium 120+ permits 30 seconds. The extension now
requires 120, uses 30 seconds, restores missing alarms on worker wake/startup, and migrates the old interval.
See [Chrome alarms documentation](https://developer.chrome.com/docs/extensions/reference/api/alarms).

## Requested audit

| Area | Result |
| --- | --- |
| manifest host_permissions | Loopback only, unchanged. Target access remains optional and requested per approved origin. |
| content_scripts.matches | No static content scripts. Worker dynamically registers ISOLATED collectors and MAIN error forwarder with `https://m2lbdev.bufetat.no/*`, document_start, top frame only. |
| Runtime permissions | Added permission checks during registration, session-scope delivery, page messages, evidence messages and alarm page resolution. Denied/revoked origins fail closed. |
| Current-page reporting | Immediate approved-visit report plus 15-second page reports, independent of stabilization and evidence update limits. The alarm supplies worker-driven liveness when page timers are throttled. |
| Origin comparison | Shared exact-origin policy retained. URL parsing normalizes host/default ports. Subdomains, other schemes/ports and identity-provider infrastructure are not admitted by suffix matching. Backend normalizes approved origins already; the supplied M2LB origin matches. |
| Tab/profile | Store only tab id and environment profile id in extension session storage; resolve that tab's current safe identity for heartbeat. Browser profiles remain isolated by extension storage. Incognito and subframe messages are ignored. No active-tab lookup. |
| Content startup | Real extension registration and document_start injection passed on a fixture, with no page startup errors. No evidence of a startup exception on live M2LB was available. |
| Worker messaging | Sender extension, top frame, approved origin, host permission and evidence environment id checked. Page identity comes from the browser tab, not message-supplied URL. |
| BirkNext active tab | No dependency found. Regression closes the popup, opens an unapproved foreground tab, and continues receiving M2LB reports. |

No new cookie, credential, storage-of-target-page, request-body or token collection APIs were introduced. Raw tab URLs are
not persisted or posted: existing sanitizer removes userinfo/query/fragment and redacts credential-shaped path values.
DOM/accessibility/performance still use the existing evidence endpoint; heartbeats do not create evidence pages.

## Changes

- `background.js`: alarm restoration/migration, restart-safe tab association, safe current-page resolution, permission/sender
  checks, registration failures preserved in popup status.
- `content.js`: immediate and periodic page reporting independent of evidence collection.
- `manifest.json`: minimum Chromium version 120 for the supported alarm interval. Host permissions unchanged.
- `tests/background.test.mjs`: restart/lost alarm, registration failure and rejected sender/origin/permission regressions.
- `tests/reporting.test.mjs`: real isolated extension navigation, refresh, worker stop, foreground-tab independence and evidence.
- `../backend/BirkNext.Api.Tests/Services/BrowserCompanion/BrowserCompanionServiceTests.cs`: pairing, repeated heartbeats,
  current page, evidence count 0 before receipt and 1 afterwards, continued Connected state.

## Validation

- Three worker regression tests fail on original HEAD and pass with the fix.
- Extension build: 57 passed, 1 optional axe comparison skipped; syntax, manifest and credential-API checks pass; ZIP produced.
- Backend BrowserCompanionServiceTests: 24 passed.
- Real Chromium regression: Pair → Connected → open approved M2LB fixture → refresh → stop worker → resume → keep reporting
  beyond 45 seconds with an unapproved tab foregrounded. Current page stays M2LB; evidence includes DOM, accessibility and
  performance; no unapproved evidence sent.
- Headless test pregrants exactly the fixture origin in a temporary manifest because it cannot accept the native permission
  dialog. Production host permissions are unchanged; denied permissions are covered by worker tests.
- Fixture backend records extension HTTP posts; real backend behavior is covered separately by the service tests.
- An existing generated CSS bundle was temporarily moved to permit the backend test build and restored afterwards.

## Apply to the installed extension

Reload Browser Companion in `edge://extensions` from this source folder (or update the packaged extension), then refresh the
approved target page in that same Edge profile. A page already loaded before registration needs refresh for document_start
injection. The installed extension and live authenticated M2LB session were not modified by this audit.
