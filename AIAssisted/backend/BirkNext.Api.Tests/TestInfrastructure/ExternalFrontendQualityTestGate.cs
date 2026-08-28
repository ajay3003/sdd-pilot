namespace BirkNext.Api.Tests.TestInfrastructure;

/// <summary>
/// Gating mechanism for external frontend quality integration tests.
/// Prevents expensive external tools (Chromium, Lighthouse, ZAP, Docker/Podman)
/// from launching during normal test runs.
///
/// Tests requiring external tools must check IsEnabled or IsLocalHeadedEnabled before
/// starting any infrastructure. If not enabled, return early from the test method.
/// This ensures guards execute BEFORE fixture initialization.
///
/// Design:
/// - Layer 1: appsettings default OFF (production/runtime defense)
/// - Layer 2: test host default OFF (test-time defense)
/// - Layer 3: test execution opt-in (gate before fixture startup)
/// - Layer 4: category filter (select desired real gate)
/// </summary>
public static class ExternalFrontendQualityTestGate
{
    /// <summary>
    /// Environment variable that enables external frontend quality test execution.
    /// Set to "true" to allow Browser Runtime, Accessibility, Lighthouse, ZAP, and other
    /// external tool-backed tests to run. Case-insensitive.
    /// </summary>
    public const string EnvironmentVariableName = "RUN_EXTERNAL_FRONTEND_QUALITY_TESTS";

    /// <summary>
    /// Environment variable that enables local headed Playwright browser tests.
    /// Requires a GUI/interactive environment. Set to "true" to allow visible browser windows.
    /// Case-insensitive.
    /// </summary>
    public const string LocalHeadedEnvironmentVariableName = "RUN_LOCAL_AUTHENTICATED_BROWSER_TESTS";

    /// <summary>
    /// Returns true if external frontend quality tests are enabled.
    /// </summary>
    public static bool IsEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable(EnvironmentVariableName),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if local headed browser tests are enabled.
    /// Implies IsEnabled is also true (headed tests are a subset of external tests).
    /// </summary>
    public static bool IsLocalHeadedEnabled =>
        string.Equals(
            Environment.GetEnvironmentVariable(LocalHeadedEnvironmentVariableName),
            "true",
            StringComparison.OrdinalIgnoreCase);
}
