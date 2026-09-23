# Headless Authentication & Session Control Diagnostic

This diagnostic asks whether the selected Target Environment can establish a usable authenticated browser session unattended in Playwright headless Edge, and identifies the first observed blocker. It does not configure an automation identity or change authentication readiness, verified user access, authenticated testing context, or Critical E2E configuration.

## Location and prerequisite

Target Environment → Authentication → Authentication diagnostics. The compact result has a Copy IT report action and collapsed Technical details with stages, timing, exception types, browser versions and sanitized navigation.

Browser Automation Diagnostic remains independent in System Settings → Frontend Engine Capabilities. Run it first for the same environment, URL and environment type. The API records its headless result in memory for 30 minutes (`BrowserAutomationEvidenceStore`, the single source of truth).

The recorded fact is `HeadlessAutomationControlAfterTargetNavigation`: headless Playwright stayed in stable control of the browser through the navigation to the target (post-settle probe, a bounded stability window with no page close / crash / context close / disconnect, and a second probe), and the browser ended on the target origin or at the target's authentication handoff (Microsoft Entra sign-in hosts, the configured authority, or a `*.mcas.ms` session-control proxy). It deliberately does **not** require the target application to have been identified: a fresh profile is redirected to Entra before it can see the application, and that redirect is exactly what this diagnostic inspects. An unrelated final origin does not qualify. Headed success alone is insufficient. A subsequent failed run revokes the proof. Server restart and target changes require another prerequisite run. No client-supplied boolean can satisfy this prerequisite.

POST `api/headless-auth-diagnostic/prerequisite` checks that evidence; POST `api/headless-auth-diagnostic/run` enforces the prerequisite again and returns a report, including blocked results. Cancellation is a separate run status.

## Runtime and scope

The browser is Playwright-owned Microsoft Edge, using `Chromium.LaunchPersistentContextAsync` with `Channel = "msedge"`, `Headless = true`, `ChromiumSandbox = true`, and a 30-second launch timeout. There is no CDP attachment, security-disabling flag, certificate bypass, personal profile, credential entry, storage-state import, token injection or MFA interaction.

Every run uses a new GUID directory under `%LOCALAPPDATA%\BirkNext\HeadlessAuthDiagnosticEdgeProfile`. The path must be exactly a run directory under that root; normal Edge paths and reparse-point ancestry are refused. Fresh directories prevent previous diagnostic sessions being reused. Only the owned context/driver and exact fresh profile are cleaned up in `finally`; no process-name kill is used. Cleanup failures are visible, and the profile is never reused.

Development, QA, Test, Local and RC are accepted. Production, Prod, missing/unknown types and production-indicating target hostnames are blocked. Additional explicitly non-production types can be listed in backend `HeadlessAuthDiagnostic:AdditionalNonProductionTypes`; Production/Prod remain forbidden. Main-document navigation to production-indicating hostnames is also blocked. These are configuration/hostname guards, not an authoritative inventory of every production hostname. Target profiles currently originate in frontend configuration, as with the existing browser diagnostic.

Driver creation, launch, navigation, read operations, observation and context cleanup are bounded (10, 30, 20, 5, 15 and 10 seconds respectively). Observation uses a cancellable periodic timer; it does not fill or click forms. One headless authentication diagnostic runs at a time per API instance.

## Evidence and first-blocker rules

Stages are Runtime, HeadlessEdgeLaunch, HeadlessBrowserControl, TargetNavigation, AuthenticationDetection, IdentityProviderDetection, NonInteractiveContinuation, MfaDetection, ConditionalAccessObservation, SessionControlObservation, AuthenticatedReturn, AuthenticatedSessionVerification, PostAuthenticationAutomationControl, HeadlessReadiness and Cleanup. Each is NotRun, Running, Passed, Blocked, Failed, Unknown or NotApplicable. Unreached stages remain NotRun.

The single primary blocker is one of None, BrowserAutomationBlocked, TargetNavigationBlocked, InteractiveAuthenticationRequired, InteractiveMfaRequired, ConditionalAccessBlocked, SessionControlHeadlessRestriction, AuthenticationSessionNotEstablished, AutomationControlLostAfterAuthentication, IdentityNotAvailableForAutomation, AutomationControlLostAfterObservedSessionControl or Unknown. Some values are reserved for evidence that V1 cannot currently establish; lack of a QA identity alone is not treated as proof that the current flow is blocked.

The reducer processes observations chronologically and stops at the first terminal finding. Later observations cannot overwrite it. On the same page an explicit CA error outranks normal sign-in form controls; a specific MFA challenge outranks generic login controls. This is same-page specificity, not a claim about hidden server-side chronology. Browser or target-control failure before observation prevents all authentication/MFA/CA/session-control assessment.

M2LB signs in through an MSAL **popup**: the main page stays on the application's `/authentication/login` route while a second page goes to Entra and on to a `*.access.mcas.ms` host. The diagnostic therefore traces pages the target opens (marked "sign-in popup" in the trace) and observes the most recent open popup while one exists; when the popup closes itself at the end of sign-in, observation returns to the diagnostic's own page rather than treating the close as lost control.

Redirects to `login.microsoftonline.com` identify Microsoft Entra ID. Configured authority origin and authentication type provide additional detection context; provider detection is separate from session verification. Credential/account-selection controls identify interactive sign-in (never MFA). Known visible MFA controls identify human MFA — by stable element ids and input semantics first (`input[name=otc]`, `autocomplete=one-time-code`, Authenticator approval / number-matching ids, proof selection), with narrow English challenge text only as a fallback. The selector lists live in `EntraSignals`. Unrecognized/localized challenges remain Unknown; absence never means MFA is disabled.

Only browser-visible AADSTS53003 on a Microsoft Entra host establishes an explicit CA block. Other `AADSTS530xx` codes are reported as "Conditional Access error observed (code)", a signal, not a block; other Conditional Access wording is a signal too. The same number on a non-Entra page is ignored. The report does not claim CA allowed authentication merely because no error appeared. On an Entra error page the diagnostic keeps only fixed-shape values — the AADSTS code, the Correlation ID and Request ID (GUIDs) and the timestamp — so IT can find the sign-in log entry. The id labels are matched in English; on a localised page they may be Unknown while the code is still recognised. No other page text or error payload is retained.

HTTPS hosts ending in `.mcas.ms`, `.mcas-gov.us` or `.mcas-gov.ms` are session-control signals. Product wording without a matching host is possible involvement. A direct headless restriction on a session-control host is classified separately. Loss of control following an observed session-control signal is reported as `AutomationControlLostAfterObservedSessionControl` — "this establishes sequence, not causality" — with required IT confirmation, never as proof that MCAS caused it. `SessionControlHeadlessRestriction` is reserved for an explicit headless/automation restriction shown on a session-control host.

Every headline value in the report carries its provenance: **Observed** (seen in this run), **Derived** (read from what was seen, including "nothing was seen"), **Configured** (from settings) or **Unknown** (not reached). Plain TargetClosedException is not a security-policy diagnosis.

## Authenticated session verification

An authentication redirect followed by a target-origin navigation is an authenticated return observation, not proof of a usable session. Existing manual verification refers to a human's browser, while the existing attended session validator accepts generic application content; neither proves authentication in this new isolated headless context.

V1 therefore requires an explicit, non-secret authenticated application-shell contract supplied by the target owner. Example backend configuration (replace the selector with a signal that is present only after successful authentication):

```json
{
  "HeadlessAuthDiagnostic": {
    "Verification": {
      "your-qa-environment-id": {
        "TargetOrigin": "https://your-qa-host.example",
        "AuthenticatedSelector": "[data-testid='authenticated-user-menu']"
      }
    }
  }
}
```

Optionally, the same contract can carry `"ApplicationShellSelector"`: a stable structural marker of the application shell that exists **before** sign-in (for example a root element with a `data-app-shell` attribute supplied by the target owner). The Browser Automation Diagnostic uses it, after the expected origin, to decide whether the page Playwright controls is the target application; without it, the origin is the only identification evidence and the report says so. It is never used as authentication evidence.

The selector is read only at the exact configured target origin, with no login controls present. Do not configure a generic public shell selector such as `body` or `main`. Cookies, a callback URL, an HTTP 200 or the disappearance of Entra alone never establish authentication. Missing positive evidence produces Unknown on timeout. Proxied application-origin verification is deliberately conservative: it remains Unknown unless the exact target-origin verification contract is satisfied.

After verification, safe title and DOM reads must succeed for Ready. A verified session plus retained automation control plus no interaction yields Ready / None. A precise observed blocker yields Not Ready; uncertainty or a timeout yields Unknown. The result describes the current fresh diagnostic context; an approved future QA identity may behave differently. No QA identity authentication mechanism is implemented here.

## Privacy and IT report

The observer returns booleans only from DOM checks. No page text/HTML, input values, screenshots, cookies, browser storage, headers, network bodies or token payloads are captured. Main-frame navigation is capped at 100 entries. URLs keep only scheme, hostname/port and an allowlisted path category; userinfo, arbitrary paths, every query value and fragments are removed. Thus code, state, nonce, token fields, login_hint, SAMLResponse and RelayState cannot enter navigation evidence.

Copy IT report distinguishes observations from interpretation, includes the primary blocker, context and UTC times, and asks for an approved non-interactive DEV/QA authentication design. It never prescribes removing MFA or excluding MCAS. Optional attended comparison is not implemented: Diagnostic 1's headed/headless control comparison does not imply attended authentication success.

## Validation and manual acceptance

Run focused backend tests using `--filter FullyQualifiedName~HeadlessDiagnosticTests`, and frontend tests using `--filter FullyQualifiedName~HeadlessAuthDiagnosticCardTests`.

Real Edge adapter tests use local synthetic pages only. Set `RUN_HEADLESS_EDGE_DIAGNOSTIC_TESTS=true` and run `--filter FullyQualifiedName~HeadlessEdgeAcceptanceTests`; otherwise these tests explicitly skip. They seed prerequisite evidence to isolate the real headless adapter, so they are not an end-to-end validation of Diagnostic 1.

For target acceptance, run Browser Automation Diagnostic, record both mode results (requested URL, final location, whether an authentication redirect was detected, browser control, stability, target application), then run this diagnostic only if headless control through the target navigation is demonstrated. Preserve the report even if it stops at interactive login: MFA and later controls cannot be inferred beyond that point. A real DEV/QA result requires access to the configured environment and must not be fabricated from the synthetic tests.

## Technical references

- [Playwright .NET BrowserType persistent-context API](https://playwright.dev/dotnet/docs/api/class-browsertype)
- [Playwright browser channels and headless modes](https://playwright.dev/dotnet/docs/browsers)
- [Microsoft Entra error codes](https://learn.microsoft.com/en-us/entra/identity-platform/reference-error-codes)
- [Defender for Cloud Apps session-control troubleshooting](https://learn.microsoft.com/en-us/defender-cloud-apps/troubleshooting-proxy)

## Browser Automation Diagnostic (prerequisite) — semantics

- **Eligibility:** an explicit non-production allow-list (Local, Development, QA, Test, RC). Production, Custom and blank types are refused, and so is any target whose hostname indicates Production.
- **Profiles:** one dedicated profile per target and per mode, `%LOCALAPPDATA%\BirkNext\BrowserAutomationDiagnostic\<target key>\Headed|Headless`, where the key is a hash of the Target Environment id and canonical origin. The run lock is per profile set, so different targets never share a profile or block each other.
- **Per mode it reports separately:** requested URL (sanitized), initial navigation, final URL/host/origin, expected-origin match, authentication redirect (main page or popup), session-control hop, browser control after navigation settled, a bounded stability window with a second probe (close/crash/context-close/disconnect invalidate it), and target identification (origin, plus an optional `ApplicationShellSelector`). "Authenticated application" is always "Not assessed".
- **TargetClosedException** is reported only when Playwright threw it, with its phase (during navigation, after navigation, during the stability window). A closed page seen without an exception is reported as that observation.
- **Cleanup** is reported as it happened: a closing error is a WARNING beside the result and never replaces the finding. Only the diagnostic's own browser is closed.
- **Independent controls:** the visible DevTools policy (Edge `DeveloperToolsAvailability`, read-only), the remote debugging policy (`RemoteDebuggingAllowed`, read-only), Playwright-owned automation (this run) and CDP attach to an existing Edge (not tested here). None is inferred from another.
- The configured target URL is sanitized in the report; the evidence store binds to the exact configured URL server-side.
- When the prerequisite is met, the card links to this diagnostic (`?section=target-environments&tab=auth&open=headless-auth&profile=<id>`); it does not run it.
