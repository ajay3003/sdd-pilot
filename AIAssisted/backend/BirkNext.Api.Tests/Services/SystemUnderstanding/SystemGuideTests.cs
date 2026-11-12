using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.Integrations;
using BirkNext.SystemUnderstanding;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services.SystemUnderstanding;

/// <summary>
/// System Understanding guides on a fresh migrated PostgreSQL database: the curated seed lands once as an unreviewed Draft, revisions move only
/// Draft → InReview → Approved → Published, a published revision is never rewritten (the database refuses it too), identical content creates
/// nothing, concurrent edits are detected, newly imported source snapshots make the guide "may need review" without touching its content, and the
/// model is generic (a second, unrelated synthetic system validates and is versioned the same way).
/// </summary>
public sealed class SystemGuideTests : IAsyncLifetime
{
    private readonly string _database = TestPostgres.TemporaryName("birknext_guide");
    private static readonly SystemGuideCatalogEntry Skole = new("skolenaervaer", "SystemGuide.skolenaervaer.guide.json");
    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);
    private SystemGuideService Service(AppDbContext db, params SystemGuideCatalogEntry[] entries) => new(db, entries.Length == 0 ? [Skole] : entries, Array.Empty<IIntegrationJourneyPack>());

    public async Task InitializeAsync()
    {
        await Execute($"CREATE DATABASE \"{_database}\"");
        await using var db = Context();
        await DatabaseMigrationLock.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await Execute($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");
    }

    private static async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(TestPostgres.Admin().ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public void The_Skolenaervaer_seed_is_consistent_grounded_and_honest()
    {
        var seed = SystemGuideService.LoadSeed(Skole.SeedResource);
        SystemGuideValidation.Problems(seed).Should().BeEmpty();
        seed.Language.Should().Be("nb");
        seed.Flows.Select(f => f.Kind).Should().Contain([GuideFlowKind.MainReport, GuideFlowKind.Acknowledgment, GuideFlowKind.IdentityLookup, GuideFlowKind.SupportingData,
            GuideFlowKind.PlacementLookup, GuideFlowKind.PeriodicExport], "main and side flows are distinguishable");
        seed.Flows.Single(f => f.Id == "rapport").Path.Should().Equal("altinn", "mu", "mm", "skole");
        seed.Edges.Select(e => e.DataFormat).Should().Contain(f => f.Contains("XML")).And.Contain(f => f.Contains("JSON")).And.Contain(f => f.Contains("Parquet"));
        seed.Steps.Count(s => s.Stage is "s-innsending" or "s-mu" or "s-mm" or "s-inntak" or "s-kvittering").Should().Be(20, "the 20 steps of the main report flow");
        // No claim is runtime- or test-verified: nothing was executed for this guide.
        SystemGuideService.AllEvidence(seed).Should().NotContain(e => e.Class == GuideEvidenceClass.RuntimeVerified || e.Class == GuideEvidenceClass.TestVerified);
        seed.Rules.Should().OnlyContain(r => r.Verification == GuideEvidenceClass.NotAssessed);
        seed.Scenarios.Should().OnlyContain(s => s.State == GuideScenarioState.Suggested || s.State == GuideScenarioState.NeedsClarification, "guidance is never coverage");
        // Both imported M2LB source generations are kept as separate facts, and the deployed one is not guessed (real-source audit 2026-11-11).
        var intake = seed.Transfers.Single(t => t.EdgeId == "e-mm-skole").Versions;
        intake.Should().Contain(v => v.Fact.Contains("/rapporter")).And.Contain(v => v.Fact.Contains("/reports"));
        intake.Should().NotContain(v => v.Class == GuideEvidenceClass.RuntimeVerified, "which generation is deployed is not known");
        // Parquet delivery exists in source in one generation only; it is never presented as delivered to Bufdata.
        var delivery = seed.Edges.Single(e => e.Id == "e-periodelaas-bufdata");
        delivery.DataFormat.Should().Contain("Parquet");
        delivery.Evidence.Should().NotContain(e => e.Class == GuideEvidenceClass.RuntimeVerified);
    }

    [Fact]
    public async Task The_seed_lands_once_as_an_unreviewed_draft_and_publishing_follows_review()
    {
        await using var db = Context();
        var guides = Service(db);
        var view = await guides.GetAsync("skolenaervaer", canEdit: true, default);
        view.Shown!.Revision.Should().Be(1);
        view.Shown.State.Should().Be(GuideRevisionState.Draft, "a seed is never approved");
        view.ReviewState.Should().Be("Draft only — not reviewed");
        view.Shown.CreatedBy.Should().Be(SystemGuideService.SeedAuthor);
        (await guides.GetAsync("skolenaervaer", true, default)).Revisions.Should().ContainSingle("seeding is idempotent");

        await FluentActions.Invoking(() => guides.PublishAsync("skolenaervaer", 1, default)).Should().ThrowAsync<SystemGuideException>().WithMessage("*approved*");
        await FluentActions.Invoking(() => guides.ApproveAsync("skolenaervaer", 1, "reviewer", default)).Should().ThrowAsync<SystemGuideException>().WithMessage("*in review*");
        await guides.SubmitAsync("skolenaervaer", 1, "author", default);
        var content = view.Shown.Content;
        await FluentActions.Invoking(() => guides.SaveDraftAsync("skolenaervaer", new(content with { Purpose = "x" }, null, null), "author", default))
            .Should().ThrowAsync<SystemGuideException>().WithMessage("*in review*");
        (await guides.ApproveAsync("skolenaervaer", 1, "reviewer", default)).ApprovedBy.Should().Be("reviewer");
        var published = await guides.PublishAsync("skolenaervaer", 1, default);
        published.State.Should().Be(GuideRevisionState.Published);
        (await guides.GetAsync("skolenaervaer", true, default)).ReviewState.Should().Be("Published (revision 1)");
    }

    [Fact]
    public async Task A_published_revision_is_never_rewritten_edits_open_a_new_revision_and_publishing_supersedes()
    {
        await using var db = Context();
        var guides = Service(db);
        var r1 = (await guides.GetAsync("skolenaervaer", true, default)).Shown!;
        await guides.SubmitAsync("skolenaervaer", 1, "a", default);
        await guides.ApproveAsync("skolenaervaer", 1, "r", default);
        await guides.PublishAsync("skolenaervaer", 1, default);

        await FluentActions.Invoking(() => guides.SaveDraftAsync("skolenaervaer", new(r1.Content, null, null), "a", default))
            .Should().ThrowAsync<SystemGuideException>().WithMessage("*identical*", "unchanged content creates no revision");
        var changed = r1.Content with { Purpose = r1.Content.Purpose + " (rettet)" };
        var r2 = await guides.SaveDraftAsync("skolenaervaer", new(changed, "Rettet formål", null), "author", default);
        r2.Revision.Should().Be(2);
        var view = await guides.GetAsync("skolenaervaer", true, default);
        view.Shown!.Revision.Should().Be(1, "readers keep seeing the published revision while the draft is edited");
        view.Draft!.Revision.Should().Be(2);
        (await guides.SaveDraftAsync("skolenaervaer", new(changed, null, r2.ContentFingerprint), "author", default)).Revision.Should().Be(2, "identical draft content is a no-op");
        await FluentActions.Invoking(() => guides.SaveDraftAsync("skolenaervaer", new(changed with { Title = "Skolenærvær 2" }, null, new string('0', 64)), "other", default))
            .Should().ThrowAsync<SystemGuideException>().WithMessage("*changed by someone else*");

        await guides.SubmitAsync("skolenaervaer", 2, "a", default);
        await guides.ApproveAsync("skolenaervaer", 2, "r", default);
        await guides.PublishAsync("skolenaervaer", 2, default);
        var old = await guides.RevisionAsync("skolenaervaer", 1, default);
        old.State.Should().Be(GuideRevisionState.Superseded);
        old.SupersededByRevision.Should().Be(2);
        old.ContentFingerprint.Should().Be(r1.ContentFingerprint, "the historical content is preserved");

        // The database itself: a published or superseded revision's content cannot be changed, and a superseded one is final.
        var rewrite = () => db.Database.ExecuteSqlRawAsync("UPDATE system_guide_revisions SET \"ContentJson\" = 'tampered' WHERE \"Revision\" = 2");
        (await rewrite.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("immutable beyond Draft");
        var revive = () => db.Database.ExecuteSqlRawAsync("UPDATE system_guide_revisions SET \"State\" = 'Published' WHERE \"Revision\" = 1");
        (await revive.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("final");
        var second = () => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO system_guide_revisions (\"Id\", \"GuideKey\", \"Revision\", \"State\", \"ContentFingerprint\", \"ContentJson\", \"SourceBasisJson\", \"CreatedBy\", \"CreatedAt\") " +
            "VALUES (gen_random_uuid(), 'skolenaervaer', 3, 'Published', 'x', 'tampered', '[]', 'x', now())");
        (await second.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505", "only one published revision per guide");
    }

    [Fact]
    public async Task Newly_imported_source_snapshots_make_the_guide_may_need_review_without_changing_it()
    {
        await using var db = Context();
        var guides = Service(db);
        var seed = (await guides.GetAsync("skolenaervaer", true, default)).Shown!;
        var generic = Synthetic() with { SourceRoles = [new GuideSourceRole("order-service", "Order service", ["OrderService"])] };
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord
        {
            Id = Guid.NewGuid(), EnvironmentId = "", IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = DateTimeOffset.UtcNow,
            EvidenceJson = JsonSerializer.Serialize(new IqrSourceSnapshot
            {
                Archive = new("orders-v2.zip", new string('c', 64), 12), AnalyzedAt = DateTimeOffset.UtcNow,
                Projects = [new SourceProject("Contoso.OrderService.Api", "src/OrderService", false, "Api", SourceConfidence.Confirmed)],
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        });
        await db.SaveChangesAsync();
        var current = await guides.CurrentSourcesAsync(Skole, generic, default);
        current.Should().ContainSingle(s => s.SnapshotId != null).Which.Label.Should().Contain("orders-v2.zip");
        var revision = seed with { Content = generic, SourceBasis = [new GuideSourceBasis("order-service", null, null, "Order service: not imported")] };
        var changes = SystemGuideService.SourceChanges(revision, current);
        changes.Should().ContainSingle().Which.Change.Should().Contain("may need review");
        changes[0].AffectedClaims.Should().Be(1);
        (await guides.GetAsync("skolenaervaer", true, default)).Shown!.ContentFingerprint.Should().Be(seed.ContentFingerprint, "nothing is rewritten automatically");
    }

    [Fact] // Baseline correction: alternative versions without a baseline are neither cited nor reported as "not imported"
    public async Task A_role_imported_only_in_unselected_alternative_versions_says_baseline_not_selected()
    {
        await using var db = Context();
        var guides = Service(db);
        var generic = Synthetic() with { SourceRoles = [new GuideSourceRole("order-service", "Order service", ["OrderService"])] };
        IqrSourceSnapshotRecord Version(string archive, char sha, int minutes)
        {
            var id = Guid.NewGuid();
            return new()
            {
            Id = id, EnvironmentId = "", IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = DateTimeOffset.UtcNow.AddMinutes(minutes),
            EvidenceJson = JsonSerializer.Serialize(new IqrSourceSnapshot
            {
                Id = id, Archive = new(archive, new string(sha, 64), 12), AnalyzedAt = DateTimeOffset.UtcNow.AddMinutes(minutes),
                Projects = [new SourceProject("Contoso.OrderService.Api", "src/OrderService", false, "Api", SourceConfidence.Confirmed)],
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            };
        }
        var older = Version("orders.zip", 'a', -5);
        db.IqrSourceSnapshots.AddRange(older, Version("orders (2).zip", 'b', 0));
        await db.SaveChangesAsync();

        var current = await guides.CurrentSourcesAsync(Skole, generic, default);
        current.Should().ContainSingle().Which.Should().Match<GuideSourceBasis>(b => b.SnapshotId == null && b.Label.Contains("BASELINE_NOT_SELECTED") && !b.Label.Contains("not imported"));

        (await new BirkNext.Api.Services.SourceAnalysis.SourceBaselineService(db, new IqrSourceStore(db)).SelectBaselineAsync(
            BirkNext.Api.Services.SourceAnalysis.SourceBaselineService.KeyOf(JsonSerializer.Deserialize<IqrSourceSnapshot>(older.EvidenceJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!), older.Id, "local-session:loopback", null, default)).Changed.Should().BeTrue();
        var afterChoice = await guides.CurrentSourcesAsync(Skole, generic, default);
        afterChoice.Should().ContainSingle().Which.SnapshotId.Should().Be(older.Id, "the chosen version is the basis, not the newest import");

        // Real-archive finding: a revision written from the other (still imported) version said "No longer imported" after a baseline switch.
        var seed = (await guides.GetAsync("skolenaervaer", true, default)).Shown!;
        var newerId = db.IqrSourceSnapshots.Single(r => r.Id != older.Id).Id;
        var revision = seed with { Content = generic, SourceBasis = [new GuideSourceBasis("order-service", newerId, null, "Order service: orders (2).zip")] };
        var changes = SystemGuideService.SourceChanges(revision, afterChoice, new HashSet<Guid> { older.Id, newerId });
        changes.Should().Contain(c => c.Change.StartsWith("No longer the analysis baseline (still imported")).And.NotContain(c => c.Change.StartsWith("No longer imported"));
        SystemGuideService.SourceChanges(revision, afterChoice, new HashSet<Guid> { older.Id }).Should().Contain(c => c.Change.StartsWith("No longer imported"), "a deleted snapshot is still reported as such");
    }

    private static IqrSourceSnapshotRecord SnapshotRecord(string archive, string project) => new()
    {
        Id = Guid.NewGuid(), EnvironmentId = "", IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = DateTimeOffset.UtcNow,
        EvidenceJson = JsonSerializer.Serialize(new IqrSourceSnapshot
        {
            Archive = new(archive, new string('d', 64), 12), AnalyzedAt = DateTimeOffset.UtcNow,
            Projects = [new SourceProject(project, "src/" + project, false, "Api", SourceConfidence.Confirmed)],
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    [Fact]
    public async Task A_curated_seed_never_claims_the_snapshots_present_when_it_was_seeded_as_its_basis()
    {
        await using var db = Context();
        // The source is imported BEFORE the guide is first opened (the order a real installation has).
        db.IqrSourceSnapshots.Add(SnapshotRecord("M2LB (7).zip", "M2LB.SkoleService.Api"));
        await db.SaveChangesAsync();
        // The real catalog wiring: the guide's source roles come from the Skolenærvær pack (recognition by components/projects).
        var flows = new BirkNext.Api.Tests.Services.IntegrationJourneys.IntegrationJourneyHarness.FakeMessageFlows();
        var kit = new BirkNext.Api.Tests.Services.AltinnTesting.AltinnTestKit();
        var pack = new BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer.SkoleNaervaerJourneyPack(flows, kit.Policy, kit.Contracts(flows));
        var guides = new SystemGuideService(db, [Skole with { PackId = pack.PackId }], [pack]);
        var view = await guides.GetAsync("skolenaervaer", true, default);

        view.Shown!.SourceBasis.Should().OnlyContain(b => b.SnapshotId == null && b.Label.Contains("curated outside this installation"),
            "the seed was written elsewhere; it was not checked against this installation's snapshots");
        view.SourceChanges.Should().ContainSingle(c => c.Label.Contains("M2LB (7).zip")).Which.Change.Should().Contain("may need review",
            "an imported source the guide was not written from must be flagged, whenever it was imported");
    }

    [Fact]
    public async Task An_unedited_seed_draft_is_refreshed_from_the_shipped_seed_and_an_edited_or_published_one_never_is()
    {
        await using (var db = Context())
        {
            var guides = Service(db);
            var seed = (await guides.GetAsync("skolenaervaer", true, default)).Shown!;
            // Simulate an installation seeded by an older BirkNext: same seed author, older content.
            var row = await db.SystemGuideRevisions.SingleAsync();
            var older = seed.Content with { Purpose = "Eldre kuratert tekst." };
            row.ContentJson = JsonSerializer.Serialize(older, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            row.ContentFingerprint = SystemGuideService.Fingerprint(older);
            await db.SaveChangesAsync();
            var refreshed = (await guides.GetAsync("skolenaervaer", true, default)).Shown!;
            refreshed.ContentFingerprint.Should().Be(seed.ContentFingerprint, "an unedited curated draft follows the shipped seed");
            refreshed.Revision.Should().Be(1);

            // A person edited the draft: it is theirs now and is never replaced.
            var edited = await guides.SaveDraftAsync("skolenaervaer", new(seed.Content with { Purpose = "Redigert av en person." }, null, null), "reviewer", default);
            (await guides.GetAsync("skolenaervaer", true, default)).Shown!.ContentFingerprint.Should().Be(edited.ContentFingerprint);
        }
    }

    // ── Generic model, safety ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>An unrelated synthetic system: different components, transports, rules and stages.</summary>
    private static SystemGuideContent Synthetic() => new()
    {
        Title = "Order fulfilment", Purpose = "Orders are paid, picked and shipped.", Language = "en",
        Components = [new() { Id = "shop", Name = "Shop", Responsibility = "Takes orders" }, new() { Id = "wms", Name = "Warehouse", Responsibility = "Picks", Lane = "logistics" }],
        Edges = [new() { Id = "e1", From = "shop", To = "wms", Kind = GuideFlowKind.MainReport, Transport = "Kafka topic orders", DataFormat = "Avro",
            Evidence = [new() { Class = GuideEvidenceClass.SourceImplemented, SourceRole = "order-service", File = "src/OrderService/Publisher.cs", Link = "source-analysis" }] }],
        Flows = [new() { Id = "f", Title = "Order", Kind = GuideFlowKind.MainReport, Path = ["shop", "wms"] }],
        Stages = [new() { Id = "s1", Title = "Pick", FlowId = "f" }],
        Steps = [new() { Id = "1", Order = 1, Stage = "s1", Title = "Pick the order", ComponentId = "wms", TestScenarioIds = ["t1"] }],
        Rules = [new() { Id = "r1", Category = "Stock", Title = "No negative stock", ComponentId = "wms", Implementation = GuideEvidenceClass.Specified }],
        Scenarios = [new() { Id = "t1", Title = "Out of stock", Objective = "Refused", StepIds = ["1"] }],
        Transfers = [new() { EdgeId = "e1", From = "Shop", To = "Warehouse", Transport = "Kafka", DataFormat = "Avro" }],
    };

    [Fact]
    public void The_model_is_generic_and_every_reference_must_resolve()
    {
        SystemGuideValidation.Problems(Synthetic()).Should().BeEmpty("a second, unrelated system uses the same model");
        SystemGuideValidation.Problems(Synthetic() with { Steps = [Synthetic().Steps[0] with { ComponentId = "ghost" }] }).Should().Contain(p => p.Contains("unknown component ghost"));
        SystemGuideValidation.Problems(Synthetic() with { Edges = [Synthetic().Edges[0] with { To = "nowhere" }] }).Should().Contain(p => p.Contains("unknown component nowhere"));
        SystemGuideValidation.Problems(Synthetic() with { Scenarios = [Synthetic().Scenarios[0] with { StepIds = ["99"] }] }).Should().Contain(p => p.Contains("unknown step 99"));
        SystemGuideValidation.Problems(Synthetic() with { Components = [.. Synthetic().Components, Synthetic().Components[0]] }).Should().Contain(p => p.Contains("duplicate component"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://evil.example/x")]
    [InlineData("//evil.example")]
    [InlineData("source-analysis/../../etc")]
    [InlineData("a\\b")]
    [InlineData("x\" onmouseover=\"y")]
    public void Unsafe_evidence_links_are_refused(string link)
    {
        SystemGuideValidation.IsSafeRoute(link).Should().BeFalse();
        var bad = Synthetic() with { Edges = [Synthetic().Edges[0] with { Evidence = [new() { Class = GuideEvidenceClass.Inferred, Link = link }] }] };
        SystemGuideValidation.Problems(bad).Should().Contain(p => p.Contains("unsafe link"));
    }

    [Theory]
    [InlineData("source-analysis?snapshot=1&area=architecture")]
    [InlineData("api-quality-review/contracts")]
    [InlineData("extensions/skolenaervaer")]
    public void BirkNext_routes_are_allowed(string link) => SystemGuideValidation.IsSafeRoute(link).Should().BeTrue();

    [Fact]
    public async Task An_inconsistent_or_unsafe_draft_is_refused_before_anything_is_stored()
    {
        await using var db = Context();
        var guides = Service(db);
        await guides.GetAsync("skolenaervaer", true, default);
        var bad = Synthetic() with { Edges = [Synthetic().Edges[0] with { Evidence = [new() { Link = "javascript:alert(1)" }] }] };
        await FluentActions.Invoking(() => guides.SaveDraftAsync("skolenaervaer", new(bad, null, null), "a", default)).Should().ThrowAsync<SystemGuideException>().Where(e => e.Status == 422);
        (await guides.RevisionAsync("skolenaervaer", 1, default)).Content.Title.Should().Be("Skolenærvær");
        await FluentActions.Invoking(() => guides.GetAsync("unknown", true, default)).Should().ThrowAsync<SystemGuideException>().Where(e => e.Status == 404);
    }
}
