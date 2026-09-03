using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace BirkNext.Api.Services.TargetEnvironmentDetection;

/// <summary>
/// Resolves target hostnames to IP addresses for DNS-based security validation.
/// All resolved addresses must be validated through BrowserTargetValidator before making HTTP requests.
/// </summary>
public interface ITargetHostResolver
{
    /// <summary>
    /// Resolves a hostname to all associated IP addresses.
    /// Returns empty collection if hostname is invalid or resolution fails.
    /// </summary>
    Task<IReadOnlyList<IPAddress>> ResolveHostAsync(string hostname, CancellationToken cancellationToken = default);
}

/// <summary>
/// Production DNS resolver using .NET's Dns class.
/// </summary>
public sealed class DnsTargetHostResolver : ITargetHostResolver
{
    private readonly ILogger<DnsTargetHostResolver> _logger;

    public DnsTargetHostResolver(ILogger<DnsTargetHostResolver> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveHostAsync(string hostname, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return Array.Empty<IPAddress>();

        try
        {
            var hostEntry = await Dns.GetHostEntryAsync(hostname, AddressFamily.Unspecified, cancellationToken);
            return hostEntry.AddressList.Length > 0
                ? hostEntry.AddressList
                : Array.Empty<IPAddress>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DNS resolution failed for hostname {Hostname}", hostname);
            return Array.Empty<IPAddress>();
        }
    }
}
