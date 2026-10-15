using System.Runtime.CompilerServices;

namespace BirkNext.Api.Tests.Utilities;

/// <summary>
/// Keeps every test host away from real Altinn. Hosts started by this test process (WebApplicationFactory) read configuration from process
/// environment variables, so the Altinn TT02 endpoints are pointed at reserved <c>.invalid</c> hosts (RFC 2606: they never resolve) through the
/// normal endpoint configuration. A test that accidentally becomes able to read metadata or run fails with an unreachable host instead of
/// contacting TT02. Unit tests that build <c>AltinnTestingOptions</c> themselves are unaffected and use in-process fakes.
/// </summary>
internal static class AltinnNetworkSafety
{
    public const string PlatformBaseUrl = "https://platform.tt02.altinn.invalid/";
    public const string AppsDomain = "apps.tt02.altinn.invalid";

    [ModuleInitializer]
    internal static void PointTestHostsAwayFromAltinn()
    {
        Environment.SetEnvironmentVariable("AltinnTesting__Endpoints__Test__PlatformBaseUrl", PlatformBaseUrl);
        Environment.SetEnvironmentVariable("AltinnTesting__Endpoints__Test__AppsDomain", AppsDomain);
    }
}
