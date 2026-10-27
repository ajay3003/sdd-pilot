using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Deterministic classification of source content an Explorer did not structure: one category per block, Explorer-aware potential
/// mapping gaps only when the structured model does not already hold the fact, and grouping that never changes the evidence.
/// </summary>
public sealed class UnmappedSourceContentTests
{
    private static MarkdownSourceNote Note(int line, string text, string type = "Text", string? section = null) =>
        new($"Doc:F:{line}:{text.GetHashCode():x}", line, line, type, "F" + line, text, section);

    private static ClassifiedSourceBlock One(UnmappedSourceContext context, string text, string type = "Text", string? section = null) =>
        UnmappedSourceContent.Classify([Note(1, text, type, section)], context).Blocks.Single();

    private static readonly UnmappedSourceContext Generic = new(SourceContentExplorer.None, null);

    // ── Common ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Version change: 1.0.0 → 1.0.1", "Text", SourceContentCategory.ChangeHistory)]
    [InlineData("Previous version history: Version change (new) → 1.0.0", "Text", SourceContentCategory.ChangeHistory)]
    [InlineData("```", "FencedCodeStart", SourceContentCategory.Structural)]
    [InlineData("```", "FencedCodeEnd", SourceContentCategory.Structural)]
    [InlineData("", "FencedCodeLine", SourceContentCategory.Structural)]
    [InlineData("<!--", "Text", SourceContentCategory.Structural)]
    [InlineData("-->", "Text", SourceContentCategory.Structural)]
    [InlineData("|", "Text", SourceContentCategory.Structural)]
    [InlineData("│        │", "FencedCodeLine", SourceContentCategory.Structural)]
    [InlineData("====", "Text", SourceContentCategory.Structural)]
    [InlineData("## Summary", "Heading", SourceContentCategory.Structural)]
    [InlineData("Source: docs/person-domain-model-no.md", "Text", SourceContentCategory.Reference)]
    [InlineData("Build the Person Module — the platform's single source of truth for person identity.", "Text", SourceContentCategory.Informational)]
    public void Common_categories_apply_to_every_explorer(string text, string type, SourceContentCategory expected)
    {
        foreach (var artifact in Enum.GetValues<SourceContentExplorer>())
            One(new UnmappedSourceContext(artifact, null), text, type).Category.Should().Be(expected, $"{artifact}: {text}");
    }

    [Fact]
    public void Html_comment_change_report_is_history_never_a_gap()
    {
        var blocks = new[] { Note(1, "<!--"), Note(2, "Sync Impact Report"), Note(3, "Modified principles: PP-03 MUST log access"), Note(4, "-->") };
        var content = UnmappedSourceContent.Classify(blocks, UnmappedSourceContext.For(new ConstitutionDocument()));

        content.Blocks.Select(b => b.Category).Should().Equal(SourceContentCategory.Structural, SourceContentCategory.ChangeHistory, SourceContentCategory.ChangeHistory, SourceContentCategory.Structural);
        content.Gaps.Should().BeEmpty();
    }

    [Fact]
    public void Examples_and_legends_never_become_gaps()
    {
        var plan = UnmappedSourceContext.For(new PlanDocument());
        One(plan, "Example: Phase 2 blocks all work").Category.Should().Be(SourceContentCategory.Informational);
        One(plan, "Phase 2 BLOCKS all user stories", section: "Example plan").Category.Should().Be(SourceContentCategory.Informational);
        One(UnmappedSourceContext.For(new TaskTree()), "[P]: Can run in parallel (different files)", "BulletItem", "Format: [ID] [P?] [Story] Description")
            .Category.Should().Be(SourceContentCategory.Informational);
    }

    [Fact]
    public void Without_an_explorer_context_nothing_is_a_gap()
    {
        One(Generic, "Phase 2 (Foundational) ← BLOCKS all user stories").Category.Should().Be(SourceContentCategory.Informational);
        One(Generic, "The system MUST mask national IDs.").Category.Should().Be(SourceContentCategory.Informational);
    }

    // ── Plan ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Plan_dependency_is_a_gap_only_when_not_represented()
    {
        const string text = "Phase 2 (Foundational) ← BLOCKS all user stories";
        var gap = One(UnmappedSourceContext.For(new PlanDocument()), text);
        gap.Category.Should().Be(SourceContentCategory.PotentialMappingGap);
        gap.PotentialKind.Should().Be("Dependency");

        var represented = new PlanDocument { Phases = [new PlanImplementationPhase { Title = "Foundational", Description = text }] };
        var block = One(UnmappedSourceContext.For(represented), text);
        block.Category.Should().NotBe(SourceContentCategory.PotentialMappingGap);
        block.AlreadyRepresented.Should().BeTrue();
    }

    [Fact]
    public void Plan_testing_strategy_and_checkpoint_are_gaps_when_absent()
    {
        var plan = UnmappedSourceContext.For(new PlanDocument());
        One(plan, "Tests written alongside or before implementation").PotentialKind.Should().Be("Testing strategy");
        One(plan, "STOP and VALIDATE: dotnet test --filter Category=Security — all pass").PotentialKind.Should().Be("Test checkpoint");
        One(UnmappedSourceContext.For(new PlanDocument { Summary = "Tests written alongside or before implementation." }), "Tests written alongside or before implementation")
            .Category.Should().NotBe(SourceContentCategory.PotentialMappingGap);
    }

    // ── Tasks ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Task_parallel_statement_is_a_gap_but_prose_of_an_extracted_task_is_not()
    {
        const string statement = "US1, US2, US3, US4 can all start in parallel after Phase 2 completes";
        One(UnmappedSourceContext.For(new TaskTree()), statement, "BulletItem").Category.Should().Be(SourceContentCategory.PotentialMappingGap);

        const string prose = "Checkpoint: search returns only authorised children";
        var tree = new TaskTree { Roots = [new TaskNode { Title = "Search", NodeType = TaskNodeType.Task, TaskId = "T010", RawText = "T010 Search. " + prose }] };
        One(UnmappedSourceContext.For(tree), prose).Category.Should().NotBe(SourceContentCategory.PotentialMappingGap, "the text belongs to an extracted task");
        One(UnmappedSourceContext.For(tree), "- [ ] T010 Search", "BulletItem").AlreadyRepresented.Should().BeTrue("T010 is a structured task");
    }

    // ── Data Model ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static DataModelDocument Model(bool withRelationship, bool withIndex = false) => new()
    {
        Entities = [new DataEntity { Name = "Person" }, new DataEntity { Name = "BarnIAndrelinjeBarnevernet" }],
        Relationships = withRelationship ? [new DataRelationship { Source = "BarnIAndrelinjeBarnevernet.PersonId", Target = "Person.PersonId", RelationshipType = "1:1" }] : [],
        Indexes = withIndex ? [new DataIndex { Name = "IX_Person_Navn", EntityName = "Person", Columns = ["Navn"] }] : [],
    };

    [Fact]
    public void Data_model_relationship_is_a_gap_only_when_not_structured()
    {
        const string text = "Person —(1:1)→ BarnIAndrelinjeBarnevernet";
        One(UnmappedSourceContext.For(Model(withRelationship: true)), text, "FencedCodeLine").Should()
            .Match<ClassifiedSourceBlock>(b => b.Category != SourceContentCategory.PotentialMappingGap && b.AlreadyRepresented && b.PotentialKind == "Relationship");
        var gap = One(UnmappedSourceContext.For(Model(withRelationship: false)), text, "FencedCodeLine");
        gap.Category.Should().Be(SourceContentCategory.PotentialMappingGap);
        gap.PotentialKind.Should().Be("Relationship");
    }

    [Fact]
    public void Data_model_index_and_seed_rules()
    {
        const string index = "Full-text index on `Person.Navn` added via `migrationBuilder.Sql(...)`";
        One(UnmappedSourceContext.For(Model(false)), index, "BulletItem").PotentialKind.Should().Be("Index");
        One(UnmappedSourceContext.For(Model(false, withIndex: true)), index, "BulletItem").Category.Should().NotBe(SourceContentCategory.PotentialMappingGap);
        One(UnmappedSourceContext.For(Model(false)), "Initial migration seeds all reference data", "BulletItem").PotentialKind.Should().Be("Reference data");
        One(UnmappedSourceContext.For(Model(false)), "All reference data is domain-local configuration stored as DB rows.", section: "Reference Data")
            .Category.Should().Be(SourceContentCategory.Informational, "describing reference data is not a seed rule");
    }

    // ── Constitution ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constitution_rule_id_mentions_are_references_when_the_rules_exist()
    {
        const string text = "Platform Principles (PP-01 through PP-09)";
        var withRules = new ConstitutionDocument { RuleCatalog = [new ConstitutionRule { RuleId = "PP-01" }, new ConstitutionRule { RuleId = "PP-09" }] };
        var block = One(UnmappedSourceContext.For(withRules), text);
        block.Category.Should().Be(SourceContentCategory.Reference);
        block.AlreadyRepresented.Should().BeTrue();
        One(UnmappedSourceContext.For(new ConstitutionDocument()), text).Category.Should().Be(SourceContentCategory.PotentialMappingGap);
        One(UnmappedSourceContext.For(withRules), "Version change: 1.0.0 → 1.0.1").Category.Should().Be(SourceContentCategory.ChangeHistory);
    }

    // ── Specification ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Specification_flags_only_strong_candidates()
    {
        var empty = UnmappedSourceContext.For(new SpecTree());
        One(empty, "FR-099: The system MUST mask national IDs").PotentialKind.Should().Be("Requirement");
        One(empty, "Given a caseworker without clearance, when they search, then no Kode 6 child is returned").PotentialKind.Should().Be("Scenario");
        One(empty, "Q: Should deactivated records be searchable?").PotentialKind.Should().Be("Clarification");
        One(empty, "A caseworker needs to quickly locate a child they are responsible for.").Category.Should().Be(SourceContentCategory.Informational);

        var tree = new SpecTree { Roots = [new SpecNode { Title = "FR-099", NodeType = SpecNodeType.Requirement, SpecItemId = "FR-099" }] };
        One(UnmappedSourceContext.For(tree), "FR-099: The system MUST mask national IDs").AlreadyRepresented.Should().BeTrue();
    }

    // ── Grouping and counts ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Grouping_keeps_ids_text_lines_and_order_and_counts_add_up()
    {
        var blocks = new[]
        {
            Note(10, "Build the Person Module.", section: "Summary"), Note(11, "It exposes GraphQL and REST.", section: "Summary"),
            Note(13, "It enforces Kode 6/7 invisibility.", section: "Summary"),
            Note(20, "```", "FencedCodeStart", "Notes"), Note(21, "Unrelated note.", section: "Notes"),
        };
        var content = UnmappedSourceContent.Classify(blocks, Generic);

        content.Counts.Sum(c => c.Count).Should().Be(content.Total).And.Be(blocks.Length);
        content.Blocks.Select(b => b.Block).Should().Equal(blocks, "classification never changes or reorders the evidence");
        var groups = content.GroupsOf(SourceContentCategory.Informational);
        groups.Should().HaveCount(2);
        groups[0].Section.Should().Be("Summary");
        groups[0].Blocks.Select(b => b.Block.BlockId).Should().Equal(blocks[0].BlockId, blocks[1].BlockId, blocks[2].BlockId);
        (groups[0].StartLine, groups[0].EndLine).Should().Be((10, 13));
        groups[0].Title.Should().Be("Explanatory prose · Summary");
        groups[0].Preview.Should().StartWith("Build the Person Module. It exposes GraphQL and REST.");
    }

    [Fact]
    public void Real_parse_yields_section_context_from_authored_headings()
    {
        var plan = new PlanAnalysisService().Parse("# Plan\n\n## Rollout Notes\n\nSome loose prose here.\n");
        plan.UnmappedSourceBlocks.Should().Contain(b => b.Text == "Some loose prose here." && b.Section == "Rollout Notes");
    }
}
