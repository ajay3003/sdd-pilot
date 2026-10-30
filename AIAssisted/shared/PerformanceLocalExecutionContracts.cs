namespace BirkNext.PerformanceTests;

/// <summary>
/// Container TLS trust for local k6 execution (System Settings → Performance Test Engines → Local execution setup). Machine-local execution
/// configuration: never part of a Target Environment, the Setup Library, a project import or an Integration Quality Review. TLS verification is
/// never disabled — the only lever is a CA bundle mounted read-only into the container, used identically by the network check and the run.
/// </summary>
public static class ContainerTlsTrustStates
{
    public const string NotConfigured = "Not configured";
    public const string Configured = "Configured";
    public const string Validated = "Validated";
    public const string ValidationFailed = "Validation failed";
    public const string NeedsRecheck = "Needs recheck";
}

/// <summary>Where the effective CA bundle comes from.</summary>
public static class ContainerTlsTrustSources
{
    public const string None = "None";
    /// <summary>Configured in System Settings on this machine (wins over the configuration file).</summary>
    public const string SystemSettings = "System Settings (this machine)";
    public const string ConfigurationFile = "Configuration file";
}

/// <summary>One certificate the target presented (public certificate facts only — never key material).</summary>
public sealed record TlsCertificateInfo(string Subject, string Issuer, DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Sha256, bool IsAuthority, bool SelfSigned)
{
    public bool ExpiredAt(DateTimeOffset now) => now > NotAfter || now < NotBefore;
}

/// <summary>Which trust layer failed (see <see cref="TlsChainDiagnostics.FailedLayer"/>).</summary>
public static class TlsTrustLayers
{
    public const string TargetCertificate = "Target certificate";
    public const string ServerChain = "Server certificate chain";
    public const string ContainerCaBundle = "Container CA bundle";
    public const string ContainerDefaultStore = "Container default CA store";
    public const string Unknown = "Not determined";
}

/// <summary>
/// The certificate chain the target presents, inspected from the BirkNext host after a TLS failure in the k6 container: presented certificates
/// (leaf first), host (Windows) trust, whether the configured CA bundle would let the container trust it (no certificate downloads, like k6),
/// the failing layer and what to do. Inspection always rejects the certificate; TLS verification is never relaxed.
/// </summary>
public sealed record TlsChainDiagnostics
{
    public bool Reached { get; init; }
    public string Detail { get; init; } = "";
    public IReadOnlyList<TlsCertificateInfo> Certificates { get; init; } = [];
    /// <summary>How many of <see cref="Certificates"/> the server itself sent (the rest the host found locally or downloaded).</summary>
    public int SentByServer { get; init; }
    public bool HostnameMatches { get; init; } = true;
    public bool Expired { get; init; }
    public bool HostTrusted { get; init; }
    public string HostDetail { get; init; } = "";
    /// <summary>Null when no CA bundle is configured.</summary>
    public bool? BundleTrusted { get; init; }
    public string BundleDetail { get; init; } = "";
    public bool MissingIntermediate { get; init; }
    public string FailedLayer { get; init; } = TlsTrustLayers.Unknown;
    public string Guidance { get; init; } = "";
    public DateTimeOffset CheckedAt { get; init; }
}

/// <summary>One stage of the container network check. State: Passed, Failed, Not reached, Not applicable, Not checked.</summary>
public sealed record PerformanceNetworkStage(string Key, string Name, string State, string Detail);

/// <summary>The last container network check recorded as trust validation (metadata only; no certificate content, no response body).</summary>
public sealed record ContainerTlsValidation
{
    public string TargetOrigin { get; init; } = "";
    public string ProbeState { get; init; } = PerformanceProbeStates.Unknown;
    public bool Passed { get; init; }
    public int? ErrorCode { get; init; }
    public string Detail { get; init; } = "";
    public DateTimeOffset CheckedAt { get; init; }
    /// <summary>CA content, image, network, proxy and target the check ran with. A different current value means "Needs recheck".</summary>
    public string TrustFingerprint { get; init; } = "";
    /// <summary>The CA bundle fingerprint at validation time ("" = default container CA store).</summary>
    public string CaFingerprint { get; init; } = "";
    /// <summary>The presented chain and the failing trust layer, after a TLS failure (null otherwise or for older records).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TlsChainDiagnostics? Chain { get; init; }
}

/// <summary>Local execution summary + Container TLS trust card. Distinct from engine readiness (runtime/image/k6 version).</summary>
public sealed record PerformanceLocalExecutionStatus
{
    public string TrustState { get; init; } = ContainerTlsTrustStates.NotConfigured;
    public string TrustDetail { get; init; } = "";
    public string Source { get; init; } = ContainerTlsTrustSources.None;
    public string ConfigurationKey { get; init; } = "PerformanceTests:Container:CaBundlePath";
    /// <summary>Host path of the bundle (trust material location, not a secret). Its content is never returned.</summary>
    public string? BundlePath { get; init; }
    public string? BundleFileName { get; init; }
    /// <summary>Ready, Invalid or Not configured (bundle validation, before any network check).</summary>
    public string BundleStatus { get; init; } = "Not configured";
    public string BundleDetail { get; init; } = "";
    public int Certificates { get; init; }
    public string? Fingerprint { get; init; }
    public DateTimeOffset? ConfiguredAt { get; init; }
    public ContainerTlsValidation? LastValidation { get; init; }
    public IReadOnlyList<PerformanceNetworkStage> Stages { get; init; } = [];
    public string Image { get; init; } = "";
    public string? Network { get; init; }
    public bool ProxyConfigured { get; init; }
    public static readonly string[] AllowedExtensions = [".pem", ".crt", ".cer"];
}

/// <summary>Configure the CA bundle by host path. Only .pem/.crt/.cer PEM certificate bundles; private keys and PFX/PKCS#12 are refused.</summary>
public sealed record ContainerCaBundleRequest(string Path);

/// <summary>Container network check stages, derived deterministically from the probe state.</summary>
public static class PerformanceNetworkStages
{
    public static readonly (string Key, string Name)[] Order =
        [("runtime", "Container runtime"), ("image", "k6 image"), ("dns", "DNS resolution"), ("connection", "Connection (TCP / proxy)"), ("tls", "TLS trust"), ("http", "HTTP response")];

    /// <summary>The failing stage of a probe state, or null when every stage passed.</summary>
    public static string? FailedStage(string probeState) => probeState switch
    {
        PerformanceProbeStates.Reachable or PerformanceProbeStates.AuthChallenge => null,
        PerformanceProbeStates.RuntimeUnavailable or PerformanceProbeStates.RuntimeFailure => "runtime",
        PerformanceProbeStates.ImageMissing => "image",
        PerformanceProbeStates.DnsFailure => "dns",
        PerformanceProbeStates.Timeout or PerformanceProbeStates.ConnectionRefused or PerformanceProbeStates.ConnectionFailed or PerformanceProbeStates.ProxyFailure => "connection",
        PerformanceProbeStates.TlsTrustFailure or PerformanceProbeStates.HostnameMismatch or PerformanceProbeStates.TlsFailure => "tls",
        _ => "http",
    };

    public static string Classification(string probeState) => probeState switch
    {
        PerformanceProbeStates.TlsTrustFailure => "TLS trust: the certificate chain is not trusted by the container CA store",
        PerformanceProbeStates.HostnameMismatch => "TLS: the certificate does not match the target host name",
        PerformanceProbeStates.TlsFailure => "TLS handshake failure",
        PerformanceProbeStates.DnsFailure => "DNS: the container cannot resolve the target host",
        PerformanceProbeStates.Timeout => "Connection timeout",
        PerformanceProbeStates.ConnectionRefused => "Connection refused",
        PerformanceProbeStates.ConnectionFailed => "Connection failure",
        PerformanceProbeStates.ProxyFailure => "Proxy failure",
        PerformanceProbeStates.RuntimeUnavailable or PerformanceProbeStates.RuntimeFailure => "Container runtime failure",
        PerformanceProbeStates.ImageMissing => "k6 image missing",
        PerformanceProbeStates.Reachable => "Reachable",
        PerformanceProbeStates.AuthChallenge => "Reachable (authentication challenge)",
        _ => "Unknown — no classifiable response",
    };

    /// <summary>Every stage with its state: passed up to the failing one, failed there, not reached after. HTTPS-only TLS stage.</summary>
    public static IReadOnlyList<PerformanceNetworkStage> From(ContainerTlsValidation? validation)
    {
        if (validation is null) return [.. Order.Select(o => new PerformanceNetworkStage(o.Key, o.Name, "Not checked", ""))];
        var failed = FailedStage(validation.ProbeState);
        var https = validation.TargetOrigin.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var reachedFailure = false;
        var stages = new List<PerformanceNetworkStage>();
        foreach (var (key, name) in Order)
        {
            if (key == "tls" && !https) { stages.Add(new(key, name, "Not applicable", "The target is not HTTPS.")); continue; }
            if (reachedFailure) { stages.Add(new(key, name, "Not reached", "")); continue; }
            if (key == failed) { reachedFailure = true; stages.Add(new(key, name, "Failed", Classification(validation.ProbeState))); continue; }
            stages.Add(new(key, name, "Passed", ""));
        }
        return stages;
    }
}
