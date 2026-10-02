using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BirkNext.TestEvidence;
using Microsoft.Extensions.Configuration;

namespace BirkNext.Api.Services.TestEvidence;

/// <summary>An execution-result provider: reads one result artifact into generic, provider-independent execution evidence. It never reads source.</summary>
public interface ITestExecutionEvidenceProvider
{
    string ProviderId { get; }
    int ProviderVersion { get; }
    /// <summary>True when the artifact looks like this provider's format (name/content sniff; no full parse).</summary>
    bool CanRead(string fileName, byte[] head);
    /// <summary>Parses and normalizes. Malformed input is <see cref="TestResultImportStatus.InvalidArtifact"/> — an import problem, never a test failure.</summary>
    TestResultArtifactPreview Read(string fileName, byte[] content);
}

/// <summary>Conservative, configurable import limits (<c>TestEvidence:*</c>).</summary>
public sealed record TestEvidenceOptions(long MaxArtifactBytes, int MaxResults, int MaxMessageChars, int MaxStackTraceChars, int MaxOutputChars)
{
    public static TestEvidenceOptions Default { get; } = new(20 * 1024 * 1024, 50_000, 4_000, 8_000, 2_000);

    public static TestEvidenceOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("TestEvidence");
        var d = Default;
        return new TestEvidenceOptions(
            Math.Clamp(section.GetValue<long?>("MaxArtifactBytes") ?? d.MaxArtifactBytes, 1024, 100L * 1024 * 1024),
            Math.Clamp(section.GetValue<int?>("MaxResults") ?? d.MaxResults, 1, 200_000),
            Math.Clamp(section.GetValue<int?>("MaxMessageChars") ?? d.MaxMessageChars, 100, 20_000),
            Math.Clamp(section.GetValue<int?>("MaxStackTraceChars") ?? d.MaxStackTraceChars, 0, 50_000),
            Math.Clamp(section.GetValue<int?>("MaxOutputChars") ?? d.MaxOutputChars, 0, 20_000));
    }
}

/// <summary>
/// TRX (<c>test.execution.trx</c>) — the Visual Studio TeamTest 2010 result format written by the VSTest TRX logger and by the Microsoft.Testing.Platform
/// TrxReport extension. Framework-independent: xUnit, NUnit and MSTest results all parse; only source correlation is framework-specific.
/// Untrusted XML: DTDs prohibited, no resolver (no external entities, no network or file access), bounded size, result count and strings.
/// Machine names, run users, code-base/storage paths and deployment roots are never retained.
/// </summary>
public sealed class TrxTestExecutionEvidenceProvider(TestEvidenceOptions options) : ITestExecutionEvidenceProvider
{
    public const string Namespace = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    public string ProviderId => TestEvidenceProviderIds.Trx;
    public int ProviderVersion => 1;

    public bool CanRead(string fileName, byte[] head) =>
        fileName.EndsWith(".trx", StringComparison.OrdinalIgnoreCase) && Encoding.UTF8.GetString(head).Contains("TestRun", StringComparison.Ordinal);

    public TestResultArtifactPreview Read(string fileName, byte[] content)
    {
        var name = TestEvidenceText.FileName(fileName) ?? "result.trx";
        var fingerprint = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        TestResultArtifactPreview Fail(TestResultImportStatus status, string error) => new()
        { Status = status, FileName = name, SizeBytes = content.LongLength, Fingerprint = fingerprint, Error = error, ProviderVersion = ProviderVersion };

        if (!fileName.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)) return Fail(TestResultImportStatus.UnsupportedFormat, "Only .trx test result files are supported by this provider.");
        if (content.LongLength == 0) return Fail(TestResultImportStatus.InvalidArtifact, "The file is empty.");
        if (content.LongLength > options.MaxArtifactBytes) return Fail(TestResultImportStatus.TooLarge, $"The file exceeds the {options.MaxArtifactBytes / (1024 * 1024)} MB TRX limit.");

        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0, IgnoreProcessingInstructions = true, IgnoreComments = true,
                MaxCharactersInDocument = options.MaxArtifactBytes * 2,
            };
            using var reader = XmlReader.Create(new MemoryStream(content), settings);
            document = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex) { return Fail(TestResultImportStatus.InvalidArtifact, $"Not well-formed XML ({ex.GetType().Name} at line {ex.LineNumber}). No test result was imported."); }
        catch (InvalidOperationException) { return Fail(TestResultImportStatus.InvalidArtifact, "The XML could not be read safely. No test result was imported."); }

        XNamespace ns = Namespace;
        var run = document.Root;
        if (run is null || run.Name.LocalName != "TestRun") return Fail(TestResultImportStatus.InvalidArtifact, "The document is not a TRX TestRun.");
        var warnings = new List<string>();
        if (run.Name.Namespace != ns)
        {
            warnings.Add("The TestRun element does not use the TeamTest 2010 namespace; elements were read by local name.");
            ns = run.Name.Namespace;
        }
        var results = run.Element(ns + "Results")?.Elements().Where(e => e.Name.LocalName == "UnitTestResult" || e.Name.LocalName == "TestResult").ToList() ?? [];
        if (results.Count > options.MaxResults) return Fail(TestResultImportStatus.TooLarge, $"The file has {results.Count:N0} results; the limit is {options.MaxResults:N0}.");
        var definitions = (run.Element(ns + "TestDefinitions")?.Elements() ?? [])
            .Select(e => (Id: e.Attribute("id")?.Value, Method: e.Element(ns + "TestMethod"), Storage: e.Attribute("storage")?.Value))
            .Where(d => d.Id is not null).GroupBy(d => d.Id!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var executions = new List<NormalizedTestExecution>();
        foreach (var result in results)
        {
            var testId = result.Attribute("testId")?.Value ?? "";
            var executionId = result.Attribute("executionId")?.Value ?? "";
            var testName = TestEvidenceText.Bound(result.Attribute("testName")?.Value, 1024);
            definitions.TryGetValue(testId, out var definition);
            var className = definition.Method?.Attribute("className")?.Value;
            var methodName = definition.Method?.Attribute("name")?.Value;
            var assembly = TestEvidenceText.FileName(definition.Method?.Attribute("codeBase")?.Value ?? definition.Storage);
            var (fqn, row) = Identity(className, methodName, testName);
            var outcome = result.Attribute("outcome")?.Value ?? "";
            var (state, normalized) = Normalize(outcome);
            if (outcome.Length == 0) warnings.Add("Some results have no outcome; they are recorded as Unknown.");
            var output = result.Element(ns + "Output");
            var error = output?.Element(ns + "ErrorInfo");
            executions.Add(new NormalizedTestExecution
            {
                ProviderExecutionId = TestEvidenceText.Bound(executionId, 100), ProviderTestId = TestEvidenceText.Bound(testId, 100),
                TestName = TestEvidenceText.Redact(testName, 1024) ?? "", ClassName = className is null ? null : TestEvidenceText.Bound(className, 512),
                MethodName = methodName is null ? null : TestEvidenceText.Bound(methodName, 1024), FullyQualifiedName = fqn, DataRowLabel = row,
                AssemblyName = assembly, ExecutionState = state, Result = normalized, ProviderOutcome = TestEvidenceText.Bound(outcome, 40),
                StartedAt = Time(result.Attribute("startTime")?.Value), FinishedAt = Time(result.Attribute("endTime")?.Value), DurationMs = Duration(result.Attribute("duration")?.Value),
                ErrorMessage = TestEvidenceText.Redact(error?.Element(ns + "Message")?.Value, options.MaxMessageChars),
                StackTrace = options.MaxStackTraceChars == 0 ? null : TestEvidenceText.Redact(error?.Element(ns + "StackTrace")?.Value, options.MaxStackTraceChars),
                Output = options.MaxOutputChars == 0 ? null : TestEvidenceText.Redact(output?.Element(ns + "StdOut")?.Value, options.MaxOutputChars),
            });
            if (definition.Method is null) warnings.Add("Some results have no matching test definition in the file; their class/method identity is unknown.");
        }
        var duplicateIds = executions.GroupBy(e => e.ProviderExecutionId).Count(g => g.Key.Length > 0 && g.Count() > 1);
        if (duplicateIds > 0) warnings.Add($"{duplicateIds} execution id(s) appear more than once; each result row is kept.");

        var times = run.Element(ns + "Times");
        var summary = run.Element(ns + "ResultSummary");
        var counters = summary?.Element(ns + "Counters");
        var counts = TestResultCounts.From(executions);
        TestResultCounts? declared = counters is null ? null : new TestResultCounts
        {
            Total = Int(counters, "total"), Passed = Int(counters, "passed"), Failed = Int(counters, "failed"), NotExecuted = Int(counters, "notExecuted") + Int(counters, "notRunnable"),
            Inconclusive = Int(counters, "inconclusive"), TimedOut = Int(counters, "timeout"), Aborted = Int(counters, "aborted"), ExecutionFailed = Int(counters, "error") + Int(counters, "disconnected"),
        };
        if (declared is not null && (declared.Total != counts.Total || declared.Passed != counts.Passed || declared.Failed != counts.Failed || declared.NotExecuted != counts.NotExecuted))
            warnings.Add("The TRX summary counters differ from the individual results; counts shown are parsed from the results.");
        var summaryOutcome = summary?.Attribute("outcome")?.Value;
        var aborted = string.Equals(summaryOutcome, "Aborted", StringComparison.OrdinalIgnoreCase) || (declared?.Aborted ?? 0) > 0 || executions.Any(e => e.ExecutionState == "Aborted");
        var messages = (summary?.Element(ns + "RunInfos")?.Elements() ?? []).Select(i => TestEvidenceText.Redact(i.Element(ns + "Text")?.Value, 500)).OfType<string>().Distinct().Take(20).ToList();
        if (executions.Count == 0) warnings.Add("The run contains no test results.");
        var started = Time(times?.Attribute("start")?.Value) ?? Time(times?.Attribute("creation")?.Value);
        var finished = Time(times?.Attribute("finish")?.Value);
        return new TestResultArtifactPreview
        {
            Status = TestResultImportStatus.Valid, ProviderVersion = ProviderVersion, FileName = name, SizeBytes = content.LongLength, Fingerprint = fingerprint,
            Run = new TestRunPreview
            {
                ProviderRunId = TestEvidenceText.Bound(run.Attribute("id")?.Value, 100), StartedAt = started, FinishedAt = finished,
                DurationMs = started is { } s && finished is { } f && f >= s ? (f - s).TotalMilliseconds : null,
                RunState = aborted ? "Aborted" : summaryOutcome is null && executions.Count == 0 ? "Unknown" : "Completed",
                ProviderOutcome = summaryOutcome is null ? null : TestEvidenceText.Bound(summaryOutcome, 40), Counts = counts, DeclaredCounts = declared, Messages = messages,
            },
            Executions = executions, Warnings = warnings.Distinct().ToList(),
            Limitations = aborted ? ["The run was aborted: tests without a result row have no execution evidence in this artifact (they are not Failed)."] : [],
        };
    }

    /// <summary>
    /// TRX outcome → generic execution state + result. Not every non-pass is a failure: NotExecuted (incl. xUnit skips) stays NotExecuted, Error and
    /// Disconnected are execution failures (no test verdict), Timeout and Aborted keep their own state, Warning is not upgraded to Passed.
    /// </summary>
    public static (string ExecutionState, string Result) Normalize(string outcome) => outcome.Trim().ToLowerInvariant() switch
    {
        "passed" => ("Completed", "Passed"),
        "failed" => ("Completed", "Failed"),
        "inconclusive" => ("Completed", "Inconclusive"),
        "warning" => ("Completed", "Inconclusive"),
        "passedbutrunaborted" => ("Completed", "Passed"),
        "notexecuted" or "notrunnable" => ("NotExecuted", "Unknown"),
        "timeout" => ("TimedOut", "Unknown"),
        "aborted" => ("Aborted", "Unknown"),
        "error" or "disconnected" => ("ExecutionFailed", "Unknown"),
        "inprogress" => ("Running", "Unknown"),
        "pending" => ("Queued", "Unknown"),
        _ => ("Unknown", "Unknown"),
    };

    /// <summary>
    /// Stable identity from the TRX test definition. The VSTest logger writes className + bare method name; Microsoft.Testing.Platform writes
    /// className + "Namespace.Class.Method(args)" (or a declared display name). Only a name that provably starts with the class becomes a
    /// fully-qualified name; a display name stays a display name.
    /// </summary>
    public static (string? FullyQualifiedName, string? DataRow) Identity(string? className, string? methodName, string testName)
    {
        static (string Name, string? Row) Split(string value)
        {
            var open = value.IndexOf('(');
            return open > 0 && value.EndsWith(')') ? (value[..open], value[(open + 1)..^1]) : (value, null);
        }
        if (!string.IsNullOrEmpty(className) && !string.IsNullOrEmpty(methodName))
        {
            var (name, row) = Split(methodName);
            if (name.StartsWith(className + ".", StringComparison.Ordinal) && IsIdentifier(name[(className.Length + 1)..])) return (name, row ?? Split(testName).Row);
            if (IsIdentifier(name)) return (className + "." + name, Split(testName).Row);
            return (null, null);
        }
        if (!string.IsNullOrEmpty(className) && testName.StartsWith(className + ".", StringComparison.Ordinal))
        {
            var (name, row) = Split(testName);
            return IsIdentifier(name[(className.Length + 1)..]) ? (name, row) : (null, null);
        }
        return (null, null);
    }

    private static bool IsIdentifier(string value) => value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c == '_') && !char.IsDigit(value[0]);

    private static DateTimeOffset? Time(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;

    private static double? Duration(string? value) => TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var d) ? Math.Round(d.TotalMilliseconds, 3) : null;

    private static int Int(XElement element, string attribute) => int.TryParse(element.Attribute(attribute)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Max(0, v) : 0;
}
