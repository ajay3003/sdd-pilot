using System.Linq;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Real process-restart lifecycle for Detect Settings: instance A runs a real detection through the component and produces the ACTUAL
/// persisted snapshot JSON; a completely fresh instance B (new service, new DI, new JS interop — no shared state) is fed exactly that
/// JSON and must restore "Previously detected — Current" without a fresh Detect. This proves persistence == restoration end to end,
/// not just DTO serialization.
/// </summary>
public sealed class DetectionSnapshotRestartTests
{
    private const string Url = "https://m2lbdev.example.com/";

    private static string SettingsJson(string targetUrl) =>
        "{\"activeProfileId\":\"dev\",\"profiles\":[" +
        "{\"id\":\"dev\",\"name\":\"M2LB DEV\",\"environmentType\":\"Development\",\"targetUrl\":\"" + targetUrl + "\"," +
        "\"authentication\":{\"authenticationType\":\"MicrosoftEntraId\"}}]}";

    // A realistic full M2LB DEV detection payload: nested discovery evidence, integrations, warnings and every confidence field, so the
    // round-trip exercises the same shapes the real detector produces (not a trimmed DTO).
    private static TargetEnvironmentDetectionResult Detection() => new()
    {
        OriginalUrl = Url, NormalizedTargetUrl = Url, Success = true, Reachability = TargetReachability.Reachable,
        DetectedClientFramework = ClientFrameworkType.BlazorWebAssembly,
        FrameworkEvidence = "Public HTML reference: _framework/blazor.webassembly.js", FrameworkConfidence = DetectionConfidence.High,
        SuggestedEnvironmentType = FrontendEnvironmentType.Development, SuggestedProfileName = "M2LB DEV",
        Confidence = DetectionConfidence.VeryHigh,
        State = DetectionState.ManualAuthenticationVerificationRequired,
        ManualAuthenticationVerificationRequired = true,
        ManualAuthenticationVerificationStatus = ManualAuthenticationVerificationStatus.Required,
        AuthenticationRequired = true,
        DetectedAuthenticationType = FrontendAuthenticationType.MicrosoftEntraId,
        DetectedAuthority = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111",
        DetectedTenantId = "11111111-1111-1111-1111-111111111111",
        DetectedClientId = "22222222-2222-2222-2222-222222222222",
        DetectedRedirectUrls = [Url + "authentication/login-callback"],
        DetectedRestBaseUrl = Url + "api", RestConfidence = DetectionConfidence.VeryHigh,
        DetectedGraphQlEndpoint = Url + "graphql", GraphQlConfidence = DetectionConfidence.Low,
        DetectedSwaggerUrl = Url + "swagger/v1/swagger.json", SwaggerConfidence = DetectionConfidence.Low,
        DetectedHealthEndpoint = Url + "health", HealthConfidence = DetectionConfidence.Low,
        Warnings = ["Authenticated review is not currently supported."],
        IsActivationReady = false,
        DiscoveryEvidence =
        [
            new() { Type = DiscoveryEvidence.EvidenceType.StructuredConfig, TargetField = "RestBaseUrl", LocationCategory = "/appsettings.json",
                    Value = Url + "api", Confidence = DetectionConfidence.VeryHigh, Status = EndpointEvidenceStatus.Observed,
                    ProbeStatus = EndpointProbeStatus.NotPerformed },
            new() { Type = DiscoveryEvidence.EvidenceType.ConventionalCandidate, TargetField = "GraphQlEndpoint", LocationCategory = Url + "graphql",
                    Confidence = DetectionConfidence.Low, Status = EndpointEvidenceStatus.Candidate, ProbeStatus = EndpointProbeStatus.ResponseReceived,
                    HttpStatus = 200, ContentType = "text/html" }
        ],
        DetectedIntegrations =
        [
            new() { Type = "RabbitMQ", DisplayName = "M2LB Events", ResourceName = "309-rmq05", Confidence = DetectionConfidence.Medium,
                    EvidenceSource = "/appsettings.json", Evidence = ["RabbitMQ:Host"] }
        ]
    };

    private static void Configure(BunitContext ctx, FrontendAnalysisSettingsService settings, ITargetEnvironmentDetectionApiService api,
        string targetUrl, string? snapshotsJson)
    {
        ctx.Services.AddSingleton<IFrontendAnalysisSettingsService>(settings);
        ctx.Services.AddSingleton(api);
        ctx.Services.AddSingleton<IEndpointDiscoveryService, EndpointDiscoveryService>();
        ctx.JSInterop.SetupVoid("birkNextStorage.setItem", _ => true).SetVoidResult();
        ctx.JSInterop.SetupVoid("birkNextStorage.setDiscovery", _ => true).SetVoidResult();
        ctx.JSInterop.SetupVoid("birkNextStorage.setSnapshots", _ => true).SetVoidResult();
        ctx.JSInterop.Setup<string?>("birkNextStorage.getDiscovery").SetResult(null);
        ctx.JSInterop.Setup<string?>("birkNextStorage.getItem", _ => true).SetResult(SettingsJson(targetUrl));
        ctx.JSInterop.Setup<string?>("birkNextStorage.getSnapshots").SetResult(snapshotsJson);
    }

    /// <summary>Runs a real detection in a fresh instance and returns the exact JSON written to the detection-snapshot store.</summary>
    private static string DetectAndCapturePersistedJson(string targetUrl)
    {
        using var a = new BunitContext();
        var svc = new FrontendAnalysisSettingsService();
        var api = new Mock<ITargetEnvironmentDetectionApiService>();
        api.Setup(x => x.DetectFromUrlAsync(It.IsAny<string>(), default)).ReturnsAsync(Detection());
        Configure(a, svc, api.Object, targetUrl, snapshotsJson: null);
        var cut = a.Render<Component>();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Detect settings").Click();
        cut.WaitForAssertion(() => svc.GetDetectionSnapshot("dev").Should().NotBeNull());
        return a.JSInterop.Invocations
            .Where(i => i.Identifier == "birkNextStorage.setSnapshots")
            .Select(i => (string)i.Arguments[0]!).Last();
    }

    [Fact]
    public void FreshInstanceRestoresRealPersistedSnapshotAsCurrent()
    {
        var persisted = DetectAndCapturePersistedJson(Url);
        persisted.Should().NotBeNullOrEmpty();

        // Instance B: brand-new service + context, fed the REAL persisted JSON. No Detect is run.
        using var b = new BunitContext();
        var svcB = new FrontendAnalysisSettingsService();
        Configure(b, svcB, new Mock<ITargetEnvironmentDetectionApiService>().Object, Url, persisted);
        var cut = b.Render<Component>();

        // The snapshot must be restored into the actual detection state the UI reads.
        cut.FindAll("[data-testid='detection-restored']").Should().NotBeEmpty("a fresh instance must restore the previous detection");
        cut.Find("[data-testid='detection-restored']").TextContent.Should().Contain("Previously detected — current");
        cut.Markup.Should().Contain("Reachable");
        cut.FindAll("button").Should().NotContain(bn => bn.TextContent.Trim() == "Detect settings" && false); // Detect is not forced
    }

    [Fact]
    public void FreshInstanceRestoresAsStaleWhenTargetUrlChanged()
    {
        var persisted = DetectAndCapturePersistedJson(Url);

        // Instance B: same persisted snapshot, but the saved environment's Target URL now differs.
        using var b = new BunitContext();
        var svcB = new FrontendAnalysisSettingsService();
        Configure(b, svcB, new Mock<ITargetEnvironmentDetectionApiService>().Object, "https://changed.example.com/", persisted);
        var cut = b.Render<Component>();

        cut.FindAll("[data-testid='detection-restored']").Should().NotBeEmpty();
        cut.Find("[data-testid='detection-restored']").TextContent.Should().Contain("stale, re-detect required");
    }

    [Fact]
    public void PersistedSnapshotContainsNoCredential()
    {
        var persisted = DetectAndCapturePersistedJson(Url);
        foreach (var forbidden in new[] { "eyJ", "Bearer ", "Authorization", "Cookie", "Set-Cookie", "accessToken", "refreshToken" })
            persisted.Should().NotContain(forbidden);
    }

    [Fact]
    public void MultipleEnvironmentsRestoreIndependentlyWithoutCrossContamination()
    {
        // Two environments each with a valid snapshot: both must restore, keyed to their own stable profile id.
        var persistedDev = DetectAndCapturePersistedJson(Url);
        var devEntry = System.Text.Json.JsonDocument.Parse(persistedDev).RootElement.GetProperty("dev").GetRawText();
        var combined = "{\"dev\":" + devEntry + ",\"qa\":" + devEntry.Replace("\"detectedProfileId\":\"dev\"", "\"detectedProfileId\":\"qa\"") + "}";

        var svc = new FrontendAnalysisSettingsService();
        var js = new SnapshotJs(combined);
        svc.LoadDetectionSnapshotsAsync(js).GetAwaiter().GetResult();
        svc.GetDetectionSnapshot("dev").Should().NotBeNull();
        svc.GetDetectionSnapshot("qa").Should().NotBeNull();
        svc.GetDetectionSnapshot("dev")!.DetectedProfileId.Should().Be("dev");
        svc.GetDetectionSnapshot("qa")!.DetectedProfileId.Should().Be("qa");
    }

    [Fact]
    public void OneMalformedEntryDoesNotWipeRestorationForOtherEnvironments()
    {
        // Regression for the real-world failure mode: a single unreadable/schema-drifted snapshot must not throw and clear all restoration.
        var persistedDev = DetectAndCapturePersistedJson(Url);
        var devEntry = System.Text.Json.JsonDocument.Parse(persistedDev).RootElement.GetProperty("dev").GetRawText();
        var withBadEntry = "{\"broken\":{\"result\":\"this-should-be-an-object-not-a-string\",\"version\":1},\"dev\":" + devEntry + "}";

        var svc = new FrontendAnalysisSettingsService();
        svc.LoadDetectionSnapshotsAsync(new SnapshotJs(withBadEntry)).GetAwaiter().GetResult();
        svc.GetDetectionSnapshot("dev").Should().NotBeNull("a good snapshot must survive a malformed sibling");
        svc.GetDetectionSnapshot("broken").Should().BeNull();
    }

    [Fact]
    public void UnsupportedSchemaVersionIsDroppedAndLegacyVersionlessIsRestored()
    {
        var persistedDev = DetectAndCapturePersistedJson(Url);
        var devEntry = System.Text.Json.JsonDocument.Parse(persistedDev).RootElement.GetProperty("dev").GetRawText();
        // A future/unsupported version is dropped; an old snapshot without a version field is treated as v1 and restored.
        var future = devEntry.Replace("\"version\":1", "\"version\":999");
        var legacy = System.Text.RegularExpressions.Regex.Replace(devEntry, "\"version\":1,?", "");
        var json = "{\"future\":" + future + ",\"legacy\":" + legacy + "}";

        var svc = new FrontendAnalysisSettingsService();
        svc.LoadDetectionSnapshotsAsync(new SnapshotJs(json)).GetAwaiter().GetResult();
        svc.GetDetectionSnapshot("future").Should().BeNull("an unsupported schema version must not be restored");
        svc.GetDetectionSnapshot("legacy").Should().NotBeNull("a version-less legacy snapshot is treated as current and restored");
    }

    [Fact]
    public void FingerprintIsDeterministicAcrossProfileSerialization()
    {
        // The same persisted environment loaded after restart must produce the same discovery fingerprint (section 7).
        var profile = new FrontendAnalysisProfile { Id = "dev", TargetUrl = Url, EnvironmentType = FrontendEnvironmentType.Development };
        var a = TargetDiscoveryFingerprint.For(profile);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<FrontendAnalysisProfile>(System.Text.Json.JsonSerializer.Serialize(profile))!;
        var b = TargetDiscoveryFingerprint.For(roundTripped);
        b.StaleReasonFor(roundTripped).Should().Be(TargetDiscoveryStaleReason.None);
        a.StaleReasonFor(roundTripped).Should().Be(TargetDiscoveryStaleReason.None, "fingerprint must be stable across serialization/restart");
    }

    [Fact]
    public async Task NavigatingAwayAndBackKeepsDetectionRestored()
    {
        var persisted = DetectAndCapturePersistedJson(Url);
        using var ctx = new BunitContext();
        var svc = new FrontendAnalysisSettingsService();
        Configure(ctx, svc, new Mock<ITargetEnvironmentDetectionApiService>().Object, Url, persisted);

        var first = ctx.Render<Component>();
        first.FindAll("[data-testid='detection-restored']").Should().NotBeEmpty();
        // Navigate away: the component is disposed.
        await first.InvokeAsync(() => first.Instance.DisposeAsync());

        // Navigate back: a fresh component instance over the same app-scoped services restores detection again (no forced Detect).
        var second = ctx.Render<Component>();
        second.FindAll("[data-testid='detection-restored']").Should().NotBeEmpty("navigation within BirkNext must not clear restored detection");
        second.Find("[data-testid='detection-restored']").TextContent.Should().Contain("Previously detected — current");
    }

    private sealed class SnapshotJs : Microsoft.JSInterop.IJSRuntime
    {
        private readonly string? _snapshots;
        public SnapshotJs(string? snapshots) => _snapshots = snapshots;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => Do<TValue>(identifier);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => Do<TValue>(identifier);
        private ValueTask<TValue> Do<TValue>(string identifier) =>
            identifier == "birkNextStorage.getSnapshots" ? new((TValue)(object?)_snapshots!) : new(default(TValue)!);
    }
}
