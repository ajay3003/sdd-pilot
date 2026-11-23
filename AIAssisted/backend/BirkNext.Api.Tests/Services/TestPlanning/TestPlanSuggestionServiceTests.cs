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

    private readonly ScenarioDiscoveryCache _discoveries = new();

    private TestPlanSuggestionService Service(AppDbContext db)
    {
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "reg"), NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([_values, new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, null, new TestDataIdentityStore(Path.Combine(_dir, "ids")));
        var sources = new NoSources();
        var testData = new TestDataCatalogService(new TestDataSchemaValidationService(catalog, new NoSchemas()), catalog, registry, new TestDataCandidateService(sources, registry), sources, TimeProvider.System);
        var guides = new SystemGuideService(db, [Skole], Array.Empty<IIntegrationJourneyPack>());
        return new TestPlanSuggestionService(db, guides, new SourceBaselineService(db, new IqrSourceStore(db)), testData, Array.Empty<IIntegrationJourneyPack>(),
            new CriticalE2EStore(Path.Combine(_dir, "e2e"), NullLogger<CriticalE2EStore>.Instance), discoveries: _discoveries);
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

    /// <summary>Gives a case what the Azure DevOps file needs (an expected result on every step), so it can be approved.</summary>
    private static Task<TestPlanDraft> ReadyForApprovalAsync(TestPlanSuggestionService service, TestPlanDraft plan, string scenarioId)
    {
        var c = plan.Cases.Single(x => x.Source.ScenarioId == scenarioId);
        List<string> steps = c.EffectiveSteps.Count > 0 ? [.. c.EffectiveSteps] : ["Utfør scenariet"];
        return service.SaveAsync(plan.Id, Human(plan, (x, part) => x.Source.ScenarioId == scenarioId
            ? part with { Edits = part.Edits with { Steps = steps, StepExpected = [.. steps.Select(_ => (string?)"Som beskrevet i scenariet")] } } : part), "r", default);
    }

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
        workspace.Candidates.Should().HaveCount(22, "every 'What to test' scenario is a candidate, none invented");
        workspace.Candidates.Single(c => c.Source.ScenarioId == "ukjent-xml-versjon").GuideState.Should().Be("NeedsClarification");
        var unknownChild = workspace.Candidates.Single(c => c.Source.ScenarioId == "manglende-barn");
        unknownChild.GuideState.Should().Be("Suggested");
        unknownChild.HasExpected.Should().BeTrue("the current authoritative intake contract and integration tests define HTTP 200 with encrypted holding");
        workspace.Basis!.GuideReviewState.Should().Be("Draft only — not reviewed");
        workspace.Basis.Roles.Should().NotBeEmpty().And.OnlyContain(r => r.State == "NOT_IMPORTED", "nothing is imported in this database");

        var plan = await CreateSeven(db);
        plan.Cases.Select(c => c.Source.ScenarioId).Should().Equal(Seven);
        plan.Cases.Should().OnlyContain(c => c.Review == TestCaseReviewState.Generated && c.Generated.ExpectedBasis != ExpectedResultBasis.ReviewedGuide);
        plan.Cases.Should().OnlyContain(c => !c.Testability.Ready, "a documented scenario is not executable");
        var unknownChildCase = plan.Cases.Single(c => c.Source.ScenarioId == "manglende-barn");
        unknownChildCase.Generated.ExpectedBasis.Should().Be(ExpectedResultBasis.UnreviewedGuide);
        unknownChildCase.Generated.ExpectedResult.Should().Contain("HTTP 200").And.Contain("kryptert").And.Contain("ikke ordinært behandlet");
        unknownChildCase.Generated.Evidence.Should().Contain(e => e.Reference.Contains("M2LB.zip") && e.Reference.Contains("rapportinntak.md"))
            .And.Contain(e => e.Reference.Contains("M2LB.zip") && e.Reference.Contains("ReportIntakeHandler.cs"))
            .And.Contain(e => e.Reference.Contains("M2LB.zip") && e.Reference.Contains("HeldReportTests.cs"));
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
    public async Task Review_decisions_keep_their_history_need_a_rationale_to_exclude_and_approve_only_what_the_export_can_carry()
    {
        await using var db = Context();
        var service = Service(db);
        var plan = await CreateSeven(db);
        var noReason = () => service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, "periodelaas", TestCaseReviewState.Excluded, null), "r", default);
        (await noReason.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("rationale-required");
        var r1 = await service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, "gyldig-rapport", TestCaseReviewState.UnderReview, null), "Kari", default);
        // Approval follows the Azure DevOps export's rules: a guide scenario whose steps state no expected result is refused until they do.
        var early = () => service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(r1.Revision, "gyldig-rapport", TestCaseReviewState.Accepted, null), "Kari", default);
        if (TestPlanSuggestionService.ApproveBlocker(r1.Cases.Single(c => c.Source.ScenarioId == "gyldig-rapport")) is not null)
            (await early.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("steps-incomplete");
        r1 = await ReadyForApprovalAsync(service, r1, "gyldig-rapport");
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
        plan = await ReadyForApprovalAsync(service, plan, "manglende-barn");
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
        empty.Candidates.Should().HaveCount(22);
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

    // ── Project plans (project-scoped discovery) ────────────────────────────────────────────────────────────

    private async Task<ScenarioDiscoveryResult> Discover(string project, string? then = "the book is reserved for the member")
    {
        var service = new TestScenarioDiscoveryService(new IqrSourceStore(null!), [new SpecificationScenarioProvider()], _discoveries);
        return await service.DiscoverAsync(new ScenarioDiscoveryRequest
        {
            ProjectKey = project, ProjectName = "Library",
            Workspace = new BirkNext.TestCoverage.CoverageWorkspaceEvidence
            {
                SpecificationAvailable = true, Requirements = [new("FR-001", "System MUST let a member reserve a book.", null), new("FR-002", "System MUST email a confirmation.", null)],
                AcceptanceScenarios = [new BirkNext.TestCoverage.QaTestCandidate { Id = "US1-1", Title = "Reserve a book", When = "the member reserves the book", Then = then, RequirementIds = ["FR-001"] }],
            },
        }, default);
    }

    [Fact]
    public async Task A_project_plan_is_prepared_from_its_own_discovery_keeps_human_edits_on_rediscovery_and_never_mixes_projects()
    {
        await using var db = Context();
        var service = Service(db);
        var discovery = await Discover("library");
        var scenario = discovery.Candidates.Single(c => c.CanonicalKey.StartsWith("scenario:"));
        var plan = await service.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "library", ProjectName = "Library", DiscoveryId = discovery.DiscoveryId,
            CandidateIds = [.. discovery.Candidates.Select(c => c.CandidateId), "tsc-unknown"] }, "tester", default);
        plan.ProjectKey.Should().Be("library");
        plan.GuideKey.Should().Be("project:library");
        plan.PlanName.Should().Be("Library – test plan", "a name is asked for only at save/export");
        plan.Cases.Should().HaveCount(2, "an id the discovery does not hold is ignored");
        var c = plan.Cases.Single(x => x.Source.ScenarioId == scenario.CandidateId);
        c.Generated.ExpectedResult.Should().Be("the book is reserved for the member");
        c.Generated.StepExpected.Should().Equal("the book is reserved for the member");
        c.EffectiveType.Should().Be(TestCaseType.Functional);

        var edited = Human(plan, (x, p) => x.Source.ScenarioId == scenario.CandidateId
            ? p with { Edits = new TestCaseEdits { Title = "Reserve an available book", Steps = ["Open the book", "Press Reserve"], StepExpected = [null, "Reserved"], TestDataNeed = TestDataNeed.Required } } : p);
        var saved = await service.SaveAsync(plan.Id, edited, "tester", default);
        var mine = saved.Cases.Single(x => x.Source.ScenarioId == scenario.CandidateId);
        mine.EditHistory.Single().Fields.Should().Contain(["Title", "Steps", "Step results", "Test Data need"]);
        mine.Testability.Readiness.Should().Be(CaseReadiness.RequiredButMissing);

        // The specification changes; a new discovery flags the case, and updating the plan keeps the person's edits.
        var changed = await Discover("library", "the book is reserved and a confirmation is shown");
        (await service.DraftAsync(plan.Id, default)).Cases.Single(x => x.Source.ScenarioId == scenario.CandidateId).SourceChanged.Should().BeTrue();
        var refreshed = await service.RefreshAsync(plan.Id, saved.Revision, "tester", default);
        var after = refreshed.Cases.Single(x => x.Source.ScenarioId == scenario.CandidateId);
        after.Generated.ExpectedResult.Should().Be("the book is reserved and a confirmation is shown");
        after.EffectiveTitle.Should().Be("Reserve an available book");
        after.EffectiveSteps.Should().Equal("Open the book", "Press Reserve");
        after.EffectiveStepExpected.Should().Equal(null, "Reserved");
        after.SourceChanged.Should().BeFalse();
        refreshed.DiscoveryId.Should().Be(changed.DiscoveryId);

        (await service.ProjectWorkspaceAsync("other-project", null, true, default)).Drafts.Should().BeEmpty("another project never sees this plan");
        (await service.ProjectWorkspaceAsync("library", null, true, default)).Drafts.Single().Id.Should().Be(plan.Id);
        var foreign = () => service.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "other-project", DiscoveryId = discovery.DiscoveryId, CandidateIds = [scenario.CandidateId] }, "x", default);
        (await foreign.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("discovery-missing");
    }

    [Fact]
    public async Task A_project_without_candidates_can_start_with_a_hand_written_case_and_it_saves_reloads_snapshots_and_exports()
    {
        await using var db = Context();
        var service = Service(db);
        var plan = await service.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "empty-project", ManualCaseTitle = "Log in as a librarian" }, "tester", default);
        var manual = plan.Cases.Single();
        manual.Source.Kind.Should().Be(ScenarioSourceKind.Manual);
        manual.Generated.Steps.Should().BeEmpty("nothing is generated for a hand-written case");
        manual.Missing.Should().Contain("Expected result: NEEDS CLARIFICATION (none in the source)");
        var withSecond = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Return a book", "Check returns", ScenarioCandidateType.UiFunctional), "tester", default);
        withSecond.Cases.Should().HaveCount(2);
        var reloaded = await Service(Context()).DraftAsync(plan.Id, default);
        reloaded.Cases.Select(c => c.EffectiveTitle).Should().Equal("Log in as a librarian", "Return a book");
        var snapshot = await service.SaveSnapshotAsync(plan.Id, new SaveTestPlanSnapshotRequest(reloaded.Revision, "Sprint 1", null), "lead", default);
        snapshot.Unchanged.Should().BeFalse();
        var export = await service.ExportAsync(snapshot.Snapshot.Id, default);
        export.Text.Should().Contain("Project: empty-project").And.Contain("NEEDS CLARIFICATION");
    }

    [Fact]
    public async Task A_historical_guide_plan_keeps_loading_unchanged_next_to_project_plans()
    {
        await using var db = Context();
        var service = Service(db);
        var guidePlan = await CreateSeven(db);
        var discovery = await Discover("library");
        await service.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "library", DiscoveryId = discovery.DiscoveryId, CandidateIds = [discovery.Candidates[0].CandidateId] }, "tester", default);
        var loaded = await service.DraftAsync(guidePlan.Id, default);
        loaded.ProjectKey.Should().BeNull();
        loaded.GuideKey.Should().Be("skolenaervaer");
        loaded.Cases.Select(c => c.Source.ScenarioId).Should().Equal(Seven);
        (await service.WorkspaceAsync("skolenaervaer", true, default)).Drafts.Should().ContainSingle(d => d.Id == guidePlan.Id, "project plans never appear among the guide's plans");
    }

    // ── Saved cases in Test Coverage / Requirements Traceability ─────────────────────────────────────────────

    [Fact]
    public async Task Saved_project_cases_are_traced_to_the_reviewed_specification_after_reload_and_never_counted_as_executed_or_for_another_project()
    {
        const string spec = "specs/001-library/spec.md";
        await using var db = Context();
        var service = Service(db);
        var discovery = await new TestScenarioDiscoveryService(new IqrSourceStore(null!), [new SpecificationScenarioProvider()], _discoveries).DiscoverAsync(new ScenarioDiscoveryRequest
        {
            ProjectKey = "library", ProjectName = "Library",
            Workspace = new BirkNext.TestCoverage.CoverageWorkspaceEvidence
            {
                SpecificationAvailable = true, SpecificationArtifacts = [new("spec-1", spec, null, "fp")],
                Requirements = [new("FR-001", "System MUST let a member reserve a book.", null), new("FR-002", "System MUST email a confirmation.", null)],
                AcceptanceScenarios = [new BirkNext.TestCoverage.QaTestCandidate { Id = "US1-1", Title = "Reserve a book", When = "the member reserves the book", Then = "it is reserved", RequirementIds = ["FR-001"] }],
            },
        }, default);
        var plan = await service.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "library", ProjectName = "Library", DiscoveryId = discovery.DiscoveryId,
            CandidateIds = [.. discovery.Candidates.Select(c => c.CandidateId)] }, "tester", default);
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Log in as a librarian", "Sign in", ScenarioCandidateType.UiFunctional), "tester", default);
        var scenario = plan.Cases.Single(c => c.Generated.Evidence.Any(e => e.Kind == "specification")).Source.ScenarioId;
        await service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, scenario, TestCaseReviewState.Accepted, "Ready"), "lead", default);
        var guidePlan = await CreateSeven(db);

        // A fresh context: everything below is read back from the database.
        var view = await Service(Context()).PreparedCaseTraceAsync("library", [spec], ["FR-001", "FR-002"], default);

        view.Cases.Should().Be(3);
        view.CaseList.Should().OnlyContain(c => c.DraftId == plan.Id && c.Execution == PreparedCaseTraceView.NotExecuted);
        view.Requirements.Select(r => (r.RequirementId, r.State)).Should().Equal(("FR-001", "Approved test case"), ("FR-002", "Prepared, not approved yet"));
        view.CaseList.Single(c => c.SourceKind == ScenarioSourceKind.Manual).Should().Match<PreparedCaseTrace>(c => c.Title == "Log in as a librarian" && !c.Counted);
        view.UnlinkedCases.Should().Be(1, "the hand-written case names no requirement: visible, not counted");
        view.CaseList.Should().NotContain(c => c.DraftId == guidePlan.Id, "the Skolenærvær guide plan belongs to MT-7, not to this project");

        (await Service(Context()).PreparedCaseTraceAsync("other-project", [spec], null, default)).Cases.Should().Be(0, "another project never sees these cases");
        (await Service(Context()).DraftAsync(guidePlan.Id, default)).Cases.Select(c => c.Source.ScenarioId).Should().Equal(Seven, "the guide plan is unchanged");
    }

    // ── Manual requirement links ─────────────────────────────────────────────────────────────────────────────

    private const string LinkSpec = "specs/001-library/spec.md";

    private static ProjectRequirementCatalog Catalog(string project = "library", params string[] ids) => new()
    {
        ProjectKey = project,
        Requirements = [.. (ids.Length == 0 ? ["FR-001", "FR-002", "FR-003"] : ids).Select(id => new ProjectRequirementOption(id, $"System MUST do {id}.", LinkSpec))],
    };

    private static ManualRequirementLink Want(string id, string path = LinkSpec) => new() { RequirementId = id, SpecificationPath = path, Title = "typed by the client" };

    /// <summary>The save a browser sends: every case's human part as it stands, with the requirement links given per case (others unchanged).</summary>
    private static SaveTestPlanDraftRequest Save(TestPlanDraft plan, Dictionary<string, List<ManualRequirementLink>> links, ProjectRequirementCatalog? catalog, Func<TestCaseDraft, TestCaseEdits>? edits = null) => new()
    {
        BaseRevision = plan.Revision, PlanName = plan.PlanName, ScenarioIds = [.. plan.Cases.Select(c => c.Source.ScenarioId)], Suites = plan.Suites, RequirementCatalog = catalog,
        Cases = [.. plan.Cases.Select(c => new TestCaseHumanPart
        {
            ScenarioId = c.Source.ScenarioId, SuiteId = c.SuiteId, Edits = edits?.Invoke(c) ?? c.Edits, Risk = c.Risk, TestDataAssetIds = [.. c.TestData.Select(d => d.AssetId)],
            RequirementLinks = links.TryGetValue(c.Source.ScenarioId, out var l) ? l : [.. c.RequirementLinks],
        })],
    };

    private async Task<TestPlanDraft> ManualPlan(TestPlanSuggestionService service, string project = "library", params string[] titles)
    {
        var plan = await service.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = project, ManualCaseTitle = titles.Length > 0 ? titles[0] : "Log in as a librarian" }, "tester", default);
        foreach (var title in titles.Skip(1))
            plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, title, null, null), "tester", default);
        return plan;
    }

    [Fact]
    public async Task A_tester_links_one_and_several_existing_requirements_and_the_links_survive_a_reload_and_a_restart()
    {
        await using var db = Context();
        var service = Service(db);
        var plan = await ManualPlan(service, "library", "Log in as a librarian", "Return a book");
        var (first, second) = (plan.Cases[0].Source.ScenarioId, plan.Cases[1].Source.ScenarioId);

        plan = await service.SaveAsync(plan.Id, Save(plan, new() { [first] = [Want("FR-001")], [second] = [Want("fr-002"), Want("FR-003")] }, Catalog()), "anna", default);

        // A new context and a new service: what a restarted API reads from the database.
        var reloaded = await Service(Context()).DraftAsync(plan.Id, default);
        reloaded.Cases.Single(c => c.Source.ScenarioId == first).RequirementLinks.Should().ContainSingle().Which.Should().Match<ManualRequirementLink>(l =>
            l.RequirementId == "FR-001" && l.SpecificationPath == LinkSpec && l.Title == "System MUST do FR-001." && l.LinkedBy == "anna" && l.LinkedAt != default,
            "the server records the catalog's title, who and when — never the client's text");
        reloaded.Cases.Single(c => c.Source.ScenarioId == second).RequirementLinks.Select(l => l.RequirementId).Should().Equal("FR-002", "FR-003");
        reloaded.Cases.Single(c => c.Source.ScenarioId == second).EditHistory.Last().Fields.Should().Contain(TestPlanSuggestionService.LinkedRequirementsField);

        var view = await Service(Context()).PreparedCaseTraceAsync("library", [LinkSpec], ["FR-001", "FR-002", "FR-003", "FR-004"], default);
        view.Requirements.Select(r => (r.RequirementId, r.ManualCases, r.AutomaticCases, r.State)).Should().Equal(
            ("FR-001", 1, 0, "Prepared, not approved yet"), ("FR-002", 1, 0, "Prepared, not approved yet"), ("FR-003", 1, 0, "Prepared, not approved yet"));
        view.WithoutTestDesign.Select(g => g.RequirementId).Should().Equal("FR-004");
        view.LinkedCases.Should().Be(2);
        view.CaseList.Should().OnlyContain(c => c.Execution == PreparedCaseTraceView.NotExecuted);
    }

    [Fact]
    public async Task A_link_to_a_requirement_the_project_does_not_have_or_to_another_projects_requirements_is_refused_and_nothing_is_saved()
    {
        await using var db = Context();
        var service = Service(db);
        var plan = await ManualPlan(service);
        var sid = plan.Cases[0].Source.ScenarioId;

        var unknown = () => service.SaveAsync(plan.Id, Save(plan, new() { [sid] = [Want("FR-999")] }, Catalog()), "anna", default);
        (await unknown.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("requirement-unknown");
        var otherFile = () => service.SaveAsync(plan.Id, Save(plan, new() { [sid] = [Want("FR-001", "specs/002-other/spec.md")] }, Catalog()), "anna", default);
        (await otherFile.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("requirement-unknown", "a requirement is identified by id and specification file");
        var crossProject = () => service.SaveAsync(plan.Id, Save(plan, new() { [sid] = [Want("FR-001")] }, Catalog("other-project")), "anna", default);
        (await crossProject.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("requirement-other-project");
        var noCatalog = () => service.SaveAsync(plan.Id, Save(plan, new() { [sid] = [Want("FR-001")] }, null), "anna", default);
        (await noCatalog.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("requirements-unavailable");

        var stored = await Service(Context()).DraftAsync(plan.Id, default);
        stored.Revision.Should().Be(plan.Revision);
        stored.Cases[0].RequirementLinks.Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_a_manual_link_keeps_the_cases_own_automatic_evidence_and_editing_the_case_keeps_its_links()
    {
        await using var db = Context();
        var service = Service(db);
        var discovery = await new TestScenarioDiscoveryService(new IqrSourceStore(null!), [new SpecificationScenarioProvider()], _discoveries).DiscoverAsync(new ScenarioDiscoveryRequest
        {
            ProjectKey = "library", ProjectName = "Library",
            Workspace = new BirkNext.TestCoverage.CoverageWorkspaceEvidence
            {
                SpecificationAvailable = true, SpecificationArtifacts = [new("spec-1", LinkSpec, null, "fp")],
                Requirements = [new("FR-001", "System MUST do FR-001.", null), new("FR-002", "System MUST do FR-002.", null)],
            },
        }, default);
        var candidate = discovery.Candidates.First(c => c.Title.StartsWith("FR-001", StringComparison.Ordinal));
        var plan = await service.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "library", DiscoveryId = discovery.DiscoveryId, CandidateIds = [candidate.CandidateId] }, "tester", default);
        var sid = plan.Cases.Single().Source.ScenarioId;

        // A manual confirmation of the same requirement the evidence names, plus a second requirement.
        plan = await service.SaveAsync(plan.Id, Save(plan, new() { [sid] = [Want("FR-001"), Want("FR-002")] }, Catalog()), "anna", default);
        var linkedAt = plan.Cases.Single().RequirementLinks.Select(l => l.LinkedAt).ToList();
        var traced = (await service.PreparedCaseTraceAsync("library", [LinkSpec], ["FR-001", "FR-002"], default)).CaseList.Single();
        traced.Requirements.Select(r => (r.RequirementId, r.Basis, r.Counted)).Should().Equal(
            ("FR-001", PreparedCaseLinkBasis.SpecificationEvidence, true), ("FR-001", PreparedCaseLinkBasis.ManualConfirmation, true), ("FR-002", PreparedCaseLinkBasis.ManualConfirmation, true));

        // Editing the title (links sent as they stand, no catalog needed) keeps who/when of every link.
        plan = await service.SaveAsync(plan.Id, Save(plan, [], null, c => c.Edits with { Title = "Register a loan (edited)" }), "bob", default);
        plan.Cases.Single().RequirementLinks.Select(l => l.LinkedAt).Should().Equal(linkedAt);
        plan.Cases.Single().RequirementLinks.Should().OnlyContain(l => l.LinkedBy == "anna");
        // An older client that sends no links at all leaves them unchanged.
        var older = Save(plan, [], null);
        plan = await service.SaveAsync(plan.Id, older with { Cases = [.. older.Cases.Select(c => c with { RequirementLinks = null })] }, "bob", default);
        plan.Cases.Single().RequirementLinks.Should().HaveCount(2);

        // Removing every manual link: the automatic evidence stays and still counts.
        plan = await service.SaveAsync(plan.Id, Save(plan, new() { [sid] = [] }, null), "anna", default);
        plan.Cases.Single().RequirementLinks.Should().BeEmpty();
        plan.Cases.Single().Generated.Requirements.Select(r => r.RuleId).Should().Equal("FR-001");
        var after = await Service(Context()).PreparedCaseTraceAsync("library", [LinkSpec], ["FR-001", "FR-002"], default);
        after.Requirements.Select(r => (r.RequirementId, r.AutomaticCases, r.ManualCases)).Should().Equal(("FR-001", 1, 0));
        after.WithoutTestDesign.Select(g => g.RequirementId).Should().Equal("FR-002");
    }

    [Fact]
    public async Task A_link_whose_requirement_left_the_specification_is_kept_flagged_and_never_counted()
    {
        await using var db = Context();
        var service = Service(db);
        var plan = await ManualPlan(service);
        var sid = plan.Cases[0].Source.ScenarioId;
        plan = await service.SaveAsync(plan.Id, Save(plan, new() { [sid] = [Want("FR-003")] }, Catalog()), "anna", default);

        // FR-003 was removed from the specification; a later save with the current catalog keeps the held link (the tester removes it explicitly).
        plan = await service.SaveAsync(plan.Id, Save(plan, [], Catalog("library", "FR-001", "FR-002"), c => c.Edits with { Purpose = "Edited" }), "anna", default);
        plan.Cases[0].RequirementLinks.Select(l => l.RequirementId).Should().Equal("FR-003");

        var view = await Service(Context()).PreparedCaseTraceAsync("library", [LinkSpec], ["FR-001", "FR-002"], default);
        var link = view.CaseList.Single().Requirements.Single();
        link.Should().Match<PreparedCaseRequirementLink>(l => !l.Counted && l.Validity == PreparedLinkValidity.RequirementNotFound && l.Basis == PreparedCaseLinkBasis.ManualConfirmation);
        view.Requirements.Should().BeEmpty();
        view.StaleManualLinks.Should().Be(1);
        view.Limitations.Should().Contain(l => l.Contains("no longer in the reviewed specification"));
        view.WithoutTestDesign.Select(g => g.RequirementId).Should().Equal("FR-001", "FR-002");
    }

    [Fact]
    public async Task Several_cases_on_one_requirement_count_it_once_an_excluded_case_never_counts_and_another_project_sees_nothing()
    {
        await using var db = Context();
        var service = Service(db);
        var plan = await ManualPlan(service, "library", "Case A", "Case B", "Case C");
        var ids = plan.Cases.Select(c => c.Source.ScenarioId).ToList();
        plan = await service.SaveAsync(plan.Id, Save(plan, new() { [ids[0]] = [Want("FR-001")], [ids[1]] = [Want("FR-001")], [ids[2]] = [Want("FR-002")] }, Catalog()), "anna", default);
        plan = await service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, ids[2], TestCaseReviewState.Excluded, "Out of scope"), "lead", default);
        var other = await ManualPlan(service, "other-project", "Other project's case");
        await service.SaveAsync(other.Id, Save(other, new() { [other.Cases[0].Source.ScenarioId] = [Want("FR-002")] }, Catalog("other-project")), "anna", default);

        var view = await Service(Context()).PreparedCaseTraceAsync("library", [LinkSpec], ["FR-001", "FR-002"], default);
        view.Requirements.Should().ContainSingle().Which.Should().Match<PreparedRequirementTrace>(r => r.RequirementId == "FR-001" && r.CaseKeys.Count == 2 && r.ManualCases == 2);
        view.RequirementsWithPreparedCaseOnly.Should().Be(1, "FR-001 once, however many cases cover it");
        view.CaseList.Single(c => c.CaseId == ids[2]).Requirements.Single().Counted.Should().BeFalse("an excluded case never counts");
        view.WithoutTestDesign.Select(g => g.RequirementId).Should().Equal("FR-002");
        view.CaseList.Should().HaveCount(3).And.OnlyContain(c => c.DraftId == plan.Id, "the other project's linked case is never read");
    }

    [Fact]
    public async Task A_plan_without_manual_links_stores_no_link_field_so_its_snapshot_identity_is_unchanged()
    {
        await using var db = Context();
        var service = Service(db);
        var plan = await ManualPlan(service);
        var json = await db.TestPlanDrafts.AsNoTracking().Where(d => d.Id == plan.Id).Select(d => d.ContentJson).SingleAsync();
        json.Should().NotContain("requirementLinks");
        var first = await service.SaveSnapshotAsync(plan.Id, new SaveTestPlanSnapshotRequest(plan.Revision, "D1", null), "lead", default);
        plan = await service.SaveAsync(plan.Id, Save(plan, [], null), "lead", default);
        (await service.SaveSnapshotAsync(plan.Id, new SaveTestPlanSnapshotRequest(plan.Revision, "D1", null), "lead", default)).Unchanged.Should().BeTrue();
        first.Unchanged.Should().BeFalse();
    }
}
