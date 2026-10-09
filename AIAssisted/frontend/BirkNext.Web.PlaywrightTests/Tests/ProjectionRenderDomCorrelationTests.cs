using BirkNext.MarkdownDiagnostics;
using FluentAssertions;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Tests;

public sealed class ProjectionRenderDomCorrelationTests
{
    [Fact]
    public async Task Browser_DOM_ids_compare_as_verified_missing_duplicate_and_unexpected()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("""
            <main>
              <article data-birknext-projection-id="expected-one" data-birknext-projection-kind="Requirement"></article>
              <article data-birknext-projection-id="expected-one" data-birknext-projection-kind="Requirement"></article>
              <article data-birknext-projection-id="unexpected-one" data-birknext-projection-kind="Risk"></article>
            </main>
            """);
        var observed = await page.Locator("[data-birknext-projection-id]").EvaluateAllAsync<string[]>(
            "els => els.map(el => el.getAttribute('data-birknext-projection-id'))");

        await page.Locator("main").EvaluateAsync("node => node.setAttribute('data-testid', 'artifact-explorer')");
        await page.Locator("main").EvaluateAsync("node => { node.setAttribute('data-birknext-document-id', 'fixture-spec'); node.setAttribute('data-role', 'Specification'); node.setAttribute('data-status', 'Loaded'); }");
        var result = await ProjectionRenderBrowserVerifier.CompareCurrentPageAsync(page, "Specification", "fixture-spec", "/specification-explorer",
            ["expected-one", "expected-two"], observed);

        result.FoundProjectionIds.Should().ContainSingle("expected-one");
        result.MissingProjectionIds.Should().ContainSingle("expected-two");
        result.DuplicateProjectionIds.Should().ContainSingle("expected-one");
        result.UnexpectedProjectionIds.Should().ContainSingle("unexpected-one");
        await page.CloseAsync();
    }

    [Fact]
    public async Task Document_mismatch_is_not_projection_missing()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<section data-testid='artifact-explorer' data-role='Specification' data-status='Loaded' data-birknext-document-id='document-b'><article data-birknext-projection-id='projection-a'></article></section>");

        var result = await ProjectionRenderBrowserVerifier.CompareCurrentPageAsync(page, "Specification", "document-a",
            "/specification-explorer", ["projection-a"]);

        result.ExecutionStatus.Should().Be(ProjectionRenderExecutionStatus.Unavailable);
        result.UnavailableReason.Should().Be("DocumentMismatch");
        result.ExpectedDocumentId.Should().Be("document-a");
        result.ActualDocumentId.Should().Be("document-b");
        result.MissingProjectionIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_document_identity_is_not_projection_missing()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("<section data-testid='artifact-explorer' data-role='Specification' data-status='Loaded'><article data-birknext-projection-id='projection-a'></article></section>");

        var result = await ProjectionRenderBrowserVerifier.CompareCurrentPageAsync(page, "Specification", "document-a",
            "/specification-explorer", ["projection-a"]);

        result.ExecutionStatus.Should().Be(ProjectionRenderExecutionStatus.Unavailable);
        result.UnavailableReason.Should().Be("DocumentIdentityUnavailable");
        result.ActualDocumentId.Should().BeNull();
        result.MissingProjectionIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Unavailable_explorer_route_is_not_reported_as_render_missing()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync();

        var result = await ProjectionRenderBrowserVerifier.VerifyAsync(page, "http://127.0.0.1:1",
            "/specification-explorer", "Specification", "safe-doc-1", ["projection-1"]);

        result.ExecutionStatus.Should().Be(ProjectionRenderExecutionStatus.Unavailable);
        result.MissingProjectionIds.Should().BeEmpty();
        result.UnavailableReason.Should().NotBeNullOrWhiteSpace();
        await page.CloseAsync();
    }
}
