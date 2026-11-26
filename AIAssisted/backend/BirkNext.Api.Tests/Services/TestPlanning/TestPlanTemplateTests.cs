using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Test plan templates (2026-11-26): structure only, new suite ids per plan, built-ins read-only, custom templates saved/duplicated/archived in an
/// App_Data file of a temporary directory (never the installation's). The database flow (empty plan, plan from a template, manual cases inside a
/// suite) is in <see cref="SourceDrivenPlanningTests"/>.
/// </summary>
public sealed class TestPlanTemplateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-templates-" + Guid.NewGuid().ToString("N")[..10]);
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 10, 0, 0, TimeSpan.Zero);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public void Built_in_templates_are_valid_and_instantiating_gives_new_ids_with_the_same_nesting_order_and_level()
    {
        TestPlanTemplateRules.BuiltIn.Select(t => t.Id).Should().Contain(TestPlanTemplateRules.StandardId);
        TestPlanTemplateRules.BuiltIn.Should().OnlyContain(t => t.BuiltIn && TestPlanTemplateRules.Validate(t.Name, t.Suites) == null);
        var template = TestPlanTemplateRules.BuiltIn.First(t => t.Suites.Any(s => s.ParentKey is not null));

        var a = TestPlanTemplateRules.Instantiate(template);
        var b = TestPlanTemplateRules.Instantiate(template);
        a.Select(s => s.Id).Should().NotIntersectWith(b.Select(s => s.Id), "every plan gets its own suite ids");
        a.Select(s => s.Id).Should().NotIntersectWith(template.Suites.Select(s => s.Key), "template keys are never plan ids");
        PlanStructure.Validate(a).Should().BeNull();
        PlanStructure.Flatten(a).Select(x => (x.Suite.Name, x.Depth)).Should().Equal(PlanStructure.Flatten(b).Select(x => (x.Suite.Name, x.Depth)));
        a.Select(s => s.TestLevel).Should().Equal(template.Suites.Select(s => s.TestLevel));
    }

    [Fact]
    public void A_template_saved_from_a_plan_holds_only_the_structure()
    {
        var root = new TestSuiteDraft("suite:x:1", "Ende til ende", "structure", null) { TestLevel = ScenarioTestLevel.EndToEnd };
        var child = new TestSuiteDraft("suite:x:2", "Rapportering", "structure", null) { ParentId = root.Id, Order = 0 };
        var plan = new TestPlanDraft
        {
            Id = Guid.NewGuid(), PlanName = "Plan", Suites = [root, child],
            Cases = [new TestCaseDraft { SuiteId = child.Id, Review = TestCaseReviewState.Accepted, Generated = new TestCaseContent { Title = "A case" } }],
        };
        var suites = TestPlanTemplateRules.FromPlan(plan);
        suites.Select(s => s.Name).Should().Equal("Ende til ende", "Rapportering");
        suites.Single(s => s.Name == "Rapportering").ParentKey.Should().Be(suites.Single(s => s.Name == "Ende til ende").Key);
        suites.Single(s => s.Name == "Ende til ende").TestLevel.Should().Be(ScenarioTestLevel.EndToEnd);
        suites.Select(s => s.Key).Should().NotContain([root.Id, child.Id], "a plan's suite ids (and so any Azure DevOps mapping) never leave the plan");
    }

    [Fact]
    public void Custom_templates_are_saved_renamed_duplicated_archived_and_restored_and_built_ins_are_refused()
    {
        var store = new TestPlanTemplateStore(_dir);
        store.List().Should().OnlyContain(t => t.BuiltIn, "a new installation has only the built-ins");

        var saved = store.Save(null, "Min mal", "Til regresjon", [new TemplateSuite("a", "Regresjon"), new TemplateSuite("b", "Smoke", "a")], "anna", Now);
        saved.Id.Should().StartWith("custom:");
        new TestPlanTemplateStore(_dir).Find(saved.Id)!.Suites.Should().HaveCount(2, "kept in the file across restarts");
        File.Exists(Path.Combine(_dir, "plan-templates.json")).Should().BeTrue();

        FluentActions.Invoking(() => store.Save(null, "min mal", null, [], "anna", Now)).Should().Throw<ArgumentException>("names are unique, ignoring case");
        FluentActions.Invoking(() => store.Save(null, "Syklus", null, [new TemplateSuite("a", "A", "b"), new TemplateSuite("b", "B", "a")], "anna", Now)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => store.Save(TestPlanTemplateRules.StandardId, "Standard", null, [], "anna", Now)).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => store.Apply(TestPlanTemplateRules.StandardId, TemplateAction.Archive, "anna", Now)).Should().Throw<InvalidOperationException>();

        var renamed = store.Save(saved.Id, "Regresjonsmal", null, saved.Suites, "per", Now.AddMinutes(1));
        (renamed.Name, renamed.CreatedBy, renamed.UpdatedBy).Should().Be(("Regresjonsmal", "anna", "per"));

        var copy = store.Apply(TestPlanTemplateRules.StandardId, TemplateAction.Duplicate, "anna", Now);
        (copy.BuiltIn, copy.Archived).Should().Be((false, false));
        copy.Name.Should().EndWith("(kopi)");
        copy.Suites.Should().BeEquivalentTo(TestPlanTemplateRules.BuiltIn.Single(t => t.Id == TestPlanTemplateRules.StandardId).Suites);

        store.Apply(saved.Id, TemplateAction.Archive, "anna", Now);
        store.List().Select(t => t.Id).Should().NotContain(saved.Id, "an archived template is not offered for new plans");
        store.List(includeArchived: true).Single(t => t.Id == saved.Id).Archived.Should().BeTrue();
        store.Apply(saved.Id, TemplateAction.Restore, "anna", Now);
        store.List().Select(t => t.Id).Should().Contain(saved.Id);
    }

    [Fact]
    public void A_template_plan_exports_its_manual_case_and_lists_every_empty_suite_in_the_manifest()
    {
        var suites = TestPlanTemplateRules.Instantiate(TestPlanTemplateRules.BuiltIn.Single(t => t.Id == TestPlanTemplateRules.StandardId));
        var e2e = suites.Single(s => s.TestLevel == ScenarioTestLevel.EndToEnd);
        var manual = new TestCaseDraft
        {
            Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = "import:m", ScenarioId = "manual:1" }, SuiteId = e2e.Id,
            Generated = new TestCaseContent { Title = "Send rapport" }, Edits = new TestCaseEdits { TestLevel = ScenarioTestLevel.EndToEnd, TestKind = ScenarioTestKind.Manual },
        };
        var plan = new TestPlanDraft { Id = Guid.NewGuid(), PlanName = "Fra mal", Suites = suites, Cases = [manual] };
        var package = AdoExportPackage.Build(plan, new HashSet<string> { "manual:1" },
            subset => new AdoExportPreview { Rows = [.. subset.Select(c => new AdoExportRow("", "Test Case", c.EffectiveTitle, 1, "Open", "Opens", "Area", "", "Design", null) { CaseId = c.Source.ScenarioId })] },
            p => System.Text.Encoding.UTF8.GetBytes(string.Join(",", p.Rows.Select(r => r.Title))), "csv");
        package.Rows.Should().Be(1, "a case written by hand is exported like any other ready case");
        package.Manifest.Should().HaveCount(suites.Count, "every suite of the plan, empty or not, is in the manifest");
        package.Manifest.Single(m => m.SuiteId == e2e.Id).File.Should().NotBeNull();
        package.Manifest.Where(m => m.SuiteId != e2e.Id).Should().OnlyContain(m => m.Cases == 0 && m.File == null);
    }

    [Fact]
    public void An_unreadable_template_file_never_hides_the_built_ins()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "plan-templates.json"), "{ not json");
        new TestPlanTemplateStore(_dir).List().Should().HaveCount(TestPlanTemplateRules.BuiltIn.Count);
    }
}
