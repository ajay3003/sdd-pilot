# BirkNext Independent Audit â€” Security and Readiness

## Current security state (source review)

The previous broad audit conclusion that the API had no global caller boundary is **stale for the current source state**. Current `Program.cs` registers JWT plus a local-session authentication handler, validates configured bind URLs, and places `LocalRequestGuardMiddleware` after authentication and before authorization. The guard covers `/api` and `/graphql`. See [Program.cs:84â€“117, 919â€“927](../../AIAssisted/backend/BirkNext.Api/Program.cs) and [LocalRequestGuard.cs:30â€“96](../../AIAssisted/backend/BirkNext.Api/Services/LocalSecurity/LocalRequestGuard.cs).

### Implemented controls

- API request Host restricted to `localhost`, `127.0.0.1`, or IPv6 loopback by the central request guard.
- Caller must authenticate using a local session or valid JWT bearer token for protected `/api` and `/graphql` requests.
- Cookie-authenticated unsafe requests require exact frontend Origin and `X-BirkNext-Client`; GraphQL POST is covered as an unsafe method.
- Session bootstrap is an explicit launch-code exchange, rate-limited on wrong attempts, with HttpOnly/SameSite cookie and in-memory token-hash validation.
- Session cookie authentication carries local identity, not Active Event or integration-write permission claims. Existing named policies continue to enforce separate capabilities.
- Browser Companion extension endpoints bypass the central session check but have dedicated loopback/Host/extension-Origin and paired-session logic.

### Tests in source versus results

`LocalSessionSecurityTests.cs` contains assertions for loopback URL classification, anonymous REST/GraphQL rejection, bootstrap exchange, cookies, origin checks, throttling and session revocation. [LocalSessionSecurityTests.cs:18â€“60, 79â€“130](../../AIAssisted/backend/BirkNext.Api.Tests/Controllers/LocalSessionSecurityTests.cs). In this audit, both existing Debug and Release test assemblies reported no matching test case for the class filter. Thus **mechanism exists in source; test source exists; runtime behavior not test-verified in this audit**.

## Security boundaries and limits

| Boundary | Current evidence | Status / residual |
|---|---|---|
| API bind | Startup checks `urls` and `Kestrel:Endpoints` URL values and refuses non-loopback unless explicit opt-out. [Program.cs:84â€“94](../../AIAssisted/backend/BirkNext.Api/Program.cs) | Source-confirmed policy. Actual listener and all alternate host configuration sources are runtime/configuration-unverified; see BQA-001. |
| Request Host | Middleware checks loopback Host on `/api` and `/graphql`. [LocalRequestGuard.cs:39â€“45](../../AIAssisted/backend/BirkNext.Api/Services/LocalSecurity/LocalRequestGuard.cs) | Source-confirmed. It is a Host check, not a user identity or IP-peer check for all API calls. |
| Authentication | Local session or JWT bearer. [LocalRequestGuard.cs:49â€“64](../../AIAssisted/backend/BirkNext.Api/Services/LocalSecurity/LocalRequestGuard.cs) | Source-confirmed central authentication; not per-project authorization. |
| CSRF | Cookie unsafe operations require configured frontend Origin and custom client header. [LocalRequestGuard.cs:65â€“72](../../AIAssisted/backend/BirkNext.Api/Services/LocalSecurity/LocalRequestGuard.cs) | Source-confirmed. CORS alone is not the control. Browser runtime not verified. |
| Entra permissions | Local session grants identity only; existing named policies remain. [Program.cs:108â€“117](../../AIAssisted/backend/BirkNext.Api/Program.cs) | Source-confirmed distinction. Per-resource ownership is not established by this local identity. |
| Launch-code storage | Plaintext launch code in `App_Data/local-session/launch-code.txt`; source does not set ACLs. [LocalSessionService.cs:33â€“45, 109â€“126](../../AIAssisted/backend/BirkNext.Api/Services/LocalSecurity/LocalSessionService.cs) | Filesystem ACL is inherited from install directory. Same-user process access is an acknowledged limitation; multi-user package ACL remains unknown. |
| Session lifetime | 12-hour idle, 7-day absolute; sessions kept in process memory; restart loses sessions. [LocalSessionService.cs:19â€“20, 70â€“88](../../AIAssisted/backend/BirkNext.Api/Services/LocalSecurity/LocalSessionService.cs) | Source-confirmed; restart/browser behavior not re-executed. |
| External target credentials | Separate target authentication and execution capabilities. | This pass did not re-audit each provider/session combination; do not equate target bearer credentials with BirkNext API identity. |

## Readiness semantics

Recent hardening reports describe separate states such as blocked, partial, not assessed and not executed. This audit did not enumerate every readiness enum or rendering path, so no global invariant claim is made. Current examples such as API review explicitly state that no findings is not a passed review, but that one page/model cannot prove all modules obey the same rule. See [ApiReviewWorkflow.cs:64](../../AIAssisted/frontend/BirkNext.Web/Services/ApiReviewWorkflow.cs) and [ApiReviewPresentation.cs:87](../../AIAssisted/frontend/BirkNext.Web/Services/ApiReviewPresentation.cs).

Required cross-module runtime checks remain: historical Passed versus current result, configured versus executable, static versus runtime evidence, and empty findings versus a verified pass. These are covered by BQA-004, not asserted as defects.

## Security/readiness disposition

- Previous anonymous REST/GraphQL gap: **current source now has a central guard**; historical finding should be marked source-remediated, not runtime-closed.
- Per-user/project isolation: **not implemented as a distinct identity/ownership model by the local session**, and may be acceptable for the documented single-user workstation model; multi-user use is not verified.
- Authentication modes and target-level execution: **partial review only**; no real credentials or target tests were used.
- M01 Code 6/7: several related commits landed during this audit from work outside this audit; the resulting feature was not exhaustively reverified here.

See central items [BQA-001 through BQA-006](birknext-gap-register-2026-11-06.md#gap-register).
