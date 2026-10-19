using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>
/// Resolves a consumer's role requirements against approved test data — selection only, never creation. Per role: an explicit choice wins
/// when it qualifies (it is never replaced by a default); otherwise exactly one qualifying asset is chosen, or exactly one marked default
/// among several; anything else is Ambiguous (the candidates' aliases) or Unresolved (why). When the consumer names data-set capabilities,
/// one approved data set that satisfies every required role is used first. Constraints are checked on the values in the backend; only
/// aliases leave it.
/// </summary>
public static class TestDataResolver
{
    public static TestDataResolutionResult Resolve(IReadOnlyList<TestDataAsset> assets, IReadOnlyList<TestDataRequirement> requirements,
        IReadOnlyList<TestDataConstraint>? constraints = null, IReadOnlyDictionary<string, string>? selections = null,
        IReadOnlyList<TestDataSet>? sets = null, IReadOnlyList<string>? dataSetCapabilities = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var chosen = new Dictionary<string, TestDataAsset>(StringComparer.Ordinal);
        var roles = new List<TestDataRoleResolution>();
        string? setAlias = null;

        if (dataSetCapabilities is { Count: > 0 } && sets is not null && (selections is null || selections.Count == 0))
        {
            var fitting = sets.Where(set => set.Approval == TestDataApproval.Approved && dataSetCapabilities.All(cap => set.Capabilities.Contains(cap, StringComparer.Ordinal)))
                .Select(set => (set, bound: requirements.Where(req => !req.Optional).Select(req => (req, asset: Bound(set, req, assets, at))).ToList()))
                .Where(item => item.bound.All(pair => pair.asset is not null)).ToList();
            if (fitting.Count == 1)
            {
                var (set, _) = fitting[0];
                setAlias = set.Alias;
                foreach (var requirement in requirements)
                {
                    var asset = Bound(set, requirement, assets, at);
                    if (asset is not null) chosen[requirement.Role] = asset;
                    roles.Add(asset is not null
                        ? new(requirement.Role, TestDataResolutionState.Resolved, asset.Alias, [], $"From approved data set {set.Alias}.", asset.Source, requirement.Optional)
                        : new(requirement.Role, TestDataResolutionState.Unresolved, null, [], $"Data set {set.Alias} binds no qualifying asset for this role.", null, requirement.Optional));
                }
                return new(roles, ConstraintProblems(constraints, chosen), setAlias);
            }
        }

        foreach (var requirement in requirements)
        {
            var candidates = Candidates(assets, requirement, at);
            if (selections?.GetValueOrDefault(requirement.Role) is { Length: > 0 } selected)
            {
                var pick = candidates.FirstOrDefault(asset => string.Equals(asset.Alias, selected, StringComparison.Ordinal));
                if (pick is not null) chosen[requirement.Role] = pick;
                roles.Add(pick is not null
                    ? new(requirement.Role, TestDataResolutionState.Selected, pick.Alias, [], "Chosen explicitly.", pick.Source, requirement.Optional)
                    : new(requirement.Role, TestDataResolutionState.Unresolved, null, [.. candidates.Select(asset => asset.Alias)],
                        $"The chosen asset {selected} does not satisfy this requirement; it is not replaced by another.", null, requirement.Optional));
                continue;
            }
            var defaults = candidates.Where(asset => asset.IsDefault).ToList();
            var single = candidates.Count == 1 ? candidates[0] : defaults.Count == 1 ? defaults[0] : null;
            if (single is not null)
            {
                chosen[requirement.Role] = single;
                roles.Add(new(requirement.Role, TestDataResolutionState.Resolved, single.Alias, [],
                    candidates.Count == 1 ? "The only qualifying asset." : "The one default among several qualifying assets.", single.Source, requirement.Optional));
            }
            else if (candidates.Count > 1)
                roles.Add(new(requirement.Role, TestDataResolutionState.Ambiguous, null, [.. candidates.Select(asset => asset.Alias)],
                    $"{candidates.Count} assets qualify and no single default decides: choose one.", null, requirement.Optional));
            else
                roles.Add(new(requirement.Role, TestDataResolutionState.Unresolved, null, [], Unqualified(assets, requirement, at), null, requirement.Optional));
        }
        return new(roles, ConstraintProblems(constraints, chosen), null);
    }

    /// <summary>The assets that satisfy a requirement now, in alias order.</summary>
    public static List<TestDataAsset> Candidates(IReadOnlyList<TestDataAsset> assets, TestDataRequirement requirement, DateTimeOffset now) =>
        [.. assets.Where(asset => Exclusion(asset, requirement, now) is null).OrderBy(asset => asset.Alias, StringComparer.Ordinal)];

    /// <summary>Why an asset does not satisfy a requirement, or null when it does.</summary>
    public static string? Exclusion(TestDataAsset asset, TestDataRequirement requirement, DateTimeOffset now)
    {
        if (asset.Kind != requirement.Kind) return $"is {asset.Kind}, not {requirement.Kind}";
        if (requirement.SourceIds.Count > 0 && !requirement.SourceIds.Contains(asset.SourceId, StringComparer.Ordinal)) return $"comes from source {asset.SourceId}, not {string.Join(" or ", requirement.SourceIds)}";
        if (requirement.ValueType is { } type && asset.ValueType != type) return $"is {asset.ValueType}, not {type}";
        if (requirement.MediaType is { } media && TestDataValidator.MediaType(asset.MediaType) != TestDataValidator.MediaType(media)) return $"is {asset.MediaType ?? "untyped"}, not {media}";
        if (requirement.EntityType is { } entity && !string.Equals(asset.EntityType, entity, StringComparison.OrdinalIgnoreCase)) return $"refers to {asset.EntityType ?? "nothing declared"}, not {entity}";
        if (requirement.Capabilities.FirstOrDefault(cap => !asset.Capabilities.Contains(cap, StringComparer.Ordinal)) is { } missing) return $"lacks capability {missing}";
        if (requirement.Environment is { } environment && (asset.Environment is null ? !requirement.AllowEnvironmentAgnostic
                : !string.Equals(asset.Environment, environment, StringComparison.OrdinalIgnoreCase)))
            return $"applies to {asset.Environment ?? "any environment (not allowed here)"}, not {environment}";
        if (EffectiveApproval(asset, now) is var approval and not TestDataApproval.Approved) return $"is {approval}";
        if (asset.Value is null && !asset.ExpectsAbsence) return "has no value available";
        if (requirement.RequireValidated && TestDataValidator.Validate(asset).Validation != TestDataValidation.Validated) return "is not validated";
        return null;
    }

    public static TestDataApproval EffectiveApproval(TestDataAsset asset, DateTimeOffset now) =>
        asset.Approval == TestDataApproval.Approved && asset.ExpiresAt is { } expires && expires <= now ? TestDataApproval.Expired : asset.Approval;

    private static TestDataAsset? Bound(TestDataSet set, TestDataRequirement requirement, IReadOnlyList<TestDataAsset> assets, DateTimeOffset now)
    {
        if (!set.Bindings.TryGetValue(requirement.Role, out var alias)) return null;
        if (requirement.Environment is { } environment && set.Environment is { } setEnvironment && !string.Equals(environment, setEnvironment, StringComparison.OrdinalIgnoreCase)) return null;
        var asset = assets.FirstOrDefault(item => string.Equals(item.Alias, alias, StringComparison.Ordinal));
        return asset is not null && Exclusion(asset, requirement, now) is null ? asset : null;
    }

    private static string Unqualified(IReadOnlyList<TestDataAsset> assets, TestDataRequirement requirement, DateTimeOffset now)
    {
        var near = assets.Where(asset => asset.Kind == requirement.Kind && requirement.Capabilities.All(cap => asset.Capabilities.Contains(cap, StringComparer.Ordinal)))
            .Select(asset => $"{asset.Alias} {Exclusion(asset, requirement, now)}").Take(3).ToList();
        var prefix = requirement.Optional ? "Optional: " : "";
        return near.Count == 0
            ? $"{prefix}No {requirement.Kind} asset with {(requirement.Capabilities.Count == 0 ? "the required metadata" : string.Join(", ", requirement.Capabilities))} exists. Nothing is generated."
            : $"{prefix}No asset qualifies: {string.Join("; ", near)}. Nothing is generated.";
    }

    private static List<string> ConstraintProblems(IReadOnlyList<TestDataConstraint>? constraints, IReadOnlyDictionary<string, TestDataAsset> chosen)
    {
        var problems = new List<string>();
        foreach (var constraint in constraints ?? [])
        {
            var bound = constraint.Roles.Where(chosen.ContainsKey).Select(role => (role, asset: chosen[role])).ToList();
            for (var i = 0; i < bound.Count; i++)
                for (var j = i + 1; j < bound.Count; j++)
                    if (bound[i].asset.Value is { } a && bound[j].asset.Value is { } b && a.Is(b.Reveal()))
                        problems.Add($"{bound[i].role} ({bound[i].asset.Alias}) and {bound[j].role} ({bound[j].asset.Alias}) are the same underlying value; they must differ.");
        }
        return problems;
    }
}
