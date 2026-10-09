using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Browser acceptance of the shared Active Event panel against an already running, isolated BirkNext (backend + frontend) — never a real
/// send: active execution is disabled in that backend and no Entra configuration exists, so the expected product state is "blocked".
/// Configure with BIRKNEXT_ACTIVE_EVENT_BACKEND_URL and BIRKNEXT_ACTIVE_EVENT_FRONTEND_URL (the frontend's configured backend URL is
/// routed to the isolated backend); without them the test does nothing. The backend must run in Development with
/// Authorization:LocalConfigurationWrites (loopback catalog writes) and a TargetEnvironments:Trusted record for "skole-acceptance".
/// </summary>
public sealed class ActiveEventTestingBrowserAcceptanceTests
{
    private const string EnvironmentId = "skole-acceptance";
    private static readonly int[] Widths = [1440, 1100, 768, 390];

    [Fact]
    public async Task ProviderSelection_BlockedReadiness_AuthNotConfigured_History_ResponsiveAxeAndKeyboard()
    {
        var backend = Environment.GetEnvironmentVariable("BIRKNEXT_ACTIVE_EVENT_BACKEND_URL")?.TrimEnd('/');
        var frontend = Environment.GetEnvironmentVariable("BIRKNEXT_ACTIVE_EVENT_FRONTEND_URL")?.TrimEnd('/');
        if (backend is null || frontend is null) return; // not configured: this acceptance runs only against an isolated, pre-started instance
        var configuredBackend = (Environment.GetEnvironmentVariable("BIRKNEXT_ACTIVE_EVENT_FRONTEND_BACKEND_URL") ?? "http://localhost:5000").TrimEnd('/');

        using (var api = new HttpClient { BaseAddress = new Uri(backend + "/") })
        {
            // Loopback configuration writes (Development only): the seeded M2LB DEV platform + Person CDC, plus an Utdanning CDC integration.
            (await api.PostAsync($"api/integrations/templates/m2lb-dev-eventhub/apply?environmentId={EnvironmentId}", null)).EnsureSuccessStatusCode();
            var created = await api.PostAsJsonAsync($"api/integrations?environmentId={EnvironmentId}", new
            {
                id = "dev:eventhub:birk-cdc:dbo.Utdanning", environmentId = EnvironmentId, platformId = "dev:eventhub:m2lb", displayName = "BIRK Utdanning CDC",
                kind = "EventHub", enabled = true, sourceResource = "BirkM2LB.dbo.Utdanning", endpointOrTopic = "m2lb-cdc-dev.BirkM2LB.dbo.Utdanning",
                consumer = new { displayName = "SkoleAdapter" },
            });
            created.StatusCode.Should().BeOneOf(System.Net.HttpStatusCode.OK, System.Net.HttpStatusCode.Conflict);
            (await api.GetAsync($"api/active-events/runs?environmentId={EnvironmentId}")).StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized, "history is protected");
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        await context.RouteAsync(configuredBackend + "/**", route => route.ContinueAsync(new() { Url = backend + route.Request.Url[configuredBackend.Length..] }));
        var settings = JsonSerializer.Serialize(JsonSerializer.Serialize(new
        {
            profiles = new[] { new { id = EnvironmentId, name = "Skole acceptance", environmentType = "Development", targetUrl = "https://skole-dev.example.test/",
                authentication = new { requiresAuthentication = false, authenticationType = "None" }, integrations = Array.Empty<object>() } },
            activeProfileId = EnvironmentId,
        }));
        await context.AddInitScriptAsync($"localStorage.setItem('birknext:frontend-analysis-settings', {settings});");
        var page = await context.NewPageAsync();
        var sendRequests = 0;
        page.Request += (_, request) => { if (request.Method == "POST" && request.Url.Contains("/api/active-events/runs", StringComparison.Ordinal)) sendRequests++; };

        await page.GotoAsync(frontend + "/integration-quality-review", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        var panel = page.Locator("[data-testid=act-panel]");
        await panel.WaitForAsync(new() { Timeout = 60000 });
        await page.Locator("[data-testid=act-open]").ClickAsync();

        // Provider selection: both registered providers are listed; Person is preselected (it has an applicable integration).
        var person = page.Locator("[data-testid='act-provider-m2lb.person']");
        var skole = page.Locator("[data-testid='act-provider-skolenaervaer.cdc']");
        await person.WaitForAsync(new() { Timeout = 30000 });
        (await person.IsCheckedAsync()).Should().BeTrue();
        await page.Locator("[data-testid=act-check][data-key=authentication]").WaitForAsync(new() { Timeout = 30000 });
        (await page.Locator("[data-testid=act-check][data-key=authentication]").GetAttributeAsync("data-state")).Should().Be("NotConfigured");
        (await page.Locator("[data-testid=act-check][data-key=enabled]").GetAttributeAsync("data-state")).Should().Be("NotConfigured", "active execution is disabled in this backend");
        (await page.Locator("[data-testid=act-start]").IsDisabledAsync()).Should().BeTrue();
        (await page.Locator("[data-testid=act-summary-execution]").InnerTextAsync()).Should().Be("Authentication not configured");
        await AssertLayoutAndAxeAsync(page, "person");

        // Skolenærvær: applicable to the Utdanning integration, every missing prerequisite is shown, nothing can be sent.
        await skole.CheckAsync();
        await page.Locator("[data-testid=act-check][data-key=cdc-capture]").WaitForAsync(new() { Timeout = 30000 });
        (await page.Locator("[data-testid=act-integration] option").AllInnerTextsAsync()).Should().Equal("BIRK Utdanning CDC");
        (await page.Locator("[data-testid=act-check][data-key=source-contract]").InnerTextAsync()).Should().Contain("SkoleAdapter source is not present");
        (await page.Locator("[data-testid=act-check][data-key=cdc-capture]").GetAttributeAsync("data-state")).Should().Be("Blocked");
        (await page.Locator("[data-testid=act-check][data-key=synthetic-data]").GetAttributeAsync("data-state")).Should().Be("NotConfigured");
        (await page.Locator("[data-testid=act-check][data-key=downstream]").GetAttributeAsync("data-state")).Should().Be("Partial");
        (await page.Locator("[data-testid=act-start]").IsDisabledAsync()).Should().BeTrue();
        await page.Locator("[data-testid='act-scenario-skolenaervaer.utdanning.unknown-child-late-linkage']").CheckAsync();
        await page.Locator("[data-testid=act-support]").WaitForAsync(new() { Timeout = 30000 });
        await AssertLayoutAndAxeAsync(page, "skolenaervaer-blocked");

        // History is protected: without sign-in it says so, and authorization is shown as not ready.
        await page.Locator("[data-testid=act-history] summary").ClickAsync();
        await page.Locator("[data-testid=act-history-error]").WaitForAsync(new() { Timeout = 30000 });
        (await page.Locator("[data-testid=act-history-error]").InnerTextAsync()).Should().Contain("Sign-in required");
        (await page.Locator("[data-testid=act-check][data-key=authorization]").GetAttributeAsync("data-state")).Should().Be("Blocked");
        (await page.Locator("[data-testid=act-filter-provider] option").AllInnerTextsAsync()).Should().Contain(["M2LB Person CDC", "Skolenærvær Testing"]);
        await AssertLayoutAndAxeAsync(page, "history");

        // Keyboard: every control is reachable and shows a visible focus indicator.
        foreach (var selector in new[] { "[data-testid='act-provider-m2lb.person']", "[data-testid=act-integration]", "[data-testid='act-scenario-skolenaervaer.utdanning.create']",
                     "[data-testid=act-history] summary", "[data-testid=act-filter-provider]", "[data-testid=act-filter-status]", "[data-testid=act-filter-from]" })
        {
            var element = page.Locator(selector).First;
            await page.Keyboard.PressAsync("Shift"); // keyboard modality, so :focus-visible applies to programmatic focus
            await element.FocusAsync();
            (await element.EvaluateAsync<bool>("e => e === document.activeElement")).Should().BeTrue(selector);
            var outline = await element.EvaluateAsync<string>("e => { const s = getComputedStyle(e); return s.outlineStyle + ' ' + s.outlineWidth + ' ' + s.boxShadow; }");
            outline.Should().NotBe("none 0px none", $"{selector} needs a visible focus indicator");
        }
        await page.Keyboard.PressAsync("Shift+Tab");
        sendRequests.Should().Be(0, "nothing can be sent in this configuration");
    }

    private static async Task AssertLayoutAndAxeAsync(IPage page, string state)
    {
        await page.AddScriptTagAsync(new() { Path = AxePath() });
        var violations = await page.EvaluateAsync<JsonElement>(@"async () => {
            const result = await axe.run(document.querySelector('[data-testid=act-panel]'), { runOnly: { type: 'tag', values: ['wcag2a','wcag2aa','wcag21a','wcag21aa'] } });
            return result.violations.map(v => v.id + ': ' + v.nodes.map(n => n.target.join(' ')).join(', '));
        }");
        violations.EnumerateArray().Select(v => v.GetString()).Should().BeEmpty($"no A/AA violations in the Active Event panel ({state})");
        foreach (var width in Widths)
        {
            await page.SetViewportSizeAsync(width, 1000);
            await page.WaitForTimeoutAsync(150);
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth")).Should().BeFalse($"no horizontal overflow at {width}px ({state})");
            Directory.CreateDirectory(ScreenshotDirectory());
            await page.Locator("[data-testid=act-panel]").ScreenshotAsync(new() { Path = Path.Combine(ScreenshotDirectory(), $"{state}-{width}.png") });
        }
        await page.SetViewportSizeAsync(1440, 1000);
    }

    private static string ScreenshotDirectory() =>
        Environment.GetEnvironmentVariable("BIRKNEXT_ACTIVE_EVENT_SCREENSHOTS") ?? Path.Combine(Path.GetTempPath(), "birknext-active-event-acceptance");

    private static string AxePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "AIAssisted", "browser-companion", "vendor", "axe.min.js");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Repository axe-core bundle was not found.");
    }
}
