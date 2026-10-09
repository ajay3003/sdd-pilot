using BirkNext.MarkdownDiagnostics;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests;

/// <summary>Collects stable projection attributes from a real Explorer route and correlates them with page-model IDs.</summary>
public static class ProjectionRenderBrowserVerifier
{
    public static async Task<ProjectionRenderVerification> VerifyAsync(IPage page, string appBaseUrl, string route,
        string artifactRole, string expectedDocumentId, IEnumerable<string> expectedProjectionIds, bool reuseCurrentRoute = false)
    {
        var expected = expectedProjectionIds.ToArray();
        if (!Uri.TryCreate(appBaseUrl, UriKind.Absolute, out var baseUri) ||
            !route.StartsWith("/", StringComparison.Ordinal) || route.StartsWith("//", StringComparison.Ordinal))
            return ProjectionRenderVerification.Unavailable(artifactRole, expectedDocumentId, route, expected, "Explorer route was invalid.");

        try
        {
            var targetUri = new Uri(baseUri, route);
            var alreadyOnTarget = Uri.TryCreate(page.Url, UriKind.Absolute, out var currentUri) &&
                string.Equals(currentUri.Scheme, targetUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(currentUri.Host, targetUri.Host, StringComparison.OrdinalIgnoreCase) && currentUri.Port == targetUri.Port &&
                string.Equals(currentUri.AbsolutePath.TrimEnd('/'), targetUri.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(currentUri.Query, targetUri.Query, StringComparison.Ordinal);
            if (reuseCurrentRoute && !alreadyOnTarget)
                return ProjectionRenderVerification.Unavailable(artifactRole, expectedDocumentId, route, expected, "ExplorerRouteMismatch");
            if (!alreadyOnTarget)
                await page.GotoAsync(targetUri.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
            var host = page.Locator("[data-testid='artifact-explorer']");
            await host.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
            await page.WaitForFunctionAsync("() => { const host = document.querySelector('[data-testid=artifact-explorer]'); return host && host.dataset.status !== 'Loading'; }",
                null, new PageWaitForFunctionOptions { Timeout = 30000 });
            var role = await host.GetAttributeAsync("data-role");
            var status = await host.GetAttributeAsync("data-status");
            var expectedHostRole = artifactRole switch
            {
                "Data Model" => "DataModel",
                "Tasks" => "Tasks",
                _ => artifactRole
            };
            if (!string.Equals(role, expectedHostRole, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(status, "Loaded", StringComparison.OrdinalIgnoreCase))
                return ProjectionRenderVerification.Unavailable(artifactRole, expectedDocumentId, route, expected,
                    $"ExplorerArtifactNotLoaded:role={role ?? "absent"};status={status ?? "absent"}.");

            var identityFailure = await ValidateDocumentIdentityAsync(page, artifactRole, expectedDocumentId, route, expected);
            if (identityFailure is not null) return identityFailure;

            var observed = new Dictionary<string, int>(StringComparer.Ordinal);
            await ExpandAllAsync(page, artifactRole == "Specification" ? ".se-ctrl-btn" : ".te-ctrl-btn");
            await CollectCurrentSurfaceAsync(page, observed);
            foreach (var selector in TabsFor(artifactRole))
            {
                var tabs = page.Locator(selector);
                var count = await tabs.CountAsync();
                for (var index = 0; index < count; index++)
                {
                    var tab = tabs.Nth(index);
                    if (!await tab.IsVisibleAsync()) continue;
                    await tab.ClickAsync();
                    await page.WaitForTimeoutAsync(50);
                    await CollectCurrentSurfaceAsync(page, observed);
                }
            }

            if (artifactRole == "Tasks")
            {
                foreach (var taskRoute in new[] { route, route.Contains('?') ? route + "&view=map" : route + "?view=map" })
                {
                    if (taskRoute != route)
                    {
                        await page.GotoAsync(new Uri(baseUri, taskRoute).ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                        await host.WaitForAsync(new LocatorWaitForOptions { Timeout = 30000 });
                    }
                    await ExpandAllAsync(page, ".te-ctrl-btn");
                    await CollectCurrentSurfaceAsync(page, observed);
                }
            }

            return await CompareCurrentPageAsync(page, artifactRole, expectedDocumentId, route,
                expected, observed.SelectMany(pair => Enumerable.Repeat(pair.Key, pair.Value)));
        }
        catch (Exception ex)
        {
            return ProjectionRenderVerification.Unavailable(artifactRole, expectedDocumentId, route, expected,
                $"Browser verification could not complete ({ex.GetType().Name}).");
        }
    }

    /// <summary>Verifies page identity before reading projection IDs; mismatch/absence cannot become RenderMissing.</summary>
    public static async Task<ProjectionRenderVerification> CompareCurrentPageAsync(IPage page, string artifactRole,
        string expectedDocumentId, string route, IEnumerable<string> expectedProjectionIds,
        IEnumerable<string>? observedProjectionIds = null)
    {
        var expected = expectedProjectionIds.ToArray();
        var host = page.Locator("[data-testid='artifact-explorer']");
        if (await host.CountAsync() == 0)
            return ProjectionRenderVerification.Unavailable(artifactRole, expectedDocumentId, route, expected,
                "ExplorerTargetUnavailable");
        var role = await host.GetAttributeAsync("data-role");
        var status = await host.GetAttributeAsync("data-status");
        var expectedHostRole = artifactRole switch { "Data Model" => "DataModel", "Tasks" => "Tasks", _ => artifactRole };
        if (!string.Equals(role, expectedHostRole, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(status, "Loaded", StringComparison.OrdinalIgnoreCase))
            return ProjectionRenderVerification.Unavailable(artifactRole, expectedDocumentId, route, expected,
                "ExplorerTargetUnavailable");
        var identityFailure = await ValidateDocumentIdentityAsync(page, artifactRole, expectedDocumentId, route, expected);
        if (identityFailure is not null) return identityFailure;

        var observed = observedProjectionIds?.ToArray();
        if (observed is null)
            observed = await page.Locator("[data-birknext-projection-id]").EvaluateAllAsync<string[]>(
                "els => els.filter(el => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden').map(el => el.getAttribute('data-birknext-projection-id')).filter(Boolean)");
        var actualDocumentId = await host.GetAttributeAsync("data-birknext-document-id");
        return ProjectionRenderVerification.Compare(artifactRole, expectedDocumentId, actualDocumentId!, route, expected, observed);
    }

    private static async Task<ProjectionRenderVerification?> ValidateDocumentIdentityAsync(IPage page, string role,
        string expectedDocumentId, string route, IReadOnlyList<string> expectedProjectionIds)
    {
        var host = page.Locator("[data-testid='artifact-explorer']");
        var actualDocumentId = await host.GetAttributeAsync("data-birknext-document-id");
        if (string.IsNullOrWhiteSpace(actualDocumentId))
            return ProjectionRenderVerification.Unavailable(role, expectedDocumentId, route, expectedProjectionIds,
                "DocumentIdentityUnavailable");
        if (!string.Equals(expectedDocumentId, actualDocumentId, StringComparison.Ordinal))
            return ProjectionRenderVerification.Unavailable(role, expectedDocumentId, route, expectedProjectionIds,
                "DocumentMismatch", actualDocumentId);
        return null;
    }

    private static IReadOnlyList<string> TabsFor(string role) => role switch
    {
        "Specification" => [".se-view-toggle button"],
        "Constitution" => [".ce-view-toggle button"],
        "Plan" => [".pe-view-toggle [role='tab']"],
        "Data Model" => [".dme-tab-bar [role='tab']"],
        _ => []
    };

    private static async Task CollectCurrentSurfaceAsync(IPage page, IDictionary<string, int> maxCounts)
    {
        var values = await page.Locator("[data-birknext-projection-id]").EvaluateAllAsync<string[]>(
            "els => els.filter(el => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden').map(el => el.getAttribute('data-birknext-projection-id')).filter(Boolean)");
        foreach (var group in values.GroupBy(value => value, StringComparer.Ordinal))
        {
            maxCounts.TryGetValue(group.Key, out var current);
            maxCounts[group.Key] = Math.Max(current, group.Count());
        }
    }

    private static async Task ExpandAllAsync(IPage page, string selector)
    {
        for (var pass = 0; pass < 20; pass++)
        {
            var buttons = page.Locator(selector);
            var count = await buttons.CountAsync();
            var expandAll = false;
            for (var index = 0; index < count; index++)
            {
                var button = buttons.Nth(index);
                if (await button.IsVisibleAsync() && (await button.InnerTextAsync()).Contains("Expand All", StringComparison.OrdinalIgnoreCase))
                {
                    await button.ClickAsync();
                    expandAll = true;
                    break;
                }
            }
            if (!expandAll) break;
        }
        var expanders = page.Locator(".se-expand-btn[aria-label='Expand'], .te-expand-btn[aria-label='Expand']");
        for (var pass = 0; pass < 20; pass++)
        {
            var count = await expanders.CountAsync();
            if (count == 0) break;
            await expanders.First.ClickAsync();
            await page.WaitForTimeoutAsync(10);
        }
        var mapExpanders = page.Locator(".ce-map-toggle[aria-expanded='false']");
        for (var pass = 0; pass < 20; pass++)
        {
            if (await mapExpanders.CountAsync() == 0) break;
            await mapExpanders.First.ClickAsync();
            await page.WaitForTimeoutAsync(10);
        }
    }
}
