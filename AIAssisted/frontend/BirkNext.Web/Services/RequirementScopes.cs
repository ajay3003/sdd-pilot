namespace BirkNext.Web.Services;

/// <summary>
/// The identity of a requirement: workspace + module + artifact pack + local id. "FR-001" in pack 001 and "FR-001" in pack 002 are different
/// requirements; the display id stays "FR-001". Without packs (a single pack or none) the scope is null and identity is the local id, exactly as
/// before packs existed.
/// </summary>
public sealed record RequirementKey(string? Workspace, string? Module, string? Pack, string LocalId)
{
    public string? Scope => RequirementScopes.Of(Workspace, Module ?? "", Pack);

    public static RequirementKey Of(string? scope, string localId) =>
        RequirementScopes.Parse(scope) is var (workspace, module, pack) ? new(workspace, module, pack, localId) : new(null, null, null, localId);

    /// <summary>"Pack 002-refund · FR-001", or "FR-001" without packs.</summary>
    public string Display => Pack is null ? LocalId : $"Pack {Pack} · {LocalId}";
}

/// <summary>
/// The requirement scope persisted on lifecycle items (snapshots, changes, links, implementation and test evidence, clarifications). An item
/// applies only within its own scope: a sibling pack's evidence never satisfies a requirement, and unscoped (legacy) evidence is never applied to
/// a pack implicitly.
/// </summary>
public static class RequirementScopes
{
    public static string? Of(string? workspace, string module, string? pack) => pack is null ? null : $"{workspace ?? "manual"}|{module}|{pack}";

    public static (string? Workspace, string Module, string Pack)? Parse(string? scope)
    {
        if (scope is null) return null;
        var parts = scope.Split('|');
        return parts.Length < 3 ? null : (parts[0] == "manual" ? null : string.Join('|', parts[..^2]), parts[^2], parts[^1]);
    }

    public static bool Matches(string? itemScope, string? activeScope) => string.Equals(itemScope, activeScope, StringComparison.Ordinal);

    public static string? PackOf(string? scope) => Parse(scope)?.Pack;

    /// <summary>"Pack 002-refund" for a pack scope; "Unscoped" for legacy items without one.</summary>
    public static string Label(string? scope) => PackOf(scope) is { } pack ? $"Pack {pack}" : "Unscoped";
}
