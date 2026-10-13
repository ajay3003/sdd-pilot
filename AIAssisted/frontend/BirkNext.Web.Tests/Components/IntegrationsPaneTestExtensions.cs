using Bunit;
using BirkNext.Web.Components;

namespace BirkNext.Web.Tests.Components;

/// <summary>Platform and runtime-evidence setup, the messaging scope and source diagnostics live under Advanced (collapsed by default).</summary>
internal static class IntegrationsPaneTestExtensions
{
    public static IRenderedComponent<IntegrationsPane> WithAdvancedOpen(this IRenderedComponent<IntegrationsPane> cut)
    {
        cut.WaitForState(() => cut.FindAll("[data-testid=ip-advanced-toggle]").Count > 0 || cut.FindAll("[data-testid=ip-load-error]").Count > 0);
        if (cut.FindAll("[data-testid=ip-advanced-toggle]") is { Count: > 0 } toggle && toggle[0].GetAttribute("aria-expanded") != "true") toggle[0].Click();
        return cut;
    }
}
