# Skolenærvær unattended Altinn TT02 authentication audit

**Audit only.** No code was changed. No browser automation, Companion, or Dedicated Edge was used. No real Altinn request or ID-porten sign-in was performed. The public documentation checks below were read-only.

## 1. Executive summary

**A supported unattended person-authentication path exists for API testing in ID-porten's TEST environment.** Digdir documents “headless login” for synthetic TestID users: submit a normal OAuth authorization-code request with a TestID `login_hint`; ID-porten automatically authenticates the named synthetic person and grants the requested test scopes without user interaction; the client handles the redirect authorization code and completes the ordinary token request. The issued token is a full ID-porten TEST token and identifies a person (`amr=TestID`). This is explicitly intended to simplify automated API testing, not as a Production login method.

For Altinn TT02 App API calls, exchange the resulting ID-porten **access token** for an Altinn token at the TT02 Authentication API, then use that Altinn token for the app API and Storage reads. BirkNext already implements this exchange and a typed create/upload/validate/advance/read sequence. The missing pieces are a headless TestID token acquisition provider, safe configured synthetic identity aliases, and readiness/configuration for that provider. This is not client credentials, password grant, or machine identity. **It does not need a browser, manual TestID interaction, Companion, or Edge.**

There are two material qualifications:

1. The Altinn API identity path is available, but BirkNext's current Integration Journey endpoints themselves require `ActiveEventExecute`, backed by a JWT bearer scheme configured specifically for Entra. The general product can run without Entra, but protected journey execution is disabled without it. This is a BirkNext authorization decision separate from Altinn authentication; an unattended Altinn provider alone does not remove that current gate. To meet the requested BirkNext UX without introducing Entra as a product prerequisite, implementation must deliberately supply a safe local execution authorization mode (or an already-supported non-Entra principal). Simply removing `[Authorize]` would be unsafe.
2. No `buf/skolenaervaer` policy/XACML or app repository is available in this workspace. The app contract reader verifies form schema/tasks, not authorization policy. Person/TestID semantics match the intended reporter model, but required roles, authentication level, instance-owner constraints and whether this exact app accepts the named synthetic reporter cannot be asserted from current source. The first live run must be supervised after the owner confirms the test identity and policy.

**Current exact product state:** BirkNext is not generally Entra-only, but the existing Active Event/Integration Journey execution and history route is Entra-protected. Current Altinn authentication is interactive, and its caller token is keyed to that BirkNext caller. Headless TestID is a supported Altinn-side option that can be added without adding an Entra dependency to the Altinn identity flow, but the BirkNext API gate also needs a no-Entra-compatible design for the requested deployment.

## 2. Current BirkNext authentication and authorization model

The browser app reads optional `Authentication:Entra` configuration. When tenant, client and Active Event API scope are present, it registers MSAL public-client auth-code-with-PKCE and attaches bearer tokens to protected calls. Without those values, the application still starts and general review features remain available; execution UI reports authentication not configured. This confirms Entra is not BirkNext's universal product login model.

The backend API registers only `JwtBearerDefaults.AuthenticationScheme`, with `Authority` and `Audience` read from `Authentication:Entra`. `ActiveEventExecute` requires an authenticated principal and a claim named `scp`, `roles`, or `permission` containing the permission. At the claims-handler level the permission is abstract; at the actual configured authentication boundary it is an Entra JWT because the only registered validator has Entra authority/audience. There is no local execution bypass or alternate local principal. `Authorization:LocalConfigurationWrites` grants only loopback Development integration-configuration writes; it does not grant ActiveEventExecute.

Integration Journey start, history and run detail are under `[Authorize(Policy = ActiveEventExecute)]`. `CallerId` extracts `oid`, `sub`, or NameIdentifier and passes it transiently as the run initiator. The serialized `IntegrationJourneyRun` does not persist an initiator field; history is scoped by environment and journey filters. Altinn's current token cache is keyed by `(initiator, environmentId)`, so current execution requires a stable authenticated BirkNext caller as well as a separate TestID sign-in.

Answers to the authorization questions:

1. **Does BirkNext require Entra to run Integration Journeys today?** Yes, for the current API endpoints: absent a valid JWT from the configured Entra authority/audience carrying `ActiveEventExecute`, run/history calls fail. General BirkNext use does not require Entra.
2. **Is Entra optional/configurable?** Yes for the general app and frontend auth setup; no for these protected execution endpoints under current backend registration.
3. **Is ActiveEventExecute tied to Entra or abstract?** Abstract claim check in `BirkNextPermissions.Holds`; operationally Entra-only because JWT bearer is the only configured scheme.
4. **Can BirkNext run locally without Entra today?** General UI/reviews can. Integration Journey/Active Event execution and history cannot. There is no local execution exception.
5. **How are history and initiator represented?** One durable `IntegrationJourneyRuns` record per run; initiator is passed to transient execution context for token/cache association but omitted from durable `IntegrationJourneyRun`. Thus no Entra subject is persisted in run history today.
6. **Did Altinn introduce an Entra prerequisite?** It reused the existing ActiveEventExecute-protected journey route and added sign-in endpoints under the same policy. It did not introduce Entra into ID-porten/Altinn token exchange. Its use of `CallerId` to key the interactive Altinn token cache makes the current Altinn sign-in UX dependent on the existing Entra caller.
7. **Can that be removed generically?** Not by deleting authorization. A local single-user execution policy would need an explicit backend-owned mode, loopback/host protection, CSRF/origin considerations, request authorization, and a stable run owner independent of caller-supplied headers. Alternatively, add a supported non-Entra auth scheme. Keep the shared journey runner and use one authorization policy; do not create an Altinn-only bypass.

## 3. Desired unattended flow and current Altinn implementation

Desired UI behavior is one confirmed typed run: choose an approved reporter alias, a known or intentionally unknown synthetic child alias as required by the scenario, reporting period and hours; click Run; backend acquires the TEST person token, exchanges it, then performs the registered app's bounded API sequence. No auth popup or token is shown.

Current API and journey flow already models separate steps for authentication, instance creation, typed data storage, app validation, process advancement and receipt/read-back. The submission executor resolves the authenticated party using the current token and creates the instance owned by that party; the child identifier is a separate form field. It does not let the caller supply an arbitrary endpoint, app id, raw XML body, or instance-owner id. Safe results are recorded by the shared Integration Journey runner/history. Downstream MU/MM/report/Bufdata steps remain unassessed without providers.

The existing `IdPortenTestSignIn` is an interactive authorization-code + PKCE provider: it redirects the browser, accepts GET/form-post callbacks, exchanges the ID-porten code server-side, then exchanges the ID-porten access token at Altinn TT02. It intentionally sends no `login_hint` and its tests assert no `login_hint`; the source comments say ID-porten documents no TestID hint. Current official docs now explicitly document a TestID hint for automated API testing. Those tests/comments need reevaluation when implementing the separately scoped headless test path; do not loosen the ordinary interactive flow unintentionally.

`IAltinnTestTokens` is only a token retrieval abstraction, not an authentication-provider interface. `AltinnTestSessionCache` is a process-memory token store keyed by Entra initiator and environment. A future headless provider should plug into the current Altinn execution path and shared `IntegrationJourneyRunner`, not create a second runner. Bind token issuance to a run and a backend-configured synthetic reporter alias rather than caller-provided identity data.

## 4. ID-porten TEST, TestID and headless-login findings

Digdir's current ID-porten test-user guide recommends TestID, identifies synthetic person identifiers (the test identifier convention) and says synthetic TestID users can be used for headless API testing. The automated-testing guide is explicit: in test environments, `login_hint` plus requested scopes yields automatic TestID authentication and implicit test-scope consent without user interaction; ID-porten returns an authorization code by redirect, which the client validates and exchanges through the ordinary token endpoint. The resulting ID token has `amr=TestID`, and the tokens are full tokens that can be introspected, refreshed or revoked.

The documented request is still OAuth authorization code (with normal `state`, `nonce`, `redirect_uri`, client registration and PKCE where the client requires it). The client reads the `Location` on ID-porten's authorization response; no browser is needed for this documented direct HTTP API-testing pattern. The documented sample uses `client_secret_basic` at `/token`; use the exact client authentication method registered/advertised for the BirkNext test client. The headless guide is for test API testing, not Production, and it is not a password grant or a general user-impersonation facility.

**TestID-specific answers:**

- Can it be completed non-interactively? **Yes**, for synthetic TestID users in ID-porten test environments, via documented headless API testing.
- Is there a documented way to select a synthetic person? **Yes**: `login_hint=testid:<synthetic-person-identifier>_<requested-acr>`, e.g. the documented shape with `idporten-loa-high`.
- Is `login_hint` supported? **Yes for this documented test-only headless API flow.** It should not be generalized into the ordinary Production login flow.
- Direct token endpoint without authorization? **No.** The documented flow first obtains a code from `/authorize`, then uses the standard `/token` authorization-code exchange.
- Username/password? **No** for TestID: its user guide says TestID has no password. No credentials should be automated.
- ROPC/password grant? **Not documented or appropriate.** Use the documented authorization-code headless route, not password grant.
- Test-only automation bypass? **Yes, specifically the documented headless API-testing flow in ID-porten test environments.** This is not a LocalTest helper and not a Production bypass.
- Does the path require browser interaction? **No browser is needed for the documented headless API test request/redirect/token-exchange sequence.** It is a server-side HTTP OIDC client flow.

The backend should request the required security level explicitly, keep `login_hint` aligned with that level, validate issuer/state/redirect/code binding, apply bounded HTTP timeouts and no redirects outside exact ID-porten TEST hosts, then exchange only the access token with TT02. `login_hint` contains a synthetic person identifier and must never appear in logs, traces, run history, errors or frontend responses.

## 5. Altinn token exchange and accepted actor classes

Altinn docs state that an ID-porten-authenticated end user can exchange the ID-porten **access token** for an Altinn Platform JWT; the ID token is not the exchange input. The resulting Altinn token is used against Altinn Apps/Platform APIs. Current BirkNext code already sends the ID-porten access token as bearer to `https://platform.tt02.altinn.no/authentication/api/v1/exchange/id-porten`, holds the returned token only in process memory, then uses it on the App API and Storage API.

Altinn distinguishes these actor types:

| Candidate | Actor and authentication | Fit for person reporter test? |
|---|---|---|
| ID-porten TestID headless | Synthetic **person**; test-only automatic OIDC auth-code flow; exchanged to Altinn token | **Supported and semantically appropriate** for person/TestID reporter testing |
| Maskinporten organization | Organization/client authenticated with signed JWT grant and certificate/asymmetric key; exchange to Altinn token | **Wrong actor** if the test is a natural person reporter; Altinn docs say organizations are not valid submitters on their own |
| Maskinporten service owner | App/service-owner organization with `altinn:serviceowner`; may start/modify instances subject to policy, but not a valid submitter | **Wrong actor** for validating reporter behavior; could test owner/admin operations only |
| Maskinporten system user | Virtual user representing an organization/customer with delegated system rights; exchanged Altinn token for Apps API where supported | **Wrong actor** for a synthetic individual reporter. It represents a system acting for an organization, not a TestID person |
| Altinn 2 enterprise user | Classified as Organization in current Altinn 3 auth docs; only partial support | **Wrong actor/uncertain** and not a substitute for TestID person |
| LocalTest user token | Local simulated identity/token issued by the local platform | **Local-only**; not TT02 and cannot establish a TT02 TestID person session |

Altinn's generic API docs group app API capabilities for application users and owners, but endpoint authorization remains scoped by the actual user/party and app XACML policy. The fact that a token exchange works does not establish that the reporter can create this specific app instance.

## 6. Maskinporten and system-user findings

Maskinporten supports machine-to-machine client authentication: a registered client signs a short-lived JWT grant using a business certificate or pre-registered asymmetric key, then exchanges it for a scoped access token. For Altinn apps, an organization token must be exchanged for an Altinn token. This identifies an organization or system, not a synthetic TestID person.

Altinn documents system users as a virtual user created for an organization and system, with delegated organizational rights. Operations are attributed to that system user and cannot be linked to the specific person performing an action. App-side system-user support requires app libraries `Altinn.App.Api` and `Altinn.App.Core` v8.6.0 or later, and app authorization must allow it. The target app version/policy is not available here. Even if accepted, this would change actor semantics and would not prove person-reporter behavior.

Altinn docs specifically state a service-owner actor is not a valid submitter, although policy may permit instance creation/modification. An organization actor likewise is not a valid submitter on its own. A service-owner or organization credential could exercise a different API-owner pathway; it must not be presented as unattended TestID submission.

**Maskinporten decision:** not suitable for the required person actor. It requires a client id, registered scope, key/certificate (not a client secret), and signer; it does not authenticate as the required synthetic person. **System-user decision:** possible for a different organizational actor model only if the app supports it, so reject for the desired reporter-semantic test.

## 7. LocalTest versus TT02

Altinn LocalTest documentation describes helper endpoints such as `/Home/GetTestUserToken`, `/Home/GetTestSystemUserToken`, `/Home/GetTestOrgToken/{org}` and `/Home/Tokens` on `http://local.altinn.cloud`. These issue local-platform test identities for apps running with LocalTest. LocalTest also supplies predefined test identities and simulated organization/system users.

They are not TT02 token endpoints. TT02 uses its actual test hosts, ID-porten TEST for person authentication, and Altinn's TT02 token exchange. **LocalTest token != TT02 token. LocalTest unattended auth != TT02 unattended auth.** Do not direct LocalTest helper calls to TT02 or infer TT02 permissions from LocalTest success.

LocalTest is useful for fast automated contract, XML, instance lifecycle and failure-path tests with seeded synthetic identities. It does not replace supervised TT02 acceptance with a known TestID reporter, nor does it prove the public TT02 app's policy or downstream integrations.

## 8. `buf/skolenaervaer` authorization requirements and identity semantics

The configured/approved app id is `buf/skolenaervaer`; existing backend policy pins it and builds the TT02 app base. Current App API code resolves the authenticated person's current party, creates an instance for that party, writes typed form data, validates, advances process tasks and reads the instance. It does not accept a caller-selected owner party. The form's child identifier is a distinct value from the reporter identity.

The current workspace contains no target app policy XML/XACML, app repository, verified authorization rules, concrete reporter role map or authoritative identity fixture. `AltinnAppContractService` inspects JSON schema/application metadata and process tasks, not roles, auth level or XACML. Therefore:

- Intended reporter is a person (the documented journey says external reporter, institution/foster-home test actor authenticated with TestID). The machine alternative would change semantics.
- Whether an institution can submit as an organization, whether a particular Altinn role is required, the minimum authentication level, and whether the selected person can represent an institution are **not proven**.
- Whether the app allows system user/service-owner actors is **not proven**; service-owner/organization are not valid submitters by default semantics, and system-user app support needs an app version/policy check.
- The actual instance owner should remain the party resolved by the authenticated reporter's Altinn token. Do not add an arbitrary instance-owner field to UI or backend inputs.
- The child identifier remains the report subject. Reporter PID != child PID. Valid syntax != registered Test-Folkeregister/Altinn person. Registered reporter != registered child.

Before implementation, the service owner must confirm the TT02 policy/app rules for an approved synthetic reporter, required `acr_values`, party representation rules and intended instance owner. Do not invent a role or auth level.

## 9. Existing Altinn architecture fit and candidate comparison

There is no `IAltinnTestAuthenticationProvider` today. There is `IAltinnTestTokens`, `AltinnTestSessionCache`, an interactive `IdPortenTestSignIn`, `AltinnTestingPolicy`, `AltinnStorageInstanceReader`, the App API client, and one journey executor/observer. The clean fit is one authentication abstraction/provider producing a short-lived Altinn token from a configured reporter alias; interactive and headless implementations may share the cache/token contract, while the single executor continues to run the same Altinn steps. Do not build a second Altinn runner.

| Option | Classification | Result |
|---|---|---|
| A. Current interactive ID-porten TEST | **Supported** | Correct person semantics; rejected for the requested no-click UX; retain as optional attended fallback only if desired |
| B. Documented headless ID-porten/TestID test flow | **Supported** | Correct synthetic person semantics; direct HTTP auth-code test flow; best match for unattended TT02 API testing |
| C. Maskinporten machine auth | **Possible but wrong semantics** | Organization/service/system identity, not TestID person; requires client certificate/asymmetric key and scopes |
| D. Altinn system user | **Possible but wrong semantics / target support unknown** | Organizational virtual actor; app support and XACML unknown; does not test person reporter behavior |
| E. TT02 test-token helper | **Unsupported / not found** | No documented TT02 endpoint for minting arbitrary synthetic-person tokens found; supported method is ID-porten headless OIDC |
| F. LocalTest token | **Supported locally, not TT02** | Good for local test automation only; never a TT02 credential |
| G. Browser automation | **Reject for this goal** | Unneeded; official server-side headless API test flow exists |
| H. Companion/Dedicated Edge | **Reject for this goal** | Unneeded and not an auth provider |

## 10. Synthetic identity strategy

Use a backend-configured **identity set of aliases**, not free-form PID inputs or generated national-number-shaped values:

- Reporter alias maps to a known synthetic TestID person identifier and declared required auth level. Only approved aliases appear in UI. The backend uses this PID only in `login_hint` and does not write it to journey history.
- Child alias maps to a separate synthetic child identifier approved for this scenario. Known-child scenarios require an owner-confirmed identity known to the Person downstream service; unknown-child scenarios use a separately confirmed identity known to be absent there. A syntactically valid generated number is not evidence of either property.
- Keep configured reporter identity and child identity separate. Do not expose IDs, token, claims or TestID auth details in UI or logs. Store only alias/reference and masked evidence in history.
- Treat synthetic person identifiers as sensitive test data. Keep them backend-side in environment-specific secure configuration/secret storage, never in frontend appsettings, general message-flow JSON or run inputs. Ensure they are synthetic test identities, not real PII.
- The existing `ReporterReference` and `SyntheticIdentitySetReference` are descriptive references/readiness flags; they do not contain/resolve actual identity credentials or identity set data today.

## 11. Secret and credential model

For the recommended ID-porten headless path, exact material is: registered ID-porten TEST client id; registered callback URI; allowed scopes; client authentication method; and, if confidential registration uses it, the ID-porten TEST client secret. The synthetic TestID person identifier is test identity data used in `login_hint`, not a password. There is no TestID password. The backend obtains ID-porten code/tokens and the Altinn token transiently.

Store a confidential client secret in backend user-secrets for local development or a managed secret store in deployed environments. Never put it in SPA config, IQR message flow, scenario input, logs, exceptions or history. Support rotation and bind each credential set to TT02 only. If the registered client is public/no-secret, use only the registered public-client + PKCE method advertised by ID-porten; do not silently fall back between authentication methods. Current code supports `none` or `client_secret_post`; the official headless sample uses `client_secret_basic`. The actual registered method must be confirmed and code aligned before use.

If Maskinporten were evaluated for a separate org/system test, it requires a registered client id, permitted scope and private signing key/business certificate (or registered asymmetric key), with rotation. It still cannot substitute for the person test. No Maskinporten secret/key should be introduced for the current reporter flow.

## 12. Readiness model

Report these as distinct checks:

1. TT02 selected and Production blocked by backend policy.
2. Exact approved app id `buf/skolenaervaer`.
3. App contract and process tasks complete/verified.
4. Headless ID-porten TEST provider supported by this deployment.
5. Provider configuration present: registered client id, callback, scopes, valid client auth method, secure secret if required.
6. Named synthetic reporter alias configured and approved for this test; required auth level known.
7. Synthetic identity set configured; child alias matches scenario (known/unknown by explicit owner confirmation).
8. App authorization policy/role/party representation and instance-owner semantics confirmed compatible.
9. Request input is valid and typed; reporting period and hours satisfy contract.
10. Backend secret available and current; no token is yet implied.
11. Authenticated: headless login plus Altinn token exchange succeeds. This remains distinct from submission.
12. Downstream evidence providers and correlation contract available for any claimed MU/MM/report/Bufdata verification.

Provider supported != configured; configured != authenticated; authenticated != submitted. A ready Altinn section must not imply downstream readiness.

## 13. Security model for a future implementation

Keep the existing server-side controls and add:

1. TT02 only; exact allowed ID-porten TEST/Altinn TT02 hosts; hard server-side Production denial regardless of UI/environment input.
2. Exact app allowlist `buf/skolenaervaer`; no arbitrary URLs or arbitrary app identifiers from requests.
3. Typed Skolenærvær inputs only; no raw XML, free-form payload or user-supplied party id/instance owner.
4. Reporter and child selected by approved synthetic aliases. Never support arbitrary actor impersonation or identity enumeration.
5. Client secret/private material backend-only, environment-specific, rotatable; no frontend, history or logs.
6. No token persistence; short-lived process-memory token handling, bounded lifetime, zeroing where feasible and no token in errors/telemetry.
7. State/nonce/PKCE and callback binding preserved; headless redirect parser validates exact registered redirect, issuer and state before code exchange.
8. Bounded authorization request count, one token acquisition per run or safe short-lived cache, bounded non-state-changing retries, cancellation and request timeouts.
9. Production host blocked in `AltinnTestingRequestGuard`; test OAuth client configuration alone is not the boundary.
10. No hidden browser automation, CDP, Companion, Edge, or manual token entry.
11. Safe step evidence only, separate auth/created/data/validated/submitted/receipt states and no raw payload/PII in history.
12. Keep run confirmation and backend revalidation; no implicit submit on readiness check.

## 14. One-button UX and future full-E2E architecture

Proposed UI: scenario, approved reporter alias, child alias (if applicable), period, planned hours, actual hours, readiness, then **Run E2E test**. A single click starts one explicitly confirmed journey. The backend acquires headless TEST authentication, obtains an Altinn token, and reports each stage in order. UI shows exact failure stage, safe HTTP/result facts and cancellation; it never opens a browser or displays token/secret. Login acquisition is a backend substep, not an auth popup.

Future evidence path and current status:

| Stage | Future evidence source | Current state |
|---|---|---|
| TestID auth / Altinn token | ID-porten token response + Altinn exchange status, safe claims/provenance only | Interactive provider exists; headless provider not implemented |
| Instance/data/validation/process/receipt | TT02 App API and Storage, safe typed response fields | Submission executor and read-only observer exist; auth currently interactive |
| MU receives/stores message | MU read-only API, Service Bus or approved blob metadata/log evidence | No source/runtime provider or verified correlation contract |
| MM retrieves/decrypts/XSD/maps | MM status, telemetry and safe processing evidence | No provider, endpoint or verified contract |
| Skolenærværsrapport receives/stores/Person lookup | Report-service read-only API/database/status contract | No source or read contract |
| Period close | Existing domain workflow/status owned by Skolenærværsrapport | No safe trigger/source verified |
| Bufdata receives/processes dataset | Service Bus plus safe output/Parquet/status evidence | No Bufdata read/verification provider |

Altinn completion alone remains Partial at most where downstream evidence is missing. Preserve: Authenticated != submitted; Submitted != MU received; MU received != MM processed; MM processed != report stored; report stored != period closed; period closed != Bufdata processed.

## 15. Scenario impact

`Altinn unattended` below means only the Altinn-side identity and steps could be automated once implementation/configuration/policy gates are satisfied. It does not imply downstream completion.

| Scenario | Unattended Altinn input/identity | Downstream state and likely ceiling |
|---|---|---|
| E2E-01 / MU-01 valid report | Yes in principle: approved TestID reporter alias + known registered synthetic child alias | Altinn unattended; MU/MM/report evidence absent, so Partial Altinn journey, not full E2E |
| E2E-02 child not found | Yes in principle with approved reporter and separately confirmed unknown synthetic child | Person outcome/hold behavior is downstream and unobservable; Partially unattended |
| E2E-07 correction | Auth could be reused, but correction/version semantics and scenario implementation are NotAssessed | Not supported as an end-to-end scenario until report-service correction contract/read exists |
| E2E-08 overlapping report | Two Altinn submissions can be unattended with known reporter/child and prior run reference | Overlap decision belongs downstream; Partially unattended |
| E2E-09 several reporters | Each reporter needs a separately approved synthetic TestID alias and person token; current scenario is NotAssessed | Multiple reporters can be submitted in principle, but distinct reporter retention/outcome is unproven; Partially unattended only after scenario work |
| MU-01 | Same Altinn input as E2E-01 | Name includes MU but current MU/MM observers are absent; not Full E2E |
| SPOR-01 | Altinn start could be unattended | No proven cross-system correlation key or downstream observers; chain remains Not supported/full trace not verified |

An unknown-child test value must come from an approved synthetic identity strategy; do not generate arbitrary real-format IDs and assume they are unregistered. Existing tests that synthesize identifiers prove formatting only, not Test-Norge registration or downstream Person behavior.

## 16. Future test strategy (mock-only before supervised live use)

Do not make real TT02 calls in unit/CI tests. Use deterministic HTTP handlers and synthetic fixtures:

- **Auth request:** expected `login_hint` format for configured alias; correct registered scopes/acr; state, nonce and PKCE; exact TEST issuer and allowlisted endpoints; callback URI binding; authorization redirect parsed without following it; wrong state/issuer/host rejected.
- **Token exchange:** authorization code exchanged with registered client auth method; wrong environment rejected; missing/expired secret rejected; no secret/token/code/login_hint in logs; token never persisted; token expiration/reacquisition; cancellation/timeouts and bounded retry.
- **Actor checks:** token subject maps to approved reporter alias; no arbitrary PID from UI; wrong reporter/party or unauthorized current party rejected; child identity remains distinct; known/unknown labels cannot be self-asserted without approved fixture configuration.
- **Altinn API:** fixed TT02/app host, exact app id; create, typed XML upload, validate, process-next and receipt read-back fixtures; redirect blocked; Production and wrong host rejected; cancellation stops subsequent writes.
- **Semantics/history:** Authenticated != submitted; submitted != MU; process ended != MU; storage path != MU blob; raw XML/PIDs/tokens absent from durable history; step evidence source and timestamp retained; downstream stays NotAssessed without provider.
- **BirkNext auth:** verify current protected route behavior without Entra; implement and test only an explicit local authorization mode if desired. Do not solve by anonymous access or trusting caller-supplied identity headers.
- **UI:** one confirmed run; no external browser/auth prompt/token display; readiness distinguishes provider support/configuration/authentication; failure shown at exact stage; progress and cancellation; no retry of non-idempotent create/submit stages.

## 17. Root decision

**A. YES — supported unattended TT02 authentication exists and matches person/test semantics.**

The supported method is ID-porten TEST's documented headless API-testing flow for a named synthetic TestID person, using OAuth authorization code with `login_hint`, then the normal token exchange and Altinn TT02 access-token exchange. It is not a client-credentials flow. The target app's actual authorization rules and BirkNext's no-Entra local execution authorization remain implementation prerequisites to prove before any live run. This audit authorizes no implementation or real request.

## 18. Smallest implementation plan (no implementation performed)

Estimate: **Medium**.

1. Introduce/reuse an Altinn authentication-provider abstraction around the existing token/cache boundary; add a headless ID-porten TEST provider and keep the one existing journey runner/executor.
2. Implement the documented headless `login_hint` test flow with exact redirect/state/PKCE/token validation and the currently registered client authentication method; exchange the access token for TT02 Altinn token.
3. Add backend-only secret/identity configuration: TT02 client id, callback, scopes, auth method/secret, reporter alias and synthetic identity-set alias; never store raw IDs in journey history.
4. Change readiness to separate provider supported/configured, identity aliases approved, app policy confirmed and authenticated state; retain hard Production block and exact app allowlist.
5. Remove the interactive-sign-in prerequisite from the unattended scenario only. Preserve interactive provider as an optional separate attended mode if product wants it.
6. Resolve BirkNext's current Entra-only `ActiveEventExecute` gate for the intended local product through an explicit safe local execution authorization design; preserve authorization and run ownership. Do not add a blanket anonymous bypass or caller identity header.
7. Reuse the current typed submission executor, observer and `IntegrationJourneyRunStore`; add run-bound auth evidence/provenance and keep downstream steps not assessed.
8. Add mock-only auth, policy, stage, history and UI tests described above. Update existing test/comments that assert TestID `login_hint` is unsupported.
9. Confirm actual `buf/skolenaervaer` TT02 policy and one configured synthetic reporter/child set with the service owner.
10. Only after tests/readiness pass, perform one explicitly supervised first live TT02 run; no Companion/Edge dependency.

No implementation was done as part of this audit.

## 19. Exact code references

- **BirkNext optional Entra frontend:** `AIAssisted/frontend/BirkNext.Web/Program.cs`; `Configuration/EntraClientSettings.cs`; `AnonymousAuthenticationStateProvider`.
- **Backend Entra and permission:** `AIAssisted/backend/BirkNext.Api/Program.cs` (JWT bearer registration and `ActiveEventExecute` policy); `Services/ActiveEventTesting/ActiveEventAuthorization.cs` (`BirkNextPermissions.Holds`, handler and local config-write-only exception).
- **Journey authorization/initiator/history:** `Controllers/IntegrationJourneysController.cs` (`[Authorize]`, `CallerId`); `Services/IntegrationJourneys/IntegrationJourneyService.cs`; `Services/IntegrationJourneys/IntegrationJourneyRunner.cs`; `shared/IntegrationJourneyContracts.cs` (`IntegrationJourneyRun` has no initiator field); `Services/IntegrationJourneys/IntegrationJourneyRunStore.cs` (single durable history table).
- **Interactive ID-porten:** `Controllers/AltinnTestingController.cs`; `Services/AltinnTesting/IdPortenTestSignIn.cs`; `Services/AltinnTesting/AltinnTestingPolicy.cs`; `backend/BirkNext.Api/appsettings.json`; `backend/BirkNext.Api.Tests/Services/AltinnTesting/AltinnTestingTests.cs` (currently asserts `login_hint` absent).
- **Altinn submission and identity semantics:** `Services/IntegrationJourneys/Packs/SkoleNaervaer/SkoleNaervaerAltinnSubmission.cs` (auth/current party, create, data, validate, process, receipt); `AltinnAppApiClient.cs`; `AltinnStorageInstanceEvidence.cs`; `SkoleNaervaerJourneyPack.cs` (scenario definitions, readiness, ReporterReference and synthetic-data prerequisites).
- **Current identity configuration:** `shared/MessageFlowReviewContracts.cs` (`ReporterReference`, `SyntheticIdentitySetReference`, approval booleans); `Services/Integrations/MessageFlowReviewStore.cs` (rejects credential-like/raw-person-id values from review JSON).
- **Current documented downstream gaps:** `AIAssisted/docs/integration-journeys.md`; `AIAssisted/docs/skolenaervaer-mu-blob-reference-audit.md`; `AIAssisted/docs/skolenaervaer-test-capability-audit.md`.
- **Repository commits:** `9166a245` — `Add Altinn TT02 API submission for Skolenærvær testing`; `89a5d311` — `Add read-only Altinn Storage instance evidence for Skolenærvær`.

## 20. External documentation and source references

- [ID-porten test users (Digdir)](https://docs.digdir.no/docs/idporten/idporten/idporten_testbrukere.html) — recommends TestID, synthetic test persons and points to headless API testing.
- [Automated testing with ID-porten (Digdir)](https://docs.digdir.no/docs/idporten/oidc/oidc_func_apitesting.html) — headless TestID login using `login_hint`; authorization-code redirect/token flow; `amr=TestID`; explicit API-testing purpose.
- [ID-porten `/authorize` protocol](https://docs.digdir.no/docs/idporten/oidc/oidc_protocol_authorize) — normal auth-code, redirect URI, state/nonce and PKCE requirements.
- [ID-porten client registration (Digdir)](https://docs.digdir.no/docs/idporten/oidc/oidc_func_clientreg) — clients must be pre-registered and supported integration types/scopes are constrained by registration.
- [Altinn App API authentication (Altinn Studio)](https://docs.altinn.studio/en/altinn-studio/v8/reference/api/auth/) — end-user ID-porten identity, organization/service-owner/system-user distinctions, token exchange, service-owner submitter limitation, system-user app-library requirement and ability for apps to impose additional policy.
- [Altinn authentication/token exchange](https://docs.altinn.studio/en/authorization/what-do-you-get/authentication/) — ID-porten access-token to Altinn token exchange.
- [Altinn ID-porten authentication guide](https://docs.altinn.studio/en/authorization/getting-started/authentication/id-porten/) — Altinn token required for API and exchange behavior.
- [Altinn LocalTest user tokens](https://docs.altinn.studio/en/altinn-studio/v8/reference/testing/local/testusers/) and [LocalTest org API test](https://docs.altinn.studio/en/altinn-studio/v8/reference/testing/local/apitests/org/) — local helper tokens, not TT02 credentials.
- [Maskinporten JWT grant](https://docs.digdir.no/docs/Maskinporten/maskinporten_protocol_jwtgrant.html) and [Maskinporten client guide](https://docs.digdir.no/docs/Maskinporten/maskinporten_guide_apikonsument) — certificate/asymmetric-key machine authentication.
- [Altinn system user](https://docs.altinn.studio/en/authorization/what-do-you-get/systemuser/) and [system-user vendor guide](https://docs.altinn.studio/en/authorization/guides/system-vendor/system-user/) — system user acts on behalf of organization; not an individual TestID person.
- [Altinn API overview](https://docs.altinn.studio/en/api/) — App API and Storage API roles and capabilities.

## 21. Final confidence table

| Question | Answer | Confidence |
|---|---|---|
| Does BirkNext require Entra today? | General product: no. Current Integration Journey/Active Event execution and history routes: yes, Entra JWT is the only configured auth scheme. | High |
| Did Altinn code introduce an unnecessary Entra assumption? | It did not add Entra to Altinn authentication, but reused an Entra-only BirkNext execution policy and keys Altinn token to the Entra caller. | High |
| Can ID-porten TEST be unattended? | Yes, documented headless API-test authorization-code flow for synthetic TestID users. | High |
| Can TestID be unattended? | Yes for the documented synthetic-user headless API-testing flow; not a general production bypass. | High |
| Is there a supported TT02 test-user token API? | No direct mint-token helper found; supported method is ID-porten headless OIDC then Altinn token exchange. | High |
| Can Maskinporten act as the required person? | No; it identifies a client organization/system or system user, not a TestID person. | High |
| Can system-user auth act as the required person? | No; it represents an organizational virtual user. | High |
| Can LocalTest be unattended? | Yes, for local-only simulated app testing; not TT02. | High |
| Can TT02 be unattended with correct person semantics? | Yes at the authentication-protocol level via headless TestID; exact target-app policy/identity authorization remains unverified. | High |
| Can BirkNext avoid Edge/Companion? | Yes; direct server-side HTTP auth-code headless flow needs neither. | High |
| Can one-button Altinn submission be supported? | Yes in principle with headless provider, configured synthetic aliases, app policy confirmation and a safe BirkNext execution authorization mode. | Medium |
| Can one-button full E2E eventually be supported? | Not established; MU/MM/report/period-close/Bufdata evidence sources and correlation are absent. | High |

## 22. Required semantic distinctions

BirkNext user identity != Altinn actor identity.

Machine identity != person identity.

Authenticated != submitted.

Submitted != MU received.

MU received != MM processed.

MM processed != report stored.

Report stored != period closed.

Period closed != Bufdata processed.

**No browser automation was used. No Companion/Dedicated Edge was used. No real Altinn call was made. No ID-porten sign-in was performed. No production code was changed. Nothing was committed. Nothing was pushed.**
