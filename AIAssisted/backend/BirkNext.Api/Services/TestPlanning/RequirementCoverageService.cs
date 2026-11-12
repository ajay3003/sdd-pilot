using System.Text.Json;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Interpretation;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>A tester's review of one guide scenario in Test Plan Suggestions (the most recently updated draft that contains it).</summary>
public sealed record ScenarioReviewFact(string GuideKey, string ScenarioId, Guid DraftId, TestCaseReviewState Review, string? Fingerprint, bool Current,
    string? ReviewedBy, DateTimeOffset? ReviewedAt);

/// <summary>One stored scenario → requirement relation with its endpoints, as the coverage rule reads it.</summary>
public sealed record GuideAssociationFact(string RelationKey, string GuideKey, string ScenarioId, string ScenarioTitle, string? ScenarioFingerprint,
    string RequirementKey, string RequirementId, string RuleId, RelationResolution Resolution, RelationReviewState ReviewState, string Explanation,
    string? Reviewer, DateTimeOffset? DecidedAt);

/// <summary>Where scenario reviews and current scenario fingerprints come from (Test Plan Suggestions).</summary>
public interface IScenarioReviewSource
{
    Task<IReadOnlyDictionary<(string, string), ScenarioReviewFact>> ScenarioReviewsAsync(IReadOnlyCollection<string> guideKeys, CancellationToken ct);
    Task<Dictionary<string, string>> ScenarioFingerprintsAsync(string guideKey, CancellationToken ct);
}

/// <summary>A specification requirement of the latest interpretation (the denominator).</summary>
public sealed record RequirementFact(string Key, string Id, string? Module, string? Feature, string Title);

/// <summary>
/// MT-7 reviewed requirement coverage. Reads only: the shared canonical relationship registry (requirements of the scope's latest interpretation,
/// active scenario → requirement relations and the Relationship Review decisions on them) and Test Plan Suggestions reviews. It owns no table and
/// no second traceability store; every decision is made in Relationship Review and every scenario review in Test Plan Suggestions.
/// </summary>
public sealed class RequirementCoverageService(CanonicalInterpretationDbContext canonical, IScenarioReviewSource plans)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="rows">False returns the counts and the associations without one row per requirement (Test Plan Suggestions, Dashboard and
    /// Recommended Workflow need no requirement list; the real M2LB interpretation has hundreds of requirements).</param>
    public async Task<RequirementCoverageView> CoverageAsync(string scope, CancellationToken ct, bool rows = true)
    {
        scope = CanonicalInterpretationService.ValidScope(scope);
        var latest = await canonical.Snapshots.AsNoTracking().Where(s => s.Scope == scope).OrderByDescending(s => s.CreatedAt).Select(s => new { s.Id, s.CreatedAt }).FirstOrDefaultAsync(ct);
        if (latest is null)
            return Unknown(scope, null, null, "No relationship interpretation exists for this project yet. Run Analyze relationships in Relationship Review with the Source Analysis snapshots and a Delivery snapshot.");

        var requirementType = nameof(CanonicalEntityType.Requirement);
        var requirementRows = await canonical.Entities.AsNoTracking().Where(e => e.Scope == scope && e.EntityType == requirementType && e.LastInterpretationId == latest.Id)
            .Select(e => new { e.Id, e.IdentityKey, e.DisplayName, e.AttributesJson }).ToListAsync(ct);
        // The denominator: requirements a specification feature pack defines (module + feature). An id known only from a test reference has no
        // verified requirement identity and is not counted.
        var requirements = requirementRows.Select(e => (e, a: Attributes(e.AttributesJson))).Where(x => x.a.ContainsKey("module") && x.a.ContainsKey("feature")).Select(x =>
        {
            var (e, a) = x;
            return new RequirementFact(CanonicalKeys.EntityKey(CanonicalEntityType.Requirement, e.IdentityKey), a.GetValueOrDefault("id") ?? e.IdentityKey, a.GetValueOrDefault("module"),
                a.GetValueOrDefault("feature"), e.DisplayName);
        }).ToList();

        var rules = CorrelationRules.GuideScenarioRules.ToList();
        var relations = await canonical.Relations.AsNoTracking().Where(r => r.Scope == scope && r.Active && rules.Contains(r.RuleId)).ToListAsync(ct);
        var endpointIds = relations.SelectMany(r => new[] { r.SourceEntityId, r.TargetEntityId }).Distinct().ToList();
        var endpoints = await canonical.Entities.AsNoTracking().Where(e => endpointIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var decisionIds = relations.Select(r => r.ReviewDecisionId).OfType<Guid>().ToList();
        var decisions = await canonical.Decisions.AsNoTracking().Where(d => decisionIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var associations = relations.Where(r => endpoints.ContainsKey(r.SourceEntityId) && endpoints.ContainsKey(r.TargetEntityId)).Select(r =>
        {
            var (scenario, requirement) = (endpoints[r.SourceEntityId], endpoints[r.TargetEntityId]);
            var sa = Attributes(scenario.AttributesJson);
            var ra = Attributes(requirement.AttributesJson);
            var decision = r.ReviewDecisionId is { } d ? decisions.GetValueOrDefault(d) : null;
            return new GuideAssociationFact(r.RelationKey, sa.GetValueOrDefault("guide") ?? "", sa.GetValueOrDefault("scenarioId") ?? scenario.IdentityKey, scenario.DisplayName,
                sa.GetValueOrDefault("contentFingerprint"), CanonicalKeys.EntityKey(CanonicalEntityType.Requirement, requirement.IdentityKey), ra.GetValueOrDefault("id") ?? requirement.IdentityKey,
                r.RuleId, Enum.Parse<RelationResolution>(r.Resolution), Enum.Parse<RelationReviewState>(r.ReviewState), r.Explanation,
                decision?.Reviewer, decision?.DecidedAt);
        }).ToList();

        var scenarioType = nameof(CanonicalEntityType.Scenario);
        var guideScenarios = (await canonical.Entities.AsNoTracking().Where(e => e.Scope == scope && e.EntityType == scenarioType && e.LastInterpretationId == latest.Id
                && e.IdentityKey.StartsWith("system-understanding/")).Select(e => new { e.IdentityKey, e.AttributesJson }).ToListAsync(ct))
            .Select(e => Attributes(e.AttributesJson)).Select(a => (Guide: a.GetValueOrDefault("guide") ?? "", Id: a.GetValueOrDefault("scenarioId") ?? "")).ToList();

        if (requirements.Count == 0)
            return Unknown(scope, latest.Id, latest.CreatedAt, "The latest interpretation holds no specification requirements. Interpret a Delivery snapshot together with the Source Analysis snapshots.");
        if (guideScenarios.Count == 0)
            return Unknown(scope, latest.Id, latest.CreatedAt, "The latest interpretation includes no System Understanding scenarios (no guide recognises the interpreted sources), so no scenario coverage can be stated.");

        var guides = guideScenarios.Select(s => s.Guide).Concat(associations.Select(a => a.GuideKey)).Where(g => g.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var reviews = await plans.ScenarioReviewsAsync(guides, ct);
        var current = new Dictionary<(string, string), string>();
        foreach (var g in guides) foreach (var (id, fp) in await plans.ScenarioFingerprintsAsync(g, ct)) current[(g, id)] = fp;
        var view = Build(scope, latest.Id, latest.CreatedAt, requirements, associations, guideScenarios, reviews, current);
        return rows ? view : view with { Rows = [] };
    }

    static RequirementCoverageView Unknown(string scope, Guid? id, DateTimeOffset? at, string reason) =>
        new() { Scope = scope, Known = false, UnknownReason = reason, InterpretationId = id, InterpretedAt = at };

    static Dictionary<string, string> Attributes(string json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? [];

    /// <summary>Trust of one stored association (Relationship Review state wins; a decision made on older evidence is a suggestion again).</summary>
    public static AssociationTrust Trust(RelationResolution resolution, RelationReviewState review) => review switch
    {
        RelationReviewState.Confirmed => AssociationTrust.ReviewedCanonicalAssociation,
        RelationReviewState.Rejected => AssociationTrust.Rejected,
        RelationReviewState.NotReviewed or RelationReviewState.Deferred when resolution is RelationResolution.Explicit => AssociationTrust.ExplicitSourceReference,
        _ => AssociationTrust.SuggestedAssociation,
    };

    /// <summary>The coverage rule (pure). A requirement is "reviewed scenario coverage" only with a verified association (explicit from a published
    /// guide or confirmed on current evidence) to a scenario whose current revision a tester accepted for preparation, and only while the
    /// interpretation describes that same scenario revision.</summary>
    public static RequirementCoverageView Build(string scope, Guid interpretationId, DateTimeOffset interpretedAt, IReadOnlyList<RequirementFact> requirements,
        IReadOnlyList<GuideAssociationFact> associations, IReadOnlyList<(string Guide, string Id)> guideScenarios,
        IReadOnlyDictionary<(string, string), ScenarioReviewFact> reviews, IReadOnlyDictionary<(string, string), string> currentFingerprints)
    {
        var views = associations.Select(a =>
        {
            var key = (a.GuideKey, a.ScenarioId);
            var outdated = currentFingerprints.TryGetValue(key, out var now) ? now != a.ScenarioFingerprint : true;
            reviews.TryGetValue(key, out var review);
            var accepted = review is { Review: TestCaseReviewState.Accepted };
            var reviewCurrent = accepted && review!.Current && !outdated && review.Fingerprint == a.ScenarioFingerprint;
            var trust = Trust(a.Resolution, a.ReviewState);
            return new RequirementScenarioAssociation
            {
                RelationKey = a.RelationKey, GuideKey = a.GuideKey, ScenarioId = a.ScenarioId, ScenarioTitle = a.ScenarioTitle, RequirementKey = a.RequirementKey, RequirementId = a.RequirementId,
                // An association proposed on an older scenario text is not verified for the current one, whatever was decided on it.
                Trust = outdated && trust != AssociationTrust.Rejected ? AssociationTrust.SuggestedAssociation : trust,
                RuleId = a.RuleId, Resolution = a.Resolution.ToString(), ReviewState = a.ReviewState.ToString(), Explanation = a.Explanation, Reviewer = a.Reviewer, DecidedAt = a.DecidedAt,
                ScenarioReview = review is null ? "No draft" : accepted ? (reviewCurrent ? "Accepted for preparation" : "Accepted for an earlier scenario revision") : review.Review switch
                {
                    TestCaseReviewState.Excluded => "Excluded by the tester",
                    TestCaseReviewState.NeedsClarification => "Needs clarification",
                    _ => "Not reviewed",
                },
                ScenarioReviewCurrent = reviewCurrent, DraftId = review?.DraftId, ScenarioReviewedBy = review?.ReviewedBy, ScenarioReviewedAt = review?.ReviewedAt,
                InterpretationOutdated = outdated,
            };
        }).OrderBy(a => a.GuideKey, StringComparer.Ordinal).ThenBy(a => a.ScenarioId, StringComparer.Ordinal).ThenBy(a => a.RequirementKey, StringComparer.Ordinal).ToList();

        var byRequirement = views.ToLookup(a => a.RequirementKey, StringComparer.Ordinal);
        var rows = requirements.OrderBy(r => r.Module, StringComparer.Ordinal).ThenBy(r => r.Feature, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).Select(r =>
        {
            var list = byRequirement[r.Key].ToList();
            var verified = list.Where(a => a.Verified).ToList();
            var (state, reason) = verified.FirstOrDefault(a => a.ScenarioReviewCurrent) is { } covered
                ? (RequirementCoverageState.ReviewedScenarioCoverage, $"\"{covered.ScenarioTitle}\" is linked ({Words(covered.Trust)}) and was accepted for preparation by {covered.ScenarioReviewedBy ?? "a tester"}. Not an executed test.")
                : verified.Count > 0
                    ? (RequirementCoverageState.AssociationVerifiedScenarioNotReviewed, $"Linked to {string.Join(", ", verified.Select(a => $"\"{a.ScenarioTitle}\""))}; no tester has accepted the current scenario revision yet.")
                    : list.Any(a => a.Trust == AssociationTrust.SuggestedAssociation)
                        ? (RequirementCoverageState.NeedsRelationshipReview, "Only suggested associations (or a decision made on older evidence). Coverage association not verified: decide it in Relationship Review.")
                        : (RequirementCoverageState.NoDocumentedScenarioCoverage, list.Count > 0
                            ? "Every proposed association was rejected. No documented scenario covers this requirement."
                            : "No System Understanding scenario states or suggests coverage. This says nothing about other tests.");
            return new RequirementCoverageRow { RequirementKey = r.Key, RequirementId = r.Id, Module = r.Module, Feature = r.Feature, Title = r.Title, State = state, Reason = reason, Associations = list };
        }).ToList();

        var linked = views.Where(a => a.Trust != AssociationTrust.Rejected).Select(a => (a.GuideKey, a.ScenarioId)).ToHashSet();
        return new RequirementCoverageView
        {
            Scope = scope, Known = true, InterpretationId = interpretationId, InterpretedAt = interpretedAt, Requirements = rows.Count,
            Reviewed = rows.Count(r => r.State == RequirementCoverageState.ReviewedScenarioCoverage),
            VerifiedNotReviewed = rows.Count(r => r.State == RequirementCoverageState.AssociationVerifiedScenarioNotReviewed),
            NeedsReview = rows.Count(r => r.State == RequirementCoverageState.NeedsRelationshipReview),
            NoScenario = rows.Count(r => r.State == RequirementCoverageState.NoDocumentedScenarioCoverage),
            Rows = rows, Associations = views,
            ScenariosWithoutRequirement = [.. guideScenarios.Where(s => !linked.Contains((s.Guide, s.Id))).Select(s => $"{s.Guide}/{s.Id}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        };
    }

    static string Words(AssociationTrust trust) => trust == AssociationTrust.ExplicitSourceReference ? "named by the published guide" : "confirmed in Relationship Review";
}
