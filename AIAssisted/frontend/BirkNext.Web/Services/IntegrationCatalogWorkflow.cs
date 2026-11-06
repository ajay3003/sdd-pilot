namespace BirkNext.Web.Services;

/// <summary>
/// Target Environment → Integration Catalog as guided steps: discover → review proposals → resolve requirements → save configuration → validate.
/// Derived from the pane's own authoritative inputs (catalog completeness, Test Pack proposals backed by recognized source evidence, Source Analysis
/// technical relationships). Discovered ≠ accepted ≠ configured ≠ validated ≠ runtime verified; nothing here confirms a proposal or an identity.
/// Accepting a proposal does not need a registered Connected System (integrations name their systems; they are not foreign keys).
/// </summary>
public static class IntegrationCatalogWorkflow
{
    public const string ReviewProposals = "review-proposals", ShowConfigured = "show-configured";

    public static IReadOnlyList<GuidedStep> Derive(IntegrationsSummary catalog, IReadOnlyList<IntegrationPackProposal> proposals, SuggestionSummary sources, string runtimeLabel)
    {
        var ready = proposals.Count(p => p.Ready);
        var decisions = proposals.Count - ready;
        var open = catalog.NeedsConfirmation + catalog.NeedsConfiguration;
        // Proposals rest on Test Pack source recognition; technical relationships on a Source Analysis report. Either is evidence that was read.
        var evidence = sources.Available || proposals.Count > 0;
        var steps = new List<GuidedStep>
        {
            evidence
                ? new("discover", "Discover integrations", GuidedStepState.Done, "Evidence read",
                    $"{proposals.Count} relevant proposal{(proposals.Count == 1 ? "" : "s")} (integrations an applicable Test Pack requires, backed by recognized source evidence).",
                    Evidence: sources.Available ? $"{sources.Total} technical relationship{(sources.Total == 1 ? "" : "s")} from Source Analysis — evidence, not proposals."
                        : "Source Analysis technical relationships are not available for this project.")
                : new("discover", "Discover integrations", GuidedStepState.Current, "No source evidence",
                    "Proposals and technical relationships come from the project's Source Analysis snapshot. None is available for this project yet.",
                    ActionLabel: "Open Source Analysis", Href: "/source-analysis"),
        };

        steps.Add(proposals.Count > 0
            ? new("review", "Review proposals", GuidedStepState.Current, $"{proposals.Count} open",
                decisions > 0 ? $"{ready} ready to review · {decisions} need a decision first (which evidence source, or missing environment values). Nothing is added for you."
                    : $"{ready} ready to review. Review & add opens the normal form prefilled; nothing is saved until you save it.",
                ActionLabel: "Review proposals", ActionId: ReviewProposals)
            : new("review", "Review proposals", evidence ? GuidedStepState.Done : GuidedStepState.Waiting,
                evidence ? "No open proposals" : "Waiting for evidence",
                evidence ? "Every relevant proposal is configured, or no applicable Test Pack requires one." : "Proposals appear once source evidence is available."));

        steps.Add(catalog.Configured == 0
            ? new("resolve", "Resolve requirements", GuidedStepState.Waiting, "Nothing configured yet",
                "Consumer mappings, endpoints and contracts are resolved per configured integration.",
                Evidence: "Identity conflicts between systems are decided in Relationship Review; the catalog never confirms or rejects them.",
                ActionLabel: "Open Relationship Review", Href: "/correlation-review")
            : open > 0
                ? new("resolve", "Resolve requirements", GuidedStepState.Current, $"{open} of {catalog.Configured} need attention",
                    $"{catalog.NeedsConfirmation} need a confirmation (e.g. a suggested consumer mapping) · {catalog.NeedsConfiguration} need configuration. Suggested mappings stay unconfirmed until a person confirms them.",
                    Evidence: "Identity conflicts between systems are decided in Relationship Review; the catalog never confirms or rejects them.",
                    ActionLabel: "Show configured integrations", ActionId: ShowConfigured)
                : new("resolve", "Resolve requirements", GuidedStepState.Done, "No open requirement",
                    "Every configured integration has its mapping and configuration complete (completeness only — not runtime health).",
                    Evidence: "Identity conflicts between systems are decided in Relationship Review; the catalog never confirms or rejects them.",
                    ActionLabel: "Open Relationship Review", Href: "/correlation-review"));

        steps.Add(catalog.Configured == 0
            ? new("save", "Save configuration", proposals.Count > 0 ? GuidedStepState.Waiting : GuidedStepState.Current, "Not configured",
                "Only integrations you save are configured; a proposal stays a proposal. Changes are saved immediately and duplicates of an existing integration are refused.")
            : new("save", "Save configuration", GuidedStepState.Done, $"{catalog.Configured} configured",
                $"{catalog.Enabled} of {catalog.Configured} enabled. Saved immediately; the Target Environment Save/Cancel does not change them."));

        steps.Add(catalog.Configured == 0
            ? new("validate", "Validate", GuidedStepState.Waiting, "Not assessed", "Validation applies to configured integrations.")
            : new("validate", "Validate", GuidedStepState.Current, "Not validated here",
                "Configured is not validated: Integration Quality Review reviews each integration's evidence, and runtime is verified only from runtime evidence.",
                Evidence: $"Runtime evidence: {runtimeLabel}.", ActionLabel: "Open Integration Quality Review", Href: "/integration-quality-review"));

        var first = steps.FindIndex(s => s.State == GuidedStepState.Current);
        return [.. steps.Select((s, i) => i > first && first >= 0 && s.State == GuidedStepState.Current ? s with { State = GuidedStepState.Waiting } : s)];
    }
}
