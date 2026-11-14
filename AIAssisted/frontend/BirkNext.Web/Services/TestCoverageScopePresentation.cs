using BirkNext.Interpretation;
using BirkNext.ManualReview;
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

/// <summary>How a source-derived journey relates to an Integration Journey: the same id, or a reviewer's recorded decision.</summary>
public enum JourneyAssociationKind { Exact, Confirmed, Rejected }

/// <summary>One association of a source journey with an Integration Journey ("packId:journeyId"). Only Exact and Confirmed ever count.</summary>
public sealed record JourneyAssociationLink(string IntegrationJourneyId, JourneyAssociationKind Kind, string? Reviewer, DateTimeOffset? DecidedAt);

/// <summary>Why a source journey is or is not tied to the selected scopes.</summary>
public enum JourneyScopeReason { SameIdentity, ConfirmedAssociation, NoCandidate, CandidateNotReviewed, Rejected, NoJourneyInScope }

/// <param name="Members">The scope Journey members the journey is tied to (exact or confirmed); empty unless in scope.</param>
/// <param name="Scopes">Names of the selected scopes that contributed those members.</param>
public sealed record JourneyScopeState(CoverageScopeAssociation Association, JourneyScopeReason Reason, string Text, IReadOnlyList<string> Members, IReadOnlyList<string> Scopes);

/// <summary>One Test Data asset linked to QA tests in this review (deduplicated by asset id), with the tests and selected scopes that use it.</summary>
/// <param name="Unresolved">Every link to this asset is unresolved (deprecated, invalid, no longer approved or changed since bound): shown, never counted as in scope.</param>
public sealed record CoverageAssetUse(string AssetId, string? Alias, IReadOnlyList<string> Fingerprints, IReadOnlyList<string> Tests, IReadOnlyList<string> Sources, bool InScope, IReadOnlyList<string> Scopes,
    bool Unresolved = false);

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
/// ("packId:journeyId"). A journey is in scope only through (a) exact identity (a Journey member id equals the review journey id) or (b) a
/// reviewer's confirmed association recorded in the shared manual-review store (module <see cref="ManualReviewModules.JourneyAssociation"/>,
/// subject = source journey id, applies to = "packId:journeyId", latest decision Acceptable). A rejected association (latest IssueFound) never
/// counts; a structural candidate (<see cref="JourneyAssociationCandidate"/>) is shown for review and never counts. Names are never matched.</item>
/// <item>Test Data: an asset is in scope when a TestDataAsset member id equals an asset id a QA test is linked to (from the flow definition or a
/// confirmed Test Plan case); each asset counts once however many tests and scopes use it.</item>
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

    public const string CandidateLabel = "Candidate — needs review";

    /// <summary>The latest recorded association decision per (source journey, Integration Journey) pair. The store marks the latest per subject
    /// only, so the pair is resolved here by decision time. Decisions of other modules or without an "applies to" are ignored.</summary>
    public static IReadOnlyDictionary<(string Subject, string AppliesTo), ManualReviewDecisionView> LatestAssociations(IEnumerable<ManualReviewDecisionView>? decisions) =>
        (decisions ?? []).Where(d => d.Module == ManualReviewModules.JourneyAssociation && !string.IsNullOrWhiteSpace(d.AppliesTo) && !string.IsNullOrWhiteSpace(d.SubjectKey))
            .GroupBy(d => (d.SubjectKey, d.AppliesTo!)).ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.DecidedAt).First());

    /// <summary>The recorded associations of one source journey (Acceptable = confirmed, IssueFound = rejected; any other latest value is undecided).</summary>
    public static IReadOnlyList<JourneyAssociationLink> Associations(Journey journey, IReadOnlyDictionary<(string Subject, string AppliesTo), ManualReviewDecisionView> latest) =>
        [.. latest.Where(kv => kv.Key.Subject == journey.Id && kv.Value.Decision is ManualReviewDecisionValue.Acceptable or ManualReviewDecisionValue.IssueFound)
            .Select(kv => new JourneyAssociationLink(kv.Key.AppliesTo, kv.Value.Decision == ManualReviewDecisionValue.Acceptable ? JourneyAssociationKind.Confirmed : JourneyAssociationKind.Rejected,
                kv.Value.Reviewer, kv.Value.DecidedAt))
            .OrderBy(l => l.IntegrationJourneyId, StringComparer.Ordinal)];

    /// <summary>The recorded decision on one candidate, or null when it was not reviewed.</summary>
    public static JourneyAssociationKind? CandidateDecision(Journey journey, string integrationJourneyId, IReadOnlyDictionary<(string Subject, string AppliesTo), ManualReviewDecisionView> latest) =>
        latest.TryGetValue((journey.Id, integrationJourneyId), out var d)
            ? d.Decision switch { ManualReviewDecisionValue.Acceptable => JourneyAssociationKind.Confirmed, ManualReviewDecisionValue.IssueFound => JourneyAssociationKind.Rejected, _ => (JourneyAssociationKind?)null }
            : null;

    /// <summary>Whether a source journey is tied to the selected scopes' Journey members (exact identity or confirmed association only).</summary>
    public static JourneyScopeState JourneyScope(Journey journey, ScopeSelection selection, IReadOnlyDictionary<(string Subject, string AppliesTo), ManualReviewDecisionView> latest)
    {
        var members = Of(selection, ScopeMemberKind.Journey).ToList();
        if (members.Count == 0) return new(CoverageScopeAssociation.OutOfScope, JourneyScopeReason.NoJourneyInScope, "No Integration Journey is in the selected scopes", [], []);
        static IReadOnlyList<string> ScopesOf(IEnumerable<ScopeSelectionMember> ms) =>
            [.. ms.SelectMany(m => m.ContributedBy).Select(c => c.ScopeName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        var exact = members.Where(m => string.Equals(m.Member.TargetId, journey.Id, StringComparison.Ordinal)).ToList();
        if (exact.Count > 0) return new(CoverageScopeAssociation.InScope, JourneyScopeReason.SameIdentity, "In scope — same journey identity", [journey.Id], ScopesOf(exact));
        var links = Associations(journey, latest);
        var confirmed = members.Where(m => links.Any(l => l.Kind == JourneyAssociationKind.Confirmed && l.IntegrationJourneyId == m.Member.TargetId)).ToList();
        if (confirmed.Count > 0)
            return new(CoverageScopeAssociation.InScope, JourneyScopeReason.ConfirmedAssociation,
                $"In scope — association confirmed with {string.Join(", ", confirmed.Select(m => m.Member.Label is { Length: > 0 } l ? l : m.Member.TargetId).Distinct())}",
                [.. confirmed.Select(m => m.Member.TargetId).Distinct(StringComparer.Ordinal)], ScopesOf(confirmed));
        var memberIds = members.Select(m => m.Member.TargetId).ToHashSet(StringComparer.Ordinal);
        var unreviewed = journey.AssociationCandidates.Where(c => memberIds.Contains(c.IntegrationJourneyId) && CandidateDecision(journey, c.IntegrationJourneyId, latest) is null).ToList();
        var rejected = links.Where(l => l.Kind == JourneyAssociationKind.Rejected && memberIds.Contains(l.IntegrationJourneyId)).ToList();
        var reason = unreviewed.Count > 0 ? JourneyScopeReason.CandidateNotReviewed : rejected.Count > 0 ? JourneyScopeReason.Rejected : JourneyScopeReason.NoCandidate;
        return new(CoverageScopeAssociation.NotConfirmed, reason, $"{NotConfirmedLabel} — {ReasonText(reason)}", [], []);
    }

    /// <summary>Why a journey is (not) tied to the scope, in plain words.</summary>
    public static string ReasonText(JourneyScopeReason reason) => reason switch
    {
        JourneyScopeReason.SameIdentity => "same journey identity",
        JourneyScopeReason.ConfirmedAssociation => "association confirmed by a reviewer",
        JourneyScopeReason.CandidateNotReviewed => "candidate not reviewed",
        JourneyScopeReason.Rejected => "association rejected by a reviewer",
        JourneyScopeReason.NoCandidate => "no candidate",
        _ => "no Integration Journey in the selected scopes",
    };

    /// <summary>Plain label of a QA test's data need. "Not assessed" is never "no data needed".</summary>
    public static string DataNeed(QaTestCandidate q) => q.DataNeed switch
    {
        CoverageDataNeed.Linked => $"Linked ({string.Join(", ", q.TestData.Select(l => l.AssetId).Distinct(StringComparer.Ordinal))})",
        CoverageDataNeed.RequiredButMissing => "Required but missing",
        CoverageDataNeed.LinkNeedsReview => $"Linked revision not usable — needs review ({string.Join(", ", q.TestData.Where(l => !l.Resolved).Select(l => l.AssetId).Distinct(StringComparer.Ordinal))})",
        _ => "Not assessed",
    };

    /// <summary>Plain state of one link: the bound revision is current and approved, or it needs review (with Test Data's reason).</summary>
    public static string LinkState(CoverageTestDataLink link) => link.Resolved ? "Resolved" : $"Unresolved — {link.StateReason ?? "needs review"}";

    /// <summary>
    /// Opens Test Data with this QA test as the consumer (Test Data owns requirements and bindings): it shows matching approved assets, records
    /// the requirement ("Add to requested Test Data") and binds an asset revision ("Use for this requirement"). The consumer id is project-scoped.
    /// </summary>
    public static string TestDataHref(QaTestCandidate q, string? projectKey, BirkNext.TestData.TestDataDataType dataType, string? environmentId = null) =>
        "test-data?consumer=" + Uri.EscapeDataString(CoverageTestDataConsumers.ConsumerId(projectKey, q.Id))
        + "&consumerType=" + Uri.EscapeDataString(CoverageTestDataConsumers.ConsumerType)
        + "&consumerName=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(q.Title) ? q.Id : q.Title)
        + "&dataType=" + dataType
        + (string.IsNullOrWhiteSpace(environmentId) ? "" : "&environment=" + Uri.EscapeDataString(environmentId));

    /// <summary>Every Test Data asset linked to a QA test in this review, once per asset id, with the tests (and, with a selection, the scopes) that use it.</summary>
    public static IReadOnlyList<CoverageAssetUse> Assets(TestCoverageReviewResult r, ScopeSelection? selection)
    {
        var members = selection is null ? [] : Of(selection, ScopeMemberKind.TestDataAsset).ToList();
        return [.. r.QaTests.SelectMany(q => q.TestData.Select(l => (Test: q, Link: l))).GroupBy(x => x.Link.AssetId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var named = members.Where(m => string.Equals(m.Member.TargetId, g.Key, StringComparison.Ordinal)).ToList();
            return new CoverageAssetUse(g.Key, g.Select(x => x.Link.Alias).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)),
                [.. g.Select(x => x.Link.Fingerprint).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!).Distinct(StringComparer.Ordinal)],
                [.. g.Select(x => x.Test.Title).Distinct(StringComparer.Ordinal)], [.. g.Select(x => x.Link.Source).Distinct(StringComparer.Ordinal)],
                named.Count > 0, [.. named.SelectMany(m => m.ContributedBy).Select(c => c.ScopeName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                g.All(x => !x.Link.Resolved));
        })];
    }

    public static CoverageScopeSummary Summarize(TestCoverageReviewResult r, ScopeSelection selection, IEnumerable<ManualReviewDecisionView>? decisions = null)
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
        var assetMembers = Of(selection, ScopeMemberKind.TestDataAsset).Select(m => m.Member.TargetId).Distinct(StringComparer.Ordinal).ToList();
        var latest = LatestAssociations(decisions);
        var journeyStates = r.Journeys.GroupBy(j => j.Id, StringComparer.Ordinal).Select(g => JourneyScope(g.First(), selection, latest)).ToList();
        var assets = Assets(r, selection);
        var assetsUnlinked = assetMembers.Count(id => assets.All(a => a.AssetId != id));
        var notAssessed = r.QaTests.Count(q => q.DataNeed == CoverageDataNeed.NotAssessed);
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
            new("journeys", "Journeys", Count(journeyStates.Select(s => s.Association), CoverageScopeAssociation.InScope),
                Count(journeyStates.Select(s => s.Association), CoverageScopeAssociation.NotConfirmed), journeyStates.Count,
                journeyMembers > 0 ? $"The scope names {journeyMembers} Integration Journey{(journeyMembers == 1 ? "" : "s")}; a source journey counts only by the same id or a reviewer-confirmed association ({journeyStates.Count(s => s.Reason == JourneyScopeReason.CandidateNotReviewed)} with a candidate not reviewed, {journeyStates.Count(s => s.Reason == JourneyScopeReason.Rejected)} rejected)." : null),
            // An asset whose every link is unresolved is named by the scope but not usable: reported as not confirmed, never counted as in scope.
            new("test-data", "Test Data assets", assets.Count(a => a.InScope && !a.Unresolved), assets.Count(a => a.InScope && a.Unresolved), assets.Count, TestDataNote(assetMembers.Count, assetsUnlinked, notAssessed)),
        };
        static string? TestDataNote(int named, int unlinked, int notAssessed)
        {
            var parts = new List<string>();
            if (named > 0) parts.Add($"The scope names {named} Test Data asset{(named == 1 ? "" : "s")}; {unlinked} of them {(unlinked == 1 ? "is" : "are")} not linked to any QA test in this review.");
            if (notAssessed > 0) parts.Add($"{notAssessed} QA test{(notAssessed == 1 ? " is" : "s are")} not assessed for Test Data (no link recorded; never read as \"no data needed\").");
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
        var relevant = new[] { ScopeMemberKind.Requirement, ScopeMemberKind.CriticalE2EFlow, ScopeMemberKind.Journey, ScopeMemberKind.TestDataAsset };
        var shared = selection.Members.Count(m => relevant.Contains(m.Member.Kind) && m.ContributedBy.Select(c => c.ScopeId).Distinct().Count() > 1);
        return new(rows, shared, selection.Scopes.Count, notes);
    }
}
