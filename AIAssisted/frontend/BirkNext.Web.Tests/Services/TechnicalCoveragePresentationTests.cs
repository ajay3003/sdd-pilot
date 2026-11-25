using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using FluentAssertions;
using TC = BirkNext.Web.Services.TechnicalCoveragePresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// «Hva er allerede kontrollert?» (2026-11-25). Link, extent, currency and result are separate facts: a test naming the scenario is «funnet», never
/// «kontrollert»; only a reviewer's assessment of a confirmed link says how much it checks; a changed test or scenario needs a new look; and Manual
/// Testing never shows a test as run or passed.
/// </summary>
public sealed class TechnicalCoveragePresentationTests
{
    private static CandidateTestAssociation T(string id, bool confirmed = true, TestScenarioLinkOrigin origin = TestScenarioLinkOrigin.Scenario, string fp = "fp1") =>
        new(id, id, "SkoleService/tests/X.cs:10", "The method name names scenario 9", confirmed)
        { Origin = origin, Layer = "Integration", ClassName = "S.CorrectionTests", MethodName = "Scenarios_2_and_8_and_9_two_reporters_are_flagged", SourceFingerprint = fp };

    private static DiscoveredScenario C(params CandidateTestAssociation[] tests) => new()
    {
        CandidateId = "s9", ProjectKey = "p", Title = "s9", Fingerprint = "sc1", ExistingTests = [.. tests],
        Story = new CoverageUserStory { Key = "k", StoryId = "US5" }, NumberInStory = 9,
    };

    private static Dictionary<string, TC.Decision> D(params (string Test, TestLinkReview Review, TechnicalCoverageExtent Extent, string Fp, string Sc)[] ds) =>
        ds.ToDictionary(d => StoryGroupingEngine.TestLinkKey("s9", d.Test), d => new TC.Decision(d.Review, d.Extent, d.Fp, d.Sc, "anna", DateTimeOffset.UtcNow));

    [Fact]
    public void C26_C44_no_test_is_honest_and_an_older_import_is_not_assessed_rather_than_none()
    {
        TC.Of(C(), D(), testsRead: true).Should().Match<TC.Coverage>(c => c.State == TC.State.NoKnownTest && c.Label == "Ingen kjent automatisk test");
        TC.Of(C(), D(), testsRead: false).Label.Should().Be("Automatiske tester ikke vurdert");
        TC.ListTag(TC.State.NoKnownTest).Should().BeNull("no tag for nothing");
    }

    [Fact]
    public void C27_C28_C30_a_candidate_is_possible_a_named_test_is_found_and_neither_is_full_coverage()
    {
        TC.Of(C(T("t1", confirmed: false)), D(), true).Should().Match<TC.Coverage>(c => c.State == TC.State.Candidate && c.Label == "Mulig automatisk test");
        var found = TC.Of(C(T("t1")), D(), true);
        found.State.Should().Be(TC.State.Linked);
        found.Label.Should().Be("Automatisk test funnet");
        found.Tests.Single().Extent.Should().Be(TechnicalCoverageExtent.NotAssessed, "a link never implies coverage");
        TC.ExtentLabel(found.Tests.Single().Extent).Should().Be("Dekning ikke vurdert");
    }

    [Fact]
    public void C29_C37_C38_partial_and_full_come_only_from_a_reviewer_on_a_confirmed_link()
    {
        TC.Of(C(T("t1")), D(("t1", TestLinkReview.Confirmed, TechnicalCoverageExtent.Partial, "fp1", "sc1")), true).Label.Should().Be("Delvis kontrollert");
        TC.Of(C(T("t1")), D(("t1", TestLinkReview.Confirmed, TechnicalCoverageExtent.Full, "fp1", "sc1")), true).Label.Should().Be("Kontrollert av automatisk test (vurdert)");
        TC.Of(C(T("t1")), D(("t1", TestLinkReview.Confirmed, TechnicalCoverageExtent.NotAssessed, "fp1", "sc1")), true).State.Should().Be(TC.State.Linked);
    }

    [Fact]
    public void C31_execution_is_never_shown_as_passed()
    {
        TC.NoResult.Should().Be("Testresultat ikke tilgjengelig");
        foreach (var s in Enum.GetValues<TC.State>()) TC.StateLabel(s).Should().NotContainAny("bestått", "Bestått", "kjørt", "passed");
    }

    [Fact]
    public void C36_C43_a_rejection_stands_until_the_test_or_the_scenario_changes_and_then_it_must_be_reviewed_again()
    {
        var rejected = TC.Of(C(T("t1", confirmed: false)), D(("t1", TestLinkReview.Rejected, TechnicalCoverageExtent.NotAssessed, "fp1", "sc1")), true);
        rejected.State.Should().Be(TC.State.NoKnownTest, "a rejected candidate is not offered again");
        rejected.Tests.Single().Link.Should().Be(TC.Link.Rejected, "the decision stays visible");

        var testChanged = TC.Of(C(T("t1", fp: "fp2")), D(("t1", TestLinkReview.Confirmed, TechnicalCoverageExtent.Full, "fp1", "sc1")), true);
        testChanged.State.Should().Be(TC.State.Review);
        testChanged.Label.Should().Be("Kontroller koblingen");
        testChanged.Tests.Single().ReviewReason.Should().Be("Testen er endret etter vurderingen");
        testChanged.Tests.Single().Extent.Should().Be(TechnicalCoverageExtent.NotAssessed, "a stale «hele scenarioet» is never claimed");

        TC.Of(C(T("t1")), D(("t1", TestLinkReview.Rejected, TechnicalCoverageExtent.NotAssessed, "fp1", "old")), true).Tests.Single().ReviewReason.Should().Be("Scenarioet er endret etter vurderingen");
    }

    [Fact]
    public void Shared_requirement_tests_are_supporting_evidence_never_a_link_and_removed_reviewed_tests_are_counted()
    {
        var shared = TC.Of(C(T("t-fr", origin: TestScenarioLinkOrigin.SharedRequirement)), D(), true);
        shared.State.Should().Be(TC.State.SameRequirementOnly);
        TC.LinkLabel(shared.Tests.Single().Link).Should().Be("Samme krav, ikke koblet til scenarioet");
        TC.Matches(shared.State, "none").Should().BeTrue("for prioritising, a test of the same requirement is not a known test of the scenario");

        TC.Of(C(T("t1")), D(("gone", TestLinkReview.Confirmed, TechnicalCoverageExtent.Full, "x", "sc1")), true).ReviewedButGone.Should().Be(1);
    }

    [Fact]
    public void C32_C33_C34_details_carry_path_class_method_and_basis_in_readable_form()
    {
        var row = TC.Of(C(T("t1")), D(), true).Tests.Single();
        row.Layer.Should().Be("Integrasjonstest");
        row.ReadableName.Should().Be("Scenarios 2 and 8 and 9 two reporters are flagged");
        row.Test.File.Should().Be("SkoleService/tests/X.cs:10");
        row.Test.ClassName.Should().Be("S.CorrectionTests");
        row.Test.Basis.Should().Contain("scenario 9");
    }

    [Fact]
    public void C39_filters_tell_candidate_linked_assessed_and_review_apart()
    {
        TC.Matches(TC.State.Candidate, "candidate").Should().BeTrue();
        TC.Matches(TC.State.Linked, "candidate").Should().BeFalse();
        TC.Matches(TC.State.Partial, "linked").Should().BeFalse();
        TC.Matches(TC.State.Full, "full").Should().BeTrue();
        TC.Matches(TC.State.Review, "review").Should().BeTrue();
        TC.Matches(TC.State.NotAssessed, "none").Should().BeFalse("not assessed is not «none»");
    }
}
