using System.Text.RegularExpressions;
using BirkNext.Integrations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

/// <summary>
/// Multi-stage source path: CDC field → adapter model → HTTP ingestion contract → ingestion DTO → persisted entity → domain event →
/// envelope → outbox → Service Bus. Everything is discovered from syntax (types, projections, HTTP routes, DbSets, publish calls,
/// dispatchers); no application-specific name is built in, so a new field, event or test in a later archive appears in its snapshot.
/// Syntax evidence only — no compilation, no binding, no execution. What cannot be proven stays "Not resolved".
/// </summary>
internal static class IqrPathAnalyzer
{
    private const int MaxSteps = 25;
    private static readonly Regex SensitiveName = new(@"(?i)(f(ø|oe|o)dselsnummer|f(ø|oe|o)dselsdato|^f(ø|oe|o)dt$|birth|national-?id|\bssn\b|social-?security|passport|personnummer|dufnummer|\bd-?nummer\b|^navn$|fornavn|etternavn|mellomnavn|adresse|telefon|e-?post|email)", RegexOptions.Compiled);
    private static readonly Regex AuditMetadata = new(@"(?i)^(endret|opprettet|modified|updated|created|changed)(av|by|tidspunkt|at|on|time|timestamp)?$|tidspunkt$|timestamp$|^kilde$|^source$|korrelasjon|correlation", RegexOptions.Compiled);
    private static readonly string[] JsonTypes = ["JsonElement", "JsonNode", "JsonObject", "JObject", "JToken"];
    private static readonly string[] DirectReads = ["GetString", "GetInt32", "GetInt64", "GetBool", "GetBoolean", "GetGuid", "GetDecimal", "GetDouble", "GetProperty", "TryGetProperty", "GetOptionalInt", "GetOptionalString", "GetValue"];

    private static string Safe(string value) => IqrSourceArchiveReader.SafeLabel(value);
    private static readonly Regex StrongIdentifier = new(@"(?i)(f(ø|oe|o)dselsnummer|personnummer|dufnummer|birth|f(ø|oe|o)dselsdato|^f(ø|oe|o)dt$|national-?id|\bssn\b|social-?security|passport)", RegexOptions.Compiled);
    private static readonly Regex NameOnly = new(@"(?i)^(navn|fornavn|etternavn|mellomnavn)$", RegexOptions.Compiled);
    internal static bool IsSensitive(string field) => SensitiveName.IsMatch(field);
    /// <summary>A plain name field is personal data only on a model that also carries a personal identifier (a municipality's name is not).</summary>
    private static bool IsSensitiveOn(string field, IEnumerable<string> siblings) => IsSensitive(field) && (!NameOnly.IsMatch(field) || siblings.Any(StrongIdentifier.IsMatch));

    private sealed record TypeInfo(string Name, string Path, string Project, TypeDeclarationSyntax Syntax, Dictionary<string, string> Properties, bool IsTest)
    {
        public string Root => Path.Split('/')[0];
        /// <summary>Node identity: the simple name, qualified with the component folder when the name exists in several components.</summary>
        public string Key { get; set; } = Name;
        /// <summary>Properties excluded from serialization ([JsonIgnore]): an intentional filter at a JSON boundary.</summary>
        public HashSet<string> Ignored { get; } = new(StringComparer.Ordinal);
        public bool HasProperty(string p) => Properties.Keys.Any(k => k.Equals(p, StringComparison.OrdinalIgnoreCase));
        public string? PropertyType(string p) => Properties.FirstOrDefault(k => k.Key.Equals(p, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private sealed record Edge(string From, string To, FieldTransformation Transformation, string Detail, SourceLocation Location);
    private sealed record Publication(string Topic, string Subject, string SessionId, string Priority, string? EventType, string Method, SourceLocation Location, string Path);
    private static string Display(string key) => key.Split('@')[0];
    private static string DisplayNode(string node) => node.StartsWith("cdc:", StringComparison.Ordinal) && node.Contains('|')
        ? $"CDC {Regex.Replace(node[4..node.IndexOf('|')], @"@.+$", "")}.{node[(node.IndexOf('|') + 1)..]}" : Regex.Replace(node, @"@[^.|]+", "");
    private static string CdcField(string node) => node.Contains('|') ? node[(node.IndexOf('|') + 1)..] : node.Replace("cdc:", "");
    private static string StripKeys(string text) => Regex.Replace(text, @"@[A-Za-z0-9_\-]+", "");
    private sealed record TestFacts(DeveloperTestEvidence Evidence, HashSet<string> Calls, HashSet<string> Asserted, HashSet<string> Constructed, HashSet<string> NonNullAssigned,
        Dictionary<string, int> CallCounts, HashSet<string> Referenced);

    public static IntegrationPathEvidence? Analyze(IReadOnlyList<IqrSourceAnalyzer.Code> allProduction, IReadOnlyList<IqrSourceAnalyzer.Code> all,
        IReadOnlyList<DeveloperTestEvidence> tests, CancellationToken ct)
    {
        var limitations = new List<string>();
        var production = allProduction;
        var types = new Dictionary<string, List<TypeInfo>>(StringComparer.Ordinal);
        foreach (var c in production)
        foreach (var t in c.Root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in t.Members.OfType<PropertyDeclarationSyntax>()) props[p.Identifier.ValueText] = p.Type.ToString();
            if (RecordParameters(t) is { } list) foreach (var p in list.Parameters) if (p.Type is not null) props[p.Identifier.ValueText] = p.Type.ToString();
            var info = new TypeInfo(t.Identifier.ValueText, c.Path, c.Project?.Name ?? "Unknown", t, props, false);
            static bool JsonIgnored(SyntaxList<AttributeListSyntax> lists) => lists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString().Split('.').Last() is "JsonIgnore" or "JsonIgnoreAttribute");
            foreach (var p in t.Members.OfType<PropertyDeclarationSyntax>().Where(p => JsonIgnored(p.AttributeLists))) info.Ignored.Add(p.Identifier.ValueText);
            if (RecordParameters(t) is { } rp) foreach (var p in rp.Parameters.Where(p => JsonIgnored(p.AttributeLists))) info.Ignored.Add(p.Identifier.ValueText);
            if (!types.TryGetValue(info.Name, out var bucket)) types[info.Name] = bucket = [];
            bucket.Add(info);
        }
        foreach (var bucket in types.Values.Where(b => b.Select(t => t.Root).Distinct().Count() > 1))
            foreach (var t in bucket) t.Key = $"{t.Name}@{t.Root.Replace('.', '_').Replace('@', '_')}";
        var byKey = types.Values.SelectMany(b => b).GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        TypeInfo? Resolve(string? typeName, string contextPath)
        {
            if (typeName is null) return null;
            var simple = Simple(typeName);
            if (!types.TryGetValue(simple, out var bucket)) return null;
            var root = contextPath.Split('/')[0];
            var sameRoot = bucket.Where(b => b.Root == root).ToList();
            return sameRoot.Count == 1 ? sameRoot[0] : bucket.Count == 1 ? bucket[0] : null;
        }
        var methods = allProduction.SelectMany(c => c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => (Code: c, Method: m))).ToList();
        string? ReturnTypeOf(string methodName, string contextPath)
        {
            var candidates = methods.Where(m => m.Method.Identifier.ValueText == methodName).ToList();
            var root = contextPath.Split('/')[0];
            var chosen = candidates.Where(c => c.Code.Path.Split('/')[0] == root).Select(c => c.Method.ReturnType.ToString()).Distinct().ToList();
            return chosen.Count == 1 ? UnwrapTask(chosen[0]) : null;
        }

        // ── Type of an identifier / expression (syntax-only: parameters, typed locals, fields, catalog properties) ─────
        string? TypeOfIdentifier(string name, SyntaxNode at, string path)
        {
            foreach (var ancestor in at.AncestorsAndSelf())
            {
                IEnumerable<ParameterSyntax> parameters = ancestor switch
                {
                    BaseMethodDeclarationSyntax m => m.ParameterList.Parameters,
                    LocalFunctionStatementSyntax l => l.ParameterList.Parameters,
                    ParenthesizedLambdaExpressionSyntax l => l.ParameterList.Parameters,
                    SimpleLambdaExpressionSyntax l => [l.Parameter],
                    _ => [],
                };
                if (parameters.FirstOrDefault(p => p.Identifier.ValueText == name) is { Type: { } pt }) return pt.ToString();
                if (ancestor is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)
                {
                    var local = ancestor.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.ValueText == name);
                    if (local?.Parent is VariableDeclarationSyntax decl)
                    {
                        if (!decl.Type.IsVar) return decl.Type.ToString();
                        return local.Initializer?.Value switch
                        {
                            ObjectCreationExpressionSyntax o => o.Type.ToString(),
                            AwaitExpressionSyntax { Expression: InvocationExpressionSyntax inv } => ReturnTypeOf(MethodName(inv) ?? "", path),
                            InvocationExpressionSyntax inv => ReturnTypeOf(MethodName(inv) ?? "", path),
                            _ => null,
                        };
                    }
                    var foreachVar = ancestor.DescendantNodes().OfType<ForEachStatementSyntax>().FirstOrDefault(f => f.Identifier.ValueText == name);
                    if (foreachVar is not null)
                        return foreachVar.Type.IsVar ? ElementType(TypeOfExpression(foreachVar.Expression, path) ?? "") : foreachVar.Type.ToString();
                }
                if (ancestor is TypeDeclarationSyntax type)
                {
                    var field = type.Members.OfType<FieldDeclarationSyntax>().FirstOrDefault(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == name));
                    if (field is not null) return field.Declaration.Type.ToString();
                    var prop = type.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.ValueText == name);
                    if (prop is not null) return prop.Type.ToString();
                }
            }
            return null;
        }
        string? TypeOfExpression(ExpressionSyntax expression, string path) => expression switch
        {
            IdentifierNameSyntax id => TypeOfIdentifier(id.Identifier.ValueText, id, path),
            MemberAccessExpressionSyntax m => TypeOfExpression(m.Expression, path) is { } owner ? Resolve(owner, path)?.PropertyType(m.Name.Identifier.ValueText) : null,
            ParenthesizedExpressionSyntax p => TypeOfExpression(p.Expression, path),
            PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } p => TypeOfExpression(p.Operand, path),
            _ => null,
        };

        bool IsJsonRead(InvocationExpressionSyntax inv, string path, out string field)
        {
            field = "";
            var literal = inv.ArgumentList.Arguments.Select(a => a.Expression).OfType<LiteralExpressionSyntax>().FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression));
            var name = MethodName(inv);
            if (literal is null || name is null || !(name.StartsWith("Get", StringComparison.Ordinal) || name.StartsWith("TryGet", StringComparison.Ordinal))) return false;
            var candidates = inv.ArgumentList.Arguments.Select(a => a.Expression).Where(e => e != literal).ToList();
            if (inv.Expression is MemberAccessExpressionSyntax receiver) candidates.Add(receiver.Expression);
            var json = candidates.Any(e => TypeOfExpression(e, path) is { } t && JsonTypes.Contains(Simple(t)));
            if (!json) return false;
            field = literal.Token.ValueText;
            return field.Length > 0;
        }

        // HTTP: outbound JSON calls vs mapped endpoints, matched by verb + path.
        var outbound = new List<(string Verb, string Path, string Type, IqrSourceAnalyzer.Code Code, SyntaxNode At)>();
        var endpoints = new List<(string Verb, string Path, string Type, IqrSourceAnalyzer.Code Code, SyntaxNode At)>();
        foreach (var c in production)
        foreach (var inv in c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = MethodName(inv) ?? "";
            var verb = Regex.Match(name, "^(Put|Post|Patch)AsJsonAsync$") is { Success: true } v ? v.Groups[1].Value.ToUpperInvariant()
                : Regex.Match(name, "^Map(Put|Post|Patch)$") is { Success: true } e ? e.Groups[1].Value.ToUpperInvariant() : null;
            if (verb is null || inv.ArgumentList.Arguments.Count < 2) continue;
            var path = ConstantString(inv.ArgumentList.Arguments[0].Expression, c);
            if (path is null) continue;
            path = Safe(path);
            if (name.StartsWith("Map", StringComparison.Ordinal))
            {
                var lambda = inv.ArgumentList.Arguments.Select(a => a.Expression).OfType<ParenthesizedLambdaExpressionSyntax>().FirstOrDefault();
                var request = lambda?.ParameterList.Parameters.Select(p => p.Type?.ToString()).FirstOrDefault(t => t is not null && Resolve(t, c.Path) is not null);
                if (request is not null) endpoints.Add((verb, Normalize(path), Resolve(request, c.Path)!.Key, c, inv));
            }
            else if (TypeOfExpression(inv.ArgumentList.Arguments[1].Expression, c.Path) is { } sent && Resolve(sent, c.Path) is { } sentInfo)
                outbound.Add((verb, Normalize(path), sentInfo.Key, c, inv));
        }
        outbound = outbound.DistinctBy(o => (o.Verb, o.Path, o.Type)).ToList();
        var scopeRoots = outbound.SelectMany(o => endpoints.Where(e => e.Verb == o.Verb && e.Path == o.Path).SelectMany(e => new[] { Root(o.Code.Path), Root(e.Code.Path) })).ToHashSet(StringComparer.Ordinal);
        if (scopeRoots.Count == 0)
            scopeRoots = allProduction.Where(c => c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => MethodName(i) is "SendMessageAsync" or "SendMessagesAsync")).Select(c => Root(c.Path)).ToHashSet(StringComparer.Ordinal);
        if (scopeRoots.Count > 0)
        {
            production = allProduction.Where(c => scopeRoots.Contains(Root(c.Path))).ToList();
            all = all.Where(c => scopeRoots.Contains(Root(c.Path))).ToList();
            outbound.RemoveAll(o => !scopeRoots.Contains(Root(o.Code.Path)));
            endpoints.RemoveAll(e => !scopeRoots.Contains(Root(e.Code.Path)));
        }
        methods = production.SelectMany(c => c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(m => (Code: c, Method: m))).ToList();

        // ── Projections: initializers, record constructors, update assignments ──────────────────────────────────────────
        var edges = new List<Edge>();
        var targetTypes = new HashSet<string>(StringComparer.Ordinal);
        var cdcTargets = new HashSet<string>(StringComparer.Ordinal);
        var jsonReadMethods = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<(string Type, string Symbol)>();
        var serializedCopies = new List<(string Entity, string Property, string Source, SourceLocation Location)>();

        foreach (var c in production)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var m in c.Root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                if (m.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => IsJsonRead(i, c.Path, out _)) && m is MethodDeclarationSyntax md)
                    jsonReadMethods.Add(md.Identifier.ValueText);
        }

        (List<string> Refs, FieldTransformation Kind, string Detail) Classify(ExpressionSyntax expression, IqrSourceAnalyzer.Code c, string target, int depth = 0)
        {
            expression = Unwrap(expression);
            var refs = RefsOf(expression, c, depth);
            var direct = DirectRef(expression, c);
            FieldTransformation kind;
            string detail;
            if (direct is not null)
            {
                var field = direct.Split('.').Last().Replace("cdc:", "");
                kind = field.Equals(target, StringComparison.OrdinalIgnoreCase) || Fold(field) == Fold(target) ? FieldTransformation.PassThrough : FieldTransformation.Renamed;
                detail = kind == FieldTransformation.PassThrough ? "Copied unchanged" : $"Copied from {Safe(field)}";
                if (expression is InvocationExpressionSyntax read && MethodName(read) is { } readName && !DirectReads.Contains(readName))
                { kind = FieldTransformation.Converted; detail = $"Converted by {Safe(readName)}"; }
                return ([direct], kind, detail);
            }
            switch (expression)
            {
                case IsPatternExpressionSyntax:
                case BinaryExpressionSyntax b when (b.IsKind(SyntaxKind.NotEqualsExpression) || b.IsKind(SyntaxKind.EqualsExpression)) && (IsNull(b.Left) || IsNull(b.Right)):
                    return (refs, FieldTransformation.Booleanized, "Presence flag (value not copied)");
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.NotEqualsExpression) || b.IsKind(SyntaxKind.EqualsExpression):
                    return (refs, FieldTransformation.Booleanized, "Change/comparison flag (value not copied)");
                case MemberAccessExpressionSyntax m when m.Name.Identifier.ValueText is "HasValue":
                    return (refs, FieldTransformation.Booleanized, "Presence flag (value not copied)");
                case InvocationExpressionSyntax inv when MethodName(inv) is "IsNullOrEmpty" or "IsNullOrWhiteSpace" or "Any":
                    return (refs, FieldTransformation.Booleanized, "Presence flag (value not copied)");
                case ConditionalExpressionSyntax cond:
                    return (refs, FieldTransformation.Conditional, $"Conditional on {Describe(RefsOf(cond.Condition, c, depth))}");
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.CoalesceExpression):
                    return (refs, IsConstant(Unwrap(b.Right)) ? FieldTransformation.Defaulted : FieldTransformation.Derived,
                        IsConstant(Unwrap(b.Right)) ? "Fallback to a constant when missing" : $"First available of {Describe(refs)}");
                case InterpolatedStringExpressionSyntax:
                    return (refs, refs.Count == 0 ? FieldTransformation.Defaulted : FieldTransformation.Derived, $"Composed from {Describe(refs)}");
                case InvocationExpressionSyntax inv when MethodName(inv) == "Serialize":
                    return (refs, FieldTransformation.Derived, "Serialized copy of the whole object (raw values retained)");
                case InvocationExpressionSyntax inv:
                    return (refs, refs.Count == 0 ? FieldTransformation.Defaulted : refs.Count == 1 ? FieldTransformation.Converted : FieldTransformation.Derived,
                        refs.Count == 0 ? "Generated / constant (no source field)" : $"{(refs.Count == 1 ? "Converted" : "Derived")} by {Safe(MethodName(inv) ?? "call")}");
                case IdentifierNameSyntax id when LocalInitializer(id) is { } init && depth < 3:
                    return Classify(init, c, target, depth + 1);
                case IdentifierNameSyntax id when depth < 2 && CallSiteArguments(id) is { Count: > 0 } args:
                {
                    var results = args.Select(a => Classify(a.Argument, a.Code, target, depth + 1)).ToList();
                    var merged = results.SelectMany(r => r.Refs).Distinct().ToList();
                    var kinds = results.Select(r => r.Kind).Distinct().ToList();
                    return (merged, kinds.Count == 1 ? kinds[0] : merged.Count == 0 ? FieldTransformation.NotResolved : FieldTransformation.Derived,
                        kinds.Count == 1 ? results[0].Detail + " (via call-site argument)" : $"Passed in by callers from {Describe(merged)}");
                }
                case IdentifierNameSyntax:
                    return (refs, refs.Count == 0 ? FieldTransformation.NotResolved : FieldTransformation.PassThrough, refs.Count == 0 ? "Method parameter or value not resolved" : "Copied");
                case LiteralExpressionSyntax or DefaultExpressionSyntax:
                    return ([], FieldTransformation.Defaulted, "Constant");
                case MemberAccessExpressionSyntax when refs.Count == 0:
                    return ([], FieldTransformation.Defaulted, "Configuration / constant (no source field)");
                default:
                    return (refs, refs.Count == 0 ? FieldTransformation.NotResolved : FieldTransformation.Derived, refs.Count == 0 ? "Not resolved" : $"Derived from {Describe(refs)}");
            }
        }

        // A parameter's values: the matching argument at every call site of the enclosing method (by name and position; overloads unresolved).
        List<(ExpressionSyntax Argument, IqrSourceAnalyzer.Code Code)> CallSiteArguments(IdentifierNameSyntax id)
        {
            var method = id.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var index = method?.ParameterList.Parameters.IndexOf(p => p.Identifier.ValueText == id.Identifier.ValueText) ?? -1;
            if (method is null || index < 0) return [];
            // Only value-like parameters: a catalog-typed or generic parameter would bypass the projection that builds it.
            var parameterType = method.ParameterList.Parameters[index].Type?.ToString() ?? "";
            var typeParameters = method.TypeParameterList?.Parameters.Select(t => t.Identifier.ValueText)
                .Concat(method.Ancestors().OfType<TypeDeclarationSyntax>().SelectMany(t => t.TypeParameterList?.Parameters.Select(x => x.Identifier.ValueText) ?? [])) ?? [];
            if (Resolve(parameterType, id.SyntaxTree.FilePath) is not null || typeParameters.Contains(Simple(parameterType)) || ElementType(parameterType) is { } el && Resolve(el, id.SyntaxTree.FilePath) is not null) return [];
            var name = method.Identifier.ValueText;
            if (methods.Count(m => m.Method.Identifier.ValueText == name) != 1) return [];
            var found = new List<(ExpressionSyntax, IqrSourceAnalyzer.Code)>();
            foreach (var c2 in production)
            foreach (var call in c2.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => MethodName(i) == name))
            {
                var args = call.ArgumentList.Arguments;
                var arg = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == id.Identifier.ValueText) ?? (index < args.Count && args[index].NameColon is null ? args[index] : null);
                if (arg is not null) found.Add((arg.Expression, c2));
                if (found.Count >= 10) return found;
            }
            return found;
        }

        // A source reference is "Type.Property" for a catalog member, "cdc:Field" for a JSON read.
        string? DirectRef(ExpressionSyntax expression, IqrSourceAnalyzer.Code c)
        {
            expression = Unwrap(expression);
            if (expression is InvocationExpressionSyntax inv && IsJsonRead(inv, c.Path, out var field)) return "cdc:" + field;
            if (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToString" } ts } && ts.Expression is MemberAccessExpressionSyntax inner)
                return MemberRef(inner, c);
            return expression is MemberAccessExpressionSyntax m ? MemberRef(m, c) : null;
        }
        string? MemberRef(MemberAccessExpressionSyntax m, IqrSourceAnalyzer.Code c)
        {
            var owner = TypeOfExpression(m.Expression, c.Path);
            var info = Resolve(owner, c.Path);
            return info is not null && info.HasProperty(m.Name.Identifier.ValueText) ? $"{info.Key}.{m.Name.Identifier.ValueText}" : null;
        }
        List<string> RefsOf(ExpressionSyntax expression, IqrSourceAnalyzer.Code c, int depth)
        {
            var refs = new List<string>();
            foreach (var node in expression.DescendantNodesAndSelf())
            {
                if (node is InvocationExpressionSyntax inv)
                {
                    if (IsJsonRead(inv, c.Path, out var field)) refs.Add("cdc:" + field);
                    else if (depth < 2 && MethodName(inv) is { } name && jsonReadMethods.Contains(name) && inv.Expression is IdentifierNameSyntax)
                    {
                        // A same-class helper that reads JSON fields itself (e.g. a derived value): its reads are this value's sources.
                        var helper = c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(md => md.Identifier.ValueText == name);
                        if (helper is not null) foreach (var i in helper.DescendantNodes().OfType<InvocationExpressionSyntax>()) if (IsJsonRead(i, c.Path, out var f2)) refs.Add("cdc:" + f2);
                    }
                }
                else if (node is MemberAccessExpressionSyntax m && MemberRef(m, c) is { } r) refs.Add(r);
                else if (node is IdentifierNameSyntax local && depth < 3 && !(node.Parent is MemberAccessExpressionSyntax pm && pm.Name == node) && LocalInitializer(local) is { } init)
                    refs.AddRange(RefsOf(init, c, depth + 1));
            }
            return refs.Distinct().ToList();
        }

        void AddProjection(string targetType, string property, ExpressionSyntax value, IqrSourceAnalyzer.Code c, SyntaxNode at)
        {
            var target = Resolve(targetType, c.Path);
            if (target is null) return;
            targetTypes.Add(target.Key);
            // A dictionary initializer ["key"] = expr inside the property is projected per key (e.g. audit "after state").
            if (Unwrap(value) is ObjectCreationExpressionSyntax { Initializer: { } dict } && dict.Expressions.OfType<AssignmentExpressionSyntax>().Any(a => a.Left is ImplicitElementAccessSyntax))
            {
                foreach (var entry in dict.Expressions.OfType<AssignmentExpressionSyntax>())
                    if (entry.Left is ImplicitElementAccessSyntax key && key.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax lit)
                        AddEdges($"{target.Key}.{property}.{lit.Token.ValueText}", entry.Right, c, entry, lit.Token.ValueText);
                return;
            }
            AddEdges($"{target.Key}.{property}", value, c, at, property);
            if (Unwrap(value) is InvocationExpressionSyntax { } ser && MethodName(ser) == "Serialize" && ser.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } arg
                && Resolve(TypeOfExpression(arg, c.Path), c.Path) is { } copied && !IsEnvelope(copied))
            {
                serializedCopies.Add((target.Key, property, copied.Key, At(c, at)));
                foreach (var p in copied.Properties.Keys)
                    edges.Add(new Edge($"{copied.Key}.{p}", $"{target.Key}.{property}", FieldTransformation.Derived, $"Serialized copy of {Safe(copied.Name)} (raw value retained)", At(c, at)));
            }
        }
        void AddEdges(string to, ExpressionSyntax value, IqrSourceAnalyzer.Code c, SyntaxNode at, string targetField)
        {
            var (refs, kind, detail) = Classify(value, c, targetField);
            foreach (var r in refs)
            {
                var from = r;
                if (r.StartsWith("cdc:", StringComparison.Ordinal)) { cdcTargets.Add(to.Split('.')[0]); from = $"cdc:{to.Split('.')[0]}|{r[4..]}"; }
                edges.Add(new Edge(from, to, kind, detail, At(c, at)));
            }
            if (refs.Count == 0) edges.Add(new Edge("", to, kind, detail, At(c, at)));
        }

        foreach (var c in production)
        foreach (var node in c.Root.DescendantNodes())
        {
            switch (node)
            {
                case ObjectCreationExpressionSyntax o:
                    var ot = o.Type.ToString();
                    if (o.Initializer is not null)
                        foreach (var a in o.Initializer.Expressions.OfType<AssignmentExpressionSyntax>()) if (a.Left is IdentifierNameSyntax p) AddProjection(ot, p.Identifier.ValueText, a.Right, c, a);
                    if (o.ArgumentList is not null) AddArguments(ot, o.ArgumentList, c);
                    break;
                case ImplicitObjectCreationExpressionSyntax io when ImplicitTarget(io) is { } it:
                    if (io.Initializer is not null)
                        foreach (var a in io.Initializer.Expressions.OfType<AssignmentExpressionSyntax>()) if (a.Left is IdentifierNameSyntax p) AddProjection(it, p.Identifier.ValueText, a.Right, c, a);
                    AddArguments(it, io.ArgumentList, c);
                    break;
                case AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax left } assign when assign.Parent is ExpressionStatementSyntax
                    && TypeOfExpression(left.Expression, c.Path) is { } owner && Resolve(owner, c.Path) is { } ownerInfo && ownerInfo.HasProperty(left.Name.Identifier.ValueText):
                    AddProjection(owner, left.Name.Identifier.ValueText, assign.Right, c, assign);
                    break;
            }
        }
        void AddArguments(string type, ArgumentListSyntax args, IqrSourceAnalyzer.Code c)
        {
            var info = Resolve(type, c.Path);
            if (RecordParameters(info?.Syntax) is not { } parameters) return;
            for (var i = 0; i < args.Arguments.Count; i++)
            {
                var arg = args.Arguments[i];
                var name = arg.NameColon?.Name.Identifier.ValueText ?? (i < parameters.Parameters.Count ? parameters.Parameters[i].Identifier.ValueText : null);
                if (name is not null) AddProjection(type, name, arg.Expression, c, arg);
            }
        }
        string? ImplicitTarget(ImplicitObjectCreationExpressionSyntax io)
        {
            foreach (var a in io.Ancestors())
            {
                if (a is ArrowExpressionClauseSyntax { Parent: MethodDeclarationSyntax m }) return UnwrapTask(m.ReturnType.ToString());
                if (a is ReturnStatementSyntax) return a.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.ReturnType.ToString() is { } r ? UnwrapTask(r) : null;
                if (a is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type.IsVar: false } d } }) return d.Type.ToString();
                if (a is ArgumentSyntax or StatementSyntax) return null;
            }
            return null;
        }

        // ── Persistence, events, envelope, outbox, dispatcher, HTTP boundaries ─────────────────────────────────────────
        var dbSets = production.SelectMany(c => c.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Where(p => p.Type is GenericNameSyntax { Identifier.ValueText: "DbSet" })
                .Select(p => (Code: c, Property: p.Identifier.ValueText, Type: Resolve(((GenericNameSyntax)p.Type).TypeArgumentList.Arguments[0].ToString(), c.Path)?.Key)))
            .Where(d => d.Type is not null).Select(d => (d.Code, d.Property, Type: d.Type!)).ToList();
        TypeInfo? envelope = null;
        bool IsEnvelope(TypeInfo t) => t.Syntax.TypeParameterList is { Parameters.Count: 1 } tp && t.Properties.Values.Any(v => v == tp.Parameters[0].Identifier.ValueText);

        var publications = new List<Publication>();
        string? priorityDefault = null;
        foreach (var (c, m) in methods)
        {
            var priority = m.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.ValueText.Equals("priority", StringComparison.OrdinalIgnoreCase))?.Default?.Value as LiteralExpressionSyntax;
            if (priority is not null) priorityDefault ??= priority.Token.ValueText;
        }
        foreach (var c in production)
        foreach (var inv in c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            ExpressionSyntax? Named(params string[] names) => inv.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon is { } n && names.Contains(n.Name.Identifier.ValueText, StringComparer.OrdinalIgnoreCase))?.Expression;
            var topic = Named("topicName", "topic", "queueName", "entityPath") as LiteralExpressionSyntax;
            if (topic is null || !topic.IsKind(SyntaxKind.StringLiteralExpression)) continue;
            var subject = (Named("subject", "messageType", "eventType") as LiteralExpressionSyntax)?.Token.ValueText ?? "Not resolved";
            var session = Named("sessionId", "partitionKey") is { } s ? DescribeMember(s, c) : "Not set";
            var priority = Named("priority") is LiteralExpressionSyntax pl ? Safe(pl.Token.ValueText) : priorityDefault is null ? "Not resolved" : $"{Safe(priorityDefault)} (publisher default)";
            string? eventType = Named("data", "message", "payload") is { } data
                ? Unwrap(data) is InvocationExpressionSyntax di ? ReturnTypeOf(MethodName(di) ?? "", c.Path) is { } rt ? Simple(rt) : null
                  : TypeOfExpression(data, c.Path) is { } dt ? Simple(dt) : null
                : null;
            var method = inv.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "?";
            var eventKey = eventType is null ? null : Resolve(eventType, c.Path)?.Key;
            publications.Add(new Publication(Safe(topic.Token.ValueText), Safe(subject), session, priority, eventKey, Safe(method), At(c, inv), c.Path));
        }
        var eventTypes = publications.Select(p => p.EventType).OfType<string>().Distinct().ToList();

        // Outbox: a persisted entity whose property is assigned the serialized envelope.
        (IqrSourceAnalyzer.Code Code, ObjectCreationExpressionSyntax Creation, string Entity)? outboxCreation = null;
        foreach (var c in production)
        foreach (var o in c.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var entity = Resolve(o.Type.ToString(), c.Path);
            if (entity is null || !dbSets.Any(d => d.Type == entity.Key) || o.Initializer is null) continue;
            var method = o.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
            var wrapped = method?.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Select(x => Resolve(x.Type.ToString(), c.Path)).FirstOrDefault(x => x is not null && IsEnvelope(x));
            if (wrapped is not null && method!.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => MethodName(i) == "Serialize"))
            { outboxCreation = (c, o, entity.Key); envelope = wrapped; break; }
        }
        var outboxType = outboxCreation?.Entity;

        // Dispatcher: a class that reads the outbox set and sends through a Service Bus sender.
        var outboxProperty = dbSets.FirstOrDefault(d => d.Type == outboxType).Property;
        var dispatcher = production.Where(c => outboxCreation is null || Root(c.Path) == Root(outboxCreation.Value.Code.Path))
            .SelectMany(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(cls => (Code: c, Class: cls)))
            .FirstOrDefault(x => (outboxProperty is null || x.Class.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.ValueText == outboxProperty))
                && x.Class.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => MethodName(i) is "CreateSender")
                && x.Class.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => MethodName(i) is "SendMessageAsync" or "SendMessagesAsync"));

        var boundaries = new List<ContractBoundaryEvidence>();
        var boundaryPairs = new List<(string Out, string In, int Index)>();
        foreach (var o in outbound)
        foreach (var e in endpoints.Where(e => e.Verb == o.Verb && e.Path == o.Path))
        {
            var sender = byKey[o.Type];
            var receiver = byKey[e.Type];
            var (matched, mismatches) = Compare(sender, receiver, o.Code.Path, e.Code.Path, 0);
            boundaryPairs.Add((sender.Key, receiver.Key, boundaries.Count));
            foreach (var p in matched) edges.Add(new Edge($"{p.Split(" → ")[0]}", $"{p.Split(" → ")[1]}", FieldTransformation.PassThrough, $"JSON property by name over HTTP {o.Verb} {o.Path}", At(o.Code, o.At)));
            boundaries.Add(new ContractBoundaryEvidence
            {
                Name = $"{Safe(sender.Project)} → {Safe(receiver.Project)}", From = Safe(sender.Name), To = Safe(receiver.Name),
                ImplementationContract = mismatches.Count == 0 ? "Source verified (property names and types match)" : "Partial (see mismatches)",
                Matched = matched.Select(x => string.Join(" → ", x.Split(" → ").Select(DisplayNode))).ToList(), Mismatches = mismatches.Select(StripKeys).ToList(),
                Confidence = SourceConfidence.StrongSourceEvidence,
            });
        }
        (List<string> Matched, List<string> Mismatches) Compare(TypeInfo sender, TypeInfo receiver, string sPath, string rPath, int depth)
        {
            var matched = new List<string>();
            var mismatches = new List<string>();
            foreach (var (prop, type) in sender.Properties)
            {
                if (sender.Ignored.Contains(prop)) continue;
                var rType = receiver.PropertyType(prop);
                if (rType is null) { mismatches.Add($"{sender.Key}.{prop} is sent but {receiver.Key} has no such property (ignored by the receiver)"); continue; }
                var rName = receiver.Properties.Keys.First(k => k.Equals(prop, StringComparison.OrdinalIgnoreCase));
                if (ElementType(type) is { } se && ElementType(rType) is { } re && Resolve(se, sPath) is { } sei && Resolve(re, rPath) is { } rei && depth < 2)
                {
                    var (m2, x2) = Compare(sei, rei, sPath, rPath, depth + 1);
                    matched.AddRange(m2); mismatches.AddRange(x2);
                    continue;
                }
                matched.Add($"{sender.Key}.{prop} → {receiver.Key}.{rName}");
                if (Simple(type).TrimEnd('?') != Simple(rType).TrimEnd('?')) mismatches.Add($"{sender.Name}.{prop} is {type} but {receiver.Name}.{rName} is {rType}");
                else if (type.EndsWith('?') && !rType.EndsWith('?') && IsValueLike(rType)) mismatches.Add($"{sender.Name}.{prop} may be null but {receiver.Name}.{rName} is not nullable");
            }
            foreach (var (prop, type) in receiver.Properties.Where(p => !sender.HasProperty(p.Key)))
            {
                var parameter = RecordParameters(receiver.Syntax)?.Parameters.FirstOrDefault(p => p.Identifier.ValueText == prop);
                var optional = type.EndsWith('?') || parameter?.Default is not null;
                mismatches.Add(optional ? $"{receiver.Name}.{prop} is not sent by {sender.Name} (receiver default applies)" : $"{receiver.Name}.{prop} is required by the receiver but not sent by {sender.Name}");
            }
            return (matched, mismatches);
        }

        // ── Change detection: compared vs assigned-on-update, per (entity, input) pair ────────────────────────────────
        var changes = new List<ChangeDetectionEvidence>();
        var persisted = dbSets.Select(d => d.Type).ToHashSet(StringComparer.Ordinal);
        var comparisons = new Dictionary<(string E, string D), (HashSet<string> Fields, List<SourceLocation> At, HashSet<string> Methods)>();
        var assignments = new Dictionary<(string E, string D), (HashSet<string> Fields, List<SourceLocation> At)>();
        var changeFlags = new Dictionary<string, string>(StringComparer.Ordinal);   // out/bool variable name → compared field
        var nameLists = new HashSet<string>(StringComparer.Ordinal);                // methods returning field-name lists
        foreach (var (c, m) in methods)
        {
            var typed = m.ParameterList.Parameters.Select(p => (Name: p.Identifier.ValueText, Info: Resolve(p.Type?.ToString(), c.Path))).Where(p => p.Info is not null).ToList();
            var entities = typed.Where(p => persisted.Contains(p.Info!.Key)).ToList();
            var inputs = typed.Where(p => !persisted.Contains(p.Info!.Key)).ToList();
            foreach (var e in entities)
            foreach (var d in inputs)
            {
                var key = (e.Info!.Key, d.Info!.Key);
                foreach (var b in m.DescendantNodes().OfType<BinaryExpressionSyntax>().Where(b => b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression)))
                {
                    if (b.Left is not MemberAccessExpressionSyntax l || b.Right is not MemberAccessExpressionSyntax r) continue;
                    string? field = l.Expression.ToString() == e.Name && r.Expression.ToString() == d.Name ? l.Name.Identifier.ValueText
                        : r.Expression.ToString() == e.Name && l.Expression.ToString() == d.Name ? r.Name.Identifier.ValueText : null;
                    if (field is null) continue;
                    if (!comparisons.TryGetValue(key, out var set)) comparisons[key] = set = ([], [], []);
                    set.Fields.Add(field); set.At.Add(At(c, b)); set.Methods.Add(m.Identifier.ValueText);
                    if (b.Parent is AssignmentExpressionSyntax { Left: IdentifierNameSyntax flag }) changeFlags[flag.Identifier.ValueText] = field;
                }
                foreach (var a in m.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    if (a.Left is not MemberAccessExpressionSyntax l || l.Expression.ToString() != e.Name) continue;
                    if (!a.Right.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(x => x.Expression.ToString() == d.Name)) continue;
                    if (!assignments.TryGetValue(key, out var set)) assignments[key] = set = ([], []);
                    set.Fields.Add(l.Name.Identifier.ValueText); set.At.Add(At(c, a));
                }
            }
            if (m.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => MethodName(i) == "Add" && i.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression }
                    && i.Ancestors().OfType<IfStatementSyntax>().Any(f => f.Condition is BinaryExpressionSyntax)))
                nameLists.Add(m.Identifier.ValueText);
        }
        foreach (var (key, compared) in comparisons)
        {
            if (!assignments.TryGetValue(key, out var assigned)) continue;
            var notTracked = assigned.Fields.Except(compared.Fields).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var comparing = new HashSet<string>(compared.Methods, StringComparer.Ordinal);
            for (var round = 0; round < 3; round++)
                foreach (var (_, m) in methods)
                    if (m.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => MethodName(i) is { } n && comparing.Contains(n))) comparing.Add(m.Identifier.ValueText);
            var gate = GateFor(comparing);
            changes.Add(new ChangeDetectionEvidence
            {
                Entity = Safe(Display(key.E)), Input = Safe(Display(key.D)), TrackedFields = compared.Fields.OrderBy(f => f, StringComparer.Ordinal).Select(Safe).ToList(),
                AssignedOnUpdate = assigned.Fields.OrderBy(f => f, StringComparer.Ordinal).Select(Safe).ToList(),
                AssignedNotTracked = notTracked.Where(f => !AuditMetadata.IsMatch(f)).Select(Safe).ToList(),
                AuditMetadataNotTracked = notTracked.Where(f => AuditMetadata.IsMatch(f)).Select(Safe).ToList(),
                Gate = gate, Emits = compared.Methods.Any(nameLists.Contains) ? "Changed field names and boolean change flags; no field values" : "Change-specific events; values per event contract",
                Locations = compared.At.Concat(assigned.At).Take(20).ToList(),
            });
        }
        string GateFor(HashSet<string> comparingMethods)
        {
            foreach (var (_, m) in methods)
            foreach (var branch in m.DescendantNodes().OfType<IfStatementSyntax>())
            {
                var returns = branch.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any();
                if (!returns) continue;
                foreach (var id in branch.Condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
                {
                    var assigned = m.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(v => v.Identifier.ValueText == id.Identifier.ValueText)
                        .Select(v => v.Initializer?.Value).OfType<ExpressionSyntax>()
                        .Concat(m.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => a.Left.ToString() == id.Identifier.ValueText).Select(a => a.Right));
                    if (assigned.SelectMany(x => x.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()).Any(i => MethodName(i) is { } n && comparingMethods.Contains(n)))
                        return "An empty change set returns before the update is applied (source-defined)";
                }
            }
            return "Not resolved";
        }
        // Change flags and field-name lists reach event properties through parameters of the same name (linked by name → Partial).
        var flagEdges = new List<Edge>();
        foreach (var c in production)
        foreach (var a in c.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (a.Right is not IdentifierNameSyntax id || a.Parent is not InitializerExpressionSyntax { Parent: BaseObjectCreationExpressionSyntax creation }) continue;
            var type = creation is ObjectCreationExpressionSyntax oc ? oc.Type.ToString() : creation is ImplicitObjectCreationExpressionSyntax ic ? ImplicitTarget(ic) : null;
            var target = Resolve(type, c.Path);
            if (target is null || a.Left is not IdentifierNameSyntax prop) continue;
            if (changeFlags.TryGetValue(id.Identifier.ValueText, out var flagField))
                foreach (var key in comparisons.Keys.Where(k => comparisons[k].Fields.Contains(flagField)))
                    flagEdges.Add(new Edge($"{key.D}.{flagField}", $"{target.Key}.{prop.Identifier.ValueText}", FieldTransformation.Booleanized, "Change flag (linked by parameter name; value not copied)", At(c, a)));
            if ((target.PropertyType(prop.Identifier.ValueText) ?? "") is var pt && ElementType(pt) is "string" && nameLists.Count > 0)
                foreach (var key in comparisons.Keys.Where(k => comparisons[k].Methods.Any(nameLists.Contains)))
                    foreach (var f in comparisons[key].Fields)
                        flagEdges.Add(new Edge($"{key.D}.{f}", $"{target.Key}.{prop.Identifier.ValueText}", FieldTransformation.MetadataOnly, "Field name only when changed (value not copied)", At(c, a)));
        }
        edges.RemoveAll(e => e.From == "" && flagEdges.Any(f => f.To == e.To));
        edges.AddRange(flagEdges);

        // ── Stages ───────────────────────────────────────────────────────────────────────────────────────────────
        var requestTypes = endpoints.Select(e => e.Type).ToHashSet(StringComparer.Ordinal);
        var outboundTypes = outbound.Select(o => o.Type).Concat(outbound.SelectMany(o => byKey[o.Type].Properties.Values.Select(v => ElementType(v)).OfType<string>()
            .Select(el => Resolve(el, o.Code.Path)?.Key).OfType<string>())).ToHashSet(StringComparer.Ordinal);
        foreach (var e in endpoints) foreach (var el in byKey[e.Type].Properties.Values.Select(v => ElementType(v)).OfType<string>().Select(el => Resolve(el, e.Code.Path)?.Key).OfType<string>()) requestTypes.Add(el);
        SourceStageKind KindOf(string type)
        {
            if (type == outboxType) return SourceStageKind.Outbox;
            if (envelope?.Key == type) return SourceStageKind.EventEnvelope;
            if (eventTypes.Contains(type)) return SourceStageKind.DomainEvent;
            if (persisted.Contains(type)) return SourceStageKind.DomainEntity;
            if (requestTypes.Contains(type)) return SourceStageKind.IngestionRequest;
            if (cdcTargets.Contains(type) || outboundTypes.Contains(type)) return SourceStageKind.AdapterModel;
            if (edges.Any(x => x.To.StartsWith(type + ".", StringComparison.Ordinal) && requestTypes.Contains(x.From.Split('.')[0]))) return SourceStageKind.IngestionDto;
            return SourceStageKind.Other;
        }
        string NodeType(string node) => node.StartsWith("cdc:", StringComparison.Ordinal) ? "CDC payload" : node.StartsWith("sb:", StringComparison.Ordinal) ? node[3..^5] : node.Split('.')[0];
        SourceStageKind NodeKind(string node) => node.StartsWith("cdc:", StringComparison.Ordinal) ? SourceStageKind.CdcField : node.StartsWith("sb:", StringComparison.Ordinal) ? SourceStageKind.ServiceBus : KindOf(NodeType(node));

        // Implicit edges: event → envelope Data → outbox payload → Service Bus body for each published event type.
        if (envelope is not null && outboxType is not null)
            foreach (var pub in publications.Where(p => p.EventType is not null))
            {
                var ev = byKey.GetValueOrDefault(pub.EventType!);
                if (ev is null) continue;
                var data = envelope.Properties.First(p => p.Value == envelope.Syntax.TypeParameterList!.Parameters[0].Identifier.ValueText).Key;
                foreach (var p in ev.Properties.Keys)
                    edges.Add(new Edge($"{ev.Key}.{p}", $"{envelope.Key}.{data}", FieldTransformation.PassThrough, "Wrapped as envelope data", pub.Location));
                foreach (var nested in edges.Where(e => e.To.StartsWith(ev.Key + ".", StringComparison.Ordinal) && e.To.Count(ch => ch == '.') == 2).Select(e => e.To).Distinct().ToList())
                    edges.Add(new Edge(nested, $"{envelope.Key}.{data}", FieldTransformation.PassThrough, "Wrapped as envelope data", pub.Location));
            }
        if (envelope is not null && outboxType is not null)
        {
            var payload = outboxCreation!.Value.Creation.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>()
                .FirstOrDefault(a => a.Right is IdentifierNameSyntax id && LocalInitializer(id) is InvocationExpressionSyntax inv && MethodName(inv) == "Serialize");
            var payloadProp = (payload?.Left as IdentifierNameSyntax)?.Identifier.ValueText;
            if (payloadProp is not null)
            {
                var data = envelope.Properties.First(p => p.Value == envelope.Syntax.TypeParameterList!.Parameters[0].Identifier.ValueText).Key;
                edges.Add(new Edge($"{envelope.Key}.{data}", $"{outboxType}.{payloadProp}", FieldTransformation.PassThrough, "Serialized envelope stored as outbox payload", At(outboxCreation.Value.Code, payload!)));
                foreach (var topic in publications.Select(p => p.Topic).Distinct())
                    if (dispatcher.Class is not null) edges.Add(new Edge($"{outboxType}.{payloadProp}", $"sb:{topic}.body", FieldTransformation.PassThrough, "Outbox payload sent as the Service Bus message body by the dispatcher", At(dispatcher.Code, dispatcher.Class)));
            }
        }

        var stageTypes = edges.SelectMany(e => new[] { e.From, e.To }).Where(n => n.Length > 0 && !n.StartsWith("sb:", StringComparison.Ordinal) && !n.StartsWith("cdc:", StringComparison.Ordinal))
            .Select(NodeType).Where(byKey.ContainsKey).Distinct().ToList();
        var stages = new List<SourceStage>();
        if (edges.Any(e => e.From.StartsWith("cdc:", StringComparison.Ordinal)))
            stages.Add(new SourceStage("cdc", SourceStageKind.CdcField, "CDC payload", "Debezium event", edges.Where(e => e.From.StartsWith("cdc:")).Select(e => Safe(CdcField(e.From))).Distinct().OrderBy(f => f).ToList(),
                edges.First(e => e.From.StartsWith("cdc:")).Location, SourceConfidence.StrongSourceEvidence, "JSON fields read by the adapter mapper"));
        foreach (var t in stageTypes)
        {
            var kind = KindOf(t);
            if (kind == SourceStageKind.Other) continue;
            var info = byKey[t];
            stages.Add(new SourceStage(Safe(t), kind, Safe(info.Name), Safe(info.Project), info.Properties.Keys.Select(Safe).ToList(), At(info.Path, info.Syntax), SourceConfidence.StrongSourceEvidence, StageEvidence(kind)));
        }
        foreach (var topic in publications.Select(p => p.Topic).Distinct())
            stages.Add(new SourceStage($"sb:{topic}", SourceStageKind.ServiceBus, topic, dispatcher.Code?.Project?.Name is { } dp ? Safe(dp) : "Not resolved", ["body"],
                publications.First(p => p.Topic == topic).Location, dispatcher.Class is null ? SourceConfidence.Partial : SourceConfidence.StrongSourceEvidence,
                dispatcher.Class is null ? "Topic named at publish; no dispatcher resolved" : "Topic named at publish; sent by the outbox dispatcher"));
        if (stages.Count(s => s.Kind != SourceStageKind.Other) < 2) return null;
        stages = stages.OrderBy(s => s.Kind).ThenBy(s => s.TypeName, StringComparer.Ordinal).ToList();

        // ── Developer-test facts (reused as evidence; never duplicated) ─────────────────────────────────────────────
        var facts = TestFactsOf(all, tests);
        List<TestFacts> AssertingTests(IEnumerable<string> productionSymbols, IEnumerable<string> assertedAny) =>
            facts.Where(f => productionSymbols.Any(f.Calls.Contains) && assertedAny.Any(f.Asserted.Contains)).ToList();
        static SourceCoverageStatus Covered(TestFacts f) => f.Evidence.Layer switch
        {
            DeveloperTestLayer.Contract => SourceCoverageStatus.DeveloperContractCovered,
            DeveloperTestLayer.Integration or DeveloperTestLayer.Component => SourceCoverageStatus.DeveloperIntegrationCovered,
            _ => SourceCoverageStatus.DeveloperUnitCovered,
        };
        List<string> Symbols(string typeName) => methods.Where(m => m.Method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText == typeName)
            .Select(m => Safe(typeName + "." + m.Method.Identifier.ValueText)).ToList();
        string OwnerOf(SourceLocation at) => production.FirstOrDefault(c => Safe(c.Path) == at.File)?.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => { var span = t.GetLocation().GetLineSpan(); return at.Line >= span.StartLinePosition.Line + 1 && at.Line <= span.EndLinePosition.Line + 1; })?.Identifier.ValueText ?? "";
        // The production entry points that reach a publish call: the class that publishes, and the classes that call it.
        var publishingOwners = publications.Select(p => OwnerOf(p.Location)).Where(o => o.Length > 0).ToHashSet(StringComparer.Ordinal);
        var entrySymbols = publishingOwners.SelectMany(Symbols).ToHashSet(StringComparer.Ordinal);

        // ── Events ───────────────────────────────────────────────────────────────────────────────────────────────
        var events = new List<EventContractEvidence>();
        foreach (var e in eventTypes)
        {
            var info = byKey[e];
            var pubs = publications.Where(p => p.EventType == e).ToList();
            var fields = info.Properties.Select(p =>
            {
                var incoming = edges.Where(x => x.To == $"{info.Key}.{p.Key}" || x.To.StartsWith($"{info.Key}.{p.Key}.", StringComparison.Ordinal)).ToList();
                var kind = incoming.Count == 0 ? FieldTransformation.NotResolved : incoming.Select(x => x.Transformation).GroupBy(k => k).OrderByDescending(g => g.Count()).First().Key;
                var src = incoming.Where(x => x.From.Length > 0).Select(x => Safe(DisplayNode(x.From))).Distinct().Take(6).ToList();
                return new EventFieldEvidence(Safe(p.Key), Safe(p.Value), src.Count == 0 ? incoming.Count == 0 ? "Not resolved" : incoming[0].Detail : string.Join(", ", src), kind, IsSensitive(p.Key));
            }).ToList();
            var shapeTests = facts.Where(f => f.Constructed.Contains(info.Name) && f.Asserted.Count > 0).ToList();
            events.Add(new EventContractEvidence
            {
                EventType = Safe(info.Name), Project = Safe(info.Project), Fields = fields, Topics = pubs.Select(p => p.Topic).Distinct().ToList(), Subjects = pubs.Select(p => p.Subject).Distinct().ToList(),
                SessionId = string.Join("; ", pubs.Select(p => p.SessionId).Distinct()), Priority = string.Join("; ", pubs.Select(p => p.Priority).Distinct()),
                CreatedIn = pubs.Select(p => p.Method).Distinct().ToList(), Location = At(info.Path, info.Syntax),
                DeveloperTestIds = shapeTests.Select(t => t.Evidence.Id).Distinct().ToList(),
                Coverage = [.. shapeTests.Select(Covered).Distinct(), .. shapeTests.Count == 0 ? new[] { SourceCoverageStatus.SourceEvidenceOnly } : [], SourceCoverageStatus.RuntimeGap],
            });
        }

        // ── Field traces ─────────────────────────────────────────────────────────────────────────────────────────
        var origins = edges.Where(e => e.From.StartsWith("cdc:", StringComparison.Ordinal)).Select(e => e.From).Distinct().ToList();
        if (origins.Count == 0)
            origins = requestTypes.SelectMany(r => byKey[r].Properties.Keys.Select(p => $"{r}.{p}")).Where(n => edges.Any(e => e.From == n)).Distinct().ToList();
        var outgoing = edges.Where(e => e.From.Length > 0).GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.ToList());
        var mapperTests = new Dictionary<string, List<TestFacts>>();
        var traces = new List<FieldTrace>();
        foreach (var origin in origins.OrderBy(o => o, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var reached = new Dictionary<string, (Edge Via, bool Reduced)>();
            var queue = new Queue<(string Node, bool Reduced)>();
            queue.Enqueue((origin, false));
            while (queue.Count > 0 && reached.Count < 200)
            {
                var (node, reduced) = queue.Dequeue();
                if (!outgoing.TryGetValue(node, out var next)) continue;
                foreach (var e in next)
                {
                    if (reached.ContainsKey(e.To) || e.To == origin) continue;
                    var r = reduced || e.Transformation is FieldTransformation.Booleanized or FieldTransformation.MetadataOnly;
                    reached[e.To] = (e, r);
                    queue.Enqueue((e.To, r));
                }
            }
            var originField = origin.StartsWith("cdc:", StringComparison.Ordinal) ? CdcField(origin) : origin.Split('.').Last();
            var steps = reached.OrderBy(r => NodeKind(r.Key)).ThenBy(r => r.Key, StringComparer.Ordinal).Take(MaxSteps).Select(r =>
            {
                var kind = NodeKind(r.Key);
                var field = r.Key.StartsWith("sb:", StringComparison.Ordinal) ? "body" : string.Join('.', r.Key.Split('.').Skip(1));
                var stepTests = kind == SourceStageKind.AdapterModel || kind == SourceStageKind.DomainEntity || kind == SourceStageKind.DomainEvent
                    ? AssertingTests(kind == SourceStageKind.AdapterModel ? Symbols(OwnerOf(r.Value.Via.Location)) : entrySymbols, [field.Split('.').Last()]).Select(t => t.Evidence.Id).Distinct().ToList() : [];
                var transformation = r.Value.Reduced && r.Value.Via.Transformation is FieldTransformation.PassThrough ? FieldTransformation.Reduced : r.Value.Via.Transformation;
                return new FieldTraceStep(kind, Safe(Display(NodeType(r.Key))), Safe(field), transformation, r.Value.Via.Detail, r.Value.Via.Location, stepTests.Count == 0 ? null : stepTests);
            }).ToList();
            var boundaryGap = "";
            foreach (var node in reached.Keys.Where(k => NodeKind(k) == SourceStageKind.AdapterModel && outboundTypes.Contains(NodeType(k))).ToList())
            {
                var prop = node.Split('.').Last();
                var model = byKey[NodeType(node)];
                var receivers = boundaryPairs.Where(b => b.Out == model.Key).Select(b => byKey[b.In]).ToList();
                if (receivers.Count == 0) continue;
                if (model.Ignored.Contains(prop))
                    steps.Add(new FieldTraceStep(SourceStageKind.IngestionRequest, Safe(string.Join(", ", receivers.Select(r => r.Name))), Safe(prop), FieldTransformation.FilteredIntentionally,
                        "Excluded from the HTTP payload by [JsonIgnore] on the sender (intentional)", reached[node].Via.Location));
                else if (receivers.All(r => !r.HasProperty(prop)))
                {
                    steps.Add(new FieldTraceStep(SourceStageKind.IngestionRequest, Safe(string.Join(", ", receivers.Select(r => r.Name))), Safe(prop), FieldTransformation.Dropped,
                        "Sent by the adapter, but the receiving contract has no such property (reason not resolved)", reached[node].Via.Location));
                    boundaryGap = $"Not resolved: {Safe(model.Name)}.{Safe(prop)} is dropped at the adapter → ingestion boundary";
                }
            }
            var originModel = origin.StartsWith("cdc:", StringComparison.Ordinal) && origin.Contains('|') ? origin[4..origin.IndexOf('|')] : NodeType(origin);
            var siblingFields = edges.Where(e => e.From.StartsWith($"cdc:{originModel}|", StringComparison.Ordinal)).Select(e => CdcField(e.From))
                .Concat(byKey.TryGetValue(originModel, out var om) ? om.Properties.Keys : []);
            var sensitive = IsSensitiveOn(originField, siblingFields);
            var eventSteps = reached.Where(r => NodeKind(r.Key) == SourceStageKind.DomainEvent).ToList();
            var emittedRaw = eventSteps.Where(r => !r.Value.Reduced && r.Value.Via.Transformation is FieldTransformation.PassThrough or FieldTransformation.Renamed or FieldTransformation.Converted or FieldTransformation.Derived or FieldTransformation.Conditional).ToList();
            var reducedOut = eventSteps.Where(r => r.Value.Reduced).ToList();
            var stored = reached.Where(r => NodeKind(r.Key) == SourceStageKind.DomainEntity).ToList();
            var boundary = reached.Any(r => NodeKind(r.Key) == SourceStageKind.IngestionRequest);
            var adapterOnly = reached.Keys.All(k => NodeKind(k) is SourceStageKind.AdapterModel or SourceStageKind.Other);
            var minimization = !sensitive ? "Not sensitive"
                : emittedRaw.Count > 0 ? "Potential data-minimization issue"
                : reducedOut.Count > 0 ? "Intentional reduction / metadata projection"
                : stored.Count > 0 ? "Retained internally; not emitted"
                : steps.Any(st => st.Transformation == FieldTransformation.FilteredIntentionally) ? "Filtered intentionally before ingestion"
                : "Not resolved";
            var outcome = new List<string>();
            if (eventSteps.Count > 0) outcome.Add("Events: " + string.Join(", ", eventSteps.Take(6).Select(r => $"{Safe(DisplayNode(r.Key))} ({(r.Value.Reduced ? "reduced" : r.Value.Via.Transformation.ToString())})")));
            if (stored.Count > 0) outcome.Add("Stored: " + string.Join(", ", stored.Take(6).Select(r => Safe(DisplayNode(r.Key)))));
            if (steps.Any(st => st.Transformation == FieldTransformation.Dropped)) outcome.Add("Dropped at the adapter → ingestion boundary (receiver has no such property)");
            if (steps.Any(st => st.Transformation == FieldTransformation.FilteredIntentionally)) outcome.Add("Filtered intentionally before the HTTP boundary");
            if (reached.Count == 0) outcome.Add("Read, but not assigned to any traced model");
            else if (adapterOnly && outbound.Count > 0) outcome.Add("No outbound HTTP contract resolved for this model (it may be delivered through a path the analyzer does not resolve)");
            var stepTestIds = steps.SelectMany(s => s.DeveloperTestIds ?? []).Distinct().ToList();
            var coverage = new List<SourceCoverageStatus>();
            coverage.AddRange(facts.Where(f => stepTestIds.Contains(f.Evidence.Id)).Select(Covered).Distinct());
            if (boundary && boundaries.All(b => b.DeveloperTestIds.Count == 0)) coverage.Add(SourceCoverageStatus.SourceEvidenceOnly);
            if (stepTestIds.Count == 0) coverage.Add(SourceCoverageStatus.SourceEvidenceOnly);
            coverage.Add(SourceCoverageStatus.RuntimeGap); coverage.Add(SourceCoverageStatus.E2EGap);
            traces.Add(new FieldTrace
            {
                Key = Safe(DisplayNode(origin)), OriginField = Safe(originField), OriginStage = NodeKind(origin), Steps = steps, Sensitive = sensitive, Minimization = minimization,
                Outcome = outcome.Count == 0 ? "Traced within the inspected stages" : string.Join("; ", outcome),
                DeveloperTestIds = stepTestIds, Coverage = coverage.Distinct().ToList(),
                Confidence = steps.Any(s => s.Detail.Contains("linked by parameter name")) || steps.Count == 0 ? SourceConfidence.Partial : SourceConfidence.StrongSourceEvidence,
                Gap = minimization == "Potential data-minimization issue" ? "Raw sensitive value appears in an emitted event field" : reached.Count == 0 ? "Not resolved: read but not assigned" : boundaryGap,
            });
        }

        // ── Hops (message flow, each with its own evidence) ────────────────────────────────────────────────────────
        var hops = new List<SourceHop>();
        var adapterRoots = outbound.Select(o => Root(o.Code.Path)).ToHashSet(StringComparer.Ordinal);
        var processor = production.Where(c => adapterRoots.Count == 0 || adapterRoots.Contains(Root(c.Path))).FirstOrDefault(c => c.Root.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.ValueText is "EventProcessorClient" or "EventHubConsumerClient"));
        var adapterStage = stages.FirstOrDefault(s => s.Kind == SourceStageKind.AdapterModel && outbound.Any(o => Safe(o.Type) == s.Id)) ?? stages.FirstOrDefault(s => s.Kind == SourceStageKind.AdapterModel);
        if (processor is not null && adapterStage is not null)
            hops.Add(new SourceHop { Id = "eventhub-adapter", From = "Event Hub", To = string.Join(", ", stages.Where(st => st.Kind == SourceStageKind.AdapterModel && cdcTargets.Contains(st.Id)).Select(st => st.TypeName).DefaultIfEmpty(adapterStage.TypeName)), Mechanism = $"Event Hub processor in {Safe(processor.Project?.Name ?? "Unknown")}",
                Confidence = SourceConfidence.StrongSourceEvidence, Locations = [new(Safe(processor.Path), 1)], SourceState = "Source route known", RuntimeState = "See Event Hub runtime evidence",
                Coverage = [SourceCoverageStatus.RuntimeGap], Note = "The consumer's route is source-defined; which events it processed is runtime evidence." });
        foreach (var (o, i, index) in boundaryPairs)
        {
            var boundary = boundaries[index];
            var (on, iname) = (Display(o), Display(i));
            var builders = edges.Where(e => e.To.StartsWith(o + ".", StringComparison.Ordinal) && e.From.StartsWith("cdc:", StringComparison.Ordinal)).Select(e => OwnerOf(e.Location)).Where(x => x.Length > 0).Distinct().SelectMany(Symbols).ToHashSet(StringComparer.Ordinal);
            var sideTests = facts.Where(f => f.Constructed.Contains(iname) || f.Referenced.Contains(iname) || f.Constructed.Contains(on) || f.Asserted.Contains(on) || builders.Any(f.Calls.Contains))
                .Select(f => f.Evidence).DistinctBy(t => t.Id).ToList();
            var both = facts.Where(f => (f.Constructed.Contains(on) || f.Referenced.Contains(on)) && (f.Constructed.Contains(iname) || f.Referenced.Contains(iname))).Select(f => f.Evidence.Id).ToList();
            boundaries[index] = boundary = boundary with
            {
                DeveloperTestIds = both, DeveloperContractTests = both.Count > 0 ? "Present (sender and receiver types in one test)"
                    : sideTests.Count > 0 ? $"One-sided only: {sideTests.Count} test(s) cover the sender or the receiver; none spans the boundary" : "Absent",
            };
            var ep = endpoints.First(e => e.Type == i && outbound.Any(x => x.Type == o && x.Path == e.Path && x.Verb == e.Verb));
            hops.Add(new SourceHop { Id = $"http-{Safe(on)}-{Safe(iname)}-{index}", From = Safe(on), To = Safe(iname), Mechanism = $"HTTP {ep.Verb} {ep.Path} (JSON by property name)", Confidence = SourceConfidence.StrongSourceEvidence,
                Locations = [At(ep.Code, ep.At), .. outbound.Where(x => x.Type == o && x.Path == ep.Path).Select(x => At(x.Code, x.At))], SourceState = boundary.Mismatches.Count == 0 ? "Source route verified" : "Source route verified with contract differences",
                DeveloperTestIds = both, Coverage = [.. both.Count > 0 ? new[] { SourceCoverageStatus.DeveloperContractCovered } : [SourceCoverageStatus.SourceEvidenceOnly], SourceCoverageStatus.RuntimeGap],
                Note = both.Count > 0 ? "" : "Sender and receiver are tested separately; no developer test exercises both sides of this contract." });
        }
        var receiverRoots = endpoints.Select(e => Root(e.Code.Path)).ToHashSet(StringComparer.Ordinal);
        var ingestion = stages.Where(s => s.Kind == SourceStageKind.DomainEntity && (receiverRoots.Count == 0 || byKey.TryGetValue(s.Id, out var ti) && receiverRoots.Contains(ti.Root))).ToList();
        var persistTests = facts.Where(f => entrySymbols.Any(f.Calls.Contains) && dbSets.Any(d => f.Asserted.Contains(d.Property) || f.Referenced.Contains(d.Property)) && f.Evidence.Layer is DeveloperTestLayer.Integration).ToList();
        if (ingestion.Count > 0 && requestTypes.Count > 0)
            hops.Add(new SourceHop { Id = "ingestion-domain", From = string.Join(", ", requestTypes.Select(r => Safe(Display(r)))), To = string.Join(", ", ingestion.Select(s => s.TypeName)),
                Mechanism = "Request → DTO → entity projections (mapper/initializers) and repository add/update", Confidence = SourceConfidence.StrongSourceEvidence,
                Locations = ingestion.Select(s => s.Location).ToList(), DeveloperTestIds = persistTests.Select(t => t.Evidence.Id).ToList(),
                Coverage = [.. persistTests.Select(Covered).Distinct(), .. persistTests.Count == 0 ? new[] { SourceCoverageStatus.SourceEvidenceOnly } : [], SourceCoverageStatus.RuntimeGap] });
        var eventTests = facts.Where(f => entrySymbols.Any(f.Calls.Contains) && (outboxType is not null && (f.Asserted.Contains(outboxType) || dbSets.Any(d => d.Type == outboxType && (f.Asserted.Contains(d.Property) || f.Referenced.Contains(d.Property)))))).ToList();
        if (events.Count > 0)
            hops.Add(new SourceHop { Id = "domain-event", From = string.Join(", ", ingestion.Select(s => s.TypeName)), To = string.Join(", ", events.Select(e => e.EventType)),
                Mechanism = $"Publish calls in {string.Join(", ", publishingOwners.Select(Safe))} (topic, subject, session id, priority at the call site)", Confidence = SourceConfidence.StrongSourceEvidence,
                Locations = publications.Select(p => p.Location).Take(20).ToList(), DeveloperTestIds = eventTests.Select(t => t.Evidence.Id).ToList(),
                Coverage = [.. eventTests.Select(Covered).Distinct(), .. eventTests.Count == 0 ? new[] { SourceCoverageStatus.SourceEvidenceOnly } : [], SourceCoverageStatus.RuntimeGap] });

        // ── Outbox + dispatcher + Service Bus topology ─────────────────────────────────────────────────────────────
        OutboxEvidence? outbox = null;
        ServiceBusSourceTopology? serviceBus = null;
        if (outboxCreation is { } oc2)
        {
            var init = oc2.Creation.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>().Where(a => a.Left is IdentifierNameSyntax).ToList();
            string From(AssignmentExpressionSyntax a) => Unwrap(a.Right) switch
            {
                InvocationExpressionSyntax inv when MethodName(inv) == "NewGuid" => "generated per row",
                IdentifierNameSyntax id when LocalInitializer(id) is InvocationExpressionSyntax s && MethodName(s) == "Serialize" => "serialized envelope",
                IdentifierNameSyntax id => $"publish argument {Safe(id.Identifier.ValueText)}",
                LiteralExpressionSyntax l => $"constant {Safe(l.Token.ValueText)}",
                _ => "expression",
            };
            var method = oc2.Creation.Ancestors().OfType<MethodDeclarationSyntax>().First();
            var cls = method.Ancestors().OfType<ClassDeclarationSyntax>().First();
            var adds = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => MethodName(i) == "Add");
            var saves = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => MethodName(i) is "SaveChanges" or "SaveChangesAsync");
            var camel = cls.ToString().Contains("JsonNamingPolicy.CamelCase", StringComparison.Ordinal);
            var messageId = init.FirstOrDefault(a => ((IdentifierNameSyntax)a.Left).Identifier.ValueText.Contains("Id", StringComparison.Ordinal) && Unwrap(a.Right) is InvocationExpressionSyntax inv && MethodName(inv) == "NewGuid");
            var outboxTests = facts.Where(f => dbSets.Any(d => d.Type == oc2.Entity && (f.Asserted.Contains(d.Property) || f.Referenced.Contains(d.Property))) && entrySymbols.Any(f.Calls.Contains)).ToList();
            var dispatcherInfo = "Not resolved";
            var retry = "Not resolved";
            var ordering = "Not resolved";
            var mapping = "Not resolved";
            if (dispatcher.Class is { } d)
            {
                var text = new List<string>();
                var take = d.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => MethodName(i) == "Take")?.ArgumentList.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax;
                var order = d.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => MethodName(i) is "OrderBy" or "OrderByDescending");
                var status = d.DescendantNodes().OfType<BinaryExpressionSyntax>().Where(b => b.IsKind(SyntaxKind.EqualsExpression)).Select(b => b.Right).OfType<LiteralExpressionSyntax>().FirstOrDefault(l => l.IsKind(SyntaxKind.StringLiteralExpression));
                dispatcherInfo = $"{Safe(d.Identifier.ValueText)} polls {Safe(Display(oc2.Entity))}{(status is null ? "" : $" where status is {Safe(status.Token.ValueText)}")} and sends each row through a Service Bus sender";
                ordering = $"{(order is null ? "Order not resolved" : $"{Safe(MethodName(order)!)} {Safe(order.ArgumentList.Arguments.FirstOrDefault()?.ToString().Split("=>").Last().Trim().Split('.').Last() ?? "")}")}{(take is null ? "" : $", batch of {Safe(take.Token.ValueText)}")}; no cross-replica claim/lock resolved";
                var attempts = d.DescendantNodes().OfType<BinaryExpressionSyntax>().FirstOrDefault(b => b.IsKind(SyntaxKind.GreaterThanOrEqualExpression) && b.Right is LiteralExpressionSyntax);
                retry = attempts is null ? "Failure handling not resolved" : $"Attempts counted on failure; permanently failed after {Safe(((LiteralExpressionSyntax)attempts.Right).Token.ValueText)} attempts (source-defined, not observed)";
                var message = d.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault(o => Simple(o.Type.ToString()) == "ServiceBusMessage");
                if (message is not null)
                {
                    if (message.ArgumentList?.Arguments.FirstOrDefault()?.Expression is MemberAccessExpressionSyntax body) text.Add($"Body ← {Safe(body.Name.Identifier.ValueText)}");
                    foreach (var a in message.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>() ?? [])
                        if (a.Left is IdentifierNameSyntax l) text.Add($"{Safe(l.Identifier.ValueText)} ← {Safe(a.Right.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault()?.Name.Identifier.ValueText ?? "expression")}");
                }
                foreach (var el in d.DescendantNodes().OfType<ElementAccessExpressionSyntax>().Where(e => e.Expression.ToString().EndsWith("ApplicationProperties", StringComparison.Ordinal)))
                    if (el.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax key && el.Parent is AssignmentExpressionSyntax pa)
                        text.Add($"ApplicationProperties[{Safe(key.Token.ValueText)}] ← {Safe(pa.Right.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault()?.Name.Identifier.ValueText ?? "expression")}");
                mapping = text.Count == 0 ? "Not resolved" : string.Join("; ", text);
            }
            var clientCreation = production.SelectMany(c => c.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Select(o => (Code: c, Creation: o))).Where(x => Simple(x.Creation.Type.ToString()) == "ServiceBusClient").ToList();
            var credential = clientCreation.Any(x => x.Creation.ArgumentList?.Arguments.Any(a => a.Expression.ToString().Contains("DefaultAzureCredential", StringComparison.Ordinal) || a.Expression.ToString().Contains("ManagedIdentityCredential", StringComparison.Ordinal)) == true);
            var connection = clientCreation.Any(x => x.Creation.ArgumentList?.Arguments.Count == 1);
            var options = clientCreation.Select(x => x.Creation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()).OfType<ClassDeclarationSyntax>().FirstOrDefault();
            var section = production.SelectMany(c => c.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()).Where(i => MethodName(i) == "GetSection" && options is not null
                && i.Ancestors().OfType<InvocationExpressionSyntax>().Any(o => o.ToString().Contains(options.Identifier.ValueText, StringComparison.Ordinal)))
                .Select(i => (i.ArgumentList.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax)?.Token.ValueText).OfType<string>().FirstOrDefault();
            var keys = options?.Members.OfType<PropertyDeclarationSyntax>().Where(p => p.Type.ToString() is "string" or "int" or "bool" or "string?").Select(p => Safe($"{section ?? options.Identifier.ValueText}:{p.Identifier.ValueText}")).ToList() ?? [];
            outbox = new OutboxEvidence
            {
                EntityType = Safe(Display(oc2.Entity)), Fields = init.Select(a => $"{Safe(((IdentifierNameSyntax)a.Left).Identifier.ValueText)} ({From(a)})").ToList(),
                Envelope = envelope is null ? "Not resolved" : Safe(envelope.Name), EnvelopeFields = envelope?.Properties.Keys.Select(Safe).ToList() ?? [],
                Serialization = camel ? "JSON, camelCase property names" : "JSON (naming policy not resolved)",
                Transaction = adds && !saves ? "Outbox row added to the caller's unit of work; committed with the domain change by the caller's save (source-defined, not observed)" : saves ? "Saved inside the publish method" : "Not resolved",
                MessageId = messageId is null ? "Not resolved" : $"{Safe(((IdentifierNameSyntax)messageId.Left).Identifier.ValueText)} generated per outbox row and reused on every send attempt",
                Dispatcher = dispatcherInfo, Retry = retry, Ordering = ordering,
                Locations = [At(oc2.Code, oc2.Creation), .. dispatcher.Class is null ? [] : new[] { At(dispatcher.Code, dispatcher.Class) }],
                DeveloperTestIds = outboxTests.Select(t => t.Evidence.Id).ToList(), Coverage = [.. outboxTests.Select(Covered).Distinct(), .. outboxTests.Count == 0 ? new[] { SourceCoverageStatus.SourceEvidenceOnly } : [], SourceCoverageStatus.RuntimeGap],
            };
            serviceBus = new ServiceBusSourceTopology
            {
                Publications = publications.GroupBy(p => p.Topic).Select(g => new ServiceBusSourcePublication(g.Key, g.Select(p => p.Subject).Distinct().ToList(),
                    string.Join("; ", g.Select(p => p.SessionId).Distinct()), string.Join("; ", g.Select(p => p.Priority).Distinct()), g.Select(p => p.EventType).OfType<string>().Distinct().ToList(), g.First().Location)).ToList(),
                Client = clientCreation.Count == 0 ? "Not resolved" : "ServiceBusClient",
                Authentication = credential && connection ? "Azure credential (DefaultAzureCredential/managed identity) by namespace FQDN, or a connection string when configured (local/emulator); no value read"
                    : credential ? "Azure credential by namespace FQDN" : connection ? "Connection string (value never read)" : "Not resolved",
                ConfigurationKeys = keys, MessageMapping = mapping,
                Locations = clientCreation.Select(x => At(x.Code, x.Creation)).Take(5).ToList(),
            };
            var dispatcherTests = dispatcher.Class is null ? [] : facts.Where(f => f.Referenced.Contains(dispatcher.Class.Identifier.ValueText)).ToList();
            hops.Add(new SourceHop { Id = "event-outbox", From = string.Join(", ", events.Select(e => e.EventType)), To = Safe(Display(oc2.Entity)), Mechanism = $"{outbox.Envelope} serialized into {outbox.EntityType} in the same unit of work",
                Confidence = SourceConfidence.StrongSourceEvidence, Locations = [At(oc2.Code, oc2.Creation)], DeveloperTestIds = outbox.DeveloperTestIds, Coverage = outbox.Coverage,
                Note = "An outbox row is not a delivered message." });
            hops.Add(new SourceHop { Id = "outbox-servicebus", From = Safe(Display(oc2.Entity)), To = string.Join(", ", serviceBus.Publications.Select(p => p.Entity)),
                Mechanism = dispatcher.Class is null ? "No dispatcher resolved in source" : $"{Safe(dispatcher.Class.Identifier.ValueText)} → ServiceBusSender", Confidence = dispatcher.Class is null ? SourceConfidence.NotResolved : SourceConfidence.StrongSourceEvidence,
                Locations = outbox.Locations, DeveloperTestIds = dispatcherTests.Select(t => t.Evidence.Id).ToList(),
                Coverage = [.. dispatcherTests.Select(Covered).Distinct(), .. dispatcherTests.Count == 0 ? new[] { SourceCoverageStatus.SourceEvidenceOnly } : [], SourceCoverageStatus.RuntimeGap],
                SourceState = dispatcher.Class is null ? "Not resolved" : "Source-defined", Note = "A source-defined publisher is not observed delivery." });
            hops.Add(new SourceHop { Id = "servicebus-subscribers", From = string.Join(", ", serviceBus.Publications.Select(p => p.Entity)), To = "Subscribers", Mechanism = "Not in the inspected publisher source",
                Confidence = SourceConfidence.NotResolved, SourceState = "Not in inspected source", Coverage = [SourceCoverageStatus.E2EGap], Note = "Subscriber processing needs subscriber source, Azure subscription metadata or runtime evidence." });
        }

        // ── Source rules with reused developer evidence (session id, priority, outbox, duplicates, dispatcher) ─────────
        var rules = new List<PathRuleEvidence>();
        void Rule(string kind, string title, string source, IEnumerable<TestFacts> covering, IEnumerable<SourceLocation> at, string? remaining = null)
        {
            var list = covering.DistinctBy(f => f.Evidence.Id).ToList();
            rules.Add(new PathRuleEvidence
            {
                Id = IqrSourceAnalyzer.TestId(kind + "|" + title, rules.Count), Kind = kind, Title = title, Source = source,
                DeveloperTestIds = list.Select(f => f.Evidence.Id).ToList(),
                Coverage = [.. list.Select(Covered).Distinct(), .. list.Count == 0 ? new[] { SourceCoverageStatus.SourceEvidenceOnly } : [], SourceCoverageStatus.RuntimeGap],
                BirkNextAction = list.Count > 0 ? $"Covered by developer {string.Join("/", list.Select(f => f.Evidence.Layer.ToString().ToLowerInvariant()).Distinct())} test — reused as evidence; no duplicate BirkNext test.{(remaining is null ? "" : " " + remaining)}"
                    : $"Source trace only; no matching developer test resolved.{(remaining is null ? "" : " " + remaining)}",
                Locations = at.Take(6).ToList(),
            });
        }
        foreach (var topic in publications.GroupBy(p => p.Topic))
        {
            var sessions = topic.Select(p => p.SessionId).Distinct().ToList();
            Rule("SessionId", $"Session id on {topic.Key}", string.Join("; ", sessions), facts.Where(f => f.Asserted.Contains("SessionId") && entrySymbols.Any(f.Calls.Contains)
                    && (f.Referenced.Contains(topic.Key) || topic.Any(p => f.Referenced.Contains(p.Subject) || p.EventType is { } e && f.Referenced.Contains(Display(e))
                        || p.SessionId.Split('.')[0] is { Length: > 0 } sessionType && (f.Referenced.Contains(sessionType) || f.Constructed.Contains(sessionType))))),
                topic.Select(p => p.Location), "Whether the subscriber enforces sessions is Service Bus configuration, not source.");
        }
        foreach (var pub in publications.Where(p => !p.Priority.Contains("default", StringComparison.OrdinalIgnoreCase) && p.Priority != "Not resolved").GroupBy(p => (p.Subject, p.Priority)))
            Rule("Priority", $"{pub.Key.Subject} published with priority {pub.Key.Priority}", $"{string.Join(", ", pub.Select(p => p.Method).Distinct())} on {string.Join(", ", pub.Select(p => p.Topic).Distinct())}",
                facts.Where(f => f.Asserted.Contains("Priority") && entrySymbols.Any(f.Calls.Contains)), pub.Select(p => p.Location));
        if (outbox is not null)
        {
            Rule("OutboxCreation", $"Domain change writes an {outbox.EntityType} row", outbox.Transaction, facts.Where(f => outbox.DeveloperTestIds.Contains(f.Evidence.Id)), outbox.Locations,
                "An outbox row is not a delivered Service Bus message.");
            Rule("Dispatcher", $"{outbox.EntityType} rows are sent to Service Bus", $"{outbox.Dispatcher}. {outbox.Retry}. {outbox.Ordering}",
                dispatcher.Class is null ? [] : facts.Where(f => f.Referenced.Contains(dispatcher.Class.Identifier.ValueText)), outbox.Locations, "Delivery is runtime evidence.");
        }
        var duplicateEntities = dbSets.Where(d => serializedCopies.Any(x => x.Entity == d.Type)).ToList();
        var entryMethodNames = entrySymbols.Select(x => x.Split('.').Last()).ToHashSet(StringComparer.Ordinal);
        var repeatedTests = facts.Where(f => f.CallCounts.Any(kv => kv.Value >= 2 && entryMethodNames.Contains(kv.Key)) && entrySymbols.Any(f.Calls.Contains) && f.Asserted.Count > 0).ToList();
        if (changes.Count > 0 || repeatedTests.Count > 0)
            Rule("Idempotency", "Repeated ingestion of the same record (re-send, update, conflict)", string.Join("; ", changes.Select(ch => $"{ch.Input} → {ch.Entity}: {ch.Gate}")), repeatedTests,
                changes.SelectMany(ch => ch.Locations.Take(2)), "Matched by tests that call the ingestion entry point more than once and assert; which repetition each test covers is not resolved from syntax.");
        foreach (var d in duplicateEntities)
            Rule("DuplicateHandling", $"Natural-key conflicts are recorded in {Display(d.Type)} instead of applied", $"{Display(d.Type)} persisted via {d.Property}",
                facts.Where(f => (f.Referenced.Contains(d.Property) || f.Referenced.Contains(Display(d.Type)) || f.Asserted.Any(a => a.Contains("Duplikat", StringComparison.OrdinalIgnoreCase) || a.Contains("Duplicate", StringComparison.OrdinalIgnoreCase))) && entrySymbols.Any(f.Calls.Contains)),
                serializedCopies.Where(x => x.Entity == d.Type).Select(x => x.Location));
        foreach (var e in events)
            Rule("EventPrivacy", $"{e.EventType} carries no raw personal field", string.Join(", ", e.Fields.Where(f => f.Transformation is FieldTransformation.Booleanized or FieldTransformation.MetadataOnly).Select(f => $"{f.Name} ({f.Transformation})").DefaultIfEmpty("No reduced fields")),
                facts.Where(f => e.DeveloperTestIds.Contains(f.Evidence.Id)), [e.Location],
                e.DeveloperTestIds.Count > 0 ? "The event type is tested directly; the mapper projection from the ingestion DTO is covered by this source trace." : null);

        // Developer evidence on change detection and duplicates, and the gaps.
        var gaps = new List<PathGap>();
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var ownerSymbols = entrySymbols.Count > 0 ? entrySymbols : [.. methods.Select(m => Safe((m.Method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "") + "." + m.Method.Identifier.ValueText))];
            var entryNames = ownerSymbols.Select(x => x.Split('.').Last()).ToHashSet(StringComparer.Ordinal);
            var repeated = facts.Where(f => f.CallCounts.Any(kv => kv.Value >= 2 && entryNames.Contains(kv.Key)) && ownerSymbols.Any(f.Calls.Contains) && f.Asserted.Count > 0).Select(f => f.Evidence.Id).ToList();
            changes[i] = change with { DeveloperTestIds = repeated };
            foreach (var field in change.AssignedNotTracked)
            {
                // Syntax cannot prove a test changes ONLY this field; update-scenario tests that set it are named as candidates, never as closure.
                var candidates = facts.Where(f => f.NonNullAssigned.Contains(field) && ownerSymbols.Any(f.Calls.Contains) && f.CallCounts.Any(kv => kv.Value >= 2 && entryNames.Contains(kv.Key))).ToList();
                gaps.Add(new PathGap(SourceCoverageStatus.CrossLayerGap, $"{change.Entity}.{field} is assigned on update but not change-tracked",
                    $"{change.Input} → {change.Entity}: {field} is written by the update, but the change check compares only {string.Join(", ", change.TrackedFields)}. {(change.Gate.StartsWith("An empty", StringComparison.Ordinal) ? "Because an empty change set returns before the update, a change to only this field is not applied and emits no event" : "Whether a change to only this field is applied is not resolved")}. "
                    + (candidates.Count == 0 ? "No developer test sets this field in an update scenario. Needs confirmation."
                        : $"Update-scenario developer tests that set this field: {string.Join(", ", candidates.Take(3).Select(f => Safe(f.Evidence.Class + "." + f.Evidence.Method)))} — whether they change only this field is not resolved. Needs confirmation."),
                    change.Gate.StartsWith("An empty", StringComparison.Ordinal) ? SourceConfidence.StrongSourceEvidence : SourceConfidence.Partial, change.Locations.Take(4).ToList()));
            }
        }
        foreach (var b in boundaries)
            foreach (var mismatch in b.Mismatches.Where(x => !x.Contains("receiver default applies", StringComparison.Ordinal)))
                gaps.Add(new PathGap(SourceCoverageStatus.CrossLayerGap, $"Contract difference {b.From} → {b.To}", mismatch, SourceConfidence.StrongSourceEvidence, []));
        foreach (var t in traces.Where(t => t.Minimization == "Potential data-minimization issue"))
            gaps.Add(new PathGap(SourceCoverageStatus.CrossLayerGap, $"Potential data-minimization issue: {t.OriginField}", $"{t.OriginField} reaches an emitted event field without reduction: {t.Outcome}.", t.Confidence, t.Steps.Select(s => s.Location).OfType<SourceLocation>().Take(4).ToList()));
        foreach (var copy in serializedCopies)
        {
            var sensitive = byKey.TryGetValue(copy.Source, out var copied) ? copied.Properties.Keys.Where(IsSensitive).ToList() : [];
            if (sensitive.Count > 0)
                gaps.Add(new PathGap(SourceCoverageStatus.ManualVerification, $"Raw copy retained internally in {Display(copy.Entity)}.{copy.Property}",
                    $"{Display(copy.Entity)}.{copy.Property} stores a serialized {Display(copy.Source)}, which includes {string.Join(", ", sensitive.Select(Safe))}. Internal retention, not an emitted exposure; confirm it is intended and access-controlled.", SourceConfidence.StrongSourceEvidence, [copy.Location]));
        }
        if (hops.Any(h => h.Id == "outbox-servicebus"))
            gaps.Add(new PathGap(SourceCoverageStatus.RuntimeGap, "Outbox → Service Bus delivery", "Outbox rows and the dispatcher are source-defined; delivery to Service Bus is runtime evidence and was not observed here.", SourceConfidence.StrongSourceEvidence, []));
        if (hops.Any(h => h.Id == "servicebus-subscribers"))
            gaps.Add(new PathGap(SourceCoverageStatus.E2EGap, "Subscriber consumption", "No subscriber source was inspected; subscriber processing is not assessed.", SourceConfidence.NotResolved, []));
        if (boundaries.Any(b => b.DeveloperTestIds.Count == 0))
            gaps.Add(new PathGap(SourceCoverageStatus.CrossLayerGap, "No developer test spans the adapter → ingestion contract", "Sender mapping and receiver request are tested separately; this source trace is the only cross-boundary evidence.", SourceConfidence.StrongSourceEvidence, []));

        var minimizationSummary = new DataMinimizationSummary
        {
            SensitiveFieldsEntering = traces.Where(t => t.Sensitive).Select(t => t.OriginField).Distinct().ToList(),
            RetainedInternally = traces.Where(t => t.Sensitive).SelectMany(t => t.Steps.Where(s => s.Stage == SourceStageKind.DomainEntity).Select(s => $"{s.TypeName}.{s.Field}")).Distinct().ToList(),
            EmittedRaw = traces.Where(t => t.Minimization == "Potential data-minimization issue").Select(t => t.OriginField).Distinct().ToList(),
            ReducedMetadata = traces.Where(t => t.Sensitive).SelectMany(t => t.Steps.Where(s => s.Stage == SourceStageKind.DomainEvent && s.Transformation is FieldTransformation.Booleanized or FieldTransformation.MetadataOnly or FieldTransformation.Reduced)
                .Select(s => $"{s.TypeName}.{s.Field} ({s.Transformation})")).Distinct().ToList(),
            PotentialExposures = gaps.Where(g => g.Title.StartsWith("Potential data-minimization", StringComparison.Ordinal)).Select(g => g.Detail).ToList(),
            InternalRawCopies = serializedCopies.Select(x => $"{Safe(Display(x.Entity))}.{Safe(x.Property)} ← serialized {Safe(Display(x.Source))}").Distinct().ToList(),
        };
        var inspected = new List<string>();
        foreach (var s in stages.Where(s => s.Kind is SourceStageKind.AdapterModel)) inspected.Add($"Adapter model {s.TypeName} ({s.Project})");
        foreach (var o in outbound) inspected.Add($"Outbound HTTP {o.Verb} {o.Path} with {Safe(Display(o.Type))}");
        foreach (var e in endpoints) inspected.Add($"Ingestion endpoint {e.Verb} {e.Path} ({Safe(Display(e.Type))})");
        foreach (var chg in changes) inspected.Add($"Change detection {chg.Input} → {chg.Entity}");
        if (events.Count > 0) inspected.Add($"Domain-event projection: {string.Join(", ", events.Select(e => e.EventType))}");
        if (outbox is not null) inspected.Add($"Outbox {outbox.EntityType} with envelope {outbox.Envelope}");
        if (dispatcher.Class is not null) inspected.Add($"Service Bus dispatcher {Safe(dispatcher.Class.Identifier.ValueText)}");
        inspected.Add($"Developer test definitions: {tests.Count} (not executed)");
        limitations.AddRange([
            "Path tracing is syntax-only: members are resolved from declared parameter/local/property types; DI, reflection, dynamic JSON and generic helpers may be unresolved.",
            "Field-name sensitivity is a naming heuristic; no personal value is read, shown or stored.",
            "Change flags and field-name lists are linked to event properties by parameter name (Partial).",
            "Implementation contracts are classes in source, not formal schemas.",
        ]);
        return new IntegrationPathEvidence
        {
            Stages = stages, Hops = hops, Fields = traces, Events = events, ChangeDetection = changes, Outbox = outbox, ServiceBus = serviceBus, Boundaries = boundaries,
            Gaps = gaps, Rules = rules, Minimization = minimizationSummary, Inspected = inspected, Limitations = limitations,
        };

        string StageEvidence(SourceStageKind kind) => kind switch
        {
            SourceStageKind.AdapterModel => "Constructed from CDC fields and/or sent over HTTP by the adapter",
            SourceStageKind.IngestionRequest => "Request type bound by a mapped ingestion endpoint",
            SourceStageKind.IngestionDto => "Constructed from the ingestion request",
            SourceStageKind.DomainEntity => "Persisted entity (DbSet)",
            SourceStageKind.DomainEvent => "Published as event data",
            SourceStageKind.EventEnvelope => "Generic envelope around event data",
            SourceStageKind.Outbox => "Persisted outbox row carrying the serialized envelope",
            _ => "",
        };
        string DescribeMember(ExpressionSyntax expression, IqrSourceAnalyzer.Code c)
        {
            var member = Unwrap(expression).DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Select(m => MemberRef(m, c)).OfType<string>().FirstOrDefault();
            return member is null ? "Set (source not resolved)" : $"{Safe(member)}";
        }
    }

    // ── Test facts ────────────────────────────────────────────────────────────────────────────────────────────────
    private static List<TestFacts> TestFactsOf(IReadOnlyList<IqrSourceAnalyzer.Code> all, IReadOnlyList<DeveloperTestEvidence> tests)
    {
        var byId = tests.ToDictionary(t => t.Id);
        var facts = new List<TestFacts>();
        var assertion = new Regex(@"^(Should\w*|Assert\w*|Received|DidNotReceive|Verify\w*|Equal|NotEqual|True|False|Null|NotNull|Contains|DoesNotContain|Single|Empty|NotEmpty)$");
        foreach (var c in all)
        foreach (var method in c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (!byId.TryGetValue(IqrSourceAnalyzer.TestId(c.Path, method.SpanStart), out var evidence)) continue;
            var invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
            var asserted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var inv in invocations.Where(i => MethodName(i) is { } n && assertion.IsMatch(n) || i.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "Assert" } }))
                foreach (var id in inv.DescendantNodesAndSelf().OfType<SimpleNameSyntax>()) asserted.Add(id.Identifier.ValueText);
            var referenced = method.DescendantNodes().OfType<SimpleNameSyntax>().Select(n => n.Identifier.ValueText)
                .Concat(method.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression) && l.Token.ValueText.Length <= 80).Select(l => l.Token.ValueText))
                .ToHashSet(StringComparer.Ordinal);
            var classText = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            if (classText is not null) foreach (var n in classText.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.DescendantNodes().OfType<SimpleNameSyntax>())) referenced.Add(n.Identifier.ValueText);
            var constructed = method.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Select(o => Simple(o.Type.ToString())).ToHashSet(StringComparer.Ordinal);
            var nonNull = new HashSet<string>(StringComparer.Ordinal);
            foreach (var arg in method.DescendantNodes().OfType<ArgumentSyntax>().Where(a => a.NameColon is not null && !IsNull(a.Expression))) nonNull.Add(arg.NameColon!.Name.Identifier.ValueText);
            foreach (var a in method.DescendantNodes().OfType<AssignmentExpressionSyntax>().Where(a => !IsNull(a.Right)))
                if (a.Left is IdentifierNameSyntax l) nonNull.Add(l.Identifier.ValueText); else if (a.Left is MemberAccessExpressionSyntax m) nonNull.Add(m.Name.Identifier.ValueText);
            // Calls per method name, counting only those that are not inside an assertion (a mock verification is not a second call).
            var counts = invocations.Where(i => !i.Ancestors().OfType<InvocationExpressionSyntax>().Any(a => MethodName(a) is { } n && assertion.IsMatch(n)) && !(MethodName(i) is { } self && assertion.IsMatch(self))
                    && !(i.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Received" or "DidNotReceive" } } }))
                .Select(MethodName).OfType<string>().GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            facts.Add(new TestFacts(evidence, evidence.ProductionSymbols.ToHashSet(StringComparer.Ordinal), asserted, constructed, nonNull, counts, referenced));
        }
        return facts;
    }

    // ── Syntax helpers ────────────────────────────────────────────────────────────────────────────────────────────
    private static SourceLocation At(IqrSourceAnalyzer.Code c, SyntaxNode node) => new(Safe(c.Path), node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
    private static SourceLocation At(string path, SyntaxNode node) => new(Safe(path), node.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
    private static string? MethodName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        IdentifierNameSyntax i => i.Identifier.ValueText,
        GenericNameSyntax g => g.Identifier.ValueText,
        MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText,
        _ => null,
    };
    private static ExpressionSyntax Unwrap(ExpressionSyntax e) => e switch
    {
        ParenthesizedExpressionSyntax p => Unwrap(p.Expression),
        CastExpressionSyntax c => Unwrap(c.Expression),
        AwaitExpressionSyntax a => Unwrap(a.Expression),
        PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } p => Unwrap(p.Operand),
        _ => e,
    };
    private static ParameterListSyntax? RecordParameters(TypeDeclarationSyntax? t) => (t as RecordDeclarationSyntax)?.ParameterList;
    private static bool IsNull(ExpressionSyntax e) => Unwrap(e) is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.NullLiteralExpression);
    private static bool IsConstant(ExpressionSyntax e) => e is LiteralExpressionSyntax or DefaultExpressionSyntax
        || e is MemberAccessExpressionSyntax { Expression: PredefinedTypeSyntax } || e is MemberAccessExpressionSyntax m && m.ToString() is "string.Empty" or "String.Empty";
    private static bool IsValueLike(string type) => Simple(type) is "Guid" or "int" or "long" or "bool" or "DateOnly" or "DateTime" or "DateTimeOffset" or "decimal" or "double";
    private static ExpressionSyntax? LocalInitializer(IdentifierNameSyntax id) =>
        id.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault()?.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.ValueText == id.Identifier.ValueText && v.SpanStart < id.SpanStart)?.Initializer?.Value;
    private static string Simple(string type)
    {
        var t = type.Trim().TrimEnd('?');
        var generic = t.IndexOf('<');
        if (generic >= 0) t = t[..generic];
        return t.Split('.').Last();
    }
    private static string? ElementType(string type)
    {
        var m = Regex.Match(type.Trim(), @"^(?:System\.Collections\.Generic\.)?(?:IReadOnlyList|IReadOnlyCollection|IList|ICollection|IEnumerable|List)<(.+)>\??$");
        if (m.Success) return m.Groups[1].Value.Trim();
        return type.Trim().EndsWith("[]", StringComparison.Ordinal) ? type.Trim()[..^2] : null;
    }
    private static string UnwrapTask(string type)
    {
        var m = Regex.Match(type.Trim(), @"^(?:Task|ValueTask)<(.+)>$");
        return (m.Success ? m.Groups[1].Value : type).Trim().TrimEnd('?');
    }
    private static string Root(string path) => path.Split('/')[0];
    private static string Normalize(string path) => "/" + path.Trim().Trim('/');
    private static string Fold(string value) => value.Replace("ø", "oe", StringComparison.OrdinalIgnoreCase).Replace("æ", "ae", StringComparison.OrdinalIgnoreCase)
        .Replace("å", "aa", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
    private static string Describe(List<string> refs) => refs.Count == 0 ? "no resolved field" : string.Join(", ", refs.Select(r => Safe(r.Replace("cdc:", "CDC "))).Take(4));
    private static string? ConstantString(ExpressionSyntax expression, IqrSourceAnalyzer.Code c)
    {
        if (expression is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression)) return l.Token.ValueText;
        if (expression is IdentifierNameSyntax id)
            return c.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>().FirstOrDefault(v => v.Identifier.ValueText == id.Identifier.ValueText && v.Parent?.Parent is FieldDeclarationSyntax)
                ?.Initializer?.Value is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression) ? lit.Token.ValueText : null;
        return null;
    }
}
