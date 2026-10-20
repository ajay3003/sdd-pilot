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
    public async Task OnlyMuBound_MuIsBound_M2lbIsAvailableButNotBound_AndTheAltinnAppSourceIsMissing()
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
        Source(guidance, "altinn-app").State.Should().Be(TestPackSourceState.Missing);
        Source(guidance, "altinn-app").NextActionTarget.Should().Be("source-analysis");

        // Architecture rules need M2LB: not assessed, never "component not found in the MU source".
        rules.Findings.Should().HaveCount(4).And.OnlyContain(finding => finding.Outcome == ArchitectureRuleOutcome.NotAssessed);
        rules.Findings.Should().OnlyContain(finding => finding.Detail.StartsWith("Not assessed — this rule requires M2LB source evidence. The currently bound source is MU (Meldingsutveksleren).", StringComparison.Ordinal)
            && finding.Detail.Contains("M2LB (2).zip is available but not bound", StringComparison.Ordinal));
        rules.Findings.Should().NotContain(finding => finding.Detail.Contains("not found", StringComparison.OrdinalIgnoreCase) || finding.Detail.Contains("not present", StringComparison.OrdinalIgnoreCase));
        rules.RuleSources.Should().OnlyContain(item => item.SourceRole == "m2lb" && item.BoundArchive == null && item.AvailableArchive == "M2LB (2).zip" && item.State == TestPackSourceState.AvailableNotBound);

        // Source prerequisites: MM needs M2LB (available, not bound) — not "not present".
        var rapport = (await harness.Service(Pack(harness)).PacksAsync(ActiveEventTestHarness.Env, default)).Single().Journeys.Single(view => view.Journey.JourneyId == "rapportmottak");
        rapport.Prerequisites.Single(item => item.Key == "source:MM").Detail.Should().StartWith("Needs M2LB source evidence: component M2LB.Meldingsutvekslermottak.Host is in M2LB (2).zip");
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
        Source(guidance, "m2lb").Detail.Should().Contain("is not recognized as the M2LB source");
        rules.Findings.Should().OnlyContain(finding => finding.Outcome == ArchitectureRuleOutcome.NotAssessed && finding.Detail.StartsWith("Not assessed — this rule requires M2LB source evidence.", StringComparison.Ordinal));
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
    public async Task NoSourceAtAll_EveryRoleIsMissing_AndTheLocalFixtureIsNeverAManualSource()
    {
        await using var harness = new IntegrationJourneyHarness();
        var (guidance, rules) = await ReadAsync(harness);
        guidance.Sources.Where(item => item.Role.Requirement == TestPackSourceRequirement.Required).Should().OnlyContain(item => item.State == TestPackSourceState.Missing);
        Source(guidance, "infrastructure").State.Should().Be(TestPackSourceState.NotProvided, "Infrastructure as Code is optional for Skolenærvær, never a missing requirement");
        rules.Findings.Should().OnlyContain(finding => finding.Detail.Contains("No M2LB source is imported", StringComparison.Ordinal));
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
        m2lb.Detail.Should().Contain("Several stored snapshots match the M2LB source").And.Contain("not chosen automatically");
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
        sources["altinn-app"].State.Should().Be(TestPackSourceState.Missing);
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
}
