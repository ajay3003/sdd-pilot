# Security testing in BirkNext: what runs, where, and what it proves

BirkNext's security checks are **bounded QA/security checks**. They are not penetration testing, vulnerability exploitation or
OWASP compliance. This page describes what each review actually executes and where the evidence comes from.

## Capability matrix

| Area | State | Where | Evidence |
|---|---|---|---|
| Security headers | Runnable with expectations | FQR Static Security (frontend document), AQR (API responses) | Runtime response headers vs Target Environment → Security Expectations (presence only) |
| CORS | Partially runnable | AQR (OPTIONS preflight), FQR (wildcard on the document) | Runtime; own frontend origin only — foreign/reflected origins not tested |
| Cookies (Secure/HttpOnly/SameSite) | Not implemented | — | Set-Cookie is deliberately not captured; ZAP passive rules may flag cookies only if ZAP runs |
| Swagger/OpenAPI exposure | Not implemented | — | AQR reviews the configured contract's quality; it does not probe for exposed documents |
| GraphQL introspection | Runnable | AQR | Runtime `__schema` query; policy warning in production-like environments |
| Error leakage | Runnable | AQR (unknown route / unknown field probes outside production, every response), safe fuzzing | Indicator names only (stack trace, exception, SQL, path, internal host, assembly, framework page, token/secret) |
| Authentication enforcement | Runnable | AQR | Deliberate anonymous request per authenticated target |
| Authorization | Partial, M2LB-specific only | Security Classification live probe | General role/identity allow-deny testing is future work |
| OWASP | Reporting metadata | AQR/FQR findings | "Related" references on emitted finding rule ids; never a finding generator |
| Passive Security (ZAP) | Optional, truthful readiness | FQR | Needs server enablement, a trusted target profile and a local container image |
| Safe fuzzing | Runnable (bounded, read-only, contract-derived) | AQR → Safe fuzzing | Runtime responses to synthetic invalid/boundary input |
| Dependency vulnerabilities | Runnable (separate) | Dependency Health (OSV + nuget.org) | Package metadata; fuzzing does not replace it |

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
- **Methods:** REST GET/HEAD/OPTIONS, GraphQL queries (inline literals, no variables). Write methods, request bodies, GraphQL mutations
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
- Browser Runtime and Browser Quality collect no cookie, token or storage evidence.

## OWASP references

References are attached to findings whose rule ids the engines actually emit (a test fails on any mapping key no engine emits). They
are always "Related" and say "does not establish OWASP compliance". The "OWASP ASVS / Top 10" Document Quality pack is documentation
keyword coverage, not a security audit.

## Not covered

Penetration testing, authorization matrices between roles, request-body and write-method fuzzing, mutation testing, rate-limit and load
testing, cookie attributes, Swagger exposure probing, certificate inspection, and foreign-origin CORS reflection.
