using System.Net;
using BirkNext.Integrations;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Target Environment → External Integrations: the provider combobox renders only the selected provider's fields, every system shows the same
/// five dimensions, Altinn uses its own provider component, Detect never saves, import shows the fail-closed preflight with owner links and
/// explicit conflict decisions, export is scope- and provider-aware, and the Setup Library filters by provider. Skolenærvær light rows name one
/// owner per unresolved dimension.
/// </summary>
public sealed class ExternalIntegrationsUiTests : BunitContext
{
    private const string Env = "dev";
    private readonly FakeApi _api = new();

    public ExternalIntegrationsUiTests()
    {
        Services.AddSingleton<IExternalIntegrationsApiService>(_api);
        var altinn = new Mock<IAltinnEnvironmentApiService>();
        altinn.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ActiveEventApiResult<AltinnEnvironmentView>(AltinnEnvironmentUiTests.View(), null));
        Services.AddSingleton(altinn.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static IReadOnlyList<string> FieldKeys(IRenderedComponent<ExternalIntegrationsPane> cut) =>
        [.. cut.FindAll("[data-testid=ei-add-form] [data-testid=psf-field]").Select(field => field.GetAttribute("data-key")!)];

    [Fact]
    public void Provider_combobox_renders_only_the_selected_providers_fields()
    {
        var cut = Render<ExternalIntegrationsPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ei-add]").HasAttribute("disabled").Should().BeFalse());
        cut.Find("[data-testid=ei-add]").Click();

        FieldKeys(cut).Should().Contain(["appId", "formDataType", "clientId", "redirectUri"]).And.NotContain(["namespace", "openApiUrl", "clientSecret"]);
        cut.Find("[data-testid=psf-secret-note]").TextContent.Should().Contain("never stored or exported");
        cut.FindAll("[data-testid=ei-add-form] input[type=password]").Should().BeEmpty("a secret is never part of the integration form");

        cut.Find("[data-testid=ei-provider]").Change(nameof(ExternalProviderType.RestApi));
        FieldKeys(cut).Should().Contain(["baseUrl", "openApiUrl", "authScheme"]).And.NotContain(["appId", "formDataType", "namespace"]);
        cut.Find("[data-testid=ei-provider]").Change(nameof(ExternalProviderType.AzureServiceBus));
        FieldKeys(cut).Should().Contain(["namespace", "entityType", "entity", "subscription"]).And.NotContain(["appId", "baseUrl"]);
        cut.Find("[data-testid=ei-add-form] [data-key=entityType] select").QuerySelectorAll("option").Select(o => o.TextContent).Should().Contain(["Queue", "Topic"]);
    }

    [Fact]
    public void Saving_sends_only_the_selected_providers_settings()
    {
        var cut = Render<ExternalIntegrationsPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ei-add]").Click());
        cut.Find("[data-testid=ei-provider]").Change(nameof(ExternalProviderType.RestApi));
        cut.Find("[data-testid=ei-name]").Change("Person API");
        cut.Find("[data-testid=ei-add-form] [data-key=baseUrl] input").Change("https://person.qa.example/api");
        cut.Find("[data-testid=ei-create]").Click();

        cut.WaitForAssertion(() => _api.Saved.Should().ContainSingle());
        _api.Saved[0].ProviderType.Should().Be(ExternalProviderType.RestApi);
        _api.Saved[0].Settings.Should().Equal(new Dictionary<string, string> { ["baseUrl"] = "https://person.qa.example/api" });
    }

    [Fact]
    public void Cards_share_five_dimensions_and_altinn_reviews_with_its_provider_component()
    {
        var cut = Render<ExternalIntegrationsPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=ei-card]").Count.Should().Be(2));
        foreach (var card in cut.FindAll("[data-testid=ei-card]"))
            card.QuerySelectorAll("[data-testid=ei-dims] > div").Select(d => d.GetAttribute("data-key")).Should().Equal("configuration", "dependencies", "authentication", "runtime", "execution");
        var altinn = cut.Find("[data-testid=ei-card][data-provider=Altinn]");
        altinn.TextContent.Should().Contain("Altinn – Skolenærvær").And.Contain("Observed").And.Contain("Blocked");
        altinn.QuerySelector("[data-testid=ei-review]")!.Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ae]"), TimeSpan.FromSeconds(2));
        cut.FindAll("[data-testid=ei-card][data-provider=Altinn] [data-testid=psf]").Should().BeEmpty("Altinn uses its own provider component");
    }

    [Fact]
    public void Altinn_deep_link_opens_the_altinn_system()
    {
        var cut = Render<ExternalIntegrationsPane>(p => p.Add(c => c.EnvironmentId, Env).Add(c => c.Focus, "altinn"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ae]"));
    }

    [Fact]
    public void Detect_proposes_without_saving_and_accept_is_explicit()
    {
        var cut = Render<ExternalIntegrationsPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ei-detect]").HasAttribute("disabled").Should().BeFalse());
        cut.Find("[data-testid=ei-detect]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=ei-proposal]").TextContent.Should().Contain("Payments API").And.Contain("Detected from: Integration Quality Review"));
        _api.Saved.Should().BeEmpty("Detect never saves");
        cut.Find("[data-testid=ei-notice]").TextContent.Should().Contain("Nothing was saved");
        cut.Find("[data-testid=ei-accept]").Click();
        cut.WaitForAssertion(() => _api.Saved.Should().ContainSingle(s => s.Name == "Payments API" && s.ProviderType == ExternalProviderType.RestApi));
    }

    [Fact]
    public void Blocked_import_shows_owner_links_and_apply_needs_every_conflict_decided()
    {
        _api.Preflight = Preflight(blocked: true);
        var cut = Render<SetupPackagePanel>(p => p.Add(c => c.EnvironmentId, Env).Add(c => c.Import, true).Add(c => c.Providers, Providers()));
        cut.WaitForAssertion(() => cut.Find("[data-testid=spp-library]"));
        cut.Find("[data-testid=spp-library]").Change(_api.Library[0].Id.ToString());
        cut.Find("[data-testid=spp-preflight]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=spv-status]").TextContent.Should().Be("IMPORT BLOCKED"));
        cut.Find("[data-testid=spv-required]").TextContent.Should().Be("1 / 3 resolved");
        cut.FindAll("[data-testid=spv-owner]").Select(a => a.GetAttribute("href")).Should().Contain(["/test-data", "/integration-quality-review"]);
        cut.Find("[data-testid=spv-optional]").TextContent.Should().Be("Import can proceed with 1 optional capability unavailable.");
        cut.Find("[data-testid=spv-never]").TextContent.Should().Be("0 · 0 · 0");
        cut.Find("[data-testid=spp-apply]").HasAttribute("disabled").Should().BeTrue("required dependencies are unresolved");

        _api.Preflight = Preflight(blocked: false);
        cut.Find("[data-testid=spp-preflight]").TextContent.Should().Be("Rerun preflight");
        cut.Find("[data-testid=spp-preflight]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=spv-status]").TextContent.Should().Be("IMPORT READY"));
        cut.Find("[data-testid=spp-apply]").HasAttribute("disabled").Should().BeTrue("the conflict has no decision yet");
        cut.FindAll("[data-testid=spv-decision] input")[0].Change(true); // Keep current
        cut.Find("[data-testid=spp-apply]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=spp-apply]").Click();
        cut.WaitForAssertion(() => _api.LibraryDecisions.Should().ContainSingle().Which.Should().Equal(new Dictionary<string, bool> { ["0:baseUrl"] = false }));
    }

    [Fact]
    public void Export_is_scope_and_provider_aware()
    {
        var integrations = new List<ExternalIntegration> { new() { Id = "dev:altinn", Name = "Altinn – Skolenærvær", ProviderType = ExternalProviderType.Altinn } };
        var cut = Render<SetupPackagePanel>(p => p.Add(c => c.EnvironmentId, Env).Add(c => c.Providers, Providers()).Add(c => c.Integrations, integrations));
        cut.FindAll("[data-testid=spp-provider]").Should().BeEmpty("the environment scope needs no provider");
        cut.Find("[data-testid=spp-scope]").Change(nameof(SetupScope.Integration));
        cut.Find("[data-testid=spp-export-yaml]").HasAttribute("disabled").Should().BeTrue("choose the integration first");
        cut.Find("[data-testid=spp-integration]").Change("dev:altinn");
        cut.Find("[data-testid=spp-export-yaml]").Click();
        cut.WaitForAssertion(() => _api.Exports.Should().ContainSingle(e => e == "Integration|Altinn|dev:altinn|Yaml"));
        cut.Find("[data-testid=spp-excluded]").TextContent.Should().Contain("Secrets");
    }

    [Fact]
    public void Setup_library_filters_by_provider()
    {
        var cut = Render<SetupLibraryPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=slp-item]").Should().ContainSingle());
        cut.Find("[data-testid=slp-filter-provider]").Change(nameof(ExternalProviderType.Altinn));
        cut.WaitForAssertion(() => _api.LibraryQueries.Last().Should().Be("Altinn"));
        cut.Find("[data-testid=slp-open]").Click();
        cut.Find("[data-testid=slp-versions]").TextContent.Should().Contain("v2").And.Contain("v1");
        cut.Find("[data-testid=slp-sanitization]").TextContent.Should().Contain("Secrets");
    }

    [Fact]
    public void Skolenaervaer_light_rows_name_one_owner_per_unresolved_dimension()
    {
        static JourneyPrerequisite Row(string key, JourneyReadinessState state) => new(key, key, JourneyPrerequisiteCategory.Integration, state, $"{key} detail");
        var ready = new[] { "altinn-environment", "altinn-app", "integration:report-intake", "synthetic-reporter", "reporter-profile", "synthetic-data", "submission-contract", "process-contract" }
            .Select(key => Row(key, JourneyReadinessState.Ready)).ToList();
        SkoleNaervaerReadinessPresentation.LightRows(ready, Env).Where(r => r.Key != "mu-receipt").Should().OnlyContain(r => r.State == "Ready" && r.OwnerHref == null);
        SkoleNaervaerReadinessPresentation.LightRows(ready, Env).Single(r => r.Key == "mu-receipt").State.Should().Be("Not verified", "a submission is never MU receipt");

        var reporterMissing = ready.Select(r => r.Key == "synthetic-reporter" ? Row(r.Key, JourneyReadinessState.NotConfigured) : r).ToList();
        var rows = SkoleNaervaerReadinessPresentation.LightRows(reporterMissing, Env);
        rows.Where(r => r.OwnerHref is not null).Should().ContainSingle().Which.Should().Match<SkoleNaervaerReadinessPresentation.LightRow>(r => r.Key == "reporter" && r.State == "Missing" && r.OwnerHref == "/test-data");

        var integrationPartial = ready.Select(r => r.Key == "integration:report-intake" ? Row(r.Key, JourneyReadinessState.NotReady) : r).ToList();
        SkoleNaervaerReadinessPresentation.LightRows(integrationPartial, Env).Single(r => r.OwnerHref is not null).Should()
            .Match<SkoleNaervaerReadinessPresentation.LightRow>(r => r.Key == "integration" && r.State == "Partial" && r.OwnerLabel == "Open IQR");

        var altinnMissing = ready.Select(r => r.Key == "altinn-app" ? Row(r.Key, JourneyReadinessState.NotConfigured) : r).ToList();
        SkoleNaervaerReadinessPresentation.LightRows(altinnMissing, Env).Single(r => r.OwnerHref is not null).OwnerHref.Should().Be(ExternalIntegrationPresentation.ExternalHref(Env, "altinn"));
    }

    private static IReadOnlyList<ExternalProviderDescriptor> Providers() =>
    [
        new(ExternalProviderType.Altinn, "Altinn", "", [new("appId", "App identifier", "Application", ProviderFieldKind.Text, true, true)], ["AltinnReportSubmission"], 1),
        new(ExternalProviderType.RestApi, "REST API", "", [new("baseUrl", "Base URL", "Connection", ProviderFieldKind.Url, true, true)], [], 20),
    ];

    private static SetupPreflightResult Preflight(bool blocked) => new()
    {
        Valid = true, PackageName = "Skolenærvær TT02", PackageVersion = "3", SchemaVersion = 1, DetectedScope = SetupScope.Provider, DetectedProviders = [ExternalProviderType.Altinn],
        Changes = [new("0:baseUrl", "Altinn – Skolenærvær", ExternalProviderType.Altinn, "App base URL", "https://other.example", "https://buf.apps.tt02.altinn.no/buf/skolenaervaer", "Conflict", "")],
        Dependencies =
        [
            new(new(SetupDependencyKind.TestData, "reporter-a", true, "Reporter"), blocked ? SetupDependencyState.Missing : SetupDependencyState.Resolved, "", "Test Data", "/test-data"),
            new(new(SetupDependencyKind.Integration, "skolenaervaer-report-intake", true), blocked ? SetupDependencyState.Missing : SetupDependencyState.Resolved, "", "Integration Quality Review", "/integration-quality-review"),
            new(new(SetupDependencyKind.Contract, "form", true), SetupDependencyState.Resolved, "", "Integration Quality Review", "/integration-quality-review"),
            new(new(SetupDependencyKind.TestData, "duplicate-report-payload", false, "DuplicatePayload"), SetupDependencyState.OptionalMissing, "", "Test Data", "/test-data"),
        ],
        BlockReasons = blocked ? ["2 required dependencies unresolved"] : [],
    };

    private sealed class FakeApi : IExternalIntegrationsApiService
    {
        public List<ExternalIntegrationRequest> Saved { get; } = [];
        public List<string> Exports { get; } = [];
        public List<string?> LibraryQueries { get; } = [];
        public List<Dictionary<string, bool>> LibraryDecisions { get; } = [];
        public SetupPreflightResult Preflight { get; set; } = new();
        public List<SetupLibraryItemView> Library { get; } =
        [
            new()
            {
                Id = Guid.NewGuid(), Name = "Skolenærvær TT02", SetupType = SetupScope.Provider, Providers = [ExternalProviderType.Altinn], Status = SetupLibraryStatus.Ready,
                Sanitization = ["Secrets (client secret…)"],
                Versions = [new(Guid.NewGuid(), 2, 1, "Base URL moved", DateTimeOffset.UnixEpoch, [], [ExternalProviderType.Altinn]), new(Guid.NewGuid(), 1, 1, "Created", DateTimeOffset.UnixEpoch, [], [ExternalProviderType.Altinn])],
            },
        ];

        private static ExternalIntegrationView Card(string id, string name, ExternalProviderType type, SetupReadinessState runtime, SetupReadinessState execution) => new()
        {
            Integration = new ExternalIntegration { Id = id, Name = name, ProviderType = type, EnvironmentName = "TT02" },
            Summary = new(new("configuration", "Configuration", SetupReadinessState.Ready, ""), new("dependencies", "Dependencies", SetupReadinessState.Ready, ""),
                new("authentication", "Authentication", SetupReadinessState.Partial, ""), new("runtime", "Runtime", runtime, ""), new("execution", "Execution", execution, "")),
        };

        private ExternalIntegrationsView View(bool detect) => new(Env,
            [Card("dev:altinn", "Altinn – Skolenærvær", ExternalProviderType.Altinn, SetupReadinessState.Observed, SetupReadinessState.Blocked),
             Card("dev:restapi:person", "Person API", ExternalProviderType.RestApi, SetupReadinessState.NotAssessed, SetupReadinessState.NotAssessed)],
            detect ? [new("Payments API", ExternalProviderType.RestApi, "QA", "Integration Quality Review", "Ready to configure.", new() { ["baseUrl"] = "https://pay.example" }, false)] : [],
            BirkNext.Web.Tests.Components.ExternalIntegrationsUiTests.CatalogDescriptors);

        public Task<ActiveEventApiResult<ExternalIntegrationsView>> ListAsync(string environmentId, CancellationToken ct = default) => Ok(View(false));
        public Task<ActiveEventApiResult<ExternalIntegrationsView>> DetectAsync(string environmentId, CancellationToken ct = default) => Ok(View(true));
        public Task<ActiveEventApiResult<ExternalIntegrationView>> DetectOneAsync(string environmentId, string id, CancellationToken ct = default) => Ok(View(false).Integrations[1]);
        public Task<ActiveEventApiResult<ExternalIntegrationView>> SaveAsync(string environmentId, string? id, ExternalIntegrationRequest request, CancellationToken ct = default)
        { Saved.Add(request); return Ok(View(false).Integrations[1] with { Integration = new ExternalIntegration { Id = "new", Name = request.Name, ProviderType = request.ProviderType } }); }
        public Task<ActiveEventApiResult<bool>> DeleteAsync(string environmentId, string id, CancellationToken ct = default) => Ok(true);
        public Task<ActiveEventApiResult<bool>> AddSecretAsync(string environmentId, string id, string secret, CancellationToken ct = default) => Ok(true);
        public Task<ActiveEventApiResult<SetupPackageExport>> ExportAsync(string environmentId, SetupScope scope, ExternalProviderType? provider, string? integrationId, SetupPackageFormat format,
            IReadOnlyList<string>? testPacks = null, CancellationToken ct = default)
        { Exports.Add($"{scope}|{provider}|{integrationId}|{format}"); return Ok(new SetupPackageExport(format, "x.setup.yaml", "schemaVersion: 1", ["Secrets (client secret…)"], [])); }
        public Task<ActiveEventApiResult<SetupPreflightResult>> PreflightAsync(string environmentId, SetupPreflightRequest request, CancellationToken ct = default) => Ok(Preflight);
        public Task<ActiveEventApiResult<SetupApplyPackageResult>> ApplyAsync(string environmentId, SetupApplyPackageRequest request, CancellationToken ct = default) => Ok(new SetupApplyPackageResult(true, "Applied", [], Preflight));
        public Task<ActiveEventApiResult<IReadOnlyList<SetupLibraryItemView>>> LibraryAsync(ExternalProviderType? provider = null, SetupLibraryStatus? status = null, string? testPack = null, SetupScope? setupType = null, CancellationToken ct = default)
        { LibraryQueries.Add(provider?.ToString()); return Ok<IReadOnlyList<SetupLibraryItemView>>(Library); }
        public Task<ActiveEventApiResult<SetupLibraryItemView>> SaveToLibraryAsync(SetupLibrarySaveRequest request, CancellationToken ct = default) => Ok(Library[0]);
        public Task<ActiveEventApiResult<SetupLibraryItemView>> NewLibraryVersionAsync(Guid id, SetupLibrarySaveRequest request, CancellationToken ct = default) => Ok(Library[0]);
        public Task<ActiveEventApiResult<SetupLibraryItemView>> DuplicateLibraryItemAsync(Guid id, CancellationToken ct = default) => Ok(Library[0]);
        public Task<ActiveEventApiResult<bool>> ArchiveLibraryItemAsync(Guid id, CancellationToken ct = default) => Ok(true);
        public Task<ActiveEventApiResult<SetupPackageExport>> ExportLibraryVersionAsync(Guid id, int? version, SetupPackageFormat format, CancellationToken ct = default) => Ok(new SetupPackageExport(format, "x", "", [], []));
        public Task<ActiveEventApiResult<IReadOnlyList<SetupVersionDifference>>> CompareLibraryVersionsAsync(Guid id, int from, int to, CancellationToken ct = default) => Ok<IReadOnlyList<SetupVersionDifference>>([]);
        public Task<ActiveEventApiResult<SetupPreflightResult>> PreflightLibraryVersionAsync(Guid id, int? version, string environmentId, CancellationToken ct = default) => Ok(Preflight);
        public Task<ActiveEventApiResult<SetupApplyPackageResult>> ApplyLibraryVersionAsync(Guid id, int? version, string environmentId, Dictionary<string, bool>? decisions, CancellationToken ct = default)
        { LibraryDecisions.Add(new(decisions ?? [])); return Ok(new SetupApplyPackageResult(true, "Applied", [], Preflight)); }

        private static Task<ActiveEventApiResult<T>> Ok<T>(T value) => Task.FromResult(new ActiveEventApiResult<T>(value, null, HttpStatusCode.OK));
    }

    /// <summary>A slice of the real provider catalog (field definitions as the backend serves them).</summary>
    internal static readonly IReadOnlyList<ExternalProviderDescriptor> CatalogDescriptors =
    [
        new(ExternalProviderType.Altinn, "Altinn", "An Altinn app.",
        [
            new("environment", "Altinn environment", "Application", ProviderFieldKind.Choice, true, true, "", ["Test"]),
            new("appId", "App identifier", "Application", ProviderFieldKind.Text, true, true), new("formDataType", "Form data type", "Application", ProviderFieldKind.Text, false, true),
            new("clientId", "Unattended client ID", "Authentication", ProviderFieldKind.Text, false, true), new("redirectUri", "Registered redirect URI", "Authentication", ProviderFieldKind.Url, false, true),
            new("clientSecret", "Client secret", "Authentication", ProviderFieldKind.Text, false, false, "Session only.", Secret: true),
        ], ["AltinnReportSubmission"], 1),
        new(ExternalProviderType.RestApi, "REST API", "An HTTP API.",
        [
            new("baseUrl", "Base URL", "Connection", ProviderFieldKind.Url, true, true), new("openApiUrl", "OpenAPI URL", "Connection", ProviderFieldKind.Url, false, true),
            new("authScheme", "Auth scheme", "Authentication", ProviderFieldKind.Choice, false, true, "", ["None", "ManagedIdentity"]),
            new("clientSecret", "Client secret", "Authentication", ProviderFieldKind.Text, false, false, "", Secret: true),
        ], ["HttpApiCall"], 20),
        new(ExternalProviderType.AzureServiceBus, "Azure Service Bus", "A namespace.",
        [
            new("namespace", "Namespace", "Connection", ProviderFieldKind.Text, true, true), new("entityType", "Entity type", "Connection", ProviderFieldKind.Choice, true, true, "", ["Queue", "Topic"]),
            new("entity", "Queue / topic", "Connection", ProviderFieldKind.Text, true, true), new("subscription", "Subscription", "Connection", ProviderFieldKind.Text, false, true),
        ], ["ServiceBusPublish"], 20),
    ];
}
