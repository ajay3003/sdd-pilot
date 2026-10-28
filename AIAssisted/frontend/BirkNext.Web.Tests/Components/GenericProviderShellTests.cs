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
/// Every connected system other than Altinn renders in the same compact shell as Altinn (summary → things to fix → detected settings →
/// sections → advanced details) with only its own fields. A detected or suggested required value is "Not applied" and a thing to fix until
/// a person applies it (suggested != configured); an optional one never blocks; a conflict always does; Detect never saves.
/// </summary>
public sealed class GenericProviderShellTests : BunitContext
{
    private const string Env = "qa";
    private readonly Mock<IExternalIntegrationsApiService> _api = new();
    private readonly List<ExternalIntegrationRequest> _saved = [];

    public GenericProviderShellTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_api.Object);
        _api.Setup(api => api.SaveAsync(Env, It.IsAny<string?>(), It.IsAny<ExternalIntegrationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string? id, ExternalIntegrationRequest request, CancellationToken _) =>
            {
                _saved.Add(request);
                return new ActiveEventApiResult<ExternalIntegrationView>(Rest() with { Integration = Rest().Integration with { Settings = request.Settings!, UpdatedAt = DateTimeOffset.UtcNow } }, null, HttpStatusCode.OK);
            });
    }

    private static ProviderFieldDefinition Def(string key, string label, string group, bool required = false, bool secret = false) =>
        new(key, label, group, ProviderFieldKind.Text, required, !secret, Secret: secret);

    private static AltinnSettingField F(string key, string group, string label, string? value, AltinnSettingState state, bool required = false, AltinnSettingCandidate? proposal = null) => new()
    {
        Key = key, Group = group, Label = label, Value = value, State = state, Requirement = required ? AltinnRequirement.Manual : AltinnRequirement.Optional, Proposal = proposal,
        Source = state == AltinnSettingState.Configured ? AltinnSettingSource.TargetEnvironment : AltinnSettingSource.IntegrationBinding,
        SourceLabel = state == AltinnSettingState.Configured ? "Configured manually" : "Integration Quality Review",
    };

    private static AltinnSettingCandidate Detected(string value) => new(value, AltinnSettingState.Detected, AltinnSettingSource.IntegrationBinding, "Integration Quality Review");

    private static readonly ExternalProviderDescriptor RestDescriptor = new(ExternalProviderType.RestApi, "REST API", "An HTTP API described by OpenAPI.",
    [
        Def("baseUrl", "Base URL", "Connection", required: true), Def("healthEndpoint", "Health endpoint", "Connection"),
        Def("openApiUrl", "OpenAPI / Swagger URL", "Contract"), Def("authScheme", "Auth scheme", "Authentication"), Def("clientSecret", "Client secret", "Authentication", secret: true),
    ], ["HttpApiCall"], 20);

    private static ExternalIntegrationView Rest() => new()
    {
        Integration = new ExternalIntegration { Id = "rest-1", TargetEnvironmentId = Env, Name = "Person API", ProviderType = ExternalProviderType.RestApi, Settings = new() { ["authScheme"] = "ManagedIdentity" } },
        Detected = true,
        Fields =
        [
            F("baseUrl", "Connection", "Base URL", "https://person.qa.example/api", AltinnSettingState.Detected, required: true, Detected("https://person.qa.example/api")),
            F("healthEndpoint", "Connection", "Health endpoint", "https://person.qa.example/health", AltinnSettingState.Detected, proposal: Detected("https://person.qa.example/health")),
            F("openApiUrl", "Contract", "OpenAPI / Swagger URL", null, AltinnSettingState.Missing),
            F("authScheme", "Authentication", "Auth scheme", "ManagedIdentity", AltinnSettingState.Configured),
            F("clientSecret", "Authentication", "Client secret", null, AltinnSettingState.Missing) with { Requirement = AltinnRequirement.Automated },
        ],
        Dimensions =
        [
            new("configuration", "Configuration", SetupReadinessState.Partial, "Not applied: Base URL"),
            new("authentication", "Authentication", SetupReadinessState.Partial, "Secret not available — only automated execution needs it."),
            new("contract", "Contract", SetupReadinessState.NotAssessed, "No contract reference configured (optional)."),
            new("runtime", "Runtime", SetupReadinessState.NotAssessed, "Not verified."),
            new("execution", "Execution", SetupReadinessState.NoExecutor, "Automated execution is not implemented for this provider."),
        ],
        Detection = new(1, 2, 0, 0, 0, ["Not applied: Base URL"]),
    };

    private static readonly ExternalProviderDescriptor BusDescriptor = new(ExternalProviderType.AzureServiceBus, "Azure Service Bus", "A Service Bus namespace and entity.",
    [
        Def("namespace", "Namespace", "Connection", required: true), Def("entity", "Queue / topic", "Connection", required: true),
        Def("authMode", "Authentication mode", "Authentication"), Def("connectionSecret", "Connection secret", "Authentication", secret: true),
    ], ["ServiceBusPublish"], 20);

    private static ExternalIntegrationView Bus() => new()
    {
        Integration = new ExternalIntegration { Id = "bus-1", TargetEnvironmentId = Env, Name = "Bus", ProviderType = ExternalProviderType.AzureServiceBus, Settings = new() { ["entity"] = "topic-a" } },
        Fields =
        [
            F("namespace", "Connection", "Namespace", null, AltinnSettingState.Missing, required: true),
            F("entity", "Connection", "Queue / topic", "topic-a", AltinnSettingState.Conflict, required: true, Detected("topic-b")),
            F("authMode", "Authentication", "Authentication mode", null, AltinnSettingState.Missing),
            F("connectionSecret", "Authentication", "Connection secret", null, AltinnSettingState.Missing) with { Requirement = AltinnRequirement.Automated },
        ],
        Dimensions =
        [
            new("configuration", "Configuration", SetupReadinessState.Partial, "Missing: Namespace · Conflict: Queue / topic"),
            new("authentication", "Authentication", SetupReadinessState.Partial, "Secret not available."),
            new("runtime", "Runtime", SetupReadinessState.NotAssessed, "Not verified."),
            new("execution", "Execution", SetupReadinessState.NotAssessed, "No executor."),
        ],
    };

    private IRenderedComponent<GenericProviderPanel> Render(ExternalIntegrationView view, ExternalProviderDescriptor descriptor, Action<ExternalIntegrationView>? changed = null) =>
        Render<GenericProviderPanel>(p => p.Add(c => c.EnvironmentId, Env).Add(c => c.View, view).Add(c => c.Descriptor, descriptor)
            .Add(c => c.OnChanged, (ExternalIntegrationView v) => changed?.Invoke(v)));

    [Fact]
    public void Rest_api_default_view_is_compact_with_its_own_dimensions_and_sections()
    {
        var cut = Render(Rest(), RestDescriptor);

        cut.FindAll("[data-testid=gp-card]").Select(card => card.GetAttribute("data-key")).Should().Equal("configuration", "authentication", "contract", "runtime", "execution");
        cut.Find("[data-testid=gp-card][data-key=configuration] [data-testid=gp-card-reason]").TextContent.Should().Be("1 not applied");
        cut.FindAll("[data-testid=gp-fix-item]").Select(item => item.TextContent.Trim()).Should().Equal("Base URL · Not applied · Detected from Integration Quality Review");
        cut.Find("[data-testid=gp-fix-group][data-key=configuration] [data-testid=gp-fix-action]").TextContent.Should().Be("Apply detected settings");
        cut.FindAll("[data-testid=gp-proposal]").Select(item => item.GetAttribute("data-key")).Should().Equal("baseUrl", "healthEndpoint");
        cut.Find("[data-testid=gp-proposals]").TextContent.Should().Contain("Source: Integration Quality Review");
        cut.FindAll("section[data-testid^=gp-section-]").Select(section => section.GetAttribute("data-testid")).Should()
            .Equal("gp-section-configuration", "gp-section-contract", "gp-section-authentication", "gp-section-runtime");
        cut.FindAll("[data-testid=gp-field-table], [data-testid=psf], [data-testid=ei-secret], [data-testid=gp-advanced-body]").Should().BeEmpty("detail is collapsed by default");
        foreach (var toggle in cut.FindAll("button[aria-controls]")) toggle.GetAttribute("aria-expanded").Should().Be("false");
        _saved.Should().BeEmpty("rendering detected values never saves");
    }

    [Fact]
    public void Apply_from_things_to_fix_saves_exactly_the_listed_value_and_reports_the_new_view()
    {
        ExternalIntegrationView? changed = null;
        var cut = Render(Rest(), RestDescriptor, view => changed = view);

        cut.Find("[data-testid=gp-fix-group][data-key=configuration] [data-testid=gp-fix-action]").Click();

        cut.WaitForAssertion(() => _saved.Should().ContainSingle());
        _saved[0].Settings.Should().BeEquivalentTo(new Dictionary<string, string> { ["authScheme"] = "ManagedIdentity", ["baseUrl"] = "https://person.qa.example/api" },
            "the optional detected health endpoint is not applied by the required-settings action");
        changed.Should().NotBeNull();
    }

    [Fact]
    public void Sections_are_real_buttons_that_show_only_their_own_fields_and_advanced_details_stay_available()
    {
        var cut = Render(Rest(), RestDescriptor);

        cut.Find("[data-testid=gp-toggle-configuration]").Click();
        cut.Find("[data-testid=gp-toggle-configuration]").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll("[data-testid=gp-configuration-body] [data-testid=psf-field]").Select(field => field.GetAttribute("data-key")).Should().Equal("baseUrl", "healthEndpoint");
        cut.Find("[data-testid=gp-secret-reveal]").Click();
        cut.Find("[data-testid=ei-secret]").GetAttribute("type").Should().Be("password");
        cut.Find("[data-testid=gp-advanced-toggle]").Click();
        cut.Find("[data-testid=gp-internal-id]").TextContent.Should().Contain("rest-1");
        cut.FindAll("[data-testid=gp-evidence-field]").Should().HaveCount(5);
    }

    [Fact]
    public void Service_bus_uses_the_same_shell_and_a_conflict_or_missing_value_needs_an_edit_not_an_apply()
    {
        var cut = Render(Bus(), BusDescriptor);

        cut.FindAll("[data-testid=gp-card]").Select(card => card.GetAttribute("data-key")).Should().Equal("configuration", "authentication", "runtime", "execution");
        cut.FindAll("[data-testid=gp-fix-item]").Select(item => item.TextContent.Trim()).Should().Equal("Namespace · Missing", "Queue / topic · Conflict");
        cut.Find("[data-testid=gp-fix-action]").TextContent.Should().Be("Edit configuration");
        // A conflicting detected value is shown (configured vs detected) but never selectable: Apply selected can never overwrite it.
        cut.FindAll("[data-testid=gp-proposal][data-key=entity]").Should().BeEmpty();
        cut.Find("[data-testid=gp-conflict][data-key=entity] [data-testid=gp-conflict-configured]").TextContent.Should().Be("topic-a");
        cut.FindAll("[data-testid=gp-section-contract]").Should().BeEmpty("Service Bus has no contract section");

        cut.Find("[data-testid=gp-fix-action]").Click();
        cut.FindAll("[data-testid=gp-configuration-body] [data-testid=psf-field]").Select(field => field.GetAttribute("data-key")).Should().Equal("namespace", "entity");
        _saved.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ExternalProviderType.RestApi)]
    [InlineData(ExternalProviderType.GraphQl)]
    [InlineData(ExternalProviderType.AzureServiceBus)]
    [InlineData(ExternalProviderType.AzureEventHub)]
    [InlineData(ExternalProviderType.Database)]
    [InlineData(ExternalProviderType.FileSftp)]
    [InlineData(ExternalProviderType.GenericHttp)]
    [InlineData(ExternalProviderType.Other)]
    public void Every_non_altinn_provider_renders_through_the_generic_shell(ExternalProviderType type)
    {
        var descriptor = new ExternalProviderDescriptor(type, type.ToString(), "", [Def($"{type}-a", "A", "Connection", required: true), Def($"{type}-b", "B", "Connection")], [], 20);
        var view = new ExternalIntegrationView
        {
            Integration = new ExternalIntegration { Id = $"{type}-1", TargetEnvironmentId = Env, Name = type.ToString(), ProviderType = type },
            Fields = [F($"{type}-a", "Connection", "A", null, AltinnSettingState.Missing, required: true), F($"{type}-b", "Connection", "B", null, AltinnSettingState.Missing)],
            Summary = new(new("configuration", "Configuration", SetupReadinessState.Blocked, "Missing: A"), ExternalIntegrationView.Empty("dependencies"),
                ExternalIntegrationView.Empty("authentication"), ExternalIntegrationView.Empty("runtime"), ExternalIntegrationView.Empty("execution")),
        };

        var cut = Render(view, descriptor);

        cut.Find("[data-testid=gp]");
        cut.FindAll("[data-testid=gp-card]").Should().HaveCount(5, "without provider dimensions the five standard ones are shown");
        cut.FindAll("[data-testid=gp-fix-item]").Select(item => item.TextContent.Trim()).Should().Equal(new[] { "A · Missing" }, "an optional missing value is never a thing to fix");
        cut.Find("[data-testid=gp-toggle-configuration]").Click();
        cut.FindAll("[data-testid=psf-field]").Select(field => field.GetAttribute("data-key")).Should().OnlyContain(key => key!.StartsWith(type.ToString()));
    }

    [Fact]
    public void Suggested_required_values_are_things_to_fix_and_optional_ones_are_not()
    {
        var view = Rest() with
        {
            Fields =
            [
                F("baseUrl", "Connection", "Base URL", "https://x.example", AltinnSettingState.Declared, required: true, new("https://x.example", AltinnSettingState.Declared, AltinnSettingSource.TestPack, "Test Pack reference · OpenAPI")) with { SourceLabel = "Test Pack reference · OpenAPI" },
                F("healthEndpoint", "Connection", "Health endpoint", "https://x.example/health", AltinnSettingState.Declared, proposal: new("https://x.example/health", AltinnSettingState.Declared, AltinnSettingSource.TestPack, "Test Pack reference · OpenAPI")),
            ],
        };

        var groups = ProviderShellPresentation.ThingsToFix(view, RestDescriptor);

        groups.Should().ContainSingle().Which.Items.Should().ContainSingle().Which.Problem.Should().Be("Not applied · Suggested by Test Pack reference · OpenAPI");
        groups[0].ApplyKeys.Should().Equal("baseUrl");
        ProviderShellPresentation.Proposals(view, RestDescriptor).Select(proposal => proposal.State).Should().Equal("Suggested", "Suggested");
    }

    [Fact]
    public void Contract_without_assessment_is_not_assessed_and_execution_without_executor_says_so()
    {
        var cut = Render(Rest(), RestDescriptor);

        cut.Find("[data-testid=gp-card][data-key=contract]").TextContent.Should().Contain("Not assessed").And.NotContain("Not verified");
        var execution = cut.Find("[data-testid=gp-card][data-key=execution]");
        execution.TextContent.Should().Contain("No executor").And.Contain("Automated execution is not implemented for this provider.").And.NotContain("Not verified");
        execution.QuerySelector(".ps-pill")!.GetAttribute("title").Should().Be("BirkNext does not currently provide automated execution for this provider.");
        cut.Find("[data-testid=gp-card][data-key=runtime]").TextContent.Should().Contain("Not assessed", "no runtime check exists; configuration readiness never implies a runtime state");
        cut.Find("[data-testid=gp-card][data-key=configuration]").TextContent.Should().Contain("Partial");
    }

    [Fact]
    public void State_labels_keep_not_assessed_not_verified_no_executor_and_not_applicable_apart()
    {
        ExternalIntegrationPresentation.Label(SetupReadinessState.NotAssessed).Should().Be("Not assessed");
        ExternalIntegrationPresentation.Label(SetupReadinessState.NotVerified).Should().Be("Not verified", "a provider with an executor may still be unverified");
        ExternalIntegrationPresentation.Label(SetupReadinessState.NoExecutor).Should().Be("No executor");
        ExternalIntegrationPresentation.Label(SetupReadinessState.NotApplicable).Should().Be("Not applicable");
        ExternalIntegrationPresentation.Label(SetupReadinessState.Observed).Should().Be("Observed");
        ExternalIntegrationPresentation.Label(SetupReadinessState.Verified).Should().Be("Verified");
        ExternalIntegrationPresentation.Help(SetupReadinessState.NotAssessed).Should().Be("No assessment has been performed yet.");
    }

    [Fact]
    public void An_open_system_shows_its_name_once_as_the_card_heading_with_shell_headings_one_level_below()
    {
        _api.Setup(api => api.ListAsync(Env, It.IsAny<CancellationToken>())).ReturnsAsync(new ActiveEventApiResult<ExternalIntegrationsView>(
            new ExternalIntegrationsView(Env, [Rest()], [], [RestDescriptor]), null, HttpStatusCode.OK));
        var cut = Render<ExternalIntegrationsPane>(p => p.Add(c => c.EnvironmentId, Env));
        cut.WaitForAssertion(() => cut.Find("[data-testid=ei-card]"));

        cut.Find("[data-testid=ei-review]").Click();

        cut.Find("[data-testid=gp]");
        cut.FindAll("h1, h2, h3, h4, h5, h6").Where(h => h.TextContent.Trim() == "Person API").Select(h => h.TagName).Should().Equal("H4");
        cut.Find("[data-testid=gp]").GetAttribute("aria-label").Should().Be("Person API");
        cut.FindAll("[data-testid=gp] h4").Should().BeEmpty();
        cut.FindAll("[data-testid=gp] h5").Select(h => h.TextContent.Trim()).Should().Contain(["Things to fix", "Configuration", "Advanced details"]);
    }
}
