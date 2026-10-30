using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using BirkNext.Api.Services.ContainerRuntime;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>The configured corporate CA bundle as the container will see it. Trust material, not a secret: its path and fingerprint may be shown,
/// its content is never exported or echoed.</summary>
public sealed record PerformanceCaBundleState(string Status, string Detail, int Certificates, string Fingerprint)
{
    public const string NotConfigured = "Not configured", Ready = "Ready", Invalid = "Invalid";
    public bool Configured => Status != NotConfigured;
    public bool Usable => Status == Ready;
}

/// <summary>
/// Validates <c>PerformanceTests:Container:CaBundlePath</c> before any container uses it: a path string alone is never "Ready". The file must
/// exist, be readable, be a PEM bundle of certificates only (no private key), load as X.509 and be mountable read-only into the container.
/// Nothing here relaxes verification: an invalid bundle blocks readiness; it never falls back to an insecure mode.
/// </summary>
public static class PerformanceCaBundleInspector
{
    public const string ConfigurationKey = "PerformanceTests:Container:CaBundlePath";
    private const long MaxBytes = 2 * 1024 * 1024;

    public static PerformanceCaBundleState Inspect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new(PerformanceCaBundleState.NotConfigured, "No corporate CA bundle is configured; the container uses its default CA store.", 0, "");
        if (!ContainerArgumentRules.IsHostPath(path))
            return Invalid("The configured path is not an absolute host path the container runtime can mount.");
        FileInfo file;
        try { file = new FileInfo(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return Invalid("The configured path is not a valid file path."); }
        if (!file.Exists) return Invalid("The configured CA bundle file does not exist.");
        if (file.Length == 0) return Invalid("The configured CA bundle file is empty.");
        if (file.Length > MaxBytes) return Invalid("The configured CA bundle is larger than 2 MB; it is not a certificate bundle.");
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Invalid("The configured CA bundle cannot be read by the BirkNext backend."); }
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            return Invalid("The configured file contains a private key. A CA bundle holds public CA certificates only; it is not mounted.");
        if (!text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            return Invalid("The configured file is not a PEM certificate bundle (no BEGIN CERTIFICATE block).");
        var certificates = new X509Certificate2Collection();
        try { certificates.ImportFromPem(text); }
        catch (CryptographicException) { return Invalid("The PEM bundle could not be loaded as X.509 certificates."); }
        try
        {
            if (certificates.Count == 0) return Invalid("The PEM bundle contains no loadable certificate.");
            var authorities = certificates.Count(IsAuthority);
            var fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16];
            if (authorities == 0)
                return Invalid("The PEM bundle contains no CA certificate (only end-entity certificates). Add the issuing root or intermediate CA certificates.");
            var expired = certificates.Count(c => c.NotAfter.ToUniversalTime() < DateTime.UtcNow);
            return new(PerformanceCaBundleState.Ready,
                $"{certificates.Count} certificate{(certificates.Count == 1 ? "" : "s")} ({authorities} CA) loaded from {file.Name}; mounted read-only into the container. TLS verification stays on."
                + (expired > 0 ? $" {expired} certificate{(expired == 1 ? " is" : "s are")} expired and will not establish trust." : ""),
                certificates.Count, fingerprint);
        }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    private static bool IsAuthority(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault()?.CertificateAuthority ?? certificate.SubjectName.RawData.SequenceEqual(certificate.IssuerName.RawData);

    private static PerformanceCaBundleState Invalid(string detail) => new(PerformanceCaBundleState.Invalid, detail, 0, "");

    /// <summary>What the container-network check depends on. A change to any part (target, runtime, image, CA content, proxy) makes an
    /// earlier check stale.</summary>
    public static string NetworkFingerprint(PerformanceTestDefinition d, PerformanceContainerOptions c, PerformanceCaBundleState ca) =>
        PerformanceTestStore.Fingerprint(new
        {
            Target = d.TargetOrigin.TrimEnd('/').ToLowerInvariant(), d.ProviderId, d.RuntimeId, c.Image, c.Network,
            Ca = ca.Configured ? $"{ca.Status}:{ca.Fingerprint}" : "", c.HttpProxy, c.HttpsProxy, c.NoProxy,
        });
}
