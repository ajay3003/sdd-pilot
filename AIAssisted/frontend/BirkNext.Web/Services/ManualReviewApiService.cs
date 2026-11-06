using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.ManualReview;

namespace BirkNext.Web.Services;

/// <summary>Durable manual review decisions (append-only with history). A decision never changes an automated result.</summary>
public interface IManualReviewApiService
{
    Task<IReadOnlyList<ManualReviewDecisionView>?> ListAsync(string module, string scopeKey, string? subjectKey = null, CancellationToken ct = default);
    Task<(ManualReviewDecisionView? Decision, string? Error)> RecordAsync(ManualReviewDecisionRequest request, CancellationToken ct = default);
}

public sealed class ManualReviewApiService(HttpClient http) : IManualReviewApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ManualReviewDecisionView>?> ListAsync(string module, string scopeKey, string? subjectKey = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"api/manual-reviews?module={Uri.EscapeDataString(module)}&scope={Uri.EscapeDataString(scopeKey)}"
                + (subjectKey is null ? "" : $"&subject={Uri.EscapeDataString(subjectKey)}");
            return await http.GetFromJsonAsync<List<ManualReviewDecisionView>>(url, Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return null; }
    }

    public async Task<(ManualReviewDecisionView? Decision, string? Error)> RecordAsync(ManualReviewDecisionRequest request, CancellationToken ct = default)
    {
        // Validated before sending as well: a credential never leaves the browser in a decision.
        if (ManualReviewRules.Validate(request) is { } invalid) return (null, invalid);
        try
        {
            using var response = await http.PostAsJsonAsync("api/manual-reviews", request, Json, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<ManualReviewDecisionView>(Json, ct), null);
            return (null, (await response.Content.ReadAsStringAsync(ct)).Trim('"') is { Length: > 0 } message ? message : $"The decision was not saved ({(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (null, "The decision was not saved: the backend could not be reached."); }
    }
}
