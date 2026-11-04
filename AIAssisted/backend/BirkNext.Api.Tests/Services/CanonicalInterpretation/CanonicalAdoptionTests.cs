using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.DeliveryReporting;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.CanonicalInterpretation;

/// <summary>
/// Phase 3 adoption (correlation/3): the Phase 1 work-item interpretation persisted canonically per feature pack (A–F unchanged), requirement ids scoped
/// to their pack (FR-001 never merged across packs; tests resolved to the pack their module names, else one shared ambiguity), the one shared review
/// queue (decisions vs suggestions, one item per question, propagation through the shared registry) and the end-to-end trace built from relations only.
/// </summary>
public sealed class CanonicalAdoptionTests
{
    const string Delivery = "delivery:d1", Snap = "11111111-1111-1111-1111-111111111111";
    static CorrelationResult Run(CorrelationInput input) => CorrelationRules.Correlate(input);
    static CorrelationInput In() => CorrelationInput.Empty("proj");

    static WorkItemTaskInput T(string key, string text, DeliveryTaskRelationship rel, string? group, string? linked, string rule, string? reference = null, bool done = false) =>
        new(key, text, done, "Phase", rel, group, linked, rule, reference);

    static readonly WorkItemStructureInput SkoleService = new("SkoleService", "001-rapport", "Motta rapport",
        [new("FR-001", "Systemet MÅ lagre fravær"), new("FR-002", "Systemet MÅ validere rapporten")],
        [new("US1", "Motta rapport", false, false), new("US2", "Gammel rapportvisning", false, true)],
        [
            T("T001", "Opprett prosjekt", DeliveryTaskRelationship.SharedFoundational, "shared", null, "WI-SHARED"),
            T("T010", "Implement `FravaersRapportHandler`", DeliveryTaskRelationship.ExplicitStory, "US1", "US1", "WI-EXPLICIT", "[US1]", true),
            T("T011", "Vis gammel rapport", DeliveryTaskRelationship.RemovedStoryReference, "US2", null, "WI-REMOVED", "[US2]"),
            T("T012", "Ukjent historie", DeliveryTaskRelationship.UnresolvedReference, "US6", null, "WI-UNRESOLVED", "[US6]"),
        ], Delivery);

    static readonly WorkItemStructureInput Autorisasjon = new("AutorisasjonService", "002-tilgang", "Tilgang",
        [new("FR-001", "Systemet MÅ styre tilgang")], [],
        [T("T020", "Tilgangsgruppe", DeliveryTaskRelationship.FunctionalGroup, "US1", null, "WI-FUNCTIONAL", "[US1]")], Delivery);

    static readonly WorkItemStructureInput Hendelse = new("HendelseService", "003-hendelser", "Hendelser", [],
        [new("S1", "Mottak av hendelse", true, false)],
        [T("T030", "Behandle hendelse", DeliveryTaskRelationship.ScenarioGroup, "US1", "S1", "WI-SCENARIO", "[US1]")], Delivery);

    static readonly SourceSymbolInput[] Symbols =
    [
        new("m2lb", "src/SkoleService/FravaersRapportHandler.cs", "FravaersRapportHandler", "M2LB.SkoleService.Api.Rapport", "Class", null, null, [], [], "M2LB.SkoleService.Api", Snap),
        new("m2lb", "src/SkoleService/FravaersRapportHandler.cs", "M2LB.SkoleService.Api.Rapport.FravaersRapportHandler.Post", null, "Controller", "POST", "/api/rapport", [], [], "M2LB.SkoleService.Api", Snap),
    ];

    static CorrelationInput Project() => In() with
    {
        Structures = [SkoleService, Autorisasjon, Hendelse],
        Tasks = [new("SkoleService", "001-rapport", "T010", "Implement `FravaersRapportHandler`", Delivery)],
        Symbols = Symbols,
        Tests =
        [
            new("m2lb", "tests/M2LB.SkoleService.Tests/RapportTests.cs", "RapportTests.Lagrer_fravaer", "RapportTests", ["FR-001"], [], Snap, "m2lb|M2LB.SkoleService.Tests|RapportTests.Lagrer_fravaer"),
            new("m2lb", "tests/Shared/ContractTests.cs", "ContractTests.Fr001", "ContractTests", ["FR-001"], [], Snap, "m2lb|Shared.Tests|ContractTests.Fr001"),
            new("m2lb", "tests/M2LB.SkoleService.Tests/ValideringTests.cs", "ValideringTests.Valid", null, ["FR-002"], [], Snap),
            new("m2lb", "tests/M2LB.SkoleService.Tests/RapportApiTests.cs", "RapportApiTests.Post", null, ["ApiOperation|m2lb.skoleservice.api|POST /api/rapport"], [], Snap),
        ],
    };

    // ---- A–F persisted: classifications never collapse into "user story" ----

    [Fact]
    public void The_phase_one_interpretation_is_persisted_per_feature_pack_with_distinct_work_item_types()
    {
        var r = Run(Project());
        r.Entities.Should().Contain(e => e.Type == CanonicalEntityType.SharedFoundationGroup && e.IdentityKey == "skoleservice/001-rapport/shared");
        r.Entities.Should().Contain(e => e.Type == CanonicalEntityType.FunctionalGroup && e.IdentityKey == "autorisasjonservice/002-tilgang/group:US1");
        r.Entities.Should().Contain(e => e.Type == CanonicalEntityType.ScenarioGroup && e.IdentityKey == "hendelseservice/003-hendelser/group:US1");
        r.Entities.Should().NotContain(e => e.Type == CanonicalEntityType.UserStory && e.IdentityKey.StartsWith("autorisasjonservice/"), "a functional group is never a user story");
        r.Relations.Should().Contain(x => x.SourceKey == "Task|skoleservice/001-rapport/t001" && x.TargetKey == "SharedFoundationGroup|skoleservice/001-rapport/shared"
            && x.Explanation.Contains("not a missing user story"));
        r.Relations.Should().Contain(x => x.SourceKey == "Task|skoleservice/001-rapport/t010" && x.TargetKey == "UserStory|skoleservice/001-rapport/US1"
            && x.Type == CanonicalRelationType.BelongsTo && x.Resolution == RelationResolution.Explicit);
        r.Relations.Should().ContainSingle(x => x.SourceKey == "Task|skoleservice/001-rapport/t011").Which.Should().Match<CanonicalRelationFact>(x =>
            x.Resolution == RelationResolution.Unresolved && x.TargetKey == "UserStory|skoleservice/001-rapport/US2", "a removed story reference stays explicit and visible");
        r.Relations.Should().NotContain(x => x.SourceKey == "Task|skoleservice/001-rapport/t012" && x.Type != CanonicalRelationType.Contains, "[US6] is never re-pointed");
        r.Entities.Single(e => e.Key == "Task|skoleservice/001-rapport/t012").Attribute("relationship").Should().Be("UnresolvedReference");
        r.Entities.Single(e => e.Key == "Task|skoleservice/001-rapport/t010").Attribute("completed").Should().Be("true");
    }

    // ---- feature-pack requirement identity ----

    [Fact]
    public void The_same_requirement_id_in_two_feature_packs_stays_two_requirements_and_tests_link_to_their_own_pack()
    {
        var r = Run(Project());
        r.Entities.Where(e => e.Type == CanonicalEntityType.Requirement && e.Attribute("id") == "FR-001").Select(e => e.IdentityKey)
            .Should().BeEquivalentTo(["skoleservice/001-rapport/FR-001", "autorisasjonservice/002-tilgang/FR-001"]);
        r.Entities.Should().NotContain(e => e.Key == "Requirement|FR-001", "no unscoped requirement is created when the packs define the id");

        var verifies = r.Relations.Where(x => x.Type == CanonicalRelationType.Verifies).ToList();
        verifies.Should().ContainSingle(x => x.SourceKey.Contains("Lagrer_fravaer")).Which.Should().Match<CanonicalRelationFact>(x =>
            x.TargetKey == "Requirement|skoleservice/001-rapport/FR-001" && x.Resolution == RelationResolution.Explicit && x.Explanation.Contains("module SkoleService"));
        verifies.Where(x => x.SourceKey.Contains("ContractTests.Fr001")).Should().HaveCount(2).And.OnlyContain(x =>
            x.Resolution == RelationResolution.Ambiguous && x.RuleId == CorrelationRules.TestScopeAmbiguityRule, "a test that does not say which pack is one decision, not coverage");
        verifies.Should().ContainSingle(x => x.SourceKey.Contains("ValideringTests")).Which.TargetKey.Should().Be("Requirement|skoleservice/001-rapport/FR-002",
            "an id only one pack defines is that pack's");
    }

    [Fact]
    public void Without_a_delivery_structure_explicit_links_keep_the_unscoped_phase_two_behaviour()
    {
        var r = Run(In() with { Tests = [new("m2lb", "t.cs", "T.A", null, ["FR-001"], [], Snap)] });
        r.Relations.Should().ContainSingle(x => x.Type == CanonicalRelationType.Verifies).Which.TargetKey.Should().Be("Requirement|FR-001");
    }

    // ---- shared review queue ----

    static readonly ReviewDecisionRequest Confirm = new(ReviewTargetKind.Relation, "", ReviewDecisionOutcome.Confirmed, "tester");

    sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    static (CanonicalInterpretationDbContext Db, CanonicalInterpretationService Service) Store()
    {
        var db = new CanonicalInterpretationDbContext(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"adopt-{Guid.NewGuid():N}").Options);
        return (db, new CanonicalInterpretationService(db, new NoSources(), TimeProvider.System));
    }

    [Fact]
    public async Task The_review_queue_asks_each_question_once_separates_decisions_from_suggestions_and_honours_shared_decisions()
    {
        var (db, service) = Store();
        await using var _ = db;
        var input = Project() with
        {
            Tests = [.. Project().Tests, new("m2lb", "tests/M2LB.SkoleService.Tests/Navn.cs", "Navn.Rapport", null, [], ["FR-002"], Snap)],
            Systems = [new(CanonicalEntityType.System, "Skoletjenesten", ["Skole"], "profile"), new(CanonicalEntityType.System, "SkoleAdapter", ["Skole"], "pack")],
        };
        await service.PersistAsync("proj", Run(input), [], [], "first", default);

        var queue = await service.ReviewQueueAsync("proj", null, null, 0, 50, default);
        queue.DecisionsByType.Should().Contain(new KeyValuePair<ReviewItemType, int>(ReviewItemType.IdentityConflict, 1), "the conflicting alias is one question, not two");
        queue.DecisionsByType[ReviewItemType.TestLink].Should().Be(1, "the test that names FR-001 without saying which pack is ONE question with two alternatives");
        queue.DecisionsByType[ReviewItemType.StoryReference].Should().Be(2, "the removed-story reference and the unresolved [US6]");
        queue.SuggestionsByType[ReviewItemType.TestLink].Should().Be(1);
        queue.Decisions.Should().Be(4);
        queue.Page.Items.First().Type.Should().Be(ReviewItemType.IdentityConflict, "conflicts rank first");
        var conflict = queue.Page.Items.First();
        conflict.AffectedConsumers.Should().Contain(["Integration Topology", "Integration Quality Review", "Project Evidence"]);
        var unresolved = queue.Page.Items.Single(i => i.Subject?.IdentityKey == "skoleservice/001-rapport/t012");
        unresolved.Should().Match<ReviewItemView>(i => i.TargetKey == null && i.Resolution == CanonicalReviewQueue.StoryReferenceResolution && i.AffectedConsumers.Contains("Delivery"));

        // One shared decision answers the question everywhere: confirm the ambiguous link to the SkoleService pack.
        var question = queue.Page.Items.Single(i => i.Type == ReviewItemType.TestLink && i.Kind == ReviewItemKind.Decision);
        question.Candidates.Should().HaveCount(2).And.OnlyContain(c => c.Source.DisplayName == "ContractTests.Fr001");
        var ambiguous = question.Candidates!.Single(c => c.Target.IdentityKey.StartsWith("skoleservice/"));
        await service.DecideAsync("proj", Confirm with { TargetKey = ambiguous.RelationKey }, null, default);
        var after = await service.ReviewQueueAsync("proj", ReviewItemKind.Decision, null, 0, 50, default);
        after.Decisions.Should().Be(3, "choosing one alternative answers the whole question");
        after.Page.Items.Should().NotContain(i => i.ReviewItemId == question.ReviewItemId);
        (await service.SummaryAsync("proj", default)).Decisions.Should().Be(4, "the summary reads the interpretation snapshot, which the decision does not rewrite");

        // Reinterpreting unchanged evidence keeps the decision (reviewed state survives) and the summary reflects it.
        await service.PersistAsync("proj", Run(input), [], [], "again", default);
        (await service.ReviewQueueAsync("proj", ReviewItemKind.Decision, null, 0, 50, default)).Decisions.Should().Be(3);
        (await service.SummaryAsync("proj", default)).Decisions.Should().Be(3);
    }

    // ---- end-to-end trace ----

    [Fact]
    public async Task The_trace_follows_requirement_story_task_implementation_operation_and_tests_and_reports_gaps_instead_of_guessing()
    {
        var (db, service) = Store();
        await using var _ = db;
        await service.PersistAsync("proj", Run(Project()), [], [], "first", default);

        var summary = await service.TraceSummaryAsync("proj", default);
        summary.Should().Match<TraceSummaryView>(s => s.Features == 3 && s.Requirements == 3 && s.UserStories == 2 && s.Scenarios == 1 && s.FunctionalGroups == 1
            && s.ScenarioGroups == 1 && s.Tasks == 6 && s.SharedFoundationTasks == 1 && s.TasksWithImplementation == 1 && s.UnresolvedStoryReferences == 1
            && s.RemovedStoryReferences == 1);
        summary.RequirementsWithExplicitTests.Should().Be(2, "SkoleService FR-001 (module-scoped) and FR-002 (only pack)");
        summary.RequirementsWithCandidateTestsOnly.Should().Be(1, "Autorisasjon FR-001 has only the ambiguous link");
        summary.RequirementsWithoutTests.Should().Be(0);

        var rows = await service.TraceFeaturesAsync("proj", "SkoleService", default);
        var row = rows.Should().ContainSingle().Subject;
        row.Should().Match<TraceFeatureRow>(x => x.Requirements == 2 && x.Tasks == 4 && x.TasksWithImplementation == 1 && x.StoryReferenceIssues == 2);

        var detail = (await service.TraceFeatureAsync("proj", row.Feature.Id, default))!;
        var story = detail.WorkItems.Single(w => w.Kind == "User story");
        var task = story.Tasks.Single();
        task.Implementation.Should().ContainSingle().Which.Relation.Should().Match<CanonicalRelationView>(x => x.Counts && x.RuleId == CorrelationRules.TaskSymbolRule);
        task.Operations.Should().ContainSingle().Which.Entity.DisplayName.Should().Be("POST /api/rapport");
        task.Tests.Should().ContainSingle().Which.Entity.DisplayName.Should().Be("RapportApiTests.Post");
        detail.WorkItems.Should().Contain(w => w.Kind == "Shared & foundational" && w.Tasks.Count == 1);
        detail.WorkItems.Single(w => w.Kind == "User story (removed)").Tasks.Should().ContainSingle(t => t.Task.IdentityKey.EndsWith("/t011"),
            "a task still naming the removed story is shown under it");
        detail.WorkItems.Single(w => w.Kind == "Unresolved story references").Tasks.Should().ContainSingle(t => t.Task.IdentityKey.EndsWith("/t012"));
        detail.Requirements.Single(r => r.Requirement.IdentityKey.EndsWith("FR-001")).Tests.Should().ContainSingle(t => t.Relation.Counts);
        detail.Gaps.Should().Contain(g => g.Contains("have no linked implementation evidence") && g.Contains("checked box"));
    }

    // ---- the reader applies the one Phase 1 engine to stored Delivery rows ----

    [Fact]
    public async Task The_delivery_reader_classifies_stored_tasks_with_the_shared_work_item_engine()
    {
        await using var delivery = new DeliveryReportingDbContext(new DbContextOptionsBuilder<DeliveryReportingDbContext>().UseInMemoryDatabase($"dr-{Guid.NewGuid():N}").Options);
        var snapshot = Guid.NewGuid();
        var withStories = new DeliveryFeatureSnapshotRecord { Id = Guid.NewGuid(), SnapshotId = snapshot, Module = "SkoleService", FeatureKey = "001", Title = "Rapport", SpecPath = "spec.md",
            RequirementsJson = """[{"key":"FR-001","text":"Lagre"}]""" };
        var groups = new DeliveryFeatureSnapshotRecord { Id = Guid.NewGuid(), SnapshotId = snapshot, Module = "AutorisasjonService", FeatureKey = "002", Title = "Tilgang", SpecPath = "spec.md" };
        delivery.Snapshots.Add(new DeliverySnapshotRecord { Id = snapshot, ProjectId = Guid.NewGuid(), ReportSourceId = Guid.NewGuid(), Label = "x" });
        delivery.FeatureSnapshots.AddRange(withStories, groups);
        delivery.UserStorySnapshots.Add(new DeliveryUserStorySnapshotRecord { Id = Guid.NewGuid(), SnapshotId = snapshot, FeatureSnapshotId = withStories.Id, StoryKey = "US1", Title = "Motta", Kind = "UserStory" });
        delivery.TaskSnapshots.AddRange(
            new DeliveryTaskSnapshotRecord { Id = Guid.NewGuid(), SnapshotId = snapshot, FeatureSnapshotId = withStories.Id, TaskKey = "k1", TaskId = "T001", Text = "Oppsett", Position = 1 },
            new DeliveryTaskSnapshotRecord { Id = Guid.NewGuid(), SnapshotId = snapshot, FeatureSnapshotId = withStories.Id, TaskKey = "k2", TaskId = "T002", Text = "Motta", UserStory = "US1", Position = 2 },
            new DeliveryTaskSnapshotRecord { Id = Guid.NewGuid(), SnapshotId = snapshot, FeatureSnapshotId = withStories.Id, TaskKey = "k3", TaskId = "T003", Text = "Ukjent", UserStory = "US6", Position = 3 },
            new DeliveryTaskSnapshotRecord { Id = Guid.NewGuid(), SnapshotId = snapshot, FeatureSnapshotId = groups.Id, TaskKey = "k4", TaskId = "T004", Text = "Gruppe", UserStory = "US2", Position = 1 });
        await delivery.SaveChangesAsync();

        var structures = await new DeliveryCanonicalWorkItemReader(delivery).StructuresAsync(snapshot, default);
        var rapport = structures.Single(s => s.Module == "SkoleService");
        rapport.Requirements.Should().ContainSingle(r => r.Key == "FR-001");
        rapport.Tasks.Select(t => (t.TaskKey, t.Relationship, t.WorkItemRuleId)).Should().Equal(
            ("T001", DeliveryTaskRelationship.SharedFoundational, WorkItemInterpreter.SharedRule),
            ("T002", DeliveryTaskRelationship.ExplicitStory, "WI-EXPLICIT"),
            ("T003", DeliveryTaskRelationship.UnresolvedReference, "WI-UNRESOLVED"));
        structures.Single(s => s.Module == "AutorisasjonService").Tasks.Single().Relationship.Should().Be(DeliveryTaskRelationship.FunctionalGroup);
    }

    [Fact]
    public async Task Counts_parse_attributes_and_survive_postgres_jsonb_text_normalisation()
    {
        // Postgres returns jsonb as text with a space after each colon; string matching on '"relationship":"…"' counted nothing on real data.
        var (db, service) = Store();
        await using var _ = db;
        await service.PersistAsync("proj", Run(Project()), [], [], "first", default);
        foreach (var e in db.Entities.Where(e => e.EntityType == "Task" || e.EntityType == "Requirement"))
            e.AttributesJson = e.AttributesJson.Replace("\":\"", "\": \"").Replace("\",\"", "\", \"");
        await db.SaveChangesAsync();

        var summary = await service.TraceSummaryAsync("proj", default);
        summary.Should().Match<TraceSummaryView>(s => s.SharedFoundationTasks == 1 && s.UnresolvedStoryReferences == 1 && s.RemovedStoryReferences == 1 && s.Requirements == 3);
        (await service.ReviewQueueAsync("proj", ReviewItemKind.Decision, ReviewItemType.StoryReference, 0, 10, default)).Page.Items
            .Should().Contain(i => i.Subject != null && i.Subject.IdentityKey.EndsWith("/t012"));
        CanonicalInterpretationService.Attr("""{"relationship": "SharedFoundational"}""", "relationship").Should().Be("SharedFoundational");
        CanonicalInterpretationService.Attr("not json", "relationship").Should().BeNull();
    }

    [Theory]
    [InlineData("- **FR-001**: The system MUST store absence", "FR-001", "The system MUST store absence")]
    [InlineData("FR-001: The system MUST store absence", "FR-001", "The system MUST store absence")]
    [InlineData("The system MUST store absence", "FR-001", "The system MUST store absence")]
    [InlineData("FR-001", "FR-001", "FR-001")]
    public void Requirement_display_text_drops_the_repeated_id_without_touching_identity(string text, string key, string expected) =>
        CorrelationRules.RequirementText(text, key).Should().Be(expected);

    [Fact]
    public void Review_classification_is_one_shared_rule()
    {
        CanonicalReviewQueue.Classify(CorrelationRules.TestCandidateRule, RelationResolution.Candidate, RelationReviewState.NotReviewed, true)!.Value.Kind.Should().Be(ReviewItemKind.Suggestion);
        CanonicalReviewQueue.Classify(CorrelationRules.TestScopeAmbiguityRule, RelationResolution.Ambiguous, RelationReviewState.NotReviewed, true)!.Value
            .Should().Be((ReviewItemKind.Decision, ReviewItemType.TestLink, 3));
        CanonicalReviewQueue.Classify(CorrelationRules.TestExplicitRule, RelationResolution.Explicit, RelationReviewState.Conflict, true)!.Value.Type.Should().Be(ReviewItemType.IdentityConflict);
        CanonicalReviewQueue.Classify(CorrelationRules.IntentRule, RelationResolution.Candidate, RelationReviewState.Rejected, true).Should().BeNull("a rejection is remembered");
        CanonicalReviewQueue.Classify(CorrelationRules.IntentRule, RelationResolution.Candidate, RelationReviewState.NotReviewed, false).Should().BeNull("inactive relations are not questions");
        CanonicalReviewQueue.Classify(CorrelationRules.TestExplicitRule, RelationResolution.Explicit, RelationReviewState.NotReviewed, true).Should().BeNull();
    }
}
