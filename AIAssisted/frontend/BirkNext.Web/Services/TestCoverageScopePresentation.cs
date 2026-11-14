using BirkNext.Interpretation;
using BirkNext.Scopes;
using BirkNext.TestCoverage;

namespace BirkNext.Web.Services;

/// <summary>Whether an item of the review can be tied to the selected scopes by a stable identity.</summary>
public enum CoverageScopeAssociation
{
    /// <summary>The scope names this item by the same stable identity.</summary>
    InScope,
    /// <summary>A scope member may be this item, but the identity cannot be proven from the review's data (reported, never excluded, never counted as in scope).</summary>
    NotConfirmed,
    /// <summary>No selected scope names anything with this identity.</summary>
    OutOfScope,
}

/// <param name="InScope">Confirmed by stable identity (deduplicated).</param>
/// <param name="NotConfirmed">Possibly in scope, association not confirmed.</param>
/// <param name="WholeProject">The same kind in the whole review, for comparison.</param>
public sealed record CoverageScopeCount(string Kind, string Label, int InScope, int NotConfirmed, int WholeProject, string? Note = null);

/// <param name="SharedMembers">Members named by more than one selected scope (each counted once).</param>
public sealed record CoverageScopeSummary(IReadOnlyList<CoverageScopeCount> Rows, int SharedMembers, int ScopeCount, IReadOnlyList<string> Notes);

/// <summary>
/// The shared scope filter on Test Coverage &amp; Overlap Review. Items are tied to the selected scopes only through identities a scope member can
/// carry — never by name similarity — and nothing outside a confirmed association is ever presented as covered:
/// <list type="bullet">
/// <item>Requirements. The review only knows display ids (FR-001) plus, at most, the module and specification file of the selected
/// specification. A scope Requirement member is the canonical key "Requirement|{module}/{feature}/{id}" (or the unscoped "Requirement|{id}").
/// Rule: the requirement is <see cref="CoverageScopeAssociation.InScope"/> only when a member's identity equals
/// "{normalized module}/{normalized feature folder}/{normalized id}" built from the review's single specification (module from the
/// specification artifact, feature = the folder that holds the specification file). When a member's last segment is the same normalized
/// requirement id but the module/feature path cannot be proven (no module, several specifications, an unscoped key or another path), the
/// requirement is <see cref="CoverageScopeAssociation.NotConfirmed"/> ("Scope association not confirmed") — shown, not excluded. Only when no
/// member carries that requirement id at all is it <see cref="CoverageScopeAssociation.OutOfScope"/>.</item>
/// <item>Critical E2E flows: QA test "e2e:{flowId}" ↔ CriticalE2EFlow member {flowId} (exact).</item>
/// <item>Tests and other QA tests: through the requirement ids they reference (the rule above); a test with no requirement id has no identity a scope can name.</item>
/// <item>Journeys: the review's journeys are derived from source connections ("journey:a&gt;b"); scope journeys are Integration Journey pack ids
/// ("packId:journeyId"). No stable identity links the two, so every journey is reported as not confirmed while journeys are selected.</item>
/// <item>Test Data: the review result carries no Test Data asset ids, so scoped assets cannot be associated here.</item>
/// </list>
/// </summary>
public static class TestCoverageScopePresentation
{
    public const string NotConfirmedLabel = "Scope association not confirmed";

    /// <summary>Module and feature folder of the review's one specification, when both are known (else nulls).</summary>
    public static (string? Module, string? Feature) SpecificationPath(TestCoverageReviewResult r)
    {
        if (r.SpecificationArtifacts.Count != 1) return (null, null);
        var spec = r.SpecificationArtifacts[0];
        var parts = (spec.Path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var feature = parts.Length >= 2 ? parts[^2] : null;
        return string.IsNullOrWhiteSpace(spec.Module) || string.IsNullOrWhiteSpace(feature) ? (null, null) : (spec.Module, feature);
    }

    static IEnumerable<ScopeSelectionMember> Of(ScopeSelection s, ScopeMemberKind kind) => s.Members.Where(m => m.Member.Kind == kind);

    static string Identity(string targetId) => targetId.StartsWith("Requirement|", StringComparison.Ordinal) ? targetId["Requirement|".Length..] : targetId;

    /// <summary>The members that name this requirement id with a proven module/feature path (InScope) or only by id (NotConfirmed).</summary>
    public static (CoverageScopeAssociation Association, IReadOnlyList<ScopeSelectionMember> Members) Requirement(string requirementId, ScopeSelection selection, string? module, string? feature)
    {
        var id = CorrelationRules.NormalizeRequirement(requirementId);
        if (id is null) return (CoverageScopeAssociation.OutOfScope, []);
        var expected = module is not null && feature is not null ? $"{CanonicalKeys.Normalize(module)}/{CanonicalKeys.Normalize(feature)}/{id}" : null;
        var exact = Of(selection, ScopeMemberKind.Requirement).Where(m => expected is not null && string.Equals(Identity(m.Member.TargetId), expected, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return (CoverageScopeAssociation.InScope, exact);
        var byId = Of(selection, ScopeMemberKind.Requirement).Where(m => string.Equals(CorrelationRules.NormalizeRequirement(Identity(m.Member.TargetId).Split('/')[^1]), id, StringComparison.Ordinal)).ToList();
        return byId.Count > 0 ? (CoverageScopeAssociation.NotConfirmed, byId) : (CoverageScopeAssociation.OutOfScope, []);
    }

    /// <summary>The strongest association among an item's requirement ids (any confirmed → in scope; else any possible → not confirmed).</summary>
    public static CoverageScopeAssociation Requirements(IEnumerable<string> requirementIds, ScopeSelection selection, string? module, string? feature)
    {
        var all = requirementIds.Select(id => Requirement(id, selection, module, feature).Association).ToList();
        return all.Contains(CoverageScopeAssociation.InScope) ? CoverageScopeAssociation.InScope
            : all.Contains(CoverageScopeAssociation.NotConfirmed) ? CoverageScopeAssociation.NotConfirmed : CoverageScopeAssociation.OutOfScope;
    }

    public static string? FlowId(QaTestCandidate q) => q.Id.StartsWith("e2e:", StringComparison.Ordinal) ? q.Id["e2e:".Length..] : null;

    public static CoverageScopeAssociation QaTest(QaTestCandidate q, ScopeSelection selection, string? module, string? feature) =>
        FlowId(q) is { } flowId
            ? Of(selection, ScopeMemberKind.CriticalE2EFlow).Any(m => m.Member.TargetId == flowId) ? CoverageScopeAssociation.InScope : CoverageScopeAssociation.OutOfScope
            : Requirements(q.RequirementIds, selection, module, feature);

    /// <summary>A requirement/risk item of the "By Requirement" view: "req:{id}" by its requirement, a test-class group by the requirement ids its tests name.</summary>
    public static CoverageScopeAssociation Behavior(BehaviorCoverage b, ScopeSelection selection, string? module, string? feature) =>
        Requirements(b.RequirementIds, selection, module, feature);

    public static CoverageScopeAssociation Overlap(TestOverlap o, TestCoverageReviewResult r, ScopeSelection selection, string? module, string? feature) =>
        r.QaTests.FirstOrDefault(q => q.Id == o.QaTestId) is { } qa ? QaTest(qa, selection, module, feature) : CoverageScopeAssociation.OutOfScope;

    /// <summary>How many selected scopes contributed the members that tie this requirement id to the scope (0 when none).</summary>
    public static int Contributors(IEnumerable<string> requirementIds, ScopeSelection selection, string? module, string? feature) =>
        requirementIds.SelectMany(id => Requirement(id, selection, module, feature).Members).SelectMany(m => m.ContributedBy).Select(c => c.ScopeId).Distinct().Count();

    public static int FlowContributors(QaTestCandidate q, ScopeSelection selection) => FlowId(q) is { } flowId
        ? Of(selection, ScopeMemberKind.CriticalE2EFlow).Where(m => m.Member.TargetId == flowId).SelectMany(m => m.ContributedBy).Select(c => c.ScopeId).Distinct().Count() : 0;

    public static CoverageScopeSummary Summarize(TestCoverageReviewResult r, ScopeSelection selection)
    {
        var (module, feature) = SpecificationPath(r);
        var requirementItems = r.Behaviors.Where(b => b.Id.StartsWith("req:", StringComparison.Ordinal)).ToList();
        var reqAssoc = requirementItems.Select(b => Behavior(b, selection, module, feature)).ToList();
        // Tests: developer tests and QA tests, each counted once by its own id however many scopes or requirements tie it in.
        var testAssoc = r.Tests.GroupBy(t => t.TestId, StringComparer.Ordinal).Select(g => Requirements(g.SelectMany(t => t.RequirementIds), selection, module, feature)).ToList();
        var nonFlowQa = r.QaTests.Where(q => FlowId(q) is null).GroupBy(q => q.Id, StringComparer.Ordinal).Select(g => QaTest(g.First(), selection, module, feature)).ToList();
        var flows = r.QaTests.Where(q => FlowId(q) is not null).GroupBy(q => q.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var flowMembers = Of(selection, ScopeMemberKind.CriticalE2EFlow).Select(m => m.Member.TargetId).Distinct(StringComparer.Ordinal).ToList();
        var flowsInReview = flows.Select(FlowId).ToHashSet(StringComparer.Ordinal);
        var flowsMissing = flowMembers.Count(id => !flowsInReview.Contains(id));
        var journeyMembers = Of(selection, ScopeMemberKind.Journey).Count();
        var assetMembers = Of(selection, ScopeMemberKind.TestDataAsset).Count();
        int Count(IEnumerable<CoverageScopeAssociation> a, CoverageScopeAssociation x) => a.Count(v => v == x);
        var notes = new List<string>();
        if (module is null || feature is null)
            notes.Add("The review's specification has no single module and feature folder, so a requirement named by id can only be reported as \"" + NotConfirmedLabel + "\".");
        var rows = new List<CoverageScopeCount>
        {
            new("requirements", "Requirements", Count(reqAssoc, CoverageScopeAssociation.InScope), Count(reqAssoc, CoverageScopeAssociation.NotConfirmed), requirementItems.Count),
            new("tests", "Tests linked through requirement ids", Count(testAssoc, CoverageScopeAssociation.InScope), Count(testAssoc, CoverageScopeAssociation.NotConfirmed), testAssoc.Count),
            new("qa-tests", "QA tests and acceptance scenarios", Count(nonFlowQa, CoverageScopeAssociation.InScope), Count(nonFlowQa, CoverageScopeAssociation.NotConfirmed), nonFlowQa.Count),
            new("flows", "Critical E2E flows", flows.Count(q => flowMembers.Contains(FlowId(q)!)), 0, flows.Count,
                flowsMissing > 0 ? $"{flowsMissing} named flow{(flowsMissing == 1 ? " is" : "s are")} not in this review (other environment, removed, or no active Target Environment at run time)." : null),
            new("journeys", "Journeys", 0, journeyMembers > 0 ? r.Journeys.Count : 0, r.Journeys.Count,
                journeyMembers > 0 ? $"The scope names {journeyMembers} Integration Journey{(journeyMembers == 1 ? "" : "s")}; this review's journeys come from source connections and share no stable id with them." : null),
            new("test-data", "Test Data assets", 0, 0, 0,
                assetMembers > 0 ? $"The scope names {assetMembers} Test Data asset{(assetMembers == 1 ? "" : "s")}; this review carries no Test Data asset ids, so they cannot be associated here (see Test Data with the same scope)." : null),
        };
        var relevant = new[] { ScopeMemberKind.Requirement, ScopeMemberKind.CriticalE2EFlow, ScopeMemberKind.Journey, ScopeMemberKind.TestDataAsset };
        var shared = selection.Members.Count(m => relevant.Contains(m.Member.Kind) && m.ContributedBy.Select(c => c.ScopeId).Distinct().Count() > 1);
        return new(rows, shared, selection.Scopes.Count, notes);
    }
}
