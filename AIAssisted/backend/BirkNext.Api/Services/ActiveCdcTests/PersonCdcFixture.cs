using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveCdcTests;

/// <summary>Built-in, reviewed scenarios. Phase 1 has exactly one; there is no way to supply a payload from outside the backend.</summary>
public static class ActiveCdcScenarioCatalog
{
    public const string NormalPersonId = "person.normal.create";

    /// <summary>Field names are BiRK's real spelling (payload names are never sanitized — only Event Hub entity names are).</summary>
    public static readonly ActiveCdcScenario NormalPerson = new()
    {
        Id = NormalPersonId, Version = "1", Name = "Normal Person",
        Description = "One synthetic Debezium create (op \"c\") on the Person table: synthetic name marker, synthetic birth date (age 20, Europe/Oslo), " +
            "uncertain national id derived by the adapter from the date only, unknown gender. No national id, DUF number or child data is sent.",
        Table = "Person", Operation = "c",
        Fields = ["PersonPK", "Fornavn", "Etternavn", "Født", "UsikkerFødselsdato", "UsikkerFødselsnummer", "KjønnTypeFK", "EndretDato"],
        PassCriterion = "Passed requires a verified read-only observation of the synthetic PersonId in the Person module. No such verification path exists in Phase 1, so an accepted event is Partial.",
    };

    public const string SamePersonPkReplayId = "person.same-personpk-replay";

    /// <summary>
    /// Runtime resilience, not duplicate correctness: A (PersonPK X), A2 (byte-identical replay of A), then B (PersonPK Y) as a following valid
    /// control. It asks only whether the observable Event Hub → Person Adapter consumer path keeps going after a replay.
    /// </summary>
    public static readonly ActiveCdcScenario SamePersonPkReplay = NormalPerson with
    {
        Id = SamePersonPkReplayId, Version = "1", Name = "Same PersonPK replay", Category = "Runtime resilience", MessageCount = 3,
        Description = "Sends one synthetic Person, replays the same source identity, then sends another valid Person to check whether the observable consumer path continues.",
        Limitation = "This does not verify database idempotency or duplicate Person handling.",
        PassCriterion = "Passed requires all three sends accepted, A2 byte-identical to A, and the consumer checkpoint past both the replay and the following valid event on their partitions.",
        PassMeaning = "Passed means the same source Person identity was replayed and the observable Event Hub → Person Adapter consumer path continued processing a following valid Person event.",
        PassDoesNotMean = "This result does not prove database idempotency, Person row count, overwrite behavior, outbox duplication, Service Bus delivery or natural-key duplicate handling.",
    };

    public static IReadOnlyList<ActiveCdcScenario> All { get; } = [NormalPerson, SamePersonPkReplay];

    public static ActiveCdcScenario? Find(string? id) => All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
}

/// <summary>A generated synthetic CDC event. Only <see cref="PersonCdcFixtureBuilder"/> can create one — there is no generic "send this JSON".</summary>
public sealed class SyntheticCdcEvent
{
    internal SyntheticCdcEvent(byte[] body, Guid runId, string scenarioId, string label = "") { Body = body; RunId = runId; ScenarioId = scenarioId; Label = label; }
    public ReadOnlyMemory<byte> Body { get; }
    public Guid RunId { get; }
    public string ScenarioId { get; }
    /// <summary>Message label within a multi-message scenario ("A", "A2", "B"); empty for Normal Person. Transport metadata only — never in the body.</summary>
    public string Label { get; }
}

/// <summary>
/// The Normal Person fixture in the envelope the Person Adapter reads (<c>CdcProcessorWorker.Deserialize</c>: <c>payload.op</c>,
/// <c>payload.source.table</c>, <c>payload.after</c>). Values are synthetic: a marker name, a birth date 20 years back (AgeFilter keeps
/// create events up to 25 years), <c>UsikkerFødselsnummer = true</c> so the adapter's 6-character date component lands in the uncertain field,
/// <c>KjønnTypeFK = 3</c> (unknown). No Fødselsnummer, Personnummer, Dufnummer or Navn is sent.
/// </summary>
public static class PersonCdcFixtureBuilder
{
    public const int FixtureSchemaVersion = 1;
    private static readonly TimeZoneInfo Oslo = FindOslo();
    private static readonly JsonWriterOptions Writer = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Marker(Guid runId) => $"BIRKNEXT-TEST-{runId.ToString("N")[..12].ToUpperInvariant()}";

    /// <summary>The adapter's PersonId derivation (PersonMapper.ToDeterministicGuid): SHA-256 of the decimal PersonPK text, first 16 bytes.</summary>
    public static Guid ExpectedPersonId(int personPk) => new(SHA256.HashData(Encoding.UTF8.GetBytes(personPk.ToString(System.Globalization.CultureInfo.InvariantCulture)))[..16]);

    public static (SyntheticCdcEvent Event, ActiveCdcFixtureSummary Summary) Build(Guid runId, int personPk, ActiveCdcDestination destination, DateTimeOffset now, int maxBytes) =>
        Build(runId, personPk, destination, now, maxBytes, ActiveCdcScenarioCatalog.NormalPersonId, "", "");

    /// <summary>
    /// The exact replay of <paramref name="original"/>: the same body bytes (same PersonPK, fields, operation and timestamps). Only the
    /// transport label differs, so the replay cannot turn into an update.
    /// </summary>
    public static SyntheticCdcEvent Replay(SyntheticCdcEvent original, string label) => new(original.Body.ToArray(), original.RunId, original.ScenarioId, label);

    /// <summary>A2 is a replay of A only when the business payload is byte-identical.</summary>
    public static bool IsExactReplay(SyntheticCdcEvent a, SyntheticCdcEvent a2) => a.Body.Span.SequenceEqual(a2.Body.Span);

    public static (SyntheticCdcEvent Event, ActiveCdcFixtureSummary Summary) Build(Guid runId, int personPk, ActiveCdcDestination destination, DateTimeOffset now, int maxBytes,
        string scenarioId, string label, string markerSuffix)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Oslo).Date);
        var birth = today.AddYears(-20).AddDays(-30);
        var age = today.Year - birth.Year - (today < birth.AddYears(today.Year - birth.Year) ? 1 : 0);
        var marker = Marker(runId) + markerSuffix;
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Writer))
        {
            json.WriteStartObject();
            json.WriteStartObject("schema"); json.WriteString("type", "struct"); json.WriteEndObject();
            json.WriteStartObject("payload");
            json.WriteNull("before");
            json.WriteStartObject("after");
            json.WriteNumber("PersonPK", personPk);
            json.WriteString("Fornavn", marker);
            json.WriteString("Etternavn", "Synthetic");
            json.WriteNumber("Født", birth.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber);
            json.WriteBoolean("UsikkerFødselsdato", false);
            json.WriteBoolean("UsikkerFødselsnummer", true);
            json.WriteNumber("KjønnTypeFK", 3);
            json.WriteNumber("EndretDato", now.ToUnixTimeMilliseconds());
            json.WriteEndObject();
            json.WriteStartObject("source");
            json.WriteString("connector", "sqlserver");
            json.WriteString("name", "birknext-active-test");
            json.WriteString("db", destination.SourceDatabase);
            json.WriteString("schema", destination.SourceSchema);
            json.WriteString("table", destination.SourceTable);
            json.WriteString("snapshot", "false");
            json.WriteEndObject();
            json.WriteString("op", "c");
            json.WriteNumber("ts_ms", now.ToUnixTimeMilliseconds());
            json.WriteEndObject();
            json.WriteEndObject();
        }
        var body = buffer.ToArray();
        if (body.Length > maxBytes) throw new InvalidOperationException($"The synthetic fixture is {body.Length} bytes, above the {maxBytes}-byte bound.");
        var summary = new ActiveCdcFixtureSummary
        {
            SyntheticPersonPk = personPk, ExpectedPersonId = ExpectedPersonId(personPk), Marker = marker, SyntheticBirthDate = birth, AgeYears = age,
            Fields = [.. ActiveCdcScenarioCatalog.NormalPerson.Fields], PayloadSha256 = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), PayloadBytes = body.Length,
            Notes =
            [
                "Synthetic values only; the payload itself is not stored.",
                "No Fødselsnummer/Personnummer is sent. The adapter derives a 6-character date component from Født into the uncertain national-id field.",
                $"Birth date is synthetic (age {age} in Europe/Oslo); the adapter's age filter keeps create events up to 25 years.",
            ],
        };
        return (new SyntheticCdcEvent(body, runId, scenarioId, label), summary);
    }

    private static TimeZoneInfo FindOslo()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
    }
}

/// <summary>
/// Binds the scenario to one analyzed source snapshot: every fixture field must be a CDC field the adapter's Person record reads in that
/// snapshot. Not the latest snapshot for the integration → Outdated (a newer source was uploaded and must be the one bound).
/// </summary>
public static class ActiveCdcContractManifestService
{
    public static ActiveCdcContractManifest Evaluate(ActiveCdcScenario scenario, IqrSourceSnapshot? snapshot, Guid? latestSnapshotId)
    {
        var baseline = new ActiveCdcContractManifest
        {
            ScenarioId = scenario.Id, ScenarioVersion = scenario.Version, FixtureSchemaVersion = PersonCdcFixtureBuilder.FixtureSchemaVersion, RequiredFields = [.. scenario.Fields],
        };
        if (snapshot is null)
            return Seal(baseline with { MissingFields = [.. scenario.Fields], Detail = "No analyzed source snapshot is selected for this integration. Upload the adapter source in Source evidence first." });
        var bound = baseline with { SourceSnapshotId = snapshot.Id, ArchiveSha256 = snapshot.Archive.Sha256, SourceCommit = snapshot.Commit, AnalyzerVersion = snapshot.AnalyzerVersion };
        if (snapshot.IntegrationPath is not { } path || snapshot.AnalyzerVersion < 2)
            return Seal(bound with { Status = ActiveCdcContractStatus.Incompatible, MissingFields = [.. scenario.Fields], Detail = "The snapshot has no integration path (analyzer v2 or later is needed to see which CDC fields the adapter reads)." });
        var record = $"{scenario.Table}Record";
        var read = path.Fields.Select(f => f.Key).Where(k => k.StartsWith($"CDC {record}.", StringComparison.Ordinal)).Select(k => k[($"CDC {record}.").Length..]).ToHashSet(StringComparer.Ordinal);
        var confirmed = scenario.Fields.Where(read.Contains).ToList();
        var missing = scenario.Fields.Where(f => !read.Contains(f)).ToList();
        if (!path.Stages.Any(s => s.Kind == SourceStageKind.AdapterModel && s.TypeName == record))
            return Seal(bound with { Status = ActiveCdcContractStatus.Incompatible, ConfirmedFields = confirmed, MissingFields = missing, Detail = $"The snapshot has no adapter model {record}." });
        if (missing.Count > 0)
            return Seal(bound with { Status = ActiveCdcContractStatus.Incompatible, ConfirmedFields = confirmed, MissingFields = missing, Detail = $"The adapter in this snapshot does not read: {string.Join(", ", missing)}." });
        if (scenario.Id == ActiveCdcScenarioCatalog.SamePersonPkReplayId) bound = bound with { DeveloperCoverage = SamePersonPkCoverage(snapshot) };
        if (latestSnapshotId is { } latest && latest != snapshot.Id)
            return Seal(bound with { Status = ActiveCdcContractStatus.Outdated, ConfirmedFields = confirmed, Detail = "A newer source snapshot exists for this integration. Select it so the fixture is bound to the current source." });
        return Seal(bound with { Status = ActiveCdcContractStatus.Compatible, ConfirmedFields = confirmed, Detail = $"All {confirmed.Count} fixture fields are read by {record} in this snapshot (source compatibility, not deployment correlation)." });
    }

    /// <summary>Developer tests in the snapshot's repeated-ingestion rule that send the same Person payload twice (matched by test name; discovered, not executed).</summary>
    private static List<string> SamePersonPkCoverage(IqrSourceSnapshot snapshot)
    {
        var tests = snapshot.Tests.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        return (snapshot.IntegrationPath?.Rules ?? []).Where(r => r.Kind == "Idempotency").SelectMany(r => r.DeveloperTestIds)
            .Select(id => tests.GetValueOrDefault(id)).OfType<DeveloperTestEvidence>()
            .Where(t => t.Method.Contains("SamePayload", StringComparison.Ordinal) && t.Method.Contains("Person", StringComparison.Ordinal))
            .Select(t => $"{t.Class}.{t.Method} ({t.Layer.ToString().ToLowerInvariant()})").Distinct().ToList();
    }

    private static ActiveCdcContractManifest Seal(ActiveCdcContractManifest m)
    {
        var text = string.Join("|", m.ScenarioId, m.ScenarioVersion, m.FixtureSchemaVersion, m.SourceSnapshotId, m.ArchiveSha256, m.Status, string.Join(",", m.ConfirmedFields));
        return m with { Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16] };
    }
}
