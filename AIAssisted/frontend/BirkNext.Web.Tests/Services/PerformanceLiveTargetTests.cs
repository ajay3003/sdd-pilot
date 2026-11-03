using BirkNext.PerformanceTests;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The Target Environment owns a performance definition's target: a definition saved against another origin is brought to the live origin and must be
/// saved again before it can run; the saved values remain only as history in the drift notice.
/// </summary>
public sealed class PerformanceLiveTargetTests
{
    private static FrontendAnalysisProfile Profile(string url, string name = "M2LB DEV") =>
        new() { Id = "dev", Name = name, TargetUrl = url, EnvironmentType = FrontendEnvironmentType.Development };

    [Fact]
    public void A_definition_saved_against_the_current_target_is_unchanged()
    {
        var saved = PerformanceTestPresentation.NewDefinition(Profile("https://app.dev.example.test/start")) with { Id = "d1" };

        var (definition, drift) = PerformanceTestPresentation.WithLiveTarget(saved, Profile("https://app.dev.example.test/other"));

        drift.Should().BeNull("same origin, name and type");
        definition.Should().BeSameAs(saved);
    }

    [Fact]
    public void A_changed_Target_Environment_URL_is_used_and_the_saved_origin_is_reported_never_run_silently()
    {
        var saved = PerformanceTestPresentation.NewDefinition(Profile("https://old.example.test")) with { Id = "d1" };

        var (definition, drift) = PerformanceTestPresentation.WithLiveTarget(saved, Profile("https://new.example.test/app", "M2LB DEV 2"));

        definition.TargetOrigin.Should().Be("https://new.example.test");
        definition.EnvironmentName.Should().Be("M2LB DEV 2");
        drift.Should().Be(new PerformanceTargetDrift("https://old.example.test", "https://new.example.test", "M2LB DEV", "M2LB DEV 2"));
        definition.Id.Should().Be("d1", "the same definition, a new version once saved");
    }

    [Fact]
    public void A_Target_Environment_without_a_URL_clears_the_target_instead_of_keeping_the_stale_origin()
    {
        var saved = PerformanceTestPresentation.NewDefinition(Profile("https://old.example.test")) with { Id = "d1" };

        var (definition, drift) = PerformanceTestPresentation.WithLiveTarget(saved, Profile(""));

        definition.TargetOrigin.Should().BeEmpty();
        drift!.SavedOrigin.Should().Be("https://old.example.test");
    }
}
