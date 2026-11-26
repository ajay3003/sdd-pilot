using System.IO.Compression;
using System.Text;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Hierarchical test plans (2026-11-26): test level as its own dimension, the plan structure built by one rule set (preview = what is saved), the
/// hierarchy's validity, the export package (manifest, one import file per suite, a guide generated from the manifest) and the parent-aware
/// matching of the Azure DevOps suite tree. Pure tests; the database flow is in <see cref="SourceDrivenPlanningTests"/>.
/// </summary>
public sealed class HierarchicalTestPlanTests
{
    private static PlanStructureFacts F(string id, ScenarioTestLevel level, string? area, string? module = null, string? story = null, ScenarioRequirementType? type = null) =>
        new(id, level, area, module, story, type);

    [Fact]
    public void Test_level_comes_from_the_documented_candidate_type_never_from_titles()
    {
        TestLevelClassifier.Of(ScenarioCandidateType.BusinessE2E).Should().Match<TestLevelResult>(r => r.Level == ScenarioTestLevel.EndToEnd && r.Candidate, "a documented flow is an E2E candidate");
        TestLevelClassifier.Of(ScenarioCandidateType.Integration).Level.Should().Be(ScenarioTestLevel.Integration);
        TestLevelClassifier.Of(ScenarioCandidateType.ApiContract).Level.Should().Be(ScenarioTestLevel.Component);
        TestLevelClassifier.Of(ScenarioCandidateType.BusinessFunctional).Level.Should().Be(ScenarioTestLevel.Unclassified, "an acceptance scenario does not state its boundary");
        TestLevelClassifier.Of(null).Level.Should().Be(ScenarioTestLevel.Unclassified);
        TestLevelClassifier.Label(ScenarioTestLevel.EndToEnd).Should().Be("Ende til ende");
    }

    [Fact]
    public void The_default_structure_is_test_level_then_area_with_no_empty_suites_and_stable_parent_aware_ids()
    {
        var facts = new[]
        {
            F("c1", ScenarioTestLevel.EndToEnd, "Rapportering"), F("c2", ScenarioTestLevel.EndToEnd, "CDC"), F("c3", ScenarioTestLevel.Integration, "Meldingsmottak"),
            F("c4", ScenarioTestLevel.Integration, "Rapportering"), F("c5", ScenarioTestLevel.Component, null),
        };
        var r = PlanStructure.Build(PlanStructureTemplates.Default, facts);
        PlanStructure.Flatten(r.Suites).Select(x => (new string(' ', x.Depth * 2) + x.Suite.Name)).Should().Equal(
            "Ende til ende", "  CDC", "  Rapportering", "Integrasjon", "  Meldingsmottak", "  Rapportering", "Komponent", "  Uten angitt område");
        r.Suites.Should().OnlyContain(s => r.Assignment.ContainsValue(s.Id) || r.Suites.Any(c => c.ParentId == s.Id), "no empty suite");
        var reports = r.Suites.Where(s => s.Name == "Rapportering").Select(s => s.Id).ToList();
        reports.Should().HaveCount(2).And.OnlyHaveUniqueItems("the same name under two parents is two suites");
        PlanStructure.Build(PlanStructureTemplates.Default, facts).Suites.Select(s => s.Id).Should().Equal(r.Suites.Select(s => s.Id), "the same selection gives the same ids");
        PlanStructure.PathOf(r.Suites, r.Assignment["c4"]).Should().Be("Integrasjon / Rapportering");
        r.Suites.Single(s => s.Name == "Ende til ende").TestLevel.Should().Be(ScenarioTestLevel.EndToEnd);
        PlanStructure.Validate(r.Suites).Should().BeNull();
    }

    [Fact]
    public void An_E2E_only_plan_leaves_out_the_level_layer_but_keeps_the_level_on_its_suites()
    {
        var r = PlanStructure.Build(PlanStructureTemplates.Default, [F("a", ScenarioTestLevel.EndToEnd, "Rapportering"), F("b", ScenarioTestLevel.EndToEnd, "CDC")]);
        r.Suites.Select(s => (s.Name, s.ParentId, s.TestLevel)).Should().BeEquivalentTo([("CDC", (string?)null, (ScenarioTestLevel?)ScenarioTestLevel.EndToEnd), ("Rapportering", null, ScenarioTestLevel.EndToEnd)]);
    }

    [Fact]
    public void Module_story_type_level_and_no_grouping_templates_use_only_stated_facts()
    {
        var facts = new[] { F("a", ScenarioTestLevel.Unclassified, "Søk", "Frontend", "US1 Search", ScenarioRequirementType.Functional), F("b", ScenarioTestLevel.Unclassified, null, null, null, null) };
        PlanStructure.Flatten(PlanStructure.Build(PlanStructureTemplates.ModuleStory, facts).Suites).Select(x => x.Suite.Name).Should().Equal("Frontend", "US1 Search", "Uten modul", "Uten brukerhistorie");
        PlanStructure.Flatten(PlanStructure.Build(PlanStructureTemplates.TypeLevel, facts).Suites).Select(x => x.Suite.Name).Should().Equal("Funksjonelle", "Uavklart", "Ikke akseptansescenarioer", "Uavklart");
        PlanStructure.Build(PlanStructureTemplates.None, facts).Suites.Should().OnlyContain(s => s.ParentId == null);
        PlanStructure.Build(PlanStructureTemplates.Default, []).Suites.Should().BeEmpty();
    }

    [Fact]
    public void An_invalid_hierarchy_is_refused_with_the_reason()
    {
        TestSuiteDraft S(string id, string? parent) => new(id, id, "reviewer", null) { ParentId = parent };
        PlanStructure.Validate([S("a", null), S("b", "a")]).Should().BeNull();
        PlanStructure.Validate([S("a", "b"), S("b", "a")]).Should().Contain("under seg selv");
        PlanStructure.Validate([S("a", "x")]).Should().Contain("ikke finnes");
        PlanStructure.Validate([S("a", null), S("a", null)]).Should().Contain("to ganger");
        PlanStructure.Flatten([S("child", "gone"), S("root", null)]).Select(x => x.Suite.Id).Should().Contain(["child", "root"], "a suite whose parent is missing is still shown");
    }

    // ── Grouping axes as a new plan's suites (2026-11-26) ──

    [Fact]
    public void A_grouping_becomes_two_level_suites_with_the_list_labels_stable_ids_and_one_suite_per_case()
    {
        var key = ScenarioGroupAxes.StructureKey(ScenarioGroupAxis.TestLevel, ScenarioGroupAxis.ScenarioType)!;
        key.Should().Be("axes:TestLevel>ScenarioType");
        PlanStructureTemplates.Known(key).Should().BeTrue("the server accepts the grouping key");
        var facts = new[]
        {
            F("a", ScenarioTestLevel.EndToEnd, "Rapportering", type: ScenarioRequirementType.Functional), F("b", ScenarioTestLevel.EndToEnd, "CDC", type: ScenarioRequirementType.NonFunctional),
            F("c", ScenarioTestLevel.Integration, "Mottak", type: ScenarioRequirementType.Functional), F("d", ScenarioTestLevel.Unclassified, null),
        };
        var r = PlanStructure.Build(key, facts);
        PlanStructure.Flatten(r.Suites).Select(x => new string(' ', x.Depth * 2) + x.Suite.Name).Should().Equal(
            "Ende-til-ende-tester", "  Funksjonelle", "  Ikke-funksjonelle", "Integrasjonstester", "  Funksjonelle", "Uavklart testnivå", "  Ikke akseptansescenarioer");
        r.Assignment.Should().HaveCount(4, "each case in exactly one suite");
        r.Suites.Where(s => s.Name == "Funksjonelle").Select(s => s.Id).Should().OnlyHaveUniqueItems("the same name under two parents is two suites");
        r.Suites.Single(s => s.Name == "Ende-til-ende-tester").TestLevel.Should().Be(ScenarioTestLevel.EndToEnd);
        PlanStructure.Build(key, facts).Suites.Select(s => s.Id).Should().Equal(r.Suites.Select(s => s.Id), "stable ids");
        PlanStructure.Validate(r.Suites).Should().BeNull();
        PlanStructure.Build("axes:Story", [F("x", ScenarioTestLevel.Integration, null, story: "US1 Rapport"), F("y", ScenarioTestLevel.Integration, null)])
            .Suites.Select(s => s.Name).Should().Equal("US1 Rapport", "Scenarioer uten brukerhistorie");
    }

    [Fact]
    public void Test_method_and_unknown_or_duplicate_axes_are_never_a_plan_structure()
    {
        ScenarioGroupAxes.StructureKey(ScenarioGroupAxis.Method, ScenarioGroupAxis.None).Should().BeNull("a method is a decision about a scenario, not a fact of a case");
        ScenarioGroupAxes.StructureKey(ScenarioGroupAxis.TestLevel, ScenarioGroupAxis.Method).Should().BeNull();
        ScenarioGroupAxes.StructureKey(ScenarioGroupAxis.None, ScenarioGroupAxis.None).Should().BeNull();
        PlanStructureTemplates.Known("axes:TestLevel>TestLevel").Should().BeFalse();
        PlanStructureTemplates.Known("axes:Nonsense").Should().BeFalse();
        PlanStructureTemplates.Known("axes:Method").Should().BeFalse();
        PlanStructureTemplates.Known(PlanStructureTemplates.LevelArea).Should().BeTrue("existing templates are unchanged");
    }

    private static TestPlanDraft Plan()
    {
        var r = PlanStructure.Build(PlanStructureTemplates.Default, [F("c1", ScenarioTestLevel.EndToEnd, "Rapportering"), F("c2", ScenarioTestLevel.Integration, "Rapportering"), F("c3", ScenarioTestLevel.Integration, "Mottak")]);
        TestCaseDraft Case(string id) => new()
        {
            Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "p", ScenarioId = id }, SuiteId = r.Assignment[id], Review = TestCaseReviewState.Accepted,
            Generated = new TestCaseContent { Title = "Case " + id, Steps = ["Open"], StepExpected = ["Opens"], SourceIdentity = "tss-" + id, CandidateType = id == "c1" ? ScenarioCandidateType.BusinessE2E : ScenarioCandidateType.Integration },
        };
        var empty = new TestSuiteDraft("suite:empty", "Tom gruppe", "reviewer", null) { Order = 9 };
        return new TestPlanDraft { Id = Guid.NewGuid(), PlanName = "Skolenærvær", Revision = 3, Suites = [.. r.Suites, empty], Cases = [Case("c1"), Case("c2"), Case("c3")] };
    }

    [Fact]
    public void The_export_package_has_a_manifest_one_file_per_suite_with_cases_and_a_guide_from_the_manifest()
    {
        var plan = Plan();
        var package = AdoExportPackage.Build(plan, plan.Cases.Select(c => c.Source.ScenarioId).ToHashSet(),
            subset => new AdoExportPreview { Rows = [.. subset.Select(c => new AdoExportRow("", "Test Case", c.EffectiveTitle, 1, "Open", "Opens", "Area", "", "Design", null) { CaseId = c.Source.ScenarioId })] },
            p => Encoding.UTF8.GetBytes(string.Join("\n", p.Rows.Select(r => r.Title))), "csv");
        using var zip = new ZipArchive(new MemoryStream(package.Zip));
        var names = zip.Entries.Select(e => e.FullName).ToList();
        names.Should().Contain(["TestSuite-struktur.csv", "Testplanoversikt.csv", "Importveiledning.txt", "Ende-til-ende/Rapportering.csv", "Integrasjon/Rapportering.csv", "Integrasjon/Mottak.csv"]);
        names.Count(n => n.EndsWith(".csv") && n.Contains('/')).Should().Be(3, "only suites with cases get a file");
        package.Rows.Should().Be(3);
        package.Manifest.Should().Contain(m => m.Path == "Tom gruppe" && m.Cases == 0 && m.File == null, "an empty suite is listed, with no file");
        package.Manifest.Select(m => m.Path).Should().ContainInOrder("Ende til ende", "Ende til ende / Rapportering", "Integrasjon", "Integrasjon / Mottak");
        var guide = new StreamReader(zip.GetEntry("Importveiledning.txt")!.Open()).ReadToEnd();
        guide.Should().Contain("oppretter IKKE testgrupper").And.Contain("Integrasjon / Rapportering  ←  Integrasjon/Rapportering.csv").And.Contain("Tom gruppe (tom testgruppe)");
        guide.IndexOf("   - Integrasjon\r", StringComparison.Ordinal).Should().BeLessThan(guide.IndexOf("   - Integrasjon / Mottak", StringComparison.Ordinal), "parent before child");
    }

    [Fact]
    public void The_remote_suite_tree_is_matched_by_name_under_the_matched_parent_never_by_name_alone()
    {
        var plan = Plan();
        var remote = new List<AdoTestSuiteInfo>
        {
            new(100, "Root", null, "staticTestSuite"), new(101, "Integrasjon", 100, "staticTestSuite"), new(102, "Rapportering", 101, "staticTestSuite"),
            new(200, "Rapportering", 100, "staticTestSuite"), // a «Rapportering» directly under the destination: not «Ende til ende / Rapportering»
        };
        var items = AdoTestPlanTransferService.SuitePlan(plan, plan.Cases.Select(c => c.Source.ScenarioId).ToHashSet(), remote, 100);
        items.Select(i => (i.Path, i.Exists, i.RemoteSuiteId)).Should().Equal(
            ("Ende til ende", false, (int?)null), ("Ende til ende / Rapportering", false, null),
            ("Integrasjon", true, 101), ("Integrasjon / Mottak", false, null), ("Integrasjon / Rapportering", true, 102));
        items.Should().NotContain(i => i.Path == "Tom gruppe", "an empty suite is not created by a transfer of cases");
    }

    private static DiscoveredScenario Ac(string id, string then, string fingerprint = "fp1") => new()
    {
        CandidateId = id, CapabilityId = "specification", CanonicalKey = $"scenario:M|{id}", Title = id, ExpectedBehaviour = then, Fingerprint = fingerprint,
        Template = [new("then", "Then", then)],
        Story = new BirkNext.TestCoverage.CoverageUserStory { Key = "spec#US2", StoryId = "US2", Title = "Same story", SpecificationPath = "M/specs/001-x/spec.md" },
    };

    [Fact]
    public void AC_level_types_in_the_plan_structure_come_from_each_scenario_and_its_own_review_never_from_the_story()
    {
        var functional = Ac("ac1", "the report is accepted and stored");
        var quality = Ac("ac2", "the response is returned within 2 seconds");
        var (a, b) = (PlanStructure.FactsOf("ac1", functional), PlanStructure.FactsOf("ac2", quality));
        (a.Story, b.Story).Should().Be(("US2 Same story", "US2 Same story"), "the same story");
        a.Type.Should().Be(ScenarioTypeClassifier.Propose(functional).Type);
        b.Type.Should().Be(ScenarioTypeClassifier.Propose(quality).Type);
        a.Type.Should().NotBe(b.Type, "two ACs of one story keep their own types");

        // A reviewer's decision for THIS scenario wins (before 2026-11-26 the structure read the proposal only); for another version it does not.
        var reviewed = new ScenarioTypeDecision("ac1", "spec#US2", ScenarioRequirementType.Mixed, [], "both", "lead", DateTimeOffset.UnixEpoch, "fp1");
        PlanStructure.FactsOf("ac1", functional, decision: reviewed).Type.Should().Be(ScenarioRequirementType.Mixed);
        PlanStructure.FactsOf("ac1", functional with { Fingerprint = "fp2" }, decision: reviewed).Type.Should().Be(ScenarioTypeClassifier.Propose(functional).Type, "a review of an earlier text is not applied");
        PlanStructure.FactsOf("ac2", quality, resolvedType: ScenarioRequirementType.Unclassified).Type.Should().Be(ScenarioRequirementType.Unclassified, "the browser's resolved type is used as given");

        // A requirement without an acceptance scenario has no scenario type.
        PlanStructure.FactsOf("r1", functional with { CanonicalKey = "requirement:M|FR-1" }).Type.Should().BeNull();
        PlanStructure.Flatten(PlanStructure.Build(PlanStructureTemplates.TypeLevel, [a, b]).Suites).Where(x => x.Depth == 0).Should().HaveCount(2, "one suite per AC type, none for the story");
    }
}
