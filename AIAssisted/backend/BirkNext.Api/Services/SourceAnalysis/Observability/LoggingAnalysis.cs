using System.Text.RegularExpressions;
using BirkNext.SourceArchitecture;
using BirkNext.SourceObservability;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BirkNext.Api.Services.SourceAnalysis.Observability;

/// <summary>
/// Logging quality from C# syntax: message templates vs interpolation/concatenation, exception preservation, catch-and-swallow, advisory log
/// levels, retry and dead-letter observability, message-handler logging, correlation context in logs, sensitive-data and payload risk, and
/// redaction support. Evidence is file, line, enclosing symbol and a pattern label: argument values, literals and source text are never kept.
/// </summary>
internal static class LoggingAnalysis
{
    internal sealed record Result(LoggingSummary Summary, Dictionary<string, int> LogCallsByComponent, Dictionary<string, List<string>> FrameworksByComponent, int Handlers,
        ObservabilityDimensionState ContextLogging, string ContextDetail);

    private enum Level { Trace, Debug, Information, Warning, Error, Critical, Unknown }
    private enum TemplateKind { Template, Interpolated, Concatenated, NonConstant, None }

    private sealed record LogCall(InvocationExpressionSyntax Node, Level Level, string Framework, TemplateKind Template, ExpressionSyntax? TemplateArg, List<ExpressionSyntax> Values,
        ExpressionSyntax? ExceptionArg);

    private static Regex R(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MelMethod = R(@"^Log(Trace|Debug|Information|Warning|Error|Critical)?$");
    private static readonly Regex SerilogMethod = R(@"^(Verbose|Debug|Information|Warning|Error|Fatal)$");
    private static readonly Regex LoggerReceiver = R(@"(?i)^(_?log(ger)?|\w*logger)$");
    private static readonly Regex ExceptionName = R(@"^(e|ex|exc|err|error|exception|\w+Exception|\w+Ex)$");
    private static readonly Regex Secret = R(@"(?i)(password|passwd|pwd$|secret|apikey|api_key|accesskey|privatekey|credential|connectionstring|connstr|sastoken|sharedaccess)");
    private static readonly Regex TokenName = R(@"(?i)^(token|jwt|bearer|accesstoken|refreshtoken|idtoken|authtoken|bearertoken)$|(access|refresh|id|auth|bearer)token$");
    private static readonly Regex CookieOrAuth = R(@"(?i)^(cookie|cookies|authorization|authheader|authorizationheader)$");
    private static readonly Regex Payload = R(@"(?i)^(body|payload|requestbody|responsebody|rawbody|rawmessage|rawpayload|content|messagebody)$");
    private static readonly Regex Placeholder = R(@"\{(@|\$)?([A-Za-z_][A-Za-z0-9_]*)[^}]*\}");
    private static readonly Regex CorrelationPlaceholder = R(@"(?i)^(correlationid|traceid|spanid|messageid|requestid|operationid|conversationid)$");

    private static readonly Regex Scope = R(@"\.BeginScope\s*[<(]");
    private static readonly Regex SerilogContext = R(@"\bLogContext\.PushProperty\s*\(|\bEnrich\.FromLogContext\s*\(|\bEnrich\.With(CorrelationId\w*|Span|TraceIdentifier|ClientIp)\s*\(");
    private static readonly Regex OtelLogging = R(@"\bLogging\.AddOpenTelemetry\s*\(|\.WithLogging\s*\(|\bAddOpenTelemetry\s*\(\s*\w+\s*=>\s*\{?[^;]*IncludeFormattedMessage|\bUseAzureMonitor\s*\(|\bAddApplicationInsights\s*\(\s*\)|\bActivityTrackingOptions\b");
    private static readonly Regex Redaction = R(@"\bAddRedaction\s*\(|\bIRedactor\b|\bRedactor\b|\.Redact\s*\(|\bMask\w*\s*\(|\[LogMasked|\[NotLogged|\bDestructure\.ByTransforming|\[(SensitiveData|PersonalData|PrivateData)\b|\bDataClassification\b|\bEnableRedaction\s*\(");
    private static readonly Regex RetryConfigured = R(@"\b(AddRetry|WaitAndRetry\w*|RetryAsync|RetryForever\w*)\s*\(|\bRetryStrategyOptions\b|\bAddStandardResilienceHandler\s*\(|\bAddResilienceHandler\s*\(");
    private static readonly Regex RetryObserved = R(@"\bOnRetry\s*=|\bonRetry(Async)?\s*:|\bAddStandardResilienceHandler\s*\(|\bAddResilienceHandler\s*\(|\bConfigureTelemetry\s*\(|\bTelemetryListener\b");
    private static readonly Regex FrameworkRetry = R(@"\b(ServiceBusRetryOptions|EventHubsRetryOptions|EnableRetryOnFailure|RetryWithCooldown|ScheduleRetry|UseMessageRetry|RetryTimes)\b");
    private static readonly Regex DeadLetter = R(@"^(DeadLetterMessageAsync|MoveToErrorQueueAsync|MoveToDeadLetterQueueAsync|AbandonMessageAsync)$");
    private static readonly Regex Console_ = R(@"^(Console\.(Out\.|Error\.)?Write(Line)?|Debug\.Write(Line)?|Trace\.Write(Line)?|System\.Console\.Write(Line)?)$");
    private static readonly Regex Intentional = R(@"(?i)(intentional|deliberate|ignore|swallow|best[- ]effort|expected|no-?op|not critical|safe to)");

    public static Result Run(ObservabilitySourceAnalyzer.Analysis a, CancellationToken ct)
    {
        var sink = a.Sink;
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        var frameworks = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        int structured = 0, interpolated = 0, concatenated = 0, preserved = 0, messageOnly = 0, handlers = 0;
        var personal = a.Options.PersonalDataTerms.Select(t => t.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        int contextPlaceholders = 0;

        foreach (var c in a.Components.Where(c => !c.Project.IsTest))
        {
            var fw = new HashSet<string>(StringComparer.Ordinal);
            if (c.HasPackage("Serilog")) fw.Add("Serilog");
            if (c.HasPackage("NLog")) fw.Add("NLog");
            if (c.HasPackage("log4net")) fw.Add("log4net");
            var frontend = c.Type == ArchitectureComponentType.Frontend;
            var console = c.Type == ArchitectureComponentType.Cli;
            foreach (var f in c.Own)
            {
                ct.ThrowIfCancellationRequested();
                if (Regex.IsMatch(f.Text, @"\bILogger(<|\b)")) fw.Add("Microsoft.Extensions.Logging");
                if (Regex.IsMatch(f.Text, @"\[LoggerMessage\b|\bLoggerMessage\.Define\b"))
                {
                    fw.Add("Microsoft.Extensions.Logging");
                    var defs = Regex.Matches(f.Text, @"\[LoggerMessage\b|\bLoggerMessage\.Define\b").Count;
                    structured += defs;
                    calls[c.Id] = calls.GetValueOrDefault(c.Id) + defs;
                }
                foreach (var invocation in f.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var text = invocation.Expression.ToString();
                    if (Console_.IsMatch(text) && !console)
                    {
                        if (frontend)
                            sink.Add(ObservabilityCategory.FrontendLogging, ObservabilityFindingKind.Finding, ObservabilitySeverity.Info, ArchitectureEvidenceState.Confirmed, c.Id, "Blazor WebAssembly",
                                "Browser console output from WebAssembly code", "Console output in WebAssembly lands in the browser console only; it is not collected by server-side logging.",
                                ObsText.Evidence(f, invocation, "Console/Debug write"));
                        else
                            sink.Add(ObservabilityCategory.StructuredLogging, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Confirmed, c.Id, ".NET",
                                "Console or Debug output bypasses the logging pipeline", "Console.Write/Debug.Write output has no level, category, scope or structured properties and is not routed to configured sinks.",
                                ObsText.Evidence(f, invocation, "Console/Debug write"));
                        continue;
                    }
                    if (Classify(invocation) is not { } log) continue;
                    if (log.Framework == "Serilog") fw.Add("Serilog");
                    calls[c.Id] = calls.GetValueOrDefault(c.Id) + 1;
                    switch (log.Template)
                    {
                        case TemplateKind.Template or TemplateKind.None: structured++; break;
                        case TemplateKind.Interpolated:
                            interpolated++;
                            sink.Add(ObservabilityCategory.StructuredLogging, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Confirmed, c.Id, log.Framework,
                                "Interpolated log message", "String interpolation renders values into the message text, so they are not searchable properties and the template differs per value.",
                                ObsText.Evidence(f, invocation, "Interpolated string as message template"));
                            break;
                        default:
                            concatenated++;
                            sink.Add(ObservabilityCategory.StructuredLogging, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Confirmed, c.Id, log.Framework,
                                log.Template == TemplateKind.Concatenated ? "String-built log message" : "Non-constant log message template",
                                log.Template == TemplateKind.Concatenated ? "Concatenation or string.Format builds the message, so values are not structured properties."
                                    : "The message template is a variable, so its shape is unknown and any value in it is logged as text.",
                                ObsText.Evidence(f, invocation, log.Template == TemplateKind.Concatenated ? "Concatenated message" : "Variable as message template"));
                            break;
                    }
                    if (log.TemplateArg is LiteralExpressionSyntax lit)
                        foreach (Match m in Placeholder.Matches(lit.Token.ValueText))
                        {
                            if (CorrelationPlaceholder.IsMatch(m.Groups[2].Value)) contextPlaceholders++;
                            if (m.Groups[1].Value == "@")
                                sink.Add(ObservabilityCategory.PayloadLogging, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Confirmed, c.Id, "Serilog",
                                    "Object destructured into a log event", "{@…} captures every public property of the object; review that no sensitive or large payload is included.",
                                    ObsText.Evidence(f, invocation, "Destructuring placeholder"));
                            if (SensitiveCategory(m.Groups[2].Value, personal) is { } cat)
                                Sensitive(sink, c.Id, f, invocation, cat, "Sensitive-named placeholder in the message template", log.Framework);
                        }
                    Exceptions(sink, c.Id, f, log, ref preserved, ref messageOnly);
                    Values(sink, c.Id, f, log, personal);
                    Levels(sink, c.Id, f, log);
                }
                Catches(sink, c.Id, f);
                handlers += Handlers(sink, c, f);
                Retry(sink, c.Id, f);
            }
            if (fw.Count > 0) frameworks[c.Id] = fw.Order(StringComparer.Ordinal).ToList();
            if (c.Find(Redaction, "Redaction / masking support") is { } red)
                sink.Add(ObservabilityCategory.Redaction, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, red.State, c.Id, "Redaction", "Redaction or masking support",
                    "Redaction, masking or data-classification support exists in source. Whether it covers every sensitive value is not assessed.", red.Evidence);
            else if (c.HasPackage("Microsoft.Extensions.Compliance.Redaction") || c.HasPackage("Destructurama"))
                sink.Add(ObservabilityCategory.Redaction, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, ArchitectureEvidenceState.Inferred, c.Id, "Redaction", "Redaction package referenced",
                    "A redaction/masking package is referenced; no use of it was found in this component's code.");
        }

        // Correlation context in logs (scopes, Serilog enrichers, OpenTelemetry/Azure Monitor log correlation, configuration).
        var states = new List<(ObservabilityDimensionState State, string What)>();
        int logging = 0, withContext = 0, withTrace = 0;
        foreach (var c in a.Deployables)
        {
            var before = states.Count;
            var traceBefore = states.Count(s => s.State == ObservabilityDimensionState.StronglySupported);
            if (c.Find(OtelLogging, "Logs correlated with the current Activity (OpenTelemetry / Azure Monitor / Application Insights)") is { } otel)
            {
                states.Add((ObservabilityDimensionState.StronglySupported, "OpenTelemetry / Azure Monitor log correlation"));
                sink.Add(ObservabilityCategory.CorrelationContextLogging, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, otel.State, c.Id, "OpenTelemetry logging",
                    "Logs carry the current trace context", "The logging provider attaches TraceId/SpanId of Activity.Current to log records. Only when an Activity exists at the log call.", otel.Evidence);
            }
            if (c.Find(Scope, "ILogger.BeginScope") is { } scope)
            {
                states.Add((ObservabilityDimensionState.Detected, "logging scopes"));
                sink.Add(ObservabilityCategory.CorrelationContextLogging, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, scope.State, c.Id, "Microsoft.Extensions.Logging",
                    "Logging scopes", "BeginScope attaches properties to every log written inside the scope. Scopes reach sinks only where the provider includes them.", scope.Evidence);
            }
            if (c.Find(SerilogContext, "Serilog LogContext / enricher") is { } enrich)
            {
                states.Add((ObservabilityDimensionState.Detected, "Serilog enrichers"));
                sink.Add(ObservabilityCategory.CorrelationContextLogging, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, enrich.State, c.Id, "Serilog",
                    "Serilog context enrichment", "LogContext properties or enrichers add context to log events.", enrich.Evidence);
            }
            if (c.Project.Configuration.Any(cfg => cfg.Values.Any(kv => kv.Key.EndsWith(":IncludeScopes", StringComparison.OrdinalIgnoreCase) && kv.Value.Equals("true", StringComparison.OrdinalIgnoreCase))))
                states.Add((ObservabilityDimensionState.Detected, "IncludeScopes configuration"));
            if (calls.GetValueOrDefault(c.Id) + c.Libraries.Select(l => l.ComponentId).Distinct().Sum(id => calls.GetValueOrDefault(id)) == 0) continue;
            logging++;
            if (states.Count > before) withContext++;
            if (states.Count(s => s.State == ObservabilityDimensionState.StronglySupported) > traceBefore) withTrace++;
        }
        var contextState = withContext == 0 ? contextPlaceholders > 0 ? ObservabilityDimensionState.Partial : ObservabilityDimensionState.NotFound
            : withTrace == logging ? ObservabilityDimensionState.StronglySupported
            : withContext == logging ? ObservabilityDimensionState.Detected : ObservabilityDimensionState.Partial;
        var contextDetail = withContext == 0
            ? contextPlaceholders > 0 ? $"No scopes or trace-correlated logging found; {contextPlaceholders} message template(s) name a correlation identifier explicitly." : "No logging scopes, enrichers or trace-correlated logging found."
            : $"{withContext} of {logging} logging component(s) add context ({string.Join(", ", states.Select(s => s.What).Distinct())}); {withTrace} attach the trace context"
                + $"{(contextPlaceholders > 0 ? $"; {contextPlaceholders} template(s) name a correlation identifier" : "")}.";

        // Developer tests: correlation/tracing and logging tests in test projects. Their results are not known here.
        foreach (var c in a.Components.Where(c => c.Project.IsTest))
            foreach (var f in c.Own)
            {
                if (Regex.Match(f.Text, @"\btraceparent\b|\bActivityListener\b|\bInMemoryExporter\b|\bActivity\.Current\b|\bCorrelationId\b|\bActivitySource\b") is { Success: true } m1)
                    sink.Add(ObservabilityCategory.DeveloperTests, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, ArchitectureEvidenceState.Confirmed, c.Id, "Tests",
                        "Correlation / tracing test", "Test code exercises tracing or correlation identifiers. Test results are not known here.", ObsText.Evidence(f, m1.Index, "Correlation test"));
                if (Regex.Match(f.Text, @"\bFakeLogger\b|\bFakeLogCollector\b|\bListLogger\b|\bTestLogger\b|\bInMemorySink\b|\bTestCorrelator\b|\.Verify\s*\(\s*\w+\s*=>\s*\w+\.Log\s*\(") is { Success: true } m2)
                    sink.Add(ObservabilityCategory.DeveloperTests, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, ArchitectureEvidenceState.Confirmed, c.Id, "Tests",
                        "Logging test", "Test code captures or verifies log output. Test results are not known here.", ObsText.Evidence(f, m2.Index, "Logging test"));
            }

        var summary = new LoggingSummary
        {
            Frameworks = frameworks.Values.SelectMany(v => v).Distinct().Order(StringComparer.Ordinal).ToList(),
            StructuredCalls = structured, InterpolatedCalls = interpolated, ConcatenatedCalls = concatenated, ExceptionPreserved = preserved, ExceptionMessageOnly = messageOnly,
        };
        return new Result(summary, calls, frameworks, handlers, contextState, contextDetail);
    }

    private static LogCall? Classify(InvocationExpressionSyntax call)
    {
        var name = ObsText.Name(call);
        var receiver = ObsText.Receiver(call);
        if (receiver.Length == 0 || !LoggerReceiver.IsMatch(receiver)) return null;
        string framework;
        Level level;
        var args = call.ArgumentList.Arguments.Select(a => a.Expression).ToList();
        if (MelMethod.Match(name) is { Success: true } mel)
        {
            framework = "Microsoft.Extensions.Logging";
            if (mel.Groups[1].Success) level = Enum.Parse<Level>(mel.Groups[1].Value);
            else if (args.Count > 0 && args[0] is MemberAccessExpressionSyntax lvl && lvl.Expression.ToString() == "LogLevel" && Enum.TryParse<Level>(lvl.Name.Identifier.ValueText, out var parsed))
            { level = parsed; args = args.Skip(1).ToList(); }
            else if (args.Count > 0 && args[0].ToString().StartsWith("LogLevel.", StringComparison.Ordinal)) { level = Level.Unknown; args = args.Skip(1).ToList(); }
            else return null;
        }
        else if (SerilogMethod.Match(name) is { Success: true } s && receiver is "Log" or "_log" or "log" or "_logger" or "logger" or "Logger" or "_Logger")
        {
            framework = "Serilog";
            level = s.Groups[1].Value switch { "Verbose" => Level.Trace, "Fatal" => Level.Critical, var v => Enum.Parse<Level>(v) };
        }
        else return null;
        if (args.Count > 0 && (args[0].ToString().Contains("EventId", StringComparison.OrdinalIgnoreCase) || args[0] is ObjectCreationExpressionSyntax { Type: IdentifierNameSyntax { Identifier.ValueText: "EventId" } }))
            args = args.Skip(1).ToList();
        ExpressionSyntax? exception = null;
        if (args.Count > 1 && IsException(args[0])) { exception = args[0]; args = args.Skip(1).ToList(); }
        if (args.Count == 0) return new LogCall(call, level, framework, TemplateKind.None, null, [], exception);
        var template = args[0];
        var kind = template switch
        {
            LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) => TemplateKind.Template,
            InterpolatedStringExpressionSyntax i => i.Contents.Any(c => c is InterpolationSyntax) ? TemplateKind.Interpolated : TemplateKind.Template,
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) => TemplateKind.Concatenated,
            InvocationExpressionSyntax inv when ObsText.Name(inv) is "Format" or "Concat" or "Join" or "ToString" => TemplateKind.Concatenated,
            MemberAccessExpressionSyntax m when m.Expression.ToString() is "nameof" => TemplateKind.Template,
            InvocationExpressionSyntax inv when inv.Expression.ToString() == "nameof" => TemplateKind.Template,
            _ => TemplateKind.NonConstant,
        };
        if (template is IdentifierNameSyntax id && char.IsUpper(id.Identifier.ValueText[0]) && id.Identifier.ValueText.All(ch => char.IsUpper(ch) || ch == '_' || char.IsDigit(ch))) kind = TemplateKind.Template;
        if (template is MemberAccessExpressionSyntax constant && constant.Expression is IdentifierNameSyntax owner && owner.Identifier.ValueText.EndsWith("Messages", StringComparison.Ordinal)) kind = TemplateKind.Template;
        // A variable used as the template is also a value written to the log.
        var values = args.Skip(1).ToList();
        if (kind is TemplateKind.NonConstant or TemplateKind.Concatenated or TemplateKind.Interpolated) values.Insert(0, template);
        return new LogCall(call, level, framework, kind, template, values, exception);
    }

    private static bool IsException(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax i => ExceptionName.IsMatch(i.Identifier.ValueText),
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText is "Exception" or "InnerException" || ExceptionName.IsMatch(m.Name.Identifier.ValueText) && !m.Name.Identifier.ValueText.Equals("Error", StringComparison.Ordinal),
        ObjectCreationExpressionSyntax o => o.Type.ToString().EndsWith("Exception", StringComparison.Ordinal),
        _ => false,
    };

    private static void Exceptions(FindingSink sink, string component, ObsFile f, LogCall log, ref int preserved, ref int messageOnly)
    {
        var catchClause = log.Node.Ancestors().OfType<CatchClauseSyntax>().FirstOrDefault();
        var variable = catchClause?.Declaration?.Identifier.ValueText;
        if (log.ExceptionArg is not null) { preserved++; return; }
        if (catchClause is null) return;
        var nodes = log.Values.SelectMany(v => v.DescendantNodesAndSelf()).ToList();
        bool Refers(SyntaxNode n) => n is IdentifierNameSyntax i && (variable is { Length: > 0 } ? i.Identifier.ValueText == variable : ExceptionName.IsMatch(i.Identifier.ValueText));
        var message = nodes.OfType<MemberAccessExpressionSyntax>().FirstOrDefault(m => m.Name.Identifier.ValueText is "Message" && Refers(m.Expression));
        var toString = nodes.OfType<InvocationExpressionSyntax>().FirstOrDefault(i => ObsText.Name(i) == "ToString" && i.Expression is MemberAccessExpressionSyntax ma && Refers(ma.Expression));
        var asValue = log.Values.Skip(log.Template is TemplateKind.Template ? 0 : 1).FirstOrDefault(v => Refers(v));
        var interpolatedWhole = log.TemplateArg is InterpolatedStringExpressionSyntax interp && interp.Contents.OfType<InterpolationSyntax>().Any(x => Refers(x.Expression));
        if (message is not null)
        {
            messageOnly++;
            sink.Add(ObservabilityCategory.ExceptionPreservation, ObservabilityFindingKind.Finding, ObservabilitySeverity.Warning, ArchitectureEvidenceState.Confirmed, component, log.Framework,
                "Exception message logged without the exception", "Only ex.Message is logged: the exception type, stack trace and inner exceptions are lost. Pass the exception as the first argument.",
                ObsText.Evidence(f, log.Node, "ex.Message in log call"));
        }
        else if (toString is not null || interpolatedWhole)
        {
            messageOnly++;
            sink.Add(ObservabilityCategory.ExceptionPreservation, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Confirmed, component, log.Framework,
                "Exception flattened into message text", "The exception is rendered into the message string: the stack trace survives as text, but sinks cannot treat it as an exception.",
                ObsText.Evidence(f, log.Node, "Exception rendered into message"));
        }
        else if (asValue is not null)
        {
            messageOnly++;
            sink.Add(ObservabilityCategory.ExceptionPreservation, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Confirmed, component, log.Framework,
                "Exception passed as a template value", "The exception is a message-template argument rather than the exception parameter, so it is logged as a formatted value.",
                ObsText.Evidence(f, log.Node, "Exception as template value"));
        }
        else if (log.Level is Level.Error or Level.Critical && catchClause.Declaration is not null)
            sink.Add(ObservabilityCategory.ExceptionPreservation, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Inferred, component, log.Framework,
                "Error logged in a catch block without the exception", "The caught exception is not passed to this error log call.", ObsText.Evidence(f, log.Node, "Error log without exception"));
    }

    private static void Values(FindingSink sink, string component, ObsFile f, LogCall log, HashSet<string> personal)
    {
        foreach (var value in log.Values)
        {
            if (value is InvocationExpressionSyntax inv && ObsText.Name(inv) is "Serialize" or "SerializeObject" or "SerializeToUtf8Bytes")
            {
                sink.Add(ObservabilityCategory.PayloadLogging, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Confirmed, component, log.Framework,
                    "Serialized object logged", "A whole object is serialized into the log. Review it for sensitive fields and size.", ObsText.Evidence(f, log.Node, "Serialized payload in log"));
                continue;
            }
            foreach (var (node, name) in FinalNames(value))
            {
                if (name == "Headers")
                {
                    Sensitive(sink, component, f, log.Node, "HTTP headers (may contain authorization or cookies)", "Request/response headers passed to a log call", log.Framework);
                    break;
                }
                if (SensitiveCategory(name, personal) is { } category) { Sensitive(sink, component, f, log.Node, category, "Sensitive-named value passed to a log call", log.Framework); break; }
                if (Payload.IsMatch(name))
                {
                    sink.Add(ObservabilityCategory.PayloadLogging, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Inferred, component, log.Framework,
                        "Message or request payload logged", "A payload-named value is written to the log. Review it for personal or sensitive data and size.", ObsText.Evidence(f, log.Node, "Payload-named value in log"));
                    break;
                }
            }
        }
    }

    /// <summary>The names a logged value ends in: <c>user.Password</c> → Password, <c>payload.Source.Table</c> → Table; interpolation holes,
    /// concatenation operands, <c>?.</c> and <c>.ToString()</c> are followed. A chain through a sensitive-named object is not the object itself.</summary>
    private static IEnumerable<(SyntaxNode Node, string Name)> FinalNames(ExpressionSyntax value)
    {
        switch (value)
        {
            case IdentifierNameSyntax i: yield return (i, i.Identifier.ValueText); break;
            case MemberAccessExpressionSyntax m: yield return (m, m.Name.Identifier.ValueText); break;
            case ConditionalAccessExpressionSyntax c when c.WhenNotNull is MemberBindingExpressionSyntax b: yield return (c, b.Name.Identifier.ValueText); break;
            case ElementAccessExpressionSyntax e: foreach (var x in FinalNames(e.Expression)) yield return x; break;
            case ParenthesizedExpressionSyntax p: foreach (var x in FinalNames(p.Expression)) yield return x; break;
            case CastExpressionSyntax cast: foreach (var x in FinalNames(cast.Expression)) yield return x; break;
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToString" or "ToArray" or "ToList" } target }:
                foreach (var x in FinalNames(target.Expression)) yield return x; break;
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) || b.IsKind(SyntaxKind.CoalesceExpression):
                foreach (var x in FinalNames(b.Left)) yield return x;
                foreach (var x in FinalNames(b.Right)) yield return x;
                break;
            case InterpolatedStringExpressionSyntax s:
                foreach (var hole in s.Contents.OfType<InterpolationSyntax>()) foreach (var x in FinalNames(hole.Expression)) yield return x;
                break;
        }
    }

    internal static string? SensitiveCategory(string name, HashSet<string> personal)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("connectionstring") || lower.Contains("connstr")) return "Connection string";
        if (lower.Contains("apikey") || lower.Contains("api_key")) return "API key";
        if (Secret.IsMatch(name)) return "Password / secret";
        if (TokenName.IsMatch(name)) return "Token / bearer credential";
        if (CookieOrAuth.IsMatch(name)) return "Cookie / authorization header";
        if (personal.Contains(lower)) return "Personal data (configurable term)";
        return null;
    }

    private static void Sensitive(FindingSink sink, string component, ObsFile f, SyntaxNode node, string category, string pattern, string framework) =>
        sink.Add(ObservabilityCategory.SensitiveData, ObservabilityFindingKind.Finding, category.StartsWith("Personal", StringComparison.Ordinal) ? ObservabilitySeverity.NeedsReview : ObservabilitySeverity.HighPriority,
            ArchitectureEvidenceState.Inferred, component, framework, $"Possible sensitive data in logs: {category}",
            "A value whose name suggests this category reaches a log call. The name is a heuristic; the value itself is never read or stored.", ObsText.Evidence(f, node, pattern));

    private static void Levels(FindingSink sink, string component, ObsFile f, LogCall log)
    {
        if (log.Level is Level.Trace or Level.Debug or Level.Information && log.Node.Ancestors().Any(n => n is ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax)
            && !log.Node.Ancestors().TakeWhile(n => n is not ForEachStatementSyntax and not ForStatementSyntax and not WhileStatementSyntax and not DoStatementSyntax).Any(n => n is CatchClauseSyntax))
            sink.Add(ObservabilityCategory.LogLevel, ObservabilityFindingKind.Finding, ObservabilitySeverity.Info, ArchitectureEvidenceState.Inferred, component, log.Framework,
                "Potentially noisy: logging inside a loop", "A log call inside a loop writes once per iteration. Advisory only; volume depends on data.", ObsText.Evidence(f, log.Node, "Log call in loop"));
        if (log.Level is Level.Trace or Level.Debug or Level.Information && log.ExceptionArg is not null)
            sink.Add(ObservabilityCategory.LogLevel, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Inferred, component, log.Framework,
                "Potentially under-observed: exception logged below Warning", "An exception is logged at Trace/Debug/Information, which production filters often drop. Advisory only.",
                ObsText.Evidence(f, log.Node, "Exception at low level"));
    }

    private static void Catches(FindingSink sink, string component, ObsFile f)
    {
        foreach (var clause in f.Root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            var type = clause.Declaration?.Type.ToString() ?? "";
            if (type.EndsWith("OperationCanceledException", StringComparison.Ordinal) || type.EndsWith("TaskCanceledException", StringComparison.Ordinal)) continue;
            if (clause.Filter?.FilterExpression.ToString() is { } filter && (filter.Contains("Cancel", StringComparison.Ordinal))) continue;
            var member = clause.Ancestors().Select(n => n switch { MethodDeclarationSyntax m => m.Identifier.ValueText, LocalFunctionStatementSyntax l => l.Identifier.ValueText, _ => null }).FirstOrDefault(n => n is not null) ?? "";
            if (Regex.IsMatch(member, @"^(Try|Can|Is|Has)[A-Z]") || Regex.IsMatch(member, @"(Dispose|Cleanup|CleanUp|Close|Finalize)", RegexOptions.IgnoreCase)) continue;
            if (clause.Ancestors().OfType<FinallyClauseSyntax>().Any()) continue;
            if (Intentional.IsMatch(clause.Block.ToFullString()) || Intentional.IsMatch(clause.GetLeadingTrivia().ToFullString())) continue;
            var body = clause.Block;
            var invocations = body.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
            var handled = body.DescendantNodes().Any(n => n is ThrowStatementSyntax or ThrowExpressionSyntax)
                || invocations.Any(i => Classify(i) is not null || Console_.IsMatch(i.Expression.ToString())
                    || ObsText.Name(i) is "TrackException" or "RecordException" or "AddException" or "SetStatus" or "Fail" or "SetException" or "TrySetException" or "LogException" or "ReportError"
                    || DeadLetter.IsMatch(ObsText.Name(i)) || ObsText.Name(i).StartsWith("Log", StringComparison.Ordinal));
            if (handled) continue;
            var empty = body.Statements.Count == 0;
            var returns = body.Statements.Any(s => s is ReturnStatementSyntax { Expression: not null });
            var variable = clause.Declaration?.Identifier.ValueText;
            // The failure is handed to the caller inside the returned value (an error result, health status or problem response).
            var surfaced = returns && variable is { Length: > 0 } && body.Statements.OfType<ReturnStatementSyntax>()
                .Any(r => r.Expression!.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.ValueText == variable));
            if (surfaced)
            {
                sink.Add(ObservabilityCategory.CatchAndSwallow, ObservabilityFindingKind.Finding, ObservabilitySeverity.Info, ArchitectureEvidenceState.Confirmed, component, ".NET",
                    "Exception returned to the caller as an error result", "The exception (or its message) is placed in the returned result instead of being logged here; whether the caller logs it is not traced.",
                    ObsText.Evidence(f, clause, "Catch returning an error result"));
                continue;
            }
            sink.Add(ObservabilityCategory.CatchAndSwallow, ObservabilityFindingKind.Finding, empty ? ObservabilitySeverity.Warning : ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Confirmed, component, ".NET",
                empty ? "Empty catch block" : returns ? "Exception converted to a return value without logging" : "Exception caught without logging or rethrow",
                empty ? "The exception is dropped without any trace." : returns ? "The failure becomes a return value; nothing records why." : "The catch block neither logs, rethrows nor records the exception.",
                ObsText.Evidence(f, clause, empty ? "Empty catch" : "Catch without log/rethrow"));
        }
    }

    /// <summary>Message handlers: Azure SDK processor callbacks, Wolverine/MassTransit/NServiceBus handler classes, RabbitMQ/Kafka receive callbacks.</summary>
    private static int Handlers(FindingSink sink, ObsComponent c, ObsFile f)
    {
        var bodies = new List<(SyntaxNode Body, string Kind, bool Error, bool FrameworkLogs, SyntaxNode Anchor)>();
        var methods = f.Root.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        foreach (var assign in f.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)))
        {
            var target = assign.Left switch { MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText, IdentifierNameSyntax i => i.Identifier.ValueText, _ => "" };
            if (target is not ("ProcessMessageAsync" or "ProcessEventAsync" or "ProcessErrorAsync" or "ProcessSessionMessageAsync" or "Received" or "ReceivedAsync")) continue;
            var error = target == "ProcessErrorAsync";
            SyntaxNode? body = assign.Right switch
            {
                LambdaExpressionSyntax l => l.Body,
                IdentifierNameSyntax i => methods.FirstOrDefault(m => m.Identifier.ValueText == i.Identifier.ValueText) is { } m ? (SyntaxNode?)m.Body ?? m.ExpressionBody : null,
                MemberAccessExpressionSyntax ma => methods.FirstOrDefault(m => m.Identifier.ValueText == ma.Name.Identifier.ValueText) is { } m ? (SyntaxNode?)m.Body ?? m.ExpressionBody : null,
                _ => null,
            };
            if (body is not null) bodies.Add((body, target is "Received" or "ReceivedAsync" ? "RabbitMQ consumer callback" : $"{target} handler", error, false, assign));
        }
        var wolverine = c.HasPackage("WolverineFx") || c.HasPackage("Wolverine");
        foreach (var type in f.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var name = type.Identifier.ValueText;
            var bases = type.BaseList?.Types.Select(t => t.ToString()).ToList() ?? [];
            var framework = bases.Any(b => b.StartsWith("IConsumer<", StringComparison.Ordinal)) ? "MassTransit consumer"
                : bases.Any(b => b.StartsWith("IHandleMessages<", StringComparison.Ordinal)) ? "NServiceBus/Rebus handler"
                : wolverine && (name.EndsWith("Handler", StringComparison.Ordinal) || name.EndsWith("Consumer", StringComparison.Ordinal)) ? "Wolverine handler" : null;
            if (framework is null) continue;
            foreach (var m in type.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText is "Handle" or "HandleAsync" or "Consume" or "ConsumeAsync" or "Before" or "After" && m.Modifiers.Any(SyntaxKind.PublicKeyword) || m.Modifiers.Any(SyntaxKind.StaticKeyword) && m.Identifier.ValueText is "Handle" or "HandleAsync"))
                if (((SyntaxNode?)m.Body ?? m.ExpressionBody) is { } body) bodies.Add((body, framework, false, true, m));
        }
        foreach (var (body, kind, error, frameworkLogs, anchor) in bodies.DistinctBy(b => b.Body))
        {
            var logs = body.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => Classify(i) is not null);
            if (logs) continue;
            if (error)
                sink.Add(ObservabilityCategory.HandlerObservability, ObservabilityFindingKind.Finding, ObservabilitySeverity.Warning, ArchitectureEvidenceState.Confirmed, c.Id, kind,
                    "Message error handler does not log", "The processor's error callback does not log, so receive/processing failures may leave no trace.", ObsText.Evidence(f, anchor, "Error handler without log"));
            else
                sink.Add(ObservabilityCategory.HandlerObservability, ObservabilityFindingKind.Finding, frameworkLogs ? ObservabilitySeverity.Info : ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Confirmed, c.Id, kind,
                    "Message handler has no logging", frameworkLogs ? "The handler body does not log. The framework logs handler execution and failures by default; business outcome is not visible."
                        : "The handler body does not log; message receipt and outcome are not visible in application logs.",
                    ObsText.Evidence(f, anchor, "Handler without log"), frameworkLogs ? "Framework-level logging is configured outside source and not assessed." : null);
        }
        return bodies.DistinctBy(b => b.Body).Count();
    }

    private static void Retry(FindingSink sink, string component, ObsFile f)
    {
        if (RetryConfigured.Match(f.Text) is { Success: true } retry)
        {
            if (RetryObserved.IsMatch(f.Text))
                sink.Add(ObservabilityCategory.RetryFailure, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, ArchitectureEvidenceState.Confirmed, component, "Polly / resilience",
                    "Retry with observable attempts", "A retry policy has an OnRetry callback or built-in resilience telemetry.", ObsText.Evidence(f, retry.Index, "Retry policy with telemetry"));
            else
                sink.Add(ObservabilityCategory.RetryFailure, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Confirmed, component, "Polly / resilience",
                    "Retry attempts may not be observable", "A retry policy is configured without an OnRetry callback or resilience telemetry, so transient failures may be invisible until retries run out.",
                    ObsText.Evidence(f, retry.Index, "Retry policy without OnRetry"));
        }
        if (FrameworkRetry.Match(f.Text) is { Success: true } fwRetry)
            sink.Add(ObservabilityCategory.RetryFailure, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, ArchitectureEvidenceState.Confirmed, component, "SDK / framework",
                "Framework-managed retries", "Retries are handled by the SDK or messaging framework; their logging comes from that framework's event sources, not from this source.",
                ObsText.Evidence(f, fwRetry.Index, "Framework retry options"));
        foreach (var call in f.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => DeadLetter.IsMatch(ObsText.Name(i))))
        {
            var scope = (SyntaxNode?)call.Ancestors().FirstOrDefault(n => n is CatchClauseSyntax or BlockSyntax { Parent: MethodDeclarationSyntax or LocalFunctionStatementSyntax or LambdaExpressionSyntax })
                ?? call.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var logged = scope?.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => Classify(i) is not null) ?? false;
            if (logged)
                sink.Add(ObservabilityCategory.RetryFailure, ObservabilityFindingKind.Observation, ObservabilitySeverity.Info, ArchitectureEvidenceState.Confirmed, component, "Messaging",
                    "Dead-letter / abandon with logging", "The message is dead-lettered or abandoned next to a log call.", ObsText.Evidence(f, call, "Dead-letter with log"));
            else
                sink.Add(ObservabilityCategory.RetryFailure, ObservabilityFindingKind.Finding, ObservabilitySeverity.NeedsReview, ArchitectureEvidenceState.Confirmed, component, "Messaging",
                    "Message dead-lettered or abandoned without a log entry", "The message leaves normal processing, but the surrounding code writes no log explaining why.",
                    ObsText.Evidence(f, call, "Dead-letter without log"));
        }
    }
}
