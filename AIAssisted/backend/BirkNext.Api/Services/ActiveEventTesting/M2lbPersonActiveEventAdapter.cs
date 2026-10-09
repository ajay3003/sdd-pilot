using System.Security.Cryptography;
using System.Text.Json;
using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ActiveEventTesting;

/// <summary>Compatibility boundary from the existing M2LB fixture to the project-neutral execution event.</summary>
public static class M2lbPersonActiveEventAdapter
{
    public const string ExtensionId = "m2lb.person";

    public static GeneratedActiveEvent ToGenerated(SyntheticCdcEvent source)
    {
        var body = source.Body.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var eventId = source.Label.Length == 0 ? source.RunId.ToString("N") : $"{source.RunId:N}-{source.Label}";
        using var document = JsonDocument.Parse(body);
        var payload = document.RootElement.GetProperty("payload");
        var after = payload.GetProperty("after");
        var personPk = after.TryGetProperty("PersonPK", out var key) && key.TryGetInt32(out var parsedKey) ? parsedKey : (int?)null;
        var marker = after.TryGetProperty("Fornavn", out var firstName) ? firstName.GetString() ?? "" : "";
        var birthDate = after.TryGetProperty("Født", out var birth) && birth.TryGetInt32(out var dayNumber)
            ? DateOnly.FromDateTime(DateTime.UnixEpoch).AddDays(dayNumber).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "";
        var replayOf = source.Label == "A2" ? $"{source.RunId:N}-A" : null;
        var fields = personPk is null
            ? ActiveCdcScenarioCatalog.NormalPerson.Fields.Where(field => field != "PersonPK").ToArray()
            : ActiveCdcScenarioCatalog.NormalPerson.Fields.ToArray();
        return new GeneratedActiveEvent
        {
            EventId = eventId,
            ExtensionId = ExtensionId,
            ScenarioId = source.ScenarioId,
            SequenceIndex = source.Label switch { "A" or "I" => 0, "A2" or "V" => 1, "B" => 2, _ => 0 },
            Operation = ActiveEventOperation.Create,
            Body = body,
            BodySha256 = hash,
            BodyBytes = body.Length,
            ContentType = "application/json",
            Correlation = new ActiveEventCorrelation { RunId = source.RunId, EventId = eventId, EventFingerprint = hash,
                SyntheticMarker = marker, SafeSourceIdentity = personPk?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ReplaysEventId = replayOf, ReplayKind = replayOf is null ? ActiveEventReplayKind.None : ActiveEventReplayKind.ExactReplay },
            SafeDisplayMetadata = new Dictionary<string, string>
            {
                ["sequenceLabel"] = source.Label,
                ["role"] = source.Label switch { "A" => "First create", "A2" => "Exact replay", "B" => "Following control", "I" => "Reviewed invalid input", "V" => "Valid control", _ => "Synthetic create" },
                ["syntheticPersonPk"] = personPk?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                ["expectedPersonId"] = personPk is { } value ? PersonCdcFixtureBuilder.ExpectedPersonId(value).ToString("D") : "",
                ["marker"] = marker,
                ["syntheticBirthDate"] = birthDate,
                ["invalidCondition"] = personPk is null ? InvalidFixtureReview.Condition : "",
                ["fields"] = string.Join(",", fields),
            },
            TransportProperties = source.Label.Length == 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { ["BirkNextMessage"] = source.Label },
        };
    }
}
