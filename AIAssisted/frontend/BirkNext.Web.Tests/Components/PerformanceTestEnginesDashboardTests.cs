using BirkNext.PerformanceTests;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using P = BirkNext.Web.Services.PerformanceEngineCapabilityPresentation;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// System Settings → Performance Test Engines as a capability dashboard: everything rendered from the backend capability model, raw ids
/// only under Technical details, and Ready / Partial / Unavailable / Unsupported / Not implemented kept distinct.
/// </summary>
public sealed class PerformanceTestEnginesDashboardTests : BunitContext
{
    private sealed class Api : IPerformanceTestApiService
    {
        public List<PerformanceProviderStatus>? Providers { get; set; }
        public List<ResourceProviderCapability>? Resources { get; set; }
        public Task<List<PerformanceProviderStatus>?> ProvidersOrNullAsync(CancellationToken ct = default) => Task.FromResult(Providers);
        public Task<List<ResourceProviderCapability>?> ResourceProvidersOrNullAsync(CancellationToken ct = default) => Task.FromResult(Resources);
        public Task<List<PerformanceProviderStatus>> ProvidersAsync(CancellationToken ct = default) => Task.FromResult(Providers ?? []);
        public Task<List<ResourceProviderCapability>> ResourceProvidersAsync(CancellationToken ct = default) => Task.FromResult(Resources ?? []);
        public Task<PerformanceTestOverview?> OverviewAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<PerformanceTestOverview?>(null);
        public Task<PerformanceApiResult<PerformanceTestDefinition>> SaveDefinitionAsync(string e, PerformanceTestDefinition d, bool c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PerformanceApiResult<PerformanceTestDataProfile>> SaveDataProfileAsync(string e, PerformanceTestDataProfile p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PerformanceTestReadiness?> ReadinessAsync(string e, string d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PerformanceApiResult<PerformanceTestRun>> StartAsync(string e, PerformanceRunRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PerformanceTestRun?> RunAsync(string e, Guid r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<PerformanceTestRun>> RunsAsync(string e, string? d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PerformanceTestRun?> CancelAsync(string e, Guid r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<PerformanceBaseline>> BaselinesAsync(string e, string? d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PerformanceBaselinePromotionResult> PromoteAsync(string e, PerformanceBaselinePromotion r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PerformanceRunComparison?> CompareAsync(string e, Guid c, Guid? r, string? b, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private readonly Api _api = new();

    public PerformanceTestEnginesDashboardTests()
    {
        Services.AddSingleton<IPerformanceTestApiService>(_api);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static PerformanceProviderStatus K6(ProviderAvailability availability = ProviderAvailability.Available) => new()
    {
        ProviderId = PerformanceProviderIds.K6, DisplayName = "k6", Availability = availability, Version = "9.9.9",
        Detail = availability == ProviderAvailability.Available ? "k6 9.9.9 on Podman 7.1.0." : "The k6 image is not available locally.",
        Capabilities = new PerformanceProviderCapabilities { Http = true, GraphQl = true, Modes = [WorkloadMode.VirtualUsers, WorkloadMode.ArrivalRate], Cancellation = true },
        Runtime = new PerformanceRuntimeStatus { RuntimeId = PerformanceProviderIds.PodmanRuntime, DisplayName = "Podman", Version = "7.1.0", Availability = ProviderAvailability.Available, Detail = "Podman 7.1.0 is available." },
        Image = "docker.io/grafana/k6:9.9.9", ImagePresent = availability != ProviderAvailability.ImageMissing, ImageDigest = "sha256:" + new string('a', 64), AllowImagePull = false,
    };

    private static List<ResourceProviderCapability> Resources(string podman = "Partial") =>
    [
        new(ResourceProviderIds.Browser, "Browser memory", "Unsupported", "Not a resource provider: the JavaScript heap does not represent Blazor/.NET WASM managed memory.", [])
            { Scope = "Browser", Summary = "Browser JavaScript heap is not a reliable measure of Blazor/.NET WASM managed memory." },
        new(ResourceProviderIds.OpenTelemetry, "OpenTelemetry runtime metrics", "Not implemented", "Future provider.", []) { Scope = "External services", Summary = "Future provider." },
        new(ResourceProviderIds.Podman, "Podman container resources", podman, "Podman: CPU only — no delegated memory cgroup controller (rootless).",
            podman == "Unavailable" ? [] : [ResourceMetric.CpuPercent])
            { Scope = "Approved Podman containers", Summary = "CPU only; container memory accounting is unavailable on this host.",
              UnavailableMetrics = podman == "Unavailable" ? [ResourceMetric.ContainerMemoryBytes, ResourceMetric.CpuPercent] : [ResourceMetric.ContainerMemoryBytes] },
        new(ResourceProviderIds.DotNetRuntime, "BirkNext API runtime", "Available", "In-process runtime counters of the BirkNext API itself.",
            [ResourceMetric.WorkingSetBytes, ResourceMetric.ManagedHeapBytes, ResourceMetric.GcPauseMsPerMinute])
            { Scope = "BirkNext API process only", Summary = "Runtime counters from the BirkNext API process.",
              Limits = [new("External .NET targets", "Unsupported", "No process attachment or exported runtime telemetry provider is configured.")] },
    ];

    private IRenderedComponent<PerformanceTestEngineStatus> RenderPage()
    {
        var cut = Render<PerformanceTestEngineStatus>();
        cut.WaitForAssertion(() => cut.FindAll(".pte-card, .pte-error, .pte-empty").Should().NotBeEmpty());
        return cut;
    }

    private static string PrimaryText(IRenderedComponent<PerformanceTestEngineStatus> cut) =>
        string.Join(" ", cut.FindAll(".pte > :not(.disclosure)").Select(e => e.TextContent));

    private static string Status(IRenderedComponent<PerformanceTestEngineStatus> cut, string provider) =>
        cut.Find($"[data-testid=pte-resource][data-provider='{provider}'] .pte-card-head .sd-pill").GetAttribute("data-status")!;

    [Fact]
    public void ReadyK6_ShowsFactsAndChips_WithoutRawIdsInThePrimaryView()
    {
        _api.Providers = [K6()];
        _api.Resources = Resources();
        var cut = RenderPage();

        var engine = cut.Find("[data-testid=pte-provider]");
        engine.QuerySelector("h4")!.TextContent.Should().Be("k6");
        engine.QuerySelector(".pte-card-head .sd-pill")!.GetAttribute("data-status").Should().Be("Ready");
        engine.QuerySelector(".pte-card-head .sd-pill .visually-hidden")!.TextContent.Should().Be("k6 status: ");
        cut.Find("[data-testid=pte-runtime]").TextContent.Should().Contain("Podman 7.1.0");
        cut.Find("[data-testid=pte-image] dd").TextContent.Should().Be("grafana/k6:9.9.9");
        cut.Find("[data-testid=pte-image-status]").TextContent.Should().Contain("Present");
        cut.FindAll("[data-testid=pte-capabilities] li").Select(li => System.Text.RegularExpressions.Regex.Replace(li.TextContent, @"\s+", " ").Trim())
            .Should().Equal("✓ HTTP: supported", "✓ GraphQL queries: supported", "✓ Virtual users: supported", "✓ Arrival rate: supported", "✓ Cancellation: supported");

        var primary = PrimaryText(cut);
        primary.Should().NotContain("performance.k6").And.NotContain("container.podman").And.NotContain("sha256:").And.NotContain("resource.podman");
        cut.Find("[data-testid=pte-technical-body]").HasAttribute("hidden").Should().BeTrue("technical details are collapsed by default");
        cut.Find("[data-testid=pte-technical-body]").TextContent.Should().Contain("performance.k6").And.Contain("docker.io/grafana/k6:9.9.9").And.Contain("sha256:aaaa");
        cut.Find("[data-testid=pte-summary]").TextContent.Should().Contain("Ready").And.Contain("Podman 7.1.0").And.Contain("Partial");
        primary.Should().NotContain("%", "a capability page has no score");
    }

    [Fact]
    public void ImageMissing_IsNotReady_WithSetupGuidance_AndNoFailureTone()
    {
        _api.Providers = [K6(ProviderAvailability.ImageMissing)];
        _api.Resources = Resources();
        var cut = RenderPage();

        var pill = cut.Find("[data-testid=pte-availability] .sd-pill");
        pill.GetAttribute("data-status").Should().Be("Image missing");
        pill.ClassList.Should().Contain("sd-pill-muted").And.NotContain("sd-pill-attention");
        cut.Find("[data-testid=pte-reason]").TextContent.Should().Contain("not available locally");
        cut.Find("[data-testid=pte-guidance]").TextContent.Should().Contain("Pull the configured image on the BirkNext host");
        cut.Find("[data-testid=pte-image-status]").TextContent.Should().Contain("Missing");
    }

    [Fact]
    public void RuntimeUnavailable_IsNeverReady_AndPodmanResourcesAreUnavailable()
    {
        _api.Providers = [K6() with { Availability = ProviderAvailability.RuntimeUnavailable, Detail = "Podman: not running.",
            Runtime = K6().Runtime! with { Availability = ProviderAvailability.Unavailable, Version = null, Detail = "not running" } }];
        _api.Resources = Resources(podman: "Unavailable");
        var cut = RenderPage();

        cut.Find("[data-testid=pte-availability] .sd-pill").GetAttribute("data-status").Should().Be("Runtime unavailable");
        cut.Find("[data-testid=pte-runtime] .sd-pill").GetAttribute("data-status").Should().Be("Unavailable");
        Status(cut, ResourceProviderIds.Podman).Should().Be("Unavailable");
        cut.FindAll("[data-status=Ready]").Should().BeEmpty();
    }

    [Fact]
    public void PodmanPartial_ShowsCpuAvailable_AndMemoryUnavailable_NeverZero_WithAnAccessibleReason()
    {
        _api.Providers = [K6()];
        _api.Resources = Resources();
        var cut = RenderPage();

        Status(cut, ResourceProviderIds.Podman).Should().Be("Partial");
        var states = cut.FindAll("[data-provider='resource.podman'] [data-testid=pte-metric-states] > div")
            .Select(d => (d.QuerySelector("dt")!.TextContent, d.QuerySelector(".sd-pill")!.GetAttribute("data-status"))).ToList();
        states.Should().Equal(("CPU", "Available"), ("Container memory", "Unavailable"));
        cut.Find(".pte").TextContent.Should().NotMatchRegex(@"memory\s*:?\s*0\b");

        var toggle = cut.Find("[data-testid=pte-why-resource-podman-toggle]");
        toggle.TagName.Should().Be("BUTTON");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();
        cut.Find("[data-testid=pte-why-resource-podman-toggle]").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find("[data-testid=pte-why-resource-podman-body]").TextContent.Should().Contain("memory cgroup controller");
    }

    [Fact]
    public void BirkNextRuntime_IsScopedToTheBirkNextProcess_AndExternalDotNetIsUnsupported()
    {
        _api.Providers = [K6()];
        _api.Resources = Resources();
        var cut = RenderPage();

        var card = cut.Find("[data-provider='resource.dotnet.runtime']");
        card.QuerySelector("h4")!.TextContent.Should().Be("BirkNext API runtime");
        card.QuerySelector("[data-testid=pte-scope]")!.TextContent.Should().Be("Scope: BirkNext API process only");
        card.QuerySelectorAll("[data-testid=pte-metrics] li").Select(li => li.TextContent).Should().Equal("Working set", "Managed heap", "GC pause");
        var limit = card.QuerySelector("[data-testid=pte-limits] li")!;
        limit.TextContent.Should().Contain("External .NET targets").And.Contain("No process attachment");
        limit.QuerySelector(".sd-pill")!.GetAttribute("data-status").Should().Be("Unsupported");
    }

    [Fact]
    public void BrowserMemoryAndFutureProviders_AreSecondary_AndKeepDistinctStates()
    {
        _api.Providers = [K6()];
        _api.Resources = Resources();
        var cut = RenderPage();

        var other = cut.Find("[data-testid=pte-resources-other]");
        other.QuerySelectorAll("[data-testid=pte-resource]").Select(c => c.GetAttribute("data-provider")).Should().Equal(ResourceProviderIds.Browser, ResourceProviderIds.OpenTelemetry);
        cut.Find("[data-testid=pte-resources]").QuerySelectorAll("[data-testid=pte-resource]").Select(c => c.GetAttribute("data-provider"))
            .Should().Equal(ResourceProviderIds.DotNetRuntime, ResourceProviderIds.Podman);

        Status(cut, ResourceProviderIds.Browser).Should().Be("Unsupported");
        Status(cut, ResourceProviderIds.OpenTelemetry).Should().Be("Not implemented");
        cut.Find($"[data-provider='{ResourceProviderIds.OpenTelemetry}'] .sd-pill").ClassList.Should().Contain("pte-tone-planned");
        cut.Find($"[data-provider='{ResourceProviderIds.Browser}'] .sd-pill").ClassList.Should().NotContain("pte-tone-planned");
        cut.Find(".pte").TextContent.Should().NotContainEquivalentOf("leak detect");
    }

    [Fact]
    public void BackendError_ShowsOneErrorPanel_NotUnsupportedProviders()
    {
        _api.Providers = null;
        _api.Resources = null;
        var cut = RenderPage();

        cut.Find("[data-testid=pte-error]").TextContent.Should().Contain("could not be loaded");
        cut.FindAll(".pte-card").Should().BeEmpty();
        cut.FindAll("[data-status=Unsupported]").Should().BeEmpty();
    }

    [Fact]
    public void EmptyProviderSet_ShowsACleanEmptyState()
    {
        _api.Providers = [];
        _api.Resources = [];
        var cut = RenderPage();

        cut.Find("[data-testid=pte-none]").TextContent.Should().Be("No performance-test providers are installed.");
        cut.Find("[data-testid=pte-resource-none]").Should().NotBeNull();
        cut.Find(".pte").TextContent.Should().NotContain("Failed");
    }

    [Fact]
    public void MultipleEngines_RenderAsCards_ReadyFirst_WithUnsupportedCapabilitiesAsNeutralChips()
    {
        var fake = K6() with
        {
            ProviderId = "performance.fake", DisplayName = "Another engine", Availability = ProviderAvailability.Unavailable, Runtime = null, Image = null,
            Capabilities = new PerformanceProviderCapabilities { Http = true, Modes = [WorkloadMode.VirtualUsers] },
        };
        _api.Providers = [fake, K6()];
        _api.Resources = Resources();
        var cut = RenderPage();

        cut.FindAll("[data-testid=pte-provider] h4").Select(h => h.TextContent).Should().Equal("k6", "Another engine");
        cut.Find("#pte-engines-heading").TextContent.Should().Be("Engines");
        var chips = cut.FindAll("[data-testid=pte-provider]")[1].QuerySelectorAll("[data-testid=pte-capabilities] li");
        chips.Where(c => c.GetAttribute("data-supported") == "false").Select(c => c.QuerySelector(".visually-hidden")!.TextContent)
            .Should().Equal(": not supported", ": not supported", ": not supported");
        cut.Find("[data-testid=pte-summary]").TextContent.Should().Contain("Ready", "one ready engine is enough");
    }

    [Fact]
    public void Headings_AreSemantic_AndCardsAreNotInteractive()
    {
        _api.Providers = [K6()];
        _api.Resources = Resources();
        var cut = RenderPage();

        cut.Find("h2").TextContent.Should().Be("Performance Test Engines");
        cut.FindAll("h3").Select(h => h.TextContent).Should().Equal("Engine", "Resource Stability", "Unsupported and planned");
        cut.FindAll("article.pte-card").Should().OnlyContain(a => a.GetAttribute("tabindex") == null && a.GetAttribute("onclick") == null);
    }

    [Fact]
    public void Presentation_StatesStayDistinct_AndOrderIsDeterministic()
    {
        P.Resource("Unsupported").Should().NotBe(P.Resource("Not implemented"));
        P.Resource("Partial").Tone.Should().NotBe(P.Resource("Unavailable").Tone);
        P.Engine(ProviderAvailability.Available).Label.Should().Be("Ready");
        P.Engine(ProviderAvailability.Misconfigured).Label.Should().Be("Misconfigured");
        P.Order(Resources()).Select(r => r.Availability).Should().Equal("Available", "Partial", "Unsupported", "Not implemented");
        P.ShortImage("docker.io/grafana/k6:1.0.0").Should().Be("grafana/k6:1.0.0");
        P.ShortImage("registry.example.com/k6:1").Should().Be("registry.example.com/k6:1");
        P.ResourceSummary(Resources())!.Label.Should().Be("Partial");
        P.ResourceSummary(Resources().Where(r => r.Availability is "Unsupported" or "Not implemented").ToList()).Should().BeNull();
    }
}
