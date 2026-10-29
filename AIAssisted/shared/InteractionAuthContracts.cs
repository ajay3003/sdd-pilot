using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

/// <summary>What authentication one interaction (hop) requires. Each hop has its own: a browser login of an end user says nothing about how a
/// backend integration authenticates, and neither configures BirkNext's own execution credentials.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractionAuthRequirement { Unknown, NotRequired, BrowserOidc, BearerOAuthApi, Mtls, ApiKey, Basic, Multiple, Other }

/// <summary>Where a requirement candidate comes from, in precedence order (first wins). Runtime and Test Pack candidates only propose.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InteractionAuthSource { ExplicitConfiguration, ReviewedIntegration, SourceDeclaration, RuntimeSuggestion, TestPackProposal }

/// <summary>How far a browser login of the exact application was observed. Generic browser traffic (DOM, accessibility, performance, ordinary
/// navigation, static or API calls) never raises it above <see cref="Unknown"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserAuthEvidenceLevel { Unknown, LoginFlowObserved, AuthenticatedReturnObserved, AuthenticatedSessionObserved, LoggedOut, Historical }

/// <summary>One stated requirement for a hop, with its source and a label naming that source.</summary>
public sealed record InteractionAuthCandidate(InteractionAuthRequirement Requirement, InteractionAuthSource Source, string SourceLabel);

/// <summary>
/// A hop's resolved requirement: the highest-precedence candidate, whether it is only a suggestion (runtime or Test Pack), and a conflict when
/// two authoritative sources (explicit configuration, reviewed integration, source/contract declaration) state different requirements.
/// A conflict is shown for review; nothing is chosen silently.
/// </summary>
public sealed record InteractionAuthResolution(
    InteractionAuthRequirement Requirement,
    InteractionAuthCandidate? Basis,
    bool IsSuggestion,
    bool Conflict,
    IReadOnlyList<InteractionAuthCandidate> Candidates)
{
    public static readonly InteractionAuthResolution Unknown = new(InteractionAuthRequirement.Unknown, null, false, false, []);
}

/// <summary>
/// One interaction/hop and its authentication, kept in three separate facts: what is required, how it is configured, and what was observed
/// at runtime. <see cref="Key"/> is stable (e.g. "reporter-app", "app-integration", "execution").
/// </summary>
public sealed record InteractionAuthentication
{
    public string Key { get; init; } = "";
    /// <summary>"Reporter access", "Integration authentication", "BirkNext execution credentials".</summary>
    public string Title { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Purpose { get; init; } = "";
    public InteractionAuthResolution Requirement { get; init; } = InteractionAuthResolution.Unknown;
    /// <summary>The identity provider or mechanism detail ("ID-porten TEST"), when known.</summary>
    public string? Provider { get; init; }
    /// <summary>How the requirement is configured for this hop ("Not configured", "Configured: Bearer", "Configured in Integration Catalog").</summary>
    public string Configuration { get; init; } = "";
    /// <summary>What has been observed at runtime for this hop (never inherited from another hop).</summary>
    public string Runtime { get; init; } = "Not verified";
    /// <summary>Which work needs this hop: "Manual preparation", "End-to-end verification", "Automated execution".</summary>
    public string RequiredFor { get; init; } = "";
    /// <summary>Owner action ("Review integration authentication") and where it is done.</summary>
    public string? ActionLabel { get; init; }
    public string? ActionTarget { get; init; }
}

public static class InteractionAuthRules
{
    private static bool Authoritative(InteractionAuthSource source) =>
        source is InteractionAuthSource.ExplicitConfiguration or InteractionAuthSource.ReviewedIntegration or InteractionAuthSource.SourceDeclaration;

    /// <summary>
    /// Precedence: explicit configuration &gt; reviewed integration &gt; trusted source/contract declaration &gt; runtime-derived suggestion &gt;
    /// Test Pack proposal &gt; Unknown. Unknown candidates state nothing and are ignored. A runtime observation never overrides an authoritative
    /// requirement; two authoritative sources that disagree are a conflict.
    /// </summary>
    public static InteractionAuthResolution Resolve(IEnumerable<InteractionAuthCandidate?> candidates)
    {
        var known = candidates.OfType<InteractionAuthCandidate>().Where(c => c.Requirement != InteractionAuthRequirement.Unknown)
            .OrderBy(c => c.Source).ToList();
        if (known.Count == 0) return InteractionAuthResolution.Unknown;
        var basis = known[0];
        var conflict = known.Where(c => Authoritative(c.Source)).Select(c => c.Requirement).Distinct().Count() > 1;
        return new(basis.Requirement, basis, !Authoritative(basis.Source), conflict, known);
    }

    /// <summary>An integration's configured mechanism as a hop requirement; null when nothing is configured (never "not required").</summary>
    public static InteractionAuthRequirement? FromMechanism(IntegrationAuthMechanism mechanism) => mechanism switch
    {
        IntegrationAuthMechanism.NotConfigured => null,
        IntegrationAuthMechanism.None => InteractionAuthRequirement.NotRequired,
        IntegrationAuthMechanism.ApiKey => InteractionAuthRequirement.ApiKey,
        IntegrationAuthMechanism.EntraIdClientCredentials or IntegrationAuthMechanism.ManagedIdentity => InteractionAuthRequirement.BearerOAuthApi,
        _ => InteractionAuthRequirement.Other,
    };

    public static string Label(InteractionAuthRequirement requirement) => requirement switch
    {
        InteractionAuthRequirement.NotRequired => "Not required",
        InteractionAuthRequirement.BrowserOidc => "Browser/OIDC",
        InteractionAuthRequirement.BearerOAuthApi => "Bearer/OAuth/API",
        InteractionAuthRequirement.Mtls => "mTLS",
        InteractionAuthRequirement.ApiKey => "API key",
        InteractionAuthRequirement.Basic => "Basic",
        InteractionAuthRequirement.Multiple => "Multiple",
        InteractionAuthRequirement.Other => "Other",
        _ => "Unknown",
    };

    public static string SourceLabel(InteractionAuthSource source) => source switch
    {
        InteractionAuthSource.ExplicitConfiguration => "Configured",
        InteractionAuthSource.ReviewedIntegration => "Reviewed integration",
        InteractionAuthSource.SourceDeclaration => "Source/contract declaration",
        InteractionAuthSource.RuntimeSuggestion => "Suggested from runtime evidence",
        _ => "Test Pack proposal",
    };

    /// <summary>"Browser/OIDC · Test Pack proposal" / "Conflict — review authentication" / "Unknown".</summary>
    public static string Describe(InteractionAuthResolution resolution) =>
        resolution.Conflict ? "Conflict — review authentication"
        : resolution.Basis is null ? "Unknown"
        : $"{Label(resolution.Requirement)} · {(resolution.IsSuggestion ? "suggested" : SourceLabel(resolution.Basis.Source).ToLowerInvariant())}";

    public static string LevelLabel(BrowserAuthEvidenceLevel level) => level switch
    {
        BrowserAuthEvidenceLevel.LoginFlowObserved => "Login flow observed",
        BrowserAuthEvidenceLevel.AuthenticatedReturnObserved => "Authenticated return observed",
        BrowserAuthEvidenceLevel.AuthenticatedSessionObserved => "Authenticated session observed",
        BrowserAuthEvidenceLevel.LoggedOut => "Logged out / authentication lost",
        BrowserAuthEvidenceLevel.Historical => "Historical",
        _ => "Not observed",
    };
}
