using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Services.ApiQuality;

namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// The one policy for fetching a contract document (OpenAPI, GraphQL SDL or introspection JSON) from a URL (CI-DEF-3). Absolute http(s)
/// URLs without embedded credentials only; link-local (cloud metadata), unspecified, multicast and broadcast addresses are refused — on the
/// literal host, on every redirect hop and, through <see cref="CreateHandler"/>, on every address DNS returns at connect time. Loopback and
/// private ranges stay allowed: internal test environments are this tool's normal targets, and the run-level environment safety policy
/// still decides whether a target may be reviewed at all.
/// </summary>
public static class ContractSourcePolicy
{
    public static (Uri? Uri, string? Problem) Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return (null, "The contract URL is not an absolute URL.");
        if (uri.Scheme is not ("http" or "https")) return (null, $"The contract URL scheme '{uri.Scheme}' is not allowed (http or https only).");
        if (!string.IsNullOrEmpty(uri.UserInfo)) return (null, "The contract URL must not contain credentials.");
        if (string.IsNullOrEmpty(uri.Host)) return (null, "The contract URL has no host.");
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 && IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var literal) && IsForbidden(literal))
            return (null, $"The contract URL points at a forbidden address ({Describe(literal)}).");
        if (uri.IdnHost.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)) return (null, "The contract URL points at a cloud metadata service.");
        return (uri, null);
    }

    /// <summary>Link-local (169.254.0.0/16 incl. cloud metadata, fe80::/10), unspecified, multicast and broadcast; IPv4-mapped IPv6 is unwrapped.</summary>
    public static bool IsForbidden(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return (b[0] == 169 && b[1] == 254) || b[0] == 0 || b[0] >= 224;
        }
        return address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.Equals(IPAddress.IPv6Any);
    }

    private static string Describe(IPAddress address) => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address) switch
    {
        var a when a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [169, 254, ..] => "link-local / cloud metadata",
        var a when a.IsIPv6LinkLocal => "link-local",
        _ => "unspecified, multicast or broadcast",
    };

    /// <summary>
    /// The primary handler for contract fetches: no cookies, no automatic redirects (the fetcher re-validates every hop), and a connect
    /// callback that refuses forbidden addresses after DNS resolution, so a host name cannot be rebound to a metadata address.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = IPAddress.TryParse(context.DnsEndPoint.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var allowed = addresses.Where(a => !IsForbidden(a)).ToArray();
            if (allowed.Length == 0) throw new HttpRequestException($"Contract fetch refused: {context.DnsEndPoint.Host} resolves only to forbidden addresses.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try { await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        },
    };
}

/// <summary>Outcome of one contract fetch. <see cref="Text"/> is set only when <see cref="Status"/> is <see cref="ContractFetchStatus.Fetched"/>.</summary>
public sealed record ContractFetchResult(ContractFetchStatus Status, string Source, string? Text = null, int? HttpStatus = null, string? Problem = null, string? Sha256 = null, string? FinalUrl = null);

public enum ContractFetchStatus { Fetched, Refused, HttpError, Unreadable, Unreachable }

public interface IContractSourceFetcher
{
    Task<ContractFetchResult> FetchAsync(string url, CancellationToken ct);
}

/// <summary>
/// Fetches a contract document under <see cref="ContractSourcePolicy"/>: anonymous GET (no credentials, no cookies), at most
/// <see cref="MaxRedirects"/> redirects each re-validated, at most 10 MB decoded, 20 s overall.
/// </summary>
public sealed class ContractSourceFetcher(HttpClient client) : IContractSourceFetcher
{
    public const int MaxRedirects = 3;
    public const long MaxBytes = 10 * 1024 * 1024;

    public async Task<ContractFetchResult> FetchAsync(string url, CancellationToken ct)
    {
        var (uri, problem) = ContractSourcePolicy.Validate(url);
        if (uri is null) return new(ContractFetchStatus.Refused, url, Problem: problem);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var code = (int)response.StatusCode;
                if (code is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects) return new(ContractFetchStatus.Refused, url, HttpStatus: code, Problem: $"More than {MaxRedirects} redirects.");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    (uri, problem) = ContractSourcePolicy.Validate(next.ToString());
                    if (uri is null) return new(ContractFetchStatus.Refused, url, HttpStatus: code, Problem: $"Redirect refused: {problem}");
                    continue;
                }
                if (!response.IsSuccessStatusCode) return new(ContractFetchStatus.HttpError, url, HttpStatus: code, Problem: $"Contract fetch returned HTTP {code}.", FinalUrl: uri.ToString());
                var body = await ResponseBodyReader.ReadAsync(response, timeout.Token, maxInspected: MaxBytes);
                if (body.Text is not { } text) return new(ContractFetchStatus.Unreadable, url, HttpStatus: code, Problem: body.Evidence.Reason ?? "The document could not be read.", FinalUrl: uri.ToString());
                return new(ContractFetchStatus.Fetched, url, text, code, Sha256: Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant(), FinalUrl: uri.ToString());
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new(ContractFetchStatus.Unreachable, url, Problem: Refusal(ex) ?? $"Not reachable ({ex.GetType().Name}).");
        }
    }

    /// <summary>The connect callback's refusal, wherever the handler nested it.</summary>
    private static string? Refusal(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex.Message.StartsWith("Contract fetch refused", StringComparison.Ordinal)) return ex.Message;
        return null;
    }
}
