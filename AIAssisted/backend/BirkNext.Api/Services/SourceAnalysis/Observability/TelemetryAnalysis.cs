using System.Text.RegularExpressions;
using BirkNext.Api.Services.SourceArchitecture;
using BirkNext.SourceArchitecture;
using BirkNext.SourceObservability;

namespace BirkNext.Api.Services.SourceAnalysis.Observability;

/// <summary>
/// Telemetry and logging configuration from code and appsettings: exporters and sinks, service name / cloud role, log levels and sampling,
/// each kept per file and environment (an environment-specific value is a variant, not a conflict). Connection strings and endpoints are
/// reported as "configured" only — their values are never read out.
/// </summary>
internal static class TelemetryAnalysis
{
    internal sealed record Result(TelemetrySummary Summary, Dictionary<string, List<string>> ExportersByComponent);

    private static Regex R(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (Regex Pattern, string Name)[] CodeExporters =
    [
        (R(@"\bUseAzureMonitor\s*\("), "Azure Monitor (OpenTelemetry distro)"),
        (R(@"\bAddAzureMonitor(Trace|Log|Metric)Exporter\s*\("), "Azure Monitor exporter"),
        (R(@"\bAddApplicationInsightsTelemetry(WorkerService)?\s*\("), "Application Insights SDK"),
        (R(@"\bAddApplicationInsights\s*\("), "Application Insights logger provider"),
        (R(@"\bAdd(Otlp)Exporter\s*\(|\bUseOtlpExporter\s*\("), "OTLP exporter"),
        (R(@"\bAddJaegerExporter\s*\("), "Jaeger exporter"),
        (R(@"\bAddZipkinExporter\s*\("), "Zipkin exporter"),
        (R(@"\bAddPrometheus(Exporter|HttpListener|ScrapingEndpoint)\s*\("), "Prometheus exporter"),
        (R(@"\bAddConsoleExporter\s*\("), "OpenTelemetry console exporter"),
        (R(@"\bAddSeq\s*\("), "Seq"),
        (R(@"\bUseElasticApm\s*\(|\bAddAllElasticApm\s*\("), "Elastic APM"),
    ];
    private static readonly Regex SerilogSink = R(@"\bWriteTo\.(\w+)\s*\(");
    private static readonly Regex ServiceName = R(@"\bAddService\s*\(\s*(?:serviceName\s*:\s*)?""([^""]{1,80})""|\bCloudRoleName\s*=\s*""([^""]{1,80})""");
    private static readonly Regex Sampler = R(@"\bSetSampler\s*[<(]|\bnew\s+(TraceIdRatioBasedSampler|ParentBasedSampler|AlwaysOnSampler|AlwaysOffSampler)\s*\(|\bSamplingRatio\s*=|\bEnableAdaptiveSampling\s*=|\bTracesPerSecond\s*=");
    private static readonly HashSet<string> LevelValues = new(["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None", "Verbose", "Fatal"], StringComparer.OrdinalIgnoreCase);

    public static Result Run(ObservabilitySourceAnalyzer.Analysis a, CancellationToken ct)
    {
        var sink = a.Sink;
        var exporters = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var serviceNames = new List<ConfiguredSetting>();
        var levels = new List<ConfiguredSetting>();
        var sampling = new List<ConfiguredSetting>();
        var sinks = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in a.Deployables)
        {
            ct.ThrowIfCancellationRequested();
            var list = new List<string>();
            foreach (var (pattern, name) in CodeExporters)
                if (c.Find(pattern, name) is { } hit)
                {
                    list.Add(name);
                    sink.Add(ObservabilityCategory.Telemetry, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, hit.State, c.Id, name, $"Telemetry exporter: {name}",
                        "Registered in source. Configured ≠ telemetry delivered; destination and credentials are configured outside the analyzed code.", hit.Evidence);
                }
            foreach (var f in c.All)
                foreach (Match m in SerilogSink.Matches(f.Text))
                    if (ObsText.Label(m.Groups[1].Value) is { } s && s is not ("Logger" or "Sink")) { list.Add($"Serilog sink: {s}"); sinks.Add(s); }
            foreach (var p in c.Packages.Where(p => p.StartsWith("Serilog.Sinks.", StringComparison.OrdinalIgnoreCase)))
                if (ObsText.Label(p["Serilog.Sinks.".Length..]) is { } s) sinks.Add(s);
            foreach (var f in c.All)
                foreach (Match m in ServiceName.Matches(f.Text))
                    if (ObsText.Label(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) is { } n)
                        serviceNames.Add(new ConfiguredSetting(c.Id, m.Groups[1].Success ? "AddService" : "CloudRoleName", n, ArchitectureText.Safe(f.Path), "Code"));
            if (c.Find(Sampler, "Sampler / sampling ratio in code") is { } samp)
                sampling.Add(new ConfiguredSetting(c.Id, "Sampler", "Configured in code", samp.Evidence.File, "Code"));

            foreach (var cfg in c.Project.Configuration)
            {
                var env = cfg.Environment.Length == 0 ? "Default" : ArchitectureText.Safe(cfg.Environment);
                var file = ArchitectureText.Safe(cfg.Path);
                foreach (var (key, value) in cfg.Values)
                {
                    var k = key.Replace("__", ":");
                    if (Regex.IsMatch(k, @"^(ApplicationInsights:ConnectionString|APPLICATIONINSIGHTS_CONNECTION_STRING|AzureMonitor:ConnectionString|ApplicationInsights:InstrumentationKey)$", RegexOptions.IgnoreCase) && value.Length > 0)
                        list.Add("Azure Monitor / Application Insights connection (configured)");
                    else if (Regex.IsMatch(k, @"(^OTEL_EXPORTER_OTLP_(TRACES_)?ENDPOINT$|^OpenTelemetry:.*Otlp.*Endpoint$|^Otlp:Endpoint$)", RegexOptions.IgnoreCase) && value.Length > 0)
                        list.Add("OTLP endpoint (configured)");
                    else if (Regex.IsMatch(k, @"^Serilog:WriteTo(:\d+)?:Name$", RegexOptions.IgnoreCase) && ObsText.Label(value) is { } sinkName) { list.Add($"Serilog sink: {sinkName}"); sinks.Add(sinkName); }
                    else if (Regex.IsMatch(k, @"^(OTEL_SERVICE_NAME|OpenTelemetry:ServiceName|Otel:ServiceName|Telemetry:ServiceName|ApplicationInsights:CloudRoleName|AzureMonitor:CloudRoleName|Serilog:Properties:Application)$", RegexOptions.IgnoreCase)
                        && ObsText.Label(value) is { } svc)
                        serviceNames.Add(new ConfiguredSetting(c.Id, k, svc, file, env));
                    else if (Regex.IsMatch(k, @"^(Logging(:\w+)?:LogLevel:[^:]+|Serilog:MinimumLevel(:Default|:Override:[^:]+)?)$", RegexOptions.IgnoreCase) && LevelValues.Contains(value))
                        levels.Add(new ConfiguredSetting(c.Id, k.Length > 80 ? k[..80] : ArchitectureText.Safe(k), value, file, env));
                    else if (Regex.IsMatch(k, @"(Sampl(er|ing)|TracesPerSecond|OTEL_TRACES_SAMPLER)", RegexOptions.IgnoreCase) && !Regex.IsMatch(k, @"ConnectionString|Secret|Key$", RegexOptions.IgnoreCase))
                        sampling.Add(new ConfiguredSetting(c.Id, ArchitectureText.Safe(k), Regex.IsMatch(value, @"^[\w.\-]{1,40}$") ? value : "(set)", file, env));
                }
            }
            if (list.Count > 0) exporters[c.Id] = list.Distinct(StringComparer.Ordinal).ToList();
        }
        foreach (var svc in a.Input.Compose)
            foreach (var (key, value) in svc.Environment)
                if (key.Equals("OTEL_SERVICE_NAME", StringComparison.OrdinalIgnoreCase) && ObsText.Label(value) is { } n)
                    serviceNames.Add(new ConfiguredSetting($"compose:{ArchitectureText.Safe(svc.Name)}", key, n, ArchitectureText.Safe(svc.File), "Compose"));

        // Service names: a different value in another environment is a variant; two values for the same component and environment, or one
        // name shared by several components, need review.
        foreach (var g in serviceNames.GroupBy(s => (s.Component, s.Environment)).Where(g => g.Select(s => s.Value).Distinct(StringComparer.Ordinal).Count() > 1 && g.Key.Environment != "Code"))
            sink.Add(ObservabilityCategory.Telemetry, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Conflict, g.Key.Component, "Service name",
                "Conflicting service names", $"{string.Join(", ", g.Select(s => s.Value).Distinct())} are configured for the same component in {g.Key.Environment}.",
                new ObservabilityEvidence(g.First().File, 0, g.First().Key, "Service-name configuration", g.Key.Component));
        foreach (var g in serviceNames.Where(s => !s.Component.StartsWith("compose:", StringComparison.Ordinal)).GroupBy(s => (s.Value, s.Environment)).Where(g => g.Select(s => s.Component).Distinct().Count() > 1))
            sink.Add(ObservabilityCategory.Telemetry, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Conflict, null, "Service name",
                $"Shared service name {g.Key.Value}", $"Several components use the service name {g.Key.Value} in {g.Key.Environment}; their telemetry cannot be told apart by service.",
                new ObservabilityEvidence(g.First().File, 0, g.First().Key, "Service-name configuration", g.First().Component));
        var codeComponents = serviceNames.Where(s => s.Environment == "Code").Select(s => s.Component).ToHashSet(StringComparer.Ordinal);
        foreach (var c in a.Deployables.Where(c => exporters.ContainsKey(c.Id) && !serviceNames.Any(s => s.Component == c.Id) && !codeComponents.Contains(c.Id)))
            sink.Add(ObservabilityCategory.Telemetry, ObservabilityFindingKind.Finding, ObservabilitySeverity.Info, ArchitectureEvidenceState.Inferred, c.Id, "Service name",
                "No explicit service name or cloud role", "Telemetry is exported but no service name was found in source or appsettings; the SDK default (often the process name) applies, unless set by environment.",
                limitation: "OTEL_SERVICE_NAME set by deployment environment variables is not visible in source.");

        // Log levels: advisory per environment file.
        foreach (var l in levels.Where(l => l.Key.EndsWith(":Default", StringComparison.OrdinalIgnoreCase) || l.Key.Equals("Serilog:MinimumLevel", StringComparison.OrdinalIgnoreCase)))
        {
            var production = l.Environment is "Default" or "Production" || l.Environment.StartsWith("Prod", StringComparison.OrdinalIgnoreCase);
            if (production && l.Value is "Trace" or "Debug" or "Verbose")
                sink.Add(ObservabilityCategory.LoggingConfiguration, ObservabilityFindingKind.Finding, ObservabilitySeverity.Info, ArchitectureEvidenceState.Confirmed, l.Component, "Logging configuration",
                    "Potentially noisy: verbose default log level", $"Default level {l.Value} in {l.Environment} configuration. Advisory; other files or environment variables may override it.",
                    new ObservabilityEvidence(l.File, 0, l.Key, "Default log level", l.Component));
            else if (production && l.Value is "Error" or "Critical" or "Fatal" or "None")
                sink.Add(ObservabilityCategory.LoggingConfiguration, ObservabilityFindingKind.Finding, ObservabilitySeverity.Info, ArchitectureEvidenceState.Confirmed, l.Component, "Logging configuration",
                    "Potentially under-observed: high default log level", $"Default level {l.Value} in {l.Environment} configuration drops warnings and informational logs. Advisory.",
                    new ObservabilityEvidence(l.File, 0, l.Key, "Default log level", l.Component));
        }
        if (exporters.Count > 0 && sampling.Count == 0)
            sink.Add(ObservabilityCategory.Telemetry, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, ArchitectureEvidenceState.Inferred, null, "Sampling",
                "No sampling configuration found", "Telemetry is exported without explicit sampling in source; SDK defaults apply (which differ by SDK).");

        var summary = new TelemetrySummary
        {
            Exporters = exporters.Values.SelectMany(v => v).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            ServiceNames = serviceNames.Distinct().OrderBy(s => s.Component, StringComparer.Ordinal).ThenBy(s => s.Environment, StringComparer.Ordinal).ToList(),
            LogLevels = levels.Distinct().OrderBy(s => s.Component, StringComparer.Ordinal).ThenBy(s => s.Environment, StringComparer.Ordinal).ThenBy(s => s.Key, StringComparer.Ordinal).Take(200).ToList(),
            Sampling = sampling.Distinct().ToList(),
            Sinks = sinks.Order(StringComparer.Ordinal).ToList(),
        };
        return new Result(summary, exporters);
    }
}
