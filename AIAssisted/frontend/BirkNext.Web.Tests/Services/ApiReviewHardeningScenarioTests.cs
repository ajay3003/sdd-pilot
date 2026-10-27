using BirkNext.ApiReview;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// AQR hardening: configured ≠ available ≠ observed ≠ assessed. A missing or ambiguous contract never limits the read-only review and is
/// resolved from existing Source Analysis evidence before anyone is asked to configure it; runtime introspection is never a published
/// contract; optional active checks are neutral; a tiny timing sample never "passes" performance; manual areas are not check counts.
/// </summary>
public sealed class ApiReviewHardeningScenarioTests
{
    private static ApiReviewTarget Rest(string? contract = null) => new()
    {
        TargetId = "rest", ApiType = ApiReviewTargetType.Rest, Host = "api.example.test", BasePath = "/api", ServiceName = "Autorisasjon API",
        AuthRequired = true, ContractSource = contract,
        Operations = [new ApiReviewOperation { Method = "GET", Path = "/api/roller" }, new ApiReviewOperation { Method = "GET", Path = "/api/roller/42" }],
    };

    private static SourceContract OpenApi(string file, params string[] paths) => new()
    {
        Id = file, Type = SourceContractType.OpenApi, Name = Path.GetFileNameWithoutExtension(file), File = file, Format = "OpenAPI 3",
        Operations = [.. paths.Select(p => new ContractOperation("GET " + p, "GET", p, []))],
    };

    private static IqrSourceSnapshot Snapshot(string archive, params SourceContract[] contracts) => new()
    {
        Id = Guid.NewGuid(), IntegrationId = "source-analysis", Archive = new SourceArchive(archive, "fp-" + archive, 1), AnalyzedAt = DateTimeOffset.UnixEpoch,
        EvidenceDomains = new SourceEvidenceDomainsSnapshot { Contracts = new ContractEvidence { Domain = SourceEvidenceDomain.Contracts, Contracts = [.. contracts] } },
    };

    // A — REST contract auto-resolved from Source Analysis by declared operation paths; no configuration asked for.
    [Fact]
    public void A_rest_contract_is_auto_bound_from_source_evidence_with_provenance()
    {
        var snapshot = Snapshot("autorisasjon.zip", OpenApi("src/Autorisasjon.Api/openapi.json", "/roller", "/roller/{id}"), OpenApi("src/Other/openapi.json", "/hendelser"));
        var binding = ApiReviewContractBindings.Resolve(Rest(), [snapshot]);
        binding.State.Should().Be(ApiReviewContractBindingState.SourceBound);
        binding.Label.Should().Be("OpenAPI · Source Analysis snapshot");
        binding.File.Should().Be("src/Autorisasjon.Api/openapi.json");
        binding.SnapshotId.Should().Be(snapshot.Id);
        binding.MatchedOperations.Should().Be(2);
        binding.Meaning.Should().Contain("not approval, deployment or runtime verification");

        var review = ApiReviewPreRunPresentation.Review(Rest(), ApiReviewAccessAvailability.Available, false, false, binding);
        review.State.Should().Be(ApiReviewTargetReviewState.Ready);
        review.Action.Should().Be(ApiReviewTargetAction.None, "known evidence is never re-entered manually");
        review.ContractValue.Should().Be("OpenAPI · Source Analysis snapshot");
    }

    // Explicit configuration wins over source evidence.
    [Fact]
    public void Configured_contract_is_an_explicit_choice_and_is_assessable()
    {
        var binding = ApiReviewContractBindings.Resolve(Rest("https://api.example.test/swagger.json"), [Snapshot("a.zip", OpenApi("openapi.json", "/roller"))]);
        binding.State.Should().Be(ApiReviewContractBindingState.Configured);
        binding.CompatibilityLabel.Should().Be("Available to assess");
    }

    // B — no contract evidence: neutral, runtime review still runs.
    [Fact]
    public void B_no_contract_is_not_available_and_never_limits_the_review()
    {
        var binding = ApiReviewContractBindings.Resolve(Rest(), []);
        binding.State.Should().Be(ApiReviewContractBindingState.NotAvailable);
        binding.Label.Should().Be("Not available");
        binding.Meaning.Should().Be("Runtime API review can still run. Contract compatibility will not be assessed.");
        ApiReviewPreRunPresentation.Review(Rest(), ApiReviewAccessAvailability.Available, false, false, binding).State.Should().Be(ApiReviewTargetReviewState.Ready);
    }

    // C — several equally matching contracts, or several snapshots: review required, never auto-picked.
    [Fact]
    public void C_ambiguous_contracts_and_snapshots_are_review_required()
    {
        var two = ApiReviewContractBindings.Resolve(Rest(), [Snapshot("a.zip", OpenApi("v1/openapi.json", "/roller"), OpenApi("v2/openapi.json", "/roller"))]);
        two.State.Should().Be(ApiReviewContractBindingState.MultipleCandidates);
        two.Label.Should().Be("Review required — multiple contract candidates");
        two.CompatibilityLabel.Should().Be("Not assessed");
        ApiReviewPreRunPresentation.Review(Rest(), ApiReviewAccessAvailability.Available, false, false, two).Action.Should().Be(ApiReviewTargetAction.ConfigureContract);

        var snapshots = ApiReviewContractBindings.Resolve(Rest(), [Snapshot("a.zip", OpenApi("openapi.json", "/roller")), Snapshot("b.zip", OpenApi("openapi.json", "/roller"))]);
        snapshots.State.Should().Be(ApiReviewContractBindingState.MultipleSnapshots);
        snapshots.Label.Should().Be("Review required — multiple source snapshots may apply");

        var unrelated = ApiReviewContractBindings.Resolve(Rest(), [Snapshot("a.zip", OpenApi("openapi.json", "/hendelser"))]);
        unrelated.State.Should().Be(ApiReviewContractBindingState.IdentityNotEstablished, "no file-name guess: no declared operation matches");
        unrelated.Label.Should().Be("Not assessed — contract identity not established");
    }

    // D/E — GraphQL runtime schema is never a published contract.
    [Fact]
    public void D_E_graphql_runtime_schema_is_not_a_published_contract()
    {
        var gql = new ApiReviewTarget { TargetId = "gql", ApiType = ApiReviewTargetType.GraphQl, Host = "api.example.test", BasePath = "/graphql", ServiceName = "Autorisasjon GraphQL", AuthRequired = true };
        var review = ApiReviewPreRunPresentation.Review(gql, ApiReviewAccessAvailability.Available, false, false);
        review.ContractLabel.Should().Be("Runtime schema");
        review.ContractValue.Should().Be("Will attempt introspection during review", "nothing is observed before the run");
        review.FallbackValue.Should().Be("Not available");
        review.StatusLine.Should().NotContain("Runtime ready · Runtime schema");
        review.WillNotRun.Should().Contain("Contract compatibility (no published schema — not assessed)");
    }

    // F — optional authorization checks unavailable: neutral, never "needs attention".
    [Fact]
    public void F_optional_authorization_unavailable_never_needs_attention()
    {
        var reviews = new[] { ApiReviewPreRunPresentation.Review(Rest("https://x/swagger.json"), ApiReviewAccessAvailability.Available, false, false) };
        var limitations = new List<ApiReviewKnownLimitation> { new("Authorization checks", "Optional · not ready — non-blocking.") };
        var summary = ApiReviewPreRunPresentation.ReadinessSummary(reviews, limitations);
        summary.Should().Be("1 API target is ready for read-only review. Optional active checks unavailable — non-blocking.");
        summary.Should().NotContain("Needs attention");
    }

    // G — pipeline gates: configuration is never "not passed".
    [Fact]
    public void G_pipeline_gate_states_never_infer_execution()
    {
        DeliveryGateStates.NoSnapshot.Should().Be("Not assessed — select a source snapshot");
        DeliveryGateStates.ConfiguredNoExecution.Should().Be("Configured in source · execution result not available");
    }

    // H — timing from a tiny bounded sample is never a performance pass.
    [Fact]
    public void H_small_timing_sample_is_threshold_wording_never_passed()
    {
        var policy = new ApiReviewPolicy();
        ApiReviewStatusTaxonomy.Label(new ApiReviewCheck { CheckId = "rest-latency", Area = ApiReviewFindingType.Performance, Result = ApiReviewCheckResult.Pass }, policy)
            .Should().Be("Within configured single-request threshold");
        ApiReviewStatusTaxonomy.Label(new ApiReviewCheck { CheckId = "rest-average-latency", Area = ApiReviewFindingType.Performance, Result = ApiReviewCheckResult.Pass }, policy)
            .Should().Be("Observed average within configured review threshold");
        ApiReviewStatusTaxonomy.Label(new ApiReviewCheck { CheckId = "gql-reachability", Area = ApiReviewFindingType.Performance, Result = ApiReviewCheckResult.Pass }, policy)
            .Should().Be("Observed", "a __typename reachability query asserts nothing about quality");

        var report = ApiReviewPostRunPresentationTests.Fixture();
        var performance = ApiReviewPostRunPresentation.Coverage(report).Single(r => r.Key == "performance");
        performance.State.Should().Contain("representative performance not assessed").And.NotContain("Passed");
        performance.Limited.Should().BeFalse();
        ApiReviewPostRunPresentation.Limitations(report).Should().Contain(l => l.Title == "Representative performance: Not assessed");
    }

    // I — manual-review areas are not the individual check count.
    [Fact]
    public void I_manual_review_areas_and_checks_are_distinct_counts()
    {
        var report = ApiReviewPostRunPresentationTests.Fixture();
        var label = ApiReviewPostRunPresentation.ManualReviewLabel(report);
        label.Should().StartWith($"Manual review required — {ApiReviewPresentation.ManualReviewAreas.Count} areas · ");
        ApiReviewPostRunPresentation.ManualReviewChecks(report).Should().BeGreaterThanOrEqualTo(ApiReviewPresentation.LimitationSummary.Count);
        ApiReviewPostRunPresentation.ManualReviewAreasNote.Should().Contain("not the number of individual checks");
    }

    // Coverage gaps are never findings, issues or a decision input; a first run's baseline absence is not a reduction.
    [Fact]
    public void Coverage_gaps_are_not_findings_and_first_baseline_is_not_partial()
    {
        var report = ApiReviewPostRunPresentationTests.Fixture(findings: false);
        var outcome = ApiReviewPostRunPresentation.Outcome(report);
        outcome.LogicalIssues.Should().Be(0, "contract, schema, baseline, sample and authorization gaps never create issues");
        ApiReviewPostRunPresentation.Coverage(report).Single(r => r.Key == "authorization").Limited.Should().BeFalse();
        ApiReviewPostRunPresentation.Headline(outcome, ApiReviewPostRunPresentation.Limitations(report))
            .Should().StartWith("Automated runtime review completed for").And.Contain("Not fully assessed").And.NotContain("limited");
    }

    // Export parity: the same service states and wording as the page, never "Reviewed", raw enums or "not passed".
    [Fact]
    public void Export_uses_the_page_states_and_never_stale_wording()
    {
        var report = ApiReviewPostRunPresentationTests.Fixture();
        var html = System.Net.WebUtility.HtmlDecode(new ReportExportService().ExportApiReview(report, "Fixture"));
        foreach (var row in ApiReviewPostRunPresentation.Services(report)) html.Should().Contain(row.ReviewState);
        html.Should().Contain(ApiReviewPostRunPresentation.PerformanceScope)
            .And.Contain(ApiReviewPostRunPresentation.ManualReviewAreasNote)
            .And.NotContain("Ready to run with limitations").And.NotContain("not passed").And.NotContain(">Reviewed<").And.NotContain("Performance Passed");
    }
}
