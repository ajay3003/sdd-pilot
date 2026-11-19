using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Critical E2E end to end with the REAL Browser Companion extension and the isolated BirkNext, against a stand-in single-page application that
/// the browser serves itself (https://e2e-standin.example.test — never on the network): import a project with Razor UI source → pair the
/// extension (pairing code issued by BirkNext, redeemed by the extension) → the attended page is ready → generate suggestions → review (Approve
/// says what is missing) → Verify DOM in Edge → accept the locator → approve into a Diagnostic flow (shown at once) → run it (the Companion
/// performs the steps on the stand-in) → passed; the stand-in's link changed → run → failed with the observed route. Guide:
/// docs/guides/technical-testing.md.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class CriticalE2ECompanionWorkflowTests
{
    private const string Origin = "https://e2e-standin.example.test";
    // Flows are kept per Target Environment: a fresh environment id per run, so an earlier run's flow never covers this one.
    private readonly string ProfileId = $"e2e-standin-{Guid.NewGuid():N}"[..20];

    private static string ExtensionSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "browser-companion")) && File.Exists(Path.Combine(dir.FullName, "browser-companion", "manifest.json")))
                return Path.Combine(dir.FullName, "browser-companion");
        throw new DirectoryNotFoundException("browser-companion (the Edge Companion extension) was not found above the test output.");
    }

    /// <summary>A project with Razor UI source: /children links to /children/new (the facts the suggestions are generated from).</summary>
    private static byte[] Archive(string name)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry($"{name}/{path}").Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
            Add("src/Web/Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.BlazorWebAssembly\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            Add("src/Web/Pages/Children.razor", "@page \"/children\"\n<PageTitle>Children</PageTitle>\n<h1 data-testid=\"page-title\">Children</h1>\n<a href=\"/children/new\" data-testid=\"new-child\">New child</a>\n");
            Add("src/Web/Pages/NewChild.razor", "@page \"/children/new\"\n<PageTitle>New child</PageTitle>\n<h1 data-testid=\"page-title\">Register child</h1>\n<NavLink href=\"/children\" data-testid=\"back-link\">Back to list</NavLink>\n");
            Add("specs/001-children/spec.md", "# Feature Specification: Children register\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: The system MUST list the registered children.\n");
        }
        return buffer.ToArray();
    }

    /// <summary>The stand-in: one document whose view follows the route (history.pushState), like the Blazor frontend.</summary>
    private static string StandIn(bool broken) => $$"""
        <!doctype html><html lang="en"><head><title>Stand-in</title></head><body><main id="app"></main><script>
          const views = {
            '/children': '<h1 data-testid="page-title">Children</h1><a href="{{(broken ? "/children/old-form" : "/children/new")}}" data-testid="new-child">New child</a>',
            '/children/new': '<h1 data-testid="page-title">Register child</h1><a href="/children" data-testid="back-link">Back to list</a>',
          };
          const render = () => { document.getElementById('app').innerHTML = views[location.pathname] ?? '<h1>Not found</h1>'; };
          document.addEventListener('click', e => { const a = e.target.closest('a'); if (!a) return; e.preventDefault(); history.pushState({}, '', a.getAttribute('href')); setTimeout(render, 150); });
          addEventListener('popstate', render); render();
        </script></body></html>
        """;

    [BrowserFact]
    public async Task Pair_generate_verify_approve_run_and_a_changed_page_fails_with_what_was_observed()
    {
        var name = SyntheticProjects.NewName("PwE2E");
        var profile = Directory.CreateTempSubdirectory("birknext-e2e-companion-").FullName;
        var extension = Path.Combine(profile, "extension");
        CopyDirectory(ExtensionSource(), extension);
        // Headless cannot accept the optional host-permission prompt: pregrant the stand-in origin only; everything else stays as shipped.
        var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(extension, "manifest.json"))).RootElement;
        var hosts = manifest.GetProperty("host_permissions").EnumerateArray().Select(e => e.GetString()!).Append($"{Origin}/*").ToArray();
        var patched = JsonSerializer.Deserialize<Dictionary<string, object>>(manifest.GetRawText())!;
        patched["host_permissions"] = hosts;
        // A generated key fixes the extension id (an unpacked extension's id is derived from its key), so the test opens its pages directly.
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        patched["key"] = Convert.ToBase64String(publicKey);
        var extensionId = string.Concat(System.Security.Cryptography.SHA256.HashData(publicKey).Take(16).SelectMany(b => new[] { (char)('a' + (b >> 4)), (char)('a' + (b & 0xF)) }));
        await File.WriteAllTextAsync(Path.Combine(extension, "manifest.json"), JsonSerializer.Serialize(patched));

        using var playwright = await Playwright.CreateAsync();
        var context = await playwright.Chromium.LaunchPersistentContextAsync(Path.Combine(profile, "user"), new()
        {
            // Extensions need Chromium's new headless mode (the old one, still this Playwright's default, cannot load them).
            Channel = "chromium", Headless = false, ViewportSize = new() { Width = 1440, Height = 950 },
            Args = ["--headless=new", $"--disable-extensions-except={extension}", $"--load-extension={extension}"],
        });
        try
        {
            await BrowserTestServers.ConnectAsync(context);
            var broken = false;
            await context.RouteAsync($"{Origin}/**", route => route.FulfillAsync(new() { ContentType = "text/html", Body = StandIn(broken) }));
            await context.AddInitScriptAsync($$"""
                localStorage.setItem('birknext:frontend-analysis-settings', JSON.stringify({ activeProfileId: '{{ProfileId}}', profiles: [{ id: '{{ProfileId}}',
                  name: 'Stand-in (Development)', environmentType: 'Development', targetUrl: '{{Origin}}/',
                  authentication: { requiresAuthentication: false, authenticationType: 'None' }, integrations: [] }] }));
                """);
            var app = await context.NewPageAsync();
            var pageErrors = new List<string>();
            app.PageError += (_, error) => pageErrors.Add(error);
            app.Dialog += async (_, dialog) => await dialog.AcceptAsync();

            // 1. The project with UI source.
            await SyntheticProjects.ImportAndUseAsync(app, name, Archive(name));

            // 2. Pair the real extension: BirkNext issues a pairing code for the stand-in's profile and origin, the extension redeems it.
            var start = await context.APIRequest.PostAsync($"{BrowserTestServers.BackendUrl}/api/browser-companion/pairing/start", new()
            {
                DataObject = new { profileId = ProfileId, environmentName = "Stand-in (Development)", environmentType = "Development", approvedOrigins = new[] { Origin } },
                Headers = new Dictionary<string, string> { ["Origin"] = BrowserTestServers.FrontendUrl, ["X-BirkNext-Client"] = "playwright-tests" },
            });
            start.Ok.Should().BeTrue(await start.TextAsync());
            var pairingCode = JsonDocument.Parse(await start.TextAsync()).RootElement.GetProperty("pairingCode").GetString();
            var popup = await context.NewPageAsync();
            await popup.GotoAsync($"chrome-extension://{extensionId}/popup.html");
            await popup.EvaluateAsync("backend => chrome.storage.local.set({ backend })", BrowserTestServers.BackendUrl);
            var paired = await popup.EvaluateAsync<JsonElement>("code => chrome.runtime.sendMessage({ type: 'popup:pair', pairingCode: code })", pairingCode);
            paired.GetProperty("state").GetString().Should().Be("connected", paired.GetRawText());

            // 3. The attended page: exactly one approved stand-in tab, on the suggestion's route; a heartbeat soon (the extension's own alarm).
            var target = await context.NewPageAsync();
            await target.GotoAsync($"{Origin}/children");
            await target.WaitForTimeoutAsync(2000);
            await popup.EvaluateAsync("() => chrome.alarms.create('birknext-heartbeat', { delayInMinutes: 0.01, periodInMinutes: 0.1 })");
            await popup.CloseAsync();

            // 4. Critical E2E: the attended browser is ready; generate suggestions from the imported snapshot.
            await app.BringToFrontAsync();
            await app.GotoAsync($"{BrowserTestServers.FrontendUrl}/critical-e2e-regression", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await app.GetByTestId("sg-panel").WaitForAsync(new() { Timeout = 240000 });
            await Assertions.Expect(app.GetByTestId("e2e-attended-state")).ToHaveTextAsync("Ready", new() { Timeout = 60000 });
            var snapshot = (await app.Locator("[data-testid=sg-snapshot] option").EvaluateAllAsync<string[][]>("os => os.map(o => [o.value, o.textContent.trim()])"))
                .First(o => o[1].StartsWith(name.ToLowerInvariant(), StringComparison.Ordinal))[0];
            await app.GetByTestId("sg-snapshot").SelectOptionAsync(snapshot);
            await app.GetByTestId("sg-generate").ClickAsync();
            await app.GetByTestId("sg-summary").WaitForAsync(new() { Timeout = 120000 });
            var item = app.Locator("[data-testid=sg-item]").Filter(new() { HasText = "\"New child\" opens /children/new" });
            await Assertions.Expect(item).ToHaveCountAsync(1);
            await item.GetByTestId("sg-review").ClickAsync();

            // 5. Before verification Approve says what is missing; Verify DOM in Edge checks the locator on the attended page; accept; approve.
            await Assertions.Expect(app.GetByTestId("sg-approve")).ToBeDisabledAsync();
            await Assertions.Expect(app.GetByTestId("sg-approve-why")).ToContainTextAsync("no accepted locator");
            await Assertions.Expect(app.GetByTestId("sg-route-state")).ToContainTextAsync("locators can be verified now");
            await app.GetByTestId("sg-verify").ClickAsync();
            await Assertions.Expect(app.GetByTestId("sg-verification")).ToContainTextAsync("Verified unique", new() { Timeout = 60000 });
            await app.GetByTestId("sg-accept").First.ClickAsync();
            await Assertions.Expect(app.GetByTestId("sg-approve-why")).ToHaveCountAsync(0, new() { Timeout = 30000 });
            static Task<string[]> FlowIdsAsync(IPage page) => page.Locator("[data-testid^=e2e-run-flow-]").EvaluateAllAsync<string[]>("bs => bs.map(b => b.dataset.testid)");
            var before = await FlowIdsAsync(app);
            await app.GetByTestId("sg-approve").ClickAsync();
            await Assertions.Expect(app.GetByTestId("sg-message")).ToContainTextAsync("Approved as the Diagnostic flow", new() { Timeout = 30000 });
            // Shown at once, not hidden among the diagnostic flows: the new flow's Run button is on the page.
            await app.WaitForFunctionAsync("n => document.querySelectorAll('[data-testid^=e2e-run-flow-]').length > n", before.Length, new() { Timeout = 30000 });
            var runId = (await FlowIdsAsync(app)).Except(before).Single();
            var run = app.GetByTestId(runId);

            // 6. Run it: the Companion performs the steps on the stand-in; the journey passes (a diagnostic flow is never release evidence).
            await run.ClickAsync();
            var lastRun = app.Locator("[data-testid^=e2e-last-run]").First;
            await Assertions.Expect(lastRun).ToContainTextAsync("Diagnostic pass", new() { Timeout = 120000 });
            await Assertions.Expect(lastRun).ToContainTextAsync("never release evidence");

            // 7. The application changes: the link now leads elsewhere. The same flow fails, with what was observed.
            broken = true;
            await target.GotoAsync($"{Origin}/children");
            await app.BringToFrontAsync();
            await run.ClickAsync();
            await Assertions.Expect(lastRun).ToContainTextAsync("Failed", new() { Timeout = 120000 });
            await Assertions.Expect(lastRun).ToContainTextAsync("observed /children/old-form");
            pageErrors.Should().BeEmpty();
        }
        finally
        {
            await context.CloseAsync();
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source).Where(d => Path.GetFileName(d) is not ("dist" or "tests" or "node_modules")))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
