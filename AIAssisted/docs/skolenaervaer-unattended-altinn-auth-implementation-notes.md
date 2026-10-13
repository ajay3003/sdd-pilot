# Skolenærvær unattended Altinn TT02 authentication — implementation notes

**Status:** this records the official contract the implementation follows. It is evidence, not design speculation.

- The documentation was re-read on 2026-10-13, independently of the earlier audit (`skolenaervaer-unattended-altinn-auth-audit.md`).
- No ID-porten, TestID or Altinn endpoint was called. Only public documentation pages were read.

## 1. Sources read

| # | Source | What it establishes |
|---|---|---|
| D1 | Digdir — *Automatisert testing med ID-porten*: https://docs.digdir.no/docs/idporten/oidc/oidc_func_apitesting.html | The headless login flow, the request and response examples, and the token properties |
| D2 | Digdir — *ID-porten testbrukere*: https://docs.digdir.no/docs/idporten/idporten/idporten_testbrukere.html | TestID, synthetic identifiers and headless use for automated API testing |
| D3 | Digdir — *Authorization endpoint*: https://docs.digdir.no/docs/idporten/oidc/oidc_protocol_authorize | PKCE, state, nonce, redirect_uri and response_type rules |
| D4 | Digdir — *well-known*: https://docs.digdir.no/docs/idporten/oidc/oidc_func_wellknown.html | The TEST issuer and discovery URL |
| A1 | Altinn — *App API authentication*: https://docs.altinn.studio/en/altinn-studio/v8/reference/api/auth/ | Actor classes, which actors may submit, and that an ID-porten token must be exchanged |
| A2 | Altinn — *ID-porten authentication*: https://docs.altinn.studio/en/authorization/getting-started/authentication/id-porten/ | The exchange input is the access token, and the lifetime equals the access token's |
| A3 | Altinn — *API scenarios: authentication*: https://docs.altinn.studio/nb/api/scenarios/authentication | `GET /authentication/api/v1/exchange/id-porten` with `Bearer <ID-porten access token>`; the user must have an Altinn profile |

## 2. Root decision

The earlier audit's root decision is **confirmed**. ID-porten documents a headless login for synthetic test users in its test environments, and it is explicitly meant to simplify testing of APIs protected by ID-porten access tokens (D1, D2). It is the ordinary authorization-code flow with a TestID `login_hint`. The resulting access token is exchanged at Altinn (A2, A3).

## 3. Verified contract

| # | Question | Verified answer | Source |
|---|---|---|---|
| 1 | Is headless login supported in ID-porten TEST? | Yes: "headless login" in the test environments | D1, D2 |
| 2 | Is it intended for API/system testing? | Yes. It is meant "to simplify testing of APIs protected by access_token from ID-porten". | D1 |
| 3 | Which identities can be used? | Synthetic TestID users only: synthetic identifiers with month + 80. TestID has no password. | D2, D1 |
| 4 | Authorization endpoint | `https://login.test.idporten.no/authorize` (GET) | D1 example |
| 5 | Token endpoint | `https://test.idporten.no/token` (POST) | D1 example |
| 6 | Client type | A pre-registered ID-porten TEST client. The example authenticates as a confidential client with `client_secret_basic`. | D1, D3 |
| 7 | Grant | `authorization_code` | D1 |
| 8 | PKCE | Mandatory. Only `S256` is supported (`code_challenge` and `code_challenge_method=S256`; `code_verifier` in the token request). | D3, D1 |
| 9 | `login_hint` syntax | `testid:<synthetic person identifier>_<security level>`, e.g. `testid:12345678901_idporten-loa-high` | D1 |
| 10 | TestID selection | Only through `login_hint` (`testid:` prefix). There is no form, password or UI interaction. | D1 |
| 11 | `acr_values` | Optional per D3. The D1 example sends `acr_values=idporten-loa-substantial` and a matching level suffix in `login_hint`. BirkNext sends both and requires them to match. | D1, D3 |
| 12 | Scopes | Requested scopes are consented implicitly (`openid profile <api scope>`). Altinn needs the scopes registered for the client; BirkNext uses the scopes configured in Integration Quality Review, which must include `openid`. | D1 |
| 13 | Redirect behaviour | ID-porten answers `HTTP 302`, and the `Location` header carries `code`, `iss` (`https://test.idporten.no`) and `state`. The client reads the header and does not follow it. | D1 |
| 14 | Must the redirect be https? | It must be pre-registered. D3 says localhost is allowed only in test environments. Because the redirect is never followed, BirkNext requires an absolute https URI or a loopback http URI that exactly matches the registered one. | D3 |
| 15 | Can a backend receive the code without a browser? | Yes. It is read from the `Location` header of the backend's own `/authorize` response, so no callback endpoint is involved. | D1 |
| 16 | Is `state` required? | D3 says state is mandatory for public clients and recommended otherwise, and D1 says the client must verify it. BirkNext always sends it and requires an exact match. | D1, D3 |
| 17 | Is `nonce` required? | Recommended (D3) and present in the D1 example. BirkNext always sends it and checks it against the `id_token`. | D1, D3 |
| 18 | Secret for confidential clients | D1 uses `client_secret_basic` (an `Authorization: Basic` header). BirkNext uses exactly that method, and only when discovery advertises it. Private-key client authentication is not part of the documented headless example and is not implemented. | D1 |
| 19 | Issuer / audience | The TEST issuer is `https://test.idporten.no` (D4, and the D1 `iss` parameter). The `id_token` `aud` is the client id. D1 says the `id_token` carries `amr=TestID`. | D1, D4 |
| 20 | Altinn TT02 exchange endpoint | `GET https://platform.tt02.altinn.no/authentication/api/v1/exchange/id-porten`. The path comes from A3; the host is the TT02 platform base in A3. | A3 |
| 21 | Exchange input | The ID-porten **access token** ("NOT ID TOKEN"), sent as `Authorization: Bearer` | A2, A3 |
| 22 | Output for the App API | An Altinn JWT. App APIs accept the exchanged Altinn token as a bearer token. | A1, A3 |
| 23 | Output for the Storage API | The same Altinn JWT, used against the TT02 platform Storage API | A3 |
| 24 | Token lifetime | The ID-porten example has `expires_in: 120` seconds. The Altinn token has the same lifespan as the access token. | D1, A2 |
| 25 | Refresh tokens | ID-porten returns a refresh token (`refresh_token_expires_in: 600` in the example). BirkNext neither uses nor stores it; a run acquires a new token instead. | D1, A2 |
| 26 | Warnings and restrictions | Test environments only, for API testing, synthetic users. A3: "the user must have a profile in Altinn" (an exchange can fail for a person who never logged in to Altinn TT02). | D1, D2, A3 |
| 27 | Test-only? | Yes | D1, D2 |

**Discovery.** BirkNext reads the authorization and token endpoints from `https://test.idporten.no/.well-known/openid-configuration` (D4). It requires the issuer `https://test.idporten.no` and allowlisted hosts (`login.test.idporten.no`, `test.idporten.no`), so a discovery answer cannot redirect BirkNext anywhere else. This is the same pinned reader the interactive flow already uses.

## 4. Actor semantics (A1)

- An ID-porten person exchanged to an Altinn token is an end user. That is the right actor for a reporter.
- Organisations (Maskinporten) and service owners are **not valid submitters**. System users need app library 8.6.0 or later and an app policy that allows them.
- "There is currently no built-in configuration to restrict access based on authentication methods in an app." The app's XACML policy decides what the person may do.

## 5. App authorization policy for `buf/skolenaervaer` (not confirmed)

- **What was searched:**
  - every `policy.xml` and `*xacml*` file under `sdd-repos` (depth 8);
  - all local Skolenærvær archives in Downloads;
  - every Source Analysis snapshot in the main database (no Altinn app source; `altinnApps` is empty).
- **No public policy endpoint.** No public TT02 metadata endpoint exposes the app's XACML. The contract reader verifies schema and tasks only.
- **Unknown, therefore:** who may instantiate, write or complete; required roles; the required authentication level; whether the TestID reporter may act for an institution; and whether reporter and instance owner coincide.
- **Consequence:** the backend shows **"App authorization policy: Not confirmed"**, and real submission scenarios stay Not ready. The service owner must record a confirmation (backend `AltinnTesting:ConfirmedAppPolicies`, with reference, confirmer and date) before the first supervised real run.

## 6. Secret material

| Item | Where it lives | Notes |
|---|---|---|
| ID-porten TEST client id | Integration Quality Review → Altinn test configuration → Client reference | Not secret |
| Registered redirect URI | Backend `AltinnTesting:UnattendedIdPortenTest:RedirectUri` (falls back to `IdPortenTest:RedirectUri`) | Never contacted |
| Client secret | Backend `AltinnTesting:UnattendedIdPortenTest:ClientSecret`, set from an environment variable or a managed secret store, never in a committed file. Falls back to `IdPortenTest:ClientSecret`. | Used only in the `Authorization: Basic` header |
| Synthetic reporter identifier | Backend `AltinnTesting:UnattendedIdPortenTest:Reporters[].TestIdPersonIdentifier`, configured outside committed files | Used only in `login_hint`. Never logged, persisted, returned or shown; the UI and history see the alias only. |

## 7. Implementation boundaries

- **No token persistence.** No ID-porten token, code, verifier, `login_hint` or Altinn token is written to a log, database, history, export or response.
- **Caching.** The Altinn token is held in process memory keyed by `(environment, reporter alias)`, bounded by `expires_in`, with a 30-minute cap.
- **Requests.** `/authorize` is sent with automatic redirects disabled (the existing named client). The `Location` must match the configured redirect URI exactly (scheme, host, port and path). `state` must match, and `iss`, when present, must be `https://test.idporten.no`.
- **id_token checks.** BirkNext requires an `id_token`; it does not verify the signature. It checks `iss`, `aud` (the client id), `nonce`, `amr` containing `TestID`, and `pid` equal to the configured reporter.
- **Production.** All hosts pass the existing `AltinnTestingRequestGuard`, and Production hosts are refused by name.

## 8. Semantics preserved

- BirkNext execution authorization ≠ Altinn actor authentication.
- BirkNext execution identity ≠ synthetic TestID reporter.
- Authenticated ≠ submitted ≠ MU received ≠ MM processed ≠ report stored ≠ period closed ≠ Bufdata processed.
- Storage instance observed ≠ submission.
- Altinn `blobStoragePath` ≠ MU blob.

## 9. Implementation status (2026-10-13, uncommitted)

**Implemented, mock-tested:**
- `IAltinnTestAuthenticationProvider` with `UnattendedIdPortenTestAuthentication` (headless TestID) and `InteractiveIdPortenTestAuthentication` (fallback); `AltinnTestAuthenticator` used by both the submission executor and the read-only Storage observer (one auth path).
- Reporter alias input on the Rapportmottak scenarios (aliases from backend config, interactive fallback last); identifiers never leave the backend.
- Readiness split: Reporter authentication, Unattended provider, ID-porten TEST client, Client secret, Synthetic reporter, Interactive fallback (optional), **App authorization policy** (mandatory for submissions; re-checked by the executor).
- In-memory token cache keyed by (environment, reporter alias); single-flight per key; `expires_in`-bounded.
- Journey history: `InitiatedBy` persisted only in derived safe form (`local-session:…` / `authenticated-user:<hash>`).
- UI: unattended readiness rows on Skolenærvær Testing → Rapportmottak; interactive TestID sign-in moved to a collapsed fallback.

**BirkNext execution authorization (approved and implemented):** `ActiveEventExecute` is a capability. `ActiveEventPermissionHandler` succeeds for
an authenticated principal holding the permission, or for `LocalTrustedExecution` — backend setting `Authorization:LocalTrustedExecution`
(true only in `appsettings.Development.json`) + Development environment + loopback socket peer on both ends + Host `localhost`/loopback IP +
an Origin header exactly equal to the configured `FRONTEND_ORIGIN`, which must itself be a well-formed loopback http(s) origin. No header
grants it, X-Forwarded-For is never consulted (no forwarded-headers middleware), and it adds no claims. The policy no longer has
`RequireAuthenticatedUser()` because the handler enforces both paths; anonymous requests outside local trust still get 401.
Execution actor: `local-session:loopback` or `authenticated-user:<16 hex of SHA-256(iss|oid/sub)>`, persisted as `IntegrationJourneyRun.InitiatedBy`.
`GET /api/execution-access` returns `{mode, canExecute, detail}` only; the frontend uses it to enable Run and the backend re-checks every run.

## 10. First supervised TT02 run — preparation audit (2026-10-13)

The only external requests were three anonymous, read-only GETs: no token, cookie or identity; no instance, Storage, process or ID-porten call.

### 10.1 App authorization policy — FOUND (decision A, compatible for the person/PRIV path)

| Fact | Value | Basis |
|---|---|---|
| Source | `GET https://platform.tt02.altinn.no/resourceregistry/api/v1/resource/app_buf_skolenaervaer/policy` (HTTP 200, `text/xml`, sha256 `f8f418aa794af4f79403f578c5d29356b3b92fdb1d40bd077f722e511ee2b370`) | Read 2026-10-13. Altinn Resource Registry `ResourceController.GetPolicy`: no `[Authorize]`, no fallback policy (official source) |
| Rule 1 (end user) | Roles `PRIV` (Privatperson) or `DAGL` (Daglig leder) → actions `read`, `write`, `delete`, `instantiate` on `buf/skolenaervaer` | Proven (policy.xml) |
| Rule 2 (service owner) | org `buf` → `read`, `write`, `instantiate`, `complete` (confirm received) | Proven (policy.xml) |
| Minimum authentication level | 1 for persons; 3 for organisation tokens | Proven (policy obligations) |
| process/next on a data task | authorized by `write` (`ProcessEngineAuthorizer.GetActionsThatAllowProcessNextForTaskType`: Data → `["write"]`) | Proven (Altinn app-lib source) |
| `complete` | Service owner's "confirm received", not needed for a submission | Proven |
| Party types allowed | person, organisation, subUnit, bankruptcyEstate; `disallowUserInstantiation: false`; app lib 8.12.7 | Proven (public applicationmetadata) |
| Child identifier | Form data only; the policy has no attribute on form content | Proven |
| Reporter = instance owner | Yes in BirkNext: the instance is created for the reporter's current party (`parties/current`), so the actor is the person for their own party (PRIV) | Proven (code) |
| Acting for an organisation (DAGL) | Allowed by the policy, but NOT what the current run does; design intent DEC-52 says institutions typically report as organisations | Policy proven; out of scope for the first run |

**Ready-to-apply configuration (not committed).** The automated reviewer blocked committing a confirmation that opens the real-execution gate, so the service owner or operator decides whether to apply it (backend configuration, e.g. `appsettings.Development.json` or environment variables):

```json
"ConfirmedAppPolicies": [
  {
    "AppId": "buf/skolenaervaer",
    "Reference": "TT02 Resource Registry GET resourceregistry/api/v1/resource/app_buf_skolenaervaer/policy (anonymous, read-only)",
    "ConfirmedBy": "BirkNext audit of the deployed TT02 app policy",
    "ConfirmedOn": "2026-10-13",
    "PolicySource": "https://platform.tt02.altinn.no/resourceregistry/api/v1/resource/app_buf_skolenaervaer/policy",
    "PolicySha256": "f8f418aa794af4f79403f578c5d29356b3b92fdb1d40bd077f722e511ee2b370",
    "EndUserRoles": [ "PRIV", "DAGL" ],
    "EndUserActions": [ "read", "write", "delete", "instantiate" ],
    "MinimumAuthenticationLevel": 1
  }
]
```

### 10.2 Remaining first-run facts

- **App source:** `buf/skolenaervaer` is not in any Source Analysis snapshot. `Tiltak 2` is the Meldingsutveksler (MU) source, which consumes this app as service owner; it is not the app's policy.
- **Process contract:** `process.bpmn` is not published. Only public metadata shows the form data type on `Task_1`. The contract therefore stays incomplete, the submission prerequisite stays Not ready, and the executor never calls process/next. This is the blocking item (decision D below).
- **Meldingshode:** the real schema requires `Id`, `MeldingstypeNmsp`, `FagsystemAvsender` (`Leverandor`, `Navn`, `Versjon`), `SendtTidspunkt`, `Avsender` and `Mottaker`. BirkNext fills none of them. Because form users never enter them, the app is expected to fill them, but that is unproven. Altinn validation in the first live run is the first evidence.
- **Reporter:** a configured synthetic TestID person with `AltinnRole: PRIV` and `AltinnProfileConfirmed: true` (Altinn's exchange requires an Altinn TT02 profile). The identifier is kept outside committed files.
- **Client:** ID-porten TEST client id and `openid` plus the Altinn instance scopes come from Integration Quality Review. The registered redirect URI and client secret come from backend config or a secret store.

### 10.3 Dry run

`POST /api/integration-journeys/runs/validate` (UI: "Validate run (no external calls)") checks:

- scenario, inputs, trusted environment and every mandatory prerequisite;
- reporter resolution and unattended configuration (secret presence only);
- Production safety and the endpoint allowlist;
- app policy against the reporter's role and the contract;
- the typed form and its XML (size only);
- unfilled required fields (a risk, not a blocker);
- the process plan.

It makes no network call, holds no token and records no history. Its preview is redacted (masked child reference, no XML).

### 10.4 First supervised run (separate, explicit step)

1. Skolenærvær Testing → Rapportmottak → E2E-01.
2. Choose the approved reporter alias and an approved registered synthetic child.
3. Enter the period and whole hours.
4. Validate the run until it shows **Valid — not executed**.
5. Confirm the real external call.
6. The backend then performs, in order:
   - unattended ID-porten TEST authentication and the Altinn exchange;
   - create instance, upload XML, validate;
   - process/next (only with a complete process contract);
   - read the receipt.
7. Optionally read the Storage evidence with the same token.
8. Stop. One attempt; nothing is retried. MU, MM, report and Bufdata are observed later.
