using System.Net;
using System.Text;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.DependencyReview;
using BirkNext.BrowserCompanion;
using BirkNext.Dependencies;
using FluentAssertions;
using Moq;

namespace BirkNext.Api.Tests.Services.DependencyReview;

/// <summary>
/// Deployed evidence: one Blazor deployment-evidence capability with several strategies. A classic blazor.boot.json 404 is a strategy result
/// ("Not detected"), never the overall result; the modern runtime's embedded boot configuration, a PWA asset manifest and Browser Companion
/// runtime evidence are tried; capture capability stays separate from the capture result; artifacts never carry inferred package versions,
/// and comparison never maps a package to a same-named artifact or concludes absence from partial evidence. Local fixtures only.
/// </summary>
public sealed class BlazorDeploymentEvidenceTests
{
    private sealed class Routes(Dictionary<string, Func<HttpResponseMessage>> routes, Func<HttpResponseMessage>? fallback = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            var hit = routes.FirstOrDefault(r => path.EndsWith(r.Key, StringComparison.Ordinal)).Value;
            return Task.FromResult(hit?.Invoke() ?? fallback?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private static Func<HttpResponseMessage> Ok(string body) => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-22T10:00:00Z");
    private const string Index = "<html><head><base href=\"/\" /><script src=\"_framework/blazor.webassembly.js\"></script></head></html>";

    internal static BlazorDeploymentEvidenceSource Source(HttpMessageHandler handler, IBrowserCompanionService? companion = null)
    {
        var time = new FixedTime(Now);
        return new BlazorDeploymentEvidenceSource(new HttpClient(handler), companion ?? Mock.Of<IBrowserCompanionService>(c => c.Status(It.IsAny<string>()) == new BrowserCompanionStatus()), time);
    }

    private static IBrowserCompanionService Companion(DateTimeOffset capturedAt, params string[] urls)
    {
        var companion = new Mock<IBrowserCompanionService>();
        companion.Setup(c => c.Status(It.IsAny<string>())).Returns(new BrowserCompanionStatus());
        companion.Setup(c => c.Status("dev")).Returns(Status(capturedAt, urls));
        return companion.Object;
    }

    private static BrowserCompanionStatus Status(DateTimeOffset capturedAt, string[] urls) => new()
    {
        Pages =
        [
            new BrowserPageEvidence
            {
                ProfileId = "dev", PageOrigin = "https://m2lbdev.example.test", PagePath = "/", CapturedAt = capturedAt, BrowserName = "Edge",
                Performance = new BrowserPerformanceSummary { Timeline = [.. urls.Select(u => new BrowserResourceEntry { Url = u, Kind = "wasm", Status = 200 })] },
                Blazor = new BrowserBlazorSummary { Detected = true },
            },
        ],
    };

    private static Task<DeployedCaptureOutcome> Capture(BlazorDeploymentEvidenceSource source, string? profile = null) =>
        source.CaptureAsync(new DeployedCaptureRequest { TargetUrl = "https://m2lbdev.example.test", Environment = "Development", ProfileId = profile }, default);

    private const string ClassicBoot = """{"mainAssemblyName":"M2LB.Web","resources":{"assembly":{"M2LB.Web.wasm":"sha256-aaa","WolverineFx.wasm":"sha256-bbb"}}}""";

    [Fact]
    public async Task ClassicManifest_IsCaptured_AndFallbacksAreNotNeeded()
    {
        var handler = new Routes(new() { ["/"] = Ok(Index), ["blazor.boot.json"] = Ok(ClassicBoot) });

        var (inventory, report) = await Capture(Source(handler));

        report.Overall.Should().Be(CaptureOverall.Captured);
        report.Capability.Should().Be(CaptureCapability.Supported);
        report.RuntimeLayout.Should().Be("Classic boot manifest");
        report.Complete.Should().BeTrue();
        report.Strategies.Single(s => s.Strategy == "classic-boot-manifest").Should().Match<CaptureStrategyResult>(s => s.Outcome == CaptureStrategyOutcome.Captured && s.Artifacts == 2);
        report.Strategies.Where(s => s.Strategy is "modern-runtime" or "service-worker-assets").Should().OnlyContain(s => s.Outcome == CaptureStrategyOutcome.NotAttempted);
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get && r.Headers.Authorization == null);
        inventory!.Capture.Should().Be(report);
        inventory.Dependencies.Select(d => d.PackageName).Should().Equal("M2LB.Web.wasm", "WolverineFx.wasm");
    }

    [Fact]
    public async Task ArtifactsNeverCarryAPackageVersion()
    {
        var (inventory, _) = await Capture(Source(new Routes(new() { ["/"] = Ok(Index), ["blazor.boot.json"] = Ok(ClassicBoot) })));

        var wolverine = inventory!.Dependencies.Single(d => d.PackageName == "WolverineFx.wasm");
        wolverine.Version.Should().BeNull("an artifact file name is not package-version evidence (no 6.51.0 from any inventory)");
        wolverine.ObservedBy.Should().Equal(BlazorDeploymentEvidenceSource.Classic);
        inventory.Limitations.Should().Contain(l => l.Contains("package versions are not available from deployment evidence"));
    }

    [Fact]
    public async Task ClassicManifest404_ThenModernRuntime_IsCaptured_WithoutATerminalError()
    {
        var dotnet = "export const x=1;/*json-start*/{\"mainAssemblyName\":\"M2LB.Web\",\"resources\":{\"assembly\":[{\"virtualPath\":\"M2LB.Web.wasm\",\"name\":\"M2LB.Web.k3mzz8dq2n.wasm\",\"integrity\":\"sha256-ccc\"}]}}/*json-end*/;";
        var index = "<html><base href=\"/\" /><script type=\"importmap\">{\"imports\":{\"./_framework/dotnet.js\":\"./_framework/dotnet.wmlyg4zxxu.js\"}}</script></html>";
        var handler = new Routes(new() { ["/"] = Ok(index), ["dotnet.wmlyg4zxxu.js"] = Ok(dotnet) });

        var (inventory, report) = await Capture(Source(handler));

        report.Strategies.Single(s => s.Strategy == "classic-boot-manifest").Should().Match<CaptureStrategyResult>(s => s.Outcome == CaptureStrategyOutcome.NotDetected && s.Detail.Contains("HTTP 404"));
        report.Strategies.Single(s => s.Strategy == "modern-runtime").Should().Match<CaptureStrategyResult>(s => s.Outcome == CaptureStrategyOutcome.Captured && s.Artifacts == 1);
        report.Overall.Should().Be(CaptureOverall.Captured);
        report.RuntimeLayout.Should().Be("Modern Blazor runtime");
        report.Framework.Should().Be("Blazor WebAssembly");
        inventory!.Dependencies.Single().Should().Match<InventoryDependency>(d => d.PackageName == "M2LB.Web.wasm" && d.Hashes.Single().Value == "ccc");
        handler.Requests.Should().Contain(r => r.RequestUri!.AbsolutePath == "/_framework/dotnet.wmlyg4zxxu.js", "the import map resolves the fingerprinted runtime");
    }

    [Fact]
    public async Task ModernRuntimeWithoutStructuredMetadata_IsDetected_NotCaptured_AndNothingIsInferredFromMinifiedCode()
    {
        var (inventory, report) = await Capture(Source(new Routes(new() { ["/"] = Ok(Index), ["_framework/dotnet.js"] = Ok("var a=\"Foo.wasm\";") })));

        inventory.Should().BeNull();
        report.Strategies.Single(s => s.Strategy == "modern-runtime").Outcome.Should().Be(CaptureStrategyOutcome.Detected);
        report.Overall.Should().Be(CaptureOverall.NotCaptured);
        report.Capability.Should().Be(CaptureCapability.Partial, "the runtime is positively detected; only artifact extraction is unsupported");
        report.Framework.Should().Be("Blazor WebAssembly");
        report.Summary.Should().StartWith("Capture method unavailable: no supported capture method produced deployment evidence").And.Contain("may still exist", "one failed method is not \"deployment evidence irrelevant\"");
    }

    [Fact]
    public async Task ClassicManifest404_WithBrowserEvidence_IsPartial()
    {
        var companion = Companion(Now.AddMinutes(-5), "https://m2lbdev.example.test/_framework/M2LB.Web.k3mzz8dq2n.wasm", "https://m2lbdev.example.test/_framework/dotnet.native.wasm", "https://m2lbdev.example.test/app.css");

        var (inventory, report) = await Capture(Source(new Routes(new() { ["/"] = Ok(Index) }), companion), "dev");

        var browser = report.Strategies.Single(s => s.Strategy == "browser-runtime");
        browser.Should().Match<CaptureStrategyResult>(s => s.Outcome == CaptureStrategyOutcome.Partial && s.Artifacts == 2 && !s.Complete && s.Detail.StartsWith("Browser evidence from the current session"));
        report.Overall.Should().Be(CaptureOverall.Partial);
        report.Capability.Should().Be(CaptureCapability.Partial);
        report.Summary.Should().Contain("not necessarily all deployed artifacts");
        inventory!.Dependencies.Select(d => d.PackageName).Should().BeEquivalentTo("M2LB.Web.wasm", "dotnet.native.wasm");
        inventory.Limitations.Should().Contain(l => l.Contains("absent"));
    }

    [Fact]
    public async Task OldBrowserEvidence_IsLabelledHistorical()
    {
        var companion = Companion(Now.AddDays(-3), "https://m2lbdev.example.test/_framework/M2LB.Web.wasm");

        var (_, report) = await Capture(Source(new Routes(new() { ["/"] = Ok(Index) }), companion), "dev");

        report.Strategies.Single(s => s.Strategy == "browser-runtime").Detail.Should().StartWith("Historical browser evidence");
    }

    [Fact]
    public async Task ServiceWorkerAssetManifest_IsCaptured()
    {
        var sw = "self.assetsManifest = {\n  \"assets\": [\n    { \"hash\": \"sha256-ddd\", \"url\": \"_framework/M2LB.Web.wasm\" },\n    { \"hash\": \"sha256-eee\", \"url\": \"css/app.css\" }\n  ],\n  \"version\": \"abc\"\n};";

        var (inventory, report) = await Capture(Source(new Routes(new() { ["/"] = Ok(Index), ["service-worker-assets.js"] = Ok(sw) })));

        report.Strategies.Single(s => s.Strategy == "service-worker-assets").Outcome.Should().Be(CaptureStrategyOutcome.Captured);
        report.Overall.Should().Be(CaptureOverall.Captured);
        inventory!.Dependencies.Single().Should().Match<InventoryDependency>(d => d.PackageName == "M2LB.Web.wasm" && d.Hashes.Single().Value == "ddd");
    }

    [Fact]
    public async Task OnlyNotDetected_IsNotCaptured_ForAPositivelyDetectedBlazorApp()
    {
        var (inventory, report) = await Capture(Source(new Routes(new() { ["/"] = Ok(Index) })));

        inventory.Should().BeNull();
        report.Overall.Should().Be(CaptureOverall.NotCaptured);
        report.Framework.Should().Be("Blazor WebAssembly");
        report.Capability.Should().Be(CaptureCapability.Unsupported, "no supported strategy can capture this runtime layout");
        report.Strategies.Single(s => s.Strategy == "service-worker-assets").Detail.Should().Contain("not every Blazor app is a PWA");
        report.Strategies.Single(s => s.Strategy == "build-artifact").Outcome.Should().Be(CaptureStrategyOutcome.NotAvailable);
    }

    [Fact]
    public async Task NetworkFailure_IsFailed_NotNotDetected()
    {
        var handler = new Routes([], () => throw new HttpRequestException("down", null, HttpStatusCode.ServiceUnavailable));

        var (_, report) = await Capture(Source(handler));

        report.Strategies.Where(s => s.Strategy is "classic-boot-manifest" or "modern-runtime" or "service-worker-assets").Should().OnlyContain(s => s.Outcome == CaptureStrategyOutcome.Failed);
        report.Overall.Should().Be(CaptureOverall.Failed);
        report.Capability.Should().Be(CaptureCapability.Unknown);
    }

    [Fact]
    public async Task AuthenticationFailure_IsAuthenticationRequired_NotUnsupportedRuntime()
    {
        var (_, report) = await Capture(Source(new Routes([], () => new HttpResponseMessage(HttpStatusCode.Forbidden))));

        report.Strategies.Single(s => s.Strategy == "classic-boot-manifest").Outcome.Should().Be(CaptureStrategyOutcome.AuthenticationRequired);
        report.Capability.Should().NotBe(CaptureCapability.Unsupported);
        report.Overall.Should().Be(CaptureOverall.Failed);
    }

    [Fact]
    public async Task NonBlazorTarget_SaysNoBlazorMetadataWasFound()
    {
        var (_, report) = await Capture(Source(new Routes(new() { ["/"] = Ok("<html><script src=\"/main.js\"></script></html>") })));

        report.Framework.Should().Be("Not detected");
        report.Summary.Should().Contain("no Blazor WebAssembly deployment metadata was found");
        report.Capability.Should().Be(CaptureCapability.Unsupported);
    }

    private static DependencyInventorySnapshot Artifacts(bool complete, params (string Name, string? Hash)[] items) => new()
    {
        Id = Guid.NewGuid(), Name = "deployed", SourceType = InventorySourceType.Deployment, Stage = InventoryStage.Deployed, Capture = new DeploymentCaptureReport { Complete = complete },
        Dependencies = [.. items.Select(i => new InventoryDependency { PackageName = i.Name, PackageManager = "assembly", Stage = InventoryStage.Deployed,
            Hashes = i.Hash is null ? [] : [new DependencyHash("SHA-256 integrity (x)", i.Hash)] })],
    };

    [Fact]
    public void ArtifactComparison_IntegrityDifference_ExpectedNotObserved_AndUnexpected()
    {
        var expected = Artifacts(true, ("A.wasm", "111"), ("B.wasm", "222"), ("C.wasm", null)) with { Name = "build" };
        var observed = Artifacts(true, ("A.wasm", "111"), ("B.wasm", "999"), ("D.wasm", "444"));

        var comparison = InventoryComparer.ExpectedVsDeployed(expected, observed);

        comparison.Entries.Single(e => e.PackageName == "A.wasm").Should().Match<ComparisonEntry>(e => e.State == ComparisonState.Matched && e.Hash == HashComparison.Matched);
        comparison.Entries.Single(e => e.PackageName == "B.wasm").Should().Match<ComparisonEntry>(e => e.State == ComparisonState.IntegrityDifference && e.Detail.Contains("not a compromise"));
        comparison.Entries.Single(e => e.PackageName == "C.wasm").State.Should().Be(ComparisonState.MissingInDeployment, "a complete capture of an artifact set makes absence meaningful");
        comparison.Entries.Single(e => e.PackageName == "D.wasm").Should().Match<ComparisonEntry>(e => e.State == ComparisonState.ObservedNotExpected && e.Detail.Contains("Not an unauthorized"));
    }

    [Fact]
    public void PartialCapture_NeverConcludesAbsence()
    {
        var comparison = InventoryComparer.ExpectedVsDeployed(Artifacts(true, ("C.wasm", null)) with { Name = "build" }, Artifacts(false, ("A.wasm", null)));

        comparison.Entries.Single(e => e.PackageName == "C.wasm").State.Should().Be(ComparisonState.NotObservedInEvidence);
        comparison.Limitations[0].Should().StartWith("Partial comparison");
    }

    [Fact]
    public void AuthoritativeVersionedDeployedEvidence_StillComparesVersions()
    {
        var expected = new DependencyInventorySnapshot { Id = Guid.NewGuid(), Name = "inventory", Dependencies = [new InventoryDependency { PackageName = "M2LB.Common", Version = "2.4.1", PackageManager = "nuget" }] };
        var deployed = new DependencyInventorySnapshot { Id = Guid.NewGuid(), Name = "build mapping", Dependencies = [new InventoryDependency { PackageName = "M2LB.Common", Version = "2.4.1", PackageManager = "nuget", Stage = InventoryStage.Deployed }] };

        InventoryComparer.ExpectedVsDeployed(expected, deployed).Entries.Single().State.Should().Be(ComparisonState.Matched);
    }

    [Theory]
    [InlineData("""{"resources":{"assembly":{"X.dll":"sha256-a"}}}""", "X.dll")]
    [InlineData("""{"resources":{"assembly":[{"virtualPath":"X.wasm","name":"X.abc12345.wasm","integrity":"sha256-a"}],"coreAssembly":[{"name":"System.Private.CoreLib.wasm"}]}}""", "System.Private.CoreLib.wasm,X.wasm")]
    public void BootConfigurationShapesAreParsed(string json, string names) =>
        BlazorDeploymentMetadata.BootConfigArtifacts(json)!.Select(a => a.Name).Should().Equal(names.Split(','));

    [Fact]
    public void MetadataParsersNeverGuess()
    {
        BlazorDeploymentMetadata.BootConfigArtifacts("not json").Should().BeNull();
        BlazorDeploymentMetadata.BootConfigArtifacts("""{"other":1}""").Should().BeNull();
        BlazorDeploymentMetadata.EmbeddedBootConfig("var config = {resources:{}};").Should().BeNull("only the explicit json-start/json-end markers are read");
        BlazorDeploymentMetadata.ServiceWorkerArtifacts("console.log(1)").Should().BeNull();
        BlazorDeploymentMetadata.Logical("M2LB.Web.k3mzz8dq2n.wasm").Should().Be("M2LB.Web.wasm");
        BlazorDeploymentMetadata.Logical("System.Private.CoreLib.wasm").Should().Be("System.Private.CoreLib.wasm");
    }
}
