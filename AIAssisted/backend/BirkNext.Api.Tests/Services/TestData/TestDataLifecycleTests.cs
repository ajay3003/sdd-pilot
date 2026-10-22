using BirkNext.Api.Services.SchemaEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestData;
using BirkNext.ContractSchemaEvidence;
using BirkNext.DatabaseArchitecture;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>
/// Shared reusable Test Data lifecycle: candidates from contract and data model evidence carry structure, rules and expected outcomes (never
/// identity values), are never approved by being proposed, readiness never reuses another environment's asset, a source change marks assets for
/// review without modifying them, and executions/documentation see aliases and versions only.
/// </summary>
public sealed class TestDataLifecycleTests
{
    private static readonly Guid SnapshotId = Guid.Parse("7d000000-0000-0000-0000-000000000018");

    private const string Xsd = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:example:report" elementFormDefault="qualified">
          <xs:element name="Report">
            <xs:complexType><xs:sequence>
              <xs:element name="Fodselsnummer" type="xs:string" />
              <xs:element name="Status"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="Open" /><xs:enumeration value="Closed" /></xs:restriction></xs:simpleType></xs:element>
              <xs:element name="Note" type="xs:string" minOccurs="0" />
            </xs:sequence></xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private static SourceContract Contract() => new XsdPackageContractProvider().Read("report", new Dictionary<string, string> { ["schemas/report.xsd"] = Xsd });

    private static ContractFact Fact(SourceContract contract) => ContractEvidenceQueries.Fact(
        new ContractEvidenceSource(SnapshotId, "receiver.zip", DateTimeOffset.Parse("2026-10-22T08:00:00Z"), new ContractEvidence { AnalyzerVersion = 2, Contracts = [contract] }), contract);

    private static DatabaseArchitectureSnapshot Model() => new()
    {
        SourceSnapshotId = SnapshotId,
        Databases = [new DatabaseModel { Id = "db", LogicalName = "Reports", Schemas = [new SchemaModel { Name = "dbo", Tables =
            [new TableModel { Id = "db-t-person", LogicalName = "Persons", PhysicalName = "person", EntityTypeName = "Person", EvidenceState = DatabaseEvidenceState.Confirmed,
                Columns = [new ColumnModel { Name = "Id", ClrType = "Guid", Nullable = false, IsPrimaryKey = true }, new ColumnModel { Name = "PersonIdent", ClrType = "string", Nullable = false, MaxLength = 11 }] }] }] }],
    };

    private static TestDataAssetSummary Asset(string alias, string? environment = null, TestDataApproval approval = TestDataApproval.Approved, string? schema = null,
        string? entity = null, TestDataSchemaStatus? schemaStatus = null) => new()
    {
        Id = alias, Alias = alias, Kind = TestDataKind.Structured, Environment = environment, Approval = approval, Source = "Configured", EntityType = entity,
        Fingerprint = "f00dfeedcafebabe0123456789", DisplayValue = "SECRET-VALUE",
        Schema = schema is null ? null : new TestDataSchemaReference(schema),
        SchemaCheck = schemaStatus is { } status ? new TestDataSchemaCheck(status, schema ?? "", "", []) : null,
    };

    [Fact]
    public void Contract_candidates_are_structure_and_expected_outcomes_with_identities_as_controlled_references()
    {
        var candidates = TestDataLifecycle.FromContract(Fact(Contract()));

        candidates.Should().OnlyContain(c => c.State == TestDataLifecycleState.Candidate && c.Origin == TestDataCandidateOrigin.ContractEvidence, "proposing never approves");
        var valid = candidates.Single(c => c.Intent == TestDataIntent.Positive);
        valid.ExpectedOutcome.Should().Be("Accepted");
        valid.DerivedFrom.Should().Contain(SnapshotId.ToString("D"));
        valid.Structure.Should().Contain(s => s.Contains("Fodselsnummer") && s.EndsWith(TestDataLifecycle.ControlledReference), "identities are never generated");
        valid.Structure.Should().NotContain(s => s.Any(char.IsDigit) && s.Contains("Fodselsnummer") && !s.Contains(TestDataLifecycle.ControlledReference));
        candidates.Where(c => c.Intent == TestDataIntent.Negative).Should().NotBeEmpty()
            .And.OnlyContain(c => c.ExpectedOutcome!.StartsWith("Rejected") && c.ExpectedOutcome.Contains("expected, passing outcome"), "a negative case keeps its expected rejection apart from any actual result");
    }

    [Fact]
    public void Data_model_candidates_list_columns_and_rules_without_values()
    {
        var candidate = TestDataLifecycle.FromDataModel(BirkNext.DataModelEvidence.DataModelQueries.GetEntities(Model())).Should().ContainSingle().Subject;
        candidate.Origin.Should().Be(TestDataCandidateOrigin.DataModelEvidence);
        candidate.Structure.Should().Contain(s => s.StartsWith("PersonIdent") && s.EndsWith(TestDataLifecycle.ControlledReference));
        candidate.State.Should().Be(TestDataLifecycleState.Candidate);
        TestDataLifecycle.IsIdentity("Fødselsnummer").Should().BeTrue();
        TestDataLifecycle.IsIdentity("Status").Should().BeFalse();
    }

    [Fact]
    public void Readiness_never_reuses_another_environment_and_reports_stale_and_candidate_only()
    {
        TestDataLifecycle.Readiness("payload", [Asset("a", "Test")], 0, "Test").State.Should().Be(TestDataReadinessState.Ready);
        TestDataLifecycle.Readiness("payload", [Asset("a", "Test")], 0, "Production").State.Should().Be(TestDataReadinessState.EnvironmentMismatch);
        TestDataLifecycle.Readiness("payload", [Asset("a", "Test")], 0, null).State.Should().Be(TestDataReadinessState.Ready, "no environment selected: the asset's own scope applies");
        TestDataLifecycle.Readiness("payload", [Asset("a")], 0, "Production").State.Should().Be(TestDataReadinessState.Ready, "an unscoped asset applies to any environment");
        TestDataLifecycle.Readiness("payload", [Asset("a", schema: "report.xsd", schemaStatus: TestDataSchemaStatus.SchemaUnavailable)], 0, "Test").State.Should().Be(TestDataReadinessState.Stale);
        TestDataLifecycle.Readiness("payload", [Asset("a", approval: TestDataApproval.Unapproved)], 2, "Test").State.Should().Be(TestDataReadinessState.CandidateOnly);
        TestDataLifecycle.Readiness("payload", [], 0, "Test").State.Should().Be(TestDataReadinessState.Missing);
    }

    [Fact]
    public void A_source_change_marks_affected_assets_for_review_without_modifying_them()
    {
        TestDataAssetSummary[] assets = [Asset("report", schema: "schemas/report.xsd"), Asset("person", entity: "Person"), Asset("scoped", "QA"), Asset("other")];
        var before = assets.Select(a => a with { }).ToList();

        var affected = TestDataLifecycle.Affected(assets, ["receiver/schemas/report.xsd"], ["Person"], ["QA"]);

        affected.Select(a => (a.Alias, a.State)).Should().BeEquivalentTo([("report", TestDataLifecycleState.ReviewRequired), ("person", TestDataLifecycleState.PotentiallyStale),
            ("scoped", TestDataLifecycleState.PotentiallyStale)]);
        affected.Should().OnlyContain(a => a.Reason.Contains("review", StringComparison.OrdinalIgnoreCase));
        assets.Should().Equal(before, "assets are never modified automatically");
    }

    [Fact]
    public void Usage_and_documentation_carry_alias_and_version_never_values()
    {
        var asset = Asset("report", "Test");
        TestDataLifecycle.Usage(asset).Should().Be(new TestDataUsage("report", asset.Fingerprint, "Test", "Configured"));
        var doc = TestDataLifecycle.Documentation([asset, Asset("negative", approval: TestDataApproval.Expired)]);
        doc.Should().Contain("`report`").And.Contain("version f00dfeedcafe").And.Contain("Deprecated").And.NotContain("SECRET-VALUE");
        TestDataLifecycle.State(asset with { Validation = TestDataValidation.Invalid }).Should().Be(TestDataLifecycleState.ReviewRequired);
        TestDataLifecycle.State(asset with { Validation = TestDataValidation.Invalid, Capabilities = ["negative-test"] }).Should().Be(TestDataLifecycleState.Approved, "an approved negative fixture is expected to be invalid");
    }

    [Fact]
    public async Task Candidate_service_reads_exactly_the_requested_snapshot_and_never_the_newest_instead()
    {
        var domains = new SourceEvidenceDomainsSnapshot { SourceSnapshotId = SnapshotId, Contracts = new ContractEvidence { Domain = SourceEvidenceDomain.Contracts, AnalyzerVersion = 2, Contracts = [Contract()] } };
        var older = new IqrSourceSnapshot { Id = SnapshotId, Archive = new("receiver.zip", "abc", 1), AnalyzedAt = DateTimeOffset.Parse("2026-10-01T08:00:00Z"), EvidenceDomains = domains, DatabaseArchitecture = Model() };
        var newer = new IqrSourceSnapshot { Id = Guid.NewGuid(), Archive = new("receiver.zip", "def", 1), AnalyzedAt = DateTimeOffset.Parse("2026-10-22T08:00:00Z") };
        var service = new TestDataCandidateService(new Sources(newer, older));

        var candidates = await service.CandidatesAsync("", SnapshotId, default);

        candidates.Should().NotBeNull();
        candidates!.Should().Contain(c => c.Origin == TestDataCandidateOrigin.ContractEvidence).And.Contain(c => c.Origin == TestDataCandidateOrigin.DataModelEvidence);
        candidates.Should().OnlyContain(c => c.State == TestDataLifecycleState.Candidate);
        (await service.CandidatesAsync("", Guid.NewGuid(), default)).Should().BeNull("an unknown snapshot is not replaced by the newest one");
        (await service.CandidatesAsync("", newer.Id, default)).Should().BeEmpty();
    }

    private sealed class Sources(params IqrSourceSnapshot[] snapshots) : IReviewSourceEvidenceProvider
    {
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>(snapshots);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(snapshots.FirstOrDefault(s => s.Id == snapshotId));
    }
}
