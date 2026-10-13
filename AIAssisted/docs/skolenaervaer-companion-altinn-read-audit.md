# Skolenærvær Companion / Altinn read audit

**Audit only.** Repository inspection only: no production code was changed, no Altinn request was made, and no ID-porten sign-in was performed. This report distinguishes observed code behavior from browser/platform behavior that has not been proven in this repository.

## 1. Executive summary

The existing Companion is a safe page-quality collector paired with a local BirkNext backend. Its current contract deliberately excludes credentials, cookies, storage, request/response bodies and headers. It does not currently make arbitrary network calls or retrieve Altinn Storage data. Dedicated Edge loads that Companion in a separately profiled Edge process; it does not turn the Companion into a credential broker.

The existing Altinn read path already validates a positive party id and GUID, constructs one fixed TT02 Storage URL, sends one GET with a backend-held Altinn bearer token, parses only bounded evidence, and lets the Integration Journey observer persist safe step facts in the existing history table. Its current identity prerequisite is an ID-porten TEST client configuration and a per-user backend token cache. The browser Companion does not currently satisfy that prerequisite.

An extension-background request constrained to the one TT02 Storage host and exact instance path is technically conceivable, and could keep browser-held auth material within Edge if the browser session can actually authorize Storage. That key premise is unproven: the app and Storage use different origins; the code contains no Storage cookie/token discovery, no Storage host permission in Dedicated Edge, and no demonstrated session/auth sharing. Page-context fetch would face CORS and is not an established route. Network observation is unsupported because the Companion does not observe network traffic or response bodies.

**Recommendation: keep the backend ID-porten TEST client approach as the supported execution architecture for now.** Treat a browser-context design as a future, explicitly scoped capability only after a mock-only proof establishes authentication to the Storage host without exporting auth material. The proposed Companion path is not currently feasible as a drop-in reuse, and the audit stop condition “Companion cannot make authenticated Storage API request” is not cleared. Do not infer from this audit that browser auth works for the Storage API.

### Branch / repository state

- Branch: `008-traceability-first`
- HEAD: `89a5d31105d22d45bcec6c1c3f7b451dbe3fa9d5` — `Add read-only Altinn Storage instance evidence for Skolenærvær`
- Recent history includes Altinn submission (`9166a245`), Skolenærvær journey work (`7802b90f`, `3b9a0974`), and Dedicated Edge Companion lifecycle work (`0aab3dbf`). The top ten also include Altinn app/testing and Companion-adjacent project work.
- Initial worktree had two unrelated untracked entries: `AIAssisted/docs/codebase-guide-and-audit.md` and `NUL`. They were not opened, changed, staged, removed, or otherwise touched. Initial tracked and staged diffs were empty. The only requested output created by this audit is this file.

## 2. Dedicated Edge architecture

There are two related but distinct Edge paths:

1. **Dedicated Edge with Companion** is launched by the Local HTTPS Proxy helper. `LocalHttpsProxyService.BuildEdgeArguments` starts Edge with a BirkNext-managed `--user-data-dir`, a loopback proxy and, when provisioned, `--load-extension=<managed Companion build>`. The normal Edge profile is explicitly rejected. The target URL is opened in that profile.
2. **Managed Edge / CDP** is another local Edge launch/attach path (`ManagedEdgePreflightService` and `ManagedEdgeBrowser`). It attaches over loopback CDP for narrow checks. Its implementation reads target origins and selected DOM facts, and a same-origin probe; it is not an Altinn cross-origin proxy and the Companion extension is not evidence that CDP exposes network bodies.

The Companion is a Manifest V3 extension: popup and service worker execute as extension contexts; registered content scripts run in the isolated world on the approved application origin; one listener-only script runs in page MAIN world. The service worker posts evidence to `http://127.0.0.1:5000` or `http://localhost:5000`. It has no WebSocket/SignalR or `postMessage` bridge. Content-to-worker communication uses `chrome.runtime.sendMessage`; worker-to-backend communication uses loopback HTTP POST.

| Component | Responsibility / process boundary | Data crossing boundary | Auth material crossing? | Current policy |
|---|---|---|---|---|
| BirkNext frontend | User controls pairing, proxy/Edge launch, environment and journey UI | Pairing challenge/status, journey input/results | No Altinn credential in Companion contract | Journey execution/history protected by `ActiveEventExecute` (Entra) |
| BirkNext backend | Owns local proxy, Companion session, journey runner/history; currently owns ID-porten callback and Altinn token cache | Typed Companion evidence; journey result | Current backend Altinn path holds Altinn token in memory; Companion session id is extension-only and not an Altinn token | Local caller filters, typed contracts, fixed endpoint policy |
| Dedicated Edge process | Separate managed profile; user navigates/signs in manually | Page/session stays in Edge profile | No current transfer into BirkNext | Not the normal Edge profile; profile lifecycle is separate from the tester's normal browser |
| Companion service worker | Pairing, approved-origin registration, evidence batching and reporting | Sanitized page evidence to loopback; session id to Companion endpoints | No page cookie, page token, Authorization header, or Storage response today | Exact approved application origin for content scripts; fixed loopback backend |
| Companion content scripts | DOM/accessibility/performance/runtime/page identity evidence | Typed page snapshot to service worker | No cookies, local/session storage, forms, request bodies or auth headers | Injected only on paired approved origin |
| Altinn TT02 Storage | Current backend-side instance read | Safe parsed instance facts to journey observer | Backend sends its Altinn bearer token to TT02 | Fixed host and Storage path; GET; Production host rules |

## 3. Companion architecture and current capabilities

The extension manifest declares `storage`, `scripting`, `alarms`; static host access is loopback only. Normal Edge has optional `https://*/*` and `http://*/*`, granted for specific paired origins through a user gesture. The Dedicated Edge copy is rewritten to declare loopback plus only the single approved target application origin. Neither has a current `platform.tt02.altinn.no` permission. The build script explicitly rejects use of cookie APIs, `webRequest`, response-header APIs and browser storage APIs in extension scripts.

| Capability | Finding | Evidence / qualification |
|---|---|---|
| A. Read current page URL | **Implemented (bounded)** | Browser-supplied `sender.url`; content reports normalized origin/path. Query and fragment are discarded. |
| B. Read DOM/page metadata | **Implemented (bounded)** | DOM structure, accessibility, runtime and performance collectors; not raw HTML/text/form values. |
| C. Read browser local/session storage | **Not implemented** | README/build guard and content script comments prohibit it. `chrome.storage.local/session` is used only for Companion-owned state. |
| D. Read cookies | **Not implemented** | No `cookies` permission/API; build guard. |
| E. Access Authorization headers | **Not implemented** | No header capture; build guard forbids response-header access. |
| F. Fetch/XHR from page context | **Not implemented** | No application page fetch feature. MAIN-world script only forwards uncaught exception/rejection events. |
| G. Fetch/XHR from extension background | **Not implemented for targets** | Worker `fetch` is only used for fixed BirkNext loopback API paths. No target URL input or Altinn request. |
| H. Browser credentials automatically attached | **Unknown / unproven for Altinn** | No target fetch exists; no code establishes cookie or token availability at Storage host. |
| I. Cross-origin URL access | **Not implemented for data collection** | Existing target fetch probe is not Companion; Managed Edge CDP probe enforces same-origin. Extension could potentially request host permission in a future feature, but none is granted to Storage now. |
| J. Observe network requests | **Not implemented** | No `webRequest`, DevTools, CDP or proxy response observation in Companion. |
| K. Read response bodies | **Not implemented for target traffic** | Worker reads its own loopback JSON response; Companion does not observe application response bodies. |
| L. DevTools/CDP/network APIs | **Not implemented by Companion** | Separate Managed Edge service uses Playwright/CDP for narrow page checks, no network-body capture. |
| M. Send arbitrary data to BirkNext | **Partially implemented** | Extension can POST JSON to fixed loopback endpoints, but content input is validated/sanitized and endpoint requires envelope/session; not a general-purpose sender interface. |
| N. Send only typed/bounded evidence | **Implemented for current page evidence** | Contracts, 512 KiB envelope cap, per-list/string limits, extension and backend sanitizers. Not a typed Altinn evidence type today. |
| O. Identify active tab/origin | **Partially implemented** | Sender tab, sender URL and live page/tab id are available; it tracks approved pages, not a user-confirmed Altinn instance target. |
| P. Restrict host allowlists | **Implemented for current Companion page injection** | Exact approved origins; no wildcard origin match. Does not include Storage host. |
| Q. Restrict HTTP methods | **Not applicable to existing target calls** | Companion has no target fetch. Fixed Companion backend calls are POST. No target GET-only policy exists. |
| R. Restrict Production hosts | **Partially implemented** | Companion probes/actions only run for enumerated non-production environment types; Dedicated page host is the configured target origin. No Altinn host policy is applied to a Storage request because there is none. |
| S. Redact values before transport | **Implemented for current evidence** | Extension and backend sanitizers; URL query and sensitive values removed. No Altinn parser in Companion. |

The extension contract's capability list currently reports only `element-pick`; there is no runtime-evidence capability. Existing Browser Discovery evidence therefore remains page/browser evidence, not proof of authenticated API access.

## 4. Browser/session/auth boundary and Dedicated Edge use

1. **Normal browser window:** Dedicated Edge is an Edge window launched by BirkNext's local helper, not a normal user-controlled profile. The tester interacts with the visible browser manually.
2. **Launch:** Local HTTPS Proxy starts a separate Edge process and loads a BirkNext-managed extension build. A separate Managed Edge launcher can also start a dedicated profile for CDP. These are distinct helpers.
3. **Dedicated profile:** Yes. The launch passes a BirkNext profile directory and rejects the normal Edge user-data directory. The Companion extension build is stored separately under `%LOCALAPPDATA%\BirkNext\BrowserCompanion\build-*`.
4. **Same authenticated profile:** Companion executes inside the launched Edge profile in which the tester navigates and signs in. It can see the approved page's DOM as its current policy allows; it has no current access to auth material.
5. **Manual external sign-in:** Technically the user can navigate the browser to external systems, but Companion injection is limited to the exact approved app origin and the provisioning policy deliberately excludes authentication infrastructure hosts. No Altinn/ID-porten sign-in was performed or validated here.
6. **Session retention:** Edge's dedicated profile stores browser state under its user-data directory, so session persistence across browser launches is technically plausible. The repository does not establish Altinn cookie lifetime or cleanup semantics. Local reset documentation says managed Edge windows are not closed and profile state is not cleared by project reset.
7. **Isolation:** The profile is isolated from the tester's normal Edge profile, but it is a machine-local persistent profile, not proven per-BirkNext-user isolation. Reuse across users on one workstation needs explicit lifecycle review.
8. **BirkNext gets browser auth today:** No. Current Companion messages contain evidence and a Companion session id, not target cookies/tokens/headers. Current Altinn backend flow separately owns an Altinn token.
9. **Companion gets browser auth today:** No by code policy. It gets approved origin/session scope, not target credentials.
10. **Trusted channel:** Yes, for existing Companion messages: loopback peer/host, browser extension Origin, extension session bound to that origin, fixed endpoints, size limits. It authenticates the Companion extension session, not a TestID user or Altinn identity.

Dedicated Edge launch token is stored in the managed extension package; backend stores only its SHA-256 hash in a record. The Companion session id is stored in extension storage and backend session state. Neither is the Altinn access token.

## 5. Cross-origin/CORS and extension permissions

The requested browser page origin is `https://buf.apps.tt02.altinn.no`; Storage is `https://platform.tt02.altinn.no`. These are distinct origins and distinct hosts. Browser same-origin policy applies to page-world fetch. A page-context `fetch` would require Storage CORS approval for the page origin; credentialed fetch additionally requires compatible CORS credential headers. No such response policy is established by this repository.

Chrome/Edge extensions with host permissions can generally make cross-origin requests from extension contexts to permitted hosts, subject to browser extension rules. This is a platform capability, **not** proof that Altinn accepts the browser's authentication at the Storage host. Current Dedicated Edge host permissions do not cover Storage. Adding the exact Storage origin would be materially broader than current policy because the Companion service worker could communicate with that host; an exact-host permission is narrower than a wildcard but still requires purpose-specific review. Do not add wildcard host permissions.

| Manifest permission | Current state | Altinn consequence |
|---|---|---|
| `host_permissions` | Loopback only in source manifest; Dedicated copy adds the exact approved app origin | Storage host absent |
| `optional_host_permissions` | Normal extension allows per-origin HTTP(S) prompts | Generic capability is broad in the manifest, but current pairing grants only configured app origins; Dedicated copy removes optional permissions |
| `cookies` | Absent | Cannot use `chrome.cookies` |
| `webRequest` / `declarativeNetRequest` | Absent | No network observation or request modification |
| `activeTab` | Absent | No temporary arbitrary-tab access; registered scripts are scoped by origin permission |
| `scripting` | Present | Used to register fixed content scripts, not arbitrary code execution supplied by BirkNext |
| `storage` | Present | Companion-owned session/config only; not page local/session storage |
| `tabs` | No manifest permission | Browser supplies sender tab metadata to extension message handlers; worker does not use `chrome.tabs.query` |

No current code proves inclusion of browser cookies on a Storage-host extension request. Cookie availability depends on host/domain, SameSite/partitioning, browser credential mode and Altinn's auth mechanism. A bearer token held by page JavaScript or a BFF is not automatically available to extension background. No token extraction should be introduced.

## 6. Altinn Storage feasibility and execution options

Existing endpoint policy and reader target the fixed GET `https://platform.tt02.altinn.no/storage/api/v1/instances/{party}/{guid}`. Existing backend parser intentionally ignores owner identity fields, masks identifiers, redacts `blobStoragePath`, reads no content and caps metadata collections. The response cap is 1 MiB in current backend code. The app endpoint is intentionally excluded because its GET may change `readStatus` for person callers.

The key unresolved point is identity: `AltinnStorageInstanceReader` authenticates using an explicit bearer token exchanged by BirkNext's ID-porten TEST client. The repository provides no evidence that browser sign-in to the Skolenærvær app yields a cookie accepted at `platform.tt02.altinn.no`, or that a browser-held bearer token is available to Companion without reading/extracting it. Browser app login and Storage API authentication must not be conflated.

| Option | Assessment | Required controls / reasons |
|---|---|---|
| **A. Page-context fetch** | **Reject as current recommendation; possible only if Altinn CORS/auth behavior is proven** | Would originate from the app page and face CORS. It may share page credentials only if browser policy and Altinn auth design permit it. Current Companion does not inject a fetch helper. Risk of turning page injection into arbitrary request behavior unless inputs and target are fixed. Must return parsed safe facts only. |
| **B. Extension-background fetch** | **Possible but unproven; strongest browser-context candidate** | Requires exact `https://platform.tt02.altinn.no` host permission and a typed command. Extension CORS restrictions can be relaxed by host permission, but auth is not solved. Use browser credentials only if proven and no cookie/token extraction; exact path construction; explicit credentials mode; redirect rejection; response streaming cap; typed parser; no raw payload logging/storage. |
| **C. Existing Companion network observation** | **Reject** | No request observer, `webRequest`, CDP, or response-body capability. Adding interception would be a significant new permission/trust surface and could expose unrelated traffic. It is not “reuse” of current Companion behavior. |
| **D. Backend ID-porten client** | **Recommended supported path for now** | Already implements one bounded Storage GET, safe parsing and Integration Journey evidence. Needs configured TEST client/token flow, currently blocked by missing configuration. Keeps token in backend memory per Entra caller/environment; does not prove auth is ready until configured. |

### Authentication classifications (browser context)

| Mechanism | Finding |
|---|---|
| Page fetch from `buf.apps.tt02.altinn.no` to Storage | **Technically plausible but unproven**; requires Storage CORS and applicable auth credentials. |
| Extension/background fetch to Storage | **Technically plausible but unproven**; current host permission is absent and browser auth acceptance is unknown. |
| Browser-cookie auth | **Unknown / unproven**; hosts differ and no cookie access or cookie-sharing evidence exists. Do not assume shared cookies. |
| Bearer-token auth | **Not supported by current Companion**; current token is produced/held by backend flow. A browser-held token is not available just because user is signed in. |

## 7. Auth material handling and transport reuse

The preferred future trust boundary is sound in principle: Dedicated Edge makes the fixed authenticated GET; Companion parses and sanitizes in Edge; only typed safe evidence crosses loopback into BirkNext. This avoids exporting cookie/token/Authorization header if the browser itself can authenticate without exposing those values to extension code. Whether the Storage request can be authenticated that way is unproven.

Current transport protections: pairing uses a short-lived single-use code; backend session binds one profile, approved origins and extension Origin; Dedicated bootstrap uses a random launch token whose hash is retained backend-side; extension endpoints require loopback peer and host plus a browser-extension Origin; evidence endpoint has a 512 KiB cap and the session id/profile/version are checked; evidence is sanitized again server-side. No explicit sequence nonce/replay counter for evidence envelopes is visible in the inspected contract; session expiry, rate limit/minimum interval and idempotent page identity handling provide partial replay resistance, not strict command replay protection. Extension and tab identity are constrained, but the channel does not attest a human identity beyond the BirkNext-side session/profile.

Existing message payload is bounded page evidence, not arbitrary generic JSON by design. Unknown evidence shape is deserialized to the current contract and sanitized. Schemas are typed C# contracts but no explicit schema-version property is evident; extension version/build id provides compatibility metadata. Current browser page evidence is persisted in the existing browser evidence store, separate from Integration Journey history. No existing event/evidence envelope currently writes authenticated Storage facts into a journey run.

The current Integration Journey history is reusable: authenticated `POST /api/integration-journeys/runs` and history/detail endpoints are protected by the same Entra `ActiveEventExecute` policy. `IntegrationJourneyRunStore` persists serialized run results in the single `IntegrationJourneyRuns` table; per-step evidence includes `EvidenceSource`, `CapturedAt`, correlation and typed `Facts`. A future typed browser result should be accepted only into the active, explicitly confirmed instance-evidence journey run and recorded through this store/runner path, rather than creating a second store. The current runner is synchronous/server-driven and its observer invokes backend storage itself; a Companion callback would need a carefully bound pending run/step handshake (not currently present).

## 8. Evidence model and semantics

Current Browser Discovery is page evidence: page identity, DOM/accessibility/performance/runtime findings. It is not network evidence and does not prove an authenticated Storage request. Any future successful browser Storage read must be labeled **Authenticated browser runtime evidence**, with source `Dedicated Edge Companion`, evidence type `authenticated Altinn Storage read`, target host, method GET, observation time, safe page/tab origin, masked instance reference and sanitized process/data facts.

Preserve these distinctions in any implementation and UI:

- Browser Discovery evidence **!=** authenticated Storage API evidence.
- Authenticated Storage API evidence **!=** Altinn submission evidence.
- Instance observed **!=** process completed.
- Process completed **!=** MU received.
- Altinn `blobStoragePath` observed **!=** MU blob verified.

The current observer already marks evidence as `Observed` or `NotVerified`, and leaves downstream MU/MM/report-service steps not assessed. That semantic discipline should remain unchanged. Do not label a browser read “submitted” or let it satisfy a submission scenario.

## 9. Tester workflow and BirkNext Entra implications

If a future Companion read is proven feasible, the reasonable attended flow is: open Dedicated Edge from BirkNext; navigate and sign in manually to Altinn TT02/TestID; open the known Skolenærvær instance; return to BirkNext and select “Check Altinn instance”; show the exact masked target and require confirmation; Companion performs one fixed GET; BirkNext records sanitized facts into the selected Integration Journey run. The tester should be on the relevant Altinn tab or explicitly choose it so active origin/tab is unambiguous. URL parsing may prefill party id/GUID only after strict URL parsing and confirmation; it does not prove ownership or authorization. Do not enumerate or discover instances.

BirkNext login is a separate concern from Altinn/TestID login. Current journey execution and history require a BirkNext Entra principal with `ActiveEventExecute`; `CallerId` is used to associate execution. A Companion transport session alone is not a replacement for user identity, permission checks, evidence ownership or audit trail. Therefore a Companion design may avoid the **backend ID-porten TEST client/token ownership** if browser-authenticated Storage access is proven, but it does not remove the **BirkNext Entra login** required for journey history and execution under current code.

## 10. Security risks, safeguards, blockers

Main blockers and risks:

- No proof browser-authenticated Altinn app state authorizes Storage API at its different host.
- Storage host is absent from Dedicated Edge Companion's current host permissions.
- Page fetch requires CORS behavior not demonstrated here; background host permission alone does not yield auth.
- The current Companion contract has no run-bound request/result handshake, typed Altinn result, strict sequence nonce, or runtime-evidence provenance.
- Dedicated profile persists outside ordinary browser profile and project reset; stale Altinn session and multi-user workstation isolation need lifecycle decisions.
- New extension host access expands extension authority. Cookie/token reads, generic URL fetch, request headers, and proxy behavior would violate current design constraints.
- Existing backend parser has bounded data fields but would need a JavaScript/extension equivalent with parity tests before raw response can be safely discarded in-browser.

Any future design must enforce all specified safeguards: GET only; exact `platform.tt02.altinn.no`; exact `/storage/api/v1/instances/{positivePartyId}/{guid}` path; typed inputs only; no arbitrary URL/host/path/headers/body; no write methods; explicit TT02/Production block; redirects rejected including cross-host; no cookie/token/Authorization export; bounded response bytes; typed JSON parsing; owner PII removed before process boundary; full blob path redacted; raw response neither sent nor persisted; secret-free logs; explicit instance reference only with no enumeration; no proxy feature; and clear runtime-evidence labeling distinct from Browser Discovery.

Current policy already blocks known Production Altinn hosts in backend request policy and requires configured environment Test/ProductionBlocked. A future extension must independently block Production and revalidate at command and URL construction. Host permission must be exact, never wildcard. Response must be size-limited while streaming, not after unbounded `response.text()`. Redirect mode should be `error`; check final URL regardless. Sanitizer must allowlist fields, not redact a raw object after transport.

## 11. Required test strategy (future, mock-only)

No tests were run for this audit. Before real Altinn use, add deterministic mock/fake tests with no network access:

- **Companion:** approved exact host passes; wrong host, Production, wrong path, query/path traversal and arbitrary URL fail; GET-only; positive party id and canonical GUID validation; redirects fail; streamed size cap; malformed/deep JSON fails; owner PII discarded; full blob path absent; cookie/token/header never included in message; raw response never emitted, logged or persisted.
- **Transport:** only versioned typed evidence accepted; unknown type rejected; run id/profile/session/tab/origin binding; replay/duplicate result behavior; stale tab and wrong origin rejected; unauthenticated extension message rejected; existing extension Origin and loopback gates remain.
- **Journey:** result stored in existing `IntegrationJourneyRuns`; no second table/store; provenance visible; Entra authorization required; downstream steps remain `NotAssessed`/`NotVerified` as appropriate; no false “Submitted”; instance evidence remains `Observed`, not `Verified` merely because present.
- **Contract parity:** synthetic fixture tests compare the browser allowlist parser to safe expected facts and verify parity with the backend `AltinnStorageInstanceEvidence.Parse` behavior, including unusual owner fields and identifiers.

## 12. Backend vs Companion comparison

| Dimension | A. Backend ID-porten TEST client | B. Dedicated Edge / Companion authenticated read |
|---|---|---|
| Implementation complexity | Existing flow/read/parser/history largely present; configuration gap remains | New command, host permission, browser auth proof, parser, run callback, provenance and lifecycle controls |
| Configuration | TEST client id/scopes/redirect and optional client secret; approved app | Exact Storage host permission and auth behavior; no client secret if browser session suffices |
| Secrets / token ownership | Backend exchanges and holds Altinn token in process memory per Entra caller/environment, short-lived | Could keep auth inside Edge, but only if Storage auth naturally works; no current mechanism demonstrated |
| Browser dependency | Browser used for ID-porten callback, then backend performs Storage request | Dedicated Edge must remain running, signed in and on/bound to the intended tab |
| Test-user usability | Sign in from BirkNext journey; backend token state visible | Manual Altinn sign-in in dedicated profile; may be more natural if the same session authorizes Storage |
| Security boundary | Token crosses ID-porten callback to backend and is held there; guarded fixed destination, memory-only | Lower backend secret exposure if no auth export; wider extension authority and persistent browser session risk |
| Auditability | Server-run request and history are naturally correlated to Entra initiator | Must bind tab, profile, session, target, confirmation and asynchronous result to a journey run |
| Multi-user support | Cache keyed by initiator + environment; journey permission/initiator explicit | Dedicated profile appears machine-local; no proof of per-user isolation or safe shared workstation behavior |
| CI/mock testing | Straightforward HTTP handler/token cache/parser fakes; backend architecture exists | Extension/service worker/browser mocks and transport handshake required; feasible without Altinn calls |
| Runtime reliability | Independent of active app tab after token exists; server owns request | Depends on Edge process, extension, active authenticated state, permission grants and browser policy |
| Permission model | Server allowlist and fixed request guard; no new browser host permission | Exact new TT02 host permission; background cross-origin authority must be tightly scoped |
| Maintenance | Existing C# parsing/policy and Integration Journey contracts | Duplicate parser/sanitizer implementation unless shared contract approach is established; extension lifecycle compatibility |

## 13. Recommendation

**Keep Option D (backend ID-porten TEST client) as the supported execution path until Companion authentication to TT02 Storage is proven.** The current audit cannot establish a safe authenticated Storage request from the browser, so it cannot recommend changing the execution context. Do not add fetch, interception, auth extraction or host permissions as part of this audit.

If the backend TEST client remains unavailable, a future design spike may evaluate **Option B only** using mock data and explicit documentation/permission review. First prove whether a browser session can authorize the Storage host without exposing cookies/tokens to the extension or BirkNext. If that cannot be shown, stop and retain backend auth. If proven, add only a fixed typed command, exact-host permission, sanitized typed result and journey-run correlation; preserve the evidence distinctions above.

## 14. Final confidence table

| Question | Answer | Confidence |
|---|---|---|
| Can Dedicated Edge retain TestID/Altinn session? | Profile persistence makes retention plausible; actual TestID session behavior/lifetime is not proven here. | Medium |
| Can Companion identify current Altinn instance URL? | It identifies approved page origin and normalized route. It has not been shown running on Altinn page; Altinn host is not approved/injected today. | Low |
| Can Companion obtain party id / instance GUID safely? | No current extraction. Could be explicitly supplied/strictly parsed in future; URL alone does not prove ownership. | Low |
| Can Companion perform cross-origin Storage GET? | Not today. Extension background can potentially do cross-origin fetch with host permission, but none exists for Storage and no request code exists. | High |
| Can it reuse browser authentication? | Unproven; cookies are not assumed shared and browser bearer availability is absent. | Low |
| Can token/cookies stay inside browser? | A design could avoid exporting them, but current authenticated Storage behavior is not established. | Medium |
| Can owner PII be removed before transport? | Yes in principle with an allowlist parser inside Edge; current Companion has no Altinn parser. | Medium |
| Can only typed safe evidence be sent? | Current page evidence is typed/bounded; no typed Altinn evidence contract exists. | High |
| Can arbitrary proxy behavior be prevented? | Yes with fixed typed inputs and exact endpoint construction, but current target-fetch path does not exist. | Medium |
| Can Production be blocked? | Existing backend does; future extension would need an independent exact TT02 allowlist and runtime block. | Medium |
| Can Integration Journey reuse the evidence? | Existing one-table history/run result can store it, but callback/run handshake is not implemented. | High |
| Can backend ID-porten client be avoided? | Not with proven current capabilities. Potential only if browser Storage authentication is demonstrated. | High |
| Is BirkNext Entra still required? | Yes under current journey run/history authorization and initiator ownership model. | High |
| Is Companion approach preferable overall? | No, not on current evidence; backend approach is supported and Companion auth premise is unproven. | High |

## 15. Exact code references

- Companion permissions and service worker: `AIAssisted/browser-companion/manifest.json`, `background.js`, `content.js`, `main-world.js`, `dedicated.js`, `build.mjs`, `README.md`.
- Companion channel/contracts: `AIAssisted/shared/BrowserCompanionContracts.cs`; `AIAssisted/backend/BirkNext.Api/Controllers/BrowserCompanionController.cs`; `AIAssisted/backend/BirkNext.Api/Services/BrowserCompanion/BrowserCompanionService.cs`; `BrowserCompanionEvidenceSanitizer.cs`.
- Dedicated Edge Companion provisioning/profile: `AIAssisted/backend/BirkNext.Api/Services/LocalHttpsProxy/DedicatedCompanionProvisioner.cs` (`Prepare`, `WriteDedicatedManifest`, `IssueLaunch`, `Observe`, `Retire`); `LocalHttpsProxyService.cs` (`LaunchEdgeAsync`, `BuildEdgeArguments`); `AIAssisted/docs/local-data-reset.md`.
- Separate Managed Edge/CDP path: `AIAssisted/backend/BirkNext.Api/Services/ManagedEdge/ManagedEdgePreflight.cs` (`BuildLaunchArguments`); `ManagedEdgeBrowser.cs` (`FetchAsync`, same-origin `FetchScript`, navigation origins); `ManagedEdgeCdpService.cs`; `AIAssisted/backend/BirkNext.Api/Services/ManagedEdge/README.md`.
- Existing Altinn authentication/policy/read: `AIAssisted/backend/BirkNext.Api/Services/AltinnTesting/IdPortenTestSignIn.cs` (`AltinnTestSessionCache`, `StartAsync`, `CompleteAsync`); `AltinnTestingPolicy.cs` (`Tt02StorageInstances`, `StorageInstance`, Production blocks); `AltinnStorageInstanceEvidence.cs` (`ReadAsync`, `Parse`, `RedactStoragePath`); `AIAssisted/backend/BirkNext.Api/Controllers/AltinnTestingController.cs`.
- Existing journey evidence/history: `AIAssisted/backend/BirkNext.Api/Services/IntegrationJourneys/Packs/SkoleNaervaer/SkoleNaervaerAltinnInstanceObserver.cs`; `SkoleNaervaerJourneyPack.cs`; `IntegrationJourneyService.cs`; `IntegrationJourneyRunStore.cs`; `AIAssisted/backend/BirkNext.Api/Controllers/IntegrationJourneysController.cs`; `AIAssisted/shared/IntegrationJourneyContracts.cs`.

**Explicitly preserved:** Browser Discovery evidence != authenticated Storage API evidence; authenticated Storage API evidence != submission; instance observed != process completed; process completed != MU received; Altinn `blobStoragePath` observed != MU blob verified.

**No real Altinn call was made. No ID-porten login was performed. No production code was changed. Nothing was committed. Nothing was pushed.**
