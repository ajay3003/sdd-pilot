using System.Text.Json;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Services.Snapshots;
using BirkNext.DeliveryReporting;
using BirkNext.Interpretation;
using BirkNext.Snapshots;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>
/// Functional Quality Scope: deterministic quality classification (technical work excluded by default), Skolenærvær direct vs related membership
/// from the declared domain flows (exact project aliases, never similarity), confirmed vs candidate capability relations, explicit
/// include/exclude precedence, stable item keys across re-imports (removed items stay visible as unavailable), canonical VERIFIES coverage
/// (never claimed for an id several features share), test-design assessments, frozen scope snapshots and their registry registration.
/// </summary>
public sealed class FunctionalQualityScopeTests
{
    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed record F(string Module, string Key, string Title, (string Key, string Title, string Kind)[] Stories, (string Key, string? Story, string Text)[] Criteria,
        (string Key, string Text)[] Requirements);

    private readonly string _name = $"fqs-{Guid.NewGuid():N}";
    private readonly Clock _clock = new(Monday);
    private DeliveryReportingDbContext Db() => new(new DbContextOptionsBuilder<DeliveryReportingDbContext>().UseInMemoryDatabase(_name).Options);
    private CanonicalInterpretationDbContext Canonical() => new(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase(_name + "-canon").Options);
    private FunctionalQualityScopeService Service(DeliveryReportingDbContext db, CanonicalInterpretationDbContext? canonical = null) =>
        new(db, _clock, [new SkoleNaervaerTopologyProfile()], canonical);

    private static readonly F SkoleService = new("SkoleService", "001-rapport", "Motta skolenærværsrapport",
        [("US1", "Skoletjenesten tar imot en rapport og lagrer fraværet", "UserStory"), ("US2", "Rapporten skal ikke lagre kode 6 personer synlig", "UserStory"), ("S1", "Mottak av gyldig rapport", "Scenario")],
        [("AC-1", "US1", "Gitt en gyldig rapport, Når den mottas, Så lagres fraværet"), ("AC-2", "US1", "Gitt en ugyldig rapport, Når den mottas, Så avvises den med feilmelding")],
        [("FR-001", "Systemet MÅ lagre fravær per elev"), ("FR-002", "Systemet MÅ kreve autorisert tilgang for saksbehandlere")]);

    private static readonly F Receiver = new("MeldingsutvekslerReceiver", "002-mottak", "Meldingsmottak",
        [("US1", "Mottaket videresender rapporten til Skoletjenesten", "UserStory")], [], [("FR-001", "Systemet MÅ videresende rapporten")]);

    private static readonly F Person = new("Person", "003-person", "Personhendelser",
        [("US1", "Personhendelser publiseres når en elev flytter", "UserStory")], [], []);

    private static readonly F SkoleAdapter = new("SkoleAdapter", "004-utdanning", "Utdanningsdata",
        [("US1", "Utdanningsdata hentes fra BiRK og sendes videre", "UserStory")], [], []);

    private static readonly F Autorisasjon = new("AutorisasjonService", "005-tilgang", "Tilgangsstyring",
        [("US1", "En saksbehandler får tilgang basert på rolle", "UserStory")], [], []);

    private static readonly F Migration = new("SkoleService", "009-mediatr-wolverine", "Migrer fra MediatR til Wolverine",
        [("US1", "Utvikler bytter håndterere", "UserStory")], [], []);

    private static async Task<(Guid Project, Guid Snapshot)> SeedAsync(DeliveryReportingDbContext db, Guid? project, DateTimeOffset effectiveAt, string label, params F[] features)
    {
        var projectId = project ?? Guid.NewGuid();
        if (project is null) db.Projects.Add(new DeliveryProjectRecord { Id = projectId, Name = "M2LB", CreatedAt = effectiveAt, UpdatedAt = effectiveAt });
        var snapshot = new DeliverySnapshotRecord { Id = Guid.NewGuid(), ProjectId = projectId, ReportSourceId = Guid.NewGuid(), Label = label, EffectiveAt = effectiveAt, ImportedAt = effectiveAt,
            Role = "Current", CoverageStatus = "Complete", ParseStatus = "Parsed" };
        db.Snapshots.Add(snapshot);
        var line = 10;
        foreach (var f in features)
        {
            var feature = new DeliveryFeatureSnapshotRecord { Id = Guid.NewGuid(), SnapshotId = snapshot.Id, Module = f.Module, FeatureKey = f.Key, Folder = f.Key, Title = f.Title,
                SpecPath = $"{f.Module}/specs/{f.Key}/spec.md", RequirementsJson = JsonSerializer.Serialize(f.Requirements.Select(r => new { key = r.Key, text = r.Text })) };
            db.FeatureSnapshots.Add(feature);
            foreach (var s in f.Stories)
                db.UserStorySnapshots.Add(new DeliveryUserStorySnapshotRecord { Id = Guid.NewGuid(), SnapshotId = snapshot.Id, FeatureSnapshotId = feature.Id, StoryKey = s.Key, Title = s.Title,
                    Kind = s.Kind, Priority = "P1", SourceLine = line++ });
            foreach (var a in f.Criteria)
                db.AcceptanceCriterionSnapshots.Add(new DeliveryAcceptanceCriterionSnapshotRecord { Id = Guid.NewGuid(), SnapshotId = snapshot.Id, FeatureSnapshotId = feature.Id,
                    StoryKey = a.Story, CriterionKey = a.Key, Text = a.Text, SourceLine = line++ });
        }
        await db.SaveChangesAsync();
        return (projectId, snapshot.Id);
    }

    private static Task<(Guid Project, Guid Snapshot)> SeedAllAsync(DeliveryReportingDbContext db) =>
        SeedAsync(db, null, Monday, "Monday", SkoleService, Receiver, Person, SkoleAdapter, Autorisasjon, Migration);

    // ── Classification ───────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Migrer håndterere fra MediatR til Wolverine", QualityCategory.Technical, "fq.technical.migration")]
    [InlineData("Rapporten skal ikke vise kode 6 personer", QualityCategory.Privacy, "fq.privacy.wording")]
    [InlineData("Systemet MÅ kreve autorisert tilgang", QualityCategory.Security, "fq.security.wording")]
    [InlineData("Responstid under 2 sekunder for søk", QualityCategory.Performance, "fq.performance.wording")]
    [InlineData("Meldingen går ikke tapt ved restart", QualityCategory.Reliability, "fq.reliability.wording")]
    [InlineData("Rapporten valideres mot XSD", QualityCategory.DataQuality, "fq.data-quality.wording")]
    [InlineData("Saksbehandleren ser fraværet per elev", QualityCategory.Functional, "fq.functional.behaviour")]
    [InlineData("", QualityCategory.Unclassified, "fq.unclassified.no-text")]
    public void Classification_is_deterministic_bilingual_and_names_its_rule(string text, QualityCategory category, string rule) =>
        FunctionalQualityClassifier.Classify(text).Should().Be((category, rule));

    [Fact]
    public void A_technical_feature_marks_its_items_technical_and_technical_is_not_a_default_category()
    {
        FunctionalQualityClassifier.Classify("Utvikler bytter håndterere", "Migrer fra MediatR til Wolverine").Should().Be((QualityCategory.Technical, "fq.technical.feature"));
        FunctionalQualityClassifier.DefaultCategories.Should().NotContain(QualityCategory.Technical).And.Contain(QualityCategory.Unclassified);
    }

    [Fact]
    public void Module_to_component_mapping_uses_exact_declared_alias_segments_only()
    {
        var profile = new SkoleNaervaerTopologyProfile();
        FunctionalQualityScopeService.ComponentOf("SkoleService", profile).Should().Be("Skoletjenesten");
        FunctionalQualityScopeService.ComponentOf("MeldingsutvekslerReceiver", profile).Should().Be("MM");
        FunctionalQualityScopeService.ComponentOf("Person", profile).Should().Be("Person");
        FunctionalQualityScopeService.ComponentOf("M2LB", profile).Should().BeNull("a segment several components share identifies none of them");
        FunctionalQualityScopeService.ComponentOf("Api", profile).Should().BeNull();
        FunctionalQualityScopeService.ComponentOf("AutorisasjonService", profile).Should().BeNull("no declared alias, and never a similarity match");
        FunctionalQualityScopeService.ComponentOf("SkoleServ", profile).Should().BeNull();
    }

    // ── Skolenærvær: direct vs related ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Skolenaervaer_direct_scope_is_the_primary_flow_and_related_needs_the_toggle()
    {
        await using var db = Db();
        var (project, _) = await SeedAllAsync(db);
        var service = Service(db);

        var available = await service.AvailableAsync(project, default);
        available[0].Should().Match<AvailableScope>(a => a.Key == "profile:skolenaervaer" && a.Kind == "Domain flow");
        available.Select(a => a.Key).Should().Contain(["module:Person", "module:AutorisasjonService"]);

        var direct = await service.ResolveAsync(project, new ScopeQuery("profile:skolenaervaer", null, IncludeRelated: false), default);
        direct.Items.Select(i => i.Module).Distinct().Should().BeEquivalentTo(["SkoleService", "MeldingsutvekslerReceiver"]);
        direct.Items.Should().OnlyContain(i => i.Relation == ScopeRelation.Direct && i.Inclusion == ScopeInclusion.Smart && i.Reason.Contains("declared domain profile"));
        direct.RelatedModules.Should().Contain(m => m.Module == "Person" && m.Relation == ScopeRelation.Upstream && m.Included)
            .And.Contain(m => m.Module == "SkoleAdapter" && m.Relation == ScopeRelation.Upstream);
        direct.RelatedModules.Should().NotContain(m => m.Module == "AutorisasjonService", "no declared flow or canonical relation connects it");

        var related = await service.ResolveAsync(project, new ScopeQuery("profile:skolenaervaer", null, IncludeRelated: true), default);
        related.Items.Select(i => i.Module).Distinct().Should().BeEquivalentTo(["SkoleService", "MeldingsutvekslerReceiver", "Person", "SkoleAdapter"]);
        related.Items.Where(i => i.Module == "Person").Should().OnlyContain(i => i.Relation == ScopeRelation.Upstream && i.Reason.Contains("Person → Skoletjenesten"));
    }

    [Fact]
    public async Task Technical_work_is_excluded_by_default_counted_and_included_on_request()
    {
        await using var db = Db();
        var (project, _) = await SeedAllAsync(db);
        var service = Service(db);

        var standard = await service.ResolveAsync(project, new ScopeQuery("module:SkoleService", null, false), default);
        standard.Items.Should().NotContain(i => i.FeatureKey == "009-mediatr-wolverine");
        standard.Summary.TechnicalExcluded.Should().Be(1);

        var withTechnical = await service.ResolveAsync(project, new ScopeQuery("module:SkoleService", null, false, IncludeTechnical: true), default);
        withTechnical.Items.Should().Contain(i => i.FeatureKey == "009-mediatr-wolverine" && i.Category == QualityCategory.Technical);
        withTechnical.Summary.TechnicalExcluded.Should().Be(0);
    }

    [Fact]
    public async Task Items_keep_their_artifact_types_and_scenarios_are_never_formal_test_cases()
    {
        await using var db = Db();
        var (project, _) = await SeedAllAsync(db);
        var r = await Service(db).ResolveAsync(project, new ScopeQuery("module:SkoleService", null, false), default);

        r.Summary.Should().Match<ScopeSummary>(s => s.UserStories == 2 && s.Scenarios == 1 && s.AcceptanceCriteria == 2 && s.Requirements == 2 && s.FormalTestCases == 0);
        r.Items.Single(i => i.DisplayId == "S1").Type.Should().Be(ScopeArtifactType.Scenario);
        FunctionalQualityScopeText.TypeLabel(ScopeArtifactType.Scenario).Should().Be("Acceptance scenario");
        r.Items.Single(i => i.DisplayId == "AC-1").ParentKey.Should().Be("story:SkoleService/001-rapport/US1");
        r.Items.Single(i => i.DisplayId == "US2").Category.Should().Be(QualityCategory.Privacy);
        r.Items.Single(i => i.DisplayId == "FR-002").Category.Should().Be(QualityCategory.Security);
        r.Items.Should().OnlyContain(i => i.Coverage == ScopeCoverage.NotAssessed);
        r.Limitations.Should().Contain(l => l.Contains("Not assessed"));
    }

    // ── Custom scopes: references only, precedence, stability ────────────────────────────────────────────────────────

    [Fact]
    public async Task Explicit_exclusion_beats_inclusion_beats_smart_membership()
    {
        await using var db = Db();
        var (project, _) = await SeedAllAsync(db);
        var service = Service(db);
        var scope = await service.SaveAsync(project, new ScopeDefinition { Name = "Person release", Roots = ["module:Person"] }, default);
        const string personStory = "story:Person/003-person/US1", skoleCriterion = "ac:SkoleService/001-rapport/AC-2";

        scope = await service.ReferenceAsync(project, scope.Id!.Value, new(skoleCriterion, ScopeInclusion.ExplicitInclude), default);
        var resolved = await service.ResolveAsync(project, new ScopeQuery(null, scope.Id, false), default);
        resolved.Items.Single(i => i.Key == skoleCriterion).Should().Match<ScopeItem>(i => i.Relation == ScopeRelation.ExplicitlyIncluded && i.Inclusion == ScopeInclusion.ExplicitInclude);
        resolved.Items.Single(i => i.Key == personStory).Inclusion.Should().Be(ScopeInclusion.Smart);

        scope = await service.ReferenceAsync(project, scope.Id!.Value, new(personStory, ScopeInclusion.ExplicitExclude), default);
        scope = await service.ReferenceAsync(project, scope.Id!.Value, new(skoleCriterion, ScopeInclusion.ExplicitExclude), default);
        scope.Version.Should().Be(4, "every reference change is a new scope version");
        resolved = await service.ResolveAsync(project, new ScopeQuery(null, scope.Id, false), default);
        resolved.Items.Should().NotContain(i => i.Key == personStory || i.Key == skoleCriterion, "an explicit exclusion overrides both smart and explicit inclusion");
        resolved.PrecedenceNote.Should().StartWith("Explicit exclusion > explicit inclusion > smart");
        (await db.ScopeReferences.CountAsync()).Should().Be(2, "one reference row per item; switching mode replaces it");

        scope = await service.RemoveReferenceAsync(project, scope.Id!.Value, personStory, default);
        (await service.ResolveAsync(project, new ScopeQuery(null, scope.Id, false), default)).Items.Should().Contain(i => i.Key == personStory && i.Inclusion == ScopeInclusion.Smart);
    }

    [Fact]
    public async Task Explicit_references_survive_reanalysis_and_removed_items_stay_visible_as_unavailable()
    {
        await using var db = Db();
        var (project, _) = await SeedAllAsync(db);
        var service = Service(db);
        var scope = await service.SaveAsync(project, new ScopeDefinition { Name = "Manual", ExplicitIncludes = ["story:SkoleService/001-rapport/US1", "story:Person/003-person/US1"] }, default);

        // A newer snapshot keeps SkoleService US1 (new database rows, same stable key) and no longer has the Person feature.
        await SeedAsync(db, project, Monday.AddDays(2), "Wednesday", SkoleService, Receiver);
        var r = await service.ResolveAsync(project, new ScopeQuery(null, scope.Id, false), default);
        r.SnapshotLabel.Should().Be("Wednesday");
        r.Items.Single(i => i.Key == "story:SkoleService/001-rapport/US1").Unavailable.Should().BeFalse();
        r.Items.Single(i => i.Key == "story:Person/003-person/US1").Should().Match<ScopeItem>(i => i.Unavailable && i.Title == "Unavailable in current source" && i.Module == "Person");
        r.Summary.Unavailable.Should().Be(1);
    }

    [Fact]
    public async Task Scopes_clone_reject_duplicate_names_and_delete_without_touching_frozen_snapshots()
    {
        await using var db = Db();
        var (project, snapshot) = await SeedAllAsync(db);
        var service = Service(db);
        var scope = await service.SaveAsync(project, new ScopeDefinition { Name = "Release 1", Roots = ["module:SkoleService"], ExplicitExcludes = ["story:SkoleService/001-rapport/US2"] }, default);

        var act = () => service.SaveAsync(project, new ScopeDefinition { Name = "Release 1" }, default);
        await act.Should().ThrowAsync<FunctionalQualityScopeException>().WithMessage("*already exists*");

        var clone = await service.CloneAsync(project, scope.Id!.Value, default);
        clone.Should().Match<ScopeDefinition>(c => c.Name == "Release 1 (copy)" && c.Id != scope.Id && c.ExplicitExcludes.SequenceEqual(scope.ExplicitExcludes) && c.Roots.SequenceEqual(scope.Roots));

        var frozen = await service.FreezeAsync(project, new ScopeQuery(null, scope.Id, false), default);
        frozen.Should().Match<ScopeSnapshotView>(f => f.ScopeId == scope.Id && f.DeliverySnapshotId == snapshot && f.ItemCount > 0 && f.RuleSetVersion.StartsWith(FunctionalQualityClassifier.RuleSetVersion));
        var frozenItems = (await db.ScopeSnapshots.SingleAsync()).ItemsJson;

        await service.ReferenceAsync(project, scope.Id!.Value, new("story:SkoleService/001-rapport/US1", ScopeInclusion.ExplicitExclude), default);
        (await service.DeleteAsync(project, scope.Id!.Value, default)).Should().BeTrue();
        var kept = await db.ScopeSnapshots.AsNoTracking().SingleAsync();
        kept.Should().Match<DeliveryScopeSnapshotRecord>(s => s.ScopeId == null && s.ScopeName == "Release 1" && s.ItemsJson == frozenItems, "frozen membership never changes");
        (await db.ScopeReferences.CountAsync(r => r.ScopeId == scope.Id)).Should().Be(0);
    }

    [Fact]
    public async Task A_frozen_scope_snapshot_registers_as_frozen_report_evidence_derived_from_its_delivery_snapshot()
    {
        await using var db = Db();
        var (project, snapshot) = await SeedAllAsync(db);
        var frozen = await Service(db).FreezeAsync(project, new ScopeQuery("profile:skolenaervaer", null, true), default);

        var items = await new DeliverySnapshotSource(db).ReadAsync(new HashSet<string>(), new HashSet<string>(), default);
        var (metadata, edges) = items.Single(i => i.Metadata.Module == "functional-scope-snapshot");
        metadata.Should().Match<SnapshotMetadata>(m => m.Id == frozen.Id.ToString() && m.Frozen && m.Retention == SnapshotRetentionClass.FrozenReportEvidence && m.Label.StartsWith("Scope Skolenærvær"));
        edges.Should().ContainSingle().Which.Should().Match<SnapshotLineageEdge>(e => e.Kind == SnapshotLineageKind.DerivedFrom && e.To.Id == snapshot.ToString());
    }

    // ── Canonical coverage and capability relations ──────────────────────────────────────────────────────────────────

    private static CanonicalEntityRecord Entity(CanonicalEntityType type, string identity, string name) =>
        new() { Id = Guid.NewGuid(), Scope = "env", EntityType = type.ToString(), IdentityKey = identity, DisplayName = name, SourceIdentity = identity };

    private static CanonicalRelationRecord Relation(CanonicalEntityRecord from, CanonicalEntityRecord to, CanonicalRelationType type, RelationResolution resolution,
        RelationReviewState review = RelationReviewState.NotReviewed) =>
        new() { Id = Guid.NewGuid(), Scope = "env", SourceEntityId = from.Id, TargetEntityId = to.Id, RelationType = type.ToString(), Resolution = resolution.ToString(), ReviewState = review.ToString(),
            RelationKey = $"{from.IdentityKey}->{type}->{to.IdentityKey}", Active = true };

    [Fact]
    public async Task Coverage_comes_from_canonical_VERIFIES_and_is_never_claimed_for_an_id_several_features_share()
    {
        await using var db = Db();
        await using var canonical = Canonical();
        var (project, _) = await SeedAllAsync(db);
        var test = Entity(CanonicalEntityType.Test, "tests/rapport", "RapportTests");
        var fr1 = Entity(CanonicalEntityType.Requirement, "FR-001", "FR-001");
        var fr2 = Entity(CanonicalEntityType.Requirement, "FR-002", "FR-002");
        var us2 = Entity(CanonicalEntityType.UserStory, "US2", "US2");
        canonical.Entities.AddRange(test, fr1, fr2, us2);
        canonical.Relations.AddRange(Relation(test, fr1, CanonicalRelationType.Verifies, RelationResolution.Explicit),
            Relation(test, fr2, CanonicalRelationType.Verifies, RelationResolution.Explicit),
            Relation(test, us2, CanonicalRelationType.Verifies, RelationResolution.Candidate));
        await canonical.SaveChangesAsync();

        var r = await Service(db, canonical).ResolveAsync(project, new ScopeQuery("profile:skolenaervaer", null, false), default);
        var byKey = r.Items.ToDictionary(i => i.Key);
        byKey["req:SkoleService/001-rapport/FR-002"].Coverage.Should().Be(ScopeCoverage.AutomatedCoverage, "FR-002 is unique in the snapshot and explicitly verified");
        byKey["req:SkoleService/001-rapport/FR-001"].Should().Match<ScopeItem>(i => i.Coverage == ScopeCoverage.CandidateCoverageOnly && i.CoverageDetail!.Contains("2 features use that id"));
        byKey["story:SkoleService/001-rapport/US2"].Coverage.Should().Be(ScopeCoverage.CandidateCoverageOnly, "a candidate link is never confirmed coverage");
        byKey["ac:SkoleService/001-rapport/AC-1"].Coverage.Should().Be(ScopeCoverage.NoKnownCoverage);
        r.Limitations.Should().NotContain(l => l.Contains("Not assessed"));
    }

    [Fact]
    public async Task A_story_or_criterion_id_reused_by_several_features_is_never_claimed_as_automated_coverage()
    {
        // Real M2LB: "US1", "SC-004" and "AC-1" exist in nearly every feature; a test naming "US1" cannot say which feature it verifies.
        await using var db = Db();
        await using var canonical = Canonical();
        var (project, _) = await SeedAllAsync(db);
        var test = Entity(CanonicalEntityType.Test, "tests/mottak", "MottakTests");
        var us1 = Entity(CanonicalEntityType.UserStory, "US1", "US1");
        var ac2 = Entity(CanonicalEntityType.Requirement, CorrelationRules.NormalizeRequirement("AC-2") ?? "AC-2", "AC-2");
        canonical.Entities.AddRange(test, us1, ac2);
        canonical.Relations.AddRange(Relation(test, us1, CanonicalRelationType.Verifies, RelationResolution.Explicit),
            Relation(test, ac2, CanonicalRelationType.Verifies, RelationResolution.Explicit));
        await canonical.SaveChangesAsync();

        var r = await Service(db, canonical).ResolveAsync(project, new ScopeQuery("profile:skolenaervaer", null, false), default);
        r.Items.Where(i => i.DisplayId == "US1").Should().HaveCount(2).And.OnlyContain(i => i.Coverage == ScopeCoverage.CandidateCoverageOnly && i.CoverageDetail!.Contains("features use that id"));
        r.Items.Single(i => i.DisplayId == "AC-2").Coverage.Should().Be(ScopeCoverage.AutomatedCoverage, "AC-2 exists in one feature only");
    }

    [Fact]
    public async Task A_test_linked_to_the_feature_packs_own_requirement_counts_as_coverage_even_when_other_packs_reuse_the_id()
    {
        // correlation/3 scopes requirement ids per feature pack, so "FR-001 of SkoleService/001-rapport" is unambiguous although Receiver also has FR-001.
        await using var db = Db();
        await using var canonical = Canonical();
        var (project, _) = await SeedAllAsync(db);
        var test = Entity(CanonicalEntityType.Test, "tests/rapport", "RapportTests");
        var scoped = Entity(CanonicalEntityType.Requirement, CorrelationRules.Scoped("SkoleService", "001-rapport", "FR-001"), "FR-001");
        canonical.Entities.AddRange(test, scoped);
        canonical.Relations.Add(Relation(test, scoped, CanonicalRelationType.Verifies, RelationResolution.Explicit));
        await canonical.SaveChangesAsync();

        var r = await Service(db, canonical).ResolveAsync(project, new ScopeQuery("profile:skolenaervaer", null, false), default);
        r.Items.Single(i => i.Key == "req:SkoleService/001-rapport/FR-001").Should().Match<ScopeItem>(i =>
            i.Coverage == ScopeCoverage.AutomatedCoverage && i.CoverageDetail!.Contains("of this feature pack"));
        r.Items.Single(i => i.Key == "req:MeldingsutvekslerReceiver/002-mottak/FR-001").Coverage.Should().Be(ScopeCoverage.NoKnownCoverage, "the other pack's FR-001 is not covered");
    }

    [Fact]
    public async Task Builder_candidates_are_searched_and_bounded_on_the_server()
    {
        await using var db = Db();
        var (project, _) = await SeedAllAsync(db);
        var service = Service(db);
        var r = await service.CandidatesAsync(project, false, "person", 50, default);
        r.Items.Should().NotBeEmpty().And.OnlyContain(i => i.ParentKey == null && (i.Module == "Person" || i.Title.Contains("person", StringComparison.OrdinalIgnoreCase)));
        (await service.CandidatesAsync(project, false, null, 3, default)).Items.Should().HaveCount(3);
        (await service.CandidatesAsync(project, false, null, 500, default)).Items.Should().OnlyContain(i => i.Type != ScopeArtifactType.AcceptanceCriterion);
    }

    [Fact]
    public async Task A_confirmed_shared_capability_relates_a_module_while_a_candidate_is_only_suggested()
    {
        await using var db = Db();
        await using var canonical = Canonical();
        var (project, _) = await SeedAllAsync(db);
        var capability = Entity(CanonicalEntityType.Capability, "fravaer", "Fravær");
        var skole = Entity(CanonicalEntityType.Feature, "skoleservice/001-rapport", "Motta skolenærværsrapport");
        var autorisasjon = Entity(CanonicalEntityType.Feature, "autorisasjonservice/005-tilgang", "Tilgangsstyring");
        canonical.Entities.AddRange(capability, skole, autorisasjon);
        canonical.Relations.AddRange(Relation(skole, capability, CanonicalRelationType.Implements, RelationResolution.Explicit),
            Relation(autorisasjon, capability, CanonicalRelationType.Implements, RelationResolution.Candidate));
        await canonical.SaveChangesAsync();
        var service = Service(db, canonical);

        var suggested = await service.ResolveAsync(project, new ScopeQuery("module:SkoleService", null, true), default);
        suggested.RelatedModules.Single(m => m.Module == "AutorisasjonService").Should().Match<ScopeRelatedModule>(m => !m.Included && m.Reason.StartsWith("Suggested"));
        suggested.Items.Should().NotContain(i => i.Module == "AutorisasjonService", "a candidate capability never includes items");

        var relation = await canonical.Relations.SingleAsync(r => r.SourceEntityId == autorisasjon.Id);
        relation.ReviewState = nameof(RelationReviewState.Confirmed);
        await canonical.SaveChangesAsync();
        var confirmed = await service.ResolveAsync(project, new ScopeQuery("module:SkoleService", null, true), default);
        confirmed.Items.Where(i => i.Module == "AutorisasjonService").Should().NotBeEmpty().And.OnlyContain(i => i.Relation == ScopeRelation.RelatedCapability && i.Reason.Contains("Fravær"));
    }

    // ── Test design ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_test_design_assessment_is_stored_by_stable_key_and_drives_testability_and_test_data_state()
    {
        await using var db = Db();
        var (project, _) = await SeedAllAsync(db);
        var service = Service(db);
        const string key = "story:SkoleService/001-rapport/US1";

        await service.AssessAsync(project, new TestDesignAssessmentView(key, TestabilityClassification.ManualAndTechnical, ["Manual", "Automated API"], [], "  ", null), default);
        var item = (await service.ResolveAsync(project, new ScopeQuery("module:SkoleService", null, false), default)).Items.Single(i => i.Key == key);
        item.Should().Match<ScopeItem>(i => i.Testability == TestabilityClassification.ManualAndTechnical && i.TestData == ScopeTestDataState.NotRequired);

        await service.AssessAsync(project, new TestDesignAssessmentView(key, TestabilityClassification.Manual, [], ["td-123"], "Needs a pupil", null), default);
        item = (await service.ResolveAsync(project, new ScopeQuery("module:SkoleService", null, false), default)).Items.Single(i => i.Key == key);
        item.TestData.Should().Be(ScopeTestDataState.Unknown, "without the Test Data catalog the reference cannot be checked — never claimed available");
        (await db.TestDesignAssessments.CountAsync()).Should().Be(1);

        var detail = await service.DetailAsync(project, key, default);
        detail!.Assessment!.Comment.Should().Be("Needs a pupil");
        detail.Children.Select(c => c.DisplayId).Should().Equal("AC-1", "AC-2");
        detail.FullText.Should().Contain("lagrer fraværet");
    }

    [Fact]
    public async Task A_project_without_snapshots_reports_a_limitation_instead_of_an_empty_success()
    {
        await using var db = Db();
        var project = Guid.NewGuid();
        db.Projects.Add(new DeliveryProjectRecord { Id = project, Name = "Empty", CreatedAt = Monday, UpdatedAt = Monday });
        await db.SaveChangesAsync();
        var r = await Service(db).ResolveAsync(project, new ScopeQuery("module:Person", null, false), default);
        r.Items.Should().BeEmpty();
        r.Limitations.Should().ContainSingle().Which.Should().Contain("No Delivery snapshot");
    }
}
