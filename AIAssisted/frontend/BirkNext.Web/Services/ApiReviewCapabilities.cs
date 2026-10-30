using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using BirkNext.RuntimeAuthentication;

namespace BirkNext.Web.Services;

/// <summary>Readiness of one API Quality Review capability. Each is decided on its own: one capability that cannot run never blocks another.</summary>
public enum ApiReviewCapabilityState { Ready, Available, AuthenticationRequired, AuthenticationExpired, PermissionRequired, NotSelected, NotAvailable }

public sealed record ApiReviewCapability(string Key, string Label, ApiReviewCapabilityState State, string Detail, string? ActionText = null, string? ActionHref = null)
{
    /// <summary>Whether this capability executes (or reads evidence) in the next run.</summary>
    public bool Runs => State is ApiReviewCapabilityState.Ready or ApiReviewCapabilityState.Available;
}

/// <summary>
/// The four API Quality Review capabilities, kept independent: static contract review (anonymous public metadata), runtime evidence (Endpoint
/// Discovery observations already captured — read, never re-executed), active authenticated checks (the shared runtime authentication's
/// selected mode and the live attended context) and optional authorization checks (server-registered trusted targets only). An expired
/// session only marks the active checks; the read-only review still runs. Nothing here is a result.
/// </summary>
public static class ApiReviewCapabilities
{
    public const string Static = "static";
    public const string Public = "public";
    public const string Runtime = "runtime";
    public const string Active = "active";
    public const string Authorization = "authorization";

    /// <summary>Observations older than this are shown as stale: evidence of then, not of current access.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    public static IReadOnlyList<ApiReviewCapability> Evaluate(IReadOnlyList<ApiReviewTarget> selected, AuthenticatedReviewCapabilities? live,
        RuntimeAuthenticationResolution? authentication, string? authenticationHref, bool? authorizationRegistered, string? authorizationReason, DateTimeOffset now)
    {
        var result = new List<ApiReviewCapability>();
        var withContract = selected.Count(t => !string.IsNullOrWhiteSpace(t.ContractSource));
        result.Add(withContract > 0
            ? new(Static, "Static contract review", ApiReviewCapabilityState.Ready, $"{Count(withContract)} with a published contract: reviewed without authentication.")
            : new(Static, "Static contract review", ApiReviewCapabilityState.NotAvailable, "No selected target has a published contract (OpenAPI/schema URL)."));

        var publicTargets = selected.Count(t => !t.AuthRequired);
        result.Add(publicTargets > 0
            ? new(Public, "Public API checks", ApiReviewCapabilityState.Ready, $"{Count(publicTargets)} without authentication: read-only requests.")
            : new(Public, "Public API checks", ApiReviewCapabilityState.NotAvailable, selected.Count == 0 ? "No target selected." : "Every selected target requires authentication."));

        var observed = selected.SelectMany(t => t.Operations).Where(o => o.Source == ApiReviewTargetSource.DiscoveredTraffic).ToList();
        var last = observed.Where(o => o.LastObservedAt is not null).Select(o => o.LastObservedAt!.Value).DefaultIfEmpty().Max();
        result.Add(observed.Count == 0
            ? new(Runtime, "Runtime evidence", ApiReviewCapabilityState.NotAvailable, "No runtime request to the selected targets has been observed.")
            : new(Runtime, "Runtime evidence", ApiReviewCapabilityState.Available,
                $"{observed.Count} observed operation{(observed.Count == 1 ? "" : "s")}{(last == default ? "" : $", last {last.ToLocalTime():d MMM HH:mm}")}{(last != default && now - last > StaleAfter ? " (stale)" : "")}. Observed, not verified: evidence of earlier traffic, not current access."));

        result.Add(ActiveChecks(selected, live, authentication, authenticationHref));

        var authorization = authorizationRegistered switch
        {
            true => new ApiReviewCapability(Authorization, "Authorization checks (optional)", ApiReviewCapabilityState.Ready, "Server-registered trusted target. Run them from Security → Authorization scenarios."),
            false => new ApiReviewCapability(Authorization, "Authorization checks (optional)", ApiReviewCapabilityState.PermissionRequired, authorizationReason ?? "Server permission required: the environment is not a server-registered trusted target."),
            null => new ApiReviewCapability(Authorization, "Authorization checks (optional)", ApiReviewCapabilityState.NotSelected, "Optional; not part of the read-only review."),
        };
        result.Add(authorization);
        return result;
    }

    private static ApiReviewCapability ActiveChecks(IReadOnlyList<ApiReviewTarget> selected, AuthenticatedReviewCapabilities? live, RuntimeAuthenticationResolution? authentication, string? href)
    {
        const string label = "Active authenticated API checks";
        var protectedTargets = selected.Count(t => t.AuthRequired);
        if (protectedTargets == 0) return new(Active, label, ApiReviewCapabilityState.NotSelected, "No selected target requires authentication.");
        switch (authentication?.Effective)
        {
            case RuntimeAuthenticationMode.None:
                return new(Active, label, ApiReviewCapabilityState.NotSelected, "Authentication None selected: protected operations are skipped, not passed.");
            case RuntimeAuthenticationMode.Unattended:
                return new(Active, label, ApiReviewCapabilityState.NotAvailable, "Unattended login is not supported for API checks: it produces a browser session, not API credentials.");
        }
        if (live?.AuthenticatedApi == true)
            return new(Active, label, ApiReviewCapabilityState.Ready, $"{Count(protectedTargets)} with a current authenticated context (verified by the backend before each request).");
        if (live?.ContextStatus == AuthenticatedApiContextStatus.Expired)
            return new(Active, label, ApiReviewCapabilityState.AuthenticationExpired, $"The attended session expired. {Count(protectedTargets)} will be skipped until you sign in again.", "Re-authenticate", href);
        return new(Active, label, ApiReviewCapabilityState.AuthenticationRequired, $"No authenticated context. {Count(protectedTargets)} will be skipped.", "Open Authentication setup", href);
    }

    private static string Count(int n) => n == 1 ? "1 target" : $"{n} targets";

    public static string StateLabel(ApiReviewCapabilityState state) => state switch
    {
        ApiReviewCapabilityState.Ready => "Ready",
        ApiReviewCapabilityState.Available => "Available",
        ApiReviewCapabilityState.AuthenticationRequired => "Authentication required",
        ApiReviewCapabilityState.AuthenticationExpired => "Authentication expired",
        ApiReviewCapabilityState.PermissionRequired => "Server permission required",
        ApiReviewCapabilityState.NotSelected => "Not selected",
        _ => "Not available",
    };
}
