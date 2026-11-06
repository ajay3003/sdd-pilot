using BirkNext.ManualReview;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Web.Tests.Components;

/// <summary>The manual decision panel: saved and reloaded, append-only history, client-side refusal of credentials, never a pass.</summary>
public sealed class ManualDecisionPanelTests : BunitContext
{
    private sealed class FakeApi : IManualReviewApiService
    {
        public List<ManualReviewDecisionView> Stored { get; } = [];
        public int Posts { get; private set; }
        public Task<IReadOnlyList<ManualReviewDecisionView>?> ListAsync(string module, string scopeKey, string? subjectKey = null, CancellationToken ct = default)
        {
            var rows = Stored.Where(d => d.Module == module && d.ScopeKey == scopeKey && (subjectKey is null || d.SubjectKey == subjectKey)).OrderByDescending(d => d.DecidedAt).ToList();
            var latest = rows.GroupBy(r => r.SubjectKey).Select(g => g.First().Id).ToHashSet();
            return Task.FromResult<IReadOnlyList<ManualReviewDecisionView>?>([.. rows.Select(r => r with { Current = latest.Contains(r.Id) })]);
        }
        public Task<(ManualReviewDecisionView? Decision, string? Error)> RecordAsync(ManualReviewDecisionRequest request, CancellationToken ct = default)
        {
            if (ManualReviewRules.Validate(request) is { } error) return Task.FromResult<(ManualReviewDecisionView?, string?)>((null, error));
            Posts++;
            var view = new ManualReviewDecisionView
            {
                Id = Guid.NewGuid(), Module = request.Module, ScopeKey = request.ScopeKey, SubjectKey = request.SubjectKey, Reviewer = request.Reviewer, Decision = request.Decision,
                Note = request.Note, EvidenceReferences = request.EvidenceReferences, DecidedAt = DateTimeOffset.UtcNow.AddSeconds(Stored.Count),
            };
            Stored.Add(view);
            return Task.FromResult<(ManualReviewDecisionView?, string?)>((view, null));
        }
    }

    private readonly FakeApi _api = new();

    public ManualDecisionPanelTests() => Services.AddSingleton<IManualReviewApiService>(_api);

    private IRenderedComponent<ManualDecisionPanel> Panel() => Render<ManualDecisionPanel>(p => p
        .Add(c => c.Module, ManualReviewModules.ApiQuality).Add(c => c.ScopeKey, "dev").Add(c => c.SubjectKey, "Authorization between roles and tenants").Add(c => c.TestId, "mdp"));

    private static void Fill(IRenderedComponent<ManualDecisionPanel> cut, ManualReviewDecisionValue decision, string note, string evidence)
    {
        cut.Find("[data-testid=mdp-reviewer]").Change("Test lead");
        cut.Find("[data-testid=mdp-decision]").Change(decision.ToString());
        cut.Find("[data-testid=mdp-note]").Change(note);
        cut.Find("[data-testid=mdp-evidence]").Change(evidence);
        cut.Find("[data-testid=mdp-save]").Click();
    }

    [Fact]
    public void A_decision_is_saved_reloaded_and_a_second_one_keeps_the_first_as_history()
    {
        var cut = Panel();
        cut.Find("[data-testid=mdp-current]").TextContent.Should().Contain("Not reviewed");
        Fill(cut, ManualReviewDecisionValue.NeedsEvidence, "", "TC-1");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mdp-current]").GetAttribute("data-decision").Should().Be("NeedsEvidence"));
        Fill(cut, ManualReviewDecisionValue.IssueFound, "Role B reads role A's data.", "TC-2");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mdp-current]").GetAttribute("data-decision").Should().Be("IssueFound"));
        cut.FindAll("[data-testid=mdp-history-item]").Should().HaveCount(2);
        cut.FindAll("[data-testid=mdp-history-item][data-current=true]").Should().ContainSingle();

        var reloaded = Panel();
        reloaded.WaitForAssertion(() => reloaded.Find("[data-testid=mdp-current]").GetAttribute("data-decision").Should().Be("IssueFound"));
        reloaded.Find("[data-testid=mdp-current]").TextContent.Should().Contain("Test lead");
    }

    [Fact]
    public void A_credential_is_refused_and_nothing_is_saved()
    {
        var cut = Panel();
        Fill(cut, ManualReviewDecisionValue.Acceptable, "", "Bearer eyJhbGciOi.x.y");
        cut.WaitForAssertion(() => cut.Find("[data-testid=mdp-error]").TextContent.Should().Contain("never the secret"));
        _api.Posts.Should().Be(0);
        cut.Find("[data-testid=mdp-current]").TextContent.Should().Contain("Not reviewed");
    }

    [Fact]
    public void The_panel_says_a_decision_never_changes_the_automated_result()
    {
        Panel().Markup.Should().Contain("never changes the automated result").And.Contain("never makes a check Passed");
    }
}
