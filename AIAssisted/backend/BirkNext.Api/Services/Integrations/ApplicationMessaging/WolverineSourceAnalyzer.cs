using System.Xml.Linq;
using BirkNext.Integrations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BirkNext.Api.Services.Integrations.ApplicationMessaging;

/// <summary>
/// Wolverine application-messaging evidence from uploaded source: syntax-only analysis (Roslyn parse, no compilation, nothing executed).
/// Every fact names the file and line it came from. The analyzer states what source CONFIGURES — never that a handler ran, a retry
/// happened or a message reached a dead-letter queue. Nothing is inferred from names: a handler mapping needs an exact message type
/// and exactly one handler in an established discovery scope; Service Bus or Event Hub usage alone never detects Wolverine.
/// </summary>
public static class WolverineSourceAnalyzer
{
    /// <summary>2 = routes carry resolved Service Bus entity names and direct Azure SDK routes are recorded.</summary>
    public const int Version = 2;

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);
    private static readonly string[] HandlerMethods = ["Handle", "HandleAsync", "Handles", "HandlesAsync", "Consume", "ConsumeAsync", "Consumes", "ConsumesAsync"];
    private static readonly string[] RetryActions = ["RetryWithCooldown", "RetryTimes", "RetryOnce", "ScheduleRetry", "ScheduleRetryIndefinitely", "RetryTwice"];
    private static readonly string[] ErrorActions = ["MoveToErrorQueue", "Requeue", "Discard", "PauseThenRequeue", "MoveToDeadLetterQueue"];

    private sealed record Project(string Name, string Dir, string Path, List<(string Id, string? Version)> Packages, List<string> References, string? Version, bool IsTest);

    private sealed record Wrapper(string Name, string Project, SourceLocation Location, bool MediatorOnly, bool Transport, string? EfTransactionsParameter,
        int EfTransactionsIndex, List<string> ConfigurationKeys, bool IncludesAssemblyParameters);

    private sealed record Code(string Path, SyntaxTree Tree, CompilationUnitSyntax Root, Project? Project);

    public static ApplicationMessagingEvidenceSet Analyze(string environmentId, IReadOnlyList<SourceArchive> archives, IReadOnlyList<SourceFile> files, DateTimeOffset now)
    {
        var projects = Projects(files);
        var code = files.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(f => { var tree = CSharpSyntaxTree.ParseText(BlankPrimaryConstructors(f.Content), Parse, f.Path); return new Code(f.Path, tree, tree.GetCompilationUnitRoot(), Owner(projects, f.Path)); })
            .Where(c => c.Project is { IsTest: false })
            .ToList();
        var settings = Settings(files, projects);
        var wrappers = Wrappers(code);
        var observability = ObservabilitySources(code);
        var applications = projects.Where(p => !p.IsTest && code.Any(c => c.Project == p && System.IO.Path.GetFileName(c.Path) == "Program.cs"))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => Application(p, projects, code, wrappers, observability, settings))
            .ToList();
        return new ApplicationMessagingEvidenceSet
        {
            EnvironmentId = environmentId, AnalyzedAt = now, AnalyzerVersion = Version, Archives = archives.ToList(), Applications = applications,
            Limitations = SetLimitations(applications, projects, code),
        };
    }

    /// <summary>
    /// The bundled Roslyn parser (4.5, the version the EF Core design tools pin) predates C# 12 class/struct primary constructors and loses the
    /// class members after one. The parameter list is blanked to spaces (newlines kept, so line numbers stay exact) before syntax-only parsing.
    /// </summary>
    internal static string BlankPrimaryConstructors(string source)
    {
        var chars = source.ToCharArray();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(source, @"\b(?:class|struct)\s+[A-Za-z_]\w*\s*(?:<[^<>(){};]*>)?\s*\("))
        {
            var depth = 0;
            for (var i = match.Index + match.Length - 1; i < chars.Length; i++)
            {
                if (chars[i] == '(') depth++;
                else if (chars[i] == ')') depth--;
                if (chars[i] is not ('\n' or '\r')) chars[i] = ' ';
                if (depth == 0) break;
            }
        }
        return new string(chars);
    }

    // ── Projects and packages ───────────────────────────────────────────────────────────────────────────────────────

    private static List<Project> Projects(IReadOnlyList<SourceFile> files)
    {
        var central = files.Where(f => System.IO.Path.GetFileName(f.Path).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Dir: Dir(f.Path), Versions: Xml(f.Content)?.Descendants().Where(e => e.Name.LocalName == "PackageVersion")
                .Where(e => e.Attribute("Include") is not null).ToDictionary(e => e.Attribute("Include")!.Value, e => e.Attribute("Version")?.Value, StringComparer.OrdinalIgnoreCase) ?? []))
            .ToList();
        var result = new List<Project>();
        foreach (var file in files.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            var xml = Xml(file.Content);
            if (xml is null) continue;
            var dir = Dir(file.Path);
            var versions = central.Where(c => dir.StartsWith(c.Dir, StringComparison.Ordinal)).OrderByDescending(c => c.Dir.Length).Select(c => c.Versions).FirstOrDefault() ?? [];
            var packages = xml.Descendants().Where(e => e.Name.LocalName == "PackageReference" && e.Attribute("Include") is not null)
                .Select(e => (e.Attribute("Include")!.Value, e.Attribute("Version")?.Value ?? (versions.TryGetValue(e.Attribute("Include")!.Value, out var v) ? v : null))).ToList();
            var references = xml.Descendants().Where(e => e.Name.LocalName == "ProjectReference" && e.Attribute("Include") is not null)
                .Select(e => Normalize(dir + e.Attribute("Include")!.Value.Replace('\\', '/'))).ToList();
            var name = System.IO.Path.GetFileNameWithoutExtension(file.Path);
            var isTest = packages.Any(p => p.Item1.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
                || file.Path.Split('/').Any(s => s.Equals("tests", StringComparison.OrdinalIgnoreCase) || s.Equals("test", StringComparison.OrdinalIgnoreCase));
            result.Add(new Project(name, dir, file.Path, packages, references, xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value, isTest));
        }
        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex SecretKey = new("(connectionstring|key|secret|password|token|sas|credential|pwd)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex SecretValue = new("(sharedaccesskey|accountkey|password=|pwd=|sig=|secret=)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Non-secret string values of each project's base appsettings.json, flattened to "A:B:C". Used only to resolve entity names.</summary>
    private static Dictionary<Project, Dictionary<string, (string Value, SourceLocation Where)>> Settings(IReadOnlyList<SourceFile> files, List<Project> projects)
    {
        var result = new Dictionary<Project, Dictionary<string, (string, SourceLocation)>>();
        foreach (var file in files.Where(f => System.IO.Path.GetFileName(f.Path).Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)))
        {
            if (Owner(projects, file.Path) is not { } project) continue;
            var values = new Dictionary<string, (string, SourceLocation)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(file.Content, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                void Walk(System.Text.Json.JsonElement e, string prefix)
                {
                    if (e.ValueKind == System.Text.Json.JsonValueKind.Object) foreach (var p in e.EnumerateObject()) Walk(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}:{p.Name}");
                    else if (e.ValueKind == System.Text.Json.JsonValueKind.String && e.GetString() is { Length: > 0 and <= 200 } v && !SecretKey.IsMatch(prefix) && !SecretValue.IsMatch(v))
                        values[prefix] = (v, new SourceLocation(file.Path, LineOf(file.Content, prefix.Split(':').Last())));
                }
                Walk(doc.RootElement, "");
            }
            catch (System.Text.Json.JsonException) { continue; }
            result[project] = values;
        }
        return result;
    }

    private static int LineOf(string content, string key)
    {
        var at = content.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        return at < 0 ? 1 : content[..at].Count(c => c == '\n') + 1;
    }

    private static XDocument? Xml(string content)
    {
        try { return XDocument.Parse(content); } catch (System.Xml.XmlException) { return null; }
    }

    private static string Dir(string path) => path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";

    private static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (part is not ("." or "")) parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static Project? Owner(List<Project> projects, string path) =>
        projects.Where(p => path.StartsWith(p.Dir, StringComparison.Ordinal)).OrderByDescending(p => p.Dir.Length).FirstOrDefault();

    private static List<Project> Closure(Project root, List<Project> projects)
    {
        var seen = new List<Project>();
        var queue = new Queue<Project>([root]);
        while (queue.TryDequeue(out var p))
        {
            if (seen.Contains(p)) continue;
            seen.Add(p);
            foreach (var reference in p.References)
                if (projects.FirstOrDefault(x => x.Path == reference) is { } found) queue.Enqueue(found);
        }
        return seen;
    }

    private static SourceLocation Location(SyntaxNode node) => new(node.SyntaxTree.FilePath, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);

    private static string Short(string text, int max = 120)
    {
        var flat = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static string? InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        _ => null,
    };

    private static SimpleNameSyntax? InvokedSimpleName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name,
        SimpleNameSyntax s => s,
        _ => null,
    };

    // ── Registration wrappers (e.g. M2LB.Common.Messaging.AddM2LbWolverine) ─────────────────────────────────────────

    /// <summary>Methods whose body calls UseWolverine (directly, or through another such method) — each with what its body configures.</summary>
    private static List<Wrapper> Wrappers(List<Code> code)
    {
        var names = new HashSet<string>(["UseWolverine"], StringComparer.Ordinal);
        var result = new List<Wrapper>();
        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var c in code)
            foreach (var method in c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var name = method.Identifier.ValueText;
                if (names.Contains(name) && result.Any(w => w.Name == name)) continue;
                var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
                if (!invocations.Any(i => InvokedName(i) is { } n && names.Contains(n) && n != name)) continue;
                var parameters = method.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToList();
                var ef = invocations.FirstOrDefault(i => InvokedName(i) == "UseEntityFrameworkCoreTransactions");
                var guard = ef?.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault()?.Condition is IdentifierNameSyntax id && parameters.Contains(id.Identifier.ValueText) ? id.Identifier.ValueText : null;
                var keys = (method.Parent as TypeDeclarationSyntax ?? (SyntaxNode)method).DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression))
                    .Where(l => l.Parent?.Parent?.Parent is ElementAccessExpressionSyntax || l.Parent?.Parent?.Parent is InvocationExpressionSyntax inv && InvokedName(inv) == "GetConnectionString")
                    .Select(l => l.Parent?.Parent?.Parent is InvocationExpressionSyntax gc && InvokedName(gc) == "GetConnectionString" ? $"ConnectionStrings:{l.Token.ValueText}" : l.Token.ValueText)
                    .Distinct().ToList();
                result.Add(new Wrapper(name, c.Project?.Name ?? "", Location(method),
                    MediatorOnly: method.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(m => m.Name.Identifier.ValueText == "MediatorOnly"),
                    Transport: invocations.Any(i => InvokedName(i) == "UseAzureServiceBus"),
                    EfTransactionsParameter: guard, EfTransactionsIndex: guard is null ? -1 : parameters.IndexOf(guard) - (method.ParameterList.Parameters.FirstOrDefault()?.Modifiers.Any(SyntaxKind.ThisKeyword) == true ? 1 : 0),
                    ConfigurationKeys: keys,
                    IncludesAssemblyParameters: invocations.Any(i => InvokedName(i) == "IncludeAssembly") && method.ParameterList.Parameters.Any(p => p.Modifiers.Any(SyntaxKind.ParamsKeyword))));
                names.Add(name);
            }
        }
        return result;
    }

    /// <summary>Tracing sources and meters registered by observability setup methods, per method name (e.g. AddM2LbObservability).</summary>
    private static Dictionary<string, (List<string> Sources, SourceLocation Location)> ObservabilitySources(List<Code> code)
    {
        var result = new Dictionary<string, (List<string>, SourceLocation)>(StringComparer.Ordinal);
        foreach (var c in code)
        foreach (var method in c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
            if (!invocations.Any(i => InvokedName(i) is "UseAzureMonitor" or "AddOpenTelemetry")) continue;
            var sources = invocations.Where(i => InvokedName(i) is "AddSource" or "AddMeter")
                .SelectMany(i => i.ArgumentList.Arguments).Select(a => a.Expression is LiteralExpressionSyntax l ? l.Token.ValueText : Short(a.Expression.ToString(), 60)).ToList();
            result[method.Identifier.ValueText] = (sources, Location(method));
        }
        return result;
    }

    // ── One application ─────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Builder
    {
        public readonly List<MessagingFact> Facts = [];
        public readonly List<MessagingRoute> Routes = [];
        public readonly List<MessagingFailureRule> Rules = [];
        public readonly List<string> Limitations = [];
        public readonly HashSet<string> ScopeProjects = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (string Value, SourceLocation Where)> Settings = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Text, SourceLocation Where, bool Storage)> Persistence = [];
        public MessagingFact Fact(string id) => Facts.First(f => f.Id == id);
        public void Fact(string id, string label, MessagingFactState state, string detail, IntegrationEvidenceSource source, params SourceLocation[] where) =>
            Facts.Add(new MessagingFact { Id = id, Label = label, State = state, Detail = detail, Source = source, Locations = where.Take(6).ToList() });
    }

    private static ApplicationMessagingEvidence Application(Project app, List<Project> projects, List<Code> allCode, List<Wrapper> wrappers,
        Dictionary<string, (List<string> Sources, SourceLocation Location)> observability, Dictionary<Project, Dictionary<string, (string Value, SourceLocation Where)>> settings)
    {
        var closure = Closure(app, projects);
        var code = allCode.Where(c => c.Project is not null && closure.Contains(c.Project)).ToList();
        var b = new Builder();
        // The application's own appsettings.json first, then its referenced projects'.
        foreach (var p in closure) if (settings.TryGetValue(p, out var values)) foreach (var (k, v) in values) b.Settings.TryAdd(k, v);
        var sdk = SdkRoutes(code, b);
        var wrapperNames = wrappers.Select(w => w.Name).ToHashSet(StringComparer.Ordinal);
        var invocations = code.SelectMany(c => c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()).ToList();

        // Package evidence: direct WolverineFx references, or a referenced package whose analyzed project references WolverineFx.
        var packageLines = new List<string>();
        var packageWhere = new List<SourceLocation>();
        foreach (var p in closure)
        foreach (var (id, version) in p.Packages)
        {
            if (id.StartsWith("WolverineFx", StringComparison.OrdinalIgnoreCase)) { packageLines.Add($"{id} {version ?? "(version not in source)"} in {p.Name}"); packageWhere.Add(new(p.Path, 1)); }
            else if (projects.FirstOrDefault(x => x.Name.Equals(id, StringComparison.OrdinalIgnoreCase) && !closure.Contains(x)) is { } library
                     && library.Packages.Where(lp => lp.Id.StartsWith("WolverineFx", StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } wolverine)
            {
                packageLines.Add($"{p.Name} → {id} {version} → {string.Join(", ", wolverine.Select(w => $"{w.Id} {w.Version ?? "(version not in source)"}"))} (analyzed source of {id} {library.Version ?? "unknown version"})");
                packageWhere.Add(new(p.Path, 1));
                packageWhere.Add(new(library.Path, 1));
                if (version is not null && library.Version is not null && version != library.Version)
                    b.Limitations.Add($"{p.Name} references {id} {version}; the analyzed {id} source is {library.Version}. The Wolverine package version of {version} is not established by this source.");
            }
        }
        // Packages that may wrap Wolverine but whose source is not analyzed (vendor SDKs such as Azure.Messaging.* are not wrappers).
        var wrapperPackages = closure.SelectMany(p => p.Packages).Where(pk => !pk.Id.StartsWith("WolverineFx", StringComparison.OrdinalIgnoreCase)
                && pk.Id.Contains("Messaging", StringComparison.OrdinalIgnoreCase)
                && !pk.Id.StartsWith("Azure.", StringComparison.OrdinalIgnoreCase) && !pk.Id.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) && !pk.Id.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                && !projects.Any(x => x.Name.Equals(pk.Id, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(pk => (pk.Id, pk.Version)).ToList();

        // Registration: UseWolverine directly, or an analyzed wrapper; plus Wolverine namespaces in source.
        var registrations = invocations.Where(i => InvokedName(i) is { } n && (n == "UseWolverine" || wrapperNames.Contains(n))).ToList();
        var entryCalls = registrations.Where(r => !r.Ancestors().OfType<MethodDeclarationSyntax>().Any(m => wrapperNames.Contains(m.Identifier.ValueText))).ToList();
        var usings = code.SelectMany(c => c.Root.Usings).Where(u => u.Name?.ToString() is { } n && (n == "Wolverine" || n.StartsWith("Wolverine.", StringComparison.Ordinal))).ToList();
        var unresolvedWrapperCalls = invocations.Where(i => InvokedName(i) is { } n && n.Contains("Wolverine", StringComparison.Ordinal) && n.StartsWith("Add", StringComparison.Ordinal) && !wrapperNames.Contains(n)).ToList();

        var detected = registrations.Count > 0 || usings.Count > 0 || packageLines.Count > 0;
        var sourceEvidence = registrations.Count > 0 && (usings.Count > 0 || registrations.Any(r => InvokedName(r) == "UseWolverine"));
        // No registration at all (resolved or not) means Wolverine is not configured in this application, even when its packages or types are
        // present through a referenced project.
        var detection = registrations.Count == 0 && unresolvedWrapperCalls.Count == 0 ? MessagingDetection.NotDetected
            : registrations.Count > 0 && (packageLines.Count > 0 || sourceEvidence) ? MessagingDetection.Confirmed
            : MessagingDetection.Likely;
        var reason = detection switch
        {
            MessagingDetection.Confirmed => packageLines.Count > 0
                ? "Wolverine registration in source and a package chain to WolverineFx."
                : "Wolverine registration and Wolverine namespaces in source (the package chain is not in the analyzed archives).",
            MessagingDetection.Likely => registrations.Count == 0
                ? $"{string.Join(", ", unresolvedWrapperCalls.Select(InvokedName).Distinct())} is called, but its source is not in the analyzed archives, so it cannot be shown to register Wolverine."
                : "A Wolverine registration is called, but neither a WolverineFx package chain nor Wolverine namespaces are in the analyzed source.",
            _ => detected
                ? "Wolverine packages or types are present through referenced projects, but this application does not register Wolverine."
                : "No Wolverine package, namespace or registration in the analyzed source. Service Bus or Event Hub usage alone is not Wolverine evidence.",
        };

        if (detection == MessagingDetection.NotDetected)
        {
            if (packageLines.Count > 0)
                b.Fact("package", "Wolverine package", MessagingFactState.Detected, string.Join("; ", packageLines) + ". Present, but not registered by this application.",
                    IntegrationEvidenceSource.PackageManifest, [.. packageWhere]);
            TransportOnlyFacts(b, code);
            return new ApplicationMessagingEvidence
            {
                ApplicationId = app.Name, TelemetryRoleName = RoleName(invocations), Detection = detection, DetectionReason = reason,
                HandlerMapping = MessagingFactState.NotApplicable, RetryPolicy = MessagingFactState.NotApplicable, Outbox = MessagingFactState.NotApplicable,
                ErrorHandling = MessagingFactState.NotApplicable, Facts = b.Facts, Limitations = b.Limitations, SdkRoutes = sdk,
            };
        }

        b.Fact("package", "Wolverine package", packageLines.Count > 0 ? MessagingFactState.Detected : MessagingFactState.NotAssessable,
            packageLines.Count > 0 ? string.Join("; ", packageLines)
                : wrapperPackages.Count > 0 ? $"No direct WolverineFx reference. Referenced {string.Join(", ", wrapperPackages.Select(p => $"{p.Id} {p.Version}"))}, whose source is not in the analyzed archives." : "No WolverineFx package reference in the analyzed project files.",
            IntegrationEvidenceSource.PackageManifest, [.. packageWhere]);

        var registrationLines = registrations.Select(r => InvokedName(r)!).Distinct().ToList();
        var resolved = wrappers.Where(w => registrationLines.Contains(w.Name)).ToList();
        b.Fact("registration", "Wolverine registration", registrations.Count > 0 ? MessagingFactState.Configured : unresolvedWrapperCalls.Count > 0 ? MessagingFactState.NotAssessable : MessagingFactState.NotFound,
            registrations.Count > 0
                ? string.Join("; ", registrationLines.Select(n => wrappers.FirstOrDefault(w => w.Name == n) is { } w ? $"{n} (defined in {w.Project}; calls UseWolverine)" : n))
                    + (usings.Count > 0 ? $". Wolverine namespaces used in {usings.Select(u => u.SyntaxTree.FilePath).Distinct().Count()} file(s)." : "")
                : unresolvedWrapperCalls.Count > 0
                    ? $"{string.Join(", ", unresolvedWrapperCalls.Select(InvokedName).Distinct())} is called; its source is not in the analyzed archives. Upload the library's source to establish what it registers."
                    : "No UseWolverine call or analyzed Wolverine registration method.",
            IntegrationEvidenceSource.SourceCode, [.. registrations.Concat(unresolvedWrapperCalls).Select(Location), .. usings.Take(3).Select(u => Location(u))]);

        var serviceName = ServiceName(entryCalls.Concat(registrations), resolved);
        var mediatorOnly = resolved.Any(w => w.MediatorOnly) && !resolved.Any(w => w.Transport)
            || registrations.Any(r => r.ArgumentList.ToString().Contains("MediatorOnly", StringComparison.Ordinal));
        if (mediatorOnly)
            b.Fact("mode", "Wolverine mode", MessagingFactState.Configured, "In-process mediator only (DurabilityMode.MediatorOnly): no Wolverine transport listens or sends.", IntegrationEvidenceSource.SourceCode,
                [.. resolved.Where(w => w.MediatorOnly).Select(w => w.Location)]);

        // Transport binding: what source configures, with the configuration keys whose VALUES are deployment settings (never read).
        var directTransport = invocations.Where(i => InvokedName(i) == "UseAzureServiceBus").ToList();
        var transportWrapper = resolved.FirstOrDefault(w => w.Transport);
        if (!mediatorOnly)
            b.Fact("transport", "Azure Service Bus transport", transportWrapper is not null || directTransport.Count > 0 ? MessagingFactState.Configured : MessagingFactState.NotFound,
                transportWrapper is not null
                    ? $"UseAzureServiceBus in {transportWrapper.Name}; connection from configuration {string.Join(", ", transportWrapper.ConfigurationKeys)} (values are deployment settings and are not read). Source registers no transport when those settings are absent."
                    : directTransport.Count > 0 ? "UseAzureServiceBus in application source; connection from deployment configuration." : "No Azure Service Bus transport registration in source.",
                IntegrationEvidenceSource.SourceCode, [.. directTransport.Select(Location), .. (transportWrapper is null ? Array.Empty<SourceLocation>() : [transportWrapper.Location])]);

        // EF Core transactions through the wrapper's bool parameter.
        foreach (var call in entryCalls.Concat(registrations).Distinct())
        {
            if (wrappers.FirstOrDefault(w => w.Name == InvokedName(call) && w.EfTransactionsParameter is not null) is not { } w) continue;
            var args = call.ArgumentList.Arguments;
            var arg = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == w.EfTransactionsParameter)
                ?? (w.EfTransactionsIndex >= 0 && w.EfTransactionsIndex < args.Count ? args[w.EfTransactionsIndex] : null);
            if (arg?.Expression is LiteralExpressionSyntax literal && literal.Token.Value is bool on)
                b.Fact("ef-transactions", "EF Core transactional middleware", on ? MessagingFactState.Configured : MessagingFactState.NotFound,
                    on ? $"{w.Name}(…, {w.EfTransactionsParameter}: true) calls UseEntityFrameworkCoreTransactions." : $"{w.Name}(…, {w.EfTransactionsParameter}: false): EF Core transactions are not enabled.",
                    IntegrationEvidenceSource.SourceCode, Location(call));
        }

        // Configuration lambdas passed to the registrations.
        // Also the lambdas of registration methods whose source is not analyzed: the WolverineOptions calls inside are the application's own source.
        foreach (var lambda in registrations.Concat(unresolvedWrapperCalls).SelectMany(r => r.ArgumentList.Arguments).Select(a => a.Expression).OfType<LambdaExpressionSyntax>())
            Walk(lambda.Body, [], b, code, projects);
        if (!mediatorOnly && transportWrapper is null && directTransport.Count == 0 && b.Routes.Count > 0 && unresolvedWrapperCalls.Count > 0)
            b.Facts[b.Facts.FindIndex(f => f.Id == "transport")] = b.Fact("transport") with
            {
                State = MessagingFactState.NotAssessable,
                Detail = $"Routes target Azure Service Bus, but the transport registration is inside {string.Join(", ", unresolvedWrapperCalls.Select(InvokedName).Distinct())}, whose source is not analyzed.",
            };
        foreach (var call in registrations.Where(r => resolved.Any(w => w.IncludesAssemblyParameters && w.Name == InvokedName(r))))
            foreach (var arg in call.ArgumentList.Arguments)
                if (TypeOfAssembly(arg.Expression) is { } type && ProjectOfType(type, code) is { } p) b.ScopeProjects.Add(p);
        // UseWolverine called directly from the application's own project puts that assembly in discovery.
        if (registrations.Any(r => InvokedName(r) == "UseWolverine" && code.Any(c => c.Tree == r.SyntaxTree && c.Project == app))) b.ScopeProjects.Add(app.Name);

        foreach (var mapped in invocations.Where(i => InvokedName(i) == "MapWolverineEnvelopeStorage"))
            b.Persistence.Add(($"Wolverine envelope tables mapped into {mapped.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "an EF Core model"} (MapWolverineEnvelopeStorage)", Location(mapped), false));
        var listenTypes = b.Routes.Where(r => r.Direction == MessagingRouteDirection.Listen && r.MessageType is not null).Select(r => r.MessageType!.Split('.').Last()).ToHashSet(StringComparer.Ordinal);
        var handlers = Handlers(code, b.ScopeProjects, registrations.Concat(unresolvedWrapperCalls).ToList(), wrappers, listenTypes);
        MapRoutes(b, handlers, code);
        FailureAndDurabilityFacts(b, mediatorOnly);
        TransportOnlyFacts(b, code);
        var role = RoleName(invocations);
        TelemetryFacts(b, invocations, code, observability, role);

        var listen = b.Routes.Where(r => r.Direction == MessagingRouteDirection.Listen).ToList();
        var handlerMapping = mediatorOnly
            ? handlers.Count == 0 ? MessagingFactState.NotFound : handlers.All(h => h.InDiscoveryScope == true) ? MessagingFactState.Available : MessagingFactState.NotAssessable
            : listen.Count == 0 ? MessagingFactState.NotApplicable
            : listen.All(r => r.HandlerMapping == MessagingFactState.Available) ? MessagingFactState.Available
            : listen.All(r => r.HandlerMapping is MessagingFactState.Available or MessagingFactState.NotFound) ? MessagingFactState.NotFound
            : MessagingFactState.NotAssessable;
        b.Fact("handler-discovery", "Handler discovery", handlers.Count == 0 ? MessagingFactState.NotFound : handlers.All(h => h.InDiscoveryScope == true) ? MessagingFactState.Configured : MessagingFactState.NotAssessable,
            handlers.Count == 0 ? "No Wolverine handler (…Handler/…Consumer with Handle/Consume) in the application's projects."
                : string.Join("; ", handlers.Select(h => $"{h.Type}.{h.Method}({h.MessageType}) — {h.DiscoveryReason}")),
            IntegrationEvidenceSource.SourceCode, [.. handlers.Where(h => h.Location is not null).Select(h => h.Location!)]);

        return new ApplicationMessagingEvidence
        {
            ApplicationId = app.Name, ServiceName = serviceName, TelemetryRoleName = role, Detection = detection, DetectionReason = reason,
            HandlerMapping = handlerMapping,
            RetryPolicy = mediatorOnly && b.Rules.Count == 0 ? MessagingFactState.NotApplicable : b.Rules.Any(r => r.Actions.Any(a => RetryActions.Any(a.StartsWith))) ? MessagingFactState.Configured : MessagingFactState.NotFound,
            Outbox = mediatorOnly ? MessagingFactState.NotApplicable : OutboxState(b),
            ErrorHandling = mediatorOnly && b.Rules.Count == 0 ? MessagingFactState.NotApplicable : b.Rules.Any(r => r.Actions.Any(a => ErrorActions.Any(a.StartsWith))) ? MessagingFactState.Configured : MessagingFactState.NotFound,
            Facts = b.Facts, Routes = b.Routes, Handlers = handlers, FailureRules = b.Rules, Limitations = b.Limitations, SdkRoutes = sdk,
        };
    }

    private static string? ServiceName(IEnumerable<InvocationExpressionSyntax> calls, List<Wrapper> resolved)
    {
        foreach (var call in calls)
        {
            if (resolved.Any(w => w.Name == InvokedName(call)) && call.ArgumentList.Arguments.Select(a => a.Expression).OfType<LiteralExpressionSyntax>()
                    .FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression)) is { } name) return name.Token.ValueText;
            if (call.DescendantNodes().OfType<AssignmentExpressionSyntax>().FirstOrDefault(a => a.Left is MemberAccessExpressionSyntax m && m.Name.Identifier.ValueText == "ServiceName")?.Right is LiteralExpressionSyntax l)
                return l.Token.ValueText;
        }
        return null;
    }

    private static string? RoleName(List<InvocationExpressionSyntax> invocations) =>
        invocations.Where(i => InvokedName(i) is { } n && n.Contains("Observability", StringComparison.Ordinal))
            .Select(i => i.ArgumentList.Arguments.Select(a => a.Expression).OfType<LiteralExpressionSyntax>().FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression))?.Token.ValueText)
            .FirstOrDefault(n => n is not null);

    // ── Configuration lambda walk ───────────────────────────────────────────────────────────────────────────────────

    private static void Walk(CSharpSyntaxNode body, List<string> conditions, Builder b, List<Code> code, List<Project> projects)
    {
        if (body is ExpressionSyntax expression) { Statement(expression, conditions, b, code); return; }
        if (body is not BlockSyntax block) return;
        var guards = new List<string>(conditions);
        foreach (var statement in block.Statements)
        {
            switch (statement)
            {
                case IfStatementSyntax { Statement: var then } ifs when IsReturnOnly(then):
                    guards.Add($"unless {Short(ifs.Condition.ToString(), 90)}");
                    break;
                case IfStatementSyntax ifs:
                    Walk(ifs.Statement, [.. guards, $"when {Short(ifs.Condition.ToString(), 90)}"], b, code, projects);
                    if (ifs.Else?.Statement is { } otherwise) Walk(otherwise, [.. guards, $"unless {Short(ifs.Condition.ToString(), 90)}"], b, code, projects);
                    break;
                case BlockSyntax inner:
                    Walk(inner, guards, b, code, projects);
                    break;
                case ExpressionStatementSyntax e:
                    Statement(e.Expression, guards, b, code);
                    break;
            }
        }
    }

    private static bool IsReturnOnly(StatementSyntax statement) =>
        statement is ReturnStatementSyntax || statement is BlockSyntax { Statements: [ReturnStatementSyntax] };

    private sealed record Link(string Name, List<TypeSyntax> TypeArguments, List<ArgumentSyntax> Arguments, SyntaxNode Node);

    /// <summary>a.B&lt;T&gt;(x).C(y) → [B&lt;T&gt;(x), C(y)], innermost first; member names without a call (Policies, Discovery) are links without arguments.</summary>
    private static List<Link> Chain(ExpressionSyntax expression)
    {
        var links = new List<Link>();
        var current = expression;
        while (true)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation when InvokedSimpleName(invocation) is { } name:
                    links.Add(new Link(name.Identifier.ValueText, name is GenericNameSyntax g ? g.TypeArgumentList.Arguments.ToList() : [], invocation.ArgumentList.Arguments.ToList(), invocation));
                    current = invocation.Expression is MemberAccessExpressionSyntax m ? m.Expression : null;
                    break;
                case MemberAccessExpressionSyntax member:
                    links.Add(new Link(member.Name.Identifier.ValueText, [], [], member));
                    current = member.Expression;
                    break;
                default:
                    links.Reverse();
                    return links;
            }
            if (current is null) { links.Reverse(); return links; }
        }
    }

    private static void Statement(ExpressionSyntax expression, List<string> conditions, Builder b, List<Code> code)
    {
        if (expression is AssignmentExpressionSyntax) return;
        var links = Chain(expression);
        var condition = conditions.Count == 0 ? null : string.Join("; ", conditions);
        var where = Location(expression);
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            var rest = links.Skip(i + 1).ToList();
            switch (link.Name)
            {
                case "PublishMessage" or "PublishAllMessages":
                {
                    var message = link.TypeArguments.FirstOrDefault()?.ToString();
                    var target = rest.FirstOrDefault(l => l.Name is "ToAzureServiceBusQueue" or "ToAzureServiceBusTopic");
                    var (entityName, entitySource) = Resolve(target?.Arguments.FirstOrDefault()?.Expression, code, b.Settings);
                    b.Routes.Add(new MessagingRoute
                    {
                        Direction = MessagingRouteDirection.Publish, MessageType = message,
                        EndpointKind = target?.Name == "ToAzureServiceBusTopic" ? MessagingEndpointKind.Topic : MessagingEndpointKind.Queue,
                        Endpoint = target is null ? "(no Azure Service Bus endpoint in source)" : Describe(target.Arguments.FirstOrDefault()?.Expression, code),
                        EntityName = entityName, EntityNameSource = entitySource,
                        Condition = condition, Options = Options(rest), Location = where,
                    });
                    return;
                }
                case "ListenToAzureServiceBusQueue" or "ListenToAzureServiceBusSubscription":
                {
                    var subscription = link.Name == "ListenToAzureServiceBusSubscription";
                    var defaultType = rest.FirstOrDefault(l => l.Name == "DefaultIncomingMessage")?.TypeArguments.FirstOrDefault()?.ToString();
                    var mapper = rest.FirstOrDefault(l => l.Name == "InteropWith")?.Arguments.FirstOrDefault()?.Expression is ObjectCreationExpressionSyntax created ? created.Type.ToString() : null;
                    var mapped = mapper is null ? null : MapperMessageType(mapper, code);
                    var (entityName, entitySource) = Resolve(link.Arguments.FirstOrDefault()?.Expression, code, b.Settings);
                    var (topicName, _) = subscription && rest.FirstOrDefault(l => l.Name == "FromTopic") is { } fromTopic ? Resolve(fromTopic.Arguments.FirstOrDefault()?.Expression, code, b.Settings) : (null, null);
                    b.Routes.Add(new MessagingRoute
                    {
                        Direction = MessagingRouteDirection.Listen, EndpointKind = subscription ? MessagingEndpointKind.Subscription : MessagingEndpointKind.Queue,
                        Endpoint = Describe(link.Arguments.FirstOrDefault()?.Expression, code),
                        EntityName = entityName, EntityNameSource = entitySource, TopicName = topicName,
                        Topic = subscription ? rest.FirstOrDefault(l => l.Name == "FromTopic") is { } from ? Describe(from.Arguments.FirstOrDefault()?.Expression, code) : null : null,
                        MessageType = defaultType ?? mapped,
                        MappingReason = defaultType is not null ? "Message type from DefaultIncomingMessage<T>." : mapped is not null ? $"Message type set by {mapper}.MapIncomingToEnvelope." : null,
                        Condition = condition, Options = Options(rest), Location = where,
                    });
                    return;
                }
                case "OnException" or "OnAnyException":
                {
                    var actions = new List<string>();
                    var delays = new List<string>();
                    foreach (var action in rest)
                    {
                        if (action.Name is "Then" or "And" or "Or") continue;
                        var args = action.Arguments.Select(a => Short(a.Expression.ToString(), 40)).ToList();
                        actions.Add(args.Count == 0 ? action.Name : $"{action.Name}({string.Join(", ", args)})");
                        if (action.Name is "RetryWithCooldown" or "ScheduleRetry" or "PauseThenRequeue") delays.AddRange(args);
                    }
                    b.Rules.Add(new MessagingFailureRule
                    {
                        ExceptionType = link.TypeArguments.FirstOrDefault()?.ToString() ?? (link.Name == "OnAnyException" ? "Any exception" : "Exception"),
                        Condition = link.Arguments.FirstOrDefault()?.Expression is { } filter ? Short(filter.ToString(), 120) : null,
                        Actions = actions, Delays = delays, Location = where,
                    });
                    return;
                }
                case "PersistMessagesWithSqlServer" or "PersistMessagesWithPostgresql":
                {
                    var schema = link.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "schema")?.Expression ?? link.Arguments.Skip(1).FirstOrDefault()?.Expression;
                    var store = link.Name == "PersistMessagesWithSqlServer" ? "SQL Server" : "PostgreSQL";
                    b.Persistence.Add(($"{store} message storage{(schema is null ? "" : $" (schema {Describe(schema, code)})")}{(condition is null ? "" : $" — {condition}")}", where, true));
                    return;
                }
                case "UseDurableLocalQueues" or "UseDurableOutboxOnAllSendingEndpoints" or "UseDurableInboxOnAllListeners" or "UseEntityFrameworkCoreTransactions":
                    b.Persistence.Add(($"{link.Name}{(condition is null ? "" : $" — {condition}")}", where, false));
                    return;
                case "IncludeAssembly":
                    if (TypeOfAssembly(link.Arguments.FirstOrDefault()?.Expression) is { } type && ProjectOfType(type, code) is { } project) b.ScopeProjects.Add(project);
                    return;
            }
        }
    }

    private static List<string> Options(List<Link> rest) =>
        rest.Where(l => l.Name is "ProcessInline" or "BufferedInMemory" or "UseDurableInbox" or "UseDurableOutbox" or "SendInline" or "InteropWith" or "MaximumParallelMessages")
            .Select(l => l.Name == "InteropWith" && l.Arguments.FirstOrDefault()?.Expression is ObjectCreationExpressionSyntax o ? $"InteropWith({o.Type})" : l.Name).ToList();

    /// <summary>A literal, a local variable's initializer, a configuration lookup and its "?? default" — never the configuration value itself.</summary>
    private static string Describe(ExpressionSyntax? expression, List<Code> code, int depth = 0)
    {
        switch (expression)
        {
            case null: return "(not in source)";
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression): return literal.Token.ValueText;
            case PostfixUnaryExpressionSyntax suppressed when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression): return Describe(suppressed.Operand, code, depth);
            case ParenthesizedExpressionSyntax parenthesized: return Describe(parenthesized.Expression, code, depth);
            case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                return $"{Describe(coalesce.Left, code, depth + 1)}, default \"{Describe(coalesce.Right, code, depth + 1)}\"";
            case ElementAccessExpressionSyntax access when access.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax key:
                return $"configuration {key.Token.ValueText}";
            case IdentifierNameSyntax identifier when depth < 3:
                var declarator = code.Where(c => c.Tree == expression.SyntaxTree).Concat(code).SelectMany(c => c.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                    .FirstOrDefault(v => v.Identifier.ValueText == identifier.Identifier.ValueText && v.Initializer is not null);
                return declarator?.Initializer is { } init ? Describe(init.Value, code, depth + 1) : identifier.Identifier.ValueText;
            default: return Short(expression.ToString(), 80);
        }
    }

    // Entity-shaped names: lower-case Service Bus entity characters. Constants must also contain '.' or '-' to avoid unrelated strings.
    private static readonly System.Text.RegularExpressions.Regex EntityLike = new(@"^[a-z0-9][a-z0-9._\-]{2,}$");
    private static readonly System.Text.RegularExpressions.Regex QualifiedEntityLike = new(@"^[a-z0-9][a-z0-9._\-]*[.\-][a-z0-9._\-]*$");

    /// <summary>
    /// A concrete entity name and where it comes from: a literal; a constant, field or property default; base appsettings.json; or the
    /// "?? default" of a configuration lookup. Deployment settings can override configuration — the source says so and is kept.
    /// </summary>
    private static (string? Name, string? Source) Resolve(ExpressionSyntax? expression, List<Code> code, Dictionary<string, (string Value, SourceLocation Where)> settings, int depth = 0)
    {
        if (depth > 4) return (null, null);
        switch (expression)
        {
            case null: return (null, null);
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return (literal.Token.ValueText, $"literal ({Location(literal).File.Split('/').Last()}:{Location(literal).Line})");
            case PostfixUnaryExpressionSyntax suppressed when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression): return Resolve(suppressed.Operand, code, settings, depth);
            case ParenthesizedExpressionSyntax parenthesized: return Resolve(parenthesized.Expression, code, settings, depth);
            case ElementAccessExpressionSyntax access when access.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax key:
                return settings.TryGetValue(key.Token.ValueText, out var set) ? (set.Value, $"appsettings.json {key.Token.ValueText} ({set.Where.File.Split('/').Last()}:{set.Where.Line}; deployment settings may override)")
                    : (null, $"configuration {key.Token.ValueText} (value not in source)");
            case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
            {
                var left = Resolve(coalesce.Left, code, settings, depth + 1);
                if (left.Name is not null) return left;
                var right = Resolve(coalesce.Right, code, settings, depth + 1);
                return right.Name is null ? left : (right.Name, $"default {right.Source} when {(left.Source ?? "the configuration value").Replace(" (value not in source)", "")} is not set");
            }
            case IdentifierNameSyntax identifier:
            {
                var name = identifier.Identifier.ValueText;
                var declarator = code.Where(c => c.Tree == expression.SyntaxTree).Concat(code).SelectMany(c => c.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
                    .FirstOrDefault(v => v.Identifier.ValueText == name && v.Initializer is not null);
                if (declarator?.Initializer is { } init) return Resolve(init.Value, code, settings, depth + 1);
                return PropertyDefault(name, code, settings, depth);
            }
            case MemberAccessExpressionSyntax member: return PropertyDefault(member.Name.Identifier.ValueText, code, settings, depth);
            default: return (null, null);
        }
    }

    private static (string? Name, string? Source) PropertyDefault(string name, List<Code> code, Dictionary<string, (string Value, SourceLocation Where)> settings, int depth)
    {
        var property = code.SelectMany(c => c.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()).FirstOrDefault(p => p.Identifier.ValueText == name && p.Initializer is not null);
        if (property?.Initializer?.Value is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            return (literal.Token.ValueText, $"default of {property.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText}.{name} ({Location(literal).File.Split('/').Last()}:{Location(literal).Line}; configuration may override)");
        var field = code.SelectMany(c => c.Root.DescendantNodes().OfType<FieldDeclarationSyntax>()).SelectMany(f => f.Declaration.Variables).FirstOrDefault(v => v.Identifier.ValueText == name && v.Initializer is not null);
        return field?.Initializer is { } init && depth < 4 ? Resolve(init.Value, code, settings, depth + 1) : (null, null);
    }

    /// <summary>
    /// Service Bus entities the application's own Azure SDK code uses outside Wolverine: CreateSender (publish), CreateProcessor /
    /// CreateReceiver (listen), and entity-shaped values that source passes as topic names, declares as constants or uses as configuration
    /// defaults (reference — direction not provable). Nothing here says a message was sent or received.
    /// </summary>
    private static List<MessagingRoute> SdkRoutes(List<Code> code, Builder b)
    {
        var routes = new List<MessagingRoute>();
        void Add(MessagingRouteDirection direction, MessagingEndpointKind kind, ExpressionSyntax? entity, ExpressionSyntax? topic, SyntaxNode where, string endpoint, bool qualified = false)
        {
            var (name, source) = Resolve(entity, code, b.Settings);
            var (topicName, _) = Resolve(topic, code, b.Settings);
            if (direction == MessagingRouteDirection.Reference && (name is null || !(qualified ? QualifiedEntityLike : EntityLike).IsMatch(name))) return;
            var location = Location(where);
            // One reference per entity name is enough evidence; publish/listen sites are kept per location.
            if (routes.Any(r => r.EntityName == name && r.TopicName == topicName && r.Direction == direction && (direction == MessagingRouteDirection.Reference || r.Location == location))) return;
            routes.Add(new MessagingRoute
            {
                Direction = direction, EndpointKind = kind, Endpoint = endpoint, EntityName = name, EntityNameSource = source, TopicName = topicName,
                Technology = "Azure SDK", Location = location,
                Senders = where.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText is { } owner ? [owner] : [],
            });
        }
        foreach (var c in code)
        {
            foreach (var call in c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var args = call.ArgumentList.Arguments;
                switch (InvokedName(call))
                {
                    case "CreateSender" when args.Count > 0:
                        Add(MessagingRouteDirection.Publish, MessagingEndpointKind.Queue, args[0].Expression, null, call, Short(args[0].Expression.ToString(), 80));
                        break;
                    case "CreateProcessor" or "CreateReceiver" or "CreateSessionProcessor" when args.Count > 0:
                        var subscription = args.Count > 1 && args[1].Expression is not ObjectCreationExpressionSyntax && args[1].NameColon is null;
                        Add(MessagingRouteDirection.Listen, subscription ? MessagingEndpointKind.Subscription : MessagingEndpointKind.Queue,
                            subscription ? args[1].Expression : args[0].Expression, subscription ? args[0].Expression : null, call, Short(call.ArgumentList.ToString(), 80));
                        break;
                }
                foreach (var named in args.Where(a => a.NameColon?.Name.Identifier.ValueText is "topicName" or "queueName" or "entityPath"))
                    Add(MessagingRouteDirection.Reference, named.NameColon!.Name.Identifier.ValueText == "topicName" ? MessagingEndpointKind.Topic : MessagingEndpointKind.Queue,
                        named.Expression, null, named, $"{named.NameColon.Name.Identifier.ValueText}: {Short(named.Expression.ToString(), 60)}");
            }
            // Configuration lookups with a default whose key names Service Bus settings.
            foreach (var coalesce in c.Root.DescendantNodes().OfType<BinaryExpressionSyntax>().Where(x => x.IsKind(SyntaxKind.CoalesceExpression)
                         && x.Left is ElementAccessExpressionSyntax { ArgumentList.Arguments: [{ Expression: LiteralExpressionSyntax key }] }
                         && key.Token.ValueText.Contains("ServiceBus", StringComparison.OrdinalIgnoreCase)))
                Add(MessagingRouteDirection.Reference, MessagingEndpointKind.Queue, coalesce, null, coalesce, Short(coalesce.ToString(), 80));
            // Entity-name constants in classes that use the Service Bus SDK.
            if (c.Root.Usings.Any(u => u.Name?.ToString() == "Azure.Messaging.ServiceBus"))
                foreach (var constant in c.Root.DescendantNodes().OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword)).SelectMany(f => f.Declaration.Variables))
                    Add(MessagingRouteDirection.Reference, MessagingEndpointKind.Topic, constant.Initializer?.Value, null, constant, $"const {constant.Identifier.ValueText}", qualified: true);
        }
        return routes.Where(r => r.EntityName is not null || r.Direction != MessagingRouteDirection.Reference).ToList();
    }

    private static string? TypeOfAssembly(ExpressionSyntax? expression) =>
        expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Assembly", Expression: TypeOfExpressionSyntax typeOf } ? typeOf.Type.ToString() : null;

    private static string? ProjectOfType(string type, List<Code> code)
    {
        var simple = type.Split('.').Last();
        return code.FirstOrDefault(c => c.Root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any(t => t.Identifier.ValueText == simple))?.Project?.Name;
    }

    /// <summary>The message type an Azure Service Bus envelope mapper assigns: envelope.MessageType = typeof(T)… inside MapIncomingToEnvelope.</summary>
    private static string? MapperMessageType(string mapper, List<Code> code)
    {
        var simple = mapper.Split('.').Last();
        var type = code.SelectMany(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()).FirstOrDefault(t => t.Identifier.ValueText == simple);
        var method = type?.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "MapIncomingToEnvelope");
        var assignment = method?.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .FirstOrDefault(a => a.Left is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "MessageType" });
        return assignment?.Right.DescendantNodesAndSelf().OfType<TypeOfExpressionSyntax>().FirstOrDefault()?.Type.ToString();
    }

    // ── Handlers and routes ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wolverine handler candidates by Wolverine's naming convention — counted only when source ties them to Wolverine: their assembly is in
    /// established discovery, their file uses Wolverine, or a Wolverine listener names their message type. A convention-shaped class without
    /// any of these (e.g. an Event Hub processor's handler) is not a Wolverine handler.
    /// </summary>
    private static List<MessagingHandler> Handlers(List<Code> code, HashSet<string> scope, List<InvocationExpressionSyntax> registrations, List<Wrapper> wrappers, HashSet<string> listenTypes)
    {
        var registrationProject = registrations.Select(r => code.FirstOrDefault(c => c.Tree == r.SyntaxTree)?.Project?.Name).FirstOrDefault(n => n is not null);
        var viaWrapper = wrappers.Where(w => registrations.Any(r => InvokedName(r) == w.Name)).Select(w => w.Project).FirstOrDefault();
        var result = new List<MessagingHandler>();
        foreach (var c in code)
        foreach (var type in c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var name = type.Identifier.ValueText;
            var conventional = name.EndsWith("Handler", StringComparison.Ordinal) || name.EndsWith("Consumer", StringComparison.Ordinal)
                || type.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString().Contains("WolverineHandler", StringComparison.Ordinal));
            if (!conventional) continue;
            var usesWolverine = c.Root.Usings.Any(u => u.Name?.ToString() is { } n && (n == "Wolverine" || n.StartsWith("Wolverine.", StringComparison.Ordinal)));
            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>().Where(m => HandlerMethods.Contains(m.Identifier.ValueText)
                         && m.Modifiers.Any(SyntaxKind.PublicKeyword) && m.ParameterList.Parameters.Count > 0))
            {
                var message = method.ParameterList.Parameters[0].Type?.ToString().Split('.').Last() ?? "";
                var project = c.Project?.Name ?? "";
                var inScope = scope.Contains(project);
                if (!inScope && !usesWolverine && !listenTypes.Contains(message)) continue;
                result.Add(new MessagingHandler
                {
                    Type = name, Method = method.Identifier.ValueText, MessageType = message, Project = project, InDiscoveryScope = inScope ? true : (bool?)null,
                    DiscoveryReason = inScope ? $"{project} is in Wolverine discovery (source includes the assembly)."
                        : $"Discovery scope not established: UseWolverine is called from {viaWrapper ?? registrationProject ?? "another assembly"} and no Discovery.IncludeAssembly covers {project}.",
                    Location = Location(method),
                });
            }
        }
        return result;
    }

    private static void MapRoutes(Builder b, List<MessagingHandler> handlers, List<Code> code)
    {
        for (var i = 0; i < b.Routes.Count; i++)
        {
            var route = b.Routes[i];
            if (route.Direction == MessagingRouteDirection.Publish)
            {
                var senders = route.MessageType is null ? [] : Senders(route.MessageType, code);
                b.Routes[i] = route with { Senders = senders, FailurePath = FailureCallers(senders, code) };
                continue;
            }
            var simple = route.MessageType?.Split('.').Last();
            if (simple is null)
            {
                b.Routes[i] = route with { HandlerMapping = MessagingFactState.NotAssessable, MappingReason = "Message type is decided at runtime: no DefaultIncomingMessage<T> or envelope mapper type in source." };
                continue;
            }
            var candidates = handlers.Where(h => h.MessageType == simple).ToList();
            b.Routes[i] = candidates.Count switch
            {
                0 => route with { HandlerMapping = MessagingFactState.NotFound, MappingReason = $"{route.MappingReason} No handler for {simple} in the application's projects." },
                1 when candidates[0].InDiscoveryScope == true => route with { HandlerMapping = MessagingFactState.Available, Handlers = [$"{candidates[0].Type}.{candidates[0].Method}"], MappingReason = $"{route.MappingReason} Exactly one handler: {candidates[0].Type}." },
                1 => route with { HandlerMapping = MessagingFactState.NotAssessable, Handlers = [$"{candidates[0].Type}.{candidates[0].Method}"], MappingReason = $"{route.MappingReason} {candidates[0].Type} handles {simple}, but {candidates[0].DiscoveryReason}" },
                _ => route with { HandlerMapping = MessagingFactState.NotAssessable, Handlers = candidates.Select(c => $"{c.Type}.{c.Method}").ToList(), MappingReason = $"{route.MappingReason} Ambiguous: {candidates.Count} handlers for {simple}; none is chosen." },
            };
        }
    }

    /// <summary>Classes whose source constructs <paramref name="message"/> and sends it through the Wolverine message bus in the same method.</summary>
    private static List<string> Senders(string message, List<Code> code)
    {
        var simple = message.Split('.').Last();
        return code.SelectMany(c => c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Where(m => m.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Any(o => o.Type.ToString().Split('.').Last() == simple)
                && m.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => InvokedName(i) is "SendAsync" or "PublishAsync" or "SendToTopicAsync"))
            .Select(m => m.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText)
            .OfType<string>().Distinct().ToList();
    }

    private static MessagingFactState OutboxState(Builder b) =>
        b.Persistence.Any(p => p.Storage) ? MessagingFactState.Configured : b.Persistence.Count > 0 ? MessagingFactState.NotAssessable : MessagingFactState.NotFound;

    /// <summary>
    /// Where source calls a sender from a catch block — through an interface the sender implements, held in a field or parameter of that type
    /// (e.g. BirkCdcEventHandler catch → IErrorQueuePublisher.PublishAsync → WolverineErrorQueuePublisher). Call sites only; nothing ran.
    /// </summary>
    private static List<string> FailureCallers(List<string> senders, List<Code> code)
    {
        var result = new List<string>();
        foreach (var sender in senders)
        {
            var type = code.SelectMany(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>()).FirstOrDefault(t => t.Identifier.ValueText == sender);
            var contracts = type?.BaseList?.Types.Select(t => t.Type.ToString().Split('.').Last()).Append(sender).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var c in code)
            foreach (var catchClause in c.Root.DescendantNodes().OfType<CatchClauseSyntax>())
            foreach (var call in catchClause.Block.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } member) continue;
                var owner = call.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                var declared = owner?.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.ValueText == receiver.Identifier.ValueText)?.Parent is VariableDeclarationSyntax d ? d.Type.ToString()
                    : owner?.DescendantNodes().OfType<ParameterSyntax>().FirstOrDefault(p => p.Identifier.ValueText == receiver.Identifier.ValueText)?.Type?.ToString();
                if (declared is null || !contracts.Contains(declared.Split('.').Last().TrimEnd('?'))) continue;
                var where = Location(call);
                var text = $"{owner!.Identifier.ValueText} (catch, {where.File.Split('/').Last()}:{where.Line}) → {declared}.{member.Name.Identifier.ValueText} → {sender}";
                if (!result.Contains(text)) result.Add(text);
            }
        }
        return result;
    }

    private static void FailureAndDurabilityFacts(Builder b, bool mediatorOnly)
    {
        var retry = b.Rules.Where(r => r.Actions.Any(a => RetryActions.Any(a.StartsWith))).ToList();
        b.Fact("retry-policy", "Retry policy", retry.Count > 0 ? MessagingFactState.Configured : mediatorOnly ? MessagingFactState.NotApplicable : MessagingFactState.NotFound,
            retry.Count > 0 ? string.Join("; ", retry.Select(r => $"{r.ExceptionType}{(r.Condition is null ? "" : $" when {r.Condition}")} → {string.Join(" → ", r.Actions)}"))
                : "No explicit Wolverine retry rule (OnException … Retry…) in source. Wolverine defaults are not asserted here.",
            IntegrationEvidenceSource.SourceCode, [.. retry.Where(r => r.Location is not null).Select(r => r.Location!)]);
        var errors = b.Rules.Where(r => r.Actions.Any(a => ErrorActions.Any(a.StartsWith))).ToList();
        b.Fact("error-policy", "Failure disposition (error queue / dead-letter)", errors.Count > 0 ? MessagingFactState.Configured : mediatorOnly ? MessagingFactState.NotApplicable : MessagingFactState.NotFound,
            errors.Count > 0 ? string.Join("; ", errors.Select(r => $"{r.ExceptionType} → {string.Join(" → ", r.Actions)}"))
                + (errors.Any(r => r.Actions.Contains("MoveToErrorQueue")) ? ". For Azure Service Bus endpoints Wolverine's error queue is the native dead-letter queue." : "")
                : "No explicit Wolverine failure disposition (MoveToErrorQueue, Requeue, Discard) in source.",
            IntegrationEvidenceSource.SourceCode, [.. errors.Where(r => r.Location is not null).Select(r => r.Location!)]);
        if (!mediatorOnly)
            b.Fact("outbox", "Durable messaging / outbox", OutboxState(b),
                b.Persistence.Count == 0 ? "No Wolverine message persistence, durable queues or durable outbox in source."
                    : string.Join("; ", b.Persistence.Select(p => p.Text))
                        + (b.Persistence.Any(p => p.Storage) ? ". Configured storage is not evidence that the outbox is used successfully."
                            : ". No PersistMessagesWith… message store registration in source, so a durable outbox is not established."),
                IntegrationEvidenceSource.SourceCode, [.. b.Persistence.Select(p => p.Where)]);
    }

    /// <summary>Messaging that source shows is NOT done by Wolverine: Event Hub processors and raw Service Bus clients.</summary>
    private static void TransportOnlyFacts(Builder b, List<Code> code)
    {
        var eventHub = code.SelectMany(c => c.Root.DescendantNodes().OfType<IdentifierNameSyntax>())
            .Where(i => i.Identifier.ValueText is "EventProcessorClient" or "EventHubConsumerClient" or "BlobCheckpointStore").ToList();
        if (eventHub.Count > 0)
            b.Fact("event-hub-consumer", "Event Hub consumption", MessagingFactState.Detected,
                $"Event Hub is consumed with the Azure.Messaging.EventHubs SDK ({string.Join(", ", eventHub.Select(i => i.Identifier.ValueText).Distinct())}), not by a Wolverine listener.",
                IntegrationEvidenceSource.SourceCode, [.. eventHub.GroupBy(i => i.SyntaxTree.FilePath).Select(g => Location(g.First()))]);
        var serviceBus = code.SelectMany(c => c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(i => InvokedName(i) is "CreateProcessor" or "CreateReceiver" or "CreateSessionProcessor").ToList();
        if (serviceBus.Count > 0)
            b.Fact("service-bus-sdk-consumer", "Service Bus consumption outside Wolverine", MessagingFactState.Detected,
                $"Service Bus messages are received with the Azure.Messaging.ServiceBus SDK ({string.Join(", ", serviceBus.Select(i => i.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText).OfType<string>().Distinct())}), not by a Wolverine listener.",
                IntegrationEvidenceSource.SourceCode, [.. serviceBus.Select(Location)]);
    }

    private static void TelemetryFacts(Builder b, List<InvocationExpressionSyntax> invocations, List<Code> code,
        Dictionary<string, (List<string> Sources, SourceLocation Location)> observability, string? role)
    {
        var setup = invocations.FirstOrDefault(i => InvokedName(i) is { } n && observability.ContainsKey(n));
        var extra = invocations.Where(i => InvokedName(i) is "AddSource" or "AddMeter").SelectMany(i => i.ArgumentList.Arguments)
            .Select(a => a.Expression is LiteralExpressionSyntax l ? l.Token.ValueText : a.Expression.ToString()).ToList();
        var known = setup is not null ? observability[InvokedName(setup)!].Sources.Concat(extra).ToList() : extra;
        var wolverine = known.Where(s => s.Contains("Wolverine", StringComparison.OrdinalIgnoreCase)).ToList();
        b.Fact("telemetry-export", "Wolverine tracing / metrics export", wolverine.Count > 0 ? MessagingFactState.Configured : setup is not null ? MessagingFactState.NotFound : MessagingFactState.NotAssessable,
            wolverine.Count > 0 ? $"Registered: {string.Join(", ", wolverine)}."
                : setup is not null ? $"{InvokedName(setup)} registers {(known.Count == 0 ? "no custom source or meter" : string.Join(", ", known))}; no Wolverine ActivitySource or meter. Wolverine processing duration, retry and dead-letter counts are not exported."
                : "The observability setup is not in the analyzed source.",
            IntegrationEvidenceSource.SourceCode, [.. (setup is null ? Array.Empty<SourceLocation>() : [Location(setup), observability[InvokedName(setup)!].Location])]);
        var meters = code.SelectMany(c => c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(i => InvokedName(i) is { } n && (n.StartsWith("CreateCounter", StringComparison.Ordinal) || n.StartsWith("CreateHistogram", StringComparison.Ordinal) || n.StartsWith("CreateObservable", StringComparison.Ordinal)))
            .Select(i => (Name: i.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax l ? l.Token.ValueText : null, Where: Location(i)))
            .Where(m => m.Name is not null).ToList();
        if (meters.Count > 0)
            b.Fact("custom-metrics", "Application metrics defined in source", MessagingFactState.Available,
                $"{string.Join(", ", meters.Select(m => m.Name))}. Defined in source — not read from telemetry in this build.", IntegrationEvidenceSource.SourceCode, [.. meters.Select(m => m.Where)]);
        if (role is not null)
            b.Fact("telemetry-role", "Telemetry role name", MessagingFactState.Configured, $"OpenTelemetry service name \"{role}\" (the Application Insights role when deployed with that setup).",
                IntegrationEvidenceSource.SourceCode, setup is null ? [] : [Location(setup)]);
    }

    /// <summary>Cross-application facts the source cannot settle, e.g. a topic published with one message type and handled with another name.</summary>
    private static List<string> SetLimitations(List<ApplicationMessagingEvidence> applications, List<Project> projects, List<Code> code)
    {
        var limitations = new List<string>();
        foreach (var app in applications)
        foreach (var listen in app.Routes.Where(r => r.Direction == MessagingRouteDirection.Listen && r.Topic is not null))
        foreach (var other in applications.Where(o => o != app))
        foreach (var publish in other.Routes.Where(r => r.Direction == MessagingRouteDirection.Publish && r.EndpointKind == MessagingEndpointKind.Topic && r.Endpoint == listen.Topic))
        {
            var handled = listen.MessageType ?? string.Join("/", app.Handlers.Select(h => h.MessageType).Distinct());
            if (publish.MessageType?.Split('.').Last() != listen.MessageType?.Split('.').Last())
                limitations.Add($"Message identity across services not established: {other.ApplicationId} publishes {publish.MessageType} to topic {publish.Endpoint}; {app.ApplicationId} listens on subscription {listen.Endpoint} with {(listen.MessageType is null ? "no message type in source" : listen.MessageType)}{(listen.MessageType is null && handled.Length > 0 ? $" (handlers for {handled})" : "")}.");
        }
        if (!projects.Any()) limitations.Add("No project files in the analyzed archives; package evidence cannot be established.");
        return limitations;
    }
}
