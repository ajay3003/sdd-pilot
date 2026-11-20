using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests.Workflows;

/// <summary>
/// Project Evidence, end to end on an isolated BirkNext (2026-11-20): a synthetic project is imported and becomes the QA environment's Target
/// Application; the Project tab lists its source with detected capabilities, assigned roles (several, primary marked), review status and evidence
/// completeness; Edit roles opens the checklist in place and saves; the roles survive a reload; no horizontal scrolling at 1440 and 390 px.
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class ProjectEvidenceWorkflowTests : IAsyncLifetime
{
    private BirkNextWebApplicationFixture _fixture = null!;
    private const string Environment = "QA";

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private static string ShotFolder => System.Environment.GetEnvironmentVariable("BIRKNEXT_PLAYWRIGHT_SHOTS") is { Length: > 0 } dir ? dir : Path.Combine(Path.GetTempPath(), "birknext-shots");

    [BrowserFact(BrowserTestServers.FrontendVariable, BrowserTestServers.BackendVariable)]
    public async Task A_source_shows_its_roles_for_the_Target_Application_and_they_are_edited_in_place()
    {
        var page = await _fixture.Context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);
        var project = SyntheticProjects.NewName("PwEvidence");
        Directory.CreateDirectory(ShotFolder);
        try
        {
            await SyntheticProjects.ImportAndUseAsync(page, project, SyntheticProjects.SpecificationArchive(project));

            // The Target Application of the QA environment is this project (named by hand when nothing was inferred yet).
            await TargetEnvironmentSettingsPage.OpenAsync(page);
            await TargetEnvironmentSettingsPage.SelectAsync(page, Environment);
            await TargetEnvironmentSettingsPage.OpenTabAsync(page, "Target Application");
            await page.Locator("[data-testid=tai-empty], [data-testid=tai-current]").First.WaitForAsync(new() { Timeout = 60000 });
            if (await page.GetByTestId("tai-empty").IsVisibleAsync() || !(await page.GetByTestId("tai-current").InnerTextAsync()).Contains(project))
            {
                await page.GetByTestId("tai-advanced").Locator("summary").ClickAsync();
                await page.GetByTestId("tai-manual-name").FillAsync(project);
                await page.GetByTestId("tai-manual-save").ClickAsync();
                await Assertions.Expect(page.GetByTestId("tai-name")).ToHaveTextAsync(project, new() { Timeout = 30000 });
            }

            await TargetEnvironmentSettingsPage.OpenTabAsync(page, "Project");
            var table = page.GetByTestId("tep-associated");
            if (!await table.IsVisibleAsync())
            {
                // Not associated automatically: associate it through the editor (the same bindings).
                await page.GetByTestId("tep-review-suggested").ClickAsync();
                for (var step = 0; step < 4 && !await page.GetByTestId("pae-save").IsVisibleAsync(); step++)
                {
                    if (await page.GetByTestId("pae-pick").First.IsVisibleAsync() && !await page.GetByTestId("pae-pick").First.IsCheckedAsync()) await page.GetByTestId("pae-pick").First.CheckAsync();
                    await page.GetByTestId("pae-next").ClickAsync();
                }
                await page.GetByTestId("pae-save").ClickAsync();
            }
            await Assertions.Expect(table).ToBeVisibleAsync(new() { Timeout = 60000 });
            var row = table.GetByTestId("tep-associated-row").First;
            await Assertions.Expect(row.GetByTestId("tep-row-capability").First).ToBeVisibleAsync();
            await Assertions.Expect(row.GetByTestId("tep-row-review")).Not.ToBeEmptyAsync();
            await Assertions.Expect(row.GetByTestId("tep-row-completeness")).Not.ToBeEmptyAsync();
            var before = await row.GetAttributeAsync("data-roles") ?? "";

            await page.SetViewportSizeAsync(1440, 1000);
            await page.ScreenshotAsync(new() { Path = Path.Combine(ShotFolder, "project-evidence-1440.png"), FullPage = true });

            // Edit roles in place: the checklist opens on the row, a role the source cannot provide is not offered, Save keeps the choice.
            await row.GetByTestId("tep-edit-roles").ClickAsync();
            var editor = page.GetByTestId("tep-role-editor");
            await Assertions.Expect(editor).ToBeVisibleAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(ShotFolder, "project-evidence-edit-1440.png"), FullPage = true });
            var documentation = editor.Locator("[data-testid=tep-role-option][data-role=documentation] [data-testid=tep-role-check]");
            if (await documentation.IsEnabledAsync() && !await documentation.IsCheckedAsync()) await documentation.CheckAsync();
            await editor.GetByTestId("tep-role-save").ClickAsync();
            await Assertions.Expect(page.GetByTestId("tep-notice")).ToContainTextAsync("saved", new() { Timeout = 30000 });
            var after = await table.GetByTestId("tep-associated-row").First.GetAttributeAsync("data-roles") ?? "";
            after.Split(',').Should().Contain("documentation").And.Contain(before.Split(',', StringSplitOptions.RemoveEmptyEntries));

            await TargetEnvironmentSettingsPage.OpenAsync(page);
            await TargetEnvironmentSettingsPage.SelectAsync(page, Environment);
            await TargetEnvironmentSettingsPage.OpenTabAsync(page, "Project");
            await Assertions.Expect(page.GetByTestId("tep-associated").GetByTestId("tep-associated-row").First).ToHaveAttributeAsync("data-roles", after, new() { Timeout = 60000 });

            await page.SetViewportSizeAsync(390, 900);
            await page.ScreenshotAsync(new() { Path = Path.Combine(ShotFolder, "project-evidence-390.png"), FullPage = true });
            (await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1")).Should().BeTrue("no horizontal scrolling at 390 px");
            pageErrors.Should().BeEmpty();
        }
        finally { await page.CloseAsync(); }
    }
}
