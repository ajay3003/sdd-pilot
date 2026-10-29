using BirkNext.Integrations;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Connected Systems → Altinn → Access &amp; authentication: the three hops (reporter → app, app → integration consumer, BirkNext execution) as
/// testers read them, with the reporter's browser-login runtime taken from Browser Discovery evidence of THIS app only. Browser evidence
/// never changes the other two hops.
/// </summary>
public static class AltinnAccessPresentation
{
    /// <summary>ID-porten hosts (the TEST family first, so a TT02 login reads "ID-porten TEST"). TestID is ID-porten TEST's test-identity option.</summary>
    public static readonly IReadOnlyList<BrowserIdentityProvider> Providers =
    [
        new("test.idporten.no", "ID-porten TEST"),
        new("idporten.no", "ID-porten"),
    ];

    /// <summary>Altinn app requests that only an authenticated session answers successfully (the app's own profile, party and instance APIs).</summary>
    public static readonly IReadOnlyList<string> SessionPaths = ["/api/v1/profile/user", "/api/authorization/parties/current", "/api/v1/parties/current", "/instances"];

    /// <summary>The exact app: its base URL's origin plus "/{org}/{app}" (or the base URL's own path). Null without an app and a base URL.</summary>
    public static BrowserAuthAppScope? Scope(AltinnEnvironmentView view)
    {
        var baseUrl = view.Fields.FirstOrDefault(f => f.Key == "app-base-url")?.Value;
        var app = view.Fields.FirstOrDefault(f => f.Key == "app")?.Value;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return null;
        var origin = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}";
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0 && !string.IsNullOrWhiteSpace(app)) path = "/" + app.Trim('/');
        return path.Length == 0 ? null : new BrowserAuthAppScope(origin, path);
    }

    public static BrowserAuthEvidenceResult Evidence(AltinnEnvironmentView view, EndpointDiscoverySnapshot? snapshot, DateTimeOffset now) =>
        Scope(view) is { } scope ? BrowserAuthEvidence.Classify(snapshot, scope, Providers, SessionPaths, now) : BrowserAuthEvidenceResult.None;

    public static InteractionAuthentication? Hop(AltinnEnvironmentView view, string key) => view.Interactions.FirstOrDefault(i => i.Key == key);

    /// <summary>The reporter hop's runtime: Browser Discovery evidence of this exact app first, else the manual record, else not observed — and
    /// when an identity provider was seen only for another application, that is said (it never counts).</summary>
    public static string ReporterRuntime(InteractionAuthentication hop, BrowserAuthEvidenceResult evidence) =>
        evidence.ObservedLevel != BrowserAuthEvidenceLevel.Unknown
            ? $"{BrowserAuthEvidence.Label(evidence)} · {BrowserAuthEvidenceResult.Source}{(evidence.LastObservedAt is { } at ? $" · last observed {at:yyyy-MM-dd HH:mm} UTC" : "")}"
            : hop.Runtime + (evidence.ProviderObservedElsewhere ? " · ID-porten was seen in browser evidence for another application; it does not count for this app" : "");

    /// <summary>The reporter-access summary card with Browser Discovery evidence applied: Ready once this app's login was observed to complete.</summary>
    public static AltinnReadinessItem ReporterCard(AltinnReadinessItem item, AltinnEnvironmentView view, BrowserAuthEvidenceResult evidence)
    {
        if (item.Key != "reporter-access" || Hop(view, AltinnHops.ReporterApp) is not { } hop) return item;
        var current = evidence.Usable && evidence.Level != BrowserAuthEvidenceLevel.Historical;
        return item with
        {
            State = current ? AltinnReadinessState.Ready : item.State,
            Reason = $"{InteractionAuthRules.Label(hop.Requirement.Requirement)} · {hop.Provider} · Runtime: {ReporterRuntime(hop, evidence)}",
        };
    }

    /// <summary>One line per hop for the section summary.</summary>
    public static string Line(AltinnEnvironmentView view, BrowserAuthEvidenceResult evidence) => string.Join(" · ", view.Interactions.Select(hop =>
        hop.Key == AltinnHops.ReporterApp ? $"{hop.From} → {hop.To}: {InteractionAuthRules.Label(hop.Requirement.Requirement)}, {BrowserAuthEvidence.Label(evidence)}"
        : hop.Key == AltinnHops.AppIntegration ? $"{hop.From} → {hop.To}: {(hop.Requirement.Conflict ? "Conflict" : InteractionAuthRules.Label(hop.Requirement.Requirement))}"
        : $"{hop.Title}: {hop.Configuration}"));

    public static string RequirementText(InteractionAuthentication hop) =>
        hop.Requirement.Conflict ? $"Conflict — {string.Join(" vs ", hop.Requirement.Candidates.Select(c => $"{InteractionAuthRules.Label(c.Requirement)} ({c.SourceLabel})"))}. Review authentication."
        : hop.Requirement.Basis is not { } basis ? "Unknown — not configured or reviewed"
        : $"{InteractionAuthRules.Label(hop.Requirement.Requirement)} · {(hop.Requirement.IsSuggestion ? "suggested by " : "")}{basis.SourceLabel}";

    public static string BrowserDiscoveryHref(string environmentId) =>
        $"/admin/system-settings?section=target-environments&tab=browser&profile={Uri.EscapeDataString(environmentId)}";

    public static string IntegrationCatalogHref(string environmentId) =>
        $"/admin/system-settings?section=target-environments&tab=integrations&profile={Uri.EscapeDataString(environmentId)}";
}

/// <summary>Stable hop keys (mirrors the backend's AltinnInteractionAuthentication).</summary>
public static class AltinnHops
{
    public const string ReporterApp = "reporter-app", AppIntegration = "app-integration", Execution = "execution";
}
