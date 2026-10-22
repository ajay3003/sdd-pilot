using BirkNext.Api.Services.IntegrationTopology;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.SchemaEvidence;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.Api.Services.TestData;
using BirkNext.ContractSchemaEvidence;
using BirkNext.Integrations;
using BirkNext.IntegrationTopology;
using BirkNext.Schemas;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.SchemaEvidence;

/// <summary>
/// Shared Contract &amp; Schema Evidence: one normalized model (stored Source Analysis contracts and the shared XSD engine projected into the same
/// shape), entry points (one root / candidate roots / reusable definitions), unresolved references kept, versions preserved without newest
/// selection, one comparison engine, relationship review states that a name match never confirms, and consumers (integrations, topology,
/// Test Data, Test Coverage, Impact, traceability, documentation) that read it instead of re-parsing. Runtime validation is always separate.
/// </summary>
public sealed class ContractEvidenceArchitectureTests
{
    private const string Ns = "urn:example:report";

    private static string Entry(string version, string extra = "", string root = "Report") => $"""
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns="{Ns}" xmlns:lib="urn:example:lib" targetNamespace="{Ns}" elementFormDefault="qualified" version="{version}">
          <xs:import namespace="urn:example:lib" schemaLocation="example.lib.xsd" />
          <xs:element name="{root}">
            <xs:complexType><xs:sequence>
              <xs:element name="Id" type="xs:string" />
              <xs:element name="Status"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="Open" /><xs:enumeration value="Closed" /></xs:restriction></xs:simpleType></xs:element>
              <xs:element name="Hours" minOccurs="0"><xs:simpleType><xs:restriction base="xs:decimal"><xs:minInclusive value="0" /><xs:maxInclusive value="24" /></xs:restriction></xs:simpleType></xs:element>
              <xs:element name="Address" type="lib:AddressType" minOccurs="0" />{extra}
            </xs:sequence></xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private const string Library = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:example:lib" elementFormDefault="qualified">
          <xs:complexType name="AddressType"><xs:sequence><xs:element name="Street" type="xs:string" /></xs:sequence></xs:complexType>
        </xs:schema>
        """;

    private const string TwoRoots = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:example:two" elementFormDefault="qualified">
          <xs:element name="Request" type="xs:string" /><xs:element name="Receipt" type="xs:string" />
        </xs:schema>
        """;

    private static readonly Guid SnapshotId = Guid.Parse("5c000000-0000-0000-0000-000000000013");

    private static ContractEvidenceSource Source(params SourceContract[] contracts) =>
        new(SnapshotId, "receiver.zip", DateTimeOffset.Parse("2026-10-22T08:00:00Z"), new ContractEvidence { AnalyzerVersion = 2, Contracts = [.. contracts] });

    private static ContractFact Fact(SourceContract contract) => ContractEvidenceQueries.Fact(Source(contract), contract);

    private static SourceContract Package(string version, string extra = "") =>
        new XsdPackageContractProvider().Read("report", new Dictionary<string, string> { ["report.xsd"] = Entry(version, extra), ["example.lib.xsd"] = Library });

    [Fact]
    public void Provider_family_reports_capabilities_separately_and_keeps_runtime_validation_separate()
    {
        ContractEvidenceProviders.All.Select(p => p.Key).Should().Contain(["xsd-package", "xsd-source", "openapi", "json-schema", "graphql-sdl", "graphql-introspection", "test-pack-reference"]);
        ContractEvidenceProviders.All.Should().OnlyContain(p => p.RuntimeValidation == ContractCapabilitySupport.SeparateEvidence);
        ContractEvidenceProviders.Get("json-schema").ReferenceResolution.Should().Be(ContractCapabilitySupport.NotSupported, "$ref is not resolved — never overclaimed");
        ContractEvidenceProviders.Get("graphql-introspection").Detection.Should().Be(ContractCapabilitySupport.NotSupported, "runtime introspection is not source evidence");
        ContractEvidenceProviders.Get("test-pack-reference").CannotProve.Should().ContainEquivalentOf("not a Source Analysis snapshot");
        ContractEvidenceProviders.Get("openapi").Proves.Should().Contain("not the application or deployment version");
        ContractEvidenceService.Providers().Should().HaveCount(ContractEvidenceProviders.All.Count);
    }

    [Fact]
    public void Xsd_bundle_is_one_connected_contract_with_its_entry_root_supporting_schema_and_rules()
    {
        var contract = Package("2.0.0");
        var fact = Fact(contract);
        fact.EntryPoint.State.Should().Be(ContractEntryPointState.SingleMessageRoot);
        fact.EntryPoint.Roots.Should().Equal("Report");
        fact.Usage.Should().Be(ContractUsageKind.MessageSchema);
        contract.XmlSchema!.Dependencies.Should().ContainSingle(d => d.Kind == "import" && d.ResolvedInSnapshot, "the supporting library is part of the package");
        contract.XmlSchema.Elements.Should().Contain(e => e.Path == "Report/Address/Street", "the effective structure resolves the imported type");
        contract.XmlSchema.Elements.Should().OnlyContain(e => e.QualifiedName.StartsWith("{"), "names are URI-qualified: a prefix change is not a contract change");
        fact.Parse.Should().Be(ContractParseStatus.Parsed);
        fact.Provenance.Should().Match<ContractProvenance>(p => p.SnapshotId == SnapshotId && p.File == "report.xsd" && p.Namespace == Ns && p.Version == "2.0.0");

        var constraints = ContractEvidenceQueries.GetConstraints(fact);
        constraints.Should().Contain(c => c.Path == "Report/Id" && c.PlainText == "Required field");
        constraints.Should().Contain(c => c.Path == "Report/Status" && c.Kind == "Allowed values" && c.Value == "Open, Closed");
        constraints.Should().Contain(c => c.Path == "Report/Hours" && c.Kind == "Range" && c.Value == "0..24");
        constraints.Should().NotContain(c => c.Path == "Report/Hours" && c.Kind == "Required");
        constraints.Should().OnlyContain(c => c.Provenance.Path == c.Path, "every fact keeps its element path");
    }

    [Fact]
    public void Reusable_library_and_multiple_roots_are_never_errors_and_never_resolved_silently()
    {
        var library = Fact(ContractAnalyzer.ReadXmlSchema("example.lib.xsd", Library));
        library.EntryPoint.State.Should().Be(ContractEntryPointState.ReusableDefinitions);
        library.EntryPoint.Text.Should().Be("Reusable schema definitions · no message entry point detected");
        library.Usage.Should().Be(ContractUsageKind.ReusableLibrary);
        ContractEvidenceQueries.NeedsReview(library with { Parse = ContractParseStatus.Parsed }).Should().BeFalse("a library without a root is not broken");

        var two = Fact(ContractAnalyzer.ReadXmlSchema("two.xsd", TwoRoots));
        two.EntryPoint.State.Should().Be(ContractEntryPointState.MultipleCandidateRoots);
        two.EntryPoint.Roots.Should().BeEquivalentTo(["Request", "Receipt"]);
        two.EntryPoint.Text.Should().Contain("needs selection");
        two.Usage.Should().Be(ContractUsageKind.Unknown, "usage is classified only where evidence supports it");
        ContractEvidenceQueries.NeedsReview(two).Should().BeTrue();
    }

    [Fact]
    public void An_unresolved_import_is_kept_visible_while_the_parsed_content_is_retained()
    {
        var stored = ContractAnalyzer.ReadXmlSchema("Receiver/report.xsd", Entry("2.0.0"), ["Receiver/report.xsd"]);
        var fact = Fact(stored);
        fact.Parse.Should().Be(ContractParseStatus.ReferenceUnresolved);
        fact.UnresolvedReferences.Should().ContainSingle().Which.Should().Match<ContractReferenceFact>(r => r.Location == "example.lib.xsd" && !r.Resolved && r.Provenance.File == "Receiver/report.xsd");
        stored.XmlSchema!.Elements.Should().Contain(e => e.Path == "Report/Id", "other parsed content is retained");
        var summary = ContractEvidenceQueries.Summary(Source(stored) with { Evidence = new ContractEvidence { Contracts = [stored], Diagnostics = [new("Parse error", "broken.xsd: not a schema", "broken.xsd")] } });
        (summary.Total, summary.Partial, summary.Failed, summary.UnresolvedReferences).Should().Be((2, 1, 1, 1), "one malformed contract is counted, never hides the others");
    }

    [Fact]
    public void Several_versions_are_preserved_and_none_is_selected_by_recency()
    {
        var v1 = ContractAnalyzer.ReadXmlSchema("Schemas/report.v2.0.0.flat.xsd", Entry("2.0.0"));
        var v2 = ContractAnalyzer.ReadXmlSchema("Schemas/report.v2.0.1.flat.xsd", Entry("2.0.1"));
        var facts = ContractEvidenceQueries.GetContracts(Source(v1, v2));
        var group = ContractEvidenceQueries.VersionGroups(facts).Should().ContainSingle().Subject;
        group.Members.Should().HaveCount(2);
        group.Text.Should().Contain("no version is selected automatically");

        var relationship = ContractRelationships.Assess(new("Reference", DataType: "Report", ContentType: "application/xml"), facts);
        relationship.State.Should().Be(ContractRelationshipReviewState.Ambiguous, "two equally supported versions are never decided by the newest");
        relationship.Candidates.Should().OnlyContain(c => c.State == ContractRelationshipReviewState.StrongCandidate);

        // Real-repository shape: every service ships its own autodoc/openapi.yaml — different contracts, never "versions" of one.
        SourceContract Api(string service, string title, string version) => new() { Id = $"openapi:{service}/autodoc/openapi.yaml", Type = SourceContractType.OpenApi, Name = title, Version = version, File = $"{service}/autodoc/openapi.yaml" };
        ContractEvidenceQueries.VersionGroups(ContractEvidenceQueries.GetContracts(Source(Api("Person", "Person API", "1.0.0"), Api("Hendelse", "Hendelse API", "0.2.2")))).Should().BeEmpty();
        ContractEvidenceQueries.VersionGroups(ContractEvidenceQueries.GetContracts(Source(Api("PersonV1", "Person API", "1.0.0"), Api("PersonV2", "Person API", "2.0.0")))).Should().ContainSingle();
    }

    [Fact]
    public void Relationship_states_strong_candidate_confirmed_source_usage_and_name_only_candidate()
    {
        var producer = new ContractDeclaration("Test Pack reference", DataType: "Report", ContentType: "application/xml", Version: "2.0.0", Task: "Task_1");
        var plain = ContractAnalyzer.ReadXmlSchema("Receiver/report.v2.0.1.xsd", Entry("2.0.1"));
        var strong = ContractRelationships.Assess(producer, [Fact(plain)]);
        strong.State.Should().Be(ContractRelationshipReviewState.StrongCandidate, "root + content type match, but no source usage");
        strong.Label.Should().Be("Strong candidate · needs review");
        strong.RuntimeValidation.Should().StartWith("Not assessed");
        strong.Differences.Should().ContainSingle(d => d.StartsWith("Version difference") && d.Contains("2.0.0") && d.Contains("2.0.1") && d.Contains("review required"));
        strong.Differences.Should().NotContain(d => d.Contains("incompatib", StringComparison.OrdinalIgnoreCase));

        var used = plain with { CodeReferences = [new("Receiver/ReportValidator.cs", 12, "Validator", "Receiver")] };
        var confirmed = ContractRelationships.Assess(producer, [Fact(used)]);
        confirmed.State.Should().Be(ContractRelationshipReviewState.Confirmed);
        confirmed.Label.Should().Be("Confirmed source usage");
        confirmed.RuntimeValidation.Should().StartWith("Not assessed", "source usage is still not runtime validation");

        var nameOnly = ContractRelationships.Assess(new("Reference", DataType: "skolenaervaer"),
            [Fact(ContractAnalyzer.ReadXmlSchema("Mottak/skolenaervaer.v2.0.1.flat.m2lb.xsd", TwoRoots))]);
        nameOnly.State.Should().Be(ContractRelationshipReviewState.Candidate, "a file-name resemblance alone never confirms a relationship");
        nameOnly.Candidates.Single().Matches.Should().ContainSingle(m => m.Strength == ContractMatchStrength.Weak);

        ContractRelationships.Assess(producer, [Fact(plain)], selectedContractId: plain.Id).State.Should().Be(ContractRelationshipReviewState.Bound, "an explicit selection is bound, not confirmed");
        ContractRelationships.ImpactConfidence(ContractRelationshipReviewState.Confirmed).Should().Be("High-confidence impact");
        ContractRelationships.ImpactConfidence(ContractRelationshipReviewState.StrongCandidate).Should().Be("Likely impact");
        ContractRelationships.ImpactConfidence(ContractRelationshipReviewState.Candidate).Should().Be("Possible impact");
    }

    [Fact]
    public void Skolenaervaer_reference_case_keeps_the_receiver_schema_a_strong_candidate_with_a_version_difference_and_runtime_unverified()
    {
        // Values as the built-in Test Pack reference declares them (reference metadata — not a source snapshot, not a binding).
        var declared = new ContractDeclaration("Skolenærvær Altinn reference", DataType: "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn", ContentType: "application/xml",
            Version: ContractEvidenceQueries.NameVersion("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn"), Task: "Task_1");
        declared.Version.Should().Be("2.0.0");
        // Receiver-side schema shape as Source Analysis stores it: root Skolenaervaer, loaded by the receiver's message type (validator reference).
        const string receiverXsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="https://bufdir.no/barnevern.tiltak.skolenaervaer.v2.0.1" elementFormDefault="qualified" version="2.0.1">
              <xs:element name="Skolenaervaer"><xs:complexType><xs:sequence><xs:element name="Barn" type="xs:string" /></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var receiver = ContractAnalyzer.ReadXmlSchema("MeldingsutvekslerReceiver/src/Receiver.Skolenaervaer/Skjema/bufdir.barnevern.tiltak.skolenaervaer.v2.0.1.flat.m2lb.xsd", receiverXsd)
            with { CodeReferences = [new("MeldingsutvekslerReceiver/src/Receiver.Skolenaervaer/SkolenaervaerMessageType.cs", 19, "Validator")] };
        var altinnCopy = ContractAnalyzer.ReadXmlSchema("TestData/xsds/bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd",
            BirkNext.Api.Tests.Services.Schemas.RealSchemaFixtures.Read("bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd"));

        var assessment = ContractRelationships.Assess(declared, [Fact(receiver), Fact(altinnCopy)]);
        var mu = assessment.Candidates.Single(c => c.File.EndsWith("flat.m2lb.xsd"));
        mu.State.Should().Be(ContractRelationshipReviewState.StrongCandidate, "the receiver loads it, but its root is not the declared data type: matching evidence, not the active downstream schema");
        mu.Matches.Should().Contain(m => m.Kind == "source-usage").And.Contain(m => m.Kind == "content-type");
        assessment.State.Should().Be(ContractRelationshipReviewState.StrongCandidate);
        assessment.Label.Should().Be("Strong candidate · needs review");
        assessment.Differences.Should().Contain(d => d.StartsWith("Version difference: producer 2.0.0") && d.Contains("2.0.1"));
        assessment.RuntimeValidation.Should().StartWith("Not assessed");
        assessment.Candidates.Should().NotContain(c => c.State == ContractRelationshipReviewState.Confirmed, "nothing is confirmed without source usage of the declared message");
    }

    [Fact]
    public void Integration_contract_discovery_carries_the_relationship_with_two_consumer_candidates_needing_review()
    {
        var definition = new IntegrationDefinition { Id = "dev:http:report", EnvironmentId = "dev", DisplayName = "Report", Kind = IntegrationKind.HttpApi, Producer = "Partner", Consumer = new IntegrationConsumer { DisplayName = "Receiver" } };
        var embedded = new SourceContractCodeReference("Receiver/Receiver.csproj", 9, "Embedded resource", "Receiver");
        var snapshot = new IqrSourceSnapshot
        {
            Id = SnapshotId, Archive = new SourceArchive("receiver.zip", "abc", 3), AnalyzedAt = DateTimeOffset.Parse("2026-10-22T08:00:00Z"),
            EvidenceDomains = new SourceEvidenceDomainsSnapshot { Contracts = new ContractEvidence { AnalyzerVersion = 2, Contracts =
            [
                ContractAnalyzer.ReadXmlSchema("Receiver/report.v2.0.0.xsd", Entry("2.0.0")) with { CodeReferences = [embedded] },
                ContractAnalyzer.ReadXmlSchema("Receiver/report.v2.0.1.xsd", Entry("2.0.1")) with { CodeReferences = [embedded] },
            ] } },
        };
        var facts = new IntegrationReviewEvidenceFacts(definition.Id)
        {
            SourceSide = IntegrationSourceSide.Consumer, ReferenceSide = IntegrationSourceSide.Producer, ReferenceTitle = "Partner app reference",
            ContractReference = [new("Form data type", "Report"), new("Content type", "application/xml")],
        };
        var discovery = IntegrationContractDiscoveryEngine.Discover(new(definition, IntegrationEvidenceBinding.For("dev", definition.Id, snapshot, DateTimeOffset.UtcNow), snapshot, facts, []));
        var relationship = discovery.Relationship!;
        relationship.Producer!.DataType.Should().Be("Report");
        relationship.Producer.ContentType.Should().Be("application/xml");
        relationship.Candidates.Should().HaveCount(2);
        relationship.State.Should().Be(ContractRelationshipReviewState.Ambiguous);
        relationship.RuntimeValidation.Should().StartWith("Not assessed");
        discovery.Consumer.Selected.Should().BeNull("discovering never selects or binds");
    }

    [Fact]
    public void One_comparison_engine_classifies_required_added_as_potential_breaking_and_optional_as_non_breaking_candidate()
    {
        var before = Package("2.0.0");
        var requiredAdded = ContractComparison.Compare(before, Package("2.0.0", """<xs:element name="Child" type="xs:string" />"""));
        requiredAdded.Should().ContainSingle(c => c.Path == "Report/Child" && c.Class == ContractChangeClass.FieldAddedRequired && c.Classification == ContractComparison.PotentialBreaking);
        var optionalAdded = ContractComparison.Compare(before, Package("2.0.0", """<xs:element name="Note" type="xs:string" minOccurs="0" />"""));
        optionalAdded.Should().ContainSingle(c => c.Path == "Report/Note" && c.Classification == ContractComparison.NonBreakingCandidate);
        ContractComparison.Compare(before, Package("2.0.1")).Should().Contain(c => c.Class == ContractChangeClass.VersionChanged && c.Classification == ContractComparison.ReviewRequired);
        ContractComparison.Compare(before, before).Should().BeEmpty();

        // The stored-snapshot diff (Source Analysis, Impact Analysis) runs the same per-contract comparison.
        var a = ContractAnalyzer.ReadXmlSchema("r.xsd", Entry("2.0.0"));
        var b = ContractAnalyzer.ReadXmlSchema("r.xsd", Entry("2.0.0").Replace("""<xs:element name="Id" type="xs:string" />""", ""));
        var diff = SourceEvidenceDiff.Compare(new SourceEvidenceDomainsSnapshot { Contracts = new ContractEvidence { Contracts = [a] } }, new SourceEvidenceDomainsSnapshot { Contracts = new ContractEvidence { Contracts = [b] } });
        diff.Select(c => c.Key).Should().BeEquivalentTo(ContractComparison.Compare(a, b).Select(c => $"{b.Id}|{c.Path}"));
    }

    [Fact]
    public void Test_data_reads_normalized_constraints_and_never_picks_the_newest_of_different_versions()
    {
        var analysis = XmlSchemaPackageAnalyzer.Analyze(new Dictionary<string, string> { ["report.xsd"] = Entry("2.0.0"), ["example.lib.xsd"] = Library });
        TestDataSchemaValidator.ContractRules("report.xsd", SnapshotId, analysis).Should()
            .StartWith("Derived from contract report.xsd:").And.Contain("required field(s)").And.Contain("1 allowed-value list(s)").And.Contain("1 range rule(s)");

        var asset = new TestDataAsset
        {
            Id = "fixtures:report", Alias = "report", MediaType = "application/xml", Kind = TestDataKind.Structured, Approval = TestDataApproval.Approved,
            Sensitivity = TestDataSensitivity.Public, Source = "Fixtures",
            Value = new TestDataValue("<Report xmlns=\"urn:example:report\"><Id>1</Id><Status>Open</Status></Report>"), Schema = new("report.xsd"),
        };
        var package = new TestDataSchemaPackage(SnapshotId, new Dictionary<string, string> { ["report.xsd"] = Entry("2.0.0"), ["example.lib.xsd"] = Library }, true);
        var check = TestDataSchemaValidator.Check(asset, package)!;
        check.ContractRules.Should().Contain("Derived from contract");
        var ambiguous = TestDataSchemaValidator.Check(asset, package with { DistinctVersions = 2 })!;
        ambiguous.Status.Should().Be(TestDataSchemaStatus.SchemaUnavailable);
        ambiguous.Detail.Should().Contain("needs selection").And.Contain("never chosen automatically");
    }

    [Fact]
    public void Test_coverage_maps_tests_to_operations_and_contract_files_without_claiming_execution()
    {
        var openApi = new SourceContract
        {
            Id = "openapi:api/openapi.json", Type = SourceContractType.OpenApi, Name = "Orders API", Version = "3.1.0", File = "api/openapi.json", Line = 1, Format = "json",
            Operations = [new("GET /orders/{id}", "GET", "/orders/{id}", ["id"]), new("POST /orders", "POST", "/orders", [])],
            CodeReferences = [new("tests/OrdersContractTests.cs", 4, "Test code reference")],
        };
        var fact = Fact(openApi);
        fact.Versions.Should().ContainSingle(v => v.Value == "3.1.0" && v.Kind.Contains("not the application or deployment version"));
        var links = ContractTestCoverage.Map(fact, [new("Orders_returns_404_for_unknown_id", "tests/OrdersContractTests.cs"), new("Unrelated_test", "tests/Other.cs")]);
        links.Should().Contain(l => l.Confidence == "Direct" && l.Basis.Contains("references the contract file"));
        links.Should().Contain(l => l.Element == "/orders/{id}" || l.Element == "/orders");
        links.Should().NotContain(l => l.Test == "Unrelated_test");
        ContractTestCoverage.Label(ContractTestEvidenceState.GeneratedCandidate).Should().Be("Generated candidate (not executed)");

        ContractTraceability.For([fact], "Orders can be fetched by id").Should().Contain(e => e.Control == "Implemented by API operation" && e.Test == "Not assessed");
        var markdown = ContractDocumentation.Markdown(Source(openApi), [fact]);
        markdown.Should().Contain("Orders API 3.1.0").And.Contain("Operations: GET /orders/{id}, POST /orders").And.NotContain("{\"openapi\"");
        var (nodes, edges) = ContractEvidenceGraph.For([fact]);
        nodes.Should().Contain(n => n.Kind == "Test");
        edges.Should().Contain(e => e.Kind == "tests");
    }

    [Fact]
    public void Topology_annotates_an_existing_connection_and_a_contract_alone_creates_no_edge()
    {
        var builder = new TopologyBuilder();
        var provenance = new TopologyProvenance(TopologySource.IntegrationCatalog, "i1", "Configured integration", null);
        builder.Component("Contract-only schema", TopologyStatus.Discovered, provenance);
        var from = builder.Component("Altinn", TopologyStatus.Configured, provenance);
        var to = builder.Component("MU", TopologyStatus.Configured, provenance);
        builder.Connection(from, to, TopologyInterfaceType.Rest, "HTTP", "/report", TopologyStatus.Configured, provenance, contract: "skolenaervaer.flat.xsd",
            contractStatus: "Configured contract · relationship confirmed (runtime acceptance not verified)");
        var view = builder.Build("dev", null, DateTimeOffset.UtcNow);
        view.Connections.Should().ContainSingle().Which.ContractStatus.Should().Contain("relationship confirmed").And.Contain("runtime acceptance not verified");
    }
}
