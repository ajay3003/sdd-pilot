using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Services.ActiveEventTesting.Debezium;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting.Providers.M2lbPerson;

/// <summary>One reviewed Person scenario: its stable id, the Person fields it sends, and what its result does and does not mean.</summary>
public sealed record PersonScenario(
    string Id, string Version, string Name, string Category, string Description, int EventCount, ActiveEventReplayKind ReplayKind,
    bool RequiresContinuity, string Limitation, string ResultMeaning, string ResultDoesNotMean);

/// <summary>Built-in, reviewed Person scenarios. There is no way to supply a payload from outside the backend.</summary>
public static class M2lbPersonScenarios
{
    public const string Table = "Person";
    /// <summary>Field names are BiRK's real spelling (payload names are never sanitized — only Event Hub entity names are).</summary>
    public static readonly IReadOnlyList<string> Fields = ["PersonPK", "Fornavn", "Etternavn", "Født", "UsikkerFødselsdato", "UsikkerFødselsnummer", "KjønnTypeFK", "EndretDato"];

    public const string NormalPersonId = "person.normal.create";
    public const string SamePersonPkReplayId = "person.same-personpk-replay";
    public const string InvalidThenValidId = "person.invalid-then-valid";

    public static readonly PersonScenario NormalPerson = new(NormalPersonId, "1", "Normal Person", "Transport",
        "One synthetic Debezium create (op \"c\") on the Person table: synthetic name marker, synthetic birth date (age 20, Europe/Oslo), " +
        "uncertain national id derived by the adapter from the date only, unknown gender. No national id, DUF number or child data is sent.",
        1, ActiveEventReplayKind.None, false,
        "No read-only Person verification exists, so an accepted event is at most limited evidence.",
        "The synthetic Person was observed downstream by a verifier.",
        "Without a downstream verifier it does not mean the Person was persisted, an outbox event was created or Service Bus delivered anything.");

    /// <summary>
    /// Runtime resilience, not duplicate correctness: A (key X), A2 (byte-identical replay of A), then B (key Y) as a following valid control.
    /// It asks only whether the observable Event Hub → Person Adapter consumer path keeps going after a replay.
    /// </summary>
    public static readonly PersonScenario SamePersonPkReplay = new(SamePersonPkReplayId, "1", "Same PersonPK replay", "Runtime resilience",
        "Sends one synthetic Person, replays the same source identity, then sends another valid Person to check whether the observable consumer path continues.",
        3, ActiveEventReplayKind.ExactReplay, true,
        "This does not verify database idempotency or duplicate Person handling.",
        "Runtime continuity after replay was observed: the Event Hub consumer advanced beyond the replay and the following control event without becoming stuck.",
        "It does not mean the replay was processed successfully, the control Person was persisted, database idempotency was proven, no outbox duplication occurred, " +
        "Person row count or overwrite behavior was correct, natural-key duplicates were handled, or Service Bus was verified. A checkpoint past an event shows consumer progression, not successful handling.");

    /// <summary>
    /// Fault resilience: one controlled invalid Person CDC event (valid Debezium envelope, PersonPK missing) followed by one valid synthetic
    /// Person. It asks only whether the observable Event Hub consumer keeps advancing after the invalid input.
    /// </summary>
    public static readonly PersonScenario InvalidThenValid = new(InvalidThenValidId, "1", "Invalid Person → valid Person", "Fault resilience",
        "Sends one controlled invalid Person CDC event followed by a valid synthetic Person event to check whether the observable consumer continues advancing.",
        2, ActiveEventReplayKind.ControlAfterInvalid, true,
        "This verifies consumer continuity, not correct handling or persistence of either message.",
        "A controlled invalid Person CDC event was followed by a valid Person CDC event, and the observable Event Hub consumer advanced beyond the valid control event without becoming stuck.",
        "This does not prove that the invalid event was rejected for the correct reason or that the valid Person was persisted. It also does not prove the invalid event left no database state, " +
        "the mapper output was correct, or that no exception, retry, fault record, outbox event or Service Bus effect occurred.");

    public static IReadOnlyList<PersonScenario> All { get; } = [NormalPerson, SamePersonPkReplay, InvalidThenValid];
    public static PersonScenario? Find(string? id) => All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// The reviewed invalid fixture. The M2LB source analyzer records that PersonPK derives the Person identity but not the mapper's null gate,
/// so the review is bound to the exact archive it was made against; any other archive is "Needs review" until re-reviewed (an operator can
/// add re-reviewed archive hashes in <c>ActiveEventTesting:Providers:m2lb.person:InvalidFixtureReviewedArchives</c>).
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

/// <summary>
/// The Person record in the envelope the Person Adapter reads (<c>payload.op</c>, <c>payload.source.table</c>, <c>payload.after</c>), built
/// by the generic <see cref="DebeziumEventBuilder"/>. Values are synthetic: a marker name, a birth date 20 years back (AgeFilter keeps create
/// events up to 25 years), <c>UsikkerFødselsnummer = true</c> so the adapter's 6-character date component lands in the uncertain field,
/// <c>KjønnTypeFK = 3</c> (unknown). No Fødselsnummer, Personnummer, Dufnummer or Navn is sent.
/// </summary>
public static class PersonCdcFixtureBuilder
{
    public const int FixtureSchemaVersion = 1;
    private static readonly TimeZoneInfo Oslo = FindOslo();

    public static string Marker(Guid runId) => $"BIRKNEXT-TEST-{runId.ToString("N")[..12].ToUpperInvariant()}";

    /// <summary>The adapter's PersonId derivation (PersonMapper.ToDeterministicGuid): SHA-256 of the decimal PersonPK text, first 16 bytes.</summary>
    public static Guid ExpectedPersonId(long personPk) => new(SHA256.HashData(Encoding.UTF8.GetBytes(personPk.ToString(System.Globalization.CultureInfo.InvariantCulture)))[..16]);

    public static DateOnly SyntheticBirthDate(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Oslo).Date).AddYears(-20).AddDays(-30);

    /// <summary>The synthetic Person row; <paramref name="personPk"/> null omits PersonPK (the reviewed invalid fixture — nothing else differs).</summary>
    public static DebeziumRow Row(long? personPk, string marker, DateTimeOffset now)
    {
        var row = new DebeziumRow();
        if (personPk is { } pk) row.Add("PersonPK", DebeziumValue.Number(pk));
        row.Add("Fornavn", DebeziumValue.Text(marker));
        row.Add("Etternavn", DebeziumValue.Text("Synthetic"));
        row.Add("Født", DebeziumValue.Number(SyntheticBirthDate(now).DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber));
        row.Add("UsikkerFødselsdato", DebeziumValue.Boolean(false));
        row.Add("UsikkerFødselsnummer", DebeziumValue.Boolean(true));
        row.Add("KjønnTypeFK", DebeziumValue.Number(3));
        row.Add("EndretDato", DebeziumValue.Number(now.ToUnixTimeMilliseconds()));
        return row;
    }

    public static DebeziumEnvelope Create(long? personPk, string marker, DebeziumSource source, DateTimeOffset now, int maxBytes) =>
        DebeziumEventBuilder.Build(new DebeziumChange(ActiveEventOperation.Create, source, null, Row(personPk, marker, now), now), maxBytes);

    private static TimeZoneInfo FindOslo()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
    }
}
