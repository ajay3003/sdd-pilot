using System.Text.Json.Serialization;
using BirkNext.ContractSchemaEvidence;
using BirkNext.DataModelEvidence;
using BirkNext.SourceDomains;

namespace BirkNext.TestData;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Shared reusable Test Data lifecycle. Candidate providers (contract evidence, data model evidence, Schema Studio generation, imported
// fixtures, manual entries) propose structure — never values for identities, never secrets — and a candidate is never approved test data:
// approval is a separate, explicit step owned by the Test Data catalog. Approved assets are versioned by content fingerprint; executions keep
// the exact version they used; environment-scoped assets are never reused elsewhere; a source change marks affected assets for review and
// structural revalidation — it never edits them. Expected results stay apart from actual results.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataLifecycleState { Candidate, Generated, NeedsReview, Approved, Registered, InUse, ReviewRequired, PotentiallyStale, Deprecated, Archived }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataCandidateOrigin { ContractEvidence, DataModelEvidence, SchemaStudio, ApiExample, ImportedFixture, Manual, EnvironmentReference }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataIntent { Positive, Negative, ExpectedResult }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataReadinessState { Ready, CandidateOnly, Missing, Stale, EnvironmentMismatch }

/// <summary>A proposed asset: structure and rules with provenance. Never approved by creation; never contains identities or secrets.</summary>
public sealed record TestDataCandidate(string Id, TestDataCandidateOrigin Origin, string Label, string MediaType, TestDataIntent Intent, string? Environment,
    IReadOnlyList<string> Structure, IReadOnlyList<string> Rules, string DerivedFrom, string? ExpectedOutcome, TestDataLifecycleState State = TestDataLifecycleState.Candidate)
{
    /// <summary>Why it was proposed ("Proposed because OpenAPI operation POST /api/person requires CreatePersonRequest.").</summary>
    public string Reason { get; init; } = "";
    /// <summary>The suggested asset data type; the reviewer confirms or changes it when registering.</summary>
    public TestDataDataType DataType { get; init; } = TestDataDataType.JsonPayload;
    /// <summary>The contract/schema element it came from (schema, operation, input type, entity, enum).</summary>
    public string? SchemaRef { get; init; }
    /// <summary>A registered Test Data asset that already represents this candidate (no duplicate is registered).</summary>
    public string? ExistingAssetId { get; init; }
    /// <summary>Structure fingerprint: the same candidate re-derived from a newer snapshot with another fingerprint means the contract changed.</summary>
    public string StructureFingerprint => TestDataCatalogRules.Fingerprint(string.Join("\n", Structure));
    public string Status => ExistingAssetId is null ? "Proposed from source evidence" : "Existing Test Data asset";
}

/// <summary>Which asset version an execution used (alias + content fingerprint) — kept with the run; never a value.</summary>
public sealed record TestDataUsage(string Alias, string? Fingerprint, string? Environment, string Source);

public sealed record TestDataReadiness(string Role, TestDataReadinessState State, string Text);

public sealed record TestDataImpact(string Alias, TestDataLifecycleState State, string Reason);

public static class TestDataLifecycle
{
    public const string ControlledReference = "<controlled identity reference — never generated>";

    /// <summary>Structural candidates from a contract's normalized constraints: one valid case and, per required field, a negative
    /// "missing required field" case with its expected rejection. Identity-like fields get a controlled reference placeholder.</summary>
    public static IReadOnlyList<TestDataCandidate> FromContract(ContractFact fact)
    {
        var constraints = ContractEvidenceQueries.GetConstraints(fact);
        if (constraints.Count == 0 && fact.EntryPoint.State != ContractEntryPointState.SingleMessageRoot) return [];
        var media = fact.Contract.Type == BirkNext.SourceDomains.SourceContractType.XmlSchema ? "application/xml" : "application/json";
        var fields = ContractEvidenceQueries.GetFields(fact);
        var structure = fields.Take(60).Select(f => $"{f.Path}{(IsIdentity(f.Name) ? $" = {ControlledReference}" : "")}").ToList();
        var rules = constraints.Select(c => $"{c.Path}: {c.PlainText}").Take(60).ToList();
        var xml = fact.Contract.Type == BirkNext.SourceDomains.SourceContractType.XmlSchema;
        var dataType = xml ? TestDataDataType.XmlPayload : TestDataDataType.JsonPayload;
        var reason = fact.Contract.Type switch
        {
            BirkNext.SourceDomains.SourceContractType.XmlSchema => $"Proposed from XSD entry point {fact.Name} ({fact.Contract.File}).",
            BirkNext.SourceDomains.SourceContractType.OpenApi => $"Proposed from OpenAPI schema {fact.Name} ({fact.Contract.File}).",
            _ => $"Proposed from JSON Schema {fact.Name} ({fact.Contract.File}).",
        };
        var result = new List<TestDataCandidate>
        {
            new($"contract:{fact.Id}:valid", TestDataCandidateOrigin.ContractEvidence, $"{fact.Name} — valid structure", media, TestDataIntent.Positive, null, structure, rules,
                $"{fact.Contract.File} (snapshot {fact.Provenance.SnapshotId:D})", "Accepted") { Reason = reason, DataType = dataType, SchemaRef = fact.Contract.File },
        };
        foreach (var required in constraints.Where(c => c.Kind == "Required").Take(10))
            result.Add(new($"contract:{fact.Id}:missing:{required.Path}", TestDataCandidateOrigin.ContractEvidence, $"{fact.Name} — missing {required.Path}", media, TestDataIntent.Negative, null,
                [.. structure.Where(s => !s.StartsWith(required.Path, StringComparison.Ordinal))], [$"{required.Path}: omitted on purpose"],
                $"{fact.Contract.File} (snapshot {fact.Provenance.SnapshotId:D})", "Rejected (schema validation) — a rejection is the expected, passing outcome")
            {
                Reason = $"Negative case: {required.Path} is required by {fact.Name}; omitting it should be rejected.", DataType = dataType, SchemaRef = fact.Contract.File,
            });
        return result;
    }

    /// <summary>A structural candidate per data-model entity (columns + constraints), without values.</summary>
    public static IReadOnlyList<TestDataCandidate> FromDataModel(IEnumerable<DataEntity> entities) =>
        [.. entities.Where(e => e.Fields.Count > 0).Select(e => new TestDataCandidate($"data:{e.Id}", TestDataCandidateOrigin.DataModelEvidence, $"{e.Entity ?? e.Table} — row structure",
            "application/json", TestDataIntent.Positive, null, [.. e.Fields.Select(f => $"{f.Property ?? f.Column}{(IsIdentity(f.Column) ? $" = {ControlledReference}" : "")}")],
            DataModelQueries.Constraints(e), $"{e.Store}/{e.Table} ({e.Mapping})", null)
        {
            Reason = $"Proposed from data model entity {e.Entity ?? e.Table} (table {e.Table}).", DataType = TestDataDataType.JsonPayload, SchemaRef = $"{e.Store}/{e.Table}",
        })];

    /// <summary>
    /// Requirements from declared operations: an OpenAPI request body (JSON payload), path and required query parameters (simple values), GraphQL
    /// input arguments (JSON payload) or scalar arguments (simple values), and GraphQL result types (expected-result shape — never a pass
    /// criterion by itself). Names and declared types only; never an example value.
    /// </summary>
    public static IReadOnlyList<TestDataCandidate> FromOperations(SourceContract contract, Guid snapshotId)
    {
        var result = new List<TestDataCandidate>();
        var graphQl = contract.Type == BirkNext.SourceDomains.SourceContractType.GraphQlSchema;
        var from = $"{contract.File} (snapshot {snapshotId:D})";
        List<string> FieldsOf(string? type) => contract.Types.FirstOrDefault(t => string.Equals(t.Name, type?.Trim('!', '[', ']'), StringComparison.Ordinal)) is { } shape
            ? [.. shape.Fields.Take(60).Select(f => $"{f.Name}: {f.Type}{(f.Required ? " (required)" : "")}{(IsIdentity(f.Name) ? $" = {ControlledReference}" : "")}")] : [];
        foreach (var op in contract.Operations.Where(o => o.Detailed).Take(80))
        {
            var key = $"op:{contract.Id}:{op.Name}";
            if (!graphQl && op.RequestSchema is { Length: > 0 } body)
                result.Add(new($"{key}:body", TestDataCandidateOrigin.ContractEvidence, $"{body} request body", op.RequestContentTypes.FirstOrDefault() ?? "application/json",
                    TestDataIntent.Positive, null, FieldsOf(body), [], from, null)
                {
                    Reason = $"Proposed because OpenAPI operation {op.Name} requires {body}.", DataType = TestDataDataType.JsonPayload, SchemaRef = $"{contract.File}#{body}",
                });
            foreach (var p in op.ParameterDetails.Where(p => graphQl || p.In == "path" || p.In == "query" && p.Required))
            {
                var inputType = graphQl && p.Type is { } t && contract.Types.Any(s => s.Name == t.Trim('!', '[', ']') && s.Kind.Contains("input", StringComparison.OrdinalIgnoreCase));
                result.Add(new($"{key}:param:{p.Name}", TestDataCandidateOrigin.ContractEvidence, inputType ? $"{p.Type!.Trim('!', '[', ']')} input" : $"{p.Name} parameter",
                    inputType ? "application/json" : "text/plain", TestDataIntent.Positive, null,
                    inputType ? FieldsOf(p.Type) : [$"{p.Name}: {p.Type ?? "string"}{(IsIdentity(p.Name) ? $" = {ControlledReference}" : "")}"], [], from, null)
                {
                    Reason = graphQl
                        ? inputType ? $"Proposed because GraphQL {op.Name} requires {p.Type!.Trim('!', '[', ']')}." : $"Proposed because GraphQL {op.Name} needs variable ${p.Name}."
                        : $"Proposed because OpenAPI operation {op.Name} needs {(p.In == "path" ? "path" : "required query")} parameter {p.Name}.",
                    DataType = inputType ? TestDataDataType.JsonPayload : TestDataDataType.SimpleValues, SchemaRef = inputType ? $"{contract.File}#{p.Type!.Trim('!', '[', ']')}" : $"{contract.File}#{op.Name}:{p.Name}",
                });
            }
            if (graphQl && op.ResultType is { Length: > 0 } resultType && FieldsOf(resultType) is { Count: > 0 } resultFields)
                result.Add(new($"{key}:result", TestDataCandidateOrigin.ContractEvidence, $"{resultType.Trim('!', '[', ']')} expected result", "application/json", TestDataIntent.ExpectedResult, null,
                    resultFields, [], from, "Expected shape only — the consumer decides how it is used")
                {
                    Reason = $"Proposed from GraphQL {op.Name} result type {resultType.Trim('!', '[', ']')} (expected-result shape, never a pass criterion by itself).",
                    DataType = TestDataDataType.ExpectedResult, SchemaRef = $"{contract.File}#{resultType.Trim('!', '[', ']')}",
                });
        }
        return result;
    }

    /// <summary>Reference data from declared enums / status lists: the declared member names (never business values).</summary>
    public static IReadOnlyList<TestDataCandidate> FromEnums(SourceContract contract, Guid snapshotId) =>
        [.. contract.Types.Where(t => t.Values.Count > 0).Take(40).Select(t => new TestDataCandidate($"enum:{contract.Id}:{t.Name}", TestDataCandidateOrigin.ContractEvidence,
            $"{t.Name} reference values", "application/json", TestDataIntent.ExpectedResult, null, [.. t.Values.Take(60)], [], $"{contract.File} (snapshot {snapshotId:D})", null)
        {
            Reason = $"Proposed from enum/status reference data {t.Name} ({t.Values.Count} declared values).", DataType = TestDataDataType.ExpectedResult, SchemaRef = $"{contract.File}#{t.Name}",
        })];

    /// <summary>Identity-like fields (national identity numbers, D-numbers, personal ids): never generated, only controlled references.</summary>
    public static bool IsIdentity(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("fodselsnummer") || n.Contains("fødselsnummer") || n.Contains("personnummer") || n.Contains("dnummer") || n.Contains("d-nummer") || n.Contains("ssn")
            || n.Contains("nationalid") || n.Contains("personident") || n is "fnr" or "pid";
    }

    /// <summary>An environment-scoped asset is usable only in its environment; an unscoped asset is usable anywhere only when allowed. A null
    /// environment means the consumer has none selected: nothing is reused across environments, so only the asset's own scope matters.</summary>
    public static bool UsableIn(TestDataAssetSummary asset, string? environment, bool allowEnvironmentAgnostic) =>
        asset.Environment is null ? allowEnvironmentAgnostic : environment is null || string.Equals(asset.Environment, environment, StringComparison.OrdinalIgnoreCase);

    /// <summary>The version an execution used: alias + content fingerprint (the asset's identity at run time).</summary>
    public static TestDataUsage Usage(TestDataAssetSummary asset) => new(asset.Alias, asset.Fingerprint, asset.Environment, asset.Source);

    public static TestDataLifecycleState State(TestDataAssetSummary asset) => asset.Approval switch
    {
        TestDataApproval.Approved when asset.Validation == TestDataValidation.Invalid && !asset.Capabilities.Contains("negative-test") => TestDataLifecycleState.ReviewRequired,
        TestDataApproval.Approved => TestDataLifecycleState.Approved,
        TestDataApproval.Expired => TestDataLifecycleState.Deprecated,
        TestDataApproval.Disabled => TestDataLifecycleState.Archived,
        _ => TestDataLifecycleState.NeedsReview,
    };

    /// <summary>Readiness of one role for one environment: approved and usable → Ready; only candidates → Candidate only; an asset scoped to
    /// another environment → Environment mismatch (never silently reused); a schema-stale asset → Stale; none → Missing.</summary>
    public static TestDataReadiness Readiness(string role, IEnumerable<TestDataAssetSummary> assets, int candidates, string? environment)
    {
        var list = assets.ToList();
        if (list.Any(a => a.Approval == TestDataApproval.Approved && UsableIn(a, environment, true) && a.SchemaCheck?.Status is not TestDataSchemaStatus.SchemaUnavailable))
            return new(role, TestDataReadinessState.Ready, "Approved test data available for this environment.");
        if (list.Any(a => a.Approval == TestDataApproval.Approved && a.SchemaCheck?.Status == TestDataSchemaStatus.SchemaUnavailable))
            return new(role, TestDataReadinessState.Stale, "Approved asset's schema is no longer available — review and revalidate.");
        if (list.Any(a => a.Approval == TestDataApproval.Approved && !UsableIn(a, environment, true)))
            return new(role, TestDataReadinessState.EnvironmentMismatch, $"Approved assets exist for another environment only — they are never reused in {environment}.");
        return candidates > 0 ? new(role, TestDataReadinessState.CandidateOnly, $"{candidates} candidate(s) — review and approve before use.")
            : new(role, TestDataReadinessState.Missing, "No test data for this role.");
    }

    /// <summary>Assets affected by a change (schema path, contract fingerprint, entity, environment): marked for review / potentially stale —
    /// never modified automatically. Structural revalidation is the next step.</summary>
    public static IReadOnlyList<TestDataImpact> Affected(IEnumerable<TestDataAssetSummary> assets, IEnumerable<string> changedContractFiles, IEnumerable<string> changedEntities,
        IEnumerable<string> changedEnvironments)
    {
        var files = changedContractFiles.Select(f => f.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entities = changedEntities.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var envs = changedEnvironments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<TestDataImpact>();
        foreach (var a in assets)
        {
            if (a.Schema is { } s && files.Any(f => f.EndsWith(s.SchemaPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                result.Add(new(a.Alias, TestDataLifecycleState.ReviewRequired, $"Its schema {s.SchemaPath} changed — review and revalidate structurally; the asset is not modified."));
            else if (a.EntityType is { } entity && entities.Contains(entity))
                result.Add(new(a.Alias, TestDataLifecycleState.PotentiallyStale, $"Data model entity {entity} changed — review; the asset is not modified."));
            else if (a.Environment is { } env && envs.Contains(env))
                result.Add(new(a.Alias, TestDataLifecycleState.PotentiallyStale, $"Environment {env} changed — review the environment-scoped asset."));
        }
        return result;
    }

    /// <summary>Safe metadata for documentation: aliases, kinds, intent, environment, approval, version — never values.</summary>
    public static string Documentation(IEnumerable<TestDataAssetSummary> assets)
    {
        var lines = new List<string> { "## Test data", "", "Metadata only — no values, identities or secrets.", "" };
        foreach (var a in assets.OrderBy(a => a.Alias, StringComparer.Ordinal))
            lines.Add($"- `{a.Alias}` · {a.Kind} · {(a.Capabilities.Contains("negative-test") ? "negative" : "positive")} · {a.Environment ?? "any environment"} · {State(a)} · version {(a.Fingerprint is { Length: > 12 } f ? f[..12] : a.Fingerprint ?? "n/a")}");
        return string.Join("\n", lines) + "\n";
    }
}
