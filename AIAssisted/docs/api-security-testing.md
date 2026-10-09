# Security testing in BirkNext: what runs, where, and what it proves

BirkNext's security checks are **bounded QA/security checks**. They are not penetration testing, vulnerability exploitation or
OWASP compliance. This page describes what each review actually executes and where the evidence comes from.

## Capability matrix

> BirkNext performs bounded non-production security checks. These checks are not a penetration test and do not establish that an
> application is secure.

| Area | State | Where | Evidence |
|---|---|---|---|
| Security headers | Runnable | FQR Static Security (frontend document), AQR (API responses) | Runtime response headers vs Target Environment → Security Expectations (presence only) |
| CORS | Runnable with bounded configured-origin tests | AQR → Security | Anonymous preflights: frontend origin, up to 3 configured allowed origins, one synthetic foreign origin (`https://foreign.birknext.invalid`) |
| Cookies | Runnable metadata inspection | FQR Static Security → Cookie security | Set-Cookie attributes of the frontend document; Local HTTPS proxy observed attributes. Never values |
| Swagger/OpenAPI exposure | Runnable | AQR → Security | Anonymous GETs of ≤ 6 same-origin documentation paths vs the exposure expectation |
| GraphQL introspection | Runnable | AQR | Runtime `__schema` query; policy warning in production-like environments |
| Error leakage | Runnable | AQR probes, safe fuzzing | Indicator names only |
| Authentication enforcement | Runnable | AQR | Deliberate anonymous request per authenticated target |
| Role-based authorization | Runnable with explicit scenarios and available test identities | AQR → Authorization scenarios | Each identity against its own Allow/Deny expectation; trusted targets only |
| Query/path/header fuzzing | Runnable | AQR → Safe fuzzing | Runtime responses to synthetic invalid/boundary input |
| Body fuzzing | Runnable only on explicitly opted-in operations | AQR → Safe fuzzing | Read-only-body-safe opt-ins on server-registered trusted targets |
| GraphQL mutation fuzzing | Not implemented | — | GraphQL mutations are never sent |
| OWASP | Reporting metadata | AQR/FQR findings | "Related" references on emitted finding rule ids; never a finding generator |
| ZAP | Optional dependency | FQR Passive Security | Needs server enablement, a trusted target profile and a local container image |
| Dependency vulnerabilities | Runnable (separate) | Dependency Health (OSV + nuget.org) | Package metadata; fuzzing does not replace it |
| Full penetration test | Not implemented | — | — |

## Environment safety (backend authority)

Active API testing — error probes and safe fuzzing — is decided by the backend (`ApiEnvironmentSafetyPolicy`), never by a client
boolean alone:

- **Allowed:** Local, Development, QA, Test.
- **Blocked:** Production (claimed type, client production flag, or the server-held Local HTTPS proxy context of the same Target
  Environment), any selected target or environment URL whose host carries a production marker (`prod`, `prd`, `production`, `live`),
  hosts listed in `ApiActiveTesting:BlockedHosts`.
- **Unknown/Custom/RC:** blocked ("Environment safety could not be established" / "not permitted").
- **Conflict:** blocked when the review's classification disagrees with the server-held proxy context.
- `ApiActiveTesting:FuzzingEnabled=false` disables safe fuzzing for the instance.

Error probes follow the production-like part of the decision (production claim or marker); safe fuzzing needs the full allow-list.

## Authentication enforcement (AQR)

For each target that requires authentication and was reviewed with the proxy credential, AQR repeats **one already-reviewed safe GET**
(REST) or `query { __typename }` (GraphQL) **without any credential**. Sign-in, token, OAuth and session endpoints are never probed.

| Anonymous answer | Result |
|---|---|
| 401 | Verified (authentication required) |
| 403 | Verified (denial evidence; authorization is not assessed) |
| GraphQL error envelope without data | Verified |
| 2xx with a JSON body / GraphQL data | Unexpectedly public (`rest-unexpectedly-public` / `gql-unexpectedly-public`, Medium) |
| 404 | Not verified (anti-disclosure is legitimate) |
| 3xx, non-JSON 2xx, 5xx, no answer | Not verified |
| Target does not require authentication | Not applicable |

Authenticated ≠ authorized: this check never claims anything about what an identity may access.

## Security headers and Security Expectations

`ExpectedSecurityHeaders` (Target Environment → Security Expectations) drives both reviews; the model stores names only, so evaluation
is **presence only** (X-Content-Type-Options must be `nosniff`).

- FQR Static Security evaluates the frontend document: an expected header that is absent is a finding (`HDR-MISSING-*`, now including
  X-Frame-Options when it is expected); a header that is not expected is never a finding.
- AQR evaluates transport headers (HSTS, X-Content-Type-Options) on API responses; document-level headers (CSP, X-Frame-Options,
  Referrer-Policy, Permissions-Policy) are recorded as "not applicable on API responses".
- HSTS on plain HTTP is not applicable. Requests from older clients without an expectation list keep each engine's historical defaults.

Security Expectations fields and their consumers are labelled in the editor: Authority, Allowed backend domains and CDN hosts → FQR
Static Security; REST/GraphQL hosts → Local HTTPS proxy scope; Expected security headers → FQR + AQR; Tenant, Redirect URLs and Client
IDs → Configuration Review only.

## Safe fuzzing (API Quality Review → Safe fuzzing)

> Safe fuzzing sends a bounded set of deterministic invalid/boundary requests to non-production APIs. It is not a penetration test.

- **Levels:** Off · Contract fuzzing · Safe security fuzzing. There is no aggressive mode.
- **Inputs:** only the published contract. REST cases need an OpenAPI 3.x document (Swagger/OpenAPI URL of the target); path, query and
  header parameters are modelled with type, format, enum, nullable, min/max, length and a JSON-pointer `SourceRef`. GraphQL cases need
  the schema (runtime introspection or a configured SDL artifact). Without a contract, fuzzing is **Not available** — unknown endpoints
  are never fuzzed.
- **Methods:** REST GET/HEAD/OPTIONS, GraphQL queries (inline literals, no variables). Write methods (unless explicitly opted in for request-body cases, see below), GraphQL mutations
  and subscriptions are listed as skipped and never sent. Sign-in/token endpoints are skipped.
- **Cases (contract):** missing required, invalid enum, invalid UUID, invalid date, wrong type, below minimum / above maximum,
  zero/negative where invalid, longer than maxLength, missing required header, invalid header value; GraphQL unknown field, wrong scalar,
  missing required argument, null for non-null, invalid enum. **Safe security adds:** empty string, bounded oversized string, an
  unexpected query parameter (acceptance is fine), a malformed header value, one malformed GraphQL document. No attack dictionaries,
  path traversal, SQL/XSS payloads or recursive structures.
- **Determinism:** case ids are digests of target, operation, mutation and parameter; the same contract and settings produce the same
  cases in the same order. Values are synthetic; nothing is copied from traffic.
- **Bounds (backend-enforced, `ApiFuzzRequestBudget` + `ApiFuzzingSettings.Clamped`):** at most 50 requests (default 40), at most 8 per
  operation (default 4), concurrency 1, ≥100 ms between requests (default 250 ms), ≤20 s timeout, parameters ≤1024 characters (default
  256), GraphQL documents ≤4 KB, URL ≤2048 characters, optional stop at the first unexpected 5xx. Eligible cases beyond the budget are
  reported as over budget, not dropped silently.
- **Guard (`ApiSafeRequestGuard`):** every request must target the reviewed target's own origin, use an allowed method, carry no
  credential/cookie/proxy/gateway header, stay within size limits and pass the GraphQL query-only rule. Authenticated targets run through
  the review gateway, which re-validates before applying the memory-only credential.
- **Outcomes:** Handled validation, Unexpected 5xx, Unexpected acceptance, Contract violation (rejection status the contract does not
  declare), Potential information leak, Blocked by authentication (401), Blocked by access control (403), Timeout, Connection failure,
  Safety blocked, Not executed. A 4xx for invalid input is the expected behaviour, not a defect; GraphQL is judged by its envelope.
- **Findings:** equivalent cases become one logical finding (rule + target + operation; leaks: rule + target + indicator set). Severities
  are conservative: unexpected 5xx and leaks Medium, unexpected acceptance and contract violations Low.
- **Completeness:** Full (every planned case executed), Partial (cancelled, blocked, timed out or over budget), Failed (nothing produced a
  response). Findings never make a run Failed.
- **Cancel:** stops scheduling and cancels the in-flight request; the run ends Partial with its results so far.
- **Evidence:** status, outcome and indicator names only — no response body, header value or credential is stored, logged or exported.
  Runs are kept in API review history (latest five per Target Environment) with their environment, settings and contract fingerprints;
  old runs are never re-evaluated against a newer contract.

## Runtime security expectations (Target Environment → Security Expectations)

Manual expectations only — source discovery never fills them. Each field shows the review that reads it ("Used by"):

| Field | Used by |
|---|---|
| API documentation exposure (Not specified / Allowed / Expected protected / Expected unavailable) + optional paths | AQR → Runtime security: API documentation exposure |
| CORS allowed origins, credentials expectation, allowed methods/headers | AQR → Runtime security: CORS |
| Auth/session cookie names, Secure/HttpOnly required, allowed SameSite and domains, persistent auth cookies | FQR → Static Security: cookie security |
| Authorization scenarios | AQR → Authorization scenarios |
| Request-body fuzzing opt-ins | AQR → Safe fuzzing (request-body cases) |

A run copies the expectations it uses (AQR policy snapshot, authorization expectation fingerprint, fuzzing opt-ins): history is never
re-evaluated against newer settings. Never enter passwords, tokens or cookie values.

## API documentation exposure (AQR)

Probing runs only when an exposure expectation or documentation paths are set, and never in production-like environments. Candidates are
same-origin only: configured paths, the target's OpenAPI URL, then `/swagger`, `/swagger/index.html`, `/swagger/v1/swagger.json`,
`/openapi.json`, `/api-docs` (at most 6, stopping at the first public document). Anonymous GET; redirects are not followed.

| Response | State |
|---|---|
| 2xx with an OpenAPI document or Swagger UI page | ReachablePublic (a JSON document is validated with the shared OpenAPI parser — exposure evidence, not contract authority) |
| 2xx that is not documentation (SPA fallback) | NotVerified |
| 401 / 403 | ReachableProtected |
| 404 | NotFound (not a security pass by itself) |
| 3xx | Redirected |

Assessment follows the expectation: public documentation with **Allowed** is as expected; with **Expected protected** or **Expected
unavailable** it is `api-docs-unexpected-exposure` (Medium, once per API origin); a protected endpoint with **Expected unavailable** is
`api-docs-unexpected-endpoint` (Low); nothing found with **Allowed** is UnexpectedAbsence (no finding). **Not specified** = not probed.

## CORS (AQR)

The frontend-origin preflight always runs (as before). Outside production-like environments AQR also sends anonymous preflights for up to
three configured allowed origins and one synthetic foreign origin. Observed behaviour (allowed, reflected, wildcard, denied, no CORS
headers) and the expectation are kept apart:

- Wildcard + credentials → `cors-wildcard-credentials` (High, unchanged).
- Foreign origin reflected with credentials → `cors-reflected-origin-credentials` (High); without credentials on an authenticated API →
  `cors-reflected-origin` (Medium); on a public API → `cors-reflected-origin-public` (Low).
- Configured allowed origin not granted → `cors-allowed-origin-denied` (Low). Credentials granted although the expectation says none →
  `cors-credentials-unexpected` (Medium). Methods/headers beyond the expected lists → `cors-preflight-broader-than-expected` (Low).
- No CORS headers is not a network failure and not a finding by itself.

## Cookie security (FQR)

Sources: the Set-Cookie headers of the anonymous frontend document response (Static Security), and Set-Cookie attributes observed by the
Local HTTPS proxy on approved hosts (loaded on demand in the cookie section through `api/local-https-proxy/observed-cookie-attributes`;
the polled proxy status deliberately carries no cookie data). The parser drops the value before anything is kept: name (digested when
secret-shaped), Secure, HttpOnly, SameSite, Domain, Path, session/persistent.

Declared auth/session cookies (exact names): missing Secure (High), missing HttpOnly (Medium), SameSite outside the allowed list (Low),
explicit Domain outside the allowed list (Low/Medium), persistent (Low). Every cookie: SameSite=None without Secure (Medium). Undeclared
cookies are observations — their purpose cannot be known. Cookie findings are listed in the cookie section and are not scored.

## Trusted targets (backend authority for authorization scenarios and body fuzzing)

These two checks cross the read-only boundary, so the server — not the client — must register the Target Environment:

```json
"SecurityTesting": {
  "TrustedTargets": {
    "<target-environment-id>": { "EnvironmentType": "QA", "ApiOrigins": [ "https://api-qa.example.test" ] }
  },
  "RequireAuthenticatedUser": false
}
```

Production is refused even when registered; Local/Development/QA/Test only; every destination origin must be a registered API origin
(arbitrary URLs are never contacted). The client environment type, production flag and URLs are inputs, never the decision; the existing
environment safety policy (production markers, proxy context) applies as well.

`RequireAuthenticatedUser=true` makes the protected endpoints refuse with "Security execution requires Entra authentication
configuration" — this instance has no user authentication scheme. The frontend has configuration hooks only (`wwwroot/appsettings.json`
→ `BirkNextAuthentication`: TenantId, SpaClientId, ApiScope, Authority, RedirectUri, PostLogoutRedirectUri; no client secret) and attaches
a bearer token to authorization/fuzzing runs only when a sign-in provider returns one. No tenant or client values are shipped, and no
sign-in library is bundled in this build.

## Authorization scenarios (AQR)

A scenario is one safe request (REST GET/HEAD or one GraphQL query without variables; mutations rejected) plus 1–4 identities, each with
an explicit expectation. Identities: `anonymous` (no credential), `proxy-session` (the memory-only Local HTTPS proxy credential) or an alias
served by a server-side `IAuthorizationTestIdentityProvider` (none is registered by default; passwords and tokens are never stored). The
Security Classification probe's M2LB-specific fixed queries remain a separate extension; the scenario machinery here is project-neutral.

| Expected | Response | Outcome |
|---|---|---|
| Allow | configured success (default 2xx; GraphQL: data without authorization error) | VerifiedAllow |
| Allow | 401 | AuthenticationFailed |
| Allow | 403, GraphQL authorization error | UnexpectedDeny |
| Deny | 401, 403, GraphQL authorization error (AUTH_NOT_AUTHORIZED …) | VerifiedDeny |
| Deny | 404 with anti-disclosure declared | VerifiedDeny |
| Deny | 404 without the declaration | NotVerified |
| Deny | success | UnexpectedAllow |
| any | identity not available, untrusted, invalid | ExecutionUnavailable |

Counts and differences between identities are never evidence. Bounds: 20 scenarios, 4 identities each, 40 requests, one at a time, 250 ms
apart, 15 s timeout, cancellation. Results keep aliases, roles, status codes and the expectation fingerprint — never a credential — and are
kept in API review history (latest five per Target Environment).

## Request-body fuzzing (AQR → Safe fuzzing)

Off by default ("Include request-body cases for explicitly opted-in operations"). Policies per operation: Disabled, Read-only body safe,
State-changing (needs cleanup), Not allowed. Only **Read-only body safe** POST/PUT/PATCH operations of a trusted target get cases;
state-changing operations are blocked (an `IRequestCleanupStrategy` contract exists — precondition, execution id, cleanup, verification —
but state-changing execution is not implemented in this milestone); DELETE and GraphQL mutations are never fuzzed; operations without a
locally resolvable JSON object body are skipped.

Cases from the contract body (flat, synthetic, deterministic): missing required field, null for non-null, empty string, invalid enum, wrong
type, invalid UUID, invalid date, below minimum / above maximum, longer than maxLength, unknown extra field, malformed JSON, wrong
Content-Type (text/plain), missing Content-Type. Duplicate-field cases are not generated (serializer-dependent). Per-operation and total
request caps, body ≤ 4 KB by default (hard limit 8 KB), concurrency 1, delay, timeout and cancellation are backend-enforced; mutation types
are interleaved so a capped run covers the most distinct mutations. Outcomes reuse safe fuzzing: Handled validation, Unexpected 5xx,
Unexpected acceptance, Contract violation, Potential information leak, Timeout, Connection failure, Safety blocked. The guard and the
authenticated execution service both require a server-built approval before any body is sent.

## Frontend Quality Review security

- **Static Security** (default on): anonymous HTTP review of the deployed frontend — exposed configuration, boot/debug assets, source
  maps, MSAL configuration, endpoint exposure, token-storage *text patterns* in fetched files (not runtime storage), expected security
  headers and a wildcard CORS header on the document.
- **Passive Security (OWASP ZAP)** is optional and off by default for new Target Environments. It runs only when the server enables it
  (`FrontendPassiveSecurity:Enabled` / `FrontendQualityEnginePreferences:PassiveSecurityEnabled`), the deployment allows it
  (`FrontendQualityCapabilities:PassiveSecurityAllowed`), the target is registered in `FrontendPassiveSecurity:TrustedProfiles` and the
  pinned image is available to the container runtime. Engine status reports an untrusted target as unavailable; the engine settings
  never offer a usable switch for an engine the server cannot run. Readiness allows up to 75 s for the ZAP JVM start. The real ZAP
  integration tests are reported as **skipped** unless `RUN_EXTERNAL_FRONTEND_QUALITY_TESTS=true`.
- Browser Runtime and Browser Quality collect no cookie, token or storage evidence; cookie metadata comes from Static Security and the Local HTTPS proxy only.

## OWASP references

References are attached to findings whose rule ids the engines actually emit (a test fails on any mapping key no engine emits). They
are always "Related" and say "does not establish OWASP compliance". The "OWASP ASVS / Top 10" Document Quality pack is documentation
keyword coverage, not a security audit.

## Not covered

Aggressive or dictionary fuzzing, GraphQL mutation fuzzing, state-changing body fuzzing, brute-force or password-spraying authentication
tests, account lockout and user enumeration, denial-of-service and rate-limit/load testing, automatic exploit confirmation, certificate
inspection, and full penetration testing. Real QA runtime acceptance of these checks needs an explicitly approved runtime target.
