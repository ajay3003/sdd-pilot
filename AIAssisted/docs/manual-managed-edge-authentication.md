# Managed Edge manual authentication verification

Enterprise sign-in requiring a managed Edge work profile is a supported manual verification gate. It does not require BirkNext to own an authenticated browser session.

## Configuration

Development configuration explicitly lists `m2lbdev.bufetat.no` in `TargetDetection:ManualManagedEdgeHosts`. Host matching is exact and case insensitive. Other deployments can configure their own list. No Microsoft login-page text is scraped to infer this policy.

Profiles also offer Authentication verification = Manual managed Microsoft Edge (`Authentication.VerificationMode = ManualManagedEdge`). Selecting this mode changes the draft; Save changes remains explicit. Configured authentication `None` means no authentication settings were preconfigured, not proof of anonymous access.

## Flow and ownership audit

Before this change:

1. `frontend/BirkNext.Web/Components/FrontendAnalysisSettings.razor`: `DetectSelectedTargetAsync` / `DetectConfigurationAsync` request server preflight. `CanContinueDetectionInBrowser` offers continuation for authentication or SPA runtime inspection. `StartBrowserDetectionAsync` requests an owned browser.
2. `frontend/BirkNext.Web/Services/TargetEnvironmentDetectionApiService.cs`: `DetectFromUrlAsync` and `StartBrowserDetectionAsync` call the detection endpoints.
3. `backend/BirkNext.Api/Controllers/TargetEnvironmentDetectionController.cs`: `DetectConfiguration` runs preflight; `ContinueDetectionInBrowser` constructs the interactive strategy.
4. `backend/BirkNext.Api/Services/TargetEnvironmentDetection/TargetEnvironmentDetectionService.cs`: `DetectWithStrategyAsync` sends authentication challenges and browser-runtime requirements into the strategy.
5. `backend/BirkNext.Api/Services/TargetEnvironmentDetection/InteractiveBrowserDetectionStrategy.cs`: `ContinueDetectionAsync` calls session-manager `StartAsync`, initiates authentication and polls session status. Completion depends on owned-session authentication.
6. `backend/BirkNext.Api/Services/AuthenticatedReview/AuthenticatedBrowserSessionManager.cs`: `StartAsync` calls the host; `BeginAuthenticationAsync` owns the single target navigation and observation.
7. `backend/BirkNext.Api/Services/AuthenticatedReview/PlaywrightAuthenticatedBrowserHost.cs`: `LaunchAsync` creates isolated Edge resources through `CreateLaunchOptionsForEdge`, channel `msedge`.

After this change, configured host policy is applied during preflight in `ApplyTypedOutcome`. Explicit profile mode is transmitted by `DetectManualManagedEdgeAsync` and applied by the controller. `ManualAuthenticationVerification.Apply` produces `ManualAuthenticationVerificationRequired`, separate verification status `Required`, no browser-runtime requirement, and readiness false. Failed target preflight remains failed. `DetectWithStrategyAsync` returns before strategy invocation for the manual policy. Explicit manual continuation also returns before resolving the browser manager.

The existing browser host, single navigation, observer, authentication state machine, and isolated Edge behavior remain available for supported automated environments and local fixtures.

## User verification and readiness

Open verification instructions does not authenticate anything or record Passed. The user opens the target in normal managed Edge, signs in with a work account, completes PIN/passkey/MFA manually, and continues MCAS/Defender manually if shown. After reaching the authenticated application, they return and explicitly mark Passed or Failed. Retry repeats these manual steps.

Recording requires a current successful manual preflight and saved, valid configuration. Authentication attestation is separate from target completeness: a pass can be recorded while other detection gates still block activation. Activation additionally requires detected framework metadata, no unresolved detection warnings, matching context and Passed. It never activates automatically. Existing review-engine readiness requirements are not waived and the evidence cannot supply a browser session to a review engine.

## Persistence and invalidation

The profile stores only method, result, UTC verification timestamp, profile ID, target origin and a SHA-256 context fingerprint. There is no raw login identity, note, credential, token, cookie, storage state, Edge profile, or browser-session transfer. No CDP, profile attachment, profile-switch automation, MFA automation, or MCAS automation is added.

The context fingerprint covers profile ID, exact frontend URL, environment type, authentication settings, timeout/retry settings, security expectations and target allowlists. A changed context evaluates as Stale on edit and reload; the historical result remains available as secondary evidence. Duplicating a profile clears evidence. Reload starts with neutral current detection and historical verification; a fresh preflight is required before activation. No arbitrary time-based expiry is imposed.

## Acceptance evidence

Real M2LB DEV server preflight returned Reachable, BlazorWebAssembly, Development, ManualAuthenticationVerificationRequired, Required, BrowserRuntimeInspectionRequired=false and IsActivationReady=false. An isolated Edge visiting only BirkNext verified that this real preflight renders the manual notice and keyboard-accessible instructions with activation disabled. No real sign-in or real Passed result was automated or recorded. Managed-profile sign-in and user confirmation remain manual acceptance steps.
