using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Post-run API Quality Review semantics.
///
/// Three things were saying more than they knew. The result badge folded the manual-review obligation into the execution
/// state, so a review that had finished announced "Completed — manual review required". The obligations themselves came
/// from two hand-maintained lists, so the page said three areas in one place and five in another. And a service whose
/// schema could never be retrieved was labelled "Assessed", which claims a coverage the review did not have.
/// </summary>
public sealed class ApiReviewPostRunSemanticsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static ApiReviewTarget Target(ApiReviewTargetType type, string basePath, bool auth = true) => new()
    {
        TargetId = $"{type}:{basePath}", EnvironmentId = "dev", ApiType = type, Scheme = "https", Host = "m2lbdev.bufetat.no", Port = 443,
        BasePath = basePath, ServiceName = basePath, Source = ApiReviewTargetSource.DiscoveredTraffic, AuthRequired = auth,
        DiscoveredAt = T0, Confidence = ObservedEndpointConfidence.Verified, Selected = true,
    };

    private static ApiReviewTargetResult Result(ApiReviewTargetType type, string basePath, ApiReviewTargetStatus status = ApiReviewTargetStatus.Completed,
        ApiReviewContractSummary? contract = null) => new()
    {
        Target = Target(type, basePath), Status = status, AccessMode = ApiReviewAccessMode.AuthenticatedHttp, Contract = contract,
        Operations = [new ApiReviewOperationResult { Display = $"GET {basePath}", Executed = true, StatusCode = 200, AccessMode = ApiReviewAccessMode.AuthenticatedHttp }],
    };

    /// <summary>The three areas the engine reports after a run.</summary>
    private static readonly string[] EngineManualItems =
    [
        "Write operations (POST/PUT/PATCH/DELETE, GraphQL mutations): behaviour, idempotency and side effects.",
        "Access control between roles/tenants.",
        "Business correctness of returned data.",
    ];

    private static ApiReviewReport Report(params ApiReviewTargetResult[] targets) => new()
    {
        GeneratedAt = T0,
        Targets = [.. targets], Findings = [], ManualReviewItems = [.. EngineManualItems], Limitations = [],
        Coverage = new ApiReviewCoverage(),
        Access = new AuthenticatedReviewCapabilities { AuthenticatedApi = true },
    };

    // ── §42. One canonical obligation model ─────────────────────────────────

    // 17, 18, 19, 20.
    [Fact]
    public void TheAreaCountAndTheLimitationListComeFromOneSource()
    {
        var obligations = ApiReviewPresentation.ManualReviewObligations;

        // Three areas; the statements under them are detail, not a second set of obligations.
        obligations.Should().HaveCount(3);
        ApiReviewPresentation.ManualReviewAreas.Should().Equal(obligations.Select(o => o.Area));
        ApiReviewPresentation.LimitationSummary.Should().Equal(obligations.SelectMany(o => o.Details));
        ApiReviewPresentation.LimitationSummary.Count.Should().BeGreaterThan(ApiReviewPresentation.ManualReviewAreas.Count,
            "detail is expected to be longer than the areas; it is not a competing count");

        // The grouping loses nothing: every statement the old flat list carried still appears.
        ApiReviewPresentation.LimitationSummary.Should().Contain(
        [
            "Write behaviour and side effects",
            "Role-based authorization correctness",
            "Business correctness of returned data",
            "JSON value semantics beyond the recorded structure",
            "GraphQL mutation behaviour (never executed)",
        ]);
    }

    // 22.
    [Fact]
    public void AGraphQlMutationIsGroupedUnderWriteOperations()
    {
        var writes = ApiReviewPresentation.ManualReviewObligations.Single(o => o.Area.StartsWith("Write operations"));

        writes.Details.Should().Contain(d => d.Contains("GraphQL mutation"));
        ApiReviewPresentation.ManualReviewObligations.Should().NotContain(o => o.Area.Contains("mutation"),
            "a mutation is a write operation, not an area of its own");
    }

    /// <summary>The post-run count comes from the engine's own items and agrees with the areas the page lists.</summary>
    // 18, 19.
    [Fact]
    public void ThePostRunCountAgreesWithTheAreasThePageLists()
    {
        var view = ApiReviewPresentation.Result(Report(Result(ApiReviewTargetType.Rest, "/api/autorisasjon")));

        view.ManualReviewCount.Should().Be(ApiReviewPresentation.ManualReviewAreas.Count);
        view.ManualReviewCount.Should().Be(3);
    }

    // 21.
    [Fact]
    public void ManualObligationsAreNotFindings()
    {
        var view = ApiReviewPresentation.Result(Report(Result(ApiReviewTargetType.Rest, "/api/autorisasjon")));

        view.FindingCount.Should().Be(0, "three manual review areas are obligations, not findings");
        view.ManualReviewCount.Should().Be(3);
        view.Summary.Should().Contain("0 findings");
    }

    // ── §43. Execution status is only execution ─────────────────────────────

    // 23, 24, 26.
    [Fact]
    public void AFinishedReviewSaysCompletedEvenWhenAPersonStillHasWorkToDo()
    {
        var view = ApiReviewPresentation.Result(Report(
            Result(ApiReviewTargetType.Rest, "/api/autorisasjon"),
            Result(ApiReviewTargetType.GraphQl, "/api/autorisasjon/graphql")));

        view.State.Should().Be(ApiReviewResultState.Completed);
        view.StateLabel.Should().Be("Completed");
        view.StateLabel.Should().NotContain("manual review");
        view.ManualReviewCount.Should().Be(3, "the obligation is carried beside the state, not inside it");
    }

    /// <summary>The combined state is gone from the model, so it cannot be rendered again by accident.</summary>
    // 26.
    [Fact]
    public void NoCombinedExecutionAndManualReviewStateExists()
    {
        Enum.GetNames<ApiReviewResultState>().Should().NotContain(n => n.Contains("ManualReview"));
        foreach (var state in Enum.GetValues<ApiReviewResultState>())
            ApiReviewResultStates.Label(state).Should().NotContain("manual review");
    }

    // 25.
    [Fact]
    public void ExecutionFailureIsNotConfusedWithAManualReviewObligation()
    {
        var blocked = ApiReviewPresentation.Result(Report(
            Result(ApiReviewTargetType.Rest, "/api/autorisasjon", ApiReviewTargetStatus.NotTested)));

        blocked.State.Should().Be(ApiReviewResultState.FailedToRun);
        blocked.StateLabel.Should().Be("No target could be reviewed");
        // The obligation is still stated; it is simply a different fact.
        blocked.ManualReviewCount.Should().Be(3);
    }

    // ── §44. Service status wording ─────────────────────────────────────────

    // 27, 28, 29, 30.
    [Fact]
    public void AServiceThatTookPartInTheReviewIsReviewedNotAssessed()
    {
        ApiReviewStatusLabels.Label(ApiReviewTargetPresentationStatus.Assessed).Should().Be("Reviewed");
        ApiReviewStatusLabels.Label(ApiReviewTargetPresentationStatus.PartiallyAssessed).Should().Be("Reviewed with limitations");
        ApiReviewStatusLabels.Label(ApiReviewTargetPresentationStatus.NotAssessed).Should().Be("Not reviewed");

        foreach (var status in Enum.GetValues<ApiReviewTargetPresentationStatus>())
            ApiReviewStatusLabels.Label(status).Should().NotBe("Assessed", "the word claims a coverage the review does not have");
    }

    /// <summary>
    /// The case the wording was wrong for: a GraphQL service whose introspection failed still took part in the review,
    /// so it is Reviewed — and the contract column, not the status, carries what could not be retrieved.
    /// </summary>
    // 28, 29, 33, 34.
    [Fact]
    public void GraphQlWithoutIntrospectionIsStillReviewedAndSaysSoInTheContractColumn()
    {
        var contract = new ApiReviewContractSummary { Kind = "GraphQL schema", Available = false, IntrospectionEnabled = false, Note = "Introspection unavailable" };
        var result = Result(ApiReviewTargetType.GraphQl, "/api/autorisasjon/graphql", contract: contract);

        var status = ApiReviewStatusLabels.Of(result);

        ApiReviewStatusLabels.Label(status).Should().Be("Reviewed");
        ApiReviewStatusLabels.Label(status).Should().NotContain("Unavailable", "introspection is a coverage limit, not a service failure");
        result.Contract!.Note.Should().Contain("Introspection unavailable");
    }

    // ── §45. GraphQL pre-run vs post-run schema wording ─────────────────────

    // 35.
    [Fact]
    public void PreRunSchemaWordingIsAPlanAndPostRunWordingIsAResult()
    {
        var card = ApiReviewPresentation.TargetCard(Target(ApiReviewTargetType.GraphQl, "/api/autorisasjon/graphql"));

        card.SchemaLabel.Should().Be("Retrieval will be attempted during review");
        card.SchemaLabel.Should().NotContain("available", "nothing has been retrieved before the review runs");

        // Post-run, the contract rows carry what actually happened.
        var selected = new[] { "GraphQl:/api/autorisasjon/graphql" };
        var targets = new List<ApiReviewTarget> { Target(ApiReviewTargetType.GraphQl, "/api/autorisasjon/graphql") };
        var afterFailure = ApiReviewPresentation.Contracts(targets, selected, null,
            Report(Result(ApiReviewTargetType.GraphQl, "/api/autorisasjon/graphql",
                contract: new ApiReviewContractSummary { Kind = "GraphQL schema", Available = false, IntrospectionEnabled = false })));

        afterFailure.Rows.Single(r => r.Label == "GraphQL").State.Should().Be(ApiReviewContractState.IntrospectionUnavailable);
    }

    // ── §48. Semantic guards ────────────────────────────────────────────────

    [Fact]
    public void NoSurfaceClaimsCompleteCoverageOrAPass()
    {
        var view = ApiReviewPresentation.Result(Report(
            Result(ApiReviewTargetType.Rest, "/api/autorisasjon"),
            Result(ApiReviewTargetType.GraphQl, "/api/autorisasjon/graphql")));
        var text = string.Join(" ", new[] { view.StateLabel, view.Summary }
            .Concat(ApiReviewPresentation.ManualReviewAreas)
            .Concat(ApiReviewPresentation.LimitationSummary)
            .Concat(Enum.GetValues<ApiReviewTargetPresentationStatus>().Select(ApiReviewStatusLabels.Label)));

        foreach (var claim in new[] { "All APIs passed", "Fully assessed", "Fully covered", "Compliant", "is secure", "Passed" })
            text.Should().NotContain(claim);
    }
}
