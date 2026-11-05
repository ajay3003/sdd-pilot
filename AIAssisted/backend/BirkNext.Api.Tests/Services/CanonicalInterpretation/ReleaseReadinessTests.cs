using System.Text.Json;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Governance;
using BirkNext.Integrations;
using BirkNext.Interpretation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.CanonicalInterpretation;

/// <summary>
/// Release readiness (release/1): a decision from separate dimensions (never a checklist percentage), Not assessed instead of invented execution
/// results, module-scoped, frozen reports that later evidence never changes, and sign-offs recorded beside the report without changing it.
/// </summary>
public sealed class ReleaseReadinessTests
{
    sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    static ReadinessDimension D(ReadinessDimensionKey key, DimensionApplicability a, GateDecision state) => new(key, key.ToString(), a, state, "", [], null, null);

    [Fact]
    public void The_decision_follows_required_dimensions_and_never_turns_not_assessed_into_ready()
    {
        const DimensionApplicability R = DimensionApplicability.Required, O = DimensionApplicability.Optional, N = DimensionApplicability.NotApplicable;
        ReleaseReadinessRules.Decide([D(ReadinessDimensionKey.Requirements, R, GateDecision.Ready), D(ReadinessDimensionKey.Performance, N, GateDecision.Blocked)])
            .Decision.Should().Be(GateDecision.Ready, "Not applicable never counts");
        ReleaseReadinessRules.Decide([D(ReadinessDimensionKey.Requirements, R, GateDecision.Ready), D(ReadinessDimensionKey.Performance, O, GateDecision.NotAssessed)])
            .Should().Be((GateDecision.ReadyWithLimitations, "Limitations in performance."));
        ReleaseReadinessRules.Decide([D(ReadinessDimensionKey.TestDesign, R, GateDecision.NotAssessed), D(ReadinessDimensionKey.Requirements, R, GateDecision.ReadyWithLimitations)])
            .Decision.Should().Be(GateDecision.NotAssessed);
        ReleaseReadinessRules.Decide([D(ReadinessDimensionKey.Governance, R, GateDecision.Blocked), D(ReadinessDimensionKey.TestDesign, R, GateDecision.NotAssessed)])
            .Should().Be((GateDecision.Blocked, "Blocked by governance."));
        ReleaseReadinessRules.Decide([D(ReadinessDimensionKey.Governance, R, GateDecision.PassedWithException)]).Decision
            .Should().Be(GateDecision.ReadyWithLimitations, "an accepted exception is a limitation, never plain Ready");
    }

    static readonly DateTimeOffset Now = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    static async Task<(CanonicalInterpretationDbContext Db, CanonicalInterpretationService Service, Dictionary<string, Guid> Ids)> Project(bool conflict = true)
    {
        var db = new CanonicalInterpretationDbContext(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"rel-{Guid.NewGuid():N}").Options);
        var snapshot = Guid.NewGuid();
        db.Snapshots.Add(new InterpretationSnapshotRecord { Id = snapshot, Scope = "proj", CreatedAt = Now });
        var ids = new Dictionary<string, Guid>();
        void Entity(CanonicalEntityType type, string id, string name, params (string, string)[] attributes)
        {
            ids[id] = Guid.NewGuid();
            db.Entities.Add(new CanonicalEntityRecord { Id = ids[id], Scope = "proj", EntityType = type.ToString(), IdentityKey = id, DisplayName = name,
                AttributesJson = JsonSerializer.Serialize(attributes.ToDictionary(a => a.Item1, a => a.Item2)) });
            db.SnapshotEntities.Add(new InterpretationSnapshotEntityRecord { SnapshotId = snapshot, EntityId = ids[id], DisplayName = name });
        }
        void Relation(string s, CanonicalRelationType t, string target, RelationResolution r, string rule = "CR-X") =>
            db.Relations.Add(new CanonicalRelationRecord { Id = Guid.NewGuid(), Scope = "proj", SourceEntityId = ids[s], TargetEntityId = ids[target], RelationType = t.ToString(),
                RelationKey = $"{s}-{t}-{target}", Resolution = r.ToString(), ReviewState = "NotReviewed", RuleId = rule });
        (string, string) Skole = ("module", "SkoleService"), Person = ("module", "PersonService");
        Entity(CanonicalEntityType.Feature, "skoleservice/001-rapport", "001 Motta rapport", Skole);
        Entity(CanonicalEntityType.Feature, "personservice/001-person", "001 Person", Person);
        Entity(CanonicalEntityType.Requirement, "fr1", "FR-001", Skole);
        Entity(CanonicalEntityType.Requirement, "fr2", "FR-002", Skole);
        Entity(CanonicalEntityType.Requirement, "p1", "Person FR-001", Person);
        Entity(CanonicalEntityType.Task, "t1", "T001", Skole, ("relationship", "Story"), ("completed", "true"), ("id", "T001"));
        Entity(CanonicalEntityType.Task, "t2", "T002", Skole, ("relationship", "RemovedStoryReference"), ("completed", "true"), ("id", "T002"), ("storyReference", "US3"));
        Entity(CanonicalEntityType.Test, "test1", "Fr001Tests");
        Entity(CanonicalEntityType.SourceSymbol, "sym", "RapportHandler");
        Entity(CanonicalEntityType.Module, "skoleservice", "SkoleService");
        Entity(CanonicalEntityType.System, "skole-system", "SkoleService");
        Entity(CanonicalEntityType.System, "other", "Skoletjenesten");
        Relation("test1", CanonicalRelationType.Verifies, "fr1", RelationResolution.Explicit);
        Relation("t1", CanonicalRelationType.References, "sym", RelationResolution.Derived, CorrelationRules.TaskSymbolRule);
        if (conflict) Relation("other", CanonicalRelationType.CandidateSameAs, "skole-system", RelationResolution.Ambiguous, CorrelationRules.AliasConflictRule);
        await db.SaveChangesAsync();
        return (db, new CanonicalInterpretationService(db, new NoSources(), TimeProvider.System), ids);
    }

    [Fact]
    public async Task A_module_assessment_reads_its_own_evidence_and_never_counts_checked_tasks_as_implementation()
    {
        var (_, service, _) = await Project();
        var skole = await service.AssessReleaseAsync("proj", ["SkoleService"], "Skolenærvær 1.4", default);
        var dims = skole.Dimensions.ToDictionary(d => d.Key);

        skole.ScopeReference.Name.Should().Be("Skolenærvær 1.4");
        skole.ScopeReference.Modules.Should().Equal("SkoleService");
        skole.ScopeReference.InterpretationId.Should().NotBeNull("the assessment names the exact interpretation it read");
        dims[ReadinessDimensionKey.ScopeClarity].Summary.Should().StartWith("1 feature pack in scope");
        dims[ReadinessDimensionKey.Requirements].State.Should().Be(GateDecision.Ready);
        dims[ReadinessDimensionKey.Traceability].State.Should().Be(GateDecision.ReadyWithLimitations);
        dims[ReadinessDimensionKey.Traceability].Evidence.Should().ContainSingle(e => e.Contains("T002 → US3"));
        dims[ReadinessDimensionKey.Implementation].Summary.Should().Contain("1 of 2 tasks name source code").And.Contain("2 tasks are checked")
            .And.Contain("a checked task is not implementation, test or release evidence");
        dims[ReadinessDimensionKey.TestDesign].Summary.Should().Be("1 of 2 requirements have a confirmed test · 0 only a suggested test (not counted) · 1 none.");
        dims[ReadinessDimensionKey.Governance].State.Should().Be(GateDecision.Blocked, "the SkoleService system has an identity conflict");
        dims[ReadinessDimensionKey.Performance].Should().Match<ReadinessDimension>(d => d.State == GateDecision.NotAssessed && d.Applicability == DimensionApplicability.Optional
            && d.Href == "performance-test-review", "no execution result is invented");
        skole.Decision.Should().Be(GateDecision.Blocked);
        skole.Blockers.Should().ContainSingle(b => b.StartsWith("Identity conflict"));
        skole.RuleSetVersion.Should().Be("release/1");
        skole.AvailableModules.Should().Equal(["SkoleService"], "declared module names, for modules with feature packs (PersonService has no Module entity here)");
        skole.Facts.Should().BeEquivalentTo(new ReleaseReadinessFacts(1, 2, 1, 0, 1, 2, 1, skole.Facts!.BlockingFindings, skole.Facts.Warnings, skole.Facts.OpenDecisions),
            "the counts behind the dimensions: 2 requirements (1 confirmed test, 1 none), 2 tasks (1 names code)");
        skole.Facts.BlockingFindings.Should().BeGreaterThan(0, "the identity conflict is a blocking finding");
        dims[ReadinessDimensionKey.FunctionalExecution].Summary.Should().Contain("a linked test is not an executed or passed test");

        var person = await service.AssessReleaseAsync("proj", ["PersonService"], null, default);
        person.Decision.Should().Be(GateDecision.ReadyWithLimitations, "SkoleService's conflict and story references never block the PersonService release");
        person.Dimensions.Single(d => d.Key == ReadinessDimensionKey.Traceability).State.Should().Be(GateDecision.Ready);
    }

    [Fact]
    public async Task Nothing_interpreted_or_nothing_in_scope_is_not_assessed()
    {
        var empty = new CanonicalInterpretationService(new CanonicalInterpretationDbContext(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>()
            .UseInMemoryDatabase($"rel-empty-{Guid.NewGuid():N}").Options), new NoSources(), TimeProvider.System);
        var nothing = await empty.AssessReleaseAsync("proj", null, null, default);
        nothing.Decision.Should().Be(GateDecision.NotAssessed);
        nothing.Facts.Should().BeNull("without an analysis no count is known — never shown as 0");
        nothing.Dimensions.Should().ContainSingle().Which.Summary.Should().StartWith("Required relationship evidence has not been prepared");
        var (_, service, _) = await Project();
        var none = await service.AssessReleaseAsync("proj", ["NoSuchModule"], null, default);
        none.Decision.Should().Be(GateDecision.NotAssessed);
        none.NextActions.Should().Contain(a => a.StartsWith("Scope:"));
    }

    [Fact]
    public async Task A_frozen_report_never_changes_and_a_sign_off_is_recorded_beside_it()
    {
        var (db, service, ids) = await Project();
        var frozen = await service.FreezeReleaseAsync("proj", new("Skolenærvær 1.4", ["SkoleService"]), default);
        frozen.Assessment.Decision.Should().Be(GateDecision.Blocked);
        // Freezing the same name over the same evidence again (double click, second reviewer) is refused.
        var duplicate = () => service.FreezeReleaseAsync("proj", new("Skolenærvær 1.4", ["SkoleService"]), default);
        await duplicate.Should().ThrowAsync<CanonicalRequestException>().WithMessage("*already freezes this scope*");

        // Later evidence: the conflict is resolved (a reviewer confirmed it) — the live assessment changes, the frozen report does not.
        var conflict = await db.Relations.SingleAsync(r => r.RuleId == CorrelationRules.AliasConflictRule);
        conflict.ReviewState = "Confirmed";
        await db.SaveChangesAsync();
        (await service.AssessReleaseAsync("proj", ["SkoleService"], null, default)).Decision.Should().NotBe(GateDecision.Blocked);
        var again = (await service.ReleaseReportAsync("proj", frozen.Id, default))!;
        again.Assessment.Decision.Should().Be(GateDecision.Blocked);
        JsonSerializer.Serialize(again.Assessment).Should().Be(JsonSerializer.Serialize(frozen.Assessment), "the frozen assessment is exactly as frozen");

        var accept = () => service.SignOffReleaseAsync("proj", frozen.Id, new(SignOffOutcome.Accepted, null), null, default);
        await accept.Should().ThrowAsync<CanonicalRequestException>().WithMessage("*accepted risk*");
        var signed = (await service.SignOffReleaseAsync("proj", frozen.Id, new(SignOffOutcome.AcceptedWithRisk, "MU naming conflict accepted for 1.4"), "test.lead", default))!;
        signed.SignOffs.Should().ContainSingle().Which.Should().Match<ReleaseSignOff>(s => s.By == "test.lead" && s.Outcome == SignOffOutcome.AcceptedWithRisk);
        signed.Assessment.Decision.Should().Be(GateDecision.Blocked, "a sign-off never changes the computed decision or its evidence");

        var list = await service.ReleaseReportsAsync("proj", 20, default);
        list.Should().ContainSingle().Which.LatestSignOff.Should().Be(SignOffOutcome.AcceptedWithRisk);
        (await service.ReleaseReportAsync("proj", Guid.NewGuid(), default)).Should().BeNull("an unknown report is not found — never the latest instead");
        (await service.ReleaseReportAsync("other", frozen.Id, default)).Should().BeNull("reports are project-scoped");
        var unnamed = () => service.FreezeReleaseAsync("proj", new(" ", null), default);
        await unnamed.Should().ThrowAsync<CanonicalRequestException>();

        // Changed evidence (the conflict was confirmed since) or another scope is a new report under the same name.
        (await service.FreezeReleaseAsync("proj", new("Skolenærvær 1.4", ["SkoleService"]), default)).Assessment.Decision.Should().NotBe(GateDecision.Blocked);
        (await service.FreezeReleaseAsync("proj", new("Skolenærvær 1.4", null), default)).Assessment.ScopeReference.Modules.Should().BeEmpty();
        (await service.ReleaseReportsAsync("proj", 20, default)).Should().HaveCount(3);
        _ = ids;
    }
}
