# External Frontend Quality Test Gating

This document describes how external tool execution is controlled in BirkNext.Api.Tests.

## Overview

External frontend quality tests (Browser Runtime, Accessibility, Lighthouse, ZAP, Authenticated Browser) require expensive external tools:
- **Playwright/Chromium** - browser automation
- **Axe** - accessibility scanning
- **Lighthouse/Node** - performance auditing  
- **ZAP/Podman** - security scanning
- **Local headed browser** - interactive GUI browser

These tests should NOT execute during normal `dotnet test` runs. They must be explicitly opted in to prevent:
- Long test execution times
- Unexpected process launches
- System resource consumption
- CI pipeline bloat

## Architecture

Three-layer defense:

1. **Runtime Defense** - `appsettings` default disables engines
2. **Test Time Defense** - test host configuration defaults to disabled
3. **Test Execution Gate** - explicit environment variable opt-in required

## Usage

### Normal Development Run (No External Tools)

```bash
dotnet test BirkNext.Api.Tests -c Release
```

Expected result:
- 913 deterministic tests execute
- 0 Chromium processes launched
- 0 Lighthouse/Node processes launched
- 0 ZAP/Podman containers launched
- Duration: ~40 seconds

### Run Browser Runtime Real Tests

```bash
set RUN_EXTERNAL_FRONTEND_QUALITY_TESTS=true
dotnet test BirkNext.Api.Tests -c Release --filter "Category=FrontendBrowserRuntimeIntegration"
```

Expected result:
- 6 real Playwright/Chromium tests execute
- Actual Chromium process launches and runs browser automation
- Duration: ~50 seconds

### Run Accessibility Tests

```bash
set RUN_EXTERNAL_FRONTEND_QUALITY_TESTS=true
dotnet test BirkNext.Api.Tests -c Release --filter "Category=FrontendAccessibilityIntegration"
```

Expected result:
- 5 real Axe + Chromium tests execute
- Actual Chromium process launches, Axe library executes
- Duration: ~10 seconds

### Run Lighthouse Tests

```bash
set RUN_EXTERNAL_FRONTEND_QUALITY_TESTS=true
dotnet test BirkNext.Api.Tests -c Release --filter "Category=FrontendLighthouseIntegration"
```

Expected result:
- 3 real Lighthouse tests execute
- Node process launches, Lighthouse CLI runs, Chromium launches
- Duration: ~100 seconds

### Run ZAP Passive Security Tests

```bash
set RUN_EXTERNAL_FRONTEND_QUALITY_TESTS=true
dotnet test BirkNext.Api.Tests -c Release --filter "Category=FrontendZapPassiveIntegration"
```

Expected result:
- 6 real ZAP tests execute
- Podman containers launch and run ZAP
- Duration: ~200 seconds

### Run Authenticated Browser Tests

For A2 and A3 real acceptance tests:

```bash
set RUN_LOCAL_AUTHENTICATED_BROWSER_TESTS=true
dotnet test BirkNext.Api.Tests -c Release --filter "Category=AuthenticatedReviewPhaseA2RealAcceptance"
```

For headed local browser test:

```bash
set RUN_LOCAL_AUTHENTICATED_BROWSER_TESTS=true
dotnet test BirkNext.Api.Tests -c Release --filter "Category=LocalHeadedPlaywright"
```

Expected result:
- Playwright browser launches (visible for headed test)
- Tests execute with real browser automation
- Duration: ~10-20 seconds

## Implementation Details

### ExternalFrontendQualityTestGate

Helper class in `TestInfrastructure/ExternalFrontendQualityTestGate.cs` provides:

```csharp
// Check if external tests are enabled
if (!ExternalFrontendQualityTestGate.IsEnabled) return;

// Check if local headed tests are enabled
if (!ExternalFrontendQualityTestGate.IsLocalHeadedEnabled) return;
```

### Environment Variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `RUN_EXTERNAL_FRONTEND_QUALITY_TESTS` | Enable all external tool tests | unset (disabled) |
| `RUN_LOCAL_AUTHENTICATED_BROWSER_TESTS` | Enable headed browser tests | unset (disabled) |

### Test Categories

| Category | Tests | External Tool | Guard |
|----------|-------|---|---|
| `FrontendBrowserRuntimeIntegration` | 6 | Playwright/Chromium | `IsEnabled` |
| `FrontendAccessibilityIntegration` | 5 | Chromium + Axe | `IsEnabled` |
| `FrontendLighthouseIntegration` | 3 | Node + Lighthouse + Chromium | `IsEnabled` |
| `FrontendZapPassiveIntegration` | 6 | Podman + ZAP | `IsEnabled` |
| `AuthenticatedReviewPhaseA2RealAcceptance` | 9 | Playwright + local HTTP | `IsLocalHeadedEnabled` |
| `AuthenticatedReviewPhaseA3RealAcceptance` | 8 | Playwright + local HTTP | `IsLocalHeadedEnabled` |
| `LocalHeadedPlaywright` | 1 | Visible Playwright browser | `IsLocalHeadedEnabled` |
| `ExternalEngineHardening` | 6 | None (mocked) | None - always runs |

### Guard Pattern

Each external test starts with an early return:

```csharp
[Fact]
public async Task BrowserRuntime_HealthyPage_StartsSuccessfully()
{
    if (!ExternalFrontendQualityTestGate.IsEnabled) return;
    
    // Test code follows...
}
```

This pattern:
- Executes before any fixture initialization
- Prevents external tool launches
- Is repository-standard (matched existing A2/A3 tests)
- Compatible with xUnit 2.5.3

## CI Integration

Configure jobs as follows:

### Main API Tests (Deterministic)

```yaml
job:
  steps:
    - run: dotnet test BirkNext.Api.Tests -c Release
```

No environment variables set. Expected: 913 tests, ~40 seconds, no external tools.

### Browser Runtime Integration

```yaml
job:
  env:
    RUN_EXTERNAL_FRONTEND_QUALITY_TESTS: "true"
  steps:
    - run: dotnet test BirkNext.Api.Tests -c Release --filter "Category=FrontendBrowserRuntimeIntegration"
```

### Accessibility Integration

```yaml
job:
  env:
    RUN_EXTERNAL_FRONTEND_QUALITY_TESTS: "true"
  steps:
    - run: dotnet test BirkNext.Api.Tests -c Release --filter "Category=FrontendAccessibilityIntegration"
```

### Lighthouse Integration

```yaml
job:
  env:
    RUN_EXTERNAL_FRONTEND_QUALITY_TESTS: "true"
  steps:
    - run: dotnet test BirkNext.Api.Tests -c Release --filter "Category=FrontendLighthouseIntegration"
```

### ZAP Security Scanning

```yaml
job:
  env:
    RUN_EXTERNAL_FRONTEND_QUALITY_TESTS: "true"
  steps:
    - run: dotnet test BirkNext.Api.Tests -c Release --filter "Category=FrontendZapPassiveIntegration"
```

### Authenticated Browser Tests

These require GUI/interactive environment (run only on desktop agents):

```yaml
job:
  # Only run on desktop/interactive agents
  env:
    RUN_LOCAL_AUTHENTICATED_BROWSER_TESTS: "true"
  steps:
    - run: dotnet test BirkNext.Api.Tests -c Release --filter "Category=AuthenticatedReviewPhaseA2RealAcceptance"
```

## Key Points

✅ **Normal test run is fast & safe** - 913 tests, ~40 sec, no external tools
✅ **Explicit opt-in required** - environment variables must be set
✅ **Categories are selective** - can run one category at a time
✅ **Guards before startup** - external tools never launch without explicit gate
✅ **Deterministic tests unaffected** - ExternalEngineHardening and others run normally
✅ **No special xUnit features needed** - works with xUnit 2.5.3
✅ **Three-layer defense** - runtime + test-time + execution gates

## Troubleshooting

**Problem**: Tests return early without running

**Reason**: Environment variable not set

**Solution**: Set `RUN_EXTERNAL_FRONTEND_QUALITY_TESTS=true` and/or `RUN_LOCAL_AUTHENTICATED_BROWSER_TESTS=true`

---

**Problem**: Normal run is taking too long (> 2 min)

**Reason**: External tool tests may be executing despite gate

**Solution**: Check that environment variables are NOT set. Run: `set RUN_EXTERNAL_FRONTEND_QUALITY_TESTS=` (unset)

---

**Problem**: Lighthouse tests fail with "lighthouse not found"

**Reason**: Node/Lighthouse tools not installed in test environment

**Solution**: This is expected in CI/remote environments. These tests should only run on agents with Node.js installed.

---

**Problem**: ZAP tests fail with "podman: command not found"

**Reason**: Podman/Docker not available in test environment

**Solution**: These tests should only run on agents with Podman or Docker installed.
