using BirkNext.Web.Components;
using Bunit;

namespace BirkNext.Web.Tests.Components;

/// <summary>Browser Discovery paints a loading state first and its content once the stored evidence is ready (the live status may still be in
/// flight); tests that read content wait for it.</summary>
internal static class BrowserDiscoveryTestExtensions
{
    public static IRenderedComponent<BrowserDiscoveryTab> Loaded(this IRenderedComponent<BrowserDiscoveryTab> cut)
    {
        cut.WaitForAssertion(() =>
        {
            if (cut.FindAll("[data-testid=bd-loading]").Count > 0) throw new InvalidOperationException("Browser Discovery is still loading its stored evidence.");
        });
        return cut;
    }
}
