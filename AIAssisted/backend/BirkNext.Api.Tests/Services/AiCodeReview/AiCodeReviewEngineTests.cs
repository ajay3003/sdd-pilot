using BirkNext.AiCodeReview;
using BirkNext.Api.Services.AiCodeReview;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.AiCodeReview;

/// <summary>
/// The AI-Generated Code Review profile over real Source Analysis snapshots of a generic (non-M2LB) project: deterministic rules, honest
/// rule states, provenance on every finding, baseline/current change semantics and no authorship inference.
/// </summary>
public sealed class AiCodeReviewEngineTests : IDisposable
{
    private readonly BirkNext.Api.Data.AppDbContext _db = AiCodeReviewFixture.Db();
    public void Dispose() => _db.Dispose();

    private async Task<(IqrSourceSnapshot Baseline, IqrSourceSnapshot Current)> SnapshotsAsync() =>
        (await AiCodeReviewFixture.AnalyzeAsync(_db, "contoso-shop.zip", AiCodeReviewFixture.Baseline()),
         await AiCodeReviewFixture.AnalyzeAsync(_db, "contoso-shop.zip", AiCodeReviewFixture.Current()));

    private static AiCodeReviewResult Review(IqrSourceSnapshot current, IqrSourceSnapshot? baseline = null, AiWorkspaceEvidence? workspace = null, AiReviewScope scope = AiReviewScope.EntireSource) =>
        AiCodeReviewEngine.Review(current, baseline, new AiCodeReviewRequest { CurrentSnapshotId = current.Id, BaselineSnapshotId = baseline?.Id, Scope = scope, Workspace = workspace }, DateTimeOffset.UtcNow);

    private static IEnumerable<AiCodeFinding> Rule(AiCodeReviewResult r, string id) => r.Findings.Where(f => f.RuleId == id);
    private static AiRuleExecution State(AiCodeReviewResult r, string id) => r.Rules.Single(x => x.RuleId == id);

    [Fact]
    public async Task Source_analysis_captures_code_risk_observations_once_at_upload()
    {
        var (_, current) = await SnapshotsAsync();
        var risk = current.CodeRiskEvidence!;
        risk.Languages.Should().ContainSingle(l => l.Supported && l.Language == "C#");
        var kinds = risk.Observations.Select(o => o.Kind).ToHashSet();
        kinds.Should().Contain([CodeRiskObservationKind.NotImplementedThrow, CodeRiskObservationKind.PlaceholderReturn, CodeRiskObservationKind.PlaceholderLiteral,
            CodeRiskObservationKind.EmptyCatch, CodeRiskObservationKind.BroadCatchReturnsSuccess, CodeRiskObservationKind.ExceptionDetailReturned,
            CodeRiskObservationKind.UnresolvedNamespaceImport, CodeRiskObservationKind.UnusedPrivateMethod, CodeRiskObservationKind.DeveloperExceptionPageUnconditional,
            CodeRiskObservationKind.PermissiveCors, CodeRiskObservationKind.InputModelWithoutValidation, CodeRiskObservationKind.TestWithoutAssertion,
            CodeRiskObservationKind.EmptyTest, CodeRiskObservationKind.CommentMarker]);
        risk.Endpoints.Should().Contain(e => e.Display == "GET OrdersController.List" && !e.Authorized);
        risk.Endpoints.Should().Contain(e => e.Display == "POST /orders/import" && !e.Authorized && !e.AllowAnonymous);
        risk.Duplicates.Should().ContainSingle(d => d.Kind == "Type" && d.Locations.Count == 2);
        risk.ConfigurationKeyReads.Should().Contain(c => c.Symbol == "Payments:ApiKey");
        risk.Observations.Should().OnlyContain(o => !o.Detail.Contains("ex.Message") && !o.Detail.Contains("best effort"), "details are fixed descriptions, never source text");
    }

    [Fact]
    public async Task Placeholder_rules_stay_low_noise()
    {
        var (_, current) = await SnapshotsAsync();
        var obs = current.CodeRiskEvidence!.Observations;
        obs.Where(o => o.Kind == CodeRiskObservationKind.EmptyCatch).Should().ContainSingle("a commented empty catch is a documented decision, not a swallowed failure");
        obs.Should().NotContain(o => o.Kind == CodeRiskObservationKind.NotImplementedThrow && o.Symbol.EndsWith("Legacy"), "NotSupportedException is an intentional unsupported path");
        obs.Should().NotContain(o => o.Kind == CodeRiskObservationKind.TestWithoutAssertion && o.Symbol.EndsWith("Uses_helper"), "assertions in a test-class helper count");
        // Found on a real project: interface members are not unused private methods; a Result returned to the caller is not a client response.
        obs.Should().NotContain(o => o.Kind == CodeRiskObservationKind.UnusedPrivateMethod && (o.Symbol.EndsWith("Publish") || o.Symbol.EndsWith("CountAsync")));
        obs.Where(o => o.Kind == CodeRiskObservationKind.ExceptionDetailReturned).Should().ContainSingle(o => o.Symbol == "OrdersController.Create");
        var review = Review(current);
        var comments = Rule(review, "AIC-PLACEHOLDER-004").Single();
        comments.Severity.Should().Be(AiFindingSeverity.Info, "comment-only TODOs are informational");
        Rule(review, "AIC-PLACEHOLDER-001").Should().OnlyContain(f => f.Severity == AiFindingSeverity.Medium, "a runtime placeholder matters more than a comment");
    }

    [Fact]
    public async Task Current_snapshot_review_reports_every_category_honestly()
    {
        var (_, current) = await SnapshotsAsync();
        var review = Review(current);

        review.Mode.Should().Be(AiReviewMode.CurrentSnapshot);
        review.Rules.Should().HaveCount(AiCodeReviewEngine.Catalogue.Count, "every rule reports how it ran — none is hidden");
        review.Categories.Single(c => c.Category == AiCodeReviewCategory.RequirementsAlignment).Status.Should().Be(AiCategoryStatus.NotAssessed, "no Specification is loaded");
        review.Categories.Single(c => c.Category == AiCodeReviewCategory.ContractDrift).Status.Should().Be(AiCategoryStatus.NotApplicable, "contract drift needs a baseline");
        review.Categories.Single(c => c.Category == AiCodeReviewCategory.ChangeRisk).Status.Should().Be(AiCategoryStatus.NotApplicable);
        State(review, "AIC-ARCH-002").State.Should().Be(AiRuleExecutionState.NotAssessed, "no explicit architecture rule exists; none is invented");
        State(review, "AIC-TEST-006").State.Should().Be(AiRuleExecutionState.Unsupported, "mirror tests are not guessed from text similarity");
        State(review, "AIC-REF-004").State.Should().Be(AiRuleExecutionState.NotAssessed, "build not run = not verified");
        State(review, "AIC-TEST-005").State.Should().Be(AiRuleExecutionState.NotAssessed, "test files are not executed tests");
        review.Attention.Should().BeNull("there is no score and no attention level without a change");

        Rule(review, "AIC-SEC-003").Single().Severity.Should().Be(AiFindingSeverity.High, "any origin together with credentials");
        Rule(review, "AIC-SEC-004").Should().ContainSingle();
        Rule(review, "AIC-SEC-002").Should().Contain(f => f.Title.Contains("ProductsController.Delete")).And.Contain(f => f.Title.Contains("ProductsController.Update"));
        Rule(review, "AIC-SEC-002").Should().NotContain(f => f.Title.Contains("Catalogue"), "an explicit [AllowAnonymous] is a decision, not a gap");
        Rule(review, "AIC-VAL-003").Single().Limitation.Should().Contain("not stored");
        Rule(review, "AIC-VAL-003").Single().Severity.Should().Be(AiFindingSeverity.Medium, "a broad catch's exception text reaches the client");
        Rule(review, "AIC-VAL-004").Single().Title.Should().Contain("validation");
        Rule(review, "AIC-REF-001").Single().Title.Should().Be("Unresolved reference: namespace Shop.Api.Payments");
        Rule(review, "AIC-REF-001").Single().Description.Should().StartWith("Potential generated-code hallucination risk");
        Rule(review, "AIC-REF-002").Should().Contain(f => f.Title.Contains("Payments:ApiKey"));
        Rule(review, "AIC-DUP-001").Should().ContainSingle(f => f.Locations.Count == 2);
        Rule(review, "AIC-DEAD-001").Should().ContainSingle(f => f.Locations[0].Symbol!.EndsWith("Unused"));
        Rule(review, "AIC-TEST-001").Should().HaveCount(2);
        Rule(review, "AIC-TEST-002").Should().ContainSingle();
    }

    [Fact]
    public async Task Every_finding_carries_provenance_and_no_text_claims_authorship()
    {
        var (baseline, current) = await SnapshotsAsync();
        var review = Review(current, baseline, Workspace(current));

        review.Findings.Should().NotBeEmpty();
        foreach (var f in review.Findings)
        {
            f.EvidenceSources.Should().NotBeEmpty();
            f.SourceSnapshotId.Should().Be(current.Id);
            f.BaselineSnapshotId.Should().Be(baseline.Id);
            (f.Locations.Count + f.RelatedIds.Count).Should().BePositive($"{f.RuleId} must point at evidence");
            f.Limitation.Should().NotBeNullOrWhiteSpace();
            f.Recommendation.Should().NotBeNullOrWhiteSpace();
        }
        var text = string.Join("\n", review.Findings.SelectMany(f => new[] { f.Title, f.Description, f.Rationale, f.Limitation, f.Recommendation }))
                   + string.Join("\n", review.Categories.Select(c => c.Explanation)) + string.Join("\n", review.Rules.Select(r => r.Title + r.Reason));
        text.Should().NotContainAny("written by AI", "AI-generated code was", "generated by AI", "AI wrote", "hallucinated this", "authored by");
        review.Disclaimer.Should().Contain("does not detect whether code was written by AI");
        text.Should().NotMatchRegex(@"\d+(\.\d+)?\s*%", "there is no score");
    }

    [Fact]
    public async Task Change_review_compares_baseline_and_current_snapshots()
    {
        var (baseline, current) = await SnapshotsAsync();
        var review = Review(current, baseline);

        review.Mode.Should().Be(AiReviewMode.SnapshotChange);
        review.ChangedFiles.Should().BeGreaterThan(0);
        review.Changes.Select(c => c.Area).Should().Contain(["Files", "Dependencies", "Security controls", "Tests"]);

        var removed = Rule(review, "AIC-SEC-001").ToList();
        removed.Should().Contain(f => f.Title == "Authorization protection removed in source evidence: GET OrdersController.List");
        removed.Should().OnlyContain(f => f.Severity == AiFindingSeverity.High && f.Change == AiChangeKind.Changed && f.Baseline != null && f.Current == "no authorization metadata");
        removed.Should().OnlyContain(f => f.Limitation.Contains("runtime enforcement is not verified"));
        string.Join(" ", removed.Select(f => f.Title + f.Description)).Should().NotContain("bypass confirmed");

        var delete = review.Findings.Single(f => f.LogicalId.Contains("ProductsController.Delete"));
        delete.EvidenceReferences.Should().Be(2, "seen by the removal rule and the sibling-pattern rule: one logical finding, two evidence references");
        delete.Severity.Should().Be(AiFindingSeverity.High);

        var deps = Rule(review, "AIC-DEP-001").ToList();
        deps.Should().ContainSingle(f => f.Title.StartsWith("Added dependency Polly") && f.Change == AiChangeKind.Introduced);
        Rule(review, "AIC-DEP-002").Should().ContainSingle(f => f.Title.EndsWith("Polly"), "Serilog is imported, Polly is not");
        State(review, "AIC-DEP-003").State.Should().Be(AiRuleExecutionState.NotAssessed, "vulnerabilities are never assumed without dependency health evidence");

        var contract = Rule(review, "AIC-CONTRACT-001").ToList();
        contract.Should().Contain(f => f.Change == AiChangeKind.Removed && f.Severity == AiFindingSeverity.Medium && f.Description.Contains("potentially incompatible"));
        contract.Should().Contain(f => f.Change == AiChangeKind.Introduced && f.Severity == AiFindingSeverity.Info && f.Description.Contains("additive"));
        contract.Should().OnlyContain(f => f.Limitation.Contains("Contract changed ≠ incompatible"));

        Rule(review, "AIC-DRIFT-001").Should().Contain(f => f.Title.Contains("Authority") && f.Change == AiChangeKind.Removed && f.Severity == AiFindingSeverity.Medium);
        Rule(review, "AIC-TEST-003").Should().BeEmpty("the Shop.Api.Tests project changed together with Shop.Api");
        Rule(review, "AIC-TEST-004").Should().ContainSingle(f => f.Severity == AiFindingSeverity.Info);
        Rule(review, "AIC-PLACEHOLDER-001").Should().OnlyContain(f => f.Change == AiChangeKind.Introduced);
        review.Attention.Should().Be(AiReviewAttention.High);
        review.AttentionBasis.Should().Contain("not a score");
    }

    [Fact]
    public async Task Production_change_without_test_change_is_a_potential_gap_only_when_mapping_is_reliable()
    {
        (string, string)[] Files(string body) =>
        [
            ("Inv/src/Billing/Billing.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"),
            ("Inv/src/Billing/Invoice.cs", "namespace Billing; public sealed class Invoice { public int Total() => " + body + "; }"),
            ("Inv/src/Reports/Reports.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"),
            ("Inv/src/Reports/Report.cs", "namespace Reports; public sealed class Report { public int Count() => " + body + "; }"),
            ("Inv/tests/Billing.Tests/Billing.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.0\" /></ItemGroup></Project>"),
            ("Inv/tests/Billing.Tests/InvoiceTests.cs", "using Xunit; namespace Billing.Tests; public sealed class InvoiceTests { [Fact] public void Total() => Assert.Equal(1, 1); }"),
        ];
        var before = await AiCodeReviewFixture.AnalyzeAsync(_db, "inv.zip", AiCodeReviewFixture.Zip(Files("1")));
        var after = await AiCodeReviewFixture.AnalyzeAsync(_db, "inv.zip", AiCodeReviewFixture.Zip(Files("2")));
        var review = Review(after, before);
        Rule(review, "AIC-TEST-003").Should().ContainSingle(f => f.Title == "Potential test coverage gap: Billing" && f.Severity == AiFindingSeverity.Low);
        Rule(review, "AIC-TEST-003").Should().NotContain(f => f.Title.EndsWith("Reports"), "Reports has no test project by naming convention: no reliable mapping, no claim");
        Rule(review, "AIC-TEST-003").Single().Limitation.Should().Contain("no execution or coverage evidence");
    }

    [Fact]
    public async Task Changed_files_scope_only_reviews_files_that_changed()
    {
        var (baseline, current) = await SnapshotsAsync();
        var scoped = Review(current, baseline, scope: AiReviewScope.ChangedFiles);
        var changedTestFile = "ContosoShop/tests/Shop.Api.Tests/OrdersTests.cs";
        scoped.Scope.Should().Be(AiReviewScope.ChangedFiles);
        // Every location-based finding touches at least one changed file (a duplicate pair may also point at its unchanged partner).
        scoped.Findings.Where(f => f.Locations.Any(l => l.File.EndsWith(".cs"))).Should().OnlyContain(f => f.Locations.Any(l =>
            l.File.Contains("/Controllers/") || l.File.Contains("/Services/") || l.File.EndsWith("Program.cs") || l.File.EndsWith("Summaries.cs") || l.File == changedTestFile));
        scoped.Findings.Should().NotContain(f => f.Locations.Count > 0 && f.Locations.All(l => l.File.EndsWith("Models/Models.cs")), "Models.cs did not change");
        Review(current, baseline).Findings.Count.Should().BeGreaterThanOrEqualTo(scoped.Findings.Count);
    }

    [Fact]
    public async Task Requirements_alignment_reuses_the_workspace_requirement_graph()
    {
        var (baseline, current) = await SnapshotsAsync();
        var review = Review(current, baseline, Workspace(current));

        Rule(review, "AIC-REQ-001").Single().RelatedIds.Should().Equal("FR-002");
        Rule(review, "AIC-REQ-002").Single().RelatedIds.Should().Equal("T003");
        Rule(review, "AIC-REQ-003").Single().RelatedIds.Should().Equal(new[] { "FR-003" }, "FR-003's link was recorded against another snapshot");
        Rule(review, "AIC-REQ-004").Single().Locations.Should().Contain(l => l.File.EndsWith("OrderService.cs"));
        Rule(review, "AIC-REQ-001").Single().Limitation.Should().Contain("not that the requirement is implemented");

        var noSpec = Review(current, baseline, new AiWorkspaceEvidence { SpecificationAvailable = false });
        noSpec.Rules.Where(r => r.RuleId.StartsWith("AIC-REQ")).Should().OnlyContain(r => r.State == AiRuleExecutionState.NotAssessed);
        noSpec.Rules.Single(r => r.RuleId == "AIC-PLACEHOLDER-001").State.Should().Be(AiRuleExecutionState.Executed, "missing documents block only the rules that need them");
    }

    [Fact]
    public async Task Imported_failed_test_results_are_surfaced_and_absence_is_not_verified()
    {
        var (_, current) = await SnapshotsAsync();
        var withResults = Review(current, workspace: new AiWorkspaceEvidence { TestExecution = new(1, 10, 8, 2, 0, "run-1") });
        Rule(withResults, "AIC-TEST-005").Single().Severity.Should().Be(AiFindingSeverity.Medium);
        Review(current).Rules.Single(r => r.RuleId == "AIC-TEST-005").Reason.Should().Contain("NotVerified");
    }

    [Fact]
    public async Task A_snapshot_without_code_risk_evidence_is_not_assessed_rather_than_clean()
    {
        var (_, current) = await SnapshotsAsync();
        var old = current with { CodeRiskEvidence = null };
        var review = Review(old);
        review.Categories.Single(c => c.Category == AiCodeReviewCategory.Placeholders).Status.Should().Be(AiCategoryStatus.NotAssessed);
        review.Categories.Should().NotContain(c => c.Category == AiCodeReviewCategory.Security && c.Status == AiCategoryStatus.NoIndicators);
        review.Readiness.Single(r => r.Key == "code-risk").Available.Should().BeFalse();
    }

    [Fact]
    public async Task Unsupported_languages_are_reported_not_treated_as_clean()
    {
        var bytes = AiCodeReviewFixture.Zip(
            ("web/package.json", """{ "name": "web", "dependencies": { "left-pad": "1.3.0" } }"""),
            ("web/src/app.ts", "export const placeholder = () => { throw new Error('TODO'); };"),
            ("web/src/util.py", "def todo():\n    raise NotImplementedError()\n"));
        var snapshot = await AiCodeReviewFixture.AnalyzeAsync(_db, "web.zip", bytes);
        snapshot.CodeRiskEvidence!.Languages.Should().Contain(l => l.Language == "TypeScript" && !l.Supported).And.Contain(l => l.Language == "Python" && !l.Supported);
        var review = Review(snapshot);
        review.Limitations.Should().Contain(l => l.StartsWith("TypeScript: 1 file(s) present"));
        review.Findings.Should().NotContain(f => f.Category == AiCodeReviewCategory.Placeholders && f.Severity > AiFindingSeverity.Info);
    }

    [Fact]
    public async Task Service_binds_runs_to_exact_snapshots_and_refuses_unrelated_baselines()
    {
        var (baseline, current) = await SnapshotsAsync();
        var other = await AiCodeReviewFixture.AnalyzeAsync(_db, "other-repo.zip", AiCodeReviewFixture.Zip(("Other/Other.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"), ("Other/A.cs", "namespace Other; class A {}")));
        var service = new AiCodeReviewService(_db, new ReviewSourceEvidenceProvider(new IqrSourceStore(_db)));

        (await service.RunAsync(new AiCodeReviewRequest { CurrentSnapshotId = current.Id, BaselineSnapshotId = other.Id })).Status.Should().Be(400);
        (await service.RunAsync(new AiCodeReviewRequest { CurrentSnapshotId = current.Id, Scope = AiReviewScope.ChangedFiles })).Status.Should().Be(400);
        (await service.RunAsync(new AiCodeReviewRequest { CurrentSnapshotId = Guid.NewGuid() })).Status.Should().Be(404);
        (await service.RunAsync(new AiCodeReviewRequest { CurrentSnapshotId = current.Id, BaselineSnapshotId = current.Id })).Status.Should().Be(400);

        var outcome = await service.RunAsync(new AiCodeReviewRequest { CurrentSnapshotId = current.Id, BaselineSnapshotId = baseline.Id });
        outcome.Result.Should().NotBeNull();
        var history = await service.HistoryAsync();
        history.Should().ContainSingle(h => h.RunId == outcome.Result!.RunId && h.CurrentSnapshotId == current.Id && h.BaselineSnapshotId == baseline.Id);
        // A newer snapshot of the same repository does not re-bind the stored run.
        await AiCodeReviewFixture.AnalyzeAsync(_db, "contoso-shop.zip", AiCodeReviewFixture.Current());
        (await service.GetAsync(outcome.Result!.RunId))!.Current.SnapshotId.Should().Be(current.Id);

        var sources = await service.SourcesAsync();
        sources.Snapshots.Should().OnlyContain(s => s.HasConsumerEvidence);
    }

    [Fact]
    public void Contract_compatibility_is_classified_by_change_class_never_assumed_breaking()
    {
        static BirkNext.SourceDomains.SourceEvidenceChange Change(BirkNext.SourceDomains.ContractChangeClass cls, BirkNext.SourceDomains.SourceEvidenceChangeKind kind, string detail = "") =>
            new(BirkNext.SourceDomains.SourceEvidenceDomain.Contracts, kind, "Field", "k", "n", detail, cls);
        var c = typeof(AiCodeReviewEngine).GetNestedType("Context", System.Reflection.BindingFlags.NonPublic)!.GetMethod("Compatibility", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        BirkNext.SourceDomains.ContractCompatibilityConcern Of(BirkNext.SourceDomains.SourceEvidenceChange x) => (BirkNext.SourceDomains.ContractCompatibilityConcern)c.Invoke(null, [x])!;
        Of(Change(BirkNext.SourceDomains.ContractChangeClass.FieldAddedOptional, BirkNext.SourceDomains.SourceEvidenceChangeKind.Added)).Should().Be(BirkNext.SourceDomains.ContractCompatibilityConcern.PotentiallyCompatible);
        Of(Change(BirkNext.SourceDomains.ContractChangeClass.FieldRemoved, BirkNext.SourceDomains.SourceEvidenceChangeKind.Removed)).Should().Be(BirkNext.SourceDomains.ContractCompatibilityConcern.PotentiallyBreaking);
        Of(Change(BirkNext.SourceDomains.ContractChangeClass.RequirednessChanged, BirkNext.SourceDomains.SourceEvidenceChangeKind.Changed, "A.b: optional → required")).Should().Be(BirkNext.SourceDomains.ContractCompatibilityConcern.PotentiallyBreaking);
        Of(Change(BirkNext.SourceDomains.ContractChangeClass.RequirednessChanged, BirkNext.SourceDomains.SourceEvidenceChangeKind.Changed, "A.b: required → optional")).Should().Be(BirkNext.SourceDomains.ContractCompatibilityConcern.PotentiallyCompatible);
        Of(Change(BirkNext.SourceDomains.ContractChangeClass.TypeChanged, BirkNext.SourceDomains.SourceEvidenceChangeKind.Changed)).Should().Be(BirkNext.SourceDomains.ContractCompatibilityConcern.NeedsReview);
    }

    private static AiWorkspaceEvidence Workspace(IqrSourceSnapshot current) => new()
    {
        ProjectName = "Contoso Shop", SpecificationAvailable = true, TasksAvailable = true, ConstitutionAvailable = false, RequirementCount = 3,
        Requirements =
        [
            new("FR-001", true, ["ContosoShop/src/Shop.Api/Controllers/OrdersController.cs"], [current.Id.ToString()], true, false),
            new("FR-002", false, [], [], false, false),
            new("FR-003", true, ["ContosoShop/src/Shop.Api/Controllers/ProductsController.cs"], [Guid.NewGuid().ToString()], false, false),
        ],
        CompletedTasksWithoutEvidence = [new("T003", ["FR-002"])],
    };
}
