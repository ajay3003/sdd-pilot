using System.Text.Json.Serialization;
using BirkNext.TestData;

namespace BirkNext.Integrations;

// ── Connected Systems ↔ Test Data ───────────────────────────────────────────────────────────────────────────────────────
// Test Data owns reusable assets; a Connected System declares what Test Data it needs (requirements, from its provider) and stores the ACTUAL
// binding — an asset alias, never a value. Asset metadata (semantic role, "Applies to") only drives suggestions; a suggestion is never a binding
// until a person applies it, an existing binding always wins, and nothing is ever bound to "the first" asset of a role.

/// <summary>A reusable system-level Test Data need declared by a provider (e.g. Altinn intake: Reporter identity, Child identity).</summary>
public sealed record ConnectedSystemTestDataRequirement(string Id, string DisplayName, string SemanticRole, TestDataDataType DataType, bool Required,
    IReadOnlyList<string> Contexts, string Description);

/// <summary>
/// A persisted binding: requirement → Test Data asset. <see cref="AssetId"/> is the authoritative, stable reference (a payload keeps its id across
/// versions, so a newly approved version is used without rebinding); <see cref="AssetAlias"/> is the alias at bind time, kept for display and for
/// bindings saved before asset ids were recorded (migrated to an id once, deterministically). <see cref="ContextMatched"/> records whether the
/// asset's "Applies to" named this system when it was bound, so a later metadata change is reported (Needs review) instead of silently unbinding.
/// </summary>
public sealed record ConnectedSystemTestDataBinding(string RequirementId, string AssetAlias)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AssetId { get; init; }
    public DateTimeOffset BoundAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? UpdatedAt { get; init; }
    public bool ContextMatched { get; init; }
    /// <summary>How the binding was made (<see cref="ConnectedSystemTestDataBindingOrigins"/>); null = chosen by a person before origins were recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? BoundBy { get; init; }
}

public static class ConnectedSystemTestDataBindingOrigins
{
    /// <summary>Applied or changed by a person in Connected Systems → Test Data.</summary>
    public const string User = "User";
    /// <summary>Recorded once by the binding migration: the asset the provider's readiness already consumed, now persisted (never a suggestion).</summary>
    public const string Migration = "Migration";

    public static string Label(string? origin) => origin == Migration ? "Recorded from existing readiness" : "Chosen by you";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectedSystemTestDataState { Bound, Suggested, NeedsSelection, Missing, MissingReference, NeedsReview, OptionalMissing }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataMatchStrength { Strong, Weak }

public sealed record ConnectedSystemTestDataCandidate(string Alias, string? Label, TestDataMatchStrength Strength, string Reason, string Scope, bool IsDefault);

/// <summary>One requirement with its binding/suggestion state. <see cref="Suggested"/> is preselected for "Apply selected" only when it is the
/// single strong match — never persisted until applied.</summary>
public sealed record ConnectedSystemTestDataRow(ConnectedSystemTestDataRequirement Requirement, ConnectedSystemTestDataState State, string? BoundAlias, string? Suggested,
    IReadOnlyList<ConnectedSystemTestDataCandidate> Candidates, string Detail)
{
    public string? BoundLabel { get; init; }
    public string? BoundScope { get; init; }
    /// <summary>The stable asset id the binding resolves to (null when unbound or the reference is missing).</summary>
    public string? BoundAssetId { get; init; }
    public string? BoundBy { get; init; }
    /// <summary>A newer version of the bound payload exists but is not approved yet: the binding keeps using the approved version.</summary>
    public string? DraftNote { get; init; }
}

public sealed record ConnectedSystemTestDataView(string IntegrationId, string IntegrationName, IReadOnlyList<ConnectedSystemTestDataRow> Rows, SetupReadinessItem Readiness, string StatusLabel);

/// <summary>Apply/Change/Clear: the bindings to set (alias null = clear). Requirements not listed keep their binding.</summary>
public sealed record ConnectedSystemTestDataUpdate(IReadOnlyList<ConnectedSystemTestDataBindingChange> Changes);

public sealed record ConnectedSystemTestDataBindingChange(string RequirementId, string? AssetAlias);

public static class ConnectedSystemTestDataMatcher
{
    public const string ConsumerType = "Connected system";

    /// <summary>
    /// The asset can be used in this system's environment: Shared (no environment), or the same environment by id, by name, or by the canonical name
    /// a provider shows in parentheses ("Test (TT02)" is TT02). Never a partial-text match.
    /// </summary>
    public static bool ScopeCompatible(TestDataAssetSummary asset, string? environmentId, string? environmentName) =>
        asset.Environment is null || EnvironmentNames(environmentId, environmentName).Contains(asset.Environment.Trim(), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> EnvironmentNames(string? environmentId, string? environmentName)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(environmentId)) names.Add(environmentId.Trim());
        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            names.Add(environmentName.Trim());
            var open = environmentName.IndexOf('(');
            var close = environmentName.LastIndexOf(')');
            if (open >= 0 && close > open + 1) { names.Add(environmentName[(open + 1)..close].Trim()); names.Add(environmentName[..open].Trim()); }
        }
        return names;
    }

    private static bool TypeCompatible(TestDataAssetSummary asset, ConnectedSystemTestDataRequirement r) => (asset.DataType ?? TestDataDataTypes.Of(asset)) == r.DataType;

    private static bool Usable(TestDataAssetSummary asset) => asset.Approval == TestDataApproval.Approved && asset.Status is not (TestDataAssetStatus.Deprecated or TestDataAssetStatus.NeedsReview);

    public static bool ContextMatches(TestDataAssetSummary asset, ConnectedSystemTestDataRequirement r) => asset.AppliesTo?.Any(r.Contexts.Contains) == true;

    public static string Scope(TestDataAssetSummary asset) => asset.Environment ?? "Shared";

    /// <summary>
    /// Compatible assets for a requirement: the exact semantic role and data type, usable in the environment, not deprecated. Strong = approved and
    /// "Applies to" names one of the requirement's contexts; Weak = role/type match without that context, or not approved yet.
    /// </summary>
    public static IReadOnlyList<ConnectedSystemTestDataCandidate> Candidates(ConnectedSystemTestDataRequirement r, IReadOnlyList<TestDataAssetSummary> assets, string? environmentId, string? environmentName) =>
        [.. assets.Where(a => string.Equals(a.SemanticRole, r.SemanticRole, StringComparison.Ordinal) && TypeCompatible(a, r) && ScopeCompatible(a, environmentId, environmentName)
                && a.Status != TestDataAssetStatus.Deprecated)
            .Select(a =>
            {
                var strong = Usable(a) && ContextMatches(a, r);
                var reason = $"{TestDataSemanticRoles.Label(a.SemanticRole)} · {(ContextMatches(a, r) ? string.Join(", ", a.AppliesTo!.Where(r.Contexts.Contains).Select(TestDataContexts.Label)) : "no matching Applies to")} · {Scope(a)}"
                    + (Usable(a) ? "" : $" · {a.Status?.ToString() ?? a.Approval.ToString()}");
                return new ConnectedSystemTestDataCandidate(a.Alias, a.Label, strong ? TestDataMatchStrength.Strong : TestDataMatchStrength.Weak, reason, Scope(a), a.IsDefault);
            })
            .OrderBy(c => c.Strength).ThenByDescending(c => c.IsDefault).ThenBy(c => c.Alias, StringComparer.Ordinal)];

    /// <summary>
    /// One requirement's state. Precedence: explicit saved binding &gt; single strong match &gt; the source's single default among several strong
    /// matches (Test Pack/provider default) &gt; weak candidates (choose) &gt; unresolved. A binding resolves by its stable asset id (by alias only
    /// for a legacy binding without one); a binding whose asset is gone is a missing reference — never re-pointed to a similarly named asset; the
    /// replacement is suggested, never applied.
    /// </summary>
    public static ConnectedSystemTestDataRow Evaluate(ConnectedSystemTestDataRequirement r, ConnectedSystemTestDataBinding? binding, IReadOnlyList<TestDataAssetSummary> assets,
        string? environmentId, string? environmentName)
    {
        var candidates = Candidates(r, assets, environmentId, environmentName);
        var strong = candidates.Where(c => c.Strength == TestDataMatchStrength.Strong).ToList();
        // Only a single strong match is a suggestion; among several, the source's single default is highlighted but never preselected.
        var suggestion = strong.Count == 1 ? strong[0].Alias : null;
        var defaultOfSeveral = strong.Count > 1 && strong.Count(c => c.IsDefault) == 1 ? strong.Single(c => c.IsDefault).Alias : null;
        if (binding is not null)
        {
            if (Resolve(binding, assets) is not { } bound)
                return new(r, ConnectedSystemTestDataState.MissingReference, binding.AssetAlias, suggestion ?? defaultOfSeveral, candidates,
                    $"Previously bound Test Data ({binding.AssetAlias}) no longer exists.{(suggestion is null ? "" : " Suggested replacement available.")}");
            var row = new ConnectedSystemTestDataRow(r, ConnectedSystemTestDataState.Bound, bound.Alias, null, candidates, $"{TestDataSemanticRoles.Label(bound.SemanticRole)} · {Scope(bound)}")
                { BoundLabel = bound.Label, BoundScope = Scope(bound), BoundAssetId = bound.Id, BoundBy = binding.BoundBy,
                  DraftNote = bound.DraftVersion is { } draft ? $"Draft update available (version {draft}); this system keeps using approved version {bound.Version ?? 1} until it is approved." : null };
            string? problem = bound.SemanticRole is { } role && role != r.SemanticRole ? $"its role is now {TestDataSemanticRoles.Label(role)}, not {TestDataSemanticRoles.Label(r.SemanticRole)}"
                : !TypeCompatible(bound, r) ? $"it is {TestDataDataTypes.Label(bound.DataType ?? TestDataDataTypes.Of(bound))}, not {TestDataDataTypes.Label(r.DataType)}"
                : !ScopeCompatible(bound, environmentId, environmentName) ? $"it is scoped to {bound.Environment}"
                : !Usable(bound) ? $"it is {(bound.Status?.ToString() ?? bound.Approval.ToString()).ToLowerInvariant()}"
                : binding.ContextMatched && !ContextMatches(bound, r) ? "context metadata no longer matches this consumer" : null;
            return problem is null ? row : row with { State = ConnectedSystemTestDataState.NeedsReview, Detail = $"Needs review: {bound.Alias} is still bound, but {problem}." };
        }
        if (suggestion is not null)
            return new(r, ConnectedSystemTestDataState.Suggested, null, suggestion, candidates, $"Strong match: {candidates.First(c => c.Alias == suggestion).Reason}");
        if (strong.Count > 1) return new(r, ConnectedSystemTestDataState.NeedsSelection, null, null, candidates,
            $"{strong.Count} compatible assets found — needs selection.{(defaultOfSeveral is null ? "" : $" Default: {defaultOfSeveral}.")}");
        if (candidates.Count > 0) return new(r, ConnectedSystemTestDataState.NeedsSelection, null, null, candidates, $"{candidates.Count} possible asset{(candidates.Count == 1 ? "" : "s")} — not a strong match; choose explicitly.");
        return new(r, r.Required ? ConnectedSystemTestDataState.Missing : ConnectedSystemTestDataState.OptionalMissing, null, null, candidates,
            r.Required ? $"No compatible {TestDataSemanticRoles.Label(r.SemanticRole)} Test Data found." : "Optional · not configured.");
    }

    /// <summary>The bound asset: by its stable id; by alias only for a legacy binding that has no id yet. Never a similarly named asset.</summary>
    public static TestDataAssetSummary? Resolve(ConnectedSystemTestDataBinding binding, IReadOnlyList<TestDataAssetSummary> assets) =>
        binding.AssetId is { Length: > 0 } id ? assets.FirstOrDefault(a => a.Id == id) : assets.FirstOrDefault(a => a.Alias == binding.AssetAlias);

    /// <summary>
    /// TEST DATA readiness from persisted references only: Ready when every required requirement resolves to a valid bound asset; a suggestion,
    /// a role match or a name match never counts. Optional requirements never block, and are reported ("1 optional not configured").
    /// </summary>
    public static ConnectedSystemTestDataView View(ExternalIntegration integration, IReadOnlyList<ConnectedSystemTestDataRequirement> requirements, IReadOnlyList<TestDataAssetSummary> assets)
    {
        var rows = requirements.Select(r => Evaluate(r, integration.TestDataBindings?.FirstOrDefault(b => b.RequirementId == r.Id), assets, integration.TargetEnvironmentId, integration.EnvironmentName)).ToList();
        var required = rows.Where(r => r.Requirement.Required).ToList();
        var ready = required.Count(r => r.State == ConnectedSystemTestDataState.Bound);
        var optional = rows.Where(r => !r.Requirement.Required).ToList();
        var optionalMissing = optional.Count(r => r.State != ConnectedSystemTestDataState.Bound);
        var optionalNote = optionalMissing == 0 ? "" : $" · {optionalMissing} optional not configured";
        var review = rows.Any(r => r.State == ConnectedSystemTestDataState.NeedsReview);
        var (state, label, reason) = requirements.Count == 0 ? (SetupReadinessState.NotApplicable, "Not required", "This system declares no Test Data requirements.")
            : review ? (SetupReadinessState.Partial, "Needs review", string.Join(" ", rows.Where(r => r.State == ConnectedSystemTestDataState.NeedsReview).Select(r => r.Detail)))
            : ready == required.Count ? (SetupReadinessState.Ready, "Ready", $"{ready} / {required.Count} required assets resolved{optionalNote}.")
            : ready == 0 && rows.All(r => r.State is not ConnectedSystemTestDataState.Suggested) ? (SetupReadinessState.Partial, "Missing",
                string.Join(" · ", required.Where(r => r.State != ConnectedSystemTestDataState.Bound).Select(r => $"{r.Requirement.DisplayName}: {StateLabel(r.State)}")))
            : (SetupReadinessState.Partial, "Partial", $"{ready} / {required.Count} required assets resolved · " + string.Join(" · ", required.Where(r => r.State != ConnectedSystemTestDataState.Bound).Select(r => $"{r.Requirement.DisplayName}: {StateLabel(r.State)}")));
        return new(integration.Id, integration.Name, rows, new("test-data", "Test Data", state, reason), label);
    }

    public static string StateLabel(ConnectedSystemTestDataState state) => state switch
    {
        ConnectedSystemTestDataState.Bound => "Ready",
        ConnectedSystemTestDataState.Suggested => "Suggested",
        ConnectedSystemTestDataState.NeedsSelection => "Needs selection",
        ConnectedSystemTestDataState.MissingReference => "Missing reference",
        ConnectedSystemTestDataState.NeedsReview => "Needs review",
        ConnectedSystemTestDataState.OptionalMissing => "Optional · not configured",
        _ => "Missing",
    };
}
