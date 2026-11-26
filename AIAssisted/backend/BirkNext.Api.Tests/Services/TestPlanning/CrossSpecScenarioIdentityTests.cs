using BirkNext.Api.Services.Specifications;
using BirkNext.Api.Services.TestPlanning;
using FluentAssertions;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>Cross-page context hardening (2026-11-26): US1/AC1 of two specifications — also in the SAME module — are two scenarios with two
/// identities, so «Forbered testtilfelle» for one can never resolve to the other.</summary>
public sealed class CrossSpecScenarioIdentityTests
{
    private static string Spec(string title, string outcome) => $"""
        # Feature Specification: {title}

        ## User Scenarios & Testing

        ### User Story 1 - {title} (Priority: P1)

        **Acceptance Scenarios**:

        1. **Given** a saksbehandler, **When** the report is opened, **Then** {outcome}.
        """;

    [Theory]
    [InlineData("SkoleService", "SkoleService")]
    [InlineData("SkoleService", "TjenesteService")]
    public void US1_AC1_of_two_specifications_are_two_candidates_with_distinct_ids_and_source_identities(string moduleA, string moduleB)
    {
        var a = SpecificationEvidenceBuilder.Build($"{moduleA}/specs/001-rapport/spec.md", Spec("Rapport", "the report is shown"), moduleA);
        var b = SpecificationEvidenceBuilder.Build($"{moduleB}/specs/002-fravaer/spec.md", Spec("Fravær", "the absence is shown"), moduleB);
        // Discovery finalises the candidates of every specification together (where same-key candidates used to be merged into one).
        var all = D.Finalize("m2lb", "s1", [.. D.FromSpecification("m2lb", a).Candidates, .. D.FromSpecification("m2lb", b).Candidates], []);
        var ca = all.Single(c => c.CanonicalKey.StartsWith("scenario:") && c.Title.Contains("opened") && c.ExpectedBehaviour!.Contains("report is shown"));
        var cb = all.Single(c => c.CanonicalKey.StartsWith("scenario:") && c.ExpectedBehaviour!.Contains("absence is shown"));
        all.Count(c => c.CanonicalKey.StartsWith("scenario:")).Should().Be(2, "never merged into one candidate");
        (ca.Story!.StoryId, cb.Story!.StoryId, ca.NumberInStory, cb.NumberInStory).Should().Be(("US1", "US1", 1, 1), "identically numbered US1/AC1");
        ca.CandidateId.Should().NotBe(cb.CandidateId, "the candidate id is what «Forbered testtilfelle» carries");
        ca.SourceIdentity.Should().NotBe(cb.SourceIdentity);
        ca.CanonicalKey.Should().NotBe(cb.CanonicalKey);
        ca.Story.Key.Should().NotBe(cb.Story.Key, "stories are specification-scoped");
    }
}

public sealed class CrossSpecScenarioIdentityStabilityTests
{
    [Fact]
    public void A_key_claimed_by_one_specification_keeps_its_existing_candidate_id()
    {
        var spec = """
            # F
            ### User Story 1 - Only one (Priority: P1)
            1. **Given** x, **When** y, **Then** z.
            """;
        var c = D.FromSpecification("m2lb", SpecificationEvidenceBuilder.Build("Solo/specs/001-x/spec.md", spec, "Solo")).Candidates.Single(x => x.CanonicalKey.StartsWith("scenario:"));
        var finalized = D.Finalize("m2lb", "s1", [c], []).Single();
        (finalized.CandidateId, finalized.CanonicalKey).Should().Be((c.CandidateId, c.CanonicalKey), "existing plans keep their links");
    }
}
