using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.TestCoverage;
using BirkNext.TestEvidence;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;
using E = BirkNext.Api.Services.TestPlanning.TestPlanSuggestionEngine;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// Project-scoped test scenario discovery: each capability normalises only what its analyzer states (expected behaviour is never invented),
/// ids are stable per project and independent of the snapshot, deduplication uses the canonical identity only (a shared requirement is a
/// "possible duplicate", never a merge), existing tests attach only by explicit requirement reference, readiness never reads "no test data
/// described" as "not required", and a failing analyzer is reported without hiding the others.
/// </summary>
public sealed class TestScenarioDiscoveryTests
{
    private const string Project = "library-booking";

    private static CoverageWorkspaceEvidence Spec(params QaTestCandidate[] scenarios) => new()
    {
        ProjectName = "Library", SpecificationAvailable = true,
        SpecificationArtifacts = [new("a1", "specs/001-booking/spec.md", null, "fp")],
        Requirements = [new("FR-001", "System MUST let a member reserve an available book.", null), new("FR-002", "System MUST email a confirmation.", null)],
        AcceptanceScenarios = [.. scenarios],
    };

    private static QaTestCandidate Scenario(string id, string? then, params string[] requirements) => new()
    {
        Id = id, Title = "Reserve a book", Given = "a signed-in member and an available book", When = "the member reserves the book", Then = then, RequirementIds = [.. requirements],
    };

    [Fact]
    public void Specification_scenarios_become_business_candidates_with_the_stated_outcome_and_unnamed_requirements_get_their_own()
    {
        var output = D.FromSpecification(Project, Spec(Scenario("US1-1", "the book is reserved for the member", "FR-001")));
        output.Capability.State.Should().Be(DiscoveryCapabilityState.Available);
        var scenario = output.Candidates.Single(c => c.CanonicalKey.StartsWith("scenario:"));
        scenario.Type.Should().Be(ScenarioCandidateType.BusinessFunctional);
        scenario.Confidence.Should().Be(CandidateConfidence.ExplicitSpecification);
        scenario.ExpectedBehaviour.Should().Be("the book is reserved for the member");
        scenario.Preconditions.Should().Equal("a signed-in member and an available book");
        scenario.Steps.Single().Should().Be(new DiscoveredStep("the member reserves the book", "the book is reserved for the member"));
        output.Candidates.Should().ContainSingle(c => c.CanonicalKey.EndsWith("|FR-002"), "FR-002 is named by no scenario").And
            .NotContain(c => c.CanonicalKey.StartsWith("requirement:") && c.CanonicalKey.EndsWith("|FR-001"), "FR-001 is covered by an acceptance scenario");
        output.Candidates.Should().OnlyContain(c => c.ProjectKey == Project && c.CandidateId.StartsWith("tsc-"));
    }

    // Found in browser acceptance: the specification parser keeps a requirement's markdown line ("- **FR-001**: …") and titles a scenario with its
    // Given clause, so candidates read "FR-001: - **FR-001**: The system MUST …" and "a room is free …". Shown text is plain; the meaning is unchanged.
    [Fact]
    public void Specification_text_is_shown_plain_and_a_scenario_titled_by_its_starting_situation_also_names_its_action()
    {
        var evidence = Spec(new QaTestCandidate { Id = "US1-1", Title = "a room is free from 10:00 to 11:00", Given = "a room is free from 10:00 to 11:00",
            When = "the employee books it for that slot", Then = "the booking is **confirmed**" }) with
        { Requirements = [new("FR-001", "- **FR-001**: The system MUST let an employee book a free room.", null), new("FR-002", "**FR-002** The system MUST refuse an overlap.", null)] };
        var candidates = D.FromSpecification(Project, evidence).Candidates;
        var scenario = candidates.Single(c => c.CanonicalKey.StartsWith("scenario:"));
        scenario.Title.Should().Be("A room is free from 10:00 to 11:00 — the employee books it for that slot");
        scenario.ExpectedBehaviour.Should().Be("the booking is confirmed");
        var r1 = candidates.Single(c => c.CanonicalKey.EndsWith("|FR-001"));
        r1.Title.Should().Be("FR-001: The system MUST let an employee book a free room.");
        r1.ExpectedBehaviour.Should().Be("The system MUST let an employee book a free room.");
        candidates.Single(c => c.CanonicalKey.EndsWith("|FR-002")).Purpose.Should().Be("The system MUST refuse an overlap.");
        D.FromSpecification(Project, Spec(Scenario("US1-1", "x"))).Candidates.Single(c => c.CanonicalKey.StartsWith("scenario:")).Title
            .Should().Be("Reserve a book", "a title that says more than the starting situation is kept");
        D.RequirementStatement("FR-003", "  ").Should().BeNull();
    }

    [Fact]
    public void A_scenario_without_an_outcome_needs_clarification_and_nothing_is_invented()
    {
        var c = D.FromSpecification(Project, Spec(Scenario("US1-2", null, "FR-001"))).Candidates.Single(x => x.CanonicalKey.StartsWith("scenario:"));
        c.ExpectedBehaviour.Should().BeNull();
        c.NeedsClarification.Should().BeTrue();
        c.Confidence.Should().Be(CandidateConfidence.Insufficient);
        c.Steps.Single().Expected.Should().BeNull();
        c.OpenQuestions.Should().Contain(q => q.Contains("clarify the expected result"));
        var content = D.Content(c);
        content.ExpectedBasis.Should().Be(ExpectedResultBasis.Missing);
        // The procedure prepares the starting situation and then carries out the action; the action's result stays open (never invented).
        content.Steps.Last().Should().StartWith(D.ActionStepPrefix);
        content.StepExpected.Last().Should().BeNull();
        content.StepExpected.Count(e => e is null).Should().Be(1);
    }

    [Fact]
    public void No_workspace_specification_is_an_honest_no_evidence_state()
    {
        var output = D.FromSpecification(Project, null);
        output.Capability.State.Should().Be(DiscoveryCapabilityState.NoEvidence);
        output.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void Contract_operations_use_only_documented_responses_and_error_scenarios()
    {
        var contract = new SourceContract
        {
            Id = "openapi:api/openapi.yaml", Type = SourceContractType.OpenApi, Name = "Booking API", File = "api/openapi.yaml", Line = 1,
            Operations =
            [
                new ContractOperation("POST /bookings", "POST", "/bookings", []) { Detailed = true, Summary = "Create a booking", RequestSchema = "NewBooking", RequestBodyRequired = true,
                    ResponseCodes = ["201", "400", "401"], ResponseSchemas = new() { ["201"] = "Booking" }, Security = ["bearer"] },
                new ContractOperation("GET /books/{id}", "GET", "/books/{id}", ["id"]) { Detailed = true, ParameterDetails = [new("id", "path", true, "string")], ResponseCodes = ["404"] },
            ],
        };
        var list = D.FromContracts(Project, [contract]);
        var create = list.Single(c => c.CanonicalKey == "contract-op:api/openapi.yaml#POST /bookings");
        create.Type.Should().Be(ScenarioCandidateType.ApiContract);
        create.ExpectedBehaviour.Should().Be("Responds 201 with Booking, as the contract documents.");
        create.Steps.Should().Contain(s => s.Expected == "Responds 400, as the contract documents.");
        create.Template.Single(t => t.Key == "schema").Value.Should().Be("Response body matches Booking");
        var auth = list.Single(c => c.Type == ScenarioCandidateType.SecurityAuthorization);
        auth.ExpectedBehaviour.Should().Be("Responds 401, as the contract documents.");
        var get = list.Single(c => c.CanonicalKey.EndsWith("#GET /books/{id}"));
        get.ExpectedBehaviour.Should().BeNull("no 2xx response is documented, so nothing is assumed");
        get.Steps.Should().Contain(s => s.Expected == "Responds 404, as the contract documents.");
        D.Content(create).ExpectedBasis.Should().Be(ExpectedResultBasis.Contract);
    }

    [Fact]
    public void A_page_that_only_opens_is_UI_smoke_never_business_end_to_end()
    {
        var suggestions = new[]
        {
            new TestSuggestion { SuggestionId = "sug-1", Kind = SuggestionKind.PageOpens, Category = SuggestionCategory.BusinessE2E, Route = "/books", Feature = "Books", Title = "Books opens", RuleVersion = "v" },
            new TestSuggestion { SuggestionId = "sug-2", Kind = SuggestionKind.RequiredFields, Category = SuggestionCategory.UiFunctional, Route = "/reserve", Feature = "Reserve", Title = "Reserve requires fields",
                Steps = [new SuggestedStep { Description = "Submit empty", Expected = "Title is required" }], TestDataNeeds = ["A member"], RuleVersion = "v" },
        };
        var list = D.FromUi(Project, suggestions);
        list.Single(c => c.Title == "Books opens").Type.Should().Be(ScenarioCandidateType.UiSmoke);
        var form = list.Single(c => c.Title == "Reserve requires fields");
        form.Type.Should().Be(ScenarioCandidateType.UiFunctional);
        form.Confidence.Should().Be(CandidateConfidence.SourceDerived);
        form.ExpectedBehaviour.Should().BeNull("the analyzer stated no expected result");
        D.Content(form).TestDataNeed.Should().Be(TestDataNeed.Required);
    }

    [Fact]
    public void Ids_are_stable_per_project_and_independent_of_the_snapshot()
    {
        var a = D.FromSpecification(Project, Spec(Scenario("US1-1", "reserved", "FR-001"))).Candidates;
        var b = D.FromSpecification(Project, Spec(Scenario("US1-1", "reserved", "FR-001"))).Candidates;
        var other = D.FromSpecification("other-project", Spec(Scenario("US1-1", "reserved", "FR-001"))).Candidates;
        a.Select(c => c.CandidateId).Should().Equal(b.Select(c => c.CandidateId));
        a.Select(c => c.CandidateId).Should().NotIntersectWith(other.Select(c => c.CandidateId));
        var first = D.Finalize(Project, Guid.NewGuid().ToString(), a, []);
        var second = D.Finalize(Project, Guid.NewGuid().ToString(), b, []);
        first.Select(c => c.CandidateId).Should().Equal(second.Select(c => c.CandidateId), "a new import of the same scenario keeps its id");
    }

    [Fact]
    public void Deduplication_uses_the_canonical_identity_only_and_shared_requirements_stay_separate_as_possible_duplicates()
    {
        var spec = D.FromSpecification(Project, Spec(Scenario("US1-1", "reserved", "FR-001"))).Candidates;
        var twice = spec.Concat(spec.Select(c => c with { Evidence = [new("specification", "other#ref")] }));
        var ui = D.FromUi(Project, [new TestSuggestion { SuggestionId = "sug-9", Kind = SuggestionKind.ValidSubmission, Category = SuggestionCategory.UiFunctional, Route = "/reserve",
            Feature = "Reserve", Title = "Reserve a book (form)", RequirementRefs = ["FR-001"], RuleVersion = "v" }]);
        var foreign = D.FromSpecification("other-project", Spec(Scenario("US9-9", "x"))).Candidates;
        var final = D.Finalize(Project, "snap", twice.Concat(ui).Concat(foreign), []);
        final.Should().OnlyContain(c => c.ProjectKey == Project, "another project's candidate is never included");
        final.Count(c => c.CanonicalKey.StartsWith("scenario:")).Should().Be(1, "same canonical key = one candidate");
        final.Single(c => c.CanonicalKey.StartsWith("scenario:")).Evidence.Should().Contain(e => e.Reference == "other#ref");
        var specCandidate = final.Single(c => c.CanonicalKey.StartsWith("scenario:"));
        var uiCandidate = final.Single(c => c.CapabilityId == D.UiSource);
        specCandidate.PossibleDuplicates.Should().Equal(uiCandidate.CandidateId);
        uiCandidate.PossibleDuplicates.Should().Equal(specCandidate.CandidateId);
        final.Select(c => c.Title).Should().Contain(["Reserve a book", "Reserve a book (form)"], "similar names are never merged");
    }

    [Fact]
    public void Existing_tests_attach_only_through_an_explicit_requirement_reference()
    {
        var inventory = new SourceTestInventory
        {
            Definitions =
            [
                new SourceTestDefinition { TestDefinitionId = "t1", MethodName = "Reserve_available_book", FilePath = "tests/BookingTests.cs", Line = 10,
                    References = [new SourceTestReference("FR-001", TestReferenceKind.Requirement, TestEvidenceConfidence.Confirmed, "[Trait(\"Requirement\", \"FR-001\")]", 9)] },
                new SourceTestDefinition { TestDefinitionId = "t2", MethodName = "Reserve_a_book", FilePath = "tests/Other.cs", Line = 3 },
            ],
        };
        var final = D.Finalize(Project, "snap", D.FromSpecification(Project, Spec(Scenario("US1-1", "reserved", "FR-001"))).Candidates, D.TestLinks(inventory));
        var c = final.Single(x => x.CanonicalKey.StartsWith("scenario:"));
        c.ExistingTests.Should().ContainSingle().Which.TestId.Should().Be("t1");
        final.SelectMany(x => x.ExistingTests).Should().NotContain(t => t.TestId == "t2", "a similar test name is not a link");
    }

    private static TestCaseDraft ProjectCase(TestCaseContent content, TestCaseEdits? edits = null, params TestCaseDataLink[] data) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = Project, ScenarioId = "tsc-1" }, Generated = content, Edits = edits ?? new(), TestData = [.. data],
    };

    [Fact]
    public void Readiness_is_classified_from_evidence_and_decisions_and_never_reads_no_description_as_not_required()
    {
        var documented = new TestCaseContent { Title = "t", Steps = ["do"], StepExpected = ["ok"], ExpectedResult = "ok", ExpectedBasis = ExpectedResultBasis.Specification };
        E.Assess(ProjectCase(documented)).Testability.Readiness.Should().Be(CaseReadiness.NotAssessed);
        E.Assess(ProjectCase(documented with { TestDataNeed = TestDataNeed.Required })).Testability.Readiness.Should().Be(CaseReadiness.RequiredButMissing);
        E.Assess(ProjectCase(documented with { TestDataNeed = TestDataNeed.Required }, null, new TestCaseDataLink { AssetId = "a", Approval = "Draft" })).Testability.Readiness
            .Should().Be(CaseReadiness.LinkedButUnsuitable);
        E.Assess(ProjectCase(documented with { TestDataNeed = TestDataNeed.Required }, null, new TestCaseDataLink { AssetId = "a", Approval = "Approved" })).Testability.Readiness
            .Should().Be(CaseReadiness.RequiredAndAvailable);
        E.Assess(ProjectCase(documented, new TestCaseEdits { TestDataNeed = TestDataNeed.NotRequired })).Testability.Readiness.Should().Be(CaseReadiness.NotRequired);
        E.Assess(ProjectCase(documented, new TestCaseEdits { BlockedBy = "Payment sandbox missing" })).Testability.Readiness.Should().Be(CaseReadiness.BlockedByPrerequisite);
        E.Assess(ProjectCase(documented with { ExpectedResult = null, ExpectedBasis = ExpectedResultBasis.Missing })).Testability.Readiness.Should().Be(CaseReadiness.NeedsClarification);
        var proposed = E.Assess(ProjectCase(documented with { ExpectedBasis = ExpectedResultBasis.SourceDerived }));
        proposed.Testability.Readiness.Should().Be(CaseReadiness.NeedsClarification, "implementation behaviour is not a basis");
        proposed.Missing.Should().Contain(m => m.StartsWith("Expected result: proposed from source code"));
    }

    [Fact]
    public void A_step_without_an_expected_result_is_reported_and_a_documented_result_is_known_only_once_accepted()
    {
        var content = new TestCaseContent { Title = "t", Steps = ["one", "two"], StepExpected = ["ok", null], ExpectedResult = "ok", ExpectedBasis = ExpectedResultBasis.Contract };
        var open = E.Assess(ProjectCase(content));
        open.Missing.Should().Contain("Step results: 1 step(s) without an expected result (needs clarification)");
        open.Testability.ExpectedBehavior.Should().Be(TestabilityFact.Missing);
        E.Assess(ProjectCase(content) with { Review = TestCaseReviewState.Accepted }).Testability.ExpectedBehavior.Should().Be(TestabilityFact.Known);
    }

    private sealed class Fixed(string id, Func<ScenarioDiscoveryContext, CandidateProviderOutput> run) : ITestScenarioCandidateProvider
    {
        public string CapabilityId => id;
        public Task<CandidateProviderOutput> DiscoverAsync(ScenarioDiscoveryContext context, CancellationToken ct) => Task.FromResult(run(context));
    }

    private sealed class Broken : ITestScenarioCandidateProvider
    {
        public string CapabilityId => D.ApiContract;
        public Task<CandidateProviderOutput> DiscoverAsync(ScenarioDiscoveryContext context, CancellationToken ct) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task Orchestration_reports_every_capability_isolates_a_failure_remembers_the_result_and_keeps_projects_apart()
    {
        var cache = new ScenarioDiscoveryCache();
        var service = new TestScenarioDiscoveryService(new IqrSourceStore(null!), [new Fixed(D.Specification, ctx => D.FromSpecification(ctx.ProjectKey, ctx.Workspace)), new Broken()], cache);
        var result = await service.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = Project, ProjectName = "Library", Workspace = Spec(Scenario("US1-1", "reserved", "FR-001")) }, default);
        result.Capabilities.Single(c => c.Id == D.Specification).State.Should().Be(DiscoveryCapabilityState.Available);
        result.Capabilities.Single(c => c.Id == D.ApiContract).State.Should().Be(DiscoveryCapabilityState.Failed);
        result.Candidates.Should().NotBeEmpty();
        result.Limitations.Should().Contain(l => l.Contains("No source snapshot"));
        service.Last(Project, null)!.DiscoveryId.Should().Be(result.DiscoveryId);
        cache.Find("other-project", result.DiscoveryId).Should().BeNull("a result is never served to another project");
        service.Last("other-project", null).Should().BeNull();
        var bad = () => service.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = "" }, default);
        (await bad.Should().ThrowAsync<TestPlanException>()).Which.Status.Should().Be(400);
    }

    [Fact]
    public async Task A_project_without_usable_evidence_gets_an_honest_empty_result()
    {
        var service = new TestScenarioDiscoveryService(new IqrSourceStore(null!), [new Fixed(D.Specification, ctx => D.FromSpecification(ctx.ProjectKey, ctx.Workspace)),
            new ExistingTestScenarioProvider(), new UiSourceScenarioProvider(), new ApiContractScenarioProvider()], new ScenarioDiscoveryCache());
        var result = await service.DiscoverAsync(new ScenarioDiscoveryRequest { ProjectKey = Project }, default);
        result.Candidates.Should().BeEmpty();
        result.Capabilities.Should().OnlyContain(c => c.State == DiscoveryCapabilityState.NoEvidence || c.State == DiscoveryCapabilityState.NotApplicable);
    }
}
