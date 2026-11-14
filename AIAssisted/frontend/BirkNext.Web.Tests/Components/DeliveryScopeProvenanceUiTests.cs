using BirkNext.DeliveryReporting;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Sc = BirkNext.Scopes;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Shared scope provenance in the Functional Quality Scope view: freezing a snapshot while a shared scope is selected sends the exact scope ids,
/// revision numbers and the revision ids the Scope Registry reports (resolved once, never invented); frozen snapshots compared A vs B say whether
/// they used the same scope revision, and a percentage change is shown only like-for-like; snapshots frozen before scope support say so.
/// </summary>
public sealed class DeliveryScopeProvenanceUiTests : BunitContext
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid ScopeA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid RevisionA2 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid RevisionA3 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset Friday = new(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);
    private readonly DeliveryReportingSession _session = new();

    public DeliveryScopeProvenanceUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_session);
    }

    private static readonly ScopeItem Story = new("story:SkoleService/001-rapport/US1", ScopeArtifactType.UserStory, "US1", "Skoletjenesten tar imot en rapport", QualityCategory.Functional,
        "fq.functional.behaviour", "SkoleService", "001-rapport", "Motta rapport", null, "P1", ScopeRelation.Direct, ScopeInclusion.Smart, "Owned by module SkoleService",
        ScopeCoverage.NoKnownCoverage, null, ScopeTestDataState.Unknown, TestabilityClassification.Unclassified, false, "SkoleService/specs/001-rapport/spec.md", 12);

    private static ScopeResolution Resolution() => new(new ScopeDefinition { Name = "Skolenærvær", Roots = ["profile:skolenaervaer"], BuiltIn = true }, Guid.NewGuid(), "Monday",
        FunctionalQualityClassifier.RuleSetVersion, [Story],
        new(1, 0, 0, 0, 0, new Dictionary<QualityCategory, int> { [QualityCategory.Functional] = 1 }, new Dictionary<ScopeCoverage, int> { [ScopeCoverage.NoKnownCoverage] = 1 }, 0, 0),
        [], [], FunctionalQualityScopeText.Precedence);

    private static DeliveryScopeProvenance Provenance(int revision, Guid? revisionId) => new()
    {
        Scopes = [new(ScopeA, "Release 1", revision, revisionId)], MembershipResolution = DeliveryScopeProvenance.DefaultMembershipResolution, CreatedAt = Friday,
    };

    private static ScopeSnapshotView Frozen(int items, DeliveryScopeProvenance? provenance, int minutes) =>
        new(Guid.NewGuid(), null, "Skolenærvær", 1, Guid.NewGuid(), FunctionalQualityClassifier.RuleSetVersion, items, Friday.AddMinutes(minutes)) { ScopeProvenance = provenance };

    private Mock<IFunctionalQualityScopeApiService> Api(params ScopeSnapshotView[] snapshots)
    {
        var api = new Mock<IFunctionalQualityScopeApiService>();
        api.Setup(a => a.AvailableAsync(Project, It.IsAny<CancellationToken>()))
            .ReturnsAsync(([new AvailableScope("profile:skolenaervaer", "Skolenærvær", "Domain flow", "1 module(s)")], (string?)null));
        api.Setup(a => a.ResolveAsync(Project, It.IsAny<ScopeQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resolution(), (string?)null));
        api.Setup(a => a.SnapshotsAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync(snapshots);
        Services.AddSingleton(api.Object);
        return api;
    }

    /// <summary>The shared Scope Registry with one approved scope at revision 3 (revision 2 also exists), selected through the URL.</summary>
    private Mock<IScopeRegistryApiService> Registry()
    {
        var scopes = new Mock<IScopeRegistryApiService>();
        scopes.Setup(x => x.ListAsync("import-1", It.IsAny<CancellationToken>())).ReturnsAsync(([
            new Sc.ScopeSummary { Id = ScopeA, Name = "Release 1", State = Sc.ScopeState.Approved, ApprovedRevision = 3, CurrentRevision = 3 }], (string?)null));
        scopes.Setup(x => x.SelectionAsync("import-1", It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new Sc.ScopeSelection { ProjectKey = "import-1", Scopes = [new(ScopeA, "Release 1", 3)] }, (string?)null));
        var detail = new Sc.ScopeDetail
        {
            Scope = new Sc.ScopeSummary { Id = ScopeA, Name = "Release 1", State = Sc.ScopeState.Approved, ApprovedRevision = 3, CurrentRevision = 3 },
            Revisions = [new Sc.ScopeRevisionView { Id = RevisionA2, Number = 2 }, new Sc.ScopeRevisionView { Id = RevisionA3, Number = 3 }],
        };
        scopes.Setup(x => x.GetAsync("import-1", ScopeA, It.IsAny<CancellationToken>())).ReturnsAsync(((Sc.ScopeDetail?)detail, (string?)null));
        Services.AddSingleton(scopes.Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(
            BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "import-1", "Northwind Orders", WorkspaceArtifactType.Specification)).Object);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"delivery-reporting?tab=scope&scope={ScopeA}");
        return scopes;
    }

    [Fact]
    public void Freezing_with_a_shared_scope_selected_sends_its_provenance_with_the_resolved_revision_id()
    {
        var api = Api();
        var sent = new List<ScopeQuery>();
        api.Setup(a => a.FreezeAsync(Project, It.IsAny<ScopeQuery>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, ScopeQuery, CancellationToken>((_, q, _) => sent.Add(q))
            .ReturnsAsync((Frozen(1, Provenance(3, RevisionA3), 0), (string?)null));
        var registry = Registry();

        var cut = Render<FunctionalQualityScopePanel>(p => p.Add(x => x.ProjectId, Project));
        cut.WaitForElement("[data-testid=fqs-canonical-scope]");
        cut.Find("[data-testid=fqs-freeze]").Click();
        cut.WaitForAssertion(() => sent.Should().ContainSingle());

        var provenance = sent[0].ScopeProvenance;
        provenance.Should().NotBeNull("a shared scope was selected when the snapshot was frozen");
        provenance!.Scopes.Should().ContainSingle().Which.Should().Be(new DeliveryScopeProvenanceEntry(ScopeA, "Release 1", 3, RevisionA3),
            "the exact approved revision number and the registry's id for that revision (not revision 2's)");
        provenance.MembershipResolution.Should().Be("approved revisions; canonical key match type+module+feature+id");
        _session.ScopeProvenance(Project)!.Scopes.Single().RevisionId.Should().Be(RevisionA3, "report generation in this tab records the same selection");

        cut.Find("[data-testid=fqs-freeze]").Click();
        cut.WaitForAssertion(() => sent.Should().HaveCount(2));
        sent[1].ScopeProvenance!.Scopes.Single().RevisionId.Should().Be(RevisionA3);
        registry.Verify(x => x.GetAsync("import-1", ScopeA, It.IsAny<CancellationToken>()), Times.Once(), "revision ids are resolved once per scope revision");
    }

    [Fact]
    public void Freezing_without_a_shared_scope_records_no_provenance()
    {
        var api = Api();
        var sent = new List<ScopeQuery>();
        api.Setup(a => a.FreezeAsync(Project, It.IsAny<ScopeQuery>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, ScopeQuery, CancellationToken>((_, q, _) => sent.Add(q))
            .ReturnsAsync((Frozen(1, null, 0), (string?)null));

        var cut = Render<FunctionalQualityScopePanel>(p => p.Add(x => x.ProjectId, Project));
        cut.WaitForElement("[data-testid=fqs-item]");
        cut.Find("[data-testid=fqs-freeze]").Click();
        cut.WaitForAssertion(() => sent.Should().ContainSingle());
        sent[0].ScopeProvenance.Should().BeNull();
    }

    private IRenderedComponent<FunctionalQualityScopePanel> Compare(ScopeSnapshotView a, ScopeSnapshotView b)
    {
        Api(b, a);
        var cut = Render<FunctionalQualityScopePanel>(p => p.Add(x => x.ProjectId, Project));
        cut.WaitForElement("[data-testid=fqs-snapshot-compare]");
        cut.Find("[data-testid=fqs-compare-a]").Change(a.Id.ToString());
        cut.Find("[data-testid=fqs-compare-b]").Change(b.Id.ToString());
        cut.WaitForElement("[data-testid=fqs-snapshot-comparison]");
        return cut;
    }

    [Fact]
    public void Snapshots_frozen_with_different_scope_revisions_show_a_mismatch_notice_and_no_like_for_like_percentage()
    {
        var cut = Compare(Frozen(40, Provenance(2, RevisionA2), 0), Frozen(44, Provenance(3, RevisionA3), 10));

        cut.Find("[data-testid=fqs-snapshot-comparison]").GetAttribute("data-match").Should().Be(nameof(DeliveryScopeProvenanceMatch.DifferentRevisions));
        cut.Find("[data-testid=fqs-scope-provenance-notice]").TextContent.Should()
            .Be("Compared deliveries used different scope revisions: A Release 1 rev 2 vs B Release 1 rev 3 — membership may differ; percentages are not like-for-like");
        cut.Find("[data-testid=fqs-compare-side-a]").TextContent.Should().Contain("Release 1 rev 2");
        cut.Find("[data-testid=fqs-compare-side-b]").TextContent.Should().Contain("Release 1 rev 3");
        var delta = cut.Find("[data-testid=fqs-compare-delta]").TextContent;
        delta.Should().Contain("40 → 44").And.Contain("+4").And.Contain("not like-for-like").And.NotContain("%");
    }

    [Fact]
    public void Snapshots_frozen_with_the_same_scope_revision_compare_like_for_like()
    {
        var cut = Compare(Frozen(40, Provenance(3, RevisionA3), 0), Frozen(44, Provenance(3, RevisionA3), 10));

        cut.Find("[data-testid=fqs-scope-provenance-notice]").TextContent.Should().StartWith("Same scope revision");
        cut.Find("[data-testid=fqs-compare-delta]").TextContent.Should().Contain("%").And.NotContain("not like-for-like");
    }

    [Fact]
    public void A_snapshot_frozen_before_scope_support_says_provenance_was_not_recorded()
    {
        var legacy = Frozen(40, null, 0);
        var cut = Compare(legacy, Frozen(44, Provenance(3, RevisionA3), 10));

        cut.FindAll("[data-testid=fqs-snapshot-provenance]").Select(e => e.TextContent.Trim())
            .Should().Contain("Scope provenance not recorded (created before scope support)");
        cut.Find("[data-testid=fqs-compare-side-a]").TextContent.Should().Contain("Scope provenance not recorded (created before scope support)");
        cut.Find("[data-testid=fqs-scope-provenance-notice]").TextContent.Should().Be("Scope provenance not recorded for A");
        cut.Find("[data-testid=fqs-compare-delta]").TextContent.Should().Contain("not like-for-like").And.NotContain("%");
    }
}
