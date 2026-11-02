using BirkNext.Integrations;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Integration Quality Review's run decision on the shared readiness model: review readiness, runtime verification and active
/// execution are separate answers; only the first decides whether the read-only review can run.</summary>
public sealed record IqrRunDecision(
    ReviewDecision Decision, string Title, string Detail, int Integrations, string Contracts, string RuntimeEvidence, string ActiveExecution,
    IReadOnlyList<string> Limitations, IReadOnlyList<string> Blockers, string? ActionLabel = null, string? ActionHref = null);

public static class IntegrationReviewDecisions
{
    public const string AssessingTitle = "Assessing integration review readiness…";
    public const string StaticTitle = "Ready for static integration review · runtime verification unavailable";

    private static readonly IntegrationReviewDomain[] RuntimeDomains =
        [IntegrationReviewDomain.Connectivity, IntegrationReviewDomain.MessageFlow, IntegrationReviewDomain.Reliability, IntegrationReviewDomain.ErrorHandling, IntegrationReviewDomain.Performance];

    /// <summary>Runtime verification exists only when a runtime evidence adapter actually delivered — configuration is never runtime evidence.</summary>
    public static bool RuntimeVerified(IntegrationReviewReadiness r) => r.EvidenceAdapters.Any(a => a.State == IntegrationEvidenceState.Available);

    /// <param name="connectedSystems">Connected Systems registered for the environment (names) — a separate model from the Integration Catalog.</param>
    /// <param name="journeys">Active-verification journeys (total, executable) — never part of the review decision.</param>
    public static IqrRunDecision Of(IntegrationReviewReadiness? r, bool loading, string? error, IReadOnlyList<string> connectedSystems, (int Total, int Ready)? journeys, string? profileId)
    {
        var active = journeys is not { } j ? "Not known" : j.Total == 0 ? "No journeys" : j.Ready > 0 ? $"{j.Ready} / {j.Total} journeys executable" : $"Unavailable — 0 / {j.Total} journeys executable";
        if (r is null)
            return loading || error is null
                ? new(ReviewDecision.Assessing, AssessingTitle, "Loading configured integrations, source evidence, contracts, runtime evidence and authentication.", 0, "—", "—", active, [], [])
                : new(ReviewDecision.FailedToAssess, ReviewDecisions.FailedTitle, error, 0, "—", "—", active, [], []);

        var catalogHref = IntegrationReviewPresentation.IntegrationsHref(profileId);
        if (!r.CanRun)
        {
            if (r.ConfiguredIntegrations > 0)
                return new(ReviewDecision.NeedsReview, "Needs review · integrations configured but none enabled", $"{r.ConfiguredIntegrations} integration(s) are configured; enable one to review it.",
                    0, "—", "—", active, [], [], "Open Integration Catalog", catalogHref);
            if (connectedSystems.Count > 0)
                return new(ReviewDecision.NeedsReview, "Connected System available, but no Integration Catalog binding exists",
                    $"{string.Join(", ", connectedSystems)} {(connectedSystems.Count == 1 ? "is" : "are")} registered under Connected Systems. Integration Quality Review reviews Integration Catalog integrations (producer → consumer); add one for this system.",
                    0, "—", "—", active, [], [], "Open Integration Catalog", catalogHref);
            return new(ReviewDecision.Blocked, "No integration to review", "No integration is configured for this environment.", 0, "—", "—", active, [],
                ["No enabled integration in the Integration Catalog"], "Open Integration Catalog", catalogHref);
        }

        var runtime = RuntimeVerified(r);
        var limitations = r.Domains.Where(d => d.Readiness != IntegrationDomainReadiness.Ready).Select(d => $"{IntegrationReviewLabels.Domain(d.Domain)}: {DomainLabel(d, runtime)}").ToList();
        if (!runtime) limitations.Insert(0, "Runtime transport evidence unavailable");
        if (journeys is { Ready: 0, Total: > 0 }) limitations.Add("Active execution unavailable — active testing is separate and does not limit the read-only review");
        var contract = r.Domains.FirstOrDefault(d => d.Domain == IntegrationReviewDomain.Contract) is { } c ? DomainLabel(c, runtime) : "Not applicable";
        var decision = limitations.Count == 0 ? ReviewDecision.Ready : ReviewDecision.ReadyWithLimitations;
        var title = decision == ReviewDecision.Ready ? "Ready" : !runtime ? StaticTitle : "Ready with limitations";
        return new(decision, title, r.Headline, r.EnabledIntegrations, contract, runtime ? "Verified (runtime evidence available)" : "Not verified", active, limitations, []);
    }

    /// <summary>
    /// A precise, capability-aware state per review area instead of a blanket "Not assessable": what static (source/configuration/contract)
    /// evidence allows, and whether runtime verification exists.
    /// </summary>
    public static string DomainLabel(IntegrationDomainReadinessRow row, bool runtimeVerified)
    {
        var hasStatic = row.Available is { Count: > 0 };
        var runtimeArea = RuntimeDomains.Contains(row.Domain);
        return row.Readiness switch
        {
            IntegrationDomainReadiness.Ready => runtimeArea && !runtimeVerified ? "Ready from source" : "Ready",
            IntegrationDomainReadiness.Available => "Available",
            IntegrationDomainReadiness.Partial => "Reviewable with limitations",
            IntegrationDomainReadiness.Limited when row.Domain == IntegrationReviewDomain.Security => "Needs review · authorization not proven",
            IntegrationDomainReadiness.Limited when runtimeArea && !runtimeVerified => "Limited · static evidence only",
            IntegrationDomainReadiness.Limited => "Limited",
            _ => row.Domain switch
            {
                IntegrationReviewDomain.Configuration => "Not configured",
                IntegrationReviewDomain.Contract => "No contract evidence",
                IntegrationReviewDomain.DataQuality => hasStatic ? "Limited · declared structure only" : "No declared structure",
                IntegrationReviewDomain.Observability => "No monitoring evidence",
                _ when runtimeArea && hasStatic => "Static only · not verified at runtime",
                _ when runtimeArea => "Not verified at runtime",
                _ => "Not assessable",
            },
        };
    }

    /// <summary>"Static: Available · Runtime: Not verified" — the provenance of an area's assessment.</summary>
    public static string Provenance(IntegrationDomainReadinessRow row, bool runtimeVerified) =>
        $"Static evidence: {(row.Available is { Count: > 0 } ? "Available" : "None")} · Runtime evidence: {(RuntimeDomains.Contains(row.Domain) ? runtimeVerified && row.Readiness is IntegrationDomainReadiness.Ready or IntegrationDomainReadiness.Partial ? "Available" : "Not verified" : "Not applicable")}";
}
