using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Moq;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// A runtime review result belongs to the Target Environment it ran against: switching the active environment clears it (the review's own
/// history keeps it per environment); a run started before a switch keeps its own environment label and is cleared by the next switch.
/// </summary>
public sealed class RuntimeReviewEnvironmentScopeTests
{
    private static FrontendAnalysisContext Context(string id) => new() { ActiveProfile = new FrontendAnalysisProfile { Id = id, Name = id.ToUpperInvariant() }, TargetUrl = $"https://{id}.example.test" };
    private static FrontendQualityReviewReport Report() => new() { GeneratedAt = DateTime.UtcNow };

    [Fact]
    public void Switching_the_active_environment_clears_results_of_the_previous_one()
    {
        var settings = new Mock<IFrontendAnalysisSettingsService>();
        var active = new FrontendAnalysisProfile { Id = "dev" };
        settings.SetupGet(s => s.ActiveProfile).Returns(() => active);
        var session = new RuntimeReviewSessionService(settings.Object);
        session.SaveQualityResult(Report(), Context("dev"));
        session.QualityReview.ProfileId.Should().Be("dev");

        settings.Raise(s => s.Changed += null);
        session.QualityReview.HasResult.Should().BeTrue("a settings save that keeps the same active environment changes nothing");

        active = new FrontendAnalysisProfile { Id = "test" };
        settings.Raise(s => s.Changed += null);
        session.QualityReview.HasResult.Should().BeFalse("dev's result is never shown as the current result for test");
        session.QualityReview.Status.Should().Be(RuntimeReviewStatus.NotRun);
    }

    [Fact]
    public void A_run_finishing_after_a_switch_keeps_its_own_environment_label()
    {
        var settings = new Mock<IFrontendAnalysisSettingsService>();
        settings.SetupGet(s => s.ActiveProfile).Returns(new FrontendAnalysisProfile { Id = "test" });
        var session = new RuntimeReviewSessionService(settings.Object);

        session.SaveQualityResult(Report(), Context("dev"));

        (session.QualityReview.ProfileId, session.QualityReview.ProfileName).Should().Be(("dev", "DEV"), "the result says which environment it ran against");
        settings.Raise(s => s.Changed += null);
        session.QualityReview.HasResult.Should().BeFalse("the next settings change for another active environment clears it");
    }

    [Fact]
    public void Changing_the_same_environments_target_URL_clears_its_results_but_an_unrelated_save_does_not()
    {
        var settings = new Mock<IFrontendAnalysisSettingsService>();
        var active = new FrontendAnalysisProfile { Id = "dev", Name = "DEV", TargetUrl = "https://dev.example.test" };
        settings.SetupGet(s => s.ActiveProfile).Returns(() => active);
        var session = new RuntimeReviewSessionService(settings.Object);
        session.SaveQualityResult(Report(), new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "DEV", TargetUrl = "https://dev.example.test" }, TargetUrl = "https://dev.example.test" });

        active.Notes = "edited notes";
        settings.Raise(s => s.Changed += null);
        session.QualityReview.HasResult.Should().BeTrue("notes are not part of what the result depends on");

        active.TargetUrl = "https://dev2.example.test";
        settings.Raise(s => s.Changed += null);
        session.QualityReview.HasResult.Should().BeFalse("the result reviewed another target");
    }

    [Fact]
    public void Feature_visibility_changes_are_announced_so_navigation_follows_immediately()
    {
        var features = new FeatureVisibilityService();
        var raised = 0;
        features.Changed += () => raised++;

        features.ApplyLocalFlags(new FeatureVisibilityDto { Dashboard = false });

        raised.Should().Be(1);
        features.Dashboard.Should().BeFalse();
    }
}
