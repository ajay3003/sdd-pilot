using BirkNext.Web.Components;
using Bunit;

namespace BirkNext.Web.Tests.Components;

/// <summary>Browser Discovery paints a loading state first and its content after loading; tests that read content wait for it.</summary>
internal static class BrowserDiscoveryTestExtensions
{
    public static IRenderedComponent<BrowserDiscoveryTab> Loaded(this IRenderedComponent<BrowserDiscoveryTab> cut)
    {
        cut.WaitForAssertion(() =>
        {
            if (cut.Find("[data-testid=browser-discovery]").GetAttribute("data-loaded") != "true") throw new InvalidOperationException("Browser Discovery is still loading.");
        });
        // Loading ends with a live-status refresh whose re-render is queued on the renderer: drain the queue so a following click targets the
        // final render's handlers (a click dispatched before it would be addressed to handlers that the queued render replaces).
        for (var settled = 0; settled < 3;)
        {
            var renders = cut.RenderCount;
            cut.InvokeAsync(() => { }).GetAwaiter().GetResult();
            Thread.Sleep(10);
            settled = cut.RenderCount == renders ? settled + 1 : 0;
        }
        return cut;
    }
}
