using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.RuntimeAuthentication;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Shared runtime authentication (api/runtime-authentication): read-only resolution of Auto / Attended / Unattended / None per operation.</summary>
public interface IRuntimeAuthenticationApiService
{
    Task<IReadOnlyList<RuntimeAuthenticationResolution>?> ResolveAsync(IReadOnlyList<RuntimeAuthenticationRequest> requests, CancellationToken ct = default);
}

public sealed class RuntimeAuthenticationApiService(HttpClient http) : IRuntimeAuthenticationApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<RuntimeAuthenticationResolution>?> ResolveAsync(IReadOnlyList<RuntimeAuthenticationRequest> requests, CancellationToken ct = default)
    {
        try
        {
            var response = await http.PostAsJsonAsync("api/runtime-authentication/resolve", new RuntimeAuthenticationResolveRequest([.. requests]), Json, ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<List<RuntimeAuthenticationResolution>>(Json, ct) : null;
        }
        catch (Exception e) when (BackendRequestClassifier.IsRequestFailure(e)) { return null; }
    }
}

/// <summary>
/// The mode a person selected, per environment and operation, for this browser session. Keyed by environment id so switching project,
/// environment, page or workspace view never carries a choice — or an identity — to another environment. Nothing here is a credential.
/// </summary>
public sealed class RuntimeAuthenticationChoices
{
    private readonly Dictionary<(string Environment, string Operation), RuntimeAuthenticationMode> _choices = new();

    public RuntimeAuthenticationMode Get(string environmentId, string operationKey) =>
        _choices.GetValueOrDefault((environmentId, operationKey), RuntimeAuthenticationMode.Auto);

    public void Set(string environmentId, string operationKey, RuntimeAuthenticationMode mode)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return;
        if (mode == RuntimeAuthenticationMode.Auto) _choices.Remove((environmentId, operationKey));
        else _choices[(environmentId, operationKey)] = mode;
    }
}

public static class RuntimeAuthenticationRequests
{
    /// <summary>The request for <paramref name="profile"/>: its saved testing method, proxy context fingerprint and effective sign-in requirement
    /// (null when neither configured nor observed).</summary>
    public static RuntimeAuthenticationRequest For(FrontendAnalysisProfile profile, string operationKey, RuntimeAuthenticationMode mode, DetectionSnapshot? snapshot)
    {
        var identity = ReviewAuthenticationIdentity.For(profile);
        var effective = EffectiveAuthentication.Resolve(profile, snapshot);
        return new RuntimeAuthenticationRequest
        {
            OperationKey = operationKey, Mode = mode, EnvironmentId = profile.Id, ContextFingerprint = identity.ContextFingerprint, FrontendUrl = profile.TargetUrl,
            SignInRequired = effective.SignInValue.Origin == AuthenticationValueOrigin.Unknown ? null : effective.SignInRequired,
            TestingMethod = identity.Method,
        };
    }
}
