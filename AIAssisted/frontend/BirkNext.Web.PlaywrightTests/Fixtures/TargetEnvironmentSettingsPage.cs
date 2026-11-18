using System.Text.Json;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Fixtures;

/// <summary>
/// System Settings → Target Environments, driven through what the page renders and states (no fixed delays).
///
/// Target checks are answered by <see cref="FakeTargetCheckAsync"/>: a page route returns a synthetic detection result for exactly the URL the
/// page asked about, so no test ever makes the BirkNext API contact a real target (m2lbdev, TT02, …). The server-side detection logic itself
/// is covered by the backend tests; these tests cover what the tester sees and what is (not) saved.
/// </summary>
public static class TargetEnvironmentSettingsPage
{
    public static string Url(string? query = null) =>
        $"{BrowserTestServers.FrontendUrl}/admin/system-settings?section=target-environments{(query is null ? "" : "&" + query)}";

    /// <summary>
    /// Opens the section and waits until the selected environment's detail is ready: it renders (and accepts clicks) while the page is still
    /// loading, and is marked aria-busy / data-ready="false" until loading has finished. Actions taken earlier were handled seconds later on a
    /// loaded machine (a check "did nothing" within the test's wait, a focused button was re-rendered), so tests start once it is ready.
    /// </summary>
    public static async Task OpenAsync(IPage page, string? query = null)
    {
        await page.GotoAsync(Url(query), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        await page.Locator("[data-testid=fa-profile-detail][data-ready=true]").WaitForAsync(new() { Timeout = 120000 });
    }

    /// <summary>Selects an environment by name and waits until the detail shows it.</summary>
    public static async Task SelectAsync(IPage page, string environment)
    {
        await page.Locator(".fa-profile-chip").Filter(new() { HasText = environment }).First.ClickAsync();
        await Assertions.Expect(page.Locator(".fa-detail-name")).ToHaveTextAsync(environment);
    }

    public static async Task OpenTabAsync(IPage page, string tab)
    {
        var tabButton = page.GetByRole(AriaRole.Tab, new() { Name = tab, Exact = true });
        await tabButton.ClickAsync();
        await Assertions.Expect(tabButton).ToHaveAttributeAsync("aria-selected", "true");
    }

    public static async Task EditAsync(IPage page)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit Environment", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true }).WaitForAsync(new() { Timeout = 30000 });
    }

    /// <summary>
    /// Creates a test-owned environment (unique name) through "+ Add Environment" and waits until it is the selected one. Tests that need a
    /// particular saved state create it this way instead of changing the shared environments, and delete it again with
    /// <see cref="DeleteSelectedAsync"/>.
    /// </summary>
    public static async Task CreateAsync(IPage page, string name, string environmentType, string frontendUrl)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "+ Add Environment", Exact = true }).ClickAsync();
        await page.Locator("#fa-create-name").FillAsync(name);
        await page.GetByTestId("fa-create-type").SelectOptionAsync(environmentType);
        await page.GetByTestId("fa-create-url").FillAsync(frontendUrl);
        await page.Locator(".modal-actions").GetByRole(AriaRole.Button, new() { Name = "Add Environment", Exact = true }).ClickAsync();
        await page.Locator(".fa-profile-chip").Filter(new() { HasText = name }).WaitForAsync(new() { Timeout = 30000 });
        await SelectAsync(page, name);
    }

    /// <summary>Deletes the selected environment (only ever one a test created) through the confirmation dialog.</summary>
    public static async Task DeleteSelectedAsync(IPage page, string expectedName)
    {
        await Assertions.Expect(page.Locator(".fa-detail-name")).ToHaveTextAsync(expectedName);
        await page.Locator(".fa-profile-detail").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Delete Environment", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".fa-profile-chip").Filter(new() { HasText = expectedName })).ToHaveCountAsync(0, new() { Timeout = 30000 });
    }

    /// <summary>The detection request bodies seen by the fake (the URL the page asked to check).</summary>
    public sealed class TargetCheckFake
    {
        private readonly List<string> _requestedUrls = [];
        public IReadOnlyList<string> RequestedUrls { get { lock (_requestedUrls) return _requestedUrls.ToList(); } }
        internal void Add(string url) { lock (_requestedUrls) _requestedUrls.Add(url); }
    }

    /// <summary>
    /// Answers <c>api/frontend-target/detect</c> with a synthetic result for the requested URL. <paramref name="result"/> supplies the detected
    /// facts (camelCase detection fields, enum names as strings); the URL fields are always the requested URL, so the check is current.
    /// </summary>
    public static async Task<TargetCheckFake> FakeTargetCheckAsync(IPage page, Dictionary<string, object?> result)
    {
        var fake = new TargetCheckFake();
        await page.RouteAsync("**/api/frontend-target/detect", async route =>
        {
            using var request = JsonDocument.Parse(route.Request.PostData ?? "{}");
            var url = request.RootElement.TryGetProperty("targetUrl", out var value) ? value.GetString() ?? "" : "";
            fake.Add(url);
            var body = new Dictionary<string, object?>(result)
            {
                ["originalUrl"] = url, ["normalizedTargetUrl"] = url, ["success"] = true, ["warnings"] = Array.Empty<string>(),
            };
            body.TryAdd("isActivationReady", true);
            await route.FulfillAsync(new() { Status = 200, ContentType = "application/json", Body = JsonSerializer.Serialize(body) });
        });
        return fake;
    }

    /// <summary>A public Blazor WebAssembly target with no sign-in, plus values detection used to propose (REST/GraphQL) and an environment type.</summary>
    public static Dictionary<string, object?> PublicBlazorTarget(string suggestedEnvironmentType) => new()
    {
        ["reachability"] = "Reachable", ["authenticationRequired"] = false, ["detectedAuthenticationType"] = "None",
        ["detectedClientFramework"] = "BlazorWebAssembly", ["frameworkEvidence"] = "_framework/blazor.webassembly.js", ["frameworkConfidence"] = "High",
        ["confidence"] = "High", ["state"] = "Complete", ["suggestedEnvironmentType"] = suggestedEnvironmentType,
        ["detectedRestBaseUrl"] = "https://api.synthetic.example.test/api", ["detectedGraphQlEndpoint"] = "https://api.synthetic.example.test/graphql",
        ["detectedSwaggerUrl"] = "https://api.synthetic.example.test/swagger/v1/swagger.json",
    };
}
