using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Messaging;

namespace BirkNext.Web.Services;

/// <summary>Imported message evidence (read-only): import a file, list, view, delete. No call here reaches a namespace or a database.</summary>
public interface IMessageObservationApiService
{
    Task<(List<MessageObservationSummary>? Sets, string? Error)> ListAsync(string environmentId, CancellationToken ct = default);
    Task<(MessageObservationView? View, string? Error)> GetAsync(string environmentId, Guid id, CancellationToken ct = default);
    Task<(MessageObservationSummary? Set, string? Error)> ImportAsync(MessageObservationImport request, CancellationToken ct = default);
    Task<string?> DeleteAsync(string environmentId, Guid id, CancellationToken ct = default);
}

public sealed class MessageObservationApiService(HttpClient http) : IMessageObservationApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string Base = "api/message-observations";

    public async Task<(List<MessageObservationSummary>? Sets, string? Error)> ListAsync(string environmentId, CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<List<MessageObservationSummary>>($"{Base}?environmentId={Uri.EscapeDataString(environmentId)}", Json, ct) ?? [], null); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException("Message observations", ex).UserMessage); }
    }

    public async Task<(MessageObservationView? View, string? Error)> GetAsync(string environmentId, Guid id, CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<MessageObservationView>($"{Base}/{id}?environmentId={Uri.EscapeDataString(environmentId)}", Json, ct), null); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException("Message observations", ex).UserMessage); }
    }

    public async Task<(MessageObservationSummary? Set, string? Error)> ImportAsync(MessageObservationImport request, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.SendJsonForEnvironmentAsync(HttpMethod.Post, Base, request, Json, request.EnvironmentId, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<MessageObservationSummary>(Json, ct), null);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return (null, "You do not have permission to import evidence (configuration write permission).");
            return (null, await Message(response, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, BackendRequestClassifier.FromException("Message observations", ex).UserMessage); }
    }

    public async Task<string?> DeleteAsync(string environmentId, Guid id, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{Base}/{id}?environmentId={Uri.EscapeDataString(environmentId)}");
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode ? null : response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ? "You do not have permission to delete evidence." : await Message(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return BackendRequestClassifier.FromException("Message observations", ex).UserMessage; }
    }

    private static async Task<string> Message(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try { if (JsonDocument.Parse(text).RootElement is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("message", out var m)) return m.GetString() ?? text; }
        catch (JsonException) { }
        return string.IsNullOrWhiteSpace(text) ? $"The request failed (HTTP {(int)response.StatusCode})." : text.Trim('"');
    }
}
