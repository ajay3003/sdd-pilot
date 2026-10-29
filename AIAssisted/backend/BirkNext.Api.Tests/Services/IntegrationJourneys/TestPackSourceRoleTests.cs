using BirkNext.Api.Services.Integrations.SourceDiscovery;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// Logical source roles: a requirement or rule is evaluated against the canonical binding of the source that is expected to contain it. A
/// component that belongs to another source is "needs that source", never "missing from the bound source"; an available but unbound snapshot
/// is named and never used; a snapshot is recognized by its components (never its archive name); the local OpenAPI fixture is never a manual
/// source. A second, non-Skolenærvær pack proves the mechanism is generic.
/// </summary>
public sealed class TestPackSourceRoleTests
{
    private static IntegrationDefinition Intake => new()
    {
        Id = "dev:http:altinn-skolenaervaer", EnvironmentId = ActiveEventTestHarness.Env, DisplayName = "Skolenærvær – Altinn report intake", Kind = IntegrationKind.HttpApi,
        Enabled = true, Producer = "Altinn", Consumer = new IntegrationConsumer { DisplayName = "MU" },
    };

    private static IntegrationDefinition PeriodClosed => new()
    {
        Id = "dev:servicebus:skolenaervaer-periode-lukket", EnvironmentId = ActiveEventTestHarness.Env, DisplayName = "Skolenærvær – Period Closed", Kind = IntegrationKind.ServiceBus,
        Enabled = true, EndpointOrTopic = "SkolenaervaersperiodeLukket", Producer = "Skolenærværsrapport", Consumer = new IntegrationConsumer { DisplayName = "Bufdata" },
    };

    private static IqrSourceSnapshot Repo(string archive, params (string Name, string? Dependency)[] components) => new()
    {
        Id = Guid.NewGuid(), Archive = new(archive, new string('a', 64), 1), AnalyzedAt = DateTimeOffset.UtcNow,
        Architecture = new ArchitectureSnapshot
        {
            Components = [.. components.Select(item => new ArchitectureComponent { Id = item.Name, Name = item.Name, ComponentType = ArchitectureComponentType.Api }),
                new ArchitectureComponent { Id = "person", Name = "M2LB.Person.Api", ComponentType = ArchitectureComponentType.Api }],
            Dependencies = [.. components.Where(item => item.Dependency is not null).Select(item => new ArchitectureDependency
                { Id = $"{item.Name}->person", FromComponentId = item.Name, ToId = "person", DependencyType = ArchitectureDependencyType.Http })],
        },
    };

    private static IqrSourceSnapshot Mu(string archive = "Tiltak 2 - Sikkert digitalt økosystem (1).zip") => Repo(archive, ("Buf.Meldingsutveksler.Service", null));
    private static IqrSourceSnapshot M2lb(string archive = "M2LB (2).zip") => Repo(archive, ("M2LB.Meldingsutvekslermottak.Host", null), ("M2LB.SkoleAdapter.Worker", null),
        ("M2LB.Utdanning.Api", "person"), ("M2LB.Skolenaervaersrapport.Api", "person"));

    private static SkoleNaervaerJourneyPack Pack(IntegrationJourneyHarness harness)
    {
        var kit = new AltinnTestKit();
        return new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));
    }

    private static async Task<(TestPackSourceGuidance Guidance, ArchitectureRuleReport Rules)> ReadAsync(IntegrationJourneyHarness harness)
    {
        var service = harness.Service(Pack(harness));
        return (await service.SourceGuidanceAsync(ActiveEventTestHarness.Env, SkoleNaervaerJourneyPack.Id, default),
            await service.RulesAsync(ActiveEventTestHarness.Env, SkoleNaervaerJourneyPack.Id, null, default));
    }

    private static TestPackSourceStatus Source(TestPackSourceGuidance guidance, string role) => guidance.Sources.Single(item => item.Role.Key == role);

    [Fact]
    public async Task OnlyMuBound_MuIsBound_M2lbIsAvailableButNotBound_AndTheAltinnAppIsTheTestPackReference()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(Intake);
        var mu = Mu();
        harness.Sources.Snapshots.AddRange([mu, M2lb()]);
        harness.Bindings.Bind(Intake.Id, mu);
        var (guidance, rules) = await ReadAsync(harness);

        Source(guidance, "mu").State.Should().Be(TestPackSourceState.Bound);
        Source(guidance, "mu").BoundArchives.Should().Equal("Tiltak 2 - Sikkert digitalt økosystem (1).zip");
        var m2lb = Source(guidance, "m2lb");
        m2lb.State.Should().Be(TestPackSourceState.AvailableNotBound);
        m2lb.AvailableArchives.Should().Equal("M2LB (2).zip");
        m2lb.NextActionTarget.Should().Be("target-environment-integrations", "no M2LB integration is configured yet");
        Source(guidance, "altinn-app").State.Should().Be(TestPackSourceState.ReferenceAvailable, "the pack provides the Altinn app metadata itself");
        Source(guidance, "altinn-app").Candidates.Should().BeEmpty("a reference is not an imported snapshot");

        // Architecture rules need M2LB: not assessed, never "component not found in the MU source".
        rules.Findings.Should().HaveCount(4).And.OnlyContain(finding => finding.Outcome == ArchitectureRuleOutcome.NotAssessed);
        rules.Findings.Should().OnlyContain(finding => finding.Detail.StartsWith("Not assessed — this rule requires M2LB application source evidence. The currently bound source is MU (Meldingsutveksleren).", StringComparison.Ordinal)
            && finding.Detail.Contains("M2LB (2).zip is available but not bound", StringComparison.Ordinal));
        rules.Findings.Should().NotContain(finding => finding.Detail.Contains("not found", StringComparison.OrdinalIgnoreCase) || finding.Detail.Contains("not present", StringComparison.OrdinalIgnoreCase));
        rules.RuleSources.Should().OnlyContain(item => item.SourceRole == "m2lb" && item.BoundArchive == null && item.AvailableArchive == "M2LB (2).zip" && item.State == TestPackSourceState.AvailableNotBound);

        // Source prerequisites: MM needs M2LB (available, not bound) — not "not present".
        var rapport = (await harness.Service(Pack(harness)).PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.Single(view => view.Journey.JourneyId == "rapportmottak");
        rapport.Prerequisites.Single(item => item.Key == "source:MM").Detail.Should().StartWith("Needs M2LB application source evidence: component M2LB.Meldingsutvekslermottak.Host is in M2LB (2).zip");
        rapport.Prerequisites.Single(item => item.Key == "source:MU").State.Should().Be(JourneyReadinessState.Ready);
    }

    [Fact]
    public async Task WithTheM2lbIntegrationBound_TheRulesAreAssessedAgainstM2lb()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.AddRange([Intake, PeriodClosed]);
        var (mu, m2lb) = (Mu(), M2lb());
        harness.Sources.Snapshots.AddRange([mu, m2lb]);
        harness.Bindings.Bind(Intake.Id, mu);
        harness.Bindings.Bind(PeriodClosed.Id, m2lb);
        var (guidance, rules) = await ReadAsync(harness);

        Source(guidance, "m2lb").State.Should().Be(TestPackSourceState.Bound);
        rules.RuleSources.Should().OnlyContain(item => item.BoundArchive == "M2LB (2).zip");
        rules.SourceArchive.Should().Be("M2LB (2).zip");
        rules.Findings.Single(finding => finding.RuleId == "utdanning-person").Outcome.Should().Be(ArchitectureRuleOutcome.Conforms);
        rules.Findings.Single(finding => finding.RuleId == "mm-no-person").Outcome.Should().Be(ArchitectureRuleOutcome.Conforms);
        rules.Findings.Should().NotContain(finding => finding.Outcome == ArchitectureRuleOutcome.NotAssessed);
    }

    [Fact]
    public async Task TheWrongSourceBoundToAnM2lbIntegration_IsReviewRequired_NotComponentMissing()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.AddRange([Intake, PeriodClosed]);
        var mu = Mu();
        harness.Sources.Snapshots.Add(mu);
        harness.Bindings.Bind(PeriodClosed.Id, mu);
        var (guidance, rules) = await ReadAsync(harness);
        Source(guidance, "m2lb").State.Should().Be(TestPackSourceState.ReviewRequired);
        Source(guidance, "m2lb").Detail.Should().Contain("is not recognized as the M2LB application source");
        rules.Findings.Should().OnlyContain(finding => finding.Outcome == ArchitectureRuleOutcome.NotAssessed && finding.Detail.StartsWith("Not assessed — this rule requires M2LB application source evidence.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANewerUnboundSnapshot_NeverBecomesAuthoritative_AndARenamedArchiveIsStillRecognized()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.AddRange([Intake, PeriodClosed]);
        var bound = M2lb("M2LB (2).zip");
        var renamed = M2lb("m2lb-export-renamed.zip") with { AnalyzedAt = DateTimeOffset.UtcNow.AddDays(2) };
        harness.Sources.Snapshots.AddRange([renamed, bound]);
        harness.Bindings.Bind(PeriodClosed.Id, bound);
        var (guidance, rules) = await ReadAsync(harness);
        Source(guidance, "m2lb").BoundArchives.Should().Equal("M2LB (2).zip");
        Source(guidance, "m2lb").AvailableArchives.Should().Equal(["m2lb-export-renamed.zip"], "recognized by its components, whatever the archive is called");
        rules.SourceArchive.Should().Be("M2LB (2).zip", "the newer unbound snapshot never replaces the binding");
    }

    [Fact]
    public async Task NoSourceAtAll_EveryImportedRoleIsMissing_TheReferenceIsAvailable_AndTheLocalFixtureIsNeverAManualSource()
    {
        await using var harness = new IntegrationJourneyHarness();
        var (guidance, rules) = await ReadAsync(harness);
        guidance.Sources.Where(item => item.Role.Requirement == TestPackSourceRequirement.Required && item.Role.Provider == TestPackSourceProvider.ImportedSource)
            .Select(item => item.Role.Key).Should().Equal("mu", "m2lb");
        guidance.Sources.Where(item => item.Role.Provider == TestPackSourceProvider.ImportedSource && item.Role.Requirement == TestPackSourceRequirement.Required)
            .Should().OnlyContain(item => item.State == TestPackSourceState.Missing);
        Source(guidance, "altinn-app").State.Should().Be(TestPackSourceState.ReferenceAvailable);
        Source(guidance, "infrastructure").State.Should().Be(TestPackSourceState.NotProvided, "Infrastructure as Code is optional for Skolenærvær, never a missing requirement");
        rules.Findings.Should().OnlyContain(finding => finding.Detail.Contains("No M2LB application source is imported", StringComparison.Ordinal));
        var text = System.Text.Json.JsonSerializer.Serialize(new { guidance, rules, roles = Pack(harness).SourceRoles });
        text.Should().NotContain("skolenaervaer.json", "the BirkNext OpenAPI fixture is automated test evidence, never a package a tester imports");
        text.Should().NotContain("testdata");
    }

    // ── Genericity: a second pack with two logical sources and per-rule sources ─────────────────────────────────────

    private sealed class TwoSourcePack : IIntegrationJourneyPack
    {
        public string PackId => "demo.warehouse";
        public string PackVersion => "1";
        public string DisplayName => "Warehouse";
        public string Description => "Two repositories: shop and warehouse.";
        public IReadOnlyList<string> Limitations => [];
        public IReadOnlyList<IntegrationJourneyDefinition> Journeys { get; } =
        [
            new()
            {
                PackId = "demo.warehouse", JourneyId = "order", DisplayName = "Order", Order = 1, ExecutionMode = JourneyExecutionMode.JourneyRunner,
                Integrations = [new("orders", "Orders API", IntegrationKind.HttpApi, "Shop", "Warehouse", "Order"), new("stock", "Stock events", IntegrationKind.ServiceBus, "Warehouse", "Shop", "Stock")],
                Steps = [new("place", "Shop places an order", JourneyStepKind.ExternalSubmission, "Shop", "Accepted", IntegrationKey: "orders")],
                Scenarios = [new("one", "One order", "One synthetic order.", JourneyScenarioSupport.Supported)],
            },
        ];
        public IReadOnlyList<TestPackSourceRole> SourceRoles { get; } =
        [
            new("shop", "Shop", "The web shop") { IntegrationRequirementKeys = ["orders"], ComponentTokens = ["Shop"] },
            new("warehouse", "Warehouse", "The warehouse system") { IntegrationRequirementKeys = ["stock"], ComponentTokens = ["Warehouse"] },
        ];
        public IReadOnlyList<ArchitectureResponsibilityRule> Rules { get; } =
        [
            new("shop-no-person", "Shop does no Person lookup", "Shop", ["Shop"], "Person", ["Person"], ArchitectureRuleExpectation.MustNotDependOn, "r") { SourceRole = "shop" },
            new("warehouse-no-person", "Warehouse does no Person lookup", "Warehouse", ["Warehouse"], "Person", ["Person"], ArchitectureRuleExpectation.MustNotDependOn, "r") { SourceRole = "warehouse" },
        ];
        public bool Matches(JourneyIntegrationRequirement requirement, IntegrationDefinition integration) => integration.Id == $"demo:{requirement.Key}";
        public Task<IReadOnlyList<JourneyPrerequisite>> PrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JourneyPrerequisite>>([]);
    }

    [Fact]
    public async Task AnotherPack_ResolvesEachRuleAgainstItsOwnSource()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.AddRange([
            new IntegrationDefinition { Id = "demo:orders", EnvironmentId = ActiveEventTestHarness.Env, DisplayName = "Orders", Kind = IntegrationKind.HttpApi, Enabled = true },
            new IntegrationDefinition { Id = "demo:stock", EnvironmentId = ActiveEventTestHarness.Env, DisplayName = "Stock", Kind = IntegrationKind.ServiceBus, Enabled = true }]);
        var shop = Repo("shop.zip", ("Acme.Shop.Web", null));
        harness.Sources.Snapshots.AddRange([shop, Repo("warehouse.zip", ("Acme.Warehouse.Worker", null))]);
        harness.Bindings.Bind("demo:orders", shop);
        var service = harness.Service(new TwoSourcePack());
        var rules = await service.RulesAsync(ActiveEventTestHarness.Env, "demo.warehouse", null, default);
        rules.Findings.Single(item => item.RuleId == "shop-no-person").Outcome.Should().Be(ArchitectureRuleOutcome.Conforms);
        rules.Findings.Single(item => item.RuleId == "warehouse-no-person").Detail.Should().Be(
            "Not assessed — this rule requires Warehouse source evidence. The currently bound source is Shop. warehouse.zip is available but not bound.");
        TestPackDescriber.Describe(new TwoSourcePack()).SourceRoles.Select(role => role.Key).Should().Equal("shop", "warehouse");
    }

    // ── Requirement levels, several matching snapshots, Infrastructure as Code and the Import Project source setup ──────

    private static IqrSourceSnapshot WithFingerprint(IqrSourceSnapshot snapshot, char fill) => snapshot with { Archive = snapshot.Archive with { Sha256 = new string(fill, 64) } };

    private static IqrSourceSnapshot Terraform(string archive) => new()
    {
        Id = Guid.NewGuid(), Archive = new(archive, new string('t', 64), 1), AnalyzedAt = DateTimeOffset.UtcNow,
        EvidenceDomains = new BirkNext.SourceDomains.SourceEvidenceDomainsSnapshot
        {
            Infrastructure = new BirkNext.SourceDomains.InfrastructureEvidence
            {
                Domain = BirkNext.SourceDomains.SourceEvidenceDomain.Infrastructure, Formats = [BirkNext.SourceDomains.InfrastructureFormat.Terraform],
                Resources = [new BirkNext.SourceDomains.InfrastructureResource { Id = "main/azurerm_servicebus_namespace.bus", Format = BirkNext.SourceDomains.InfrastructureFormat.Terraform, LogicalName = "bus" }],
            },
        },
    };

    [Fact]
    public async Task TheSkolenaervaerSources_DeclareTheirRequirementLevels_AndTerraformIsNeverARequiredSource()
    {
        await using var harness = new IntegrationJourneyHarness();
        var roles = Pack(harness).SourceRoles.ToDictionary(role => role.Key);
        roles["mu"].Requirement.Should().Be(TestPackSourceRequirement.Required);
        roles["m2lb"].Requirement.Should().Be(TestPackSourceRequirement.Required);
        roles["altinn-app"].Requirement.Should().Be(TestPackSourceRequirement.Required);
        roles["altinn-app"].RequirementCondition.Should().Contain("application metadata");
        var iac = roles["infrastructure"];
        iac.Requirement.Should().Be(TestPackSourceRequirement.Optional);
        iac.Label.Should().Be("Infrastructure as Code", "the logical capability, not one technology");
        iac.RequiresInfrastructureAsCode.Should().BeTrue();
        iac.BindingRequired.Should().BeFalse();
        roles.Values.Where(role => role.Requirement == TestPackSourceRequirement.Required).Should().NotContain(role => role.Label.Contains("Terraform", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SeveralMatchingM2lbSnapshots_AreReviewRequired_UntilOneIsBound_AndTheNewestIsNeverChosen()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.AddRange([Intake, PeriodClosed]);
        var older = WithFingerprint(M2lb("M2LB (2).zip"), 'b') with { AnalyzedAt = DateTimeOffset.UtcNow.AddDays(-3) };
        var newer = WithFingerprint(M2lb("M2LB.zip"), 'c');
        harness.Sources.Snapshots.AddRange([newer, older]);
        var (guidance, _) = await ReadAsync(harness);
        var m2lb = Source(guidance, "m2lb");
        m2lb.State.Should().Be(TestPackSourceState.ReviewRequired);
        m2lb.Detail.Should().Contain("Several stored snapshots match the M2LB application source").And.Contain("not chosen automatically");
        m2lb.Candidates.Select(item => item.ArchiveFileName).Should().Equal("M2LB.zip", "M2LB (2).zip");
        m2lb.Candidates.Should().OnlyContain(item => !item.Bound);
        m2lb.NextActionTarget.Should().Be("iqr-evidence");

        harness.Bindings.Bind(PeriodClosed.Id, older);
        (guidance, _) = await ReadAsync(harness);
        m2lb = Source(guidance, "m2lb");
        m2lb.State.Should().Be(TestPackSourceState.Bound, "the canonical binding wins over the newer import");
        m2lb.BoundArchives.Should().Equal("M2LB (2).zip");
        m2lb.Candidates.First().Should().Match<TestPackSourceCandidate>(item => item.Bound && item.ArchiveFileName == "M2LB (2).zip");
        m2lb.Candidates.Single(item => item.ArchiveFileName == "M2LB.zip").Bound.Should().BeFalse();
    }

    [Fact]
    public async Task TheSameArchiveAnalyzedTwice_IsOneSource_NotAnAmbiguity()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.AddRange([Intake, PeriodClosed]);
        harness.Sources.Snapshots.AddRange([M2lb("M2LB.zip"), M2lb("M2LB (2).zip")]);
        var (guidance, _) = await ReadAsync(harness);
        Source(guidance, "m2lb").State.Should().Be(TestPackSourceState.AvailableNotBound, "same fingerprint = same content, whatever the file is called");
    }

    [Fact]
    public async Task EachM2lbPartArchive_IsRecognizedByItsProjects_NotByItsFileName()
    {
        await using var harness = new IntegrationJourneyHarness();
        var skole = WithFingerprint(Repo("Skole.zip", ("M2LB.Skole.Api", null)), 'd');
        var mm = WithFingerprint(Repo("Meldingsutvekslermottak.zip", ("M2LB.Meldingsutvekslermottak.Host", null), ("M2LB.Meldingsutvekslermottak.Meldingsutveksler", null)), 'e');
        var older = WithFingerprint(Repo("M2LB.zip", ("M2LB.Autorisasjon.Api", null), ("M2LB.Hendelse.Api", null)), 'f');
        harness.Sources.Snapshots.AddRange([skole, mm, older]);
        var (guidance, _) = await ReadAsync(harness);
        Source(guidance, "m2lb").Candidates.Select(item => item.ArchiveFileName).Should().BeEquivalentTo(["Skole.zip", "Meldingsutvekslermottak.zip"],
            "an M2LB export without the Skolenærvær parts is not that source, whatever the archive is called");
        Source(guidance, "m2lb").State.Should().Be(TestPackSourceState.ReviewRequired);
        Source(guidance, "mu").Candidates.Should().BeEmpty("MM's adapter project named after MU does not make the M2LB receiver the MU source");
    }

    private static IqrSourceSnapshot ApiDocuments(string archive, char fill, params (string Title, string Key, string Value)[] values) => new()
    {
        Id = Guid.NewGuid(), Archive = new(archive, new string(fill, 64), 1), AnalyzedAt = DateTimeOffset.UtcNow,
        EvidenceDomains = new BirkNext.SourceDomains.SourceEvidenceDomainsSnapshot
        {
            ConfigurationValues = new BirkNext.SourceDomains.SourceConfigurationValuesEvidence
            {
                Values = [.. values.Select(item => new BirkNext.SourceDomains.SourceConfigurationValue
                {
                    SemanticKey = item.Key, Value = item.Value, EvidenceKind = BirkNext.SourceDomains.SourceConfigurationEvidenceKind.OpenApi,
                    Artifact = "contracts/api.yaml", Scope = item.Title, LogicalSource = item.Title,
                })],
            },
        },
    };

    [Fact]
    public async Task AnApiThatOnlyMentionsTheAppInItsTitle_IsNotTheAltinnApplication()
    {
        await using var harness = new IntegrationJourneyHarness();
        var intake = ApiDocuments("Skole.zip", 'g', ("Skoletjenesten _ rapportinntak (skolenærvær)", BirkNext.SourceDomains.SourceConfigurationKeys.ApplicationDisplayName, "Skoletjenesten _ rapportinntak (skolenærvær)"),
            ("Skoletjenesten _ rapportinntak (skolenærvær)", BirkNext.SourceDomains.SourceConfigurationKeys.AuthenticationScheme, "bearer JWT"));
        var app = ApiDocuments("altinn-app-contract.zip", 'h', ("buf/skolenaervaer", BirkNext.SourceDomains.SourceConfigurationKeys.ApplicationRoute, "buf/skolenaervaer"));
        harness.Sources.Snapshots.AddRange([intake, app]);
        var (guidance, _) = await ReadAsync(harness);
        Source(guidance, "altinn-app").Candidates.Select(item => item.ArchiveFileName).Should().Equal(["altinn-app-contract.zip"],
            "only a document declaring the app route / form data type / process task is the Altinn application");
        Source(guidance, "altinn-app").State.Should().Be(TestPackSourceState.ReferenceAvailable, "an imported Altinn document is additional evidence and never replaces the reference");
    }

    [Fact]
    public async Task InfrastructureAsCode_IsAvailableFromAnyIacSnapshot_WithItsTechnology_AndNeedsNoBinding()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Sources.Snapshots.Add(Terraform("platform-infra.zip"));
        var (guidance, _) = await ReadAsync(harness);
        var iac = Source(guidance, "infrastructure");
        iac.State.Should().Be(TestPackSourceState.Available);
        iac.Candidates.Single().Technologies.Should().Equal("Terraform");
        iac.Detail.Should().Contain("platform-infra.zip").And.Contain("Terraform").And.Contain("No binding is needed");
    }

    [Fact]
    public async Task SourceSetup_WithoutATargetEnvironment_ListsSnapshotsAndSources_ButNothingReadsAsBound()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(Intake);
        var mu = Mu();
        harness.Sources.Snapshots.AddRange([mu, M2lb()]);
        harness.Bindings.Bind(Intake.Id, mu);
        var setup = await harness.Service(Pack(harness)).SourceSetupAsync("", default);
        setup.BindingsRead.Should().BeFalse();
        setup.Snapshots.Should().HaveCount(2).And.OnlyContain(item => item.BoundIntegrations.Count == 0);
        var pack = setup.Packs.Single();
        pack.Sources.Single(item => item.Role.Key == "mu").State.Should().Be(TestPackSourceState.AvailableNotBound);
        pack.Sources.Single(item => item.Role.Key == "mu").NextActionTarget.Should().Be("target-environment");
        setup.Limitations.Should().ContainSingle(item => item.Contains("No target environment is selected"));
    }

    [Fact]
    public async Task SourceSetup_ForATarget_ReadsTheCanonicalBinding_PerSnapshot()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(Intake);
        var (mu, m2lb) = (Mu(), M2lb());
        harness.Sources.Snapshots.AddRange([mu, m2lb]);
        harness.Bindings.Bind(Intake.Id, mu);
        var setup = await harness.Service(Pack(harness)).SourceSetupAsync(ActiveEventTestHarness.Env, default);
        setup.BindingsRead.Should().BeTrue();
        setup.Snapshots.Single(item => item.SnapshotId == mu.Id).BoundIntegrations.Should().Equal("Skolenærvær – Altinn report intake");
        setup.Snapshots.Single(item => item.SnapshotId == m2lb.Id).BoundIntegrations.Should().BeEmpty();
        var sources = setup.Packs.Single().Sources.ToDictionary(item => item.Role.Key);
        sources["mu"].State.Should().Be(TestPackSourceState.Bound);
        sources["mu"].Candidates.Single().Should().Match<TestPackSourceCandidate>(item => item.Bound && item.SnapshotId == mu.Id);
        sources["m2lb"].State.Should().Be(TestPackSourceState.AvailableNotBound);
        sources["altinn-app"].State.Should().Be(TestPackSourceState.ReferenceAvailable);
        sources["infrastructure"].State.Should().Be(TestPackSourceState.NotProvided);
        var text = System.Text.Json.JsonSerializer.Serialize(setup);
        text.Should().NotContain("skolenaervaer.json").And.NotContain("testdata");
    }

    private sealed class ConditionalSourcePack : IIntegrationJourneyPack
    {
        public string PackId => "demo.ledger";
        public string PackVersion => "1";
        public string DisplayName => "Ledger";
        public string Description => "One required repository and one conditional infrastructure source.";
        public IReadOnlyList<string> Limitations => [];
        public IReadOnlyList<IntegrationJourneyDefinition> Journeys { get; } = [];
        public IReadOnlyList<TestPackSourceRole> SourceRoles { get; } =
        [
            new("ledger", "Ledger", "The ledger service") { ComponentTokens = ["Ledger"] },
            new("ledger-infra", "Ledger infrastructure", "Declared infrastructure")
            {
                RequiresInfrastructureAsCode = true, BindingRequired = false, Requirement = TestPackSourceRequirement.Conditional,
                RequirementCondition = "infrastructure resource configuration must be source-verified.",
            },
        ];
        public IReadOnlyList<ArchitectureResponsibilityRule> Rules => [];
        public bool Matches(JourneyIntegrationRequirement requirement, IntegrationDefinition integration) => false;
        public Task<IReadOnlyList<JourneyPrerequisite>> PrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JourneyPrerequisite>>([]);
    }

    [Fact]
    public async Task AnotherPack_RendersItsOwnSourceSetup_WithAConditionalSource()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Sources.Snapshots.Add(Repo("ledger-main.zip", ("Acme.Ledger.Api", null)));
        var setup = await harness.Service(new ConditionalSourcePack()).SourceSetupAsync("", default);
        var pack = setup.Packs.Single();
        pack.DisplayName.Should().Be("Ledger");
        pack.Sources.Single(item => item.Role.Key == "ledger").State.Should().Be(TestPackSourceState.AvailableNotBound);
        var infra = pack.Sources.Single(item => item.Role.Key == "ledger-infra");
        infra.State.Should().Be(TestPackSourceState.NotProvided);
        infra.Detail.Should().Be("Not provided. Needed only when: infrastructure resource configuration must be source-verified.");
    }

    // ── Import Project screenshot case: current import terraform, earlier M2LB.Common and M2LB imports, nothing bound ──────────────────────

    private static IqrSourceSnapshot Imported(IqrSourceSnapshot snapshot, string importId) =>
        snapshot with { ProjectImport = new BirkNext.ProjectImport.ProjectImportProvenance { ImportId = importId, ArchiveFileName = snapshot.Archive.FileName, ArchiveSha256 = snapshot.Archive.Sha256, ImportedAt = snapshot.AnalyzedAt } };

    [Fact]
    public async Task StoredEarlierImports_AreSearched_SoAnUnboundCompatibleM2lbIsAvailableWhileAnotherProjectIsCurrent()
    {
        await using var harness = new IntegrationJourneyHarness();
        var m2lb = Imported(WithFingerprint(M2lb("backend-source-2026.zip"), 'b'), "import-m2lb");
        var common = Imported(WithFingerprint(Repo("M2LB.Common.zip", ("M2LB.Common", null)), 'c'), "import-common");
        var terraform = Imported(Terraform("terraform.zip"), "import-terraform");
        harness.Sources.Snapshots.AddRange([m2lb, common, terraform]);

        var setup = await harness.Service(Pack(harness)).SourceSetupAsync(ActiveEventTestHarness.Env, default);
        var sources = setup.Packs.Single().Sources;
        var m2lbSource = sources.Single(item => item.Role.Key == "m2lb");
        m2lbSource.State.Should().Be(TestPackSourceState.AvailableNotBound, "a compatible earlier import counts even though it is not the current project and nothing binds it");
        var candidate = m2lbSource.Candidates.Single();
        candidate.ArchiveFileName.Should().Be("backend-source-2026.zip", "the archive name is provenance; the components made it M2LB");
        candidate.ImportId.Should().Be("import-m2lb");
        candidate.MatchedOn.Should().Contain(["Component: M2LB.Meldingsutvekslermottak.Host", "Component: M2LB.SkoleAdapter.Worker"]);
        candidate.Bound.Should().BeFalse();
        sources.Single(item => item.Role.Key == "infrastructure").State.Should().Be(TestPackSourceState.Available);
        sources.Single(item => item.Role.Key == "mu").State.Should().Be(TestPackSourceState.Missing);
        harness.Sources.Snapshots.Should().HaveCount(3, "reading source setup never creates or changes snapshots");
    }

    [Fact]
    public async Task AnArchiveNamedLikeTheSource_WithoutItsComponents_StaysMissing_AndSaysWhy()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Sources.Snapshots.Add(Imported(WithFingerprint(Repo("M2LB.zip", ("M2LB.Autorisasjon.Api", null), ("M2LB.Hendelse.Api", null)), 'f'), "import-m2lb"));
        harness.Sources.Snapshots.Add(Imported(Terraform("terraform.zip"), "import-terraform"));

        var setup = await harness.Service(Pack(harness)).SourceSetupAsync(ActiveEventTestHarness.Env, default);
        var m2lb = setup.Packs.Single().Sources.Single(item => item.Role.Key == "m2lb");
        m2lb.State.Should().Be(TestPackSourceState.Missing, "the file name is a hint, never the identity");
        m2lb.Candidates.Should().BeEmpty();
        var miss = m2lb.NearMisses.Should().ContainSingle().Subject;
        miss.ArchiveFileName.Should().Be("M2LB.zip");
        miss.Reason.Should().Contain("none of the expected components").And.Contain("Meldingsutvekslermottak").And.Contain("SkoleAdapter");
        setup.Packs.Single().Sources.Single(item => item.Role.Key == "infrastructure").NearMisses.Should().BeEmpty();
    }

    // ── Import Project: source status and source role semantics ─────────────────────────────────────────────────────────────────────

    /// <summary>An infrastructure-only repository: no application project, so the code analyzer's status is Failed; the evidence domains are analyzed.</summary>
    private static IqrSourceSnapshot InfrastructureOnly(string archive) => Terraform(archive) with
    {
        Status = SourceAnalysisStatus.Failed,
        Architecture = new ArchitectureSnapshot { Status = ArchitectureStatus.Unsupported },
        EvidenceDomains = Terraform(archive).EvidenceDomains! with
        {
            Infrastructure = Terraform(archive).EvidenceDomains!.Infrastructure with { Status = BirkNext.SourceDomains.SourceDomainStatus.Complete },
            Configuration = new BirkNext.SourceDomains.ConfigurationEvidence
            {
                Domain = BirkNext.SourceDomains.SourceEvidenceDomain.Configuration, Status = BirkNext.SourceDomains.SourceDomainStatus.Complete,
                Files = [new() { Path = "env/qa.tfvars", Format = "hcl", Technology = "Terraform variables", Environment = new(BirkNext.SourceDomains.SourceEnvironmentKind.QA, "qa") },
                    new() { Path = "env/prod.tfvars", Format = "hcl", Technology = "Terraform variables", Environment = new(BirkNext.SourceDomains.SourceEnvironmentKind.Production, "prod") }],
            },
            CiCd = new BirkNext.SourceDomains.PipelineEvidence { Domain = BirkNext.SourceDomains.SourceEvidenceDomain.CiCd, Status = BirkNext.SourceDomains.SourceDomainStatus.Partial },
        },
    };

    [Fact]
    public void AreaOutcomes_ShowThatAFailedCodeAnalysisIsOneAreaOnly_AndTheOtherAreasKeepTheirEvidence()
    {
        var areas = SourceSetupAreas.Of(InfrastructureOnly("Terraform-iac-meldingsutveksler.zip")).ToDictionary(a => a.Area, a => a.Status);

        areas.Should().Equal(new Dictionary<string, string>
        {
            ["Application code"] = "NotDetected", ["Architecture"] = "Unsupported", ["Infrastructure as Code"] = "Complete",
            ["Configuration"] = "Complete", ["CI/CD"] = "Partial", ["Contracts"] = "NotDetected",
        });
        // Production projects that exist but could not be read are a failed code analysis, not "no application code".
        SourceSetupAreas.Of(new IqrSourceSnapshot { Status = SourceAnalysisStatus.Failed, Projects = [new SourceProject("Orders.Api", "src/Orders.Api/Orders.Api.csproj", false, "Application", SourceConfidence.Confirmed)] })
            .Single(a => a.Area == "Application code").Status.Should().Be("Failed");
        SourceSetupAreas.Of(new IqrSourceSnapshot { Status = SourceAnalysisStatus.Ready }).Should().Contain(new SourceSetupArea("Architecture", "NotAnalyzed"),
            "a snapshot from before an area existed says so, never Failed");
    }

    [Fact]
    public void ServiceProjects_CountsOnlyWebOrEventHostProjects_SoALibraryOnlyPackageIsNotAnApplication()
    {
        // Project kinds come from the analyzer's own project-file classification (Web SDK / Event Hub), never from the archive name.
        static SourceProject Project(string name, string classification, bool test = false) => new(name, $"src/{name}/{name}.csproj", test, classification, SourceConfidence.StrongSourceEvidence);
        var application = new IqrSourceSnapshot
        {
            Projects = [Project("Orders.Api", "Application/service project"), Project("Orders.Domain", "Production/shared project"),
                Project("Orders.Consumer", "Event Hub SDK project; consumer role requires source review"), Project("Orders.Tests", "Developer test project (layer classified per test)", test: true)],
        };
        SourceSetupAreas.ServiceProjects(application).Should().Be(2);
        SourceSetupAreas.ServiceProjects(new IqrSourceSnapshot { Projects = [Project("Common", "Production/shared project"), Project("Common.Text", "Production/shared project")] })
            .Should().Be(0, "a shared library has production code but no host project");
    }

    [Fact]
    public void A_sample_project_inside_a_documentation_tree_is_not_application_code()
    {
        // The real Terraform-iac-m2lb.zip ships docs/templates/new-api/sample/template-api.csproj next to 129 Terraform files.
        var infrastructure = new IqrSourceSnapshot
        {
            Status = SourceAnalysisStatus.Partial,
            Projects = [new SourceProject("template-api", "docs/templates/new-api/sample/template-api.csproj", false, "Application/service project", SourceConfidence.StrongSourceEvidence)],
        };
        SourceSetupAreas.ServiceProjects(infrastructure).Should().Be(0);
        SourceSetupAreas.Of(infrastructure).Single(a => a.Area == SourceSetupAreas.ApplicationCode).Status.Should().Be("NotDetected");

        var application = infrastructure with { Projects = [.. infrastructure.Projects, new SourceProject("Orders.Api", "src/Orders.Api/Orders.Api.csproj", false, "Application/service project", SourceConfidence.StrongSourceEvidence)] };
        SourceSetupAreas.ServiceProjects(application).Should().Be(1, "real application projects still count");
        SourceSetupAreas.Of(application).Single(a => a.Area == SourceSetupAreas.ApplicationCode).Status.Should().Be("Partial");
    }

    [Fact]
    public async Task SourceSetup_ForAFailedCodeAnalysis_KeepsAreasEnvironmentLabelsAndTheInfrastructureRole()
    {
        await using var harness = new IntegrationJourneyHarness();
        var infra = Imported(InfrastructureOnly("Terraform-iac-meldingsutveksler.zip"), "import-iac");
        harness.Sources.Snapshots.Add(infra);

        var setup = await harness.Service(Pack(harness)).SourceSetupAsync("", default);

        var snapshot = setup.Snapshots.Single();
        snapshot.Status.Should().Be("Failed", "the stored status is the code analyzer's own and is not rewritten");
        snapshot.Areas.Should().Contain(new SourceSetupArea("Infrastructure as Code", "Complete"));
        snapshot.ConfigurationEnvironments.Should().Equal("QA", "Production");
        snapshot.InfrastructureFormats.Should().Equal("Terraform");
        var sources = setup.Packs.Single().Sources;
        sources.Single(s => s.Role.Key == "infrastructure").Candidates.Should().ContainSingle(c => c.SnapshotId == infra.Id);
        sources.Single(s => s.Role.Key == "mu").Candidates.Should().BeEmpty("a name containing \"meldingsutveksler\" never makes infrastructure the MU application");
    }

    [Fact]
    public async Task EachRealSourceShape_IsClassifiedByContent_AndTheM2lbRequirementNeedsTheApplicationSource()
    {
        await using var harness = new IntegrationJourneyHarness();
        var mu = WithFingerprint(Mu("Tiltak 2 - Sikkert digitalt økosystem.zip"), 'm');
        var common = WithFingerprint(Repo("M2LB.Common.zip", ("M2LB.Common", null), ("M2LB.Common.Messaging", null)), 'c');
        var m2lbIac = Terraform("Terraform-iac-m2lb.zip");
        var m01Iac = Terraform("Terraform-iac-m2lbm01.zip");
        var genericIac = Terraform("platform-infra.zip");
        var unknown = WithFingerprint(Repo("orders.zip", ("Orders.Api", null)), 'o');
        harness.Sources.Snapshots.AddRange([mu, common, m2lbIac, m01Iac, genericIac, unknown]);

        var sources = (await harness.Service(Pack(harness)).SourceSetupAsync("", default)).Packs.Single().Sources.ToDictionary(s => s.Role.Key);
        IEnumerable<string> RolesOf(IqrSourceSnapshot snapshot) => sources.Values.Where(s => s.Candidates.Any(c => c.SnapshotId == snapshot.Id)).Select(s => s.Role.Key);

        RolesOf(mu).Should().Equal("mu");
        RolesOf(m2lbIac).Should().Equal(["infrastructure"], "M2LB infrastructure is related evidence, not the M2LB application source");
        RolesOf(m01Iac).Should().Equal("infrastructure");
        RolesOf(genericIac).Should().Equal("infrastructure");
        RolesOf(common).Should().BeEmpty("a shared library has none of the application's components");
        RolesOf(unknown).Should().BeEmpty();

        var m2lb = sources["m2lb"];
        m2lb.Role.Label.Should().Be("M2LB application", "the requirement names the exact role it needs");
        m2lb.State.Should().Be(TestPackSourceState.Missing);
        m2lb.Detail.Should().StartWith("M2LB application source is missing");
        m2lb.NearMisses.Select(n => n.ArchiveFileName).Should().Contain(["M2LB.Common.zip", "Terraform-iac-m2lb.zip"], "M2LB-named archives are checked and explained, never accepted by name");
        m2lb.NearMisses.Should().OnlyContain(n => n.Reason.Contains("none of the expected components"));

        harness.Sources.Snapshots.Add(M2lb());
        var withApplication = (await harness.Service(Pack(harness)).SourceSetupAsync("", default)).Packs.Single().Sources.Single(s => s.Role.Key == "m2lb");
        withApplication.State.Should().Be(TestPackSourceState.AvailableNotBound, "only the application source (MM, SkoleAdapter, Utdanning …) satisfies it");
    }

    [Fact]
    public async Task MatchedOnNamesTheEvidence_ForComponentsAndInfrastructure()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Sources.Snapshots.AddRange([M2lb(), Terraform("platform-infra.zip")]);
        var (guidance, _) = await ReadAsync(harness);

        Source(guidance, "m2lb").Candidates.Single().MatchedOn.Should().HaveCountLessThanOrEqualTo(8).And.Contain("Component: M2LB.Utdanning.Api").And.NotContain(item => item.Contains("Person"));
        Source(guidance, "infrastructure").Candidates.Single().MatchedOn.Should().Equal("Infrastructure as Code: Terraform");
    }

    // ── Integration Catalog discovery scope (multi-source) ──────────────────────────────────────────────────────────────

    private static IqrSourceSnapshot WithArchitecture(IqrSourceSnapshot snapshot) => snapshot with { Architecture = snapshot.Architecture ?? new ArchitectureSnapshot { Status = ArchitectureStatus.Unsupported } };

    [Fact]
    public async Task Discovery_scope_is_the_current_project_plus_recognized_sources_never_unrelated_history_or_a_name_match()
    {
        await using var harness = new IntegrationJourneyHarness();
        var now = DateTimeOffset.UtcNow;
        var common = WithFingerprint(Repo("M2LB.Common.zip", ("M2LB.Common", null)), 'c') with { AnalyzedAt = now.AddHours(-1) };
        var mu = WithFingerprint(Mu("Tiltak 2 - Sikkert digitalt økosystem.zip"), 'm') with { AnalyzedAt = now.AddHours(-2) };
        var olderMu = WithFingerprint(Mu("Tiltak 2 - Sikkert digitalt økosystem.zip"), 'n') with { AnalyzedAt = now.AddHours(-9) };
        var infra = WithArchitecture(Terraform("Terraform-iac-m2lb.zip")) with { AnalyzedAt = now.AddHours(-3) };
        var unrelated = WithFingerprint(Repo("orders.zip", ("Orders.Api", null)), 'o') with { AnalyzedAt = now };
        var namedOnly = WithFingerprint(Repo("Meldingsutveksler-notes.zip", ("Notes.Api", null)), 'q') with { AnalyzedAt = now.AddMinutes(-5) };
        var scope = SourceIntegrationScope.Select([common, mu, olderMu, infra, unrelated, namedOnly], common.Id, [], [Pack(harness)]);

        scope.Select(s => s.Snapshot.Archive.FileName).Should().Equal(["M2LB.Common.zip", "Tiltak 2 - Sikkert digitalt økosystem.zip", "Terraform-iac-m2lb.zip"],
            "the current project first; the newest analysis of each recognized source; unrelated and name-only archives never");
        scope[0].Reasons.Should().Equal(SourceIntegrationScope.PrimaryReason);
        scope[1].Reasons.Single().Should().StartWith("MU (Meldingsutveksleren) source");
        scope[1].Originates.Should().BeTrue();
        scope[2].Originates.Should().BeFalse("Infrastructure as Code only corroborates");
        scope.Should().NotContain(s => s.Snapshot.Id == olderMu.Id, "an earlier analysis of the same archive is replaced, not doubled");

        // An IQR-bound snapshot is in scope even when it is an older analysis; without any context the newest stays the fallback.
        SourceIntegrationScope.Select([mu, olderMu], null, [olderMu.Id], []).Select(s => s.Snapshot.Id).Should().Equal(olderMu.Id);
        SourceIntegrationScope.Select([common, unrelated], null, [], []).Single().Reasons.Should().Equal(SourceIntegrationScope.FallbackReason);
    }

    private static SourceIntegrationCandidate Found(string id, Guid snapshot, string? consumer = null, string? capture = null) =>
        new() { Id = id, SourceSnapshotId = snapshot, Transport = IntegrationTransport.ServiceBusQueue, Pattern = IntegrationPattern.Messaging, ChannelName = id, ConsumerCandidate = consumer, CaptureTechnology = capture };

    private static ScopedSourceSnapshot Scoped(string archive, bool originates) =>
        new(new IqrSourceSnapshot { Id = Guid.NewGuid(), Archive = new(archive, new string('x', 64), 1) }, [originates ? "Current project source" : "Infrastructure as Code source"], originates);

    [Fact]
    public void Merging_deduplicates_by_identity_keeps_every_source_fills_complementary_fields_and_never_picks_between_disagreeing_ones()
    {
        var app = Scoped("app.zip", true);
        var other = Scoped("other.zip", true);
        var infra = Scoped("infra.zip", false);
        var merged = SourceIntegrationScope.Merge(
        [
            (app, new SourceIntegrationDiscoveryResult { SourceSnapshotId = app.Snapshot.Id, Candidates = [Found("q1", app.Snapshot.Id, capture: "Custom"), Found("q2", app.Snapshot.Id)] }),
            (other, new SourceIntegrationDiscoveryResult { SourceSnapshotId = other.Snapshot.Id, Candidates = [Found("q1", other.Snapshot.Id, consumer: "Hub.Consumer", capture: "Debezium")] }),
            (infra, new SourceIntegrationDiscoveryResult { SourceSnapshotId = infra.Snapshot.Id, Candidates = [Found("q2", infra.Snapshot.Id), Found("infra-only", infra.Snapshot.Id)] }),
        ])!;

        merged.Candidates.Select(c => c.Id).Should().Equal(["q1", "q2"], "one candidate per identity; infrastructure never originates one");
        var q1 = merged.Candidates[0];
        q1.Sources.Select(s => s.ArchiveName).Should().Equal("app.zip", "other.zip");
        q1.ConsumerCandidate.Should().Be("Hub.Consumer", "a consumer one repository names fills the gap of another");
        q1.CaptureTechnology.Should().Be("Custom", "the first source's value is kept…");
        q1.SourceDisagreements.Should().ContainSingle().Which.Should().Be("Capture technology: Custom (app.zip) ≠ Debezium (other.zip)", "…and the disagreement is surfaced");
        merged.Candidates[1].Sources.Should().Equal(app.Provenance, infra.Provenance);
        merged.Candidates[1].Sources[1].CorroboratingOnly.Should().BeTrue();
        merged.SourceSnapshotId.Should().Be(app.Snapshot.Id, "the primary context stays first");
    }
}
