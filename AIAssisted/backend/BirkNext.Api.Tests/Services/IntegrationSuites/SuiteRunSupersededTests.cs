using BirkNext.Api.Services.IntegrationSuites;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationSuites;

/// <summary>
/// A suite's outcome is decided by the latest result of its tests: a run whose every test was run again later is superseded (kept in the history,
/// not counted), so a passing retest clears an earlier failure. A partial retest leaves the earlier run counting — its calls are not split per test.
/// </summary>
public sealed class SuiteRunSupersededTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();
    private static readonly Guid Run1 = Guid.NewGuid(), Run2 = Guid.NewGuid(), Run3 = Guid.NewGuid();

    [Fact]
    public void A_full_retest_supersedes_the_earlier_run()
    {
        // newest first: Run2 retested A and B after Run1 (A, B) failed.
        IntegrationSuiteService.Superseded([(Run2, [A, B]), (Run1, [A, B])]).Should().Equal(Run1);
    }

    [Fact]
    public void A_partial_retest_leaves_the_earlier_run_counting()
    {
        IntegrationSuiteService.Superseded([(Run2, [A]), (Run1, [A, B])]).Should().BeEmpty("B was not run again, and Run1's calls cannot be split per test");
    }

    [Fact]
    public void Several_later_runs_together_can_supersede_an_earlier_one()
    {
        IntegrationSuiteService.Superseded([(Run3, [B, C]), (Run2, [A]), (Run1, [A, B])]).Should().Equal(Run1);
    }

    [Fact]
    public void The_newest_run_and_runs_without_recorded_tests_are_never_superseded()
    {
        IntegrationSuiteService.Superseded([(Run2, [A]), (Run1, [])]).Should().BeEmpty();
        IntegrationSuiteService.Superseded([(Run1, [A])]).Should().BeEmpty();
    }
}
