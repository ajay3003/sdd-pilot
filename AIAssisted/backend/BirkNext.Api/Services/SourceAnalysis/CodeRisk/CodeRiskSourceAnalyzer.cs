using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.AiCodeReview;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BirkNext.Api.Services.SourceAnalysis.CodeRisk;

/// <summary>A language-specific code-risk analyzer. Only languages the Source Analysis reader actually retains can have one.</summary>
public interface IAiCodeRiskAnalyzer
{
    string Language { get; }
    string AnalyzerId { get; }
    bool Handles(string path);
}

/// <summary>
/// Records syntax-level code-risk observations for C# source (the only language whose source the Source Analysis reader retains) once at
/// upload, so the AI-Generated Code Review never re-reads the archive. Observations are facts with file:line and a short redacted detail —
/// never source text, string values or exception messages. Everything is single-pass or hash-indexed (no pairwise file comparison).
/// Other languages present in the archive are reported as unsupported, never as clean.
/// </summary>
public sealed class CodeRiskSourceAnalyzer : IAiCodeRiskAnalyzer
{
    public const int Version = 1;
    public const int MaxObservationsPerKind = 2_000;
    public const int MaxFiles = 20_000;

    public string Language => "C#";
    public string AnalyzerId => "dotnet-roslyn-syntax";
    public bool Handles(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);
    private static readonly (string Extension, string Language)[] UnsupportedLanguages =
        [(".ts", "TypeScript"), (".tsx", "TypeScript"), (".js", "JavaScript"), (".jsx", "JavaScript"), (".py", "Python"), (".java", "Java"), (".kt", "Kotlin"), (".go", "Go")];
    private static readonly Regex CommentMarker = new(@"\b(TODO|FIXME|HACK|XXX)\b|implement(ed)? later|not yet implemented", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PlaceholderText = new(@"^\s*(todo|tbd|not implemented|placeholder|lorem ipsum|dummy( data| value)?|fake (data|value)|implement later|stub)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ValidationAttributes = new(StringComparer.Ordinal)
        { "Required", "Range", "StringLength", "MaxLength", "MinLength", "RegularExpression", "EmailAddress", "Url", "Phone", "Compare", "CreditCard", "AllowedValues", "Length" };
    private static readonly HashSet<string> TestAttributes = new(StringComparer.Ordinal) { "Fact", "Theory", "Test", "TestCase", "TestMethod", "DataTestMethod", "SkippableFact", "SkippableTheory" };
    private static readonly string[] AssertionMarkers = ["Assert", "Should", "Verify", "Expect", "Throws", "Approve", "Snapshot", "MarkupMatches", "Received", "Match"];
    private static readonly string[] HttpVerbs = ["HttpGet", "HttpPost", "HttpPut", "HttpPatch", "HttpDelete"];

    /// <summary>Analyzes the workspace's C# files. <paramref name="testProjectDirectories"/> come from the snapshot's test inventory.</summary>
    public static CodeRiskSourceEvidence Analyze(Guid snapshotId, IReadOnlyList<SourceFile> files, IReadOnlyList<string> allPaths,
        IReadOnlyCollection<string> testProjectDirectories, CancellationToken ct = default)
    {
        var analyzer = new CodeRiskSourceAnalyzer();
        var projectDirs = allPaths.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).Select(Directory).Distinct(StringComparer.Ordinal)
            .OrderByDescending(d => d.Length).ToList();
        var testDirs = testProjectDirectories.Select(d => d.Replace('\\', '/').TrimEnd('/')).Where(d => d.Length > 0).ToHashSet(StringComparer.Ordinal);
        var limitations = new List<string>();
        var csFiles = files.Where(f => analyzer.Handles(f.Path) && !Generated(f.Path, f.Content)).Take(MaxFiles + 1).ToList();
        var truncated = csFiles.Count > MaxFiles;
        if (truncated) { csFiles = csFiles.Take(MaxFiles).ToList(); limitations.Add($"Code-risk analysis read the first {MaxFiles:N0} C# files; the rest were not analyzed."); }

        var units = new List<Unit>();
        foreach (var file in csFiles)
        {
            ct.ThrowIfCancellationRequested();
            var path = file.Path.Replace('\\', '/');
            var project = projectDirs.FirstOrDefault(d => d.Length == 0 || path.StartsWith(d + "/", StringComparison.Ordinal)) ?? "";
            var test = IsTest(path, project, testDirs);
            var tree = CSharpSyntaxTree.ParseText(WolverineSourceAnalyzer.BlankPrimaryConstructors(file.Content), Parse, path, cancellationToken: ct);
            units.Add(new Unit(path, project, test, tree.GetCompilationUnitRoot(ct)));
        }

        var observations = new List<CodeRiskObservation>();
        var counts = new Dictionary<CodeRiskObservationKind, int>();
        void Observe(CodeRiskObservationKind kind, Unit unit, SyntaxNode node, string symbol, string detail)
        {
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
            if (counts[kind] > MaxObservationsPerKind) { truncated = true; return; }
            observations.Add(new(kind, unit.Path, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, symbol, detail, unit.Test));
        }

        // Declared namespaces (for conservative unresolved-import detection) and per-project imports (usage evidence for dependencies).
        var declared = units.SelectMany(u => u.Root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString())).ToHashSet(StringComparer.Ordinal);
        var declaredPrefixes = declared.SelectMany(Prefixes).ToHashSet(StringComparer.Ordinal);
        var ownRoots = declared.Select(n => n.Split('.')[0]).Where(r => r is not ("System" or "Microsoft")).ToHashSet(StringComparer.Ordinal);
        var usage = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            // Every C# project folder is listed (even without imports): it is also the project index rules map changed files with.
            if (!usage.ContainsKey(unit.Project)) usage[unit.Project] = new(StringComparer.Ordinal);
            foreach (var u in unit.Root.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(u => u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name is not null))
            {
                var ns = u.Name!.ToString();
                usage[unit.Project].Add(ns);
                if (ownRoots.Contains(ns.Split('.')[0]) && !declaredPrefixes.Contains(ns))
                    Observe(CodeRiskObservationKind.UnresolvedNamespaceImport, unit, u, ns, "Imported namespace is not declared anywhere in this source snapshot.");
            }
        }

        var endpoints = new List<CodeEndpointAuthorization>();
        var configReads = new List<CodeLocation>();
        var dtoShapes = new Dictionary<string, List<CodeLocation>>(StringComparer.Ordinal);
        var methodBodies = new Dictionary<string, List<CodeLocation>>(StringComparer.Ordinal);
        var fromBodyTypes = new List<(Unit Unit, ParameterSyntax Parameter, string Type)>();
        var validatedTypes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var unit in units)
        {
            ct.ThrowIfCancellationRequested();
            var root = unit.Root;
            // Comment markers: one observation per file (count in detail) to keep comment-only TODOs informational and low-noise.
            var markers = root.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia))
                .Where(t => CommentMarker.IsMatch(t.ToString())).ToList();
            if (markers.Count > 0 && !unit.Test)
            {
                var first = markers[0];
                counts[CodeRiskObservationKind.CommentMarker] = counts.GetValueOrDefault(CodeRiskObservationKind.CommentMarker) + 1;
                if (counts[CodeRiskObservationKind.CommentMarker] <= MaxObservationsPerKind)
                    observations.Add(new(CodeRiskObservationKind.CommentMarker, unit.Path, first.GetLocation().GetLineSpan().StartLinePosition.Line + 1, "",
                        $"{markers.Count} TODO/FIXME-style comment(s)", false));
            }

            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var typeName = Qualified(type);
                if (HasValidation(type)) validatedTypes.Add(type.Identifier.Text);
                if (type.BaseList?.Types.Any(b => b.Type.ToString().StartsWith("AbstractValidator<", StringComparison.Ordinal)) == true)
                    foreach (var b in type.BaseList.Types.Select(b => b.Type).OfType<GenericNameSyntax>().Where(g => g.Identifier.Text == "AbstractValidator"))
                        validatedTypes.Add(b.TypeArgumentList.Arguments[0].ToString().Split('.').Last());
                if (type.BaseList?.Types.Any(b => b.Type.ToString() is "IValidatableObject" or "System.ComponentModel.DataAnnotations.IValidatableObject") == true)
                    validatedTypes.Add(type.Identifier.Text);

                if (!unit.Test && DtoShape(type) is { } shape)
                {
                    var key = unit.Project + "|" + shape;
                    if (!dtoShapes.TryGetValue(key, out var list)) dtoShapes[key] = list = [];
                    list.Add(new(unit.Path, Line(type), typeName));
                }

                if (!unit.Test) Endpoints(unit, type, typeName, endpoints);

                // Unused private methods: name never referenced elsewhere in the (non-partial) type. Attributes may mean reflection: skipped.
                // Interfaces declare members without bodies (no modifier ≠ private); partial types may reference members from another file.
                if (!unit.Test && type is not InterfaceDeclarationSyntax && !type.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    var identifiers = type.DescendantNodes().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.Text)
                        .Concat(type.DescendantNodes().OfType<GenericNameSyntax>().Select(g => g.Identifier.Text)).ToHashSet(StringComparer.Ordinal);
                    foreach (var method in type.Members.OfType<MethodDeclarationSyntax>().Where(m => IsPrivate(m) && m.AttributeLists.Count == 0 && (m.Body is not null || m.ExpressionBody is not null)
                        && m.ExplicitInterfaceSpecifier is null && !m.Modifiers.Any(SyntaxKind.OverrideKeyword) && !m.Modifiers.Any(SyntaxKind.PartialKeyword)))
                        if (!identifiers.Contains(method.Identifier.Text))
                            Observe(CodeRiskObservationKind.UnusedPrivateMethod, unit, method, $"{typeName}.{method.Identifier.Text}", "Private method is not referenced within its type.");
                }
            }

            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var name = $"{Qualified(method.Parent as TypeDeclarationSyntax)}.{method.Identifier.Text}";
                var isTestMethod = method.AttributeLists.SelectMany(a => a.Attributes).Any(a => TestAttributes.Contains(AttributeName(a)));
                if (isTestMethod)
                {
                    var statements = method.Body?.Statements.Count ?? (method.ExpressionBody is null ? 0 : 1);
                    if (statements == 0) Observe(CodeRiskObservationKind.EmptyTest, unit, method, name, "Test method has no statements.");
                    else if (!HasAssertion(method)) Observe(CodeRiskObservationKind.TestWithoutAssertion, unit, method, name, "Test method has no recognizable assertion or verification call.");
                    continue;
                }
                if (unit.Test) continue;
                foreach (var parameter in method.ParameterList.Parameters.Where(p => p.AttributeLists.SelectMany(a => a.Attributes).Any(a => AttributeName(a) == "FromBody") && p.Type is not null))
                    fromBodyTypes.Add((unit, parameter, parameter.Type!.ToString().Split('.').Last().TrimEnd('?')));

                if (method.Body is { } body)
                {
                    // Placeholder return: a TODO-style marker in the method and a body that only returns a literal/empty value.
                    var marker = method.DescendantTrivia().Any(t => (t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)) && CommentMarker.IsMatch(t.ToString()));
                    if (marker && body.Statements.Count == 1 && body.Statements[0] is ReturnStatementSyntax { Expression: { } returned } && IsPlaceholderValue(returned))
                        Observe(CodeRiskObservationKind.PlaceholderReturn, unit, method, name, "Method marked TODO/FIXME only returns a constant or empty value.");
                    var statementCount = body.DescendantNodes().OfType<StatementSyntax>().Count();
                    if (statementCount >= 8)
                    {
                        var fingerprint = NormalizedBody(method);
                        if (fingerprint is not null)
                        {
                            var key = unit.Project + "|" + fingerprint;
                            if (!methodBodies.TryGetValue(key, out var list)) methodBodies[key] = list = [];
                            list.Add(new(unit.Path, Line(method), name));
                        }
                    }
                }
            }

            if (unit.Test) continue;
            // Minimal APIs (often in top-level statements): app.MapGet("/route", …) with RequireAuthorization()/AllowAnonymous() on the chain or its route group.
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => InvocationName(i) is "MapGet" or "MapPost" or "MapPut" or "MapPatch" or "MapDelete"))
                AddMinimal(unit, invocation, endpoints);
            foreach (var creation in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(o => o.Type.ToString().Split('.').Last() == "NotImplementedException"))
                if (creation.Parent is ThrowStatementSyntax or ThrowExpressionSyntax)
                    Observe(CodeRiskObservationKind.NotImplementedThrow, unit, creation, Enclosing(creation), "Production code throws NotImplementedException.");

            foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression) && PlaceholderText.IsMatch(l.Token.ValueText)))
                if (literal.Ancestors().Any(a => a is ReturnStatementSyntax || a is ArrowExpressionClauseSyntax)
                    || literal.Parent?.Parent is ArgumentListSyntax { Parent: InvocationExpressionSyntax inv } && InvocationName(inv) is "Ok" or "Content" or "Json")
                    Observe(CodeRiskObservationKind.PlaceholderLiteral, unit, literal, Enclosing(literal), "A placeholder-like string is returned to the caller.");

            foreach (var catchClause in root.DescendantNodes().OfType<CatchClauseSyntax>())
            {
                var hasComment = catchClause.Block.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia))
                    || catchClause.Block.CloseBraceToken.LeadingTrivia.Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia));
                if (catchClause.Block.Statements.Count == 0 && !hasComment)
                    Observe(CodeRiskObservationKind.EmptyCatch, unit, catchClause, Enclosing(catchClause), "Exception is caught and silently discarded.");
                var broad = catchClause.Declaration is null || catchClause.Declaration.Type.ToString() is "Exception" or "System.Exception";
                var rethrows = catchClause.Block.DescendantNodes().Any(n => n is ThrowStatementSyntax or ThrowExpressionSyntax);
                if (broad && catchClause.Filter is null && !rethrows && catchClause.Block.DescendantNodes().OfType<ReturnStatementSyntax>().Any(r => r.Expression is { } e && IsSuccess(e)))
                    Observe(CodeRiskObservationKind.BroadCatchReturnsSuccess, unit, catchClause, Enclosing(catchClause), "A broad catch returns a success result.");
                var variable = catchClause.Declaration?.Identifier.Text;
                if (!string.IsNullOrEmpty(variable) && LeaksException(catchClause.Block, variable!))
                    Observe(CodeRiskObservationKind.ExceptionDetailReturned, unit, catchClause, Enclosing(catchClause), broad
                        ? "Exception message, text or stack trace of a broad catch is placed in a response."
                        : "The message of a specific exception type is placed in a response (it may be designed for clients).");
            }

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = InvocationName(invocation);
                switch (name)
                {
                    case "UseDeveloperExceptionPage" when !invocation.Ancestors().Any(a => a is IfStatementSyntax i && i.Condition.ToString().Contains("IsDevelopment", StringComparison.Ordinal)
                                                                              || a is ConditionalExpressionSyntax c && c.Condition.ToString().Contains("IsDevelopment", StringComparison.Ordinal)):
                        Observe(CodeRiskObservationKind.DeveloperExceptionPageUnconditional, unit, invocation, "", "Developer exception page is enabled without an environment condition.");
                        break;
                    case "AllowAnyOrigin":
                    case "SetIsOriginAllowed" when invocation.ArgumentList.Arguments.Count == 1 && AlwaysTrue(invocation.ArgumentList.Arguments[0].Expression):
                    case "WithOrigins" when invocation.ArgumentList.Arguments.Any(a => a.Expression is LiteralExpressionSyntax l && l.Token.ValueText == "*"):
                        var chain = ChainText(invocation);
                        Observe(CodeRiskObservationKind.PermissiveCors, unit, invocation, name,
                            chain.Contains(".AllowCredentials(", StringComparison.Ordinal) ? "Any origin is allowed together with credentials." : "Any origin is allowed.");
                        break;
                    case "GetSection" or "GetValue" or "GetConnectionString" or "GetRequiredSection" when ConfigReceiver(invocation)
                        && invocation.ArgumentList.Arguments.Count >= 1 && invocation.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax { Token.ValueText: { Length: > 0 } key }:
                        configReads.Add(new(unit.Path, Line(invocation), name == "GetConnectionString" ? "ConnectionStrings:" + key : key));
                        break;
                }
            }
            foreach (var access in root.DescendantNodes().OfType<ElementAccessExpressionSyntax>().Where(e => e.Expression.ToString().Split('.').Last().Contains("config", StringComparison.OrdinalIgnoreCase)))
                if (access.ArgumentList.Arguments.Count == 1 && access.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax { Token.ValueText: { Length: > 0 } key })
                    configReads.Add(new(unit.Path, Line(access), key));
        }

        // Input models without validation where the project validates sibling request models (project pattern, not a universal rule).
        foreach (var group in fromBodyTypes.GroupBy(x => x.Unit.Project))
        {
            var types = group.Select(x => x.Type).Distinct(StringComparer.Ordinal).ToList();
            if (!types.Any(validatedTypes.Contains)) continue;
            foreach (var (unit, parameter, type) in group.Where(x => !validatedTypes.Contains(x.Type)).DistinctBy(x => x.Type))
                Observe(CodeRiskObservationKind.InputModelWithoutValidation, unit, parameter, type, "Request body model has no validation while other request models in the project do.");
        }

        var duplicates = dtoShapes.Where(g => g.Value.Count > 1).Select(g => new CodeDuplicateGroup("Type", Hash(g.Key)[..16],
                "Types with an identical property shape in the same project.", g.Value.Take(10).ToList()))
            .Concat(methodBodies.Where(g => g.Value.Select(l => l.Symbol).Distinct().Count() > 1).Select(g => new CodeDuplicateGroup("Method", Hash(g.Key)[..16],
                "Methods with an identical normalized syntax tree (identifiers of locals and parameters normalized) in the same project.", g.Value.Take(10).ToList())))
            .OrderBy(d => d.Locations[0].File, StringComparer.Ordinal).ThenBy(d => d.Locations[0].Line).Take(MaxObservationsPerKind).ToList();

        var languages = new List<CodeRiskLanguageCoverage> { new(analyzer.Language, analyzer.AnalyzerId, true, csFiles.Count) };
        foreach (var group in allPaths.Select(p => (Path: p, Lang: UnsupportedLanguages.FirstOrDefault(l => p.EndsWith(l.Extension, StringComparison.OrdinalIgnoreCase)).Language))
                     .Where(x => x.Lang is not null && !x.Path.Contains("/node_modules/", StringComparison.Ordinal) && !x.Path.Contains("/wwwroot/lib/", StringComparison.Ordinal))
                     .GroupBy(x => x.Lang!))
            languages.Add(new(group.Key, "", false, group.Count(), $"No {group.Key} code-risk analyzer: Source Analysis does not retain {group.Key} source."));

        return new CodeRiskSourceEvidence
        {
            AnalyzerVersion = Version, SnapshotId = snapshotId, Languages = languages,
            ProductionFiles = units.Count(u => !u.Test), TestFiles = units.Count(u => u.Test),
            Observations = observations.OrderBy(o => o.File, StringComparer.Ordinal).ThenBy(o => o.Line).ThenBy(o => o.Kind).ToList(),
            Endpoints = endpoints.OrderBy(e => e.Key, StringComparer.Ordinal).ToList(), Duplicates = duplicates,
            ProjectUsage = usage.OrderBy(u => u.Key, StringComparer.Ordinal).Select(u => new CodeProjectUsage(u.Key, u.Value.Order(StringComparer.Ordinal).ToList())).ToList(),
            ConfigurationKeyReads = configReads.DistinctBy(c => (c.File, c.Symbol)).Take(MaxObservationsPerKind).ToList(),
            Truncated = truncated, Limitations = limitations,
        };
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Unit(string Path, string Project, bool Test, CompilationUnitSyntax Root);

    private static string Directory(string path) { var p = path.Replace('\\', '/'); var i = p.LastIndexOf('/'); return i < 0 ? "" : p[..i]; }

    private static IEnumerable<string> Prefixes(string ns)
    {
        var parts = ns.Split('.');
        for (var i = 1; i <= parts.Length; i++) yield return string.Join('.', parts.Take(i));
    }

    private static bool Generated(string path, string content) =>
        path.Contains("/obj/", StringComparison.Ordinal) || path.Contains("/bin/", StringComparison.Ordinal) || path.Contains("/Migrations/", StringComparison.Ordinal)
        || path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) || content.AsSpan(0, Math.Min(content.Length, 600)).Contains("<auto-generated", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTest(string path, string project, IReadOnlySet<string> testDirs)
    {
        if (testDirs.Contains(project)) return true;
        var folder = project.Split('/').LastOrDefault() ?? "";
        return Regex.IsMatch(folder, @"(\.|^)(Tests?|UnitTests|IntegrationTests|ContractTests|E2ETests|Specs|Benchmarks)$", RegexOptions.IgnoreCase)
            || path.Split('/').Any(s => s is "test" or "tests" or "Tests");
    }

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static string Qualified(TypeDeclarationSyntax? type)
    {
        if (type is null) return "";
        var ns = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        var outer = string.Join('.', type.Ancestors().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text));
        return string.Join('.', new[] { ns, outer, type.Identifier.Text }.Where(s => !string.IsNullOrEmpty(s)));
    }

    private static string Enclosing(SyntaxNode node)
    {
        var member = node.Ancestors().FirstOrDefault(a => a is MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax or LocalFunctionStatementSyntax);
        var type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var memberName = member switch
        {
            MethodDeclarationSyntax m => m.Identifier.Text, ConstructorDeclarationSyntax c => c.Identifier.Text, PropertyDeclarationSyntax p => p.Identifier.Text,
            LocalFunctionStatementSyntax l => l.Identifier.Text, _ => null,
        };
        return string.Join('.', new[] { type?.Identifier.Text, memberName }.Where(s => !string.IsNullOrEmpty(s)));
    }

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString().Split('.').Last();
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, Func<string, bool> match) => lists.SelectMany(a => a.Attributes).Any(a => match(AttributeName(a)));

    private static bool IsPrivate(MethodDeclarationSyntax method) =>
        method.Modifiers.Any(SyntaxKind.PrivateKeyword) || !method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.InternalKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword));

    private static bool HasValidation(TypeDeclarationSyntax type) =>
        type.Members.OfType<PropertyDeclarationSyntax>().Any(p => HasAttribute(p.AttributeLists, ValidationAttributes.Contains))
        || (type is RecordDeclarationSyntax { ParameterList: { } parameters } && parameters.Parameters.Any(p => HasAttribute(p.AttributeLists, ValidationAttributes.Contains)));

    /// <summary>A data-only type's normalized shape: sorted property name:type pairs (≥ 3), no methods. Null for anything with behaviour.</summary>
    private static string? DtoShape(TypeDeclarationSyntax type)
    {
        if (type is InterfaceDeclarationSyntax || type.Modifiers.Any(SyntaxKind.AbstractKeyword) || type.Modifiers.Any(SyntaxKind.PartialKeyword)) return null;
        if (type.Members.Any(m => m is MethodDeclarationSyntax or ConstructorDeclarationSyntax or OperatorDeclarationSyntax or EventFieldDeclarationSyntax)) return null;
        var properties = type.Members.OfType<PropertyDeclarationSyntax>().Where(p => p.AccessorList is not null && p.AccessorList.Accessors.All(a => a.Body is null && a.ExpressionBody is null))
            .Select(p => $"{p.Identifier.Text.ToLowerInvariant()}:{p.Type.ToString().Replace(" ", "")}").ToList();
        if (type is RecordDeclarationSyntax { ParameterList: { } list })
            properties.AddRange(list.Parameters.Where(p => p.Type is not null).Select(p => $"{p.Identifier.Text.ToLowerInvariant()}:{p.Type!.ToString().Replace(" ", "")}"));
        return properties.Count >= 3 ? string.Join(';', properties.Order(StringComparer.Ordinal)) : null;
    }

    /// <summary>Normalized syntax of a method body: tokens with local/parameter identifiers replaced, trivia removed. Null when too small.</summary>
    private static string? NormalizedBody(MethodDeclarationSyntax method)
    {
        var locals = method.ParameterList.Parameters.Select(p => p.Identifier.Text)
            .Concat(method.DescendantNodes().OfType<VariableDeclaratorSyntax>().Select(v => v.Identifier.Text))
            .Concat(method.DescendantNodes().OfType<SingleVariableDesignationSyntax>().Select(v => v.Identifier.Text)).ToHashSet(StringComparer.Ordinal);
        var tokens = method.Body!.DescendantTokens().Select(t => t.IsKind(SyntaxKind.IdentifierToken) && locals.Contains(t.Text) ? "$v" : t.Text).ToList();
        return tokens.Count < 60 ? null : Hash(string.Join(' ', tokens));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static bool HasAssertion(MethodDeclarationSyntax method)
    {
        var siblings = (method.Parent as TypeDeclarationSyntax)?.Members.OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.Text).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var text = invocation.Expression.ToString();
            if (AssertionMarkers.Any(m => text.Contains(m, StringComparison.Ordinal))) return true;
            // A helper method of the test class may hold the assertions; without following it, the test is not called weak.
            if (siblings.Contains(InvocationName(invocation))) return true;
        }
        return false;
    }

    private static string InvocationName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        MemberBindingExpressionSyntax b => b.Name.Identifier.Text,
        _ => "",
    };

    private static bool IsPlaceholderValue(ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax => true,
        DefaultExpressionSyntax => true,
        ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 0, Initializer: null } => true,
        ImplicitObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 0, Initializer: null } => true,
        ArrayCreationExpressionSyntax { Initializer: null or { Expressions.Count: 0 } } a => a.Type.RankSpecifiers.All(r => r.Sizes.All(z => z is OmittedArraySizeExpressionSyntax || z.ToString() == "0")),
        MemberAccessExpressionSyntax m => m.ToString() is "Task.CompletedTask" or "string.Empty" or "String.Empty" || m.Name.Identifier.Text == "Empty",
        InvocationExpressionSyntax i => InvocationName(i) is "Empty" or "FromResult" && i.ArgumentList.Arguments.All(a => IsPlaceholderValue(a.Expression)),
        _ => false,
    };

    private static bool IsSuccess(ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax l => l.IsKind(SyntaxKind.TrueLiteralExpression),
        InvocationExpressionSyntax i => InvocationName(i) is "Ok" or "NoContent" or "Accepted" || i.ToString().StartsWith("Results.Ok", StringComparison.Ordinal)
            || i.ToString().StartsWith("TypedResults.Ok", StringComparison.Ordinal),
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text is "Success" or "Succeeded" or "Ok",
        _ => false,
    };

    private static bool LeaksException(BlockSyntax block, string variable)
    {
        bool Mentions(SyntaxNode node) => node.DescendantNodesAndSelf().Any(n =>
            n is MemberAccessExpressionSyntax m && m.Expression.ToString() == variable && m.Name.Identifier.Text is "Message" or "StackTrace" or "ToString"
            || n is InterpolationSyntax ip && ip.Expression.ToString() == variable);
        // Only HTTP response constructions count as "returned to the client": a service returning a Result to its own caller or UI is not.
        foreach (var invocation in block.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = InvocationName(invocation);
            var responseCall = name is "Ok" or "BadRequest" or "Problem" or "StatusCode" or "Json" or "Content" or "NotFound" or "Conflict" or "UnprocessableEntity"
                or "ValidationProblem" or "WriteAsync" or "WriteAsJsonAsync" || invocation.Expression.ToString().StartsWith("Results.", StringComparison.Ordinal)
                || invocation.Expression.ToString().StartsWith("TypedResults.", StringComparison.Ordinal);
            if (responseCall && Mentions(invocation.ArgumentList)) return true;
        }
        foreach (var creation in block.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(o => o.Type.ToString().Split('.').Last() is "ObjectResult" or "BadRequestObjectResult" or "ContentResult" or "JsonResult" or "ProblemDetails"))
            if (Mentions(creation)) return true;
        return false;
    }

    private static bool AlwaysTrue(ExpressionSyntax e) => e is SimpleLambdaExpressionSyntax { ExpressionBody: LiteralExpressionSyntax l } && l.IsKind(SyntaxKind.TrueLiteralExpression)
        || e is ParenthesizedLambdaExpressionSyntax { ExpressionBody: LiteralExpressionSyntax p } && p.IsKind(SyntaxKind.TrueLiteralExpression);

    /// <summary>The whole fluent chain an invocation belongs to (outermost invocation's text), to see AllowCredentials beside AllowAnyOrigin.</summary>
    private static string ChainText(InvocationExpressionSyntax invocation)
    {
        SyntaxNode node = invocation;
        while (node.Parent is MemberAccessExpressionSyntax or InvocationExpressionSyntax) node = node.Parent;
        var lambda = invocation.Ancestors().FirstOrDefault(a => a is LambdaExpressionSyntax);
        return (lambda ?? node).ToString();
    }

    private static bool ConfigReceiver(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax m && m.Expression.ToString().Split('.').Last().Contains("config", StringComparison.OrdinalIgnoreCase);

    private static void Endpoints(Unit unit, TypeDeclarationSyntax type, string typeName, List<CodeEndpointAuthorization> endpoints)
    {
        var classAuthorized = HasAttribute(type.AttributeLists, n => n.EndsWith("Authorize", StringComparison.Ordinal));
        var classAnonymous = HasAttribute(type.AttributeLists, n => n == "AllowAnonymous");
        var isController = !type.Modifiers.Any(SyntaxKind.AbstractKeyword) && (HasAttribute(type.AttributeLists, n => n == "ApiController")
            || type.BaseList?.Types.Any(b => b.Type.ToString() is "Controller" or "ControllerBase" || b.Type.ToString().EndsWith("Controller", StringComparison.Ordinal)) == true);
        var isGraphQl = HasAttribute(type.AttributeLists, n => n is "QueryType" or "MutationType" or "SubscriptionType" or "ExtendObjectType")
            || type.Identifier.Text is "Query" or "Mutation" or "Subscription";
        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) && !m.Modifiers.Any(SyntaxKind.StaticKeyword) || isGraphQl && m.Modifiers.Any(SyntaxKind.PublicKeyword)))
        {
            var methodAuthorized = HasAttribute(method.AttributeLists, n => n.EndsWith("Authorize", StringComparison.Ordinal));
            var methodAnonymous = HasAttribute(method.AttributeLists, n => n == "AllowAnonymous");
            string? kind = null, verb = null;
            if (isController && (verb = method.AttributeLists.SelectMany(a => a.Attributes).Select(AttributeName).FirstOrDefault(n => HttpVerbs.Contains(n))) is not null) kind = "HTTP";
            else if (isGraphQl && !isController) { kind = "GraphQL"; verb = type.Identifier.Text; }
            if (kind is null) continue;
            var basis = methodAuthorized ? "[Authorize] on the operation" : classAuthorized ? "[Authorize] on the type" : methodAnonymous || classAnonymous ? "[AllowAnonymous]" : "no authorization metadata";
            endpoints.Add(new($"{kind}|{typeName}.{method.Identifier.Text}|{verb}", kind, $"{(kind == "HTTP" ? verb!.Replace("Http", "").ToUpperInvariant() : "GraphQL")} {type.Identifier.Text}.{method.Identifier.Text}",
                unit.Path, Line(method), methodAuthorized || classAuthorized && !methodAnonymous, methodAnonymous || classAnonymous && !methodAuthorized, basis));
        }
    }

    private static void AddMinimal(Unit unit, InvocationExpressionSyntax invocation, List<CodeEndpointAuthorization> endpoints)
    {
        if (invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax { Token.ValueText: var route }) return;
        var verb = InvocationName(invocation)[3..].ToUpperInvariant();
        SyntaxNode outer = invocation;
        while (outer.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax parent }) outer = parent;
        var chain = outer.ToString();
        var group = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression.ToString();
        var scope = (SyntaxNode?)invocation.Ancestors().OfType<BlockSyntax>().LastOrDefault() ?? invocation.SyntaxTree.GetRoot();
        var groupDeclaration = group is null ? null : scope.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == group)?.Initializer?.Value.ToString();
        var authorized = chain.Contains(".RequireAuthorization(", StringComparison.Ordinal) || groupDeclaration?.Contains(".RequireAuthorization(", StringComparison.Ordinal) == true;
        var anonymous = chain.Contains(".AllowAnonymous(", StringComparison.Ordinal);
        endpoints.Add(new($"HTTP|{verb} {route}|minimal", "HTTP", $"{verb} {route}", unit.Path, Line(invocation), authorized && !anonymous, anonymous,
            authorized ? "RequireAuthorization()" : anonymous ? "AllowAnonymous()" : "no authorization metadata"));
    }
}
