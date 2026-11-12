using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;

namespace BirkNext.Web.Services;

/// <summary>Whether this browser must connect to the local BirkNext service (docs/security/localhost-session-design-2026-11-06.md).</summary>
public sealed class LocalSessionState
{
    public const string ClientHeader = "X-BirkNext-Client";
    public const string ChallengeScheme = "BirkNext-LocalSession";

    public bool ConnectRequired { get; private set; }
    public event Action? Changed;

    public void MarkRequired()
    {
        if (ConnectRequired) return;
        ConnectRequired = true;
        Changed?.Invoke();
    }

    public void MarkConnected()
    {
        ConnectRequired = false;
        Changed?.Invoke();
    }
}

/// <summary>The backend origin the session applies to: requests elsewhere (a target application) never carry it.</summary>
public sealed record LocalSessionBackend(Uri BaseAddress);

/// <summary>
/// For requests to the BirkNext backend only: sends the HttpOnly session cookie (credentials: include) and the client header the backend requires
/// for state changes, and reports a session challenge (401 with the BirkNext-LocalSession scheme). It never retries a request: after connecting,
/// the page reloads, so no mutation is replayed implicitly.
/// </summary>
public sealed class LocalSessionHandler(LocalSessionBackend backend, LocalSessionState state) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var toBackend = request.RequestUri is { } uri && uri.IsAbsoluteUri
            && Uri.Compare(uri, backend.BaseAddress, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
        if (toBackend)
        {
            request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
            if (!request.Headers.Contains(LocalSessionState.ClientHeader)) request.Headers.Add(LocalSessionState.ClientHeader, "web");
        }
        var response = await base.SendAsync(request, cancellationToken);
        if (toBackend && response.StatusCode == HttpStatusCode.Unauthorized
            && response.Headers.WwwAuthenticate.Any(h => string.Equals(h.Scheme, LocalSessionState.ChallengeScheme, StringComparison.Ordinal)))
            state.MarkRequired();
        return response;
    }
}

/// <summary>The bootstrap calls: whether this browser is connected, and exchanging the code shown in the BirkNext start window.</summary>
public sealed class LocalSessionApiService(HttpClient http)
{
    private sealed record Status(bool Connected);
    private sealed record Problem(string? Message);

    /// <summary>True/false when the backend answered; null when it could not be reached (connectivity is reported elsewhere).</summary>
    public async Task<bool?> ConnectedAsync(CancellationToken ct = default)
    {
        try { return (await http.GetFromJsonAsync<Status>("api/local-session", ct))?.Connected; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return null; }
    }

    public Task<(bool Connected, string? Message)> ConnectAsync(string code, CancellationToken ct = default) => PostAsync(new { code }, ct);

    /// <summary>Exchanges the one-time token the Start-BirkNext launcher put in this page's URL fragment (automatic connection; never the code).</summary>
    public Task<(bool Connected, string? Message)> ConnectWithBootstrapAsync(string bootstrapToken, CancellationToken ct = default) =>
        PostAsync(new { bootstrapToken }, ct);

    /// <summary>The bootstrap token in a URL fragment (<c>#bootstrap=…</c>, 64 hex characters), or null. Pure.</summary>
    public static string? BootstrapToken(string uri)
    {
        var hash = uri.IndexOf('#');
        if (hash < 0) return null;
        foreach (var part in uri[(hash + 1)..].Split('&'))
            if (part.StartsWith("bootstrap=", StringComparison.Ordinal) && part[10..] is { Length: 64 } t && t.All(Uri.IsHexDigit)) return t;
        return null;
    }

    /// <summary>The same address without its fragment (the token is removed from the address bar and the history entry).</summary>
    public static string WithoutFragment(string uri) => uri.IndexOf('#') is var i and >= 0 ? uri[..i] : uri;

    private async Task<(bool Connected, string? Message)> PostAsync(object body, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("api/local-session", body, ct);
            if (response.IsSuccessStatusCode) return (true, null);
            var problem = await response.Content.ReadFromJsonAsync<Problem>(cancellationToken: ct);
            return (false, problem?.Message ?? $"The code was not accepted ({(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { return (false, "The BirkNext service could not be reached. Check that the start window is still open."); }
    }
}
