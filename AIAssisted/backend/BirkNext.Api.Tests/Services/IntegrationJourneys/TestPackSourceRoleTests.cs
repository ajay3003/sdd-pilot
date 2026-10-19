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
        guidance.Sources.Should().OnlyContain(item => item.State == TestPackSourceState.Missing);
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
}
