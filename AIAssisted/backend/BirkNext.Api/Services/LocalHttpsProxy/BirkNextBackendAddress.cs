namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>
/// The loopback port this BirkNext backend listens on, as Kestrel bound it: 5000 in development, the launcher's port (e.g. 5480) in the
/// Windows tester package. Dedicated Edge's Browser Companion is told this address (its launch file) and Edge bypasses the interception proxy
/// for it. Both were hard-coded to 5000, so in the tester package Dedicated Edge never paired by itself and testers were shown a pairing code
/// (2026-11-23).
/// </summary>
public static class BirkNextBackendAddress
{
    public const int DevelopmentPort = 5000;

    /// <summary>Set once the server has started (<see cref="Observe"/>); the development port until then.</summary>
    public static int Port { get; private set; } = DevelopmentPort;

    public static string LoopbackBase => $"http://127.0.0.1:{Port}";

    /// <summary>Takes the first plain-HTTP loopback listener (localhost, 127.0.0.1 or [::1]) of the bound addresses.</summary>
    public static void Observe(IEnumerable<string> addresses)
    {
        if (LoopbackPort(addresses) is { } port) Port = port;
    }

    public static int? LoopbackPort(IEnumerable<string> addresses)
    {
        foreach (var address in addresses)
        {
            // Kestrel reports wildcard bindings as http://+:port or http://*:port, which Uri cannot parse; those are not loopback-only anyway.
            if (!Uri.TryCreate(address.Replace("://localhost:", "://127.0.0.1:", StringComparison.OrdinalIgnoreCase), UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && uri.Port is > 0 and <= 65535) return uri.Port;
        }
        return null;
    }

    /// <summary>Loopback is proxied except the BirkNext backend itself (see <see cref="LocalHttpsProxyService.ProxyBypassList"/>).</summary>
    public static string ProxyBypassList(int port) => $"<-loopback>;127.0.0.1:{port};localhost:{port}";
}
