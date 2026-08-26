using System.Net.Http.Json;
using BirkNext.Web.Models;
namespace BirkNext.Web.Services;

public interface IFrontendPassiveSecurityApiService
{
    Task<PassiveSecurityResultDto> ReviewAsync(string targetUrl, string profileId, string configuredBaseUrl,
        string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default);
}
public sealed class FrontendPassiveSecurityApiService(HttpClient client) : IFrontendPassiveSecurityApiService
{
    public async Task<PassiveSecurityResultDto> ReviewAsync(string targetUrl, string profileId, string configuredBaseUrl,
        string environmentType, bool requiresAuthentication, CancellationToken cancellationToken = default)
    {
        try { var response = await client.PostAsJsonAsync("api/frontend-passive-security/review", new { targetUrl, environmentProfileId=profileId, configuredBaseUrl, environmentType, requiresAuthentication }, cancellationToken);
            if (!response.IsSuccessStatusCode) return Error(targetUrl, $"Passive security API returned HTTP {(int)response.StatusCode}.");
            return await response.Content.ReadFromJsonAsync<PassiveSecurityResultDto>(cancellationToken: cancellationToken) ?? Error(targetUrl,"Passive security API returned no result."); }
        catch (Exception ex) { return Error(targetUrl, ex.Message); }
    }
    private static PassiveSecurityResultDto Error(string url,string error) => new(PassiveSecurityExecutionStatusDto.EngineError,"ZAP Passive","Passive",null,url,null,null,null,null,0,0,0,0,[],[],error,"Configured target only; no spidering",null);
}
