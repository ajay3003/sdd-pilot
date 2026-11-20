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
/// Scenario-centric Plan &amp; prepare on a fresh migrated PostgreSQL database: several test cases under one scenario (added, duplicated, from an
/// evidence-backed variant), each with its own identity and revisions; translation of one scenario through a deterministic local translator
/// (identifiers kept, review untouched, a person's Norwegian text never overwritten unasked, unavailable and failed honestly); requirement
/// suggestions confirmed or rejected by a tester and saved with who and when; scenario-level links never counted as a case's coverage; the
/// Azure DevOps export in the chosen language per case; everything read back after a reload. Synthetic specification in own wording.
/// </summary>
public sealed class ScenarioCentricPlanningServiceTests : IAsyncLifetime, IDisposable
{
    private const string Spec = "SkoleService/specs/001-skoleservice/spec.md";
    private readonly string _database = TestPostgres.TemporaryName("birknext_scp");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scp-" + Guid.NewGuid().ToString("N")[..8]);
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

    /// <summary>A plan with acceptance scenario 42 (child past compulsory school age) and scenario 7 (valid report) prepared from discovery.</summary>
    private async Task<(TestPlanSuggestionService Service, TestPlanDraft Plan, string S42, string S7)> PlanAsync(AppDbContext db, string project = "import:p1")
    {
        var discovery = await new TestScenarioDiscoveryService(new IqrSourceStore(null!), [new SpecificationScenarioProvider()], _discoveries).DiscoverAsync(new ScenarioDiscoveryRequest
        {
            ProjectKey = project, ProjectName = "SkoleService",
            Workspace = new CoverageWorkspaceEvidence
            {
                SpecificationAvailable = true, SpecificationArtifacts = [new("spec-1", Spec, "SkoleService", "fp")],
                Requirements = [new("FR-027", "A child MUST be counted as expected only when of compulsory school age; children past compulsory school age are not expected.", null),
                                new("FR-001", "The service MUST accept a valid report.", null)],
                AcceptanceScenarios =
                [
                    new QaTestCandidate { Id = "scenario-42", Title = "Child past compulsory school age", Given = "a child past compulsory school age", When = "the period locks",
                        Then = "the child is not expected, even with an upper secondary education relationship" },
                    new QaTestCandidate { Id = "scenario-7", Title = "Valid report", Given = "a valid report", When = "the report is submitted", Then = "the report is accepted", RequirementIds = ["FR-001"] },
                ],
            },
        }, default);
        var service = Service(db);
        var scenarios = discovery.Candidates.Where(c => c.CanonicalKey.StartsWith("scenario:", StringComparison.Ordinal)).ToList();
        var plan = await service.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = project, ProjectName = "SkoleService", DiscoveryId = discovery.DiscoveryId,
            CandidateIds = [.. scenarios.Select(c => c.CandidateId)] }, "tester", default);
        return (service, plan, scenarios.Single(c => c.Title.Contains("past")).CandidateId, scenarios.Single(c => c.Title == "Valid report").CandidateId);
    }

    private static SaveTestPlanDraftRequest Human(TestPlanDraft plan, Func<TestCaseDraft, TestCaseHumanPart, TestCaseHumanPart>? change = null) => new()
    {
        BaseRevision = plan.Revision, PlanName = plan.PlanName, ScenarioIds = [.. plan.Cases.Select(c => c.Source.ScenarioId)], Suites = plan.Suites,
        Cases = [.. plan.Cases.Select(c =>
        {
            var part = new TestCaseHumanPart { ScenarioId = c.Source.ScenarioId, SuiteId = c.SuiteId, Edits = c.Edits, Risk = c.Risk, RequirementLinks = [.. c.RequirementLinks],
                Norwegian = c.Norwegian, RejectedSuggestions = c.RejectedSuggestions };
            return change is null ? part : change(c, part);
        })],
        RequirementCatalog = new ProjectRequirementCatalog { ProjectKey = plan.ProjectKey!, Requirements =
            [new("FR-027", "A child MUST be counted as expected only when of compulsory school age.", Spec), new("FR-001", "The service MUST accept a valid report.", Spec)] },
    };

    // ── Generation ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_prepared_scenario_is_a_procedure_not_one_copied_line_and_its_verification_method_is_open()
    {
        await using var db = Context();
        var (_, plan, s42, _) = await PlanAsync(db);
        var c = plan.Cases.Single(x => x.Source.ScenarioId == s42);

        c.EffectiveSteps.Should().Equal(TestScenarioDiscoveryEngine.PrepareStepPrefix + "a child past compulsory school age", TestScenarioDiscoveryEngine.ActionStepPrefix + "the period locks");
        c.EffectiveStepExpected.Should().Equal(TestScenarioDiscoveryEngine.PrepareStepExpected, "the child is not expected, even with an upper secondary education relationship");
        c.Missing.Should().Contain("Verification method: " + ScenarioPlanning.NeedsVerificationMethod);
        c.Testability.Observation.Should().Be(TestabilityFact.Missing, "a stated outcome is not a way to observe it");
        c.Testability.Ready.Should().BeFalse();
        c.Review.Should().Be(TestCaseReviewState.Generated);
    }

    // ── One scenario → several test cases ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Several_cases_are_written_under_one_scenario_each_with_its_own_identity_and_nothing_invented()
    {
        await using var db = Context();
        var (service, plan, s42, s7) = await PlanAsync(db);

        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "No report submitted", null, null, ScenarioKey: s42), "anna", default);
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Valid report submitted", null, null, ScenarioKey: s42), "anna", default);

        var group = ScenarioPlanning.Groups(plan).Single(g => g.Key == s42);
        group.Cases.Should().HaveCount(3);
        group.Cases.Select(c => c.Source.ScenarioId).Should().OnlyHaveUniqueItems();
        var added = group.Cases.Where(c => c.Source.Kind == ScenarioSourceKind.Manual).ToList();
        added.Should().OnlyContain(c => c.ScenarioKey == s42 && c.ScenarioTitle == "Child past compulsory school age");
        added.Should().OnlyContain(c => c.EffectivePreconditions == "a child past compulsory school age", "the scenario's starting situation is the only thing carried over");
        added.Should().OnlyContain(c => c.EffectiveSteps.Count == 0 && c.EffectiveExpected == null, "no step or outcome is invented");
        ScenarioPlanning.Groups(plan).Single(g => g.Key == s7).Cases.Should().ContainSingle();

        var unknown = () => service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "x", null, null, ScenarioKey: "tsc-not-in-plan"), "anna", default);
        (await unknown.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("scenario-unknown");
    }

    [Fact]
    public async Task Editing_one_case_never_changes_its_siblings_and_each_save_is_a_revision()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "No report submitted", null, null, ScenarioKey: s42), "anna", default);
        var variant = plan.Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual).Source.ScenarioId;

        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == variant
            ? part with { Edits = part.Edits with { Steps = ["Lock the period without any report"], StepExpected = ["The child is not expected"] } } : part), "anna", default);

        plan = await Service(Context()).DraftAsync(plan.Id, default); // reload
        plan.Cases.Single(c => c.Source.ScenarioId == variant).EffectiveSteps.Should().Equal("Lock the period without any report");
        plan.Cases.Single(c => c.Source.ScenarioId == s42).EffectiveSteps.Should().HaveCount(2, "the scenario's own case is untouched");
        (await Service(Context()).RevisionsAsync(plan.Id, default)).Select(r => r.Reason).Should().Contain("Test case added").And.Contain("Saved");
    }

    [Fact]
    public async Task A_duplicate_is_a_new_case_with_the_text_but_without_review_history_or_Azure_DevOps_id()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        await service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, s42, TestCaseReviewState.NeedsClarification, "How is the lock triggered?"), "lead", default);
        plan = await service.DraftAsync(plan.Id, default);

        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Child past school age (copy)", null, null, CopyFrom: s42), "anna", default);

        var copy = plan.Cases.Single(c => c.DuplicatedFrom == s42);
        copy.Source.ScenarioId.Should().NotBe(s42).And.StartWith("manual-");
        copy.ParentScenario.Should().Be(s42, "the copy stays under the same scenario");
        copy.EffectiveSteps.Should().Equal(plan.Cases.Single(c => c.Source.ScenarioId == s42).EffectiveSteps);
        copy.ReviewHistory.Should().BeEmpty();
        copy.Review.Should().Be(TestCaseReviewState.Generated);
        copy.VerifiedAzureDevOpsTestCaseId.Should().BeNull();
        copy.EditHistory.Single().Fields.Single().Should().StartWith("Duplicated from");
    }

    [Fact]
    public async Task A_variant_is_added_only_from_the_scenarios_evidence()
    {
        await using var db = Context();
        var (service, plan, s42, s7) = await PlanAsync(db);
        var variants = await service.VariantsAsync(plan.Id, s42, default);
        variants.Should().ContainSingle(v => v.Basis.Contains("even with an upper secondary education relationship"));
        (await service.VariantsAsync(plan.Id, s7, default)).Should().BeEmpty("the valid-report scenario states no further condition");

        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, variants[0].Title, null, null, ScenarioKey: s42, Variant: variants[0].Key), "anna", default);
        var added = plan.Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual);
        added.EffectivePreconditions.Should().Contain("a child past compulsory school age").And.ContainEquivalentOf("an upper secondary education relationship");
        added.EffectiveExpected.Should().Contain("Even with an upper secondary education relationship");
        (await service.VariantsAsync(plan.Id, s42, default)).Should().BeEmpty("the variant is written now");
    }

    // ── Translation ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A deterministic local translator: prefixes every word piece, keeps placeholders. Optionally drops placeholders (a bad service).</summary>
    private sealed class FakeTranslator(bool available = true, bool losePlaceholders = false, bool fail = false) : ITestCaseTranslator
    {
        public int Calls { get; private set; }
        public TranslationAvailability Availability => new(available, available ? "Fake" : null, available ? "Fake translator." : "No translation service is enabled.");
        public Task<List<string?>> TranslateAsync(IReadOnlyList<string?> texts, CancellationToken ct)
        {
            Calls++;
            if (fail) throw new TestCaseTranslationException("The fake service is down. Nothing was changed.");
            return Task.FromResult(texts.Select(t => t is null ? null : losePlaceholders ? "NB: " + System.Text.RegularExpressions.Regex.Replace(t, "⟦\\d+⟧", "") : "NB: " + t).ToList());
        }
    }

    [Fact]
    public async Task Translating_a_scenario_translates_only_its_cases_keeps_identifiers_and_the_original_and_approves_nothing()
    {
        await using var db = Context();
        var (service, plan, s42, s7) = await PlanAsync(db);
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Report for FR-027 is accepted", null, null, ScenarioKey: s42), "anna", default);

        var result = await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(), "anna", default);

        result.Translated.Should().HaveCount(2);
        var after = result.Draft!;
        after.Cases.Where(c => c.ParentScenario == s42).Should().OnlyContain(c => c.Norwegian != null && c.Norwegian.Origin == TranslationOrigin.Machine && c.Review == TestCaseReviewState.Generated);
        after.Cases.Single(c => c.Source.ScenarioId == s7).Norwegian.Should().BeNull("another scenario is not translated");
        var manual = after.Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual);
        manual.Norwegian!.Title.Should().Be("NB: Report for FR-027 is accepted", "the id came back unchanged");
        manual.EffectiveTitle.Should().Be("Report for FR-027 is accepted", "the original is never replaced");
        (await Service(Context()).RevisionsAsync(plan.Id, default)).First().Reason.Should().Be("Translated to Norwegian");
    }

    [Fact]
    public async Task A_persons_Norwegian_text_is_kept_unless_overwriting_is_asked_and_goes_stale_when_the_original_changes()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        plan = (await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(), "anna", default)).Draft!;

        // A person edits the Norwegian title: Machine → MachineEdited, recorded with who.
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42 ? part with { Norwegian = c.Norwegian! with { Title = "Barn over opplæringspliktig alder" } } : part), "inger", default);
        var edited = plan.Cases.Single(c => c.Source.ScenarioId == s42).Norwegian!;
        edited.Origin.Should().Be(TranslationOrigin.MachineEdited);
        edited.By.Should().Be("inger");

        var again = await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(), "anna", default);
        again.KeptPersonEdits.Should().ContainSingle();
        again.Draft.Should().BeNull("nothing changed");
        (await service.DraftAsync(plan.Id, default)).Cases.Single(c => c.Source.ScenarioId == s42).Norwegian!.Title.Should().Be("Barn over opplæringspliktig alder");

        var overwritten = await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42, Overwrite: true), new FakeTranslator(), "anna", default);
        overwritten.Draft!.Cases.Single(c => c.Source.ScenarioId == s42).Norwegian!.Origin.Should().Be(TranslationOrigin.Machine);
        plan = overwritten.Draft!;

        // The original changes after the translation: the Norwegian text is kept and flagged.
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42 ? part with { Edits = part.Edits with { Title = "Child past school age at lock" } } : part), "anna", default);
        var stale = plan.Cases.Single(c => c.Source.ScenarioId == s42);
        ScenarioPlanning.StateOf(stale).Should().Be(TranslationState.SourceChanged);
        stale.Norwegian.Should().NotBeNull();
    }

    [Fact]
    public async Task Unavailable_failing_or_identifier_losing_translation_changes_nothing()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);

        var unavailable = () => service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(available: false), "anna", default);
        (await unavailable.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("translation-unavailable");

        var down = await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(fail: true), "anna", default);
        down.Failed.Should().ContainSingle(f => f.Contains("down"));
        down.Draft.Should().BeNull();

        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Report for FR-027", null, null, ScenarioKey: s42), "anna", default);
        var lossy = await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(losePlaceholders: true), "anna", default);
        lossy.Failed.Should().Contain(f => f.Contains("technical identifier"));
        (await service.DraftAsync(plan.Id, default)).Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual).Norwegian.Should().BeNull();
    }

    // ── Requirement suggestions, scenario-level links, traceability ────────────────────────────────────────────────

    [Fact]
    public async Task Confirmed_and_rejected_suggestions_are_saved_with_who_and_a_scenario_level_link_is_never_case_coverage()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);

        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42 ? part with
        {
            RequirementLinks = [new ManualRequirementLink { RequirementId = "FR-027", SpecificationPath = Spec, Scope = RequirementLinkScope.Scenario,
                SuggestionLevel = RequirementSuggestionLevel.Semantic, SuggestionReason = "Both state the same behaviour." }],
            RejectedSuggestions = [new RejectedRequirementSuggestion { RequirementId = "FR-001", SpecificationPath = Spec, RejectedBy = "spoofed" }],
        } : part), "inger", default);

        var c = (await Service(Context()).DraftAsync(plan.Id, default)).Cases.Single(x => x.Source.ScenarioId == s42);
        c.RequirementLinks.Single().Should().Match<ManualRequirementLink>(l => l.Scope == RequirementLinkScope.Scenario && l.SuggestionLevel == RequirementSuggestionLevel.Semantic && l.LinkedBy == "inger");
        c.RejectedSuggestions!.Single().Should().Match<RejectedRequirementSuggestion>(r => r.RequirementId == "FR-001" && r.RejectedBy == "inger", "the server records who rejected it");

        var trace = await Service(Context()).PreparedCaseTraceAsync(plan.ProjectKey!, [Spec], ["FR-027", "FR-001"], default);
        var link = trace.CaseList.Single(x => x.CaseId == s42).Requirements.Single(r => r.RequirementId == "FR-027");
        link.Counted.Should().BeFalse();
        link.Explanation.Should().Contain("scenario-level");
        trace.Requirements.Should().NotContain(r => r.RequirementId == "FR-027", "a scenario-level association is not test design of any case");
    }

    [Fact]
    public async Task Plans_and_scenarios_of_another_project_are_never_mixed()
    {
        await using var db = Context();
        var (service, planA, s42, _) = await PlanAsync(db, "import:p1");
        var (_, planB, _, _) = await PlanAsync(db, "import:p2");
        var foreign = () => service.AddManualCaseAsync(planB.Id, new AddManualTestCaseRequest(planB.Revision, "x", null, null, ScenarioKey: s42), "anna", default);
        // Candidate ids are project-scoped, so p1's scenario is not a scenario of p2's plan.
        (await foreign.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("scenario-unknown");
        planA.Cases.Select(c => c.Source.ScenarioId).Should().NotIntersectWith(planB.Cases.Select(c => c.Source.ScenarioId));
    }

    // ── Azure DevOps export ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_export_carries_each_case_of_a_scenario_in_the_chosen_language_and_never_invents_an_id()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "No report submitted", null, null, ScenarioKey: s42), "anna", default);
        var manual = plan.Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual).Source.ScenarioId;
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == manual
            ? part with { Edits = part.Edits with { Steps = ["Lock the period without a report"], StepExpected = ["The child is not expected"] } } : part), "anna", default);
        plan = (await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(), "anna", default)).Draft!;

        var nb = AdoTestPlanExporter.Preview(plan, new AdoExportRequest { ProjectKey = plan.ProjectKey!, DraftId = plan.Id, PlanRevision = plan.Revision, IncludeNotApproved = true, Language = ScenarioPlanning.Norwegian });
        var original = AdoTestPlanExporter.Preview(plan, new AdoExportRequest { ProjectKey = plan.ProjectKey!, DraftId = plan.Id, PlanRevision = plan.Revision, IncludeNotApproved = true });

        var nbCases = nb.Cases.Where(c => c.ScenarioKey == s42).ToList();
        nbCases.Should().HaveCount(2).And.OnlyContain(c => c.Language == "Norwegian" && c.AzureDevOpsId == null);
        nb.Rows.Where(r => r.Title.StartsWith("NB: ", StringComparison.Ordinal)).Should().NotBeEmpty().And.OnlyContain(r => r.StepAction.StartsWith("NB: ", StringComparison.Ordinal) && r.StepExpected.StartsWith("NB: ", StringComparison.Ordinal),
            "a Norwegian case is Norwegian in every cell");
        nb.Rows.Single(r => r.StepAction == "NB: Lock the period without a report").Description.Should().Contain("under the scenario \"Child past compulsory school age\"");
        original.Rows.Should().NotContain(r => r.Title.StartsWith("NB: ", StringComparison.Ordinal));
        original.Cases.Should().OnlyContain(c => c.Language == "Original");
        nb.Cases.Where(c => c.ScenarioKey != s42).Should().OnlyContain(c => c.Language == "Original" && c.Issues.Any(i => i.Message.Contains("original language")), "an untranslated case says so");
        nb.Boundary.Should().Contain("Downloading is not importing");
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
