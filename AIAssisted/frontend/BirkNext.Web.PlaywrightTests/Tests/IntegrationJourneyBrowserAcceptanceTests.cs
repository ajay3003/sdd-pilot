using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Browser acceptance of Integration journeys (Skolenærvær Testing) against an already running, isolated BirkNext — never a real call: no
/// executor or observer is registered and no Entra configuration exists, so every journey is expected to be blocked/not ready. Uses the same
/// configuration as the Active Event acceptance (BIRKNEXT_ACTIVE_EVENT_BACKEND_URL / _FRONTEND_URL; Development backend with loopback
/// configuration writes and a TargetEnvironments:Trusted record for "skole-acceptance"); without it the test does nothing.
/// </summary>
public sealed class IntegrationJourneyBrowserAcceptanceTests
{
    private const string EnvironmentId = "skole-acceptance";
    private static readonly int[] Widths = [1440, 1100, 768, 390];

    [Fact]
    public async Task Overview_JourneyTabs_Rules_History_ResponsiveAxeAndKeyboard_WithoutAnyExternalCall()
    {
        var backend = Environment.GetEnvironmentVariable("BIRKNEXT_ACTIVE_EVENT_BACKEND_URL")?.TrimEnd('/');
        var frontend = Environment.GetEnvironmentVariable("BIRKNEXT_ACTIVE_EVENT_FRONTEND_URL")?.TrimEnd('/');
        if (backend is null || frontend is null) return;
        var configuredBackend = (Environment.GetEnvironmentVariable("BIRKNEXT_ACTIVE_EVENT_FRONTEND_BACKEND_URL") ?? "http://localhost:5000").TrimEnd('/');

        using (var api = new HttpClient { BaseAddress = new Uri(backend + "/") })
        {
            // Loopback configuration writes (Development only): an Altinn report intake (REST) and a Service Bus period-closed integration.
            foreach (var integration in new object[]
            {
                new { id = "dev:http:altinn-skolenaervaer", environmentId = EnvironmentId, displayName = "Skolenærvær – Altinn report intake", kind = "HttpApi", enabled = true,
                    producer = "Altinn (test environment)", consumer = new { displayName = "MU" } },
                new { id = "dev:servicebus:skolenaervaer-periode-lukket", environmentId = EnvironmentId, displayName = "Skolenærvær – Period Closed", kind = "ServiceBus", enabled = true,
                    endpointOrTopic = "SkolenaervaersperiodeLukket", producer = "Skolenærværsrapport", consumer = new { displayName = "Bufdata" } },
            })
            {
                var created = await api.PostAsJsonAsync($"api/integrations?environmentId={EnvironmentId}", integration);
                created.StatusCode.Should().BeOneOf(System.Net.HttpStatusCode.OK, System.Net.HttpStatusCode.Conflict);
            }
            (await api.PostAsJsonAsync("api/integration-journeys/runs", new { environmentId = EnvironmentId, packId = "skolenaervaer", journeyId = "rapportmottak", scenarioId = "valid-report", confirmed = true }))
                .StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized, "journey execution needs ActiveEventExecute");
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
        var executionRequests = 0;
        page.Request += (_, request) =>
        {
            if (request.Method == "POST" && (request.Url.Contains("/api/integration-journeys/runs", StringComparison.Ordinal) || request.Url.Contains("/api/active-events/runs", StringComparison.Ordinal)))
                executionRequests++;
        };

        await page.GotoAsync(frontend + "/integration-quality-review", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        var panel = page.Locator("[data-testid=ijp-panel]");
        await panel.WaitForAsync(new() { Timeout = 60000 });
        await page.Locator("[data-testid=ijp-overview-row]").First.WaitForAsync(new() { Timeout = 60000 });

        // Overview: three journeys, each with its own readiness; nothing claims to be verified.
        (await page.Locator("[data-testid=ijp-pack-title]").InnerTextAsync()).Should().Be("Skolenærvær Testing");
        var rows = page.Locator("[data-testid=ijp-overview-row]");
        (await rows.EvaluateAllAsync<string[]>("rows => rows.map(r => r.dataset.journey)")).Should().Equal("utdanningsdata", "rapportmottak", "periodeavslutning");
        (await rows.EvaluateAllAsync<string[]>("rows => rows.map(r => r.dataset.readiness)")).Should().NotContain("Ready");
        (await panel.InnerTextAsync()).Should().Contain("Not verified (no run)");
        await AssertLayoutAndAxeAsync(page, "overview");

        // Rapportmottak: Altinn, MU, MM and Skolenærværsrapport steps; auth and contract missing; run disabled.
        await page.Locator("[data-testid=ijp-tab-rapportmottak]").ClickAsync();
        await page.Locator("[data-testid=ijp-journey][data-journey=rapportmottak]").WaitForAsync(new() { Timeout = 30000 });
        (await page.Locator("[data-testid=ijp-step]").CountAsync()).Should().Be(11);
        (await page.Locator("[data-testid=ijp-step][data-step=mm-xsd]").InnerTextAsync()).Should().Contain("MM");
        (await page.Locator("[data-testid=ijp-prerequisite][data-key=altinn-auth]").InnerTextAsync()).Should().Contain("Authentication not configured");
        (await page.Locator("[data-testid=ijp-prerequisite][data-key='integration:report-intake']").GetAttributeAsync("data-state")).Should().Be("Ready", "the IQR integration is reused");
        (await page.Locator("[data-testid=ijp-start]").IsDisabledAsync()).Should().BeTrue();
        await AssertLayoutAndAxeAsync(page, "rapportmottak");

        // Periodeavslutning: Service Bus reused from IQR; no safe trigger; Bufdata not verifiable.
        await page.Locator("[data-testid=ijp-tab-periodeavslutning]").ClickAsync();
        await page.Locator("[data-testid=ijp-prerequisite][data-key=domain-trigger]").WaitForAsync(new() { Timeout = 30000 });
        (await page.Locator("[data-testid=ijp-prerequisite][data-key=domain-trigger]").GetAttributeAsync("data-state")).Should().Be("NotReady");
        (await page.Locator("[data-testid=ijp-prerequisite][data-key=bufdata-verifier]").GetAttributeAsync("data-state")).Should().Be("NotAvailable");
        await AssertLayoutAndAxeAsync(page, "periodeavslutning");

        // Utdanningsdata: delegated to Active tests, no second runner.
        await page.Locator("[data-testid=ijp-tab-utdanningsdata]").ClickAsync();
        await page.Locator("[data-testid=ijp-delegated]").WaitForAsync(new() { Timeout = 30000 });
        (await page.Locator("[data-testid=ijp-start]").CountAsync()).Should().Be(0);

        // Architecture rules: not assessed without source.
        await page.Locator("[data-testid=ijp-tab-rules]").ClickAsync();
        await page.Locator("[data-testid=ijp-rule]").First.WaitForAsync(new() { Timeout = 30000 });
        (await page.Locator("[data-testid=ijp-rule]").EvaluateAllAsync<string[]>("rows => rows.map(r => r.dataset.outcome)")).Should().OnlyContain(outcome => outcome == "NotAssessed");
        await AssertLayoutAndAxeAsync(page, "rules");

        // History: protected, and says why.
        await page.Locator("[data-testid=ijp-tab-history]").ClickAsync();
        await page.Locator("[data-testid=ijp-history-error]").WaitForAsync(new() { Timeout = 30000 });
        (await page.Locator("[data-testid=ijp-history-error]").InnerTextAsync()).Should().Contain("Authentication not configured");
        await AssertLayoutAndAxeAsync(page, "history");

        // Keyboard: tabs move with arrow keys; tabs, filters and export show a visible focus indicator.
        await page.Keyboard.PressAsync("Shift");
        await page.Locator("[data-testid=ijp-tab-overview]").ClickAsync();
        await page.Locator("[data-testid=ijp-tab-overview]").FocusAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        (await page.EvaluateAsync<string>("document.activeElement?.dataset?.testid ?? ''")).Should().Be("ijp-tab-utdanningsdata");
        await page.Locator("[data-testid=ijp-tab-history]").ClickAsync();
        foreach (var selector in new[] { "[data-testid=ijp-tab-overview]", "[data-testid=ijp-tab-rapportmottak]", "[data-testid=ijp-history-journey]", "[data-testid=ijp-history-state]", "[data-testid=ijp-export]" })
        {
            var element = page.Locator(selector).First;
            await page.Keyboard.PressAsync("Shift");
            await element.FocusAsync();
            (await element.EvaluateAsync<bool>("e => e === document.activeElement")).Should().BeTrue(selector);
            var outline = await element.EvaluateAsync<string>("e => { const s = getComputedStyle(e); return s.outlineStyle + ' ' + s.outlineWidth + ' ' + s.boxShadow; }");
            outline.Should().NotBe("none 0px none", $"{selector} needs a visible focus indicator");
        }

        // Export is generated in the browser from the readiness contracts.
        var download = await page.RunAndWaitForDownloadAsync(() => page.Locator("[data-testid=ijp-export]").ClickAsync());
        download.SuggestedFilename.Should().StartWith("integration-journeys-skolenaervaer-");
        executionRequests.Should().Be(0, "nothing is executed in this configuration");
    }

    private static async Task AssertLayoutAndAxeAsync(IPage page, string state)
    {
        await page.AddScriptTagAsync(new() { Path = AxePath() });
        var violations = await page.EvaluateAsync<JsonElement>(@"async () => {
            const result = await axe.run(document.querySelector('[data-testid=ijp-panel]'), { runOnly: { type: 'tag', values: ['wcag2a','wcag2aa','wcag21a','wcag21aa'] } });
            return result.violations.map(v => v.id + ': ' + v.nodes.map(n => n.target.join(' ')).join(', '));
        }");
        violations.EnumerateArray().Select(v => v.GetString()).Should().BeEmpty($"no A/AA violations in the journey panel ({state})");
        foreach (var width in Widths)
        {
            await page.SetViewportSizeAsync(width, 1000);
            await page.WaitForTimeoutAsync(150);
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth")).Should().BeFalse($"no horizontal overflow at {width}px ({state})");
            Directory.CreateDirectory(ScreenshotDirectory());
            await page.Locator("[data-testid=ijp-panel]").ScreenshotAsync(new() { Path = Path.Combine(ScreenshotDirectory(), $"journeys-{state}-{width}.png") });
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
