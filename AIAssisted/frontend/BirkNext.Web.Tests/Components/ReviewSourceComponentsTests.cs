using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// The shared source-evidence components every source-aware review uses: the prerequisite gate (guidance only, never a result, never an
/// upload), the snapshot selector (current snapshot offered, never chosen silently), the scope panel (exact primary, newer snapshots offered,
/// related sources suggested and included only explicitly) and provenance. Generic repositories only.
/// </summary>
public sealed class ReviewSourceComponentsTests : BunitContext
{
    private static ReviewSourceSnapshot Snap(string id, string repository, string archive, string fingerprint, string at, bool latest, bool evidence = true) => new()
    {
        SnapshotId = Guid.Parse(id), RepositoryKey = repository.ToLowerInvariant(), Repository = repository, IdentityBasis = "Root solution file", ArchiveName = archive,
        Fingerprint = fingerprint + new string('0', 64 - fingerprint.Length), AnalyzedAt = DateTimeOffset.Parse(at), SourceStatus = "Partial", Latest = latest,
        HasConsumerEvidence = evidence, ConsumerEvidenceNote = evidence ? null : "Analyzed before this review's evidence was captured.", ConsumerSummary = evidence ? "12 rule(s)" : null,
    };

    private static readonly ReviewSourceSnapshot AppOld = Snap("dddddddd-0000-0000-0000-000000000001", "AppRepo", "AppRepo.zip", "0ld00000", "2026-10-01T08:00:00Z", latest: false);
    private static readonly ReviewSourceSnapshot App = Snap("dddddddd-0000-0000-0000-000000000002", "AppRepo", "AppRepo (2).zip", "a8f94deb", "2026-10-01T12:00:00Z", latest: true);
    private static readonly ReviewSourceSnapshot Shared = Snap("dddddddd-0000-0000-0000-000000000003", "Shared.Security", "Shared.Security.zip", "22cda41e", "2026-10-01T09:00:00Z", latest: true);
    private static readonly RelatedSourceCandidate SharedCandidate = new()
    {
        RepositoryKey = "shared.security", Repository = "Shared.Security", State = RelatedSourceState.SnapshotAvailable, Reason = "Declares a type the primary source uses.",
        Evidence = [new("Type reference", "Declares CdcEvent (exact type name).", 0, [])], MatchingSnapshotIds = [Shared.SnapshotId],
    };

    private static ReviewSourceScopeState State(params ReviewSourceSnapshot[] snapshots) => new() { Options = new ReviewSourceOptions { Snapshots = [.. snapshots] } };

    private static void Resolve(ReviewSourceScopeState state, params RelatedSourceCandidate[] candidates) =>
        state.Options = Pages.SourceScopeFixture.Resolve(new ReviewSourceOptions { Snapshots = [.. state.Snapshots] }, state.Request, [.. candidates]);

    private IRenderedComponent<SourceEvidencePrerequisite> Gate(ReviewSourceScopeState state, bool enabled = true, ReviewSourceRequirement requirement = ReviewSourceRequirement.RequiredForSourceChecks) =>
        Render<SourceEvidencePrerequisite>(p => p.Add(c => c.State, state).Add(c => c.SourceAnalysisEnabled, enabled).Add(c => c.Requirement, requirement).Add(c => c.Prefix, "t")
            .Add(c => c.ConsumerName, "Generic review").Add(c => c.ReturnTo, "dependency-review").Add(c => c.OtherSectionsNote, "Runtime sections stay usable.")
            .AddChildContent("<p data-testid='t-child'>source controls</p>"));

    [Fact]
    public void NoSnapshotGuidesToSourceAnalysisWithoutAnyUploadAndWithoutBlockingRuntime()
    {
        var cut = Gate(State());

        cut.Find("[data-testid=t-source-gate]").GetAttribute("data-state").Should().Be("NoSnapshotAvailable");
        cut.Find("[data-testid=t-source-state]").TextContent.Should().Be("No snapshot available");
        cut.Find("[data-testid=t-source-empty]").TextContent.Should().Contain("No source snapshot available").And.Contain("Generic review uses source snapshots managed by Source Analysis");
        cut.Find("[data-testid=t-open-source-analysis]").TextContent.Should().Be("Open Source Analysis");
        cut.Find("[data-testid=t-open-source-analysis]").GetAttribute("href").Should().Be("source-analysis?returnTo=dependency-review");
        cut.Find("[data-testid=t-source-other-sections]").TextContent.Should().Be("Runtime sections stay usable.");
        cut.FindAll("input[type=file]").Should().BeEmpty();
        cut.FindAll("[data-testid=t-child]").Should().BeEmpty();
    }

    [Fact]
    public void DisabledAndLoadingStatesAreGuidanceOnly()
    {
        var disabled = Gate(State(App), enabled: false);
        disabled.Find("[data-testid=t-source-gate]").GetAttribute("data-state").Should().Be("SourceAnalysisDisabled");
        disabled.Find("[data-testid=t-source-disabled]").TextContent.Should().Contain("Source Analysis is disabled").And.Contain("no snapshot or result is removed");
        disabled.FindAll("[data-testid=t-child]").Should().BeEmpty();

        var loading = Gate(new ReviewSourceScopeState());
        loading.Find("[data-testid=t-source-loading]").GetAttribute("role").Should().Be("status");
    }

    [Fact]
    public void OptionalEnrichmentNeverReadsAsARequirement()
    {
        var cut = Gate(State(), requirement: ReviewSourceRequirement.OptionalEnrichment);

        cut.Find("[data-testid=t-source-gate]").GetAttribute("data-state").Should().Be("NotSelected");
        cut.Find("[data-testid=t-source-gate]").TextContent.Should().Contain("Optional — the review runs without it").And.Contain("runs without it").And.NotContain("Required for source checks");
        Gate(State(App), requirement: ReviewSourceRequirement.OptionalEnrichment).Find("[data-testid=t-source-state]").TextContent.Should().Be("Available");
    }

    [Fact]
    public void SelectorOffersTheCurrentSnapshotWithoutChoosingIt()
    {
        var state = State(AppOld, App, Shared);
        Guid? picked = null;
        var cut = Render<SourceSnapshotSelector>(p => p.Add(c => c.State, state).Add(c => c.Prefix, "t").Add(c => c.OnPrimaryChanged, EventCallback.Factory.Create<Guid?>(this, id => picked = id)));

        state.PrimaryId.Should().BeNull();
        cut.Find("[data-testid=t-current-snapshot]").TextContent.Should().Contain("AppRepo").And.Contain("a8f94deb…").And.Contain("Source Analysis: Partial");
        cut.FindAll("[data-testid=t-primary] optgroup").Select(g => g.GetAttribute("label")).Should().Equal("AppRepo", "Shared.Security");
        cut.FindAll("[data-testid=t-primary] option").Select(o => o.TextContent).Should().Contain(o => o.Contains("AppRepo (2).zip") && o.Contains("latest")).And.Contain(o => o.Contains("AppRepo.zip") && o.Contains("older"));
        cut.Find("[data-testid=t-primary]").ParentElement!.TagName.Should().Be("LABEL");
        cut.Find("[data-testid=t-choose-another]").Click();
        state.PrimaryId.Should().BeNull("choosing another only moves focus");

        cut.Find("[data-testid=t-use-current]").Click();

        state.PrimaryId.Should().Be(App.SnapshotId);
        picked.Should().Be(App.SnapshotId);
        cut.FindAll("[data-testid=t-current-snapshot]").Should().BeEmpty();
    }

    [Fact]
    public void PanelShowsTheExactPrimaryAndOffersANewerSnapshotWithoutSwitching()
    {
        var state = State(AppOld, App, Shared);
        state.PickPrimary(AppOld.SnapshotId);
        Resolve(state);
        var changed = 0;
        var cut = Render<ReviewSourceScopePanel>(p => p.Add(c => c.State, state).Add(c => c.Prefix, "t").Add(c => c.EvidenceLabel, "Review evidence")
            .Add(c => c.OnScopeChanged, EventCallback.Factory.Create(this, () => { changed++; Resolve(state); })));

        cut.Find("[data-testid=t-primary-summary]").GetAttribute("data-snapshot").Should().Be(AppOld.SnapshotId.ToString());
        cut.Find("[data-testid=t-primary-fingerprint]").TextContent.Should().Be("0ld00000…");
        cut.Find("[data-testid=t-primary-status]").TextContent.Should().Contain("Partial").And.Contain("not a review result");
        cut.Find("[data-testid=t-primary-evidence]").TextContent.Should().Be("12 rule(s)");
        var notice = cut.Find("[data-testid=t-newer-snapshot]");
        notice.GetAttribute("role").Should().Be("note");
        notice.TextContent.Should().Contain("Newer source snapshot available").And.Contain("0ld00000…").And.Contain("a8f94deb…").And.Contain("not switched automatically");

        cut.Find("[data-testid=t-keep-current]").Click();
        cut.FindAll("[data-testid=t-newer-snapshot]").Should().BeEmpty();
        state.PrimaryId.Should().Be(AppOld.SnapshotId);
        changed.Should().Be(0, "keeping the current snapshot changes nothing");
    }

    [Fact]
    public void ReviewNewerSnapshotSwitchesOnlyOnTheExplicitAction()
    {
        var state = State(AppOld, App);
        state.PickPrimary(AppOld.SnapshotId);
        Resolve(state);
        var cut = Render<ReviewSourceScopePanel>(p => p.Add(c => c.State, state).Add(c => c.Prefix, "t").Add(c => c.OnScopeChanged, EventCallback.Factory.Create(this, () => Resolve(state))));

        cut.Find("[data-testid=t-review-newer]").Click();

        state.PrimaryId.Should().Be(App.SnapshotId);
    }

    [Fact]
    public void RelatedSourcesAreSuggestedIncludedOnlyExplicitlyAndContinuingWithoutIsRecorded()
    {
        var state = State(App, Shared);
        state.PickPrimary(App.SnapshotId);
        Resolve(state, SharedCandidate);
        var cut = Render<ReviewSourceScopePanel>(p => p.Add(c => c.State, state).Add(c => c.Prefix, "t").Add(c => c.OnScopeChanged, EventCallback.Factory.Create(this, () => Resolve(state, SharedCandidate))));

        cut.Find("[data-testid=t-related-candidate]").TextContent.Should().Contain("Related source suggested: Shared.Security").And.Contain("Suggested").And.Contain("Snapshot available").And.Contain("Type reference");
        state.Related.Should().BeEmpty("a suggestion is never included automatically");
        cut.Find("[data-testid=t-related-count]").TextContent.Should().Contain("None included");
        cut.Find("[data-testid=t-scope-incomplete]").TextContent.Should().Contain("Review scope may be incomplete").And.Contain("recorded as a scope limitation");

        cut.Find("[data-testid=t-related-include]").Click();
        state.Request!.RelatedSnapshotIds.Should().Equal(Shared.SnapshotId);
        state.Options!.Scope!.Related.Should().ContainSingle(r => r.SnapshotId == Shared.SnapshotId, "two entries — never merged");
        cut.Find("[data-testid=t-related-included]").TextContent.Should().Contain("Included");

        cut.Find("[data-testid=t-related-remove]").Click();
        state.Related.Should().BeEmpty();
        cut.Find("[data-testid=t-continue-without]").Click();
        state.Request!.ExcludedSuggestions.Should().Equal("Shared.Security");
        cut.Find("[data-testid=t-continued-without]").TextContent.Should().Contain("recorded as a scope limitation");
    }

    [Fact]
    public void RelatedSourceWithoutSnapshotPointsToSourceAnalysisAndAReviewMayNotSupportRelatedSources()
    {
        var state = State(App);
        state.PickPrimary(App.SnapshotId);
        var missing = SharedCandidate with { State = RelatedSourceState.SnapshotUnavailable, MatchingSnapshotIds = [] };
        Resolve(state, missing);
        var cut = Render<ReviewSourceScopePanel>(p => p.Add(c => c.State, state).Add(c => c.Prefix, "t"));

        cut.Find("[data-testid=t-related-missing]").TextContent.Should().Contain("No analyzed Source Analysis snapshot is available");
        cut.Find("[data-testid=t-related-open-source-analysis]").GetAttribute("href").Should().Be("source-analysis");
        cut.FindAll("[data-testid=t-related-include]").Should().BeEmpty();

        var single = Render<ReviewSourceScopePanel>(p => p.Add(c => c.State, state).Add(c => c.Prefix, "u").Add(c => c.SupportsRelated, false));
        single.FindAll("[data-testid=u-related]").Should().BeEmpty();
        single.Find("[data-testid=u-primary-summary]").Should().NotBeNull();
    }

    [Fact]
    public void AnUnavailableSelectionIsShownForRepairNeverSubstituted()
    {
        var state = State(App);
        state.PickPrimary(Guid.Parse("dddddddd-0000-0000-0000-00000000ffff"));
        Resolve(state);
        var cut = Gate(state);

        cut.Find("[data-testid=t-source-gate]").GetAttribute("data-state").Should().Be("SelectedSnapshotUnavailable");
        cut.Find("[data-testid=t-source-state]").TextContent.Should().Be("Needs repair");
        var panel = Render<ReviewSourceScopePanel>(p => p.Add(c => c.State, state).Add(c => c.Prefix, "t"));
        panel.Find("[data-testid=t-scope-error]").TextContent.Should().Contain("nothing is substituted");
        panel.FindAll("[data-testid=t-primary-summary]").Should().BeEmpty();
    }

    [Fact]
    public void ProvenanceShowsRepositoryShortFingerprintAndAnalysisTime()
    {
        var entry = new SourceScopeEntry { SnapshotId = App.SnapshotId, Repository = "AppRepo", ArchiveName = "AppRepo (2).zip", Fingerprint = App.Fingerprint, AnalyzedAt = App.AnalyzedAt };
        var cut = Render<SourceEvidenceProvenance>(p => p.Add(c => c.Entry, entry).Add(c => c.Role, "Primary").Add(c => c.TestId, "prov"));

        cut.Find("[data-testid=prov]").TextContent.Should().Contain("Primary").And.Contain("AppRepo").And.Contain("a8f94deb…").And.Contain("AppRepo (2).zip").And.Contain("2026-10-01 12:00 UTC");
        cut.Find("code").GetAttribute("title").Should().Be(App.Fingerprint);
    }
}
