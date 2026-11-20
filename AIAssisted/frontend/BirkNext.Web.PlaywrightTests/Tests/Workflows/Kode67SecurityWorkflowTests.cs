using System.Text.Json;
using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit.Abstractions;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// M01 – Gradert tilgang (Kode 6/7) end to end, as a test lead runs it, against a local stand-in of the Person module's GraphQL endpoint
/// (<see cref="Kode67StandIn"/>; nothing external, synthetic children and tokens only): a synthetic M2LB source through the Source Analysis upload
/// → the page picks it as the only source with security evidence → the temporary DEV test context (endpoint, one test child per level, two
/// identity labels, test-lead approval) → tokens for this run → static + runtime review → the graded-access defects are reported per level,
/// identity and surface (positive control, existence disclosure, revision log, search count) and the shared requirements show the issue → the
/// endpoint is fixed → the same review again → every runtime check passes → only the three allowed queries were ever sent, tokens never reach
/// the page or the report → the context is cleared. Guide: docs/guides/technical-testing.md.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class Kode67SecurityWorkflowTests(ITestOutputHelper output) : IAsyncLifetime
{
    private BirkNextWebApplicationFixture _fixture = null!;
    private readonly Kode67StandIn _person = new();

    public async Task InitializeAsync()
    {
        _person.Start();
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _person.DisposeAsync();
    }

    /// <summary>The authorization matrix as "level:identity:surface" → state (None = not tested).</summary>
    private static async Task<Dictionary<string, string>> MatrixAsync(IPage page) =>
        (await page.GetByTestId("sc-matrix-cell").EvaluateAllAsync<string[]>(
            "cs => cs.map(c => c.closest('tr').dataset.level + ':' + c.dataset.identity + ':' + c.dataset.surface + '=' + c.dataset.state)"))
        .Select(s => s.Split('=')).ToDictionary(p => p[0], p => p[1]);

    private static async Task<Dictionary<string, string>> ConsumerAsync(IPage page, string consumer)
    {
        if (await page.GetByTestId("sc-consumers").CountAsync() == 0) await page.GetByTestId("sc-consumers-disclosure-toggle").ClickAsync();
        var rows = page.Locator($"[data-testid=sc-consumer][data-consumer='{consumer}'] [data-testid=sc-consumer-requirement]");
        await rows.First.WaitForAsync(new() { Timeout = 30000 });
        return (await rows.EvaluateAllAsync<string[]>("rs => rs.map(r => r.dataset.key + '=' + r.dataset.state)")).Select(s => s.Split('=')).ToDictionary(p => p[0], p => p[1]);
    }

    private async Task RunBothAsync(IPage page)
    {
        // Tokens are typed for this run only; the page clears them after every run.
        if (!await page.GetByTestId("sc-token-unauthorized").IsVisibleAsync()) await page.GetByTestId("sc-run-options-toggle").ClickAsync();
        await page.GetByTestId("sc-token-unauthorized").FillAsync(Kode67StandIn.UnauthorizedToken);
        await page.GetByTestId("sc-token-authorized").FillAsync(Kode67StandIn.AuthorizedToken);
        await page.GetByTestId("sc-token-authorized").PressAsync("Tab");
        await Assertions.Expect(page.GetByTestId("sc-live-readiness")).ToContainTextAsync("Available", new() { Timeout = 30000 });
        var run = page.GetByTestId("sc-run");
        await Assertions.Expect(run).ToHaveAttributeAsync("data-mode", "static-runtime", new() { Timeout = 30000 });
        await run.ClickAsync();
        await Assertions.Expect(page.GetByTestId("sc-status")).ToContainTextAsync("Checked.", new() { Timeout = 120000 });
        await Assertions.Expect(page.GetByTestId("sc-token-unauthorized")).ToHaveValueAsync("");
    }

    [BrowserFact]
    public async Task Graded_access_defects_are_found_per_level_and_identity_and_a_fixed_endpoint_passes()
    {
        await _fixture.Context.AddInitScriptAsync($$"""
            localStorage.setItem('birknext:frontend-analysis-settings', JSON.stringify({ activeProfileId: 'k67-standin', profiles: [{ id: 'k67-standin',
              name: 'Person stand-in (Development)', environmentType: 'Development', targetUrl: '{{_person.Url}}/',
              authentication: { requiresAuthentication: false, authenticationType: 'None' }, integrations: [] }] }));
            """);
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        try
        {
            // 1. The synthetic M2LB source, through the Source Analysis upload.
            var archive = $"synth-m2lb-kode67-{Guid.NewGuid():N}"[..28] + ".zip";
            var form = _fixture.Context.APIRequest.CreateFormData();
            form.Set("file", new FilePayload { Name = archive, MimeType = "application/zip", Buffer = Kode67StandIn.SourcePackage("M2LB") });
            var upload = await _fixture.Context.APIRequest.PostAsync($"{BrowserTestServers.BackendUrl}/api/source-analysis/snapshots", new()
            {
                Multipart = form,
                Headers = new Dictionary<string, string> { ["Origin"] = BrowserTestServers.FrontendUrl, ["X-BirkNext-Client"] = "playwright-tests" },
            });
            var uploadText = await upload.TextAsync();
            upload.Ok.Should().BeTrue(uploadText);
            using var uploaded = JsonDocument.Parse(uploadText);
            var snapshotId = (uploaded.RootElement.TryGetProperty("snapshotId", out var sid) ? sid : uploaded.RootElement.GetProperty("id")).GetString()!;

            // 2. The page: the active Target Environment, and the synthetic source (picked when it is the only one with security evidence,
            //    chosen explicitly under Advanced when other imported sources also hold some).
            await page.GotoAsync($"{BrowserTestServers.FrontendUrl}/security-classification-review", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Assertions.Expect(page.GetByTestId("sc-target-active")).ToContainTextAsync("Person stand-in", new() { Timeout = 120000 });
            var summary = page.GetByTestId("sc-source-summary");
            await Assertions.Expect(summary).Not.ToContainTextAsync("Missing", new() { Timeout = 60000 });
            if (!(await summary.InnerTextAsync()).Contains(archive))
            {
                await page.GetByTestId("sc-advanced-source-toggle").ClickAsync();
                await page.GetByTestId("sc-primary").SelectOptionAsync(snapshotId);
            }
            await Assertions.Expect(summary).ToContainTextAsync($"Ready · {archive}", new() { Timeout = 60000 });
            await Assertions.Expect(page.GetByTestId("sc-live-readiness")).ToContainTextAsync("Not run");
            await Assertions.Expect(page.GetByTestId("sc-run-runtime")).ToHaveCountAsync(0);

            // 3. The temporary DEV test context: endpoint, a synthetic child for levels 0, 2 and 3, two identity labels, approval.
            await page.GetByTestId("sc-configure").ClickAsync();
            await page.GetByTestId("sc-context-environment").SelectOptionAsync("DEV");
            await page.GetByTestId("sc-context-endpoint").FillAsync(_person.Endpoint);
            await Assertions.Expect(page.GetByTestId("sc-context-child")).ToHaveCountAsync(4);
            foreach (var (level, (id, birkId)) in Kode67StandIn.Children)
            {
                await page.GetByTestId($"sc-context-child-id-{level}").FillAsync(id.ToString());
                await page.GetByTestId($"sc-context-child-birk-{level}").FillAsync(birkId);
            }
            await page.GetByTestId("sc-context-unauthorized").FillAsync("Caseworker without graded access");
            await page.GetByTestId("sc-context-authorized").FillAsync("Caseworker with graded access");
            await page.GetByTestId("sc-context-approved").CheckAsync();
            await page.GetByTestId("sc-context-save").ClickAsync();
            await Assertions.Expect(page.GetByTestId("sc-context-status")).ToHaveTextAsync("Ready", new() { Timeout = 30000 });
            (await page.Locator("[data-testid=sc-context-level]").EvaluateAllAsync<string[]>("ls => ls.map(l => l.dataset.level + ':' + l.innerText.includes('Configured'))"))
                .Should().Equal("0:true", "1:false", "2:true", "3:true");
            await Assertions.Expect(page.GetByTestId("sc-live-readiness")).ToContainTextAsync("token", new() { Timeout = 30000 }); // the context is ready; a run still needs a token

            // 4. The defective endpoint: static + runtime in one review.
            _person.Broken = true;
            await RunBothAsync(page);
            await page.GetByTestId("sc-matrix-table").WaitForAsync(new() { Timeout = 30000 });
            var before = await MatrixAsync(page);
            output.WriteLine("before: " + string.Join(", ", before.Select(kv => $"{kv.Key}={kv.Value}")));
            before.Should().Contain("2:Authorized:DirectProfile", "Fail", "the profile query hides graded children even from an identity with graded access")
                .And.Contain("3:Authorized:DirectProfile", "Fail")
                .And.Contain("2:Unauthorized:NonexistentComparison", "Fail", "the Kode 7 child answers with an error where a nonexistent id answers null")
                .And.Contain("3:Unauthorized:AuditLog", "Fail", "the Kode 6 revision log has no graded check")
                .And.Contain("3:Unauthorized:SearchTotalCount", "Fail", "the hidden Kode 6 child is counted")
                .And.Contain("3:Unauthorized:DirectProfile", "Pass").And.Contain("3:Unauthorized:NonexistentComparison", "Pass")
                .And.Contain("2:Unauthorized:DirectProfile", "Pass").And.Contain("3:Unauthorized:Search", "Pass")
                .And.Contain("0:Unauthorized:DirectProfile", "Pass").And.Contain("0:Authorized:DirectProfile", "Pass")
                .And.Contain("1:Unauthorized:DirectProfile", "None", "no level 1 test child was configured: not tested, never passed");
            await Assertions.Expect(page.GetByTestId("sc-overall")).ToBeVisibleAsync();
            output.WriteLine("overall before: " + await page.GetByTestId("sc-overall").InnerTextAsync());
            (await page.GetByTestId("sc-control-row").EvaluateAllAsync<string[]>("rs => rs.map(r => r.dataset.control + ':' + r.dataset.runtime)"))
                .Should().NotBeEmpty();
            var findings = await page.GetByTestId("sc-finding").EvaluateAllAsync<string[]>("fs => fs.map(f => f.dataset.rule)");
            output.WriteLine("findings before: " + string.Join(", ", findings));
            findings.Should().Contain("live-positive-control").And.Contain("live-disclosure");
            var consumerBefore = await ConsumerAsync(page, "m01");
            output.WriteLine("consumer before: " + string.Join(", ", consumerBefore.Select(kv => $"{kv.Key}={kv.Value}")));
            consumerBefore.Should().Contain("unauthorized-access", "IssueDetected").And.Contain("authorized-graded-access", "IssueDetected");

            // 5. The endpoint is fixed; the same review again.
            _person.Broken = false;
            await RunBothAsync(page);
            await page.WaitForFunctionAsync("() => document.querySelector(\"[data-testid=sc-matrix-row][data-level='2'] [data-testid=sc-matrix-cell][data-identity=Authorized][data-surface=DirectProfile]\")?.dataset.state === 'Pass'",
                null, new() { Timeout = 60000 });
            var after = await MatrixAsync(page);
            output.WriteLine("after: " + string.Join(", ", after.Select(kv => $"{kv.Key}={kv.Value}")));
            after.Where(kv => !kv.Key.StartsWith("1:", StringComparison.Ordinal) && kv.Value != "None")
                .Should().OnlyContain(kv => kv.Value == "Pass" || kv.Key.StartsWith("0:", StringComparison.Ordinal) && kv.Key.EndsWith(":AuditLog") && kv.Value == "Observed",
                    "every runtime check passes; an ungraded child's revision log is only observed");
            after.Should().Contain("2:Unauthorized:NonexistentComparison", "Pass").And.Contain("3:Unauthorized:SearchTotalCount", "Pass")
                .And.Contain("3:Unauthorized:AuditLog", "Pass").And.Contain("3:Authorized:DirectProfile", "Pass");
            var consumerAfter = await ConsumerAsync(page, "m01");
            output.WriteLine("consumer after: " + string.Join(", ", consumerAfter.Select(kv => $"{kv.Key}={kv.Value}")));
            // Every runtime check passed, but the source still has authorization findings: source and runtime disagree → Review required
            // (an observed-only check of the ungraded child used to keep both at Partial).
            consumerAfter.Should().Contain("unauthorized-access", "ReviewRequired").And.Contain("authorized-graded-access", "ReviewRequired");
            (await page.GetByTestId("sc-finding").EvaluateAllAsync<string[]>("fs => fs.map(f => f.dataset.rule)"))
                .Should().NotContain(rule => rule.StartsWith("live-", StringComparison.Ordinal), "no runtime finding is left").And.Contain("cdc-classification-constant", "the source was not changed");

            // 6. Both reviews are stored; only the three allowed queries with the two synthetic tokens were ever sent.
            await Assertions.Expect(page.GetByTestId("sc-history").Locator("option")).ToHaveCountAsync(3, new() { Timeout = 30000 });
            List<(string Field, string Document, bool KnownToken)> requests;
            lock (_person.Requests) requests = [.. _person.Requests];
            requests.Should().NotBeEmpty().And.OnlyContain(r => r.KnownToken && r.Field != "other" && !r.Document.Contains("mutation"));
            requests.Select(r => r.Field).Distinct().Should().BeEquivalentTo(["getBarn", "searchBarn", "getAuditLog"], "the current Person schema");

            // 7. The report: the result, never a token.
            var download = await page.RunAndWaitForDownloadAsync(() => page.GetByTestId("sc-export").ClickAsync());
            download.SuggestedFilename.Should().MatchRegex(@"^security-classification-\d{4}-\d{2}-\d{2}\.html$");
            var file = Path.Combine(Path.GetTempPath(), $"k67-{Guid.NewGuid():N}.html");
            await download.SaveAsAsync(file);
            try
            {
                var report = await File.ReadAllTextAsync(file);
                report.Should().Contain("Kode 6").And.NotContain(Kode67StandIn.UnauthorizedToken).And.NotContain(Kode67StandIn.AuthorizedToken);
            }
            finally { File.Delete(file); }
            (await page.ContentAsync()).Should().NotContain(Kode67StandIn.AuthorizedToken).And.NotContain(Kode67StandIn.UnauthorizedToken);

            // 8. The temporary context is cleared; the stored reviews stay.
            await page.GetByTestId("sc-context-clear").ClickAsync();
            await Assertions.Expect(page.GetByTestId("sc-context-status")).ToHaveTextAsync("Not configured", new() { Timeout = 30000 });
            await Assertions.Expect(page.GetByTestId("sc-run")).ToHaveAttributeAsync("data-mode", "static");
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
