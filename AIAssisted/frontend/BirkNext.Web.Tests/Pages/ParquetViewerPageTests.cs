using BirkNext.ExecutionEvidence;
using BirkNext.Parquet;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Parquet Quality Review page: compact source selection, contract discovery and confirmation, generated rules with review/approval, requirement
/// coverage and separate quality dimensions, rule drill-down into the data grid (typed filter chips, no unlabelled "and" field, paging of full-scan
/// matches), a type-specific rule editor with preview, exact profile with the contract aggregate, baseline comparison, a searchable run picker,
/// saved evidence, history and report. Synthetic fixtures; the page never computes figures itself.
/// </summary>
public sealed class ParquetViewerPageTests : BunitContext
{
    private static readonly Guid Session = Guid.Parse("aaaaaaaa-1111-0000-0000-000000000001");
    private static readonly Guid Baseline = Guid.Parse("bbbbbbbb-1111-0000-0000-000000000002");
    private static readonly Guid Snapshot = Guid.Parse("cccccccc-1111-0000-0000-000000000003");
    private static readonly Guid Run = Guid.Parse("dddddddd-1111-0000-0000-000000000004");
    private readonly Mock<IParquetApiService> _api = new();
    private readonly Mock<IExecutionEvidenceApiService> _runs = new();
    private ParquetQualityRequest? _lastReview;
    private ParquetRowsRequest? _lastRows;

    private static readonly DatasetContract Contract = new()
    {
        Key = "skolenaervaer-dekning", Title = "Coverage breakdown", Version = "1.0.0-draft", Governance = ContractGovernance.Draft, GovernanceReason = "Contract version 1.0.0-draft is a draft.",
        DocumentPath = "SkoleService/specs/001-skoleservice/contracts/bufdata-leveranse.md", DocumentSha256 = new string('d', 64), Container = "skolenaervaer-dekning",
        Columns = [new() { Name = "period" }, new() { Name = "category" }, new() { Name = "measure" }, new() { Name = "reason_code", Nullable = true }, new() { Name = "count" }],
    };

    private static readonly ContractCandidate Candidate = new()
    {
        Source = new("m2lb", "M2LB", Snapshot, DateTimeOffset.UtcNow, "M2LB.zip"), Contract = Contract, Match = ContractMatchKind.CandidateMapping, Confidence = "Low",
        Reasons = ["File name contains the storage container name skolenaervaer-dekning (a hint only).", "Not explained by the source: category, measure, reason_code, count."],
        DocumentedMappings = [new("periode", "period", "Documented")], UnmappedExpected = ["category", "measure", "reason_code", "count"], ExpectedColumns = 5, MappedColumns = 1,
    };

    private static readonly List<ParquetRule> Generated =
    [
        new() { Id = "skolenaervaer-dekning.category.values", Kind = ParquetRuleKind.AllowedValues, Column = "category", Values = ["TOTAL", "FOSTERHJEM", "PRIVAT_INSTITUSJON"], Origin = RuleOrigin.ContractDerived, Severity = "High", Provenance = "bufdata-leveranse.md:L71", ContractVersion = "1.0.0-draft" },
        new() { Id = "skolenaervaer-dekning.L74.sum", Kind = ParquetRuleKind.SumIdentity, Column = "measure", Values = ["RECEIVED", "REJECTED_ONLY", "MISSING"], Expected = "EXPECTED", ValueColumn = "count", GroupColumns = ["category"], Origin = RuleOrigin.ContractDerived, Review = RuleReviewState.NeedsReview, Severity = "High", Semantics = "RECEIVED + REJECTED_ONLY + MISSING = EXPECTED per category." },
    ];

    public ParquetViewerPageTests()
    {
        _api.Setup(a => a.OpenAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, Stream _, CancellationToken _) => (name.Contains("baseline") ? Overview(Baseline, name) : Overview(Session, name), null));
        _api.Setup(a => a.DiscoverAsync(Session, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new ContractDiscovery
        {
            SessionId = Session, Outcome = ContractMatchKind.CandidateMapping, Candidates = [Candidate], Projects = ["m2lb"],
            Explanation = "skolenaervaer-dekning 1.0.0-draft is a candidate (not proven). Confirm it before validating.",
        }, null));
        _api.Setup(a => a.AssociateAsync(Session, It.IsAny<ContractAssociation>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, ContractAssociation a, CancellationToken _) => (a with { Match = ContractMatchKind.CandidateMapping, Confidence = "Low", Reasons = Candidate.Reasons,
                ColumnMappings = [new("periode", "period", "Documented"), .. a.ColumnMappings.Where(m => m.Provenance == "Declared")] }, null));
        _api.Setup(a => a.GenerateAsync(Session, It.IsAny<ContractAssociation?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, ContractAssociation? a, CancellationToken _) => (a is null
                ? new RuleGeneration { RuleSetName = "Schema", Rules = [new() { Id = "schema.id.exists", Kind = ParquetRuleKind.ColumnExists, Column = "id", Origin = RuleOrigin.SchemaDerived }] }
                : new RuleGeneration { RuleSetName = "Coverage 1.0.0-draft", Rules = Generated, Association = a }, null));
        _api.Setup(a => a.ReviewAsync(Session, It.IsAny<ParquetQualityRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, ParquetQualityRequest r, CancellationToken _) => { _lastReview = r; return (Quality(r), null); });
        _api.Setup(a => a.RowsAsync(Session, It.IsAny<ParquetRowsRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, ParquetRowsRequest r, CancellationToken _) =>
            {
                _lastRows = r;
                return (new ParquetRowsResult
                {
                    Columns = ["id", "name"], Rows = [["1", "Synthetic child 1"], ["17", null]], RowNumbers = [1 + r.Offset, 17 + r.Offset], TotalRows = 1000, ScannedRows = r.FullScan ? 1000 : 50, MatchingRows = r.Filters.Count > 0 ? 120 : null,
                    Scope = r.FullScan ? "Full scan of all 1,000 rows: 120 match" : "Rows 1–50 of 1000", Truncated = r.FullScan,
                }, null);
            });
        _api.Setup(a => a.ValidateAsync(Session, It.IsAny<ParquetRuleSet>(), It.IsAny<CancellationToken>())).ReturnsAsync(((ParquetValidation?)new ParquetValidation("preview", 1,
            [new ParquetRuleResult("own.1-unique", ParquetRuleKind.Unique, null, ParquetRuleOutcome.Fail, "unique combinations", "1 row(s) do not", "Full scan of all 1,000 rows", 1000, [44], "Row numbers…")], 1000, "Full scan"), (string?)null));
        _api.Setup(a => a.ApproveRulesAsync(Session, It.IsAny<ParquetApprovalRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((new ParquetRuleSetVersion { Version = 1 }, null));
        _api.Setup(a => a.BaselineAsync(It.IsAny<ParquetBaselineRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((new ParquetBaselineResult(new BaselineAssessment
        {
            BaselineLabel = "baseline.parquet", BaselineKind = "Local file", Comparable = true, Overall = BaselineClass.Regression, Checks = ["Same dataset contract skolenaervaer-dekning."],
            Findings = [new("Rule", "RECEIVED + REJECTED_ONLY + MISSING = EXPECTED", "Pass", "Fail", BaselineClass.Regression, "1 of 3 group(s) inconsistent."), new("Aggregate", "TOTAL · EXPECTED", "10", "11", BaselineClass.Inconclusive, "Changed business aggregate.")],
        }, new ParquetComparison { KeyColumns = ["category", "measure"], RecordCounts = new() { ["Added"] = 0, ["Removed"] = 0, ["Changed"] = 2, ["Unchanged"] = 13, ["Ambiguous"] = 0 }, Records = [new("FOSTERHJEM | EXPECTED", ParquetRecordChange.Changed, ["count"])] }), null));
        _api.Setup(a => a.EvidenceAsync("dev", It.IsAny<CancellationToken>())).ReturnsAsync(([Saved()], null));
        _api.Setup(a => a.EvidenceDetailAsync("dev", It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Saved(), null));
        _api.Setup(a => a.SaveEvidenceAsync(It.IsAny<ParquetEvidenceRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync((Saved(), null));
        _api.Setup(a => a.ReportAsync("dev", It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(("# report"u8.ToArray(), null));
        _runs.Setup(r => r.RunsAsync(It.IsAny<ExecutionRunFilter?>(), It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<ExecutionRunSummary>?)[new ExecutionRunSummary
        { RunId = Run, Name = "Bufdata export (synthetic)", EnvironmentId = "dev", State = ExecutionState.Passed, StartedAt = DateTimeOffset.UtcNow }], (string?)null));
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev", EnvironmentType = FrontendEnvironmentType.Development } });
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(_runs.Object);
        Services.AddSingleton(context.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ParquetQualityResult Quality(ParquetQualityRequest r)
    {
        var active = r.Rules.Rules.Where(x => x.Review == RuleReviewState.Active).ToList();
        var contract = r.Association is not null;
        return new ParquetQualityResult
        {
            SessionId = Session, Association = r.Association,
            Schema = contract ? new ParquetSchemaCheck(SchemaCompatibility.Incompatible, [new("category", null, SchemaFindingKind.Missing, "UTF8 · not null", "absent", "High", "A contract column is missing from the file.")], "Incompatible: 4 missing.")
                : new ParquetSchemaCheck(SchemaCompatibility.NotAssessed, [], "No contract selected."),
            Validation = active.Count == 0 ? null : new ParquetValidation(r.Rules.Name, 1, [.. active.Select(x => new ParquetRuleResult(x.Id, x.Kind, x.Column,
                x.Kind == ParquetRuleKind.SumIdentity ? ParquetRuleOutcome.Fail : ParquetRuleOutcome.Pass, "expected", x.Kind == ParquetRuleKind.SumIdentity ? "1 of 3 group(s) inconsistent" : "all rows comply", "Full scan of all 1,000 rows", 1000, [], "Every row was examined.")
                {
                    AffectedRows = x.Kind == ParquetRuleKind.SumIdentity ? 1 : 0, Details = x.Kind == ParquetRuleKind.SumIdentity ? ["category=FOSTERHJEM: RECEIVED + REJECTED_ONLY + MISSING = 5, EXPECTED = 6"] : [],
                    DrillFilters = x.Kind == ParquetRuleKind.SumIdentity ? [new ParquetFilter("kategori", ParquetFilterOperator.In, null, null, ["FOSTERHJEM"])] : [],
                })], 1000, "Full scan of all 1,000 rows"),
            Coverage = contract ? [new() { RequirementId = "skolenaervaer-dekning@1.0.0-draft#L74", Category = "Aggregate consistency", Text = "Rules: RECEIVED + REJECTED_ONLY + MISSING = EXPECTED", Status = active.Any(x => x.Kind == ParquetRuleKind.SumIdentity) ? RequirementStatus.CoveredFail : RequirementStatus.NotAssessed, Reason = "awaiting review", Review = "Needs review" },
                new() { RequirementId = "skolenaervaer-dekning@1.0.0-draft#L72", Category = "Presentation", Text = "Never a single percentage.", Status = RequirementStatus.NotSupported, Reason = "Not a property of a single value." }] : [],
            Dimensions = [new("File readable", QualityState.Pass, "All 3 columns decode."), new("Schema compatible", contract ? QualityState.Fail : QualityState.NotAssessed, contract ? "Incompatible: 4 missing." : "No contract selected."),
                new("Data valid", QualityState.Pass, "1 passed."), new("Business rules verified", QualityState.NotAssessed, "1 rule(s) await review; nothing was run."), new("E2E evidence verified", QualityState.NotAssessed, "No execution run is correlated with this file.")],
            Statistics = new ParquetStatistics { Rows = 1000, DuplicateRows = 3, Mode = EvaluationMode.Exact, Scope = "Full scan of all 1,000 rows (2 readable column(s))",
                Columns = [new() { Column = "status", Rows = 1000, Nulls = 100, Distinct = 2, Mode = EvaluationMode.Exact, Categories = [new("ACTIVE", 600), new("MOVED", 300)] }],
                PivotRowColumn = "category", PivotColumnColumn = "measure", PivotValueColumn = "count", Pivot = [new("TOTAL", "EXPECTED", 10), new("FOSTERHJEM", "EXPECTED", 6)] },
            NextAction = contract ? (r.Association!.ReviewStatus == "Confirmed" ? "Review findings" : "Confirm contract") : "Select contract",
        };
    }

    private static ParquetInspectionEvidence Saved() => new()
    {
        Id = Guid.Parse("eeeeeeee-1111-0000-0000-000000000005"), EnvironmentId = "dev", FileName = "skolenaervaer-dekning-example.parquet", Sha256 = new string('e', 64), Rows = 15, SchemaFingerprint = "0123456789abcdef",
        SavedAt = DateTimeOffset.UtcNow, SavedBy = "tester", DocumentVersion = 2, Association = new() { ContractKey = "skolenaervaer-dekning", ContractVersion = "1.0.0-draft" },
        Dimensions = [new("File readable", QualityState.Pass, "ok"), new("E2E evidence verified", QualityState.NotAssessed, "Manual, unverified association: it does not prove the run produced this file.")],
        Coverage = [new() { RequirementId = "x#L74", Text = "Rules: …", Status = RequirementStatus.CoveredFail, Reason = "1 group" }],
        Correlation = new() { RunId = Run, Level = CorrelationLevel.ManualUnverified, Explanation = "Manual, unverified association: it does not prove the run produced this file." },
    };

    private static ParquetOverview Overview(Guid id, string name) => new()
    {
        SessionId = id, FileName = name, SizeBytes = 39103, Sha256 = new string('c', 64), Rows = 1000, CreatedBy = "independent writer", SchemaFingerprint = "abcdef0123456789",
        Columns =
        [
            new("id", "INT64", null, false, 0, ParquetFigure.FromMetadata, "1", "1000", ParquetFigure.FromMetadata, ["SNAPPY"], ["PLAIN", "RLE"], 100, 200, true, null),
            new("name", "BYTE_ARRAY", "STRING", true, 58, ParquetFigure.FromMetadata, null, null, ParquetFigure.NotAvailable, ["SNAPPY"], ["PLAIN"], 100, 200, true, null),
            new("tags", "BYTE_ARRAY", "STRING", true, null, ParquetFigure.NotAvailable, null, null, ParquetFigure.NotAvailable, ["SNAPPY"], ["PLAIN"], 0, 0, false, "Repeated values (lists, maps) are not read by this explorer; the schema is shown."),
        ],
        Schema = new ParquetSchemaNode { Children = [new() { Path = "id", Name = "id", PhysicalType = "INT64", Repetition = "REQUIRED", Readable = true }] },
        RowGroups = [new(0, 400, 1, 1), new(1, 400, 1, 1), new(2, 200, 1, 1)], Limitations = ["Statistics shown on Overview come from the file's metadata."],
        OpenedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
    };

    private static InputFileContent File(string name) => InputFileContent.CreateFromBinary([0x50, 0x41, 0x52, 0x31], name);

    private IRenderedComponent<ParquetViewer> OpenPage()
    {
        var cut = Render<ParquetViewer>();
        cut.Find("[data-testid=pq-empty]");
        cut.FindAll("[data-testid^=pq-tab-]").Should().BeEmpty("no work area before a file is open");
        cut.FindComponent<InputFile>().UploadFiles(File("skolenaervaer-dekning-example.parquet"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-overview]"));
        return cut;
    }

    private IRenderedComponent<ParquetViewer> OpenWithContract()
    {
        var cut = OpenPage();
        cut.Find("[data-testid=pq-candidate-select]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-contract-key]").TextContent.Should().Be("skolenaervaer-dekning"));
        return cut;
    }

    [Fact]
    public void Opening_shows_a_compact_source_bar_four_areas_and_separate_quality_results()
    {
        var cut = OpenPage();
        cut.Find("[data-testid=pq-name]").TextContent.Should().Be("skolenaervaer-dekning-example.parquet");
        cut.Find("[data-testid=pq-rows]").TextContent.Should().Be("1,000");
        cut.Find("[data-testid=pq-contract-chip]").TextContent.Should().Contain("No contract selected");
        cut.FindAll("[role=tab]").Select(t => t.TextContent).Should().Equal("Quality Overview", "Validation", "Explore & Compare", "Evidence & History");
        cut.FindAll("[data-testid=pq-file-compare]").Should().BeEmpty("the baseline is chosen in Compare, not before the explorer");
        cut.FindAll("[data-testid=pq-dimension]").Select(d => d.GetAttribute("data-state")).Should().Equal("Pass", "NotAssessed", "Pass", "NotAssessed", "NotAssessed");
        cut.Find("[data-testid=pq-candidate]").GetAttribute("data-match").Should().Be("CandidateMapping");
        cut.Find("[data-testid=pq-candidate]").TextContent.Should().Contain("a hint only").And.Contain("Not explained by the source");
        cut.Find("[data-testid=pq-next-action]").TextContent.Should().Be("Select contract");
    }

    [Fact]
    public void Selecting_a_contract_shows_governance_mappings_and_requires_confirmation()
    {
        var cut = OpenWithContract();
        cut.Find("[data-testid=pq-governance]").TextContent.Should().Be("Draft");
        cut.Find("[data-testid=pq-mappings]").TextContent.Should().Contain("periode → period").And.Contain("documented");
        cut.Find("[data-testid=pq-contract-chip]").TextContent.Should().Contain("proposed");
        cut.Find("[data-testid=pq-next-action]").TextContent.Should().Be("Confirm contract");
        cut.FindAll("[data-testid=pq-dimension]")[1].GetAttribute("data-state").Should().Be("Fail");
        // Declaring a mapping goes through the server, labelled as declared.
        cut.Find("[data-testid=pq-declare-category]").Change("name");
        cut.Find("[data-testid=pq-declare-apply]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-mappings]").TextContent.Should().Contain("name → category").And.Contain("declared"));
        cut.Find("[data-testid=pq-confirm]").Click();
        cut.WaitForAssertion(() => _lastReview!.Association!.ReviewStatus.Should().Be("Confirmed"));
        cut.Find("[data-testid=pq-contract-chip]").TextContent.Should().Contain("confirmed");
    }

    [Fact]
    public void Generated_business_rules_wait_for_approval_and_failures_drill_into_the_data()
    {
        var cut = OpenWithContract();
        cut.Find("[data-testid=pq-tab-validation]").Click();
        var rules = cut.FindAll("[data-testid=pq-rule]");
        rules.Select(r => r.GetAttribute("data-review")).Should().Equal("Active", "NeedsReview");
        rules[1].GetAttribute("data-outcome").Should().Be("NotRun", "an unapproved business rule is not run");
        _lastReview!.Rules.Rules.Single(r => r.Kind == ParquetRuleKind.SumIdentity).Review.Should().Be(RuleReviewState.NeedsReview);
        cut.Find("[data-testid=pq-review-panel]").TextContent.Should().Contain("1 generated business rule(s) await review");
        cut.Find("[data-testid=pq-rule-select]").Change(true);
        // After approval the generator returns the rule as an approved business rule.
        _api.Setup(a => a.GenerateAsync(Session, It.IsAny<ContractAssociation?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new RuleGeneration { RuleSetName = "Coverage", Rules = [Generated[0], Generated[1] with { Review = RuleReviewState.Active, Origin = RuleOrigin.ApprovedBusinessRule }] }, null));
        cut.Find("[data-testid=pq-approve]").Click();
        _api.Verify(a => a.ApproveRulesAsync(Session, It.Is<ParquetApprovalRequest>(r => r.Approval.Rules.Single(x => x.Kind == ParquetRuleKind.SumIdentity).Review == RuleReviewState.Active
            && r.Association.ContractKey == "skolenaervaer-dekning"), It.IsAny<CancellationToken>()), Times.Once);
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=pq-rule]")[1].GetAttribute("data-outcome").Should().Be("Fail"));
        cut.FindAll("[data-testid=pq-rule-open]")[1].Click();
        cut.Find("[data-testid=pq-rule-detail]").TextContent.Should().Contain("1 of 3 group(s) inconsistent").And.Contain("values stay in the file");
        cut.Find("[data-testid=pq-rule-groups]").TextContent.Should().Contain("category=FOSTERHJEM");
        cut.Find("[data-testid=pq-drill]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-data]"));
        _lastRows!.FullScan.Should().BeTrue();
        _lastRows.Filters.Should().ContainSingle(f => f.Column == "kategori" && f.Operator == ParquetFilterOperator.In);
        cut.Find("[data-testid=pq-drill-source]").TextContent.Should().Contain("RECEIVED + REJECTED_ONLY + MISSING");
        cut.Find("[data-testid=pq-filter-chip]").TextContent.Should().Contain("kategori is one of FOSTERHJEM");
        cut.Find("[data-testid=pq-scope]").TextContent.Should().Contain("Full scan").And.Contain("120 matching");
        cut.Find("[data-testid=pq-next]").HasAttribute("disabled").Should().BeFalse("full-scan matches can be paged");
        cut.Find("[data-testid=pq-next]").Click();
        cut.WaitForAssertion(() => _lastRows!.Offset.Should().Be(50));
    }

    [Fact]
    public void Requirements_and_schema_checks_show_expected_actual_and_status()
    {
        var cut = OpenWithContract();
        cut.Find("[data-testid=pq-tab-validation]").Click();
        cut.Find("[data-testid=pq-view-requirements]").Click();
        cut.FindAll("[data-testid=pq-requirement]").Select(r => r.GetAttribute("data-status")).Should().Equal("NotAssessed", "NotSupported");
        cut.Find("[data-testid=pq-coverage]").TextContent.Should().Contain("Never a single percentage");
        cut.Find("[data-testid=pq-view-schema]").Click();
        cut.Find("[data-testid=pq-schema-summary]").TextContent.Should().Contain("Incompatible");
        cut.Find("[data-testid=pq-schema-finding]").GetAttribute("data-kind").Should().Be("Missing");
    }

    [Fact]
    public void The_rule_editor_shows_only_the_fields_of_the_rule_type_and_previews_before_adding()
    {
        var cut = OpenPage();
        cut.Find("[data-testid=pq-tab-validation]").Click();
        cut.Find("[data-testid=pq-rule-new]").Click();
        cut.Find("[data-testid=pq-rule-kind]").Change(ParquetRuleKind.Unique.ToString());
        cut.FindAll("[data-testid=pq-rule-values]").Should().BeEmpty();
        cut.FindAll("[data-testid=pq-rule-max]").Should().BeEmpty();
        cut.Find("[data-testid=pq-rule-preview-run]").Click();
        cut.Find("[data-testid=pq-rule-error]").TextContent.Should().Contain("one or more columns");
        cut.FindAll("[data-testid=pq-editor] input[type=checkbox]")[0].Change(true);
        cut.FindAll("[data-testid=pq-editor] input[type=checkbox]")[1].Change(true);
        cut.Find("[data-testid=pq-rule-preview-run]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-rule-preview]").TextContent.Should().Contain("Fail").And.Contain("1 row(s) do not"));
        _api.Verify(a => a.ValidateAsync(Session, It.Is<ParquetRuleSet>(s => s.Rules.Count == 1 && s.Rules[0].Columns.SequenceEqual(new[] { "id", "name" })), It.IsAny<CancellationToken>()));
        cut.Find("[data-testid=pq-rule-add]").Click();
        cut.FindAll("[data-testid=pq-rule]").Should().Contain(r => r.GetAttribute("data-origin") == "UserAuthored" && r.TextContent.Contains("Your rule"));
        cut.Find("[data-testid=pq-rule-new]").Click();
        cut.Find("[data-testid=pq-rule-kind]").Change(ParquetRuleKind.AllowedValues.ToString());
        cut.Find("[data-testid=pq-rule-values]");
        cut.FindAll("[data-testid=pq-editor] input[type=checkbox]").Should().BeEmpty();
    }

    [Fact]
    public void Data_filters_are_typed_chips_with_explicit_scope_and_no_unlabelled_field()
    {
        var cut = OpenPage();
        cut.Find("[data-testid=pq-tab-explore]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-grid]"));
        cut.Find("[data-testid=pq-scope]").TextContent.Should().Be("Rows 1–50 of 1000");
        cut.Find("[data-testid=pq-filter-column]").Change("id");
        cut.Find("[data-testid=pq-filter-operator]").Change(ParquetFilterOperator.Between.ToString());
        cut.Find("[data-testid=pq-filter]").TextContent.Should().Contain("From").And.Contain("To").And.NotContain(" and ");
        cut.Find("[data-testid=pq-filter-value]").Change("10");
        cut.Find("[data-testid=pq-filter-value2]").Change("20");
        cut.Find("[data-testid=pq-filter-apply]").Click();
        cut.WaitForAssertion(() => _lastRows!.Filters.Should().ContainSingle(f => f.Operator == ParquetFilterOperator.Between && f.Value == "10" && f.Value2 == "20"));
        _lastRows!.FullScan.Should().BeFalse("filters apply to the page until the tester chooses the whole file");
        cut.Find("[data-testid=pq-filter-chip]").TextContent.Should().Contain("id between 10 and 20");
        cut.Find("[data-testid=pq-filter-column]").Change("name");
        cut.FindAll("[data-testid=pq-filter-operator] option").Select(o => o.TextContent).Should().NotContain("between", "text columns get text conditions");
        cut.Find("[data-testid=pq-filter-full]").Change(true);
        cut.WaitForAssertion(() => _lastRows!.FullScan.Should().BeTrue());
        cut.FindAll("[data-testid=pq-row]")[1].QuerySelector("button")!.Click();
        cut.Find("[data-testid=pq-row-detail]").TextContent.Should().Contain("null");
        cut.Find("[data-testid=pq-view-schema]").Click();
        var columns = cut.FindAll("[data-testid=pq-column]");
        columns[2].GetAttribute("data-readable").Should().Be("false");
        columns[2].TextContent.Should().Contain("Repeated values");
        cut.Find("[data-testid=pq-view-profile]").Click();
        cut.Find("[data-testid=pq-profile-scope]").TextContent.Should().Contain("EXACT").And.Contain("duplicate rows: 3");
        cut.Find("[data-testid=pq-pivot]").TextContent.Should().Contain("TOTAL").And.Contain("FOSTERHJEM");
        cut.Find("[data-testid=pq-profile-row]").TextContent.Should().Contain("10%").And.Contain("ACTIVE");
    }

    [Fact]
    public void Baseline_comparison_is_chosen_in_compare_and_classified()
    {
        var cut = OpenWithContract();
        cut.Find("[data-testid=pq-tab-explore]").Click();
        cut.Find("[data-testid=pq-view-compare]").Click();
        cut.Find("[data-testid=pq-compare-empty]");
        cut.Find("[data-testid=pq-compare-run]").HasAttribute("disabled").Should().BeTrue();
        cut.FindComponents<InputFile>().Last().UploadFiles(File("baseline.parquet"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-compare-direction]").TextContent.Should().Contain("baseline.parquet"));
        cut.FindAll("[data-testid=pq-compare-key]")[0].Change(true);
        cut.Find("[data-testid=pq-compare-run]").Click();
        _api.Verify(a => a.BaselineAsync(It.Is<ParquetBaselineRequest>(r => r.BaselineSessionId == Baseline && r.CurrentSessionId == Session && r.KeyColumns.SequenceEqual(new[] { "id" })
            && r.Quality!.Association!.ContractKey == "skolenaervaer-dekning"), It.IsAny<CancellationToken>()));
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-baseline-overall]").TextContent.Should().Contain("Regression"));
        cut.FindAll("[data-testid=pq-baseline-finding]").Select(f => f.GetAttribute("data-class")).Should().Equal("Regression", "Inconclusive");
        cut.Find("[data-testid=pq-records-changed]").TextContent.Should().Contain("2");
    }

    [Fact]
    public void Evidence_uses_a_searchable_run_picker_and_history_reopens_without_the_file()
    {
        var cut = OpenWithContract();
        cut.Find("[data-testid=pq-tab-evidence]").Click();
        cut.Find("[data-testid=pq-run-search]").Change("Bufdata");
        cut.Find("[data-testid=pq-run-find]").Click();
        _runs.Verify(r => r.RunsAsync(It.Is<ExecutionRunFilter?>(f => f!.EnvironmentId == "dev" && f.Search == "Bufdata"), It.IsAny<CancellationToken>()));
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-runs]").TextContent.Should().Contain("Bufdata export (synthetic)"));
        cut.Find("[data-testid=pq-run]").Change(true);
        cut.Find("[data-testid=pq-manual]").Change(true);
        cut.Find("[data-testid=pq-manual-reason]").Change("exported by hand");
        cut.Find("[data-testid=pq-evidence-save]").Click();
        _api.Verify(a => a.SaveEvidenceAsync(It.Is<ParquetEvidenceRequest>(r => r.LinkedRunId == Run && r.ManualLink && r.ManualReason == "exported by hand" && r.EnvironmentId == "dev"
            && r.Association!.ContractKey == "skolenaervaer-dekning" && r.Rules!.Rules.Count == 2), It.IsAny<CancellationToken>()));
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-history-detail]"));
        cut.Find("[data-testid=pq-history-correlation]").TextContent.Should().Contain("Manual unverified").And.Contain("does not prove");
        cut.Find("[data-testid=pq-evidence-row]").TextContent.Should().Contain("1 failed");
        cut.Find("[data-testid=pq-evidence-report]").Click();
        _api.Verify(a => a.ReportAsync("dev", It.IsAny<Guid>(), It.IsAny<CancellationToken>()));
        JSInterop.VerifyInvoke("downloadBase64File");
    }

    [Fact]
    public void History_is_available_without_an_open_file_and_never_claims_the_file_is_still_there()
    {
        var cut = Render<ParquetViewer>();
        cut.Find("[data-testid=pq-open-history]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-evidence-list]"));
        cut.FindAll("[data-testid=pq-save]").Should().BeEmpty("nothing to save without an open file");
        cut.Find("[data-testid=pq-evidence-open]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=pq-history-file]").TextContent.Should().Contain("not available here").And.Contain("cannot be re-run"));
    }
}
