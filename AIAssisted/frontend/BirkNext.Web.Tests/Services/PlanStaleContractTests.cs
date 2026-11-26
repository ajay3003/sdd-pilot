using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Both 409 draft-changed messages of the plan service (the base-revision check and the concurrent-write race found in the V9 browser
/// journey, 2026-11-26) are recognised as stale, so Planlegg's automatic translation retries on the new revision instead of reporting a failure.</summary>
public sealed class PlanStaleContractTests
{
    [Theory]
    [InlineData("The proposal is at revision 4; reload it. Your change was not saved.")]
    [InlineData("The proposal was changed by someone else. Reload it; your change was not saved.")]
    [InlineData("The proposal was changed by someone else at the same time (another save or an automatic translation). Reload it; your change was not saved.")]
    public void Every_revision_conflict_message_is_stale(string serverMessage) =>
        PlanPreparePresentation.IsStale(serverMessage).Should().BeTrue();
}
