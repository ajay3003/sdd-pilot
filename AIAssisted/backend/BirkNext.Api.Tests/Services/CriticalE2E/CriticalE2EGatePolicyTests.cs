using System.Text.Json;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.CriticalE2E;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// The versioned release gate. Policy 2 ("critical-e2e-gate/2") reads the test type: UI smoke never satisfies Business E2E, UI functional is
/// functional coverage only, Integration/API is integration coverage only, Business E2E counts only when Critical, release-ready and passed on
/// the named build, and a legacy (Unknown) flow requires review — never PASS. Criticality never overrides the test type. A build whose
/// evidence was recorded before versioned policies keeps its legacy verdict (policy 1). Pure rules: nothing here runs or contacts anything.
/// </summary>
public sealed class CriticalE2EGatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly string[] Known = ["M02", "EN01"];

    private static CriticalE2EFlowDefinition Flow(string id, CriticalE2ETestType type, string module = "M02",
        CriticalE2EFlowKind kind = CriticalE2EFlowKind.Critical, bool required = true) => new()
        {
            Id = id, Module = module, Name = id, Mode = CriticalE2EExecutionMode.CompanionBrowser, ProfileId = "dev", EnvironmentId = "env-1",
            Enabled = true, RequiredForRelease = required, Kind = kind, TestType = type,
            Boundary = new CriticalE2EAutomationBoundary { Start = "Approved application page", End = "Status visible" },
            Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.AssertVisible,
                Selector = new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "status" }, IsFinalAssertion = true }],
        };

    /// <param name="policy">The gate policy recorded on the run; null = recorded before versioned policies (legacy).</param>
    private static CriticalE2ERunResult Run(string flowId, CriticalE2EStatus status, string? build = "RC60", string? policy = CriticalE2EGatePolicy.Current,
        int minutesAgo = 1) => new()
    {
        RunId = $"{flowId}-{build}-{minutesAgo}", FlowId = flowId, EnvironmentId = "env-1", Status = status,
        StartedAt = Now.AddMinutes(-minutesAgo), BuildId = build, RequiredForRelease = true,
        FlowVersion = 1, ReleaseEvidence = build is not null, GatePolicyVersion = policy,
    };

    private static CriticalE2EReleaseStatus Release(IReadOnlyList<CriticalE2EFlowDefinition> flows, IReadOnlyList<CriticalE2ERunResult> history, string? build = "RC60") =>
        CriticalE2ECoverage.Release(flows, history, Known, "env-1", build, null);

    private static CriticalE2EGateEligibility Gate(CriticalE2EFlowDefinition flow, IReadOnlyList<CriticalE2ERunResult> history, string? build = "RC60") =>
        CriticalE2ECoverage.Summarize(flow, history, build, Known).Gate;

    // ── UI smoke vs Business E2E ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APassedCriticalUiSmokeFlowNeverSatisfiesBusinessE2E()
    {
        var smoke = Flow("smoke", CriticalE2ETestType.UiSmoke);
        var history = new[] { Run("smoke", CriticalE2EStatus.Passed) };

        var gate = Gate(smoke, history);
        (gate.Eligibility, gate.CountsTowardBusinessE2E, gate.PolicyVersion).Should().Be((CriticalE2EGateEligibilityKind.Never, false, CriticalE2EGatePolicy.Current));
        gate.Reason.Should().Be("UI smoke — never counts toward Business E2E");

        var release = Release([smoke], history);
        release.Disposition.Should().NotBe(CriticalE2EReleaseDisposition.Ready, "a UI smoke pass is not Business E2E coverage");
        release.Disposition.Should().Be(CriticalE2EReleaseDisposition.NotConfigured);
        release.BusinessE2EFlowsCounted.Should().Be(0);
        release.RequiredFlowsPassed.Should().Be(1, "the run itself still passed; it just earns no Business E2E credit");
        release.Summary.Should().Contain("none counts toward Business E2E");
        CriticalE2ECoverage.Modules([smoke], history, Known, "RC60").Single(m => m.Module == "M02").Covered.Should().BeFalse();
    }

    [Fact]
    public void BusinessE2EAlongsideUiSmokeIsReady_AndOnlyTheBusinessFlowCounts()
    {
        var flows = new[] { Flow("smoke", CriticalE2ETestType.UiSmoke), Flow("biz", CriticalE2ETestType.BusinessE2E, module: "EN01") };
        var history = new[] { Run("smoke", CriticalE2EStatus.Passed), Run("biz", CriticalE2EStatus.Passed) };

        var release = Release(flows, history);
        release.Disposition.Should().Be(CriticalE2EReleaseDisposition.Ready);
        (release.RequiredFlowsPassed, release.BusinessE2EFlowsCounted).Should().Be((2, 1));
        release.Summary.Should().Be("All 2 required flow(s) passed against this build · 1 count toward Business E2E.");
        var modules = CriticalE2ECoverage.Modules(flows, history, Known, "RC60");
        modules.Single(m => m.Module == "EN01").Covered.Should().BeTrue();
        modules.Single(m => m.Module == "M02").Covered.Should().BeFalse();
        modules.Single(m => m.Module == "M02").Status.Should().Be("M02 · required flow(s) not Business E2E · no Business E2E coverage");
    }

    [Fact]
    public void AFailingRequiredUiSmokeFlowStillBlocks_ItEarnsNoCreditButAFailureIsAFailure()
    {
        var flows = new[] { Flow("smoke", CriticalE2ETestType.UiSmoke), Flow("biz", CriticalE2ETestType.BusinessE2E, module: "EN01") };
        Release(flows, [Run("smoke", CriticalE2EStatus.Failed), Run("biz", CriticalE2EStatus.Passed)]).Disposition
            .Should().Be(CriticalE2EReleaseDisposition.Blocked);
    }

    // ── UI functional ─────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UiFunctionalCountsAsFunctionalCoverageOnly()
    {
        var functional = Flow("func", CriticalE2ETestType.UiFunctional);
        var history = new[] { Run("func", CriticalE2EStatus.Passed) };

        var gate = Gate(functional, history);
        (gate.Eligibility, gate.CountsTowardBusinessE2E).Should().Be((CriticalE2EGateEligibilityKind.FunctionalOnly, false));
        gate.Reason.Should().Be("UI functional — counts as functional coverage only, not Business E2E");
        Release([functional], history).Disposition.Should().NotBe(CriticalE2EReleaseDisposition.Ready);
    }

    // ── Business E2E needs passed execution evidence ──────────────────────────────────────────────────────────────────

    [Fact]
    public void BusinessE2EWithoutAPassedRunEarnsNoCredit()
    {
        var biz = Flow("biz", CriticalE2ETestType.BusinessE2E);

        var notRun = Gate(biz, []);
        (notRun.Eligibility, notRun.CountsTowardBusinessE2E).Should().Be((CriticalE2EGateEligibilityKind.NotExecuted, true));
        notRun.Reason.Should().Be("Business E2E — no passed run on this build, no credit");
        Release([biz], []).Disposition.Should().Be(CriticalE2EReleaseDisposition.Incomplete);
        Release([biz], []).BusinessE2EFlowsCounted.Should().Be(0);

        var failed = new[] { Run("biz", CriticalE2EStatus.Failed) };
        Gate(biz, failed).Eligibility.Should().Be(CriticalE2EGateEligibilityKind.NotExecuted);
        Gate(biz, failed).Reason.Should().Be("Business E2E — failed on this build, no credit");
        Release([biz], failed).Disposition.Should().Be(CriticalE2EReleaseDisposition.Blocked);

        // A pass on another build, or with no build named, is not evidence for this one.
        Gate(biz, [Run("biz", CriticalE2EStatus.Passed, build: "RC59")]).Eligibility.Should().Be(CriticalE2EGateEligibilityKind.NotExecuted);
        Gate(biz, [Run("biz", CriticalE2EStatus.Passed, build: null)], build: null).Reason.Should().Be("Business E2E — no build selected, so no passed-run credit");

        var passed = new[] { Run("biz", CriticalE2EStatus.Passed) };
        var counts = Gate(biz, passed);
        (counts.Eligibility, counts.Reason).Should().Be((CriticalE2EGateEligibilityKind.Counts, "Counts — Business E2E, critical, release-ready and passed on this build"));
        var release = Release([biz], passed);
        (release.Disposition, release.BusinessE2EFlowsCounted, release.Summary).Should().Be((CriticalE2EReleaseDisposition.Ready, 1, "All 1 required flow(s) passed against this build."));
    }

    [Fact]
    public void BusinessE2EThatIsNotReleaseReadyDoesNotCount()
    {
        var noBoundary = Flow("biz", CriticalE2ETestType.BusinessE2E) with { Boundary = null };
        var history = new[] { Run("biz", CriticalE2EStatus.Passed) };
        var gate = Gate(noBoundary, history);
        gate.Eligibility.Should().Be(CriticalE2EGateEligibilityKind.NotReady);
        gate.Reason.Should().StartWith("Business E2E — definition not release-ready: ");
        Release([noBoundary], history).Disposition.Should().Be(CriticalE2EReleaseDisposition.Blocked);
    }

    // ── Integration/API ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IntegrationApiCountsTowardIntegrationCoverageOnly()
    {
        var api = Flow("api", CriticalE2ETestType.IntegrationApi);
        var history = new[] { Run("api", CriticalE2EStatus.Passed) };
        var gate = Gate(api, history);
        (gate.Eligibility, gate.CountsTowardBusinessE2E).Should().Be((CriticalE2EGateEligibilityKind.IntegrationOnly, false));
        gate.Reason.Should().Be("Integration/API — counts toward integration coverage only, not Business E2E");
        Release([api], history).BusinessE2EFlowsCounted.Should().Be(0);
    }

    // ── Criticality is a separate axis ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CriticalityNeverOverridesTheTestType_AndTheTestTypeNeverMakesADiagnosticFlowCount()
    {
        // Critical alone is not enough: a critical UI smoke flow stays "never".
        Gate(Flow("smoke", CriticalE2ETestType.UiSmoke), [Run("smoke", CriticalE2EStatus.Passed)]).Eligibility.Should().Be(CriticalE2EGateEligibilityKind.Never);
        // Business E2E alone is not enough: a diagnostic Business E2E flow never counts and never gates.
        var diagnostic = Flow("diag", CriticalE2ETestType.BusinessE2E, kind: CriticalE2EFlowKind.Diagnostic);
        var history = new[] { Run("diag", CriticalE2EStatus.Passed) };
        var gate = Gate(diagnostic, history);
        (gate.Eligibility, gate.CountsTowardBusinessE2E).Should().Be((CriticalE2EGateEligibilityKind.Never, false));
        gate.Reason.Should().Be("Diagnostic flow — never counts toward Business E2E, whatever its test type");
        Release([diagnostic], history).Disposition.Should().Be(CriticalE2EReleaseDisposition.NotConfigured);
    }

    // ── Legacy (Unknown) classification ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ALegacyUnclassifiedFlowRequiresReviewUnderPolicy2_NeverPass()
    {
        var legacy = Flow("old", CriticalE2ETestType.Unknown);
        var history = new[] { Run("old", CriticalE2EStatus.Passed) };   // a new (policy 2) pass

        var gate = Gate(legacy, history);
        (gate.Eligibility, gate.CountsTowardBusinessE2E, gate.Reason)
            .Should().Be((CriticalE2EGateEligibilityKind.LegacyReview, false, CriticalE2EGatePolicy.LegacyReviewReason));
        gate.Reason.Should().StartWith("Legacy classification requires review");

        var release = Release([legacy], history);
        release.Disposition.Should().Be(CriticalE2EReleaseDisposition.ReviewRequired, "never silently counted as Business E2E");
        release.RequiredFlowsLegacyReview.Should().Be(1);
        release.BusinessE2EFlowsCounted.Should().Be(0);
        release.Summary.Should().Be("1 required flow(s) need review: legacy classification requires review before they can count toward Business E2E.");
        CriticalE2ECoverage.Modules([legacy], history, Known, "RC60").Single(m => m.Module == "M02")
            .Should().Match<CriticalE2EModuleCoverage>(m => !m.Covered && m.Status == "M02 · required flow needs review · legacy classification requires review");

        // Without a build it is still named, not hidden.
        var unevaluated = Release([legacy], [], build: null);
        unevaluated.Disposition.Should().Be(CriticalE2EReleaseDisposition.NotEvaluated);
        unevaluated.Summary.Should().EndWith("1 need legacy classification review before they can count toward Business E2E.");

        // The stored definition is never reclassified by evaluating it.
        legacy.TestType.Should().Be(CriticalE2ETestType.Unknown);
    }

    [Fact]
    public void AFailedLegacyFlowStillBlocks_ReviewNeverHidesAFailure() =>
        Release([Flow("old", CriticalE2ETestType.Unknown)], [Run("old", CriticalE2EStatus.Failed)]).Disposition.Should().Be(CriticalE2EReleaseDisposition.Blocked);

    // ── Historical evaluations ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABuildWhoseEvidencePredatesVersionedPoliciesKeepsItsLegacyVerdict()
    {
        // Recorded before this change: no policy version on the runs. An unclassified flow and a UI smoke flow both passed on RC57.
        var flows = new[] { Flow("old", CriticalE2ETestType.Unknown), Flow("smoke", CriticalE2ETestType.UiSmoke, module: "EN01") };
        var history = new[] { Run("old", CriticalE2EStatus.Passed, "RC57", policy: null), Run("smoke", CriticalE2EStatus.Passed, "RC57", policy: null) };

        var release = Release(flows, history, "RC57");
        release.PolicyVersion.Should().Be(CriticalE2EGatePolicy.Legacy);
        release.Disposition.Should().Be(CriticalE2EReleaseDisposition.Ready, "the legacy gate counted every critical, release-ready pass");
        release.Summary.Should().Be("All 2 required flow(s) passed against this build.");
        (release.RequiredFlowsPassed, release.RequiredFlowsLegacyReview, release.BusinessE2EFlowsCounted).Should().Be((2, 0, 2));
        release.ModulesCovered.Should().Be(2, "module coverage is read by the policy that evaluated the build");

        var gate = Gate(flows[1], history, "RC57");
        (gate.PolicyVersion, gate.Eligibility).Should().Be((CriticalE2EGatePolicy.Legacy, CriticalE2EGateEligibilityKind.Counts));
        gate.Reason.Should().Contain("legacy policy");

        // The same build's verdict under the legacy rules is exactly the verdict the pre-change gate produced for these inputs.
        var pending = Release(flows, [history[0] with { Status = CriticalE2EStatus.Failed }, history[1]], "RC57");
        (pending.PolicyVersion, pending.Disposition, pending.Summary).Should().Be((CriticalE2EGatePolicy.Legacy, CriticalE2EReleaseDisposition.Blocked, "1 required flow(s) failed against this build."));
    }

    [Fact]
    public void NewEvidenceOnABuildIsANewEvaluationUnderTheCurrentPolicy()
    {
        var flows = new[] { Flow("old", CriticalE2ETestType.Unknown) };
        var legacyOnly = new[] { Run("old", CriticalE2EStatus.Passed, "RC57", policy: null, minutesAgo: 10) };
        Release(flows, legacyOnly, "RC57").Disposition.Should().Be(CriticalE2EReleaseDisposition.Ready);

        var withNewRun = legacyOnly.Append(Run("old", CriticalE2EStatus.Passed, "RC57", minutesAgo: 1)).ToList();
        var release = Release(flows, withNewRun, "RC57");
        release.PolicyVersion.Should().Be(CriticalE2EGatePolicy.Current);
        release.Disposition.Should().Be(CriticalE2EReleaseDisposition.ReviewRequired);
    }

    [Fact]
    public void ThePolicyIsChosenFromTheBuildsRecordedEvidence()
    {
        CriticalE2EGatePolicy.ForBuild([], "RC1").Should().Be(CriticalE2EGatePolicy.Current, "a build with no evidence is evaluated by the current policy");
        CriticalE2EGatePolicy.ForBuild([Run("a", CriticalE2EStatus.Passed, "RC1", policy: null)], null).Should().Be(CriticalE2EGatePolicy.Current);
        CriticalE2EGatePolicy.ForBuild([Run("a", CriticalE2EStatus.Passed, "RC1", policy: null)], "rc1").Should().Be(CriticalE2EGatePolicy.Legacy);
        CriticalE2EGatePolicy.ForBuild([Run("a", CriticalE2EStatus.Passed, "RC1", policy: null)], "RC2").Should().Be(CriticalE2EGatePolicy.Current);
        CriticalE2EGatePolicy.ForBuild([Run("a", CriticalE2EStatus.Passed, "RC1", policy: null), Run("b", CriticalE2EStatus.Passed, "RC1")], "RC1")
            .Should().Be(CriticalE2EGatePolicy.Current);
    }

    // ── Provenance ────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ThePolicyVersionIsRecordedInTheVerdictAndPerFlow()
    {
        var biz = Flow("biz", CriticalE2ETestType.BusinessE2E);
        var history = new[] { Run("biz", CriticalE2EStatus.Passed) };
        Release([biz], history).PolicyVersion.Should().Be("critical-e2e-gate/2");
        Release([biz], [], build: null).PolicyVersion.Should().Be("critical-e2e-gate/2");
        Gate(biz, history).PolicyVersion.Should().Be("critical-e2e-gate/2");
        CriticalE2EGatePolicy.Legacy.Should().Be("critical-e2e-gate/1");
    }

    [Fact]
    public void TheRunsPolicyFieldIsAdditive_ALegacyRunNeitherGainsNorWritesIt()
    {
        var legacyJson = """{"RunId":"r1","FlowId":"f1","Status":"Passed","BuildId":"RC57"}""";
        var legacy = JsonSerializer.Deserialize<CriticalE2ERunResult>(legacyJson)!;
        legacy.GatePolicyVersion.Should().BeNull();
        JsonSerializer.Serialize(legacy).Should().NotContain("GatePolicyVersion");
        JsonSerializer.Serialize(legacy with { GatePolicyVersion = CriticalE2EGatePolicy.Current }).Should().Contain("\"GatePolicyVersion\":\"critical-e2e-gate/2\"");

        JsonSerializer.Serialize(CriticalE2EReleaseDisposition.ReviewRequired).Should().Be("\"ReviewRequired\"");
        ((int)CriticalE2EReleaseDisposition.NotEvaluated).Should().Be(4, "existing values keep their numbers");
    }

    [Theory]
    [InlineData(CriticalE2EGateEligibilityKind.Counts, "counts")]
    [InlineData(CriticalE2EGateEligibilityKind.FunctionalOnly, "functional-only")]
    [InlineData(CriticalE2EGateEligibilityKind.IntegrationOnly, "integration-only")]
    [InlineData(CriticalE2EGateEligibilityKind.Never, "never")]
    [InlineData(CriticalE2EGateEligibilityKind.LegacyReview, "legacy-review")]
    [InlineData(CriticalE2EGateEligibilityKind.NotExecuted, "not-executed")]
    [InlineData(CriticalE2EGateEligibilityKind.NotReady, "not-ready")]
    public void EligibilityTokensAreStable(CriticalE2EGateEligibilityKind kind, string token) => CriticalE2EGatePolicy.Token(kind).Should().Be(token);
}
