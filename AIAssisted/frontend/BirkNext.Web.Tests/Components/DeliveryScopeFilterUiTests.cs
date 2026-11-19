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
/// The Functional Quality Scope view with a shared scope selected: freezing is explicitly "Scope context" (default — provenance only) or "Filter by
/// scope"; in filter mode the view previews included / excluded / unresolved counts (canonical keys only, children follow their parent, candidates and
/// other member kinds never counted) and the freeze request carries the mode and the scopes' project; a filtered snapshot lists its counts.
/// </summary>
public sealed class DeliveryScopeFilterUiTests : BunitContext
{
    private static readonly Guid Project = Guid.NewGuid();
    private static readonly Guid ScopeA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid RevisionA3 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset Friday = new(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);
    private readonly DeliveryReportingSession _session = new();

    public DeliveryScopeFilterUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(_session);
    }

    private static ScopeItem Item(string key, ScopeArtifactType type, string id, string? parent = null) => new(key, type, id, $"Item {id}", QualityCategory.Functional,
        "fq.functional.behaviour", "SkoleService", "001-rapport", "Motta rapport", parent, "P1", ScopeRelation.Direct, ScopeInclusion.Smart, "Owned by module SkoleService",
        ScopeCoverage.NoKnownCoverage, null, ScopeTestDataState.Unknown, TestabilityClassification.Unclassified, false, "SkoleService/specs/001-rapport/spec.md", 12);

    private static readonly ScopeItem Us1 = Item("story:SkoleService/001-rapport/US1", ScopeArtifactType.UserStory, "US1");
    private static readonly ScopeItem Ac1 = Item("ac:SkoleService/001-rapport/AC-1", ScopeArtifactType.AcceptanceCriterion, "AC-1", Us1.Key);
    private static readonly ScopeItem Us2 = Item("story:SkoleService/001-rapport/US2", ScopeArtifactType.UserStory, "US2");

    private static ScopeResolution Resolution() => new(new ScopeDefinition { Name = "Skolenærvær", Roots = ["profile:skolenaervaer"], BuiltIn = true }, Guid.NewGuid(), "Monday",
        FunctionalQualityClassifier.RuleSetVersion, [Us1, Ac1, Us2],
        new(2, 0, 1, 0, 0, new Dictionary<QualityCategory, int> { [QualityCategory.Functional] = 3 }, new Dictionary<ScopeCoverage, int> { [ScopeCoverage.NoKnownCoverage] = 3 }, 0, 0),
        [], [], FunctionalQualityScopeText.Precedence);

    private static Sc.ScopeSelectionMember Member(Sc.ScopeMemberKind kind, string id) =>
        new(new Sc.ScopeMember { Kind = kind, TargetId = id, Label = id }, [new(ScopeA, "Release 1", 3)]);

    private Mock<IFunctionalQualityScopeApiService> Api(List<ScopeQuery> sent, ScopeSnapshotView frozen)
    {
        var api = new Mock<IFunctionalQualityScopeApiService>();
        api.Setup(a => a.AvailableAsync(Project, It.IsAny<CancellationToken>()))
            .ReturnsAsync(([new AvailableScope("profile:skolenaervaer", "Skolenærvær", "Domain flow", "1 module(s)")], (string?)null));
        api.Setup(a => a.ResolveAsync(Project, It.IsAny<ScopeQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync((Resolution(), (string?)null));
        var snapshots = new List<ScopeSnapshotView>();
        api.Setup(a => a.SnapshotsAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync(() => (IReadOnlyList<ScopeSnapshotView>)snapshots.ToList());
        api.Setup(a => a.FreezeAsync(Project, It.IsAny<ScopeQuery>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, ScopeQuery, CancellationToken>((_, q, _) => { sent.Add(q); snapshots.Add(frozen); })
            .ReturnsAsync((frozen, (string?)null));
        Services.AddSingleton(api.Object);
        return api;
    }

    /// <summary>One approved scope naming US1 (confirmed), a story not in this Delivery population, and a system (not a Delivery item).</summary>
    private void Registry()
    {
        var scopes = new Mock<IScopeRegistryApiService>();
        scopes.Setup(x => x.ListAsync("import-1", It.IsAny<CancellationToken>())).ReturnsAsync(([
            new Sc.ScopeSummary { Id = ScopeA, Name = "Release 1", State = Sc.ScopeState.Approved, ApprovedRevision = 3, CurrentRevision = 3 }], (string?)null));
        scopes.Setup(x => x.SelectionAsync("import-1", It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new Sc.ScopeSelection
            {
                ProjectKey = "import-1", Scopes = [new(ScopeA, "Release 1", 3)],
                Members = [Member(Sc.ScopeMemberKind.UserStory, DeliveryScopeMembership.CanonicalKeyOf(Us1)!), Member(Sc.ScopeMemberKind.UserStory, "UserStory|skoleservice/009-gone/US9"),
                    Member(Sc.ScopeMemberKind.System, "System|altinn")],
            }, (string?)null));
        var detail = new Sc.ScopeDetail
        {
            Scope = new Sc.ScopeSummary { Id = ScopeA, Name = "Release 1", State = Sc.ScopeState.Approved, ApprovedRevision = 3, CurrentRevision = 3 },
            Revisions = [new Sc.ScopeRevisionView { Id = RevisionA3, Number = 3 }],
        };
        scopes.Setup(x => x.GetAsync("import-1", ScopeA, It.IsAny<CancellationToken>())).ReturnsAsync(((Sc.ScopeDetail?)detail, (string?)null));
        Services.AddSingleton(scopes.Object);
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(
            BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "import-1", "Northwind Orders", WorkspaceArtifactType.Specification)).Object);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"delivery-reporting?tab=scope&scope={ScopeA}");
    }

    private static ScopeSnapshotView Frozen(int items, DeliveryScopeProvenance provenance) =>
        new(Guid.NewGuid(), null, "Skolenærvær", 1, Guid.NewGuid(), FunctionalQualityClassifier.RuleSetVersion, items, Friday) { ScopeProvenance = provenance };

    private static DeliveryScopeProvenance Recorded(bool filtered) => new()
    {
        Scopes = [new(ScopeA, "Release 1", 3, RevisionA3)], CreatedAt = Friday, Mode = filtered ? DeliveryScopeMode.Filter : DeliveryScopeMode.Context, Filtered = filtered,
        Counts = filtered ? new() { Included = 2, Excluded = 1, MembersMatched = 1, MembersUnresolved = 1, MembersNotApplicable = 1 } : null,
    };

    [Fact]
    public void A_shared_scope_id_in_the_url_is_never_read_as_a_functional_quality_scope_key()
    {
        Api([], Frozen(3, Recorded(false)));
        Registry();

        var cut = Render<FunctionalQualityScopePanel>(p => p.Add(x => x.ProjectId, Project).Add(x => x.InitialScope, ScopeA.ToString()));
        cut.WaitForElement("[data-testid=fqs-item]");
        cut.FindAll("[data-testid=fqs-scope-mode]").Should().BeEmpty("there is no shared-scope mode to choose any more");
        cut.FindAll("[data-testid=fqs-notice]").Should().BeEmpty("?scope= also carries Shared Scope Registry ids; a GUID is not a missing Functional Quality Scope");
    }

}
