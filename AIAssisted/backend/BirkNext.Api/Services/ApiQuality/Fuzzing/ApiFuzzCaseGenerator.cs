using System.Globalization;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.ApiReview;

namespace BirkNext.Api.Services.ApiQuality.Fuzzing;

/// <summary>
/// Deterministic, contract-derived fuzz cases. Every case mutates exactly one declared parameter or argument of one read-only operation;
/// all other required inputs get fixed synthetic valid values (never values from traffic, never real identifiers). Same contract + same
/// settings → same cases, same ids, same order. No attack dictionaries: the values are type/constraint violations and bounded strings.
/// </summary>
public static class ApiFuzzCaseGenerator
{
    /// <summary>Synthetic values. The UUID is the nil-like v4 form; nothing resembles a real person or record.</summary>
    internal const string SyntheticUuid = "00000000-0000-4000-8000-000000000000";
    internal const string SyntheticString = "birknext-fuzz";
    internal const string SyntheticDate = "2000-01-01";
    internal const string SyntheticDateTime = "2000-01-01T00:00:00Z";
    internal const string InvalidUuid = "not-a-uuid";
    internal const string InvalidDate = "2000-13-45";
    internal const string InvalidEnum = "BIRKNEXT_INVALID_ENUM";
    internal const string UnexpectedParameterName = "birknextUnexpectedParam";

    private static readonly HashSet<string> ReadOnlyMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    public sealed record GenerationResult(List<ApiFuzzOperationEligibility> Operations, List<ApiFuzzCase> Cases);

    // ── REST ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Cases for every read-only operation of the OpenAPI contract; write operations are listed as UnsafeMethod and get none.</summary>
    public static GenerationResult Rest(ApiReviewTarget target, NormalizedContract contract, ApiFuzzingSettings settings)
    {
        var operations = new List<ApiFuzzOperationEligibility>();
        var cases = new List<ApiFuzzCase>();
        foreach (var op in contract.Operations.OrderBy(o => o.Path, StringComparer.Ordinal).ThenBy(o => o.Method, StringComparer.Ordinal))
        {
            var operationId = $"{op.Method.ToUpperInvariant()} {op.Path}";
            var display = operationId;
            if (!ReadOnlyMethods.Contains(op.Method))
            {
                operations.Add(new() { TargetId = target.TargetId, OperationId = operationId, Display = display, Protocol = ApiFuzzProtocol.Rest, Method = op.Method, Classification = ApiFuzzSafetyClassification.UnsafeMethod, Reason = $"{op.Method} can change state; only GET, HEAD and OPTIONS are fuzzed." });
                continue;
            }
            if (ApiReviewEngine.IsAuthenticationFlowPath(op.Path))
            {
                operations.Add(new() { TargetId = target.TargetId, OperationId = operationId, Display = display, Protocol = ApiFuzzProtocol.Rest, Method = op.Method, Classification = ApiFuzzSafetyClassification.UnknownSafety, Reason = "Sign-in, token and session endpoints are never fuzzed." });
                continue;
            }
            var opCases = RestOperationCases(target, op, operationId, settings).Take(settings.MaxCasesPerOperation).ToList();
            operations.Add(new()
            {
                TargetId = target.TargetId, OperationId = operationId, Display = display, Protocol = ApiFuzzProtocol.Rest, Method = op.Method,
                Classification = opCases.Count > 0 ? ApiFuzzSafetyClassification.ReadOnlyEligible : ApiFuzzSafetyClassification.MissingContract,
                Reason = opCases.Count > 0 ? $"Read-only; {op.Parameters.Count} declared parameter(s)." : "Read-only, but the contract declares no parameter a contract or robustness case can be derived from.",
                CaseCount = opCases.Count,
            });
            cases.AddRange(opCases);
        }
        return new(operations, cases);
    }

    private static IEnumerable<ApiFuzzCase> RestOperationCases(ApiReviewTarget target, NormalizedOperation op, string operationId, ApiFuzzingSettings settings)
    {
        var security = settings.Level == ApiFuzzingLevel.SafeSecurityFuzzing;
        var parameters = op.Parameters.Where(p => !ApiSafeRequestGuard.IsForbiddenHeader(p.Name)).ToList();

        // Order: path, query, header; within a location, contract order. Mutations per parameter in a fixed order.
        foreach (var p in parameters.OrderBy(p => p.Location))
        {
            foreach (var (mutation, value, preview, behavior) in Mutations(p, settings, security))
                yield return Build(target, op, operationId, p, mutation, value, preview, behavior, parameters);
        }
        // One unexpected query parameter per operation (robustness; acceptance is not a defect).
        if (security)
            yield return Build(target, op, operationId, new NormalizedParameter { Name = UnexpectedParameterName, Location = NormalizedParameterLocation.Query, SourceRef = $"{OperationPointer(op)} (undeclared)" },
                ApiFuzzMutationType.UnexpectedParameter, "1", "undeclared query parameter", ApiFuzzExpectedBehavior.AcceptOrReject, parameters);
    }

    private static IEnumerable<(ApiFuzzMutationType Mutation, string? Value, string Preview, ApiFuzzExpectedBehavior Behavior)> Mutations(NormalizedParameter p, ApiFuzzingSettings settings, bool security)
    {
        const ApiFuzzExpectedBehavior reject = ApiFuzzExpectedBehavior.RejectWithClientError;
        var type = p.Type?.ToLowerInvariant();
        var format = p.Format?.ToLowerInvariant();
        var header = p.Location == NormalizedParameterLocation.Header;
        if (p.Required && p.Location != NormalizedParameterLocation.Path)
            yield return (header ? ApiFuzzMutationType.MissingRequiredHeader : ApiFuzzMutationType.MissingRequired, null, "omitted", reject);
        if (p.EnumValues is { Count: > 0 })
            yield return (header ? ApiFuzzMutationType.InvalidHeaderValue : ApiFuzzMutationType.InvalidEnum, InvalidEnum, $"not one of {p.EnumValues.Count} declared values", reject);
        if (format == "uuid")
            yield return (header ? ApiFuzzMutationType.InvalidHeaderValue : ApiFuzzMutationType.InvalidUuid, InvalidUuid, InvalidUuid, reject);
        if (format is "date" or "date-time")
            yield return (header ? ApiFuzzMutationType.InvalidHeaderValue : ApiFuzzMutationType.InvalidDate, format == "date" ? InvalidDate : InvalidDate + "T25:61:00Z", "impossible calendar date", reject);
        if (type is "integer" or "number")
        {
            if (!header) yield return (ApiFuzzMutationType.WrongType, "abc", "non-numeric text", reject);
            // Lower bound: "zero where invalid" (minimum 1, or exclusive 0), "negative where invalid" (minimum 0), otherwise just below it.
            if (p.Minimum is { } min)
            {
                if ((min == 1 && !p.ExclusiveMinimum) || (min == 0 && p.ExclusiveMinimum))
                    yield return (ApiFuzzMutationType.ZeroNotAllowed, "0", $"zero where the minimum is {(p.ExclusiveMinimum ? "> 0" : "1")}", reject);
                else if (min == 0)
                    yield return (ApiFuzzMutationType.NegativeNotAllowed, "-1", "negative where the minimum is 0", reject);
                else
                    yield return (ApiFuzzMutationType.NumericBelowMinimum, Number(p.ExclusiveMinimum ? min : min - 1, type), $"below declared minimum {Number(min, type)}", reject);
            }
            if (p.Maximum is { } max)
                yield return (ApiFuzzMutationType.NumericAboveMaximum, Number(p.ExclusiveMaximum ? max : max + 1, type), $"above declared maximum {Number(max, type)}", reject);
        }
        if (type == "boolean" && !header) yield return (ApiFuzzMutationType.WrongType, "maybe", "not a boolean", reject);
        if (type == "string" && p.MaxLength is { } maxLength && maxLength + 1 <= settings.MaxParameterLength)
            yield return (ApiFuzzMutationType.StringTooLong, new string('a', maxLength + 1), $"{maxLength + 1} characters (declared maxLength {maxLength})", reject);
        if (!security) yield break;
        if (type == "string" && (p.Required || p.MinLength is >= 1) && p.EnumValues is null && format is null)
            yield return (ApiFuzzMutationType.EmptyString, "", "empty string", p.MinLength is >= 1 ? reject : ApiFuzzExpectedBehavior.AcceptOrReject);
        if (type == "string" && p.MaxLength is null && p.EnumValues is null && format is null && !header && p.Location != NormalizedParameterLocation.Path)
            yield return (ApiFuzzMutationType.OversizedString, new string('a', settings.MaxParameterLength), $"{settings.MaxParameterLength} characters (bounded)", ApiFuzzExpectedBehavior.AcceptOrReject);
        if (header && type == "string")
            yield return (ApiFuzzMutationType.MalformedHeaderValue, "\"birknext-unterminated", "unterminated quoted header value", reject);
    }

    private static string Number(decimal value, string? type) =>
        type == "integer" ? decimal.Truncate(value).ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture);

    private static ApiFuzzCase Build(ApiReviewTarget target, NormalizedOperation op, string operationId, NormalizedParameter mutated, ApiFuzzMutationType mutation, string? value, string preview,
        ApiFuzzExpectedBehavior behavior, IReadOnlyList<NormalizedParameter> all)
    {
        var location = mutated.Location switch
        {
            NormalizedParameterLocation.Path => ApiFuzzParameterLocation.Path,
            NormalizedParameterLocation.Header => ApiFuzzParameterLocation.Header,
            _ => ApiFuzzParameterLocation.Query,
        };
        var path = op.Path;
        var query = new List<KeyValuePair<string, string>>();
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var p in all)
        {
            var isMutated = p.Name == mutated.Name && p.Location == mutated.Location;
            if (!isMutated && !p.Required) continue;
            var v = isMutated ? value : SyntheticValid(p);
            if (v is null) continue;   // omitted (missing-required) — or a required parameter without a derivable valid value
            switch (p.Location)
            {
                case NormalizedParameterLocation.Path: path = path.Replace("{" + p.Name + "}", Uri.EscapeDataString(v), StringComparison.Ordinal); break;
                case NormalizedParameterLocation.Header: headers.Add(new(p.Name, v)); break;
                default: query.Add(new(p.Name, v)); break;
            }
        }
        if (mutation == ApiFuzzMutationType.UnexpectedParameter) query.Add(new(mutated.Name, value!));
        return new ApiFuzzCase
        {
            CaseId = ApiFuzzCase.IdFor(target.TargetId, operationId, mutation, location, mutated.Name),
            Protocol = ApiFuzzProtocol.Rest, TargetId = target.TargetId, OperationId = operationId, OperationDisplay = operationId,
            MutationType = mutation, Parameter = mutated.Name, Location = location, SourceContractRef = mutated.SourceRef, Method = op.Method.ToUpperInvariant(),
            ExpectedBehavior = behavior, ValuePreview = preview, Path = path, Query = query, Headers = headers,
        };
    }

    /// <summary>A fixed valid value for a non-mutated required parameter, derived from its declared type/format/enum/bounds only.</summary>
    internal static string? SyntheticValid(NormalizedParameter p)
    {
        if (p.EnumValues is { Count: > 0 } values) return values[0];
        var format = p.Format?.ToLowerInvariant();
        if (format == "uuid") return SyntheticUuid;
        if (format == "date") return SyntheticDate;
        if (format == "date-time") return SyntheticDateTime;
        var type = p.Type?.ToLowerInvariant();
        if (type is "integer" or "number")
        {
            // The smallest value inside the declared bounds, preferring 1 when unbounded below.
            var value = p.Minimum is { } min ? (p.ExclusiveMinimum ? min + 1 : min) : 1m;
            if (type == "integer") value = decimal.Ceiling(value);
            if (p.Maximum is { } max && value > (p.ExclusiveMaximum ? max - 1 : max)) value = p.ExclusiveMaximum ? max - 1 : max;
            return Number(value, type);
        }
        return type switch
        {
            "boolean" => "true",
            _ => p.MinLength is { } minLength && minLength > SyntheticString.Length ? new string('a', minLength) : p.MaxLength is { } maxLength && maxLength < SyntheticString.Length ? new string('a', Math.Max(1, maxLength)) : SyntheticString,
        };
    }

    private static string OperationPointer(NormalizedOperation op) => $"#/paths/{op.Path.Replace("~", "~0").Replace("/", "~1")}/{op.Method.ToLowerInvariant()}";

    // ── GraphQL ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> BuiltInScalars = new(StringComparer.Ordinal) { "String", "Int", "Float", "Boolean", "ID" };

    /// <summary>
    /// Validation-level cases for the schema's query root fields (observed root fields first, then alphabetical). Every case is a document the
    /// server must reject during validation — before any resolver runs. Mutations and subscriptions are listed and never get a case.
    /// </summary>
    public static GenerationResult GraphQl(ApiReviewTarget target, GraphQlNormalizedContract schema, ApiFuzzingSettings settings, IReadOnlyCollection<string> observedRootFields)
    {
        var operations = new List<ApiFuzzOperationEligibility>();
        var cases = new List<ApiFuzzCase>();
        var security = settings.Level == ApiFuzzingLevel.SafeSecurityFuzzing;
        var types = schema.Types.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var roots = RootOperations(schema, types);

        foreach (var op in roots.Where(o => o.Kind is "mutation" or "subscription").OrderBy(o => o.RootField, StringComparer.Ordinal))
            operations.Add(new()
            {
                TargetId = target.TargetId, OperationId = $"{op.Kind} {op.RootField}", Display = $"{op.Kind} {op.RootField}", Protocol = ApiFuzzProtocol.GraphQl, Method = "POST",
                Classification = op.Kind == "mutation" ? ApiFuzzSafetyClassification.Mutation : ApiFuzzSafetyClassification.UnknownSafety,
                Reason = op.Kind == "mutation" ? "GraphQL mutations are never sent." : "Subscriptions are not fuzzed.",
            });

        // Endpoint-level cases (one per target): unknown field; malformed syntax at the security level.
        var endpointId = "query (endpoint)";
        var endpointCases = new List<ApiFuzzCase>
        {
            GraphQlCase(target, endpointId, endpointId, ApiFuzzMutationType.GraphQlUnknownField, "__birknextFuzzUnknownField", ApiFuzzParameterLocation.GraphQlField, $"{schema.QueryTypeName ?? "Query"}.__birknextFuzzUnknownField",
                "query { __birknextFuzzUnknownField }", "field not in the schema"),
        };
        if (security)
            endpointCases.Add(GraphQlCase(target, endpointId, endpointId, ApiFuzzMutationType.GraphQlMalformedSyntax, "(document)", ApiFuzzParameterLocation.GraphQlDocument, "GraphQL syntax",
                "query { __typename ", "unterminated selection set", syntaxError: true));
        operations.Add(new() { TargetId = target.TargetId, OperationId = endpointId, Display = "Endpoint (schema-independent)", Protocol = ApiFuzzProtocol.GraphQl, Method = "POST", Classification = ApiFuzzSafetyClassification.ReadOnlyEligible, Reason = "Validation-level documents only; no resolver runs.", CaseCount = endpointCases.Count });
        cases.AddRange(endpointCases);

        var observed = observedRootFields.ToHashSet(StringComparer.Ordinal);
        foreach (var op in roots.Where(o => o.Kind == "query" && !o.RootField.StartsWith("__", StringComparison.Ordinal))
                     .OrderBy(o => observed.Contains(o.RootField) ? 0 : 1).ThenBy(o => o.RootField, StringComparer.Ordinal))
        {
            var operationId = $"query {op.RootField}";
            var opCases = GraphQlFieldCases(target, op, operationId, types).Take(settings.MaxCasesPerOperation).ToList();
            operations.Add(new()
            {
                TargetId = target.TargetId, OperationId = operationId, Display = operationId, Protocol = ApiFuzzProtocol.GraphQl, Method = "POST",
                Classification = opCases.Count > 0 ? ApiFuzzSafetyClassification.ReadOnlyEligible : ApiFuzzSafetyClassification.MissingContract,
                Reason = opCases.Count > 0 ? $"Query field; {op.Arguments.Count} argument(s){(observed.Contains(op.RootField) ? "; observed in frontend traffic" : "")}." : "Query field without arguments: no argument case can be derived (covered by the endpoint cases).",
                CaseCount = opCases.Count,
            });
            cases.AddRange(opCases);
        }
        return new(operations, cases);
    }

    /// <summary>
    /// Root fields from the root operation types (query/mutation/subscription type names as the schema declares them) — the same for an
    /// introspection result and an SDL artifact. Falls back to the extractor's operation list when the root type is not among the types.
    /// </summary>
    private static List<GraphQlOperation> RootOperations(GraphQlNormalizedContract schema, IReadOnlyDictionary<string, GraphQlType> types)
    {
        var roots = new List<GraphQlOperation>();
        foreach (var (kind, typeName) in new[] { ("query", schema.QueryTypeName ?? "Query"), ("mutation", schema.MutationTypeName), ("subscription", schema.SubscriptionTypeName) })
        {
            if (typeName is null || !types.TryGetValue(typeName, out var rootType)) continue;
            roots.AddRange(rootType.Fields.Select(f => new GraphQlOperation { Kind = kind, Name = f.Name, RootType = typeName, RootField = f.Name, Arguments = f.Arguments, ReturnType = f.Type }));
        }
        return roots.Count > 0 ? roots : schema.Operations;
    }

    private static IEnumerable<ApiFuzzCase> GraphQlFieldCases(ApiReviewTarget target, GraphQlOperation op, string operationId, IReadOnlyDictionary<string, GraphQlType> types)
    {
        var selection = Selection(op.ReturnType, types);
        var args = op.Arguments.Where(a => a.Type is not null).ToList();
        string Doc(GraphQlArgument? mutated, string? literal)
        {
            var parts = new List<string>();
            foreach (var a in args)
            {
                var (typeName, nonNull, listDepth) = a.Type!.Unwrap();
                var isMutated = ReferenceEquals(a, mutated);
                if (isMutated) { if (literal is not null) parts.Add($"{a.Name}: {literal}"); continue; }
                if (!nonNull || a.DefaultValue is not null) continue;
                if (ValidLiteral(typeName, listDepth, types) is { } valid) parts.Add($"{a.Name}: {valid}");
            }
            return $"query {{ {op.RootField}{(parts.Count > 0 ? "(" + string.Join(", ", parts) + ")" : "")}{selection} }}";
        }
        foreach (var a in args)
        {
            var (typeName, nonNull, listDepth) = a.Type!.Unwrap();
            var coordinate = $"Query.{op.RootField}({a.Name}:)";
            if (nonNull && a.DefaultValue is null)
            {
                yield return GraphQlCase(target, operationId, op.RootField, ApiFuzzMutationType.GraphQlMissingRequiredArgument, a.Name, ApiFuzzParameterLocation.GraphQlArgument, coordinate, Doc(a, null), "required argument omitted");
                yield return GraphQlCase(target, operationId, op.RootField, ApiFuzzMutationType.GraphQlNullForNonNull, a.Name, ApiFuzzParameterLocation.GraphQlArgument, coordinate, Doc(a, "null"), "null for a non-null argument");
            }
            if (listDepth > 0) continue;
            if (types.TryGetValue(typeName, out var t) && t.Kind == "ENUM")
                yield return GraphQlCase(target, operationId, op.RootField, ApiFuzzMutationType.GraphQlInvalidEnum, a.Name, ApiFuzzParameterLocation.GraphQlArgument, coordinate, Doc(a, InvalidEnum), "value not in the enum");
            else if (WrongScalarLiteral(typeName) is { } wrong)
                yield return GraphQlCase(target, operationId, op.RootField, ApiFuzzMutationType.GraphQlWrongScalarType, a.Name, ApiFuzzParameterLocation.GraphQlArgument, coordinate, Doc(a, wrong.Literal), wrong.Preview);
        }
    }

    /// <summary>A literal the declared scalar cannot coerce (validation error). Custom scalars: only the well-known UUID form.</summary>
    private static (string Literal, string Preview)? WrongScalarLiteral(string scalar) => scalar switch
    {
        "Int" => ("\"not-an-int\"", "string for Int"),
        "Float" => ("\"not-a-float\"", "string for Float"),
        "Boolean" => ("\"yes\"", "string for Boolean"),
        "String" => ("12345", "number for String"),
        "ID" => ("true", "boolean for ID"),
        "UUID" or "Uuid" or "Guid" => ($"\"{InvalidUuid}\"", "invalid UUID text"),
        _ => null,
    };

    private static string? ValidLiteral(string typeName, int listDepth, IReadOnlyDictionary<string, GraphQlType> types)
    {
        string? scalar = typeName switch
        {
            "Int" => "1", "Float" => "1.0", "Boolean" => "true", "String" => $"\"{SyntheticString}\"", "ID" => $"\"{SyntheticString}\"",
            "UUID" or "Uuid" or "Guid" => $"\"{SyntheticUuid}\"", "Date" => $"\"{SyntheticDate}\"", "DateTime" => $"\"{SyntheticDateTime}\"",
            _ => types.TryGetValue(typeName, out var t) && t.Kind == "ENUM" && t.EnumValues is { Count: > 0 } values ? values[0].Name : null,
        };
        return scalar is null ? null : listDepth > 0 ? $"[{scalar}]" : scalar;
    }

    /// <summary>Minimal selection: none for scalars/enums, <c>{ __typename }</c> for objects — the smallest valid sub-selection, no data fields.</summary>
    private static string Selection(GraphQlTypeRef? returnType, IReadOnlyDictionary<string, GraphQlType> types)
    {
        if (returnType is null) return "";
        var (name, _, _) = returnType.Unwrap();
        if (BuiltInScalars.Contains(name)) return "";
        return types.TryGetValue(name, out var t) && t.Kind is "SCALAR" or "ENUM" ? "" : " { __typename }";
    }

    private static ApiFuzzCase GraphQlCase(ApiReviewTarget target, string operationId, string display, ApiFuzzMutationType mutation, string parameter, ApiFuzzParameterLocation location,
        string coordinate, string query, string preview, bool syntaxError = false) => new()
    {
        CaseId = ApiFuzzCase.IdFor(target.TargetId, operationId, mutation, location, parameter),
        Protocol = ApiFuzzProtocol.GraphQl, TargetId = target.TargetId, OperationId = operationId, OperationDisplay = operationId.StartsWith("query (", StringComparison.Ordinal) ? "Endpoint" : $"query {display}",
        MutationType = mutation, Parameter = parameter, Location = location, SourceContractRef = coordinate, Method = "POST",
        ExpectedBehavior = ApiFuzzExpectedBehavior.RejectWithGraphQlError, ValuePreview = preview, Path = target.BasePath, GraphQlQuery = query, ExpectSyntaxError = syntaxError,
    };
}
