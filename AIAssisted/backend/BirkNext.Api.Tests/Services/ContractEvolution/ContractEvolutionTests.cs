using System.Text.RegularExpressions;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.ContractEvolution;
using BirkNext.SourceDomains;
using FluentAssertions;
using static BirkNext.Api.Tests.Services.ContractEvolution.ContractEvolutionFixtures;

namespace BirkNext.Api.Tests.Services.ContractEvolution;

/// <summary>
/// The generic contract-evolution engine on neutral fixtures: normalization through the canonical XSD reader, a meaningful fingerprint,
/// structural diff, direction-aware compatibility, the semantic overlay hook, graph traversal and the first incompatible boundary.
/// </summary>
public sealed class ContractEvolutionTests
{
    private static List<ContractChange> Diff(string current) => ContractDiff.Compare(Snapshot(Baseline), Snapshot(current));
    private static (ContractCompatibility Producer, ContractCompatibility Consumer) Classify(ContractChange change, IReadOnlyCollection<string>? paths,
        ContractUnknownMemberPolicy unknown = ContractUnknownMemberPolicy.Unknown) =>
        (ContractCompatibilityRules.Structural(change, ContractPerspective.Producer, paths).Compatibility,
         ContractCompatibilityRules.Structural(change, ContractPerspective.Consumer, paths, unknown).Compatibility);

    // ── Normalization and fingerprint ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Normalization_UsesTheCanonicalReader_AndKeepsHierarchyOccurrenceTypesEnumsAndNamespace()
    {
        var snapshot = Snapshot(Baseline);
        snapshot.Namespace.Should().Be("urn:example:order:v1");
        snapshot.Descriptor.Version.Should().Be("1.0", "the schema's explicit version");
        snapshot.Nodes.Single(node => node.Path == "Order/Customer/Name").DataType.Should().Contain("string");
        snapshot.Nodes.Single(node => node.Path == "Order/Note").Required.Should().BeFalse();
        snapshot.Nodes.Single(node => node.Path == "Order/Quantity").Required.Should().BeTrue();
        snapshot.Nodes.Single(node => node.Path == "Order/Status").EnumValues.Should().Equal("Closed", "Open");
        snapshot.Fingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Fingerprint_IsDeterministic_IgnoresFormatting_AndChangesWithStructure()
    {
        Snapshot(Baseline).Fingerprint.Should().Be(Snapshot(Baseline).Fingerprint);
        Snapshot(Reformatted).Fingerprint.Should().Be(Snapshot(Baseline).Fingerprint, "whitespace, comments and line endings are not structure");
        foreach (var variant in new[] { OptionalFieldAdded, RequiredFieldAdded, FieldRemoved, TypeChanged, EnumAdded, EnumRemoved, NamespaceChanged, CardinalityChanged, HierarchyChanged, OrderChanged })
            Snapshot(variant).Fingerprint.Should().NotBe(Snapshot(Baseline).Fingerprint);
    }

    [Fact]
    public void UnchangedContract_HasNoDrift_AndNoBlockers()
    {
        var result = ContractImpactAnalyzer.Analyze(Snapshot(Baseline), Snapshot(Reformatted), Graph());
        result.Changed.Should().BeFalse();
        result.Changes.Should().BeEmpty();
        result.Overall.Should().Be(ContractCompatibility.Compatible);
        result.FirstIncompatibleBoundary.Should().BeNull();
        result.Boundaries.Should().OnlyContain(item => item.Overall == ContractCompatibility.Compatible);
    }

    // ── Diff and direction-aware structural compatibility ─────────────────────────────────────────────────────────

    [Fact]
    public void OptionalFieldAdded_IsCompatibleForTheProducer_AndForATolerantConsumer()
    {
        var change = Diff(OptionalFieldAdded).Should().ContainSingle().Subject;
        (change.Change, change.Path).Should().Be((ContractChangeClass.FieldAddedOptional, "Order/Comment"));
        Classify(change, ["Order/Id"], ContractUnknownMemberPolicy.Ignore).Should().Be((ContractCompatibility.Compatible, ContractCompatibility.Compatible));
        Classify(change, ["Order/Id"], ContractUnknownMemberPolicy.Reject).Consumer.Should().Be(ContractCompatibility.Breaking, "a strict consumer rejects unknown members");
        Classify(change, ["Order/Id"]).Consumer.Should().Be(ContractCompatibility.NeedsReview, "unknown tolerance is never assumed");
    }

    [Fact]
    public void RequiredFieldAdded_BreaksAProducerThatDoesNotWriteIt_ButNotATolerantConsumer()
    {
        var change = Diff(RequiredFieldAdded).Should().ContainSingle().Subject;
        change.Change.Should().Be(ContractChangeClass.FieldAddedRequired);
        Classify(change, ["Order/Id", "Order/Quantity"], ContractUnknownMemberPolicy.Ignore).Should().Be((ContractCompatibility.Breaking, ContractCompatibility.Compatible));
        Classify(change, ["Order/Currency"]).Producer.Should().Be(ContractCompatibility.Compatible, "a producer that already writes it is fine");
        Classify(change, null).Producer.Should().Be(ContractCompatibility.NeedsReview, "unknown producer paths are never compatible");
    }

    [Fact]
    public void FieldRemoved_BreaksPartiesThatUseIt_AndIsCompatibleForPartiesThatDoNot()
    {
        var change = Diff(FieldRemoved).Should().ContainSingle().Subject;
        (change.Change, change.Path).Should().Be((ContractChangeClass.FieldRemoved, "Order/Quantity"));
        Classify(change, ["Order/Quantity"]).Should().Be((ContractCompatibility.Breaking, ContractCompatibility.Breaking));
        Classify(change, ["Order/Id"]).Should().Be((ContractCompatibility.Compatible, ContractCompatibility.Compatible));
    }

    [Fact]
    public void TypeChanged_IsBreakingForUsers_AndNeverSilentlyCoerced()
    {
        var change = Diff(TypeChanged).Should().ContainSingle().Subject;
        change.Change.Should().Be(ContractChangeClass.TypeChanged);
        (change.OldValue, change.NewValue).Should().Be(("{http://www.w3.org/2001/XMLSchema}int", "{http://www.w3.org/2001/XMLSchema}decimal"));
        Classify(change, ["Order/Quantity"]).Should().Be((ContractCompatibility.Breaking, ContractCompatibility.Breaking));
    }

    [Fact]
    public void EnumValueAdded_NeedsReviewForAConsumerThatReadsIt_EnumValueRemoved_ForAProducerThatWritesIt()
    {
        var added = Diff(EnumAdded).Should().ContainSingle().Subject;
        (added.Change, added.NewValue).Should().Be((ContractChangeClass.EnumValueAdded, "Cancelled"));
        Classify(added, ["Order/Status"]).Should().Be((ContractCompatibility.Compatible, ContractCompatibility.NeedsReview));
        var removed = Diff(EnumRemoved).Should().ContainSingle().Subject;
        (removed.Change, removed.OldValue).Should().Be((ContractChangeClass.EnumValueRemoved, "Closed"));
        Classify(removed, ["Order/Status"]).Should().Be((ContractCompatibility.NeedsReview, ContractCompatibility.Compatible));
    }

    [Fact]
    public void NamespaceChange_IsDetectedOnce_AndSameNamesAreNotAssumedEquivalent()
    {
        var changes = Diff(NamespaceChanged);
        changes.Where(change => change.Change == ContractChangeClass.NamespaceChanged).Should().ContainSingle().Which.NewValue.Should().Be("urn:example:order:v2");
        changes.Should().OnlyContain(change => change.Change == ContractChangeClass.NamespaceChanged || change.Change == ContractChangeClass.TypeChanged,
            "member types are qualified by the namespace, so they change with it");
        Classify(changes.First(), null).Should().Be((ContractCompatibility.NeedsReview, ContractCompatibility.NeedsReview));
    }

    [Fact]
    public void CardinalityAndOrderChanges_AreDetected_AndNeedReviewOrBreakStrictConsumers()
    {
        Diff(CardinalityChanged).Should().ContainSingle().Which.Change.Should().Be(ContractChangeClass.CardinalityChanged);
        var order = Diff(OrderChanged).Should().ContainSingle(change => change.Change == ContractChangeClass.OrderChanged).Subject;
        order.Path.Should().Be("Order");
        Classify(order, ["Order"], ContractUnknownMemberPolicy.Reject).Consumer.Should().Be(ContractCompatibility.Breaking, "a strict sequence validator rejects a reorder");
    }

    [Fact]
    public void HierarchyChange_IsARemovalPlusAnAddition_NeverAGuessedRename()
    {
        var changes = Diff(HierarchyChanged);
        changes.Should().Contain(change => change.Change == ContractChangeClass.FieldRemoved && change.Path == "Order/Customer/Name");
        changes.Should().Contain(change => change.Change == ContractChangeClass.FieldAddedRequired && change.Path == "Order/CustomerName");
        changes.Should().NotContain(change => change.Detail.Contains("renam", StringComparison.OrdinalIgnoreCase));
    }

    // ── Graph traversal, semantic overlay and first incompatible boundary ─────────────────────────────────────────

    /// <summary>Producer → A → Consumer 1 (pass-through mapping A → B) → B → Consumer 2, plus an unknown mapping branch.</summary>
    private static ContractDependencyGraph Graph(bool consumer2ReadsCount = true) => new(
    [
        new() { Id = "producer", Label = "Producer → A", Role = ContractBoundaryRole.Produces, ContractId = ContractId, Paths = ["Order/Id", "Order/Customer/Name", "Order/Quantity", "Order/Status"] },
        new() { Id = "transport", Label = "A transport", Role = ContractBoundaryRole.Transports, ContractId = ContractId },
        new()
        {
            Id = "consumer-1", Label = "Consumer 1 maps A → B", Role = ContractBoundaryRole.Maps, ContractId = ContractId, OutputContractId = "example:summary",
            Mapping = new() { ["Order/Quantity"] = ["Summary/Count"], ["Order/Id"] = ["Summary/OrderId"] }, PassThrough = true,
        },
        new() { Id = "consumer-2", Label = "Consumer 2 reads B", Role = ContractBoundaryRole.Consumes, ContractId = "example:summary",
            Paths = consumer2ReadsCount ? ["Summary/Count"] : ["Summary/OrderId"], UnknownMembers = ContractUnknownMemberPolicy.Ignore },
        new() { Id = "unknown-mapper", Label = "Unknown mapper A → C", Role = ContractBoundaryRole.Maps, ContractId = ContractId, OutputContractId = "example:other", Mapping = null },
        new() { Id = "consumer-3", Label = "Consumer 3 reads C", Role = ContractBoundaryRole.Consumes, ContractId = "example:other", Paths = ["X"] },
    ]);

    private sealed class EverythingReviewed : IContractSemanticRules
    {
        public ContractSemanticAssessment? Assess(ContractChange change) => new(ContractCompatibility.Compatible, "Reviewed: technical only.");
    }

    [Fact]
    public void FirstIncompatibleBoundary_IsFoundDownstream_ThroughADeclaredPassThroughMapping()
    {
        var result = ContractImpactAnalyzer.Analyze(Snapshot(Baseline), Snapshot(TypeChanged), Graph(), new EverythingReviewed());

        result.Changed.Should().BeTrue();
        Boundary(result, "producer").Overall.Should().Be(ContractCompatibility.Breaking, "the producer writes Quantity as int");
        result.FirstIncompatibleBoundary!.BoundaryId.Should().Be("producer");

        var downstreamOnly = ContractImpactAnalyzer.Analyze(Snapshot(Baseline), Snapshot(TypeChanged), new(Graph().Boundaries.Where(b => b.Id != "producer").ToList()), new EverythingReviewed());
        Boundary(downstreamOnly, "transport").Overall.Should().Be(ContractCompatibility.Compatible);
        Boundary(downstreamOnly, "consumer-1").Overall.Should().Be(ContractCompatibility.Compatible, "a pass-through carries the change unchanged");
        Boundary(downstreamOnly, "consumer-2").Should().Match<ContractBoundaryImpact>(item => item.Overall == ContractCompatibility.Breaking && !item.Potential && item.Depth == 1);
        downstreamOnly.FirstIncompatibleBoundary!.BoundaryId.Should().Be("consumer-2");
        downstreamOnly.FirstIncompatibleBoundary.Reasons.Should().Contain(reason => reason.Contains("Summary/Count", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnmappedChange_DoesNotReachDownstreamConsumers()
    {
        var result = ContractImpactAnalyzer.Analyze(Snapshot(Baseline), Snapshot(EnumAdded), new(Graph().Boundaries.Where(b => b.Id != "unknown-mapper").ToList()), new EverythingReviewed());
        Boundary(result, "consumer-1").Overall.Should().Be(ContractCompatibility.Compatible);
        Boundary(result, "consumer-2").Should().Match<ContractBoundaryImpact>(item => item.Overall == ContractCompatibility.Compatible && item.Reasons[0].Contains("No changed data", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownEvidence_IsNotEvaluated_NeverCompatible()
    {
        var result = ContractImpactAnalyzer.Analyze(Snapshot(Baseline), Snapshot(OptionalFieldAdded), Graph());

        Boundary(result, "unknown-mapper").Overall.Should().Be(ContractCompatibility.NotEvaluated, "the mapping is not known");
        Boundary(result, "consumer-3").Should().Match<ContractBoundaryImpact>(item => item.Overall == ContractCompatibility.NotEvaluated && item.Potential);
        result.Changes.Single().Semantic.Should().Be(ContractCompatibility.NotEvaluated, "without a domain rule the meaning of a change is not evaluated");
        Boundary(result, "producer").Overall.Should().Be(ContractCompatibility.NotEvaluated, "structurally compatible, semantically not evaluated");
        result.Overall.Should().NotBe(ContractCompatibility.Compatible);
        result.Unknowns.Should().Contain(item => item.StartsWith("Unknown mapper", StringComparison.Ordinal));
    }

    [Fact]
    public void StructuralAndSemanticCompatibility_StaySeparate_AndOverallIsTheWorse()
    {
        var reviewNeeded = new Overlay(ContractCompatibility.NeedsReview);
        var result = ContractImpactAnalyzer.Analyze(Snapshot(Baseline), Snapshot(OptionalFieldAdded), Graph(), reviewNeeded);
        var producer = Boundary(result, "producer");
        (producer.Structural, producer.Semantic, producer.Overall).Should().Be((ContractCompatibility.Compatible, ContractCompatibility.NeedsReview, ContractCompatibility.NeedsReview));
    }

    [Fact]
    public void WithoutABaseline_NothingIsCalledChangedOrCompatible()
    {
        var result = ContractImpactAnalyzer.Analyze(null, Snapshot(Baseline), Graph());
        result.BaselineAvailable.Should().BeFalse();
        result.Changed.Should().BeFalse();
        result.Overall.Should().Be(ContractCompatibility.NotEvaluated);
        result.Boundaries.Should().OnlyContain(item => item.Overall == ContractCompatibility.NotEvaluated);
    }

    [Fact]
    public void AnUnsupportedContractVersion_NeedsReview_AtAConsumerThatDeclaresItsVersions()
    {
        var previous = Snapshot(Baseline);
        var current = ContractSnapshots.FromXmlSchemaText(Descriptor(), "contracts/order.xsd", Baseline.Replace("qualified\" version=\"1.0\"", "qualified\" version=\"2.0\""), At);
        var graph = new ContractDependencyGraph([new() { Id = "strict", Label = "Strict consumer", Role = ContractBoundaryRole.Consumes, ContractId = ContractId,
            Paths = ["Order/Id"], UnknownMembers = ContractUnknownMemberPolicy.Reject, SupportedVersions = ["1.0"] }]);
        var result = ContractImpactAnalyzer.Analyze(previous, current, graph, new EverythingReviewed());
        result.Changes.Should().ContainSingle().Which.Change.Change.Should().Be(ContractChangeClass.VersionChanged);
        Boundary(result, "strict").Should().Match<ContractBoundaryImpact>(item => item.Overall == ContractCompatibility.NeedsReview && item.Reasons[0].Contains("2.0", StringComparison.Ordinal));
    }

    // ── Other contract types share the engine ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void JsonFieldsAndOpenApi_NormalizeIntoTheSameModel()
    {
        var before = ContractSnapshots.FromFields(new("example:json", SourceContractType.JsonSchema, "Json", null, "fixture"), null,
            [new("order.id", "string", true), new("order.status", "string", false, ["Open"])], At, "fixture");
        var after = ContractSnapshots.FromFields(before.Descriptor, null,
            [new("order.id", "string", true), new("order.status", "string", false, ["Open", "Held"]), new("order.total", "number", true)], At, "fixture");
        ContractDiff.Compare(before, after).Select(change => (change.Change, change.Path)).Should().BeEquivalentTo(new[]
        {
            (ContractChangeClass.EnumValueAdded, "order/status"), (ContractChangeClass.FieldAddedRequired, "order/total"),
        });

        var api = new NormalizedContract { Operations = [new NormalizedOperation { Method = "POST", Path = "/orders", Parameters = [new NormalizedParameter { Name = "tenant", Location = NormalizedParameterLocation.Header, Required = true, Type = "string" }] }] };
        var openApi = ContractSnapshots.FromOpenApi(new("example:api", SourceContractType.OpenApi, "Api", null, "fixture"), api, At);
        openApi.Nodes.Select(node => node.Path).Should().Equal("POST /orders", "POST /orders/header/tenant");
    }

    // ── Security and architecture ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void XmlSchemaReading_RefusesDtdsAndExternalEntities_AndNeverFetchesImports()
    {
        const string xxe = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///c:/windows/win.ini">]><xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="A" type="xs:string"/></xs:schema>""";
        FluentActions.Invoking(() => Snapshot(xxe)).Should().Throw<System.Xml.XmlException>();

        const string remote = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:import namespace="urn:x" schemaLocation="https://attacker.invalid/x.xsd"/><xs:element name="A" type="xs:string"/></xs:schema>""";
        var contract = BirkNext.Api.Services.SourceAnalysis.Evidence.ContractAnalyzer.ReadXmlSchema("a.xsd", remote);
        contract.XmlSchema!.Dependencies.Should().ContainSingle().Which.ResolvedInSnapshot.Should().BeFalse("an import is recorded, never fetched");
    }

    [Fact]
    public void GenericEngine_KnowsNoProjectDomainOrIntegrationNames()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "BirkNext.Api"))) root = Path.GetDirectoryName(root)!;
        var files = Directory.EnumerateFiles(Path.Combine(root, "BirkNext.Api", "Services", "ContractEvolution"), "*.cs")
            .Append(Path.GetFullPath(Path.Combine(root, "..", "shared", "ContractEvolutionContracts.cs"))).ToList();
        files.Should().HaveCountGreaterThan(4);
        var forbidden = new Regex(@"Skole|Skolenærvær|skolenaervaer|Altinn|Bufdata|Meldingshode|\bMU\b|\bMM\b|M2LB|BiRK|buf/", RegexOptions.CultureInvariant);
        files.Where(file => forbidden.IsMatch(File.ReadAllText(file))).Select(Path.GetFileName).Should().BeEmpty();
    }

    private sealed class Overlay(ContractCompatibility verdict) : IContractSemanticRules
    {
        public ContractSemanticAssessment? Assess(ContractChange change) => new(verdict, "Domain rule (test).");
    }

    private static ContractBoundaryImpact Boundary(ContractImpactResult result, string id) => result.Boundaries.Single(item => item.BoundaryId == id);
}
