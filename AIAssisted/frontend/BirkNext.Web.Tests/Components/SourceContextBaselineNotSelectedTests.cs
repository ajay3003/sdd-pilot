using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using BirkNext.SourceBaselines;
using BirkNext.Web.Components.SourceManagement;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Baseline correction: with alternative versions and no analysis baseline (BASELINE_NOT_SELECTED) the pages say that the source is left out until a
/// person chooses — never "uses the newest import" — the newest import is labelled a non-authoritative preview, no version is shown as used for
/// analysis, and any two versions can still be compared. Expectations come from the rule, not from the markup.
/// </summary>
public sealed class SourceContextBaselineNotSelectedTests : BunitContext
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid V5 = Guid.NewGuid(), V7 = Guid.NewGuid();

    private static SourceContextView Context(bool selected)
    {
        SourceSnapshotView View(Guid id, string name, int minutes, bool newest) => new(id, name, new string('a', 64), new string('c', 64), T0.AddMinutes(minutes), "Ready",
            SourceBranchKind.Unknown, null, null, null, selected && id == V5, newest, selected && id == V5, 1, 100, ["SkoleService"], 0);
        var source = new LogicalSourceView("m2lb", "M2LB", "Archive file name", selected ? SourceBaselineState.Selected : SourceBaselineState.NotSelected,
            selected ? V5 : null, selected ? V5 : null, selected ? "Analysis baseline selected." : "BASELINE_NOT_SELECTED: …", 2, 2,
            [View(V7, "M2LB (7).zip", 2, true), View(V5, "M2LB (5).zip", 1, false)], selected ? "BASELINE_SELECTED" : "BASELINE_NOT_SELECTED", selected ? null : V7);
        return new SourceContextView([source], 1, 2, selected ? 0 : 1, "fp");
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private void Serve(SourceContextView context, SourceContextView? afterSelection = null)
    {
        var http = new HttpClient(new Handler(context, afterSelection)) { BaseAddress = new Uri("http://localhost/") };
        Services.AddSingleton(new SourceContextApiService(http));
    }

    /// <summary>A minimal backend: serves the context; a baseline POST switches it to <paramref name="afterSelection"/> and answers "changed".</summary>
    private sealed class Handler(SourceContextView context, SourceContextView? afterSelection) : HttpMessageHandler
    {
        private SourceContextView _current = context;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body;
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/source-context/baseline" && afterSelection is not null)
            {
                _current = afterSelection;
                body = JsonSerializer.Serialize(new SourceDecisionResult(true, "Analysis baseline set to M2LB (5).zip.", null), Web);
            }
            else body = request.RequestUri!.AbsolutePath == "/api/source-context" ? JsonSerializer.Serialize(_current, Web) : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact] // Real-browser finding: choosing a baseline collapsed the source (decided sources start closed), hiding the choice just made
    public void The_source_stays_open_after_the_person_chooses_its_baseline()
    {
        Serve(Context(selected: false), afterSelection: Context(selected: true));
        var cut = Render<SourceContextPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=scp-snapshot]").Should().HaveCount(2));
        cut.Find($"[data-testid=scp-snapshot][data-snapshot='{V5}'] [data-testid=scp-select]").Click();
        cut.WaitForAssertion(() => cut.Find($"[data-testid=scp-snapshot][data-snapshot='{V5}'] [data-testid=scp-baseline-badge]"));
        cut.Find("[data-testid=scp-notice]").TextContent.Should().Contain("Analysis baseline set to");
    }

    [Fact]
    public void The_notice_says_the_source_is_left_out_and_never_that_the_newest_import_is_used()
    {
        Serve(Context(selected: false));
        var cut = Render<SourceContextNotice>(p => p.Add(x => x.Purpose, "Integration Topology"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=source-context-notice]").GetAttribute("data-state").Should().Be("needs-baseline"));
        cut.Find("[data-testid=source-context-notice-code]").GetAttribute("data-code").Should().Be("BASELINE_NOT_SELECTED");
        var text = cut.Find("[data-testid=source-context-notice]").TextContent;
        text.Should().Contain("Integration Topology leaves it out until you choose").And.Contain("never picks a branch from import order");
        text.Should().NotContain("uses the newest import");
    }

    [Fact]
    public void The_panel_labels_the_newest_import_a_preview_shows_nothing_as_used_and_offers_a_free_comparison()
    {
        Serve(Context(selected: false));
        var cut = Render<SourceContextPanel>();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=scp-snapshot]").Should().HaveCount(2));
        cut.Find("[data-testid=scp-source]").GetAttribute("data-code").Should().Be("BASELINE_NOT_SELECTED");
        cut.Find("[data-testid=scp-state]").TextContent.Should().Be("Baseline not selected");
        cut.FindAll("[data-testid=scp-snapshot][data-used=true]").Should().BeEmpty("nothing is authoritative without a baseline");
        cut.FindAll("[data-testid=scp-used-badge]").Should().BeEmpty();
        var preview = cut.Find($"[data-testid=scp-snapshot][data-snapshot='{V7}'] [data-testid=scp-preview-badge]");
        preview.TextContent.Should().Contain("preview only, not authoritative");
        cut.Find("[data-testid=scp-compare]").TextContent.Should().Be("Compare with the newest import");
        cut.Find("[data-testid=scp-pair-from]").GetAttribute("value").Should().Be(V5.ToString());
        cut.Find("[data-testid=scp-pair-to]").GetAttribute("value").Should().Be(V7.ToString());
        cut.Find("[data-testid=scp-pair-go]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void With_a_baseline_the_chosen_version_is_used_and_there_is_no_preview()
    {
        Serve(Context(selected: true));
        var cut = Render<SourceContextPanel>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=scp-source-toggle]"));
        cut.FindAll("[data-testid=scp-snapshot]").Should().BeEmpty("a decided source starts collapsed; an undecided one opens by itself");
        cut.Find("[data-testid=scp-source-toggle]").Click();
        cut.FindAll("[data-testid=scp-snapshot]").Should().HaveCount(2);
        cut.Find("[data-testid=scp-source]").GetAttribute("data-code").Should().Be("BASELINE_SELECTED");
        cut.FindAll("[data-testid=scp-preview-badge]").Should().BeEmpty();
        cut.Find($"[data-testid=scp-snapshot][data-snapshot='{V5}']").GetAttribute("data-used").Should().Be("true");
        cut.Find("[data-testid=scp-compare]").TextContent.Should().Be("Compare with the version in use");
    }

    [Theory] // Source Analysis says which version a shown snapshot is; it never implies the newest import is the analysed one
    [InlineData(false, "v7", "BASELINE_NOT_SELECTED")]
    [InlineData(true, "v5", "ANALYSIS_BASELINE")]
    [InlineData(true, "v7", "ALTERNATIVE_VERSION")]
    public void The_version_badge_states_the_role_of_the_shown_snapshot(bool selected, string shown, string code)
    {
        Serve(Context(selected));
        var cut = Render<SourceVersionBadge>(p => p.Add(x => x.SnapshotId, shown == "v5" ? V5 : V7));
        cut.WaitForAssertion(() => cut.Find("[data-testid=source-version-badge]").GetAttribute("data-code").Should().Be(code));
    }
}
