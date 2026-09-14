namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>
/// Exact-match allowlist of <c>host:port</c> authorities the proxy may intercept. Built only from hosts explicitly configured in the
/// selected Target Environment. Identity providers, Defender for Cloud Apps intermediaries, loopback names, wildcards and IP literals
/// are never interceptable, whatever the configuration says (fail closed). Everything else passes through untouched.
/// </summary>
public sealed class ApprovedHostSet
{
    public const int MaxHosts = 32;

    private static readonly string[] NeverInterceptHosts =
    [
        "login.microsoftonline.com", "login.microsoft.com", "login.live.com", "login.windows.net", "sts.windows.net",
        "account.live.com", "autologon.microsoftazuread-sso.com", "device.login.microsoftonline.com", "aadcdn.msftauth.net",
        "aadcdn.msauth.net", "graph.microsoft.com", "enterpriseregistration.windows.net"
    ];

    private static readonly string[] NeverInterceptSuffixes =
    [
        ".access.mcas.ms", ".mcas.ms", ".msauth.net", ".msftauth.net", ".microsoftonline.com", ".microsoftonline-p.com",
        ".login.microsoft.com", ".live.com", ".localhost", ".local"
    ];

    private readonly HashSet<string> _authorities;

    private ApprovedHostSet(string targetHost, int targetPort, HashSet<string> authorities)
    {
        TargetHost = targetHost;
        TargetPort = targetPort;
        _authorities = authorities;
        Authorities = authorities.OrderBy(a => a, StringComparer.Ordinal).ToList();
    }

    public string TargetHost { get; }
    public int TargetPort { get; }
    /// <summary>Sorted <c>host:port</c> entries. Safe to display.</summary>
    public IReadOnlyList<string> Authorities { get; }

    public static ApprovedHostSet Create(string targetUrl, IEnumerable<string>? approvedHosts)
    {
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var target) || target.Scheme != Uri.UriSchemeHttps || target.UserInfo.Length != 0)
            throw new ArgumentException("An HTTPS target URL without user information is required for the local HTTPS proxy.");
        var targetAuthority = NormalizeAuthority($"{target.IdnHost}:{target.Port}");
        var set = new HashSet<string>(StringComparer.Ordinal) { targetAuthority };
        foreach (var entry in approvedHosts ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            set.Add(NormalizeAuthority(entry));
            if (set.Count > MaxHosts) throw new ArgumentException($"At most {MaxHosts} approved hosts are supported.");
        }
        var (host, port) = Split(targetAuthority);
        return new ApprovedHostSet(host, port, set);
    }

    /// <summary>Accepts <c>host</c>, <c>host:port</c> or an absolute HTTPS URL and returns lower-case <c>host:port</c>, default port 443.</summary>
    public static string NormalizeAuthority(string entry)
    {
        var value = entry.Trim();
        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
                throw new ArgumentException("Approved host URLs must be HTTPS without user information.");
            value = $"{uri.IdnHost}:{uri.Port}";
        }
        if (value.Contains('/') || value.Contains('?') || value.Contains('#') || value.Contains('@') || value.Contains('*') || value.Contains(' '))
            throw new ArgumentException("Approved hosts must be plain host names, optionally with a port.");
        var host = value;
        var port = 443;
        var colon = value.LastIndexOf(':');
        if (colon > 0)
        {
            host = value[..colon];
            if (!int.TryParse(value[(colon + 1)..], out port) || port is < 1 or > 65535) throw new ArgumentException("Approved host port is invalid.");
        }
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (!IsInterceptableHost(host))
            throw new ArgumentException($"Host {host} cannot be intercepted: only explicitly configured DNS application hosts are allowed; identity providers, Defender intermediaries, loopback and wildcard names are excluded.");
        return $"{host}:{port}";
    }

    /// <summary>A syntactically valid DNS host name (with a dot) that is not an identity, intermediary or loopback host.</summary>
    public static bool IsInterceptableHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253) return false;
        var lower = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (Uri.CheckHostName(lower) != UriHostNameType.Dns) return false;
        if (!lower.Contains('.')) return false;
        if (lower is "localhost" || lower.StartsWith("127.", StringComparison.Ordinal)) return false;
        if (NeverInterceptHosts.Contains(lower, StringComparer.Ordinal)) return false;
        return !NeverInterceptSuffixes.Any(s => lower.EndsWith(s, StringComparison.Ordinal));
    }

    public bool Contains(string? host, int port) =>
        host is not null && _authorities.Contains($"{host.Trim().TrimEnd('.').ToLowerInvariant()}:{port}");

    public bool ContainsUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && Contains(uri.IdnHost, uri.Port);

    private static (string Host, int Port) Split(string authority)
    {
        var colon = authority.LastIndexOf(':');
        return (authority[..colon], int.Parse(authority[(colon + 1)..]));
    }
}
