using BirkNext.Api.Data;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// A journey run never claims success it did not observe: without a mandatory step nothing is verified, and a run whose process is gone is
/// recorded as interrupted (NotVerified with the reason) — never left Running forever and never read as "never run".
/// </summary>
public sealed class JourneyRunOutcomeTests
{
    private static JourneyStepDefinition Step(string id, bool mandatory) => new(id, id, default, "owner", "evidence", mandatory);

    [Fact]
    public void A_journey_without_mandatory_steps_is_not_verified_never_completed()
    {
        var journey = new IntegrationJourneyDefinition { Steps = [Step("optional", mandatory: false)] };

        IntegrationJourneyRunner.Overall(journey, [new JourneyStepResult { StepId = "optional", State = JourneyStepState.Verified }])
            .Should().Be((JourneyRunState.NotVerified, "The journey defines no mandatory step, so no outcome could be verified."));
        IntegrationJourneyRunner.Overall(journey, []).State.Should().Be(JourneyRunState.NotVerified);
        IntegrationJourneyRunner.Overall(journey, [new JourneyStepResult { StepId = "optional", State = JourneyStepState.UnexpectedResult }]).State
            .Should().Be(JourneyRunState.Failed, "an unexpected result is still a failure");
        IntegrationJourneyRunner.Overall(new IntegrationJourneyDefinition { Steps = [Step("a", true)] }, [new JourneyStepResult { StepId = "a", State = JourneyStepState.Verified }]).State
            .Should().Be(JourneyRunState.Completed, "a verified mandatory step still completes");
    }

    [Fact]
    public async Task A_running_row_from_a_gone_process_is_recovered_as_interrupted_and_a_live_run_is_left_alone()
    {
        var services = new ServiceCollection();
        var db = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        await using var provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var live = new IntegrationJourneyRunStore(scopes, NullLogger<IntegrationJourneyRunStore>.Instance);
        var run = new IntegrationJourneyRun { RunId = Guid.NewGuid(), EnvironmentId = "dev", PackId = "pack", JourneyId = "j", StartedAt = DateTimeOffset.UtcNow };
        (await live.InsertAsync(run, default)).Should().BeTrue();

        (await live.GetAsync(run.RunId, default))!.OverallState.Should().Be(JourneyRunState.Running, "the run is still executing in this process");

        var afterRestart = new IntegrationJourneyRunStore(scopes, NullLogger<IntegrationJourneyRunStore>.Instance);
        var recovered = (await afterRestart.ListAsync(new IntegrationJourneyHistoryQuery("dev"), 10, default)).Single();
        recovered.OverallState.Should().Be(JourneyRunState.NotVerified);
        recovered.StateReason.Should().Be(IntegrationJourneyRunStore.InterruptedReason);
        recovered.CompletedAt.Should().NotBeNull();
        (await afterRestart.GetAsync(run.RunId, default))!.OverallState.Should().Be(JourneyRunState.NotVerified, "the recovery is persisted");
        JourneyReadinessComposer.Assessment(recovered.OverallState).Should().Be(JourneyAssessmentState.ExecutedNotVerified, "an interrupted run is not 'never run'");
    }
}
