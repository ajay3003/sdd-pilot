using BirkNext.Dependencies;

namespace BirkNext.Web.Services;

/// <summary>The workflow shown prominently. Not exclusive in the data model: selections made in one workflow (e.g. deployed evidence) stay
/// available to another (the health review).</summary>
public enum DependencyReviewMode { None, Source, Inventory, Sbom, Deployed }

public sealed record DependencyReviewModeCard(DependencyReviewMode Mode, string Title, string Summary, string Action, string BestFor)
{
    /// <summary>The review type this evidence produces ("Source &amp; policy review", "Dependency health review" …).</summary>
    public string Purpose { get; init; } = "";
    public IReadOnlyList<string> CanAssess { get; init; } = [];
    public IReadOnlyList<string> CannotAssess { get; init; } = [];
}

/// <summary>The compact Ready state: what will run, on what, and what is deliberately not part of this run.</summary>
public sealed record DependencyReviewReadySummary(string Title, IReadOnlyList<(string Label, string Value)> Facts, string WillAssess, string? NotInThisRun);

/// <summary>Readiness rows are grouped: what was chosen (inputs), what the run will check (analysis), what it will compare (comparison).</summary>
public enum ReadinessGroup { Inputs, Analysis, Comparison }

public sealed record ReadinessRow(string Key, string Name, string State, string Tone, string Detail = "")
{
    public ReadinessGroup Group { get; init; }
    /// <summary>What this check needs before it can run — a dependency, never a state.</summary>
    public string DependsOn { get; init; } = "";
}

public sealed record ReadinessView(string Headline, string Tone, IReadOnlyList<ReadinessRow> Rows);

/// <summary>The one next action for the selected review source. Choose* actions select an input; the others run an existing command.</summary>
public enum DependencyReviewNextAction { ChooseReviewSource, ChooseSourceSnapshot, OpenSourceAnalysis, ResolveSourceScope, ChooseInventory, UploadSbom, ReviewSbom, ChooseTarget, CaptureDeployed, RunSourceReview, RunHealthReview }

/// <summary>Readiness of the SELECTED review source: Ready when its required inputs exist; the next action names the exact blocker or command.</summary>
public sealed record DependencyReviewNextStep(bool Ready, DependencyReviewNextAction Action, string Label, string Reason, string Tone = "muted")
{
    public bool Runs => Action is DependencyReviewNextAction.RunSourceReview or DependencyReviewNextAction.RunHealthReview
        or DependencyReviewNextAction.ReviewSbom or DependencyReviewNextAction.CaptureDeployed;
    public string Status => Ready ? "Ready" : "Not ready";
}

/// <summary>What the page knows before a run — selections only, never a result.</summary>
public sealed record ReadinessInput
{
    public DependencyReviewMode Mode { get; init; }
    // ── Source snapshot mode (Source Analysis owns the snapshots; Dependency Review only selects them) ──
    /// <summary>Source Analysis is hidden by Feature Visibility: its snapshots are not offered here.</summary>
    public bool SourceAnalysisDisabled { get; init; }
    public bool SourceSnapshotsAvailable { get; init; }
    /// <summary>The chosen primary snapshot, e.g. "M2LB · c850a1b2…".</summary>
    public string? PrimarySource { get; init; }
    /// <summary>The primary snapshot's repository name for the compact ready summary ("M2LB.Common").</summary>
    public string? PrimaryRepository { get; init; }
    public bool PrimaryHasEvidence { get; init; }
    public string? PrimaryEvidenceNote { get; init; }
    public int RelatedIncluded { get; init; }
    public string? RelatedSummary { get; init; }
    /// <summary>Detected related sources with an analyzed snapshot that are not included (optional; never blocks).</summary>
    public int RelatedNotIncluded { get; init; }
    public string? ScopeProblem { get; init; }
    public bool SourceReady => PrimarySource is not null && PrimaryHasEvidence && ScopeProblem is null;
    public InventorySummary? Inventory { get; init; }
    public string? PendingSbom { get; init; }
    public bool SbomInvalid { get; init; }
    /// <summary>The chosen SBOM was validated and stored as an inventory (the inventory is then <see cref="Inventory"/> in SBOM mode).</summary>
    public bool SbomReviewed { get; init; }
    /// <summary>Supported SBOM candidates found in the checked source snapshots (by file name); null until the snapshots were checked.</summary>
    public int? SbomCandidates { get; init; }
    public string? SbomFormat { get; init; }
    public InventorySummary? PolicyInventory { get; init; }
    public InventorySummary? Deployed { get; init; }
    public string? DeploymentTarget { get; init; }
    /// <summary>A Target Environment URL (or the advanced manual URL) is available, so deployed evidence can be captured.</summary>
    public bool CanCaptureDeployed { get; init; }
}

/// <summary>
/// Pre-run readiness of the Dependency Review. Uses only pre-run states — Ready, Ready to test, Waiting for …, Not selected, Not assessed,
/// Not required, Unavailable, Available — never Pass, Fail or Issue detected, and never a result count: nothing has been executed yet.
/// </summary>
public static class DependencyReviewReadiness
{
    public static readonly IReadOnlyList<DependencyReviewModeCard> Modes =
    [
        new(DependencyReviewMode.Source, "Source snapshot", "Use source already analyzed by Source Analysis.", "Choose source snapshot", "repository/source review")
        {
            Purpose = "Source & policy review",
            CanAssess = ["declared dependencies", "PackageReference/project usage", "Renovate configuration/policy", "source dependency mapping", "cross-source version differences"],
            CannotAssess = ["registry freshness", "vulnerability/advisory state", "license registry metadata", "deployed inventory"],
        },
        new(DependencyReviewMode.Inventory, "Existing inventory", "Use a previously captured dependency inventory without uploading source.", "Choose inventory", "registry/security/license review")
        {
            Purpose = "Dependency health review",
            CanAssess = ["latest published stable version", "registry metadata", "deprecated/unlisted status", "security advisories", "license metadata"],
            CannotAssess = ["source usage mapping", "Renovate policy without a linked source review", "runtime loading", "deployment state without deployed evidence"],
        },
        new(DependencyReviewMode.Sbom, "Review SBOM", "Review CycloneDX, SPDX or packages.lock.json.", "Upload SBOM", "generated dependency manifests")
        {
            Purpose = "Manifest health review",
            CanAssess = ["dependencies in the supplied manifest", "registry/version health", "advisories", "licenses"],
            CannotAssess = ["source usage", "runtime loading", "deployment alignment", "source Renovate policy"],
        },
        new(DependencyReviewMode.Deployed, "Deployed evidence", "Compare a known dependency inventory with a selected Target Environment.", "Choose target", "inventory vs environment comparison")
        {
            Purpose = "Deployment comparison",
            CanAssess = ["expected inventory vs deployed evidence", "environment differences where proven"],
            CannotAssess = ["source policy review", "dependency-health evidence (unless also present)"],
        },
    ];

    /// <summary>The compact Ready state of the selected mode, or null when it is not ready.</summary>
    public static DependencyReviewReadySummary? ReadySummary(ReadinessInput input)
    {
        if (!NextStep(input).Ready) return null;
        return input.Mode switch
        {
            DependencyReviewMode.Source => new("Ready to run",
                [("Source scope", (input.PrimaryRepository ?? input.PrimarySource ?? "") + (input.RelatedIncluded > 0 ? $" + {input.RelatedIncluded} related source{(input.RelatedIncluded == 1 ? "" : "s")}" : ""))],
                "Declared dependencies, Renovate configuration and policy, dependency mapping, cross-source usage and version differences.",
                "Registry, advisory and license checks require inventory evidence and are not part of this source-only run."),
            DependencyReviewMode.Inventory when input.Inventory is { } inv => new("Ready to run dependency health review",
                [("Inventory", inv.Name), ("Dependencies", inv.Dependencies.ToString()), ("Freshness", inv.Freshness.ToString())],
                "Registry/version health, advisory evidence, license metadata and deprecation status.",
                "Source usage mapping and Renovate policy are not part of an inventory review unless a linked source review is chosen."),
            DependencyReviewMode.Sbom when input.Inventory is { } sbom => new("Ready to run SBOM dependency health review",
                [("Evidence", "SBOM"), ("Format", input.SbomFormat ?? "Validated document"), ("Dependencies", sbom.Dependencies.ToString()), ("Validation", "Passed"), ("Inventory", "Derived from SBOM")],
                "Registry/version health, advisory evidence, license metadata and deprecation status of the dependencies in the SBOM.",
                "Source usage, Renovate policy and deployed state are not proven by an SBOM."),
            DependencyReviewMode.Deployed when input.Inventory is { } known && input.Deployed is { } deployed => new("Ready to compare",
                [("Known inventory", known.Name), ("Deployed evidence", deployed.Name)],
                "Expected inventory vs deployed evidence, where the deployed evidence proves it.",
                "Versions are compared only where the deployed evidence states them; absence is never assumed."),
            _ => null,
        };
    }

    public const string ReadinessMeaning = "Readiness shows whether the inputs required for the selected review mode are available. It is not a dependency-health result.";

    public static string ModeName(DependencyReviewMode mode) => Modes.FirstOrDefault(m => m.Mode == mode)?.Title ?? "Not selected";

    /// <summary>Security-fix policy evidence as a status (not an input): a source review's Renovate snapshot, when there is one.</summary>
    public static (string State, string Tone, string Detail) PolicyStatus(InventorySummary? inventory, InventorySummary? policy) =>
        policy is not null ? ("Available", "info", $"Renovate policy of {policy.Name}.")
        : inventory?.SourceType == InventorySourceType.SourceReview ? ("Available", "info", "Renovate policy of the source review this inventory comes from.")
        : ("Not assessed", "muted", "No source/Renovate review selected.");

    public static ReadinessView Evaluate(ReadinessInput input)
    {
        static ReadinessRow In(ReadinessRow r) => r with { Group = ReadinessGroup.Inputs };
        static ReadinessRow Check(ReadinessRow r, string dependsOn) => r with { Group = ReadinessGroup.Analysis, DependsOn = dependsOn };

        string waiting = input.PendingSbom is not null ? input.SbomInvalid ? "Unavailable" : "Waiting for SBOM review" : "Waiting for inventory";
        var rows = new List<ReadinessRow>
        {
            In(new("source", "Dependency evidence", ModeName(input.Mode), input.Mode == DependencyReviewMode.None ? "muted" : "info")),
            In(input.PrimarySource is { } primary
                ? new("primary", "Primary source", input.PrimaryHasEvidence ? "Selected" : "Selected · no dependency evidence", input.PrimaryHasEvidence ? "info" : "muted", primary)
                : new("primary", "Primary source", input.Mode == DependencyReviewMode.Source ? "Not selected" : "Not required", "muted")),
            In(input.RelatedIncluded > 0
                ? new("related", "Related sources", $"{input.RelatedIncluded} included", "info", input.RelatedSummary ?? "")
                : new("related", "Related sources", input.Mode == DependencyReviewMode.Source ? input.RelatedNotIncluded > 0 ? "Available · not included" : "None included" : "Not required", "muted")),
            In(input.Inventory is { } inv
                ? new("inventory", "Inventory", "Ready", "info", $"{inv.Name} · {DependencyHealthLabels.Source(inv.SourceType)} · {inv.Dependencies} dependencies · {inv.Freshness}")
                : new("inventory", "Inventory", input.PendingSbom is not null ? waiting : "Not selected", "muted",
                    input.SbomInvalid ? "The SBOM is not valid; no inventory was created." : input.PendingSbom is not null ? $"{input.PendingSbom} becomes an inventory when reviewed." : "")),
            In(input.DeploymentTarget is { } chosenTarget ? new("target", "Target environment", "Selected", "info", chosenTarget)
                : input.Deployed is not null ? new("target", "Target environment", "Evidence captured", "info")
                : new("target", "Target environment", input.Mode == DependencyReviewMode.Deployed ? "Not selected" : "Not required", "muted")),
            Check(SourceCheck("declared", "Declared dependencies", input), "Source scope"),
            Check(SourceCheck("renovate", "Renovate policy", input), "Source scope"),
        };
        foreach (var (key, name) in new[] { ("registry", "Registry checks"), ("security", "Security advisories"), ("license", "License metadata") })
            rows.Add(Check(input.Inventory is not null ? new(key, name, "Ready", "info") : new(key, name, waiting, "muted"), "Inventory"));
        var (policyState, policyTone, policyDetail) = PolicyStatus(input.Inventory, input.PolicyInventory);
        // Without an inventory the dependency column already says what is missing; the row repeats nothing.
        rows.Add(Check(new("policy", "Renovate security-fix policy", input.Inventory is null ? "Not assessed" : policyState, input.Inventory is null ? "muted" : policyTone,
            input.Inventory is null ? "" : policyDetail), "Inventory + source review"));
        rows.Add((input.Deployed is { } deployed
            ? new ReadinessRow("deployment", "Deployment comparison", input.Inventory is null ? "Waiting for inventory" : "Ready", input.Inventory is null ? "muted" : "info", deployed.Name)
            : input.DeploymentTarget is { } target
                ? new ReadinessRow("deployment", "Deployment comparison", "Waiting for deployed evidence capture", "muted", target)
                : new ReadinessRow("deployment", "Deployment comparison", "Not assessed", "muted", "No deployed evidence selected."))
            with { Group = ReadinessGroup.Comparison, DependsOn = "Inventory + deployed evidence" });

        var (headline, tone) = input.Inventory is not null ? ("Ready for dependency health review", "info")
            : input.SourceReady ? ("Ready to analyze the source scope", "info")
            : input.SbomInvalid ? ("Not ready — the SBOM is not valid", "attention")
            : input.PendingSbom is not null ? ("Ready to review SBOM", "info")
            : ("Not ready — choose a review source", "muted");
        return new ReadinessView(headline, tone, rows);
    }

    /// <summary>
    /// The one next step for the selected review source, from the same selections as <see cref="Evaluate"/>: the first missing input, or the
    /// existing command that runs with what is chosen. Never a result and never a failure — a missing input is a next step.
    /// </summary>
    /// <summary>A source-scope check: ready to analyze once a primary snapshot with dependency evidence is chosen — never "done" by selection.</summary>
    private static ReadinessRow SourceCheck(string key, string name, ReadinessInput input) =>
        input.SourceReady ? new(key, name, "Ready to analyze", "info", input.RelatedIncluded > 0 ? $"Primary + {input.RelatedIncluded} related source(s), each analyzed separately." : "")
        : input.PrimarySource is not null && !input.PrimaryHasEvidence ? new(key, name, "Not available", "muted", input.PrimaryEvidenceNote ?? "")
        : new(key, name, input.Mode == DependencyReviewMode.Source ? "Waiting for source snapshot" : "Not selected", "muted");

    public static DependencyReviewNextStep NextStep(ReadinessInput input) => input.Mode switch
    {
        DependencyReviewMode.Source => input.SourceAnalysisDisabled
                ? new(false, DependencyReviewNextAction.ChooseReviewSource, "Choose other dependency evidence", "Source Analysis is disabled in Feature Visibility, so its snapshots are not offered. Existing inventory, SBOM and deployed evidence remain available.")
            : !input.SourceSnapshotsAvailable
                ? new(false, DependencyReviewNextAction.OpenSourceAnalysis, "Open Source Analysis", "No source snapshot available. Dependency Review uses source snapshots managed by Source Analysis.")
            : input.PrimarySource is null
                ? new(false, DependencyReviewNextAction.ChooseSourceSnapshot, "Choose source snapshot", "Source snapshot mode reviews source already analyzed by Source Analysis.")
            : !input.PrimaryHasEvidence
                ? new(false, DependencyReviewNextAction.ChooseSourceSnapshot, "Choose a snapshot with dependency evidence", input.PrimaryEvidenceNote ?? "This snapshot has no dependency evidence.")
            : input.ScopeProblem is { } problem
                ? new(false, DependencyReviewNextAction.ResolveSourceScope, "Resolve source scope", problem, "attention")
            : new(true, DependencyReviewNextAction.RunSourceReview, "Run source dependency analysis",
                $"Declared dependencies and Renovate policy of {input.PrimarySource}{(input.RelatedIncluded > 0 ? $" and {input.RelatedIncluded} related source(s)" : "")}, each with its own provenance. Registry, advisory and license checks need the resulting inventory."
                + (input.RelatedNotIncluded > 0 ? " A detected related source is not included; the review can still run." : ""), "info"),
        DependencyReviewMode.Inventory => input.Inventory is { } inventory
            ? new(true, DependencyReviewNextAction.RunHealthReview, "Run dependency health review", $"Registry, security advisory and license checks run on {inventory.Name}.", "info")
            : new(false, DependencyReviewNextAction.ChooseInventory, "Choose inventory", "Registry, security advisory and license checks need a stored inventory."),
        // SBOM lifecycle: no SBOM → uploaded (validate) → invalid (no inventory) | valid (inventory derived) → health review.
        DependencyReviewMode.Sbom => input.PendingSbom is null
            ? new(false, DependencyReviewNextAction.UploadSbom, "Upload SBOM", (input.SbomCandidates == 0 ? "No supported SBOM was detected in the checked source snapshots. " : "")
                + "Review SBOM needs a CycloneDX, SPDX or packages.lock.json document.")
            : input.SbomInvalid
                ? new(false, DependencyReviewNextAction.UploadSbom, "Upload a valid SBOM", "The SBOM is not valid; no inventory was created.", "attention")
                : input.SbomReviewed && input.Inventory is not null
                    ? new(true, DependencyReviewNextAction.RunHealthReview, "Run SBOM dependency health review", $"{input.PendingSbom} was validated and an inventory was created from it.", "info")
                    : new(false, DependencyReviewNextAction.ReviewSbom, "Validate SBOM", $"{input.PendingSbom} is validated first; only a valid document becomes an inventory."),
        DependencyReviewMode.Deployed => input.Deployed is null
            ? input.CanCaptureDeployed
                ? new(false, DependencyReviewNextAction.CaptureDeployed, "Capture deployed evidence", $"Reads the deployed metadata of {input.DeploymentTarget ?? "the target"} with a read-only GET.")
                : new(false, DependencyReviewNextAction.ChooseTarget, "Choose Target Environment", "Deployment comparison needs deployed evidence from a Target Environment.")
            : input.Inventory is { } known
                ? new(true, DependencyReviewNextAction.RunHealthReview, "Run dependency health review", $"Compares {known.Name} with {input.Deployed.Name}.", "info")
                : new(false, DependencyReviewNextAction.ChooseInventory, "Choose the inventory to compare", "Deployed evidence is captured; the comparison also needs a known inventory."),
        _ => new(false, DependencyReviewNextAction.ChooseReviewSource, "Choose a review source", "Choose a review source above. Each source checks different things; nothing has been reviewed yet."),
    };
}
