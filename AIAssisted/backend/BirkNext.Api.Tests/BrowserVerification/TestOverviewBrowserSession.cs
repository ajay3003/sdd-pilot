using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Playwright;
using F = BirkNext.Api.Tests.BrowserVerification.TestOverviewFixture;

namespace BirkNext.Api.Tests.BrowserVerification;

/// <summary>
/// Browser steps for the Testoversikt journeys: import through the real Project Import page, choose scope through the real dropdowns, and read what
/// the page shows. Reads are by stable <c>data-testid</c> hooks; scenario identities are the fixture tokens in the visible text.
/// </summary>
public sealed partial class TestOverviewBrowserSession(IsolatedBrowserHost host, IPage page)
{
    public IPage Page => page;
    public List<string> ConsoleErrors { get; } = [];
    public List<string> FailedRequests { get; } = [];

    public static async Task<TestOverviewBrowserSession> OpenAsync(IsolatedBrowserHost host, IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        var session = new TestOverviewBrowserSession(host, page);
        page.Console += (_, m) => { if (m.Type == "error") lock (session.ConsoleErrors) session.ConsoleErrors.Add(m.Text); };
        page.PageError += (_, e) => { lock (session.ConsoleErrors) session.ConsoleErrors.Add("pageerror: " + e); };
        page.Response += (_, r) => { if (r.Status >= 500) lock (session.FailedRequests) session.FailedRequests.Add($"{r.Status} {r.Url}"); };
        return session;
    }

    [GeneratedRegex(@"\b(?:AUR|BOR|CIR|DOR)-[A-Z0-9]+-(?:US\d+-)?S\d+\b|\bAUR-SHARED-S1\b")]
    private static partial Regex TokenRe();

    public static IEnumerable<string> Tokens(string text) => TokenRe().Matches(text).Select(m => m.Value);

    // ------------------------------------------------------------------ import (the supported user path)

    /// <summary>Imports <paramref name="app"/> through Project Import as a new application and makes it current (confirmed after a reload).</summary>
    public async Task ImportAsync(F.Application app, bool asNewVersion = false)
    {
        await page.GotoAsync($"{host.WebUrl}/project-import?import=source", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
        var form = page.GetByTestId("pi-choose");
        var header = page.GetByTestId("pi-header-import");
        // The import form may open by itself while the page settles (the header button then disappears): poll, click the header only while shown.
        for (var attempt = 0; attempt < 120 && await form.CountAsync() == 0; attempt++)
        {
            if (await header.CountAsync() > 0 && await header.IsVisibleAsync())
                try { await header.ClickAsync(new() { Timeout = 2000 }); } catch (TimeoutException) { }
            await page.WaitForTimeoutAsync(1000);
        }
        await form.WaitForAsync(new() { Timeout = 30000 });
        var detect = page.GetByTestId("pi-zip-detect");
        await detect.WaitForAsync(new() { Timeout = 60000 });
        if (!await detect.IsCheckedAsync() && await page.Locator("[data-testid=pi-zip-role]:checked").CountAsync() == 0) await detect.CheckAsync();
        var target = page.GetByTestId("pi-target-project");
        if (await target.CountAsync() > 0)
        {
            if (asNewVersion) await SelectByTextAsync(target, app.Name);
            else await target.SelectOptionAsync("new");
        }
        var chooser = page.GetByTestId("pi-choose-file");
        await chooser.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60000 });
        await chooser.SetInputFilesAsync(new FilePayload { Name = $"{app.Name}.zip", MimeType = "application/zip", Buffer = F.Archive(app) });
        await page.GetByTestId("pi-preview").WaitForAsync(new() { Timeout = 120000 });
        var again = page.GetByTestId("pi-duplicate-import-again");
        if (await again.CountAsync() > 0) await again.ClickAsync();
        // An archive with source gets the server's application choice; «Ny versjon» must then be offered (never silently a new application).
        if (asNewVersion && app.WithSource) await page.GetByTestId("pi-app-version").WaitForAsync(new() { Timeout = 60000 });
        if (asNewVersion && await page.GetByTestId("pi-app-version").CountAsync() > 0)
        {
            // «Ny versjon av eksisterende applikasjon», the application by name, and «Bruk denne versjonen som gjeldende kildegrunnlag».
            await page.GetByTestId("pi-app-version").CheckAsync();
            await SelectByTextAsync(page.GetByTestId("pi-app-select"), app.Name);
            var current = page.GetByTestId("pi-app-current");
            if (!await current.IsCheckedAsync()) await current.CheckAsync();
            await Assertions.Expect(page.GetByTestId("pi-app-chosen")).ToContainTextAsync("ny versjon", new() { Timeout = 30000 });
        }
        else if (await page.GetByTestId("pi-app-new").CountAsync() > 0)
        {
            await page.GetByTestId("pi-app-new").CheckAsync();
            await page.GetByTestId("pi-app-chosen").WaitForAsync(new() { Timeout = 30000 });
        }
        try { await Assertions.Expect(page.GetByTestId("pi-import")).ToBeEnabledAsync(new() { Timeout = 60000 }); }
        catch (PlaywrightException)
        {
            Directory.CreateDirectory(host.EvidenceDirectory);
            await page.ScreenshotAsync(new() { Path = Path.Combine(host.EvidenceDirectory, $"import-{app.Name}-disabled.png"), FullPage = true });
            var text = await page.Locator("main").First.InnerTextAsync();
            throw new InvalidOperationException($"Import of {app.Name} cannot start (button disabled). Page: {text[..Math.Min(text.Length, 4000)]}");
        }
        await page.GetByTestId("pi-import").ClickAsync();
        var result = page.GetByTestId("pi-result");
        await result.WaitForAsync(new() { Timeout = 300000 });
        if (await result.GetAttributeAsync("data-outcome") is "failed" or "rejected")
            throw new InvalidOperationException($"Fixture {app.Name} was not imported: {await result.InnerTextAsync()}");
        await UseAsync(app);
    }

    /// <summary>
    /// A project or package choice is saved by the workspace auto-save (an HTTP call after the click). A full page load before that call finished
    /// restored the previous choice (DEF-V12-01 investigation, 2026-11-28: STALE-01 seeded the previous project; «show the older import» never
    /// stuck). Waits for the save response; a choice that needs no save (already current) is not waited for longer than the timeout.
    /// </summary>
    private Task ClickAndWaitForSaveAsync(ILocator target) => SaveAfterAsync(() => target.ClickAsync());

    private async Task SaveAfterAsync(Func<Task> action)
    {
        try
        {
            await page.RunAndWaitForResponseAsync(action, r => r.Request.Method != "GET" && r.Url.Contains("api/workspace-persistence/", StringComparison.Ordinal)
                && (r.Url.Contains("auto-save", StringComparison.Ordinal) || r.Url.Contains("save-current", StringComparison.Ordinal)), new() { Timeout = 30000 });
        }
        catch (TimeoutException) { }
    }

    /// <summary>Makes an already imported application current through the shared project selector (the switch every page uses).</summary>
    public async Task UseAsync(F.Application app)
    {
        await page.GotoAsync($"{host.WebUrl}/project-import", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
        var current = page.Locator("[data-testid=pi-current-name]", new() { HasTextString = app.Name });
        var use = page.Locator($"[data-testid=pi-previous-use][aria-label=\"Show {app.Name}'s documents\"]");
        var selector = page.Locator("[data-testid=project-selector-select]");
        // The page renders its parts one after another: wait until one of the three ways is there, then use the first that applies.
        for (var attempt = 0; attempt < 120; attempt++)
        {
            if (await current.CountAsync() > 0) break;
            if (await use.CountAsync() > 0) { await ClickAndWaitForSaveAsync(use.First); break; }
            if (await selector.CountAsync() > 0) { await SaveAfterAsync(() => SelectByTextAsync(selector.First, app.Name)); break; }
            await page.WaitForTimeoutAsync(1000);
        }
        await Assertions.Expect(page.GetByTestId("pi-current-name").Or(page.GetByTestId("pi-application-name")).First).ToContainTextAsync(app.Name, new() { Timeout = 120000 });
    }

    // ------------------------------------------------------------------ Testoversikt

    public async Task OpenOverviewAsync()
    {
        await page.GotoAsync($"{host.WebUrl}/manual-testing", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
        await WaitForFiguresAsync();
    }

    /// <summary>
    /// Shows one import of an application in the workspace (Project Import → «Show this package»): the NEWEST one (Testoversikt then says nothing
    /// about a newer import) or an OLDER one (it does). Import rows differ only by a minute-resolution date, so the row is chosen by what the page
    /// then shows, never by its position. Throws when no row gives the wanted state.
    /// </summary>
    public async Task UseImportAsync(string application, bool newest)
    {
        var seen = new List<string>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await page.GotoAsync($"{host.WebUrl}/project-import", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            var buttons = page.Locator($"[data-testid=pi-previous-use][aria-label=\"Show {application}'s documents\"]");
            await buttons.First.WaitForAsync(new() { Timeout = 120000 });
            var count = await buttons.CountAsync();
            if (count == 0) continue; // the list re-rendered between the wait and the count
            await ClickAndWaitForSaveAsync(buttons.Nth(attempt % count));
            await OpenOverviewAsync();
            var stale = await page.GetByTestId("mto-source-stale").CountAsync() > 0;
            if (stale != newest) return;
            seen.Add($"attempt {attempt}: {count} button(s), clicked #{attempt % count}, shown «{(await page.GetByTestId("mto-source-text").CountAsync() > 0 ? await page.GetByTestId("mto-source-text").InnerTextAsync() : "?")}», stale={stale}");
        }
        throw new InvalidOperationException($"No import of {application} could be shown as the {(newest ? "newest" : "older")} one: {string.Join(" | ", seen)}");
    }

    public async Task WaitForFiguresAsync()
    {
        var kpis = page.GetByTestId("mto-kpis").Or(page.GetByTestId("mto-scope-invalid")).Or(page.GetByTestId("mto-error")).Or(page.GetByTestId("mto-scope-error"));
        await kpis.First.WaitForAsync(new() { Timeout = 180000 });
        await Assertions.Expect(page.GetByTestId("mto-loading")).ToHaveCountAsync(0, new() { Timeout = 120000 });
        await Assertions.Expect(page.GetByTestId("ssp-busy")).ToHaveCountAsync(0, new() { Timeout = 120000 });
        if (await page.GetByTestId("mto-error").CountAsync() > 0) throw new InvalidOperationException("Testoversikt error: " + await page.GetByTestId("mto-error").InnerTextAsync());
    }

    /// <summary>Opens the picker when it is collapsed (one line once a specification is in use).</summary>
    private async Task OpenPickerAsync()
    {
        var details = page.GetByTestId("ssp-choose");
        if (await details.CountAsync() > 0 && await details.GetAttributeAsync("open") is null) await details.Locator("summary").ClickAsync();
    }

    /// <summary>Chooses the module by its option value (the module key; «(project-level)» for the project level; null = «Alle moduler»).</summary>
    public async Task SelectModuleAsync(string? moduleKey, bool wait = true)
    {
        await OpenPickerAsync();
        var select = page.GetByTestId("ssp-module");
        await select.SelectOptionAsync(moduleKey is null ? "(all-modules)" : moduleKey.Length == 0 ? "(project-level)" : moduleKey);
        if (wait) await WaitForFiguresAsync();
    }

    public async Task SelectPackAsync(string? pack)
    {
        await OpenPickerAsync();
        var select = page.GetByTestId("ssp-feature");
        await select.SelectOptionAsync(pack is null ? "" : await OptionValueAsync(select, o => o.Value == pack || o.Text.Contains($"({pack})", StringComparison.Ordinal) || o.Text == pack));
        await WaitForFiguresAsync();
    }

    public async Task SelectDocumentAsync(string? sourcePath)
    {
        await OpenPickerAsync();
        var select = page.GetByTestId("ssp-specification");
        await select.SelectOptionAsync(sourcePath is null ? "" : await OptionValueAsync(select, o => o.Text.Contains(sourcePath, StringComparison.OrdinalIgnoreCase)));
        await WaitForFiguresAsync();
    }

    public async Task<IReadOnlyList<(string Value, string Text)>> OptionsAsync(string testId)
    {
        var select = page.GetByTestId(testId);
        if (await select.CountAsync() == 0) return [];
        var raw = await select.EvaluateAsync<string[][]>("s => [...s.options].map(o => [o.value, o.textContent.trim()])");
        return [.. raw.Select(r => (r[0], r[1]))];
    }

    public async Task<string?> SelectedTextAsync(string testId)
    {
        var select = page.GetByTestId(testId);
        if (await select.CountAsync() == 0) return null;
        return await select.EvaluateAsync<string>("s => s.options[s.selectedIndex]?.textContent.trim() ?? ''");
    }

    private static async Task<string> OptionValueAsync(ILocator select, Func<(string Value, string Text), bool> match)
    {
        var raw = await select.EvaluateAsync<string[][]>("s => [...s.options].map(o => [o.value, o.textContent.trim()])");
        var hit = raw.Select(r => (Value: r[0], Text: r[1])).Where(match).ToList();
        if (hit.Count != 1) throw new InvalidOperationException($"Expected one matching option, found {hit.Count}: {string.Join(" | ", raw.Select(r => r[1]))}");
        return hit[0].Value;
    }

    private static async Task SelectByTextAsync(ILocator select, string startsWith) =>
        await select.SelectOptionAsync(await OptionValueAsync(select, o => o.Text.StartsWith(startsWith, StringComparison.Ordinal) && (o.Text.Length == startsWith.Length || !char.IsLetterOrDigit(o.Text[startsWith.Length]))));

    public async Task<int?> KpiAsync(string widget)
    {
        var value = page.Locator($"[data-testid=mto-kpi][data-widget='{widget}'] .mto-kpi-v");
        if (await value.CountAsync() == 0) return null;
        var text = (await value.First.InnerTextAsync()).Replace(" ", "").Replace(" ", "").Trim();
        return int.TryParse(text, out var n) ? n : null;
    }

    /// <summary>The module/specification/story table: row label → (scenarios, requirements without scenario).</summary>
    public async Task<Dictionary<string, (int? Scenarios, int? Requirements, string Key)>> RowsAsync()
    {
        var rows = page.GetByTestId("mto-row");
        var result = new Dictionary<string, (int?, int?, string)>(StringComparer.Ordinal);
        for (var i = 0; i < await rows.CountAsync(); i++)
        {
            var row = rows.Nth(i);
            var label = (await row.Locator(".mto-row-open, .mto-row-title").First.InnerTextAsync()).Trim();
            static int? N(string t) => int.TryParse(t.Trim(), out var n) ? n : null;
            result[label] = (N(await row.GetByTestId("mto-row-scenarios").InnerTextAsync()), N(await row.GetByTestId("mto-row-requirements").InnerTextAsync()),
                await row.GetAttributeAsync("data-key") ?? "");
        }
        return result;
    }

    /// <summary>Opens a bar of one chart (<c>chart.priority</c>, <c>chart.coverageGaps</c>, …) and returns its drill panel.</summary>
    public async Task<ILocator> OpenBarAsync(string chart, string key)
    {
        await page.Locator($"[data-widget='{chart}'] [data-testid=mto-bar][data-key='{key}'] [data-testid=mto-bar-open]").First.ClickAsync();
        return await DrillAsync(chart, key);
    }

    /// <summary>The value a chart shows for one bar, or null when the bar is not shown.</summary>
    public async Task<int?> BarValueAsync(string chart, string key)
    {
        var bar = page.Locator($"[data-widget='{chart}'] [data-testid=mto-bar][data-key='{key}']");
        if (await bar.CountAsync() == 0) return null;
        var label = await bar.Locator("[data-testid=mto-bar-open]").First.GetAttributeAsync("aria-label") ?? "";
        var m = Regex.Match(label, @":\s*(\d+)\.");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>Opens a «Viktig å følge opp» line (<c>p1-uncovered</c>, …) and returns its list.</summary>
    public async Task<ILocator> OpenAttentionAsync(string key)
    {
        await page.Locator($"li[data-key='{key}'] [data-testid=mto-attention-open]").ClickAsync();
        return await DrillAsync("panel.p1Attention", key);
    }

    public async Task<ILocator> DrillAsync(string chart, string key)
    {
        var drill = page.Locator($"[data-testid=mto-drill][data-chart='{chart}'][data-key='{key}']");
        await drill.WaitForAsync(new() { Timeout = 30000 });
        return drill;
    }

    /// <summary>Clicks a follow-up line that opens Finn og forstå and returns what that page shows for the hand-over.</summary>
    public async Task<DiscoverResult> FollowUpToDiscoverAsync(string key)
    {
        var line = page.Locator($"li[data-key='{key}'] [data-testid=mto-attention-open]");
        (await line.GetAttributeAsync("data-destination")).Should().Be("discover", "a scenario follow-up line opens Finn og forstå, not an inline list");
        await line.ClickAsync();
        return await ReadDiscoverAsync();
    }

    /// <summary>Finn og forstå after a Testoversikt hand-over: the selection banner (found / wanted / missing) and the fixture tokens of every listed
    /// scenario (groups expanded, «vis flere» pressed), with multiplicity — the same token in two documents is two rows.</summary>
    public async Task<DiscoverResult> ReadDiscoverAsync()
    {
        await page.WaitForURLAsync(u => u.Contains("/manual-testing/discover", StringComparison.Ordinal), new() { Timeout = 60000, WaitUntil = WaitUntilState.Commit });
        var banner = page.GetByTestId("mtd-overview-selection");
        await banner.WaitForAsync(new() { Timeout = 180000 });
        await Assertions.Expect(page.GetByTestId("mtd-loading")).ToHaveCountAsync(0, new() { Timeout = 180000 });
        var expand = page.GetByTestId("mtd-groups-expand");
        // A single opened scenario shows its detail drawer over the list: the list controls are pressed directly (not through the drawer).
        if (await expand.CountAsync() > 0 && await expand.First.IsVisibleAsync()) await expand.First.DispatchEventAsync("click");
        for (var i = 0; i < 40; i++)
        {
            var more = page.Locator("[data-testid=mtd-more] button, button[data-testid=mtd-more], [data-testid=mtd-sgroup-more] button, button[data-testid=mtd-sgroup-more]");
            var visible = false;
            for (var m = 0; m < await more.CountAsync(); m++) if (await more.Nth(m).IsVisibleAsync()) { await more.Nth(m).DispatchEventAsync("click"); visible = true; break; }
            if (!visible) break;
        }
        var text = await banner.InnerTextAsync();
        var counts = Regex.Match(text, @"(\d+) av (\d+) scenario");
        var rows = await page.GetByTestId("mtd-scenario").AllInnerTextsAsync();
        return new(int.Parse(counts.Groups[1].Value), int.Parse(counts.Groups[2].Value), await page.GetByTestId("mtd-overview-selection-missing").CountAsync() > 0,
            [.. rows.SelectMany(Tokens)], page.Url);
    }

    /// <summary>Planlegg og klargjør after a Testoversikt case hand-over: every (plan, case) the selection shows, visiting each named plan.</summary>
    public async Task<PlanResult> ReadPlanSelectionAsync()
    {
        await page.WaitForURLAsync(u => u.Contains("/manual-testing/plan?draft=", StringComparison.Ordinal), new() { Timeout = 60000, WaitUntil = WaitUntilState.Commit });
        var banner = page.GetByTestId("mtp-overview-selection");
        await banner.WaitForAsync(new() { Timeout = 180000 });
        var total = int.Parse(await banner.GetAttributeAsync("data-total") ?? "0");
        var seen = new HashSet<(Guid, string)>();
        var missing = false;
        var visited = new HashSet<Guid>();
        for (var hop = 0; hop < 10; hop++)
        {
            var planId = Guid.Parse(await page.GetByTestId("mtp-plan-select").CountAsync() > 0
                ? await page.GetByTestId("mtp-plan-select").InputValueAsync()
                : Regex.Match(page.Url, @"draft=([0-9a-f-]{36})").Groups[1].Value);
            visited.Add(planId);
            await Assertions.Expect(page.GetByTestId("mtp-loading")).ToHaveCountAsync(0, new() { Timeout = 60000 });
            foreach (var id in await page.Locator("[data-testid=mtp-case]").EvaluateAllAsync<string[]>("els => els.map(e => e.dataset.case)")) seen.Add((planId, id));
            missing |= await page.GetByTestId("mtp-overview-selection-missing").CountAsync() > 0;
            ILocator? target = null;
            foreach (var n in await page.GetByTestId("mtp-overview-selection-plan").AllAsync())
                if (!visited.Contains(Guid.Parse(await n.GetAttributeAsync("data-plan") ?? Guid.Empty.ToString()))) { target = n; break; }
            if (target is null) break;
            var nextPlan = (await target.GetAttributeAsync("data-plan"))!;
            await target.ClickAsync();
            await Assertions.Expect(page.GetByTestId("mtp-plan-select")).ToHaveValueAsync(nextPlan, new() { Timeout = 60000 });
        }
        return new(total, missing, seen);
    }

    public sealed record PlanResult(int Total, bool Missing, IReadOnlySet<(Guid PlanId, string CaseId)> Cases);

    public sealed record DiscoverResult(int Found, int Wanted, bool Missing, IReadOnlyList<string> Tokens, string Url);

    public static async Task<int> DrillCountAsync(ILocator drill) =>
        int.Parse(Regex.Match(await drill.GetByTestId("mto-drill-count").First.InnerTextAsync(), @"\d+").Value);

    /// <summary>The oracle identities a drill panel lists: every group (data-spec) expanded and every «vis flere» pressed, every page visited.</summary>
    public async Task<HashSet<F.Key>> DrillIdentitiesAsync(ILocator drill)
    {
        var keys = new HashSet<F.Key>();
        for (var pageIndex = 0; pageIndex < 50; pageIndex++)
        {
            var groups = drill.GetByTestId("mto-drill-group");
            for (var g = 0; g < await groups.CountAsync(); g++)
            {
                var group = groups.Nth(g);
                var toggle = group.GetByTestId("mto-follow-toggle");
                if (await toggle.CountAsync() > 0 && await toggle.GetAttributeAsync("aria-expanded") != "true") await toggle.ClickAsync();
                for (var more = 0; more < 20; more++)
                {
                    var showMore = group.Locator("[data-testid=mto-follow-more] button, button[data-testid=mto-follow-more]");
                    if (await showMore.CountAsync() == 0 || !await showMore.First.IsVisibleAsync()) break;
                    await showMore.First.ClickAsync();
                }
                var spec = await group.GetAttributeAsync("data-spec") ?? "";
                var rows = group.GetByTestId("mto-drill-row");
                for (var r = 0; r < await rows.CountAsync(); r++)
                    foreach (var token in Tokens(await rows.Nth(r).InnerTextAsync())) keys.Add(new(Normalize(spec), token));
            }
            var next = drill.GetByTestId("mto-follow-next");
            if (await next.CountAsync() == 0 || await next.IsDisabledAsync()) break;
            await next.ClickAsync();
        }
        return keys;
    }

    /// <summary>A document path as the fixture names it (relative to the application folder, forward slashes).</summary>
    public static string Normalize(string path)
    {
        var p = path.Replace('\\', '/').TrimStart('/');
        foreach (var app in new[] { "Aurora/", "Borealis/", "Cirrus/", "Dorado/" }) if (p.StartsWith(app, StringComparison.Ordinal)) return p[app.Length..];
        return p;
    }
}
