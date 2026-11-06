# Localhost security — local-session bootstrap design (Phase 1)

Branch `008-traceability-first` · 2026-11-06. This design is the basis for implementation; nothing is implemented outside it.

## Current model (source-verified)

- **Binding:** the API is bound only by launchSettings or `ASPNETCORE_URLS`, with no Kestrel enforcement. `AllowedHosts: "*"` and no Host check outside two filters.
- **Endpoint protection:** 63 of 76 controllers and all of GraphQL are anonymous. CORS allows one exact origin, which restricts what a browser may *read*; it is not authentication.
- **Stronger protections that already exist, and are kept:**
  - Named policies `ActiveEventExecute` and `IntegrationConfigurationWrite`: Entra permission claims, or Development-only local trust.
  - Browser Companion: pairing code, then a 64-hex session bound to the extension origin.
  - `ManagedEdgeLocalCallerFilter`: loopback peer, loopback Host and exact frontend Origin.
  - The local proxy binds loopback.
- **Frontend:** always a separate origin (`http://localhost:5173`; dev server or `dotnet-serve`). 61 HttpClients share one `ConfigureHttpClientDefaults` handler; GraphQL uses Strawberry Shake on the same factory. No client sends credentials.
- **Logs:** the launcher redirects the API's stdout and stderr into `logs\backend.*.log`, so anything printed by the API lands in a file.

## Threat boundaries

| Actor | In scope | Defence |
|---|---|---|
| Another machine on the LAN | Yes | Loopback-only binding, enforced at startup (refuses to start on a wider address unless explicitly allowed) |
| A malicious website in the tester's browser (CSRF, DNS rebinding) | Yes | Session cookie `SameSite=Strict` + `HttpOnly`. Unsafe requests and GraphQL need the exact frontend Origin **and** a custom header (forces CORS preflight). Host must be a loopback name (defeats DNS rebinding) |
| Another web app on a different localhost port (same-site) | Yes | The same Origin and custom-header checks. SameSite alone does not separate localhost ports |
| An extension origin | Yes | `chrome-extension://` / `moz-extension://` Origins are accepted only on the dedicated Browser Companion extension routes, which keep their own pairing check |
| Another Windows user on the same PC | Yes | The launch code is stored in the backend's own `App_Data`, inside the running user's package folder and governed by that folder's file permissions. It is never in logs, URLs, static files or command lines |
| A process running as the same Windows user | **No** | It can read the launch code file, as it can read the browser profile. Local credentials do **not** protect against a compromised same-user process |

## Bootstrap: launcher-mediated launch code (selected)

**Options rejected**

| Option | Why rejected |
|---|---|
| Same-origin hosting by the API | Would still need an issuance step, and the brief forbids unauthenticated token issuance. It also does not match how the package is launched today |
| A token in the URL fragment, or in an injected static file | Explicitly forbidden |
| Trusting Origin / `Sec-Fetch-Site` for issuance | Origin must not be authentication |

**Selected flow**

1. **Launch code.** At startup the API creates a launch code: 60 bits from `RandomNumberGenerator`, shown as `XXXX-XXXX-XXXX` in an unambiguous alphabet.
   - It is stored in `App_Data/local-session/launch-code.txt` beside the running API, so each installed copy has its own.
   - It is reused across restarts until rotated, and never logged.
2. **Showing the code.** The launcher reads that file once the API is up and prints the code in its own window. It does not add the code to `launcher.log`. A developer without the launcher reads the same file.
3. **Exchange.** The browser app asks the tester for the code once and calls `POST /api/local-session` with it.
   - This is **proof of possession**, not anonymous issuance.
   - Attempts are throttled and the comparison is constant-time.
   - The endpoint requires a loopback Host and the exact frontend Origin, as defence in depth rather than as authentication.
4. **Session cookie.** On success the API sets `BirkNext.LocalSession`: 256 random bits, `HttpOnly`, `SameSite=Strict`, `Path=/`.
   - Only the hash of the session is kept, in memory. Idle lifetime is 12 h; absolute lifetime is 7 days.
   - Multiple tabs and reloads share the cookie. An API restart forgets all sessions, and the tester re-enters the same code.
   - Rotating the code revokes every session. `DELETE /api/local-session` revokes the current one.
5. **Cross-port requests.** The cookie is same-site with `localhost:5173`. The frontend sends it with `credentials: include` on every API and GraphQL request, through one shared HTTP handler. CORS adds `AllowCredentials` for the single configured origin.

## Enforcement (central, no per-controller code)

- **Authentication scheme `LocalSession`** validates the cookie. The fallback authorization policy requires an authenticated user from `LocalSession` or Entra JwtBearer.
  - The named policies keep their own requirements, so a local session never grants Active Event execution or integration configuration writes.
- **`LocalRequestGuardMiddleware`**, for `/api` and `/graphql`:
  - **Host check:** the Host must be `localhost`, `127.0.0.1` or `[::1]`; otherwise 400.
  - **CSRF check:** for cookie-authenticated unsafe methods, and for all `/graphql` POSTs, the request needs the exact frontend Origin plus `X-BirkNext-Client`. Entra bearer requests are exempt, since a bearer token is not ambient.
  - **Extension-origin check:** an extension Origin is rejected outside `/api/browser-companion/extension/*`.
- **Anonymous exceptions, explicit and narrow:**
  - `GET /api/local-session` (status only: whether this browser is connected; no secret).
  - `POST /api/local-session` (code exchange).
  - The Browser Companion extension routes, which keep their own pairing and session checks.
  - Existing `[AllowAnonymous]` reads on policy-protected controllers now need a local session: they were anonymous only to escape the permission policy.
- **401 response:** carries `WWW-Authenticate: BirkNext-LocalSession`. The frontend shows "Connect this browser — enter the code from the BirkNext start window". It never retries a mutation; after connecting it reloads the page.

## Loopback binding

- At startup the configured URLs (`urls` / `ASPNETCORE_URLS` / launch settings) must all have a loopback host. `*`, `+`, `0.0.0.0`, LAN addresses and host names are refused with a clear error.
- An explicit `Hosting:AllowNonLoopbackBinding=true` opt-out exists for deliberately non-local hosting, and it is logged.
- The test server (no URLs) is unaffected. Outbound container networking for k6 and ZAP targets is not involved.

## Tests

Backend tests use the real bootstrap: they read the code from the service, exchange it, and the in-process client keeps the cookie and sends the Origin and client headers. There is no test-only bypass in production code.
