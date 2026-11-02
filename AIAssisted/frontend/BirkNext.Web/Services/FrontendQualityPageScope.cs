using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// "Review current page only": one run narrowed to the page open in the paired browser. Page evidence engines (Browser Quality, Browser
/// Performance Quality) read that page's evidence alone; URL engines review its address instead of the Target Environment's frontend URL.
/// The narrowing is stated in the report — a single-page review never reads as a review of the application.
/// </summary>
public static class FrontendQualityPageScope
{
    public const string AllPagesLabel = "All captured pages";
    public const string CurrentPageLabel = "Current page only";

    /// <summary>Whether a page belongs to this run: every page without a narrowing, else the same origin + route (trailing slash and
    /// origin case ignored — the identity is the same page either way).</summary>
    public static bool Includes(FrontendAnalysisContext context, string pageIdentity) =>
        context.ReviewPageIdentity is not { Length: > 0 } current || string.Equals(Normalize(pageIdentity), Normalize(current), StringComparison.OrdinalIgnoreCase);

    public static string? Limitation(FrontendAnalysisContext context) => context.ReviewPageIdentity is { Length: > 0 } current
        ? $"Scope: current page only ({current}). Other captured pages were not reviewed in this run."
        : null;

    /// <summary>The address URL engines review: the current page when it is an absolute http(s) URL, else the frontend URL.</summary>
    public static string ReviewUrl(FrontendAnalysisContext context) =>
        context.ReviewPageIdentity is { Length: > 0 } current && Uri.TryCreate(current, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.ToString() : context.TargetUrl;

    private static string Normalize(string identity) => identity.Length > 1 ? identity.TrimEnd('/') : identity;
}
