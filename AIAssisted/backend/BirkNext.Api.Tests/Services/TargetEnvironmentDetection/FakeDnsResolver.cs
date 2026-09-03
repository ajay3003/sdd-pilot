using System.Net;
using BirkNext.Api.Services.TargetEnvironmentDetection;

namespace BirkNext.Api.Tests.Services.TargetEnvironmentDetection;

/// <summary>
/// Deterministic test DNS resolver.
/// No external DNS calls. Configure expected resolutions explicitly.
/// </summary>
public sealed class FakeDnsResolver : ITargetHostResolver
{
    private readonly Dictionary<string, List<IPAddress>> _resolutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failingHosts = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ResolvedHostnames { get; } = new();

    /// <summary>
    /// Configure a hostname to resolve to one or more addresses.
    /// </summary>
    public void Add(string hostname, params string[] addressStrings)
    {
        var addresses = addressStrings
            .Select(addr => IPAddress.Parse(addr))
            .ToList();

        _resolutions[hostname] = addresses;
    }

    /// <summary>
    /// Configure a hostname to fail resolution.
    /// </summary>
    public void Fail(string hostname)
    {
        _failingHosts.Add(hostname);
    }

    public Task<IReadOnlyList<IPAddress>> ResolveHostAsync(string hostname, CancellationToken cancellationToken = default)
    {
        ResolvedHostnames.Add(hostname);

        if (_failingHosts.Contains(hostname))
            return Task.FromResult<IReadOnlyList<IPAddress>>(Array.Empty<IPAddress>());

        if (_resolutions.TryGetValue(hostname, out var addresses))
            return Task.FromResult<IReadOnlyList<IPAddress>>(addresses.AsReadOnly());

        // Unconfigured hostname returns empty (safe failure)
        return Task.FromResult<IReadOnlyList<IPAddress>>(Array.Empty<IPAddress>());
    }
}
