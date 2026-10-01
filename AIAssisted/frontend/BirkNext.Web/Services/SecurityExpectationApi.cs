using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Integrations;
using BirkNext.SecurityExpectations;

namespace BirkNext.Web.Services;

public interface ISecurityExpectationApi
{
    Task<IReadOnlyList<IqrSourceSnapshot>> SourcesAsync(string environmentId);
    Task<IReadOnlyList<SecurityExpectationDiscoveryResult>> HistoryAsync(string environmentId);
    Task<SecurityExpectationDiscoveryResult> DiscoverAsync(string environmentId, SecurityDiscoveryRequest request);
    Task<SecurityCandidateReviewResponse> ReviewAsync(string environmentId, SecurityCandidateReviewRequest request, bool accept);
}
public sealed class SecurityExpectationApi(HttpClient http) : ISecurityExpectationApi
{
    private static string Path(string env) => $"api/target-environments/{Uri.EscapeDataString(env)}/security-expectations/";
    public async Task<IReadOnlyList<IqrSourceSnapshot>> SourcesAsync(string env)
    {
        // Select the latest standalone Source Analysis directly: the general source history has
        // a bounded list that also contains IQR integration snapshots.
        using var response = await http.GetAsync(Path(env) + "source-snapshot");
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return [];
        response.EnsureSuccessStatusCode();
        var snapshot = await response.Content.ReadFromJsonAsync<IqrSourceSnapshot>();
        return snapshot is null ? [] : [snapshot];
    }
    public async Task<IReadOnlyList<SecurityExpectationDiscoveryResult>> HistoryAsync(string env) =>
        await http.GetFromJsonAsync<List<SecurityExpectationDiscoveryResult>>(Path(env) + "discovery") ?? [];
    public async Task<SecurityExpectationDiscoveryResult> DiscoverAsync(string env, SecurityDiscoveryRequest request) =>
        await Read<SecurityExpectationDiscoveryResult>(await http.PostAsJsonAsync(Path(env) + "discover", request));
    public async Task<SecurityCandidateReviewResponse> ReviewAsync(string env, SecurityCandidateReviewRequest request, bool accept) =>
        await Read<SecurityCandidateReviewResponse>(await http.PostAsJsonAsync(Path(env) + (accept ? "accept" : "reject"), request));
    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        using(response) {
            if(!response.IsSuccessStatusCode) {
                var text = await response.Content.ReadAsStringAsync();
                try { using var doc = JsonDocument.Parse(text); if(doc.RootElement.TryGetProperty("message",out var message)) throw new InvalidOperationException(message.GetString()); }
                catch(JsonException) { }
                throw new InvalidOperationException("Source discovery could not be completed; refresh source evidence.");
            }
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
    }
}
