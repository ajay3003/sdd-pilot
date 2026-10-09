using BirkNext.Api.Services.ActiveEventTesting.Debezium;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting.Providers.SkoleNaervaer;

/// <summary>
/// One BiRK education table the SkoleAdapter specification says it consumes, with the only fields a fixture may send. The specification
/// forbids names, remarks and contact data; those columns are never part of a fixture.
/// </summary>
public sealed record SkoleNaervaerTable(
    string Table, string Slug, string DisplayName, string KeyField, string ChildReferenceField, string FromField, string ToField,
    IReadOnlyList<(string Field, string ReferenceCode)> ReferenceFields)
{
    public string KeyScope => $"skolenaervaer.{Table}.{KeyField}";
    public IReadOnlyList<string> Fields => [KeyField, ChildReferenceField, FromField, ToField, .. ReferenceFields.Select(r => r.Field)];
}

/// <summary>
/// Fixture construction for the SkoleAdapter CDC input (external specification: SkoleAdapter spec 001 — CDC of dbo.Utdanning and
/// dbo.ManglendeSkoletilbud, child reference + period + type codes only). All values are synthetic: keys and child references come from
/// operator-agreed reserved ranges, type codes from configured reference values. Dates use Debezium's default SQL Server <c>date</c>
/// encoding (days since 1970-01-01); the encoding must be confirmed against the adapter's mapper during fixture review.
/// </summary>
public static class SkoleNaervaerFixtures
{
    public const int FixtureSchemaVersion = 1;
    public const string ChildReferenceScope = "skolenaervaer.BarnFK";

    public static readonly SkoleNaervaerTable Utdanning = new("Utdanning", "utdanning", "Utdanning CDC", "UtdanningPK", "BarnFK", "Fra", "Til",
        [("UtdanningTypeFk", "UtdanningTypeFk"), ("utdanningSkoleTypeFk", "UtdanningSkoleTypeFk")]);

    public static readonly SkoleNaervaerTable ManglendeSkoletilbud = new("ManglendeSkoletilbud", "manglende-skoletilbud", "Manglende skoletilbud CDC",
        "ManglendeSkoletilbudPK", "BarnFk", "GyldigFraDato", "GyldigTilDato", [("ManglendeSkoletilbudÅrsakTypeFk", "ManglendeSkoletilbudArsakTypeFk")]);

    public static IReadOnlyList<SkoleNaervaerTable> Tables { get; } = [Utdanning, ManglendeSkoletilbud];

    public static SkoleNaervaerTable? ForTable(string? table) => Tables.FirstOrDefault(t => string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase));

    /// <summary>Synthetic values for one record. <see cref="ChildReference"/> null is the reviewed invalid case (child reference missing).</summary>
    public sealed record RecordValues(long Key, long? ChildReference, DateOnly From, DateOnly? To, IReadOnlyDictionary<string, long> ReferenceCodes);

    public static DebeziumRow Row(SkoleNaervaerTable table, RecordValues values)
    {
        var row = new DebeziumRow();
        row.Add(table.KeyField, DebeziumValue.Number(values.Key));
        if (values.ChildReference is { } child) row.Add(table.ChildReferenceField, DebeziumValue.Number(child));
        row.Add(table.FromField, DebeziumValue.Number(DayNumber(values.From)));
        row.Add(table.ToField, values.To is { } to ? DebeziumValue.Number(DayNumber(to)) : DebeziumValue.Null);
        foreach (var (field, code) in table.ReferenceFields)
            row.Add(field, DebeziumValue.Number(values.ReferenceCodes.TryGetValue(code, out var value) ? value : throw new InvalidOperationException($"No reference code is configured for {code}.")));
        return row;
    }

    public static DebeziumRow Key(SkoleNaervaerTable table, long key) => new() { { table.KeyField, DebeziumValue.Number(key) } };

    /// <summary>create / snapshot read: after only.</summary>
    public static DebeziumEnvelope Insert(SkoleNaervaerTable table, DebeziumSource source, RecordValues values, DateTimeOffset now, int maxBytes, bool snapshot = false) =>
        DebeziumEventBuilder.Build(new DebeziumChange(snapshot ? ActiveEventOperation.ReadSnapshot : ActiveEventOperation.Create, source, null, Row(table, values), now), maxBytes);

    /// <summary>update: the same record identity before and after.</summary>
    public static DebeziumEnvelope Update(SkoleNaervaerTable table, DebeziumSource source, RecordValues before, RecordValues after, DateTimeOffset now, int maxBytes)
    {
        if (before.Key != after.Key) throw new InvalidOperationException("An update keeps the record identity.");
        return DebeziumEventBuilder.Build(new DebeziumChange(ActiveEventOperation.Update, source, Row(table, before), Row(table, after), now), maxBytes);
    }

    /// <summary>delete: before only, after null.</summary>
    public static DebeziumEnvelope Delete(SkoleNaervaerTable table, DebeziumSource source, RecordValues before, DateTimeOffset now, int maxBytes) =>
        DebeziumEventBuilder.Build(new DebeziumChange(ActiveEventOperation.Delete, source, Row(table, before), null, now), maxBytes);

    public static DebeziumEnvelope Tombstone(SkoleNaervaerTable table, long key) => DebeziumEventBuilder.Tombstone(Key(table, key));

    public static long DayNumber(DateOnly date) => date.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;
}
