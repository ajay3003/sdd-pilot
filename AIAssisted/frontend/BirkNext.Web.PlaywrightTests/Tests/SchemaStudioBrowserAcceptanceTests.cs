using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>Functional acceptance for a separately started current-worktree frontend. Set BIRKNEXT_SCHEMA_STUDIO_FRONTEND_URL to enable.</summary>
public sealed class SchemaStudioBrowserAcceptanceTests
{
    [Fact]
    public async Task LocalXsdWorkflowIsResponsiveAccessibleAndDoesNotContactExternalHosts()
    {
        var baseUrl = Environment.GetEnvironmentVariable("BIRKNEXT_SCHEMA_STUDIO_FRONTEND_URL")?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl)) return;

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var pageErrors = new List<string>();
        var consoleErrors = new List<string>();
        var externalRequests = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        page.Console += (_, message) => { if (message.Type == "error") consoleErrors.Add(message.Text); };
        page.Request += (_, request) =>
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                || uri.Host is not ("localhost" or "127.0.0.1" or "[::1]")) externalRequests.Add(request.Url);
        };

        await page.GotoAsync(baseUrl + "/schema-studio", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        await page.Locator("#ss-source-title").WaitForAsync(new() { Timeout = 60000 });
        await RemovePriorAcceptanceFixtureAsync(page);
        var initialEmptyText = string.Join(" ", await page.Locator(".ss-empty").AllInnerTextsAsync());
        var initialBody = await page.Locator("body").InnerTextAsync();
        Assert.True((initialEmptyText + " " + initialBody).Contains("No schemas imported", StringComparison.Ordinal),
            $"Empty state missing at {page.Url}. Body tail: {initialBody[^Math.Min(initialBody.Length, 1200)..]}. Page errors: {string.Join(" | ", pageErrors)}. Console errors: {string.Join(" | ", consoleErrors)}");
        var visibleFocus = false;
        var focusDiagnostic = "no control received focus";
        for (var i = 0; i < 10 && !visibleFocus; i++)
        {
            await page.Keyboard.PressAsync("Tab");
            visibleFocus = await page.EvaluateAsync<bool>("() => { const e = document.activeElement; return !!e && e.matches(':focus-visible') && parseFloat(getComputedStyle(e).outlineWidth) > 0; }");
            focusDiagnostic = await page.EvaluateAsync<string>("() => { const e = document.activeElement; return `${e?.tagName}.${e?.className}#${e?.id}; focusVisible=${e?.matches(':focus-visible')}; outline=${e ? getComputedStyle(e).outline : ''}`; }");
        }
        Assert.True(visibleFocus, $"Keyboard navigation reaches a control with visible focus. Last focus: {focusDiagnostic}");
        var importInput = page.Locator("input[aria-label='Choose XSD files']");
        var keyboardReachedImport = false;
        for (var i = 0; i < 100 && !keyboardReachedImport; i++)
        {
            await page.Keyboard.PressAsync("Tab");
            keyboardReachedImport = await importInput.EvaluateAsync<bool>("element => document.activeElement === element");
        }
        Assert.True(keyboardReachedImport, "Keyboard tab order reaches the XSD import control.");
        await AssertNoHorizontalOverflow(page);
        await AssertAxe(page);

        const string xsd = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:browser-acceptance" xmlns="urn:browser-acceptance" elementFormDefault="qualified">
              <xs:element name="Record"><xs:complexType><xs:sequence><xs:element name="Code"><xs:simpleType><xs:restriction base="xs:integer"><xs:minInclusive value="1"/><xs:maxInclusive value="3"/></xs:restriction></xs:simpleType></xs:element><xs:element name="Repeat" type="xs:string" minOccurs="0" maxOccurs="3"/></xs:sequence><xs:attribute name="mode" type="xs:string" use="required"/></xs:complexType></xs:element>
            </xs:schema>
            """;
        await importInput.SetInputFilesAsync(new FilePayload
        {
            Name = "browser-contract.xsd", MimeType = "application/xml", Buffer = Encoding.UTF8.GetBytes(xsd)
        });
        await page.GetByRole(AriaRole.Button, new() { Name = "browser-contract", Exact = false }).First.WaitForAsync(new() { Timeout = 30000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Effective Schema", Exact = true }).ClickAsync();
        await page.GetByText("@mode", new() { Exact = true }).WaitForAsync();
        await AssertNoHorizontalOverflow(page);
        await AssertAxe(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Test Data Profile", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Create profile", Exact = true }).ClickAsync();
        await page.GetByLabel("Profile rule element path").SelectOptionAsync("/Record/Repeat");
        await page.GetByLabel("Occurrence count (optional, 0–100)").FillAsync("2");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save generation rule", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Generate XML from profile", Exact = true }).ClickAsync();
        var generatedProfileXml = await page.GetByLabel("XML instance to validate").InputValueAsync();
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(generatedProfileXml, "<Repeat(?:\\s|>)").Count);
        Assert.Contains("Valid against", await page.Locator(".ss-result").Last.InnerTextAsync());
        await AssertNoHorizontalOverflow(page);
        await AssertAxe(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Generated Tests", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Run generated tests (first 100)", Exact = true }).ClickAsync();
        await page.Locator(".ss-table-wrap details summary").First.ClickAsync();
        await page.GetByText("Valid baseline XML", new() { Exact = true }).First.WaitForAsync();
        var resultText = await page.Locator(".ss-table-wrap").Nth(1).InnerTextAsync();
        Assert.Contains("missing required attribute", resultText);
        Assert.Contains("wrong datatype", resultText);
        foreach (var width in new[] { 1440, 1100, 768, 390 }) await AssertNoHorizontalOverflow(page, width);
        await AssertAxe(page);

        // Verify persistence across a fresh page/component instance using BirkNext's
        // supported Manage Workspaces reopen flow. Generated XML remains session-only.
        await page.GetByRole(AriaRole.Button, new() { Name = "Overview", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save analysis to workspace", Exact = true }).ClickAsync();
        await page.GetByText("Saved browser-contract to the current BirkNext workspace.", new() { Exact = true }).WaitForAsync(new() { Timeout = 30000 });
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        await page.Locator("#ss-source-title").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Reopen analysis", Exact = true }).WaitForAsync(new() { Timeout = 30000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Reopen analysis", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Effective Schema", Exact = true }).ClickAsync();
        await page.Locator(".ss-card h3").First.WaitForAsync(new() { Timeout = 30000 });
        Assert.Contains("Record", await page.Locator(".ss-card h3").First.InnerTextAsync());
        await AssertAxe(page);
        await RemovePriorAcceptanceFixtureAsync(page);

        Assert.Empty(pageErrors);
        Assert.Empty(consoleErrors);
        Assert.Empty(externalRequests);
    }

    private static async Task RemovePriorAcceptanceFixtureAsync(IPage page)
    {
        // The browser workflow is repeatable without deleting user artifacts: only remove
        // the exact synthetic fixture fingerprint created by this test.
        var savedFixture = page.Locator("section[aria-labelledby='ss-saved-title'] .ss-package")
            .Filter(new() { HasText = "459e58a8ad9e" });
        if (await savedFixture.CountAsync() > 0)
        {
            await savedFixture.First.GetByRole(AriaRole.Button, new() { Name = "Remove saved analysis", Exact = true }).ClickAsync();
        }
        var currentFixture = page.GetByRole(AriaRole.Button, new() { Name = "Remove browser-contract", Exact = true });
        if (await currentFixture.CountAsync() > 0) await currentFixture.First.ClickAsync();
    }

    private static async Task AssertNoHorizontalOverflow(IPage page, int width = 1440)
    {
        await page.SetViewportSizeAsync(width, 1000);
        var overflow = await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth");
        Assert.False(overflow, $"Schema Studio overflows horizontally at {width}px.");
    }

    private async Task AssertAxe(IPage page)
    {
        await page.AddScriptTagAsync(new() { Path = FindAxePath() });
        var violations = await page.EvaluateAsync<JsonElement>(@"async () => {
            const result = await axe.run(document.querySelector('.ss-page'), { runOnly: { type: 'tag', values: ['wcag2a','wcag2aa','wcag21a','wcag21aa'] } });
            return result.violations.map(v => ({ id: v.id, impact: v.impact, help: v.help, targets: v.nodes.map(n => n.target) }));
        }");
        Assert.Equal(JsonValueKind.Array, violations.ValueKind);
        Assert.Empty(violations.EnumerateArray());
    }

    private static string FindAxePath()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "AIAssisted", "browser-companion", "vendor", "axe.min.js");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Repository axe-core bundle was not found.");
    }
}
