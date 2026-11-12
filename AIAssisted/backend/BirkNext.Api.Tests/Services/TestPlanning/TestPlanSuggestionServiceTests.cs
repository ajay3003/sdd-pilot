using BirkNext.Api.Data;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.Integrations;
using BirkNext.SystemUnderstanding;
using BirkNext.TestData;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Test Plan Suggestions on a fresh migrated PostgreSQL database with the real curated Skolenærvær guide: drafts are built from the guide's own
/// scenarios, generated content cannot be rewritten by a client, stale saves are refused, review decisions keep their history and rules, Test Data
/// is linked by immutable asset id, snapshots are immutable in the database, identical content is not saved twice (also concurrently), history
/// stays readable after the draft changes and after a restart, and a guide change is detected and applied only by an explicit update.
/// </summary>
public sealed class TestPlanSuggestionServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _database = TestPostgres.TemporaryName("birknext_tps");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tps-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly SystemGuideCatalogEntry Skole = new("skolenaervaer", "SystemGuide.skolenaervaer.guide.json");
    private static readonly string[] Seven = ["gyldig-rapport", "manglende-barn", "barn-dukker-opp-senere", "duplikat-melding", "korrigert-rapport", "periodelaas", "bufdata-fil"];
    private readonly Values _values = new();

    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);

    private TestPlanSuggestionService Service(AppDbContext db)
    {
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "reg"), NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([_values, new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, null, new TestDataIdentityStore(Path.Combine(_dir, "ids")));
        var sources = new NoSources();
        var testData = new TestDataCatalogService(new TestDataSchemaValidationService(catalog, new NoSchemas()), catalog, registry, new TestDataCandidateService(sources, registry), sources, TimeProvider.System);
        var guides = new SystemGuideService(db, [Skole], Array.Empty<IIntegrationJourneyPack>());
        return new TestPlanSuggestionService(db, guides, new SourceBaselineService(db, new IqrSourceStore(db)), testData, Array.Empty<IIntegrationJourneyPack>(),
            new CriticalE2EStore(Path.Combine(_dir, "e2e"), NullLogger<CriticalE2EStore>.Instance));
    }

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

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static async Task Execute(string sql)
    {
        await using var connection = new NpgsqlConnection(TestPostgres.Admin().ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<TestPlanDraft> CreateSeven(AppDbContext db) =>
        await Service(db).CreateAsync(new CreateTestPlanDraftRequest("skolenaervaer", "Skolenærvær – Funksjonell testing", "Leveranse 1", [.. Seven]), "tester", default);

    private static SaveTestPlanDraftRequest Human(TestPlanDraft plan, Func<TestCaseDraft, TestCaseHumanPart, TestCaseHumanPart>? change = null) => new()
    {
        BaseRevision = plan.Revision, PlanName = plan.PlanName, DeliveryLabel = plan.DeliveryLabel, ScenarioIds = [.. plan.Cases.Select(c => c.Source.ScenarioId)], Suites = plan.Suites,
        Cases = [.. plan.Cases.Select(c =>
        {
            var part = new TestCaseHumanPart { ScenarioId = c.Source.ScenarioId, SuiteId = c.SuiteId, Edits = c.Edits, Risk = c.Risk, TestDataAssetIds = [.. c.TestData.Select(d => d.AssetId)] };
            return change is null ? part : change(c, part);
        })],
    };

    [Fact]
    public async Task A_proposal_is_built_from_the_guides_own_scenarios_grouped_by_stage_with_an_explicit_unreviewed_basis()
    {
        await using var db = Context();
        var workspace = await Service(db).WorkspaceAsync(null, canWrite: true, default);
        workspace.GuideKey.Should().Be("skolenaervaer");
        workspace.Candidates.Should().HaveCount(21, "every 'What to test' scenario is a candidate, none invented");
        workspace.Candidates.Single(c => c.Source.ScenarioId == "ukjent-xml-versjon").GuideState.Should().Be("NeedsClarification");
        workspace.Basis!.GuideReviewState.Should().Be("Draft only — not reviewed");
        workspace.Basis.Roles.Should().NotBeEmpty().And.OnlyContain(r => r.State == "NOT_IMPORTED", "nothing is imported in this database");

        var plan = await CreateSeven(db);
        plan.Cases.Select(c => c.Source.ScenarioId).Should().Equal(Seven);
        plan.Cases.Should().OnlyContain(c => c.Review == TestCaseReviewState.Generated && c.Generated.ExpectedBasis != ExpectedResultBasis.ReviewedGuide);
        plan.Cases.Should().OnlyContain(c => !c.Testability.Ready, "a documented scenario is not executable");
        plan.Suites.Select(s => s.Name).Should().Contain(["Skoletjenesten tar imot", "Periodelås", "Levering til Bufdata"]);
        plan.Cases.Single(c => c.Source.ScenarioId == "bufdata-fil").Missing.Should().Contain(m => m.Contains("NEEDS CLARIFICATION"));
        plan.Basis.GuideRevision.Should().Be(1);
        plan.RuleVersion.Should().Be(TestPlanSuggestionEngine.RuleVersion);
    }

    [Fact]
    public async Task A_client_cannot_rewrite_generated_content_and_a_stale_save_is_refused_without_losing_anything()
    {
        await using var db = Context();
        var service = Service(db);
        var plan = await CreateSeven(db);
        var request = Human(plan, (c, p) => c.Source.ScenarioId == "manglende-barn" ? p with { Edits = new TestCaseEdits { ExpectedResult = "Rapporten holdes og kobles når barnet finnes" }, Risk = TestCaseRisk.High } : p);
        var saved = await service.SaveAsync(plan.Id, request, "reviewer", default);
        saved.Revision.Should().Be(2);
        var held = saved.Cases.Single(c => c.Source.ScenarioId == "manglende-barn");
        held.Edits.ExpectedResult.Should().Be("Rapporten holdes og kobles når barnet finnes");
        held.Generated.ExpectedResult.Should().Be(plan.Cases.Single(c => c.Source.ScenarioId == "manglende-barn").Generated.ExpectedResult, "the generated content is kept apart and unchanged");
        held.Risk.Should().Be(TestCaseRisk.High);

        var stale = () => service.SaveAsync(plan.Id, request, "other", default);
        (await stale.Should().ThrowAsync<TestPlanException>()).Which.Status.Should().Be(409);
        (await Service(Context()).DraftAsync(plan.Id, default)).Revision.Should().Be(2);
    }

    [Fact]
    public async Task Review_decisions_keep_their_history_need_a_rationale_to_exclude_and_never_accept_a_case_without_an_expected_result()
    {
        await using var db = Context();
        var service = Service(db);
        var plan = await CreateSeven(db);
        var noReason = () => service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, "periodelaas", TestCaseReviewState.Excluded, null), "r", default);
        (await noReason.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("rationale-required");
        var r1 = await service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, "gyldig-rapport", TestCaseReviewState.UnderReview, null), "Kari", default);
        var r2 = await service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(r1.Revision, "gyldig-rapport", TestCaseReviewState.Accepted, "Bekreftet med fagansvarlig"), "Kari", default);
        r2.Cases.Single(c => c.Source.ScenarioId == "gyldig-rapport").ReviewHistory.Select(e => (e.State, e.Reviewer)).Should().Equal(
            (TestCaseReviewState.UnderReview, "Kari"), (TestCaseReviewState.Accepted, "Kari"));

        var synthetic = await service.SaveAsync(plan.Id, Human(r2, (c, p) => c.Source.ScenarioId == "periodelaas" ? p with { Edits = new TestCaseEdits { ExpectedResult = " " } } : p), "r", default);
        synthetic.Cases.Single(c => c.Source.ScenarioId == "periodelaas").Edits.ExpectedResult.Should().BeNull("blank edits are no edits");
    }

    [Fact]
    public async Task Test_Data_is_linked_by_immutable_asset_id_and_an_unknown_id_is_ignored()
    {
        _values.Items.Add(new TestDataAsset { Id = "values:child", Alias = "syntetisk-barn", Kind = TestDataKind.Scalar, ValueType = TestDataValueType.String, Approval = TestDataApproval.Approved,
            Source = "Values", Sensitivity = TestDataSensitivity.Internal, Value = new TestDataValue("x") });
        await using var db = Context();
        var service = Service(db);
        var workspace = await service.WorkspaceAsync("skolenaervaer", true, default);
        var asset = workspace.TestData.Single(a => a.Alias == "syntetisk-barn");
        asset.AssetId.Should().StartWith("tda_");
        var plan = await CreateSeven(db);
        var saved = await service.SaveAsync(plan.Id, Human(plan, (c, p) => c.Source.ScenarioId == "gyldig-rapport" ? p with { TestDataAssetIds = [asset.AssetId, "tda_does_not_exist"] } : p), "r", default);
        var link = saved.Cases.Single(c => c.Source.ScenarioId == "gyldig-rapport").TestData.Should().ContainSingle().Subject;
        link.AssetId.Should().Be(asset.AssetId);
        link.Alias.Should().Be("syntetisk-barn");
        link.Approval.Should().Be("Approved");
        saved.Cases.Single(c => c.Source.ScenarioId == "gyldig-rapport").Missing.Should().NotContain(m => m.StartsWith("Test data"));
    }

    [Fact]
    public async Task Snapshots_are_immutable_in_the_database_identical_content_is_not_saved_twice_and_history_survives_draft_changes_and_restart()
    {
        Guid draftId, first, second;
        await using (var db = Context())
        {
            var service = Service(db);
            var plan = await CreateSeven(db);
            draftId = plan.Id;
            var s1 = await service.SaveSnapshotAsync(plan.Id, new SaveTestPlanSnapshotRequest(plan.Revision, "Leveranse 1", null), "lead", default);
            s1.Unchanged.Should().BeFalse();
            s1.Snapshot.Sequence.Should().Be(1);
            first = s1.Snapshot.Id;
            var again = await service.SaveSnapshotAsync(plan.Id, new SaveTestPlanSnapshotRequest(plan.Revision, "Leveranse 1", null), "lead", default);
            again.Unchanged.Should().BeTrue();
            again.Snapshot.Id.Should().Be(first);

            var reviewed = await service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, "duplikat-melding", TestCaseReviewState.NeedsClarification, "Hva er naturlig nøkkel?"), "Kari", default);
            var s2 = await service.SaveSnapshotAsync(plan.Id, new SaveTestPlanSnapshotRequest(reviewed.Revision, "Leveranse 2", null), "lead", default);
            s2.Snapshot.Sequence.Should().Be(2);
            second = s2.Snapshot.Id;

            var tamper = async () => await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE test_plan_snapshots SET \"ContentJson\" = {"{}"} WHERE \"Id\" = {first}");
            (await tamper.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("test plan snapshots are immutable");
        }
        await using (var restarted = Context())
        {
            var service = Service(restarted);
            var old = await service.SnapshotAsync(first, default);
            old.Plan.Cases.Single(c => c.Source.ScenarioId == "duplikat-melding").Review.Should().Be(TestCaseReviewState.Generated, "the first snapshot keeps its own content");
            (await service.WorkspaceAsync("skolenaervaer", true, default)).Snapshots.Select(s => s.Sequence).Should().Equal(2, 1);
            var compare = await service.CompareAsync(first, second, default);
            compare.Cases.Single(c => c.ScenarioId == "duplikat-melding").Changes.Should().Contain(c => c.Category == "Review");
            compare.Cases.Single(c => c.ScenarioId == "duplikat-melding").PotentialBehaviourChange.Should().BeFalse();
            (await service.ExportAsync(second, default)).Unresolved.Should().NotBeEmpty();
        }
        _ = draftId;
    }

    [Fact]
    public async Task Concurrent_identical_snapshot_saves_resolve_to_one_snapshot()
    {
        Guid id; int revision;
        await using (var db = Context()) { var plan = await CreateSeven(db); id = plan.Id; revision = plan.Revision; }
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = Context();
            return await Service(db).SaveSnapshotAsync(id, new SaveTestPlanSnapshotRequest(revision, null, null), "lead", default);
        }));
        results.Select(r => r.Snapshot.Id).Distinct().Should().ContainSingle();
        await using var check = Context();
        (await check.TestPlanSnapshots.CountAsync(s => s.DraftId == id)).Should().Be(1);
    }

    [Fact]
    public async Task A_guide_change_is_flagged_never_applied_silently_and_an_explicit_update_puts_an_accepted_case_back_under_review()
    {
        await using var db = Context();
        var service = Service(db);
        var plan = await CreateSeven(db);
        plan = await service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, "manglende-barn", TestCaseReviewState.Accepted, null), "Kari", default);
        var before = await service.SaveSnapshotAsync(plan.Id, new SaveTestPlanSnapshotRequest(plan.Revision, "Leveranse 1", null), "lead", default);

        // The guide owner changes the expected outcome of "Barnet finnes ikke" in the (draft) guide.
        var guides = new SystemGuideService(db, [Skole], Array.Empty<IIntegrationJourneyPack>());
        var guide = await guides.GetAsync("skolenaervaer", true, default);
        var content = guide.Shown!.Content with
        {
            Scenarios = [.. guide.Shown.Content.Scenarios.Select(s => s.Id == "manglende-barn" ? s with { ExpectedOutcome = "Rapporten avvises når barnet er ukjent." } : s)],
        };
        await guides.SaveDraftAsync("skolenaervaer", new SaveGuideDraftRequest(content, "Endret forventning", guide.Shown.ContentFingerprint), "fagansvarlig", default);

        var flagged = await service.DraftAsync(plan.Id, default);
        var held = flagged.Cases.Single(c => c.Source.ScenarioId == "manglende-barn");
        held.SourceChanged.Should().BeTrue();
        held.Generated.ExpectedResult.Should().NotBe("Rapporten avvises når barnet er ukjent.", "nothing is rewritten without an explicit update");
        var add = () => service.SaveAsync(plan.Id, Human(flagged) with { ScenarioIds = [.. Seven, "ugyldig-xml"] }, "r", default);
        (await add.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("guide-changed");

        var updated = await service.RefreshAsync(plan.Id, flagged.Revision, "lead", default);
        var after = updated.Cases.Single(c => c.Source.ScenarioId == "manglende-barn");
        after.Generated.ExpectedResult.Should().Be("Rapporten avvises når barnet er ukjent.");
        after.Review.Should().Be(TestCaseReviewState.UnderReview);
        after.ReviewHistory.Last().Reviewer.Should().Be("BirkNext");
        after.SourceChanged.Should().BeFalse();

        var second = await service.SaveSnapshotAsync(plan.Id, new SaveTestPlanSnapshotRequest(updated.Revision, "Leveranse 2", null), "lead", default);
        var compare = await service.CompareAsync(before.Snapshot.Id, second.Snapshot.Id, default);
        var change = compare.Cases.Single(c => c.ScenarioId == "manglende-barn");
        change.Changes.Select(c => c.Category).Should().Contain(["ExpectedResult", "SourceScenario", "Review"]);
        change.PotentialBehaviourChange.Should().BeFalse("the guide is an unreviewed draft on both sides: needs review, not a proven rule change");
        compare.Recommendations.Should().Contain(r => r.Kind == "Expected result update" && r.ScenarioId == "manglende-barn");
    }

    private sealed class Values : ITestDataProvider
    {
        public List<TestDataAsset> Items { get; } = [];
        public string Id => "values";
        public string Label => "Values";
        public string Description => "In-test assets.";
        public IReadOnlyList<TestDataAsset> Assets() => Items;
        public IReadOnlyList<TestDataSet> Sets() => [];
    }

    private sealed class NoSources : IReviewSourceEvidenceProvider
    {
        public bool SourceAnalysisEnabled => false;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([]);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    private sealed class NoSchemas : ITestDataSchemaSource
    {
        public Task<TestDataSchemaPackage?> FindAsync(TestDataSchemaReference reference, CancellationToken ct) => Task.FromResult<TestDataSchemaPackage?>(null);
    }

    [Fact]
    public async Task The_summary_for_Dashboard_and_Recommended_Workflow_has_only_persisted_facts()
    {
        await using var db = Context();
        var service = Service(db);
        var empty = await service.SummaryAsync(null, default);
        empty.GuideKey.Should().Be("skolenaervaer");
        empty.Candidates.Should().HaveCount(21);
        empty.Drafts.Should().BeEmpty();
        var plan = await CreateSeven(db);
        await service.SaveSnapshotAsync(plan.Id, new SaveTestPlanSnapshotRequest(plan.Revision, "L1", null), "lead", default);
        var summary = await service.SummaryAsync("skolenaervaer", default);
        summary.Drafts.Single().Cases.Should().Be(7);
        summary.Drafts.Single().MissingTestData.Should().Be(7);
        summary.Snapshots.Single().Sequence.Should().Be(1);
        summary.Basis.Should().BeNull("no source or baseline read for the summary");
        summary.TestData.Should().BeEmpty();
    }
}
