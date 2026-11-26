using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.Specifications;
using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.Integrations;
using BirkNext.SourceEvidence;
using BirkNext.SystemUnderstanding;
using BirkNext.TestCoverage;
using BirkNext.TestData;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Source-driven test planning end to end on a fresh migrated PostgreSQL database (2026-11-23), with <b>no static guide registered</b>: two imports
/// of one application (synthetic M2LB-like specification in own wording), the consistent source basis (analysis baseline, server-held
/// specification), the application guide generated from sources and reviewed by a person, the two-snapshot comparison (unchanged, changed,
/// removed, new, conflicting), historical inspection that never moves the current discovery, Refresh that never rolls back, and every manual case
/// and old revision kept.
/// </summary>
public sealed class SourceDrivenPlanningTests : IAsyncLifetime, IDisposable
{
    private const string Repository = "repo:m2lb-synthetic";
    private const string SpecPath = "specs/001-intake/spec.md";
    private readonly string _database = TestPostgres.TemporaryName("birknext_sdp");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sdp-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ScenarioDiscoveryCache _discoveries = new();

    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);

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

    // ── Fixture ─────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Stack(TestScenarioDiscoveryService Discovery, TestPlanSuggestionService Plans, SystemGuideService Guides, SystemGuideGenerationService Generation,
        SourceBaselineService Baselines, IqrSourceStore Store);

    /// <summary>The services as the API wires them; <paramref name="catalog"/> empty = no static guide is registered.</summary>
    private Stack Services(AppDbContext db, IEnumerable<SystemGuideCatalogEntry>? catalog = null, ITestScenarioCandidateProvider? extra = null)
    {
        var store = new IqrSourceStore(db);
        var baselines = new SourceBaselineService(db, store);
        var specs = new SpecificationCatalogService(db);
        var guides = new SystemGuideService(db, catalog ?? [], Array.Empty<IIntegrationJourneyPack>());
        var resolver = new ScenarioSourceBasisResolver(baselines, store, specs);
        List<ITestScenarioCandidateProvider> providers = [new SpecificationScenarioProvider(), new SystemGuideScenarioProvider(guides), new ExistingTestScenarioProvider()];
        if (extra is not null) providers.Add(extra);
        var discoveryStore = new ScenarioDiscoveryStore(db, _discoveries);
        var discovery = new TestScenarioDiscoveryService(store, providers, _discoveries, store: discoveryStore, basisResolver: resolver);
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "reg"), NullLogger<TestDataRegistryStore>.Instance);
        var testDataCatalog = new TestDataCatalog([new NoValues(), new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, null, new TestDataIdentityStore(Path.Combine(_dir, "ids")));
        var sources = new NoSources();
        var testData = new TestDataCatalogService(new TestDataSchemaValidationService(testDataCatalog, new NoSchemas()), testDataCatalog, registry, new TestDataCandidateService(sources, registry), sources, TimeProvider.System);
        var plans = new TestPlanSuggestionService(db, guides, baselines, testData, Array.Empty<IIntegrationJourneyPack>(), new CriticalE2EStore(Path.Combine(_dir, "e2e"), NullLogger<CriticalE2EStore>.Instance),
            discoveries: _discoveries, discoveryStore: discoveryStore, specificationCatalogs: specs, basisResolver: resolver);
        return new Stack(discovery, plans, guides, new SystemGuideGenerationService(guides, providers, store, specs), baselines, store);
    }

    private static string Spec(params string[] scenarios) =>
        "# Feature Specification: Attendance intake\n\n## User Scenarios & Testing\n\n### User Story 1 - Report intake (Priority: P1)\n\n**Acceptance Scenarios**:\n\n"
        + string.Join("\n", scenarios.Select((s, i) => $"{i + 1}. {s}")) + "\n\n## Requirements\n\n### Functional Requirements\n\n"
        + "- **FR-001**: System MUST store a valid attendance report.\n- **FR-002**: System MUST reject a report for a closed period.\n";

    private const string A = "**Given** a valid attendance report, **When** the report is submitted, **Then** the report is stored and the service responds 200.";
    private const string B = "**Given** a report for a closed period, **When** the report is submitted, **Then** the report is rejected with status 400.";
    private const string BChanged = "**Given** a report for a closed period, **When** the report is submitted, **Then** the report is rejected with status 422.";
    private const string C = "**Given** a duplicate attendance report, **When** the report is submitted, **Then** the duplicate is ignored.";
    private const string D = "**Given** a report without a school, **When** the report is submitted, **Then** the report is rejected with status 400.";
    private const string E = "**Given** no matching child exists, **When** a report arrives, **Then** the service responds 200 and the report is held encrypted.";

    /// <summary>An import: a Source Analysis snapshot of the application and the specification captured with it (as Project Import does).</summary>
    private async Task<Guid> ImportAsync(string importId, string archive, string content, string spec, DateTimeOffset at, string specPath = SpecPath)
    {
        var id = Guid.NewGuid();
        await using var db = Context();
        db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord
        {
            Id = id, EnvironmentId = "", IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = at,
            EvidenceJson = JsonSerializer.Serialize(new IqrSourceSnapshot
            {
                Id = id, Archive = new SourceArchive(archive, new string(archive.Length % 2 == 0 ? 'a' : 'b', 63) + content[^1], 10, content), AnalyzedAt = at,
                Repository = new SourceRepositoryIdentity(Repository, "M2LB (syntetisk)", "Test"),
                Projects = [new SourceProject("SkoleService.Api", "SkoleService/src/SkoleService.Api", false, "Api", SourceConfidence.Confirmed)],
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        });
        await db.SaveChangesAsync();
        await new SpecificationCatalogService(db).CaptureAsync(importId, archive, id, [new SpecificationCatalogService.Document(specPath, spec)], default);
        return id;
    }

    /// <summary>A browser that still holds an older, different specification text (it must never contaminate discovery against the basis).</summary>
    private static CoverageWorkspaceEvidence StaleBrowser() => new()
    {
        SpecificationAvailable = true, SpecificationArtifacts = [new("spec-1", SpecPath, null, "fp-old")],
        AcceptanceScenarios = [new QaTestCandidate { Id = "scenario-1", Title = "Obsolete browser scenario", Given = "an obsolete draft", When = "nothing", Then = "the obsolete result" }],
    };

    private static List<DiscoveredScenario> Scenarios(ScenarioDiscoveryResult r) =>
        [.. r.Candidates.Where(c => c.CapabilityId == TestScenarioDiscoveryEngine.Specification && c.CanonicalKey.StartsWith("scenario:", StringComparison.Ordinal))];

    private static DiscoveredScenario About(ScenarioDiscoveryResult r, string words) => Scenarios(r).Single(c => (c.Preconditions.FirstOrDefault() ?? "").Contains(words, StringComparison.OrdinalIgnoreCase));

    private static TestCaseDraft Case(TestPlanDraft plan, string words) =>
        plan.Cases.Single(c => c.Source.Kind == ScenarioSourceKind.ProjectDiscovery && (c.Generated.Preconditions ?? "").Contains(words, StringComparison.OrdinalIgnoreCase));

    // ── The two-snapshot workflow ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_imports_of_one_application_without_a_static_guide_detect_unchanged_changed_removed_new_and_conflicting_scenarios_and_keep_every_manual_case()
    {
        // ── Day 1: import S1, generate the application guide, discover, plan, review ───────────────────────────────
        var s1 = await ImportAsync("imp1", "m2lb-v1.zip", "content-v1", Spec(A, B, C), DateTimeOffset.UtcNow.AddDays(-1));
        Guid planId; string guideKey; int day1Revision;
        await using (var db = Context())
        {
            var x = Services(db);
            var jobs = await x.Guides.EnqueueGenerationAsync((await x.Store.FindSourceAnalysisAsync(s1))!, default);
            jobs.Should().ContainSingle().Which.GuideKey.Should().StartWith("app-", "no static guide is registered: the application gets its own guide");
            guideKey = jobs[0].GuideKey;
            (await x.Generation.ProcessPendingAsync(5, default)).Should().Be(1);
            (await x.Guides.JobsAsync(guideKey, default)).Single().Status.Should().Be("Succeeded");
            var candidate = (await x.Guides.CandidatesAsync(guideKey, default)).Single();
            candidate.Changes.Where(c => c.Kind == "NewScenario").Should().HaveCount(3, "three acceptance scenarios, each proposed once");
            candidate.Content.Rules.Select(r => r.Id).Should().BeEquivalentTo(["req:FR-001", "req:FR-002"], "requirements no scenario names become business rules, never scenarios");
            candidate.Content.Rules.Should().OnlyContain(r => r.Implementation == GuideEvidenceClass.Specified && r.Evidence.All(e => e.SourceLabel == "m2lb-v1.zip"));

            var d1 = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest
            {
                ProjectKey = "import:imp1", ProjectName = "M2LB", SnapshotId = s1, MemberSnapshotIds = [s1], Workspace = StaleBrowser(),
            }, default);
            d1.Basis!.Kind.Should().Be(DiscoveryBasisKind.Current);
            d1.Basis.SpecificationOrigin.Should().Be(SpecificationOrigin.ImportedSnapshot, "the imported specification is authoritative");
            d1.Basis.Complete.Should().BeTrue();
            d1.Limitations.Should().Contain(l => l.Contains("annen versjon enn den importerte"), "the browser's different text is reported, not used");
            Scenarios(d1).Should().HaveCount(3).And.NotContain(c => c.Title.Contains("Obsolete"));
            d1.Capabilities.Single(c => c.Id == TestScenarioDiscoveryEngine.SystemGuide).State.Should().NotBe(DiscoveryCapabilityState.Failed, "no static guide never breaks discovery");

            var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest
            {
                ProjectKey = "import:imp1", ProjectName = "M2LB", DiscoveryId = d1.DiscoveryId, CandidateIds = [.. Scenarios(d1).Select(c => c.CandidateId)],
            }, "tester", default);
            plan.ApplicationKey.Should().Be(Repository);
            plan.DiscoveryBasis!.Fingerprint.Should().Be(d1.Basis.Fingerprint);
            planId = plan.Id;
            var bId = Case(plan, "closed period").Source.ScenarioId;
            plan = await x.Plans.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Rapport for stengt periode via portalen", null, null, ScenarioKey: bId), "tester", default);
            var aId = Case(plan, "valid attendance").Source.ScenarioId;
            plan = await x.Plans.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == aId ? part with { Edits = part.Edits with { ObservationMethod = "Se kvitteringen i portalen" } } : part), "tester", default);
            plan = await x.Plans.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, aId, TestCaseReviewState.Accepted, null), "lead", default);
            plan = await x.Plans.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, bId, TestCaseReviewState.Accepted, null), "lead", default);
            day1Revision = plan.Revision;
            AdoTestPlanExporter.Preview(plan, new AdoExportRequest { ProjectKey = plan.ProjectKey!, DraftId = plan.Id, PlanRevision = plan.Revision, IncludeNotApproved = true })
                .Cases.Should().OnlyContain(c => c.AzureDevOpsId == null, "no id is invented");

            // A person reviews the generated guide, adds a scenario of their own (from a business decision) and publishes it.
            foreach (var change in candidate.Changes) await x.Guides.DecideAsync(guideKey, candidate.Id, new DecideGuideChangeRequest(change.ChangeId!, "Accepted", null, null), "lead", default);
            var draft = await x.Guides.ApplyCandidateAsync(guideKey, candidate.Id, new ApplyGuideCandidateRequest(null), "lead", default);
            draft.Revision.Should().Be(1);
            draft.Content.Scenarios.Should().HaveCount(3).And.OnlyContain(s => s.SourceIdentity != null);
            var written = draft.Content with
            {
                Scenarios = [.. draft.Content.Scenarios, new GuideTestScenario { Id = "barnet-finnes-ikke", Title = "Barnet finnes ikke", Objective = "Rapport for ukjent barn",
                    Preconditions = "No matching child exists. A report arrives.", ExpectedOutcome = "The service responds 503." }],
            };
            await x.Guides.SaveDraftAsync(guideKey, new SaveGuideDraftRequest(written, "Skrevet av fagansvarlig", draft.ContentFingerprint), "lead", default);
            await x.Guides.SubmitAsync(guideKey, 1, "lead", default);
            await x.Guides.ApproveAsync(guideKey, 1, "reviewer", default);
            await x.Guides.PublishAsync(guideKey, 1, default);
        }

        // ── Day 2: import S2 (one unchanged, one changed, one removed, one new, one conflicting) ───────────────────
        var s2 = await ImportAsync("imp2", "m2lb-v2.zip", "content-v2", Spec(A, BChanged, D, E), DateTimeOffset.UtcNow);
        await using (var db = Context())
        {
            var x = Services(db);
            // Freshness before anyone discovers again: the plan says a newer source version exists; nothing is rewritten.
            var workspace = await x.Plans.ProjectWorkspaceAsync("import:imp1", null, true, default);
            workspace.PlanStatus![planId].State.Should().Be("Ny kildeversjon");
            workspace.PlanStatus[planId].NewerSources.Should().ContainSingle().Which.NewerArchiveName.Should().Contain("m2lb-v2.zip");
            (await x.Plans.DraftAsync(planId, default)).Revision.Should().Be(day1Revision, "a new import never rewrites a plan");

            // The guide proposal of S2 against the published guide: a changeset, never duplicates.
            await x.Guides.EnqueueGenerationAsync((await x.Store.FindSourceAnalysisAsync(s2))!, default);
            await x.Generation.ProcessPendingAsync(5, default);
            var proposal = (await x.Guides.CandidatesAsync(guideKey, default)).First(c => c.SourceSnapshotId == s2);
            proposal.Changes.Should().Contain(c => c.Kind == "ConflictsWithReviewed" && c.PreviousExpected!.Contains("400") && c.ProposedExpected!.Contains("422"));
            proposal.Changes.Should().Contain(c => c.Kind == "EvidenceRemoved" && c.Proposed!.Preconditions!.Contains("duplicate"));
            proposal.Changes.Should().Contain(c => c.Kind == "NewScenario" && c.Proposed!.Preconditions!.Contains("without a school"));
            proposal.Changes.Should().NotContain(c => c.Proposed != null && c.Proposed.Preconditions != null && c.Proposed.Preconditions.Contains("valid attendance"), "the unchanged scenario is not proposed again");
            proposal.Content.Scenarios.Select(s => s.SourceIdentity).OfType<string>().Should().OnlyHaveUniqueItems("no duplicate of an existing scenario is appended");
            (await x.Guides.GetAsync(guideKey, false, default)).Shown!.Revision.Should().Be(1, "the published guide is never rewritten by a proposal");

            // Two different versions and no analysis baseline: the source is unresolved, so nothing can be called removed.
            var ambiguous = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp2", SnapshotId = s2, MemberSnapshotIds = [s2] }, default);
            ambiguous.Basis!.Complete.Should().BeFalse();
            ambiguous.Basis.Members.Single().StateCode.Should().Be("BASELINE_NOT_SELECTED");
            var unresolved = await x.Plans.DraftAsync(planId, default);
            Case(unresolved, "duplicate").SourceState.Should().NotBe(CaseSourceState.Removed, "an incomplete comparison never removes a scenario");

            await x.Baselines.SelectBaselineAsync(Repository, s2, "lead", "Ny versjon", default);
            var d2 = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp2", ProjectName = "M2LB", SnapshotId = s2, MemberSnapshotIds = [s2] }, default);
            d2.Basis!.Complete.Should().BeTrue();
            d2.Basis.ApplicationKey.Should().Be(Repository, "both imports are one application");
            var e = About(d2, "no matching child");
            e.Conflicts.Should().ContainSingle(k => k.Kind == ConflictKind.ContradictoryExpectedResult).Which.Claims.Select(c => c.Expected)
                .Should().Contain(x2 => x2.Contains("503")).And.Contain(x2 => x2.Contains("200"));
            About(d2, "closed period").Conflicts.Should().Contain(k => k.Kind == ConflictKind.DifferentSourceVersion, "the reviewed guide still says 400");

            var flagged = await x.Plans.DraftAsync(planId, default);
            Case(flagged, "valid attendance").SourceState.Should().Be(CaseSourceState.Current, Case(flagged, "valid attendance").SourceStateReason);
            Case(flagged, "closed period").SourceState.Should().Be(CaseSourceState.Changed);
            Case(flagged, "duplicate").SourceState.Should().Be(CaseSourceState.Removed);
            Case(flagged, "duplicate").SourceStateReason.Should().StartWith("Utgått scenario");
            flagged.Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual).SourceState.Should().BeNull("a hand-written case is never compared with a source");

            var impact = await x.Plans.ImpactAsync(planId, default);
            impact.Cases.Single(i => i.CaseId == Case(flagged, "closed period").Source.ScenarioId).Kinds.Should().Contain(CaseImpactKind.ExpectedResultChanged);
            impact.Cases.Single(i => i.CaseId == Case(flagged, "duplicate").Source.ScenarioId).Kinds.Should().Equal(CaseImpactKind.ScenarioRemoved);
            impact.Cases.Should().NotContain(i => i.CaseId == Case(flagged, "valid attendance").Source.ScenarioId, "unchanged: no impact");
            impact.Cases.Single(i => i.CaseId == Case(flagged, "closed period").Source.ScenarioId).ReviewedBefore.Should().BeTrue();
            impact.NewScenarios.Should().Contain(i => i.Title.Contains("school", StringComparison.OrdinalIgnoreCase)).And.Contain(i => i.Title.Contains("matching child", StringComparison.OrdinalIgnoreCase));

            // A deliberate look at S1 is historical: it never moves the current discovery, so the plan keeps comparing against S2.
            var history = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp2", SnapshotId = s1, MemberSnapshotIds = [s2], Inspect = true }, default);
            history.Basis!.Kind.Should().Be(DiscoveryBasisKind.Historical);
            Scenarios(history).Should().Contain(c => (c.Preconditions.FirstOrDefault() ?? "").Contains("duplicate"));
            (await x.Discovery.LastAsync("import:imp2", null, default))!.DiscoveryId.Should().Be(d2.DiscoveryId);
            Case(await x.Plans.DraftAsync(planId, default), "duplicate").SourceState.Should().Be(CaseSourceState.Removed, "still compared with S2 after inspecting S1");

            // Update: changed goes back to review, removed is superseded (kept), unchanged and manual cases keep their content and edits.
            var refreshed = await x.Plans.RefreshAsync(planId, flagged.Revision, "tester", default);
            Case(refreshed, "valid attendance").Review.Should().Be(TestCaseReviewState.Accepted);
            Case(refreshed, "valid attendance").Edits.ObservationMethod.Should().Be("Se kvitteringen i portalen");
            Case(refreshed, "closed period").Review.Should().Be(TestCaseReviewState.UnderReview);
            Case(refreshed, "closed period").EffectiveExpected.Should().Contain("422");
            Case(refreshed, "duplicate").Review.Should().Be(TestCaseReviewState.Superseded);
            Case(refreshed, "duplicate").EffectiveTitle.Should().NotBeNullOrWhiteSpace("the original content is kept");
            refreshed.Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual).EffectiveTitle.Should().Be("Rapport for stengt periode via portalen");
            refreshed.Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual).ScenarioKey.Should().Be(Case(refreshed, "closed period").Source.ScenarioId, "the manual case follows its scenario");
            refreshed.DiscoveryBasis!.Fingerprint.Should().Be(d2.Basis.Fingerprint);
            Case(await x.Plans.RevisionAsync(planId, day1Revision, default), "closed period").Generated.ExpectedResult.Should().Contain("400", "the historical revision is preserved");

            // A new, conflicting scenario of the newer import is added to the same plan: it can never be approved until a person resolves it.
            var withE = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest
            {
                ProjectKey = "import:imp2", DiscoveryId = d2.DiscoveryId, CandidateIds = [e.CandidateId], DraftId = planId, BaseRevision = refreshed.Revision,
            }, "tester", default);
            var eCase = Case(withE, "no matching child");
            eCase.HasUnresolvedConflict.Should().BeTrue();
            eCase.Testability.Readiness.Should().Be(CaseReadiness.NeedsClarification);
            var approve = () => x.Plans.ReviewAsync(planId, new ReviewTestCaseRequest(withE.Revision, eCase.Source.ScenarioId, TestCaseReviewState.Accepted, null), "lead", default);
            (await approve.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("conflict-unresolved");
            var resolved = await x.Plans.ResolveConflictAsync(planId, new ResolveConflictRequest(withE.Revision, eCase.Source.ScenarioId, "The service responds 200 and the report is held encrypted.",
                e.CandidateId, "Bekreftet av fagansvarlig: ukjent barn er ikke en teknisk feil."), "lead", default);
            Case(resolved, "no matching child").HasUnresolvedConflict.Should().BeFalse();
            (await x.Plans.ReviewAsync(planId, new ReviewTestCaseRequest(resolved.Revision, eCase.Source.ScenarioId, TestCaseReviewState.Accepted, null), "lead", default))
                .Cases.Single(c => c.Source.ScenarioId == eCase.Source.ScenarioId).Review.Should().Be(TestCaseReviewState.Accepted);

            // Export: the superseded case is never exported, the manual case is, and no id is invented.
            var current = await x.Plans.DraftAsync(planId, default);
            var export = AdoTestPlanExporter.Preview(current, new AdoExportRequest { ProjectKey = current.ProjectKey!, DraftId = current.Id, PlanRevision = current.Revision, IncludeNotApproved = true });
            export.Cases.Single(c => c.CaseId == Case(current, "duplicate").Source.ScenarioId).Included.Should().BeFalse();
            export.Cases.Should().Contain(c => c.Title == "Rapport for stengt periode via portalen");
            export.Cases.Should().OnlyContain(c => c.AzureDevOpsId == null);

            // Selecting the older version as baseline again: an Update would roll the plan back, so it is refused.
            await x.Baselines.SelectBaselineAsync(Repository, s1, "lead", "Tilbake", default);
            await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp2", SnapshotId = s2, MemberSnapshotIds = [s2] }, default);
            var rollback = () => x.Plans.RefreshAsync(planId, current.Revision, "tester", default);
            (await rollback.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("basis-older");
        }
    }

    // ── Focused regressions ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Project_mode_discovery_planning_and_export_work_with_the_Skolenaervaer_guide_registration_disabled()
    {
        var s1 = await ImportAsync("imp1", "orders.zip", "content-o1", Spec(A, B), DateTimeOffset.UtcNow);
        await using var db = Context();
        var x = Services(db, catalog: []);
        x.Guides.GuideKeys.Should().BeEmpty("no static guide is registered");
        var d = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp1", SnapshotId = s1, MemberSnapshotIds = [s1] }, default);
        d.Capabilities.Single(c => c.Id == TestScenarioDiscoveryEngine.SystemGuide).State.Should().Be(DiscoveryCapabilityState.NotApplicable);
        Scenarios(d).Should().HaveCount(2);
        var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:imp1", DiscoveryId = d.DiscoveryId, CandidateIds = [.. Scenarios(d).Select(c => c.CandidateId)] }, "t", default);
        AdoTestPlanExporter.Preview(plan, new AdoExportRequest { ProjectKey = plan.ProjectKey!, DraftId = plan.Id, PlanRevision = plan.Revision, IncludeNotApproved = true }).ReadyCases.Should().Be(2);
        (await x.Guides.ListAsync(default)).Should().BeEmpty("no guide exists until sources are generated into one");
    }

    [Fact]
    public async Task A_failing_provider_never_turns_a_scenario_into_removed_and_never_yields_a_partial_guide_proposal()
    {
        var s1 = await ImportAsync("imp1", "m2lb-v1.zip", "content-v1", Spec(A, B), DateTimeOffset.UtcNow);
        Guid planId;
        await using (var db = Context())
        {
            var x = Services(db);
            var d = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp1", SnapshotId = s1, MemberSnapshotIds = [s1] }, default);
            planId = (await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:imp1", DiscoveryId = d.DiscoveryId, CandidateIds = [.. Scenarios(d).Select(c => c.CandidateId)] }, "t", default)).Id;
        }
        await using (var db = Context())
        {
            // The specification analyzer fails now: its scenarios are missing from the result, but they are "Kilde mangler", never "Utgått".
            var x = Services(db, extra: null);
            var failing = new TestScenarioDiscoveryService(x.Store, [new FailingSpecificationProvider(), new SystemGuideScenarioProvider(x.Guides)], _discoveries,
                store: new ScenarioDiscoveryStore(db, _discoveries), basisResolver: new ScenarioSourceBasisResolver(x.Baselines, x.Store, new SpecificationCatalogService(db)));
            var d = await failing.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp1", SnapshotId = s1, MemberSnapshotIds = [s1] }, default);
            d.Capabilities.Single(c => c.Id == TestScenarioDiscoveryEngine.Specification).State.Should().Be(DiscoveryCapabilityState.Failed);
            var plan = await x.Plans.DraftAsync(planId, default);
            plan.Cases.Should().OnlyContain(c => c.SourceState == CaseSourceState.SourceUnavailable);
            var refreshed = await x.Plans.RefreshAsync(planId, plan.Revision, "t", default);
            refreshed.Cases.Should().NotContain(c => c.Review == TestCaseReviewState.Superseded, "nothing is superseded on an incomplete comparison");

            // Guide generation with the failing analyzer fails with diagnostics; retried with a working one, it succeeds and is not duplicated.
            var guides = x.Guides;
            var jobs = await guides.EnqueueGenerationAsync((await x.Store.FindSourceAnalysisAsync(s1))!, default);
            await new SystemGuideGenerationService(guides, [new FailingSpecificationProvider()], x.Store, new SpecificationCatalogService(db)).ProcessPendingAsync(5, default);
            var failed = (await guides.JobsAsync(jobs[0].GuideKey, default)).Single();
            failed.Status.Should().Be("Failed");
            failed.LastError.Should().Contain("Ufullstendig");
            (await guides.CandidatesAsync(jobs[0].GuideKey, default)).Should().BeEmpty("a partial result is never presented as a proposal");
            (await guides.RetryJobAsync(jobs[0].GuideKey, failed.Id, default)).Status.Should().Be("Pending");
            await x.Generation.ProcessPendingAsync(5, default);
            (await guides.JobsAsync(jobs[0].GuideKey, default)).Single().Status.Should().Be("Succeeded");
            await guides.RetryJobAsync(jobs[0].GuideKey, failed.Id, default); // a succeeded job is left as it is
            await x.Generation.RunAsync(jobs[0].GuideKey, s1, default); // a concurrent/manual second run of the same input
            (await guides.CandidatesAsync(jobs[0].GuideKey, default)).Should().ContainSingle("the same input never creates a second candidate");
        }
    }

    [Fact]
    public async Task Inspecting_another_applications_source_is_refused_and_a_newer_import_makes_an_unreviewed_proposal_stale()
    {
        var s1 = await ImportAsync("imp1", "m2lb-v1.zip", "content-v1", Spec(A), DateTimeOffset.UtcNow.AddHours(-2));
        var other = Guid.NewGuid();
        await using (var db = Context())
        {
            db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord
            {
                Id = other, EnvironmentId = "", IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = DateTimeOffset.UtcNow,
                EvidenceJson = JsonSerializer.Serialize(new IqrSourceSnapshot { Id = other, Archive = new SourceArchive("other.zip", new string('c', 64), 3, "x"), AnalyzedAt = DateTimeOffset.UtcNow,
                    Repository = new SourceRepositoryIdentity("repo:other", "Other", "Test") }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            });
            await db.SaveChangesAsync();
            var x = Services(db);
            var mix = () => x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp1", SnapshotId = other, MemberSnapshotIds = [s1], Inspect = true }, default);
            (await mix.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("snapshot-not-in-project");

            await x.Guides.EnqueueGenerationAsync((await x.Store.FindSourceAnalysisAsync(s1))!, default);
            await x.Generation.ProcessPendingAsync(5, default);
        }
        var s2 = await ImportAsync("imp2", "m2lb-v2.zip", "content-v2", Spec(A, B), DateTimeOffset.UtcNow);
        await using (var db2 = Context())
        {
            var x = Services(db2);
            var jobs = await x.Guides.EnqueueGenerationAsync((await x.Store.FindSourceAnalysisAsync(s2))!, default);
            await x.Generation.ProcessPendingAsync(5, default);
            var candidates = await x.Guides.CandidatesAsync(jobs[0].GuideKey, default);
            candidates.Should().HaveCount(2);
            candidates.Single(c => c.SourceSnapshotId == s1).Status.Should().Be("Stale");
            candidates.Single(c => c.SourceSnapshotId == s1).SupersededBy.Should().Be(candidates.Single(c => c.SourceSnapshotId == s2).Id);
            var decide = () => x.Guides.DecideAsync(jobs[0].GuideKey, candidates.Single(c => c.SourceSnapshotId == s1).Id,
                new DecideGuideChangeRequest(candidates.Single(c => c.SourceSnapshotId == s1).Changes[0].ChangeId!, "Accepted", null, null), "lead", default);
            await decide.Should().ThrowAsync<SystemGuideException>();
        }
    }

    [Fact]
    public async Task Decided_proposals_merge_into_an_open_draft_without_overwriting_its_edits()
    {
        var s1 = await ImportAsync("imp1", "m2lb-v1.zip", "content-v1", Spec(A, B), DateTimeOffset.UtcNow.AddHours(-1));
        await using var db = Context();
        var x = Services(db);
        var key = (await x.Guides.EnqueueGenerationAsync((await x.Store.FindSourceAnalysisAsync(s1))!, default))[0].GuideKey;
        await x.Generation.ProcessPendingAsync(5, default);
        var first = (await x.Guides.CandidatesAsync(key, default)).Single();
        await x.Guides.DecideAsync(key, first.Id, new DecideGuideChangeRequest(first.Changes[0].ChangeId!, "Accepted", null, null), "lead", default);
        var draft = await x.Guides.ApplyCandidateAsync(key, first.Id, new ApplyGuideCandidateRequest(null), "lead", default);
        draft.Content.Scenarios.Should().ContainSingle("only the decided change is applied");
        // A person edits the open draft.
        var edited = draft.Content with { Purpose = "Fagansvarlig har skrevet formålet selv." };
        draft = await x.Guides.SaveDraftAsync(key, new SaveGuideDraftRequest(edited, null, draft.ContentFingerprint), "lead", default);

        // The second change of the same proposal goes into the same open draft: the person's edit stays.
        await x.Guides.DecideAsync(key, first.Id, new DecideGuideChangeRequest(first.Changes[1].ChangeId!, "Edited", "The report is rejected with status 400 and a message.", "Presisert"), "lead", default);
        var stale = () => x.Guides.ApplyCandidateAsync(key, first.Id, new ApplyGuideCandidateRequest(new string('0', 64)), "lead", default);
        await stale.Should().ThrowAsync<SystemGuideException>().WithMessage("*endret siden*");
        var merged = await x.Guides.ApplyCandidateAsync(key, first.Id, new ApplyGuideCandidateRequest(draft.ContentFingerprint), "lead", default);
        merged.Revision.Should().Be(draft.Revision, "merged into the open draft, no second draft");
        merged.Content.Purpose.Should().Be("Fagansvarlig har skrevet formålet selv.");
        merged.Content.Scenarios.Should().HaveCount(2).And.Contain(s => s.ExpectedOutcome == "The report is rejected with status 400 and a message.");
        merged.State.Should().Be(GuideRevisionState.Draft, "nothing is approved or published automatically");
    }

    // ── Review findings (2026-11-23) ───────────────────────────────────────────────────────────────────────────

    private static DiscoveredScenario Merged(string id, string identity, string given, string fingerprint, params string[] mergedFrom) => new()
    {
        CandidateId = id, ProjectKey = "import:m", CapabilityId = TestScenarioDiscoveryEngine.Specification, SourceCategory = "Specification", Title = given, Purpose = "Formål",
        ExpectedBehaviour = "the report is stored", Preconditions = [given], Steps = [new("Send the report", "the report is stored")], CanonicalKey = "scenario:|" + id,
        SourceIdentity = identity, Fingerprint = fingerprint, MergedFrom = [.. mergedFrom], Evidence = [new("specification", "spec.md#" + id)],
    };

    private static ScenarioDiscoveryResult Result(string id, DateTimeOffset at, DiscoveryBasisKind kind, params DiscoveredScenario[] candidates) => new()
    {
        DiscoveryId = id, ProjectKey = "import:m", DiscoveredAt = at, Candidates = [.. candidates], RuleVersion = "tsd-1",
        Capabilities = [new DiscoveryCapability { Id = TestScenarioDiscoveryEngine.Specification, State = DiscoveryCapabilityState.Available }],
        Basis = new DiscoverySourceBasis { ApplicationKey = "app-m", Kind = kind, Fingerprint = id, SpecificationOrigin = SpecificationOrigin.ImportedSnapshot, SpecificationReference = "spec",
            Members = [new DiscoverySourceMember { RepositoryKey = "app-m", DisplayName = "M", SnapshotId = Guid.NewGuid(), StateCode = "SINGLE_SNAPSHOT" }] },
    };

    [Fact]
    public async Task When_the_source_merges_two_scenarios_a_refresh_never_gives_two_cases_the_same_scenario_and_the_plan_still_saves()
    {
        await using var db = Context();
        var x = Services(db);
        _discoveries.Store(Result("d-m1", DateTimeOffset.UtcNow.AddHours(-1), DiscoveryBasisKind.Current,
            Merged("a", "tss-x", "a report from the portal", "f-a"), Merged("b", "tss-y", "a report from the API", "f-b")));
        var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:m", DiscoveryId = "d-m1", CandidateIds = ["a", "b"] }, "t", default);
        // The newer source holds one scenario for both: "b" absorbed "a".
        _discoveries.Store(Result("d-m2", DateTimeOffset.UtcNow, DiscoveryBasisKind.Current, Merged("b", "tss-y", "a report from the API", "f-b2", "tss-x")));

        var refreshed = await x.Plans.RefreshAsync(plan.Id, plan.Revision, "t", default);

        refreshed.Cases.Select(c => c.Source.ScenarioId).Should().OnlyHaveUniqueItems();
        var a = refreshed.Cases.Single(c => c.Source.ScenarioId == "a");
        a.SourceStateReason.Should().Contain("slått sammen");
        a.Review.Should().NotBe(TestCaseReviewState.Superseded, "a merged scenario is not a removed one");
        var saved = await x.Plans.SaveAsync(plan.Id, Human(refreshed, (_, part) => part), "t", default);
        saved.Cases.Should().HaveCount(2, "nothing is lost and saving works");
    }

    // ── Each saved case is compared with its OWN specification, never with the one selected now (2026-11-25) ─────────────────────

    private const string SkoleSpec = "SkoleService/specs/001-skoleservice/spec.md", FrontendSpec = "Frontend/specs/002-child-search-profile/spec.md";
    private static readonly Guid SameSnapshot = Guid.Parse("0f0f0f0f-0000-0000-0000-000000000001");

    private static DiscoveredScenario InSpec(string id, string spec, string story, string fingerprint) => Merged(id, "tss-" + id, "given " + id, fingerprint) with
    {
        Story = new CoverageUserStory { Key = spec + "#" + story, StoryId = story, Title = story, SpecificationPath = spec }, Evidence = [new("specification", spec + "#" + id)],
    };

    /// <summary>A discovery of one selected specification over the SAME source version (same snapshot, same content).</summary>
    private static ScenarioDiscoveryResult Selected(string id, DateTimeOffset at, string spec, params DiscoveredScenario[] candidates) => Result(id, at, DiscoveryBasisKind.Current, candidates) with
    {
        SelectionScope = spec,
        Basis = new DiscoverySourceBasis { ApplicationKey = "app-m", Kind = DiscoveryBasisKind.Current, Fingerprint = "fp-" + spec, SpecificationOrigin = SpecificationOrigin.ImportedSnapshot,
            SpecificationReference = spec, Members = [new DiscoverySourceMember { RepositoryKey = "app-m", DisplayName = "M", SnapshotId = SameSnapshot, StateCode = "SINGLE_SNAPSHOT" }] },
    };

    [Fact]
    public async Task A_SkoleService_plan_opened_while_Frontend_is_selected_is_compared_with_SkoleService_and_shows_no_missing_source()
    {
        await using var db = Context();
        var x = Services(db);
        _discoveries.Store(Selected("d-skole", DateTimeOffset.UtcNow.AddMinutes(-10), SkoleSpec, InSpec("s1", SkoleSpec, "US2", "f1"), InSpec("s2", SkoleSpec, "US2", "f2")));
        var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:m", DiscoveryId = "d-skole", CandidateIds = ["s1", "s2"] }, "t", default);
        var revision = plan.Revision;
        // The tester now selects Frontend in Finn og forstå: its scenarios have the same US2/AC numbers but are other scenarios.
        _discoveries.Store(Selected("d-front", DateTimeOffset.UtcNow, FrontendSpec, InSpec("f1", FrontendSpec, "US2", "g1"), InSpec("f2", FrontendSpec, "US2", "g2")));

        var opened = await x.Plans.DraftAsync(plan.Id, default);

        opened.Cases.Should().OnlyContain(c => c.SourceState == CaseSourceState.Current, "each case is compared with its own (SkoleService) specification");
        var status = (await x.Plans.ProjectWorkspaceAsync("import:m", null, true, default)).PlanStatus![plan.Id];
        status.State.Should().Be("Klar");
        status.Unavailable.Should().Be(0);
        status.NewScenarios.Should().Be(0, "Frontend scenarios are not new for a SkoleService plan");
        opened.Revision.Should().Be(revision, "opening a plan never writes it");
        opened.Cases.Select(c => c.Source.ScenarioId).Should().Equal("s1", "s2");
    }

    [Fact]
    public async Task A_real_change_and_a_real_removal_in_the_own_specification_are_still_found_while_another_is_selected()
    {
        await using var db = Context();
        var x = Services(db);
        _discoveries.Store(Selected("d-skole1", DateTimeOffset.UtcNow.AddMinutes(-20), SkoleSpec, InSpec("s1", SkoleSpec, "US2", "f1"), InSpec("s2", SkoleSpec, "US2", "f2")));
        var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:m", DiscoveryId = "d-skole1", CandidateIds = ["s1", "s2"] }, "t", default);
        // SkoleService read again (same source version): s1's text changed, s2 is gone. Then Frontend is selected.
        _discoveries.Store(Selected("d-skole2", DateTimeOffset.UtcNow.AddMinutes(-10), SkoleSpec, InSpec("s1", SkoleSpec, "US2", "f1-changed"), InSpec("s3", SkoleSpec, "US3", "f3")));
        _discoveries.Store(Selected("d-front", DateTimeOffset.UtcNow, FrontendSpec, InSpec("f1", FrontendSpec, "US2", "g1")));

        var opened = await x.Plans.DraftAsync(plan.Id, default);

        opened.Cases.Single(c => c.Source.ScenarioId == "s1").SourceState.Should().Be(CaseSourceState.Changed);
        opened.Cases.Single(c => c.Source.ScenarioId == "s2").SourceState.Should().Be(CaseSourceState.Removed, "a complete comparison of its own specification");
    }

    [Fact]
    public async Task Without_any_discovery_of_the_own_specification_the_case_is_not_compared_not_missing_and_the_plan_is_not_called_in_line()
    {
        await using var db = Context();
        var x = Services(db);
        _discoveries.Store(Selected("d-skole", DateTimeOffset.UtcNow.AddMinutes(-10), SkoleSpec, InSpec("s1", SkoleSpec, "US2", "f1")));
        var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:m", DiscoveryId = "d-skole", CandidateIds = ["s1"] }, "t", default);
        // A NEW source version (another snapshot) read with Frontend selected: no discovery of SkoleService exists for it.
        _discoveries.Store(Result("d-front-v2", DateTimeOffset.UtcNow, DiscoveryBasisKind.Current, InSpec("f1", FrontendSpec, "US2", "g1")) with { SelectionScope = FrontendSpec });

        var opened = await x.Plans.DraftAsync(plan.Id, default);
        var c = opened.Cases.Single();
        c.SourceState.Should().Be(CaseSourceState.NotCompared, "a discovery of another specification proves nothing about this case");
        c.SourceStateReason.Should().StartWith("Ikke sammenlignet").And.Contain(SkoleSpec).And.NotContain("Kilde mangler");
        var status = (await x.Plans.ProjectWorkspaceAsync("import:m", null, true, default)).PlanStatus![plan.Id];
        status.Unavailable.Should().Be(0);
        status.State.Should().NotBe("Kilde mangler");
    }

    [Fact]
    public async Task A_plan_with_cases_from_two_specifications_resolves_each_case_independently()
    {
        await using var db = Context();
        var x = Services(db);
        _discoveries.Store(Selected("d-skole", DateTimeOffset.UtcNow.AddMinutes(-20), SkoleSpec, InSpec("s1", SkoleSpec, "US2", "f1")));
        var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:m", DiscoveryId = "d-skole", CandidateIds = ["s1"] }, "t", default);
        _discoveries.Store(Selected("d-front", DateTimeOffset.UtcNow.AddMinutes(-10), FrontendSpec, InSpec("f1", FrontendSpec, "US2", "g1")));
        plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:m", DiscoveryId = "d-front", CandidateIds = ["f1"], DraftId = plan.Id, BaseRevision = plan.Revision }, "t", default);
        // Frontend's scenario changes; SkoleService is unchanged; SkoleService is selected again last.
        _discoveries.Store(Selected("d-front2", DateTimeOffset.UtcNow.AddMinutes(-5), FrontendSpec, InSpec("f1", FrontendSpec, "US2", "g1-changed")));
        _discoveries.Store(Selected("d-skole2", DateTimeOffset.UtcNow, SkoleSpec, InSpec("s1", SkoleSpec, "US2", "f1")));

        var opened = await x.Plans.DraftAsync(plan.Id, default);

        opened.Cases.Single(c => c.Source.ScenarioId == "s1").SourceState.Should().Be(CaseSourceState.Current);
        opened.Cases.Single(c => c.Source.ScenarioId == "f1").SourceState.Should().Be(CaseSourceState.Changed, "the Frontend case is compared with Frontend");
    }

    [Fact]
    public async Task The_overview_attributes_each_case_to_its_own_specification_and_module_and_writes_nothing()
    {
        await using var db = Context();
        var x = Services(db);
        _discoveries.Store(Selected("d-skole", DateTimeOffset.UtcNow.AddMinutes(-20), SkoleSpec, InSpec("s1", SkoleSpec, "US2", "f1")));
        var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:m", DiscoveryId = "d-skole", CandidateIds = ["s1"] }, "t", default);
        _discoveries.Store(Selected("d-front", DateTimeOffset.UtcNow.AddMinutes(-10), FrontendSpec, InSpec("f1", FrontendSpec, "US1", "g1")));
        plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:m", DiscoveryId = "d-front", CandidateIds = ["f1"], DraftId = plan.Id, BaseRevision = plan.Revision }, "t", default);
        plan = await x.Plans.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Skole: report twice", null, null, ScenarioKey: "s1"), "t", default);
        plan = await x.Plans.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Free text check", null, null), "t", default);
        var revisions = (await x.Plans.RevisionsAsync(plan.Id, default)).Count;

        var overview = await x.Plans.OverviewAsync("import:m", default);

        overview.Cases.Should().HaveCount(4);
        overview.Cases.Single(c => c.CaseId == "s1").Module.Should().Be("SkoleService");
        overview.Cases.Single(c => c.CaseId == "f1").Module.Should().Be("Frontend");
        overview.Cases.Single(c => c.Title == "Skole: report twice").Should().Match<OverviewCase>(c => c.Module == "SkoleService" && c.ScenarioIdentity == "tss-s1",
            "a hand-written case under a scenario counts for that scenario");
        overview.Cases.Single(c => c.Title == "Free text check").Module.Should().Be(TestPlanSuggestionService.NoModule, "a case without a source is kept, not dropped");
        overview.Plans.Single().Modules.Should().BeEquivalentTo(["Frontend", "SkoleService", TestPlanSuggestionService.NoModule]);
        (await x.Plans.RevisionsAsync(plan.Id, default)).Should().HaveCount(revisions, "reading the overview writes nothing");
    }

    [Fact]
    public async Task A_plan_created_with_a_structure_keeps_its_suite_tree_and_a_moved_case_keeps_its_identity_and_approval()
    {
        await using var db = Context();
        var x = Services(db);
        var flow = InSpec("e1", SkoleSpec, "US1", "f1") with { Type = ScenarioCandidateType.BusinessE2E, Area = "Rapportering" };
        var receive = InSpec("i1", SkoleSpec, "US2", "f2") with { Type = ScenarioCandidateType.Integration, Area = "Meldingsmottak" };
        _discoveries.Store(Selected("d-tree", DateTimeOffset.UtcNow, SkoleSpec, flow, receive));
        var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:m", DiscoveryId = "d-tree", CandidateIds = ["e1", "i1"],
            PlanName = "Skolenærvær", Structure = PlanStructureTemplates.LevelArea, CreationRequestId = "tree-1" }, "anna", default);

        PlanStructure.Flatten(plan.Suites).Select(t => new string(' ', t.Depth * 2) + t.Suite.Name).Should().Equal("Ende til ende", "  Rapportering", "Integrasjon", "  Meldingsmottak");
        PlanStructure.PathOf(plan.Suites, plan.Cases.Single(c => c.Source.ScenarioId == "e1").SuiteId).Should().Be("Ende til ende / Rapportering");

        // A reviewer adds a child suite under «Integrasjon» and moves the integration case into it; the case is approved first.
        var integration = plan.Suites.Single(t => t.Name == "Integrasjon");
        var mine = new TestSuiteDraft("suite:r:1", "Duplikater", "reviewer", null) { ParentId = integration.Id, Order = 1 };
        plan = await x.Plans.SaveAsync(plan.Id, Human(plan, (c, part) => part) with { Suites = [.. plan.Suites, mine] }, "anna", default);
        var before = plan.Cases.Single(c => c.Source.ScenarioId == "i1");
        var request = Human(plan, (c, part) => c.Source.ScenarioId == "i1" ? part with { SuiteId = mine.Id } : part);
        plan = await x.Plans.SaveAsync(plan.Id, request, "anna", default);
        var moved = (await x.Plans.DraftAsync(plan.Id, default)).Cases.Single(c => c.Source.ScenarioId == "i1");
        moved.SuiteId.Should().Be(mine.Id);
        (moved.Source.ScenarioId, moved.Generated.SourceIdentity, moved.Review).Should().Be((before.Source.ScenarioId, before.Generated.SourceIdentity, before.Review), "moving never changes identity, source or review");
        PlanStructure.PathOf(plan.Suites, moved.SuiteId).Should().Be("Integrasjon / Duplikater");
        (await x.Plans.RevisionsAsync(plan.Id, default)).Should().HaveCount(3, "created, suite added, case moved — each a revision");

        // A cycle is refused, never repaired silently.
        var cyclic = Human(plan, (c, part) => part) with { Suites = [.. plan.Suites.Select(t => t.Id == integration.Id ? t with { ParentId = mine.Id } : t)] };
        (await FluentActions.Awaiting(() => x.Plans.SaveAsync(plan.Id, cyclic, "anna", default)).Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("invalid-suites");
    }

    [Fact]
    public async Task A_historical_inspection_can_never_extend_a_plan_or_move_its_basis()
    {
        await using var db = Context();
        var x = Services(db);
        _discoveries.Store(Result("d-h1", DateTimeOffset.UtcNow, DiscoveryBasisKind.Historical, Merged("a", "tss-x", "a report", "f-a")));
        var add = () => x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:m", DiscoveryId = "d-h1", CandidateIds = ["a"] }, "t", default);
        (await add.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("historical-discovery");
    }

    [Fact]
    public async Task A_proposal_generated_late_for_an_older_source_version_is_stale_and_never_makes_the_newer_one_stale()
    {
        var s1 = await ImportAsync("imp1", "m2lb-v1.zip", "content-v1", Spec(A, B), DateTimeOffset.UtcNow.AddDays(-1));
        var s2 = await ImportAsync("imp2", "m2lb-v2.zip", "content-v2", Spec(A, BChanged), DateTimeOffset.UtcNow);
        await using var db = Context();
        var x = Services(db);
        var key = (await x.Guides.EnqueueGenerationAsync((await x.Store.FindSourceAnalysisAsync(s2))!, default))[0].GuideKey;
        await x.Generation.ProcessPendingAsync(5, default);
        await x.Generation.RunAsync(key, s1, default); // a retry of the older version, after the newer one

        var candidates = await x.Guides.CandidatesAsync(key, default);
        var newer = candidates.Single(c => c.SourceSnapshotId == s2);
        newer.Status.Should().Be("NeedsReview", "the newer source version keeps its proposal");
        candidates.Single(c => c.SourceSnapshotId == s1).Should().Match<SystemGuideCandidateView>(c => c.Status == "Stale" && c.SupersededBy == newer.Id);
    }

    [Fact]
    public async Task A_re_imported_archive_whose_catalog_predates_the_evidence_gets_it_beside_the_append_only_row_and_discovery_finds_it()
    {
        // Real data (2026-11-23): M2LB-language was first imported by an older build (catalog without evidence), then again. The catalog row is
        // append-only (a database trigger), and the re-import keeps the first import's catalog and snapshot id.
        var spec = Spec(A, B);
        await using (var db = Context())
        {
            // The same content always has the same fingerprint; the older build stored the row without evidence.
            var probe = await new SpecificationCatalogService(db).CaptureAsync("probe", null, null, [new SpecificationCatalogService.Document(SpecPath, spec)], default);
            db.SpecificationCatalogs.Add(new SpecificationCatalogRecord
            {
                Id = Guid.NewGuid(), ProjectKey = SpecificationCatalogService.ProjectKeyOf("reimport"), Revision = 1, SnapshotId = Guid.NewGuid(),
                CapturedAt = DateTimeOffset.UtcNow.AddHours(-5), Fingerprint = probe!.Fingerprint, DocumentsJson = "[]", EntriesJson = "[]",
            });
            await db.SaveChangesAsync();
        }
        var second = await ImportAsync("reimport", "m2lb-language.zip", "content-l", spec, DateTimeOffset.UtcNow);

        await using (var db = Context())
        {
            var catalogs = new SpecificationCatalogService(db);
            (await db.SpecificationCatalogs.AsNoTracking().SingleAsync(c => c.ProjectKey == "import:reimport")).EvidenceJson.Should().BeNull("the append-only row is never rewritten");
            db.SpecificationCatalogEvidence.Should().ContainSingle("its evidence is stored beside it");
            (await catalogs.EvidenceAsync(second, null, default)).Should().BeNull("the new snapshot alone does not name the first import's catalog");
            var found = await catalogs.EvidenceAsync(second, null, default, "reimport");
            found!.Value.Evidence.Single().Evidence.AcceptanceScenarios.Should().HaveCount(2, "found through the snapshot's own import");
        }
    }

    [Fact]
    public async Task After_a_restart_an_older_result_restored_first_never_hides_the_applications_newest_discovery()
    {
        // Real data (2026-11-23): after the launcher restarted the API, the import's own project restored its older result (captured before the
        // specification evidence existed) first; the application pointer then stayed on it and a plan of another project of the same application
        // showed "Kilde mangler" for every specification case until that project happened to be opened through the API.
        var s1 = await ImportAsync("imp1", "m2lb-v1.zip", "content-v1", Spec(A, B), DateTimeOffset.UtcNow.AddHours(-1));
        ScenarioDiscoveryResult older, newer;
        await using (var db = Context())
            older = await Services(db).Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp1", SnapshotId = s1, MemberSnapshotIds = [s1] }, default);
        await using (var db = Context())
            newer = await Services(db).Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "m2lb", ProjectName = "M2LB", SnapshotId = s1, MemberSnapshotIds = [s1] }, default);
        var application = newer.Basis!.ApplicationKey;
        older.Basis!.ApplicationKey.Should().Be(application);

        var restarted = new ScenarioDiscoveryCache();
        await using (var db = Context())
        {
            var store = new ScenarioDiscoveryStore(db, restarted);
            await store.RestoreAsync("import:imp1", null, default);
            restarted.CurrentForApplication(application)!.DiscoveryId.Should().Be(older.DiscoveryId, "precondition: the older result set the pointer first");
            await store.RestoreApplicationAsync(application, default);
            restarted.CurrentForApplication(application)!.DiscoveryId.Should().Be(newer.DiscoveryId, "the application's newest stored discovery is current");
            await store.RestoreAsync("import:imp1", null, default);
            restarted.CurrentForApplication(application)!.DiscoveryId.Should().Be(newer.DiscoveryId, "and an older one never moves it back");
        }
    }

    [Fact]
    public async Task The_change_summary_of_a_new_version_counts_only_what_it_compared()
    {
        var s1 = await ImportAsync("imp1", "m2lb-v1.zip", "content-v1", Spec(A, B, C), DateTimeOffset.UtcNow.AddDays(-1));
        await using (var db = Context())
        {
            var x = Services(db);
            var first = await x.Discovery.ChangesAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp1", SnapshotId = s1, MemberSnapshotIds = [s1] }, default);
            first.Compared.Should().BeFalse("nothing to compare the first import with");
            first.Reason.Should().Contain("Ingen tidligere versjon");
        }
        var s2 = await ImportAsync("imp2", "m2lb-language.zip", "content-v2", Spec(A, BChanged, D), DateTimeOffset.UtcNow);
        await using (var db = Context())
        {
            var x = Services(db);
            await x.Baselines.SelectBaselineAsync(Repository, s2, "t", "new version", default);
            var changes = await x.Discovery.ChangesAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp2", SnapshotId = s2, MemberSnapshotIds = [s2] }, default);
            changes.Compared.Should().BeTrue();
            changes.PreviousArchives.Should().Contain("m2lb-v1.zip");
            Scenarios((await x.Discovery.LastAsync("import:imp2", null, default))!).Should().HaveCount(3);
            (changes.New, changes.Changed, changes.Removed, changes.Unchanged, changes.NotCompared).Should().Be((1, 1, 1, 1 + 2, 0),
                "A unchanged, B changed (400 → 422), C removed, D new; the two requirement candidates (FR-001, FR-002) are unchanged");
            // Asked again (the latest discovery is now this version): still compared with the previous version, never with itself.
            var again = await x.Discovery.ChangesAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp2", SnapshotId = s2, MemberSnapshotIds = [s2] }, default);
            (again.Compared, again.New, again.Changed, again.Removed).Should().Be((true, 1, 1, 1));
            again.PreviousArchives.Should().Contain("m2lb-v1.zip");
        }
    }

    [Fact]
    public async Task A_specification_moved_to_another_file_in_a_new_version_reads_the_same_in_status_impact_refresh_and_the_change_summary()
    {
        // Real data (2026-11-23, M2LB → M2LB-language): the specification folder was renamed, so every scenario's identity (file + Given + When)
        // changed while its text did not. Status said "4 endret", impact "4 utgått" and refresh kept the cases — three answers for one plan.
        var s1 = await ImportAsync("imp1", "m2lb-v1.zip", "content-v1", Spec(A, B), DateTimeOffset.UtcNow.AddHours(-2));
        Guid planId; string edited, approved;
        await using (var db = Context())
        {
            var x = Services(db);
            var d = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp1", SnapshotId = s1, MemberSnapshotIds = [s1] }, default);
            var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = "import:imp1", DiscoveryId = d.DiscoveryId, CandidateIds = [.. Scenarios(d).Select(c => c.CandidateId)] }, "anna", default);
            edited = plan.Cases[0].Source.ScenarioId; approved = plan.Cases[1].Source.ScenarioId;
            plan = await x.Plans.SaveAsync(plan.Id, WithTitle(plan, edited, "Testers egen tittel"), "anna", default);
            plan = await x.Plans.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, approved, TestCaseReviewState.Accepted, "ok"), "anna", default);
            planId = plan.Id;
        }
        var s2 = await ImportAsync("imp2", "m2lb-language.zip", "content-v2", Spec(A, B), DateTimeOffset.UtcNow, specPath: "service/specs/001-intake/spec.md");
        await using (var db = Context())
        {
            var x = Services(db);
            await x.Baselines.SelectBaselineAsync(Repository, s2, "t", "new version", default);
            var changes = await x.Discovery.ChangesAsync(new ScenarioDiscoveryRequest { ProjectKey = "import:imp2", SnapshotId = s2, MemberSnapshotIds = [s2] }, default);
            (changes.Removed, changes.New).Should().Be((0, 0), "the same text in a moved file is neither removed nor new");
            changes.Moved.Should().BeGreaterThanOrEqualTo(2);

            var status = (await x.Plans.ProjectWorkspaceAsync("import:imp1", null, true, default)).PlanStatus![planId];
            var impact = await x.Plans.ImpactAsync(planId, default);
            status.Removed.Should().Be(0);
            impact.Cases.Should().NotContain(c => c.Kinds.Contains(CaseImpactKind.ScenarioRemoved), "impact agrees with status: nothing was removed");
            impact.Cases.Count.Should().Be(status.Changed, "status and impact count the same cases");

            var before = await x.Plans.DraftAsync(planId, default);
            var after = await x.Plans.RefreshAsync(planId, before.Revision, "anna", default);
            after.Cases.Should().NotContain(c => c.Review == TestCaseReviewState.Superseded);
            after.Cases.Should().Contain(c => c.Edits.Title == "Testers egen tittel", "the tester's edit follows the moved scenario");
            after.Cases.Should().Contain(c => c.Review == TestCaseReviewState.Accepted, "and so does the approval (the text did not change)");
        }
    }

    // ── Plan lifecycle (2026-11-23) ─────────────────────────────────────────────────────────────────────────────

    private async Task<(Guid PlanId, Guid Snapshot)> PlanAsync(string project = "import:imp1")
    {
        var s1 = await ImportAsync("imp1", "m2lb-v1.zip", "content-v1", Spec(A, B), DateTimeOffset.UtcNow);
        await using var db = Context();
        var x = Services(db);
        var d = await x.Discovery.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = project, SnapshotId = s1, MemberSnapshotIds = [s1] }, default);
        var plan = await x.Plans.CreateProjectAsync(new CreateProjectTestPlanRequest
        {
            ProjectKey = project, DiscoveryId = d.DiscoveryId, CandidateIds = [.. Scenarios(d).Select(c => c.CandidateId)], PlanName = "Verifisering m2lb",
        }, "anna", default);
        // A tester's edit, an approval and a frozen snapshot: everything archive / restore / delete must keep.
        var edited = WithTitle(plan, plan.Cases[0].Source.ScenarioId, "Testers egen tittel");
        plan = await x.Plans.SaveAsync(plan.Id, edited, "anna", default);
        plan = await x.Plans.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, plan.Cases[1].Source.ScenarioId, TestCaseReviewState.Accepted, "ok"), "anna", default);
        await x.Plans.SaveSnapshotAsync(plan.Id, new SaveTestPlanSnapshotRequest(plan.Revision, "v1", null), "anna", default);
        return (plan.Id, s1);
    }

    [Fact]
    public async Task An_archived_plan_leaves_the_active_list_is_read_only_and_a_restore_brings_it_back_unchanged()
    {
        var (id, _) = await PlanAsync();
        await using var db = Context();
        var x = Services(db);
        var before = await x.Plans.DraftAsync(id, default);

        (await FluentActions.Awaiting(() => x.Plans.ArchiveAsync(id, new TestPlanLifecycleRequest(before.Revision - 1), "anna", default))
            .Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("draft-changed", "a plan changed since the tester saw it is not archived");
        var archived = await x.Plans.ArchiveAsync(id, new TestPlanLifecycleRequest(before.Revision, "Ferdig verifisert"), "anna", default);
        archived.Lifecycle.Should().Be(TestPlanLifecycle.Archived);
        archived.LifecycleChangedBy.Should().Be("anna");

        var workspace = await x.Plans.ProjectWorkspaceAsync("import:imp1", null, true, default);
        workspace.Drafts.Should().BeEmpty("archived plans are not part of normal work");
        workspace.ArchivedDrafts.Should().ContainSingle(p => p.Id == id && p.Scenarios == 2 && p.Accepted == 1);
        (await x.Plans.ProjectDraftsAsync("import:imp1", default)).Should().BeEmpty("traceability counts active plans only");

        var readable = await x.Plans.DraftAsync(id, default);
        readable.Cases.Should().BeEquivalentTo(before.Cases, "archiving never changes content");
        (await FluentActions.Awaiting(() => x.Plans.SaveAsync(id, WithTitle(readable, readable.Cases[0].Source.ScenarioId, "x"), "anna", default))
            .Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("plan-archived");
        (await FluentActions.Awaiting(() => x.Plans.ReviewAsync(id, new ReviewTestCaseRequest(readable.Revision, readable.Cases[0].Source.ScenarioId, TestCaseReviewState.Accepted, null), "anna", default))
            .Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("plan-archived");

        var restored = await x.Plans.RestoreFromArchiveAsync(id, new TestPlanLifecycleRequest(readable.Revision), "anna", default);
        restored.Lifecycle.Should().Be(TestPlanLifecycle.Active);
        var after = await x.Plans.DraftAsync(id, default);
        after.Cases.Should().BeEquivalentTo(before.Cases, "edits, approvals and history survive archive and restore");
        after.Cases[0].EffectiveTitle.Should().Be("Testers egen tittel");
        (await x.Plans.RevisionsAsync(id, default)).Select(r => r.Reason).Should().Contain(["Arkivert: Ferdig verifisert", "Gjenopprettet fra arkivet"]);
        (await x.Plans.ProjectWorkspaceAsync("import:imp1", null, true, default)).Drafts.Should().ContainSingle(p => p.Id == id);
    }

    [Fact]
    public async Task Only_an_archived_plan_can_be_deleted_with_its_name_and_delete_is_logical_keeping_revisions_and_snapshots()
    {
        var (id, _) = await PlanAsync();
        await using var db = Context();
        var x = Services(db);
        var plan = await x.Plans.DraftAsync(id, default);

        (await FluentActions.Awaiting(() => x.Plans.DeleteAsync(id, new DeleteTestPlanRequest(plan.Revision, plan.PlanName), "anna", default))
            .Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("plan-not-archived", "an active plan is archived first");
        var archived = await x.Plans.ArchiveAsync(id, new TestPlanLifecycleRequest(plan.Revision), "anna", default);
        (await FluentActions.Awaiting(() => x.Plans.DeleteAsync(id, new DeleteTestPlanRequest(archived.Revision, "Verifisering"), "anna", default))
            .Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("confirm-name", "a partial name never confirms");
        (await FluentActions.Awaiting(() => x.Plans.DeleteAsync(id, new DeleteTestPlanRequest(archived.Revision - 1, plan.PlanName), "anna", default))
            .Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("draft-changed");

        await x.Plans.DeleteAsync(id, new DeleteTestPlanRequest(archived.Revision, plan.PlanName), "anna", default);

        (await FluentActions.Awaiting(() => x.Plans.DraftAsync(id, default)).Should().ThrowAsync<TestPlanException>()).Which.Status.Should().Be(404);
        var workspace = await x.Plans.ProjectWorkspaceAsync("import:imp1", null, true, default);
        workspace.Drafts.Should().BeEmpty();
        workspace.ArchivedDrafts.Should().BeEmpty("a deleted plan is in no list");
        // Logical: the row, every revision and the frozen snapshot are kept (no orphaned snapshot, nothing lost for audit).
        await using var check = Context();
        var row = await check.TestPlanDrafts.IgnoreQueryFilters().SingleAsync(d => d.Id == id);
        row.Lifecycle.Should().Be("Deleted");
        row.LifecycleChangedBy.Should().Be("anna");
        (await check.TestPlanDraftRevisions.Where(r => r.DraftId == id).Select(r => r.Reason).ToListAsync()).Should().Contain(["Created", "Arkivert", "Slettet"]);
        (await check.TestPlanSnapshots.CountAsync(s => s.DraftId == id)).Should().Be(1);
        (await check.TestPlanDrafts.CountAsync(d => d.Id == id)).Should().Be(0, "the global filter hides it from every ordinary query");
    }

    private static SaveTestPlanDraftRequest WithTitle(TestPlanDraft plan, string scenarioId, string title) =>
        Human(plan, (c, part) => c.Source.ScenarioId == scenarioId ? part with { Edits = part.Edits with { Title = title } } : part);

    // ── Fakes ───────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class FailingSpecificationProvider : ITestScenarioCandidateProvider
    {
        public string CapabilityId => TestScenarioDiscoveryEngine.Specification;
        public Task<CandidateProviderOutput> DiscoverAsync(ScenarioDiscoveryContext context, CancellationToken ct) => throw new InvalidOperationException("analyzer down");
    }

    private static SaveTestPlanDraftRequest Human(TestPlanDraft plan, Func<TestCaseDraft, TestCaseHumanPart, TestCaseHumanPart> change) => new()
    {
        BaseRevision = plan.Revision, PlanName = plan.PlanName, ScenarioIds = [.. plan.Cases.Select(c => c.Source.ScenarioId)], Suites = plan.Suites,
        Cases = [.. plan.Cases.Select(c => change(c, new TestCaseHumanPart { ScenarioId = c.Source.ScenarioId, SuiteId = c.SuiteId, Edits = c.Edits, Risk = c.Risk,
            RequirementLinks = [.. c.RequirementLinks], RejectedSuggestions = c.RejectedSuggestions }))],
    };

    private sealed class NoValues : ITestDataProvider
    {
        public string Id => "values";
        public string Label => "Values";
        public string Description => "In-test assets.";
        public IReadOnlyList<TestDataAsset> Assets() => [];
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
}
