using BirkNext.Api.Data;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.ExternalIntegrations;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace BirkNext.Api.Tests.Services.ExternalIntegrations;

/// <summary>
/// The generic, provider-driven setup: provider field isolation and validation, Detect without writes, scope-aware export, the fail-closed
/// preflight (required Missing / Ambiguous / Incompatible / Not approved block Apply; optional missing does not), explicit conflict decisions,
/// all-or-nothing apply, sanitization (no secret, identity, trust or runtime evidence ever enters a package) and the Setup Library's
/// immutable versions. Isolated fakes only: no TT02, ID-porten or Azure call.
/// </summary>
public sealed class ExternalIntegrationSetupTests
{
    private const string Env = "qa-env";
    private const string Other = "dev-env";
    private static readonly string ChildIdentifier = Person("1205");

    private static string Person(string dayMonth)
    {
        for (var individual = 100; individual < 1000; individual++)
            for (var k = 0; k < 100; k++)
                if ($"{dayMonth}12{individual:000}{k:00}" is var candidate && SyntheticPersonIdentifier.IsTestPerson(candidate)) return candidate;
        throw new InvalidOperationException();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Bed
    {
        public AltinnTestKit Kit { get; } = new();
        public IntegrationJourneyHarness.FakeMessageFlows Flows { get; } = new();
        public AppDbContext Db { get; } = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"ext-{Guid.NewGuid()}").Options);
        public AltinnEnvironmentSettingsCache Cache { get; } = new();
        public Clock Time { get; } = new();
        public AltinnSessionStore Session { get; }
        public List<IntegrationDefinition> Catalog { get; } = [];
        public List<IqrSourceSnapshot> Snapshots { get; } = [];

        public Bed()
        {
            Session = new AltinnSessionStore(Kit.Options, Time);
            Kit.Options.ApprovedApps = [AltinnTestKit.AppId];
        }

        public void Identities()
        {
            Session.AddIdentity(new("reporter", "reporter-a", "Reporter A", AltinnTestKit.ReporterIdentifier, "reporter-a")).Should().BeNull();
            Session.AddIdentity(new("child", "child-a", "Child A", ChildIdentifier)).Should().BeNull();
        }

        private Mock<IIntegrationCatalogService> CatalogMock()
        {
            var mock = new Mock<IIntegrationCatalogService>();
            mock.Setup(c => c.GetAsync(It.IsAny<string>(), null, null, It.IsAny<CancellationToken>())).ReturnsAsync((string env, string? _, string? _, CancellationToken _) =>
                new IntegrationCatalog { EnvironmentId = env, Integrations = [.. Catalog.Where(item => item.EnvironmentId == env)] });
            return mock;
        }

        public AltinnTestingPolicy Policy => new(Kit.Options, Session, Cache);
        public AltinnEnvironmentSettingsStore Store => new(Db, Cache, Time);

        public ExternalIntegrationService Integrations()
        {
            var catalog = CatalogMock().Object;
            var trusted = new Mock<ITrustedExecutionEnvironmentRegistry>();
            string reason = "Not trusted (test).";
            trusted.Setup(t => t.Resolve(It.IsAny<string>(), out reason)).Returns((TrustedExecutionEnvironment?)null);
            var altinn = new AltinnEnvironmentService(Store, Flows, Policy, Kit.Contracts(Flows), catalog, trusted.Object, Kit.Factory);
            var handler = new AltinnProviderHandler(altinn, Store, Flows, Kit.Contracts(Flows), Policy);
            return new ExternalIntegrationService(Db, [handler], catalog, Session, Cache, Time);
        }

        public EnvironmentSetupService Setup()
        {
            var sources = new Mock<IReviewSourceEvidenceProvider>();
            sources.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Snapshots);
            var pack = new Mock<IIntegrationJourneyPack>();
            pack.SetupGet(p => p.PackId).Returns("skolenaervaer");
            return new EnvironmentSetupService(Db, Integrations(), CatalogMock().Object, new TestDataCatalog([new AltinnTestDataProvider(Kit.Options)]), sources.Object, [pack.Object], Time);
        }

        public SetupLibraryService Library() => new(Db, Setup(), Time);

        public void ContractEvidence(string formDataType = AltinnTestKit.RealFormDataType) =>
            Flows.Contract = new AltinnAppContractRecord(null, new AltinnContractSnapshot { AppId = AltinnTestKit.AppId, FormDataTypeId = formDataType, AllowedContentTypes = ["application/xml"] });

        public void ReportIntake(string env = Env) =>
            Catalog.Add(new IntegrationDefinition { Id = "skolenaervaer-report-intake", EnvironmentId = env, DisplayName = AltinnEnvironmentService.IntegrationName, Kind = IntegrationKind.HttpApi });

        public async Task<int> Writes() => await Db.ExternalIntegrations.CountAsync() + await Db.AltinnEnvironmentSettings.CountAsync() + await Db.SetupLibraryItems.CountAsync();
    }

    private static ExternalIntegrationRequest Altinn(string? form = AltinnTestKit.RealFormDataType, string? integration = "skolenaervaer-report-intake") => new("Altinn – Skolenærvær",
        ExternalProviderType.Altinn, "TT02", true, new()
        {
            ["environment"] = "Test", ["appId"] = AltinnTestKit.AppId, ["baseUrl"] = "https://buf.apps.tt02.altinn.no/buf/skolenaervaer",
            ["contentType"] = "application/xml", ["clientId"] = AltinnTestKit.ClientId, ["redirectUri"] = AltinnTestKit.HeadlessRedirect, ["scopes"] = "openid altinn:instances.write",
            ["unattendedEnabled"] = "true",
            ["formDataType"] = form ?? "", ["integrationId"] = integration ?? "",
        });

    private static async Task<Bed> ConfiguredAsync()
    {
        var bed = new Bed();
        bed.Identities();
        bed.ContractEvidence();
        bed.ReportIntake(Env);
        bed.ReportIntake(Other);
        var (saved, problems) = await bed.Integrations().SaveAsync(Env, null, Altinn(), "Configured manually", CancellationToken.None);
        problems.Should().BeEmpty();
        saved.Should().NotBeNull();
        (await bed.Integrations().SaveAsync(Env, null, new("Person API", ExternalProviderType.RestApi, "QA", true, new() { ["baseUrl"] = "https://person.qa.example/api", ["authScheme"] = "ManagedIdentity" }),
            "Configured manually", CancellationToken.None)).Problems.Should().BeEmpty();
        bed.Session.SetSecret(Env, "altinn-secret-DO-NOT-EXPORT");
        return bed;
    }

    // ── Provider architecture ──

    [Fact]
    public void Provider_fields_are_isolated_and_validated_by_their_provider()
    {
        var rest = ExternalProviderCatalog.Descriptor(ExternalProviderType.RestApi);
        ProviderFieldValidation.Validate(rest, new Dictionary<string, string> { ["appId"] = "buf/skolenaervaer" }).Should().ContainSingle(p => p.Contains("not a REST API setting"));
        var serviceBus = ExternalProviderCatalog.Descriptor(ExternalProviderType.AzureServiceBus);
        ProviderFieldValidation.Validate(serviceBus, new Dictionary<string, string> { ["formDataType"] = "x" }).Should().ContainSingle(p => p.Contains("not a Azure Service Bus setting"));
        ProviderFieldValidation.Validate(serviceBus, new Dictionary<string, string> { ["entityType"] = "Mailbox" }).Should().ContainSingle(p => p.Contains("Queue, Topic"));
        ProviderFieldValidation.Validate(serviceBus, new Dictionary<string, string> { ["connectionSecret"] = "Endpoint=sb://x" }).Should().ContainSingle(p => p.Contains("session only"));
        ProviderFieldValidation.Validate(rest, new Dictionary<string, string> { ["baseUrl"] = "http://person.example" }).Should().ContainSingle(p => p.Contains("https"));
        ProviderFieldValidation.Validate(rest, new Dictionary<string, string> { ["clientId"] = "Bearer eyJabc" }).Should().ContainSingle(p => p.Contains("credential"));
        ExternalProviderCatalog.Descriptor(ExternalProviderType.Altinn).Fields.Should().NotContain(field => field.Key == "namespace", "Service Bus fields never appear for Altinn");
        ExternalProviderCatalog.TryParse("Azure Service Bus", out var parsed).Should().BeTrue();
        parsed.Should().Be(ExternalProviderType.AzureServiceBus);
        ExternalProviderCatalog.TryParse("Kafka", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handler_is_selected_per_provider_and_altinn_is_one_per_environment()
    {
        var bed = await ConfiguredAsync();
        var service = bed.Integrations();
        service.Handler(ExternalProviderType.Altinn).Should().BeOfType<AltinnProviderHandler>();
        service.Handler(ExternalProviderType.AzureEventHub).Should().BeOfType<GenericProviderHandler>();
        (await service.SaveAsync(Env, null, Altinn(), "x", CancellationToken.None)).Problems.Should().Contain(p => p.Contains("already has 1 Altinn"));

        var view = await service.ViewAsync(Env, detect: false, CancellationToken.None);
        view.Integrations.Select(item => item.Integration.ProviderType).Should().Equal(ExternalProviderType.Altinn, ExternalProviderType.RestApi);
        var rest = view.Integrations.Single(item => item.Integration.ProviderType == ExternalProviderType.RestApi);
        rest.Summary.Configuration.State.Should().Be(SetupReadinessState.Ready);
        rest.Fields.Single(field => field.Key == "baseUrl").SourceLabel.Should().Be("Configured manually");
        var altinn = view.Integrations.Single(item => item.Integration.ProviderType == ExternalProviderType.Altinn);
        altinn.Fields.Single(field => field.Key == "client-secret").State.Should().Be(AltinnSettingState.AvailableForSession);
        altinn.Summary.Execution.State.Should().Be(SetupReadinessState.Blocked, "trust is not configured, so automated execution stays blocked");
    }

    [Fact]
    public async Task Detect_proposes_a_deterministic_altinn_integration_and_never_writes()
    {
        var bed = new Bed();
        bed.Flows.Configuration = bed.Flows.Configuration with { Environment = ExternalTestEnvironment.Test, AppIdentifier = AltinnTestKit.AppId };
        bed.Catalog.Add(new IntegrationDefinition { Id = "sb-1", EnvironmentId = Env, DisplayName = "Topic A", Kind = IntegrationKind.ServiceBus, EndpointOrTopic = "topic-a" });

        var view = await bed.Integrations().ViewAsync(Env, detect: true, CancellationToken.None);
        view.Proposals.Should().ContainSingle(p => p.ProviderType == ExternalProviderType.Altinn && p.Name == "Altinn – buf/skolenaervaer" && !p.Ambiguous);
        view.Proposals[0].DetectedFrom.Should().Contain("Integration Quality Review");
        (await bed.Writes()).Should().Be(0, "Detect is discovery only");
    }

    [Fact]
    public async Task Generic_detection_proposes_only_a_single_deterministic_value()
    {
        var bed = new Bed();
        var service = bed.Integrations();
        (await service.SaveAsync(Env, null, new("Bus", ExternalProviderType.AzureServiceBus, null, true, new() { ["namespace"] = "sb-qa.servicebus.windows.net", ["entityType"] = "Topic" }),
            "Configured manually", CancellationToken.None)).Problems.Should().BeEmpty();
        bed.Catalog.Add(new IntegrationDefinition { Id = "a", EnvironmentId = Env, DisplayName = "A", Kind = IntegrationKind.ServiceBus, EndpointOrTopic = "topic-a" });
        var id = (await service.ListAsync(Env, CancellationToken.None))[0].Id;
        var one = await service.ViewOneAsync(Env, id, detect: true, CancellationToken.None);
        one!.Fields.Single(field => field.Key == "entity").Proposal!.Value.Should().Be("topic-a");
        one.Fields.Single(field => field.Key == "entity").Source.Should().Be(AltinnSettingSource.IntegrationBinding, "resolved from evidence, never runtime verified");

        bed.Catalog.Add(new IntegrationDefinition { Id = "b", EnvironmentId = Env, DisplayName = "B", Kind = IntegrationKind.ServiceBus, EndpointOrTopic = "topic-b" });
        var two = await service.ViewOneAsync(Env, id, detect: true, CancellationToken.None);
        two!.Fields.Single(field => field.Key == "entity").Proposal.Should().BeNull("two credible candidates are a decision, not a guess");
        two.Notes.Should().Contain(note => note.Contains("2 candidates"));
    }

    // ── Export / sanitization ──

    [Fact]
    public async Task Provider_export_is_sanitized_and_carries_the_dependency_manifest()
    {
        var bed = await ConfiguredAsync();
        var (export, _) = await bed.Setup().ExportAsync(Env, SetupScope.Provider, ExternalProviderType.Altinn, null, SetupPackageFormat.Yaml, ["skolenaervaer"], CancellationToken.None);

        export!.Content.Should().Contain("buf/skolenaervaer").And.Contain(AltinnTestKit.RealFormDataType).And.Contain("redirectUri").And.Contain("scope: \"Provider\"")
            .And.NotContain("altinn-secret-DO-NOT-EXPORT").And.NotContain(AltinnTestKit.ReporterIdentifier).And.NotContain(ChildIdentifier)
            .And.NotContain("unattendedEnabled", "execution enablement is environment-local").And.NotContain("Person API", "a provider-scoped export holds one provider");
        export.Dependencies.Should().Contain(d => d.Kind == SetupDependencyKind.TestData && d.Reference == "reporter-a" && d.Role == "Reporter" && d.Required)
            .And.Contain(d => d.Kind == SetupDependencyKind.Integration && d.Reference == "skolenaervaer-report-intake")
            .And.Contain(d => d.Kind == SetupDependencyKind.ProviderCapability && d.Reference == "AltinnReportSubmission")
            .And.Contain(d => d.Kind == SetupDependencyKind.TestData && !d.Required);
        export.Excluded.Should().Contain(item => item.StartsWith("Secrets"));
    }

    [Fact]
    public async Task Environment_export_round_trips_to_the_same_portable_intent_in_another_environment()
    {
        var bed = await ConfiguredAsync();
        var setup = bed.Setup();
        var (export, _) = await setup.ExportAsync(Env, SetupScope.Environment, null, null, SetupPackageFormat.Json, ["skolenaervaer"], CancellationToken.None);
        var (model, issues) = SetupPackageSchema.Parse(export!.FileName, export.Content);
        issues.Should().NotContain(issue => issue.Severity == SetupIssueSeverity.Error);
        model!.Systems.Select(s => s.Provider).Should().BeEquivalentTo([ExternalProviderType.Altinn, ExternalProviderType.RestApi]);

        var preflight = await setup.PreflightAsync(Other, new(export.Content, export.FileName), CancellationToken.None);
        preflight.CanApply.Should().BeTrue(string.Join(" | ", preflight.BlockReasons.Concat(preflight.Dependencies.Where(d => d.State != SetupDependencyState.Resolved).Select(d => d.Detail))));
        preflight.Changes.Should().OnlyContain(change => change.Classification == "New");
        (await bed.Db.ExternalIntegrations.CountAsync(row => row.EnvironmentId == Other)).Should().Be(0, "preflight never writes");

        var applied = await setup.ApplyAsync(Other, new(export.Content, export.FileName), "Imported from file: test", CancellationToken.None);
        applied.Applied.Should().BeTrue(applied.Outcome);
        var (again, _) = await setup.ExportAsync(Other, SetupScope.Environment, null, null, SetupPackageFormat.Json, ["skolenaervaer"], CancellationToken.None);
        SetupPackageSchema.Parse(again!.FileName, again.Content).Model!.Systems.Select(s => (s.Provider, s.Name, string.Join(";", s.Settings.OrderBy(e => e.Key).Select(e => $"{e.Key}={e.Value}"))))
            .Should().BeEquivalentTo(model.Systems.Select(s => (s.Provider, s.Name, string.Join(";", s.Settings.OrderBy(e => e.Key).Select(e => $"{e.Key}={e.Value}")))));
        bed.Session.Secret(Other).Should().BeNull("a secret never transfers");
        (await bed.Store.GetAsync(Other)).Unattended.Enabled.Should().BeNull("execution enablement never transfers");
        (await bed.Integrations().ListAsync(Other, CancellationToken.None)).Single(i => i.ProviderType == ExternalProviderType.RestApi).Provenance["baseUrl"].Should().Be("Imported from file: test");
    }

    [Fact]
    public async Task Secrets_identities_trust_and_tokens_never_enter_a_package()
    {
        const string content = """
            schemaVersion: 1
            package:
              name: Hostile
              scope: Provider
            metadata:
              containsSecrets: false
            systems:
              - name: Altinn
                provider: Altinn
                enabled: true
                executionAllowed: true
                settings:
                  appId: buf/skolenaervaer
                  clientSecret: s3cr3t
                  unattendedEnabled: true
                  clientId: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc
                  formDataType: "12345678901"
            """;
        var (model, issues) = SetupPackageSchema.Parse("hostile.yaml", content);
        issues.Where(issue => issue.Severity == SetupIssueSeverity.Rejected).Select(issue => issue.Path)
            .Should().Contain(["systems[0].executionAllowed", "systems[0].settings.clientSecret", "systems[0].settings.unattendedEnabled"]);
        issues.Should().Contain(issue => issue.Message.Contains("looks like a secret")).And.Contain(issue => issue.Message.Contains("personal identifier"));
        issues.Should().NotContain(issue => issue.Message.Contains("s3cr3t"), "a rejected value is never echoed");
        model!.Systems[0].Settings.Keys.Should().Equal("appId");
        SetupPackageSchema.Parse("x.yaml", "schemaVersion: 1\npackage:\n  name: x\nmetadata:\n  containsSecrets: true\nsystems:\n  - name: a\n    provider: Other\n").Issues
            .Should().Contain(issue => issue.Severity == SetupIssueSeverity.Error && issue.Path == "metadata.containsSecrets");
    }

    // ── Fail-closed preflight ──

    private static async Task<SetupPreflightResult> PreflightAsync(Bed bed, string? form = AltinnTestKit.RealFormDataType, string extra = "", string environment = Env, ExternalProviderType? provider = null)
    {
        var settings = $"      appId: buf/skolenaervaer\n" + (form is null ? "" : $"      formDataType: {form}\n") + "      integrationId: skolenaervaer-report-intake\n";
        var content = $"schemaVersion: 1\npackage:\n  name: Skolenærvær TT02\n  scope: Provider\n  provider: Altinn\ntestPacks:\n  - skolenaervaer\nsystems:\n  - name: Altinn – Skolenærvær\n    provider: Altinn\n    settings:\n{settings}{extra}";
        return await bed.Setup().PreflightAsync(environment, new(content, "pkg.yaml", Provider: provider), CancellationToken.None);
    }

    private static SetupDependencyResolution Dep(SetupPreflightResult result, SetupDependencyKind kind, string? role = null) =>
        result.Dependencies.First(d => d.Dependency.Kind == kind && (role is null || d.Dependency.Role == role));

    [Fact] // A + H
    public async Task Everything_resolved_enables_apply_and_optional_missing_only_warns()
    {
        var bed = new Bed();
        bed.Identities(); bed.ContractEvidence(); bed.ReportIntake();
        var result = await PreflightAsync(bed);
        result.CanApply.Should().BeTrue(string.Join(" | ", result.Dependencies.Where(d => d.State != SetupDependencyState.Resolved).Select(d => $"{d.Dependency.Kind} {d.Dependency.Reference}: {d.Detail}")));
        result.OptionalMissing.Should().Be(2);
        result.Dependencies.Where(d => !d.Dependency.Required).Should().OnlyContain(d => d.State == SetupDependencyState.OptionalMissing && d.Owner == "Test Data");
        result.SecretsImported.Should().Be(0);
        (await bed.Writes()).Should().Be(0);
    }

    [Fact] // B, C
    public async Task Missing_reporter_or_child_blocks_and_points_to_test_data()
    {
        var bed = new Bed();
        bed.ContractEvidence(); bed.ReportIntake();
        var result = await PreflightAsync(bed);
        result.CanApply.Should().BeFalse();
        Dep(result, SetupDependencyKind.TestData, "Reporter").Should().Match<SetupDependencyResolution>(d => d.State == SetupDependencyState.Missing && d.OwnerHref == "/test-data");
        Dep(result, SetupDependencyKind.TestData, "Child").State.Should().Be(SetupDependencyState.Missing);
        result.BlockReasons.Should().Contain(reason => reason.Contains("required dependencies unresolved"));
    }

    [Fact] // D, E
    public async Task Missing_integration_or_contract_blocks_and_nothing_is_created()
    {
        var bed = new Bed();
        bed.Identities();
        var result = await PreflightAsync(bed);
        Dep(result, SetupDependencyKind.Integration).Should().Match<SetupDependencyResolution>(d => d.State == SetupDependencyState.Missing && d.Owner == "Integration Quality Review");
        Dep(result, SetupDependencyKind.Contract).State.Should().Be(SetupDependencyState.Missing);
        result.CanApply.Should().BeFalse();
        var applied = await bed.Setup().ApplyAsync(Env, new(Content(bed), "pkg.yaml"), "x", CancellationToken.None);
        applied.Applied.Should().BeFalse();
        (await bed.Writes()).Should().Be(0, "a blocked import applies nothing and creates no placeholder");
        bed.Catalog.Should().BeEmpty();
    }

    private static string Content(Bed _) => "schemaVersion: 1\npackage:\n  name: P\n  scope: Provider\nsystems:\n  - name: Altinn\n    provider: Altinn\n    settings:\n      appId: buf/skolenaervaer\n      formDataType: f\n      integrationId: skolenaervaer-report-intake\n";

    [Fact] // F
    public async Task Ambiguous_source_snapshot_blocks_and_the_newest_is_never_chosen()
    {
        var bed = new Bed();
        bed.Identities(); bed.ContractEvidence(); bed.ReportIntake();
        bed.Snapshots.Add(new IqrSourceSnapshot { Archive = new("tiltak2.zip", "aaa", 1), AnalyzedAt = bed.Time.Now.AddDays(-1) });
        bed.Snapshots.Add(new IqrSourceSnapshot { Archive = new("tiltak2.zip", "bbb", 1), AnalyzedAt = bed.Time.Now });
        var result = await PreflightAsync(bed, extra: "dependencies:\n  required:\n    - kind: SourceSnapshot\n      reference: tiltak2.zip\n");
        Dep(result, SetupDependencyKind.SourceSnapshot).Should().Match<SetupDependencyResolution>(d => d.State == SetupDependencyState.Ambiguous && d.OwnerHref == "/source-analysis");
        result.CanApply.Should().BeFalse();
    }

    [Fact] // G
    public async Task Wrong_test_data_type_and_incompatible_contract_block()
    {
        var bed = new Bed();
        bed.Identities(); bed.ContractEvidence("another_form_type"); bed.ReportIntake();
        var result = await PreflightAsync(bed, extra: "dependencies:\n  required:\n    - kind: TestData\n      reference: child-a\n      role: Reporter\n");
        result.Dependencies.Should().Contain(d => d.Dependency.Reference == "child-a" && d.State == SetupDependencyState.Incompatible);
        Dep(result, SetupDependencyKind.Contract).State.Should().Be(SetupDependencyState.Incompatible);
        result.CanApply.Should().BeFalse();
    }

    [Fact]
    public async Task Provider_capability_missing_or_unsatisfied_blocks()
    {
        var bed = new Bed();
        bed.Identities(); bed.ContractEvidence(); bed.ReportIntake();
        var result = await PreflightAsync(bed, form: null);
        Dep(result, SetupDependencyKind.ProviderCapability).Should().Match<SetupDependencyResolution>(d => d.State == SetupDependencyState.Incompatible && d.Detail.Contains("does not satisfy"));
        var none = await bed.Setup().PreflightAsync(Env, new("schemaVersion: 1\npackage:\n  name: P\ntestPacks:\n  - skolenaervaer\nsystems:\n  - name: Person API\n    provider: RestApi\n    settings:\n      baseUrl: https://person.example\n", "p.yaml"), CancellationToken.None);
        Dep(none, SetupDependencyKind.ProviderCapability).State.Should().Be(SetupDependencyState.Missing);
    }

    [Fact]
    public async Task Selected_provider_or_scope_mismatch_is_incompatible_and_unsupported_providers_block()
    {
        var bed = new Bed();
        var wrong = await PreflightAsync(bed, provider: ExternalProviderType.AzureServiceBus);
        wrong.CanApply.Should().BeFalse();
        wrong.Issues.Should().Contain(issue => issue.Message.StartsWith("Incompatible package"));
        wrong.DetectedProviders.Should().Equal(ExternalProviderType.Altinn);
        wrong.DetectedScope.Should().Be(SetupScope.Provider, "the detected scope and provider are shown for review");
        var unsupported = await bed.Setup().PreflightAsync(Env, new("schemaVersion: 1\npackage:\n  name: P\nsystems:\n  - name: K\n    provider: Kafka\n", "k.yaml"), CancellationToken.None);
        unsupported.Issues.Should().Contain(issue => issue.Message.Contains("Unsupported provider"));
        unsupported.CanApply.Should().BeFalse();
    }

    [Fact] // I
    public async Task Conflicts_need_an_explicit_keep_or_apply_decision()
    {
        var bed = await ConfiguredAsync();
        var content = "schemaVersion: 1\npackage:\n  name: P\n  scope: Integration\nsystems:\n  - name: Person API\n    provider: RestApi\n    settings:\n      baseUrl: https://other.example/api\n";
        var setup = bed.Setup();
        var id = (await bed.Integrations().ListAsync(Env, CancellationToken.None)).Single(i => i.ProviderType == ExternalProviderType.RestApi).Id;
        var preflight = await setup.PreflightAsync(Env, new(content, "p.yaml", SetupScope.Integration, ExternalProviderType.RestApi, id), CancellationToken.None);
        preflight.Changes.Should().ContainSingle(c => c.Classification == "Conflict" && c.Current == "https://person.qa.example/api" && c.Proposed == "https://other.example/api");

        (await setup.ApplyAsync(Env, new(content, "p.yaml", SetupScope.Integration, ExternalProviderType.RestApi, id), "x", CancellationToken.None)).Outcome.Should().Contain("decision");
        var keep = await setup.ApplyAsync(Env, new(content, "p.yaml", SetupScope.Integration, ExternalProviderType.RestApi, id, new() { [preflight.Changes[0].Id] = false }), "x", CancellationToken.None);
        keep.Applied.Should().BeTrue();
        (await bed.Integrations().GetAsync(Env, id, CancellationToken.None))!.Settings["baseUrl"].Should().Be("https://person.qa.example/api");
        await setup.ApplyAsync(Env, new(content, "p.yaml", SetupScope.Integration, ExternalProviderType.RestApi, id, new() { [preflight.Changes[0].Id] = true }), "Applied from Setup Library: P v1", CancellationToken.None);
        var updated = (await bed.Integrations().GetAsync(Env, id, CancellationToken.None))!;
        updated.Settings["baseUrl"].Should().Be("https://other.example/api");
        updated.Provenance["baseUrl"].Should().Be("Applied from Setup Library: P v1");
    }

    [Fact] // J
    public async Task A_missing_secret_does_not_block_import_but_automated_execution_stays_blocked()
    {
        var bed = new Bed();
        bed.Identities(); bed.ContractEvidence(); bed.ReportIntake();
        var result = await PreflightAsync(bed);
        result.CanApply.Should().BeTrue();
        (await bed.Setup().ApplyAsync(Env, new((await bed.Setup().ExportAsync(Env, SetupScope.Environment, null, null, SetupPackageFormat.Yaml, null, CancellationToken.None)).Export?.Content
            ?? "schemaVersion: 1\npackage:\n  name: P\n  scope: Provider\nsystems:\n  - name: Altinn – Skolenærvær\n    provider: Altinn\n    settings:\n      appId: buf/skolenaervaer\n      formDataType: " + AltinnTestKit.RealFormDataType + "\n      integrationId: skolenaervaer-report-intake\n", "p.yaml"), "x", CancellationToken.None)).Applied.Should().BeTrue();
        var altinn = (await bed.Integrations().ViewAsync(Env, false, CancellationToken.None)).Integrations.Single();
        altinn.Fields.Single(field => field.Key == "client-secret").State.Should().Be(AltinnSettingState.Missing);
        altinn.Summary.Execution.State.Should().Be(SetupReadinessState.Blocked);
    }

    // ── Setup Library ──

    [Fact]
    public async Task Library_versions_are_immutable_comparable_exportable_and_filterable()
    {
        var bed = await ConfiguredAsync();
        var library = bed.Library();
        var (item, problem) = await library.SaveAsync(new("Skolenærvær TT02", "Altinn TT02 for Skolenærvær", Env, SetupScope.Provider, ExternalProviderType.Altinn, TestPacks: ["skolenaervaer"]), CancellationToken.None);
        problem.Should().BeNull();
        item!.Versions.Should().ContainSingle(v => v.VersionNumber == 1);
        item.Providers.Should().Equal(ExternalProviderType.Altinn);
        var v1 = (await bed.Db.SetupLibraryVersions.SingleAsync()).Payload;
        v1.Should().NotContain("altinn-secret-DO-NOT-EXPORT").And.NotContain(AltinnTestKit.ReporterIdentifier).And.NotContain("unattendedEnabled");

        await bed.Store.UpdateAsync(Env, new() { AppBaseUrl = "https://buf.apps.tt02.altinn.no/buf/skolenaervaer-v2", Scopes = ["openid", "altinn:instances.read"] });
        (await library.NewVersionAsync(item.Id, new("ignored", null, Env, SetupScope.Provider, ExternalProviderType.Altinn, ChangeSummary: "Base URL moved"), CancellationToken.None)).Item!
            .Versions.Select(v => v.VersionNumber).Should().Equal(2, 1);
        (await bed.Db.SetupLibraryVersions.SingleAsync(v => v.VersionNumber == 1)).Payload.Should().Be(v1, "a new version never mutates an earlier one");

        var differences = await library.CompareAsync(item.Id, 1, 2, CancellationToken.None);
        differences.Should().Contain(d => d.Item.EndsWith("baseUrl") && d.After!.EndsWith("skolenaervaer-v2")).And.Contain(d => d.Item.EndsWith("scopes"));

        var json = await library.ExportAsync(item.Id, 1, SetupPackageFormat.Json, CancellationToken.None);
        SetupPackageSchema.Parse(json!.FileName, json.Content).Model!.Systems[0].Settings["appId"].Should().Be("buf/skolenaervaer", "library export round-trips through the file schema");

        (await library.ListAsync(ExternalProviderType.Altinn, null, null, null, null, CancellationToken.None)).Should().ContainSingle();
        (await library.ListAsync(ExternalProviderType.AzureServiceBus, null, null, null, null, CancellationToken.None)).Should().BeEmpty();
        (await library.ListAsync(null, null, "skolenaervaer", null, null, CancellationToken.None)).Should().ContainSingle();
        await library.ArchiveAsync(item.Id, CancellationToken.None);
        (await library.ListAsync(null, null, null, null, null, CancellationToken.None)).Should().BeEmpty("archived items are hidden by default, never deleted");
        (await library.ListAsync(null, null, null, null, SetupLibraryStatus.Archived, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task Library_apply_reruns_the_destination_preflight_and_records_it()
    {
        var bed = await ConfiguredAsync();
        var library = bed.Library();
        var (item, _) = await library.SaveAsync(new("Skolenærvær TT02", null, Env, SetupScope.Provider, ExternalProviderType.Altinn, TestPacks: ["skolenaervaer"]), CancellationToken.None);
        bed.Catalog.RemoveAll(i => i.EnvironmentId == Other);

        var blocked = await library.ApplyAsync(item!.Id, null, Other, null, CancellationToken.None);
        blocked!.Applied.Should().BeFalse();
        blocked.Preflight.Dependencies.Should().Contain(d => d.Dependency.Kind == SetupDependencyKind.Integration && d.State == SetupDependencyState.Missing);
        (await library.GetAsync(item.Id, CancellationToken.None))!.Should().Match<SetupLibraryItemView>(i => i.Status == SetupLibraryStatus.NeedsReview && i.LastPreflight!.StartsWith("Blocked"));
        (await bed.Db.AltinnEnvironmentSettings.CountAsync(r => r.EnvironmentId == Other)).Should().Be(0);

        bed.ReportIntake(Other);
        var applied = await library.ApplyAsync(item.Id, null, Other, null, CancellationToken.None);
        applied!.Applied.Should().BeTrue(applied.Outcome);
        (await bed.Store.GetAsync(Other)).AppIdentifier.Should().Be("buf/skolenaervaer");
        (await library.GetAsync(item.Id, CancellationToken.None))!.LastPreflight.Should().StartWith("Applied");
    }
}
