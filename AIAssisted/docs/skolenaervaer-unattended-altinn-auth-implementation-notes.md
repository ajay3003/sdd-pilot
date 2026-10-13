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
- **Process contract:** superseded by §11. `process.bpmn` is optional. The app's deployed OpenAPI contract confirms the one bounded process/next BirkNext performs.
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
   - read the live instance, check it is at `Task_1` (data) and completable, then exactly one process/next (§11);
   - read the receipt.
7. Optionally read the Storage evidence with the same token.
8. Stop. One attempt; nothing is retried. MU, MM, report and Bufdata are observed later.

## 11. Process contract without process.bpmn (2026-10-13)

The requirement is a **verified process execution contract** for the one operation BirkNext performs, not the `process.bpmn` file. `process.bpmn` stays supported and optional: when uploaded through Source Analysis it is parsed, compared with the deployed contract, and makes the BPMN topology known.

### 11.1 Evidence: the deployed OpenAPI contract

- **Document:** app-lib serves it at `https://buf.apps.tt02.altinn.no/buf/skolenaervaer/v1/customOpenapi.json`.
  - Read on 2026-10-13 with one anonymous GET: HTTP 200, 83,851 bytes, sha256 `4f8b4ba04c1a034ac21437a42560ab40ef389409e05ac306cab5591378263654`, app-lib 8.12.7.264.
  - It matches the copy the user supplied.
  - BirkNext reads it from the TT02 app host itself, with the public metadata ("Read public TT02 metadata"), so the evidence is tied to `buf/skolenaervaer` in TT02.
  - Only normalized facts and the fingerprint are kept, never the document.
- **Generated task table** (`CustomOpenApiController.GetIntroDoc`):
  - It lists `IProcessReader.GetProcessTasks()`, which is every BPMN `task` plus every `serviceTask`. Gateways and events are not listed.
  - The Type column is `taskExtension.taskType`, falling back to the task name. Actions is `taskExtension.actions`: an empty cell means an empty list, and `[no actions]` means no list at all.
  - For this app it shows exactly one task: `Task_1 · Utfylling · data`, with no actions.
  - The Type `data` comes from `taskType`, because the fallback would have shown "Utfylling".
- **Data types:** `bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn`, FormData (AutoCreate), application/xml, on `Task_1`. The application metadata (`dataTypes[].taskId`) says the same.
- **Operation:**
  - `PUT /buf/skolenaervaer/instances/{instanceOwnerPartyId}/{instanceGuid}/process/next`.
  - The `ProcessNext` body (`action`, `actionOnBehalfOf`) is optional.
  - A 200 returns `AppProcessState` (`started`, `startEvent`, `currentTask`, `ended`, `endEvent`, `actions`).

### 11.2 Altinn semantics (app-lib 8.12.7 source)

- **Only tasks and service tasks are executed.** `ProcessReader` models only start events, tasks, service tasks, exclusive gateways, end events and sequence flows.
  - With one listed task, no second task or service task can exist.
  - A gateway or event after `Task_1` can exist and is not listed. Its only possible targets are an end event or `Task_1` again.
- **An omitted body is the normal completion of the task.** `ProcessEngine.ProcessNext` uses `request.Action ?? ConvertTaskTypeToAction(taskType)`, which is `write` for a data task.
  - `ProcessEngineAuthorizer` authorizes it with `write`, which the confirmed policy grants PRIV.
- **Validation runs inside process/next.** `ValidateInstanceAtTask` counts error-severity issues; any error returns `Conflict`, which the controller maps to 409.
  - A process that has ended or not started is also 409.
  - An authorization failure is 403.
- **Service tasks run automatically, user tasks do not.** The loop continues only while the new current task is a service task, so one call never passes another user task.
- **Reaching an end event sets three fields together:** `CurrentTask = null`, `Ended = now` and `EndEvent = <id>`. So only all three count as "process ended".

### 11.3 What BirkNext does

- **Assessment:** `AltinnProcessContractEvaluator`:
  - **ConfirmedForCurrentOperation** when all of these hold:
    - the planned tasks are known (from process.bpmn, or a single deployed task);
    - the form's data type belongs to the first task;
    - every planned task is a data task with no declared actions;
    - the deployed contract (when present) declares process/next with an optional body and a response with `currentTask`, `ended` and `endEvent`;
    - the plan needs no more transitions than `AltinnTesting:MaxProcessTransitions`. The default is 1, and values outside 1–10 are treated as 1. A longer plan is refused, never truncated.
  - **Partial**: a specific fact is missing, and the reason is stated. Examples: more than one deployed task with no order, declared actions, a required body, a missing response field, a non-data task, or a plan over the bound.
  - **NotConfirmed**: there is no deployed task table and no process.bpmn.
  - **Conflict**: process.bpmn and the deployed tasks differ, or the metadata and the deployed contract disagree on the form's task.
- **Before process/next:** the executor reads the live instance (GET).
  - It must not have ended, must be at the planned task, of the planned type (`altinnTaskType`), and `validated.canCompleteTask` must not be false.
  - Altinn's validation earlier in the run must have reported 0 errors.
  - The executor then makes exactly one process/next. It is never retried.
- **After process/next:** the returned `AppProcessState` is classified.
  - **ProcessEnded**: `currentTask` null, `ended` set and `endEvent` set. Only this, or the planned next task inside a verified multi-task plan, continues.
  - **UnexpectedNextTask**: a current task is returned, including `Task_1` again. The run stops.
  - **UnexpectedProcessState**: anything else. The run stops.
  - No second process/next is made.
- **History facts:** each is recorded separately: `Validated`, `Live task before transition`, `Process advanced`, `Process ended`, `Submitted` (Altinn only) and `Receipt confirmed`.

### 11.4 Conclusion for E2E-01

- **Process contract:** confirmed for one bounded transition, because the TT02 deployed contract, the metadata, the live-state checks and the post-condition all line up. That was decision A of this audit.
- **The BPMN topology is not known, and this operation does not need it.**
- **process.bpmn is no longer a blocker.**
- **Still open before the first real run (decision B, configuration):**
  - apply the policy confirmation (§10.1);
  - configure the ID-porten TEST client, its redirect URI and the secret;
  - configure the synthetic reporter alias, with a confirmed profile;
  - set up the IQR Altinn test configuration;
  - approve the synthetic child;
  - read the public TT02 metadata once in the target environment, so that the deployed contract is stored for it.
- **Meldingshode** (§10.2) remains a first-run risk.

## 12. Configuration for the first supervised TT02 run (2026-10-13)

**Applied and committed:**
- **App policy confirmation:** the §10.1 confirmation is now in `appsettings.json`, with `"Environment": "TT02"` added.
  - BirkNext accepts a confirmation only when all of these hold: the environment is TT02; a recorded `PolicySource` is exactly `https://platform.tt02.altinn.no/resourceregistry/api/v1/resource/app_{org}_{app}/policy` for that app; a recorded `PolicySha256` is a SHA-256 hex digest.
  - A role list without PRIV (for example DAGL only) does not satisfy a person run.
- **Reporter ≠ child:** the dry run and the executor refuse a child identifier that equals the selected reporter's TestID identifier. They compare in the backend and echo neither value.
- **Readiness rows:** Registered redirect URI and ID-porten TEST client are now separate rows. "Known synthetic child (test data)" is a declared test prerequisite, never verified. Meldingshode is a first-run risk (Partial, not mandatory).
- **Run confirmation:** it lists the real effects:
  - TT02 call;
  - authenticate the reporter;
  - one new instance;
  - upload;
  - validate;
  - at most one process transition;
  - read the receipt;
  - no retry, no second transition, no Production.
- **Fix:** the shared `OpenApiExtractor` threw on numeric (integer) enums. The real TT02 `customOpenapi.json` has them (`ReadStatus`, `ValidationIssueSeverity`), so the deployed process contract was never stored. Non-string enum values are now skipped, as they already were for parameters. The fixture now carries numeric enums.

**Verified end to end on a scratch backend** (isolated database; placeholders for the client, redirect, secret and reporter):
- "Read public TT02 metadata" stored the live deployed contract: app-lib 8.12.7.264, sha256 `4f8b4ba0…`, `Task_1 · Utfylling · data`, no actions, form data type on `Task_1`, `PUT process/next` with an optional body, `AppProcessState` with `currentTask`/`ended`/`endEvent`.
- Readiness was complete.
- The dry run was **Valid** (XML 426 bytes, not shown), with no history and no network call.

**Still to configure per installation (never in committed files).** `appsettings.Local.json` is tracked by git, so do not use it for secrets. Use environment variables for the backend process:

| Setting | Where |
|---|---|
| `AltinnTesting__UnattendedIdPortenTest__Enabled=true` | backend environment |
| `AltinnTesting__UnattendedIdPortenTest__RedirectUri=<redirect registered for the ID-porten TEST client>` | backend environment |
| `AltinnTesting__UnattendedIdPortenTest__ClientSecret=<secret>` | backend environment / secret store only |
| `AltinnTesting__UnattendedIdPortenTest__Reporters__0__Alias=reporter-a`, `…__Label=Skolenærvær reporter A` | backend environment |
| `AltinnTesting__UnattendedIdPortenTest__Reporters__0__TestIdPersonIdentifier=<synthetic TestID person>` | backend environment only |
| `AltinnTesting__UnattendedIdPortenTest__Reporters__0__AltinnProfileConfirmed=true` | only after confirming the TT02 profile |
| ID-porten client id, `openid` plus the instance scopes, app id `buf/skolenaervaer`, environment Test, reporter reference, approved synthetic identity set | Integration Quality Review → Altinn test configuration (the selected target environment) |
| The Rapportmottak integration enrolled in `TargetEnvironments:Trusted:<n>:IntegrationIds` | backend configuration |
| "Read public TT02 metadata" once for the selected target | Skolenærvær Testing → Altinn TT02 submission |

**Meldingshode (unchanged):** `Id`, `MeldingstypeNmsp`, `FagsystemAvsender.{Leverandor,Navn,Versjon}` and `SendtTidspunkt` are required by the schema. BirkNext supplies none of them. Whether the app supplies them is unknown (expected: the app). Altinn's validation step in the first live run shows it before any process transition.
