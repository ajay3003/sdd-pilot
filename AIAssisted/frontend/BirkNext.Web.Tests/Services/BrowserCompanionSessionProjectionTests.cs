using BirkNext.BrowserCompanion;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The states Browser Discovery shows must never contradict what the companion itself reports. "Paired · not
/// reporting" is a claim that a valid session exists and is simply quiet; it must not survive the session.
///
/// Expired and NotPaired both mean there is nothing to report through, so both read as Not connected. Only a
/// session the backend still holds — Disconnected — is allowed to be described as paired.
/// </summary>
public sealed class BrowserCompanionSessionProjectionTests
{
    [Fact]
    public void AValidSessionWithNoPageReportIsPairedButNotReporting()
    {
        BrowserDiscoveryStates.Of(BrowserCompanionState.Disconnected, hasEvidence: false)
            .Should().Be(BrowserDiscoveryState.PairedNotReporting);
        BrowserDiscoveryStates.SessionLabel(BrowserCompanionState.Disconnected).Should().Be("Paired · not reporting");
    }

    [Theory]
    [InlineData(BrowserCompanionState.Expired)]
    [InlineData(BrowserCompanionState.NotPaired)]
    public void ASessionThatNoLongerExistsIsNeverDescribedAsPaired(BrowserCompanionState state)
    {
        BrowserDiscoveryStates.Of(state, hasEvidence: false).Should().Be(BrowserDiscoveryState.NotConnected);
        BrowserDiscoveryStates.SessionLabel(state).Should().NotBe("Paired · not reporting");
        // And the one action offered is the one that can actually fix it.
        BrowserDiscoveryStates.ShowsPairAction(BrowserDiscoveryState.NotConnected).Should().BeTrue();
        BrowserDiscoveryStates.ShowsGuidance(BrowserDiscoveryState.NotConnected)
            .Should().BeFalse("nothing tells an unpaired browser to refresh a page");
    }

    [Fact]
    public void EvidenceAlreadyCollectedIsNotLostWhenTheSessionEnds()
    {
        // The pages were observed; the session ending does not unobserve them. The session label still tells
        // the truth about the session alongside them.
        BrowserDiscoveryStates.Of(BrowserCompanionState.Expired, hasEvidence: true)
            .Should().Be(BrowserDiscoveryState.EvidenceAvailable);
        BrowserDiscoveryStates.SessionLabel(BrowserCompanionState.Expired).Should().Be("Session expired");
    }

    [Fact]
    public void ReportingIsADistinctStateFromMerelyBeingPaired()
    {
        BrowserDiscoveryStates.Of(BrowserCompanionState.Connected, hasEvidence: false)
            .Should().Be(BrowserDiscoveryState.ConnectedWithoutEvidence);
        BrowserDiscoveryStates.Of(BrowserCompanionState.Connected, hasEvidence: true)
            .Should().Be(BrowserDiscoveryState.EvidenceAvailable);
        // Four distinct session states, four distinct labels: none of them is overloaded.
        new[] { BrowserCompanionState.Connected, BrowserCompanionState.Disconnected,
                BrowserCompanionState.Expired, BrowserCompanionState.NotPaired }
            .Select(BrowserDiscoveryStates.SessionLabel).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// The backend decides when a session is over, and it decides it on the heartbeat. A window far longer than
    /// the companion's own heartbeat is what let BirkNext keep saying "Paired · not reporting" about a session
    /// the extension had already given up on.
    /// </summary>
    [Fact]
    public void TheSessionOutlivesOnlyAFewMissedHeartbeats()
    {
        BrowserCompanionLimits.SessionIdleLifetime.Should().BeGreaterThan(BrowserCompanionLimits.ConnectedWindow);
        BrowserCompanionLimits.SessionIdleLifetime.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
    }
}
