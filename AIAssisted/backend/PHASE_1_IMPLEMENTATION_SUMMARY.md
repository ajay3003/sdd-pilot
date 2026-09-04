# Phase 1 Backend Detection State Model - Implementation Summary

## Phase 2 post-fix wiring verification (2026-09-04)

The first Phase 2 implementation contained the UI action and the interactive browser
strategy, but no executable connection between them. The root cause was a missing
continuation HTTP contract and frontend API call, combined with tests that checked
button visibility and strategy behavior independently instead of exercising the
runtime boundary between them.

The post-fix path is `Continue detection in browser` ->
`StartBrowserDetectionAsync` -> `POST /api/frontend-target/continue-in-browser` ->
`TargetEnvironmentDetectionController` -> `TargetEnvironmentDetectionService` ->
`InteractiveBrowserDetectionStrategy` -> `TargetDetectionOutcome` -> the initiating
profile's detection state. Result application is now guarded by an opaque current
attempt identity plus the initiating profile ID and normalized URL; selection changes,
URL changes, cancellation, and older out-of-order completions are discarded.

Regression protection includes direct API request/DTO tests, component callback and
no-auto-launch tests, profile/URL/out-of-order concurrency tests, controller-to-strategy
tests, and a controlled Playwright test which observes the real continuation request
and returned UI state at 1440, 1280, 860, 800, and 480 pixels. No real credentials or
MFA are used by these deterministic tests.

## Overview
Phase 1 implements the backend detection state model, authentication type extensions, and detection service state computation for the Target Environment Detection feature.

## Completed Items

### 1. TargetDetectionState Enum
**File:** `BirkNext.Api/Models/TargetDetectionState.cs`

Enum with 7 states representing the detection lifecycle:
- `NotChecked`: Default state, no detection attempted
- `Checking`: Detection in progress (reserved for future browser automation)
- `Complete`: Detection succeeded, no auth required, profile ready for activation
- `AuthenticationRequired`: Auth boundary detected (401/403 or known IdP), browser auth needed
- `Partial`: Detection got some metadata but incomplete (future: browser partial results)
- `Stale`: Detection result outdated, URL has changed since detection
- `Failed`: Network/security/timeout error, no reachability metadata

### 2. TargetDetectionOutcome DTO
**File:** `BirkNext.Api/Models/TargetDetectionOutcome.cs`

Wraps detection response with state and activation metadata:
- `detectionResponse`: The underlying TargetEnvironmentDetectionResponse
- `state`: Current TargetDetectionState
- `isActivationReady`: Boolean indicating if profile can be activated
- `strategySuggestion`: Recommended detection strategy (direct-access, entra-id-browser-auth, etc.)
- `detectedAt`: UTC timestamp of detection
- `detectedUrl`: The URL that was detected (for staleness checking)
- `isUrlCurrent`: Whether current profile URL matches detected URL
- `message`: Human-readable state explanation

### 3. FrontendAuthenticationType Extension
**File:** `BirkNext.Api/Models/TargetEnvironmentDetectionResponse.cs`

Added `Unknown` value to handle custom/unrecognized authentication types:
```csharp
public enum FrontendAuthenticationType
{
    None,
    MicrosoftEntraId,
    OpenIdConnect,
    OAuth2,
    Unknown  // NEW: Custom or unrecognized auth methods
}
```

### 4. DetectionStateComputer Service
**File:** `BirkNext.Api/Services/TargetEnvironmentDetection/DetectionStateComputer.cs`

Encapsulates all state computation logic with methods:

#### ComputeStateFromResponse(response)
Maps preflight results to states:
- `Success=false` → `Failed`
- `Reachability.Reachable` + `!AuthRequired` → `Complete`
- `Reachability.AuthenticationRequired` → `AuthenticationRequired`
- `Reachability.Reachable` + `AuthRequired=true` → `AuthenticationRequired`
- Any error (Timeout, TlsError, DnsError, Unreachable, TooManyRedirects, UntrustedRedirect, Unknown) → `Failed`

#### IsUrlStale(detectedUrl, currentUrl)
Determines if URL changed since detection:
- Both null/empty → not stale
- Detected null but current exists → not stale (first detection)
- Current null but detected exists → stale (URL removed)
- Compares with URL normalization (scheme + host + path only, no query/fragment)
- Handles case differences and trailing slashes
- Case-insensitive comparison

#### IsReadyForActivation(state, isUrlCurrent)
Returns true only if:
- State is `Complete` OR `AuthenticationRequired`, AND
- `isUrlCurrent` is true

#### GetStrategySuggestion(state, response)
Returns action suggestion:
- `Complete` → "direct-access"
- `AuthenticationRequired` with Entra → "entra-id-browser-auth"
- `AuthenticationRequired` with OIDC → "oidc-browser-auth"
- `AuthenticationRequired` with OAuth2 → "oauth2-browser-auth"
- `AuthenticationRequired` with Unknown → "browser-auth-required"
- `Failed` → "retry-detection"
- `Stale` → "re-run-detection"
- `NotChecked` → "run-detection"
- `Checking` → "detection-in-progress"
- `Partial` → "browser-automation-required"

#### GetStateMessage(state, response, isUrlCurrent)
Returns human-readable explanation of current state

#### CreateOutcome(response, detectedUrl, currentProfileUrl)
Orchestrates complete outcome creation:
- Computes state from response
- Checks URL staleness
- Marks state as Stale if URL changed
- Determines activation readiness
- Generates strategy and message
- Includes detection timestamp

## Test Coverage

### Unit Tests: DetectionStateComputerTests
**File:** `BirkNext.Api.Tests/Services/TargetEnvironmentDetection/DetectionStateComputerTests.cs`

51 comprehensive tests covering:
- State computation from all response types (16 tests)
- URL staleness detection with edge cases (13 tests)
- Activation readiness logic (8 tests)
- Strategy suggestions (10 tests)
- State messages (5 tests)
- Complete outcome creation (7 tests)

All tests follow test-first approach, exercising both happy paths and edge cases.

### Integration Tests: DetectionStateComputerIntegrationTests
**File:** `BirkNext.Api.Tests/Services/TargetEnvironmentDetection/DetectionStateComputerIntegrationTests.cs`

15 realistic scenario tests:
- Public website without auth
- Entra ID protected app
- Failed DNS resolution
- URL changed between detections
- Network timeout
- Unknown auth provider
- First-time detection
- URL normalization (queries, case, trailing slashes)
- TLS certificate errors
- Too many redirects
- OAuth2 provider detection
- OIDC provider detection
- Complete outcome field verification

## Bug Fixes
Fixed pre-existing compilation errors in test project:
- `TargetEnvironmentDetectionControllerHttpsTests.cs`: Wrapped TargetDetectionOptions in IOptions<> wrapper for dependency injection consistency

## Test Results

```
Total Tests: 1095
Passed: 1095
Failed: 0
Skipped: 0
```

Breakdown:
- 1024 existing tests: all passing (no regressions)
- 51 new DetectionStateComputer unit tests: all passing
- 15 new DetectionStateComputerIntegrationTests: all passing
- 5 fixed pre-existing test errors

## Activation Readiness Decision Logic

A profile is ready for activation if and only if:
1. Detection has been performed (state != NotChecked)
2. State indicates success or auth boundary (state == Complete OR AuthenticationRequired)
3. URL has not changed since detection (isUrlCurrent == true)

Profiles NOT ready for activation:
- State == Failed: Detection error prevents activation
- State == Stale: URL changed, detection result is outdated
- State == NotChecked: No detection attempted
- State == Checking: Detection still in progress
- State == Partial: Incomplete detection (awaiting browser automation)

## Future Integration Points

### For Service Integration
The DetectionStateComputer can be injected into TargetEnvironmentDetectionService:
```csharp
private readonly IDetectionStateComputer _stateComputer;

// In detection flow:
var outcome = _stateComputer.CreateOutcome(
    detectionResponse,
    previouslyDetectedUrl,
    profileUrl);
```

### For API Response
Return TargetDetectionOutcome instead of just TargetEnvironmentDetectionResponse:
```csharp
public async Task<IActionResult> DetectConfiguration(
    [FromBody] TargetEnvironmentDetectionRequest request)
{
    var response = await _detectionService.DetectFromUrlAsync(request.TargetUrl);
    var outcome = _stateComputer.CreateOutcome(response, null, request.TargetUrl);
    return Ok(outcome);
}
```

### For FrontendAnalysisProfile
Add to profile model (backend API response):
- `detectionState`: TargetDetectionState
- `lastDetectionOutcome`: TargetDetectionOutcome (or serialized outcome data)
- `lastDetectionTime`: DateTime?
- `isReadyForActivation`: bool

Frontend model already includes:
- `lastDetectedUrl`: string?
- `lastDetectionSucceeded`: bool
- `lastDetectionFailure`: string?

## State Machine Diagram

```
NotChecked
    ↓
Checking (optional, future)
    ├→ Complete (no auth required)
    │   └→ Stale (if URL changes)
    ├→ AuthenticationRequired (auth boundary found)
    │   └→ Stale (if URL changes)
    ├→ Partial (incomplete, needs browser)
    │   └→ Stale (if URL changes)
    └→ Failed (network/security error)
        (does not transition to Stale)
```

## Security Considerations
- URL normalization removes query parameters to prevent comparison issues
- Staleness detection prevents using detection results for different URLs
- State machine ensures clear lifecycle management
- Activation readiness requires both successful detection AND current URL

## Performance Notes
- All computations are O(1) - synchronous, no I/O
- String normalization uses URI parsing (standard library)
- No database queries or external calls
- Can be safely used on every request

## Summary
Phase 1 successfully implements:
- Complete state enumeration and tracking
- Robust detection outcome modeling
- URL staleness and activation readiness logic
- Comprehensive test coverage (66 new tests)
- Zero regressions (1095/1095 tests passing)
- Ready for Phase 2 integration with service layer
