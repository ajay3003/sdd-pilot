using System.Diagnostics;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.ApiReview;
using BirkNext.LocalHttpsProxy;
using HotChocolate.Language;
using Microsoft.Extensions.Logging;

namespace BirkNext.Api.Services.ApiQuality;

/// <summary>
/// GraphQL client/server compatibility: do the operations the frontend actually sent (Endpoint Discovery, normalized and literal-redacted)
/// still validate against the best trusted schema of the endpoint they were observed on? Pure and in-memory: the schema is parsed once,
/// each unique document once, and nothing is executed — an observed mutation is contract-validated only.
///
/// Why a structural validator over the normalized schema rather than Hot Chocolate's executable-schema validator: the executable one needs
/// a runnable <c>ISchema</c>, which cannot be built from an introspection result or a bare SDL without binding every custom scalar and
/// resolver. The document side still uses Hot Chocolate's parser (AST), and the rules and codes follow the GraphQL specification's
/// validation section for what a schema can decide: fields, arguments, required arguments, variable types and usage, fragments, leaf and
/// composite selections, enum and input-object literals.
/// </summary>
public static class GraphQlOperationCompatibility
{
    public const string RuleId = "gql-operation-incompatible";
    public const string NoSchemaReason = "No GraphQL schema was available for validation.";
    public const string NoDocumentReason = "The operation document was not captured (Endpoint Discovery recorded its name and type only).";
    public const string UnparseableReason = "The observed operation document could not be parsed.";

    /// <summary>
    /// Assesses every observed operation of one endpoint. With no schema every operation is Not assessed — never "0 compatible" — and
    /// the observed count is unchanged. One malformed document never stops the others.
    /// </summary>
    public static ApiReviewGraphQlCompatibility Assess(
        GraphQlNormalizedContract? schema, GraphQlSchemaSource source, string? sourceDetail, DateTimeOffset? retrievedAt,
        IReadOnlyList<ApiReviewOperation> observed, string endpoint, ILogger? logger = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var index = schema is null ? null : SchemaIndex.From(schema);
        var results = new List<GraphQlOperationCompatibilityResult>();
        foreach (var operation in observed.Where(o => o.OperationType != GraphQlOperationType.None))
        {
            var result = new GraphQlOperationCompatibilityResult
            {
                OperationId = $"{endpoint}|{operation.OperationType}|{operation.OperationName}|{operation.DocumentHash}",
                OperationName = operation.OperationName, OperationType = operation.OperationType, Endpoint = endpoint,
                DocumentHash = operation.DocumentHash, ObservationCount = operation.ObservedCount,
                FirstObservedAt = operation.FirstObservedAt, LastObservedAt = operation.LastObservedAt, Historical = operation.Historical,
            };
            if (index is null) { results.Add(result with { NotAssessedReason = NoSchemaReason }); continue; }
            if (string.IsNullOrWhiteSpace(operation.Document)) { results.Add(result with { NotAssessedReason = NoDocumentReason }); continue; }
            try
            {
                var (issues, rootFields) = Validator.Validate(index, operation.Document);
                results.Add(result with
                {
                    Status = issues.Count == 0 ? GraphQlCompatibilityStatus.Compatible : GraphQlCompatibilityStatus.Incompatible,
                    Issues = issues, RootFields = rootFields,
                });
            }
            catch (SyntaxException)
            {
                results.Add(result with { NotAssessedReason = UnparseableReason });
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "GraphQL compatibility validator failed for one operation on {Endpoint}; the operation is Not assessed.", endpoint);
                results.Add(result with { NotAssessedReason = "The validator could not assess this document." });
            }
        }

        var compatibility = new ApiReviewGraphQlCompatibility
        {
            SchemaSource = index is null ? GraphQlSchemaSource.None : source,
            SchemaSourceDetail = index is null ? null : sourceDetail,
            SchemaRetrievedAt = index is null ? null : retrievedAt,
            NotAssessedReason = index is null ? NoSchemaReason : null,
            Operations = results,
            DurationMs = stopwatch.Elapsed.TotalMilliseconds,
        };
        logger?.LogInformation(
            "GraphQL compatibility for {Endpoint}: schema source {SchemaSource}, {Unique} unique operation(s), {Compatible} compatible, {Incompatible} incompatible, {NotAssessed} not assessed in {DurationMs:0} ms.",
            endpoint, compatibility.SchemaSource, compatibility.Observed, compatibility.Compatible, compatibility.Incompatible, compatibility.NotAssessed, compatibility.DurationMs);
        return compatibility;
    }

    /// <summary>
    /// One source finding per incompatible operation, carrying all its violations as evidence — so one operation with two missing fields is
    /// one logical issue with two observations, not two issues. Severity follows the existing contract policy: a break of an operation the
    /// frontend currently uses is High (as a missing required property or type mismatch is); one seen only in retained history is Low.
    /// </summary>
    public static List<ApiReviewFinding> Findings(ApiReviewGraphQlCompatibility compatibility, string targetId) =>
        compatibility.Operations.Where(o => o.Status == GraphQlCompatibilityStatus.Incompatible).Select(o =>
            OpenApiDocumentReview.Finding(targetId, RuleId, o.Historical ? ApiReviewSeverity.Low : ApiReviewSeverity.High, ApiReviewFindingType.Contract, o.Display,
                "Client/server compatibility",
                "Observed GraphQL operation is incompatible with current schema",
                $"{o.Display} ({(o.Historical ? "historical evidence only" : $"observed {o.ObservationCount} time(s)")}) does not validate against the {SourceLabel(compatibility.SchemaSource)}: {o.Issues[0].Message}"
                    + (o.Issues.Count > 1 ? $" (+{o.Issues.Count - 1} more)" : ""),
                "Update the frontend operation to the current contract, or restore the removed/changed server field if the change was unintended. Regenerate generated client code if applicable.",
                o.Issues.Select(i => $"{i.Code}: {i.Message}").Take(15).ToList(), ApiReviewCheckResult.Fail, ApiReviewDriftClassification.Breaking)).ToList();

    public static string SourceLabel(GraphQlSchemaSource source) => source switch
    {
        GraphQlSchemaSource.RuntimeIntrospection => "runtime GraphQL schema",
        GraphQlSchemaSource.ConfiguredArtifact => "configured schema artifact",
        _ => "schema",
    };

    // ── Schema index ─────────────────────────────────────────────────────────────────────────────────────────────────

    internal sealed class SchemaIndex
    {
        private static readonly string[] BuiltInScalars = ["String", "Int", "Float", "Boolean", "ID"];
        public required Dictionary<string, GraphQlType> Types { get; init; }
        public string? Query { get; init; }
        public string? Mutation { get; init; }
        public string? Subscription { get; init; }

        public static SchemaIndex From(GraphQlNormalizedContract contract)
        {
            var types = contract.Types.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var scalar in BuiltInScalars)
                types.TryAdd(scalar, new GraphQlType { Name = scalar, Kind = "SCALAR" });
            string? Root(string? declared, string kind, string fallback) =>
                declared ?? contract.Operations.FirstOrDefault(o => o.Kind == kind)?.RootType ?? (types.ContainsKey(fallback) ? fallback : null);
            return new SchemaIndex
            {
                Types = types,
                Query = Root(contract.QueryTypeName, "query", "Query"),
                Mutation = Root(contract.MutationTypeName, "mutation", "Mutation"),
                Subscription = Root(contract.SubscriptionTypeName, "subscription", "Subscription"),
            };
        }

        public GraphQlType? Type(string? name) => name is not null && Types.TryGetValue(name, out var type) ? type : null;

        public static bool IsComposite(GraphQlType type) => type.Kind is "OBJECT" or "INTERFACE" or "UNION";
        public static bool IsInput(GraphQlType type) => type.Kind is "SCALAR" or "ENUM" or "INPUT_OBJECT";

        public HashSet<string> PossibleTypes(GraphQlType type) => type.Kind switch
        {
            "OBJECT" => [type.Name],
            "INTERFACE" => type.PossibleTypes is { Count: > 0 } declared
                ? [.. declared]
                : Types.Values.Where(t => t.Kind == "OBJECT" && t.Interfaces.Contains(type.Name)).Select(t => t.Name).ToHashSet(),
            "UNION" => [.. type.PossibleTypes ?? []],
            _ => [],
        };
    }

    // ── Validator ────────────────────────────────────────────────────────────────────────────────────────────────────

    internal static class Validator
    {
        private sealed class Context(SchemaIndex schema, Dictionary<string, FragmentDefinitionNode> fragments)
        {
            public SchemaIndex Schema { get; } = schema;
            public Dictionary<string, FragmentDefinitionNode> Fragments { get; } = fragments;
            public Dictionary<string, VariableDefinitionNode>? Variables { get; set; }
            public HashSet<string> Visiting { get; } = new(StringComparer.Ordinal);
            public List<GraphQlValidationIssue> Issues { get; } = [];
            public void Add(string code, string message, string? path)
            {
                if (!Issues.Any(i => i.Code == code && i.Message == message && i.Path == path)) Issues.Add(new(code, message, path));
            }
        }

        public static (List<GraphQlValidationIssue> Issues, List<string> RootFields) Validate(SchemaIndex schema, string document)
        {
            var parsed = Utf8GraphQLParser.Parse(document);
            var fragments = parsed.Definitions.OfType<FragmentDefinitionNode>()
                .GroupBy(f => f.Name.Value, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var context = new Context(schema, fragments);
            var rootFields = new List<string>();
            var usedFragments = new HashSet<string>(StringComparer.Ordinal);

            foreach (var operation in parsed.Definitions.OfType<OperationDefinitionNode>())
            {
                var (rootName, kind) = operation.Operation switch
                {
                    OperationType.Mutation => (schema.Mutation, "mutation"),
                    OperationType.Subscription => (schema.Subscription, "subscription"),
                    _ => (schema.Query, "query"),
                };
                var path = operation.Name?.Value ?? kind;
                var root = schema.Type(rootName);
                if (root is null)
                {
                    context.Add("OPERATION_TYPE_NOT_SUPPORTED", $"The schema does not support {kind} operations.", path);
                    continue;
                }
                context.Variables = operation.VariableDefinitions.ToDictionary(v => v.Variable.Name.Value, StringComparer.Ordinal);
                foreach (var variable in operation.VariableDefinitions) ValidateVariableDefinition(context, variable, path);
                rootFields.AddRange(RootFieldNames(context, operation.SelectionSet).Where(n => !n.StartsWith("__", StringComparison.Ordinal)));
                ValidateSelectionSet(context, root, operation.SelectionSet, path, root.Name == schema.Query, usedFragments);
            }

            // A fragment no operation spreads is still part of the document the client sends; validate its body without variables.
            context.Variables = null;
            foreach (var fragment in fragments.Values.Where(f => !usedFragments.Contains(f.Name.Value)))
            {
                var type = context.Schema.Type(fragment.TypeCondition.Name.Value);
                if (type is null) { context.Add("UNKNOWN_TYPE", $"Unknown type `{fragment.TypeCondition.Name.Value}` in fragment `{fragment.Name.Value}`.", fragment.Name.Value); continue; }
                if (!SchemaIndex.IsComposite(type)) { context.Add("INVALID_FRAGMENT_TYPE", $"Fragment `{fragment.Name.Value}` cannot be on the non-composite type `{type.Name}`.", fragment.Name.Value); continue; }
                ValidateSelectionSet(context, type, fragment.SelectionSet, fragment.Name.Value, false, usedFragments);
            }
            return (context.Issues, rootFields.Distinct(StringComparer.Ordinal).ToList());
        }

        private static IEnumerable<string> RootFieldNames(Context context, SelectionSetNode selectionSet)
        {
            foreach (var selection in selectionSet.Selections)
                switch (selection)
                {
                    case FieldNode field: yield return field.Name.Value; break;
                    case InlineFragmentNode inline: foreach (var n in RootFieldNames(context, inline.SelectionSet)) yield return n; break;
                    case FragmentSpreadNode spread when context.Fragments.TryGetValue(spread.Name.Value, out var fragment) && context.Visiting.Add("root:" + spread.Name.Value):
                        foreach (var n in RootFieldNames(context, fragment.SelectionSet)) yield return n;
                        context.Visiting.Remove("root:" + spread.Name.Value);
                        break;
                }
        }

        private static void ValidateVariableDefinition(Context context, VariableDefinitionNode variable, string path)
        {
            var name = variable.Variable.Name.Value;
            var typeName = variable.Type.NamedType().Name.Value;
            var type = context.Schema.Type(typeName);
            if (type is null) { context.Add("UNKNOWN_TYPE", $"Unknown type `{typeName}` for variable `${name}`.", path); return; }
            if (!SchemaIndex.IsInput(type)) { context.Add("TYPE_MISMATCH", $"Variable `${name}` cannot be of the non-input type `{typeName}`.", path); return; }
            if (variable.DefaultValue is { } defaultValue)
                ValidateValue(context, defaultValue, ToTypeRef(variable.Type), path, $"default value of `${name}`", false);
        }

        private static void ValidateSelectionSet(Context context, GraphQlType parent, SelectionSetNode selectionSet, string path, bool isQueryRoot, HashSet<string> usedFragments)
        {
            foreach (var selection in selectionSet.Selections)
            {
                switch (selection)
                {
                    case FieldNode field:
                        ValidateField(context, parent, field, path, isQueryRoot, usedFragments);
                        break;
                    case InlineFragmentNode inline:
                    {
                        var target = inline.TypeCondition is null ? parent : context.Schema.Type(inline.TypeCondition.Name.Value);
                        if (target is null) { context.Add("UNKNOWN_TYPE", $"Unknown type `{inline.TypeCondition!.Name.Value}` in inline fragment.", path); break; }
                        if (!CheckFragmentApplies(context, target, parent, path, "Inline fragment")) break;
                        ValidateSelectionSet(context, target, inline.SelectionSet, path, false, usedFragments);
                        break;
                    }
                    case FragmentSpreadNode spread:
                    {
                        var name = spread.Name.Value;
                        if (!context.Fragments.TryGetValue(name, out var fragment)) { context.Add("UNKNOWN_FRAGMENT", $"Unknown fragment `{name}`.", path); break; }
                        usedFragments.Add(name);
                        var target = context.Schema.Type(fragment.TypeCondition.Name.Value);
                        if (target is null) { context.Add("UNKNOWN_TYPE", $"Unknown type `{fragment.TypeCondition.Name.Value}` in fragment `{name}`.", name); break; }
                        if (!CheckFragmentApplies(context, target, parent, path, $"Fragment `{name}`")) break;
                        if (!context.Visiting.Add(name)) break;   // a cycle is not a schema question; stop rather than recurse forever
                        ValidateSelectionSet(context, target, fragment.SelectionSet, name, false, usedFragments);
                        context.Visiting.Remove(name);
                        break;
                    }
                }
            }
        }

        private static bool CheckFragmentApplies(Context context, GraphQlType target, GraphQlType parent, string path, string label)
        {
            if (!SchemaIndex.IsComposite(target))
            {
                context.Add("INVALID_FRAGMENT_TYPE", $"{label} cannot be on the non-composite type `{target.Name}`.", path);
                return false;
            }
            if (!context.Schema.PossibleTypes(target).Overlaps(context.Schema.PossibleTypes(parent)))
            {
                context.Add("INVALID_FRAGMENT_TYPE", $"{label} on `{target.Name}` can never apply to `{parent.Name}`.", path);
                return false;
            }
            return true;
        }

        private static void ValidateField(Context context, GraphQlType parent, FieldNode node, string path, bool isQueryRoot, HashSet<string> usedFragments)
        {
            var name = node.Name.Value;
            var fieldPath = $"{path}.{node.Alias?.Value ?? name}";
            if (name == "__typename")
            {
                if (node.SelectionSet is not null) context.Add("SELECTION_NOT_ALLOWED", "The field `__typename` returns a leaf type and cannot have a selection set.", fieldPath);
                return;
            }
            if (isQueryRoot && name is "__schema" or "__type") return;   // introspection meta fields: answered by every server
            var field = parent.Kind == "UNION" ? null : parent.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.Ordinal));
            if (field is null)
            {
                context.Add("FIELD_NOT_FOUND", $"The field `{name}` does not exist on the type `{parent.Name}`.", fieldPath);
                return;
            }

            foreach (var argument in node.Arguments)
            {
                var definition = field.Arguments.FirstOrDefault(a => string.Equals(a.Name, argument.Name.Value, StringComparison.Ordinal));
                if (definition is null)
                    context.Add("ARGUMENT_NOT_FOUND", $"The argument `{argument.Name.Value}` does not exist on the field `{parent.Name}.{name}`.", fieldPath);
                else if (definition.Type is { } type)
                    ValidateValue(context, argument.Value, type, fieldPath, $"argument `{argument.Name.Value}` of `{parent.Name}.{name}`", definition.DefaultValue is not null);
            }
            foreach (var required in field.Arguments.Where(a => a.Type?.Kind == "NON_NULL" && a.DefaultValue is null))
                if (!node.Arguments.Any(a => string.Equals(a.Name.Value, required.Name, StringComparison.Ordinal)))
                    context.Add("REQUIRED_ARGUMENT_MISSING", $"The argument `{required.Name}` is required on the field `{parent.Name}.{name}`.", fieldPath);

            var returnType = context.Schema.Type(field.Type?.Unwrap().TypeName);
            if (returnType is null) return;   // the schema references a type it does not describe; not the client's fault
            if (SchemaIndex.IsComposite(returnType))
            {
                if (node.SelectionSet is null)
                    context.Add("SELECTION_SET_REQUIRED", $"The field `{parent.Name}.{name}` returns the composite type `{returnType.Name}` and requires a selection set.", fieldPath);
                else
                    ValidateSelectionSet(context, returnType, node.SelectionSet, fieldPath, false, usedFragments);
            }
            else if (node.SelectionSet is not null)
                context.Add("SELECTION_NOT_ALLOWED", $"The field `{parent.Name}.{name}` returns the leaf type `{returnType.Name}` and cannot have a selection set.", fieldPath);
        }

        private static void ValidateValue(Context context, IValueNode value, GraphQlTypeRef location, string path, string label, bool locationHasDefault)
        {
            if (value is VariableNode variable)
            {
                if (context.Variables is null) return;   // an unused fragment has no variable scope
                var name = variable.Name.Value;
                if (!context.Variables.TryGetValue(name, out var definition)) { context.Add("VARIABLE_NOT_DEFINED", $"The variable `${name}` is not defined by the operation.", path); return; }
                var hasNonNullDefault = definition.DefaultValue is not null and not NullValueNode;
                var allowed = VariableFits(definition.Type, location)
                    || location.Kind == "NON_NULL" && definition.Type is not NonNullTypeNode && (hasNonNullDefault || locationHasDefault) && VariableFits(definition.Type, location.OfType!);
                if (!allowed)
                    context.Add("TYPE_MISMATCH", $"The variable `${name}` of type `{definition.Type}` cannot be used for {label}, which expects `{Print(location)}`.", path);
                return;
            }
            if (location.Kind == "NON_NULL")
            {
                if (value is NullValueNode) { context.Add("TYPE_MISMATCH", $"null is not allowed for {label}, which expects `{Print(location)}`.", path); return; }
                ValidateValue(context, value, location.OfType!, path, label, false);
                return;
            }
            if (value is NullValueNode) return;
            if (location.Kind == "LIST")
            {
                if (value is ListValueNode list) foreach (var item in list.Items) ValidateValue(context, item, location.OfType!, path, label, false);
                else ValidateValue(context, value, location.OfType!, path, label, false);   // input coercion: a single value becomes a list
                return;
            }
            if (value is ListValueNode) { context.Add("TYPE_MISMATCH", $"A list is not allowed for {label}, which expects `{Print(location)}`.", path); return; }

            var type = context.Schema.Type(location.Name);
            if (type is null) return;
            switch (type.Kind)
            {
                case "ENUM":
                    if (value is not EnumValueNode enumValue) { context.Add("TYPE_MISMATCH", $"{Capitalize(label)} expects a value of the enum `{type.Name}`.", path); return; }
                    if (type.EnumValues is { } values && !values.Any(v => v.Name == enumValue.Value))
                        context.Add("ENUM_VALUE_INVALID", $"The value `{enumValue.Value}` is not a valid value of the enum `{type.Name}`.", path);
                    return;
                case "INPUT_OBJECT":
                    if (value is not ObjectValueNode obj) { context.Add("TYPE_MISMATCH", $"{Capitalize(label)} expects an object of the input type `{type.Name}`.", path); return; }
                    var fields = type.InputFields ?? [];
                    foreach (var provided in obj.Fields)
                    {
                        var definition = fields.FirstOrDefault(f => f.Name == provided.Name.Value);
                        if (definition?.Type is null) context.Add("INPUT_FIELD_NOT_FOUND", $"The field `{provided.Name.Value}` does not exist on the input type `{type.Name}`.", path);
                        else ValidateValue(context, provided.Value, definition.Type, path, $"input field `{type.Name}.{provided.Name.Value}`", false);
                    }
                    foreach (var required in fields.Where(f => f.Type?.Kind == "NON_NULL"))
                        if (!obj.Fields.Any(f => f.Name.Value == required.Name))
                            context.Add("REQUIRED_INPUT_FIELD_MISSING", $"The field `{required.Name}` is required on the input type `{type.Name}`.", path);
                    return;
                case "SCALAR":
                    var fits = type.Name switch
                    {
                        "Int" => value is IntValueNode,
                        "Float" => value is IntValueNode or FloatValueNode,
                        "String" => value is StringValueNode,
                        "Boolean" => value is BooleanValueNode,
                        "ID" => value is StringValueNode or IntValueNode,
                        _ => value is not ObjectValueNode,   // custom scalars define their own literal forms
                    };
                    if (!fits) context.Add("TYPE_MISMATCH", $"{Capitalize(label)} expects `{type.Name}`, not a {Kind(value)} literal.", path);
                    return;
            }
        }

        /// <summary>Is a variable of this declared type usable where the schema expects <paramref name="location"/>? (Spec: AreTypesCompatible.)</summary>
        private static bool VariableFits(ITypeNode variable, GraphQlTypeRef location)
        {
            if (location.Kind == "NON_NULL")
                return variable is NonNullTypeNode nonNull && VariableFits(nonNull.Type, location.OfType!);
            if (variable is NonNullTypeNode inner) return VariableFits(inner.Type, location);
            if (location.Kind == "LIST") return variable is ListTypeNode list && VariableFits(list.Type, location.OfType!);
            return variable is NamedTypeNode named && named.Name.Value == location.Name;
        }

        private static GraphQlTypeRef ToTypeRef(ITypeNode node) => node switch
        {
            NonNullTypeNode nonNull => new GraphQlTypeRef { Kind = "NON_NULL", OfType = ToTypeRef(nonNull.Type) },
            ListTypeNode list => new GraphQlTypeRef { Kind = "LIST", OfType = ToTypeRef(list.Type) },
            NamedTypeNode named => new GraphQlTypeRef { Kind = "NAMED", Name = named.Name.Value },
            _ => new GraphQlTypeRef(),
        };

        private static string Print(GraphQlTypeRef type) => type.Kind switch
        {
            "NON_NULL" => Print(type.OfType!) + "!",
            "LIST" => $"[{Print(type.OfType!)}]",
            _ => type.Name ?? "",
        };

        private static string Kind(IValueNode value) => value switch
        {
            StringValueNode => "string", IntValueNode => "integer", FloatValueNode => "float", BooleanValueNode => "boolean",
            EnumValueNode => "enum", ObjectValueNode => "object", _ => "value",
        };

        private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }
}

/// <summary>
/// Builds the normalized schema from SDL (a configured, trusted schema artifact) with Hot Chocolate's parser, so a deployment that
/// disables introspection can still be checked when its schema is published. Type and schema extensions are merged.
/// </summary>
public static class GraphQlSdlSchema
{
    public static GraphQlNormalizedContract? FromSdl(string sdl, out string? error)
    {
        error = null;
        DocumentNode document;
        try { document = Utf8GraphQLParser.Parse(sdl); }
        catch (SyntaxException ex) { error = $"SDL could not be parsed: {ex.Message}"; return null; }

        var types = new Dictionary<string, GraphQlType>(StringComparer.Ordinal);
        GraphQlType Get(string name, string kind) => types.TryGetValue(name, out var existing) ? existing : types[name] = new GraphQlType { Name = name, Kind = kind };
        string? query = null, mutation = null, subscription = null;

        foreach (var definition in document.Definitions)
        {
            switch (definition)
            {
                case SchemaDefinitionNodeBase schema:
                    foreach (var root in schema.OperationTypes)
                        switch (root.Operation)
                        {
                            case OperationType.Query: query = root.Type.Name.Value; break;
                            case OperationType.Mutation: mutation = root.Type.Name.Value; break;
                            case OperationType.Subscription: subscription = root.Type.Name.Value; break;
                        }
                    break;
                case ComplexTypeDefinitionNodeBase complex:
                {
                    // Object and interface definitions and their extensions share this base.
                    var kind = complex is ObjectTypeDefinitionNode or ObjectTypeExtensionNode ? "OBJECT" : "INTERFACE";
                    var type = Get(complex.Name.Value, kind);
                    type.Fields.AddRange(complex.Fields.Select(Field));
                    type.Interfaces.AddRange(complex.Interfaces.Select(i => i.Name.Value));
                    break;
                }
                case UnionTypeDefinitionNodeBase union:
                    (Get(union.Name.Value, "UNION").PossibleTypes ??= []).AddRange(union.Types.Select(t => t.Name.Value));
                    break;
                case EnumTypeDefinitionNodeBase enumType:
                    (Get(enumType.Name.Value, "ENUM").EnumValues ??= []).AddRange(enumType.Values.Select(v => new GraphQlEnumValue { Name = v.Name.Value, IsDeprecated = IsDeprecated(v.Directives) }));
                    break;
                case InputObjectTypeDefinitionNodeBase input:
                    (Get(input.Name.Value, "INPUT_OBJECT").InputFields ??= []).AddRange(input.Fields.Select(f => new GraphQlField { Name = f.Name.Value, Type = TypeRef(f.Type) }));
                    break;
                case ScalarTypeDefinitionNode scalar:
                    Get(scalar.Name.Value, "SCALAR");
                    break;
            }
        }

        query ??= types.ContainsKey("Query") ? "Query" : null;
        mutation ??= types.ContainsKey("Mutation") ? "Mutation" : null;
        subscription ??= types.ContainsKey("Subscription") ? "Subscription" : null;
        if (query is null) { error = "SDL defines no query root type."; return null; }
        foreach (var iface in types.Values.Where(t => t.Kind == "INTERFACE"))
            iface.PossibleTypes = types.Values.Where(t => t.Kind == "OBJECT" && t.Interfaces.Contains(iface.Name)).Select(t => t.Name).ToList();

        return new GraphQlNormalizedContract
        {
            Name = "SDL", Source = new ContractSource { Type = ContractSourceType.GraphQlSchema, FetchedAt = DateTime.UtcNow },
            Types = types.Values.ToList(), QueryTypeName = query, MutationTypeName = mutation, SubscriptionTypeName = subscription,
        };
    }

    private static GraphQlField Field(FieldDefinitionNode field) => new()
    {
        Name = field.Name.Value,
        Type = TypeRef(field.Type),
        IsDeprecated = IsDeprecated(field.Directives),
        Arguments = field.Arguments.Select(a => new GraphQlArgument { Name = a.Name.Value, Type = TypeRef(a.Type), DefaultValue = a.DefaultValue?.ToString() }).ToList(),
    };

    private static bool IsDeprecated(IReadOnlyList<DirectiveNode> directives) => directives.Any(d => d.Name.Value == "deprecated");

    private static GraphQlTypeRef TypeRef(ITypeNode node) => node switch
    {
        NonNullTypeNode nonNull => new GraphQlTypeRef { Kind = "NON_NULL", OfType = TypeRef(nonNull.Type) },
        ListTypeNode list => new GraphQlTypeRef { Kind = "LIST", OfType = TypeRef(list.Type) },
        NamedTypeNode named => new GraphQlTypeRef { Kind = "NAMED", Name = named.Name.Value },
        _ => new GraphQlTypeRef(),
    };
}
