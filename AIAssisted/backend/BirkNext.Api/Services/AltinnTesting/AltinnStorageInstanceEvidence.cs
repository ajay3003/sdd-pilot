using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>Safe process facts of one Altinn instance.</summary>
public sealed record AltinnTaskEvidence(string? ElementId, string? Name, string? AltinnTaskType, string? FlowType, DateTimeOffset? Started, DateTimeOffset? Ended,
    DateTimeOffset? ValidatedAt, bool? CanCompleteTask);

/// <summary>
/// Safe metadata of one Altinn data element. The data id is masked and the storage path redacted; content, file names, content hashes and
/// reference ids are not kept (a hash of a small form could confirm a guessed identifier). This is Altinn Storage's own path — not an MU blob.
/// </summary>
public sealed record AltinnDataElementEvidence(string DataReference, string? DataType, string? ContentType, long? Size, bool? Locked, string? RedactedStoragePath,
    bool ContentHashPresent, int ReferenceCount, IReadOnlyList<string> MetadataKeys);

/// <summary>
/// What a read-only Storage GET shows about one instance, reduced to safe facts. Instance-owner fields (person/organisation number, user name,
/// external identifier) are never read; the party id and instance GUID are not kept beyond a short reference.
/// </summary>
public sealed record AltinnStorageInstanceEvidence(
    string InstanceReference,
    bool IdentityMatches,
    string? AppId,
    DateTimeOffset? Created,
    DateTimeOffset? LastChanged,
    DateTimeOffset? ProcessStarted,
    string? StartEvent,
    AltinnTaskEvidence? CurrentTask,
    DateTimeOffset? ProcessEnded,
    string? EndEvent,
    IReadOnlyList<AltinnDataElementEvidence> Data);

/// <summary>
/// The one read Altinn instance evidence uses: GET on Altinn Storage (TT02) for an instance, with the tester's Altinn token. Storage's GET
/// instance has no side effect; the app's GET instance is deliberately not used because it updates the read status for person callers.
/// The request passes <see cref="AltinnTestingRequestGuard"/>; nothing else is ever sent.
/// </summary>
public sealed class AltinnStorageInstanceReader(IHttpClientFactory http, AltinnTestingPolicy policy)
{
    private static readonly Regex GuidPattern = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.CultureInvariant);
    private static readonly Regex DigitRun = new(@"\d{4,}", RegexOptions.CultureInvariant);
    internal const int MaxResponseBytes = 1024 * 1024;
    private const int MaxText = 200;

    public async Task<(AltinnCallResult Result, AltinnStorageInstanceEvidence? Evidence)> ReadAsync(string token, long instanceOwnerPartyId, Guid instanceGuid, CancellationToken ct)
    {
        var client = http.CreateClient(AltinnTestingRequestGuard.ClientName);
        client.MaxResponseContentBufferSize = MaxResponseBytes; // an instance document is a few KB; anything larger is refused, not parsed
        using var request = new HttpRequestMessage(HttpMethod.Get, policy.Endpoints.StorageInstance(instanceOwnerPartyId, instanceGuid));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return (new AltinnCallResult(false, response.StatusCode, response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Altinn Storage refused the TEST token (HTTP 401): sign in again.",
                HttpStatusCode.Forbidden => "The signed-in TEST person may not read this instance (HTTP 403).",
                HttpStatusCode.NotFound => "Altinn Storage has no such instance, or it is not visible to the signed-in person (HTTP 404).",
                _ => $"Altinn Storage answered HTTP {(int)response.StatusCode}.",
            }), null);
        var evidence = Parse(await response.Content.ReadAsStringAsync(ct), instanceOwnerPartyId, instanceGuid);
        return evidence is null
            ? (new AltinnCallResult(false, response.StatusCode, "Altinn Storage answered without a readable instance."), null)
            : (new AltinnCallResult(true, response.StatusCode, "Instance read from Altinn Storage."), evidence);
    }

    /// <summary>Reads only the safe fields. Owner and user fields are not touched, so they cannot reach evidence, history or logs.</summary>
    internal static AltinnStorageInstanceEvidence? Parse(string json, long instanceOwnerPartyId, Guid instanceGuid)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        var id = String(root, "id");
        var identityMatches = string.Equals(id, $"{instanceOwnerPartyId}/{instanceGuid:D}", StringComparison.OrdinalIgnoreCase);

        AltinnTaskEvidence? task = null;
        DateTimeOffset? started = null, ended = null;
        string? startEvent = null, endEvent = null;
        if (root.TryGetProperty("process", out var process) && process.ValueKind == JsonValueKind.Object)
        {
            started = Date(process, "started");
            startEvent = String(process, "startEvent");
            ended = Date(process, "ended");
            endEvent = String(process, "endEvent");
            if (process.TryGetProperty("currentTask", out var current) && current.ValueKind == JsonValueKind.Object)
            {
                DateTimeOffset? validatedAt = null;
                bool? canComplete = null;
                if (current.TryGetProperty("validated", out var validated) && validated.ValueKind == JsonValueKind.Object)
                {
                    validatedAt = Date(validated, "timestamp");
                    canComplete = validated.TryGetProperty("canCompleteTask", out var can) && can.ValueKind is JsonValueKind.True or JsonValueKind.False ? can.GetBoolean() : null;
                }
                task = new AltinnTaskEvidence(String(current, "elementId"), String(current, "name"), String(current, "altinnTaskType"), String(current, "flowType"),
                    Date(current, "started"), Date(current, "ended"), validatedAt, canComplete);
            }
        }

        var data = new List<AltinnDataElementEvidence>();
        if (root.TryGetProperty("data", out var elements) && elements.ValueKind == JsonValueKind.Array)
            foreach (var element in elements.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Take(50))
            {
                var dataId = String(element, "id");
                var metadataKeys = element.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Array
                    ? metadata.EnumerateArray().Select(item => String(item, "key")).OfType<string>().Select(MaskValue).Distinct(StringComparer.Ordinal).Take(10).ToArray()
                    : [];
                data.Add(new AltinnDataElementEvidence(
                    Guid.TryParse(dataId, out var dataGuid) ? $"data-{dataGuid.ToString("N")[..8]}" : "data-(unreadable id)",
                    String(element, "dataType"), String(element, "contentType"),
                    element.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : null,
                    element.TryGetProperty("locked", out var locked) && locked.ValueKind is JsonValueKind.True or JsonValueKind.False ? locked.GetBoolean() : null,
                    String(element, "blobStoragePath") is { } path ? RedactStoragePath(path) : null,
                    String(element, "contentHash") is { Length: > 0 },
                    element.TryGetProperty("refs", out var refs) && refs.ValueKind == JsonValueKind.Array ? refs.GetArrayLength() : 0,
                    metadataKeys));
            }

        return new AltinnStorageInstanceEvidence($"inst-{instanceGuid.ToString("N")[..8]}", identityMatches, String(root, "appId"),
            Date(root, "created"), Date(root, "lastChanged"), started, startEvent, task, ended, endEvent, data);
    }

    /// <summary>
    /// Keeps the shape of an Altinn Storage path ("org/app/{guid}/data/{guid}") without its identifiers: GUIDs, digit runs and unusual
    /// segments are masked. The raw path is never stored or shown.
    /// </summary>
    internal static string RedactStoragePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(12).Select(segment =>
            GuidPattern.IsMatch(segment) ? "{guid}" : DigitRun.IsMatch(segment) ? "{masked}" : segment.Length > 40 || segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.') ? "{masked}" : segment));

    private static string MaskValue(string value) => DigitRun.IsMatch(value) || GuidPattern.IsMatch(value) || value.Length > 40 ? "{masked}" : value;

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            ? text.Length > MaxText ? text[..MaxText] + "…" : text : null;

    private static DateTimeOffset? Date(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var at) ? at : null;
}
