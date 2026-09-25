using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Integrations;

/// <summary>One field of a JSON Schema event contract, flattened to a dotted path (nested objects "after.id", arrays "items[]").</summary>
public sealed record ContractField(string Path, IReadOnlySet<string> Types, bool Required, bool Nullable, IReadOnlyList<string>? Enum);

/// <summary>A parsed, trusted JSON Schema contract. Only structure is kept (paths, types, required, nullability, enum values).</summary>
public sealed record JsonSchemaContract(IReadOnlyDictionary<string, ContractField> Fields, string? Version)
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public static readonly string[] AllowedExtensions = [".json"];

    /// <summary>Validates an uploaded contract. A JSON sample document is not a schema and is rejected.</summary>
    public static (JsonSchemaContract? Contract, string? Error) Parse(string? fileName, string? content)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !AllowedExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            return (null, "Only JSON Schema files (.json / .schema.json) are accepted.");
        if (string.IsNullOrWhiteSpace(content)) return (null, "The file is empty.");
        if (Encoding.UTF8.GetByteCount(content) > MaxBytes) return (null, "The file is larger than the 2 MB contract limit.");
        JsonDocument document;
        try { document = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        // Position only: the parser's message can quote the offending content, and contract content is never echoed or logged.
        catch (JsonException ex) { return (null, $"Not valid JSON (line {(ex.LineNumber ?? 0) + 1}, byte {(ex.BytePositionInLine ?? 0) + 1})."); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, "A JSON Schema must be a JSON object.");
            var declaresSchema = root.TryGetProperty("$schema", out _) || root.TryGetProperty("properties", out _) || (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "object");
            if (!declaresSchema) return (null, "This looks like a sample event, not a JSON Schema (no $schema, type \"object\" or properties). Upload the schema.");
            var fields = new Dictionary<string, ContractField>(StringComparer.Ordinal);
            Walk(root, root, "", required: true, fields, depth: 0);
            if (fields.Count == 0) return (null, "The schema defines no fields.");
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()
                : root.TryGetProperty("$id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            return (new JsonSchemaContract(fields, version), null);
        }
    }

    private static void Walk(JsonElement root, JsonElement schema, string prefix, bool required, Dictionary<string, ContractField> fields, int depth)
    {
        if (depth > 12) return;
        schema = Resolve(root, schema);
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return;
        var requiredNames = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal) : [];
        foreach (var property in properties.EnumerateObject())
        {
            var path = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
            var definition = Resolve(root, property.Value);
            var (types, nullable) = TypesOf(root, definition);
            var isRequired = required && requiredNames.Contains(property.Name);
            var enumValues = definition.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Where(x => x.ValueKind != JsonValueKind.Null).Select(x => x.ToString()).ToList() : null;
            fields[path] = new ContractField(path, types, requiredNames.Contains(property.Name), nullable, enumValues);
            var objectBranch = ObjectBranch(root, definition);
            if (objectBranch is { } nested) Walk(root, nested, path, isRequired, fields, depth + 1);
            if (definition.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
            {
                var itemSchema = Resolve(root, items);
                var (itemTypes, itemNullable) = TypesOf(root, itemSchema);
                fields[path + "[]"] = new ContractField(path + "[]", itemTypes, false, itemNullable, null);
                if (ObjectBranch(root, itemSchema) is { } itemObject) Walk(root, itemObject, path + "[]", false, fields, depth + 1);
            }
        }
    }

    /// <summary>Local references only (#/definitions/x, #/$defs/x); anything else stays as declared.</summary>
    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        for (var i = 0; i < 5 && schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("$ref", out var reference) && reference.GetString() is { } pointer && pointer.StartsWith("#/", StringComparison.Ordinal); i++)
        {
            var current = root;
            foreach (var segment in pointer[2..].Split('/'))
                if (!current.TryGetProperty(segment.Replace("~1", "/").Replace("~0", "~"), out current)) return schema;
            schema = current;
        }
        return schema;
    }

    private static JsonElement? ObjectBranch(JsonElement root, JsonElement definition)
    {
        if (definition.TryGetProperty("properties", out _)) return definition;
        foreach (var keyword in new[] { "oneOf", "anyOf" })
            if (definition.TryGetProperty(keyword, out var branches) && branches.ValueKind == JsonValueKind.Array)
                foreach (var branch in branches.EnumerateArray())
                    if (Resolve(root, branch) is var resolved && resolved.TryGetProperty("properties", out _)) return resolved;
        return null;
    }

    private static (IReadOnlySet<string> Types, bool Nullable) TypesOf(JsonElement root, JsonElement definition)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        void Add(JsonElement d)
        {
            if (d.TryGetProperty("type", out var type))
            {
                if (type.ValueKind == JsonValueKind.String) types.Add(type.GetString()!);
                else if (type.ValueKind == JsonValueKind.Array) foreach (var t in type.EnumerateArray()) if (t.GetString() is { } s) types.Add(s);
            }
            else if (d.TryGetProperty("properties", out _)) types.Add("object");
        }
        Add(definition);
        foreach (var keyword in new[] { "oneOf", "anyOf" })
            if (definition.TryGetProperty(keyword, out var branches) && branches.ValueKind == JsonValueKind.Array)
                foreach (var branch in branches.EnumerateArray()) Add(Resolve(root, branch));
        var nullable = types.Remove("null") || (definition.TryGetProperty("nullable", out var n) && n.ValueKind == JsonValueKind.True);
        return (types, nullable);
    }

    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

/// <summary>
/// Producer/consumer compatibility of two trusted JSON Schema contracts. Incompatible: a consumer-required field the producer does not
/// provide, a type the producer can send that the consumer does not accept, a nullable/optional producer field the consumer requires, or
/// enum values the producer may send outside the consumer's enum. Additions by the producer are compatible. Structure only.
/// </summary>
public static class EventContractComparer
{
    public sealed record Difference(string Code, string Field, string Detail, bool Breaking);

    public static List<Difference> Compare(JsonSchemaContract producer, JsonSchemaContract consumer)
    {
        var differences = new List<Difference>();
        foreach (var (path, expected) in consumer.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!producer.Fields.TryGetValue(path, out var provided))
            {
                differences.Add(expected.Required
                    ? new("REQUIRED_FIELD_MISSING", path, $"The consumer requires `{path}`, which the producer contract does not define.", true)
                    : new("OPTIONAL_FIELD_MISSING", path, $"The consumer reads optional `{path}`, which the producer contract does not define.", false));
                continue;
            }
            var producerTypes = provided.Types.Where(t => t != "null").ToHashSet(StringComparer.Ordinal);
            var consumerTypes = expected.Types.Where(t => t != "null").ToHashSet(StringComparer.Ordinal);
            // Every type the producer may send must be accepted by the consumer; integer is a number.
            if (producerTypes.Count > 0 && consumerTypes.Count > 0 && !producerTypes.All(t => consumerTypes.Contains(t) || (t == "integer" && consumerTypes.Contains("number"))))
                differences.Add(new("TYPE_MISMATCH", path, $"`{path}` can be {string.Join("/", producerTypes.Order())} from the producer but the consumer accepts {string.Join("/", consumerTypes.Order())}.", true));
            if (expected.Required && !expected.Nullable && (provided.Nullable || !provided.Required))
                differences.Add(new("NULLABILITY_MISMATCH", path, $"The consumer requires a non-null `{path}`, but the producer contract allows it to be {(provided.Nullable ? "null" : "absent")}.", true));
            if (expected.Enum is { } accepted && provided.Enum is { } sent && sent.Except(accepted).ToList() is { Count: > 0 } extra)
                differences.Add(new("ENUM_NOT_ACCEPTED", path, $"The producer may send {string.Join(", ", extra.Select(x => $"`{x}`"))} for `{path}`, which the consumer's enum does not accept.", true));
        }
        return differences;
    }
}

/// <summary>Debezium change-event envelope as DECLARED by the producer contract. Structure only; no event is read.</summary>
public sealed record DebeziumEnvelope(bool HasBefore, bool HasAfter, bool HasSource, bool HasOp, IReadOnlyList<string>? Operations, bool BeforeIsObject, IReadOnlyList<string> SourceFields)
{
    public bool IsEnvelope => HasBefore && HasAfter && HasSource && HasOp;
    public static readonly string[] KnownOperations = ["c", "u", "d", "r", "t", "m"];

    public static DebeziumEnvelope From(JsonSchemaContract producer)
    {
        // Debezium JSON can wrap the envelope in "payload" (schemas.enable=true); accept both layouts.
        var prefix = producer.Fields.ContainsKey("payload.op") ? "payload." : "";
        producer.Fields.TryGetValue(prefix + "op", out var op);
        producer.Fields.TryGetValue(prefix + "before", out var before);
        return new DebeziumEnvelope(
            before is not null, producer.Fields.ContainsKey(prefix + "after"), producer.Fields.ContainsKey(prefix + "source"), op is not null,
            op?.Enum, before?.Types.Contains("object") == true,
            producer.Fields.Keys.Where(k => k.StartsWith(prefix + "source.", StringComparison.Ordinal)).Select(k => k[(prefix.Length + 7)..]).ToList());
    }
}

public interface IIntegrationContractStore
{
    Task<IReadOnlyList<IntegrationContractArtifact>> ListAsync(string environmentId, CancellationToken ct = default);
    Task<(IntegrationContractArtifact? Artifact, string? Error)> SaveAsync(IntegrationContractUpload upload, CancellationToken ct = default);
    Task<bool> DeleteAsync(string environmentId, string integrationId, IntegrationContractRole role, CancellationToken ct = default);
    /// <summary>Every artifact of the environment with its parsed contract (a stored artifact that no longer parses has a problem instead).</summary>
    Task<IReadOnlyList<(IntegrationContractArtifact Artifact, JsonSchemaContract? Contract, string? Problem)>> LoadAsync(string environmentId, CancellationToken ct = default);
}

/// <summary>Trusted event contracts per (environment, integration, role). Validated on upload; invalid files are never stored.</summary>
public sealed class IntegrationContractStore(AppDbContext db, IIntegrationCatalogService catalog, ILogger<IntegrationContractStore> logger) : IIntegrationContractStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<IntegrationContractArtifact>> ListAsync(string environmentId, CancellationToken ct = default) =>
        (await db.IntegrationContractArtifacts.AsNoTracking().Where(a => a.EnvironmentId == environmentId).ToListAsync(ct)).Select(Metadata).ToList();

    public async Task<(IntegrationContractArtifact? Artifact, string? Error)> SaveAsync(IntegrationContractUpload upload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(upload.EnvironmentId) || string.IsNullOrWhiteSpace(upload.IntegrationId))
            return (null, "A contract must be bound to a Target Environment and an integration.");
        var fileName = System.IO.Path.GetFileName((upload.FileName ?? "").Replace('\\', '/'));
        var (contract, error) = JsonSchemaContract.Parse(fileName, upload.Content);
        if (contract is null)
        {
            logger.LogInformation("Contract rejected for {IntegrationId} ({Role}) in {EnvironmentId}: {Reason}", upload.IntegrationId, upload.Role, upload.EnvironmentId, error);
            return (null, error);
        }
        var role = upload.Role.ToString();
        var record = await db.IntegrationContractArtifacts.FirstOrDefaultAsync(a => a.EnvironmentId == upload.EnvironmentId && a.IntegrationId == upload.IntegrationId && a.Role == role, ct);
        var artifact = new IntegrationContractArtifact
        {
            EnvironmentId = upload.EnvironmentId, IntegrationId = upload.IntegrationId, Role = upload.Role, FileName = fileName, ContentHash = JsonSchemaContract.Hash(upload.Content),
            Version = contract.Version, FieldCount = contract.Fields.Count, ImportedAt = DateTimeOffset.UtcNow,
        };
        if (record is null) db.IntegrationContractArtifacts.Add(record = new IntegrationContractArtifactRecord { EnvironmentId = upload.EnvironmentId, IntegrationId = upload.IntegrationId, Role = role });
        record.Content = upload.Content;
        record.ContentHash = artifact.ContentHash;
        record.DocumentJson = JsonSerializer.Serialize(artifact, Json);
        record.ImportedAt = artifact.ImportedAt;
        await db.SaveChangesAsync(ct);
        await SyncRelationshipAsync(upload.EnvironmentId, upload.IntegrationId, ct);
        logger.LogInformation("Contract {Role} for {IntegrationId} in {EnvironmentId} stored: {FileName}, {Hash}, {Fields} field(s).", role, upload.IntegrationId, upload.EnvironmentId, fileName, artifact.ShortHash, artifact.FieldCount);
        return (artifact, null);
    }

    public async Task<bool> DeleteAsync(string environmentId, string integrationId, IntegrationContractRole role, CancellationToken ct = default)
    {
        var name = role.ToString();
        var record = await db.IntegrationContractArtifacts.FirstOrDefaultAsync(a => a.EnvironmentId == environmentId && a.IntegrationId == integrationId && a.Role == name, ct);
        if (record is null) return false;
        db.IntegrationContractArtifacts.Remove(record);
        await db.SaveChangesAsync(ct);
        await SyncRelationshipAsync(environmentId, integrationId, ct);
        return true;
    }

    public async Task<IReadOnlyList<(IntegrationContractArtifact Artifact, JsonSchemaContract? Contract, string? Problem)>> LoadAsync(string environmentId, CancellationToken ct = default) =>
        (await db.IntegrationContractArtifacts.AsNoTracking().Where(a => a.EnvironmentId == environmentId).ToListAsync(ct))
            .Select(r => { var meta = Metadata(r); var (contract, error) = JsonSchemaContract.Parse(meta.FileName, r.Content); return (meta, contract, error); }).ToList();

    /// <summary>The configured relationship follows the uploaded artifacts (Producer / Consumer / Both available). "Verified" is set by a person only and is kept.</summary>
    private async Task SyncRelationshipAsync(string environmentId, string integrationId, CancellationToken ct)
    {
        var roles = await db.IntegrationContractArtifacts.AsNoTracking().Where(a => a.EnvironmentId == environmentId && a.IntegrationId == integrationId).Select(a => a.Role).ToListAsync(ct);
        var current = (await catalog.GetAsync(environmentId, null, null, ct)).Integrations.FirstOrDefault(i => i.Id == integrationId);
        if (current is null || current.ContractRelationship == ContractRelationshipState.RelationshipVerified && roles.Count == 2) return;
        var state = (roles.Contains("Producer"), roles.Contains("Consumer")) switch
        {
            (true, true) => ContractRelationshipState.BothContractsAvailable,
            (true, false) => ContractRelationshipState.ProducerContractAvailable,
            (false, true) => ContractRelationshipState.ConsumerContractAvailable,
            _ => ContractRelationshipState.NotConfigured,
        };
        if (state != current.ContractRelationship) await catalog.UpdateAsync(environmentId, current.Id, current with { ContractRelationship = state }, ct);
    }

    private static IntegrationContractArtifact Metadata(IntegrationContractArtifactRecord record) =>
        JsonSerializer.Deserialize<IntegrationContractArtifact>(record.DocumentJson, Json) ?? new IntegrationContractArtifact { EnvironmentId = record.EnvironmentId, IntegrationId = record.IntegrationId, ContentHash = record.ContentHash };
}
