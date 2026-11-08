using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.ExecutionEvidence;
using BirkNext.SourceDomains;
using BirkNext.TestData;
using HotChocolate.Language;

namespace BirkNext.Api.Services.ContractTesting;

/// <summary>
/// GraphQL contract checks against a stored schema version: the document (syntax; operation root fields and their arguments exist; every selected
/// field exists on its type; leaf vs object selections), the variables (declared types incl. non-null, lists, enums, input objects and unknown
/// input fields — the Test Data type rules), and the returned data (each selected field's value fits its declared type and nullability; a null
/// in a non-null position is reported unless the errors array reports that path). GraphQL errors are judged independently of the HTTP status.
/// Fragment spreads are reported as not assessed; inline fragments are checked against their type condition.
/// </summary>
public static class GraphQlContractValidator
{
    private static readonly string[] Scalars = ["String", "Int", "Float", "Boolean", "ID"];

    public static (List<ContractValidationFinding> Findings, List<string> Unsupported) ValidateRequest(SourceContract schema, string? query, string? variablesJson)
    {
        var findings = new List<ContractValidationFinding>();
        var unsupported = new List<string>();
        DocumentNode document;
        try { document = Utf8GraphQLParser.Parse(query ?? ""); }
        catch (SyntaxException ex) { findings.Add(new("Syntax", "$.query", "", "syntax", $"The query document is not valid GraphQL: {ex.Message}")); return (findings, unsupported); }
        var operations = document.Definitions.OfType<OperationDefinitionNode>().ToList();
        if (operations.Count != 1) { findings.Add(new("Syntax", "$.query", "", "operation", $"Exactly one operation is expected, found {operations.Count}.")); return (findings, unsupported); }
        var op = operations[0];
        var kind = op.Operation.ToString().ToLowerInvariant();
        JsonObject variables = [];
        if (!string.IsNullOrWhiteSpace(variablesJson))
        {
            try { variables = JsonNode.Parse(variablesJson) as JsonObject ?? []; }
            catch (JsonException) { findings.Add(new("Syntax", "$.variables", "", "variables", "The variables are not a JSON object.")); }
        }
        foreach (var definition in op.VariableDefinitions)
        {
            var name = definition.Variable.Name.Value;
            var type = definition.Type.ToString();
            if (!variables.TryGetPropertyValue(name, out var value))
            {
                if (type.EndsWith('!') && definition.DefaultValue is null) findings.Add(new("Operation", $"$.variables.{name}", "", "required", $"Variable ${name} ({type}) is required but not provided."));
                continue;
            }
            foreach (var problem in TestDataOperationCompatibility.Value(type, value, schema.Types, $"${name}")) findings.Add(new("Type", $"$.variables.{name}", "", "type", problem));
            foreach (var extra in UnknownInputFields(type, value, schema.Types, $"$.variables.{name}")) findings.Add(extra);
        }
        foreach (var (name, _) in variables.Where(v => op.VariableDefinitions.All(d => d.Variable.Name.Value != v.Key)))
            findings.Add(new("Operation", $"$.variables.{name}", "", "variables", $"${name} is not declared by the operation."));
        foreach (var field in op.SelectionSet.Selections.OfType<FieldNode>())
        {
            var name = field.Name.Value;
            if (name == "__typename") continue;
            var root = schema.Operations.FirstOrDefault(o => o.Kind == kind && (o.OperationId ?? o.Name.Split(' ').Last()) == name);
            if (root is null) { findings.Add(new("Operation", $"$.query.{name}", "", "field", $"{kind} {name} is not in the schema.")); continue; }
            foreach (var argument in field.Arguments.Where(a => root.ParameterDetails.All(p => p.Name != a.Name.Value)))
                findings.Add(new("Operation", $"$.query.{name}({argument.Name.Value})", "", "argument", $"{name} has no argument {argument.Name.Value}."));
            foreach (var missing in root.ParameterDetails.Where(p => p.Required && field.Arguments.All(a => a.Name.Value != p.Name)))
                findings.Add(new("Operation", $"$.query.{name}", "", "argument", $"{name} requires argument {missing.Name} ({missing.Type})."));
            Selections(field.SelectionSet, root.ResultType, $"$.query.{name}", schema, findings, unsupported);
        }
        foreach (var spread in op.SelectionSet.Selections.OfType<FragmentSpreadNode>()) unsupported.Add($"fragment spread ...{spread.Name.Value}");
        return (findings, unsupported);
    }

    /// <summary>Nesting beyond this is not evaluated (reported as unsupported), so a hostile document cannot exhaust the stack.</summary>
    public const int MaxNesting = 32;

    private static int Nesting(string path) => path.Count(c => c is '.' or '[');

    private static void Selections(SelectionSetNode? selections, string? type, string path, SourceContract schema, List<ContractValidationFinding> findings, List<string> unsupported)
    {
        if (Nesting(path) > MaxNesting) { if (!unsupported.Contains($"selection nesting > {MaxNesting}")) unsupported.Add($"selection nesting > {MaxNesting}"); return; }
        var named = Named(type);
        var shape = named is null ? null : schema.Types.FirstOrDefault(t => t.Name == named);
        var leaf = named is null || Scalars.Contains(named) || shape is null || shape.Kind is "enum" or "scalar";
        if (leaf)
        {
            if (selections is not null) findings.Add(new("Operation", path, "", "selection", $"{named} is a leaf type and takes no selection set."));
            return;
        }
        if (selections is null) { findings.Add(new("Operation", path, "", "selection", $"{named} is an object type and needs a selection set.")); return; }
        foreach (var selection in selections.Selections)
            switch (selection)
            {
                case FieldNode f when f.Name.Value == "__typename": break;
                case FieldNode f:
                    var field = shape!.Fields.FirstOrDefault(x => x.Name == f.Name.Value);
                    if (field is null) findings.Add(new("Operation", $"{path}.{f.Name.Value}", "", "field", $"{named} has no field {f.Name.Value}."));
                    else Selections(f.SelectionSet, field.Type, $"{path}.{f.Name.Value}", schema, findings, unsupported);
                    break;
                case InlineFragmentNode inline:
                    Selections(inline.SelectionSet, inline.TypeCondition?.Name.Value ?? named, path, schema, findings, unsupported);
                    break;
                case FragmentSpreadNode spread: unsupported.Add($"fragment spread ...{spread.Name.Value}"); break;
            }
    }

    private static IEnumerable<ContractValidationFinding> UnknownInputFields(string type, JsonNode? value, IReadOnlyList<ContractTypeShape> types, string path)
    {
        var named = Named(type);
        if (value is JsonArray list) { var i = 0; foreach (var item in list) foreach (var f in UnknownInputFields(type.Trim('!').Trim('[', ']'), item, types, $"{path}[{i++}]")) yield return f; yield break; }
        if (value is not JsonObject o || types.FirstOrDefault(t => t.Name == named && t.Kind == "input_object") is not { } shape) yield break;
        foreach (var (name, child) in o)
        {
            var field = shape.Fields.FirstOrDefault(f => f.Name == name);
            if (field is null) { yield return new("Operation", $"{path}.{name}", "", "inputField", $"{named} has no input field {name}."); continue; }
            foreach (var nested in UnknownInputFields(field.Type, child, types, $"{path}.{name}")) yield return nested;
        }
    }

    /// <summary>Checks the data of a GraphQL response against the selected fields' declared types. Errors are returned separately by the run.</summary>
    public static List<ContractValidationFinding> ValidateResponse(SourceContract schema, string? query, JsonNode? response)
    {
        var findings = new List<ContractValidationFinding>();
        if (response is not JsonObject envelope || (!envelope.ContainsKey("data") && !envelope.ContainsKey("errors")))
        { findings.Add(new("Syntax", "$", "", "envelope", "The response is not a GraphQL response (no data or errors).")); return findings; }
        var errorPaths = (envelope["errors"] as JsonArray ?? []).Select(e => e?["path"] is JsonArray p ? string.Join('.', p.Select(x => x?.ToString())) : null).Where(p => p is not null).ToHashSet();
        if (envelope["data"] is not JsonObject data) return findings;
        DocumentNode document;
        try { document = Utf8GraphQLParser.Parse(query ?? ""); } catch (SyntaxException) { return findings; }
        if (document.Definitions.OfType<OperationDefinitionNode>().FirstOrDefault() is not { } op) return findings;
        var kind = op.Operation.ToString().ToLowerInvariant();
        foreach (var field in op.SelectionSet.Selections.OfType<FieldNode>().Where(f => f.Name.Value != "__typename"))
        {
            var key = field.Alias?.Value ?? field.Name.Value;
            var root = schema.Operations.FirstOrDefault(o => o.Kind == kind && (o.OperationId ?? o.Name.Split(' ').Last()) == field.Name.Value);
            if (root is null) continue;
            if (!data.TryGetPropertyValue(key, out var value)) { findings.Add(new("Schema", $"$.data.{key}", "", "field", $"The response has no data for {key}.")); continue; }
            Value(value, root.ResultType, field.SelectionSet, $"$.data.{key}", key, schema, errorPaths, findings);
        }
        return findings;
    }

    private static void Value(JsonNode? value, string? type, SelectionSetNode? selections, string path, string errorPath, SourceContract schema, HashSet<string?> errorPaths, List<ContractValidationFinding> findings)
    {
        if (Nesting(path) > MaxNesting * 2)
        {
            if (!findings.Any(f => f.Keyword == "depth")) findings.Add(new("Unsupported", path, "", "depth", $"Response nesting beyond {MaxNesting * 2} levels is not evaluated."));
            return;
        }
        var t = (type ?? "").Trim();
        var nonNull = t.EndsWith('!');
        if (nonNull) t = t[..^1];
        if (value is null)
        {
            if (nonNull && !errorPaths.Any(p => p is not null && (p == errorPath || p.StartsWith(errorPath + ".", StringComparison.Ordinal))))
                findings.Add(new("Type", path, "", "nullability", $"null in a non-null position ({type}) without a GraphQL error for this path."));
            return;
        }
        if (t.StartsWith('['))
        {
            if (value is not JsonArray list) { findings.Add(new("Type", path, "", "list", $"Expected a list ({type}), got {value.GetValueKind()}.")); return; }
            for (var i = 0; i < list.Count; i++) Value(list[i], t[1..^1], selections, $"{path}[{i}]", $"{errorPath}.{i}", schema, errorPaths, findings);
            return;
        }
        var kind = value.GetValueKind();
        var problem = t switch
        {
            "Int" => kind == JsonValueKind.Number && JsonNumber.Of(value) % 1 == 0 ? null : "an Int",
            "Float" => kind == JsonValueKind.Number ? null : "a Float",
            "String" => kind == JsonValueKind.String ? null : "a String",
            "Boolean" => kind is JsonValueKind.True or JsonValueKind.False ? null : "a Boolean",
            "ID" => kind is JsonValueKind.String or JsonValueKind.Number ? null : "an ID",
            _ => null,
        };
        if (problem is not null) { findings.Add(new("Type", path, "", "type", $"Expected {problem} ({type}), got {kind}.")); return; }
        if (schema.Types.FirstOrDefault(x => x.Name == t) is not { } shape) return;
        if (shape.Kind == "enum")
        {
            if (kind != JsonValueKind.String || !shape.Values.Contains(value.GetValue<string>())) findings.Add(new("Type", path, "", "enum", $"{value.ToJsonString()} is not a {t} value ({string.Join(", ", shape.Values)})."));
            return;
        }
        if (value is not JsonObject o) { findings.Add(new("Type", path, "", "type", $"Expected an object ({t}), got {kind}.")); return; }
        foreach (var selection in selections?.Selections.OfType<FieldNode>() ?? [])
        {
            if (selection.Name.Value == "__typename") continue;
            var key = selection.Alias?.Value ?? selection.Name.Value;
            var field = shape.Fields.FirstOrDefault(f => f.Name == selection.Name.Value);
            if (field is null) continue;
            if (!o.TryGetPropertyValue(key, out var child)) { findings.Add(new("Schema", $"{path}.{key}", "", "field", $"The selected field {key} is missing.")); continue; }
            Value(child, field.Type, selection.SelectionSet, $"{path}.{key}", $"{errorPath}.{key}", schema, errorPaths, findings);
        }
    }

    private static string? Named(string? type) => type is null ? null : type.Replace("!", "").Replace("[", "").Replace("]", "").Trim() is { Length: > 0 } n ? n : null;
}
