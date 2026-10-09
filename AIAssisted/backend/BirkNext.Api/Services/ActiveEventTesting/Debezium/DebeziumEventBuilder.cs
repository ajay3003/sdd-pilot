using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting.Debezium;

/// <summary>A typed field value of a synthetic Debezium row. Only the value kinds fixtures need: text, integer, boolean, null.</summary>
public readonly record struct DebeziumValue(object? Value)
{
    public static DebeziumValue Text(string value) => new(value);
    public static DebeziumValue Number(long value) => new(value);
    public static DebeziumValue Boolean(bool value) => new(value);
    public static readonly DebeziumValue Null = new(null);
}

/// <summary>An ordered synthetic row (field order is preserved in the envelope).</summary>
public sealed class DebeziumRow : List<KeyValuePair<string, DebeziumValue>>
{
    public void Add(string field, DebeziumValue value) => Add(new KeyValuePair<string, DebeziumValue>(field, value));
    public DebeziumRow Without(string field) { var copy = new DebeziumRow(); copy.AddRange(this.Where(item => item.Key != field)); return copy; }
    public DebeziumRow With(string field, DebeziumValue value)
    {
        var copy = new DebeziumRow();
        copy.AddRange(this.Select(item => item.Key == field ? new KeyValuePair<string, DebeziumValue>(field, value) : item));
        if (copy.All(item => item.Key != field)) copy.Add(field, value);
        return copy;
    }
}

/// <summary>Debezium <c>source</c> block: connector, logical name and the captured table.</summary>
public sealed record DebeziumSource(string Database, string Schema, string Table, string Connector = "sqlserver", string Name = "birknext-active-test");

/// <summary>One change: c/u/d/r with before/after rows as Debezium defines them for each operation.</summary>
public sealed record DebeziumChange(ActiveEventOperation Operation, DebeziumSource Source, DebeziumRow? Before, DebeziumRow? After, DateTimeOffset Timestamp);

/// <summary>The serialized envelope (or a tombstone: no body, key only) with its fingerprint.</summary>
public sealed record DebeziumEnvelope(ActiveEventOperation Operation, byte[] Body, string Sha256, string? Key);

/// <summary>
/// Generic Debezium JSON envelope construction for synthetic CDC fixtures: <c>{"schema":{"type":"struct"},"payload":{before, after, source,
/// op, ts_ms}}</c>. Domain providers supply the table and rows; nothing here knows a domain. Operation rules follow Debezium: create and
/// snapshot read carry only <c>after</c> (snapshot sets <c>source.snapshot</c>), update carries both, delete carries only <c>before</c>,
/// and a tombstone is a key with no value.
/// </summary>
public static class DebeziumEventBuilder
{
    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string OperationCode(ActiveEventOperation operation) => operation switch
    {
        ActiveEventOperation.Create => "c",
        ActiveEventOperation.Update => "u",
        ActiveEventOperation.Delete => "d",
        ActiveEventOperation.ReadSnapshot => "r",
        _ => throw new InvalidOperationException($"{operation} has no Debezium envelope operation code."),
    };

    public static DebeziumEnvelope Build(DebeziumChange change, int maxBytes)
    {
        Validate(change);
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Writer))
        {
            json.WriteStartObject();
            json.WriteStartObject("schema"); json.WriteString("type", "struct"); json.WriteEndObject();
            json.WriteStartObject("payload");
            WriteRow(json, "before", change.Before);
            WriteRow(json, "after", change.After);
            json.WriteStartObject("source");
            json.WriteString("connector", change.Source.Connector);
            json.WriteString("name", change.Source.Name);
            json.WriteString("db", change.Source.Database);
            json.WriteString("schema", change.Source.Schema);
            json.WriteString("table", change.Source.Table);
            json.WriteString("snapshot", change.Operation == ActiveEventOperation.ReadSnapshot ? "true" : "false");
            json.WriteEndObject();
            json.WriteString("op", OperationCode(change.Operation));
            json.WriteNumber("ts_ms", change.Timestamp.ToUnixTimeMilliseconds());
            json.WriteEndObject();
            json.WriteEndObject();
        }
        var body = buffer.ToArray();
        if (body.Length > maxBytes) throw new InvalidOperationException($"The synthetic fixture is {body.Length} bytes, above the {maxBytes}-byte bound.");
        return new DebeziumEnvelope(change.Operation, body, Hash(body), null);
    }

    /// <summary>The record key as Debezium serializes it (<c>{"payload":{key fields}}</c>), used as the tombstone key and as a safe event key.</summary>
    public static string Key(DebeziumRow keyFields)
    {
        if (keyFields.Count == 0) throw new InvalidOperationException("A record key needs at least one field.");
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Writer))
        {
            json.WriteStartObject();
            WriteRow(json, "payload", keyFields);
            json.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>A tombstone follows a delete in Kafka-based CDC: the same key, no value. It has no body.</summary>
    public static DebeziumEnvelope Tombstone(DebeziumRow keyFields) => new(ActiveEventOperation.Tombstone, [], Hash([]), Key(keyFields));

    private static void Validate(DebeziumChange change)
    {
        var (needsBefore, needsAfter) = change.Operation switch
        {
            ActiveEventOperation.Create or ActiveEventOperation.ReadSnapshot => (false, true),
            ActiveEventOperation.Update => (true, true),
            ActiveEventOperation.Delete => (true, false),
            _ => throw new InvalidOperationException($"{change.Operation} is not a Debezium change event; build a tombstone with {nameof(Tombstone)}."),
        };
        if (needsBefore != (change.Before is not null)) throw new InvalidOperationException($"A Debezium {OperationCode(change.Operation)} event {(needsBefore ? "requires" : "has no")} a before row.");
        if (needsAfter != (change.After is not null)) throw new InvalidOperationException($"A Debezium {OperationCode(change.Operation)} event {(needsAfter ? "requires" : "has no")} an after row.");
        if (string.IsNullOrWhiteSpace(change.Source.Table)) throw new InvalidOperationException("The Debezium source table is required.");
    }

    private static void WriteRow(Utf8JsonWriter json, string name, DebeziumRow? row)
    {
        if (row is null) { json.WriteNull(name); return; }
        json.WriteStartObject(name);
        foreach (var (field, value) in row)
        {
            switch (value.Value)
            {
                case null: json.WriteNull(field); break;
                case string text: json.WriteString(field, text); break;
                case long number: json.WriteNumber(field, number); break;
                case bool flag: json.WriteBoolean(field, flag); break;
                default: throw new InvalidOperationException($"Unsupported fixture value for {field}.");
            }
        }
        json.WriteEndObject();
    }

    public static string Hash(byte[] body) => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
}
