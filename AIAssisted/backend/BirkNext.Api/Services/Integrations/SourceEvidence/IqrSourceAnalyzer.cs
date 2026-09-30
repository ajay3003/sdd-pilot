using System.Security.Cryptography;
using Path = System.IO.Path;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Integrations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

/// <summary>Syntax evidence, not a compiler/dataflow proof. Never compiles, executes tests or persists source snippets.</summary>
public static class IqrSourceAnalyzer
{
    /// <summary>v2 adds the multi-stage integration path (adapter → ingestion → domain → event → outbox → Service Bus) and Contract/Integration layers from test categories.</summary>
    public const int Version = 2;
    internal sealed record Code(string Path, CompilationUnitSyntax Root, SourceProject? Project);
    internal static string TestId(string path, int spanStart) => Hash($"{path}:{spanStart}:test");
    private static string Safe(string value) => IqrSourceArchiveReader.SafeLabel(value);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static SourceLocation At(Code code, SyntaxNode node) => new(Safe(code.Path), node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
    private static string Symbol(MethodDeclarationSyntax method) =>
        Safe((method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "?") + "." + method.Identifier.ValueText);
    private static bool Security(string name) => Regex.IsMatch(name, "security|sikkerhet|classification", RegexOptions.IgnoreCase);

    public static IqrSourceSnapshot Analyze(string integrationId, IqrSourceArchiveReader.Workspace workspace, DateTimeOffset now, CancellationToken ct = default)
    {
        var limitations = workspace.Limitations.ToList();
        var projects = new List<SourceProject>();
        var commits = new HashSet<string>();
        foreach (var file in workspace.Files.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var reader = XmlReader.Create(new StringReader(file.Content), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var xml = XDocument.Load(reader);
                var test = xml.Descendants().Any(e => e.Name.LocalName == "IsTestProject" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
                    || xml.Descendants().Any(e => e.Name.LocalName == "PackageReference" && new[] { "Microsoft.NET.Test.Sdk", "xunit", "NUnit", "MSTest.TestFramework" }.Contains(e.Attribute("Include")?.Value));
                var eventHub = xml.Descendants().Any(e => e.Name.LocalName == "PackageReference" && e.Attribute("Include")?.Value.StartsWith("Azure.Messaging.EventHubs", StringComparison.Ordinal) == true);
                var service = xml.Root?.Attribute("Sdk")?.Value.Contains(".Web", StringComparison.Ordinal) == true;
                projects.Add(new SourceProject(Safe(Path.GetFileNameWithoutExtension(file.Path)), Safe(file.Path), test,
                    test ? "Developer test project (layer classified per test)" : eventHub ? "Event Hub SDK project; consumer role requires source review" : service ? "Application/service project" : "Production/shared project", SourceConfidence.StrongSourceEvidence));
                foreach (var revision in xml.Descendants().Where(e => e.Name.LocalName == "SourceRevisionId" && Regex.IsMatch(e.Value.Trim(), "^[a-fA-F0-9]{40}$"))) commits.Add(revision.Value.Trim().ToLowerInvariant());
            }
            catch (Exception ex) when (ex is XmlException or InvalidOperationException)
            { limitations.Add($"Project not parsed: {Safe(file.Path)}. Other projects retained."); }
        }
        var code = workspace.Files.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Select(f =>
        {
            var tree = CSharpSyntaxTree.ParseText(WolverineSourceAnalyzer.BlankPrimaryConstructors(f.Content), new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: ct);
            var owner = projects.Where(p => f.Path.StartsWith(p.Path[..(p.Path.LastIndexOf('/') + 1)], StringComparison.Ordinal))
                .OrderByDescending(p => p.Path.Length).FirstOrDefault();
            return new Code(f.Path, tree.GetCompilationUnitRoot(), owner);
        }).ToList();
        foreach (var c in code.Where(c => c.Root.ContainsDiagnostics)) limitations.Add($"Partial syntax analysis: {Safe(c.Path)}.");
        if (code.Any(c => c.Project is null)) limitations.Add("Some C# files have no resolved project owner; their relationships are not confirmed.");
        var production = code.Where(c => c.Project?.IsTest == false && !c.Root.ContainsDiagnostics).ToList();
        var ambiguousSymbols = production.SelectMany(c => c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()).GroupBy(Symbol)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        if (ambiguousSymbols.Count > 0) limitations.Add("Overloaded or ambiguous production symbols are not correlated to developer coverage without type binding.");
        var rules = new List<ImplementationRule>();
        foreach (var c in production)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var method in c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var symbol = Symbol(method);
                void Add(SyntaxNode node, string kind, string field, string behavior, string requirement = "Not resolved", string intent = "Not resolved", string operation = "Not resolved", string? targetType = null, string fallback = "Not resolved", string? table = null, List<string>? relatedSymbols = null, string invalidBehavior = "Not resolved")
                {
                    if (rules.Count >= 5000) return;
                    var at = At(c, node);
                    rules.Add(new ImplementationRule { Id = Hash($"{c.Path}:{node.SpanStart}:{kind}:{field}"), Project = c.Project!.Name,
                        Symbol = symbol, Kind = kind, Field = Safe(field), Table = table is null ? null : Safe(table), Behavior = behavior,
                        Requirement = requirement, AssertionIntent = intent, Operation = operation, TargetType = targetType,
                        NullBehavior = requirement == "Required for mapper output" ? "Missing → discarded (null output)" : "Not resolved",
                        InvalidBehavior = invalidBehavior,
                        Fallback = fallback, Confidence = SourceConfidence.StrongSourceEvidence, Location = at,
                        ConditionFingerprint = Hash(node.WithoutTrivia().ToFullString()), RelatedSymbols = relatedSymbols ?? [], TestCorrelationResolved = !ambiguousSymbols.Contains(symbol) });
                }
                foreach (var access in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var member = access.Expression as MemberAccessExpressionSyntax;
                    var name = member?.Name.Identifier.ValueText ?? "";
                    var literal = access.ArgumentList.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
                    if (name is "TryGetProperty" or "GetProperty" or "ContainsKey" or "TryGetValue" && literal?.IsKind(SyntaxKind.StringLiteralExpression) == true)
                    {
                        var field = literal.Token.ValueText;
                        // A read alone is not a required field. Only a negative existence branch that terminates proves missing behavior.
                        var branch = access.Ancestors().OfType<IfStatementSyntax>().FirstOrDefault(i => i.Condition.Span.Contains(access.Span));
                        var negative = branch is not null && NegativeExistence(branch.Condition, access);
                        var statements = branch?.Statement is BlockSyntax block ? block.Statements.ToList() : branch is null ? [] : new List<StatementSyntax> { branch.Statement };
                        var discards = negative && statements.Any(s => s is ReturnStatementSyntax r && r.Expression?.IsKind(SyntaxKind.NullLiteralExpression) == true);
                        var throws = negative && statements.Any(s => s is ThrowStatementSyntax);
                        var conditional = access.Ancestors().OfType<ConditionalExpressionSyntax>().FirstOrDefault(t => t.Condition.Span.Contains(access.Span));
                        var fallback = conditional is null ? "Not resolved" : DefaultDescription(conditional.WhenFalse);
                        var mapper = symbol.Contains("Mapper", StringComparison.Ordinal) || method.Identifier.ValueText == "Map";
                        var requirement = discards ? mapper ? "Required for mapper output" : "Missing → null method output" : throws ? "Required in inspected branch" : conditional is not null ? "Fallback available" : "Read; requiredness not resolved";
                        var variable = access.ArgumentList.Arguments.Select(a => a.Expression).OfType<DeclarationExpressionSyntax>()
                            .Select(d => (d.Designation as SingleVariableDesignationSyntax)?.Identifier.ValueText).FirstOrDefault(v => v is not null);
                        var conversions = variable is null ? [] : method.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                            .Where(m => m.Expression.ToString() == variable).Select(m => m.Name.Identifier.ValueText)
                            .Where(n => n.StartsWith("Get") || n.StartsWith("TryGet")).Distinct().ToList();
                        var invalidCheck = variable is not null && branch?.Condition.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
                            .Any(m => m.Expression.ToString() == variable && (m.Name.Identifier.ValueText == "ValueKind" || m.Name.Identifier.ValueText.StartsWith("TryGet"))) == true;
                        Add(access, "Field", field, discards ? "Missing field returns null; caller discard behavior not established" : throws ? "Missing field throws in inspected branch" : "Source property access; no mandatory-field claim",
                            requirement, discards ? "null-output" : throws ? "exception" : "Not resolved", targetType: conversions.Count == 0 ? null : Safe(string.Join(" / ", conversions)), fallback: fallback,
                            invalidBehavior: invalidCheck && discards ? "Type/value check shares rejection branch; exact accepted types require review" : "Not resolved");
                    }
                    if (name.Contains("Checkpoint", StringComparison.OrdinalIgnoreCase))
                        Add(access, "Checkpoint", name, "Source-defined checkpoint call; enclosing control flow requires review. Runtime advance/withholding not verified");
                    if (name.StartsWith("LogWarning") || name.StartsWith("LogError")) Add(access, "ErrorHandling", name, "Source-defined logging path; log message and values excluded");
                    if (name.Contains("Retry") || name.Contains("ErrorQueue") || name.Contains("Discard")) Add(access, "Reliability", name, "Source-defined policy/call; runtime behavior not observed");
                    if (Security(member?.Expression.ToString() ?? "") || Security(name)) Add(access, "Security", name, "Security-related call found; input dataflow and downstream protection not confirmed");
                }
                foreach (var index in method.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
                    if (index.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
                        Add(index, "Field", lit.Token.ValueText, "Indexed source field access; missing/invalid behavior depends on receiver type", "Read; requiredness not resolved");
                foreach (var section in method.DescendantNodes().OfType<SwitchSectionSyntax>())
                {
                    var switchValue = section.Parent is SwitchStatementSyntax sw ? sw.Expression.ToString() : "";
                    var op = Regex.IsMatch(switchValue, @"(?i)(\bop\b|operation)");
                    var route = Regex.IsMatch(switchValue, @"(?i)table");
                    if (!op && !route) continue;
                    foreach (var label in section.Labels.OfType<CaseSwitchLabelSyntax>())
                    {
                        if (label.Value is not LiteralExpressionSyntax lit || !lit.IsKind(SyntaxKind.StringLiteralExpression)) continue;
                        var calls = section.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(i => i.Expression).OfType<MemberAccessExpressionSyntax>()
                            .Select(m => Safe((m.Expression is IdentifierNameSyntax receiver ? receiver.Identifier.ValueText + "." : "") + m.Name.Identifier.ValueText)).Distinct().ToList();
                        var sides = section.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Select(m => m.Name.Identifier.ValueText)
                            .Where(n => n.Equals("before", StringComparison.OrdinalIgnoreCase) || n.Equals("after", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
                        var terminate = section.DescendantNodes().OfType<ReturnStatementSyntax>().Any(r => r.Expression is null || r.Expression.IsKind(SyntaxKind.NullLiteralExpression));
                        Add(label, op ? "Operation" : "Route", lit.Token.ValueText,
                            $"Source branch: {(sides.Count == 0 ? "payload side not resolved" : string.Join("/", sides))}; calls {string.Join(", ", calls)}{(terminate ? "; early return found" : "")}",
                            operation: op ? Safe(lit.Token.ValueText) : "Not resolved", table: route ? lit.Token.ValueText : null, relatedSymbols: calls);
                    }
                    foreach (var label in section.Labels.OfType<DefaultSwitchLabelSyntax>())
                        Add(label, op ? "Operation" : "Route", "default", "Default switch branch found; outcome not resolved");
                }
                foreach (var expression in method.DescendantNodes().OfType<SwitchExpressionSyntax>())
                {
                    var op = Regex.IsMatch(expression.GoverningExpression.ToString(), @"(?i)(\bop\b|operation)");
                    var route = Regex.IsMatch(expression.GoverningExpression.ToString(), @"(?i)table");
                    if (!op && !route) continue;
                    foreach (var arm in expression.Arms)
                    {
                        var literal = (arm.Pattern as ConstantPatternSyntax)?.Expression as LiteralExpressionSyntax;
                        var label = literal?.IsKind(SyntaxKind.StringLiteralExpression) == true ? literal.Token.ValueText : arm.Pattern is DiscardPatternSyntax ? "default" : null;
                        if (label is null) continue;
                        var calls = arm.Expression.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Select(i => i.Expression).OfType<MemberAccessExpressionSyntax>()
                            .Select(m => Safe((m.Expression is IdentifierNameSyntax receiver ? receiver.Identifier.ValueText + "." : "") + m.Name.Identifier.ValueText)).Distinct().ToList();
                        Add(arm, op ? "Operation" : "Route", label, $"Switch expression branch: calls {string.Join(", ", calls)}; {(arm.Expression.IsKind(SyntaxKind.NullLiteralExpression) ? "null result" : "result semantics not resolved")}",
                            operation: op ? Safe(label) : "Not resolved", table: route && label != "default" ? label : null, relatedSymbols: calls);
                    }
                }
                foreach (var caught in method.DescendantNodes().OfType<CatchClauseSyntax>())
                    Add(caught, "ErrorHandling", caught.Declaration?.Type.ToString() ?? "catch-all", "Source-defined catch path; retry, subsequent event processing and checkpoint implications not proven");
                // If-based op handling remains source-defined, and is not generalized into all four Debezium operations.
                foreach (var branch in method.DescendantNodes().OfType<IfStatementSyntax>())
                {
                    var validation = branch.Condition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                        .FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax m && m.Expression.ToString() == "Enum" && m.Name.Identifier.ValueText == "IsDefined" && NegativeExistence(branch.Condition, i));
                    if (validation is not null && validation.ArgumentList.Arguments.LastOrDefault()?.Expression is IdentifierNameSyntax checkedValue)
                    {
                        var variable = checkedValue.Identifier.ValueText;
                        var initializer = method.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.ValueText == variable)?.Initializer?.Value;
                        var propertyAccess = initializer?.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault(i =>
                            i.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.ValueText is "TryGetProperty" or "TryGetValue" or "GetProperty");
                        var field = (propertyAccess?.ArgumentList.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax)?.Token.ValueText ?? variable;
                        var statements = branch.Statement is BlockSyntax block ? block.Statements.ToList() : new List<StatementSyntax> { branch.Statement };
                        var fallbackAssignment = statements.OfType<ExpressionStatementSyntax>().Select(s => s.Expression).OfType<AssignmentExpressionSyntax>().FirstOrDefault(a => a.Left.ToString() == variable);
                        var warns = statements.SelectMany(s => s.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()).Any(i => i.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.ValueText == "LogWarning");
                        Add(branch, "Validation", field, "Source enum membership validation in a negative IsDefined branch", "Conditional enum validation",
                            fallback: fallbackAssignment is null ? "Not resolved" : DefaultDescription(fallbackAssignment.Right),
                            invalidBehavior: fallbackAssignment is null ? warns ? "Invalid enum → warning; final output not resolved" : "Invalid enum branch found; final output not resolved" : warns ? "Invalid enum → warning + fallback" : "Invalid enum → fallback");
                    }
                    if (!Regex.IsMatch(branch.Condition.ToString(), @"(?i)(\bop\b|operation)")) continue;
                    foreach (var lit in branch.Condition.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
                        Add(branch, "Operation", lit.Token.ValueText, "Operation condition found; polarity and payload side require review", operation: Safe(lit.Token.ValueText));
                }
            }
            foreach (var property in c.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (rules.Count >= 5000) break;
                var attr = property.AttributeLists.SelectMany(a => a.Attributes).FirstOrDefault(a => a.Name.ToString().EndsWith("JsonPropertyName") || a.Name.ToString().EndsWith("JsonProperty"));
                if (attr?.ArgumentList?.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax literal) continue;
                var at = At(c, property);
                rules.Add(new ImplementationRule { Id = Hash($"{c.Path}:{property.SpanStart}:model"), Project = c.Project!.Name,
                    Symbol = Safe((property.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "?") + "." + property.Identifier.ValueText),
                    Kind = "Envelope/model", Field = Safe(literal.Token.ValueText), TargetType = Safe(property.Type.ToString()),
                    Requirement = property.Modifiers.Any(SyntaxKind.RequiredKeyword) ? "C# required member; deserializer enforcement not established" : "Deserializer requiredness not resolved",
                    Behavior = "JSON model field; nullable annotations alone do not prove runtime validation", Location = at, Confidence = SourceConfidence.StrongSourceEvidence });
            }
            foreach (var parameter in c.Root.DescendantNodes().OfType<ParameterSyntax>().Where(p => p.Parent?.Parent is RecordDeclarationSyntax))
            {
                if (rules.Count >= 5000) break;
                var attr = parameter.AttributeLists.SelectMany(a => a.Attributes).FirstOrDefault(a => a.Name.ToString().EndsWith("JsonPropertyName") || a.Name.ToString().EndsWith("JsonProperty"));
                if (attr?.ArgumentList?.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax literal) continue;
                var record = (RecordDeclarationSyntax)parameter.Parent!.Parent!;
                rules.Add(new ImplementationRule { Id = Hash($"{c.Path}:{parameter.SpanStart}:record-model"), Project = c.Project!.Name,
                    Symbol = Safe(record.Identifier.ValueText + "." + parameter.Identifier.ValueText), Kind = "Envelope/model", Field = Safe(literal.Token.ValueText),
                    TargetType = Safe(parameter.Type?.ToString() ?? "Not resolved"), Requirement = "Constructor/model field; deserializer requiredness not resolved",
                    Fallback = parameter.Default is null ? "Not resolved" : DefaultDescription(parameter.Default.Value),
                    Behavior = "Attributed JSON record member; runtime deserializer enforcement not established", Location = At(c, parameter), Confidence = SourceConfidence.StrongSourceEvidence });
            }
        }
        // Only exact static-symbol routing is propagated to fields. Instance/DI/helper routing stays unresolved.
        rules = rules.Select(r =>
        {
            if (r.Kind != "Field") return r;
            var tables = rules.Where(route => route.Kind == "Route" && route.Table is not null && route.RelatedSymbols.Contains(r.Symbol)).Select(route => route.Table!).Distinct().ToList();
            return tables.Count == 1 ? r with { Table = tables.Single() } : r;
        }).ToList();
        var partial = limitations.Any(l => !l.StartsWith("Configuration values excluded", StringComparison.Ordinal));
        var tests = TestInventory(code, production);
        var dataflows = SecurityFlows(production, tests);
        IntegrationPathEvidence? path = null;
        // Path tracing reads partially parsed production files too: Roslyn's trees are error-tolerant, and newer syntax (e.g. C# 12 collection
        // expressions) must not hide a whole adapter or repository. Rule extraction above keeps its stricter filter.
        var tolerant = code.Where(c => c.Project?.IsTest == false).ToList();
        if (tolerant.Count > production.Count)
            limitations.Add($"Integration path tracing also read {tolerant.Count - production.Count} partially parsed production file(s) (newer C# syntax); elements from them may be incomplete.");
        try { path = IqrPathAnalyzer.Analyze(tolerant, code, tests, ct); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Collections.Generic.KeyNotFoundException or NullReferenceException or IndexOutOfRangeException)
        { limitations.Add($"Integration path analysis stopped on an unsupported pattern ({ex.GetType().Name}); other evidence retained."); }
        var coverage = rules.Select(r =>
        {
            var covered = Correlate(r, tests);
            if (r.Kind == "Security" && dataflows.Any(f => f.Gap.StartsWith("Cross-layer gap:") && f.Locations.Contains(r.Location)))
                covered = covered with { Statuses = [.. covered.Statuses, SourceCoverageStatus.CrossLayerGap], Action = covered.Action + "; cross-layer security wiring gap remains" };
            return covered;
        }).ToList();
        if (rules.Count >= 5000 || tests.Count >= 10000) limitations.Add("Evidence inventory limit reached: 5,000 rules / 10,000 test definitions. Additional evidence may not be analyzed.");
        limitations.AddRange([
            "Syntax-only heuristic analysis; call graphs, external helpers, conditional compilation and dynamically configured routes may be unresolved.",
            "Developer tests discovered, not executed. No authoritative test-result artifact or commit-correlated CI results supplied.",
            "Implementation contract derived from source; formal schema and runtime compatibility not assessed.",
            "Malformed-message, poison-event, checkpoint resilience and end-to-end processing not exercised.",
            "Deployment/source correlation not established. No deployed commit identity compared.",
            "No executable tests recommended or created. Unresolved equivalence requires manual verification before any future recommendation."
        ]);
        return new IqrSourceSnapshot { IntegrationId = integrationId, Archive = workspace.Archive, AnalyzedAt = now, AnalyzerVersion = Version,
            Commit = commits.Count == 1 ? commits.Single() : "Unknown", Projects = projects, Configurations = workspace.Configurations ?? [], Rules = rules.Take(5000).ToList(), Tests = tests.Take(10000).ToList(),
            Coverage = coverage.Take(5000).ToList(), Dataflows = dataflows, IntegrationPath = path,
            Status = production.Count == 0 ? SourceAnalysisStatus.Failed : partial || rules.Count >= 5000 || tests.Count >= 10000 ? SourceAnalysisStatus.Partial : SourceAnalysisStatus.Ready, Limitations = limitations.Distinct().ToList() };
    }

    private static bool NegativeExistence(ExpressionSyntax condition, InvocationExpressionSyntax access) => condition switch
    {
        ParenthesizedExpressionSyntax p => NegativeExistence(p.Expression, access),
        PrefixUnaryExpressionSyntax n when n.IsKind(SyntaxKind.LogicalNotExpression) => n.Operand.Span.Contains(access.Span),
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.LogicalOrExpression) => NegativeExistence(b.Left, access) || NegativeExistence(b.Right, access),
        _ => false
    };

    private static string DefaultDescription(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.NullLiteralExpression) => "Missing → null",
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.NumericLiteralExpression) => l.Token.ValueText.Length <= 12 ? "Numeric constant fallback: " + l.Token.ValueText : "Numeric constant fallback (value excluded)",
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) => "String constant fallback (value excluded)",
        DefaultExpressionSyntax => "Missing → type default",
        MemberAccessExpressionSyntax m when m.Expression is IdentifierNameSyntax => "Symbolic fallback: " + Safe(m.ToString()),
        _ => "Fallback expression found; value/semantics not resolved"
    };

    private static List<DeveloperTestEvidence> TestInventory(List<Code> code, List<Code> production)
    {
        var classes = production.SelectMany(c => c.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()).Select(t => t.Identifier.ValueText).ToHashSet();
        var tests = new List<DeveloperTestEvidence>();
        foreach (var c in code)
        foreach (var method in c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (tests.Count >= 10000) return tests;
            var attributes = method.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString().Split('.').Last().Replace("Attribute", "")).ToList();
            var categories = method.AttributeLists.SelectMany(a => a.Attributes)
                .Where(a => a.Name.ToString().Split('.').Last() is "Category" or "TestCategory" or "Trait")
                .Select(a => a.ArgumentList?.Arguments.LastOrDefault()?.Expression).OfType<LiteralExpressionSyntax>()
                .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)).Select(l => Safe(l.Token.ValueText)).Distinct().ToList();
            var framework = attributes.Any(a => a is "Fact" or "Theory") ? "xUnit" : attributes.Any(a => a is "Test" or "TestCase" or "TestCaseSource") ? "NUnit" : attributes.Any(a => a is "TestMethod" or "DataTestMethod") ? "MSTest" : null;
            if (framework is null) continue;
            // A test method outside any type only occurs in a partially parsed file (already a stated limitation); it is not inventoried.
            if (method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is not { } type) continue;
            var created = method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(o => classes.Contains(o.Type.ToString().Split('.').Last()))
                .GroupBy(o => o.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Identifier.ValueText ?? $"@{o.SpanStart}")
                .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Type.ToString().Split('.').Last());
            foreach (var field in type.Members.OfType<FieldDeclarationSyntax>().Where(f => classes.Contains(f.Declaration.Type.ToString().Split('.').Last())))
                foreach (var variable in field.Declaration.Variables)
                    if (!method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Any(v => v.Identifier.ValueText == variable.Identifier.ValueText))
                        created.TryAdd(variable.Identifier.ValueText, field.Declaration.Type.ToString().Split('.').Last());
            var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
            var targets = calls.Select(i => i.Expression).OfType<MemberAccessExpressionSyntax>()
                .Select(m => (Receiver: m.Expression.ToString().Split('.').Last(), Method: m.Name.Identifier.ValueText))
                .Where(m => classes.Contains(m.Receiver) || created.ContainsKey(m.Receiver))
                .Select(m => Safe((created.GetValueOrDefault(m.Receiver) ?? m.Receiver) + "." + m.Method)).Distinct().ToList();
            var assertion = calls.FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax m && m.Expression.ToString() == "Assert" && m.Name.Identifier.ValueText is "Null" or "IsNull" or "True" or "IsTrue" or "False" or "IsFalse" or "Throws" or "ThrowsAsync"
                || i.Expression is MemberAccessExpressionSyntax nunit && nunit.Expression.ToString() == "Assert" && nunit.Name.Identifier.ValueText == "That"
                    && i.ArgumentList.Arguments.LastOrDefault()?.Expression.ToString() == "Is.Null");
            // Tie the assertion to the result of a production call, not merely an unrelated Assert.Null in the method.
            var asserted = assertion?.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            var fluent = calls.FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.ValueText == "BeNull"
                && m.Expression is InvocationExpressionSyntax should && should.Expression is MemberAccessExpressionSyntax s && s.Name.Identifier.ValueText == "Should");
            var fluentUsed = assertion is null && fluent is not null;
            if (fluentUsed && fluent?.Expression is MemberAccessExpressionSyntax f && f.Expression is InvocationExpressionSyntax shouldCall && shouldCall.Expression is MemberAccessExpressionSyntax shouldMember)
                asserted = shouldMember.Expression;
            var resultCall = asserted as InvocationExpressionSyntax;
            if (asserted is IdentifierNameSyntax resultName)
                resultCall = method.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.ValueText == resultName.Identifier.ValueText)?.Initializer?.Value as InvocationExpressionSyntax;
            var tied = resultCall?.Expression is MemberAccessExpressionSyntax resultMember
                && targets.Contains(Safe((created.GetValueOrDefault(resultMember.Expression.ToString()) ?? resultMember.Expression.ToString().Split('.').Last()) + "." + resultMember.Name.Identifier.ValueText));
            var productionInput = resultCall?.ArgumentList.Arguments.FirstOrDefault()?.Expression as IdentifierNameSyntax;
            var removes = calls.Where(i => productionInput is not null && i.Expression is MemberAccessExpressionSyntax m
                    && m.Name.Identifier.ValueText == "Remove" && m.Expression.ToString() == productionInput.Identifier.ValueText && i.SpanStart < resultCall!.SpanStart)
                .Where(remove => !calls.Any(later => later.SpanStart > remove.SpanStart && later.SpanStart < resultCall!.SpanStart
                        && later.DescendantNodes().OfType<IdentifierNameSyntax>().Any(n => n.Identifier.ValueText == productionInput!.Identifier.ValueText))
                    && !method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(later => later.SpanStart > remove.SpanStart && later.SpanStart < resultCall!.SpanStart
                        && later.Left.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(n => n.Identifier.ValueText == productionInput!.Identifier.ValueText)))
                .Select(i => i.ArgumentList.Arguments.FirstOrDefault()?.Expression).OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)).Select(l => Safe(l.Token.ValueText)).Distinct().ToList();
            var setup = type.ToString();
            var hosted = Regex.IsMatch(setup, "WebApplicationFactory|TestServer|Testcontainers|HostBuilder|CreateClient\\(");
            var hostUsed = Regex.IsMatch(setup, @"\.CreateClient\(|\.StartAsync\(|\.GetAsync\(|\.PostAsync\(|\.SendAsync\(");
            var mocks = Regex.IsMatch(setup, @"\bMock<|Substitute\.For|FakeItEasy");
            // Declared test categories and test-project names outrank setup heuristics (e.g. a shared container fixture lives outside the class).
            var classCategories = type.AttributeLists.SelectMany(a => a.Attributes).Where(a => a.Name.ToString().Split('.').Last() is "Trait" or "Category" or "TestCategory")
                .Select(a => a.ArgumentList?.Arguments.LastOrDefault()?.Expression).OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)).Select(l => l.Token.ValueText).ToList();
            categories = [.. categories, .. classCategories.Select(Safe).Where(x => !categories.Contains(x))];
            var project = c.Project?.Name ?? "";
            var declared = categories.Any(x => x.Equals("Contract", StringComparison.OrdinalIgnoreCase)) || project.Contains(".Contract", StringComparison.OrdinalIgnoreCase) ? DeveloperTestLayer.Contract
                : categories.Any(x => x.Equals("Integration", StringComparison.OrdinalIgnoreCase)) || project.Contains(".Integration", StringComparison.OrdinalIgnoreCase) ? DeveloperTestLayer.Integration
                : (DeveloperTestLayer?)null;
            var layer = declared ?? (hosted && hostUsed ? mocks ? DeveloperTestLayer.Component : DeveloperTestLayer.Integration : mocks || targets.Count > 0 ? DeveloperTestLayer.Unit : DeveloperTestLayer.Unknown);
            tests.Add(new DeveloperTestEvidence { Id = Hash($"{c.Path}:{method.SpanStart}:test"), Project = c.Project?.Name ?? "Unknown", Class = Safe(type.Identifier.ValueText),
                Method = Safe(method.Identifier.ValueText), Framework = framework, Categories = categories, Layer = layer,
                Confidence = declared is not null || mocks ? SourceConfidence.StrongSourceEvidence : SourceConfidence.Partial, ProductionSymbols = targets,
                Fields = removes, InputCondition = removes.Count > 0 ? "Missing" : "Not resolved",
                AssertionIntent = !tied ? "Not resolved" : fluentUsed ? "null-output" : assertion?.Expression is MemberAccessExpressionSyntax a ? a.Name.Identifier.ValueText switch
                    { "Null" or "IsNull" or "That" => "null-output", "True" or "IsTrue" => "boolean true", "False" or "IsFalse" => "boolean false", _ => "Not resolved" } : "Not resolved", Location = At(c, method) });
        }
        return tests;
    }

    public static SourceCoverage Correlate(ImplementationRule rule, IReadOnlyList<DeveloperTestEvidence> tests)
    {
        // Deliberately narrow equivalence: explicit removed key, exact production symbol and an assertion on its result.
        var matches = tests.Where(t => rule.TestCorrelationResolved && t.ProductionSymbols.Contains(rule.Symbol) && t.Fields.Contains(rule.Field)
            && t.InputCondition == "Missing" && rule.Requirement == "Required for mapper output" && t.AssertionIntent == rule.AssertionIntent
            && t.AssertionIntent != "Not resolved" && t.Layer is DeveloperTestLayer.Unit or DeveloperTestLayer.Integration).ToList();
        var states = matches.Select(t => t.Layer == DeveloperTestLayer.Unit ? SourceCoverageStatus.DeveloperUnitCovered : SourceCoverageStatus.DeveloperIntegrationCovered).Distinct().ToList();
        if (matches.Count == 0) { states.Add(SourceCoverageStatus.SourceEvidenceOnly); states.Add(SourceCoverageStatus.ManualVerification); }
        if (!rule.TestCorrelationResolved) states.Add(SourceCoverageStatus.NotAssessable);
        states.Add(SourceCoverageStatus.RuntimeGap);
        states.Add(SourceCoverageStatus.E2EGap);
        return new SourceCoverage(rule.Id, matches.Select(t => t.Id).ToList(), states,
            matches.Count > 0 ? "Same production symbol, explicit missing field on the tested input and null-output assertion on production result. Test layer inferred from source setup; verify classification confidence" : "No matching developer test resolved; analyzer may not recognize existing coverage",
            matches.Count > 0 ? "Covered by developer test; no same-layer duplicate required. Runtime/E2E evidence still missing" : "Manual coverage review; no executable test created or recommended");
    }

    private static List<SourceDataflow> SecurityFlows(List<Code> code, List<DeveloperTestEvidence> tests)
    {
        var flows = new List<SourceDataflow>();
        var guardCalls = code.SelectMany(x => x.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(i => (Code: x, Call: i)))
            .Where(x => x.Call.Expression.ToString().Contains("guard", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var c in code)
        foreach (var assignment in c.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => Security(a.Left.ToString())))
        {
            var field = assignment.Left.ToString().Split('.').Last();
            var zero = assignment.Right is LiteralExpressionSyntax l && l.Token.ValueText == "0";
            var guards = guardCalls.Where(x => x.Call.ArgumentList.Arguments.Any(a => a.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(n => n.Identifier.ValueText == field))).ToList();
            if (guards.Count == 0) continue;
            var guardSymbols = guards.Select(g =>
            {
                if (g.Call.Expression is not MemberAccessExpressionSyntax member || member.Expression is not IdentifierNameSyntax receiver) return "Not resolved";
                var type = g.Code.Root.DescendantNodes().OfType<FieldDeclarationSyntax>()
                    .FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == receiver.Identifier.ValueText))?.Declaration.Type.ToString()
                    ?? g.Code.Root.DescendantNodes().OfType<ParameterSyntax>().FirstOrDefault(p => p.Identifier.ValueText == receiver.Identifier.ValueText)?.Type?.ToString()
                    ?? receiver.Identifier.ValueText;
                return Safe(type.Split('.').Last() + "." + member.Name.Identifier.ValueText);
            }).Distinct().ToList();
            var guardTests = tests.Where(t => t.ProductionSymbols.Any(guardSymbols.Contains)).Select(t => t.Id).ToList();
            flows.Add(new SourceDataflow(Safe(field), ["Raw payload → not resolved", $"Parsed/model field {Safe(field)} → {(zero ? "constant zero assignment" : "assignment found")}",
                "Guard receives a field of the same name (symbol binding not resolved)", "Mapper/downstream → not resolved"], SourceConfidence.Partial,
                [At(c, assignment), .. guards.Select(g => At(g.Code, g.Call))], zero ? "Cross-layer gap: constant zero may bypass raw security classification; isolated guard tests do not resolve wiring" : "Cross-layer path requires manual verification", guardTests,
                guardSymbols.Count == 1 ? guardSymbols.Single() : "Not resolved"));
        }
        return flows;
    }
}
