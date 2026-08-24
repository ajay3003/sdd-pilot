namespace BirkNext.Api.Services.FrontendBrowserRuntime;

/// <summary>
/// Classifies browser runtime observations into findings with appropriate severity.
/// Identifies known Blazor/WASM failure patterns and distinguishes critical from non-critical issues.
/// </summary>
public sealed class BrowserRuntimeFindingClassifier
{
    private readonly BrowserResourceClassifier _resourceClassifier;

    public BrowserRuntimeFindingClassifier(BrowserResourceClassifier resourceClassifier)
    {
        _resourceClassifier = resourceClassifier;
    }

    public List<BrowserRuntimeFinding> ClassifyObservations(BrowserStartupObservation obs)
    {
        var findings = new List<BrowserRuntimeFinding>();

        // Check for known Blazor/WASM failures
        findings.AddRange(ClassifyConsoleErrors(obs.ConsoleEvents));
        findings.AddRange(ClassifyPageErrors(obs.PageErrors));
        findings.AddRange(ClassifyResourceFailures(obs.ResourceFailures));

        return findings;
    }

    private List<BrowserRuntimeFinding> ClassifyConsoleErrors(List<BrowserConsoleEvent> events)
    {
        var findings = new List<BrowserRuntimeFinding>();

        if (events == null || events.Count == 0)
            return findings;

        var errorEvents = events.Where(e => e.Type.Equals("error", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var evt in errorEvents)
        {
            var (severity, isKnownPattern) = ClassifyConsoleMessage(evt.Message);

            findings.Add(new BrowserRuntimeFinding(
                Id: $"console-error-{Guid.NewGuid():N}",
                Title: ExtractConsoleErrorTitle(evt.Message),
                Severity: severity,
                Category: "ConsoleError",
                Description: evt.Message,
                Recommendation: GetConsoleErrorRecommendation(evt.Message),
                Evidence: new List<string>
                {
                    evt.Location ?? "Unknown source",
                    $"Line: {evt.LineNumber ?? 0}, Column: {evt.ColumnNumber ?? 0}"
                }
            ));
        }

        return findings;
    }

    private List<BrowserRuntimeFinding> ClassifyPageErrors(List<BrowserPageError> errors)
    {
        var findings = new List<BrowserRuntimeFinding>();

        if (errors == null || errors.Count == 0)
            return findings;

        foreach (var err in errors)
        {
            var (severity, message) = ClassifyPageErrorMessage(err.Message);

            findings.Add(new BrowserRuntimeFinding(
                Id: $"page-error-{Guid.NewGuid():N}",
                Title: ExtractPageErrorTitle(err.Message),
                Severity: severity,
                Category: "PageError",
                Description: err.Message,
                Recommendation: GetPageErrorRecommendation(err.Message),
                Evidence: new List<string>
                {
                    err.Location ?? "Unknown source",
                    err.Stack != null ? $"Stack: {err.Stack[..Math.Min(200, err.Stack.Length)]}" : "No stack trace"
                }
            ));
        }

        return findings;
    }

    private List<BrowserRuntimeFinding> ClassifyResourceFailures(List<BrowserResourceFailure> failures)
    {
        var findings = new List<BrowserRuntimeFinding>();

        if (failures == null || failures.Count == 0)
            return findings;

        foreach (var failure in failures)
        {
            var classification = _resourceClassifier.Classify(failure.Url, failure.ResourceType);

            // Only create findings for important or critical resources
            if (classification.Category == "NonCritical")
                continue;

            var severity = classification.IsCritical
                ? BrowserRuntimeFindingSeverity.Critical
                : BrowserRuntimeFindingSeverity.High;

            findings.Add(new BrowserRuntimeFinding(
                Id: $"resource-failure-{Guid.NewGuid():N}",
                Title: $"Failed {classification.Category} Resource",
                Severity: severity,
                Category: "ResourceFailure",
                Description: $"{failure.ResourceType} failed to load: {failure.Url}",
                Recommendation: GetResourceFailureRecommendation(failure.Url),
                Evidence: new List<string>
                {
                    $"URL: {failure.Url}",
                    $"Type: {failure.ResourceType}",
                    $"Reason: {failure.FailureReason}",
                    failure.StatusCode.HasValue ? $"Status: {failure.StatusCode}" : "No status code"
                }
            ));
        }

        return findings;
    }

    private (BrowserRuntimeFindingSeverity Severity, bool IsKnown) ClassifyConsoleMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return (BrowserRuntimeFindingSeverity.Medium, false);

        // Known Blazor/WASM failure patterns
        if (message.Contains("Unhandled exception rendering component", StringComparison.OrdinalIgnoreCase))
            return (BrowserRuntimeFindingSeverity.Critical, true);

        if (message.Contains("no idea on how to unbox value types", StringComparison.OrdinalIgnoreCase))
            return (BrowserRuntimeFindingSeverity.High, true);

        if (message.Contains("WASM", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("blazor", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("assembly", StringComparison.OrdinalIgnoreCase))
            return (BrowserRuntimeFindingSeverity.High, true);

        return (BrowserRuntimeFindingSeverity.Medium, false);
    }

    private (BrowserRuntimeFindingSeverity Severity, string Message) ClassifyPageErrorMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return (BrowserRuntimeFindingSeverity.Medium, message);

        // Known Blazor/WASM patterns
        if (message.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase))
            return (BrowserRuntimeFindingSeverity.Critical, message);

        if (message.Contains("WASM", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("blazor", StringComparison.OrdinalIgnoreCase))
            return (BrowserRuntimeFindingSeverity.High, message);

        if (message.Contains("TypeError", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("ReferenceError", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("SyntaxError", StringComparison.OrdinalIgnoreCase))
            return (BrowserRuntimeFindingSeverity.High, message);

        return (BrowserRuntimeFindingSeverity.Medium, message);
    }

    private static string ExtractConsoleErrorTitle(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Console Error";

        var lines = message.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var title = lines.FirstOrDefault() ?? message;

        return title.Length > 100 ? title[..97] + "..." : title;
    }

    private static string ExtractPageErrorTitle(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Page Error";

        var lines = message.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var title = lines.FirstOrDefault() ?? message;

        return title.Length > 100 ? title[..97] + "..." : title;
    }

    private static string GetConsoleErrorRecommendation(string message)
    {
        if (message.Contains("Unhandled exception rendering component", StringComparison.OrdinalIgnoreCase))
            return "Check Blazor component code for unhandled exceptions. Review browser DevTools and backend logs.";

        if (message.Contains("no idea on how to unbox value types", StringComparison.OrdinalIgnoreCase))
            return "This is a known WASM interop issue. Verify JavaScript/C# type marshalling in interop calls.";

        if (message.Contains("WASM", StringComparison.OrdinalIgnoreCase))
            return "Investigate WASM assembly loading and initialization. Check Network tab for failed .wasm files.";

        if (message.Contains("blazor", StringComparison.OrdinalIgnoreCase))
            return "Review Blazor startup sequence and framework initialization. Check browser console for details.";

        return "Review the error message and browser DevTools console for more context.";
    }

    private static string GetPageErrorRecommendation(string message)
    {
        if (message.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase))
            return "Review the full error stack trace in browser DevTools. This is a critical failure.";

        if (message.Contains("TypeError", StringComparison.OrdinalIgnoreCase))
            return "Check variable initialization and object method calls. Verify null/undefined handling.";

        if (message.Contains("ReferenceError", StringComparison.OrdinalIgnoreCase))
            return "Verify variable or function is defined before use. Check script load order.";

        if (message.Contains("SyntaxError", StringComparison.OrdinalIgnoreCase))
            return "Check JavaScript syntax. Enable source maps for better error location info.";

        return "Review the error in browser DevTools for more details and stack trace.";
    }

    private static string GetResourceFailureRecommendation(string url)
    {
        if (url.Contains("_framework", StringComparison.OrdinalIgnoreCase))
            return "Critical Blazor framework resource failed. Check backend service is running and accessible.";

        if (url.Contains("blazor", StringComparison.OrdinalIgnoreCase) || url.Contains(".wasm", StringComparison.OrdinalIgnoreCase))
            return "WASM or Blazor runtime resource failed. Verify all assemblies are deployed correctly.";

        if (url.Contains(".js", StringComparison.OrdinalIgnoreCase) || url.Contains(".css", StringComparison.OrdinalIgnoreCase))
            return "Check that all application resources are properly deployed and accessible.";

        return "Verify the resource is deployed and accessible from the target environment.";
    }
}
