using System.Text.Json;
using BirkNext.Api.Data.ScopeRegistry;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.Api.Services.ScopeRegistry;
using BirkNext.DeliveryReporting;
using BirkNext.Scopes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>
/// "Filter by scope" for Delivery: membership resolved from pinned approved Scope Registry revisions (confirmed members only, overlapping scopes counted
/// once), Delivery items matched by canonical key only, report figures restricted to in-scope items (or features containing them) and the rest named
/// "Not scope-resolvable"; report-vs-report comparison with percentages only like-for-like; provenance JSON stays backward compatible.
/// </summary>
public sealed class DeliveryScopeFilterTests
{
    private const string ProjectKey = "import:m2lb";
    private static readonly TimeZoneInfo Oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string LinkedTasks(int total, int completed)
    {
        var lines = Enumerable.Range(1, total).Select(i => $"- [{(i <= completed ? "x" : " ")}] T{i:000} [US1] Implement step {i} of the story");
        return "# Tasks\n\n## Phase 1: User Story 1\n\n" + string.Join("\n", lines) + "\n\n## Phase 2: Polish\n\n- [ ] T100 Shared clean-up\n";
    }

    private static (string, string)[] TwoFeatures(int childDone, int caseDone) =>
    [
        ("Frontend/specs/001-child-search/spec.md", Spec("Child search", DefaultAcceptance)),
        ("Frontend/specs/001-child-search/tasks.md", LinkedTasks(4, childDone)),
        ("Frontend/specs/002-case-view/spec.md", Spec("Case view", DefaultAcceptance)),
        ("Frontend/specs/002-case-view/tasks.md", Tasks(6, caseDone)),
    ];

    private static WeeklyDeliveryReport Report(int childBefore = 1, int childNow = 3, int caseBefore = 2, int caseNow = 4)
    {
        var b = Content(Parse(TwoFeatures(childBefore, caseBefore)), Monday, "Monday baseline");
        var c = Content(Parse(TwoFeatures(childNow, caseNow)), Friday, "Friday current");
        return WeeklyReportBuilder.Build(Guid.NewGuid(), Guid.NewGuid(), "M2LB", Friday.AddHours(1), b, c, DeliverySnapshotComparison.Compare(b, c), Oslo);
    }

    private static DeliveryStoryRow Feature(WeeklyDeliveryReport r, string title) => r.Stories.Single(s => s.Title.Contains(title, StringComparison.OrdinalIgnoreCase));

    private static string StoryKey(DeliveryStoryRow f) =>
        DeliveryScopeMembership.CanonicalKeyOf(ScopeArtifactType.UserStory, f.Module, f.FeatureKey, f.Hierarchy!.Stories.First().Key)!;

    private static string RequirementKey(DeliveryStoryRow f) =>
        DeliveryScopeMembership.CanonicalKeyOf(ScopeArtifactType.Requirement, f.Module, f.FeatureKey, f.Hierarchy!.FunctionalRequirements.First().Key)!;

    private static int? Metric(WeeklyDeliveryReport r, string key) => r.Metrics.FirstOrDefault(m => m.Key == key)?.Value;

    private static ScopeItem Item(string key, ScopeArtifactType type, string displayId, string module = "Frontend", string feature = "001-child-search", string? parent = null, string title = "Item") =>
        new(key, type, displayId, title, QualityCategory.Functional, "fq.functional.behaviour", module, feature, "Child search", parent, null, ScopeRelation.Direct, ScopeInclusion.Smart,
            "Module", ScopeCoverage.NotAssessed, null, ScopeTestDataState.Unknown, TestabilityClassification.Unclassified, false, null, null);

    private static ScopeRegistryService Registry() => new(new ScopeRegistryDbContext(new DbContextOptionsBuilder<ScopeRegistryDbContext>()
        .UseInMemoryDatabase($"scope-filter-{Guid.NewGuid():N}").Options), TimeProvider.System);

    private static ScopeMember M(ScopeMemberKind kind, string id, ScopeMemberInclusion inclusion = ScopeMemberInclusion.Confirmed) =>
        new() { Kind = kind, TargetId = id, Label = id, Inclusion = inclusion };

    private static async Task<ScopeDetail> Scope(ScopeRegistryService registry, string name, bool approve, params ScopeMember[] members)
    {
        var created = (await registry.CreateAsync(new CreateScopeRequest(ProjectKey, name, null, ScopePurpose.ReleaseDelivery, null, [.. members]), "tester")).Scope!;
        return approve ? (await registry.ApproveAsync(ProjectKey, created.Scope.Id, new ScopeDecisionRequest(created.Scope.Version, 1, null), "lead")).Scope! : created;
    }

    private static DeliveryScopeProvenance Request(DeliveryScopeMode mode, params ScopeDetail[] scopes) => new()
    {
        Mode = mode, ProjectKey = ProjectKey,
        Scopes = [.. scopes.Select(s => new DeliveryScopeProvenanceEntry(s.Scope.Id, s.Scope.Name, s.Scope.CurrentRevision, null))],
    };

    // ── Item membership (shared rule, used by freeze and the browser preview) ───────────────────────────────────────────

    [Fact]
    public void Items_match_by_canonical_key_only_children_follow_their_parent_and_unresolved_members_are_counted()
    {
        var story = Item("story:Frontend/001-child-search/US1", ScopeArtifactType.UserStory, "US1", title: "Search children");
        var criterion = Item("ac:Frontend/001-child-search/AC-1", ScopeArtifactType.AcceptanceCriterion, "AC-1", parent: story.Key);
        var requirement = Item("req:Frontend/001-child-search/FR-001", ScopeArtifactType.Requirement, "FR-001");
        var sameTitleOtherFeature = Item("story:Frontend/002-case-view/US1", ScopeArtifactType.UserStory, "US1", feature: "002-case-view", title: "Search children");
        var keys = new[]
        {
            DeliveryScopeMembership.CanonicalKeyOf(story)!, DeliveryScopeMembership.CanonicalKeyOf(story)!.ToUpperInvariant(), // the same member twice (overlapping scopes)
            DeliveryScopeMembership.CanonicalKeyOf(requirement)!, "UserStory|frontend/009-gone/US7",
        };

        var filter = DeliveryScopeMembership.FilterItems([story, criterion, requirement, sameTitleOtherFeature], keys, membersNotApplicable: 2);

        filter.Items.Select(i => i.Key).Should().Equal(story.Key, criterion.Key, requirement.Key);
        filter.Counts.Should().Match<DeliveryScopeFilterCounts>(c => c.Included == 3 && c.Excluded == 1 && c.MembersMatched == 2 && c.MembersUnresolved == 1 && c.MembersNotApplicable == 2);
        filter.Counts.UnresolvedMemberKeys.Should().Equal("UserStory|frontend/009-gone/US7");
        DeliveryScopeProvenanceText.CountsText(filter.Counts).Should()
            .Be("3 included · 1 excluded (outside scope) · 1 scope member unresolved (named by the scope, not in this Delivery population) · 2 members of other kinds not applicable to Delivery items");
    }

    // ── Membership resolution (Scope Registry, in-process) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Membership_uses_confirmed_members_of_the_pinned_approved_revisions_once_each()
    {
        var registry = Registry();
        var a = await Scope(registry, "Release 1", true, M(ScopeMemberKind.UserStory, "UserStory|frontend/001-child-search/US1"),
            M(ScopeMemberKind.Requirement, "Requirement|frontend/001-child-search/FR-001"), M(ScopeMemberKind.UserStory, "UserStory|frontend/002-case-view/US1", ScopeMemberInclusion.Candidate),
            M(ScopeMemberKind.System, "System|altinn"));
        var b = await Scope(registry, "Payments", true, M(ScopeMemberKind.UserStory, "UserStory|frontend/001-child-search/US1"));

        var (resolution, error) = await new DeliveryScopeFilterService(registry).ResolveAsync(Request(DeliveryScopeMode.Filter, a, b), default);

        error.Should().BeNull();
        resolution!.MemberKeys.Should().BeEquivalentTo("UserStory|frontend/001-child-search/US1", "Requirement|frontend/001-child-search/FR-001");
        resolution.MembersNotApplicable.Should().Be(1, "a system is not a Delivery item; the candidate story is not a member at all");
        resolution.Provenance.Scopes.Should().HaveCount(2).And.OnlyContain(s => s.RevisionId != null, "revision ids are pinned from the registry, never from the request");
        resolution.Provenance.Scopes.Single(s => s.ScopeId == a.Scope.Id).RevisionId.Should().Be(a.Revisions.Single(r => r.Number == 1).Id);
    }

    [Fact]
    public async Task Membership_refuses_unapproved_or_mismatched_revisions_and_does_nothing_in_scope_context_mode()
    {
        var registry = Registry();
        var draft = await Scope(registry, "Draft scope", false, M(ScopeMemberKind.UserStory, "UserStory|frontend/001-child-search/US1"));
        var approved = await Scope(registry, "Approved", true, M(ScopeMemberKind.UserStory, "UserStory|frontend/001-child-search/US1"));
        var service = new DeliveryScopeFilterService(registry);

        (await service.ResolveAsync(Request(DeliveryScopeMode.Filter, draft), default)).Error.Should().Contain("is not approved");
        var wrongId = Request(DeliveryScopeMode.Filter, approved) with { Scopes = [new(approved.Scope.Id, "Approved", 1, Guid.NewGuid())] };
        (await service.ResolveAsync(wrongId, default)).Error.Should().Contain("revision id");
        (await service.ResolveAsync(Request(DeliveryScopeMode.Filter, approved) with { ProjectKey = null }, default)).Error.Should().Contain("project");
        (await new DeliveryScopeFilterService().ResolveAsync(Request(DeliveryScopeMode.Filter, approved), default)).Error.Should().Contain("Scope Registry");
        (await service.ResolveAsync(Request(DeliveryScopeMode.Context, approved), default)).Should().Be(((DeliveryScopeMembershipResolution?)null, (string?)null));
    }

    // ── Report figures under a scope ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_filtered_report_counts_only_in_scope_items_and_names_figures_it_cannot_restrict()
    {
        var report = Report();
        var child = Feature(report, "Child search");
        child.Hierarchy!.Stories.Should().NotBeEmpty();
        child.Hierarchy.FunctionalRequirements.Should().NotBeEmpty();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { StoryKey(child), RequirementKey(child), "UserStory|frontend/009-gone/US7" };

        var (filtered, counts) = DeliveryReportScopeFilter.Apply(report, keys, membersNotApplicable: 0);

        counts.Should().Match<DeliveryScopeFilterCounts>(c => c.Included == 2 && c.Excluded == 2 && c.MembersUnresolved == 1 && c.IncludedFeatures == 1);
        filtered.Stories.Should().ContainSingle().Which.FeatureKey.Should().Be(child.FeatureKey);
        filtered.Stories.Single().Hierarchy!.Stories.Should().ContainSingle();
        Metric(report, "inventory-features").Should().Be(2);
        Metric(filtered, "inventory-features").Should().Be(1, "the denominator is the features containing in-scope items, not the project");
        Metric(filtered, "inventory-stories").Should().Be(1).And.BeLessThan(Metric(report, "inventory-stories")!.Value);
        Metric(filtered, "inventory-requirements").Should().Be(1);
        var linked = child.Hierarchy.Stories.First();
        Metric(filtered, "inventory-tasks").Should().Be(linked.TaskTotal, "tasks linked to the in-scope story — not the feature's shared tasks");
        Metric(filtered, "inventory-checked").Should().Be(linked.TaskCompleted);
        Metric(filtered, "inventory-tasks").Should().BeLessThan(Metric(report, "inventory-tasks")!.Value);
        Metric(filtered, "tasks-completed").Should().BeNull();
        counts.NotScopeResolvable.Should().Contain("Tasks completed this period");
        filtered.Metrics.Single(m => m.Key == "inventory-features").Hint.Should().Contain(DeliveryReportScopeFilter.FeatureLevelHint);
        report.Stories.Should().HaveCount(2, "the unfiltered report is never changed");
    }

    [Fact]
    public void A_scope_with_no_applicable_items_shows_no_figures_instead_of_zeros()
    {
        var (filtered, counts) = DeliveryReportScopeFilter.Apply(Report(), new HashSet<string> { "UserStory|other/001-x/US1" }, 1);

        counts.NoApplicableItems.Should().BeTrue();
        counts.MembersUnresolved.Should().Be(1);
        filtered.Metrics.Should().BeEmpty("no denominator exists: never a 0 % or a 0 that reads as measured");
        filtered.ExecutiveSummary[0].Should().StartWith(DeliveryScopeProvenanceText.NoApplicableItems);
    }

    // ── Report vs report ────────────────────────────────────────────────────────────────────────────────────────────

    private static WeeklyDeliveryReport With(WeeklyDeliveryReport r, string key, int value) =>
        r with { ReportId = Guid.NewGuid(), Metrics = [.. r.Metrics.Select(m => m.Key == key ? m with { Value = value } : m)] };

    [Fact]
    public void Like_for_like_reports_show_percentages_and_added_or_removed_attention_and_blockers()
    {
        var baseline = Report();
        var checkedBefore = Metric(baseline, "inventory-checked")!.Value;
        var current = With(baseline, "inventory-checked", checkedBefore * 2) with
        {
            Attention = [.. baseline.Attention, new DeliveryAttentionItem(DeliveryAttentionKind.Blocked, DeliveryAttentionSeverity.Blocker, "Case view blocked", "Waiting for API", "002-case-view")],
            Stories = [.. baseline.Stories.Select(s => s.Title.Contains("Case view") ? s with { CurrentStatus = DeliveryStatus.Blocked } : s)],
        };

        var cmp = DeliveryReportComparer.Compare(baseline, current);

        cmp.LikeForLike.Should().BeTrue();
        cmp.Mismatches.Should().BeEmpty();
        cmp.Summary[0].Should().Be(DeliveryReportComparer.LikeForLikeText);
        var diff = cmp.Metrics.Single(m => m.Key == "inventory-checked");
        diff.Should().Match<DeliveryReportMetricDifference>(d => d.Baseline == checkedBefore && d.Current == checkedBefore * 2 && d.Delta == checkedBefore && d.PercentChange == 100.0);
        cmp.AddedAttention.Should().ContainSingle().Which.Title.Should().Be("Case view blocked");
        cmp.AddedBlockers.Should().ContainSingle().Which.Should().Contain("Case view");
        cmp.ScopeProvenance.Match.Should().Be(DeliveryScopeProvenanceMatch.MissingBoth, "legacy reports without provenance compare whole population to whole population");
    }

    [Fact]
    public void Reports_that_are_not_like_for_like_name_the_mismatch_and_show_raw_counts_without_percentages()
    {
        var baseline = Report();
        var child = Feature(baseline, "Child search");
        var (filteredReport, counts) = DeliveryReportScopeFilter.Apply(baseline, new HashSet<string> { StoryKey(child) }, 0);
        var filtered = filteredReport with
        {
            ReportId = Guid.NewGuid(),
            ScopeProvenance = new() { Scopes = [new(Guid.NewGuid(), "Release 1", 2, Guid.NewGuid())], Mode = DeliveryScopeMode.Filter, Filtered = true, Counts = counts },
        };

        var cmp = DeliveryReportComparer.Compare(baseline, filtered);

        cmp.LikeForLike.Should().BeFalse();
        cmp.Mismatches.Should().ContainSingle().Which.Should().Be("Different scope modes: whole population vs filtered by scope.");
        cmp.Metrics.Should().OnlyContain(m => m.PercentChange == null);
        cmp.Metrics.Single(m => m.Key == "inventory-features").Note.Should().Be(DeliveryScopeProvenanceText.PercentNotLikeForLike);
        cmp.Metrics.Single(m => m.Key == "tasks-completed").Note.Should().Be("Not scope-resolvable in the current report");
        cmp.Summary[0].Should().StartWith(DeliveryReportComparer.NotLikeForLikeText);

        var other = With(baseline, "attention", 1) with { CalculationVersion = baseline.CalculationVersion + 1, Kind = baseline.Kind == DeliveryReportKind.Weekly ? DeliveryReportKind.Change : DeliveryReportKind.Weekly };
        DeliveryReportComparer.Compare(baseline, other).Mismatches.Should().HaveCount(2).And.Contain(m => m.StartsWith("Different report kinds")).And.Contain(m => m.StartsWith("Different calculation rules"));
    }

    [Fact]
    public void Two_filtered_reports_are_like_for_like_only_with_the_same_scope_revisions_and_a_zero_baseline_has_no_percentage()
    {
        var scope = Guid.NewGuid();
        DeliveryScopeProvenance P(int rev) => new() { Scopes = [new(scope, "Release 1", rev, null)], Mode = DeliveryScopeMode.Filter, Filtered = true, Counts = new() };
        var a = With(Report(), "attention", 0) with { ScopeProvenance = P(2) };
        var b = With(a, "attention", 3) with { ScopeProvenance = P(2) };

        var same = DeliveryReportComparer.Compare(a, b);
        same.LikeForLike.Should().BeTrue();
        same.Metrics.Single(m => m.Key == "attention").Should().Match<DeliveryReportMetricDifference>(d => d.PercentChange == null && d.Delta == 3 && d.Note == "no percentage: the baseline value is 0");

        var different = DeliveryReportComparer.Compare(a, b with { ScopeProvenance = P(3) });
        different.LikeForLike.Should().BeFalse();
        different.Mismatches.Single().Should().Contain("different scope revisions");
    }

    // ── Provenance JSON compatibility ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Old_provenance_json_reads_as_scope_context_and_requests_can_never_claim_a_filter()
    {
        var old = """{"scopes":[{"scopeId":"aaaaaaaa-0000-0000-0000-00000000000a","scopeName":"Release 1","revisionNumber":3,"revisionId":null}],"membershipResolution":"approved revisions; canonical key match type+module+feature+id","createdAt":"2026-10-30T16:00:00+00:00"}""";
        var read = JsonSerializer.Deserialize<DeliveryScopeProvenance>(old, Json)!;
        read.Mode.Should().Be(DeliveryScopeMode.Context);
        read.Filtered.Should().BeFalse();
        read.Counts.Should().BeNull();
        DeliveryScopeProvenanceText.Describe(read).Should().Be("Release 1 rev 3 · approved revisions; canonical key match type+module+feature+id");

        var written = JsonSerializer.Serialize(DeliveryScopeProvenanceText.Normalize(read, Friday), Json);
        written.Should().NotContain("\"mode\"").And.NotContain("\"filtered\"").And.NotContain("\"counts\"", "a scope-context record keeps the original shape");

        var forged = read with { Mode = DeliveryScopeMode.Filter, Filtered = true, Counts = new() { Included = 99 }, FilterBasis = "client", ProjectKey = " import:m2lb " };
        var normalized = DeliveryScopeProvenanceText.Normalize(forged, Friday)!;
        normalized.Should().Match<DeliveryScopeProvenance>(p => p.Mode == DeliveryScopeMode.Filter && !p.Filtered && p.Counts == null && p.FilterBasis == null && p.ProjectKey == "import:m2lb");

        var filtered = normalized with { Filtered = true };
        DeliveryScopeProvenanceText.Describe(filtered).Should().StartWith("Filtered by scope: Release 1 rev 3");
        DeliveryScopeProvenanceText.Compare(read, filtered).Should().Match<DeliveryScopeProvenanceComparison>(c => c.Match == DeliveryScopeProvenanceMatch.DifferentMode && !c.LikeForLike);
    }
}
