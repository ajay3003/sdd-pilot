using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.SystemUnderstanding;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>Cross-provider consolidation, conflict validation, guide reconciliation and rollback rules (pure; 2026-11-23).</summary>
public sealed class ScenarioConsolidationTests
{
    private static DiscoveredScenario Candidate(string capability, string key, string given, string when, string? then, params string[] requirements) =>
        D.FinishCandidate(new DiscoveredScenario
        {
            CapabilityId = capability, CanonicalKey = key, SourceCategory = capability, Title = $"{given} — {when}", Preconditions = [given], Steps = [new(when, then)],
            ExpectedBehaviour = then, RequirementIds = [.. requirements], Evidence = [new("source", key)],
        }, "p");

    [Fact]
    public void Exact_duplicates_of_different_sources_are_merged_with_all_evidence_and_identities()
    {
        var spec = Candidate(D.Specification, "scenario:|s1", "a valid report exists", "the report is submitted", "the report is stored", "FR-001");
        var journey = Candidate(D.IntegrationJourneys, "journey:x", "a valid report exists", "the report is submitted", "the report is stored");
        var result = ScenarioConsolidation.Apply([spec, journey]);
        result.Should().ContainSingle().Which.CapabilityId.Should().Be(D.Specification, "the specification is the stronger source");
        result[0].Evidence.Select(e => e.Reference).Should().Contain(["scenario:|s1", "journey:x"]);
        result[0].MergedFrom.Should().Contain(journey.SourceIdentity);
    }

    [Fact]
    public void Contradictory_expected_results_for_the_same_situation_are_a_conflict_with_both_claims_and_never_a_merge()
    {
        var spec = Candidate(D.Specification, "scenario:|s2", "no matching child exists", "a report arrives", "the service responds 503");
        var contract = Candidate(D.SystemGuide, "guide:g/x", "no matching child exists", "a report arrives", "the service responds 200 and holds the report");
        var result = ScenarioConsolidation.Apply([spec, contract]);
        result.Should().HaveCount(2);
        result.Should().OnlyContain(c => c.Conflicts.Count == 1 && c.Conflicts[0].Kind == ConflictKind.ContradictoryExpectedResult && c.Conflicts[0].Claims.Count == 2);
        result[0].Conflicts[0].Explanation.Should().Contain("Ulike kilder beskriver forskjellige forventede resultater");
        result.Should().OnlyContain(c => c.Gaps.Any(g => g.Kind == EvidenceGapKind.ContradictoryEvidence));
    }

    [Fact]
    public void Different_statuses_for_different_conditions_and_negative_variants_are_never_conflicts_or_merges()
    {
        var missing = Candidate(D.Specification, "scenario:|a", "the database is unavailable", "a report arrives", "the service responds 503", "FR-010");
        var unknown = Candidate(D.SystemGuide, "guide:g/b", "no matching child exists", "a report arrives", "the service responds 200", "FR-010");
        var positive = Candidate(D.Specification, "scenario:|c", "a report with a school", "the report is submitted", "the report is accepted");
        var negative = Candidate(D.UiSource, "ui:d", "a report without a school", "the report is submitted", "the report is rejected");
        var result = ScenarioConsolidation.Apply([missing, unknown, positive, negative]);
        result.Should().HaveCount(4, "nothing is merged");
        result.Should().OnlyContain(c => c.Conflicts.Count == 0, "different conditions and variants are not conflicts");
        result.Single(c => c.CanonicalKey == "scenario:|a").Relations.Should().ContainSingle(r => r.Relation == ScenarioRelation.Related);
    }

    [Fact]
    public void Same_situation_with_compatible_wording_is_noted_as_equivalent_and_kept()
    {
        var spec = Candidate(D.Specification, "scenario:|e", "a valid attendance report", "the report is submitted", "the report is stored and a receipt is shown");
        var ui = Candidate(D.UiSource, "ui:e", "a valid attendance report", "the report is submitted", "the report is stored and the receipt is shown to the user");
        var result = ScenarioConsolidation.Apply([spec, ui]);
        result.Should().HaveCount(2);
        result.Should().OnlyContain(c => c.Relations.Any(r => r.Relation == ScenarioRelation.Equivalent));
    }

    [Fact]
    public void A_guide_scenario_generated_from_a_candidate_merges_only_while_it_says_the_same_otherwise_it_is_a_version_conflict()
    {
        var spec = Candidate(D.Specification, "scenario:|f", "a report for a closed period", "the report is submitted", "rejected with status 422");
        var same = Candidate(D.SystemGuide, "guide:g/f", "x", "y", "rejected with status 422") with { Evidence = [new(ScenarioConsolidation.DerivedFrom, spec.SourceIdentity)] };
        ScenarioConsolidation.Apply([spec, same]).Should().ContainSingle();
        var older = same with { ExpectedBehaviour = "rejected with status 400" };
        var result = ScenarioConsolidation.Apply([spec, older]);
        result.Should().HaveCount(2).And.OnlyContain(c => c.Conflicts.Single().Kind == ConflictKind.DifferentSourceVersion);
    }

    [Fact]
    public void Reconciliation_matches_written_scenarios_by_requirement_and_situation_never_appends_duplicates_and_flags_ambiguity()
    {
        var written = new GuideTestScenario { Id = "sc-1", Title = "Closed period report", Preconditions = "a report for a closed period", ExpectedOutcome = "rejected with status 400",
            Evidence = [new GuideEvidence { RequirementId = "FR-002" }] };
        var generated = new GuideTestScenario { Id = "spec-1", Title = "Closed period report", Preconditions = "a report for a closed period", ExpectedOutcome = "rejected with status 422",
            Evidence = [new GuideEvidence { RequirementId = "FR-002", SourceRole = "application", SourceLabel = "v2.zip" }], SourceIdentity = "tss-1" };
        var content = new SystemGuideContent { Title = "G", Scenarios = [written] };
        var changes = SystemGuideService.Reconcile(content, [generated], reviewed: true, "application");
        changes.Should().ContainSingle().Which.Kind.Should().Be("ConflictsWithReviewed", "a reviewed scenario says 400, the source says 422");
        changes[0].ScenarioId.Should().Be("sc-1", "the written scenario is updated, not duplicated");
        SystemGuideService.Apply(content, changes, null).Scenarios.Should().ContainSingle().Which.ExpectedOutcome.Should().Be("rejected with status 422");

        var twin = written with { Id = "sc-2" };
        var ambiguous = SystemGuideService.Reconcile(content with { Scenarios = [written, twin] }, [generated], reviewed: false, "application");
        ambiguous.Should().ContainSingle().Which.Kind.Should().Be("AmbiguousMatch");
        ambiguous[0].AlternativeScenarioIds.Should().BeEquivalentTo(["sc-1", "sc-2"]);
        SystemGuideService.Apply(content with { Scenarios = [written, twin] }, ambiguous, null).Scenarios.Should().HaveCount(2, "an ambiguous match is never applied unasked");
    }

    // ── Review findings (2026-11-23) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Numbers_and_negations_keep_scenarios_apart_and_a_merge_keeps_everything_the_other_source_said()
    {
        var two = Candidate(D.Specification, "scenario:|n2", "a search for archived reports", "the search runs", "returns 2 rows");
        var zero = Candidate(D.UiSource, "ui:n0", "a search for archived reports", "the search runs", "returns 0 rows");
        ScenarioConsolidation.Apply([two, zero]).Should().HaveCount(2, "0 rows and 2 rows are different expected results");

        var withRole = Candidate(D.Specification, "scenario:|r1", "a user with role reader", "the user opens the report", "the report is shown");
        var noRole = Candidate(D.UiSource, "ui:r0", "a user with no role reader", "the user opens the report", "the report is shown");
        var result = ScenarioConsolidation.Apply([withRole, noRole]);
        result.Should().HaveCount(2, "\"no role\" is the negative variant, never a duplicate");
        result.Should().OnlyContain(c => c.Relations.Any(r => r.Relation == ScenarioRelation.Variant));

        var spec = Candidate(D.Specification, "scenario:|m1", "a valid report exists", "the report is submitted", "the report is stored");
        var ui = Candidate(D.UiSource, "ui:m1", "a valid report exists", "the report is submitted", "the report is stored") with
            { OpenQuestions = ["Which receipt is shown?"], TestDataNeeds = ["A school with an active period"] };
        var merged = ScenarioConsolidation.Apply([spec, ui]).Single();
        merged.OpenQuestions.Should().Contain("Which receipt is shown?");
        merged.TestDataNeeds.Should().Contain("A school with an active period");
    }

    private static DiscoveredScenario SpecScenario(string? module, string id, string given, string when, string then, string file = "specs/Intake/spec.md") =>
        D.FromSpecification("p", new BirkNext.TestCoverage.CoverageWorkspaceEvidence
        {
            SpecificationAvailable = true, ArtifactScope = module, SpecificationArtifacts = [new("s", file, null, "fp")],
            AcceptanceScenarios = [new BirkNext.TestCoverage.QaTestCandidate { Id = id, Title = given, Given = given, When = when, Then = then }],
        }).Candidates.Single();

    [Fact]
    public void A_specification_scenario_keeps_its_identity_whatever_module_scope_or_path_spelling_and_twins_are_told_apart_by_outcome()
    {
        SpecScenario(null, "scenario-1", "a valid report", "it is sent", "stored").SourceIdentity
            .Should().Be(SpecScenario("Module Intake", "scenario-1", "a valid report", "it is sent", "stored", "specs\\intake\\SPEC.md").SourceIdentity,
                "a new module selection or a second specification file must not make every scenario look removed");

        var evidence = new BirkNext.TestCoverage.CoverageWorkspaceEvidence
        {
            SpecificationAvailable = true, SpecificationArtifacts = [new("s", "spec.md", null, "fp")],
            AcceptanceScenarios =
            [
                new BirkNext.TestCoverage.QaTestCandidate { Id = "scenario-1", Given = "a report", When = "it is sent", Then = "it is stored" },
                new BirkNext.TestCoverage.QaTestCandidate { Id = "scenario-2", Given = "a report", When = "it is sent", Then = "a receipt is shown" },
            ],
        };
        var both = D.Finalize("p", "", D.FromSpecification("p", evidence).Candidates, []);
        var receipt = both.Single(c => c.ExpectedBehaviour == "a receipt is shown").SourceIdentity;
        both.Select(c => c.SourceIdentity).Should().OnlyHaveUniqueItems();
        var onlySecond = D.Finalize("p", "", D.FromSpecification("p", evidence with { AcceptanceScenarios = [evidence.AcceptanceScenarios[1] with { Id = "scenario-1" }] }).Candidates, []);
        onlySecond.Single().SourceIdentity.Should().NotBe(both.Single(c => c.ExpectedBehaviour == "it is stored").SourceIdentity,
            "when the first twin is removed, the second never takes over its identity");
        var savedReceipt = new TestCaseDraft { Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "p", ScenarioId = "old" },
            Generated = new TestCaseContent { SourceIdentity = receipt } };
        new TestPlanSuggestionService.CandidateIndex(onlySecond).Find(savedReceipt)!.ExpectedBehaviour.Should().Be("a receipt is shown",
            "a saved case still finds its own twin after the other one was removed");
        var savedStored = savedReceipt with { Generated = new TestCaseContent { SourceIdentity = both.Single(c => c.ExpectedBehaviour == "it is stored").SourceIdentity } };
        new TestPlanSuggestionService.CandidateIndex(onlySecond).Find(savedStored).Should().BeNull("the removed twin is not replaced by the remaining one");
    }

    [Fact]
    public void Applying_a_change_keeps_every_word_the_reviewer_wrote_in_the_draft_and_removals_need_a_source_that_was_read()
    {
        var published = new GuideTestScenario { Id = "sc-1", Title = "Closed period", Preconditions = "a report for a closed period", ExpectedOutcome = "rejected with status 400", SourceIdentity = "tss-1",
            Evidence = [new GuideEvidence { SourceRole = "application", SourceLabel = "v1.zip" }] };
        var generated = published with { Id = "spec-1", ExpectedOutcome = "rejected with status 422", Evidence = [new GuideEvidence { SourceRole = "application", SourceLabel = "v2.zip" }] };
        var changes = SystemGuideService.Reconcile(new SystemGuideContent { Title = "G", Scenarios = [published] }, [generated], reviewed: false, "application");
        var draft = new SystemGuideContent { Title = "G", Scenarios = [published with { Title = "Rapport for stengt periode", Preconditions = "Perioden er låst", TestData = "Skole 42" }] };
        var applied = SystemGuideService.Apply(draft, changes, new Dictionary<string, GuideChangeDecision> { [changes[0].ChangeId!] = new(changes[0].ChangeId!, "Accepted", null, null, "lead", DateTimeOffset.UtcNow) });
        var s = applied.Scenarios.Single();
        s.ExpectedOutcome.Should().Be("rejected with status 422");
        s.Title.Should().Be("Rapport for stengt periode");
        s.Preconditions.Should().Be("Perioden er låst");
        s.TestData.Should().Be("Skole 42");
        s.Evidence.Select(e => e.SourceLabel).Should().Equal("v2.zip");

        var gone = SystemGuideService.Reconcile(new SystemGuideContent { Title = "G", Scenarios = [published] }, [], reviewed: false, "application", _ => false);
        gone.Should().BeEmpty("a source that was not read proves nothing about removal");
        SystemGuideService.Reconcile(new SystemGuideContent { Title = "G", Scenarios = [published] }, [], reviewed: false, "application", _ => true)
            .Should().ContainSingle(c => c.Kind == "EvidenceRemoved");
    }

    [Fact]
    public void A_case_that_cannot_be_compared_is_never_called_removed()
    {
        var complete = new ScenarioDiscoveryResult
        {
            ProjectKey = "import:new", Capabilities = [new DiscoveryCapability { Id = D.UiSource, State = DiscoveryCapabilityState.Available }],
            Basis = new DiscoverySourceBasis { ApplicationKey = "app", Members = [new DiscoverySourceMember { RepositoryKey = "r", SnapshotId = Guid.NewGuid() }] },
        };
        var old = new TestCaseDraft { Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "import:old", ScenarioId = "tsc-1" },
            Generated = new TestCaseContent { Capability = D.UiSource, SnapshotKey = Guid.NewGuid().ToString() } };
        TestPlanSuggestionService.Absence(old, complete, null).State.Should().Be(CaseSourceState.SourceUnavailable, "no identity: it cannot be found in another import");
        var withIdentity = old with { Generated = old.Generated with { SourceIdentity = "tss-1" } };
        TestPlanSuggestionService.Absence(withIdentity, complete, null).State.Should().Be(CaseSourceState.SourceUnavailable,
            "the source version it was read from is no longer known: absence proves nothing");
        var known = withIdentity with { Generated = withIdentity.Generated with { SnapshotKey = complete.Basis.Members[0].SnapshotId.ToString() } };
        TestPlanSuggestionService.Absence(known, complete, null).State.Should().Be(CaseSourceState.Removed, "a complete comparison of its own source");
    }

    [Fact]
    public void An_update_that_would_read_an_older_version_of_a_source_than_the_plan_was_made_from_is_detected()
    {
        DiscoverySourceBasis Basis(DateTimeOffset at) => new() { Members = [new DiscoverySourceMember { RepositoryKey = "r", DisplayName = "R", SnapshotId = Guid.NewGuid(), AnalyzedAt = at, ArchiveName = $"{at:HHmm}.zip" }] };
        var now = DateTimeOffset.UtcNow;
        TestPlanSuggestionService.Rollback(Basis(now), Basis(now.AddDays(-1))).Should().Contain("rulle planen tilbake");
        TestPlanSuggestionService.Rollback(Basis(now.AddDays(-1)), Basis(now)).Should().BeNull();
        TestPlanSuggestionService.Rollback(null, Basis(now)).Should().BeNull("an older plan without a basis is never blocked");
    }
}
