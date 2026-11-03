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
    public const string TopologyHref = "/integration-topology";

    private static readonly IntegrationReviewDomain[] RuntimeDomains =
        [IntegrationReviewDomain.Connectivity, IntegrationReviewDomain.MessageFlow, IntegrationReviewDomain.Reliability, IntegrationReviewDomain.ErrorHandling, IntegrationReviewDomain.Performance];

    /// <summary>Runtime verification exists only when a runtime evidence adapter actually delivered — configuration is never runtime evidence.</summary>
    public static bool RuntimeVerified(IntegrationReviewReadiness r) => r.EvidenceAdapters.Any(a => a.State == IntegrationEvidenceState.Available);

    public static string FamilyLabel(IntegrationEvidenceFamily family) => family switch
    {
        IntegrationEvidenceFamily.ProjectEvidence => "Project evidence",
        IntegrationEvidenceFamily.IntegrationTopology => "Integration Topology",
        IntegrationEvidenceFamily.ConnectedSystems => "Connected Systems",
        IntegrationEvidenceFamily.IntegrationCatalog => "Integration Catalog",
        IntegrationEvidenceFamily.Contracts => "Contracts",
        _ => "Runtime evidence",
    };

    public static string FamilyStateLabel(IntegrationEvidenceFamilyState state) => state switch
    {
        IntegrationEvidenceFamilyState.Available => "Available",
        IntegrationEvidenceFamilyState.None => "None",
        IntegrationEvidenceFamilyState.NotConfigured => "Not configured",
        _ => "Could not be read",
    };

    public static string FamilyTone(IntegrationEvidenceFamilyState state) => state switch
    {
        IntegrationEvidenceFamilyState.Available => "ready",
        IntegrationEvidenceFamilyState.Failed => "attention",
        _ => "muted",
    };

    public static string CandidateStateLabel(IntegrationCandidateState state) => state switch
    {
        IntegrationCandidateState.Reviewed => "Reviewed",
        IntegrationCandidateState.NeedsConfirmation => "Needs confirmation · not reviewed",
        _ => "Excluded · disabled in catalog",
    };

    /// <summary>"Integration Catalog", "Discovered · Integration Topology + Connected Systems": where a review subject comes from.</summary>
    public static string OriginLabel(IntegrationRecordOrigin origin, IReadOnlyList<IntegrationEvidenceFamily> sources) =>
        origin == IntegrationRecordOrigin.Discovered
            ? $"Discovered · {string.Join(" + ", sources.Where(s => s != IntegrationEvidenceFamily.IntegrationCatalog).Select(FamilyLabel).DefaultIfEmpty("evidence"))}"
            : sources.Count > 1 ? $"Integration Catalog · also in {string.Join(" + ", sources.Where(s => s != IntegrationEvidenceFamily.IntegrationCatalog).Select(FamilyLabel))}" : "Integration Catalog";

    /// <param name="journeys">Active-verification journeys (total, executable) — never part of the review decision.</param>
    public static IqrRunDecision Of(IntegrationReviewReadiness? r, bool loading, string? error, (int Total, int Ready)? journeys, string? profileId)
    {
        var active = journeys is not { } j ? "Not known" : j.Total == 0 ? "No journeys" : j.Ready > 0 ? $"{j.Ready} / {j.Total} journeys executable" : $"Unavailable — 0 / {j.Total} journeys executable";
        if (r is null)
            return loading || error is null
                ? new(ReviewDecision.Assessing, AssessingTitle, "Reading project evidence, Integration Topology, Connected Systems, the Integration Catalog, contracts and runtime evidence.", 0, "—", "—", active, [], [])
                : new(ReviewDecision.FailedToAssess, ReviewDecisions.FailedTitle, error, 0, "—", "—", active, [], []);

        var catalogHref = IntegrationReviewPresentation.IntegrationsHref(profileId);
        var failed = r.Evidence.Where(f => f.State == IntegrationEvidenceFamilyState.Failed).ToList();
        var unconfirmed = r.Candidates.Where(c => c.State == IntegrationCandidateState.NeedsConfirmation).ToList();
        if (!r.CanRun)
        {
            if (unconfirmed.Count > 0)
                return new(ReviewDecision.NeedsReview, "Needs review · integrations discovered, but not established yet",
                    $"{string.Join(", ", unconfirmed.Select(c => c.DisplayName))}: the evidence states one side or a weak relationship only. Import or bind the missing source, or define the integration in the Integration Catalog.",
                    0, "—", "—", active, [], [], "Open Integration Topology", TopologyHref);
            // An evidence family that could not be read is "could not be assessed" — never a verdict that nothing exists.
            if (failed.Count > 0)
                return new(ReviewDecision.FailedToAssess, ReviewDecisions.FailedTitle, string.Join(" ", failed.Select(f => f.Detail)), 0, "—", "—", active, [], []);
            if (r.ConfiguredIntegrations > 0)
                return new(ReviewDecision.NeedsReview, "Needs review · integrations configured but none enabled", $"{r.ConfiguredIntegrations} integration(s) are configured, none enabled, and no other integration is found in the evidence.",
                    0, "—", "—", active, [], [], "Open Integration Catalog", catalogHref);
            return new(ReviewDecision.Blocked, "No integration found", "No integration is established by Integration Topology, registered under Connected Systems or defined in the Integration Catalog.", 0, "—", "—", active, [],
                ["No integration in any evidence family"], "Open Integration Topology", TopologyHref);
        }

        var runtime = RuntimeVerified(r);
        var limitations = r.Domains.Where(d => d.Readiness != IntegrationDomainReadiness.Ready).Select(d => $"{IntegrationReviewLabels.Domain(d.Domain)}: {DomainLabel(d, runtime)}").ToList();
        if (!runtime) limitations.Insert(0, "Runtime transport evidence unavailable");
        limitations.AddRange(failed.Select(f => $"{FamilyLabel(f.Family)}: could not be read — integrations it would add are not in this review"));
        if (unconfirmed.Count > 0) limitations.Add($"{unconfirmed.Count} discovered integration{(unconfirmed.Count == 1 ? " needs" : "s need")} confirmation and {(unconfirmed.Count == 1 ? "is" : "are")} not reviewed");
        if (journeys is { Ready: 0, Total: > 0 }) limitations.Add("Active execution unavailable — active testing is separate and does not limit the read-only review");
        var contract = r.Domains.FirstOrDefault(d => d.Domain == IntegrationReviewDomain.Contract) is { } c ? DomainLabel(c, runtime) : "Not applicable";
        var decision = limitations.Count == 0 ? ReviewDecision.Ready : ReviewDecision.ReadyWithLimitations;
        var title = decision == ReviewDecision.Ready ? "Ready" : !runtime ? StaticTitle : "Ready with limitations";
        var discovered = r.Subjects.Count(s => s.Origin == IntegrationRecordOrigin.Discovered);
        var detail = discovered == 0 ? r.Headline : $"{r.Headline.TrimEnd('.')}. {discovered} of {r.Subjects.Count} integration{(r.Subjects.Count == 1 ? " is" : "s are")} discovered from evidence — no catalog entry needed.";
        return new(decision, title, detail, r.Subjects.Count, contract, runtime ? "Verified (runtime evidence available)" : "Not verified", active, limitations, []);
    }

    /// <summary>
    /// The one evaluated readiness result the page renders: decision, limitations and blockers from <see cref="Of"/>, freshness from the newest
    /// evidence family capture, and the stages that produced it. A newer generation replaces it entirely.
    /// </summary>
    public static ReviewReadinessSnapshot<IntegrationReviewReadiness> Snapshot(IqrRunDecision d, IntegrationReviewReadiness? r, int generation, IReadOnlyList<ReviewStage> stages,
        DateTimeOffset now, string? error = null)
    {
        var newest = r?.Evidence.Select(f => f.CapturedAt).OfType<DateTimeOffset>().DefaultIfEmpty().Max();
        return new(d.Decision, d.Title, r, d.Limitations, d.Blockers, ReviewDecisions.FreshnessOf(newest == default(DateTimeOffset) ? null : newest, now), stages, r?.EvaluatedAt, generation, error);
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
