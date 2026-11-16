using BirkNext.ApiReview;
using BirkNext.TechnicalHistory;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Follow-up of a technical finding: tracked from a stored run, moved only along the allowed lifecycle with the version the tester saw, and a
/// retest offered only from later stored runs of the same environment (the server verifies absence). Without a stored run it cannot start.
/// </summary>
public sealed class TechnicalFindingFollowUpTests : BunitContext
{
    private const string Project = "import:lendo";
    private static readonly Guid Original = Guid.NewGuid(), Later = Guid.NewGuid(), OtherEnv = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private readonly Mock<ITechnicalHistoryApiService> _api = new();
    private static readonly ApiReviewFinding Finding = new() { Id = "sec-no-hsts:loans", RuleId = "sec-no-hsts", TargetId = "loans", Endpoint = "/loans", Title = "No HSTS header" };

    private static TechnicalFinding Followed(TechnicalFindingStatus status) => new()
    {
        Id = Guid.NewGuid(), ProjectKey = Project, Source = TechnicalFindingRules.ApiQualityReview, FindingKey = "sec-no-hsts|loans|/loans", Title = "No HSTS header",
        EnvironmentId = "qa", OriginalRunId = Original, FirstSeenAt = T0, Status = status, Version = 3, History = [new(T0, "Kari", TechnicalFindingStatus.New, status, null)],
    };

    private static List<StoredApiReviewRun> Runs =>
    [
        new(Original, Project, "qa", "QA", T0, T0, 1, 1, 0, 1, 0, 0, 0, T0),
        new(Later, Project, "qa", "QA", T0.AddHours(2), T0.AddHours(2), 1, 1, 0, 0, 0, 0, 0, T0.AddHours(2)),
        new(OtherEnv, Project, "prod", "PROD", T0.AddHours(3), T0.AddHours(3), 1, 1, 0, 0, 0, 0, 0, T0.AddHours(3)),
    ];

    private IRenderedComponent<TechnicalFindingFollowUp> Open(TechnicalFinding? followed, Guid? runId = null)
    {
        Services.AddSingleton(_api.Object);
        return Render<TechnicalFindingFollowUp>(p => p.Add(x => x.ProjectKey, Project).Add(x => x.RunId, runId ?? Original).Add(x => x.Finding, Finding)
            .Add(x => x.Followed, followed).Add(x => x.Runs, Runs));
    }

    [Fact]
    public void A_finding_of_a_stored_run_is_tracked_and_without_a_stored_run_it_cannot_be()
    {
        TechnicalFinding? changed = null;
        _api.Setup(a => a.TrackAsync(It.Is<TrackTechnicalFindingRequest>(r => r.ProjectKey == Project && r.RunId == Original && r.FindingId == Finding.Id), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Followed(TechnicalFindingStatus.New), (string?)null));
        Services.AddSingleton(_api.Object);
        var cut = Render<TechnicalFindingFollowUp>(p => p.Add(x => x.ProjectKey, Project).Add(x => x.RunId, Original).Add(x => x.Finding, Finding)
            .Add(x => x.Changed, (TechnicalFinding f) => changed = f));
        cut.Find("[data-testid=tfu-track]").Click();
        cut.WaitForAssertion(() => changed!.Status.Should().Be(TechnicalFindingStatus.New));

        var unstored = Render<TechnicalFindingFollowUp>(p => p.Add(x => x.ProjectKey, Project).Add(x => x.RunId, (Guid?)null).Add(x => x.Finding, Finding));
        unstored.Find("[data-testid=tfu-track]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Only_allowed_moves_are_offered_and_a_retest_comes_from_later_runs_of_the_same_environment()
    {
        var cut = Open(Followed(TechnicalFindingStatus.ResolvedPendingRetest));
        cut.Find("[data-testid=tfu-status]").TextContent.Should().Be("Resolved — retest pending");
        cut.Find("[data-testid=tfu-change]").Click();
        cut.FindAll("[data-testid=tfu-to] option").Select(o => o.GetAttribute("value")).Should().Equal("VerifiedResolved", "Reopened");
        cut.FindAll("[data-testid=tfu-retest] option").Select(o => o.GetAttribute("value")).Should().Equal(new[] { "", Later.ToString() },
            "only a later run of the same environment can retest it (not the original run, not another environment)");

        _api.Setup(a => a.UpdateAsync(Project, It.IsAny<Guid>(), It.IsAny<UpdateTechnicalFindingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(((TechnicalFinding?)null, "The retest run still shows the finding; reopen it instead."));
        cut.Find("[data-testid=tfu-retest]").Change(Later.ToString());
        cut.Find("[data-testid=tfu-save]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tfu-error]").TextContent.Should().Contain("still shows the finding"));
        _api.Verify(a => a.UpdateAsync(Project, It.IsAny<Guid>(), It.Is<UpdateTechnicalFindingRequest>(r => r.Version == 3 && r.Status == TechnicalFindingStatus.VerifiedResolved && r.RetestRunId == Later),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
