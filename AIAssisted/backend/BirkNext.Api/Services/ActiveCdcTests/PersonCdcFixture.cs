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
        ReplayKind = ActiveEventReplayKind.ExactReplay,
        Description = "Sends one synthetic Person, replays the same source identity, then sends another valid Person to check whether the observable consumer path continues.",
        Limitation = "This does not verify database idempotency or duplicate Person handling.",
        PassCriterion = "Passed requires all three sends accepted, A2 byte-identical to A, and the consumer checkpoint past both the replay and the control event on their partitions.",
        PassMeaning = "Runtime continuity after replay was observed: the Event Hub consumer advanced beyond the replay and the following control event without becoming stuck.",
        PassDoesNotMean = "It does not mean the replay was processed successfully, the control Person was persisted, database idempotency was proven, no outbox duplication occurred, Person row count or overwrite behavior was correct, natural-key duplicates were handled, or Service Bus was verified. A checkpoint past an event shows consumer progression, not successful handling.",
    };

    public const string InvalidThenValidId = "person.invalid-then-valid";

    /// <summary>
    /// Fault resilience: one controlled invalid Person CDC event (valid Debezium envelope, PersonPK missing) followed by one valid synthetic
    /// Person. It asks only whether the observable Event Hub consumer keeps advancing after the invalid input.
    /// </summary>
    public static readonly ActiveCdcScenario InvalidThenValid = NormalPerson with
    {
        Id = InvalidThenValidId, Version = "1", Name = "Invalid Person → valid Person", Category = "Fault resilience", MessageCount = 2,
        ReplayKind = ActiveEventReplayKind.ControlAfterInvalid,
        Description = "Sends one controlled invalid Person CDC event followed by a valid synthetic Person event to check whether the observable consumer continues advancing.",
        Limitation = "This verifies consumer continuity, not correct handling or persistence of either message.",
        PassCriterion = "Passed requires both sends accepted, the reviewed invalid fixture, and the consumer checkpoint past the invalid event and the valid control on their partitions.",
        PassMeaning = "A controlled invalid Person CDC event was followed by a valid Person CDC event, and the observable Event Hub consumer advanced beyond the valid control event without becoming stuck.",
        PassDoesNotMean = "This does not prove that the invalid event was rejected for the correct reason or that the valid Person was persisted. It also does not prove the invalid event left no database state, the mapper output was correct, or that no exception, retry, fault record, outbox event or Service Bus effect occurred.",
        InvalidFixture = $"{InvalidFixtureReview.FixtureId} v{InvalidFixtureReview.FixtureVersion}",
        InvalidCondition = InvalidFixtureReview.Condition,
    };

    public static IReadOnlyList<ActiveCdcScenario> All { get; } = [NormalPerson, SamePersonPkReplay, InvalidThenValid];

    public static ActiveCdcScenario? Find(string? id) => All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// The reviewed invalid fixture. The M2LB source analyzer records that PersonPK derives the Person identity but not the mapper's null gate,
/// so the review is bound to the exact archive it was made against; any other archive is "Needs review" until re-reviewed (an operator can
/// add re-reviewed archive hashes in <c>ActiveCdcTests:InvalidFixtureReviewedArchives</c>).
/// </summary>
public static class InvalidFixtureReview
{
    public const string FixtureId = "person.missing-personpk";
    public const int FixtureVersion = 1;
    public const string Condition = "PersonPK missing from a structurally valid Person create (valid payload, op \"c\", source.table Person, after object).";

    /// <summary>SHA-256 of the M2LB archive reviewed on 2026-09-30 (PersonAdapter sources identical to the audited copy).</summary>
    public static readonly IReadOnlySet<string> ReviewedArchives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "c850a1b2813bbf6e2a9eba1f311d63ff0332eacf35764cd6b767489ac22dc41e",
    };

    /// <summary>The expected code path, as reviewed (source locations; no source text is stored).</summary>
    public static readonly IReadOnlyList<string> ReviewedPath =
    [
        "PersonMapper.Map returns null when PersonPK is not a JSON number (PersonAdapter/src/M2LB.PersonBiRKAdapter.Infrastructure/Mapping/PersonMapper.cs:17-19).",
        "CdcRouter.RouteAsync logs \"PersonMapper returned null — discarding\" and returns Discarded (PersonAdapter/src/M2LB.PersonBiRKAdapter.Domain/Routing/CdcRouter.cs:93).",
        "CdcProcessorWorker.OnProcessEventAsync logs \"CDC event discarded\", sets the batch's last event and flushes, so the next checkpoint passes it (PersonAdapter/src/M2LB.PersonBiRKAdapter.Worker/Workers/CdcProcessorWorker.cs:139-150).",
        "No HTTP call, no fault-queue entry and no retry on this path.",
    ];
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

    /// <summary>
    /// The controlled invalid fixture: the Normal Person envelope and fields with PersonPK omitted — nothing else differs, so there is no
    /// second invalidity. It is built by the same writer as the valid fixtures.
    /// </summary>
    public static (SyntheticCdcEvent Event, ActiveCdcFixtureSummary Summary) BuildInvalid(Guid runId, ActiveCdcDestination destination, DateTimeOffset now, int maxBytes, string scenarioId, string label) =>
        Write(runId, null, destination, now, maxBytes, scenarioId, label, "-INVALID");

    public static (SyntheticCdcEvent Event, ActiveCdcFixtureSummary Summary) Build(Guid runId, int personPk, ActiveCdcDestination destination, DateTimeOffset now, int maxBytes,
        string scenarioId, string label, string markerSuffix) => Write(runId, personPk, destination, now, maxBytes, scenarioId, label, markerSuffix);

    private static (SyntheticCdcEvent Event, ActiveCdcFixtureSummary Summary) Write(Guid runId, int? personPk, ActiveCdcDestination destination, DateTimeOffset now, int maxBytes,
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
            if (personPk is { } pk) json.WriteNumber("PersonPK", pk);
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
            SyntheticPersonPk = personPk ?? 0, ExpectedPersonId = personPk is { } key ? ExpectedPersonId(key) : Guid.Empty, Marker = marker, SyntheticBirthDate = birth, AgeYears = age,
            Fields = [.. ActiveCdcScenarioCatalog.NormalPerson.Fields.Where(f => personPk is not null || f != "PersonPK")], PayloadSha256 = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant(), PayloadBytes = body.Length,
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
    public static ActiveCdcContractManifest Evaluate(ActiveCdcScenario scenario, IqrSourceSnapshot? snapshot, Guid? latestSnapshotId, IEnumerable<string>? additionalReviewedArchives = null)
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
        if (scenario.Id == ActiveCdcScenarioCatalog.InvalidThenValidId)
        {
            var identity = path.Fields.FirstOrDefault(f => f.Key == $"CDC {record}.PersonPK")?.Steps
                .Any(s => s.Field == "PersonId" && s.Transformation == FieldTransformation.Derived && s.Location?.File.EndsWith("PersonMapper.cs", StringComparison.Ordinal) == true) == true;
            if (!identity)
                return Seal(bound with { Status = ActiveCdcContractStatus.Incompatible, ConfirmedFields = confirmed, MissingFields = missing, InvalidFixture = scenario.InvalidFixture, InvalidFixtureStatus = "Needs review",
                    Detail = "The snapshot does not show PersonMapper deriving the Person identity from PersonPK, so the invalid fixture's premise is not supported by this source." });
            var reviewed = InvalidFixtureReview.ReviewedArchives.Contains(snapshot.Archive.Sha256)
                || (additionalReviewedArchives ?? []).Contains(snapshot.Archive.Sha256, StringComparer.OrdinalIgnoreCase);
            bound = bound with
            {
                InvalidFixture = scenario.InvalidFixture, InvalidFixtureStatus = reviewed ? "Reviewed" : "Needs review",
                InvalidFixtureDetail = reviewed
                    ? $"Reviewed against archive {snapshot.Archive.Sha256[..12]}…: {string.Join(" ", InvalidFixtureReview.ReviewedPath)}"
                    : $"Archive {snapshot.Archive.Sha256[..Math.Min(12, snapshot.Archive.Sha256.Length)]}… was not reviewed for this fixture. The source analyzer does not see the mapper's null gate, so the invalid behavior must be re-reviewed before sending.",
                DeveloperCoverage = InvalidInputCoverage(snapshot),
            };
        }
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

    /// <summary>Developer tests near the invalid path (discovered, not executed): mapper-null discards, discard-with-checkpoint and envelope rejection. None is the exact missing-PersonPK case unless its name says so.</summary>
    private static List<string> InvalidInputCoverage(IqrSourceSnapshot snapshot) => snapshot.Tests
        .Where(t => t.Method.Contains("MapperReturnsNull", StringComparison.Ordinal) || (t.Method.Contains("Discarded", StringComparison.Ordinal) && t.Method.Contains("Checkpoint", StringComparison.Ordinal))
            || (t.Method.Contains("PersonPK", StringComparison.Ordinal) && (t.Method.Contains("Missing", StringComparison.Ordinal) || t.Method.Contains("Null", StringComparison.Ordinal)))
            || (t.Class == "CdcEnvelopeDeserializeTests" && t.Method.Contains("ReturnsNull", StringComparison.Ordinal)))
        .Select(t => $"{t.Class}.{t.Method} ({t.Layer.ToString().ToLowerInvariant()}{(t.Method.Contains("PersonPK", StringComparison.Ordinal) ? "" : ", related")})").Distinct().ToList();

    private static ActiveCdcContractManifest Seal(ActiveCdcContractManifest m)
    {
        var text = string.Join("|", m.ScenarioId, m.ScenarioVersion, m.FixtureSchemaVersion, m.SourceSnapshotId, m.ArchiveSha256, m.Status, string.Join(",", m.ConfirmedFields));
        if (m.InvalidFixture.Length > 0) text += $"|{m.InvalidFixture}|{m.InvalidFixtureStatus}";
        return m with { Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16] };
    }
}
