using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>One Altinn App API answer reduced to safe facts: HTTP status, and for instances only ids/process state — never the body.</summary>
public sealed record AltinnCallResult(bool Success, HttpStatusCode Status, string Detail);

public sealed record AltinnInstanceState(long PartyId, Guid InstanceGuid, Guid? FormDataGuid, string? CurrentTaskId, DateTimeOffset? Ended, string? EndEvent, DateTimeOffset? LastChanged);

public sealed record AltinnValidationSummary(int Errors, int Warnings, IReadOnlyList<string> Codes);

/// <summary>
/// The Altinn 3 App API calls a TT02 submission needs, against one approved app (base address built from the validated org/app only):
/// resolve the signed-in party, create an instance, store form data, validate, advance the process and read the instance back. Every URL
/// is composed from that base, a numeric party id and GUIDs; every request passes <see cref="AltinnTestingRequestGuard"/>; redirects are
/// not followed. Bodies are parsed for ids and states only and never logged or returned.
/// </summary>
public sealed class AltinnAppApiClient(IHttpClientFactory http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<(AltinnCallResult Result, long? PartyId)> CurrentPartyAsync(Uri appBase, string token, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, new Uri(appBase, "api/authorization/parties/current?returnPartyObject=true"), token, null, ct);
        if (!response.IsSuccessStatusCode) return (Fail(response, "resolving the signed-in party"), null);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.TryGetProperty("partyId", out var party) && party.TryGetInt64(out var id)
            ? (new AltinnCallResult(true, response.StatusCode, "Altinn accepted the TEST token and resolved the signed-in party."), id)
            : (new AltinnCallResult(false, response.StatusCode, "Altinn answered without a party id."), null);
    }

    public async Task<(AltinnCallResult Result, AltinnInstanceState? Instance)> CreateInstanceAsync(Uri appBase, string token, long partyId, string formDataTypeId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, new Uri(appBase, $"instances?instanceOwnerPartyId={partyId.ToString(CultureInfo.InvariantCulture)}"), token, null, ct);
        if (!response.IsSuccessStatusCode) return (Fail(response, "creating the instance"), null);
        var instance = ParseInstance(await response.Content.ReadAsStringAsync(ct), formDataTypeId);
        return instance is null
            ? (new AltinnCallResult(false, response.StatusCode, "Altinn answered without a readable instance id."), null)
            : (new AltinnCallResult(true, response.StatusCode, "Instance created."), instance);
    }

    /// <summary>Stores the typed form (JSON or XML, as the contract says): PUT on the instance's form data element, or POST when the app created none.</summary>
    public async Task<(AltinnCallResult Result, Guid? DataGuid)> PutFormDataAsync(Uri appBase, string token, AltinnInstanceState instance, string formDataTypeId, string body,
        string contentType, CancellationToken ct)
    {
        var path = instance.FormDataGuid is { } dataGuid
            ? $"instances/{instance.PartyId.ToString(CultureInfo.InvariantCulture)}/{instance.InstanceGuid:D}/data/{dataGuid:D}"
            : $"instances/{instance.PartyId.ToString(CultureInfo.InvariantCulture)}/{instance.InstanceGuid:D}/data?dataType={Uri.EscapeDataString(formDataTypeId)}";
        using var response = await SendAsync(instance.FormDataGuid is null ? HttpMethod.Post : HttpMethod.Put, new Uri(appBase, path), token,
            new StringContent(body, Encoding.UTF8, contentType), ct);
        if (!response.IsSuccessStatusCode) return (Fail(response, "storing the form data"), null);
        Guid? id = instance.FormDataGuid;
        if (id is null)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            id = json.RootElement.TryGetProperty("id", out var element) && Guid.TryParse(element.GetString(), out var created) ? created : null;
        }
        return (new AltinnCallResult(true, response.StatusCode, "Form data stored."), id);
    }

    public async Task<(AltinnCallResult Result, AltinnValidationSummary? Summary)> ValidateAsync(Uri appBase, string token, AltinnInstanceState instance, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, new Uri(appBase, $"{InstancePath(instance)}/validate"), token, null, ct);
        if (!response.IsSuccessStatusCode) return (Fail(response, "validating the instance"), null);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var issues = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().ToArray() : [];
        // Severity 1 = Error, 2 = Warning (Altinn ValidationIssueSeverity). Only codes are kept — never descriptions, field values or paths.
        int Severity(JsonElement issue) => issue.TryGetProperty("severity", out var severity) && severity.TryGetInt32(out var value) ? value : 0;
        var codes = issues.Select(issue => issue.TryGetProperty("code", out var code) ? code.GetString() : null).OfType<string>().Distinct(StringComparer.Ordinal).Take(20).ToArray();
        return (new AltinnCallResult(true, response.StatusCode, "Validation returned."), new AltinnValidationSummary(issues.Count(issue => Severity(issue) == 1), issues.Count(issue => Severity(issue) == 2), codes));
    }

    public async Task<(AltinnCallResult Result, string? CurrentTaskId, DateTimeOffset? Ended)> ProcessNextAsync(Uri appBase, string token, AltinnInstanceState instance, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, new Uri(appBase, $"{InstancePath(instance)}/process/next"), token, null, ct);
        if (!response.IsSuccessStatusCode) return (Fail(response, "advancing the process"), null, null);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var (task, ended, _) = Process(json.RootElement);
        return (new AltinnCallResult(true, response.StatusCode, "Process advanced."), task, ended);
    }

    public async Task<(AltinnCallResult Result, AltinnInstanceState? Instance)> GetInstanceAsync(Uri appBase, string token, AltinnInstanceState instance, string formDataTypeId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, new Uri(appBase, InstancePath(instance)), token, null, ct);
        if (!response.IsSuccessStatusCode) return (Fail(response, "reading the instance"), null);
        var read = ParseInstance(await response.Content.ReadAsStringAsync(ct), formDataTypeId);
        return read is null ? (new AltinnCallResult(false, response.StatusCode, "Altinn answered without a readable instance."), null)
            : (new AltinnCallResult(true, response.StatusCode, "Instance read."), read);
    }

    internal static AltinnInstanceState? ParseInstance(string text, string formDataTypeId)
    {
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        var parts = id?.Split('/');
        if (parts is not { Length: 2 } || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var party) || !Guid.TryParse(parts[1], out var guid)) return null;
        Guid? data = null;
        if (root.TryGetProperty("data", out var elements) && elements.ValueKind == JsonValueKind.Array)
            foreach (var element in elements.EnumerateArray())
                if (element.TryGetProperty("dataType", out var type) && type.GetString() == formDataTypeId && element.TryGetProperty("id", out var dataId) && Guid.TryParse(dataId.GetString(), out var parsed))
                { data = parsed; break; }
        var (task, ended, endEvent) = root.TryGetProperty("process", out var process) ? Process(process) : (null, null, null);
        DateTimeOffset? lastChanged = root.TryGetProperty("lastChanged", out var changed) && changed.TryGetDateTimeOffset(out var at) ? at : null;
        return new AltinnInstanceState(party, guid, data, task, ended, endEvent, lastChanged);
    }

    private static (string? Task, DateTimeOffset? Ended, string? EndEvent) Process(JsonElement process)
    {
        var task = process.TryGetProperty("currentTask", out var current) && current.ValueKind == JsonValueKind.Object && current.TryGetProperty("elementId", out var element) ? element.GetString() : null;
        DateTimeOffset? ended = process.TryGetProperty("ended", out var end) && end.ValueKind == JsonValueKind.String && end.TryGetDateTimeOffset(out var at) ? at : null;
        var endEvent = process.TryGetProperty("endEvent", out var endEventElement) && endEventElement.ValueKind == JsonValueKind.String ? endEventElement.GetString() : null;
        return (task, ended, endEvent);
    }

    private static string InstancePath(AltinnInstanceState instance) => $"instances/{instance.PartyId.ToString(CultureInfo.InvariantCulture)}/{instance.InstanceGuid:D}";

    private static AltinnCallResult Fail(HttpResponseMessage response, string action) => new(false, response.StatusCode, response.StatusCode switch
    {
        HttpStatusCode.Unauthorized => $"Altinn refused the TEST token while {action} (HTTP 401): sign in again.",
        HttpStatusCode.Forbidden => $"The signed-in TEST person is not authorized for {action} (HTTP 403).",
        _ => $"Altinn answered HTTP {(int)response.StatusCode} while {action}.",
    });

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, string token, HttpContent? content, CancellationToken ct)
    {
        var client = http.CreateClient(AltinnTestingRequestGuard.ClientName);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await client.SendAsync(request, ct);
    }
}
