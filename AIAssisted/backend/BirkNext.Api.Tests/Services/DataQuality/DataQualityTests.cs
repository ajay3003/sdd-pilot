using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Controllers;
using BirkNext.Api.Services.DataQuality;
using BirkNext.Api.Services.Specifications;
using BirkNext.DataQuality;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.DataQuality;

/// <summary>
/// System Settings → Data Quality (2026-11-27): the independent reference calculation, the reference parser, the requirement-identity dry run,
/// the stage-separated checks (source → stored, stored → API, API → UI), consistency, cancellation, the bounded diagnostics store, reports and access.
/// Fixtures are synthetic and in memory; the real snapshot reader is covered against a temporary PostgreSQL database in <see cref="DataQualitySnapshotReaderTests"/>.
/// </summary>
public sealed class DataQualityTests
{
    private const string Project = "import:import-dq0000000000001";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    internal static string Spec(string title, int stories, params string[] requirements)
    {
        var sb = new StringBuilder($"# Feature Specification: {title}\n\n## User Scenarios & Testing\n\n");
        for (var s = 1; s <= stories; s++)
            sb.Append($"### User Story {s} - Story {s} (Priority: P{s})\n\nA member does thing {s}.\n\n**Acceptance Scenarios**:\n\n")
              .Append($"1. **Given** state {s}a, **When** action {s}a, **Then** outcome {s}a (FR-001).\n2. **Given** state {s}b, **When** action {s}b, **Then** outcome {s}b.\n\n");
        sb.Append("### Edge Cases\n\n- **Given** an edge, **When** odd, **Then** handled.\n\n## Requirements *(mandatory)*\n\n");
        foreach (var r in requirements) sb.Append(r).Append('\n');
        return sb.ToString();
    }

    internal static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>A catalog over (path, text) with the production evidence format, as an import stores it.</summary>
    internal static DataQualitySnapshot.CatalogRow Catalog(string project, int revision, DateTimeOffset at, params (string Path, string Text)[] docs)
    {
        var evidence = docs.Where(d => d.Path.EndsWith("spec.md", StringComparison.Ordinal) || d.Path.Contains("/docs/", StringComparison.Ordinal) || d.Path.StartsWith("docs/", StringComparison.Ordinal))
            .Select(d => new StoredSpecificationEvidence(d.Path, Sha(d.Text), SpecificationEvidenceBuilder.Build(d.Path, d.Text))).ToList();
        return new(Guid.NewGuid(), project, revision, "a1b2c3d4e5f60718", null, at, JsonSerializer.Serialize(docs.Select(d => new { path = d.Path, sha256 = Sha(d.Text) }), Web),
            "[]", JsonSerializer.Serialize(evidence, Web));
    }

    internal static DataQualitySnapshot.DraftRow Draft(Guid id, string project, string name, string json, string lifecycle = "Active", string? application = null) =>
        new(id, "project:" + project, lifecycle, DateTimeOffset.UtcNow,
            $"{{\"id\":\"{id}\",\"projectKey\":\"{project}\",{(application is null ? "" : $"\"applicationKey\":\"{application}\",")}\"planName\":\"{name}\",{json}}}");

    internal static string Case(string scenarioId, string identity, string specPath, string review = "Generated", bool ready = false, string suite = "s1", string kind = "Discovered", string? scenarioKey = null) =>
        $"{{\"source\":{{\"kind\":\"{kind}\",\"scenarioId\":\"{scenarioId}\"}},\"suiteId\":\"{suite}\",\"review\":\"{review}\",\"testability\":{{\"ready\":{(ready ? "true" : "false")}}}," +
        $"{(scenarioKey is null ? "" : $"\"scenarioKey\":\"{scenarioKey}\",")}\"generated\":{{\"sourceIdentity\":\"{identity}\",\"evidence\":[{{\"kind\":\"specification\",\"reference\":\"{specPath}#x\"}}]}}}}";

    private static readonly string SpecA = Spec("Loans", 2, "- **FR-001**: Loans are stored.", "- **FR-002**: Receipts are sent.", "- **FR-003**: Fines are computed.");
    private static readonly string SpecB = Spec("Search", 1, "- **FR-001**: Search by name.", "- **FR-004**: Search by id.");
    private static readonly string Root = Spec("Platform rename", 1, "- **FR-001**: Rename the contract.", "- **FR-009**: Old names fail loudly.");

    internal static DataQualitySnapshot Fixture(Action<List<DataQualitySnapshot.DraftRow>>? drafts = null, string? marker = "m1")
    {
        var catalog = Catalog(Project, 2, DateTimeOffset.Parse("2026-11-27T10:00:00Z"),
            ("Library/specs/001-loans/spec.md", SpecA), ("Library/specs/002-search/spec.md", SpecB), ("specs/001-platform-rename/spec.md", Root));
        var plan = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var list = new List<DataQualitySnapshot.DraftRow>
        {
            Draft(plan, Project, "Sprint 1", "\"suites\":[{\"id\":\"s1\",\"name\":\"Loans\"},{\"id\":\"s2\",\"name\":\"Search\"}],\"cases\":[" +
                Case("c1", "tss-a1", "Library/specs/001-loans/spec.md", "Accepted", ready: true) + "," + Case("c2", "tss-a2", "Library/specs/001-loans/spec.md") + "," +
                Case("c3", "tss-x", "Library/specs/001-loans/spec.md", "Excluded") + "," + Case("m1", "", "", kind: "Manual", scenarioKey: "c1") + "]," +
                "\"plannedScenarios\":[{\"scenarioId\":\"p1\",\"suiteId\":\"s2\",\"title\":\"Planned\"}]"),
            Draft(Guid.NewGuid(), Project, "Deleted plan", "\"suites\":[],\"cases\":[" + Case("d1", "tss-d", "Library/specs/001-loans/spec.md") + "]", lifecycle: "Deleted"),
        };
        drafts?.Invoke(list);
        return new DataQualitySnapshot
        {
            ProjectKey = Project, Application = "library", Catalogs = [catalog], Drafts = list, Marker = marker ?? "m1",
            Runs = [new(Guid.NewGuid(), plan, "occ:scenario:c1", "Completed", "Passed", DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4)),
                    new(Guid.NewGuid(), plan, "occ:scenario:c1", "Completed", "Failed", DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow.AddMinutes(-2)),
                    new(Guid.NewGuid(), plan, "occ:scenario:p1", "Completed", "Blocked", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow)],
            Bodies = new(StringComparer.OrdinalIgnoreCase) { [Sha(SpecA)] = SpecA, [Sha(SpecB)] = SpecB, [Sha(Root)] = Root },
        };
    }

    /// <summary>An overview API response that agrees with <see cref="Fixture"/> (built from the reference, then mutated by each test).</summary>
    internal static ManualTestingOverview Agreeing(DataQualitySnapshot s)
    {
        var e = ReferenceCalculator.Calculate(s);
        return new ManualTestingOverview
        {
            ProjectKey = s.ProjectKey, ScenariosAvailable = true,
            Specifications = [.. e.Specs.Select(x => new OverviewSpecification(x.Path, x.Module, x.Path,
                [.. x.Acceptance.Select((a, i) => new OverviewScenario($"id-{x.Path}-{i}", a.StoryKey, null, null, a.Number, a.Priority, a.Title)),
                 .. x.RequirementOnly.Select(r => new OverviewScenario($"req-{x.Path}-{r}", null, null, null, null, null, $"{r}: statement", RequirementOnly: true))]))],
            Cases = [.. e.Cases.Select(c => new OverviewCase { PlanId = c.PlanId, PlanName = c.PlanName, CaseId = c.CaseId, ScenarioIdentity = c.Identity, SpecificationPath = c.SpecificationPath,
                Review = Enum.Parse<TestCaseReviewState>(c.Review), Ready = c.Ready })],
            Plans = [.. e.Plans.Select(p => new OverviewPlan(p.Id, p.Name, 0, 0, 0, 0, [], DateTimeOffset.UtcNow)
                { Suites = p.Suites, PlannedOnly = p.PlannedOnly, Execution = new PlanExecutionCounts(p.Occurrences.Count, p.NotRun, p.Passed, p.Failed, p.Blocked) })],
        };
    }

    private static DataQualityEngine Engine(DataQualitySnapshot s, ManualTestingOverview api, string? markerAfter = null) =>
        new((_, _, _) => Task.FromResult(s), (_, _) => Task.FromResult(markerAfter ?? s.Marker), (_, _) => Task.FromResult(api));

    private static Task<DataQualityReport> RunAll(DataQualityEngine engine) =>
        engine.RunAsync("dq-test", Project, [DataQualityCategory.Dashboard, DataQualityCategory.RequirementIdentity, DataQualityCategory.SourceImport], _ => { }, CancellationToken.None);

    // ---- 1. independent KPI calculation ----

    [Fact]
    public void DQ01_the_reference_counts_scenarios_requirements_cases_plans_and_executions_by_its_own_rules()
    {
        var e = ReferenceCalculator.Calculate(Fixture());
        e.Specs.Should().HaveCount(3);
        e.Specs.Single(x => x.Path == "Library/specs/001-loans/spec.md").Acceptance.Should().HaveCount(4, "two stories × two numbered Given items; the Edge Case Given is not a scenario");
        e.Specs.Single(x => x.Path == "Library/specs/001-loans/spec.md").RequirementOnly.Should().BeEquivalentTo(["FR-002", "FR-003"], "FR-001 is named by a scenario");
        e.Cases.Select(c => c.CaseId).Should().BeEquivalentTo(["c1", "c2", "m1"], "excluded cases and deleted plans are not counted");
        e.Cases.Single(c => c.CaseId == "m1").Identity.Should().Be("tss-a1", "a hand-written case under a scenario belongs to that scenario's case");
        var plan = e.Plans.Single();
        plan.Suites.Should().Be(2);
        plan.Occurrences.Should().BeEquivalentTo(["scenario:c1", "scenario:c2", "scenario:p1"], "the hand-written case under c1 is not a separate occurrence; the planned item is");
        plan.PlannedOnly.Should().Be(1);
        (plan.Passed, plan.Failed, plan.Blocked, plan.NotRun).Should().Be((0, 1, 1, 1), "the NEWEST result per occurrence counts");
    }

    [Fact]
    public void DQ02_project_level_and_module_grouping_follow_the_reference_rule()
    {
        var e = ReferenceCalculator.Calculate(Fixture());
        e.Specs.Single(x => x.Path.StartsWith("specs/", StringComparison.Ordinal)).Module.Should().Be(ReferenceCalculator.ProjectWide);
        e.Specs.Where(x => x.Path.StartsWith("Library/", StringComparison.Ordinal)).Select(x => x.Module).Should().AllBe("Library");
    }

    [Fact]
    public async Task DQ03_a_dashboard_that_agrees_with_the_stored_records_passes_every_stored_to_API_check()
    {
        var s = Fixture();
        var report = await RunAll(Engine(s, Agreeing(s)));
        report.Checks.Where(c => c.Stage == DataQualityStage.StoredToApi).Should().OnlyContain(c => c.Status == DataQualityStatus.Pass || c.Id == "dashboard.modules");
        report.Consistent.Should().BeTrue();
    }

    // ---- 2./3. discrepancies, also when backend and frontend share the wrong count ----

    [Fact]
    public async Task DQ04_a_scenario_missing_from_the_API_is_a_FAIL_with_the_exact_record()
    {
        var s = Fixture();
        var api = Agreeing(s);
        var spec = api.Specifications.First(x => x.Path == "Library/specs/001-loans/spec.md");
        api = api with { Specifications = [.. api.Specifications.Select(x => x == spec ? x with { Scenarios = [.. x.Scenarios.Skip(1)] } : x)] };
        var report = await RunAll(Engine(s, api));
        var check = report.Checks.Single(c => c.Id == "dashboard.acceptance");
        check.Status.Should().Be(DataQualityStatus.Fail);
        check.Cause.Should().Be(DataQualityCause.IncorrectAggregation);
        check.Records.Should().ContainSingle(r => r.Kind == "Mangler i API" && r.Path == "Library/specs/001-loans/spec.md");
    }

    [Fact]
    public async Task DQ05_when_backend_and_frontend_share_the_same_wrong_count_the_independent_expectation_still_differs()
    {
        var s = Fixture();
        var api = Agreeing(s);
        // The API (and so the browser, which computes from it) reports an extra approved case that is not in the stored plans.
        api = api with { Cases = [.. api.Cases, new OverviewCase { PlanId = api.Plans[0].Id, CaseId = "ghost", Review = TestCaseReviewState.Accepted }] };
        var report = await RunAll(Engine(s, api));
        report.Checks.Single(c => c.Id == "dashboard.cases").Status.Should().Be(DataQualityStatus.Fail);
        report.Checks.Single(c => c.Id == "dashboard.cases").Records.Should().ContainSingle(r => r.Key == "ghost" && r.Kind == "Bare i API");
        report.Kpis.Single(k => k.Id == "metric.testCases").Expected.Should().Be(3, "the expected UI value comes from the stored records, never from the API");
        report.Kpis.Single(k => k.Id == "metric.approved").Expected.Should().Be(1);
    }

    [Fact]
    public async Task DQ06_execution_and_occurrence_differences_per_plan_are_reported()
    {
        var s = Fixture();
        var api = Agreeing(s);
        api = api with { Plans = [api.Plans[0] with { Execution = new PlanExecutionCounts(2, 0, 1, 1, 0) }] };
        var check = (await RunAll(Engine(s, api))).Checks.Single(c => c.Id == "dashboard.plans");
        check.Status.Should().Be(DataQualityStatus.Fail);
        check.Records.Select(r => r.Kind).Should().Contain(["Forekomster", "Gjennomføring"]);
    }

    // ---- 4./5. baseline (source version) and stale data ----

    [Fact]
    public async Task DQ07_a_newer_import_of_the_same_application_marks_the_baseline_stale()
    {
        var s = Fixture() with { ApplicationCatalogs = [Catalog("import:import-newer00000001", 1, DateTimeOffset.Parse("2026-11-28T09:00:00Z"), ("Library/specs/001-loans/spec.md", SpecA))] };
        var check = (await RunAll(Engine(s, Agreeing(s)))).Checks.Single(c => c.Id == "baseline");
        check.Status.Should().Be(DataQualityStatus.Warning);
        check.Cause.Should().Be(DataQualityCause.StaleData);
        check.Records.Should().ContainSingle(r => r.Key == "import:import-newer00000001");
    }

    [Fact]
    public void DQ08_the_baseline_is_the_newest_catalog_revision_with_a_stored_parse()
    {
        var older = Catalog(Project, 1, DateTimeOffset.Parse("2026-11-20T10:00:00Z"), ("Library/specs/001-loans/spec.md", SpecA));
        var newest = Catalog(Project, 3, DateTimeOffset.Parse("2026-11-27T12:00:00Z"), ("Library/specs/002-search/spec.md", SpecB)) with { EvidenceJson = null };
        var s = Fixture() with { Catalogs = [older, .. Fixture().Catalogs, newest] };
        ReferenceCalculator.Calculate(s).Baseline!.Revision.Should().Be(2, "revision 3 has no stored parse; revision 2 is the newest with one");
    }

    // ---- 6./11. source and import ----

    [Fact]
    public void DQ09_the_reference_parser_counts_only_numbered_Given_items_in_acceptance_blocks()
    {
        var text = SpecA + "\n### ~~User Story 9 - Old~~ (Removed)\n\n**Acceptance Scenarios**:\n\n1. **Given** gone, **When** x, **Then** y.\n";
        var r = ReferenceSpecParser.Read(text);
        r.Scenarios.Should().HaveCount(4);
        r.GivenOutsideAcceptance.Should().Be(1, "the Edge Case line is not a scenario");
        r.WithdrawnScenarios.Should().Be(1);
        r.Requirements.Should().BeEquivalentTo(["FR-001", "FR-002", "FR-003"]);
        r.RequirementsWithoutScenario.Should().BeEquivalentTo(["FR-002", "FR-003"]);
        ReferenceSpecParser.NormalizeRequirementId("**fr – 7**").Should().Be("FR-7");
    }

    [Fact]
    public async Task DQ10_stored_text_agrees_with_the_stored_parse_and_the_archive_is_reported_SOURCE_UNAVAILABLE()
    {
        var s = Fixture();
        var report = await RunAll(Engine(s, Agreeing(s)));
        report.Checks.Single(c => c.Id == "source.archive").Status.Should().Be(DataQualityStatus.NotVerified, "the uploaded archive is not retained: never PASS");
        report.Checks.Single(c => c.Id == "source.archive").Cause.Should().Be(DataQualityCause.SourceUnavailable);
        report.Checks.Single(c => c.Id == "source.text").Status.Should().Be(DataQualityStatus.Pass);
        report.Checks.Single(c => c.Id == "source.scenarios").Status.Should().Be(DataQualityStatus.Pass);
        report.Checks.Single(c => c.Id == "source.requirementOnly").Status.Should().Be(DataQualityStatus.Pass);
    }

    [Fact]
    public async Task DQ11_missing_source_text_is_NOT_VERIFIED_and_never_passes()
    {
        var s = Fixture();
        s = s with { Bodies = new(StringComparer.OrdinalIgnoreCase) { [Sha(SpecA)] = SpecA } };
        var check = (await RunAll(Engine(s, Agreeing(s)))).Checks.Single(c => c.Id == "source.text");
        check.Status.Should().Be(DataQualityStatus.NotVerified);
        check.Records.Should().HaveCount(2).And.OnlyContain(r => r.Kind == "Kildetekst mangler");
    }

    [Fact]
    public async Task DQ12_stored_text_that_differs_from_its_hash_is_an_import_FAIL()
    {
        var s = Fixture();
        s = s with { Bodies = new(s.Bodies, StringComparer.OrdinalIgnoreCase) { [Sha(SpecB)] = SpecB + "tampered" } };
        var check = (await RunAll(Engine(s, Agreeing(s)))).Checks.Single(c => c.Id == "source.text");
        check.Status.Should().Be(DataQualityStatus.Fail);
        check.Cause.Should().Be(DataQualityCause.IncorrectImport);
    }

    // ---- 7.–10. requirement identities ----

    [Fact]
    public void DQ13_the_same_FR_001_in_separate_specifications_shares_v1_but_gets_distinct_v2()
    {
        var dry = IdentityDryRun.Run(Fixture(), ReferenceCalculator.Calculate(Fixture()));
        dry.RepeatedDisplayIds.Should().NotContainKey("FR-001", "FR-001 is named by a scenario in every fixture document, so it is not a requirement without one");
        var v1 = IdentityDryRun.V1(null, "FR-002");
        IdentityDryRun.V1(null, "FR-002").Should().Be(v1, "v1 has no file path");
        IdentityDryRun.V2("Library/specs/001-loans/spec.md", "FR-002").Should().NotBe(IdentityDryRun.V2("Library/specs/002-search/spec.md", "FR-002"));
        IdentityDryRun.V2("Library\\specs\\001-loans\\spec.md", "fr-002").Should().Be(IdentityDryRun.V2("library/specs/001-loans/spec.md", "FR-002"), "normalized path and id");
    }

    [Fact]
    public void DQ14_v2_resolution_is_unique_by_the_records_own_path_ambiguous_without_one_and_unresolved_without_a_document()
    {
        // FR-009 without a scenario in two documents of the same (empty) module: one shared v1 identity.
        var a = Spec("A", 1, "- **FR-009**: A."); var b = Spec("B", 1, "- **FR-009**: B.");
        var shared = IdentityDryRun.V1(null, "FR-009");
        var s = Fixture() with { Catalogs = [Catalog(Project, 1, DateTimeOffset.UtcNow, ("M/specs/001-a/spec.md", a), ("M/specs/002-b/spec.md", b))] };
        // «tss-gone» is known as a requirement identity from an earlier discovery; it is no longer in the baseline.
        s = s with { Discoveries = [new("d0", Project, null, "Current", DateTimeOffset.UtcNow, "{\"candidates\":[{\"canonicalKey\":\"requirement:|FR-077\",\"sourceIdentity\":\"tss-gone\"}]}")] };
        s = s with
        {
            Drafts = [Draft(Guid.NewGuid(), Project, "P", "\"suites\":[],\"cases\":[" + Case("k1", shared, "M/specs/001-a/spec.md") + "]," +
                $"\"plannedScenarios\":[{{\"scenarioId\":\"q1\",\"sourceIdentity\":\"{shared}\",\"suiteId\":\"\"}},{{\"scenarioId\":\"q2\",\"sourceIdentity\":\"tss-gone\",\"suiteId\":\"\"}}]")],
        };
        var rows = IdentityDryRun.Run(s, ReferenceCalculator.Calculate(s)).Rows;
        var forA = rows.Single(r => r.SpecificationPath == "M/specs/001-a/spec.md" && r.RequirementId == "FR-009");
        forA.DocumentsWithSameV1.Should().Be(2);
        forA.Resolution.Should().Be(IdentityResolution.Ambiguous, "the plan item has no specification path, so it cannot be placed");
        forA.TestCases.Should().ContainSingle(x => x.Contains("k1"));
        forA.PlanItems.Should().ContainSingle(x => x.Contains("q1"));
        forA.V2.Should().NotBe(rows.Single(r => r.SpecificationPath == "M/specs/002-b/spec.md").V2);
        rows.Should().ContainSingle(r => r.V1 == "tss-gone" && r.Resolution == IdentityResolution.Unresolved);
        rows.Should().NotContain(r => r.V1 == "tss-a1", "a case of an acceptance scenario is not a requirement relationship and is never «unresolved»");
        rows.Where(r => r.RequirementId is "FR-002" or "FR-003").Should().BeEmpty("those documents are not in this catalog");
    }

    [Fact]
    public void DQ15_without_unplaced_records_a_shared_v1_resolves_uniquely_and_a_unique_v1_is_not_affected()
    {
        var a = Spec("A", 1, "- **FR-009**: A.", "- **FR-010**: Only here."); var b = Spec("B", 1, "- **FR-009**: B.");
        var shared = IdentityDryRun.V1(null, "FR-009");
        var s = Fixture() with
        {
            Catalogs = [Catalog(Project, 1, DateTimeOffset.UtcNow, ("M/specs/001-a/spec.md", a), ("M/specs/002-b/spec.md", b))],
            Drafts = [Draft(Guid.NewGuid(), Project, "P", "\"suites\":[],\"cases\":[" + Case("k1", shared, "M/specs/001-a/spec.md", "Accepted") + "]")],
        };
        var rows = IdentityDryRun.Run(s, ReferenceCalculator.Calculate(s)).Rows;
        rows.Where(r => r.RequirementId == "FR-009").Should().OnlyContain(r => r.Resolution == IdentityResolution.Unique);
        rows.Single(r => r.SpecificationPath == "M/specs/001-a/spec.md" && r.RequirementId == "FR-009").Approvals.Should().ContainSingle();
        rows.Single(r => r.SpecificationPath == "M/specs/002-b/spec.md").TestCases.Should().BeEmpty("the case's own path places it in document A");
        rows.Single(r => r.RequirementId == "FR-010").Resolution.Should().Be(IdentityResolution.NotAffected);
    }

    [Fact]
    public async Task DQ16_ambiguous_relationships_are_reported_as_needing_review_and_never_resolved_or_passed()
    {
        var a = Spec("A", 1, "- **FR-009**: A."); var b = Spec("B", 1, "- **FR-009**: B.");
        var shared = IdentityDryRun.V1(null, "FR-009");
        var s = Fixture() with
        {
            Catalogs = [Catalog(Project, 1, DateTimeOffset.UtcNow, ("M/specs/001-a/spec.md", a), ("M/specs/002-b/spec.md", b))],
            Drafts = [Draft(Guid.NewGuid(), Project, "P", $"\"suites\":[],\"cases\":[],\"plannedScenarios\":[{{\"scenarioId\":\"q1\",\"sourceIdentity\":\"{shared}\",\"suiteId\":\"\"}}]")],
            Bodies = new(StringComparer.OrdinalIgnoreCase) { [Sha(a)] = a, [Sha(b)] = b },
        };
        var report = await RunAll(Engine(s, Agreeing(s)));
        var check = report.Checks.Single(c => c.Id == "identity.ambiguous");
        check.Status.Should().Be(DataQualityStatus.Warning);
        check.Cause.Should().Be(DataQualityCause.AmbiguousIdentity);
        report.Checks.Single(c => c.Id == "identity.sharedV1").Records.Should().ContainSingle(r => r.Key == shared);
        report.Checks.Single(c => c.Id == "identity.identity1").Status.Should().Be(DataQualityStatus.NotVerified);
    }

    [Fact]
    public void DQ17_a_requirement_that_moved_to_another_path_with_the_same_text_is_flagged_never_relinked()
    {
        var text = Spec("A", 1, "- **FR-020**: Old names fail loudly.");
        var before = Catalog(Project, 1, DateTimeOffset.Parse("2026-11-01T00:00:00Z"), ("M/specs/001-a/spec.md", text));
        var after = Catalog(Project, 2, DateTimeOffset.Parse("2026-11-02T00:00:00Z"), ("M/specs/009-renamed/spec.md", text));
        var s = Fixture() with { Catalogs = [before, after] };
        IdentityDryRun.Run(s, ReferenceCalculator.Calculate(s)).Relocations.Should().ContainSingle(x => x.Contains("M/specs/001-a/spec.md → M/specs/009-renamed/spec.md"));
    }

    [Fact]
    public async Task DQ25_scenarios_without_a_user_story_are_compared_by_count_not_by_incomparable_keys()
    {
        var loose = "# Feature Specification: Loose\n\n## User Scenarios & Testing\n\n**Scenario 1: First**\n\n- **Given** a, **When** b, **Then** c.\n\n**Scenario 2: Second**\n\n- **Given** d, **When** e, **Then** f.\n";
        var s = Fixture() with { Catalogs = [Catalog(Project, 1, DateTimeOffset.UtcNow, ("M/specs/001-loose/spec.md", loose))] };
        var api = Agreeing(s);
        api.Specifications.Single().Scenarios.Should().OnlyContain(sc => sc.StoryKey == null);
        (await RunAll(Engine(s, api))).Checks.Single(c => c.Id == "dashboard.acceptance").Status.Should().Be(DataQualityStatus.Pass);
        api = api with { Specifications = [api.Specifications[0] with { Scenarios = [.. api.Specifications[0].Scenarios.Skip(1)] }] };
        var check = (await RunAll(Engine(s, api))).Checks.Single(c => c.Id == "dashboard.acceptance");
        check.Status.Should().Be(DataQualityStatus.Fail);
        check.Records.Should().ContainSingle(r => r.Kind == "Antall uten brukerhistorie");
    }

    [Fact]
    public void DQ26_twin_scenarios_keep_distinct_identities_by_the_one_rule_discovery_and_Testoversikt_share()
    {
        var twins = "# Feature Specification: Twins\n\n## User Scenarios & Testing\n\n### User Story 1 - Receipt (Priority: P1)\n\nA message.\n\n**Acceptance Scenarios**:\n\n" +
            "1. **Given** a message, **When** it is processed, **Then** a positive receipt is sent.\n2. **Given** a message, **When** it is processed, **Then** a negative receipt is sent.\n";
        var evidence = SpecificationEvidenceBuilder.Build("M/specs/001-twins/spec.md", twins);
        var raw = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine.FromSpecification(Project, evidence).Candidates
            .Where(c => c.CapabilityId == BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine.Specification && c.CanonicalKey.StartsWith("scenario:", StringComparison.Ordinal)).ToList();
        raw.Select(c => c.SourceIdentity).Distinct().Should().ContainSingle("both twins share one situation identity before separation");
        var separated = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine.SeparateTwins(raw);
        separated.Select(c => c.SourceIdentity).Should().OnlyHaveUniqueItems().And.HaveCount(2);
        var finalized = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine.Finalize(Project, "", raw, []);
        finalized.Where(c => c.CanonicalKey.StartsWith("scenario:", StringComparison.Ordinal)).Select(c => c.SourceIdentity)
            .Should().BeEquivalentTo(separated.Select(c => c.SourceIdentity), "discovery and Testoversikt give a twin the same identity, so its case matches its row");
    }

    // ---- 12. consistent snapshot ----

    [Fact]
    public async Task DQ18_a_change_during_the_run_makes_passing_checks_INCONCLUSIVE()
    {
        var s = Fixture();
        var report = await RunAll(Engine(s, Agreeing(s), markerAfter: "changed"));
        report.Consistent.Should().BeFalse();
        report.Checks.Should().NotContain(c => c.Status == DataQualityStatus.Pass);
        report.Checks.Should().Contain(c => c.Status == DataQualityStatus.Inconclusive && c.Cause == DataQualityCause.ConcurrentChange);
    }

    // ---- 14. access ----

    [Fact]
    public void DQ19_the_API_requires_the_system_configuration_permission()
    {
        var attribute = typeof(DataQualityController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Single();
        attribute.Policy.Should().Be(BirkNext.Api.Services.LocalSecurity.SystemConfigurationPermissions.Policy);
        typeof(DataQualityController).GetMethods().SelectMany(m => m.GetCustomAttributes(typeof(AllowAnonymousAttribute), true)).Should().BeEmpty();
    }

    // ---- 15. cancellation, partial runs, store ----

    [Fact]
    public async Task DQ20_a_cancelled_run_ends_Cancelled_without_a_report_and_one_run_at_a_time()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bn-dq-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gate = new TaskCompletionSource();
            var services = new ServiceCollection();
            services.AddScoped(_ => new DataQualityEngine(async (_, _, ct) => { await gate.Task.WaitAsync(ct); return Fixture(); }, (_, _) => Task.FromResult("m1"),
                (_, _) => Task.FromResult(new ManualTestingOverview())));
            var store = new DataQualityRunStore(folder);
            var coordinator = new DataQualityRunCoordinator(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), store, NullLogger<DataQualityRunCoordinator>.Instance);
            var run = coordinator.Start(new DataQualityRunRequest(Project, [DataQualityCategory.Dashboard]), "tester");
            var second = () => coordinator.Start(new DataQualityRunRequest(Project, [DataQualityCategory.Dashboard]), "tester");
            second.Should().Throw<DataQualityRunConflictException>();
            coordinator.Cancel(run.RunId);
            await coordinator.WaitAsync(run.RunId);
            var done = coordinator.Get(run.RunId)!;
            done.State.Should().Be(DataQualityRunState.Cancelled);
            done.Report.Should().BeNull("a cancelled run has no checks that could count as passed");
            store.Recent().Should().ContainSingle(r => r.RunId == run.RunId);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void DQ21_the_diagnostics_store_keeps_the_newest_twenty_runs_and_Local_Data_Reset_clears_it()
    {
        var folder = Path.Combine(Path.GetTempPath(), "bn-dq-" + Guid.NewGuid().ToString("N"));
        var store = new DataQualityRunStore(folder);
        for (var i = 0; i < 25; i++)
        {
            store.Save(new DataQualityRun { RunId = $"dq-{i:00}", ProjectKey = Project, State = DataQualityRunState.Completed, StartedAt = DateTimeOffset.UtcNow.AddMinutes(i) });
        }
        Directory.GetFiles(folder, "*.json").Should().HaveCount(DataQualityRunStore.MaxRuns);
        store.Load("dq-24").Should().NotBeNull();
        store.Load("dq-00").Should().BeNull();
        store.ClearAll();
        Directory.Exists(folder).Should().BeFalse();
    }

    // ---- 16. large projects ----

    [Fact]
    public async Task DQ22_a_large_project_is_audited_within_seconds()
    {
        var docs = Enumerable.Range(1, 150).Select(i => ($"M{i % 12}/specs/{i:000}-f/spec.md", Spec($"F{i}", 6, Enumerable.Range(1, 12).Select(r => $"- **FR-{r:000}**: Requirement {r}.").ToArray()))).ToArray();
        var cases = string.Join(",", Enumerable.Range(1, 3000).Select(i => Case($"c{i}", $"tss-{i}", docs[i % docs.Length].Item1, i % 3 == 0 ? "Accepted" : "Generated")));
        var s = new DataQualitySnapshot
        {
            ProjectKey = Project, Catalogs = [Catalog(Project, 1, DateTimeOffset.UtcNow, docs)], Marker = "m",
            Drafts = [Draft(Guid.NewGuid(), Project, "Big", $"\"suites\":[{{\"id\":\"s1\"}}],\"cases\":[{cases}]")],
            Bodies = docs.ToDictionary(d => Sha(d.Item2), d => d.Item2, StringComparer.OrdinalIgnoreCase),
        };
        var api = Agreeing(s);
        var watch = Stopwatch.StartNew();
        var report = await RunAll(Engine(s, api));
        watch.Stop();
        ReferenceCalculator.Calculate(s).Specs.Sum(x => x.Acceptance.Count).Should().Be(150 * 12);
        report.Checks.Single(c => c.Id == "dashboard.cases").Status.Should().Be(DataQualityStatus.Pass);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "1,800 scenarios, 1,650 requirements and 3,000 cases");
    }

    // ---- 17. reports ----

    [Fact]
    public async Task DQ23_the_Markdown_and_JSON_reports_carry_baseline_statuses_records_and_unverified_UI_values()
    {
        var s = Fixture();
        var report = await RunAll(Engine(s, Agreeing(s)));
        var md = DataQualityMarkdown.Write(report);
        md.Should().Contain("# BirkNext Data Quality").And.Contain("Kildegrunnlag:").And.Contain("NOT VERIFIED").And.Contain("SOURCE UNAVAILABLE")
            .And.Contain("NOT VERIFIED (nettleseren har ikke sendt verdien)", "no UI value was sent, so the browser stage is not verified");
        var json = JsonSerializer.Serialize(report, DataQualityRunStore.Json);
        var back = JsonSerializer.Deserialize<DataQualityReport>(json, DataQualityRunStore.Json)!;
        back.Schema.Should().Be(DataQualityReport.SchemaVersion);
        back.Checks.Should().HaveCount(report.Checks.Count);
        json.Should().Contain("\"status\":\"NotVerified\"");
    }

    [Fact]
    public void DQ24_the_KPI_contracts_are_written_down_for_every_expected_figure()
    {
        var s = Fixture();
        foreach (var k in DataQualityEngine.Kpis(ReferenceCalculator.Calculate(s), Agreeing(s)))
            k.Contract.Should().NotBeNullOrWhiteSpace(k.Id);
        ReferenceCalculator.Contracts.Keys.Should().Contain(["metric.scenarios", "metric.covered", "e2e.documented", "e2eCandidates", "plan.occurrences", "module.scenarios"]);
    }
}
