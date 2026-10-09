using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.MarkdownDiagnostics;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.SampleProjects;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Opt-in full browser acceptance. Uses the production Project Import flow and real Explorer routes;
/// run with RunRealM2lbRenderAcceptance=true and the local archive path in BIRKNEXT_M2LB_ARCHIVE.
/// </summary>
public sealed class RealM2lbRenderAcceptance
{
    private const string ExpectedArchiveSha256 = "C850A1B2813BBF6E2A9EBA1F311D63FF0332EACF35764CD6B767489AC22DC41E";
    private static readonly (WorkspaceArtifactType Role, string Label, string Route)[] Roles =
    [
        (WorkspaceArtifactType.Specification, "Specification", "/specification-explorer"),
        (WorkspaceArtifactType.Constitution, "Constitution", "/constitution-explorer"),
        (WorkspaceArtifactType.Plan, "Plan", "/plan-explorer"),
        (WorkspaceArtifactType.Tasks, "Tasks", "/task-explorer"),
        (WorkspaceArtifactType.DataModel, "Data Model", "/data-model-explorer")
    ];

    [Fact]
    public async Task Real_M2LB_documents_render_their_page_model_projection_ids_at_all_breakpoints()
    {
        var archivePath = Environment.GetEnvironmentVariable("BIRKNEXT_M2LB_ARCHIVE");
        var frontendUrl = Environment.GetEnvironmentVariable("BIRKNEXT_ACCEPTANCE_FRONTEND_URL") ?? "http://localhost:5174";
        var backendUrl = Environment.GetEnvironmentVariable("BIRKNEXT_ACCEPTANCE_BACKEND_URL") ?? "http://localhost:5001";
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            throw new FileNotFoundException("Set BIRKNEXT_M2LB_ARCHIVE to the existing real acceptance archive.");

        using var playwright = await Playwright.CreateAsync();
        var archiveBytes = await File.ReadAllBytesAsync(archivePath);
        Convert.ToHexString(SHA256.HashData(archiveBytes)).Should().Be(ExpectedArchiveSha256);
        var candidates = ReadM2lbDocuments(archiveBytes);
        using var http = new HttpClient { BaseAddress = new Uri(backendUrl) };
        using var response = await http.PostAsync("api/system-diagnostics/markdown/explorer-coverage/run", content: null);
        response.EnsureSuccessStatusCode();
        var coverage = await response.Content.ReadFromJsonAsync<ExplorerCoverageRun>()
            ?? throw new InvalidOperationException("Explorer Text Coverage returned no result.");
        coverage.RealProjectFixtureUsed.Should().BeTrue("the configured production archive must be used");

        // The archive contains reusable .specify/templates documents as well as project-owned artifacts.
        // Only documents represented by the production archive-scoped diagnostic are eligible for rendering checks.
        var archiveDocumentIds = coverage.Documents
            .Where(document => document.SourceOrigin == "ConfiguredArchive")
            .Select(document => (document.ArtifactRole, document.ExpectedDocumentId))
            .ToHashSet();
        candidates = candidates.Where(candidate => archiveDocumentIds.Contains((candidate.RoleLabel, candidate.DocumentId))).ToList();
        candidates.Should().NotBeEmpty("the selected render samples must come from archive-owned documents");

        foreach (var pick in candidates)
        {
            var matchingCoverageDocuments = coverage.Documents.Where(document =>
                document.ArtifactRole == pick.RoleLabel && document.ExpectedDocumentId == pick.DocumentId).ToArray();
            matchingCoverageDocuments.Should().NotBeEmpty();
            var projectionSets = matchingCoverageDocuments.Select(document => document.Blocks
                .SelectMany(block => block.ProjectionIds ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
                .ToArray();
            projectionSets.Should().OnlyContain(ids => ids.SequenceEqual(projectionSets[0], StringComparer.Ordinal),
                "identical document content in one role must produce identical deterministic projection IDs");
            pick.Coverage = matchingCoverageDocuments[0];
            pick.ProjectionIds = pick.Coverage.Blocks
                .Where(block => block.Classification is CoverageClassification.RepresentedDirectly or CoverageClassification.RepresentedStructurally)
                .SelectMany(block => block.ProjectionIds ?? [])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        var picks = candidates.GroupBy(candidate => candidate.Role)
            .Select(roleGroup => roleGroup.GroupBy(candidate => candidate.DocumentId, StringComparer.Ordinal)
                .Select(documents => documents.First())
                .Where(candidate => candidate.ProjectionScore > 0)
                .OrderBy(candidate => candidate.ProjectionScore)
                .First())
            .ToList();
        picks.Should().HaveCount(5);
        picks.Should().OnlyContain(pick => pick.ProjectionIds.Length > 0,
            "the deterministic real-document selection must exercise at least one supported structured projection per role");
        foreach (var pick in picks)
            Console.WriteLine($"Selected {pick.RoleLabel} M2LB document identity={pick.DocumentId[..12]}, expected projections={pick.ProjectionIds.Length}");
        var roleFilter = Environment.GetEnvironmentVariable("BIRKNEXT_ACCEPTANCE_ROLE");
        var browserPicks = string.IsNullOrWhiteSpace(roleFilter)
            ? picks
            : picks.Where(pick => string.Equals(pick.RoleLabel, roleFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        browserPicks.Should().NotBeEmpty("the requested browser role must be among the selected real M2LB documents");

        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var page = await context.NewPageAsync();
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();

        // No browser storage is reused: the workspace is established only by this production import flow.
        await page.GotoAsync(new Uri(new Uri(frontendUrl), "/project-import").ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        await page.Locator("[data-testid='pi-choose-file']").SetInputFilesAsync(archivePath);
        await page.Locator("[data-testid='pi-preview']").WaitForAsync(new() { Timeout = 180000 });
        string.Equals(await page.Locator("[data-testid='pi-fingerprint']").GetAttributeAsync("title"), ExpectedArchiveSha256,
            StringComparison.OrdinalIgnoreCase).Should().BeTrue("the actual import preview must show the expected archive fingerprint");
        await page.GetByRole(AriaRole.Button, new() { Name = "Import project", Exact = true }).ClickAsync();
        await page.Locator("[data-testid='pi-result']").WaitForAsync(new() { Timeout = 600000 });

        var axePath = FindAxePath();
        var results = new List<(M2lbPick Pick, ProjectionRenderVerification Evidence)>();
        foreach (var pick in browserPicks)
        {
            foreach (var width in new[] { 1440, 1100, 768, 390 })
            {
                await page.SetViewportSizeAsync(width, 1000);
                var evidence = await SelectAndVerifyAsync(page, frontendUrl, pick);
                if (evidence.DuplicateProjectionIds.Count > 0)
                {
                    var duplicateDetails = await page.Locator("[data-birknext-projection-id]").EvaluateAllAsync<string[]>(
                        "els => els.filter(el => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden').map(el => ({id: el.getAttribute('data-birknext-projection-id'), tag: el.tagName, className: el.className?.toString() || '', panel: el.closest('[role=tabpanel]')?.getAttribute('aria-label') || el.closest('[role=tabpanel]')?.id || ''})).map(x => `${x.id}|${x.tag}|${x.className}|${x.panel}`)");
                    Console.WriteLine($"{pick.RoleLabel} visible projection DOM at {width}: {string.Join(";", duplicateDetails.Where(detail => evidence.DuplicateProjectionIds.Any(id => detail.StartsWith(id + "|", StringComparison.Ordinal))))}");
                }
                evidence.ExecutionStatus.Should().Be(ProjectionRenderExecutionStatus.Completed,
                    $"{pick.RoleLabel} at {width}px must have matching document identity before projection comparison; {evidence.UnavailableReason}");
                evidence.ActualDocumentId.Should().Be(pick.DocumentId);
                evidence.MissingProjectionIds.Should().BeEmpty($"{pick.RoleLabel} at {width}px");
                if (evidence.DuplicateProjectionIds.Count > 0)
                {
                    pick.RoleLabel.Should().Be("Constitution",
                        "only Constitution's relationship map intentionally repeats a rule under each parent that references it");
                    var duplicateDetails = await page.Locator("[data-birknext-projection-id]").EvaluateAllAsync<string[]>(
                        "els => els.filter(el => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden').map(el => ({id: el.getAttribute('data-birknext-projection-id'), className: el.className?.toString() || ''})).map(x => `${x.id}|${x.className}`)");
                    foreach (var duplicateId in evidence.DuplicateProjectionIds)
                    {
                        var occurrences = duplicateDetails.Where(detail => detail.StartsWith(duplicateId + "|", StringComparison.Ordinal)).ToArray();
                        occurrences.Should().HaveCountGreaterThan(1);
                        occurrences.Should().OnlyContain(detail => detail.Contains("ce-map-node", StringComparison.Ordinal),
                            "only the Constitution relationship map repeats a logical rule for multiple parent links");
                    }
                    Console.WriteLine($"{pick.RoleLabel}: {evidence.DuplicateProjectionIds.Count} repeated relationship-map projection IDs are intentional multi-parent occurrences");
                }
                var overflow = await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > window.innerWidth");
                overflow.Should().BeFalse($"{pick.RoleLabel} overflows at {width}px");
                if (width == 1440) results.Add((pick, evidence));
            }

            await page.SetViewportSizeAsync(1440, 1000);
            await page.AddScriptTagAsync(new() { Path = axePath });
            var axeComparison = await page.EvaluateAsync<string>(@"async () => {
                const root = document.querySelector('[data-testid=artifact-explorer]');
                const options = { runOnly: { type: 'tag', values: ['wcag2a','wcag2aa','wcag21a','wcag21aa'] } };
                const summarize = async () => {
                  const result = await axe.run(root, options);
                  return result.violations.map(v => `${v.id}:${v.impact}:${v.nodes.length}`).sort();
                };
                const withHooks = await summarize();
                const elements = [root, ...root.querySelectorAll('*')];
                const saved = elements.map(el => [el, [...el.attributes].filter(a => a.name.startsWith('data-birknext-')).map(a => [a.name, a.value])]);
                for (const [el, attrs] of saved) for (const [name] of attrs) el.removeAttribute(name);
                const withoutHooks = await summarize();
                for (const [el, attrs] of saved) for (const [name, value] of attrs) el.setAttribute(name, value);
                return JSON.stringify({ withHooks, withoutHooks });
            }");
            using var axeResult = JsonDocument.Parse(axeComparison);
            var violations = axeResult.RootElement.GetProperty("withHooks").EnumerateArray().Select(item => item.GetString()!).ToArray();
            var baselineViolations = axeResult.RootElement.GetProperty("withoutHooks").EnumerateArray().Select(item => item.GetString()!).ToArray();
            violations.Should().Equal(baselineViolations,
                $"adding diagnostic identity attributes must not introduce A/AA violations for {pick.RoleLabel}");
            Console.WriteLine($"axe {pick.RoleLabel}: {violations.Length} baseline A/AA node findings; no additional findings with DOM identity hooks");
            await VerifyKeyboardFocusAsync(page, pick);
        }

        // Re-run the production source accounting after browser verification; render evidence never changes source classifications.
        using var afterResponse = await http.PostAsync("api/system-diagnostics/markdown/explorer-coverage/run", content: null);
        afterResponse.EnsureSuccessStatusCode();
        var after = await afterResponse.Content.ReadFromJsonAsync<ExplorerCoverageRun>()
            ?? throw new InvalidOperationException("The source coverage regression run returned no result.");
        var fingerprintsByRole = candidates.Select(candidate => (Role: candidate.RoleLabel, candidate.DocumentId)).ToHashSet();
        var realDocuments = after.Documents.Where(document => fingerprintsByRole.Contains((document.ArtifactRole, document.ExpectedDocumentId ?? ""))).ToArray();
        realDocuments.Should().HaveCount(149);
        realDocuments.Sum(document => document.SourceBlockCount).Should().Be(35_850);
        realDocuments.Sum(document => document.RepresentedDirectlyCount).Should().Be(24_635);
        realDocuments.Sum(document => document.RepresentedStructurallyCount).Should().Be(9_921);
        realDocuments.Sum(document => document.IntentionallyIgnoredCount).Should().Be(1_294);
        realDocuments.Sum(document => document.UnsupportedCount).Should().Be(0);
        realDocuments.Sum(document => document.MissingCount).Should().Be(0);
        realDocuments.Should().OnlyContain(document => document.MissingCount == 0);

        // Keep the output safe: no source content or full relative artifact paths are reported.
        foreach (var (pick, evidence) in results)
        {
            var withRenderEvidence = ExplorerCoverageRenderEvidence.Apply(pick.Coverage!, evidence);
            var structuredBlocks = withRenderEvidence.Blocks.Where(block => block.Classification == CoverageClassification.RepresentedStructurally).ToArray();
            var verifiedBlocks = structuredBlocks.Count(block => block.RenderEvidence == CoverageEvidenceStatus.Present);
            var missingBlocks = structuredBlocks.Count(block => block.RenderEvidence == CoverageEvidenceStatus.Absent);
            missingBlocks.Should().Be(0, $"{pick.RoleLabel} source blocks must retain complete browser-backed projection evidence");
            withRenderEvidence.Blocks.Where(block => block.Classification == CoverageClassification.RepresentedDirectly)
                .Should().OnlyContain(block => block.RenderEvidence == CoverageEvidenceStatus.NotVerified,
                    "Direct source blocks have no block-level DOM identity in this milestone");
            Console.WriteLine($"{pick.RoleLabel}: document={pick.DocumentId[..12]}, expected={evidence.ExpectedProjectionIds.Count}, found={evidence.FoundProjectionIds.Count}, missing={evidence.MissingProjectionIds.Count}, duplicates={evidence.DuplicateProjectionIds.Count}, unexpected={evidence.UnexpectedProjectionIds.Count}");
            if (evidence.UnexpectedProjectionIds.Count > 0)
            {
                var unexpectedDetails = await page.Locator("[data-birknext-projection-id]").EvaluateAllAsync<string[]>(
                    "(els, ids) => els.filter(el => ids.includes(el.getAttribute('data-birknext-projection-id'))).map(el => `${el.getAttribute('data-birknext-projection-id')}|${el.getAttribute('data-birknext-projection-kind') || ''}|${el.className?.toString() || ''}`)",
                    evidence.UnexpectedProjectionIds.ToArray());
                Console.WriteLine($"{pick.RoleLabel}: unexpected DOM projection details={string.Join(";", unexpectedDetails.Distinct(StringComparer.Ordinal))}");
            }
            Console.WriteLine($"{pick.RoleLabel}: structured source blocks RenderVerified={verifiedBlocks}, RenderMissing={missingBlocks}; Direct remains NotVerified");
        }
        await context.CloseAsync();
    }

    private static async Task<ProjectionRenderVerification> SelectAndVerifyAsync(IPage page, string frontendUrl, M2lbPick pick)
    {
        await page.GotoAsync(new Uri(new Uri(frontendUrl), pick.Route).ToString(), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        var host = page.Locator("[data-testid='artifact-explorer']");
        await host.WaitForAsync(new() { Timeout = 60000 });
        await page.WaitForFunctionAsync("() => { const h=document.querySelector('[data-testid=artifact-explorer]'); return h && h.dataset.status !== 'Loading'; }",
            null, new() { Timeout = 60000 });
        var selector = page.Locator("[data-testid='artifact-explorer-selector']");
        var currentDocumentId = await host.GetAttributeAsync("data-birknext-document-id");
        if (!string.Equals(currentDocumentId, pick.DocumentId, StringComparison.Ordinal) && await selector.CountAsync() > 0)
        {
            var option = selector.Locator($"option[data-birknext-document-id='{pick.DocumentId}']");
            (await option.CountAsync()).Should().Be(1,
                $"the imported role selector should contain the exact content identity ({pick.RoleLabel})");
            var optionValue = await option.GetAttributeAsync("value");
            optionValue.Should().NotBeNullOrWhiteSpace();
            await selector.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
            await selector.SelectOptionAsync(optionValue!);
        }
        else if (!string.Equals(currentDocumentId, pick.DocumentId, StringComparison.Ordinal))
        {
            var choice = page.Locator($"[data-testid='artifact-explorer-choice'][data-birknext-document-id='{pick.DocumentId}']");
            (await choice.CountAsync()).Should().Be(1,
                $"the explicit role choices should contain the exact content identity ({pick.RoleLabel})");
            await choice.ClickAsync();
        }
        await page.WaitForFunctionAsync(
            "expected => document.querySelector('[data-testid=artifact-explorer]')?.getAttribute('data-birknext-document-id') === expected",
            pick.DocumentId, new() { Timeout = 60000 });

        var loadedHost = page.Locator("[data-testid='artifact-explorer']");
        var loadedRole = await loadedHost.GetAttributeAsync("data-role");
        var loadedStatus = await loadedHost.GetAttributeAsync("data-status");
        var loadedDocumentId = await loadedHost.GetAttributeAsync("data-birknext-document-id");
        loadedRole.Should().Be(pick.Role switch
        {
            WorkspaceArtifactType.DataModel => "DataModel",
            WorkspaceArtifactType.Tasks => "Tasks",
            _ => pick.RoleLabel
        });
        loadedStatus.Should().Be("Loaded");
        loadedDocumentId.Should().Be(pick.DocumentId);

        return await ProjectionRenderBrowserVerifier.VerifyAsync(page, frontendUrl, pick.Route, pick.RoleLabel,
            pick.DocumentId, pick.ProjectionIds, reuseCurrentRoute: true);
    }

    private static async Task VerifyKeyboardFocusAsync(IPage page, M2lbPick pick)
    {
        await page.Locator("body").ClickAsync(new() { Position = new() { X = 1, Y = 1 } });
        await page.Keyboard.PressAsync("Tab");
        var focused = await page.EvaluateAsync<bool>("document.activeElement && document.activeElement !== document.body && document.activeElement.matches(':focus-visible')");
        focused.Should().BeTrue($"{pick.RoleLabel} should preserve visible keyboard focus");

        var toggleSelector = pick.RoleLabel switch
        {
            "Specification" => ".se-ctrl-btn",
            "Tasks" => ".te-ctrl-btn",
            _ => null
        };
        if (toggleSelector is not null)
        {
            var collapseAll = page.Locator(toggleSelector).Filter(new() { HasText = "Collapse All" }).First;
            var expandAll = page.Locator(toggleSelector).Filter(new() { HasText = "Expand All" }).First;
            if (await collapseAll.CountAsync() > 0 && await collapseAll.IsVisibleAsync() && await expandAll.CountAsync() > 0)
            {
                var nodeToggle = pick.RoleLabel == "Specification" ? ".se-expand-btn[aria-label='Collapse']" : ".te-expand-btn[aria-label='Collapse']";
                var expandedBefore = await page.Locator(nodeToggle).CountAsync();
                expandedBefore.Should().BeGreaterThan(0, $"{pick.RoleLabel} should have expanded content for keyboard verification");
                await collapseAll.FocusAsync();
                await page.Keyboard.PressAsync("Enter");
                await page.WaitForFunctionAsync("selector => document.querySelectorAll(selector).length === 0", nodeToggle,
                    new() { Timeout = 10000 });
                await expandAll.FocusAsync();
                await page.Keyboard.PressAsync("Space");
                await page.WaitForFunctionAsync("selector => document.querySelectorAll(selector).length > 0", nodeToggle,
                    new() { Timeout = 10000 });
                (await page.Locator(nodeToggle).CountAsync()).Should().Be(expandedBefore,
                    $"{pick.RoleLabel} should restore the expanded projections with Space");
            }
        }
        else
        {
            var tabSelector = pick.RoleLabel switch
            {
                "Constitution" => ".ce-view-toggle button",
                "Plan" => ".pe-view-toggle [role='tab']",
                "Data Model" => ".dme-tab-bar [role='tab']",
                _ => ""
            };
            if (tabSelector.Length > 0)
            {
                var tabs = page.Locator(tabSelector);
                var count = await tabs.CountAsync();
                var targetIndex = -1;
                for (var index = 0; index < count; index++)
                {
                    var tab = tabs.Nth(index);
                    if (!await tab.IsVisibleAsync()) continue;
                    var selected = await tab.GetAttributeAsync("aria-selected");
                    var cssClass = await tab.GetAttributeAsync("class") ?? "";
                    if (!string.Equals(selected, "true", StringComparison.OrdinalIgnoreCase) && !cssClass.Contains("is-active", StringComparison.Ordinal))
                    {
                        targetIndex = index;
                        break;
                    }
                }
                if (targetIndex >= 0)
                {
                    var tab = tabs.Nth(targetIndex);
                    await tab.FocusAsync();
                    await page.Keyboard.PressAsync("Enter");
                    var selected = await tab.GetAttributeAsync("aria-selected");
                    var cssClass = await tab.GetAttributeAsync("class") ?? "";
                    (string.Equals(selected, "true", StringComparison.OrdinalIgnoreCase) || cssClass.Contains("is-active", StringComparison.Ordinal))
                        .Should().BeTrue($"{pick.RoleLabel} tab should activate with Enter");
                    await page.Keyboard.PressAsync("Space");
                }
            }
        }
        (await page.EvaluateAsync<bool>("document.activeElement && document.activeElement.matches(':focus-visible')"))
            .Should().BeTrue("focus should remain visible after keyboard activation");
    }

    private static List<M2lbPick> ReadM2lbDocuments(byte[] archiveBytes)
    {
        var documents = new List<(string Path, string Content)>();
        using var stream = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            using var reader = new StreamReader(entry.Open(), new UTF8Encoding(false, false), detectEncodingFromByteOrderMarks: true);
            documents.Add((entry.FullName.Replace('\\', '/'), reader.ReadToEnd()));
        }
        var discovery = ArtifactDocumentDiscovery.Classify(documents.Select(document =>
            new ArtifactDocumentDiscovery.Candidate(document.Path, Path.GetFileName(document.Path), document.Content)));
        var picks = new List<M2lbPick>();
        foreach (var (role, label, route) in Roles)
        {
            var matches = discovery.Where(item => item.Status == ArtifactDiscoveryStatus.Detected && item.Role == role)
                .Select(item => documents.Single(document => document.Path == item.RelativePath))
                .Select(document => new M2lbPick(role, label, route, document.Path, MarkdownTokenizer.DocumentFingerprint(ArtifactDocumentDiscovery.Normalize(document.Content))))
                .ToList();
            picks.AddRange(matches);
        }
        return picks;
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

    private sealed class M2lbPick(WorkspaceArtifactType role, string roleLabel, string route, string relativePath, string documentId)
    {
        public WorkspaceArtifactType Role { get; } = role;
        public string RoleLabel { get; } = roleLabel;
        public string Route { get; } = route;
        public string RelativePath { get; } = relativePath;
        public string DocumentId { get; } = documentId;
        public ExplorerCoverageDocument? Coverage { get; set; }
        public string[] ProjectionIds { get; set; } = [];
        public int ProjectionScore => Coverage?.Blocks
            .Where(block => block.Classification == CoverageClassification.RepresentedStructurally)
            .SelectMany(block => block.ProjectionIds ?? [])
            .Distinct(StringComparer.Ordinal).Count() ?? 0;
    }

}
