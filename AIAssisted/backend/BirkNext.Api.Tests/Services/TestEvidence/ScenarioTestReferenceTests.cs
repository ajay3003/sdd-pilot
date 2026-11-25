using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.TestEvidence;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Api.Tests.Services.Integrations;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.TestCoverage;
using BirkNext.TestEvidence;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;
using F = BirkNext.Api.Tests.Services.TestEvidence.TestEvidenceFixtures;

namespace BirkNext.Api.Tests.Services.TestEvidence;

/// <summary>
/// Technical test coverage for Manual Testing (2026-11-25): which source tests name an acceptance scenario, how strongly, and in which scope.
/// The convention is the one real Spec Kit suites use («US5 scenarios 1–9» on the class, <c>Scenario_4_…</c>/<c>Scenarios_2_and_8_and_9_…</c> on the
/// method, or a trait/comment naming «US5 scenario 9» / «US5-AS9»). A test naming a scenario is not "covered", not "ran" and not "passed".
/// </summary>
public sealed class ScenarioTestReferenceTests
{
    private const string Project = "SkoleService/tests/S.Integration/S.Integration.csproj";

    private static SourceTestInventory Discover(params (string Path, string Content)[] files)
    {
        var (workspace, error) = IqrSourceArchiveReader.Read("s.zip", IqrSourceEvidenceTests.Zip([(Project, F.IntegrationProject), .. files]));
        error.Should().BeNull();
        return DotNetXunitTestDiscoveryProvider.Discover(Guid.NewGuid(), "fp", "M2LB", workspace!);
    }

    private static IEnumerable<(string, int, TestEvidenceConfidence)> Refs(SourceTestInventory inventory, string method) =>
        inventory.Definitions.Single(d => d.MethodName == method).Scenarios.Select(s => (s.StoryId, s.Number, s.Confidence));

    private const string Chain = """
        using Xunit;
        namespace S.Integration;
        /// <summary>US5 scenarios 1–9 (FR-019..FR-022, FR-006): corrections, chains and more than one reporter.</summary>
        public class CorrectionTests
        {
            [Fact] public void Scenario_4_a_withdrawn_report_is_not_delivered() { }
            [Fact] public void Scenarios_2_and_8_and_9_two_reporters_are_both_flagged() { }
            [Fact] public void A_withdrawal_of_a_held_chain_deletes_its_held_rows() { }
            public void Scenario_1_helper_not_a_test() { }
        }
        """;

    [Fact]
    public void T01_T02_a_method_named_for_its_scenarios_in_a_class_about_one_story_names_those_scenarios_explicitly()
    {
        var inventory = Discover(("SkoleService/tests/S.Integration/CorrectionTests.cs", Chain));
        inventory.ScenarioReferenceRules.Should().Be(DotNetXunitTestDiscoveryProvider.ScenarioRules);
        Refs(inventory, "Scenario_4_a_withdrawn_report_is_not_delivered").Should().Equal(("US5", 4, TestEvidenceConfidence.StronglySupported));
        Refs(inventory, "Scenarios_2_and_8_and_9_two_reporters_are_both_flagged").Should().Equal(
            ("US5", 2, TestEvidenceConfidence.StronglySupported), ("US5", 8, TestEvidenceConfidence.StronglySupported), ("US5", 9, TestEvidenceConfidence.StronglySupported));
        Refs(inventory, "A_withdrawal_of_a_held_chain_deletes_its_held_rows").Should().BeEmpty("a class about nine scenarios never links every test to all of them");
        inventory.Definitions.Should().NotContain(d => d.MethodName == "Scenario_1_helper_not_a_test", "a method without [Fact]/[Theory] is not a test (T07)");
    }

    [Fact]
    public void T03_a_number_the_class_assigns_to_several_stories_is_only_a_candidate_for_each()
    {
        var inventory = Discover(("SkoleService/tests/S.Integration/HeldTests.cs", """
            using Xunit;
            namespace S.Integration;
            /// <summary>US4 scenarios 1, 2, 4 and 5, US2 scenarios 6 and 8 (FR-006): unresolved reports.</summary>
            public class HeldTests
            {
                [Fact] public void Scenario_2_held_and_retried() { }
                [Fact] public void Scenario_6_found_later() { }
            }
            """));
        Refs(inventory, "Scenario_2_held_and_retried").Should().Equal([("US4", 2, TestEvidenceConfidence.StronglySupported)], "only US4 has a scenario 2 in this class");
        Refs(inventory, "Scenario_6_found_later").Should().Equal(("US2", 6, TestEvidenceConfidence.StronglySupported));

        var ambiguous = Discover(("SkoleService/tests/S.Integration/X.cs", """
            using Xunit;
            namespace S.Integration;
            /// <summary>US2 scenarios 2–3, US8 scenarios 2–3: access decides the input set.</summary>
            public class AccessTests { [Fact] public void Scenario_2_access() { } }
            """));
        Refs(ambiguous, "Scenario_2_access").Should().BeEquivalentTo([("US2", 2, TestEvidenceConfidence.Inferred), ("US8", 2, TestEvidenceConfidence.Inferred)]);
    }

    [Fact]
    public void T04_T05_traits_and_the_tests_own_comment_are_explicit_a_body_comment_or_a_one_scenario_class_is_a_candidate()
    {
        var inventory = Discover(("SkoleService/tests/S.Integration/Y.cs", """
            using Xunit;
            namespace S.Integration;
            /// <summary>US4 scenario 3, FR-006, FR-009: the lock's final lookup of held reports.</summary>
            public class AtLockTests
            {
                [Fact, Trait("Scenario", "US5-AS9")] public void Traited() { }
                /// <summary>US3 scenario 4: locked periods.</summary>
                [Fact] public void Documented() { }
                [Fact] public void Commented() { /* held like an unknown child (US2 scenario 8) */ }
                [Fact] public void Unnamed() { }
            }
            """));
        Refs(inventory, "Traited").Should().Equal(("US5", 9, TestEvidenceConfidence.Confirmed));
        Refs(inventory, "Documented").Should().Equal(("US3", 4, TestEvidenceConfidence.StronglySupported));
        Refs(inventory, "Commented").Should().Equal(("US2", 8, TestEvidenceConfidence.Inferred));
        Refs(inventory, "Unnamed").Should().Equal([("US4", 3, TestEvidenceConfidence.Inferred)], "the class is about exactly one scenario: a candidate, never established per test");
    }

    [Fact]
    public void T06_number_lists_ranges_and_both_languages_are_read_and_stop_at_the_first_non_number()
    {
        DotNetXunitTestDiscoveryProvider.StatedScenarios("US5 scenarios 1–3 (FR-019..FR-022)").Should().Equal(("US5", 1), ("US5", 2), ("US5", 3));
        DotNetXunitTestDiscoveryProvider.StatedScenarios("US4 scenarios 1, 2, 4 and 5, US2 scenarios 6 og 8").Should().Equal(("US4", 1), ("US4", 2), ("US4", 4), ("US4", 5), ("US2", 6), ("US2", 8));
        DotNetXunitTestDiscoveryProvider.StatedScenarios("US8 scenario 4, FR-041").Should().Equal(("US8", 4));
        DotNetXunitTestDiscoveryProvider.StatedScenarios("see US1-AS2 and US01-AS3").Should().Equal(("US1", 2), ("US1", 3));
        DotNetXunitTestDiscoveryProvider.StatedScenarios("US5 and FR-015").Should().BeEmpty("a story alone names no scenario");
        DotNetXunitTestDiscoveryProvider.StatedScenarios("US5 scenarios 1–90").Should().BeEmpty("an implausible range is not expanded");
    }

    // ── Finalize: scope, ambiguity, many-to-many, supporting evidence ──

    private static DiscoveredScenario Scenario(string id, string spec, string story, int number, params string[] requirements) => new()
    {
        CandidateId = id, ProjectKey = "p", CapabilityId = ScenarioOwnership.Specification, Title = id, Purpose = id, CanonicalKey = "scenario:" + id, SourceIdentity = id,
        Type = ScenarioCandidateType.BusinessFunctional, Confidence = CandidateConfidence.ExplicitSpecification, ExpectedBehaviour = "ok", RequirementIds = [.. requirements],
        Story = new CoverageUserStory { Key = CoverageUserStory.KeyOf(spec, story.ToLowerInvariant()), StoryId = story, SpecificationPath = spec }, NumberInStory = number,
    };

    private static CandidateTestAssociation Test(string id, bool confirmed = true, TestScenarioLinkOrigin origin = TestScenarioLinkOrigin.Scenario) =>
        new(id, id, "SkoleService/tests/X.cs:1", "basis", confirmed) { Origin = origin, Layer = "Integration", SourceFingerprint = "f-" + id };

    [Fact]
    public void T08_T12_scenario_links_stay_in_their_module_and_story_and_are_candidates_where_the_module_is_ambiguous()
    {
        var us5s9 = Scenario("s-us5-9", "SkoleService/specs/001/spec.md", "US5", 9, "FR-021");
        var us5s8 = Scenario("s-us5-8", "SkoleService/specs/001/spec.md", "US5", 8);
        var us2s9 = Scenario("s-us2-9", "SkoleService/specs/001/spec.md", "US2", 9);
        var otherModule = Scenario("p-us5-9", "PersonService/specs/001/spec.md", "US5", 9);
        var links = new List<ScenarioTestLink>
        {
            new("SkoleService", "US5", 9, Test("t-many")), new("SkoleService", "US5", 8, Test("t-many")), new("SkoleService", "US5", 9, Test("t-second", confirmed: false)),
        };
        var requirementLinks = new List<RequirementTestLink> { new("FR-021", Test("t-fr", origin: TestScenarioLinkOrigin.SharedRequirement)), new("FR-021", Test("t-many", origin: TestScenarioLinkOrigin.SharedRequirement)) };

        var final = D.Finalize("p", "snap", [us5s9, us5s8, us2s9, otherModule], requirementLinks, links).ToDictionary(c => c.CandidateId);

        final["s-us5-9"].ExistingTests.Select(t => (t.TestId, t.Origin, t.Confirmed)).Should().Equal(
            ("t-many", TestScenarioLinkOrigin.Scenario, true), ("t-second", TestScenarioLinkOrigin.Scenario, false), ("t-fr", TestScenarioLinkOrigin.SharedRequirement, true));
        final["s-us5-8"].ExistingTests.Select(t => t.TestId).Should().Equal(["t-many"], "one test can name several scenarios");
        final["s-us2-9"].ExistingTests.Should().BeEmpty("scenario 9 of another story is a different scenario");
        final["p-us5-9"].ExistingTests.Should().BeEmpty("a test of SkoleService never names PersonService's US5 scenario 9");

        var twoSpecs = D.Finalize("p", "snap", [us5s9, Scenario("s2-us5-9", "SkoleService/specs/002/spec.md", "US5", 9)], [], links).ToDictionary(c => c.CandidateId);
        twoSpecs["s-us5-9"].ExistingTests.Should().OnlyContain(t => !t.Confirmed && t.Basis.Contains("more than one specification"));
        twoSpecs["s2-us5-9"].ExistingTests.Should().OnlyContain(t => !t.Confirmed);
    }

    [Fact]
    public void T09_T10_the_same_test_is_listed_once_per_scenario_and_scenario_links_come_from_the_inventory_with_their_details()
    {
        var inventory = Discover(("SkoleService/tests/S.Integration/CorrectionTests.cs", Chain));
        var links = D.ScenarioLinks(inventory);
        links.Should().HaveCount(4);
        links.Should().OnlyContain(l => l.Module == "SkoleService" && l.Test.Origin == TestScenarioLinkOrigin.Scenario && l.Test.Layer == "Integration"
            && l.Test.ClassName == "S.Integration.CorrectionTests" && l.Test.SourceFingerprint!.Length == 64);
        var final = D.Finalize("p", "snap", [Scenario("s9", "SkoleService/specs/001/spec.md", "US5", 9)], [], [.. links, .. links]);
        final.Single().ExistingTests.Should().ContainSingle("duplicate evidence never inflates the list");
    }

    // ── Reviewer decisions ──

    private static StoryGroupingDocument Apply(StoryGroupingDocument doc, TestLinkReview review, TechnicalCoverageExtent? extent = null, string fp = "f1") =>
        StoryGroupingEngine.Apply(doc, [new StoryGroupingOperation { Kind = StoryGroupingOperationKind.SetScenarioTestLink, ScenarioId = "s9", TestId = "t1", LinkReview = review, Extent = extent,
            TestFingerprint = fp, ScenarioFingerprint = "sc1", DisplayId = "AC 9" }], "anna", DateTimeOffset.UtcNow, _ => true);

    [Fact]
    public void T11_T13_a_confirmed_link_is_not_full_coverage_until_a_reviewer_says_so_and_a_rejected_link_has_no_extent()
    {
        var empty = new StoryGroupingDocument { ProjectKey = "p", SpecificationPath = "SkoleService/specs/001/spec.md" };
        var confirmed = Apply(empty, TestLinkReview.Confirmed);
        StoryGroupingEngine.TestLinkDecisions(confirmed).Single().Value.Should().Match<ScenarioTestLinkDecision>(d => d.Review == TestLinkReview.Confirmed && d.Extent == TechnicalCoverageExtent.NotAssessed && d.TestFingerprint == "f1");
        var partial = Apply(confirmed, TestLinkReview.Confirmed, TechnicalCoverageExtent.Partial);
        StoryGroupingEngine.TestLinkDecisions(partial).Single().Value.Extent.Should().Be(TechnicalCoverageExtent.Partial);
        partial.History.Last().Detail.Should().Contain("dekker deler av scenarioet").And.Contain("uendret");

        var rejectFull = () => Apply(confirmed, TestLinkReview.Rejected, TechnicalCoverageExtent.Full);
        rejectFull.Should().Throw<StoryGroupingEngine.GroupingRefusal>().Which.Code.Should().Be("extent-needs-confirmed-link");
        var rejected = Apply(partial, TestLinkReview.Rejected);
        StoryGroupingEngine.TestLinkDecisions(rejected).Single().Value.Should().Match<ScenarioTestLinkDecision>(d => d.Review == TestLinkReview.Rejected && d.Extent == TechnicalCoverageExtent.NotAssessed);
        StoryGroupingEngine.TestLinkDecisions(Apply(rejected, TestLinkReview.Cleared)).Should().BeEmpty("cleared = back to what the source says");
        Apply(rejected, TestLinkReview.Cleared).TestLinks.Should().HaveCount(4, "the history of decisions is kept");
        (confirmed.TestKinds.Count + confirmed.Placements.Count).Should().Be(0, "a test decision never touches the scenario's Manuell/Teknisk decision or its grouping");
    }

    // ── Real SkoleService source (opt-in; skipped with the reason otherwise) ──

    [RealSourceFolderFact("M2LB (8)")]
    [Trait("Category", RealArchiveTestGate.Category)]
    public void Real_SkoleService_tests_name_the_scenarios_their_classes_and_methods_state()
    {
        var root = Path.Combine(RealArchiveTestGate.Directory!, "M2LB (8)");
        var files = Directory.EnumerateFiles(Path.Combine(root, "SkoleService"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith("Directory.Build.props", StringComparison.OrdinalIgnoreCase))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f))).ToArray();
        var (workspace, error) = IqrSourceArchiveReader.Read("skole.zip", IqrSourceEvidenceTests.Zip(files));
        error.Should().BeNull();
        var inventory = DotNetXunitTestDiscoveryProvider.Discover(Guid.NewGuid(), "real", "M2LB", workspace!);
        var named = inventory.Definitions.Where(d => d.Scenarios.Count > 0).ToList();

        var scenarios = inventory.Definitions.Single(d => d.MethodName.StartsWith("Scenarios_2_and_8_and_9_", StringComparison.Ordinal));
        scenarios.ClassName.Should().EndWith("CorrectionAndChainTests");
        scenarios.FilePath.Should().Be("SkoleService/tests/M2LB.SkoleService.Integration/ReportIntake/CorrectionAndChainTests.cs");
        scenarios.Scenarios.Select(s => (s.StoryId, s.Number, s.Confidence)).Should().Equal(
            ("US5", 2, TestEvidenceConfidence.StronglySupported), ("US5", 8, TestEvidenceConfidence.StronglySupported), ("US5", 9, TestEvidenceConfidence.StronglySupported));
        scenarios.Kind.Should().Be(TestKind.Integration);
        D.ScenarioLinks(inventory).Should().OnlyContain(l => l.Module == "SkoleService");
        Console.WriteLine($"Real SkoleService: {inventory.Definitions.Count} tests, {named.Count} name at least one scenario; "
            + $"{named.SelectMany(d => d.Scenarios).Count(s => s.Confidence <= TestEvidenceConfidence.StronglySupported)} explicit, {named.SelectMany(d => d.Scenarios).Count(s => s.Confidence == TestEvidenceConfidence.Inferred)} candidate references.");
    }
}

/// <summary>Like <see cref="RealArchiveFactAttribute"/>, for a real source FOLDER in the opt-in directory; skipped with the reason when absent.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RealSourceFolderFactAttribute : FactAttribute
{
    public RealSourceFolderFactAttribute(string folderName)
    {
        if (RealArchiveTestGate.Directory is not { } dir)
            Skip = $"Real-source acceptance test: set {RealArchiveTestGate.EnvironmentVariableName} to a folder containing «{folderName}». Mandatory coverage is the synthetic tests.";
        else if (!Directory.Exists(Path.Combine(dir, folderName)))
            Skip = $"Real-source acceptance test: «{folderName}» not found in {RealArchiveTestGate.EnvironmentVariableName}.";
    }
}
