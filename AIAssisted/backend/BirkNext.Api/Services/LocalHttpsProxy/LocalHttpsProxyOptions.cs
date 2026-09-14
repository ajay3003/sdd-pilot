namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>Configuration for the DEV-only loopback HTTPS inspection proxy. Nothing here is a credential.</summary>
public sealed class LocalHttpsProxyOptions
{
    public const string SectionName = "LocalHttpsProxy";

    /// <summary>Preferred loopback port. 0 lets the OS choose. Occupied ports are never freed by BirkNext.</summary>
    public int Port { get; set; } = 8888;
    /// <summary>How many consecutive loopback ports after <see cref="Port"/> may be tried when the preferred one is occupied.</summary>
    public int PortSearchLimit { get; set; } = 10;
    /// <summary>Optional corporate upstream proxy (<c>host:port</c>, no credentials) for outbound connections. Default: direct.</summary>
    public string? UpstreamProxy { get; set; }
    /// <summary>Maximum lifetime of an observed in-memory credential, bounded further by the token's own expiry when known.</summary>
    public int CredentialLifetimeMinutes { get; set; } = 30;
    /// <summary>Maximum lifetime of a proxy session; the listener stops and the credential is wiped afterwards.</summary>
    public int SessionLifetimeMinutes { get; set; } = 120;
    /// <summary>Dedicated Edge profile directory for the optional "Start Edge with proxy" helper. Never the normal Edge profile.</summary>
    public string? EdgeProfileDirectory { get; set; }

    internal TimeSpan CredentialLifetime => TimeSpan.FromMinutes(Math.Clamp(CredentialLifetimeMinutes, 5, 120));
    internal TimeSpan SessionLifetime => TimeSpan.FromMinutes(Math.Clamp(SessionLifetimeMinutes, 10, 480));
}
