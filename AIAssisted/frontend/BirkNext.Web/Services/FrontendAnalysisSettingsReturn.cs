namespace BirkNext.Web.Services;

/// <summary>
/// The "return=" deep-link parameter of Target Environments: where "Back" goes after a review sent the person to an editor. Only same-app review
/// routes are honoured — never an absolute or protocol-relative URL — so the parameter cannot become an open redirect.
/// </summary>
public static class FrontendAnalysisSettingsReturn
{
    private static readonly string[] AllowedRoutes = ["/api-quality-review"];

    public static bool IsAllowed(string? href) =>
        href is { Length: > 0 and <= 2048 }
        && href.StartsWith('/') && !href.StartsWith("//", StringComparison.Ordinal) && !href.Contains('\\')
        && AllowedRoutes.Any(route => href == route || href.StartsWith(route + "?", StringComparison.Ordinal));
}
