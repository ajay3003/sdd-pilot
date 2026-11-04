using BirkNext.AiCodeReview;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Api.Services.SourceAnalysis.CodeRisk;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.CanonicalInterpretation;

/// <summary>
/// Phase 2 completion (correlation/2): the general source-symbol table (code-risk v3), task → source-symbol references scoped by module (L/M),
/// contract version context (J: same bytes under v1 and v2 stay two versions sharing one content hash; I: same bytes in two packages stay one),
/// conflicting declared aliases surfaced instead of applied (K/rule conflicts), shared libraries with USES (M2LB.Common), and multi-capability
/// packages (Terraform-iac-meldingsutveksler: Infrastructure + Contracts).
/// </summary>
public sealed class CorrelationCompletionTests
{
    const string Snap = "11111111-1111-1111-1111-111111111111";
    const string Snap2 = "22222222-2222-2222-2222-222222222222";

    static CorrelationResult Run(CorrelationInput input) => CorrelationRules.Correlate(input);
    static CorrelationInput In() => CorrelationInput.Empty("proj");

    static ContractOccurrenceInput Xsd(string package, string path, string hash, string? declaredVersion = null, string snapshot = Snap) =>
        new("XmlSchema", package, package, path, snapshot, hash, "urn:buf:fravaer", declaredVersion, "Fravaersmelding");

    // ---- J / I: version context ----

    [Theory]
    [InlineData("contracts/v1/fravaer.xsd", null, "v1")]
    [InlineData("contracts/V2/fravaer.xsd", null, "v2")]
    [InlineData("schemas/Api.V1.Fravaer.xsd", null, "v1")]
    [InlineData("schemas/fravaer_v2_1.xsd", null, "v2.1")]
    [InlineData("schemas/fravaer.xsd", "2.0", "v2.0")]
    [InlineData("schemas/fravaer.xsd", "v3", "v3")]
    [InlineData("schemas/fravaer.xsd", null, null)]
    [InlineData("services/dev2/fravaer.xsd", null, null)]
    [InlineData("schemas/review/fravaer.xsd", null, null)]
    public void Version_context_comes_from_the_declared_version_or_a_version_path_segment_only(string path, string? declared, string? expected) =>
        CorrelationRules.VersionContext(Xsd("M2LB", path, "aaa", declared)).Should().Be(expected);

    [Fact]
    public void J_same_bytes_under_v1_and_v2_stay_two_versions_that_share_one_content_hash()
    {
        var result = Run(In() with { Contracts = [Xsd("M2LB", "contracts/v1/fravaer.xsd", "AAA"), Xsd("M2LB", "contracts/v2/fravaer.xsd", "aaa")] });
        var versions = result.Entities.Where(e => e.Type == CanonicalEntityType.ContractVersion).ToList();
        versions.Select(v => v.IdentityKey).Should().BeEquivalentTo(["xmlschema:sha256:aaa@v1", "xmlschema:sha256:aaa@v2"]);
        versions.Select(v => v.Attribute("contentHash")).Distinct().Should().Equal("aaa");
        versions.Should().OnlyContain(v => v.Attribute("versionContextSource") == "path");
        result.Entities.Should().ContainSingle(e => e.Type == CanonicalEntityType.Contract, "both versions belong to one logical contract");
        result.Relations.Where(r => r.Type == CanonicalRelationType.OccurrenceOf).Should().HaveCount(2);
        result.Relations.Should().NotContain(r => r.Type == CanonicalRelationType.SameAs || r.Type == CanonicalRelationType.CandidateSameAs || r.Type == CanonicalRelationType.Supersedes,
            "identical bytes are not a version conflict and never imply compatibility or succession");
    }

    [Fact]
    public void I_same_bytes_in_three_packages_without_version_context_are_one_version_with_three_occurrences()
    {
        var result = Run(In() with { Contracts = [Xsd("M2LB", "schemas/fravaer.xsd", "aaa"), Xsd("Terraform-iac-meldingsutveksler", "xsd/fravaer.xsd", "aaa", null, Snap2),
            Xsd("MU", "Contracts/fravaer.xsd", "aaa", null, Snap2)] });
        result.Entities.Should().ContainSingle(e => e.Type == CanonicalEntityType.ContractVersion).Which.IdentityKey.Should().Be("xmlschema:sha256:aaa");
        result.Entities.Count(e => e.Type == CanonicalEntityType.ContractOccurrence).Should().Be(3);
        result.Entities.Count(e => e.Type == CanonicalEntityType.Contract).Should().Be(1, "logical contract counts are not inflated by occurrences");
    }

    [Fact]
    public void J_the_same_declared_version_with_different_content_is_still_a_reviewable_conflict()
    {
        var result = Run(In() with { Contracts = [Xsd("M2LB", "a/fravaer.xsd", "aaa", "1.0"), Xsd("MU", "b/fravaer.xsd", "bbb", "1.0", Snap2)] });
        result.Relations.Should().ContainSingle(r => r.RuleId == CorrelationRules.ContractVersionConflictRule).Which.Should().Match<CanonicalRelationFact>(r =>
            r.Resolution == RelationResolution.Ambiguous && r.SourceKey.EndsWith("bbb@v1.0") && r.TargetKey.EndsWith("aaa@v1.0"));
    }

    // ---- K: conflicting declarations ----

    [Fact]
    public void An_alias_declared_for_two_different_systems_is_not_applied_and_is_surfaced_for_review()
    {
        var result = Run(In() with
        {
            Systems = [new(CanonicalEntityType.System, "Skoletjenesten", ["Skole"], "domain profile skolenaervaer"),
                       new(CanonicalEntityType.System, "SkoleAdapter", ["Skole"], "test pack expected aliases"),
                       new(CanonicalEntityType.System, "MU", ["Meldingsutveksleren"], "domain profile skolenaervaer")],
        });
        result.Aliases.Where(a => a.Alias == "Skole").Should().HaveCount(2).And.OnlyContain(a => a.State == AliasState.Proposed && a.RuleId == CorrelationRules.AliasConflictRule);
        result.Aliases.Should().ContainSingle(a => a.Alias == "Meldingsutveksleren").Which.State.Should().Be(AliasState.Explicit, "an uncontested declaration still applies");
        result.Relations.Should().ContainSingle(r => r.RuleId == CorrelationRules.AliasConflictRule).Which.Should().Match<CanonicalRelationFact>(r =>
            r.Type == CanonicalRelationType.CandidateSameAs && r.Resolution == RelationResolution.Ambiguous && r.Evidence.Count == 2);
        result.Limitations.Should().Contain(l => l.Contains("\"Skole\"") && l.Contains("not applied"));
    }

    [Fact]
    public void Real_Skolenaervaer_declarations_merge_a_pack_name_the_profile_declares_and_keep_a_genuine_disagreement_for_review()
    {
        var systems = CanonicalEvidenceAdapter.DeclaredSystems([new Api.Services.IntegrationJourneys.Packs.SkoleNaervaer.SkoleNaervaerTopologyProfile()],
            Api.Services.IntegrationJourneys.Packs.SkoleNaervaer.SkoleNaervaerJourneyPack.ExpectedAliases);
        var result = Run(In() with { Systems = systems });

        // "Skolenærværsrapport" is declared by the profile as a name of Skoletjenesten, so the pack's aliases are Skoletjenesten's: no second system, no conflict.
        result.Entities.Should().NotContain(e => e.Type == CanonicalEntityType.System && e.IdentityKey == "skolenærværsrapport");
        result.Aliases.Where(a => a.Alias == "M2LB.SkoleService.Api").Should().OnlyContain(a => a.State == AliasState.Explicit && a.EntityKey == "System|skoletjenesten");
        // "Utdanning" and SkoleAdapter both claim M2LB.SkoleAdapter.Worker and nothing declares them to be one system: a reviewer decides.
        result.Aliases.Where(a => a.Alias == "M2LB.SkoleAdapter.Worker").Should().OnlyContain(a => a.State == AliasState.Proposed && a.RuleId == CorrelationRules.AliasConflictRule);
        result.Aliases.Should().Contain(a => a.Alias == "Meldingsutveksleren" && a.EntityKey == "System|mu" && a.State == AliasState.Explicit);
    }

    [Fact]
    public void K_MU_stays_one_explicit_system_alias_from_the_declared_profile()
    {
        var result = Run(In() with { Systems = [new(CanonicalEntityType.System, "MU", ["Meldingsutveksleren", "Buf.Meldingsutveksler.Service"], "domain profile skolenaervaer")] });
        result.Entities.Should().ContainSingle(e => e.Type == CanonicalEntityType.System);
        result.Aliases.Should().OnlyContain(a => a.State == AliasState.Explicit && a.EntityKey == "System|mu");
    }

    // ---- L / M: symbols and tasks ----

    static SourceSymbolInput Type(string ns, string name, string service, string package = "m2lb") =>
        new(package, $"src/{service}/{name}.cs", name, ns, "Class", null, null, [], [], service, Snap);

    static readonly SourceSymbolInput[] Symbols =
    [
        Type("M2LB.SkoleService.Api.Rapport", "FravaersRapportHandler", "M2LB.SkoleService.Api"),
        Type("M2LB.Person.Api.Rapport", "FravaersRapportHandler", "M2LB.Person.Api"),
        Type("M2LB.SkoleService.Api.Validation", "RapportValidator", "M2LB.SkoleService.Api"),
        Type("M2LB.Person.Api.Sync", "PersonResync", "M2LB.Person.Api"),
    ];

    static TaskReferenceInput TaskRef(string module, string text, string key = "T010") => new(module, "001-rapport", key, text, "delivery:x");

    [Fact]
    public void L_the_same_type_name_in_two_modules_stays_two_symbols()
    {
        var result = Run(In() with { Symbols = Symbols });
        result.Entities.Where(e => e.Type == CanonicalEntityType.SourceSymbol && e.DisplayName.EndsWith("FravaersRapportHandler")).Select(e => e.IdentityKey)
            .Should().BeEquivalentTo(["m2lb|M2LB.SkoleService.Api.Rapport.FravaersRapportHandler", "m2lb|M2LB.Person.Api.Rapport.FravaersRapportHandler"]);
    }

    [Fact]
    public void M_a_task_naming_a_class_references_the_one_type_with_that_name_in_its_module()
    {
        var result = Run(In() with
        {
            Symbols = Symbols,
            WorkItems = [new("SkoleService", "001-rapport", "Fraværsrapport", [], null, "delivery:x")],
            Tasks = [TaskRef("SkoleService", "Implement `FravaersRapportHandler` and wire RapportValidator.cs into the pipeline")],
        });
        var refs = result.Relations.Where(r => r.RuleId == CorrelationRules.TaskSymbolRule).ToList();
        refs.Should().HaveCount(2).And.OnlyContain(r => r.Resolution == RelationResolution.Explicit && r.Type == CanonicalRelationType.References
            && r.SourceKey == "Task|skoleservice/001-rapport/t010" && r.TargetKey.Contains("M2LB.SkoleService.Api"));
        refs.Single(r => r.TargetKey.EndsWith("FravaersRapportHandler")).Evidence.Should().ContainSingle(e => e.Kind == "task-text" && e.Detail == "FravaersRapportHandler");
        result.Relations.Should().Contain(r => r.RuleId == CorrelationRules.TaskOwnerRule && r.SourceKey == "Feature|skoleservice/001-rapport");
    }

    [Fact]
    public void M_a_name_found_only_in_another_module_is_a_candidate_and_an_unknown_name_links_nothing()
    {
        var result = Run(In() with { Symbols = Symbols, Tasks = [TaskRef("SkoleService", "Call PersonResync after the report is stored; update SomethingUnknown")] });
        result.Relations.Should().ContainSingle(r => r.RuleId == CorrelationRules.TaskSymbolRule).Which.Should().Match<CanonicalRelationFact>(r =>
            r.Resolution == RelationResolution.Candidate && r.TargetKey.EndsWith("PersonResync"));
    }

    [Fact]
    public void M_two_same_named_types_inside_the_task_module_are_ambiguous_never_picked()
    {
        SourceSymbolInput[] symbols = [Type("M2LB.SkoleService.Api.A", "RapportMapper", "M2LB.SkoleService.Api"), Type("M2LB.SkoleService.Api.B", "RapportMapper", "M2LB.SkoleService.Api")];
        var result = Run(In() with { Symbols = symbols, Tasks = [TaskRef("SkoleService", "Refactor `RapportMapper`")] });
        result.Relations.Where(r => r.RuleId == CorrelationRules.TaskSymbolRule).Should().HaveCount(2).And.OnlyContain(r => r.Resolution == RelationResolution.Ambiguous);
    }

    [Fact]
    public void A_task_naming_no_known_symbol_creates_no_task_entity()
    {
        var result = Run(In() with { Symbols = Symbols, Tasks = [TaskRef("SkoleService", "Write the README and update docs")] });
        result.Entities.Should().NotContain(e => e.Type == CanonicalEntityType.Task);
    }

    [Theory]
    [InlineData("Implement `FravaersRapportHandler.Handle()` now", "FravaersRapportHandler")]
    [InlineData("Edit `M2LB.SkoleService.Api.RapportValidator`", "RapportValidator")]
    [InlineData("Update RapportValidator.cs", "RapportValidator")]
    [InlineData("Add PersonResync worker", "PersonResync")]
    public void Task_text_names_symbols_by_backticks_file_names_and_PascalCase(string text, string expected) =>
        CorrelationRules.NamedSymbols(text).Should().Contain(expected);

    [Fact]
    public void An_endpoint_method_belongs_to_its_declared_type()
    {
        var result = Run(In() with
        {
            Symbols = [Type("M2LB.SkoleService.Api.Controllers", "RapportController", "M2LB.SkoleService.Api"),
                       new("m2lb", "src/RapportController.cs", "M2LB.SkoleService.Api.Controllers.RapportController.Post", null, "Controller", "POST", "/api/rapport", [], [], "M2LB.SkoleService.Api", Snap)],
        });
        result.Relations.Should().ContainSingle(r => r.RuleId == CorrelationRules.SymbolMemberRule).Which.Should().Match<CanonicalRelationFact>(r =>
            r.Type == CanonicalRelationType.Contains && r.SourceKey.EndsWith("Controllers.RapportController") && r.TargetKey.EndsWith("RapportController.Post"));
    }

    // ---- shared libraries ----

    [Fact]
    public void A_service_uses_a_library_another_package_publishes_and_third_party_packages_are_not_capabilities()
    {
        var common = new CanonicalEntityFact(CanonicalEntityType.Package, "m2lb.common", "M2LB.Common", "M2LB.Common", "repository", []);
        var result = Run(In() with
        {
            AdditionalEntities = [common],
            Libraries = [new("m2lb.common", "M2LB.Common.Health", "src/M2LB.Common.Health/M2LB.Common.Health.csproj", Snap2),
                         new("m2lb.common", "M2LB.Common.Logging", "src/M2LB.Common.Logging/M2LB.Common.Logging.csproj", Snap2)],
            LibraryUses = [new("M2LB.SkoleService.Api", "M2LB.Common.Health", "package reference in src/SkoleService.csproj", Snap),
                           new("M2LB.SkoleService.Api", "Serilog", "package reference in src/SkoleService.csproj", Snap)],
        });
        result.Entities.Where(e => e.Type == CanonicalEntityType.Library).Select(e => e.Attribute("capability")).Should().BeEquivalentTo(["Health", "Logging"]);
        result.Relations.Where(r => r.RuleId == CorrelationRules.LibraryRule).Should().HaveCount(2).And.OnlyContain(r => r.SourceKey == "Package|m2lb.common");
        result.Relations.Should().ContainSingle(r => r.Type == CanonicalRelationType.Uses).Which.Should().Match<CanonicalRelationFact>(r =>
            r.SourceKey == "Service|m2lb.skoleservice.api" && r.TargetKey == "Library|m2lb.common.health" && r.Resolution == RelationResolution.Explicit);
        result.Limitations.Should().NotContain(l => l.Contains("dropped"), "an unprovided third-party package is not an error");
    }

    // ---- packages: several capabilities ----

    [Fact]
    public void A_package_with_IaC_and_contracts_provides_both_capabilities()
    {
        var snapshot = new IqrSourceSnapshot
        {
            Archive = new("Terraform-iac-meldingsutveksler.zip", "abc", 1),
            EvidenceDomains = new BirkNext.SourceDomains.SourceEvidenceDomainsSnapshot
            {
                FileRoles = [new(BirkNext.SourceDomains.SourceFileRole.InfrastructureAsCode, 14, []), new(BirkNext.SourceDomains.SourceFileRole.Contract, 3, [])],
            },
        };
        var capabilities = CanonicalEvidenceAdapter.PackageCapabilities(snapshot).Select(c => c.Capability).ToList();
        capabilities.Should().Equal("Infrastructure", "Contracts");
        var input = CanonicalEvidenceAdapter.FromSourceSnapshot("proj", snapshot);
        input.AdditionalEntities!.Single(e => e.Type == CanonicalEntityType.Package).Attribute("capabilities").Should().Be("Infrastructure,Contracts");
    }

    // ---- code-risk v3: the symbol table ----

    [Fact]
    public void Code_risk_records_declared_production_types_and_never_test_types()
    {
        var files = new[]
        {
            new SourceFile("src/Api/Api.csproj", "<Project />"),
            new SourceFile("src/Api/Rapport.cs", """
                namespace M2LB.SkoleService.Api.Rapport;
                public sealed class FravaersRapportHandler { private sealed record Result(int Id); }
                public interface IRapportStore { }
                public enum RapportStatus { Ny, Lagret }
                """),
            new SourceFile("tests/Api.Tests/Api.Tests.csproj", "<Project />"),
            new SourceFile("tests/Api.Tests/HandlerTests.cs", "namespace T; public class HandlerTests { }"),
        };
        var evidence = CodeRiskSourceAnalyzer.Analyze(Guid.NewGuid(), files, files.Select(f => f.Path).ToList(), ["tests/Api.Tests"]);
        evidence.AnalyzerVersion.Should().Be(3);
        evidence.Symbols.Select(s => (s.Name, s.Namespace, s.Kind)).Should().BeEquivalentTo(new[]
        {
            ("FravaersRapportHandler", "M2LB.SkoleService.Api.Rapport", "Class"), ("FravaersRapportHandler.Result", "M2LB.SkoleService.Api.Rapport", "Record"),
            ("IRapportStore", "M2LB.SkoleService.Api.Rapport", "Interface"), ("RapportStatus", "M2LB.SkoleService.Api.Rapport", "Enum"),
        });
        evidence.Symbols.Should().OnlyContain(s => s.File == "src/Api/Rapport.cs" && s.Line > 0);
    }

    // ---- summary-first counts ----

    sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    [Fact]
    public async Task The_summary_counts_the_latest_interpretation_only_and_names_contract_counts_separately()
    {
        await using var db = new Api.Data.CanonicalInterpretation.CanonicalInterpretationDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Api.Data.CanonicalInterpretation.CanonicalInterpretationDbContext>()
                .UseInMemoryDatabase($"summary-{Guid.NewGuid():N}").Options);
        var service = new CanonicalInterpretationService(db, new NoSources(), TimeProvider.System);
        (await service.SummaryAsync("proj", default)).Limitations.Should().ContainSingle(l => l.Contains("No canonical interpretation"));

        var terraform = new CanonicalEntityFact(CanonicalEntityType.Package, "terraform-iac-meldingsutveksler", "Terraform-iac-meldingsutveksler", "x", "repository", [],
            Attributes: new Dictionary<string, string> { ["capabilities"] = "Infrastructure,Contracts" });
        var common = new CanonicalEntityFact(CanonicalEntityType.Package, "m2lb.common", "M2LB.Common", "y", "repository", [],
            Attributes: new Dictionary<string, string> { ["capabilities"] = "SharedSource,Tests" });
        var older = Run(In() with { AdditionalEntities = [terraform], Contracts = [Xsd("M2LB", "x/old.xsd", "zzz")] });
        await service.PersistAsync("proj", older, [], [], "first", default);
        var latest = Run(In() with
        {
            AdditionalEntities = [terraform, common],
            Contracts = [Xsd("M2LB", "contracts/v1/fravaer.xsd", "aaa"), Xsd("M2LB", "contracts/v2/fravaer.xsd", "aaa"),
                         Xsd("Terraform-iac-meldingsutveksler", "xsd/v1/fravaer.xsd", "aaa", null, Snap2)],
            Libraries = [new("m2lb.common", "M2LB.Common.Health", "src/M2LB.Common.Health.csproj", Snap2)],
            Symbols = Symbols, Tasks = [TaskRef("SkoleService", "Implement `FravaersRapportHandler`")],
        });
        await service.PersistAsync("proj", latest, [], [], "second", default);

        var summary = await service.SummaryAsync("proj", default);
        summary.Contracts.Should().Be(new CanonicalContractCounts(LogicalContracts: 1, Versions: 2, DistinctContents: 1, Occurrences: 3, VersionsWithoutLogicalIdentity: 0),
            "the old interpretation's zzz version is not part of the latest membership");
        summary.RuleSetVersions.Should().Contain(CorrelationRules.RuleSetVersion);
        summary.Packages.Single(p => p.Name == "Terraform-iac-meldingsutveksler").Capabilities.Should().Equal("Infrastructure", "Contracts");
        summary.Packages.Single(p => p.Name == "M2LB.Common").Libraries.Should().Equal("M2LB.Common.Health");
        summary.EntitiesByType["SourceSymbol"].Should().Be(4);
        summary.RelationsByType["OccurrenceOf"].Should().Be(3);
        summary.EstablishedRelations.Should().BePositive();
    }

    [Fact]
    public void Correlation_two_is_deterministic_for_tasks_libraries_and_contracts_in_any_order()
    {
        var input = In() with
        {
            Symbols = Symbols, Tasks = [TaskRef("SkoleService", "Implement `FravaersRapportHandler`"), TaskRef("Person", "PersonResync", "T020")],
            Contracts = [Xsd("M2LB", "contracts/v1/a.xsd", "aaa"), Xsd("M2LB", "contracts/v2/a.xsd", "aaa")],
            Libraries = [new("m2lb.common", "M2LB.Common.Health", "x", Snap2)], LibraryUses = [new("Svc", "M2LB.Common.Health", "y", Snap)],
        };
        var a = Run(input);
        var b = Run(input with { Symbols = [.. Enumerable.Reverse(Symbols)], Tasks = [.. Enumerable.Reverse(input.Tasks!)], Contracts = [.. Enumerable.Reverse(input.Contracts)] });
        a.Relations.Select(r => (r.Key, r.Resolution, r.EvidenceFingerprint)).Should().Equal(b.Relations.Select(r => (r.Key, r.Resolution, r.EvidenceFingerprint)));
        a.Entities.Select(e => e.Key).Should().Equal(b.Entities.Select(e => e.Key));
    }
}
