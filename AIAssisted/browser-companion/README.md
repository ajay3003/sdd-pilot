# BirkNext Browser Companion

A Manifest V3 extension for the user's **normal managed Microsoft Edge** session. It collects safe page-quality evidence
(DOM structure, BirkNext accessibility checks, browser performance metrics, sanitized runtime errors, Blazor WASM
diagnostics) for pages of the **active Target Environment only** and reports it to the local BirkNext backend over
loopback. Together with the Local HTTPS Proxy's network evidence this feeds the **BirkNext Browser Quality** engine of the
Frontend Quality Review and the per-page **Page quality** view in Endpoint Discovery.

No Playwright. No CDP. No token, cookie or storage access. No modification of the application under test.

## What it does / does not do

| Provides | Does not provide |
| --- | --- |
| Native DOM structure evidence per page | Full axe rule coverage |
| BirkNext Accessibility Checks (Phase 1, 14 conservative rules) | Lighthouse score equivalence |
| Field performance metrics (TTFB, DCL, load, FCP, LCP, CLS, long tasks, resource timing) | Guaranteed WCAG conformance |
| Runtime error events (window error, unhandled rejection, resource error; script exceptions forwarded by a listener-only MAIN-world script) | Console interception |
| Blazor WASM diagnostics (boot manifest, framework resources, error UI) | Backend RabbitMQ / Event Hub visibility |
| Correlation with Local HTTPS Proxy network evidence per page | Access to credentials of any kind |

## Security model

- **Loopback only**: the backend is reached at `http://127.0.0.1:5000` (or `http://localhost:5000`); the popup refuses any other host.
- **Pairing**: BirkNext shows an 8-character single-use code (3-minute lifetime). The extension presents it; the backend issues a
  random session id bound to one Target Environment and to this extension's origin. Codes cannot be replayed; sessions expire
  (30 min idle, 12 h absolute) and are invalidated when the environment is paired again or unpaired.
- **Scope**: the content script is registered dynamically **only** for the environment's approved origins (Target URL origin and
  configured allowed redirect URLs). Static `host_permissions` are loopback only; `optional_host_permissions` are requested
  per approved origin at pairing time (user gesture). `<all_urls>` is never used.
- **No credential access**: the extension never reads cookies, `localStorage`, `sessionStorage`, MSAL cache, headers, form
  values or request bodies. `build.mjs` fails if a script references any of those APIs.
- **Sanitization on both sides**: `lib/sanitize.js` redacts Bearer/JWT/e-mail/token-shaped values, strips query strings and
  restricts selectors to structural tokens; the backend `BrowserCompanionEvidenceSanitizer` applies the same policy again and
  bounds every list before anything is exposed.
- **Limits**: coalesced batches (one message per page visit snapshot, at most 20 pages), backend rate limit, 512 KB envelope cap,
  bounded findings/selectors/resources/errors.

## Install (DEV, unpacked)

1. Edge → `edge://extensions` → enable **Developer mode**.
2. **Load unpacked** → select this folder (`AIAssisted/browser-companion`).
3. In BirkNext → Target Environments → *Endpoint Discovery* tab → **Pair browser companion**; a code is shown.
4. Click the extension icon → enter the code → **Pair**. Grant the host permission prompt for the approved origin(s).
5. Sign in to the application as usual and navigate; each visited page appears in Endpoint Discovery with a *Page quality* section.

Runtime states: *Not paired*, *Pairing…*, *Connected*, *Paired · not reporting* (browser closed / extension disabled),
*Session expired*, and *Blocked by policy* when the managed browser refuses content-script registration.

## Enterprise policy check (this workstation, 2026-09-16)

`HKLM\SOFTWARE\Policies\Microsoft\Edge`: no `ExtensionInstallBlocklist`, no `ExtensionInstallTypeBlocklist`,
`DeveloperToolsAvailability` not set, `ExtensionSettings` has no `*` default → unpacked extensions are permitted.
If a later policy blocks them, BirkNext shows "Browser Companion blocked by managed Edge policy" and does **not** fall back to
token injection or CDP.

## Development

```
node --test tests/     # unit tests (page identity, sanitizer, performance parsing, SPA navigation, accessibility rules)
node build.mjs         # validate manifest, syntax-check, run tests, package dist/*.zip
```

Accessibility rule tests run the rule library inside a real Chromium DOM (Playwright, `page.setContent`) on fixed fixtures — no
navigation and no timing dependence.

## Page identity

`origin + normalized pathname` (query string and fragment dropped, trailing slash trimmed, `/` for the root) — exactly the identity
Endpoint Discovery derives from proxy traffic, so browser evidence and proxy evidence converge on one page analysis.
"Refresh analysis" in BirkNext starts a new generation for that page only: browser evidence counts again only from a visit that
started after the refresh.
