using BirkNext.PerformanceTests;
using BirkNext.Web.Services;
using FluentAssertions;
using D = BirkNext.Web.Services.PerformanceTestReviewDashboard;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Performance Test Review pre-run: the execution engine being available is not target execution readiness; a missing corporate CA is a
/// local execution prerequisite (never a performance, target or application result); TLS verification stays enabled; the readiness count
/// and Guided setup progress are worded as the different measures they are; optional capabilities never block.
/// </summary>
public sealed class PerformanceReadinessSemanticsTests
{
    private static PerformanceReadinessItem CaMissing() => new("ca-trust", "Corporate CA trust", PerformanceReadinessState.NeedsConfiguration,
        "The k6 container cannot validate the certificate chain for app.example.test because the trusted corporate CA bundle is not configured.", true)
    {
        Category = PerformanceReadinessCategories.ExecutionSetup, Summary = "container TLS trust", Headline = "Container TLS trust is not configured", Status = "Missing",
        Owner = D.LocalExecutionOwner, ConfigurationKey = "PerformanceTests:Container:CaBundlePath", Action = PerformanceReadinessActions.OpenLocalExecutionSetup,
    };

    [Fact]
    public void Engine_available_is_not_ready_and_does_not_look_like_a_pass()
    {
        var engine = D.Engine(new PerformanceProviderStatus { ProviderId = "k6", Availability = ProviderAvailability.Available, Version = "1.0.0" });
        engine.Label.Should().Be("Available");
        engine.Tone.Should().NotBe(PtTone.Ready, "available is not a pass");
        engine.Reason.Should().Be("The execution engine is available locally. Target execution readiness is evaluated separately.");
    }

    [Fact]
    public void Missing_corporate_ca_blocks_target_execution_as_a_local_prerequisite_with_a_concise_top_state()
    {
        var readiness = new PerformanceTestReadiness { Ready = false, Blockers = ["x"], Items = [CaMissing()] };
        var status = D.PageStatus(false, false, readiness, null, "Development");
        status.Label.Should().Be("Blocked — container TLS trust");
        status.Reason.Should().StartWith("Container TLS trust is not configured.")
            .And.Contain("1 required prerequisite must be resolved before this test can run")
            .And.Contain("Local execution prerequisite — not a performance, target or application result")
            .And.NotContain("certificate chain", "the full explanation is stated once, in the blocker panel");
        D.IsLocalExecutionPrerequisite(CaMissing()).Should().BeTrue();
        D.BlockerCategory(CaMissing()).Should().Be("Local execution prerequisite");
        D.TlsSafetyNote.Should().Contain("TLS verification remains enabled").And.NotContainAny("--insecure", "skip");
    }

    [Fact]
    public void Definition_and_safety_blockers_are_not_local_execution_prerequisites()
    {
        var unsafeEnv = new PerformanceReadinessItem("environment", "Environment and production guard", PerformanceReadinessState.UnsafeEnvironment, "Production cannot be used.", true)
            { Category = PerformanceReadinessCategories.Safety };
        D.IsLocalExecutionPrerequisite(unsafeEnv).Should().BeFalse();
        D.BlockerCategory(unsafeEnv).Should().Be("Safety");
    }

    [Fact]
    public void Optional_capabilities_never_block_and_never_count_as_required()
    {
        var optional = new PerformanceReadinessItem("resources", "Resource observation", PerformanceReadinessState.Optional, "Optional.", false);
        var readiness = new PerformanceTestReadiness { Ready = true, Items = [optional] };
        D.PageStatus(false, false, readiness, null, "Development").Label.Should().Be("Ready");
        var groups = D.Groups(false, false, readiness);
        groups.Single(g => g.Name == "Optional").Checks.Select(c => c.Key).Should().Contain("resources");
        groups.Where(g => g.Name != "Optional").SelectMany(g => g.Checks).Select(c => c.Key).Should().NotContain("resources", "optional capabilities are never required checks");
    }

    [Fact]
    public void Setup_progress_and_execution_readiness_are_worded_as_different_measures()
    {
        var setup = new BirkNext.Integrations.JourneySetupView { JourneyName = "Lookup baseline", RequiredSteps = 10, RequiredReady = 8 };
        D.SetupProgress(setup).Should().Be("Lookup baseline · 8 of 10 setup steps completed");
        D.SetupVersusReadiness.Should().Contain("Guided setup tracks configuration steps").And.Contain("does not directly determine");
    }

    [Fact]
    public void No_run_yet_is_neutral_never_a_performance_result()
    {
        var latest = D.LatestRun(null);
        latest.Label.Should().Be("No run yet");
        latest.Tone.Should().Be(PtTone.Neutral);
    }
}
