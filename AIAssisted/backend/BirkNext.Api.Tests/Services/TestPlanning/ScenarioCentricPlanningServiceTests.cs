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
    private static CoverageUserStory Story(string id, string title) => new()
    {
        Key = CoverageUserStory.KeyOf(Spec, id), StoryId = id, Ordinal = int.Parse(id[2..]), Title = $"User Story {id[2..]} - {title} (Priority: P2)", Priority = "P2",
        Description = $"About {title}.", IndependentTest = $"Test {title} on its own.", SpecificationPath = Spec,
    };

    private Task<ScenarioDiscoveryResult> DiscoverAsync(string project, string? storyOf42 = null, string id42 = "scenario-42") =>
        new TestScenarioDiscoveryService(new IqrSourceStore(null!), [new SpecificationScenarioProvider()], _discoveries).DiscoverAsync(new ScenarioDiscoveryRequest
        {
            ProjectKey = project, ProjectName = "SkoleService",
            Workspace = new CoverageWorkspaceEvidence
            {
                SpecificationAvailable = true, SpecificationArtifacts = [new("spec-1", Spec, "SkoleService", "fp")],
                Requirements = [new("FR-027", "A child MUST be counted as expected only when of compulsory school age; children past compulsory school age are not expected.", null),
                                new("FR-001", "The service MUST accept a valid report.", null)],
                Stories = storyOf42 is null ? [] : [Story(storyOf42, storyOf42 == "US6" ? "Expected population" : "Coverage breakdown"), Story("US1", "Accept a valid report")],
                AcceptanceScenarios =
                [
                    new QaTestCandidate { Id = id42, Title = "Child past compulsory school age", Given = "a child past compulsory school age", When = "the period locks",
                        Then = "the child is not expected, even with an upper secondary education relationship", StoryKey = storyOf42 is null ? null : CoverageUserStory.KeyOf(Spec, storyOf42), NumberInStory = storyOf42 is null ? null : 3 },
                    new QaTestCandidate { Id = "scenario-7", Title = "Valid report", Given = "a valid report", When = "the report is submitted", Then = "the report is accepted", RequirementIds = ["FR-001"],
                        StoryKey = storyOf42 is null ? null : CoverageUserStory.KeyOf(Spec, "US1"), NumberInStory = storyOf42 is null ? null : 1 },
                ],
            },
        }, default);

    private async Task<(TestPlanSuggestionService Service, TestPlanDraft Plan, string S42, string S7)> PlanWithStoriesAsync(AppDbContext db, string project = "import:p1")
    {
        var discovery = await DiscoverAsync(project, "US6");
        var service = Service(db);
        var scenarios = discovery.Candidates.Where(c => c.CanonicalKey.StartsWith("scenario:", StringComparison.Ordinal)).ToList();
        var plan = await service.CreateProjectAsync(new CreateProjectTestPlanRequest { ProjectKey = project, ProjectName = "SkoleService", DiscoveryId = discovery.DiscoveryId,
            CandidateIds = [.. scenarios.Select(c => c.CandidateId)] }, "tester", default);
        return (service, plan, scenarios.Single(c => c.Title.Contains("past")).CandidateId, scenarios.Single(c => c.Title == "Valid report").CandidateId);
    }

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
                Translations = c.TranslatedLanguages.ToDictionary(l => l, l => c.Translation(l)!), RejectedSuggestions = c.RejectedSuggestions };
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

        // 2026-11-24: the Given is the precondition (not a generic "prepare the starting situation" step); the When is the action as written, and
        // how it is brought about in a test environment is an open question, never invented.
        c.EffectiveSteps.Should().Equal("The period locks.");
        c.EffectiveStepExpected.Should().Equal("the child is not expected, even with an upper secondary education relationship");
        c.Generated.Preconditions.Should().Be("a child past compulsory school age", "the Given stays verbatim as the precondition");
        c.Generated.OpenQuestions.Should().Contain(q => q.StartsWith("How this is brought about in the test environment is not stated", StringComparison.Ordinal));
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
        plan.Cases.Single(c => c.Source.ScenarioId == s42).EffectiveSteps.Should().Equal(["The period locks."], "the scenario's own case is untouched");
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

    [Fact]
    public async Task An_accepted_design_proposal_becomes_one_case_and_accepting_it_again_is_refused()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        var v = (await service.VariantsAsync(plan.Id, s42, default))[0];
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, v.Title, null, null, ScenarioKey: s42, Variant: v.Key), "anna", default);
        var again = () => service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, v.Title, null, null, ScenarioKey: s42, Variant: v.Key), "anna", default);

        (await again.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("variant-unknown", "a case with its title exists: no duplicate");
        plan.Cases.Count(c => c.EffectiveTitle == v.Title).Should().Be(1);
        var added = plan.Cases.Single(c => c.EffectiveTitle == v.Title);
        added.FromVariant.Should().Be(v.Key);

        // The tester renames the case: the proposal must still not come back (2026-11-24 walkthrough finding).
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == added.Source.ScenarioId ? part with { Edits = part.Edits with { Title = v.Title + " (verifisert)" } } : part), "anna", default);
        plan.Cases.Single(c => c.Source.ScenarioId == added.Source.ScenarioId).FromVariant.Should().Be(v.Key, "kept through a save");
        (await service.VariantsAsync(plan.Id, s42, default)).Should().NotContain(x => x.Key == v.Key);
    }

    [Fact]
    public async Task Rejected_and_deferred_design_proposals_are_saved_with_who_and_when_and_kept_by_later_saves()
    {
        await using var db = Context();
        var (service, plan, s42, s7) = await PlanAsync(db);
        var approvedEdits = plan.Cases.Single(c => c.Source.ScenarioId == s7).Edits;
        plan = await service.SaveAsync(plan.Id, Human(plan) with
        {
            DesignDecisions =
            [
                new TestDesignDecision { ScenarioKey = s42, ProposalKey = "boundary:abc", ProposalTitle = "grense", Decision = TestDesignDecisions.Rejected, By = "spoofed" },
                new TestDesignDecision { ScenarioKey = s42, ProposalKey = "repeat:def", ProposalTitle = "gjenta", Decision = TestDesignDecisions.Deferred },
                new TestDesignDecision { ScenarioKey = s42, ProposalKey = "x", Decision = "Approved" },
            ],
        }, "anna", default);

        plan.DesignDecisions.Should().HaveCount(2, "only Rejected and Deferred are decisions; accepting adds a case");
        plan.DesignDecisions!.Should().OnlyContain(d => d.By == "anna" && d.At != default, "the server records who and when");
        var at = plan.DesignDecisions![0].At;

        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s7 ? part with { Edits = part.Edits with { Title = "Edited later" } } : part), "inger", default);
        plan.DesignDecisions.Should().HaveCount(2, "a save that does not send decisions keeps them");
        plan.DesignDecisions![0].Should().Match<TestDesignDecision>(d => d.By == "anna" && d.At == at);

        plan = await service.SaveAsync(plan.Id, Human(plan) with { DesignDecisions = [.. plan.DesignDecisions!] }, "inger", default);
        plan.DesignDecisions![0].By.Should().Be("anna", "an unchanged decision keeps its author");
        await using var fresh = Context();
        (await Service(fresh).DraftAsync(plan.Id, default)).DesignDecisions.Should().HaveCount(2, "read back after a reload");
        approvedEdits.Should().NotBeNull();
    }

    // ── Translation ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A deterministic local translator: prefixes every word piece, keeps placeholders. Optionally drops placeholders (a bad service).</summary>
    private sealed class FakeTranslator(bool available = true, bool losePlaceholders = false, bool fail = false) : ITestCaseTranslator
    {
        public int Calls { get; private set; }
        public List<(string Source, string Target)> Pairs { get; } = [];
        public TranslationAvailability Availability => new(available, available ? "Fake" : null, available ? "Fake translator." : "Azure AI Translator has no key yet (System Settings → Translation). You can write the translated text by hand.");
        public Task<TranslatedTexts> TranslateAsync(IReadOnlyList<string?> texts, string source, string target, CancellationToken ct)
        {
            Calls++;
            Pairs.Add((source, target));
            if (fail) throw new TestCaseTranslationException("The fake service is down. Nothing was changed.");
            var prefix = target.ToUpperInvariant() + ": ";
            string TranslateMarkers(string text)
            {
                var markers = target.ToLowerInvariant() switch
                {
                    // Every English marker the negation/condition check reads (ScenarioPlanning.PreservesNegationAndConditions), as a real service would.
                    "nb" or "no" or "nn" => new[] { ("not", "ikke"), ("no", "ingen"), ("never", "aldri"), ("without", "uten"), ("unless", "med mindre"), ("when", "når"), ("if", "hvis") },
                    "sv" => new[] { ("not", "inte"), ("no", "ingen"), ("never", "aldrig"), ("without", "utan"), ("unless", "såvida inte"), ("when", "när"), ("if", "om") },
                    _ => Array.Empty<(string, string)>(),
                };
                foreach (var (from, to) in markers)
                    text = System.Text.RegularExpressions.Regex.Replace(text, $@"\b{from}\b", to, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                return text;
            }
            return Task.FromResult(new TranslatedTexts([.. texts.Select(t => t is null ? null : losePlaceholders
                ? prefix + System.Text.RegularExpressions.Regex.Replace(TranslateMarkers(t), "⟦\\d+⟧", "")
                : prefix + TranslateMarkers(t))], "Fake"));
        }
    }

    private static readonly TranslationSettings Settings = new();

    [Fact]
    public async Task Translating_a_scenario_translates_only_its_cases_keeps_identifiers_and_the_original_and_approves_nothing()
    {
        await using var db = Context();
        var (service, plan, s42, s7) = await PlanAsync(db);
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Report for FR-027 is accepted", null, null, ScenarioKey: s42), "anna", default);

        var result = await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(), Settings, "anna", default);

        result.Translated.Should().HaveCount(2);
        var after = result.Draft!;
        after.Cases.Where(c => c.ParentScenario == s42).Should().OnlyContain(c => c.Translation("nb") != null && c.Translation("nb")!.Origin == TranslationOrigin.Machine && c.Review == TestCaseReviewState.Generated);
        after.Cases.Single(c => c.Source.ScenarioId == s7).Translation("nb").Should().BeNull("another scenario is not translated");
        result.Target.Should().Be("nb", "English → Norwegian bokmål is the default");
        result.Provider.Should().Be("Fake");
        var manual = after.Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual);
        manual.Translation("nb")!.Title.Should().Be("NB: Report for FR-027 is accepted", "the id came back unchanged");
        manual.Translation("nb")!.Provider.Should().Be("Fake", "the service that translated is recorded");
        manual.EffectiveTitle.Should().Be("Report for FR-027 is accepted", "the original is never replaced");
        (await Service(Context()).RevisionsAsync(plan.Id, default)).First().Reason.Should().Be("Translated to Norwegian (bokmål)");
    }

    [Fact]
    public async Task A_persons_Norwegian_text_is_kept_unless_overwriting_is_asked_and_goes_stale_when_the_original_changes()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        var translation = await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(), Settings, "anna", default);
        translation.Draft.Should().NotBeNull(translation.Message);
        translation.Failed.Should().BeEmpty(translation.Message);
        plan = translation.Draft!;

        // A person edits the Norwegian title: Machine → MachineEdited, recorded with who.
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42 ? part with { Translations = new() { ["nb"] = c.Translation("nb")! with { Title = "Barn over opplæringspliktig alder" } } } : part), "inger", default);
        var edited = plan.Cases.Single(c => c.Source.ScenarioId == s42).Translation("nb")!;
        edited.Origin.Should().Be(TranslationOrigin.MachineEdited);
        edited.By.Should().Be("inger");

        var again = await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(), Settings, "anna", default);
        again.KeptPersonEdits.Should().ContainSingle();
        again.Draft.Should().BeNull("nothing changed");
        (await service.DraftAsync(plan.Id, default)).Cases.Single(c => c.Source.ScenarioId == s42).Translation("nb")!.Title.Should().Be("Barn over opplæringspliktig alder");

        var overwritten = await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42, Overwrite: true), new FakeTranslator(), Settings, "anna", default);
        overwritten.Draft!.Cases.Single(c => c.Source.ScenarioId == s42).Translation("nb")!.Origin.Should().Be(TranslationOrigin.Machine);
        plan = overwritten.Draft!;

        // The original changes after the translation: the Norwegian text is kept and flagged.
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42 ? part with { Edits = part.Edits with { Title = "Child past school age at lock" } } : part), "anna", default);
        var stale = plan.Cases.Single(c => c.Source.ScenarioId == s42);
        ScenarioPlanning.StateOf(stale).Should().Be(TranslationState.SourceChanged);
        stale.Translation("nb").Should().NotBeNull();
    }

    [Fact]
    public async Task A_scenario_can_also_be_translated_into_another_configured_language_kept_apart_from_Norwegian()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        var translator = new FakeTranslator();
        var settings = Settings with { TargetLanguages = ["nb", "sv"] };
        var norwegian = await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), translator, settings, "anna", default);
        norwegian.Draft.Should().NotBeNull(norwegian.Message);
        plan = norwegian.Draft!;
        var sv = await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42, Target: "sv"), translator, settings, "anna", default);

        translator.Pairs.Should().Equal(("en", "nb"), ("en", "sv"));
        var c = sv.Draft!.Cases.Single(x => x.Source.ScenarioId == s42);
        c.TranslatedLanguages.Should().BeEquivalentTo(["nb", "sv"]);
        c.Translation("sv")!.Title.Should().StartWith("SV: ");
        c.Translation("nb")!.Title.Should().StartWith("NB: ", "the Norwegian text is untouched");
        var same = () => service.TranslateScenarioAsync(sv.Draft!.Id, new TranslateScenarioRequest(sv.Draft.Revision, s42, Target: "en"), translator, settings, "anna", default);
        (await same.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("translation-language");
    }

    [Fact]
    public async Task Unavailable_failing_or_identifier_losing_translation_changes_nothing()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);

        var unavailable = () => service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(available: false), Settings, "anna", default);
        var refused = (await unavailable.Should().ThrowAsync<TestPlanException>()).Which;
        // V11 (2026-11-27): «not configured» is its own case with Norwegian wording; the original reason is kept as «Detalj».
        refused.Code.Should().Be("translation-not-configured");
        refused.Message.Should().StartWith("Ingen oversettelsestjeneste er satt opp.").And.Contain("Azure AI Translator has no key")
            .And.Contain("Systeminnstillinger → Translation").And.Contain("for hånd").And.NotContain("You can write the translated");

        // Every choice fails when called: one clear message, nothing changed (no list of per-case failures).
        var down = () => service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(fail: true), Settings, "anna", default);
        (await down.Should().ThrowAsync<TestPlanException>()).Which.Message.Should().Contain("The fake service is down").And.StartWith("Oversettelsen mislyktes.");

        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Report for FR-027", null, null, ScenarioKey: s42), "anna", default);
        var lossy = await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(losePlaceholders: true), Settings, "anna", default);
        lossy.Failed.Should().Contain(f => f.Contains("technical identifier"));
        (await service.DraftAsync(plan.Id, default)).Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual).Translation("nb").Should().BeNull();
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
        plan = (await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(), Settings, "anna", default)).Draft!;

        var nb = AdoTestPlanExporter.Preview(plan, new AdoExportRequest { ProjectKey = plan.ProjectKey!, DraftId = plan.Id, PlanRevision = plan.Revision, IncludeNotApproved = true, Language = ScenarioPlanning.Norwegian });
        var original = AdoTestPlanExporter.Preview(plan, new AdoExportRequest { ProjectKey = plan.ProjectKey!, DraftId = plan.Id, PlanRevision = plan.Revision, IncludeNotApproved = true });

        var nbCases = nb.Cases.Where(c => c.ScenarioKey == s42).ToList();
        nbCases.Should().HaveCount(2).And.OnlyContain(c => c.Language == "nb" && c.AzureDevOpsId == null);
        nb.Rows.Where(r => r.Title.StartsWith("NB: ", StringComparison.Ordinal)).Should().NotBeEmpty().And.OnlyContain(r => r.StepAction.StartsWith("NB: ", StringComparison.Ordinal) && r.StepExpected.StartsWith("NB: ", StringComparison.Ordinal),
            "a Norwegian case is Norwegian in every cell");
        nb.Rows.Single(r => r.StepAction == "NB: Lock the period uten a report").Description.Should().Contain("under the scenario \"Child past compulsory school age\"");
        original.Rows.Should().NotContain(r => r.Title.StartsWith("NB: ", StringComparison.Ordinal));
        original.Cases.Should().OnlyContain(c => c.Language == AdoExportCase.OriginalLanguage);
        nb.Cases.Where(c => c.ScenarioKey != s42).Should().OnlyContain(c => c.Language == AdoExportCase.OriginalLanguage && c.Issues.Any(i => i.Message.Contains("original language")), "an untranslated case says so");
        nb.Boundary.Should().Contain("Downloading is not importing");
        nb.Issues.Should().Contain(i => i.Level == AdoIssueLevel.Warning && i.Message.StartsWith("The file is not fully in Norwegian", StringComparison.Ordinal),
            "the other scenario's case falls back, so the file says it is not fully Norwegian");
        original.Issues.Should().NotContain(i => i.Message.StartsWith("The file is not fully in", StringComparison.Ordinal), "no language was chosen");
    }

    /// <summary>A translator that keeps an English negation untranslated: the validation refuses it, the case keeps no Norwegian text and the export
    /// says so at case and file level instead of silently mixing languages.</summary>
    private sealed class NegationLosingTranslator : ITestCaseTranslator
    {
        public TranslationAvailability Availability => new(true, "Lossy", "Lossy translator.");
        public Task<TranslatedTexts> TranslateAsync(IReadOnlyList<string?> texts, string source, string target, CancellationToken ct) =>
            Task.FromResult(new TranslatedTexts([.. texts.Select(t => t is null ? null : "NB: " + t)], "Lossy"));
    }

    [Fact]
    public async Task A_translation_refused_by_validation_is_never_exported_as_Norwegian_and_the_file_says_it_is_not_fully_Norwegian()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "No report submitted", null, null, ScenarioKey: s42), "anna", default);
        var manual = plan.Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual).Source.ScenarioId;
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == manual
            ? part with { Edits = part.Edits with { Steps = ["Lock the period without a report"], StepExpected = ["The child is not expected"] } } : part), "anna", default);

        var result = await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new NegationLosingTranslator(), Settings, "anna", default);

        result.Failed.Should().Contain(f => f.Contains("negation"), "the lost \"not\"/\"No\" is reported, never accepted");
        var after = await service.DraftAsync(plan.Id, default);
        after.Cases.Single(c => c.Source.ScenarioId == manual).Translation("nb").Should().BeNull("a refused translation is not stored");
        var nb = AdoTestPlanExporter.Preview(after, new AdoExportRequest { ProjectKey = after.ProjectKey!, DraftId = after.Id, PlanRevision = after.Revision, IncludeNotApproved = true, Language = ScenarioPlanning.Norwegian });
        var exported = nb.Cases.Single(c => c.CaseId == manual);
        exported.Language.Should().Be(AdoExportCase.OriginalLanguage);
        exported.AzureDevOpsId.Should().BeNull("no id is invented");
        exported.Issues.Should().ContainSingle(i => i.Level == AdoIssueLevel.Warning && i.Message.Contains("original language"));
        nb.Rows.Where(r => r.Title == "No report submitted").Should().OnlyContain(r => !r.StepAction.StartsWith("NB: ", StringComparison.Ordinal), "never a mixed-language case");
        nb.Issues.Should().Contain(i => i.Level == AdoIssueLevel.Warning && i.Message.StartsWith("The file is not fully in Norwegian", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_partial_or_stale_Norwegian_text_is_flagged_in_the_export_and_a_partial_one_is_never_used()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        plan = (await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42), new FakeTranslator(), Settings, "anna", default)).Draft!;

        // A step added to the original after translation: the Norwegian text no longer covers every step, so it is not used.
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42
            ? part with { Edits = part.Edits with { Steps = [.. c.EffectiveSteps, "Open the report"], StepExpected = [.. c.EffectiveStepExpected, "The report is shown"] } } : part), "anna", default);
        var partial = AdoTestPlanExporter.Preview(plan, new AdoExportRequest { ProjectKey = plan.ProjectKey!, DraftId = plan.Id, PlanRevision = plan.Revision, IncludeNotApproved = true, Language = ScenarioPlanning.Norwegian });
        var p = partial.Cases.Single(c => c.CaseId == s42);
        p.Language.Should().Be(AdoExportCase.OriginalLanguage);
        p.Issues.Should().Contain(i => i.Level == AdoIssueLevel.Warning && i.Message.Contains("does not cover every step"));

        // Only a wording change: the text is complete but made from an older original — used, and flagged as a warning.
        plan = (await service.TranslateScenarioAsync(plan.Id, new TranslateScenarioRequest(plan.Revision, s42, Overwrite: true), new FakeTranslator(), Settings, "anna", default)).Draft!;
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42 ? part with { Edits = part.Edits with { Title = "Child past school age at lock" } } : part), "anna", default);
        var stale = AdoTestPlanExporter.Preview(plan, new AdoExportRequest { ProjectKey = plan.ProjectKey!, DraftId = plan.Id, PlanRevision = plan.Revision, IncludeNotApproved = true, Language = ScenarioPlanning.Norwegian });
        var s = stale.Cases.Single(c => c.CaseId == s42);
        s.Language.Should().Be("nb");
        s.Issues.Should().Contain(i => i.Level == AdoIssueLevel.Warning && i.Message.Contains("older original"));
    }

    // ── User stories ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_prepared_case_keeps_its_user_story_and_a_move_to_another_story_is_flagged_and_recorded_never_silent()
    {
        await using var db = Context();
        var (service, plan, s42, s7) = await PlanWithStoriesAsync(db);
        var c = plan.Cases.Single(x => x.Source.ScenarioId == s42);
        c.Generated.Story!.StoryId.Should().Be("US6");
        c.Generated.NumberInStory.Should().Be(3);
        c.Generated.Story.IndependentTest.Should().Be("Test Expected population on its own.");
        c.RequirementLinks.Should().BeEmpty("nothing is inherited from the story");

        await DiscoverAsync("import:p1", "US7"); // a newer source: the same scenario text, now under User Story 7
        var flagged = await Service(Context()).DraftAsync(plan.Id, default);
        flagged.Cases.Single(x => x.Source.ScenarioId == s42).SourceChanged.Should().BeTrue("a move to another story asks for review");
        flagged.Cases.Single(x => x.Source.ScenarioId == s42).Generated.Story!.StoryId.Should().Be("US6", "nothing is rebound until the tester updates");

        var refreshed = await service.RefreshAsync(plan.Id, flagged.Revision, "anna", default);
        var moved = refreshed.Cases.Single(x => x.Source.ScenarioId == s42);
        moved.Generated.Story!.StoryId.Should().Be("US7");
        moved.EditHistory.Last().Fields.Single().Should().Be("User story: US6 → US7 (User Story 7 - Coverage breakdown (Priority: P2))");
        refreshed.Cases.Single(x => x.Source.ScenarioId == s7).EditHistory.Should().BeEmpty("an unchanged story records nothing");
    }

    [Fact]
    public async Task A_scenario_renumbered_in_the_specification_is_flagged_and_on_update_its_cases_follow_the_same_text()
    {
        await using var db = Context();
        var (service, plan, s42, s7) = await PlanWithStoriesAsync(db);
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "No report submitted", null, null, ScenarioKey: s42), "anna", default);
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42 ? part with { Edits = part.Edits with { ObservationMethod = "Read the frozen report" } } : part), "anna", default);

        // A scenario was inserted earlier in the file: the same text is now scenario 43 (another candidate id), nothing else changed.
        var newer = await DiscoverAsync("import:p1", "US6", id42: "scenario-43");
        var s43 = newer.Candidates.Single(c => c.CanonicalKey.EndsWith("|scenario-43", StringComparison.Ordinal)).CandidateId;
        s43.Should().NotBe(s42);
        var flagged = await Service(Context()).DraftAsync(plan.Id, default);
        flagged.Cases.Single(x => x.Source.ScenarioId == s42).SourceChanged.Should().BeTrue("a renumbering is shown, never applied silently");

        var refreshed = await service.RefreshAsync(plan.Id, flagged.Revision, "anna", default);
        var followed = refreshed.Cases.Single(x => x.Source.Kind == ScenarioSourceKind.ProjectDiscovery && x.EffectiveTitle.Contains("past"));
        followed.Source.ScenarioId.Should().Be(s43);
        followed.SourceChanged.Should().BeFalse();
        followed.Edits.ObservationMethod.Should().Be("Read the frozen report", "the person's edits stay with the case");
        followed.EditHistory.Last().Fields.Single().Should().Be($"Moved in the specification: the same scenario text is now {Spec}#scenario-43 (was {Spec}#scenario-42)");
        refreshed.Cases.Single(x => x.Source.Kind == ScenarioSourceKind.Manual).ScenarioKey.Should().Be(s43, "a case written under the scenario moves with it");
        refreshed.Cases.Should().NotContain(x => x.Source.ScenarioId == s42);
        refreshed.Cases.Single(x => x.Source.ScenarioId == s7).EditHistory.Should().BeEmpty();
    }

    [Fact]
    public async Task A_requirement_confirmed_for_a_story_is_saved_with_who_and_never_counted_as_coverage()
    {
        await using var db = Context();
        var (service, plan, _, _) = await PlanWithStoriesAsync(db);
        var storyKey = CoverageUserStory.KeyOf(Spec, "US6");
        var request = Human(plan) with
        {
            StoryRequirementLinks = [new StoryRequirementLink(storyKey, new ManualRequirementLink { RequirementId = "FR-027", SpecificationPath = Spec, SuggestionLevel = RequirementSuggestionLevel.Structural, SuggestionReason = "tasks.md: T099" })],
            StoryRejectedSuggestions = [new StoryRequirementRejection(storyKey, new RejectedRequirementSuggestion { RequirementId = "FR-001", SpecificationPath = Spec, RejectedBy = "spoofed" })],
        };
        plan = await service.SaveAsync(plan.Id, request, "inger", default);

        var reloaded = await Service(Context()).DraftAsync(plan.Id, default);
        reloaded.StoryRequirementLinks!.Single().Should().Match<StoryRequirementLink>(l => l.StoryKey == storyKey && l.Link.RequirementId == "FR-027" && l.Link.LinkedBy == "inger"
            && l.Link.SuggestionLevel == RequirementSuggestionLevel.Structural);
        reloaded.StoryRejectedSuggestions!.Single().Rejection.RejectedBy.Should().Be("inger");
        reloaded.Cases.Should().OnlyContain(c => c.RequirementLinks.Count == 0, "a story link is never copied to its cases");
        var trace = await Service(Context()).PreparedCaseTraceAsync(plan.ProjectKey!, [Spec], ["FR-027", "FR-001"], default);
        trace.Requirements.Should().NotContain(r => r.RequirementId == "FR-027", "an association with a story is not test design of any case");

        var unknown = () => service.SaveAsync(plan.Id, Human(reloaded) with { StoryRequirementLinks = [new StoryRequirementLink(storyKey, new ManualRequirementLink { RequirementId = "FR-999", SpecificationPath = Spec })] }, "inger", default);
        (await unknown.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("requirement-unknown");
    }

    [Fact]
    public async Task Translate_all_translates_every_scenario_its_story_and_the_linked_requirement_wording_in_one_revision()
    {
        await using var db = Context();
        var (service, plan, s42, s7) = await PlanWithStoriesAsync(db);
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42
            ? part with { RequirementLinks = [new ManualRequirementLink { RequirementId = "FR-027", SpecificationPath = Spec }] } : part), "inger", default);
        var revisionsBefore = (await service.RevisionsAsync(plan.Id, default)).Count;

        var result = await service.TranslatePlanAsync(plan.Id, new TranslatePlanRequest(plan.Revision), new FakeTranslator(), Settings, "anna", default);

        result.Translated.Should().HaveCount(2);
        var after = result.Draft!;
        after.Cases.Should().OnlyContain(c => c.Translation("nb") != null);
        var c42 = after.Cases.Single(c => c.Source.ScenarioId == s42).Translation("nb")!;
        c42.Given.Should().Be("NB: a child past compulsory school age", "the scenario's Given/When/Then are translated");
        c42.When.Should().Be("NB: the period locks");
        after.StoryTranslations!.Select(t => t.StoryKey).Should().BeEquivalentTo([CoverageUserStory.KeyOf(Spec, "US6"), CoverageUserStory.KeyOf(Spec, "US1")]);
        after.StoryTranslations!.Single(t => t.StoryKey.EndsWith("#US6", StringComparison.Ordinal)).IndependentTest.Should().Be("NB: Test Expected population on its own.");
        after.RequirementTranslations!.Should().Contain(t => t.RequirementId == "FR-027" && t.Text.StartsWith("NB: ", StringComparison.Ordinal), "the linked requirement's wording is translated for display");
        (await service.RevisionsAsync(plan.Id, default)).Should().HaveCount(revisionsBefore + 1, "Translate all is one saved revision");
        result.Message.Should().Contain("in 2 scenarios").And.Contain("Saved");

        // A person's story text is kept the next time.
        var storyKey = CoverageUserStory.KeyOf(Spec, "US6");
        var edited = await service.SaveAsync(after.Id, Human(after) with
        {
            StoryTranslations = [.. after.StoryTranslations!.Select(t => t.StoryKey == storyKey ? t with { Title = "Brukerhistorie 6 – Forventet populasjon" } : t)],
        }, "inger", default);
        edited.StoryTranslations!.Single(t => t.StoryKey == storyKey).Origin.Should().Be(TranslationOrigin.MachineEdited);
        var again = await service.TranslatePlanAsync(edited.Id, new TranslatePlanRequest(edited.Revision), new FakeTranslator(), Settings, "anna", default);
        (again.Draft ?? edited).StoryTranslations!.Single(t => t.StoryKey == storyKey).Title.Should().Be("Brukerhistorie 6 – Forventet populasjon");
    }

    // ── Automatic translation: only missing (2026-11-25) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Only_missing_translates_on_first_opening_and_then_saves_nothing_and_calls_no_service()
    {
        await using var db = Context();
        var (service, plan, _, _) = await PlanWithStoriesAsync(db);
        var revisions = (await service.RevisionsAsync(plan.Id, default)).Count;

        var first = await service.TranslatePlanAsync(plan.Id, new TranslatePlanRequest(plan.Revision, OnlyMissing: true), new FakeTranslator(), Settings, "anna", default);
        first.Draft!.Cases.Should().OnlyContain(c => c.Translation("nb") != null && c.Translation("nb")!.Origin == TranslationOrigin.Machine);
        first.Draft.StoryTranslations.Should().NotBeNullOrEmpty();
        (await service.RevisionsAsync(plan.Id, default)).Should().HaveCount(revisions + 1, "one revision for the missing text");

        var translator = new FakeTranslator();
        var again = await service.TranslatePlanAsync(plan.Id, new TranslatePlanRequest(first.Draft.Revision, OnlyMissing: true), translator, Settings, "anna", default);
        again.Draft.Should().BeNull("nothing is missing, so nothing is saved");
        translator.Calls.Should().Be(0, "reopening the page sends nothing to the translation service");
        (await service.RevisionsAsync(plan.Id, default)).Should().HaveCount(revisions + 1, "no unexpected revision");
        (await service.DraftAsync(plan.Id, default)).Revision.Should().Be(first.Draft.Revision);

        // Nothing missing never needs a service: an unavailable translator is not an error then.
        var off = await service.TranslatePlanAsync(plan.Id, new TranslatePlanRequest(first.Draft.Revision, OnlyMissing: true), new FakeTranslator(available: false), Settings, "anna", default);
        off.Draft.Should().BeNull();
    }

    [Fact]
    public async Task Only_missing_never_replaces_an_existing_translation_edited_outdated_or_approved_and_translates_only_new_cases()
    {
        await using var db = Context();
        var (service, plan, s42, s7) = await PlanWithStoriesAsync(db);
        plan = (await service.TranslatePlanAsync(plan.Id, new TranslatePlanRequest(plan.Revision), new FakeTranslator(), Settings, "anna", default)).Draft!;
        // s42: a person edits the Norwegian title; s7: its ORIGINAL changes afterwards (the translation is now from an older text) and it is approved.
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42
            ? part with { Translations = new() { ["nb"] = c.Translation("nb")! with { Title = "Et barn over skolepliktig alder" } } }
            : part with { Edits = part.Edits with { Title = "A child past compulsory school age (changed)" } }), "inger", default);
        plan = await service.ReviewAsync(plan.Id, new ReviewTestCaseRequest(plan.Revision, s7, TestCaseReviewState.Accepted, "ok"), "lead", default);
        var edited = plan.Cases.Single(c => c.Source.ScenarioId == s42).Translation("nb")!;
        var outdated = plan.Cases.Single(c => c.Source.ScenarioId == s7).Translation("nb")!;
        edited.Origin.Should().Be(TranslationOrigin.MachineEdited);
        ScenarioPlanning.StateOf(plan.Cases.Single(c => c.Source.ScenarioId == s7)).Should().Be(TranslationState.SourceChanged);
        // A new case without any translation.
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "No report submitted", null, null, ScenarioKey: s42), "anna", default);
        var added = plan.Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual).Source.ScenarioId;

        var result = await service.TranslatePlanAsync(plan.Id, new TranslatePlanRequest(plan.Revision, OnlyMissing: true), new FakeTranslator(), Settings, "anna", default);

        result.Translated.Should().Equal(["No report submitted"], "only the case without a translation");
        var after = result.Draft!;
        after.Cases.Single(c => c.Source.ScenarioId == added).Translation("nb")!.Title.Should().Be("NB: ingen report submitted");
        after.Cases.Single(c => c.Source.ScenarioId == s42).Translation("nb").Should().BeEquivalentTo(edited, "a person's text is never replaced");
        after.Cases.Single(c => c.Source.ScenarioId == s7).Translation("nb").Should().BeEquivalentTo(outdated, "a translation of an older original is kept for review, not replaced");
        ScenarioPlanning.StateOf(after.Cases.Single(c => c.Source.ScenarioId == s7)).Should().Be(TranslationState.SourceChanged, "it stays marked for review");
        after.Cases.Single(c => c.Source.ScenarioId == s7).Review.Should().Be(TestCaseReviewState.Accepted, "the approval and its history are untouched");
        after.Cases.Single(c => c.Source.ScenarioId == s7).ReviewHistory.Should().BeEquivalentTo(plan.Cases.Single(c => c.Source.ScenarioId == s7).ReviewHistory);
        after.Cases.Single(c => c.Source.ScenarioId == s7).Edits.Should().BeEquivalentTo(plan.Cases.Single(c => c.Source.ScenarioId == s7).Edits, "the tester's own edits are untouched");
    }

    [Fact]
    public async Task Only_missing_with_a_save_in_between_is_refused_as_stale_and_the_save_wins()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        var saved = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42 ? part with { Edits = part.Edits with { Preconditions = "Saved by the tester" } } : part), "inger", default);

        var late = () => service.TranslatePlanAsync(plan.Id, new TranslatePlanRequest(plan.Revision, OnlyMissing: true), new FakeTranslator(), Settings, "anna", default);
        (await late.Should().ThrowAsync<TestPlanException>()).Which.Status.Should().Be(409);
        (await service.DraftAsync(plan.Id, default)).Cases.Single(c => c.Source.ScenarioId == s42).Edits.Preconditions.Should().Be("Saved by the tester");
        (await service.DraftAsync(plan.Id, default)).Revision.Should().Be(saved.Revision);
    }

    [Theory]
    [InlineData(null, 503, "translation-not-configured", "Ingen oversettelsestjeneste er satt opp.")]
    [InlineData(TranslationFailure.Unavailable, 503, "translation-unavailable", "Oversettelsestjenesten er midlertidig utilgjengelig. Prøv igjen om litt.")]
    [InlineData(TranslationFailure.Timeout, 504, "translation-timeout", "Oversettelsen tok for lang tid og ble stoppet.")]
    [InlineData(TranslationFailure.Failed, 502, "translation-failed", "Oversettelsen mislyktes.")]
    public void V11_each_translation_problem_says_what_happened_and_keeps_the_original_cause(TranslationFailure? kind, int status, string code, string start)
    {
        // Before: one «No translation service is available right now» (503) for all four, also when a configured service was only temporarily down.
        var problem = TestPlanSuggestionService.TranslationProblem(kind, "Azure AI Translator answered 503. Nothing was changed.", "norsk (bokmål)");
        (problem.Status, problem.Code).Should().Be((status, code));
        problem.Message.Should().StartWith(start).And.Contain("Ingenting ble endret").And.Contain("Detalj: Azure AI Translator answered 503.").And.NotContain("No translation service");
    }

    [Fact]
    public void V11_HTTP_and_transport_errors_are_classified_without_guessing()
    {
        TestCaseTranslationException.KindOf(503).Should().Be(TranslationFailure.Unavailable);
        TestCaseTranslationException.KindOf(429).Should().Be(TranslationFailure.Unavailable);
        TestCaseTranslationException.KindOf(400).Should().Be(TranslationFailure.Failed);
        TestCaseTranslationException.KindOf(new TaskCanceledException(), CancellationToken.None).Should().Be(TranslationFailure.Timeout);
        TestCaseTranslationException.KindOf(new TaskCanceledException(), new CancellationToken(canceled: true)).Should().Be(TranslationFailure.Failed, "the caller's own cancellation is not a timeout");
        TestCaseTranslationException.KindOf(new HttpRequestException("refused"), CancellationToken.None).Should().Be(TranslationFailure.Unavailable);
        new TestCaseTranslationException("combined", new TestCaseTranslationException("inner", null, TranslationFailure.Timeout)).Kind.Should().Be(TranslationFailure.Timeout, "a wrapper keeps the cause's kind");
    }

    [Fact]
    public async Task Only_missing_without_a_translation_service_says_so_and_changes_nothing()
    {
        await using var db = Context();
        var (service, plan, _, _) = await PlanAsync(db);
        var none = () => service.TranslatePlanAsync(plan.Id, new TranslatePlanRequest(plan.Revision, OnlyMissing: true), new FakeTranslator(available: false), Settings, "anna", default);
        (await none.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("translation-not-configured");
        (await service.DraftAsync(plan.Id, default)).Revision.Should().Be(plan.Revision);
    }

    // ── Order and exclusion ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_saved_order_is_kept_and_the_export_follows_it_and_excluding_a_scenario_keeps_its_cases()
    {
        await using var db = Context();
        var (service, plan, s42, s7) = await PlanAsync(db);
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "No report submitted", null, null, ScenarioKey: s42), "anna", default);
        var manual = plan.Cases.Single(c => c.Source.Kind == ScenarioSourceKind.Manual).Source.ScenarioId;
        // The tester puts the valid-report scenario first and the hand-written case before the scenario's own case.
        var order = new[] { s7, manual, s42 };
        plan = await service.SaveAsync(plan.Id, Human(plan) with { ScenarioIds = [.. order] }, "anna", default);
        (await Service(Context()).DraftAsync(plan.Id, default)).Cases.Select(c => c.Source.ScenarioId).Should().Equal(order);
        AdoTestPlanExporter.Preview(plan, new AdoExportRequest { ProjectKey = plan.ProjectKey!, DraftId = plan.Id, PlanRevision = plan.Revision, IncludeNotApproved = true })
            .Cases.Select(c => c.CaseId).Should().Equal(order, "the export follows the plan's order");

        var noReason = () => service.ExcludeScenarioAsync(plan.Id, new ExcludeScenarioRequest(plan.Revision, s42, " "), "lead", default);
        (await noReason.Should().ThrowAsync<TestPlanException>()).Which.Code.Should().Be("rationale-required");
        plan = await service.ExcludeScenarioAsync(plan.Id, new ExcludeScenarioRequest(plan.Revision, s42, "Covered by the integration suite"), "lead", default);

        var excluded = plan.Cases.Where(c => c.ParentScenario == s42).ToList();
        excluded.Should().HaveCount(2).And.OnlyContain(c => c.Review == TestCaseReviewState.Excluded && c.ReviewHistory.Last().Rationale == "Scenario excluded: Covered by the integration suite");
        plan.Cases.Single(c => c.Source.ScenarioId == s7).Review.Should().Be(TestCaseReviewState.Generated, "another scenario is untouched");
        plan.Cases.Should().HaveCount(3, "nothing is deleted");
        (await service.RevisionsAsync(plan.Id, default)).First().Reason.Should().Be("Scenario excluded (2 test cases)");
        AdoTestPlanExporter.Preview(plan, new AdoExportRequest { ProjectKey = plan.ProjectKey!, DraftId = plan.Id, PlanRevision = plan.Revision, IncludeNotApproved = true })
            .Rows.Should().NotContain(r => excluded.Any(c => c.EffectiveTitle == r.Title), "excluded cases are never exported");
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

    // ── Testtype on a saved case (2026-11-25): Manuell/Teknisk chosen per case; canonical values; older plans honest; separate from scenario decisions ──

    [Fact]
    public async Task B21_B23_B24_a_direct_case_is_created_with_its_type_stored_by_name_and_read_back_after_a_reload()
    {
        await using var db = Context();
        var (service, plan, _, _) = await PlanAsync(db);
        var before = plan.Revision;

        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Kontroller kryptert lagring", null, null, TestKind: ScenarioTestKind.Technical), "anna", default);

        plan.Revision.Should().Be(before + 1, "adding a case is a revision");
        var created = plan.Cases.Single(c => c.EffectiveTitle == "Kontroller kryptert lagring");
        created.ParentScenario.Should().BeNull("written directly, without a scenario");
        created.Edits.TestKind.Should().Be(ScenarioTestKind.Technical);
        created.EditHistory.Single().Fields.Should().Contain("Test type: Technical");
        (await Service(Context()).DraftAsync(plan.Id, default)).Cases.Single(c => c.Source.ScenarioId == created.Source.ScenarioId).Edits.TestKind.Should().Be(ScenarioTestKind.Technical);

        await using var raw = Context();
        var json = raw.TestPlanDrafts.Single(d => d.Id == plan.Id).ContentJson;
        json.Should().Contain("\"testKind\":\"Technical\"", "the canonical value, never a Norwegian label");
        json.Should().NotContain("Teknisk");
    }

    [Fact]
    public async Task B22_B27_B32_older_cases_stay_unset_and_keep_their_stored_form()
    {
        await using var db = Context();
        var (service, plan, s42, s7) = await PlanAsync(db);
        await using var raw = Context();
        var stored = raw.TestPlanDrafts.Single(d => d.Id == plan.Id).ContentJson;
        stored.Should().NotContain("testKind", "a case without a type writes nothing: older plans keep their stored form and fingerprint");
        plan.Cases.Should().OnlyContain(c => c.Edits.TestKind == null, "never given a type that was not chosen");

        plan = await service.SaveAsync(plan.Id, Human(plan), "anna", default); // a save that changes nothing about types
        plan.Cases.Should().OnlyContain(c => c.Edits.TestKind == null);
    }

    [Fact]
    public async Task B25_B27_changing_only_the_type_keeps_every_other_field_and_is_recorded_by_name()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42
            ? part with { Edits = part.Edits with { Steps = ["Lock the period"], StepExpected = ["The child is not expected"], Priority = "P1" } } : part), "anna", default);
        var edited = plan.Cases.Single(c => c.Source.ScenarioId == s42);

        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42 ? part with { Edits = part.Edits with { TestKind = ScenarioTestKind.Manual } } : part), "anna", default);

        var after = (await Service(Context()).DraftAsync(plan.Id, default)).Cases.Single(c => c.Source.ScenarioId == s42);
        after.Edits.Should().BeEquivalentTo(edited.Edits with { TestKind = ScenarioTestKind.Manual }, "only the type changed");
        after.EffectiveSteps.Should().Equal("Lock the period");
        after.EditHistory.Last().Fields.Should().Equal("Manual or technical");
    }

    [Fact]
    public async Task B24_B26_the_type_survives_revisions_and_a_refresh_from_the_source()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42 ? part with { Edits = part.Edits with { TestKind = ScenarioTestKind.Technical } } : part), "anna", default);
        var saved = plan.Revision;

        plan = await service.RefreshAsync(plan.Id, plan.Revision, "anna", default);

        plan.Cases.Single(c => c.Source.ScenarioId == s42).Edits.TestKind.Should().Be(ScenarioTestKind.Technical, "a refresh from the source never changes a person's choice");
        (await service.RevisionAsync(plan.Id, saved, default)).Cases.Single(c => c.Source.ScenarioId == s42).Edits.TestKind.Should().Be(ScenarioTestKind.Technical);
    }

    [Fact]
    public async Task B28_a_duplicate_keeps_its_source_cases_type_unless_another_is_chosen_and_both_live_under_one_scenario()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Manual check", null, null, ScenarioKey: s42, TestKind: ScenarioTestKind.Manual), "anna", default);
        var manual = plan.Cases.Single(c => c.EffectiveTitle == "Manual check").Source.ScenarioId;

        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Manual check (copy)", null, null, CopyFrom: manual), "anna", default);
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Technical check", null, null, CopyFrom: manual, TestKind: ScenarioTestKind.Technical), "anna", default);

        plan.Cases.Single(c => c.EffectiveTitle == "Manual check (copy)").Edits.TestKind.Should().Be(ScenarioTestKind.Manual);
        plan.Cases.Single(c => c.EffectiveTitle == "Technical check").Edits.TestKind.Should().Be(ScenarioTestKind.Technical);
        plan.Cases.Where(c => c.ParentScenario == s42).Select(c => c.Edits.TestKind).Should().Contain(ScenarioTestKind.Manual).And.Contain(ScenarioTestKind.Technical,
            "one scenario can have manual and technical cases; the scenario itself is not duplicated");
        plan.Cases.Count(c => c.Source.ScenarioId == s42).Should().Be(1);
    }

    [Fact]
    public async Task B29_B30_creating_typed_cases_never_writes_scenario_decisions_and_a_stale_revision_is_refused()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        var stale = plan.Revision;
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "First", null, null, ScenarioKey: s42, TestKind: ScenarioTestKind.Technical), "anna", default);

        var again = () => service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(stale, "Second", null, null, TestKind: ScenarioTestKind.Manual), "bo", default);
        (await again.Should().ThrowAsync<TestPlanException>()).Which.Status.Should().Be(409);

        await using var check = Context();
        check.StoryGroupings.Count().Should().Be(0, "a case's type is never written into the scenario's Manuell/Teknisk decision");
    }

    [Fact]
    public async Task B18_the_export_carries_the_type_as_metadata_and_never_inside_the_steps()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        plan = await service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Verify retry", null, null, ScenarioKey: s42, TestKind: ScenarioTestKind.Technical), "anna", default);
        var snapshot = await service.SaveSnapshotAsync(plan.Id, new SaveTestPlanSnapshotRequest(plan.Revision, "Sprint 1", null), "lead", default);

        var export = await service.ExportAsync(snapshot.Snapshot.Id, default);

        export.Json.Should().MatchRegex(@"""testKind"":\s*""Technical""");
        export.Text.Should().Contain("Testtype: Teknisk");
        var withoutType = export.Text.Split('\n').Where(l => l.StartsWith("### ", StringComparison.Ordinal)).Count() - export.Text.Split('\n').Count(l => l.StartsWith("Testtype:", StringComparison.Ordinal));
        withoutType.Should().BeGreaterThan(0, "a case without a type gets no Testtype line (nothing is assumed)");
    }

    [Fact]
    public async Task B33_Begge_is_refused_for_a_saved_case_and_nothing_is_written()
    {
        await using var db = Context();
        var (service, plan, s42, _) = await PlanAsync(db);
        var both = () => service.AddManualCaseAsync(plan.Id, new AddManualTestCaseRequest(plan.Revision, "Both", null, null, ScenarioKey: s42, TestKind: ScenarioTestKind.Both), "anna", default);
        (await both.Should().ThrowAsync<TestPlanException>()).Which.Should().Match<TestPlanException>(e => e.Status == 400 && e.Code == "invalid-test-kind");
        (await Service(Context()).DraftAsync(plan.Id, default)).Revision.Should().Be(plan.Revision, "a refused add writes nothing");

        plan = await service.SaveAsync(plan.Id, Human(plan, (c, part) => c.Source.ScenarioId == s42 ? part with { Edits = part.Edits with { TestKind = ScenarioTestKind.Both } } : part), "anna", default);
        plan.Cases.Single(c => c.Source.ScenarioId == s42).Edits.TestKind.Should().BeNull("a save never stores «Begge» on a case");
    }
}
