# Localhost security hardening — final report

Branch `008-traceability-first` · 2026-11-06 · local commits only. The design was written before implementation: `docs/security/localhost-session-design-2026-11-06.md`.

Status legend:

- **Source verified** — read in the code.
- **Test verified** — covered by passing automated tests.
- **Browser verified** — checked against the local stack in a real browser.
- **Runtime unverified** — not exercised outside the test host.

## 1. Previous security model (source verified)

- **Binding:** the API listened wherever launch settings or `ASPNETCORE_URLS` said, with no enforcement. `AllowedHosts` was `*`.
- **Endpoints:** 63 of 76 controllers and all of GraphQL were anonymous.
- **CORS:** a single allowed origin. CORS only limits what a browser may read; it is not authentication.
- **Stronger protections already in place** (all preserved):
  - Two named policies, `ActiveEventExecute` and `IntegrationConfigurationWrite`.
  - Browser Companion pairing.
  - `ManagedEdgeLocalCallerFilter`, which accepts only a loopback peer, a loopback Host and the frontend Origin.
  - The local proxy, bound to loopback.
- **Frontend clients:** none sent credentials. Eight clients sent an Entra bearer token, and only when Entra was configured.

## 2. Selected model and threat boundaries

**Protected against:**
- Other machines on the network.
- Other Windows users on the same PC.
- Malicious websites: CSRF and DNS rebinding.
- Other web apps running on other localhost ports.
- Browser extension origins.

**Explicitly not protected against:** a compromised process running as the same Windows user. It can read the launch code just as it can read the browser profile.

The threat table is in the design document.

## 3. Secure bootstrap

The bootstrap is a launch code mediated by the launcher.

1. **Code generation.** At startup the API creates a 60-bit code and writes it to `App_Data/local-session/launch-code.txt`, next to the running API. The code is:
   - unique to each installation;
   - reused across restarts until rotated;
   - never written to a log, URL, static file or command line.
2. **Display.** The launcher prints the code in its own window. It does not write it to `launcher.log`.
3. **Exchange.** The browser asks for the code once and posts it to `POST /api/local-session`. This is proof of possession, not anonymous token issuance. The exchange:
   - compares the code in constant time;
   - is throttled to 10 wrong attempts per minute;
   - accepts only the frontend Origin together with the `X-BirkNext-Client` header (defence in depth).
4. **Session cookie.** The API then sets `BirkNext.LocalSession`: HttpOnly, SameSite=Strict, a 256-bit random value. Only its hash is kept, in memory.
   - Lifetime: 12 hours idle, 7 days absolute.
   - `DELETE /api/local-session` revokes the current session.
   - `POST /api/local-session/rotate` replaces the code and revokes every session.
   - An API restart forgets all sessions; the tester enters the same code again.

## 4. Startup and middleware changes

**Loopback binding.** At startup, every configured URL must be loopback. This covers `urls`/`ASPNETCORE_URLS` and `Kestrel:Endpoints`.
- Startup is refused for `*`, `+`, `0.0.0.0`, `[::]`, LAN addresses and host names.
- The explicit opt-out is `Hosting:AllowNonLoopbackBinding=true`.

**Authentication.** A `LocalSession` authentication scheme validates the cookie. It grants identity only and carries no permission claim.

**`LocalRequestGuardMiddleware`** sits after authentication and before authorization, for `/api` and `/graphql`. For each request it:
1. Requires a loopback Host (`localhost`, `127.0.0.1` or `[::1]`); otherwise 400.
2. Confines extension origins to `/api/browser-companion/extension/*`; otherwise 403.
3. Requires an authenticated caller; otherwise 401 with `WWW-Authenticate: BirkNext-LocalSession`.
   - A presented bearer token decides on its own: an invalid token is rejected and never replaced by the ambient cookie.
4. For cookie-authenticated unsafe methods, and for every GraphQL POST, requires the exact frontend Origin plus `X-BirkNext-Client`; otherwise 403.

**CORS.** `AllowCredentials` was added for the single configured origin.

## 5. API and GraphQL protection

- REST and GraphQL are closed by default: reads, mutations and queries alike.
- The named policies run afterwards, unchanged. A local session never grants Active Event execution or configuration writes. Requests that were previously 401 ("anonymous") are now 403 ("signed in, no permission") when a session is present.

## 6. Anonymous exceptions

- `GET /api/local-session`: status only.
- `POST /api/local-session`: the code exchange.
- `/api/browser-companion/extension/*`: keeps its own pairing, extension-origin and loopback checks.
- CORS preflight (`OPTIONS`).
- Non-API paths (static files).

## 7. Client compatibility

- **`LocalSessionHandler`.** Every factory `HttpClient`, including the Strawberry Shake GraphQL client, sends credentials and the client header to the backend origin only. Requests to target applications never carry the session. A session challenge opens the connect dialog. Requests are never retried; after connecting, the page reloads.
- **`LocalSessionGate`.** A dialog in the layout asks for the code from the start window, explains where to find it, shows wrong-code and throttle messages, and is keyboard-accessible with a label.
- **Browser Companion.** The extension routes are unchanged. The UI-side companion routes and the proxy and Edge routes are called by the frontend with the session.

## 8. Tests and results (actual runs, 2026-11-06)

**Automated (test verified)**

- `LocalSessionSecurityTests`: 16 tests, all passing. They cover:
  - no session → 401;
  - a wrong code is refused, and attempts are throttled (429);
  - an exchange without the frontend Origin or the client header is refused;
  - the cookie is HttpOnly and SameSite=Strict;
  - a non-loopback Host → 400;
  - CSRF: an unsafe request without Origin or client header is refused, and so is a GraphQL POST;
  - extension origins are allowed only on `/api/browser-companion/extension/*`;
  - a presented bearer token decides alone (an invalid bearer is 401, with no cookie fallback);
  - revoke and rotate end existing sessions;
  - the code never appears in responses.
- Existing authorization tests were re-expressed for the new boundary, line by line:
  - a session without the permission → 403 (it was 401);
  - an invalid bearer stays 401;
  - a rebound Host → 400.
- Controllers: 125/125.
- Full backend suite in six partitions, with partition 6 split into three hosts after the memory guard stopped one large host: **5,946 passed, 13 skipped, 0 failed**.
  - partitions 1–5: 970, 1,054, 806, 646 and 511 passed;
  - 6a (Controllers) 125, 6b (root services) 528, 6c (rest) 1,306.
- Frontend: `LocalSessionTests` plus the full frontend suite, 8,331/8,331 at the time of the change.
- The focused re-run after the later UI batch was 231/231. It included `LocalSessionTests`.

**Browser verified.** The local stack ran on `birknext_gaps`, published Release, on API :5099 and web :5199, localhost only.

- `sec-e2e.cjs`: **12/12**.
  1. An unconnected browser is asked for the code.
  2. The code input is labelled.
  3. A wrong code is refused with a plain message.
  4. The right code connects and the page loads its data.
  5. A reload keeps the session.
  6. The cookie is HttpOnly and SameSite=Strict.
  7. `document.cookie` cannot read it.
  8. The code is never in the URL, localStorage or sessionStorage.
  9. A state change from a non-frontend origin is refused (403).
  10. The frontend reads the API with its session (200).
  11. No external host was contacted.
  12. No page errors occurred.
- Direct requests with curl:
  - a protected GET without a session → 401;
  - `Host: evil.example` → 400;
  - a GraphQL POST without a session → 401;
  - a code exchange without the frontend Origin → refused (401);
  - an extension origin outside its routes → 403.
- Every earlier browser regression passes on the secured stack. Each script connects first through `connect-session.cjs`, using the same code exchange as the dialog:
  - next53 18/18, next54 13/13, next55 13/13, next56 14/14, next57 14/14, next58 25/25, phase3 25/25 (**122/122**).
- `batch-ui-e2e.cjs` (the UI batch committed alongside) passed 15/15. No state-changing request was sent and no external host was contacted.

**Runtime unverified**

- The tester-package start window showing the connection code (`start-local.ps1`) was not run end to end from an extracted package in this session.
- The Browser Companion extension was not re-paired against the secured API in this session. Its routes are unchanged and covered by the extension-origin tests.

## 9. Known limitations

- **Same-user processes.** A process running as the same Windows user can read the launch code (§2).
- **Restarts.** After an API restart each browser must enter the code again, because sessions are kept in memory.
- **Clients outside the browser** must perform the code exchange themselves:
  - the Playwright real-project acceptance session (`BirkNext.Web.PlaywrightTests`);
  - any script calling the API directly.
- **Plain HTTP on loopback.** The cookie is not `Secure`. That is acceptable only because the API is bound to loopback.
- **Broader authorization work remains separate.** This adds the authentication boundary. Per-endpoint authorization (who may do what) beyond the two named policies remains the separate workstream.

## 10. Changed files

**Backend**

- `Services/LocalSecurity/LocalSessionService.cs` (code, exchange/throttle, validate, revoke, rotate).
- `Services/LocalSecurity/LocalRequestGuard.cs`: the authentication handler, the guard middleware and the loopback binding check.
- `Controllers/LocalSessionController.cs`.
- `Program.cs` (credentialed CORS, startup loopback validation, scheme registration, guard between authentication and authorization).

**Backend tests**

- `Utilities/LocalSessionTestClient.cs`: the real bootstrap, used by 35 call sites in 16 classes.
- `Controllers/LocalSessionSecurityTests.cs`.
- Updated expectations in 16 existing test classes.

**Frontend**

- `Services/LocalSession.cs`: state, backend, handler and API.
- `Components/LocalSessionGate.razor` and `.css`.
- `Program.cs`: the handler on every factory client.
- `Layout/MainLayout.razor`.

**Frontend tests:** `Services/LocalSessionTests.cs`.

**Scripts:** `scripts/start-local.ps1` shows the connection code in the start window. The preflight part of this file was committed separately with the tester package.

**Docs:** `docs/security/localhost-session-design-2026-11-06.md` and this report.

## 11. Commits

One local commit on `008-traceability-first`: "Localhost security: launch-code session, loopback-only binding, CSRF guard". It is not pushed.

## 12. Remaining risks

- **Same-user processes can read the launch code** (§2). This is stated, not claimed as protected against.
- **Sessions are in memory**, so a restart asks every browser again. This is accepted as a usability cost.
- **Non-browser clients must exchange the code:** the Playwright real-project acceptance and ad-hoc scripts.
- **Per-endpoint authorization** beyond the named policies remains separate work. This change adds the authentication boundary only.
