using System.Text.RegularExpressions;
using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;
using Xunit;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Target Application → target check: what a check shows, proposes and keeps.
///
/// History (Playwright regression recovery, 2026-11-17): these tests used to point the check at the real m2lbdev host and expect detection
/// to list "Discovered API Endpoints/Integrations" with Apply/Add buttons. That behaviour was retired on purpose — a target check now reports
/// target facts (reachability, framework, sign-in, a suggested environment type) as evidence only, and endpoints are owned by Endpoint
/// Discovery (source, proxy and browser evidence); the bUnit test DetectedEndpointProposals_AreNotShownOnAnyTab guards that. Each test below
/// verifies the successor of the original user goal, answering the check with a synthetic result (no target is contacted).
/// </summary>
[Collection(BrowserServersCollection.Name)]
public sealed class EndpointDiscoveryUITests : IAsyncLifetime
{
    private const string Environment = "QA";
    private BirkNextWebApplicationFixture _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    /// <summary>Opens <see cref="Environment"/>'s Target Application tab and runs one (synthetic) target check.</summary>
    private static async Task<TargetEnvironmentSettingsPage.TargetCheckFake> CheckTargetAsync(IPage page, string suggestedType = "QA")
    {
        var fake = await TargetEnvironmentSettingsPage.FakeTargetCheckAsync(page, TargetEnvironmentSettingsPage.PublicBlazorTarget(suggestedType));
        await TargetEnvironmentSettingsPage.OpenAsync(page);
        await TargetEnvironmentSettingsPage.SelectAsync(page, Environment);
        await TargetEnvironmentSettingsPage.OpenTabAsync(page, "Target Application");
        await EnsureApplicationAsync(page);
        await Assertions.Expect(page.GetByTestId("target-detection-state")).ToHaveTextAsync("Not checked");
        // Before the first check the action says "Check target" (not "again").
        await page.GetByRole(AriaRole.Button, new() { Name = "Check target", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("target-detection-state")).ToHaveTextAsync("Detection complete");
        return fake;
    }

    /// <summary>
    /// The next action asks for the application before anything else ("Select application"); the synthetic environment may have none, depending
    /// on which tests ran before. Name one (Advanced → add manually) so each test starts from the same state.
    /// </summary>
    private static async Task EnsureApplicationAsync(IPage page)
    {
        var empty = page.GetByTestId("tai-empty");
        // Wait until the panel has read the environment (either the chosen application or "not chosen"), not just until it is rendered.
        await page.Locator("[data-testid=tai-empty], [data-testid=tai-current]").First.WaitForAsync(new() { Timeout = 60000 });
        if (!await empty.IsVisibleAsync()) return;
        await page.GetByTestId("tai-advanced").Locator("summary").ClickAsync();
        await page.GetByTestId("tai-manual-name").FillAsync("Synthetic Blazor app");
        await page.GetByTestId("tai-manual-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("tai-name")).ToHaveTextAsync("Synthetic Blazor app", new() { Timeout = 30000 });
    }

    private static Task<string> SelectedUrlAsync(IPage page) => page.GetByTestId("target-detection-url").InnerTextAsync();

    [BrowserFact]
    public async Task TargetCheck_ShowsDetectedTargetFacts_AndProposesNoEndpointsAsConfiguration()
    {
        // Successor of DetectedEndpoints_AfterSuccessfulDetection_DisplaysDiscoveredSection.
        var page = await _fixture.Context.NewPageAsync();
        try
        {
            var fake = await CheckTargetAsync(page);
            fake.RequestedUrls.Should().ContainSingle().Which.Should().Be(await SelectedUrlAsync(page), "the check is for the selected environment's Frontend URL");
            await Assertions.Expect(page.GetByTestId("target-detection-reachability")).ToContainTextAsync("Reachable");
            await Assertions.Expect(page.GetByTestId("target-detection-framework")).ToContainTextAsync("Blazor WebAssembly");
            await Assertions.Expect(page.GetByTestId("target-detection-runtime")).ToContainTextAsync("Runtime not verified");

            await page.GetByTestId("target-detection-evidence-toggle").ClickAsync();
            var detected = page.GetByTestId("target-detected-summary");
            await Assertions.Expect(detected).ToContainTextAsync("Detected values describe this target. They are not saved configuration.");

            // The REST/GraphQL/Swagger values in the result are never offered as configuration on any tab.
            foreach (var tab in new[] { "Target Application", "General", "Endpoint Discovery" })
            {
                await TargetEnvironmentSettingsPage.OpenTabAsync(page, tab);
                (await page.Locator("main").InnerTextAsync()).Should().NotContain("api.synthetic.example.test", $"detected endpoints are not proposed on {tab}");
                (await page.GetByText("Discovered API Endpoints").CountAsync()).Should().Be(0);
            }
        }
        finally { await page.CloseAsync(); }
    }

    [BrowserFact]
    public async Task TargetCheck_DoesNotChangeTheSavedEnvironment()
    {
        // Successor of DetectedIntegrations_AfterSuccessfulDetection_DisplaysDiscoveredIntegrations: a check adds nothing to the environment.
        var page = await _fixture.Context.NewPageAsync();
        try
        {
            await CheckTargetAsync(page, suggestedType: "Production");
            var url = await SelectedUrlAsync(page);

            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.Locator(".fa-profile-detail").First.WaitForAsync(new() { Timeout = 120000 });
            await TargetEnvironmentSettingsPage.SelectAsync(page, Environment);
            await TargetEnvironmentSettingsPage.OpenTabAsync(page, "General");
            var detail = await page.Locator(".fa-profile-detail").InnerTextAsync();
            Regex.IsMatch(detail, @"Environment Type\s+QA").Should().BeTrue("the suggested Production type is not applied by a check");
            await TargetEnvironmentSettingsPage.OpenTabAsync(page, "Target Application");
            (await SelectedUrlAsync(page)).Should().Be(url, "the Frontend URL is never changed by a check");
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save changes" })).ToHaveCountAsync(0);
        }
        finally { await page.CloseAsync(); }
    }

    [BrowserFact]
    public async Task ApplyDetectedType_ChangesOnlyTheDraft_UntilSaved()
    {
        // Successor of ApplyEndpointButton_ForDetectedRest_IsClickable: the one detected value that can be applied goes to the draft.
        var page = await _fixture.Context.NewPageAsync();
        try
        {
            await CheckTargetAsync(page, suggestedType: "Production");
            await page.GetByTestId("target-detection-evidence-toggle").ClickAsync();
            var details = page.GetByTestId("target-detected-details");
            if (await details.GetAttributeAsync("open") is null) await details.Locator("summary").ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply detected type", Exact = true }).ClickAsync();

            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save changes", Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".fa-page-unsaved")).ToHaveTextAsync("Unsaved changes");
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Edit Environment", Exact = true })).ToBeVisibleAsync();

            await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.Locator(".fa-profile-detail").First.WaitForAsync(new() { Timeout = 120000 });
            await TargetEnvironmentSettingsPage.SelectAsync(page, Environment);
            Regex.IsMatch(await page.Locator(".fa-profile-detail").InnerTextAsync(), @"Environment Type\s+QA").Should().BeTrue("a cancelled draft is not saved");
        }
        finally { await page.CloseAsync(); }
    }

    [BrowserFact]
    public async Task OpenEndpointDiscovery_FromATargetWithoutSignIn()
    {
        // Successor of ApplyIntegrationButton_WhenClicked_AddsToConfiguredIntegrations: endpoints come from Endpoint Discovery.
        var page = await _fixture.Context.NewPageAsync();
        try
        {
            await CheckTargetAsync(page);
            var next = page.GetByTestId("target-next-action");
            await Assertions.Expect(next).ToHaveTextAsync("Open Endpoint Discovery");
            await Assertions.Expect(page.GetByTestId("target-next-reason")).ToHaveTextAsync("The target does not require sign-in.");
            await next.ClickAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Tab, new() { Name = "Endpoint Discovery", Exact = true })).ToHaveAttributeAsync("aria-selected", "true");
        }
        finally { await page.CloseAsync(); }
    }

    [BrowserFact]
    public async Task StaleDetectionWarning_WhenUrlChanged_DisplaysWarningMessage()
    {
        var page = await _fixture.Context.NewPageAsync();
        try
        {
            await CheckTargetAsync(page);
            await TargetEnvironmentSettingsPage.EditAsync(page);
            await TargetEnvironmentSettingsPage.OpenTabAsync(page, "Target Application");
            var urlInput = page.Locator("input[type='url']").First;
            await urlInput.FillAsync("https://changed.synthetic.example.test/");
            await urlInput.DispatchEventAsync("change");

            await Assertions.Expect(page.GetByTestId("target-detection-stale")).ToContainTextAsync("Discoveries are stale.");
            await Assertions.Expect(page.GetByTestId("target-detection-stale")).ToContainTextAsync("The target URL changed since detection.");
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        }
        finally { await page.CloseAsync(); }
    }

    [BrowserFact]
    public async Task StaleDetection_RequiresARecheck_BeforeItCountsAgain()
    {
        // Successor of DetectedEndpointsHidden_WhenDetectionIsStale_HidesDiscoveredSection.
        var page = await _fixture.Context.NewPageAsync();
        try
        {
            var fake = await CheckTargetAsync(page);
            await TargetEnvironmentSettingsPage.EditAsync(page);
            await TargetEnvironmentSettingsPage.OpenTabAsync(page, "Target Application");
            var urlInput = page.Locator("input[type='url']").First;
            await urlInput.FillAsync("https://different.synthetic.example.test/");
            await urlInput.DispatchEventAsync("change");

            await Assertions.Expect(page.GetByTestId("target-detection-state")).ToHaveTextAsync("Needs re-check");
            // The one check button becomes the primary action (it is never swapped for another button: 2026-11-18).
            await Assertions.Expect(page.GetByTestId("target-check-again")).ToHaveTextAsync("Check target again");
            await Assertions.Expect(page.GetByTestId("target-check-again")).ToHaveAttributeAsync("data-primary", "true");
            await Assertions.Expect(page.GetByTestId("target-next-action")).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Continue detection in browser" })).ToHaveCountAsync(0);
            fake.RequestedUrls.Should().HaveCount(1, "a URL change never re-runs the check by itself");
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        }
        finally { await page.CloseAsync(); }
    }
}
