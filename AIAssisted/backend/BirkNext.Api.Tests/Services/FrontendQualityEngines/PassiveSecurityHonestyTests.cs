using BirkNext.Api.Services.FrontendAccessibility;
using BirkNext.Api.Services.FrontendBrowserRuntime;
using BirkNext.Api.Services.FrontendLighthouse;
using BirkNext.Api.Services.FrontendPassiveSecurity;
using BirkNext.Api.Services.FrontendQualityEngines;
using BirkNext.Api.Services.FrontendQualityEngines.Readiness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BirkNext.Api.Tests.Services.FrontendQualityEngines;

/// <summary>
/// Passive Security (ZAP) is optional and must never look runnable when it is not: server enablement (Layer 2), deployment capability
/// (Layer 1) and a server-registered trusted target are all required before status says Available. No real ZAP is needed here.
/// </summary>
public sealed class PassiveSecurityHonestyTests
{
    private const string TrustedProfile = "local-fixture";
    private const string TrustedUrl = "http://localhost:5199";

    private static string BackendRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    private sealed class AlwaysReady : IFrontendQualityEngineReadinessProvider
    {
        public AlwaysReady(FrontendQualityEngineId id) => EngineId = id;
        public FrontendQualityEngineId EngineId { get; }
        public Task<FrontendQualityEngineReadiness> CheckAsync(CancellationToken ct) => Task.FromResult(new FrontendQualityEngineReadiness(EngineId, true, null, DateTime.UtcNow));
    }

    private sealed class CountingRunner : IZapProcessRunner
    {
        public int Calls { get; private set; }
        public Task<ZapProcessResult> RunAsync(string file, IReadOnlyList<string> args, int timeoutMs, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ZapProcessResult(0, "ZAP 2.16.1", ""));
        }
    }

    private static IConfiguration Config(bool allowed, bool enabled, bool trusted = true) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["FrontendQualityCapabilities:PassiveSecurityAllowed"] = allowed.ToString(),
        ["FrontendQualityEnginePreferences:PassiveSecurityEnabled"] = enabled.ToString(),
        [$"FrontendPassiveSecurity:TrustedProfiles:{TrustedProfile}:BaseUrl"] = trusted ? TrustedUrl : null,
        [$"FrontendPassiveSecurity:TrustedProfiles:{TrustedProfile}:EnvironmentType"] = trusted ? "LoopbackLocalTest" : null,
    }).Build();

    private static FrontendQualityEngineStatusService Status(IConfiguration config)
    {
        var interpreter = new FrontendQualityEngineLegacyConfigInterpreter(NullLogger<FrontendQualityEngineLegacyConfigInterpreter>.Instance, config,
            Options.Create(new FrontendBrowserRuntimeOptions()), Options.Create(new FrontendAccessibilityOptions()), Options.Create(new FrontendLighthouseOptions()));
        var aggregator = new FrontendQualityEngineReadinessAggregator(
            Enum.GetValues<FrontendQualityEngineId>().Select(id => (IFrontendQualityEngineReadinessProvider)new AlwaysReady(id)).ToList(), NullLogger<FrontendQualityEngineReadinessAggregator>.Instance);
        return new FrontendQualityEngineStatusService(aggregator, interpreter, NullLogger<FrontendQualityEngineStatusService>.Instance, new PassiveSecurityTargetAuthorizer(new BrowserTargetValidator(), config));
    }

    private static FrontendQualityEngineStatusQuery Query(string profileId = TrustedProfile, string url = TrustedUrl + "/") =>
        new(ReviewAuthenticationMode.Anonymous, new FrontendQualityEngineSelectionContext(new Dictionary<FrontendQualityEngineId, bool>(), null, new FrontendQualityEngineTargetContext(profileId, url, "Local")));

    private static async Task<FrontendQualityEngineStatus> PassiveStatus(IConfiguration config, FrontendQualityEngineStatusQuery? query = null) =>
        (await Status(config).GetStatusAsync(query ?? Query())).Engines.Single(e => e.EngineId == FrontendQualityEngineId.PassiveSecurity);

    [Fact]
    public async Task ServerDisabled_IsUnavailable_DisabledInSystemSettings()
    {
        var status = await PassiveStatus(Config(allowed: true, enabled: false));
        Assert.False(status.Available);
        Assert.Contains(FrontendQualityEngineUnavailableReason.DisabledInSystemSettings, status.Reasons);
    }

    [Fact]
    public async Task CapabilityDisallowed_IsUnavailable_BlockedByDeploymentPolicy()
    {
        var status = await PassiveStatus(Config(allowed: false, enabled: true));
        Assert.False(status.Available);
        Assert.Contains(FrontendQualityEngineUnavailableReason.BlockedByDeploymentPolicy, status.Reasons);
    }

    [Fact]
    public async Task UntrustedProfile_IsNotAvailable_EvenWhenRuntimeIsReady()
    {
        var unregistered = await PassiveStatus(Config(true, true, trusted: false));
        Assert.False(unregistered.Available);
        Assert.True(unregistered.Layer3Readiness.IsAvailable);
        Assert.Contains(FrontendQualityEngineUnavailableReason.TargetNotTrusted, unregistered.Reasons);

        var otherProfile = await PassiveStatus(Config(true, true), Query(profileId: "some-ui-created-profile"));
        Assert.Contains(FrontendQualityEngineUnavailableReason.TargetNotTrusted, otherProfile.Reasons);

        var otherOrigin = await PassiveStatus(Config(true, true), Query(url: "http://localhost:6000/"));
        Assert.Contains(FrontendQualityEngineUnavailableReason.TargetNotTrusted, otherOrigin.Reasons);
    }

    [Fact]
    public async Task AllPrerequisites_IsAvailable()
    {
        var status = await PassiveStatus(Config(true, true));
        Assert.True(status.Available);
        Assert.Equal([FrontendQualityEngineUnavailableReason.None], status.Reasons);
    }

    [Fact]
    public async Task OtherEnginesAreNotAffectedByTheZapTargetRule()
    {
        var report = await Status(Config(true, true, trusted: false)).GetStatusAsync(Query());
        Assert.DoesNotContain(report.Engines.Where(e => e.EngineId != FrontendQualityEngineId.PassiveSecurity), e => e.Reasons.Contains(FrontendQualityEngineUnavailableReason.TargetNotTrusted));
    }

    [Fact]
    public async Task ServiceReadiness_WhenDisabled_StartsNoContainer()
    {
        var runner = new CountingRunner();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FrontendPassiveSecurity:Enabled"] = "false" }).Build();
        var service = new FrontendZapPassiveReviewService(NullLogger<FrontendZapPassiveReviewService>.Instance, new PassiveSecurityTargetAuthorizer(new BrowserTargetValidator(), config), new PassiveSecurityEvidenceSanitizer(), config, runner);
        var readiness = await service.CheckReadinessAsync();
        Assert.Equal(PassiveSecurityReadinessState.Disabled, readiness.State);
        Assert.False(readiness.Available);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void ReadinessBudget_CoversTheJvmStart_ButIsBounded()
    {
        Assert.True(PassiveSecurityReadinessProvider.ReadinessTimeout >= TimeSpan.FromSeconds(60));
        Assert.True(PassiveSecurityReadinessProvider.ReadinessTimeout <= TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void AppDefaults_KeepPassiveSecurityOff()
    {
        var appsettings = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(BackendRoot(), "BirkNext.Api", "appsettings.json")));
        Assert.False(appsettings.RootElement.GetProperty("FrontendPassiveSecurity").GetProperty("Enabled").GetBoolean());
    }
}
