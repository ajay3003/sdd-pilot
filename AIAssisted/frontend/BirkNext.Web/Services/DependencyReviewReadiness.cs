using BirkNext.Dependencies;

namespace BirkNext.Web.Services;

/// <summary>The workflow shown prominently. Not exclusive in the data model: selections made in one workflow (e.g. deployed evidence) stay
/// available to another (the health review).</summary>
public enum DependencyReviewMode { None, Source, Inventory, Sbom, Deployed }

public sealed record DependencyReviewModeCard(DependencyReviewMode Mode, string Title, string Summary, string Action);

public sealed record ReadinessRow(string Key, string Name, string State, string Tone, string Detail = "");

public sealed record ReadinessView(string Headline, string Tone, IReadOnlyList<ReadinessRow> Rows);

/// <summary>What the page knows before a run — selections only, never a result.</summary>
public sealed record ReadinessInput
{
    public DependencyReviewMode Mode { get; init; }
    public int Archives { get; init; }
    public InventorySummary? Inventory { get; init; }
    public string? PendingSbom { get; init; }
    public bool SbomInvalid { get; init; }
    public InventorySummary? PolicyInventory { get; init; }
    public InventorySummary? Deployed { get; init; }
    public string? DeploymentTarget { get; init; }
}

/// <summary>
/// Pre-run readiness of the Dependency Review. Uses only pre-run states — Ready, Ready to test, Waiting for …, Not selected, Not assessed,
/// Unavailable, Available — never Pass, Fail or Issue detected, and never a result count: nothing has been executed yet.
/// </summary>
public static class DependencyReviewReadiness
{
    public static readonly IReadOnlyList<DependencyReviewModeCard> Modes =
    [
        new(DependencyReviewMode.Source, "Review from source", "Renovate policy and declared dependencies", "Choose repository archives"),
        new(DependencyReviewMode.Inventory, "Existing inventory", "Registry, security and license checks without source", "Choose inventory"),
        new(DependencyReviewMode.Sbom, "Review SBOM", "CycloneDX / SPDX / packages.lock.json", "Upload SBOM"),
        new(DependencyReviewMode.Deployed, "Deployed evidence", "Compare known inventory with a target environment", "Choose target"),
    ];

    public static string ModeName(DependencyReviewMode mode) => Modes.FirstOrDefault(m => m.Mode == mode)?.Title ?? "Not selected";

    /// <summary>Security-fix policy evidence as a status (not an input): a source review's Renovate snapshot, when there is one.</summary>
    public static (string State, string Tone, string Detail) PolicyStatus(InventorySummary? inventory, InventorySummary? policy) =>
        policy is not null ? ("Available", "info", $"Renovate policy of {policy.Name}.")
        : inventory?.SourceType == InventorySourceType.SourceReview ? ("Available", "info", "Renovate policy of the source review this inventory comes from.")
        : ("Not assessed", "muted", "No source/Renovate review selected.");

    public static ReadinessView Evaluate(ReadinessInput input)
    {
        var rows = new List<ReadinessRow>
        {
            new("source", "Review source", ModeName(input.Mode), input.Mode == DependencyReviewMode.None ? "muted" : "info"),
            input.Archives > 0
                ? new("renovate", "Source review", "Ready to test", "info", $"{input.Archives} repository archive(s) chosen.")
                : new("renovate", "Source review", input.Mode == DependencyReviewMode.Source ? "Waiting for repository archives" : "Not selected", "muted"),
        };
        string waiting = input.PendingSbom is not null ? input.SbomInvalid ? "Unavailable" : "Waiting for SBOM review" : "Waiting for inventory";
        rows.Add(input.Inventory is { } inv
            ? new("inventory", "Inventory", "Ready", "info", $"{inv.Name} · {DependencyHealthLabels.Source(inv.SourceType)} · {inv.Dependencies} dependencies · {inv.Freshness}")
            : new("inventory", "Inventory", input.PendingSbom is not null ? waiting : "Not selected", "muted",
                input.SbomInvalid ? "The SBOM is not valid; no inventory was created." : input.PendingSbom is not null ? $"{input.PendingSbom} becomes an inventory when reviewed." : ""));
        foreach (var (key, name) in new[] { ("registry", "Registry checks"), ("security", "Security advisories"), ("license", "License metadata") })
            rows.Add(input.Inventory is not null ? new(key, name, "Ready", "info") : new(key, name, waiting, "muted"));
        var (policyState, policyTone, policyDetail) = PolicyStatus(input.Inventory, input.PolicyInventory);
        rows.Add(new("policy", "Renovate policy (security fixes)", input.Inventory is null ? "Not assessed" : policyState, input.Inventory is null ? "muted" : policyTone,
            input.Inventory is null ? "Needs an inventory and a source review." : policyDetail));
        rows.Add(input.Deployed is { } deployed
            ? new("deployment", "Deployment comparison", input.Inventory is null ? "Waiting for inventory" : "Ready", input.Inventory is null ? "muted" : "info", deployed.Name)
            : input.DeploymentTarget is { } target
                ? new("deployment", "Deployment comparison", "Waiting for deployed evidence capture", "muted", target)
                : new("deployment", "Deployment comparison", "Not assessed", "muted", "No deployed evidence selected."));

        var (headline, tone) = input.Inventory is not null ? ("Ready for dependency health review", "info")
            : input.Archives > 0 ? ("Ready to test Renovate policy", "info")
            : input.SbomInvalid ? ("Not ready — the SBOM is not valid", "attention")
            : input.PendingSbom is not null ? ("Ready to review SBOM", "info")
            : ("Not ready — choose a review source", "muted");
        return new ReadinessView(headline, tone, rows);
    }
}
