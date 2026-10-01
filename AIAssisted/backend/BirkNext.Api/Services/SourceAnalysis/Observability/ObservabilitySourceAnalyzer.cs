using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.SourceArchitecture;
using BirkNext.SourceObservability;

namespace BirkNext.Api.Services.SourceAnalysis.Observability;

/// <summary>
/// Source Analysis → Observability: correlation/tracing, logging quality and telemetry configuration read from the selected snapshot's workspace,
/// once, at upload. It reuses the Architecture model (components, interfaces, HTTP dependencies, messaging channels) and adds what that model
/// does not hold: tracing and logging calls, propagation mechanisms and their boundaries. Source-derived only — implemented ≠ works at runtime,
/// configured ≠ telemetry flowing. Rules key on languages, libraries, APIs and configuration patterns; never on a project or repository name.
/// </summary>
public static class ObservabilitySourceAnalyzer
{
    public const int Version = 1;

    internal static SourceObservabilitySnapshot Analyze(Guid sourceSnapshotId, IqrSourceArchiveReader.Workspace workspace, ArchitectureInput input, ArchitectureSnapshot? architecture,
        DateTimeOffset at, CancellationToken ct, ObservabilityAnalyzerOptions? options = null)
    {
        options ??= ObservabilityAnalyzerOptions.Default;
        var limitations = new List<string>();
        var raw = workspace.Files.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).GroupBy(f => f.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Content, StringComparer.Ordinal);
        var components = Components(input, architecture, raw, limitations, ct);
        var sink = new FindingSink(options);
        var analysis = new Analysis(input, architecture, components, sink, options);

        var correlation = CorrelationAnalysis.Run(analysis, ct);
        var logging = LoggingAnalysis.Run(analysis, ct);
        var telemetry = TelemetryAnalysis.Run(analysis, ct);

        var unsupported = workspace.Limitations.Where(l => l.StartsWith("Not analyzed:", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal).ToList();
        var capabilities = Capabilities(unsupported, components);
        foreach (var u in unsupported)
            sink.Add(ObservabilityCategory.LoggingConfiguration, ObservabilityFindingKind.Limitation, ObservabilitySeverity.Info, ArchitectureEvidenceState.Unresolved, null, "Unsupported language",
                "Source in an unsupported language was not analyzed", $"{u} Correlation and logging in these files are not assessed.");
        if (unsupported.Any(u => u.Contains(".js", StringComparison.Ordinal) || u.Contains(".ts", StringComparison.Ordinal)))
            sink.Add(ObservabilityCategory.FrontendLogging, ObservabilityFindingKind.Limitation, ObservabilitySeverity.Info, ArchitectureEvidenceState.Unresolved, null, "JavaScript / TypeScript",
                "Frontend console logging and browser trace propagation not analyzed", "JavaScript/TypeScript source is outside the supported analyzers, so console.* calls and browser traceparent handling are not assessed.");

        var hasDotNet = input.Projects.Any(p => !p.IsTest);
        limitations.Add(ObservabilitySnapshot.SourceLimitation);
        limitations.Add("Business entity identifiers are never treated as correlation identifiers; MessageId, CorrelationId, RequestId and TraceId are kept apart unless source maps one to another.");
        if (architecture is null) limitations.Add("No Architecture model for this snapshot: boundaries come from code patterns only and their peers are not resolved.");
        var status = !hasDotNet ? ArchitectureStatus.Unsupported
            : unsupported.Count > 0 || limitations.Any(l => l.StartsWith("Could not parse", StringComparison.Ordinal)) || architecture?.Status is ArchitectureStatus.Partial or ArchitectureStatus.Unsupported ? ArchitectureStatus.Partial
            : ArchitectureStatus.Complete;

        var rows = components.Where(c => !c.Project.IsTest).Where(c => c.Type != ArchitectureComponentType.Library || logging.LogCallsByComponent.ContainsKey(c.Id) || correlation.TracingByComponent.ContainsKey(c.Id))
            .Select(c => Row(c, correlation, logging, telemetry, sink)).ToList();

        return new SourceObservabilitySnapshot
        {
            SourceSnapshotId = sourceSnapshotId, SourceFingerprint = input.Fingerprint, AnalyzerVersion = Version, ExtractedAt = at, Status = status,
            Correlation = correlation.Summary, Logging = logging.Summary with { Dimensions = Dimensions(correlation, logging, telemetry, sink) },
            Telemetry = telemetry.Summary, Components = rows, Mechanisms = correlation.Mechanisms, Boundaries = correlation.Boundaries, Edges = correlation.Edges,
            Findings = sink.ToList(), Capabilities = capabilities, Limitations = limitations.Distinct().ToList(), UnsupportedEvidence = unsupported,
        };
    }

    internal sealed record Analysis(ArchitectureInput Input, ArchitectureSnapshot? Architecture, List<ObsComponent> Components, FindingSink Sink, ObservabilityAnalyzerOptions Options)
    {
        public IEnumerable<ObsComponent> Deployables => Components.Where(c => c.Type != ArchitectureComponentType.Library && !c.Project.IsTest);
    }

    private static List<ObsComponent> Components(ArchitectureInput input, ArchitectureSnapshot? architecture, Dictionary<string, string> raw, List<string> limitations, CancellationToken ct)
    {
        var parsed = new Dictionary<string, ObsFile>(StringComparer.Ordinal);
        ObsFile? File(ArchProject project, CodeFile code, string componentId, string componentName)
        {
            if (parsed.TryGetValue(code.Path, out var existing)) return existing;
            ct.ThrowIfCancellationRequested();
            try
            {
                return parsed[code.Path] = new ObsFile
                {
                    Path = code.Path, Project = project, ComponentId = componentId, ComponentName = componentName, Text = code.Text,
                    Root = ObsText.Tree(code.Path, raw.GetValueOrDefault(code.Path) ?? code.Text),
                };
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                limitations.Add($"Could not parse {ArchitectureText.Safe(code.Path)}; it was skipped.");
                return null;
            }
        }
        var byProject = (architecture?.Components ?? []).Where(c => c.ComponentType != ArchitectureComponentType.ExternalSystem && c.SourceProject.Length > 0)
            .GroupBy(c => c.SourceProject, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var result = new List<ObsComponent>();
        foreach (var p in input.Projects.Where(p => !p.IsAspireHost).OrderBy(p => p.Path, StringComparer.Ordinal))
        {
            var known = byProject.GetValueOrDefault(p.Path);
            var type = known?.ComponentType ?? (p.IsTest ? ArchitectureComponentType.Unknown : Deployable(p) ? Guess(p) : ArchitectureComponentType.Library);
            var id = known?.Id ?? (type == ArchitectureComponentType.Library || p.IsTest ? "library:" : "component:") + ArchitectureText.Safe(p.Name);
            var name = known?.Name ?? ArchitectureText.Safe(p.Name);
            var component = new ObsComponent { Id = id, Name = name, Type = p.IsTest ? ArchitectureComponentType.Unknown : type, Project = p };
            foreach (var code in p.Code) if (File(p, code, id, name) is { } f) component.Own.Add(f);
            component.Packages.AddRange(p.Packages);
            result.Add(component);
        }
        var byPath = result.ToDictionary(c => c.Project.Path, StringComparer.Ordinal);
        foreach (var c in result.Where(c => c.Type != ArchitectureComponentType.Library && !c.Project.IsTest))
            foreach (var lib in input.Closure(c.Project).Where(x => x.Path != c.Project.Path))
                if (byPath.TryGetValue(lib.Path, out var libComponent) && libComponent.Type == ArchitectureComponentType.Library)
                {
                    c.Libraries.AddRange(libComponent.Own);
                    c.Packages.AddRange(lib.Packages);
                }
        return result;
    }

    private static bool Deployable(ArchProject p) =>
        p.Sdk.EndsWith(".Web", StringComparison.OrdinalIgnoreCase) || p.Sdk.EndsWith(".Worker", StringComparison.OrdinalIgnoreCase) || p.Sdk.Contains("BlazorWebAssembly", StringComparison.OrdinalIgnoreCase)
        || p.Sdk.Contains("Functions", StringComparison.OrdinalIgnoreCase) || p.OutputType is "Exe" or "WinExe" || p.HasPackage("Microsoft.Azure.Functions.Worker");

    private static ArchitectureComponentType Guess(ArchProject p) =>
        p.Sdk.Contains("BlazorWebAssembly", StringComparison.OrdinalIgnoreCase) ? ArchitectureComponentType.Frontend
        : p.Sdk.EndsWith(".Web", StringComparison.OrdinalIgnoreCase) ? ArchitectureComponentType.Api
        : p.Sdk.EndsWith(".Worker", StringComparison.OrdinalIgnoreCase) ? ArchitectureComponentType.Worker
        : ArchitectureComponentType.Unknown;

    private static List<AnalyzerCapability> Capabilities(List<string> unsupported, List<ObsComponent> components)
    {
        bool Present(params string[] exts) => exts.Any(ext => unsupported.Any(u => u.Contains($" {ext} ", StringComparison.Ordinal)));
        string Detail(string what, params string[] exts) => Present(exts) ? $"Present in this snapshot, not analyzed. {what}" : $"Not present in this snapshot. {what}";
        var list = new List<AnalyzerCapability>
        {
            new("C# / .NET", AnalyzerSupport.Supported, AnalyzerSupport.Supported, "Syntax analysis of ILogger, Serilog, Activity/OpenTelemetry, catch clauses and message handlers."),
            new("ASP.NET Core HTTP", AnalyzerSupport.Supported, AnalyzerSupport.Supported, "Inbound/outbound HTTP boundaries, instrumentation, correlation headers and middleware."),
            new("OpenTelemetry .NET / Application Insights / Azure Monitor", AnalyzerSupport.Supported, AnalyzerSupport.Supported, "Tracing, logging and exporter registration in code and JSON configuration."),
            new("Azure Service Bus / Event Hubs", AnalyzerSupport.Supported, AnalyzerSupport.Supported, "Message produce/consume, Diagnostic-Id and CorrelationId/MessageId properties."),
            new("Wolverine / MassTransit", AnalyzerSupport.Partial, AnalyzerSupport.Supported, "Handlers and framework envelope correlation; framework-internal propagation is inferred."),
            new("Kafka / RabbitMQ", AnalyzerSupport.Partial, AnalyzerSupport.Supported, "Client produce/consume calls and explicit headers; no automatic propagation is assumed."),
            new("JSON configuration (appsettings)", AnalyzerSupport.Supported, AnalyzerSupport.Supported, "Log levels, sinks, scopes, service names and sampling per environment file. Values of connection strings are never read out."),
            new("YAML (compose)", AnalyzerSupport.Partial, AnalyzerSupport.Partial, "Service-name environment variables only."),
            new("JavaScript / TypeScript", AnalyzerSupport.Unsupported, AnalyzerSupport.Unsupported, Detail("console.* and browser propagation are not assessed.", ".js", ".ts")),
            new("Java", AnalyzerSupport.Unsupported, AnalyzerSupport.Unsupported, Detail("No analyzer.", ".java")),
            new("Python", AnalyzerSupport.Unsupported, AnalyzerSupport.Unsupported, Detail("No analyzer.", ".py")),
            new("Go", AnalyzerSupport.Unsupported, AnalyzerSupport.Unsupported, Detail("No analyzer.", ".go")),
            new("F# / VB.NET", AnalyzerSupport.Unsupported, AnalyzerSupport.Unsupported, Detail("Only C# syntax is analyzed.", ".fs", ".vb")),
        };
        // The .NET-based analyzers (the first eight rows) have nothing to read without a .NET project.
        if (!components.Any(c => !c.Project.IsTest))
            for (var i = 0; i < 8; i++) list[i] = list[i] with { Correlation = AnalyzerSupport.Unsupported, Logging = AnalyzerSupport.Unsupported, Detail = $"No .NET projects in this snapshot. {list[i].Detail}" };
        return list;
    }

    private static ObservabilityComponent Row(ObsComponent c, CorrelationAnalysis.Result correlation, LoggingAnalysis.Result logging, TelemetryAnalysis.Result telemetry, FindingSink sink)
    {
        var boundaries = correlation.Boundaries.Where(b => b.Component == c.Id).ToList();
        var tracing = correlation.TracingByComponent.GetValueOrDefault(c.Id) ?? [];
        var correlationState = boundaries.Count == 0 ? tracing.Count > 0 ? ObservabilityDimensionState.Detected : ObservabilityDimensionState.NotFound
            : boundaries.Any(b => b.Propagation == PropagationState.Conflicting) ? ObservabilityDimensionState.NeedsReview
            : boundaries.All(b => ObservabilitySnapshot.Supported(b.Propagation)) ? ObservabilityDimensionState.StronglySupported
            : boundaries.Any(b => ObservabilitySnapshot.Supported(b.Propagation)) ? ObservabilityDimensionState.Partial
            : ObservabilityDimensionState.NeedsReview;
        var calls = logging.LogCallsByComponent.GetValueOrDefault(c.Id);
        var concerns = sink.ToList().Any(f => f.Component == c.Id && f.Kind is ObservabilityFindingKind.Finding && f.Severity >= ObservabilitySeverity.Warning);
        return new ObservabilityComponent
        {
            ComponentId = c.Id, Name = c.Name, ComponentType = c.Type.ToString(), TracingTechnologies = tracing,
            LoggingFrameworks = logging.FrameworksByComponent.GetValueOrDefault(c.Id) ?? [], TelemetryExporters = telemetry.ExportersByComponent.GetValueOrDefault(c.Id) ?? [],
            LogCalls = calls, Correlation = correlationState,
            Logging = calls == 0 ? ObservabilityDimensionState.NotFound : concerns ? ObservabilityDimensionState.NeedsReview : ObservabilityDimensionState.Detected,
        };
    }

    private static List<ObservabilityDimension> Dimensions(CorrelationAnalysis.Result correlation, LoggingAnalysis.Result logging, TelemetryAnalysis.Result telemetry, FindingSink sink)
    {
        int Count(ObservabilityCategory category, Func<ObservabilityFinding, bool>? where = null) =>
            sink.Of(category).Where(f => f.Kind is ObservabilityFindingKind.Finding or ObservabilityFindingKind.Unresolved && (where?.Invoke(f) ?? true)).Sum(f => f.Occurrences);
        var s = logging.Summary;
        var unstructured = s.InterpolatedCalls + s.ConcatenatedCalls;
        var swallowed = Count(ObservabilityCategory.CatchAndSwallow, f => f.Severity >= ObservabilitySeverity.NeedsReview);
        var returned = Count(ObservabilityCategory.CatchAndSwallow, f => f.Severity < ObservabilitySeverity.NeedsReview);
        var sensitive = Count(ObservabilityCategory.SensitiveData) + Count(ObservabilityCategory.PayloadLogging);
        var redaction = sink.Of(ObservabilityCategory.Redaction).Any(f => f.Kind == ObservabilityFindingKind.Observation);
        var handlerGaps = Count(ObservabilityCategory.HandlerObservability);
        var retryGaps = Count(ObservabilityCategory.RetryFailure);
        var retryObserved = sink.Of(ObservabilityCategory.RetryFailure).Any(f => f.Kind == ObservabilityFindingKind.Observation);
        var tests = sink.Of(ObservabilityCategory.DeveloperTests).Where(f => f.Kind == ObservabilityFindingKind.Observation).Sum(f => f.Occurrences);
        var total = s.StructuredCalls + unstructured;
        var boundaries = correlation.Summary.HttpBoundaries + correlation.Summary.MessagingBoundaries;
        static ObservabilityDimension D(string id, string title, ObservabilityDimensionState state, string detail) => new(id, title, state, detail);
        return
        [
            D("tracing", "Tracing configuration", correlation.TracingByComponent.Count == 0 ? ObservabilityDimensionState.NotFound : ObservabilityDimensionState.Detected,
                correlation.TracingByComponent.Count == 0 ? "No tracing registration found in source." : $"Tracing registered in source for {correlation.TracingByComponent.Count} component(s). Configured ≠ telemetry flowing."),
            D("propagation", "Correlation propagation", boundaries == 0 ? ObservabilityDimensionState.NotAssessed
                    : correlation.Summary.ConflictingMechanisms > 0 || correlation.Summary.PropagationUnresolved > 0 ? ObservabilityDimensionState.NeedsReview
                    : correlation.Summary.PropagationSupported == boundaries ? ObservabilityDimensionState.StronglySupported
                    : ObservabilityDimensionState.Partial,
                boundaries == 0 ? "No HTTP or messaging boundaries found." : $"{correlation.Summary.PropagationSupported} of {boundaries} boundaries show configured, explicit or framework propagation; "
                    + $"{boundaries - correlation.Summary.PropagationSupported - correlation.Summary.PropagationUnresolved} rely on framework defaults only (inferred); {correlation.Summary.PropagationUnresolved} unresolved."),
            D("structured", "Structured logging", total == 0 ? ObservabilityDimensionState.NotFound : unstructured == 0 ? ObservabilityDimensionState.StronglySupported : ObservabilityDimensionState.Partial,
                total == 0 ? "No log calls found." : $"{s.StructuredCalls} message-template call(s); {s.InterpolatedCalls} interpolated and {s.ConcatenatedCalls} string-built or non-constant."),
            D("exceptions", "Exception preservation", s.ExceptionPreserved + s.ExceptionMessageOnly == 0 ? ObservabilityDimensionState.NotFound : s.ExceptionMessageOnly == 0 ? ObservabilityDimensionState.StronglySupported : ObservabilityDimensionState.NeedsReview,
                $"{s.ExceptionPreserved} log call(s) pass the exception object; {s.ExceptionMessageOnly} log only its message or text."),
            D("swallow", "Catch-and-swallow", swallowed == 0 ? ObservabilityDimensionState.NotFound : ObservabilityDimensionState.NeedsReview,
                (swallowed == 0 ? "No catch blocks found that drop an exception without logging, rethrowing or recording it." : $"{swallowed} catch block(s) neither log, rethrow nor record the exception.")
                + (returned > 0 ? $" {returned} more return the exception to the caller in an error result." : "")),
            D("context", "Correlation context in logs", logging.ContextLogging, logging.ContextDetail),
            D("sensitive", "Sensitive-data risk", sensitive == 0 ? ObservabilityDimensionState.NotFound : ObservabilityDimensionState.NeedsReview,
                sensitive == 0 ? "No sensitive-named values or payloads passed to log calls." : $"{sensitive} log call(s) pass sensitive-named values, headers or payloads. Values are never stored here."),
            D("redaction", "Redaction", redaction ? ObservabilityDimensionState.Detected : sensitive > 0 ? ObservabilityDimensionState.NeedsReview : ObservabilityDimensionState.NotFound,
                redaction ? "Redaction or masking support found in source." : "No redaction or masking support found in source."),
            D("retry", "Retry / failure observability", retryGaps > 0 ? ObservabilityDimensionState.NeedsReview : retryObserved ? ObservabilityDimensionState.Detected : ObservabilityDimensionState.NotFound,
                retryGaps > 0 ? $"{retryGaps} retry or dead-letter path(s) without visible logging or telemetry." : retryObserved ? "Retry or failure handling found with logging, telemetry or framework handling." : "No retry or dead-letter handling found."),
            D("handlers", "Message-handler observability", logging.Handlers == 0 ? ObservabilityDimensionState.NotFound : handlerGaps == 0 ? ObservabilityDimensionState.Detected : ObservabilityDimensionState.NeedsReview,
                logging.Handlers == 0 ? "No message handlers found." : $"{logging.Handlers} message handler(s); {handlerGaps} without logging in the handler body."),
            D("exporters", "Telemetry exporters", telemetry.Summary.Exporters.Count == 0 ? ObservabilityDimensionState.NotFound : ObservabilityDimensionState.Detected,
                telemetry.Summary.Exporters.Count == 0 ? "No telemetry exporter or log sink registration found." : string.Join(", ", telemetry.Summary.Exporters.Take(6)) + ". Configured ≠ delivered."),
            D("service-name", "Service name / role", telemetry.Summary.ServiceNames.Count == 0 ? ObservabilityDimensionState.NotFound
                    : sink.Of(ObservabilityCategory.Telemetry).Any(f => f.Title.StartsWith("Conflicting service names", StringComparison.Ordinal) || f.Title.StartsWith("Shared service name", StringComparison.Ordinal)) ? ObservabilityDimensionState.NeedsReview
                    : ObservabilityDimensionState.Detected,
                telemetry.Summary.ServiceNames.Count == 0 ? "No explicit service name or cloud role found; SDK defaults apply." : $"{telemetry.Summary.ServiceNames.Count} service-name setting(s) across files and environments."),
            D("sampling", "Sampling", telemetry.Summary.Sampling.Count == 0 ? ObservabilityDimensionState.NotFound : ObservabilityDimensionState.Detected,
                telemetry.Summary.Sampling.Count == 0 ? "No sampling configuration found; SDK defaults apply." : $"{telemetry.Summary.Sampling.Count} sampling setting(s)."),
            D("tests", "Developer tests", tests == 0 ? ObservabilityDimensionState.NotFound : ObservabilityDimensionState.Detected,
                tests == 0 ? "No correlation or logging tests found." : $"{tests} test file(s) exercise correlation, tracing or logging. Their results are not known here."),
        ];
    }
}
