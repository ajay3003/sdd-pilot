using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// «Utgått scenario» only after a complete comparison proves the scenario is gone (2026-11-24, M2LB SkoleService): a plan prepared from
/// specs/001-skoleservice/spec.md showed both cases as removed because the latest discovery was made with another specification selected
/// (docs/avhengigheter-bestilling.md) and therefore never read spec.md. Not having read the file proves nothing: «Kilde mangler».
/// </summary>
public sealed class SourceRemovalRulesTests
{
    private const string Main = "SkoleService/specs/001-skoleservice/spec.md";
    private const string Dependencies = "SkoleService/docs/avhengigheter-bestilling.md";

    private static CoverageUserStory Story(string path) => new() { Key = path + "#US1", StoryId = "US1", Ordinal = 1, Title = "Report", Priority = "P1", SpecificationPath = path };

    private static TestCaseDraft Case(string? specificationPath, string? evidence = null) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "import:a", ScenarioId = "tsc-1", Fingerprint = "fp" },
        Generated = new TestCaseContent
        {
            Capability = "specification", SourceIdentity = "tss-valid-report", Story = specificationPath is null ? null : Story(specificationPath),
            Evidence = evidence is null ? [] : [new CandidateEvidenceRef("specification", evidence)],
        },
    };

    private static DiscoveredScenario Scenario(string id, string path) => new()
    {
        CandidateId = id, ProjectKey = "import:a", CapabilityId = "specification", SourceCategory = "Specification", Title = id, SourceIdentity = "tss-" + id,
        Story = Story(path), Evidence = [new CandidateEvidenceRef("specification", path + "#" + id)],
    };

    private static ScenarioDiscoveryResult Discovery(params DiscoveredScenario[] candidates) => new()
    {
        DiscoveryId = "d2", ProjectKey = "import:a", Candidates = [.. candidates],
        Capabilities = [new DiscoveryCapability { Id = "specification", Label = "Spesifikasjon", State = DiscoveryCapabilityState.Available }],
        Basis = new DiscoverySourceBasis
        {
            ApplicationKey = "m2lb", Kind = DiscoveryBasisKind.Current, SpecificationOrigin = SpecificationOrigin.ImportedSnapshot, SpecificationReference = "import:a#r1",
            Members = [new DiscoverySourceMember { RepositoryKey = "m2lb", DisplayName = "M2LB", SnapshotId = Guid.NewGuid(), ArchiveName = "M2LB.zip", StateCode = "SINGLE_SNAPSHOT" }],
        },
    };

    [Fact]
    public void A_discovery_made_with_another_specification_is_not_a_removal()
    {
        var current = Discovery(Scenario("dep-1", Dependencies), Scenario("dep-2", Dependencies));

        var (state, reason) = TestPlanSuggestionService.Absence(Case(Main), current, null);

        state.Should().Be(CaseSourceState.NotCompared, "another specification selected is not a missing source (2026-11-25)");
        reason.Should().StartWith("Ikke sammenlignet").And.Contain(Main).And.NotContain("Kilde mangler");
    }

    [Fact]
    public void A_scenario_gone_from_the_same_specification_that_was_read_is_removed()
    {
        var current = Discovery(Scenario("other-scenario", Main));

        TestPlanSuggestionService.Absence(Case(Main), current, null).State.Should().Be(CaseSourceState.Removed);
    }

    [Fact]
    public void The_specification_path_is_also_taken_from_the_evidence_reference_and_compared_without_case_or_slash_differences()
    {
        var current = Discovery(Scenario("other-scenario", Main));

        TestPlanSuggestionService.Absence(Case(null, "skoleservice\\SPECS\\001-skoleservice\\spec.md#scenario-1"), current, null).State
            .Should().Be(CaseSourceState.Removed, "the same file, written differently");
    }

    [Fact]
    public void Without_a_recorded_specification_removal_cannot_be_decided()
    {
        var current = Discovery(Scenario("other-scenario", Main));

        var (state, reason) = TestPlanSuggestionService.Absence(Case(null), current, null);

        state.Should().Be(CaseSourceState.SourceUnavailable);
        reason.Should().Contain("ikke registrert hvilken spesifikasjon");
    }
}
