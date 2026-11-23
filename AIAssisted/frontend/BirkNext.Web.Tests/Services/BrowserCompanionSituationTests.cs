using BirkNext.BrowserCompanion;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Pairing and connection are separate facts, and a pairing code waiting to be entered is not a pairing (2026-11-23).</summary>
public sealed class BrowserCompanionSituationTests
{
    [Theory]
    [InlineData(BrowserCompanionState.NotPaired, BrowserCompanionSituation.NotPaired, "Not paired", "—")]
    [InlineData(BrowserCompanionState.Expired, BrowserCompanionSituation.NotPaired, "Not paired", "—")]
    // The backend reports PairingPending only while no session exists: showing "Paired" put "paired but not connected" next to a pairing code.
    [InlineData(BrowserCompanionState.PairingPending, BrowserCompanionSituation.NotPaired, "Not paired", "—")]
    [InlineData(BrowserCompanionState.Disconnected, BrowserCompanionSituation.PairedNotConnected, "Paired", "Not connected")]
    [InlineData(BrowserCompanionState.Connected, BrowserCompanionSituation.ConnectedWithoutLivePage, "Paired", "Connected")]
    public void Each_backend_state_has_one_pairing_and_one_connection_label(BrowserCompanionState state, BrowserCompanionSituation expected, string pairing, string connection)
    {
        var situation = BrowserCompanionSituations.Of(new BrowserCompanionStatus { State = state });
        situation.Should().Be(expected);
        BrowserCompanionSituations.PairingLabel(situation).Should().Be(pairing);
        BrowserCompanionSituations.ConnectionLabel(situation).Should().Be(connection);
    }
}
