namespace BirkNext.Api.Tests.TestInfrastructure;

/// <summary>
/// Gate for tests that talk to the real M2LB Dev environment (https://m2lbdev.bufetat.no/) over the network.
///
/// These tests are deterministic in code but depend on an external environment (DNS, corporate network, target
/// availability). They are therefore NOT part of the normal deterministic suite. Instead of a permanent
/// <c>[Fact(Skip = ...)]</c> that can never run, they use <see cref="LiveM2LBFactAttribute"/>: when the opt-in variable is
/// absent the test is reported as <b>skipped with an explicit reason</b> (never as a silent pass); when it is set the
/// test executes for real.
///
/// Run on demand:
/// <code>
/// set RUN_LIVE_M2LB_TESTS=true
/// dotnet test BirkNext.Api.Tests -c Release --filter "Category=LiveM2LB"
/// </code>
/// or <c>AIAssisted/scripts/run-live-m2lb-tests.ps1</c>. Normal CI must not set the variable.
///
/// Security scope: the gated tests perform only the same unauthenticated public discovery GETs that the "Detect settings"
/// feature performs. They never enter credentials, never automate MFA, and never attach to a browser.
/// </summary>
public static class LiveM2LBTestGate
{
    public const string EnvironmentVariableName = "RUN_LIVE_M2LB_TESTS";
    public const string Category = "LiveM2LB";

    public const string SkipReason =
        "Live M2LB test: requires network access to https://m2lbdev.bufetat.no/ and explicit opt-in. " +
        "Set RUN_LIVE_M2LB_TESTS=true and run with --filter \"Category=LiveM2LB\" (or scripts/run-live-m2lb-tests.ps1).";

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariableName), "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// xUnit fact that is skipped with <see cref="LiveM2LBTestGate.SkipReason"/> unless <see cref="LiveM2LBTestGate.IsEnabled"/>.
/// Environment-conditional, not static: the same test binary runs the test when the opt-in variable is present.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LiveM2LBFactAttribute : FactAttribute
{
    public LiveM2LBFactAttribute()
    {
        if (!LiveM2LBTestGate.IsEnabled) Skip = LiveM2LBTestGate.SkipReason;
    }
}
