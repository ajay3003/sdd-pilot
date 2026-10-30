using System.Text.Json;
using Path = System.IO.Path;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>
/// Machine-local execution configuration for the k6 container: the CA bundle chosen in System Settings and the last trust validation. Stored as
/// metadata only (host path, file name, SHA-256 fingerprint, certificate count, timestamps, probe state) in a small JSON file under the local
/// application data folder — never the certificate content, never a private key, and never in a Target Environment, the Setup Library, a
/// project import or an Integration Quality Review. The bundle chosen here wins over <c>PerformanceTests:Container:CaBundlePath</c> and is applied
/// to the shared container options, so the network check and the run use the same trust.
/// </summary>
public sealed class PerformanceLocalExecutionStore
{
    public const string FileName = "performance-local-execution.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly PerformanceTestOptions _options;
    private readonly string _path;
    private readonly object _gate = new();
    private State _state;

    private sealed record State(string? CaBundlePath, string? CaFingerprint, int Certificates, DateTimeOffset? ConfiguredAt, ContainerTlsValidation? Validation);

    public PerformanceLocalExecutionStore(PerformanceTestOptions options, IConfiguration? configuration = null, string? directory = null)
    {
        _options = options;
        var dir = directory ?? configuration?.GetValue<string?>("PerformanceTests:LocalExecution:StateDirectory")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirkNext");
        _path = Path.Combine(dir, FileName);
        _state = Load(_path);
        _options.Container.CaBundleOverridePath = _state.CaBundlePath;
    }

    /// <summary>The CA bundle configuration as the container will see it (System Settings first, then the configuration file).</summary>
    public PerformanceCaBundleState Bundle => PerformanceCaBundleInspector.Inspect(_options.Container.EffectiveCaBundlePath);

    public string Source => _state.CaBundlePath is not null ? ContainerTlsTrustSources.SystemSettings
        : !string.IsNullOrWhiteSpace(_options.Container.CaBundlePath) ? ContainerTlsTrustSources.ConfigurationFile : ContainerTlsTrustSources.None;

    /// <summary>Validates and stores a CA bundle path. Refused (nothing stored, nothing mounted) unless it is a loadable PEM certificate bundle.</summary>
    public (bool Ok, string Message, PerformanceCaBundleState Bundle) Configure(string? path, DateTimeOffset now)
    {
        path = path?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path)) return (false, "Enter the full path of a CA bundle file (.pem, .crt or .cer).", PerformanceCaBundleInspector.Inspect(null));
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".pfx" or ".p12" or ".key")
            return (false, "PFX/PKCS#12 and key files are refused: a CA bundle holds public CA certificates only (PEM .pem, .crt or .cer).", PerformanceCaBundleInspector.Inspect(null));
        if (!PerformanceLocalExecutionStatus.AllowedExtensions.Contains(extension))
            return (false, "Only .pem, .crt or .cer PEM certificate bundles are accepted.", PerformanceCaBundleInspector.Inspect(null));
        var bundle = PerformanceCaBundleInspector.Inspect(path);
        if (!bundle.Usable) return (false, bundle.Detail, bundle);
        lock (_gate)
        {
            _state = _state with { CaBundlePath = Path.GetFullPath(path), CaFingerprint = bundle.Fingerprint, Certificates = bundle.Certificates, ConfiguredAt = now };
            _options.Container.CaBundleOverridePath = _state.CaBundlePath;
            Save();
        }
        return (true, bundle.Detail, bundle);
    }

    /// <summary>Removes the System Settings bundle (the configuration file value, if any, applies again). The file itself is not touched.</summary>
    public void Remove()
    {
        lock (_gate)
        {
            _state = _state with { CaBundlePath = null, CaFingerprint = null, Certificates = 0, ConfiguredAt = null };
            _options.Container.CaBundleOverridePath = null;
            Save();
        }
    }

    /// <summary>Records a container network check against an HTTPS (or HTTP) target as the latest trust validation.</summary>
    public void Record(PerformanceReachability result)
    {
        if (result.State is PerformanceProbeStates.RuntimeUnavailable or PerformanceProbeStates.ImageMissing) return; // never reached the network
        var bundle = Bundle;
        lock (_gate)
        {
            _state = _state with
            {
                Validation = new ContainerTlsValidation
                {
                    TargetOrigin = result.TargetOrigin, ProbeState = result.State, Passed = result.State is PerformanceProbeStates.Reachable or PerformanceProbeStates.AuthChallenge,
                    ErrorCode = result.ErrorCode, Detail = result.Detail, CheckedAt = result.CheckedAt == default ? DateTimeOffset.UtcNow : result.CheckedAt,
                    TrustFingerprint = TrustFingerprint(result.TargetOrigin, bundle), CaFingerprint = bundle.Configured ? bundle.Fingerprint : "",
                    Chain = result.Chain,
                },
            };
            Save();
        }
    }

    /// <summary>CA content, image, network, proxies and target: a change to any of them invalidates an earlier validation.</summary>
    public string TrustFingerprint(string targetOrigin, PerformanceCaBundleState bundle)
    {
        var c = _options.Container;
        return PerformanceTestStore.Fingerprint(new { Target = targetOrigin.TrimEnd('/').ToLowerInvariant(), c.Image, c.Network, Ca = bundle.Configured ? $"{bundle.Status}:{bundle.Fingerprint}" : "", c.HttpProxy, c.HttpsProxy, c.NoProxy });
    }

    public PerformanceLocalExecutionStatus Status()
    {
        var bundle = Bundle;
        var validation = _state.Validation;
        var current = validation is not null && validation.TrustFingerprint == TrustFingerprint(validation.TargetOrigin, bundle);
        var (state, detail) = bundle switch
        {
            { Configured: true, Usable: false } => (ContainerTlsTrustStates.ValidationFailed, $"The CA bundle is invalid and is not mounted: {bundle.Detail} TLS verification will not be disabled."),
            _ when validation is not null && !current => (ContainerTlsTrustStates.NeedsRecheck,
                "The CA bundle, k6 image, network, proxy or target changed since the last container network check. Run the check again."),
            _ when validation is { Passed: true } => (ContainerTlsTrustStates.Validated,
                $"The k6 container reached {validation.TargetOrigin} with {(bundle.Configured ? "the configured CA bundle" : "its default CA store")} and TLS verification on."),
            _ when validation is not null => (ContainerTlsTrustStates.ValidationFailed,
                $"{PerformanceNetworkStages.Classification(validation.ProbeState)}.{(PerformanceNetworkStages.FailedStage(validation.ProbeState) == "tls" && !bundle.Configured ? " Configure the corporate CA bundle that issued the target certificate." : "")}"),
            { Usable: true } => (ContainerTlsTrustStates.Configured, "The CA bundle loads and will be mounted read-only. Run a container network check to validate it against the target."),
            _ => (ContainerTlsTrustStates.NotConfigured, "No CA bundle: the container uses its default CA store. A target signed by a corporate CA needs the corporate CA bundle."),
        };
        var path = _options.Container.EffectiveCaBundlePath;
        return new PerformanceLocalExecutionStatus
        {
            TrustState = state, TrustDetail = detail, Source = Source, BundlePath = string.IsNullOrWhiteSpace(path) ? null : path,
            BundleFileName = string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path), BundleStatus = bundle.Status, BundleDetail = bundle.Detail,
            Certificates = bundle.Certificates, Fingerprint = bundle.Configured && bundle.Usable ? bundle.Fingerprint : null, ConfiguredAt = _state.ConfiguredAt,
            LastValidation = validation, Stages = PerformanceNetworkStages.From(validation), Image = _options.Container.Image, Network = _options.Container.Network,
            ProxyConfigured = !string.IsNullOrWhiteSpace(_options.Container.HttpProxy) || !string.IsNullOrWhiteSpace(_options.Container.HttpsProxy),
        };
    }

    private static State Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? Empty : Empty; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return Empty; }
    }

    private static State Empty => new(null, null, 0, null, null);

    /// <summary>Atomic write: a temporary file then a replace, so a crash never leaves half a file.</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* in-memory state still applies for this process */ }
    }
}
