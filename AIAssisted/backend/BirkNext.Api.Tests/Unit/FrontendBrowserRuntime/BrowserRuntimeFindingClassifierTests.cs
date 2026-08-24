using BirkNext.Api.Services.FrontendBrowserRuntime;
using Xunit;

namespace BirkNext.Api.Tests.Unit.FrontendBrowserRuntime;


public sealed class BrowserRuntimeFindingClassifierTests
{
    private readonly BrowserResourceClassifier _resourceClassifier = new();
    private readonly BrowserRuntimeFindingClassifier _classifier;

    public BrowserRuntimeFindingClassifierTests()
    {
        _classifier = new BrowserRuntimeFindingClassifier(_resourceClassifier);
    }

    [Fact]
    public void ClassifyObservations_NoErrors_ReturnsEmptyFindings()
    {
        var observation = new BrowserStartupObservation(
            true,
            false,
            100,
            false,
            false,
            new List<BrowserConsoleEvent>(),
            new List<BrowserPageError>(),
            new List<BrowserResourceFailure>(),
            0);

        var findings = _classifier.ClassifyObservations(observation);

        Assert.Empty(findings);
    }

    [Fact]
    public void ClassifyObservations_UnhandledExceptionInConsole_ReturnsCriticalFinding()
    {
        var observation = new BrowserStartupObservation(
            true,
            false,
            100,
            false,
            false,
            new List<BrowserConsoleEvent>
            {
                new("error", "Unhandled exception rendering component", "app.razor", 1, 1)
            },
            new List<BrowserPageError>(),
            new List<BrowserResourceFailure>(),
            0);

        var findings = _classifier.ClassifyObservations(observation);

        Assert.NotEmpty(findings);
        Assert.Contains(findings, f => f.Severity == BrowserRuntimeFindingSeverity.Critical);
    }

    [Fact]
    public void ClassifyObservations_WasmInteropError_ReturnsHighSeverityFinding()
    {
        var observation = new BrowserStartupObservation(
            true,
            false,
            100,
            false,
            false,
            new List<BrowserConsoleEvent>
            {
                new("error", "no idea on how to unbox value types", "interop.js", 42, 5)
            },
            new List<BrowserPageError>(),
            new List<BrowserResourceFailure>(),
            0);

        var findings = _classifier.ClassifyObservations(observation);

        Assert.NotEmpty(findings);
        Assert.Contains(findings, f => f.Severity == BrowserRuntimeFindingSeverity.High);
    }

    [Fact]
    public void ClassifyObservations_PageError_ReturnsHighSeverityFinding()
    {
        var observation = new BrowserStartupObservation(
            true,
            false,
            100,
            true,
            false,
            new List<BrowserConsoleEvent>(),
            new List<BrowserPageError>
            {
                new("TypeError: Cannot read property 'x' of undefined", "app.ts", "Error at foo()")
            },
            new List<BrowserResourceFailure>(),
            0);

        var findings = _classifier.ClassifyObservations(observation);

        Assert.NotEmpty(findings);
        Assert.Contains(findings, f => f.Category == "PageError" && f.Severity == BrowserRuntimeFindingSeverity.High);
    }

    [Fact]
    public void ClassifyObservations_CriticalResourceFailure_ReturnsCriticalFinding()
    {
        var observation = new BrowserStartupObservation(
            true,
            false,
            100,
            true,
            false,
            new List<BrowserConsoleEvent>(),
            new List<BrowserPageError>(),
            new List<BrowserResourceFailure>
            {
                new("https://example.com/_framework/dotnet.wasm", "script", "Failed to fetch", 404)
            },
            1);

        var findings = _classifier.ClassifyObservations(observation);

        Assert.NotEmpty(findings);
        Assert.Contains(findings, f => f.Severity == BrowserRuntimeFindingSeverity.Critical && f.Category == "ResourceFailure");
    }

    [Fact]
    public void ClassifyObservations_NonCriticalResourceFailure_SkipsReporting()
    {
        var observation = new BrowserStartupObservation(
            true,
            false,
            100,
            true,
            false,
            new List<BrowserConsoleEvent>(),
            new List<BrowserPageError>(),
            new List<BrowserResourceFailure>
            {
                new("https://example.com/favicon.ico", "image", "Failed to fetch", 404)
            },
            0);

        var findings = _classifier.ClassifyObservations(observation);

        // Non-critical resource failures should not create findings
        Assert.DoesNotContain(findings, f => f.Category == "ResourceFailure" && f.Title.Contains("favicon"));
    }

    [Fact]
    public void ClassifyObservations_MultipleErrors_ReturnsMultipleFindings()
    {
        var observation = new BrowserStartupObservation(
            true,
            false,
            100,
            true,
            false,
            new List<BrowserConsoleEvent>
            {
                new("error", "Error 1", "file1.js", 1, 1),
                new("error", "Error 2", "file2.js", 2, 2)
            },
            new List<BrowserPageError>
            {
                new("Page error", "file3.js", null)
            },
            new List<BrowserResourceFailure>(),
            0);

        var findings = _classifier.ClassifyObservations(observation);

        // Should have 3 findings (2 console errors + 1 page error)
        Assert.True(findings.Count >= 3);
    }

    [Theory]
    [InlineData("ReferenceError: xyz is not defined", BrowserRuntimeFindingSeverity.High)]
    [InlineData("SyntaxError in code", BrowserRuntimeFindingSeverity.High)]
    [InlineData("Random warning", BrowserRuntimeFindingSeverity.Medium)]
    public void ClassifyObservations_PageErrorSeverity_CorrectlyClassified(string message, BrowserRuntimeFindingSeverity expectedSeverity)
    {
        var observation = new BrowserStartupObservation(
            true,
            false,
            100,
            false,
            false,
            new List<BrowserConsoleEvent>(),
            new List<BrowserPageError>
            {
                new(message, "test.js", null)
            },
            new List<BrowserResourceFailure>(),
            0);

        var findings = _classifier.ClassifyObservations(observation);

        Assert.NotEmpty(findings);
        Assert.Contains(findings, f => f.Severity == expectedSeverity);
    }
}
