using BirkNext.Api.Data;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.Api.Tests.Services.SourceAnalysis.Evidence;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// Producer/consumer contract discovery from the CANONICAL bound snapshot: evidence-ranked, library schemas only supporting, several candidates
/// or versions never decided, a file-name similarity never authoritative, a stale source selection flagged after a rebind, compatibility separate
/// from availability. Generic synthetic schemas; no product file name is assumed.
/// </summary>
public sealed class IntegrationContractDiscoveryTests
{
    private const string Ns = "urn:example:report";

    private static string Entry(string version, string extraElement = "") => $"""
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns="{Ns}.v{version}" targetNamespace="{Ns}.v{version}" elementFormDefault="qualified" version="{version}">
          <xs:include schemaLocation="example.lib.xsd" />
          <xs:element name="Report">
            <xs:complexType><xs:sequence>
              <xs:element name="Id" type="xs:string" />
              <xs:element name="Hours" type="xs:decimal" minOccurs="0" />{extraElement}
            </xs:sequence></xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private const string Library = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:example:lib" elementFormDefault="qualified">
          <xs:complexType name="AddressType"><xs:sequence><xs:element name="Street" type="xs:string" /></xs:sequence></xs:complexType>
        </xs:schema>
        """;

    private static SourceContract Xsd(string path, string content, params SourceContractCodeReference[] references) =>
        ContractAnalyzer.ReadXmlSchema(path, content) with { CodeReferences = [.. references] };

    private static SourceContractCodeReference Validator(string file = "Receiver/ReportType.cs") => new(file, 21, "Validator", "Receiver");
    private static SourceContractCodeReference Embedded(string file = "Receiver/Receiver.csproj") => new(file, 9, "Embedded resource", "Receiver");

    private static readonly IntegrationDefinition Webhook = new()
    {
        Id = "dev:http:report-intake", EnvironmentId = "dev", DisplayName = "Partner – report intake", Kind = IntegrationKind.HttpApi,
        EndpointOrTopic = "https://receiver.example.invalid/api/partner/webhook", Producer = "Partner platform", Consumer = new IntegrationConsumer { DisplayName = "Message receiver" },
    };

    private static IqrSourceSnapshot Snapshot(Guid id, IEnumerable<SourceContract> contracts, int analyzerVersion = IntegrationContractDiscoveryEngine.CodeReferenceAnalyzerVersion) => new()
    {
        Id = id, Archive = new SourceArchive("receiver.zip", "abc123", 10), AnalyzedAt = DateTimeOffset.Parse("2026-10-20T10:00:00Z"),
        EvidenceDomains = new SourceEvidenceDomainsSnapshot { Contracts = new ContractEvidence { AnalyzerVersion = analyzerVersion, Contracts = [.. contracts] } },
    };

    private static IntegrationEvidenceBinding Binding(IqrSourceSnapshot snapshot) => IntegrationEvidenceBinding.For("dev", Webhook.Id, snapshot, DateTimeOffset.Parse("2026-10-20T11:00:00Z"));

    private static IntegrationReviewEvidenceFacts Facts(string? schemaName = null) => new(Webhook.Id)
    {
        SourceRole = "Receiver source", SourceSide = IntegrationSourceSide.Consumer, ReferenceSide = IntegrationSourceSide.Producer, SchemaName = schemaName,
        ReferenceTitle = "Partner app reference", ContractReference = schemaName is null ? [] : [new TestPackReferenceFact("Form data type", schemaName)],
    };

    private static IntegrationContractDiscovery Discover(IqrSourceSnapshot? snapshot, IntegrationReviewEvidenceFacts? facts, IReadOnlyList<IntegrationContractArtifact>? stored = null, IntegrationEvidenceBinding? binding = null) =>
        IntegrationContractDiscoveryEngine.Discover(new(Webhook, binding ?? (snapshot is null ? null : Binding(snapshot)), snapshot, facts, stored ?? []));

    // §63 + §27 + §19/§20
    [Fact]
    public void OneValidatorReferenceInTheBoundConsumerSource_ResolvesTheConsumerContract_AndDeclaredMetadataIsAProducerCandidate()
    {
        var snapshot = Snapshot(Guid.NewGuid(),
        [
            Xsd("Receiver/Schemas/example.report.v2.0.0.receiver.xsd", Entry("2.0.0"), Validator(), Embedded()),
            Xsd("Receiver/Schemas/example_report_v2_0_0_partner.xsd", Entry("2.0.0")),
            Xsd("Receiver/Schemas/example.lib.xsd", Library, Embedded()),
        ]);
        var discovery = Discover(snapshot, Facts("example_report_v2_0_0_partner"));

        discovery.SourceBound.Should().BeTrue();
        discovery.SnapshotId.Should().Be(snapshot.Id);
        discovery.Consumer.State.Should().Be(IntegrationContractCandidateState.Resolved);
        discovery.Consumer.Primary!.File.Should().EndWith("receiver.xsd");
        discovery.Consumer.Primary.Rank.Should().Be(IntegrationContractEvidenceRank.HandlerReference);
        discovery.Consumer.Primary.Evidence[0].Text.Should().Contain("loads/validates").And.Contain("Receiver/ReportType.cs:21");
        discovery.Consumer.Primary.SupportingSchemas.Should().Equal(["example.lib.xsd"]);

        discovery.Producer.State.Should().Be(IntegrationContractCandidateState.Candidate, "declared metadata is a candidate, not a handler reference");
        discovery.Producer.Primary!.File.Should().EndWith("partner.xsd");
        discovery.Producer.Primary.Rank.Should().Be(IntegrationContractEvidenceRank.ContractMetadata);
        discovery.Producer.ReferenceHints.Should().ContainSingle().Which.Should().Contain("metadata, not a contract");
        // Usage in the consumer's source is never producer evidence: the consumer's schema is at most a name-similarity candidate on the producer side.
        discovery.Producer.Candidates.Where(c => c.File.EndsWith("receiver.xsd")).Should().OnlyContain(c => c.Rank == IntegrationContractEvidenceRank.FilenameSimilarity
            && c.Evidence.All(e => e.Rank == IntegrationContractEvidenceRank.FilenameSimilarity));

        discovery.Compatibility.Should().Be(IntegrationContractCompatibilityState.NotAssessable, "the producer side is only a candidate until a person selects it");
    }

    // §64 + §32
    [Fact]
    public void SeveralVersionsAtTheSameRank_NeedConfirmation_AndTheHighestIsNeverChosen()
    {
        var snapshot = Snapshot(Guid.NewGuid(),
        [
            Xsd("Receiver/Schemas/example.report.v1.0.0.xsd", Entry("1.0.0"), Embedded()),
            Xsd("Receiver/Schemas/example.report.v2.0.0.xsd", Entry("2.0.0"), Embedded()),
        ]);
        var consumer = Discover(snapshot, Facts()).Consumer;
        consumer.State.Should().Be(IntegrationContractCandidateState.NeedsConfirmation);
        consumer.Primary.Should().BeNull();
        consumer.Candidates.Should().HaveCount(2);
        consumer.Explanation.Should().Contain("none is chosen").And.Contain("highest is never chosen");

        var twoValidators = Discover(Snapshot(Guid.NewGuid(),
        [
            Xsd("Receiver/Schemas/example.report.v1.0.0.xsd", Entry("1.0.0"), Validator("Receiver/V1.cs")),
            Xsd("Receiver/Schemas/example.report.v2.0.0.xsd", Entry("2.0.0"), Validator("Receiver/V2.cs")),
        ]), Facts()).Consumer;
        twoValidators.State.Should().Be(IntegrationContractCandidateState.Ambiguous);
        twoValidators.Primary.Should().BeNull();
    }

    [Fact]
    public void EquallyStrongCodeEvidence_OffersTheDeclaredFamilyFirst_AsACandidateNeverResolved()
    {
        var consumer = Discover(Snapshot(Guid.NewGuid(),
        [
            Xsd("Receiver/Receipts/receipt.v2.xsd", Entry("2.0.0"), Validator("Receiver/ReceiptBuilder.cs")),
            Xsd("Receiver/Schemas/example.report.v2.0.0.receiver.xsd", Entry("2.0.0"), Validator()),
        ]), Facts("example_report_v2_0_0_partner")).Consumer;
        consumer.State.Should().Be(IntegrationContractCandidateState.Candidate);
        consumer.Primary!.File.Should().EndWith("receiver.xsd");
        consumer.Explanation.Should().Contain("only orders the candidates");
        IntegrationContractLabels.Usable(consumer).Should().BeFalse("a candidate is used only after a person selects it");

        // Without a declared family the same evidence stays ambiguous.
        Discover(Snapshot(Guid.NewGuid(),
        [
            Xsd("Receiver/Receipts/receipt.v2.xsd", Entry("2.0.0"), Validator("Receiver/ReceiptBuilder.cs")),
            Xsd("Receiver/Schemas/example.report.v2.0.0.receiver.xsd", Entry("2.0.0"), Validator()),
        ]), Facts()).Consumer.State.Should().Be(IntegrationContractCandidateState.Ambiguous);
    }

    [Fact]
    public void ABulkPackagingProject_IsWeakEvidence()
    {
        var bundle = Enumerable.Range(1, 7).Select(i => Xsd($"TestData/xsd/schema{i}.v1.0.0.xsd", Entry("1.0.0"), Embedded("TestData/TestData.csproj"))).ToList();
        var consumer = Discover(Snapshot(Guid.NewGuid(), bundle), Facts()).Consumer;
        consumer.Candidates.Should().OnlyContain(c => c.Rank == IntegrationContractEvidenceRank.SourceRelationship);
        consumer.Candidates[0].Evidence.Single().Text.Should().Contain("Packaged in bulk with 6 other contract files").And.Contain("weak");
        consumer.State.Should().Be(IntegrationContractCandidateState.NeedsConfirmation);
    }

    // Real receiver archives (read-only, never committed). Expected outcomes are what their code shows, derived in the comments below.
    [Fact]
    [Trait("Category", "EnvironmentDependent")]
    public void RealReceiverArchives_ResolveOnlyWhatTheirCodeReferences()
    {
        static IntegrationContractDiscovery? Run(string path)
        {
            if (!File.Exists(path)) return null;
            var (workspace, error) = BirkNext.Api.Services.Integrations.SourceEvidence.IqrSourceArchiveReader.Read(System.IO.Path.GetFileName(path), File.ReadAllBytes(path));
            error.Should().BeNull();
            var id = Guid.NewGuid();
            var at = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
            var architecture = BirkNext.Api.Services.SourceArchitecture.SourceArchitectureAnalyzer.Analyze(id, workspace!, at, null, default, out var input, out _);
            var observability = BirkNext.Api.Services.SourceAnalysis.Observability.ObservabilitySourceAnalyzer.Analyze(id, workspace!, input, architecture, at, default);
            var domains = SourceEvidenceAnalyzer.Analyze(id, workspace!, input, at, architecture, null, observability, null, default, out _);
            var snapshot = new IqrSourceSnapshot { Id = id, Archive = new SourceArchive(System.IO.Path.GetFileName(path), "real", 1), EvidenceDomains = domains };
            return Discover(snapshot, Facts("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn"));
        }

        // The receiving service validates inbound reports with the relaxed m2lb copy (named by its message type's code); the unmodified
        // published copy beside it is only packaged and named by a test. A receipt schema is also validated, so the m2lb copy is a candidate.
        var receiver = Run(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "testdata", "Meldingsutvekslermottak.zip"));
        if (receiver is not null)
        {
            receiver.Consumer.State.Should().Be(IntegrationContractCandidateState.Candidate);
            receiver.Consumer.Primary!.File.Should().EndWith("skolenaervaer.v2.0.0.flat.m2lb.xsd");
            receiver.Consumer.Primary.Rank.Should().Be(IntegrationContractEvidenceRank.HandlerReference);
            receiver.Consumer.Candidates.Single(c => c.File.EndsWith("v2.0.0.flat.xsd")).Rank.Should().NotBe(IntegrationContractEvidenceRank.HandlerReference);
            receiver.Producer.State.Should().NotBe(IntegrationContractCandidateState.Resolved, "nothing in the receiver's source is the sender's schema");
        }

        // The message hub loads its schemas from storage at runtime; its test-data project only bundles copies (several versions). The sender's
        // declared data type has an exact copy there: a producer candidate, never resolved; the consumer side is never resolved by a bundle.
        var hub = Run(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Tiltak 2 - Sikkert digitalt økosystem.zip"));
        if (hub is not null)
        {
            hub.Producer.State.Should().Be(IntegrationContractCandidateState.Candidate);
            hub.Producer.Primary!.File.Should().EndWith("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd");
            hub.Consumer.State.Should().BeOneOf(IntegrationContractCandidateState.NeedsConfirmation, IntegrationContractCandidateState.Unavailable);
            hub.Consumer.Primary.Should().BeNull();
        }
    }

    // §65 + §31
    [Fact]
    public void LibrarySchemas_AreSupportingOnly_NeverThePrimaryContract()
    {
        var onlyLibrary = Discover(Snapshot(Guid.NewGuid(), [Xsd("Receiver/Schemas/example.lib.xsd", Library, Validator())]), Facts()).Consumer;
        onlyLibrary.State.Should().Be(IntegrationContractCandidateState.Unavailable);
        onlyLibrary.Primary.Should().BeNull();
        onlyLibrary.Supporting.Should().ContainSingle().Which.IsEntry.Should().BeFalse();
        onlyLibrary.Explanation.Should().Contain("library").And.Contain("never the primary contract");
        IntegrationContractDiscoveryEngine.IsEntry(ContractAnalyzer.ReadXmlSchema("lib.xsd", Library)).Should().BeFalse();
        IntegrationContractDiscoveryEngine.IsEntry(ContractAnalyzer.ReadXmlSchema("entry.xsd", Entry("1.0.0"))).Should().BeTrue();
    }

    // §66
    [Fact]
    public void AFileNameSimilarityAlone_IsAWeakCandidate_NeverResolved()
    {
        var producer = Discover(Snapshot(Guid.NewGuid(), [Xsd("Docs/example.report.v2.0.0.flat.xsd", Entry("2.0.0"))]), Facts("example_report_v2_0_0_partner")).Producer;
        producer.State.Should().Be(IntegrationContractCandidateState.NeedsConfirmation);
        producer.Primary!.Rank.Should().Be(IntegrationContractEvidenceRank.FilenameSimilarity);
        producer.Primary.Evidence.Single().Text.Should().Contain("similarity only, never proof");
        IntegrationContractLabels.Usable(producer).Should().BeFalse();
    }

    [Fact]
    public void ATestProjectCopy_IsNeverResolved()
    {
        var test = Xsd("Receiver.Tests/Schemas/example.report.v2.0.0.xsd", Entry("2.0.0"), Validator("Receiver.Tests/ReportTests.cs")) with { ProducerBasis = "Test project copy — not a producer" };
        var consumer = Discover(Snapshot(Guid.NewGuid(), [test]), Facts()).Consumer;
        consumer.State.Should().Be(IntegrationContractCandidateState.Candidate);
        consumer.Primary!.TestCopy.Should().BeTrue();
        consumer.Explanation.Should().Contain("test-project copy");
    }

    // §17 + §25 + no newest-unbound selection
    [Fact]
    public void WithoutABinding_NothingIsDiscovered_AndNoOtherSnapshotStandsIn()
    {
        var newest = Snapshot(Guid.NewGuid(), [Xsd("Receiver/Schemas/example.report.v2.0.0.xsd", Entry("2.0.0"), Validator())]);
        var discovery = IntegrationContractDiscoveryEngine.Discover(new(Webhook, null, newest, Facts(), []));
        discovery.SourceBound.Should().BeFalse();
        discovery.Producer.State.Should().Be(IntegrationContractCandidateState.Unavailable);
        discovery.Consumer.State.Should().Be(IntegrationContractCandidateState.Unavailable);
        discovery.Consumer.Candidates.Should().BeEmpty();
        discovery.Consumer.Explanation.Should().Contain("never used in its place");
    }

    [Fact]
    public void AnOlderSnapshotWithoutCodeReferences_SaysSo_AndChangesNoBinding()
    {
        var snapshot = Snapshot(Guid.NewGuid(), [Xsd("Receiver/Schemas/example.report.v2.0.0.xsd", Entry("2.0.0"))], analyzerVersion: 1);
        var discovery = Discover(snapshot, Facts());
        discovery.Limitations.Should().ContainSingle().Which.Should().Contain("Reanalyze").And.Contain("binding is not changed automatically");
        discovery.Consumer.State.Should().Be(IntegrationContractCandidateState.Unavailable);
    }

    // §73
    [Fact]
    public void AfterARebind_ASourceSelectionFromTheOldSnapshotIsStale_AndCompatibilityIsNotAssessable()
    {
        var old = Guid.NewGuid();
        var current = Snapshot(Guid.NewGuid(), [Xsd("Receiver/Schemas/example.report.v2.0.0.xsd", Entry("2.0.0"), Validator())]);
        IntegrationContractArtifact Selected(IntegrationContractRole role, Guid snapshot) => new()
        {
            EnvironmentId = "dev", IntegrationId = Webhook.Id, Role = role, FileName = "example.report.v2.0.0.xsd", Format = "XSD", ContentHash = new string('a', 64),
            Origin = IntegrationContractOrigin.SourceSelection, SourceSnapshotId = snapshot, SourceArchive = "receiver-old.zip", SourceContractId = "xmlschema:x",
        };
        var discovery = Discover(current, Facts(), [Selected(IntegrationContractRole.Producer, old), Selected(IntegrationContractRole.Consumer, current.Id)]);
        discovery.Producer.Selected!.Stale.Should().BeTrue();
        discovery.Consumer.Selected!.Stale.Should().BeFalse();
        IntegrationContractLabels.Usable(discovery.Producer).Should().BeFalse();
        discovery.Compatibility.Should().Be(IntegrationContractCompatibilityState.NotAssessable);
        var readiness = IntegrationReadinessRules.Assess(Webhook, null, new IntegrationReadinessEvidence { SourceBound = true, Contracts = discovery });
        readiness.NeedsAttention.Should().Contain("Producer contract was selected from an earlier source binding — confirm it again");

        var uploaded = Discover(current, Facts(), [Selected(IntegrationContractRole.Producer, old) with { Origin = IntegrationContractOrigin.ManualUpload, SourceSnapshotId = null }]);
        uploaded.Producer.Selected!.Stale.Should().BeFalse("a manual upload is not tied to a snapshot");
        uploaded.Compatibility.Should().Be(IntegrationContractCompatibilityState.ReadyToAssess, "both sides are usable — which is still not compatibility");
    }

    // §33
    [Fact]
    public void Compatibility_IsAssessedSeparately_IdenticalIsCompatible_DifferencesAreListed()
    {
        var snapshot = Snapshot(Guid.NewGuid(), [Xsd("Receiver/Schemas/example.report.v2.0.0.xsd", Entry("2.0.0"), Validator())]);
        var producerUpload = new IntegrationContractArtifact { IntegrationId = Webhook.Id, Role = IntegrationContractRole.Producer, FileName = "partner.xsd", Format = "XSD", ContentHash = new string('b', 64) };
        var ready = Discover(snapshot, Facts(), [producerUpload]);
        ready.Compatibility.Should().Be(IntegrationContractCompatibilityState.ReadyToAssess);
        ready.CompatibilityReason.Should().Contain("Availability is not compatibility");

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Receiver/Schemas/example.report.v2.0.0.xsd"] = Entry("2.0.0"), ["Receiver/Schemas/example.lib.xsd"] = Library };
        (IReadOnlyDictionary<string, string>, string) Single(string name, string content) => (new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = content, ["example.lib.xsd"] = Library }, name);
        var now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        IntegrationContractDiscoveryEngine.WithXsdComparison(ready, Single("partner.xsd", Entry("2.0.0")), (files, "Receiver/Schemas/example.report.v2.0.0.xsd"), now)
            .Compatibility.Should().Be(IntegrationContractCompatibilityState.Compatible);
        var different = IntegrationContractDiscoveryEngine.WithXsdComparison(ready, Single("partner.xsd", Entry("2.0.0", "\n<xs:element name=\"Comment\" type=\"xs:string\" />")),
            (files, "Receiver/Schemas/example.report.v2.0.0.xsd"), now);
        different.Compatibility.Should().Be(IntegrationContractCompatibilityState.DifferencesFound);
        different.CompatibilityDifferences.Should().Contain(d => d.Contains("Comment"));
        IntegrationContractDiscoveryEngine.WithXsdComparison(ready, null, (files, "Receiver/Schemas/example.report.v2.0.0.xsd"), now)
            .Compatibility.Should().Be(IntegrationContractCompatibilityState.ReadyToAssess, "no content: never claimed compatible");
    }

    [Fact]
    public void SourceAnalysis_RecordsValidatorPackagedAndTestReferencesToContractFiles()
    {
        var domains = SourceEvidenceFixtures.Analyze(
            ("Receiver/Receiver.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><EmbeddedResource Include="Schemas\*.xsd" /></ItemGroup></Project>"""),
            ("Receiver/ReportType.cs", """
                using System.Xml.Schema;
                public sealed class ReportType
                {
                    private const string File = "example.report.v2.0.0.receiver.xsd";
                    public XmlSchemaSet Schema() { var set = new XmlSchemaSet(); return set; }
                }
                """),
            ("Receiver/Schemas/example.report.v2.0.0.receiver.xsd", Entry("2.0.0")),
            ("Receiver/Schemas/example.lib.xsd", Library),
            ("Docs/example.report.v2.0.0.flat.xsd", Entry("2.0.0")),
            ("Receiver.Tests/Receiver.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="xunit" Version="2.0.0" /></ItemGroup></Project>"""),
            ("Receiver.Tests/ReportTests.cs", """public sealed class ReportTests { const string Name = "example.lib.xsd"; }"""));
        domains.Contracts!.AnalyzerVersion.Should().Be(IntegrationContractDiscoveryEngine.CodeReferenceAnalyzerVersion);
        var contracts = domains.Contracts.Contracts.Where(c => c.Type == SourceContractType.XmlSchema).ToDictionary(c => System.IO.Path.GetFileName(c.File));
        contracts["example.report.v2.0.0.receiver.xsd"].CodeReferences.Select(r => r.Kind).Should().BeEquivalentTo(["Validator", "Embedded resource"]);
        contracts["example.report.v2.0.0.receiver.xsd"].CodeReferences.Single(r => r.Kind == "Validator").File.Should().Be("Receiver/ReportType.cs");
        contracts["example.lib.xsd"].CodeReferences.Select(r => r.Kind).Should().BeEquivalentTo(["Embedded resource", "Test code reference"]);
        contracts["example.report.v2.0.0.flat.xsd"].CodeReferences.Should().BeEmpty("nothing names it — a file next to others is not referenced");
    }

    // §30 + §16: XSD uploads are accepted; a source selection keeps its snapshot as provenance; nothing else changes.
    [Fact]
    public async Task ContractStore_AcceptsXsd_AndStoresSourceSelectionsWithProvenance()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var catalog = new IntegrationCatalogService(db, NullLogger<IntegrationCatalogService>.Instance);
        var created = await catalog.CreateAsync("dev", Webhook);
        var store = new IntegrationContractStore(db, catalog, NullLogger<IntegrationContractStore>.Instance);

        var (rejected, error) = await store.SaveAsync(new IntegrationContractUpload { EnvironmentId = "dev", IntegrationId = created.Id, Role = IntegrationContractRole.Producer, FileName = "broken.xsd", Content = "<not-a-schema/>" });
        rejected.Should().BeNull();
        error.Should().Contain("XML Schema");

        var (uploaded, _) = await store.SaveAsync(new IntegrationContractUpload { EnvironmentId = "dev", IntegrationId = created.Id, Role = IntegrationContractRole.Producer, FileName = "partner.xsd", Content = Entry("2.0.0") });
        uploaded!.Format.Should().Be("XSD");
        uploaded.Origin.Should().Be(IntegrationContractOrigin.ManualUpload);
        uploaded.FieldCount.Should().BeGreaterThan(0);

        var snapshot = Guid.NewGuid();
        var (selected, _) = await store.SaveSourceSelectionAsync("dev", created.Id, IntegrationContractRole.Consumer, snapshot, "receiver.zip", "xmlschema:Receiver/Schemas/x.xsd", "Receiver/Schemas/x.xsd", Entry("2.0.0"));
        selected!.Origin.Should().Be(IntegrationContractOrigin.SourceSelection);
        (selected.SourceSnapshotId, selected.SourceArchive).Should().Be((snapshot, "receiver.zip"));
        (await store.ListAsync("dev")).Should().HaveCount(2);
        (await store.ContentAsync("dev", created.Id, IntegrationContractRole.Consumer))!.Value.Content.Should().Contain("Report");
        (await store.LoadAsync("dev")).Should().OnlyContain(item => item.Contract == null && item.Problem!.Contains("JSON Schema contracts only"));
        (await catalog.GetAsync("dev", null, null)).Integrations.Single().ContractRelationship.Should().Be(ContractRelationshipState.BothContractsAvailable, "available, never verified");
    }
}
