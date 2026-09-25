using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Post-run semantics: GraphQL counts from one canonical source each, logical issues over preserved source findings,
/// drift rows that say what was compared, and check outcomes that never compete with finding severity.
/// </summary>
public sealed class ApiReviewPostRunCleanupTests
{
    private const string Host = "m2lbdev.bufetat.no";
    private const string Origin = "https://" + Host;
    private static readonly string[] Names = ["HentAlleOperasjoner", "HentGenerelleTildelingerForRolle", "HentNødinnganger", "HentOrganisasjonstre", "HentAlleGenerelleRoller", "HentAlleBarnespesifikkeRoller"];

    private static ApiReviewTarget Target(string id, ApiReviewTargetType type, string path, string host = Host, int observedGql = 0) => new()
    {
        TargetId = id, ApiType = type, Scheme = "https", Host = host, Port = 443, BasePath = path, ServiceName = id,
        Operations = type == ApiReviewTargetType.GraphQl
            ? Names.Take(observedGql).Select(n => new ApiReviewOperation { Method = "POST", Path = path, OperationType = GraphQlOperationType.Query, OperationName = n }).ToList()
            : [new ApiReviewOperation { Method = "GET", Path = path }],
    };

    private static ApiReviewOperationResult TypenameQuery => new() { Display = "query { __typename }", Executed = true, StatusCode = 200, Result = ApiReviewCheckResult.Pass };

    private static ApiReviewTargetResult GraphQl(int observed, bool schema, int compatible = 0, int incompatible = 0)
    {
        var target = Target("Autorisasjon GraphQL", ApiReviewTargetType.GraphQl, "/api/autorisasjon/graphql", observedGql: observed);
        return new ApiReviewTargetResult
        {
            Target = target, Status = ApiReviewTargetStatus.Completed,
            Contract = new ApiReviewContractSummary { Kind = "GraphQL schema", Available = schema, IntrospectionEnabled = schema, Status = schema ? ApiReviewCheckResult.Pass : ApiReviewCheckResult.NotApplicable, Note = schema ? "" : "HTTP 400; introspection disabled or rejected." },
            Operations = schema ? [TypenameQuery]
                : [TypenameQuery, .. Names.Take(observed).Select(n => new ApiReviewOperationResult { Display = $"Query {n}", Executed = false, Result = ApiReviewCheckResult.NotTested })],
            GraphQlCompatibility = new ApiReviewGraphQlCompatibility
            {
                SchemaSource = schema ? GraphQlSchemaSource.RuntimeIntrospection : GraphQlSchemaSource.None,
                NotAssessedReason = schema ? null : "No GraphQL schema was available for validation.",
                Operations = Names.Take(observed).Select((n, i) => new GraphQlOperationCompatibilityResult
                {
                    OperationName = n, OperationType = GraphQlOperationType.Query, ObservationCount = 3,
                    Status = !schema ? GraphQlCompatibilityStatus.NotAssessed
                        : i < compatible ? GraphQlCompatibilityStatus.Compatible
                        : i < compatible + incompatible ? GraphQlCompatibilityStatus.Incompatible : GraphQlCompatibilityStatus.NotAssessed,
                    Issues = i >= compatible && i < compatible + incompatible ? [new GraphQlValidationIssue("FIELD_NOT_FOUND", "The field `navn` does not exist on the type `Rolle`.")] : [],
                }).ToList(),
            },
        };
    }

    private static ApiReviewFinding Finding(string rule, string targetId, string endpoint, ApiReviewSeverity severity = ApiReviewSeverity.Low, string? title = null, ApiReviewCheckResult result = ApiReviewCheckResult.Warning, bool legacyId = false) => new()
    {
        Id = $"{rule}-{Math.Abs(HashCode.Combine(targetId, endpoint)) % 100000}", RuleId = legacyId ? "" : rule, TargetId = targetId, Endpoint = endpoint,
        Severity = severity, Type = ApiReviewFindingType.Security, Title = title ?? rule, Check = rule, Result = result,
    };

    private static ApiReviewReport Report(IEnumerable<ApiReviewTargetResult> targets, IEnumerable<ApiReviewFinding>? findings = null, ApiReviewCoverage? coverage = null) => new()
    {
        Targets = targets.ToList(), Findings = findings?.ToList() ?? [], Coverage = coverage ?? new(),
    };

    // ── A. GraphQL counts ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SixObservedOperationsWithoutASchemaAreSixObserved_AndCompatibilityIsNotAssessed()
    {
        var counts = ApiReviewPresentation.GraphQlCounts(GraphQl(6, schema: false));

        counts.Observed.Should().Be(6);
        counts.CompatibilityAssessed.Should().BeFalse("compatibility needs a schema and none was available");
        counts.NotAssessed.Should().Be(6);
        counts.Summary.Should().Be("6 observed operations · compatibility not assessed — schema unavailable");
        counts.Summary.Should().NotContain("0 observed").And.NotContain("0 compatible");
    }

    [Fact]
    public void WithASchema_CompatibleIncompatibleAndNotAssessedAreCountedSeparately()
    {
        var counts = ApiReviewPresentation.GraphQlCounts(GraphQl(6, schema: true, compatible: 5, incompatible: 1));

        counts.CompatibilityAssessed.Should().BeTrue();
        (counts.Observed, counts.Compatible, counts.Incompatible, counts.NotAssessed).Should().Be((6, 5, 1, 0));
        counts.Summary.Should().Be("5 of 6 observed operations compatible · 1 incompatible");
    }

    [Fact]
    public void NoObservedOperationsSaysSo()
    {
        ApiReviewPresentation.GraphQlCounts(GraphQl(0, schema: false)).Summary.Should().Be("No observed operations");
    }

    [Fact]
    public void TheSafeTypenameQueryIsNeverPartOfTheObservedInventory()
    {
        var counts = ApiReviewPresentation.GraphQlCounts(GraphQl(6, schema: false));

        counts.SafeQueriesExecuted.Should().Be(1);
        counts.Observed.Should().Be(6, "one executed __typename query does not replace or add to six observed business operations");
    }

    [Fact]
    public void OverviewCompatibilityLineIsNotAssessedWhenNoServiceHadASchema_AndCountsWhenOneDid()
    {
        ApiReviewPresentation.GraphQlMatchingSummary(Report([GraphQl(6, schema: false)], coverage: new() { GraphQlOperationsObserved = 6 }))
            .Should().Be("Compatibility not assessed — schema unavailable");
        ApiReviewPresentation.GraphQlMatchingSummary(Report([GraphQl(6, schema: true, compatible: 5, incompatible: 1)], coverage: new() { GraphQlOperationsObserved = 6, GraphQlOperationsMatched = 5 }))
            .Should().Be("Compatibility: 5 compatible · 1 incompatible");
    }

    // ── B. Logical issues over preserved source findings ──────────────────────────────────────────────────────────────

    private static ApiReviewTargetResult Rest(string id, string host = Host) =>
        new() { Target = Target(id, ApiReviewTargetType.Rest, "/api/autorisasjon", host), Status = ApiReviewTargetStatus.Completed };

    private static ApiReviewTargetResult Gql(string id, string host = Host) =>
        new() { Target = Target(id, ApiReviewTargetType.GraphQl, "/api/autorisasjon/graphql", host), Status = ApiReviewTargetStatus.Completed };

    [Fact]
    public void TheSameHostLevelRuleOnRestAndGraphQlIsOneIssueWithTwoSourceObservations()
    {
        var report = Report([Rest("Autorisasjon API"), Gql("Autorisasjon GraphQL")],
            [Finding("sec-no-hsts", "Autorisasjon API", Origin, title: "HSTS header missing on API responses"),
             Finding("sec-no-hsts", "Autorisasjon GraphQL", Origin, title: "HSTS header missing on API responses")]);

        var issue = ApiReviewPresentation.LogicalIssues(report).Should().ContainSingle().Subject;
        issue.Severity.Should().Be(ApiReviewSeverity.Low);
        issue.Title.Should().Be("HSTS header missing on API responses");
        issue.Affects.Should().Equal("Autorisasjon API", "Autorisasjon GraphQL");
        issue.Sources.Should().HaveCount(2, "both source observations stay inspectable");
        report.Findings.Should().HaveCount(2, "grouping never removes source findings");
    }

    [Fact]
    public void EndpointSpecificFindingsOfOneRuleStaySeparate()
    {
        var report = Report([Rest("Autorisasjon API")],
            [Finding("rest-problem-details", "Autorisasjon API", "GET /api/autorisasjon/roller/x", ApiReviewSeverity.Medium),
             Finding("rest-problem-details", "Autorisasjon API", "GET /api/autorisasjon/tilganger/y", ApiReviewSeverity.Medium)]);

        ApiReviewPresentation.LogicalIssues(report).Should().HaveCount(2);
    }

    [Fact]
    public void TheSameRuleOnDifferentHostsStaysSeparate()
    {
        var report = Report([Rest("A", "a.example.test"), Rest("B", "b.example.test")],
            [Finding("sec-no-hsts", "A", "https://a.example.test"), Finding("sec-no-hsts", "B", "https://b.example.test")]);

        ApiReviewPresentation.LogicalIssues(report).Should().HaveCount(2);
    }

    [Fact]
    public void DifferentRulesWithTheSameTitleAreNotMerged_AndReportsWithoutRuleIdGroupByTheIdPrefix()
    {
        var report = Report([Rest("Autorisasjon API"), Gql("Autorisasjon GraphQL")],
            [Finding("sec-no-hsts", "Autorisasjon API", Origin, title: "Same words", legacyId: true),
             Finding("sec-no-hsts", "Autorisasjon GraphQL", Origin, title: "Same words", legacyId: true),
             Finding("sec-no-xcto", "Autorisasjon API", Origin, title: "Same words")]);

        var issues = ApiReviewPresentation.LogicalIssues(report);
        issues.Should().HaveCount(2, "the typed rule groups, never the display text");
        issues.Single(i => i.Key.StartsWith("sec-no-hsts|")).Sources.Should().HaveCount(2);
    }

    [Fact]
    public void TheResultSummaryNamesLogicalIssuesAndSourceFindings()
    {
        var report = Report([Rest("Autorisasjon API"), Gql("Autorisasjon GraphQL")],
            [Finding("sec-no-hsts", "Autorisasjon API", Origin), Finding("sec-no-hsts", "Autorisasjon GraphQL", Origin),
             Finding("sec-no-cache-control", "Autorisasjon API", Origin), Finding("sec-no-cache-control", "Autorisasjon GraphQL", Origin),
             Finding("rest-problem-details", "Autorisasjon API", "GET /api/autorisasjon/x", ApiReviewSeverity.Medium)]);

        var view = ApiReviewPresentation.Result(report);
        (view.IssueCount, view.FindingCount).Should().Be((3, 5));
        view.Summary.Should().StartWith("3 logical issues from 5 source findings across");
        ApiReviewPresentation.KeyIssues(report).First().Severity.Should().Be(ApiReviewSeverity.Medium, "most severe first");
    }

    // ── C. Drift rows ─────────────────────────────────────────────────────────────────────────────────────────────────

    private static ApiReviewCheck NoDrift => new() { CheckId = "drift-shape", Area = ApiReviewFindingType.Drift, Title = "No drift since previous review", Result = ApiReviewCheckResult.Pass, Detail = "0 change(s) vs 2026-09-25." };

    [Fact]
    public void TwoOperationComparisonsAreTwoRowsThatNameTheirOperation()
    {
        var rest = Rest("Autorisasjon API") with
        {
            Operations =
            [
                new() { Display = "GET /api/autorisasjon/roller/barn", Executed = true, Checks = [NoDrift] },
                new() { Display = "GET /api/autorisasjon/tilganger", Executed = true, Checks = [NoDrift] },
            ],
        };

        var rows = ApiReviewPresentation.DomainChecks(Report([rest]), [ApiReviewFindingType.Drift]);

        rows.Select(r => r.Title).Should().Equal(
            "Autorisasjon API · GET /api/autorisasjon/roller/barn: No drift since previous review",
            "Autorisasjon API · GET /api/autorisasjon/tilganger: No drift since previous review");
    }

    [Fact]
    public void AnExactDuplicateComparisonIsOneRow()
    {
        var op = new ApiReviewOperationResult { Display = "GET /api/autorisasjon/tilganger", Executed = true, Checks = [NoDrift, NoDrift] };
        ApiReviewPresentation.DomainChecks(Report([Rest("Autorisasjon API") with { Operations = [op] }]), [ApiReviewFindingType.Drift])
            .Should().ContainSingle();
    }

    [Fact]
    public void NoDriftReadsAsNoChangesDetected_NeverPassOrCoverage()
    {
        var label = ApiReviewEvidencePresentation.CheckLabel(NoDrift, new ApiReviewPolicy());
        label.Should().Be("No changes detected");
        label.Should().NotContainAny("Pass", "validated", "covered");
    }

    // ── D. Check outcome vs finding severity ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ServerHeaderDisclosureIsObserved_AndItsFindingStaysInfo()
    {
        var check = new ApiReviewCheck { CheckId = "sec-server-disclosure", Area = ApiReviewFindingType.Security, Result = ApiReviewCheckResult.Warning };
        var finding = Finding("sec-server-disclosure", "Autorisasjon API", Origin, ApiReviewSeverity.Info, "Server technology disclosed");

        ApiReviewEvidencePresentation.CheckLabel(check, new ApiReviewPolicy()).Should().Be("Disclosure observed");
        ApiReviewStatusLabels.FindingCheckLabel(finding).Should().Be("Observed");
        finding.Severity.Should().Be(ApiReviewSeverity.Info);
        ApiReviewStatusLabels.FindingCheckLabel(finding).Should().NotBe("Warning", "a severity-like word beside Info read as a contradiction");
    }

    [Fact]
    public void MissingHstsIsAnIssueDetected_AndItsFindingStaysLow()
    {
        var check = new ApiReviewCheck { CheckId = "sec-hsts", Area = ApiReviewFindingType.Security, Result = ApiReviewCheckResult.Warning };
        var finding = Finding("sec-no-hsts", "Autorisasjon API", Origin, ApiReviewSeverity.Low);

        ApiReviewEvidencePresentation.CheckLabel(check, new ApiReviewPolicy()).Should().Be("Issue detected");
        ApiReviewStatusLabels.FindingCheckLabel(finding).Should().Be("Issue detected");
        finding.Severity.Should().Be(ApiReviewSeverity.Low);
    }

    [Fact]
    public void ThresholdAndExpectedBehaviourPassesAndNoIndicatorsObservedAreUnchanged()
    {
        var policy = new ApiReviewPolicy();
        ApiReviewEvidencePresentation.CheckLabel(new() { CheckId = "rest-latency", Area = ApiReviewFindingType.Performance, Result = ApiReviewCheckResult.Pass }, policy).Should().Be("Pass");
        ApiReviewEvidencePresentation.CheckLabel(new() { CheckId = "rest-latency", Area = ApiReviewFindingType.Performance, Result = ApiReviewCheckResult.Warning }, policy).Should().Be("Warning", "a threshold warning keeps its real meaning");
        ApiReviewEvidencePresentation.CheckLabel(new() { CheckId = "rest-unknown-route", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.Pass }, policy).Should().Be("Pass");
        ApiReviewEvidencePresentation.CheckLabel(new() { CheckId = "errors-leak", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.Pass }, policy).Should().Be("No indicators observed");
    }

    [Fact]
    public void ManualReviewAndFailedChecksBehindFindingsKeepTheirMeaning()
    {
        ApiReviewStatusLabels.FindingCheckLabel(Finding("gql-observed-mutation", "x", "e", ApiReviewSeverity.Info, result: ApiReviewCheckResult.ManualReview)).Should().Be("Manual review");
        ApiReviewStatusLabels.FindingCheckLabel(Finding("sec-no-tls", "x", "e", ApiReviewSeverity.High, result: ApiReviewCheckResult.Fail)).Should().Be("Check failed");
    }

    // ── Export ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheExportCarriesTheSameCountsIssuesAndLabels()
    {
        var report = Report([Rest("Autorisasjon API"), GraphQl(6, schema: false)],
            [Finding("sec-no-hsts", "Autorisasjon API", Origin, title: "HSTS header missing on API responses"),
             Finding("sec-no-hsts", "Autorisasjon GraphQL", Origin, title: "HSTS header missing on API responses"),
             Finding("sec-server-disclosure", "Autorisasjon API", Origin, ApiReviewSeverity.Info, "Server technology disclosed")],
            new() { GraphQlOperationsObserved = 6 });

        var html = new ReportExportService().ExportApiReview(report, "BirkNext");

        html.Should().Contain("GraphQL observed operations").And.Contain("Not assessed")
            .And.Contain("6 observed operations · compatibility not assessed — schema unavailable")
            .And.Contain("2 logical issues from 3 source findings").And.Contain("Source findings")
            .And.Contain("Issue detected").And.Contain("Observed");
        html.Should().NotContain("0 / 6").And.NotContain("GraphQL operations matched");
    }
}
