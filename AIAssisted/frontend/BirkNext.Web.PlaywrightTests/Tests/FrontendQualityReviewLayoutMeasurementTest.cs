using BirkNext.Web.PlaywrightTests.Fixtures;
using FluentAssertions;
using Microsoft.Playwright;
using System.Text.Json;

namespace BirkNext.Web.PlaywrightTests.Tests;

/// <summary>
/// Playwright test that captures detailed layout measurements from FQR result page.
/// Used for systematic investigation of UI regression at 1440x900 viewport.
///
/// This test is designed for manual investigation and measurement capture.
/// Run with: dotnet test BirkNext.Web.PlaywrightTests -p:RunPreStartedPlaywrightTests=true
///
/// Prerequisites (same as PreStarted tests):
/// - PostgreSQL running on localhost:5432
/// - Backend started: cd AIAssisted/backend && dotnet run
/// - Frontend started: cd AIAssisted/frontend && dotnet run --project BirkNext.Web
/// </summary>
[Collection("Playwright Tests - PreStarted")]
public sealed class FrontendQualityReviewLayoutMeasurementTest : IAsyncLifetime
{
    private BirkNextWebApplicationFixture_PreStarted _fixture = null!;

    public async Task InitializeAsync()
    {
        _fixture = new BirkNextWebApplicationFixture_PreStarted();
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    /// <summary>
    /// Captures comprehensive layout measurements from populated FQR result page.
    /// Tests at exactly 1440x900 viewport with 100% browser zoom.
    ///
    /// Measurements include:
    /// - Window/viewport/client dimensions
    /// - Device pixel ratio and visual viewport scale
    /// - DOM element bounding rects (page, sections, cards, matrix)
    /// - Computed styles (typography, grid definitions)
    /// - Overflow/scroll behavior
    /// </summary>
    [Fact]
    [Trait("Category", "PreStarted")]
    public async Task FrontendQualityReview_CaptureDetailedLayoutMeasurements()
    {
        // Use existing fixture context which provides standard viewport
        var page = await _fixture.Context.NewPageAsync();
        await page.SetViewportSizeAsync(1440, 900);

        try
        {
            // Configure target environment to avoid auth requirement
            var settings = JsonSerializer.Serialize(new
            {
                profiles = new[]
                {
                    new
                    {
                        id = "measurement-test",
                        name = "Measurement Test",
                        environmentType = "Local",
                        targetUrl = _fixture.FrontendUrl,
                        authentication = new { requiresAuthentication = false, authenticationType = "None" },
                        performance = new { },
                        coreWebVitals = new { },
                        security = new { },
                        features = new
                        {
                            enableSecurityEngine = true,
                            enablePerformanceEngine = true,
                            enableBrowserRuntimeEngine = true,
                            enableAccessibilityEngine = true,
                            enableLighthouseEngine = true,
                            enablePassiveSecurityEngine = true
                        },
                        engineRequirements = new
                        {
                            staticSecurity = "Required",
                            passivePerformance = "Required",
                            browserRuntime = "Optional",
                            accessibility = "Optional",
                            lighthouse = "Optional",
                            passiveSecurity = "Optional"
                        },
                        releasePolicy = new { blockingLogicalIssueIds = Array.Empty<string>(), reviewOptionalEngineFailures = true },
                        integrations = Array.Empty<object>()
                    }
                },
                activeProfileId = "measurement-test"
            });
            var settingsLiteral = JsonSerializer.Serialize(settings);
            await page.AddInitScriptAsync($"localStorage.setItem('birknext:frontend-analysis-settings', {settingsLiteral});");

            // Navigate to FQR page
            await page.GotoAsync($"{_fixture.FrontendUrl}/frontend-quality-review", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 30000
            });

            // Click run button
            var run = page.GetByRole(AriaRole.Button, new() { Name = "Run Frontend Quality Review", Exact = true });
            await run.WaitForAsync(new LocatorWaitForOptions { Timeout = 15000 });
            (await run.IsDisabledAsync()).Should().BeFalse("target must be configured");
            await run.ClickAsync();

            // Wait for engine matrix to appear (indicates results are rendering)
            var matrix = page.Locator("table.fqr-engine-matrix");
            await matrix.WaitForAsync(new LocatorWaitForOptions { Timeout = 240000 });

            // Wait for page to stabilize
            await page.WaitForTimeoutAsync(1000);

            // ============================================================
            // STEP 3: CAPTURE VIEWPORT / SCALE DATA
            // ============================================================
            var viewportData = await page.EvaluateAsync<ViewportMeasurements>(@"
                () => {
                    const vv = window.visualViewport;
                    const html = document.documentElement;
                    const body = document.body;

                    return {
                        windowInnerWidth: window.innerWidth,
                        windowInnerHeight: window.innerHeight,
                        documentElementClientWidth: html.clientWidth,
                        documentElementClientHeight: html.clientHeight,
                        documentElementScrollWidth: html.scrollWidth,
                        documentElementScrollHeight: html.scrollHeight,
                        windowDevicePixelRatio: window.devicePixelRatio,
                        screenWidth: screen.width,
                        screenHeight: screen.height,
                        visualViewportWidth: vv?.width ?? 0,
                        visualViewportHeight: vv?.height ?? 0,
                        visualViewportScale: vv?.scale ?? 1,
                        bodyClientWidth: body.clientWidth,
                        bodyClientHeight: body.clientHeight,
                        bodyScrollWidth: body.scrollWidth,
                        bodyScrollHeight: body.scrollHeight,
                        bodyZoom: body.style.zoom || 'none',
                        htmlZoom: html.style.zoom || 'none',
                        bodyComputedZoom: window.getComputedStyle(body).zoom || 'none',
                        htmlComputedZoom: window.getComputedStyle(html).zoom || 'none'
                    };
                }
            ");

            // ============================================================
            // STEP 4: CAPTURE DOM GEOMETRY
            // ============================================================
            var domGeometry = await page.EvaluateAsync<DomGeometryMeasurements>(@"
                () => {
                    const getRect = (selector) => {
                        const el = document.querySelector(selector);
                        if (!el) return null;
                        const rect = el.getBoundingClientRect();
                        return {
                            selector: selector,
                            x: rect.x,
                            y: rect.y,
                            width: rect.width,
                            height: rect.height,
                            clientWidth: el.clientWidth,
                            clientHeight: el.clientHeight,
                            scrollWidth: el.scrollWidth,
                            scrollHeight: el.scrollHeight,
                            offsetWidth: el.offsetWidth,
                            offsetHeight: el.offsetHeight
                        };
                    };

                    return {
                        mainContent: getRect('main'),
                        fqrPage: getRect('.fqr-page'),
                        releaseDisposition: getRect('[role=""region""][aria-label*=""Release""]'),
                        engineMatrix: getRect('table.fqr-engine-matrix'),
                        engineMatrixRow: getRect('table.fqr-engine-matrix tbody tr'),
                        categoryGrid: getRect('.fqr-category-grid'),
                        categoryCard: getRect('.fqr-category-card'),
                        findingsSearch: getRect('.fqr-search'),
                        technicalDetails: getRect('.fqr-technical-details'),
                        findingsTable: getRect('.fqr-findings-table'),
                        findingsToolbar: getRect('.fqr-findings-toolbar')
                    };
                }
            ");

            // ============================================================
            // STEP 5: CAPTURE COMPUTED TYPOGRAPHY
            // ============================================================
            var typography = await page.EvaluateAsync<TypographyMeasurements>(@"
                () => {
                    const getStyles = (selector) => {
                        const el = document.querySelector(selector);
                        if (!el) return null;
                        const style = window.getComputedStyle(el);
                        return {
                            selector: selector,
                            fontSize: style.fontSize,
                            lineHeight: style.lineHeight,
                            fontFamily: style.fontFamily,
                            fontWeight: style.fontWeight
                        };
                    };

                    return {
                        html: getStyles('html'),
                        body: getStyles('body'),
                        pageTitle: getStyles('.wsr-page h1') || getStyles('h1'),
                        sectionHeading: getStyles('.fqr-section h2') || getStyles('h2'),
                        normalParagraph: getStyles('.fqr-empty-panel') || getStyles('p'),
                        engineMatrixHeader: getStyles('table.fqr-engine-matrix th'),
                        engineMatrixRow: getStyles('table.fqr-engine-matrix td'),
                        categoryCardTitle: getStyles('.fqr-category-title'),
                        categoryCardValue: getStyles('.fqr-category-card strong'),
                        searchInput: getStyles('.fqr-search'),
                        button: getStyles('button'),
                        technicalDetailsLabel: getStyles('.fqr-technical-details summary')
                    };
                }
            ");

            // ============================================================
            // STEP 6: CAPTURE GRID DEFINITIONS
            // ============================================================
            var grids = await page.EvaluateAsync<GridMeasurements>(@"
                () => {
                    const getGridInfo = (selector) => {
                        const el = document.querySelector(selector);
                        if (!el) return null;
                        const style = window.getComputedStyle(el);
                        return {
                            selector: selector,
                            display: style.display,
                            gridTemplateColumns: style.gridTemplateColumns,
                            gridTemplateRows: style.gridTemplateRows,
                            columnGap: style.columnGap,
                            rowGap: style.rowGap,
                            gap: style.gap,
                            width: style.width,
                            maxWidth: style.maxWidth,
                            minWidth: style.minWidth,
                            clientWidth: el.clientWidth,
                            scrollWidth: el.scrollWidth
                        };
                    };

                    return {
                        engineMatrix: getGridInfo('table.fqr-engine-matrix'),
                        categoryGrid: getGridInfo('.fqr-category-grid'),
                        summaryGrid: getGridInfo('.fqr-summary-grid'),
                        riskGrid: getGridInfo('.fqr-risk-grid'),
                        recommendationGrid: getGridInfo('.fqr-recommendation-grid'),
                        technicalGrid: getGridInfo('.fqr-technical-grid'),
                        findingsToolbar: getGridInfo('.fqr-findings-toolbar')
                    };
                }
            ");

            // ============================================================
            // STEP 7: CHECK FOR CSS TRANSFORM / SCALE
            // ============================================================
            var transforms = await page.EvaluateAsync<TransformMeasurements>(@"
                () => {
                    const checkAncestors = () => {
                        const checks = {};
                        let el = document.querySelector('.fqr-page');
                        let level = 0;

                        while (el && level < 10) {
                            const style = window.getComputedStyle(el);
                            checks[el.tagName + (el.className ? '.' + el.className.split(' ')[0] : '') + level] = {
                                zoom: style.zoom,
                                transform: style.transform,
                                scale: style.scale,
                                fontSize: style.fontSize,
                                display: style.display
                            };
                            el = el.parentElement;
                            level++;
                        }

                        return checks;
                    };

                    return {
                        ancestorChain: checkAncestors(),
                        bodyZoom: window.getComputedStyle(document.body).zoom,
                        htmlZoom: window.getComputedStyle(document.documentElement).zoom,
                        fqrPageZoom: window.getComputedStyle(document.querySelector('.fqr-page')).zoom,
                        fqrPageTransform: window.getComputedStyle(document.querySelector('.fqr-page')).transform
                    };
                }
            ");

            // ============================================================
            // STEP 8: INSPECT PARENT SHELL WIDTH
            // ============================================================
            var parentGeometryJson = await page.EvaluateAsync<string>(@"
                () => {
                    const getAncestorInfo = () => {
                        const info = [];
                        let el = document.querySelector('.fqr-page');
                        let level = 0;

                        while (el && level < 15) {
                            const style = window.getComputedStyle(el);
                            info.push({
                                level: level,
                                tagName: el.tagName,
                                className: el.className,
                                clientWidth: el.clientWidth,
                                scrollWidth: el.scrollWidth,
                                offsetWidth: el.offsetWidth,
                                computedWidth: style.width,
                                computedMaxWidth: style.maxWidth,
                                computedMinWidth: style.minWidth,
                                computedOverflow: style.overflow,
                                computedOverflowX: style.overflowX,
                                computedTransform: style.transform,
                                computedZoom: style.zoom
                            });
                            el = el.parentElement;
                            level++;
                        }

                        return info;
                    };

                    return JSON.stringify(getAncestorInfo());
                }
            ");
            var parentGeometry = JsonSerializer.Deserialize<List<AncestorInfo>>(
                parentGeometryJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];

            // ============================================================
            // STEP 12: CHECK ENGINE MATRIX TABLE/LAYOUT
            // ============================================================
            var matrixInfo = await page.EvaluateAsync<EngineMatrixMeasurements>(@"
                () => {
                    const matrix = document.querySelector('table.fqr-engine-matrix');
                    if (!matrix) return null;

                    const style = window.getComputedStyle(matrix);
                    const tr = matrix.querySelector('tbody tr');
                    const th = matrix.querySelector('thead th');
                    const td = matrix.querySelector('tbody td');

                    return {
                        tableDisplay: style.display,
                        tableLayout: style.tableLayout,
                        tableWidth: style.width,
                        tableMaxWidth: style.maxWidth,
                        tableClientWidth: matrix.clientWidth,
                        tableScrollWidth: matrix.scrollWidth,
                        tableOffsetWidth: matrix.offsetWidth,
                        tableWhiteSpace: style.whiteSpace,
                        trWhiteSpace: tr ? window.getComputedStyle(tr).whiteSpace : null,
                        thWhiteSpace: th ? window.getComputedStyle(th).whiteSpace : null,
                        tdWhiteSpace: td ? window.getComputedStyle(td).whiteSpace : null,
                        firstColumnWidth: th ? window.getComputedStyle(th).width : null,
                        trCount: matrix.querySelectorAll('tbody tr').length,
                        thCount: matrix.querySelectorAll('thead th').length,
                        headerHeight: th ? th.getBoundingClientRect().height : null,
                        rowHeight: tr ? tr.getBoundingClientRect().height : null
                    };
                }
            ");

            // ============================================================
            // STEP 13: CHECK SCREENSHOT DIMENSIONS VS CSS VIEWPORT
            // ============================================================
            var screenshotPath = Path.Combine(Path.GetTempPath(), $"fqr-measurement-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath, FullPage = false });

            var abMeasurements = await page.EvaluateAsync<string>(@"
                () => {
                    const page = document.querySelector('.fqr-page');
                    const measure = () => ({
                        pageWidth: page.getBoundingClientRect().width,
                        matrixWidth: document.querySelector('.fqr-engine-matrix').getBoundingClientRect().width,
                        matrixFirstColumnWidth: document.querySelector('.fqr-engine-matrix th').getBoundingClientRect().width,
                        categoryCardWidth: document.querySelector('.fqr-category-card').getBoundingClientRect().width,
                        scrollWidth: document.documentElement.scrollWidth,
                        bodyFontSize: getComputedStyle(document.body).fontSize,
                        matrixFontSize: getComputedStyle(document.querySelector('.fqr-engine-matrix td')).fontSize
                    });
                    page.style.maxWidth = 'none'; page.style.margin = '';
                    const control = measure();
                    page.style.maxWidth = '1400px'; page.style.margin = '0 auto';
                    const candidate = measure();
                    page.style.maxWidth = ''; page.style.margin = '';
                    return JSON.stringify({ control, candidate });
                }
            ");
            Console.WriteLine($"FQR_AB={abMeasurements}");

            foreach (var (width, height) in new[] { (1440, 900), (1280, 720), (1024, 768) })
            {
                await page.SetViewportSizeAsync(width, height);
                var responsive = await page.EvaluateAsync<string>(@"
                    () => JSON.stringify({
                        clientWidth: document.documentElement.clientWidth,
                        scrollWidth: document.documentElement.scrollWidth,
                        pageWidth: document.querySelector('.fqr-page').getBoundingClientRect().width,
                        categoryCardWidth: document.querySelector('.fqr-category-card').getBoundingClientRect().width,
                        representativeFontSize: getComputedStyle(document.querySelector('.fqr-category-title')).fontSize,
                        overflowing: [...document.querySelectorAll('body *')]
                            .filter(el => el.getBoundingClientRect().right > document.documentElement.clientWidth + 0.5)
                            .slice(0, 8)
                            .map(el => ({ tag: el.tagName, className: String(el.className), right: el.getBoundingClientRect().right, width: el.getBoundingClientRect().width }))
                    })
                ");
                Console.WriteLine($"FQR_RESPONSIVE_{width}x{height}={responsive}");
            }
            await page.SetViewportSizeAsync(1440, 900);

            // ============================================================
            // CONSOLIDATE MEASUREMENTS FOR REPORT
            // ============================================================
            var report = new MeasurementReport
            {
                ViewportMeasurements = viewportData,
                DomGeometry = domGeometry,
                Typography = typography,
                Grids = grids,
                Transforms = transforms,
                ParentGeometry = parentGeometry,
                EngineMatrix = matrixInfo,
                ScreenshotPath = screenshotPath,
                TestTimestamp = DateTime.UtcNow.ToString("O")
            };

            // Validate critical measurements
            viewportData.WindowInnerWidth.Should().Be(1440, "viewport width should be exactly 1440");
            viewportData.WindowInnerHeight.Should().Be(900, "viewport height should be exactly 900");
            viewportData.WindowDevicePixelRatio.Should().Be(1.0f, "device pixel ratio should be 1.0 (100% zoom)");

            // Verify result page is visible
            domGeometry.FqrPage.Should().NotBeNull("FQR page should be rendered");
            domGeometry.FqrPage!.Width.Should().BeGreaterThan(0, "FQR page should have width");
            domGeometry.EngineMatrix.Should().NotBeNull("engine matrix should be visible");

            // Readability invariants shared by the permanent desktop guard.
            viewportData.DocumentElementClientWidth.Should().Be(1440);
            viewportData.DocumentElementScrollWidth.Should().BeLessThanOrEqualTo(1440,
                "the result page must not introduce page-level horizontal overflow");
            typography.Body!.FontSize.Should().Be("16px", "result text must retain the application typography baseline");
            domGeometry.CategoryCard!.Width.Should().BeGreaterThanOrEqualTo(160,
                "desktop category cards must remain usable");
            domGeometry.FindingsSearch!.Height.Should().BeInRange(36, 56,
                "the search input must retain a normal control height");
            (await page.GetByText("Release disposition", new() { Exact = true }).IsVisibleAsync()).Should().BeTrue();
            (await page.Locator("table.fqr-engine-matrix").IsVisibleAsync()).Should().BeTrue();
            (await page.GetByText("Category Summary", new() { Exact = true }).IsVisibleAsync()).Should().BeTrue();
            (await page.GetByText("Technical Details", new() { Exact = true }).IsVisibleAsync()).Should().BeTrue();

            // Log detailed measurements for manual inspection
            Console.WriteLine("=== FQR LAYOUT MEASUREMENTS ===");
            Console.WriteLine($"Viewport: {viewportData.WindowInnerWidth}x{viewportData.WindowInnerHeight}");
            Console.WriteLine($"Device Pixel Ratio: {viewportData.WindowDevicePixelRatio}");
            Console.WriteLine($"Visual Viewport Scale: {viewportData.VisualViewportScale}");
            Console.WriteLine($"FQR Page Width: {domGeometry.FqrPage?.Width}");
            Console.WriteLine($"Engine Matrix Width: {domGeometry.EngineMatrix?.Width}");
            Console.WriteLine($"Document Scroll Width: {viewportData.DocumentElementScrollWidth}");
            Console.WriteLine($"Screenshot: {screenshotPath}");
            Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    [Trait("Category", "PreStarted")]
    [Trait("Category", "UICorrectness")]
    public Task FrontendQualityReview_ResultLayout_IsReadableAtDesktop() =>
        FrontendQualityReview_CaptureDetailedLayoutMeasurements();

    private class ViewportMeasurements
    {
        public int WindowInnerWidth { get; set; }
        public int WindowInnerHeight { get; set; }
        public int DocumentElementClientWidth { get; set; }
        public int DocumentElementClientHeight { get; set; }
        public int DocumentElementScrollWidth { get; set; }
        public int DocumentElementScrollHeight { get; set; }
        public float WindowDevicePixelRatio { get; set; }
        public int ScreenWidth { get; set; }
        public int ScreenHeight { get; set; }
        public double VisualViewportWidth { get; set; }
        public double VisualViewportHeight { get; set; }
        public double VisualViewportScale { get; set; }
        public int BodyClientWidth { get; set; }
        public int BodyClientHeight { get; set; }
        public int BodyScrollWidth { get; set; }
        public int BodyScrollHeight { get; set; }
        public string BodyZoom { get; set; } = "";
        public string HtmlZoom { get; set; } = "";
        public string BodyComputedZoom { get; set; } = "";
        public string HtmlComputedZoom { get; set; } = "";
    }

    private class ElementRect
    {
        public string? Selector { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int ClientWidth { get; set; }
        public int ClientHeight { get; set; }
        public int ScrollWidth { get; set; }
        public int ScrollHeight { get; set; }
        public int OffsetWidth { get; set; }
        public int OffsetHeight { get; set; }
    }

    private class DomGeometryMeasurements
    {
        public ElementRect? MainContent { get; set; }
        public ElementRect? FqrPage { get; set; }
        public ElementRect? ReleaseDisposition { get; set; }
        public ElementRect? EngineMatrix { get; set; }
        public ElementRect? EngineMatrixRow { get; set; }
        public ElementRect? CategoryGrid { get; set; }
        public ElementRect? CategoryCard { get; set; }
        public ElementRect? FindingsSearch { get; set; }
        public ElementRect? TechnicalDetails { get; set; }
        public ElementRect? FindingsTable { get; set; }
        public ElementRect? FindingsToolbar { get; set; }
    }

    private class StyleInfo
    {
        public string? Selector { get; set; }
        public string? FontSize { get; set; }
        public string? LineHeight { get; set; }
        public string? FontFamily { get; set; }
        public string? FontWeight { get; set; }
    }

    private class TypographyMeasurements
    {
        public StyleInfo? Html { get; set; }
        public StyleInfo? Body { get; set; }
        public StyleInfo? PageTitle { get; set; }
        public StyleInfo? SectionHeading { get; set; }
        public StyleInfo? NormalParagraph { get; set; }
        public StyleInfo? EngineMatrixHeader { get; set; }
        public StyleInfo? EngineMatrixRow { get; set; }
        public StyleInfo? CategoryCardTitle { get; set; }
        public StyleInfo? CategoryCardValue { get; set; }
        public StyleInfo? SearchInput { get; set; }
        public StyleInfo? Button { get; set; }
        public StyleInfo? TechnicalDetailsLabel { get; set; }
    }

    private class GridInfo
    {
        public string? Selector { get; set; }
        public string? Display { get; set; }
        public string? GridTemplateColumns { get; set; }
        public string? GridTemplateRows { get; set; }
        public string? ColumnGap { get; set; }
        public string? RowGap { get; set; }
        public string? Gap { get; set; }
        public string? Width { get; set; }
        public string? MaxWidth { get; set; }
        public string? MinWidth { get; set; }
        public int ClientWidth { get; set; }
        public int ScrollWidth { get; set; }
    }

    private class GridMeasurements
    {
        public GridInfo? EngineMatrix { get; set; }
        public GridInfo? CategoryGrid { get; set; }
        public GridInfo? SummaryGrid { get; set; }
        public GridInfo? RiskGrid { get; set; }
        public GridInfo? RecommendationGrid { get; set; }
        public GridInfo? TechnicalGrid { get; set; }
        public GridInfo? FindingsToolbar { get; set; }
    }

    private class TransformMeasurements
    {
        public Dictionary<string, object>? AncestorChain { get; set; }
        public string? BodyZoom { get; set; }
        public string? HtmlZoom { get; set; }
        public string? FqrPageZoom { get; set; }
        public string? FqrPageTransform { get; set; }
    }

    private class AncestorInfo
    {
        public int Level { get; set; }
        public string? TagName { get; set; }
        public string? ClassName { get; set; }
        public int ClientWidth { get; set; }
        public int ScrollWidth { get; set; }
        public int OffsetWidth { get; set; }
        public string? ComputedWidth { get; set; }
        public string? ComputedMaxWidth { get; set; }
        public string? ComputedMinWidth { get; set; }
        public string? ComputedOverflow { get; set; }
        public string? ComputedOverflowX { get; set; }
        public string? ComputedTransform { get; set; }
        public string? ComputedZoom { get; set; }
    }

    private class EngineMatrixMeasurements
    {
        public string? TableDisplay { get; set; }
        public string? TableLayout { get; set; }
        public string? TableWidth { get; set; }
        public string? TableMaxWidth { get; set; }
        public int TableClientWidth { get; set; }
        public int TableScrollWidth { get; set; }
        public int TableOffsetWidth { get; set; }
        public string? TableWhiteSpace { get; set; }
        public string? TrWhiteSpace { get; set; }
        public string? ThWhiteSpace { get; set; }
        public string? TdWhiteSpace { get; set; }
        public string? FirstColumnWidth { get; set; }
        public int TrCount { get; set; }
        public int ThCount { get; set; }
        public double? HeaderHeight { get; set; }
        public double? RowHeight { get; set; }
    }

    private class MeasurementReport
    {
        public ViewportMeasurements? ViewportMeasurements { get; set; }
        public DomGeometryMeasurements? DomGeometry { get; set; }
        public TypographyMeasurements? Typography { get; set; }
        public GridMeasurements? Grids { get; set; }
        public TransformMeasurements? Transforms { get; set; }
        public List<AncestorInfo>? ParentGeometry { get; set; }
        public EngineMatrixMeasurements? EngineMatrix { get; set; }
        public string? ScreenshotPath { get; set; }
        public string? TestTimestamp { get; set; }
    }
}
