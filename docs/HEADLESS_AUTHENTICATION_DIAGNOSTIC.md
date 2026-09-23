# Headless Authentication & Session Control Diagnostic

This diagnostic asks whether the selected Target Environment can establish a usable authenticated browser session unattended in Playwright headless Edge, and identifies the first observed blocker. It does not configure an automation identity or change authentication readiness, verified user access, authenticated testing context, or Critical E2E configuration.

## Location and prerequisite

Target Environment → Authentication → Authentication diagnostics. The compact result has a Copy IT report action and collapsed Technical details with stages, timing, exception types, browser versions and sanitized navigation.

Browser Automation Diagnostic remains independent in System Settings → Frontend Engine Capabilities. Run it first for the same environment, URL and environment type. The API records its headless target-control result in memory for 30 minutes. Headed success alone is insufficient. A subsequent failed run revokes the proof. Server restart and target changes require another prerequisite run. No client-supplied boolean can satisfy this prerequisite.

POST `api/headless-auth-diagnostic/prerequisite` checks that evidence; POST `api/headless-auth-diagnostic/run` enforces the prerequisite again and returns a report, including blocked results. Cancellation is a separate run status.

## Runtime and scope

The browser is Playwright-owned Microsoft Edge, using `Chromium.LaunchPersistentContextAsync` with `Channel = "msedge"`, `Headless = true`, `ChromiumSandbox = true`, and a 30-second launch timeout. There is no CDP attachment, security-disabling flag, certificate bypass, personal profile, credential entry, storage-state import, token injection or MFA interaction.

Every run uses a new GUID directory under `%LOCALAPPDATA%\BirkNext\HeadlessAuthDiagnosticEdgeProfile`. The path must be exactly a run directory under that root; normal Edge paths and reparse-point ancestry are refused. Fresh directories prevent previous diagnostic sessions being reused. Only the owned context/driver and exact fresh profile are cleaned up in `finally`; no process-name kill is used. Cleanup failures are visible, and the profile is never reused.

Development, QA, Test, Local and RC are accepted. Production, Prod, missing/unknown types and production-indicating target hostnames are blocked. Additional explicitly non-production types can be listed in backend `HeadlessAuthDiagnostic:AdditionalNonProductionTypes`; Production/Prod remain forbidden. Main-document navigation to production-indicating hostnames is also blocked. These are configuration/hostname guards, not an authoritative inventory of every production hostname. Target profiles currently originate in frontend configuration, as with the existing browser diagnostic.

Driver creation, launch, navigation, read operations, observation and context cleanup are bounded (10, 30, 20, 5, 15 and 10 seconds respectively). Observation uses a cancellable periodic timer; it does not fill or click forms. One headless authentication diagnostic runs at a time per API instance.

## Evidence and first-blocker rules

Stages are Runtime, HeadlessEdgeLaunch, HeadlessBrowserControl, TargetNavigation, AuthenticationDetection, IdentityProviderDetection, NonInteractiveContinuation, MfaDetection, ConditionalAccessObservation, SessionControlObservation, AuthenticatedReturn, AuthenticatedSessionVerification, PostAuthenticationAutomationControl, HeadlessReadiness and Cleanup. Each is NotRun, Running, Passed, Blocked, Failed, Unknown or NotApplicable. Unreached stages remain NotRun.

The single primary blocker is one of None, BrowserAutomationBlocked, TargetNavigationBlocked, InteractiveAuthenticationRequired, InteractiveMfaRequired, ConditionalAccessBlocked, SessionControlHeadlessRestriction, AuthenticationSessionNotEstablished, AutomationControlLostAfterAuthentication, IdentityNotAvailableForAutomation or Unknown. Some values are reserved for evidence that V1 cannot currently establish; lack of a QA identity alone is not treated as proof that the current flow is blocked.

The reducer processes observations chronologically and stops at the first terminal finding. Later observations cannot overwrite it. On the same page an explicit CA error outranks normal sign-in form controls; a specific MFA challenge outranks generic login controls. This is same-page specificity, not a claim about hidden server-side chronology. Browser or target-control failure before observation prevents all authentication/MFA/CA/session-control assessment.

Redirects to `login.microsoftonline.com` identify Microsoft Entra ID. Configured authority origin and authentication type provide additional detection context; provider detection is separate from session verification. Credential/account-selection controls identify interactive sign-in. Known visible MFA controls and narrow challenge text identify human MFA. Unrecognized/localized challenges remain Unknown; absence never means MFA is disabled.

Only browser-visible AADSTS53003 on the Entra origin establishes an explicit CA block. Other Conditional Access wording is a signal, not a block. The report does not claim CA allowed authentication merely because no error appeared. IT must correlate timestamps with Entra sign-in logs; request/correlation identifiers and raw error payloads are not collected in V1.

HTTPS hosts ending in `.mcas.ms`, `.mcas-gov.us` or `.mcas-gov.ms` are session-control signals. Product wording without a matching host is possible involvement. A direct headless restriction on a session-control host is classified separately. Loss of control following an observed session-control stage is reported as that sequence with required IT confirmation, never as proof that MCAS caused it. Plain TargetClosedException is not a security-policy diagnosis.

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

The selector is read only at the exact configured target origin, with no login controls present. Do not configure a generic public shell selector such as `body` or `main`. Cookies, a callback URL, an HTTP 200 or the disappearance of Entra alone never establish authentication. Missing positive evidence produces Unknown on timeout. Proxied application-origin verification is deliberately conservative: it remains Unknown unless the exact target-origin verification contract is satisfied.

After verification, safe title and DOM reads must succeed for Ready. A verified session plus retained automation control plus no interaction yields Ready / None. A precise observed blocker yields Not Ready; uncertainty or a timeout yields Unknown. The result describes the current fresh diagnostic context; an approved future QA identity may behave differently. No QA identity authentication mechanism is implemented here.

## Privacy and IT report

The observer returns booleans only from DOM checks. No page text/HTML, input values, screenshots, cookies, browser storage, headers, network bodies or token payloads are captured. Main-frame navigation is capped at 100 entries. URLs keep only scheme, hostname/port and an allowlisted path category; userinfo, arbitrary paths, every query value and fragments are removed. Thus code, state, nonce, token fields, login_hint, SAMLResponse and RelayState cannot enter navigation evidence.

Copy IT report distinguishes observations from interpretation, includes the primary blocker, context and UTC times, and asks for an approved non-interactive DEV/QA authentication design. It never prescribes removing MFA or excluding MCAS. Optional attended comparison is not implemented: Diagnostic 1's headed/headless control comparison does not imply attended authentication success.

## Validation and manual acceptance

Run focused backend tests using `--filter FullyQualifiedName~HeadlessDiagnosticTests`, and frontend tests using `--filter FullyQualifiedName~HeadlessAuthDiagnosticCardTests`.

Real Edge adapter tests use local synthetic pages only. Set `RUN_HEADLESS_EDGE_DIAGNOSTIC_TESTS=true` and run `--filter FullyQualifiedName~HeadlessEdgeAcceptanceTests`; otherwise these tests explicitly skip. They seed prerequisite evidence to isolate the real headless adapter, so they are not an end-to-end validation of Diagnostic 1.

For target acceptance, run Browser Automation Diagnostic, record both mode results, then run this diagnostic only if headless target control is demonstrated. Preserve the report even if it stops at interactive login: MFA and later controls cannot be inferred beyond that point. A real DEV/QA result requires access to the configured environment and must not be fabricated from the synthetic tests.

## Technical references

- [Playwright .NET BrowserType persistent-context API](https://playwright.dev/dotnet/docs/api/class-browsertype)
- [Playwright browser channels and headless modes](https://playwright.dev/dotnet/docs/browsers)
- [Microsoft Entra error codes](https://learn.microsoft.com/en-us/entra/identity-platform/reference-error-codes)
- [Defender for Cloud Apps session-control troubleshooting](https://learn.microsoft.com/en-us/defender-cloud-apps/troubleshooting-proxy)
