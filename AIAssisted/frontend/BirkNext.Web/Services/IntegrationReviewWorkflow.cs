using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Integration Quality Review as guided steps: select integration → review evidence → resolve setup → configure contracts → run static review →
/// test &amp; verify. Every step reads values the page already computes (<see cref="IntegrationReviewReadiness"/>, the light classification, the
/// attention items, the journey summary) — no new readiness engine. Three answers stay separate: static review readiness (CanRun), configuration
/// completeness (decisions, contracts) and active verification readiness (journeys + environment execution permission). Static evidence is never
/// runtime verification, and a decision is never made for the user.
/// </summary>
public static class IntegrationReviewWorkflow
{
    public sealed record Inputs(
        IntegrationReviewReadiness Readiness,
        IReadOnlyList<IntegrationReviewLightPresentation.Item> Light,
        IReadOnlyList<IntegrationReviewPrerunPresentation.AttentionItem> Attention,
        int? JourneysTotal, int? JourneysReady, bool? ExecutionAllowed,
        Func<string, string> ViewHref, string CatalogHref);

    public static IReadOnlyList<GuidedStep> Derive(Inputs x)
    {
        var r = x.Readiness;
        var subjects = r.Subjects.Count;
        var open = x.Light.Where(i => !i.Resolved).ToList();
        var (runtimeAvailable, runtimeTotal) = IntegrationReviewPrerunPresentation.RuntimeAvailability(r);
        var contracts = x.Attention.FirstOrDefault(a => a.Key == "contracts");
        var mappings = x.Attention.FirstOrDefault(a => a.Key == "mappings");
        var steps = new List<GuidedStep>
        {
            subjects == 0
                ? new("select", "Select integration", GuidedStepState.Current, "No integration to review",
                    "Nothing is enabled or discovered for this environment yet. Configure an integration in the Integration Catalog (proposals there are not reviewed until configured).",
                    ActionLabel: "Open Integration Catalog", Href: x.CatalogHref)
                : new("select", "Select integration", GuidedStepState.Done,
                    r.ScopeIntegrationId is null ? $"All {subjects} in scope" : "One integration in scope",
                    r.ScopeIntegrationId is null ? $"The review covers {subjects} relevant integration{(subjects == 1 ? "" : "s")} of this environment; choose one above to narrow it."
                        : "The review is narrowed to the selected integration.",
                    Evidence: "Relevant = enabled catalog integrations plus integrations discovered from topology evidence and enabled Connected Systems. Integration Catalog proposals and Source Analysis technical relationships are counted separately and are not reviewed until configured."),
        };

        steps.Add(new("evidence", "Review evidence", subjects == 0 ? GuidedStepState.Waiting : GuidedStepState.Done,
            r.SourceSnapshots.Count == 0 ? "No source snapshot bound" : $"{r.SourceSnapshots.Count} source snapshot{(r.SourceSnapshots.Count == 1 ? "" : "s")} bound",
            "Source evidence is what the static review reads; runtime evidence is separate and never inferred from source.",
            Evidence: runtimeTotal == 0 ? "Runtime evidence: no source configured." : $"Runtime evidence: {runtimeAvailable} of {runtimeTotal} source{(runtimeTotal == 1 ? "" : "s")} available.",
            ActionLabel: "Open Setup & Evidence", Href: x.ViewHref(IntegrationReviewPrerunPresentation.SetupView)));

        steps.Add(subjects == 0
            ? new("resolve", "Resolve integration setup", GuidedStepState.Waiting, "Not assessed", "Decisions apply to integrations in scope.")
            : open.Count > 0
                ? new("resolve", "Resolve integration setup", GuidedStepState.Current, $"{open.Count} need your decision",
                    $"{subjects - open.Count} of {subjects} resolved from evidence. Open decisions: a consumer mapping to confirm, missing or ambiguous source evidence, an unbound source or an ambiguous contract. Nothing is decided for you.",
                    Evidence: mappings is null ? null : $"Consumer mappings: {mappings.Value}. Evidence checks never confirm a mapping.",
                    ActionLabel: mappings?.ActionLabel ?? "Open Setup & Evidence", Href: mappings?.ActionHref ?? x.ViewHref(IntegrationReviewPrerunPresentation.SetupView))
                : new("resolve", "Resolve integration setup", GuidedStepState.Done, "No open decision", $"All {subjects} integrations are resolved from evidence (resolved is not runtime verified)."));

        steps.Add(subjects == 0
            ? new("contracts", "Configure contracts", GuidedStepState.Waiting, "Not assessed", "Contracts apply to integrations in scope.")
            : contracts is not null
                ? new("contracts", "Configure contracts", GuidedStepState.Current, $"Contracts: {contracts.Value}",
                    "Without a contract the review cannot compare payloads with an agreed schema; it can still run with this limitation.",
                    ActionLabel: contracts.ActionLabel, Href: contracts.ActionHref)
                : new("contracts", "Configure contracts", GuidedStepState.Done, "Contract evidence available", "Every topic in scope has a configured contract or Test Pack contract evidence."));

        steps.Add(!r.CanRun
            ? new("static", "Run static review", subjects == 0 ? GuidedStepState.Waiting : GuidedStepState.Blocked, IntegrationReviewPrerunPresentation.ReadinessLabel(r),
                "The read-only review cannot assess these integrations yet.", DisabledReason: r.Reasons.FirstOrDefault())
            : new("static", "Run static review", GuidedStepState.Current, IntegrationReviewPrerunPresentation.ReadinessLabel(r),
                r.Headline == "Ready" ? "The read-only review can assess every area in scope. Run review below; it never sends a message."
                    : $"The read-only review can run now with {(r.Reasons.Count > 0 ? $"{r.Reasons.Count} limitation{(r.Reasons.Count == 1 ? "" : "s")}" : "limitations (see what needs attention below)")}; resolving the steps above improves coverage. Run review below; it never sends a message."));

        var total = x.JourneysTotal ?? 0;
        var ready = x.JourneysReady ?? 0;
        steps.Add(x.JourneysTotal is null
            ? new("verify", "Test & verify", GuidedStepState.Waiting, "Not known", "Active verification readiness could not be read.",
                ActionLabel: "Open Test & Verify", Href: x.ViewHref(IntegrationReviewPrerunPresentation.TestView))
            : x.ExecutionAllowed != true
                ? new("verify", "Test & verify", GuidedStepState.Blocked, $"{ready} of {total} journeys executable",
                    "Active verification is separate from the static review and is not enabled for this environment; reviews and evidence never enable it.",
                    ActionLabel: "Open Test & Verify", Href: x.ViewHref(IntegrationReviewPrerunPresentation.TestView))
                : new("verify", "Test & verify", ready > 0 ? GuidedStepState.Waiting : GuidedStepState.Blocked, $"{ready} of {total} journeys executable",
                    ready > 0 ? "Executable journeys still need their own prerequisites and an explicit confirmation; nothing runs from this page."
                        : "No journey has its execution prerequisites yet (test data, endpoints, authentication).",
                    ActionLabel: "Open Test & Verify", Href: x.ViewHref(IntegrationReviewPrerunPresentation.TestView)));

        // Configuration decisions come first; the static review can still run with limitations (its label says so) — one current step.
        var first = steps.FindIndex(s => s.State == GuidedStepState.Current);
        return [.. steps.Select((s, i) => i > first && first >= 0 && s.State == GuidedStepState.Current ? s with { State = GuidedStepState.Waiting } : s)];
    }
}
