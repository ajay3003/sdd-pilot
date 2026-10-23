using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Task 10 §80–§92: the decision-first post-run of API Quality Review over a report shaped like the current Development fixture —
/// one REST service without an OpenAPI contract, one GraphQL service whose runtime schema was rejected (only __typename executed),
/// authentication enforcement verified, three manual obligations, no previous baseline.
/// </summary>
public sealed class ApiReviewPostRunPresentationTests
{
    private const string Host = "api.example.invalid";

    private static ApiReviewTarget Target(string id, ApiReviewTargetType type, string path, string name) => new()
    {
        TargetId = id, ApiType = type, Scheme = "https", Host = Host, Port = 443, BasePath = path, ServiceName = name, AuthRequired = true,
        Source = ApiReviewTargetSource.Configured, Confidence = ObservedEndpointConfidence.Verified, Selected = true,
    };

    private static ApiReviewFinding Finding(string id, string rule, string target, ApiReviewSeverity severity, ApiReviewFindingType type, string title, string endpoint = $"https://{Host}") => new()
    {
        Id = id, RuleId = rule, TargetId = target, Severity = severity, Type = type, Endpoint = endpoint, Check = title, Title = title,
        Description = title, Recommendation = $"Fix: {title}", Result = ApiReviewCheckResult.Warning,
    };

    private static ApiReviewReport Fixture(bool findings = true, bool baseline = false)
    {
        var rest = new ApiReviewTargetResult
        {
            Target = Target("rest-1", ApiReviewTargetType.Rest, "/api/autorisasjon", "Autorisasjon API"),
            AccessMode = ApiReviewAccessMode.AuthenticatedHttp, Status = ApiReviewTargetStatus.Completed, FindingCount = findings ? 4 : 0,
            Operations =
            [
                new() { Display = "GET /api/autorisasjon/roller", Method = "GET", Path = "/api/autorisasjon/roller", Executed = true, StatusCode = 200, ElapsedMs = 142,
                    Checks = [new() { CheckId = "rest-latency", Area = ApiReviewFindingType.Performance, Result = ApiReviewCheckResult.Pass, Detail = "142 ms (warning > 800 ms)" }] },
                new() { Display = "GET /api/autorisasjon/brukere", Method = "GET", Path = "/api/autorisasjon/brukere", Executed = true, StatusCode = 200, ElapsedMs = 210 },
                new() { Display = "POST /api/autorisasjon/brukere", Method = "POST", Path = "/api/autorisasjon/brukere", Executed = false, Result = ApiReviewCheckResult.NotTested },
            ],
            Checks =
            [
                new() { CheckId = "sec-hsts", Area = ApiReviewFindingType.Security, Result = ApiReviewCheckResult.Warning, Title = "HSTS" },
                new() { CheckId = "sec-server-disclosure", Area = ApiReviewFindingType.Security, Result = ApiReviewCheckResult.Warning, Title = "Server technology disclosure" },
                new() { CheckId = "errors-unknown-route", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.Pass, Title = "Unknown route returns 404" },
                new() { CheckId = "errors-leak", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.Pass, Title = "Internal details" },
                new() { CheckId = "errors-method", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.NotTested, Title = "Unsupported method", Detail = "Not tested: write methods are never sent." },
                .. baseline ? new[] { new ApiReviewCheck { CheckId = "drift-shape", Area = ApiReviewFindingType.Drift, Result = ApiReviewCheckResult.Pass, Title = "Response shape drift" } } : [],
            ],
            AuthenticationEnforcement = new() { Status = ApiAuthenticationEnforcementStatus.Verified, Probe = "GET /api/autorisasjon/roller", StatusCode = 401, Reason = "Refused without a credential." },
        };
        var gql = new ApiReviewTargetResult
        {
            Target = Target("gql-1", ApiReviewTargetType.GraphQl, "/api/autorisasjon/graphql", "Autorisasjon GraphQL"),
            AccessMode = ApiReviewAccessMode.AuthenticatedHttp, Status = ApiReviewTargetStatus.Completed, FindingCount = findings ? 2 : 0,
            Operations = [new() { Display = "query { __typename }", Method = "POST", Path = "/api/autorisasjon/graphql", Executed = true, StatusCode = 200, ElapsedMs = 95, OperationType = GraphQlOperationType.Query }],
            Contract = new() { Kind = "GraphQL schema", Available = false, IntrospectionEnabled = false, Note = "HTTP 400; introspection disabled or rejected" },
            Checks = [new() { CheckId = "gql-perf", Area = ApiReviewFindingType.Performance, Result = ApiReviewCheckResult.NotTested, Title = "GraphQL business response timing",
                Detail = "Not tested: not enough representative GraphQL business responses." }],
            AuthenticationEnforcement = new() { Status = ApiAuthenticationEnforcementStatus.Verified, Probe = "query { __typename }", StatusCode = 401 },
        };
        return new ApiReviewReport
        {
            Environment = new() { EnvironmentId = "dev", Name = "Development", EnvironmentType = "Development" },
            Targets = [rest, gql],
            Findings = findings
                ?
                [
                    Finding("hsts-1", "sec-no-hsts", "rest-1", ApiReviewSeverity.Low, ApiReviewFindingType.Security, "HSTS header missing on API responses"),
                    Finding("hsts-2", "sec-no-hsts", "gql-1", ApiReviewSeverity.Low, ApiReviewFindingType.Security, "HSTS header missing on API responses"),
                    Finding("server-1", "sec-server-disclosure", "rest-1", ApiReviewSeverity.Info, ApiReviewFindingType.Security, "Server technology disclosed"),
                    Finding("pd-1", "err-no-problem-details", "rest-1", ApiReviewSeverity.Low, ApiReviewFindingType.Errors, "Error responses do not use ProblemDetails", $"https://{Host}/api/autorisasjon"),
                ]
                : [],
            Coverage = new() { RestOperationsTotal = 3, RestOperationsReviewed = 2, AuthenticatedPlanned = 2, AuthenticatedExecuted = 2, TargetsCompleted = 2, UnsafeOperationsNotExecuted = 1, SecurityChecks = 2 },
            ManualReviewItems = ["Write operations: behaviour and side effects.", "Access control between roles/tenants.", "Business correctness of returned data."],
        };
    }

    // §80 Execution vs outcome.
    [Fact]
    public void ExecutionCompletion_IsSeparateFromTheReviewOutcome()
    {
        var outcome = ApiReviewPostRunPresentation.Outcome(Fixture());
        outcome.Execution.Should().Be("Completed");
        outcome.Assessment.Should().Be("Issues identified");
        outcome.ManualReview.Should().Be("Manual review required — 3 areas");
        outcome.Coverage.Should().Be("Limited");
        outcome.ServicesReviewed.Should().Be(2);
        outcome.RequestsExecuted.Should().Be(3);
        outcome.AuthenticatedAccess.Should().Be("Used for 2/2 authentication-required services");
    }

    // §81 No issues still owes manual review and is never a full validation.
    [Fact]
    public void NoAutomatedIssues_StillRequiresManualReview()
    {
        var outcome = ApiReviewPostRunPresentation.Outcome(Fixture(findings: false));
        outcome.Assessment.Should().Be("No automated issues identified");
        outcome.ManualReview.Should().StartWith("Manual review required");
        outcome.Coverage.Should().Be("Limited", "a missing contract and schema still limit coverage");
    }

    // §71, §88 Counts reconcile: one logical issue per rule/endpoint/severity, its observations counted separately.
    [Fact]
    public void LogicalIssuesAndSourceFindingsReconcile()
    {
        var report = Fixture();
        var issues = ApiReviewPresentation.LogicalIssues(report);
        issues.Should().HaveCount(3);
        issues.Sum(i => i.Sources.Count).Should().Be(report.Findings.Count);
        issues.Single(i => i.Title.StartsWith("HSTS")).Sources.Should().HaveCount(2, "one logical issue, two observations");
        issues.Single(i => i.Title.StartsWith("HSTS")).Affects.Should().BeEquivalentTo(["Autorisasjon API", "Autorisasjon GraphQL"]);
        var outcome = ApiReviewPostRunPresentation.Outcome(report);
        (outcome.LogicalIssues, outcome.SourceFindings).Should().Be((3, 4));
    }

    // §14, §82, §83 Coverage says what was reviewed and why it is limited; a missing contract is a limitation, never a blocker.
    [Fact]
    public void CoverageIsEvidenceAccurate_WithExactReasons()
    {
        var rows = ApiReviewPostRunPresentation.Coverage(Fixture()).ToDictionary(r => r.Domain);
        rows["REST"].State.Should().Be("Reviewed — contract validation unavailable");
        rows["REST"].Reason.Should().Be("No OpenAPI contract was configured.");
        rows["GraphQL"].State.Should().Be("Reviewed — runtime schema unavailable");
        rows["GraphQL"].Reason.Should().Contain("HTTP 400");
        rows["Contracts"].State.Should().Be("Incomplete");
        rows["Security"].State.Should().Be("Reviewed");
        rows["Error handling"].State.Should().Be("Reviewed");
        rows["Performance"].State.Should().Be("Observed — limited sample (3 requests)");
        rows["Authorization correctness"].State.Should().Be("Not assessed — manual / active checks required");
        rows["Performance"].Reason.Should().Contain("not load testing");
        rows["Write operations"].State.Should().Be("Observed — not executed");
        rows.Values.Should().NotContain(r => r.State.Contains("Blocked"));

        var limitations = ApiReviewPostRunPresentation.Limitations(Fixture());
        limitations.Select(l => l.Title).Should().Equal(
            "REST contract validation unavailable", "GraphQL compatibility not assessed", "Performance: limited sample",
            ApiReviewPostRunPresentation.NoComparableRuntimeBaseline, "Authorization correctness not assessed");
        // Classified coverage gaps, each linked to the tab holding its evidence — never findings.
        limitations.Select(l => (l.Category, l.Tab)).Should().Equal(
            ("Contract coverage", "Contracts"), ("Schema / compatibility", "GraphQL"), ("Performance evidence", "Performance"),
            ("Historical comparison", "Contracts"), ("Authorization coverage", "Security"));
    }

    /// <summary>Outcome and coverage are independent: no issues can still mean limited coverage, and the headline says both.</summary>
    [Fact]
    public void NoIssues_CoverageStillLimited_AndTheHeadlineSaysBoth()
    {
        var report = Fixture(findings: false);
        var outcome = ApiReviewPostRunPresentation.Outcome(report);
        outcome.Execution.Should().Be("Completed");
        outcome.Assessment.Should().Be("No automated issues identified");
        outcome.Coverage.Should().Be("Limited");
        ApiReviewPostRunPresentation.Headline(outcome, ApiReviewPostRunPresentation.Limitations(report))
            .Should().Be("Automated review completed. No issues were identified, and coverage remains limited in contracts, representative performance, historical comparison and authorization.");
    }

    [Fact]
    public void IssuesIdentified_HeadlineNamesTheLimitedAreas()
    {
        var report = Fixture();
        ApiReviewPostRunPresentation.Headline(ApiReviewPostRunPresentation.Outcome(report), ApiReviewPostRunPresentation.Limitations(report))
            .Should().StartWith("Automated review completed. Issues were identified, and coverage remains limited in contracts");
        ApiReviewPostRunPresentation.Limitations(report).Should().NotContain(l => l.Category.Contains("Severity"), "coverage gaps carry no severity");
    }

    // §27, §75, §84 Contract causes are separate, and a first review is never "no drift".
    [Fact]
    public void ContractCauses_AreSeparate_AndAFirstReviewHasNoDriftComparison()
    {
        var causes = ApiReviewPostRunPresentation.ContractCauses(Fixture()).ToDictionary(c => c.Key);
        causes["rest-contract"].Should().BeEquivalentTo(new { State = "Unavailable", Reason = "No OpenAPI contract configured." });
        causes["graphql-schema"].State.Should().Be("Unavailable");
        causes["compatibility"].Should().BeEquivalentTo(new { State = "Not assessed", Reason = "No usable schema available." });
        // Runtime structural drift and contract drift are separate facts; neither is a defect on a first run.
        causes["drift"].Should().BeEquivalentTo(new { Label = "Runtime structural drift", State = ApiReviewPostRunPresentation.NoComparableRuntimeBaseline, Reason = ApiReviewPostRunPresentation.FirstRuntimeBaseline });
        causes["contract-drift"].State.Should().Be("Not assessed");
        causes.Values.Should().NotContain(c => c.State.Contains("No drift"));

        ApiReviewPostRunPresentation.ContractCauses(Fixture(baseline: true)).Single(c => c.Key == "drift").State.Should().Be("Compared — no drift in the compared runtime structure");
    }

    // §24–§26, §42, §90, §91 GraphQL: observed ≠ executed; a __typename probe never establishes business performance.
    [Fact]
    public void GraphQl_ObservedIsNotExecuted_AndASystemQueryIsNotBusinessPerformance()
    {
        var report = Fixture();
        var summary = ApiReviewPostRunPresentation.GraphQl(report)!;
        // Only __typename ran: the state says runtime reachability, never a bare "Reviewed" that reads as "all observed operations were tested".
        summary.ReviewState.Should().Be("Runtime reachability reviewed · schema unavailable · business operations observed only");
        summary.Facts.Should().Contain(("Read-only queries executed", "1 (1 lightweight system query)")).And.Contain(("Business operations executed", "0"))
            .And.Contain(("Runtime schema", "Unavailable")).And.Contain(("Contract compatibility", "Not assessed"));
        summary.Note.Should().Contain("__typename").And.Contain("not business response performance");

        ApiReviewPostRunPresentation.GraphQlRepresentative(report).Should().StartWith("Not assessed").And.Contain("representative");
        var perf = ApiReviewPostRunPresentation.Performance(report);
        perf.Should().Contain(("REST requests measured", "2")).And.Contain(("GraphQL requests measured", "1"));
        perf.Should().Contain(f => f.Label == "Coverage limitation" && f.Value.Contains("too small"));
    }

    // §21–§23 REST summary first.
    [Fact]
    public void Rest_SummaryStatesWhatRan_TheContractLimitation_AndUnexecutedWrites()
    {
        var summary = ApiReviewPostRunPresentation.Rest(Fixture())!;
        summary.ReviewState.Should().Be("Reviewed with contract limitation");
        summary.Facts.Should().Contain(("Operations reviewed", "2 of 3")).And.Contain(("Read-only requests executed", "2")).And.Contain(("Access", "Authenticated access used"));
        summary.Note.Should().Be("1 write operation was observed and not executed (read-only review policy).");
    }

    // §32, §86 Authentication enforcement is verified; authorization correctness is never inferred from 401.
    [Fact]
    public void AuthenticationEnforcement_IsVerified_WithoutClaimingAuthorization()
    {
        var security = ApiReviewPresentation.SecuritySummary(Fixture()).ToDictionary(s => s.Key);
        security["authentication"].Title.Should().Be("Authentication enforcement");
        security["authentication"].Value.Should().Be("Observed on 2 of 2 services (anonymous request refused; not authorization correctness)");
        security.Values.Should().NotContain(s => s.Value.Contains("authoriz", StringComparison.OrdinalIgnoreCase) && s.Value.Contains("verified"));
        security["technology"].Value.Should().Be("1 observation issue (Info)");
    }

    // §49–§56, §87, §92 One vocabulary; recorded results never change.
    [Theory]
    [InlineData("errors-unknown-route", ApiReviewFindingType.Errors, ApiReviewCheckResult.Pass, ApiReviewCanonicalStatus.Passed, "Passed")]
    [InlineData("sec-hsts", ApiReviewFindingType.Security, ApiReviewCheckResult.Warning, ApiReviewCanonicalStatus.IssueDetected, "Issue detected")]
    [InlineData("sec-server-disclosure", ApiReviewFindingType.Security, ApiReviewCheckResult.Warning, ApiReviewCanonicalStatus.Observed, "Observed")]
    [InlineData("cors-policy", ApiReviewFindingType.Security, ApiReviewCheckResult.Pass, ApiReviewCanonicalStatus.Observed, "Observed")]
    [InlineData("errors-method", ApiReviewFindingType.Errors, ApiReviewCheckResult.NotTested, ApiReviewCanonicalStatus.NotAssessed, "Not assessed")]
    [InlineData("errors-method", ApiReviewFindingType.Errors, ApiReviewCheckResult.Blocked, ApiReviewCanonicalStatus.NotAssessed, "Not assessed")]
    [InlineData("gql-x", ApiReviewFindingType.Contract, ApiReviewCheckResult.NotApplicable, ApiReviewCanonicalStatus.NotApplicable, "Not applicable")]
    [InlineData("gql-observed-mutation", ApiReviewFindingType.Contract, ApiReviewCheckResult.ManualReview, ApiReviewCanonicalStatus.ManualReview, "Manual review")]
    [InlineData("rest-latency", ApiReviewFindingType.Performance, ApiReviewCheckResult.Pass, ApiReviewCanonicalStatus.Passed, "Within configured review threshold")]
    [InlineData("rest-latency", ApiReviewFindingType.Performance, ApiReviewCheckResult.Warning, ApiReviewCanonicalStatus.IssueDetected, "Exceeded configured review threshold")]
    [InlineData("drift-shape", ApiReviewFindingType.Drift, ApiReviewCheckResult.Pass, ApiReviewCanonicalStatus.Passed, "No changes detected")]
    public void StatusMapping_IsCanonical(string id, ApiReviewFindingType area, ApiReviewCheckResult result, ApiReviewCanonicalStatus status, string label)
    {
        var check = new ApiReviewCheck { CheckId = id, Area = area, Result = result, Detail = "x" };
        ApiReviewStatusTaxonomy.Of(check, new()).Should().Be(status);
        ApiReviewStatusTaxonomy.Label(check, new()).Should().Be(label);
        new[] { "Warning", "Not tested", "Verified", "Not validated", "No indicators observed", "Pass", "Fail" }.Should().NotContain(label);
    }

    [Fact]
    public void NotAssessed_AlwaysCarriesAReason()
    {
        var check = new ApiReviewCheck { CheckId = "errors-method", Area = ApiReviewFindingType.Errors, Result = ApiReviewCheckResult.NotTested, Detail = "" };
        ApiReviewEvidencePresentation.CheckDetail(check, new()).Should().NotBeNullOrWhiteSpace();
    }
}

/// <summary>
/// A trimmed (Release) WebAssembly build cannot reflect over KeyValuePair's constructor, so API review history — which holds fuzzing runs with
/// query/header pairs — failed to load at all. <see cref="BirkNext.ApiReview.ApiFuzzPair"/> keeps the exact JSON shape as an application type.
/// </summary>
public sealed class ApiFuzzPairJsonShapeTests
{
    [Fact]
    public void FuzzCasePairs_RoundTripInTheExistingShape()
    {
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        var fuzzCase = new BirkNext.ApiReview.ApiFuzzCase { Query = [new("id", "not-a-uuid")], Headers = [new("Accept", "application/json")] };
        var json = System.Text.Json.JsonSerializer.Serialize(fuzzCase, options);
        json.Should().Contain("\"query\":[{\"key\":\"id\",\"value\":\"not-a-uuid\"}]").And.Contain("\"headers\":[{\"key\":\"Accept\",\"value\":\"application/json\"}]");
        var back = System.Text.Json.JsonSerializer.Deserialize<BirkNext.ApiReview.ApiFuzzCase>(json, options)!;
        back.Query.Select(p => (p.Key, p.Value)).Should().Equal(("id", "not-a-uuid"));
        back.Headers.Select(p => (p.Key, p.Value)).Should().Equal(("Accept", "application/json"));
        // History stored before the fix (camelCase KeyValuePair objects) reads into the same pairs.
        System.Text.Json.JsonSerializer.Deserialize<BirkNext.ApiReview.ApiFuzzCase>("{\"query\":[{\"key\":\"a\",\"value\":\"b\"}]}", options)!.Query.Select(p => (p.Key, p.Value)).Should().Equal(("a", "b"));
    }
}
