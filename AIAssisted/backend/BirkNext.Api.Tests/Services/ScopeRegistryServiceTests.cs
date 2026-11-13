using BirkNext.Api.Data.ScopeRegistry;
using BirkNext.Api.Services.ScopeRegistry;
using BirkNext.Scopes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// The shared Scope Registry: project-scoped scopes with typed members, immutable approved revisions (an edit after approval is a new draft),
/// optimistic concurrency, revision comparison and multi-scope selection without double counting. No other project is ever read.
/// </summary>
public sealed class ScopeRegistryServiceTests
{
    private const string Project = "import:m2lb-language";
    private readonly ScopeRegistryService _service = new(new ScopeRegistryDbContext(new DbContextOptionsBuilder<ScopeRegistryDbContext>()
        .UseInMemoryDatabase($"scopes-{Guid.NewGuid():N}").Options), TimeProvider.System);

    private static ScopeMember M(ScopeMemberKind kind, string id, ScopeMemberInclusion inclusion = ScopeMemberInclusion.Confirmed) =>
        new() { Kind = kind, TargetId = id, Label = id, Inclusion = inclusion };

    private async Task<ScopeDetail> Create(string name, params ScopeMember[] members) =>
        (await _service.CreateAsync(new CreateScopeRequest(Project, name, null, ScopePurpose.Test, "Test lead", [.. members]), "tester")).Scope!;

    [Fact]
    public async Task A_scope_with_several_systems_starts_as_a_draft_revision_and_ignores_duplicate_and_empty_members()
    {
        var scope = await Create("Report intake", M(ScopeMemberKind.System, "skoleservice"), M(ScopeMemberKind.System, "mm"), M(ScopeMemberKind.System, "skoleservice"),
            M(ScopeMemberKind.Scenario, " "), M(ScopeMemberKind.Scenario, "gyldig-rapport"));

        scope.Scope.State.Should().Be(ScopeState.Draft);
        scope.Scope.CurrentRevision.Should().Be(1);
        scope.Scope.ApprovedRevision.Should().BeNull();
        scope.Revisions.Single().Members.Select(m => m.Key).Should().Equal("System:mm", "System:skoleservice", "Scenario:gyldig-rapport");
        scope.Scope.MembersByKind.Should().Contain(ScopeMemberKind.System, 2);
        (await _service.CreateAsync(new CreateScopeRequest(Project, "Report intake", null, ScopePurpose.Test, null, []), "tester")).Error.Should().Contain("already has a scope");
    }

    [Fact]
    public async Task An_approved_revision_never_changes_an_edit_becomes_a_new_draft_and_the_comparison_shows_it()
    {
        var created = await Create("Period close", M(ScopeMemberKind.System, "skoleservice"));
        var approved = (await _service.ApproveAsync(Project, created.Scope.Id, new ScopeDecisionRequest(created.Scope.Version, 1, "Agreed in planning"), "lead")).Scope!;
        approved.Scope.State.Should().Be(ScopeState.Approved);
        approved.Revisions.Single().ApprovedBy.Should().Be("lead");

        var edited = (await _service.SaveDraftAsync(Project, created.Scope.Id, new SaveScopeDraftRequest(approved.Scope.Version, "Period close", null, ScopePurpose.Test, null,
            [M(ScopeMemberKind.System, "skoleservice"), M(ScopeMemberKind.System, "bufdata")], "Add Bufdata delivery"), "tester")).Scope!;
        edited.Scope.CurrentRevision.Should().Be(2);
        edited.Scope.ApprovedRevision.Should().Be(1, "the approved revision stays the one modules use until the new one is approved");
        edited.Revisions.Single(r => r.Number == 1).Members.Should().ContainSingle("the approved membership is immutable");

        var comparison = (await _service.CompareAsync(Project, created.Scope.Id, 1, 2))!;
        comparison.Added.Should().Be(1);
        comparison.Differences.Single(d => d.Change == ScopeMemberChange.Added).After!.TargetId.Should().Be("bufdata");

        var second = (await _service.ApproveAsync(Project, created.Scope.Id, new ScopeDecisionRequest(edited.Scope.Version, 2, null), "lead")).Scope!;
        second.Revisions.Single(r => r.Number == 1).State.Should().Be(ScopeRevisionState.Superseded, "kept readable, not deleted");
    }

    [Fact]
    public async Task A_save_based_on_an_older_version_is_refused_not_merged()
    {
        var created = await Create("Education updates", M(ScopeMemberKind.System, "skoleadapter"));
        var first = await _service.SaveDraftAsync(Project, created.Scope.Id, new SaveScopeDraftRequest(created.Scope.Version, "Education updates", null, ScopePurpose.Test, null, [M(ScopeMemberKind.System, "utdanning")], null), "a");
        first.Scope.Should().NotBeNull();
        var stale = await _service.SaveDraftAsync(Project, created.Scope.Id, new SaveScopeDraftRequest(created.Scope.Version, "Education updates", null, ScopePurpose.Test, null, [], null), "b");
        stale.Conflict.Should().BeTrue();
        (await _service.GetAsync(Project, created.Scope.Id))!.Revisions.Single().Members.Should().ContainSingle(m => m.TargetId == "utdanning");
    }

    [Fact]
    public async Task Several_overlapping_scopes_are_combined_once_with_attribution_and_only_approved_confirmed_members()
    {
        var a = await Create("Intake", M(ScopeMemberKind.System, "skoleservice"), M(ScopeMemberKind.TestDataAsset, "asset-1"), M(ScopeMemberKind.Scenario, "x", ScopeMemberInclusion.Candidate));
        var b = await Create("Delivery", M(ScopeMemberKind.System, "skoleservice"), M(ScopeMemberKind.System, "bufdata"), M(ScopeMemberKind.TestDataAsset, "asset-1"));
        var draftOnly = await Create("Draft only", M(ScopeMemberKind.System, "person"));
        await _service.ApproveAsync(Project, a.Scope.Id, new ScopeDecisionRequest(a.Scope.Version, 1, null), "lead");
        await _service.ApproveAsync(Project, b.Scope.Id, new ScopeDecisionRequest(b.Scope.Version, 1, null), "lead");

        var selection = await _service.SelectAsync(Project, [a.Scope.Id, b.Scope.Id, draftOnly.Scope.Id], includeDrafts: false);

        selection.Members.Select(m => m.Member.Key).Should().Equal("System:bufdata", "System:skoleservice", "TestDataAsset:asset-1");
        selection.UniqueCount.Should().Be(3, "shared members count once");
        selection.PerScopeCounts[a.Scope.Id].Should().Be(2, "a candidate is not a member");
        selection.PerScopeCounts[b.Scope.Id].Should().Be(3);
        selection.Members.Single(m => m.Member.TargetId == "asset-1").ContributedBy.Select(c => c.ScopeName).Should().BeEquivalentTo(["Intake", "Delivery"]);
        selection.Notices.Should().ContainSingle(n => n.Contains("Draft only") && n.Contains("no approved revision"), "a draft is never silently used");
    }

    [Fact]
    public async Task Another_projects_scope_is_never_read_or_changed()
    {
        var other = (await _service.CreateAsync(new CreateScopeRequest("import:other", "Other", null, ScopePurpose.Test, null, [M(ScopeMemberKind.System, "x")]), "t")).Scope!;
        (await _service.GetAsync(Project, other.Scope.Id)).Should().BeNull();
        (await _service.ListAsync(Project)).Should().BeEmpty();
        (await _service.ApproveAsync(Project, other.Scope.Id, new ScopeDecisionRequest(other.Scope.Version, 1, null), "lead")).Error.Should().Contain("not found in this project");
        (await _service.SelectAsync(Project, [other.Scope.Id], false)).Members.Should().BeEmpty();
    }
}
