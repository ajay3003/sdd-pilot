using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.ApiReview;
using BirkNext.RuntimeSecurity;

namespace BirkNext.Api.Services.ApiQuality.Fuzzing;

/// <summary>
/// A server-side cleanup contract for state-changing body cases: precondition, an execution identifier, cleanup and cleanup verification.
/// None is registered, and state-changing body cases are not executed in this milestone even when one is (the contract exists so the
/// boundary is explicit; read-only opt-ins are the only executable body cases).
/// </summary>
public interface IRequestCleanupStrategy
{
    string Id { get; }
    Task<bool> PreconditionAsync(string operation, CancellationToken ct);
    Task CleanupAsync(string executionId, CancellationToken ct);
    Task<bool> VerifyCleanupAsync(string executionId, CancellationToken ct);
}

/// <summary>
/// Deterministic request-body cases for EXPLICITLY opted-in operations. Each case starts from a synthetic valid body (required fields
/// only, values derived from declared type/format/enum/bounds) and changes exactly one thing: one field, the JSON syntax or the
/// Content-Type. No nesting is generated, values are bounded, no attack dictionaries. Unknown operations and DELETE never get a case.
/// </summary>
public static class ApiBodyFuzzCaseGenerator
{
    internal const string UnknownFieldName = "birknextUnexpectedField";
    private static readonly HashSet<string> BodyMethods = new(StringComparer.OrdinalIgnoreCase) { "POST", "PUT", "PATCH" };

    public static ApiFuzzCaseGenerator.GenerationResult Rest(ApiReviewTarget target, NormalizedContract contract, ApiFuzzingSettings settings,
        IReadOnlyCollection<BodyFuzzOperationOptIn> optIns, IReadOnlyCollection<string> registeredCleanupStrategies)
    {
        var operations = new List<ApiFuzzOperationEligibility>();
        var cases = new List<ApiFuzzCase>();
        var byKey = optIns.GroupBy(o => o.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        foreach (var op in contract.Operations.Where(o => BodyMethods.Contains(o.Method) || string.Equals(o.Method, "DELETE", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(o => o.Path, StringComparer.Ordinal).ThenBy(o => o.Method, StringComparer.Ordinal))
        {
            var method = op.Method.ToUpperInvariant();
            var operationId = $"{method} {op.Path}";
            ApiFuzzOperationEligibility Row(ApiFuzzSafetyClassification classification, string reason, int count = 0) => new()
            {
                TargetId = target.TargetId, OperationId = operationId + " (body)", Display = operationId + " · request body", Protocol = ApiFuzzProtocol.Rest, Method = method,
                Classification = classification, Reason = reason, CaseCount = count,
            };
            if (method == "DELETE") { operations.Add(Row(ApiFuzzSafetyClassification.UnsafeMethod, "DELETE is never fuzzed.")); continue; }
            if (ApiReviewEngine.IsAuthenticationFlowPath(op.Path)) { operations.Add(Row(ApiFuzzSafetyClassification.UnknownSafety, "Sign-in, token and session endpoints are never fuzzed.")); continue; }
            if (!byKey.TryGetValue(operationId, out var optIn) || optIn.Policy is BodyFuzzingPolicy.Disabled or BodyFuzzingPolicy.NotAllowed)
            {
                operations.Add(Row(ApiFuzzSafetyClassification.BodyFuzzNotOptedIn, optIn?.Policy == BodyFuzzingPolicy.NotAllowed
                    ? "Body fuzzing is explicitly not allowed for this operation."
                    : "No body-fuzzing opt-in for this operation (Security Expectations → Body fuzzing); unknown operation safety is blocked."));
                continue;
            }
            if (optIn.Policy == BodyFuzzingPolicy.StateChangingWithCleanup)
            {
                operations.Add(Row(ApiFuzzSafetyClassification.StateChangingWithoutCleanup,
                    optIn.CleanupStrategyId is { Length: > 0 } id && registeredCleanupStrategies.Contains(id, StringComparer.Ordinal)
                        ? $"Cleanup strategy '{id}' is registered, but state-changing body fuzzing is not executed in this milestone."
                        : "State-changing body fuzzing needs a registered cleanup strategy (precondition, execution id, cleanup, verification); none is registered, so it is blocked."));
                continue;
            }
            if (op.RequestBody is not { Fields.Count: > 0 } body)
            {
                operations.Add(Row(ApiFuzzSafetyClassification.NoRequestBodySchema, "Opted in, but the contract declares no locally resolvable JSON object body to derive cases from."));
                continue;
            }
            var opCases = Interleave(AllCases(target, op, operationId, body, settings)).Take(settings.MaxCasesPerOperation).ToList();
            operations.Add(Row(opCases.Count > 0 ? ApiFuzzSafetyClassification.ReadOnlyEligible : ApiFuzzSafetyClassification.NoRequestBodySchema,
                opCases.Count > 0 ? $"Opted in as read-only body safe; {body.Fields.Count} declared body field(s)." : "No case fits the body limits.", opCases.Count));
            cases.AddRange(opCases);
        }
        return new(operations, cases);
    }

    /// <summary>Mutation types in the order a capped run covers them: one case of each type first, then the next case of each type.</summary>
    internal static readonly ApiFuzzMutationType[] Priority =
    [
        ApiFuzzMutationType.BodyMalformedJson, ApiFuzzMutationType.BodyMissingRequiredField, ApiFuzzMutationType.BodyWrongType, ApiFuzzMutationType.BodyWrongContentType,
        ApiFuzzMutationType.BodyInvalidEnum, ApiFuzzMutationType.BodyMissingContentType, ApiFuzzMutationType.BodyNullForNonNull, ApiFuzzMutationType.BodyNumericAboveMaximum,
        ApiFuzzMutationType.BodyInvalidUuid, ApiFuzzMutationType.BodyInvalidDate, ApiFuzzMutationType.BodyNumericBelowMinimum, ApiFuzzMutationType.BodyStringTooLong,
        ApiFuzzMutationType.BodyEmptyString, ApiFuzzMutationType.BodyUnknownField,
    ];

    /// <summary>Every body case of the operation within the byte limits, before the per-operation cap (deterministic order).</summary>
    internal static List<ApiFuzzCase> AllCases(ApiReviewTarget target, NormalizedOperation op, string operationId, NormalizedRequestBody body, ApiFuzzingSettings settings) =>
        Cases(target, op, operationId, body, settings).Where(c => c.Body is not null).ToList();

    /// <summary>Round-robin over <see cref="Priority"/> so the per-operation cap keeps the most distinct mutation types.</summary>
    internal static IEnumerable<ApiFuzzCase> Interleave(IReadOnlyList<ApiFuzzCase> cases)
    {
        var queues = Priority.Select(type => new Queue<ApiFuzzCase>(cases.Where(c => c.MutationType == type))).ToList();
        while (queues.Any(q => q.Count > 0))
            foreach (var queue in queues.Where(q => q.Count > 0))
                yield return queue.Dequeue();
    }

    private static IEnumerable<ApiFuzzCase> Cases(ApiReviewTarget target, NormalizedOperation op, string operationId, NormalizedRequestBody body, ApiFuzzingSettings settings)
    {
        const ApiFuzzExpectedBehavior reject = ApiFuzzExpectedBehavior.RejectWithClientError;
        var fields = body.Fields.Take(ApiFuzzingLimits.MaxBodyFields).ToList();
        var valid = new JsonObject();
        foreach (var f in fields.Where(f => f.Required)) valid[f.Name] = ValidNode(f);
        var maxString = Math.Min(settings.MaxParameterLength, settings.MaxBodyBytes / 2);

        // Field cases in contract order; each mutation in a fixed order.
        foreach (var f in fields)
        {
            var type = f.Type?.ToLowerInvariant();
            var format = f.Format?.ToLowerInvariant();
            if (f.Required) yield return Case(ApiFuzzMutationType.BodyMissingRequiredField, f, Without(valid, f.Name), "required field omitted", reject);
            if (!f.Nullable) yield return Case(ApiFuzzMutationType.BodyNullForNonNull, f, With(valid, f.Name, null), "null for a non-nullable field", reject);
            if (type is "integer" or "number" or "boolean" or "array" or "object")
                yield return Case(ApiFuzzMutationType.BodyWrongType, f, With(valid, f.Name, JsonValue.Create("birknext-wrong-type")), $"string where {type} is declared", reject);
            else if (type == "string")
                yield return Case(ApiFuzzMutationType.BodyWrongType, f, With(valid, f.Name, JsonValue.Create(12345)), "number where string is declared", reject);
            if (f.EnumValues is { Count: > 0 })
                yield return Case(ApiFuzzMutationType.BodyInvalidEnum, f, With(valid, f.Name, JsonValue.Create(ApiFuzzCaseGenerator.InvalidEnum)), $"not one of {f.EnumValues.Count} declared values", reject);
            if (format == "uuid")
                yield return Case(ApiFuzzMutationType.BodyInvalidUuid, f, With(valid, f.Name, JsonValue.Create(ApiFuzzCaseGenerator.InvalidUuid)), ApiFuzzCaseGenerator.InvalidUuid, reject);
            if (format is "date" or "date-time")
                yield return Case(ApiFuzzMutationType.BodyInvalidDate, f, With(valid, f.Name, JsonValue.Create(ApiFuzzCaseGenerator.InvalidDate)), "impossible calendar date", reject);
            if (type is "integer" or "number")
            {
                if (f.Minimum is { } min) yield return Case(ApiFuzzMutationType.BodyNumericBelowMinimum, f, With(valid, f.Name, JsonValue.Create(f.ExclusiveMinimum ? min : min - 1)), $"below declared minimum {min.ToString(CultureInfo.InvariantCulture)}", reject);
                if (f.Maximum is { } max) yield return Case(ApiFuzzMutationType.BodyNumericAboveMaximum, f, With(valid, f.Name, JsonValue.Create(f.ExclusiveMaximum ? max : max + 1)), $"above declared maximum {max.ToString(CultureInfo.InvariantCulture)}", reject);
            }
            if (type == "string" && f.MaxLength is { } maxLength && maxLength + 1 <= maxString)
                yield return Case(ApiFuzzMutationType.BodyStringTooLong, f, With(valid, f.Name, JsonValue.Create(new string('a', maxLength + 1))), $"{maxLength + 1} characters (declared maxLength {maxLength})", reject);
            if (type == "string" && f.Required && (f.MinLength is >= 1) && f.EnumValues is null && format is null)
                yield return Case(ApiFuzzMutationType.BodyEmptyString, f, With(valid, f.Name, JsonValue.Create("")), "empty string where minLength ≥ 1", reject);
        }
        // Whole-body cases.
        var unknown = With(valid, UnknownFieldName, JsonValue.Create(1));
        yield return Case(ApiFuzzMutationType.BodyUnknownField, new NormalizedParameter { Name = UnknownFieldName, SourceRef = body.SourceRef + " (undeclared)" }, unknown, "unknown extra field",
            body.AllowsAdditionalProperties == false ? reject : ApiFuzzExpectedBehavior.AcceptOrReject);
        yield return Build(ApiFuzzMutationType.BodyMalformedJson, "(body)", body.SourceRef, "{\"birknext\": ", "malformed JSON (truncated object)", reject, "application/json");
        yield return Build(ApiFuzzMutationType.BodyWrongContentType, "(content-type)", body.SourceRef, valid.ToJsonString(), "valid body sent as text/plain", reject, "text/plain");
        yield return Build(ApiFuzzMutationType.BodyMissingContentType, "(content-type)", body.SourceRef, valid.ToJsonString(), "valid body without Content-Type", reject, null);

        ApiFuzzCase Case(ApiFuzzMutationType mutation, NormalizedParameter field, JsonObject document, string preview, ApiFuzzExpectedBehavior behavior) =>
            Build(mutation, field.Name, field.SourceRef, document.ToJsonString(), preview, behavior, "application/json");

        ApiFuzzCase Build(ApiFuzzMutationType mutation, string parameter, string sourceRef, string text, string preview, ApiFuzzExpectedBehavior behavior, string? contentType) => new()
        {
            CaseId = ApiFuzzCase.IdFor(target.TargetId, operationId, mutation, ApiFuzzParameterLocation.Body, parameter),
            Protocol = ApiFuzzProtocol.Rest, TargetId = target.TargetId, OperationId = operationId + " (body)", OperationDisplay = operationId + " · request body",
            MutationType = mutation, Parameter = parameter, Location = ApiFuzzParameterLocation.Body, SourceContractRef = sourceRef, Method = op.Method.ToUpperInvariant(),
            ExpectedBehavior = behavior, ValuePreview = preview, Path = op.Path, Body = Encoding.UTF8.GetByteCount(text) <= settings.MaxBodyBytes ? text : null,
            ContentType = contentType, OmitContentType = contentType is null, BodyPolicy = BodyFuzzingPolicy.ReadOnlyBodySafe,
        };
    }

    private static JsonObject Without(JsonObject valid, string name) { var copy = (JsonObject)valid.DeepClone(); copy.Remove(name); return copy; }

    private static JsonObject With(JsonObject valid, string name, JsonNode? value) { var copy = (JsonObject)valid.DeepClone(); copy[name] = value; return copy; }

    /// <summary>A fixed valid value from the declared constraints (same derivation as parameter cases, typed for JSON).</summary>
    internal static JsonNode? ValidNode(NormalizedParameter f)
    {
        var type = f.Type?.ToLowerInvariant();
        var text = ApiFuzzCaseGenerator.SyntheticValid(f);
        return type switch
        {
            "integer" => JsonValue.Create(long.Parse(text ?? "1", CultureInfo.InvariantCulture)),
            "number" => JsonValue.Create(decimal.Parse(text ?? "1", CultureInfo.InvariantCulture)),
            "boolean" => JsonValue.Create(true),
            "array" => new JsonArray(),
            "object" => new JsonObject(),
            _ => JsonValue.Create(text ?? ApiFuzzCaseGenerator.SyntheticString),
        };
    }
}
