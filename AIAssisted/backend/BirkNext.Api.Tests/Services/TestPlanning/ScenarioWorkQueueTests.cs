using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.SystemUnderstanding;
using BirkNext.Api.Services.TestData;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.Integrations;
using BirkNext.TestCoverage;
using BirkNext.TestData;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Q = BirkNext.TestPlanning.ScenarioWorkQueue;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Finn og forstå's work queues (2026-11-24): which scenarios a manual tester should prepare next. Pure classification (eligibility separate from
/// ranking, plain reasons, no score, no invented priority, unknown coverage never zero, ready to prepare ≠ ready to execute) and the real service
/// over saved plans (draft, approved, several cases, archived plans, excluded cases, cases from an earlier import, cases of removed scenarios,
/// an unreadable plan). Synthetic specification in own wording.
/// </summary>
public sealed class ScenarioWorkQueueTests : IAsyncLifetime, IDisposable
{
    // ── Pure classification ─────────────────────────────────────────────────────────────────────────────────

    private static CoverageUserStory Story(int n, string? priority) => new() { Key = $"spec#US{n}", StoryId = $"US{n}", Ordinal = n, Title = $"Story {n}", Priority = priority };

    private static DiscoveredScenario Scenario(string id, CoverageUserStory? story = null, int? number = null, string? expected = "The report is accepted",
        CandidateConfidence confidence = CandidateConfidence.ExplicitSpecification, string[]? preconditions = null) => new()
    {
        CandidateId = id, ProjectKey = "p", CapabilityId = "specification", SourceCategory = "Specification", Title = "Scenario " + id, Purpose = "p",
        Type = ScenarioCandidateType.BusinessFunctional, Confidence = confidence, ExpectedBehaviour = expected, Story = story, NumberInStory = number,
        Preconditions = [.. preconditions ?? []], SourceIdentity = "spec:" + id,
    };

    private static readonly Dictionary<string, ScenarioCaseCoverage> None = [];

    private static ScenarioWorkItem One(DiscoveredScenario c, ScenarioCaseCoverage? coverage = null, bool complete = true, bool current = true) =>
        ScenarioWorkQueues.Classify([c], coverage is null ? None : new() { [c.CandidateId] = coverage }, complete, current).Single();

    [Fact]
    public void A_clear_supported_scenario_without_a_test_case_is_recommended_with_plain_reasons()
    {
        var item = One(Scenario("a", Story(1, "P1"), 1, preconditions: ["A school exists"]));

        item.Queues.Should().Equal(Q.Recommended);
        item.Rank.Should().Be(1);
        item.Priority.Should().Be("P1");
        item.Reasons.Should().Equal("Mangler testtilfelle", "Forventet resultat dokumentert i spesifikasjonen", "Høy prioritet (P1)", "Forutsetninger beskrevet",
            "Kildegrunnlaget er oppdatert");
        item.Clarifications.Should().BeEmpty();
    }

    [Fact]
    public void Priority_is_never_invented()
    {
        var item = One(Scenario("a"));

        item.Priority.Should().BeNull();
        item.Reasons.Should().Contain("Prioritet ikke oppgitt i kilden");
        ScenarioWorkQueues.PriorityNumber("High").Should().BeNull("only a declared P<n> is a priority");
    }

    [Fact]
    public void A_high_priority_scenario_with_a_source_conflict_needs_clarification_and_is_not_recommended()
    {
        var conflicted = Scenario("a", Story(1, "P1"), 1) with
        {
            Conflicts = [new ScenarioConflict { Explanation = "Two sources disagree", Claims = [new("x", "Spec", "A", "spec.md"), new("y", "Code", "B", "Service.cs")] }],
        };

        var item = One(conflicted);

        item.Queues.Should().Equal(Q.NeedsClarification);
        item.Rank.Should().BeNull();
        item.Clarifications.Should().Contain("Kildene beskriver ulike forventede resultater");
        item.Reasons.Should().Contain("Trenger avklaring før klargjøring");
    }

    [Theory]
    [InlineData(null, CandidateConfidence.ExplicitSpecification, "Forventet resultat mangler i kilden")]
    [InlineData("It works", CandidateConfidence.SourceDerived, "Forventet resultat er bare utledet fra koden — må bekreftes")]
    [InlineData("It works", CandidateConfidence.CandidateAssociation, "Kilden er ikke gjennomgått — forventet resultat må bekreftes")]
    [InlineData("It works", CandidateConfidence.Insufficient, "Utilstrekkelig grunnlag for forventet resultat")]
    public void A_clear_and_supported_expected_result_is_required_for_a_recommendation(string? expected, CandidateConfidence confidence, string clarification)
    {
        var item = One(Scenario("a", expected: expected, confidence: confidence));

        item.Queues.Should().Equal(Q.NeedsClarification);
        item.Clarifications.Should().Contain(clarification);
    }

    [Fact]
    public void Open_questions_and_unclear_or_unconfirmed_sources_need_clarification()
    {
        One(Scenario("a") with { OpenQuestions = ["Who locks the period?"] }).Clarifications.Should().Equal("1 åpent spørsmål (se spørsmålene)");
        One(Scenario("a") with { OpenQuestions = ["The scenario states no action (When): add the steps.", "Who decides?", "And when?"] }).Clarifications
            .Should().Equal(["2 åpne spørsmål (se spørsmålene)"], "missing steps are preparation work, not a question to clarify");
        One(Scenario("a") with { Gaps = [new EvidenceGap(EvidenceGapKind.AssociationNeedsReview, "?")] }).Clarifications.Should().Equal("Koblingen til kilden må bekreftes");
        One(Scenario("a") with { Gaps = [new EvidenceGap(EvidenceGapKind.BusinessExpectationUnclear, "?")] }).Queues.Should().Equal(Q.NeedsClarification);
    }

    /// <summary>Writing the steps is test-case preparation (2026-11-24): a clear, supported expected result without acceptance-scenario steps is
    /// recommended, with the missing steps named; it ranks after scenarios whose steps are already written.</summary>
    [Fact]
    public void Missing_steps_with_a_clear_expected_result_are_prepared_not_clarified()
    {
        var noSteps = Scenario("fr", Story(1, "P1"), 2) with { OpenQuestions = ["No acceptance scenario describes how to exercise this requirement: add the steps."] };
        var item = One(noSteps);

        item.Queues.Should().Equal(Q.Recommended);
        item.Clarifications.Should().BeEmpty();
        item.Reasons.Should().Contain("Ingen akseptansescenario beskriver stegene — de skrives under klargjøring");
        One(noSteps with { ExpectedBehaviour = null }).Queues.Should().Equal([Q.NeedsClarification], "without an expected result there is nothing to prepare against");

        var withSteps = Scenario("as", Story(1, "P1"), 9);
        ScenarioWorkQueues.Classify([noSteps, withSteps], None, true, true).OrderBy(i => i.Rank).Select(i => i.CandidateId)
            .Should().Equal(["as", "fr"], "written steps first, then steps still to write");
    }

    [Fact]
    public void Missing_test_data_or_environment_access_alone_does_not_block_preparation_but_a_conflict_does()
    {
        One(Scenario("a") with { TestDataNeeds = ["Elev"] }).Queues.Should().Equal(Q.Recommended);
        One(Scenario("a") with { Gaps = [new EvidenceGap(EvidenceGapKind.TestPrerequisiteMissing, "Tilgang til testmiljø?")] }).Queues.Should().Equal(Q.Recommended);
        var conflict = Scenario("a") with
        {
            Conflicts = [new ScenarioConflict { Explanation = "200 or 503", Claims = [new("x", "Spec", "HTTP 200", "spec.md"), new("y", "Contract", "HTTP 503", "api.yaml")] }],
        };
        One(conflict).Queues.Should().Equal([Q.NeedsClarification], "a contradiction about the expected result must be resolved first");
    }

    [Fact]
    public void Ready_to_prepare_is_not_ready_to_execute()
    {
        var item = One(Scenario("a") with
        {
            TestDataNeeds = ["Child", "School"],
            Gaps = [new EvidenceGap(EvidenceGapKind.TestPrerequisiteMissing, "?"), new EvidenceGap(EvidenceGapKind.VerificationMechanismMissing, "?")],
        });

        item.Queues.Should().Equal([Q.Recommended], "execution needs do not block preparing the test case");
        item.ExecutionNeeds.Should().Equal("Trenger testdata: Child, School", "Trenger testdata, tilgang eller oppsett før kjøring", "Mangler en måte å observere resultatet på");
    }

    [Fact]
    public void Draft_in_progress_and_approved_cases_make_a_scenario_prepared_not_recommended()
    {
        One(Scenario("a"), new ScenarioCaseCoverage { Draft = 1 }).Queues.Should().Equal(Q.Prepared);
        One(Scenario("a"), new ScenarioCaseCoverage { InProgress = 1 }).Reasons.Should().Contain("Har 1 testtilfelle");
        One(Scenario("a"), new ScenarioCaseCoverage { Approved = 2 }).Reasons.Should().Contain("Har 2 godkjente testtilfeller");
        One(Scenario("a"), new ScenarioCaseCoverage { Draft = 1, Approved = 1 }).Reasons.Should().Contain("Har 2 testtilfeller");
    }

    [Fact]
    public void Historical_archived_and_excluded_cases_are_not_current_coverage()
    {
        One(Scenario("a"), new ScenarioCaseCoverage { Historical = 2 }).Queues.Should().Equal(Q.Recommended);
        var archived = One(Scenario("a"), new ScenarioCaseCoverage { Archived = 1 });
        archived.Queues.Should().Equal(Q.Recommended);
        archived.Reasons.Should().Contain("Finnes også i en arkivert plan");
        var excluded = One(Scenario("a"), new ScenarioCaseCoverage { Excluded = 1 });
        excluded.Queues.Should().BeEmpty("a reviewer decided not to test it");
        excluded.Reasons.Should().Equal("Bevisst utelatt i en plan");
    }

    [Fact]
    public void Unknown_coverage_is_never_treated_as_no_test_case()
    {
        var item = One(Scenario("a"), complete: false);

        item.Queues.Should().BeEmpty();
        item.Reasons.Should().Equal("Ukjent om det finnes testtilfeller (en plan kunne ikke leses)");
    }

    [Fact]
    public void Changed_source_is_its_own_queue_and_blocks_a_recommendation()
    {
        var changed = One(Scenario("a"), new ScenarioCaseCoverage { Approved = 1, SourceChanged = 1 });
        changed.Queues.Should().Equal(Q.SourceChanged, Q.Prepared);
        changed.Reasons.Should().Contain("Kildegrunnlaget er endret for 1 testtilfelle");

        var old = One(Scenario("a"), current: false);
        old.Queues.Should().BeEmpty("nothing is recommended from an older source version");
        old.Reasons.Should().Equal("Funnet i en eldre kildeversjon enn den gjeldende");
        One(Scenario("a"), new ScenarioCaseCoverage { Draft = 1 }, current: false).Queues.Should().Contain(Q.SourceChanged);
    }

    [Fact]
    public void Ranking_is_deterministic_priority_first_then_support_then_preconditions_then_specification_order()
    {
        var p2 = Story(1, "P2"); var p1 = Story(2, "P1"); var none = Story(3, null);
        List<DiscoveredScenario> all =
        [
            Scenario("p2-first", p2, 1),
            Scenario("p1-contract", p1, 1, confidence: CandidateConfidence.ContractDerived),
            Scenario("p1-second", p1, 3),
            Scenario("p1-spec-preconditions", p1, 4, preconditions: ["Logged in"]),
            Scenario("no-priority", none, 1),
            Scenario("p1-spec", p1, 2),
        ];
        string[] expected = ["p1-spec-preconditions", "p1-spec", "p1-second", "p1-contract", "p2-first", "no-priority"];

        Ranked(all).Should().Equal(expected);
        Ranked([.. Enumerable.Reverse(all)]).Should().Equal(expected, "the input order never decides");

        static IEnumerable<string> Ranked(List<DiscoveredScenario> list) =>
            ScenarioWorkQueues.Classify(list, None, true, true).OrderBy(i => i.Rank).Select(i => i.CandidateId);
    }

    [Fact]
    public void A_scenario_can_be_in_several_queues()
    {
        var item = One(Scenario("a") with { OpenQuestions = ["?"] }, new ScenarioCaseCoverage { Draft = 1, SourceChanged = 1 });

        item.Queues.Should().Equal(Q.SourceChanged, Q.NeedsClarification, Q.Prepared);
    }

    [Fact]
    public void Two_thousand_scenarios_are_classified_with_every_one_accounted_for()
    {
        var stories = Enumerable.Range(1, 40).Select(n => Story(n, n % 3 == 0 ? null : $"P{n % 3}")).ToList();
        var list = Enumerable.Range(0, 2000).Select(i => Scenario($"s{i:0000}", stories[i % 40], i / 40 + 1,
            expected: i % 7 == 0 ? null : "ok", confidence: i % 11 == 0 ? CandidateConfidence.SourceDerived : CandidateConfidence.ExplicitSpecification)).ToList();
        var coverage = list.Where((_, i) => i % 5 == 0).ToDictionary(c => c.CandidateId, _ => new ScenarioCaseCoverage { Draft = 1 });

        var items = ScenarioWorkQueues.Classify(list, coverage, true, true);

        items.Should().HaveCount(2000);
        items.Select(i => i.CandidateId).Should().Equal(list.Select(c => c.CandidateId), "every scenario is kept, in the discovery's order");
        var recommended = items.Where(i => i.Queues.Contains(Q.Recommended)).ToList();
        recommended.Select(i => i.Rank).Should().BeEquivalentTo(Enumerable.Range(1, recommended.Count).Select(n => (int?)n), "ranks are 1..n without gaps");
        recommended.Should().OnlyContain(i => !coverage.ContainsKey(i.CandidateId));
        items.Where(i => coverage.ContainsKey(i.CandidateId)).Should().OnlyContain(i => i.Queues.Contains(Q.Prepared));
    }

    // ── The service over saved plans ────────────────────────────────────────────────────────────────────────

    private const string Spec = "SkoleService/specs/001-skoleservice/spec.md";
    private readonly string _database = TestPostgres.TemporaryName("birknext_swq");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "swq-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ScenarioDiscoveryCache _discoveries = new();

    private AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(TestPostgres.Temporary(_database).ConnectionString).Options);

    private TestPlanSuggestionService Service(AppDbContext db)
    {
        var registry = new TestDataRegistryStore(Path.Combine(_dir, "reg"), NullLogger<TestDataRegistryStore>.Instance);
        var catalog = new TestDataCatalog([new NoValues(), new RegisteredTestDataProvider(registry)], TimeProvider.System, registry, null, new TestDataIdentityStore(Path.Combine(_dir, "ids")));
        var sources = new NoSources();
        var testData = new TestDataCatalogService(new TestDataSchemaValidationService(catalog, new NoSchemas()), catalog, registry, new TestDataCandidateService(sources, registry), sources, TimeProvider.System);
        return new TestPlanSuggestionService(db, new SystemGuideService(db, [], Array.Empty<IIntegrationJourneyPack>()), new SourceBaselineService(db, new IqrSourceStore(db)), testData,
            Array.Empty<IIntegrationJourneyPack>(), new CriticalE2EStore(Path.Combine(_dir, "e2e"), NullLogger<CriticalE2EStore>.Instance), discoveries: _discoveries);
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

    private static QaTestCandidate Valid(string id = "scenario-7") => new()
    { Id = id, Title = "Valid report", Given = "a valid report", When = "the report is submitted", Then = "the report is accepted", RequirementIds = ["FR-001"] };

    private static QaTestCandidate PastAge(string id = "scenario-42") => new()
    { Id = id, Title = "Child past compulsory school age", Given = "a child past compulsory school age", When = "the period locks", Then = "the child is not expected" };

    private Task<ScenarioDiscoveryResult> DiscoverAsync(string project, params QaTestCandidate[] scenarios) =>
        new TestScenarioDiscoveryService(new IqrSourceStore(null!), [new SpecificationScenarioProvider()], _discoveries).DiscoverAsync(new ScenarioDiscoveryRequest
        {
            ProjectKey = project, ProjectName = "SkoleService",
            Workspace = new CoverageWorkspaceEvidence
            {
                SpecificationAvailable = true, SpecificationArtifacts = [new("spec-1", Spec, "SkoleService", "fp")],
                Requirements = [new("FR-001", "The service MUST accept a valid report.", null)],
                AcceptanceScenarios = [.. scenarios],
            },
        }, default);

    private static string Id(ScenarioDiscoveryResult d, string title) => d.Candidates.Single(c => c.Title == title).CandidateId;

    private static async Task<TestPlanDraft> PlanAsync(TestPlanSuggestionService service, ScenarioDiscoveryResult d, string project, params string[] candidateIds) =>
        await service.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = project, ProjectName = "SkoleService", DiscoveryId = d.DiscoveryId, CandidateIds = [.. candidateIds] }, "tester", default);

    private static ScenarioWorkItem Of(ScenarioWorkQueueView view, string id) => view.Items.Single(i => i.CandidateId == id);

    [Fact]
    public async Task Saved_draft_and_approved_cases_move_a_scenario_from_recommended_to_prepared()
    {
        await using var db = Context();
        var d = await DiscoverAsync("import:p1", Valid(), PastAge());
        var service = Service(db);
        var valid = Id(d, "Valid report");
        var age = Id(d, "Child past compulsory school age");

        var before = await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default);
        before.CoverageComplete.Should().BeTrue();
        before.DiscoveryCurrent.Should().BeTrue();
        Of(before, valid).Queues.Should().Equal(Q.Recommended);
        Of(before, age).Queues.Should().Equal(Q.Recommended);

        var plan = await PlanAsync(service, d, "import:p1", valid);
        var drafted = await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default);
        Of(drafted, valid).Queues.Should().Equal(Q.Prepared);
        Of(drafted, valid).Coverage!.Current.Should().Be(1);
        Of(drafted, valid).Coverage!.Plans.Should().Equal(plan.Id);
        Of(drafted, age).Queues.Should().Equal([Q.Recommended], "saving a case changes only its own scenario");

        var caseId = plan.Cases.Single().Source.ScenarioId;
        await service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, caseId, TestCaseReviewState.Accepted, null), "lead", default);
        var approved = Of(await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default), valid);
        approved.Coverage!.Approved.Should().Be(1);
        approved.Reasons.Should().Contain("Har 1 godkjent testtilfelle");
    }

    [Fact]
    public async Task Several_cases_and_plans_count_per_scenario_and_archived_plans_are_not_current_coverage()
    {
        await using var db = Context();
        var d = await DiscoverAsync("import:p1", Valid(), PastAge());
        var service = Service(db);
        var valid = Id(d, "Valid report");
        var first = await PlanAsync(service, d, "import:p1", valid);
        var second = await PlanAsync(service, d, "import:p1", valid);

        var both = Of(await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default), valid);
        both.Coverage!.Current.Should().Be(2);
        both.Coverage.Plans.Should().BeEquivalentTo([first.Id, second.Id]);

        await service.ArchiveAsync(first.Id, new TestPlanLifecycleRequest(first.Revision), "lead", default);
        await service.ArchiveAsync(second.Id, new TestPlanLifecycleRequest(second.Revision), "lead", default);
        var archived = Of(await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default), valid);
        archived.Coverage!.Current.Should().Be(0);
        archived.Coverage.Archived.Should().Be(2);
        archived.Queues.Should().Equal(Q.Recommended);
        archived.Reasons.Should().Contain("Finnes også i en arkivert plan");
    }

    // ── Saved test plan membership (2026-11-26) ──

    [Fact]
    public async Task Membership_names_each_saved_plan_with_its_case_and_follows_archive_restore_and_rename_without_writing()
    {
        await using var db = Context();
        var d = await DiscoverAsync("import:p1", Valid(), PastAge());
        var service = Service(db);
        var valid = Id(d, "Valid report");
        var age = Id(d, "Child past compulsory school age");

        Of(await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default), valid).Coverage.Should().BeNull("no plan: no membership");

        var first = await PlanAsync(service, d, "import:p1", valid);
        var second = await PlanAsync(service, d, "import:p1", valid, age);
        var cov = Of(await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default), valid).Coverage!;
        cov.Memberships.Should().HaveCount(2);
        cov.ActivePlans.Select(m => m.PlanId).Should().BeEquivalentTo([first.Id, second.Id], "one scenario in two active plans");
        cov.InActivePlan.Should().BeTrue();
        var inFirst = cov.Memberships.Single(m => m.PlanId == first.Id);
        (inFirst.PlanName, inFirst.CaseId, inFirst.Active).Should().Be((first.PlanName, first.Cases.Single().Source.ScenarioId, true));
        inFirst.SuitePath.Should().Be(PlanStructure.PathOf(first.Suites, first.Cases.Single().SuiteId), "the saved suite path, not a constructed one");
        Of(await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default), age).Coverage!.ActivePlans.Select(m => m.PlanId).Should().Equal(second.Id);

        // Read-only: computing membership never writes a revision.
        (await service.RevisionsAsync(first.Id, default)).Should().HaveCount(1);

        // Archive: the plan is no longer active membership, but is kept as archived history.
        await service.ArchiveAsync(first.Id, new TestPlanLifecycleRequest(first.Revision), "lead", default);
        cov = Of(await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default), valid).Coverage!;
        cov.ActivePlans.Select(m => m.PlanId).Should().Equal(second.Id);
        cov.Memberships.Single(m => m.PlanId == first.Id).Active.Should().BeFalse();

        await service.ArchiveAsync(second.Id, new TestPlanLifecycleRequest(second.Revision), "lead", default);
        cov = Of(await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default), valid).Coverage!;
        (cov.InActivePlan, cov.ArchivedOnly).Should().Be((false, true), "archived only: history, not active membership");

        // Restore and rename: the current name is shown.
        var restored = await service.RestoreFromArchiveAsync(first.Id, new TestPlanLifecycleRequest((await service.DraftAsync(first.Id, default)).Revision), "lead", default);
        var current = await service.DraftAsync(first.Id, default);
        await service.SaveAsync(first.Id, new SaveTestPlanDraftRequest
        {
            BaseRevision = current.Revision, PlanName = "Skolenærvær – E2E", ScenarioIds = [.. current.Cases.Select(c => c.Source.ScenarioId)], Suites = current.Suites,
            Cases = [.. current.Cases.Select(c => new TestCaseHumanPart { ScenarioId = c.Source.ScenarioId, SuiteId = c.SuiteId, Edits = c.Edits })],
        }, "lead", default);
        cov = Of(await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default), valid).Coverage!;
        cov.ActivePlans.Single().PlanName.Should().Be("Skolenærvær – E2E", "a renamed plan shows its current name");
    }

    [Fact]
    public async Task An_excluded_case_is_not_membership_and_another_project_never_shows_this_projects_plans()
    {
        await using var db = Context();
        var d = await DiscoverAsync("import:p1", Valid());
        var service = Service(db);
        var valid = Id(d, "Valid report");
        var plan = await PlanAsync(service, d, "import:p1", valid);
        var caseId = plan.Cases.Single().Source.ScenarioId;
        await service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, caseId, TestCaseReviewState.Excluded, "ikke relevant"), "lead", default);
        Of(await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default), valid).Coverage!.Memberships.Should().BeEmpty("an excluded case is a decision, not membership");

        var other = await DiscoverAsync("import:other", Valid());
        Of(await service.WorkQueuesAsync("import:other", other.DiscoveryId, default), Id(other, "Valid report")).Coverage.Should().BeNull("plans of another project are never shown");
    }

    [Fact]
    public async Task An_excluded_case_is_a_decision_not_coverage()
    {
        await using var db = Context();
        var d = await DiscoverAsync("import:p1", Valid());
        var service = Service(db);
        var valid = Id(d, "Valid report");
        var plan = await PlanAsync(service, d, "import:p1", valid);
        await service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, plan.Cases.Single().Source.ScenarioId, TestCaseReviewState.Excluded, "Covered by an integration test"), "lead", default);

        var item = Of(await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default), valid);

        item.Coverage!.Excluded.Should().Be(1);
        item.Coverage.Current.Should().Be(0);
        item.Queues.Should().BeEmpty();
        item.Reasons.Should().Equal("Bevisst utelatt i en plan");
    }

    [Fact]
    public async Task A_case_keeps_its_scenario_when_the_scenario_is_renumbered_in_a_new_discovery()
    {
        await using var db = Context();
        var first = await DiscoverAsync("import:p1", Valid(), PastAge("scenario-42"));
        var service = Service(db);
        await PlanAsync(service, first, "import:p1", Id(first, "Child past compulsory school age"));

        var renumbered = await DiscoverAsync("import:p1", Valid(), PastAge("scenario-99"));
        var view = await service.WorkQueuesAsync("import:p1", renumbered.DiscoveryId, default);

        var age = Of(view, Id(renumbered, "Child past compulsory school age"));
        age.Coverage!.Current.Should().Be(1, "the saved case follows its scenario across the new numbering");
        age.Queues.Should().Contain(Q.Prepared).And.NotContain(Q.Recommended);
        view.UnmatchedCases.Should().Be(0);
    }

    [Fact]
    public async Task Cases_of_a_scenario_that_left_the_source_are_counted_not_dropped()
    {
        await using var db = Context();
        var first = await DiscoverAsync("import:p1", Valid(), PastAge());
        var service = Service(db);
        await PlanAsync(service, first, "import:p1", Id(first, "Child past compulsory school age"));

        var without = await DiscoverAsync("import:p1", Valid());
        var view = await service.WorkQueuesAsync("import:p1", without.DiscoveryId, default);

        view.UnmatchedCases.Should().Be(1);
        view.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task An_unreadable_plan_makes_coverage_unknown_and_nothing_is_recommended()
    {
        await using var db = Context();
        var d = await DiscoverAsync("import:p1", Valid());
        var service = Service(db);
        db.TestPlanDrafts.Add(new TestPlanDraftRecord
        {
            Id = Guid.NewGuid(), GuideKey = "project:import:p1", PlanName = "Broken", Revision = 1, ContentJson = "{ not json", CreatedBy = "x", CreatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "x", UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var view = await service.WorkQueuesAsync("import:p1", d.DiscoveryId, default);

        view.CoverageComplete.Should().BeFalse();
        view.PlansUnreadable.Should().Be(1);
        view.Items.Should().OnlyContain(i => !i.Queues.Contains(Q.Recommended));
    }

    [Fact]
    public async Task Plans_of_another_project_never_count()
    {
        await using var db = Context();
        var mine = await DiscoverAsync("import:p1", Valid());
        var other = await DiscoverAsync("import:other", Valid());
        var service = Service(db);
        await PlanAsync(service, other, "import:other", Id(other, "Valid report"));

        var view = await service.WorkQueuesAsync("import:p1", mine.DiscoveryId, default);

        Of(view, Id(mine, "Valid report")).Coverage.Should().BeNull();
        Of(view, Id(mine, "Valid report")).Queues.Should().Equal(Q.Recommended);
    }

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
