using System.Text.RegularExpressions;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.ActiveEventTesting;
using BirkNext.Api.Tests.Services.AltinnTesting;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// The generic test-pack layer: any registered journey pack is described from its own metadata (journeys, verification views, rules,
/// required capabilities, history, navigation), a second pack with no Skolenærvær name works without any change to shared code, packs never
/// share state, and the canonical Integration Quality Review evidence binding — not a pack-private snapshot choice — decides which snapshot
/// architecture rules use. Nothing here contacts a network.
/// </summary>
public sealed class TestPackArchitectureTests
{
    /// <summary>A minimal second pack: one runner journey, one read-only verification view, one rule. Generic names only.</summary>
    private sealed class DemoIntegrationTestPack : IIntegrationJourneyPack
    {
        public const string IntakeId = "demo:http:parcel-intake";
        public string PackId => "demo.parcels";
        public string PackVersion => "1";
        public string DisplayName => "Parcel delivery testing";
        public string Description => "Demo pack proving the test-pack engine is generic.";
        public IReadOnlyList<string> Limitations => [];
        public List<JourneyPrerequisite> Extra { get; } = [];

        public IReadOnlyList<IntegrationJourneyDefinition> Journeys { get; } =
        [
            new()
            {
                PackId = "demo.parcels", JourneyId = "parcel-booking", DisplayName = "Parcel booking", Order = 1, ExecutionMode = JourneyExecutionMode.JourneyRunner,
                Integrations = [new("intake", "Parcel intake API", IntegrationKind.HttpApi, "Shop", "Carrier", "Booking")],
                Steps =
                [
                    new("book", "Shop books a parcel", JourneyStepKind.ExternalSubmission, "Shop", "Accepted", IntegrationKey: "intake", FromComponent: "Shop", ToComponent: "Carrier"),
                    new("label", "Carrier prints a label", JourneyStepKind.DomainState, "Carrier", "Label stored", FromComponent: "Carrier", ToComponent: "Label store"),
                ],
                Scenarios = [new("book-one", "Book one parcel", "One synthetic parcel.", JourneyScenarioSupport.Supported)],
            },
        ];

        public IReadOnlyList<ArchitectureResponsibilityRule> Rules { get; } =
            [new("shop-no-label", "Shop does not print labels", "Shop", ["Shop"], "Label store", ["Label"], ArchitectureRuleExpectation.MustNotDependOn, "Labels are the carrier's.")];

        public IReadOnlyList<TestPackView> VerificationViews { get; } =
            [new("tracking-check", "Tracking check", TestPackViewKind.Verification, "parcel-booking", "book-one", "Read-only tracking evidence.")];

        public IReadOnlyList<TestPackCapabilityRequirement> RequiredTestDataCapabilities { get; } = [new("Recipient", "Synthetic recipient", "Test Data")];

        public bool Matches(JourneyIntegrationRequirement requirement, IntegrationDefinition integration) => requirement.Key == "intake" && integration.Id == IntakeId;

        public Task<IReadOnlyList<JourneyPrerequisite>> PrerequisitesAsync(IntegrationJourneyDefinition journey, JourneyPackContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JourneyPrerequisite>>(Extra.ToArray());

        public static IntegrationDefinition Intake => new() { Id = IntakeId, EnvironmentId = ActiveEventTestHarness.Env, DisplayName = "Parcel intake", Kind = IntegrationKind.HttpApi, Enabled = true };
    }

    private static SkoleNaervaerJourneyPack Skole(IntegrationJourneyHarness harness)
    {
        var kit = new AltinnTestKit();
        return new(harness.MessageFlows, kit.Policy, kit.Contracts(harness.MessageFlows, harness.Sources));
    }

    private static IEnumerable<string> ViewIds(TestPackDescriptor descriptor) => descriptor.Navigation.SelectMany(group => group.Views).Select(view => view.ViewId);

    private static IqrSourceSnapshot Snapshot(string archive, params string[] components) => new()
    {
        Id = Guid.NewGuid(), Archive = new(archive, new string('a', 64), 1), AnalyzedAt = DateTimeOffset.UtcNow,
        Architecture = new ArchitectureSnapshot { Components = [.. components.Select(name => new ArchitectureComponent { Id = name, Name = name, ComponentType = ArchitectureComponentType.Api })] },
    };

    // ── Generic engine ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Demo_pack_is_described_entirely_from_its_own_metadata()
    {
        var descriptor = TestPackDescriber.Describe(new DemoIntegrationTestPack());
        descriptor.Journeys.Should().ContainSingle().Which.Should().Be(new TestPackJourney("parcel-booking", "Parcel booking", "Shop → Carrier → Label store", JourneyExecutionMode.JourneyRunner, null));
        descriptor.Navigation.Select(group => group.Label).Should().Equal("Overview", "Journeys", "Verify", "History");
        ViewIds(descriptor).Should().Equal("overview", "parcel-booking", "tracking-check", "architecture-rules", "history");
        descriptor.RuleIds.Should().Equal("shop-no-label");
        descriptor.RequiredIntegrationCapabilities.Should().ContainSingle().Which.Key.Should().Be("intake");
        descriptor.RequiredTestDataCapabilities.Should().ContainSingle().Which.Owner.Should().Be("Test Data");
        descriptor.ExecutionProviders.Should().Equal("Shared journey runner");
        descriptor.SupportsHistory.Should().BeTrue();
    }

    [Fact]
    public void A_pack_without_rules_or_verification_gets_no_verify_group()
    {
        var descriptor = TestPackDescriber.Describe(new ExampleOrdersJourneyPack());
        descriptor.Navigation.Select(group => group.Label).Should().Equal("Overview", "Journeys", "History");
        descriptor.RequiredTestDataCapabilities.Should().BeEmpty("the interface default declares none");
    }

    [Fact]
    public async Task Multiple_packs_register_side_by_side_and_keep_their_own_journeys_and_prerequisites()
    {
        await using var harness = new IntegrationJourneyHarness();
        var demo = new DemoIntegrationTestPack();
        demo.Extra.Add(new("demo-only", "Demo prerequisite", JourneyPrerequisiteCategory.SyntheticData, JourneyReadinessState.NotReady, "Demo pack only."));
        var registry = new IntegrationJourneyPackRegistry([demo, Skole(harness), new ExampleOrdersJourneyPack()]);
        registry.Packs.Select(pack => pack.PackId).Should().Equal("demo.parcels", "example.orders", SkoleNaervaerJourneyPack.Id);

        var views = await harness.Service(registry.Packs.ToArray()).PacksAsync(ActiveEventTestHarness.Env, CancellationToken.None);
        views.Single(view => view.PackId == "demo.parcels").Journeys.Single().Prerequisites.Should().Contain(item => item.Key == "demo-only");
        views.Where(view => view.PackId != "demo.parcels").SelectMany(view => view.Journeys).SelectMany(journey => journey.Prerequisites)
            .Should().NotContain(item => item.Key == "demo-only", "one pack's prerequisites never leak into another pack");
    }

    [Fact]
    public void Duplicate_pack_ids_are_refused()
    {
        var act = () => new IntegrationJourneyPackRegistry([new DemoIntegrationTestPack(), new DemoIntegrationTestPack()]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*demo.parcels*");
    }

    [Fact]
    public void Generic_test_pack_code_names_no_domain_or_project()
    {
        var api = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BirkNext.Api");
        var shared = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "shared");
        if (!Directory.Exists(api)) return; // Source-level check runs where the repository sources are present.
        var files = new[]
        {
            Path.Combine(api, "Services", "IntegrationJourneys", "TestPackDescriber.cs"),
            Path.Combine(api, "Services", "IntegrationJourneys", "IntegrationJourneyContracts.cs"),
            Path.Combine(api, "Services", "Integrations", "SourceEvidence", "IntegrationEvidenceBindingStore.cs"),
            Path.Combine(api, "Controllers", "IntegrationEvidenceBindingsController.cs"),
        };
        foreach (var file in files)
        {
            File.Exists(file).Should().BeTrue(file);
            Regex.IsMatch(File.ReadAllText(file), @"Skole|Skolen|Utdanning|Rapportmottak|Periodeavslutning|Altinn|TT02|Bufdata|M2LB", RegexOptions.IgnoreCase)
                .Should().BeFalse($"{Path.GetFileName(file)} is generic test-pack/evidence code");
        }
        // The shared descriptor and binding contracts: only code is checked (comments may cite examples).
        foreach (var file in new[] { Path.Combine(shared, "IntegrationJourneyContracts.cs"), Path.Combine(shared, "IqrSourceContracts.cs") })
        {
            var code = string.Join('\n', File.ReadAllLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Regex.IsMatch(code, @"Skole|Rapportmottak|Periodeavslutning|Utdanning|Altinn|TT02|M2LB", RegexOptions.IgnoreCase).Should().BeFalse($"{Path.GetFileName(file)} code is generic");
        }
    }

    // ── Skolenærvær as the reference pack ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Skolenaervaer_registers_three_journeys_message_verification_rules_and_history_with_deterministic_ids()
    {
        await using var harness = new IntegrationJourneyHarness();
        var descriptor = TestPackDescriber.Describe(Skole(harness));
        descriptor.PackId.Should().Be("skolenaervaer");
        descriptor.Journeys.Select(journey => (journey.JourneyId, journey.DisplayName, journey.Flow)).Should().Equal(
            ("utdanningsdata", "Utdanningsdata", "BiRK → Debezium → Event Hub → SkoleAdapter → Utdanning"),
            ("rapportmottak", "Rapportmottak", "Altinn → MU → MM → Skolenærværsrapport"),
            ("periodeavslutning", "Periodeavslutning", "Skolenærværsrapport → Service Bus → Bufdata"));
        descriptor.Journeys[0].ExecutionMode.Should().Be(JourneyExecutionMode.DelegatedToActiveEvent);
        descriptor.Journeys[0].ExecutionProvider.Should().Be("skolenaervaer.cdc");
        descriptor.VerificationViews.Should().ContainSingle().Which.Should().Be(new TestPackView("message-verification", "Message Verification", TestPackViewKind.Verification,
            "rapportmottak", "message-verification", "Read-only verification of what happened to one message, from recorded and telemetry evidence."));
        descriptor.RuleIds.Should().Equal("mm-no-person", "skoleadapter-no-person", "utdanning-person", "rapport-person");
        descriptor.Navigation.Select(group => group.Label).Should().Equal("Overview", "Journeys", "Verify", "History");
        ViewIds(descriptor).Should().Equal("overview", "utdanningsdata", "rapportmottak", "periodeavslutning", "message-verification", "architecture-rules", "history");
        descriptor.RequiredTestDataCapabilities.Select(item => item.Owner).Should().OnlyContain(owner => owner == "Test Data");
        descriptor.SupportsHistory.Should().BeTrue();
        TestPackDescriber.FindView(descriptor, "rapportmottak")!.Kind.Should().Be(TestPackViewKind.Journey);
        TestPackDescriber.FindView(descriptor, "unknown").Should().BeNull();
    }

    // ── Canonical evidence binding ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Architecture_rules_use_the_snapshot_bound_in_IQR_and_ignore_a_newer_unbound_snapshot()
    {
        await using var harness = new IntegrationJourneyHarness();
        var bound = Snapshot("bound.zip", "Shop", "Carrier") with { AnalyzedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        var newer = Snapshot("newer.zip", "Shop");
        harness.Sources.Snapshots.AddRange([newer, bound]);
        harness.Active.Catalog.Extra.Add(DemoIntegrationTestPack.Intake);
        harness.Bindings.Bind(DemoIntegrationTestPack.IntakeId, bound);

        var report = await harness.Service(new DemoIntegrationTestPack()).RulesAsync(ActiveEventTestHarness.Env, "demo.parcels", null, CancellationToken.None);
        report.SourceSnapshotId.Should().Be(bound.Id, "the canonical binding wins; a newer snapshot never replaces it silently");
        report.SourceArchive.Should().Be("bound.zip");
        report.Limitations.Should().NotContain(item => item.Contains("newest snapshot", StringComparison.Ordinal));
    }

    // ── Architecture rules against every source-evidence state ──────────────────────────────────────────────────────

    public enum EvidenceState { NoSnapshot, UnboundOnly, BoundToAnotherIntegration, BoundWithoutArchitecture, Bound }

    /// <summary>
    /// Rules are assessed only against the snapshot bound in IQR to one of the pack's integrations. Every other state is Not assessed: no
    /// snapshot, an unbound snapshot (even the newest, which is named only as a non-authoritative candidate), a snapshot bound to an
    /// integration outside the pack, and a bound snapshot without architecture analysis.
    /// </summary>
    [Theory]
    [InlineData(EvidenceState.NoSnapshot, ArchitectureRuleOutcome.NotAssessed)]
    [InlineData(EvidenceState.UnboundOnly, ArchitectureRuleOutcome.NotAssessed)]
    [InlineData(EvidenceState.BoundToAnotherIntegration, ArchitectureRuleOutcome.NotAssessed)]
    [InlineData(EvidenceState.BoundWithoutArchitecture, ArchitectureRuleOutcome.NotAssessed)]
    [InlineData(EvidenceState.Bound, ArchitectureRuleOutcome.Conforms)]
    public async Task Architecture_rules_are_assessed_only_against_the_canonical_binding(EvidenceState state, ArchitectureRuleOutcome expected)
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(DemoIntegrationTestPack.Intake);
        var snapshot = state == EvidenceState.BoundWithoutArchitecture ? Snapshot("source.zip") with { Architecture = null } : Snapshot("source.zip", "Shop");
        if (state != EvidenceState.NoSnapshot) harness.Sources.Snapshots.Add(snapshot);
        if (state is EvidenceState.Bound or EvidenceState.BoundWithoutArchitecture) harness.Bindings.Bind(DemoIntegrationTestPack.IntakeId, snapshot);
        if (state == EvidenceState.BoundToAnotherIntegration) harness.Bindings.Bind("other:integration", snapshot);

        var report = await harness.Service(new DemoIntegrationTestPack()).RulesAsync(ActiveEventTestHarness.Env, "demo.parcels", null, CancellationToken.None);

        report.Findings.Should().ContainSingle().Which.Outcome.Should().Be(expected);
        if (expected == ArchitectureRuleOutcome.NotAssessed)
            report.Findings.Should().NotContain(finding => finding.Outcome == ArchitectureRuleOutcome.Conforms || finding.Outcome == ArchitectureRuleOutcome.PotentialDeviation,
                "an unbound or missing source never yields an assessed rule");
        if (state is EvidenceState.UnboundOnly or EvidenceState.BoundToAnotherIntegration)
        {
            report.SourceSnapshotId.Should().BeNull("no snapshot was assessed");
            report.Findings.Single().Detail.Should().Be(ArchitectureRuleEvaluator.NotBoundReason);
            report.Limitations.Should().Contain(item => item.StartsWith("Not authoritative: source.zip", StringComparison.Ordinal), "the candidate is named for discovery only");
        }
        if (state is EvidenceState.Bound or EvidenceState.BoundWithoutArchitecture) report.SourceSnapshotId.Should().Be(snapshot.Id);
    }

    [Fact]
    public async Task An_explicitly_requested_snapshot_is_assessed_only_when_it_is_the_bound_one()
    {
        await using var harness = new IntegrationJourneyHarness();
        harness.Active.Catalog.Extra.Add(DemoIntegrationTestPack.Intake);
        var bound = Snapshot("bound.zip", "Shop");
        var other = Snapshot("other.zip", "Shop");
        harness.Sources.Snapshots.AddRange([bound, other]);
        harness.Bindings.Bind(DemoIntegrationTestPack.IntakeId, bound);
        var service = harness.Service(new DemoIntegrationTestPack());

        var unbound = await service.RulesAsync(ActiveEventTestHarness.Env, "demo.parcels", other.Id, CancellationToken.None);
        unbound.Findings.Should().OnlyContain(finding => finding.Outcome == ArchitectureRuleOutcome.NotAssessed);
        unbound.Findings.Single().Detail.Should().Contain("other.zip is not the source evidence bound");
        unbound.SourceSnapshotId.Should().BeNull();

        var assessed = await service.RulesAsync(ActiveEventTestHarness.Env, "demo.parcels", bound.Id, CancellationToken.None);
        assessed.Findings.Single().Outcome.Should().Be(ArchitectureRuleOutcome.Conforms);
        assessed.SourceSnapshotId.Should().Be(bound.Id);
    }

    [Fact]
    public void A_binding_keeps_the_identity_it_was_bound_with()
    {
        var snapshot = Snapshot("source.zip") with { ProjectImport = new BirkNext.ProjectImport.ProjectImportProvenance { ImportId = "import-1" } };
        var binding = IntegrationEvidenceBinding.For("env", "integration", snapshot, DateTimeOffset.Parse("2026-10-19T08:00:00Z"));
        binding.Should().BeEquivalentTo(new { EnvironmentId = "env", IntegrationId = "integration", SnapshotId = snapshot.Id, Fingerprint = snapshot.Archive.Sha256,
            ArchiveName = "source.zip", AnalyzedAt = snapshot.AnalyzedAt, ImportId = "import-1" });
        binding.Selection.Should().Be(new IqrSourceSelection("integration", snapshot.Id));
    }
}
