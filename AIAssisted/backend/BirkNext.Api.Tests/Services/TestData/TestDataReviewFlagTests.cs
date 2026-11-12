using BirkNext.Api.Services;
using BirkNext.SourceImpact;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestData;

/// <summary>MT-6: Test Data assets marked by stored Source Change Impact runs; the newest run per asset wins and only Test Data items count.</summary>
public sealed class TestDataReviewFlagTests
{
    private static TechnicalImpactItem Item(string type, string alias, string reason) =>
        new($"{type}:{alias}", type, alias, alias, ImpactChangeDomain.Contracts, TechnicalImpactLevel.Potential, "Inferred", reason, [], [], []);

    private static SourceChangeImpactReport Report(params TechnicalImpactItem[] items) =>
        new(Guid.NewGuid(), "r", "a.zip", "f1", DateTimeOffset.UnixEpoch, Guid.NewGuid(), "r", "b.zip", "f2", DateTimeOffset.UnixEpoch, [], [.. items], [], [], [], [], [], "Potential");

    [Fact]
    public void The_newest_run_per_asset_wins_and_the_state_is_read_from_the_reason()
    {
        var older = (Guid.NewGuid(), DateTimeOffset.Parse("2026-11-10T10:00:00Z"), (string?)"P", Report(Item("Test data asset", "child", "PotentiallyStale: entity Child changed"), Item("Test data asset", "report", "ReviewRequired: old")));
        var newer = (Guid.NewGuid(), DateTimeOffset.Parse("2026-11-12T10:00:00Z"), (string?)"P", Report(Item("Test data asset", "report", "ReviewRequired: schema changed"), Item("Journey", "x", "ReviewRequired: n/a")));
        var flags = SourceChangeImpactService.TestDataReviewFlags([older, newer]);
        flags.Select(f => (f.Alias, f.State, f.Reason)).Should().Equal(("child", "PotentiallyStale", "entity Child changed"), ("report", "ReviewRequired", "schema changed"));
        flags.Single(f => f.Alias == "report").RunId.Should().Be(newer.Item1);
    }
}
