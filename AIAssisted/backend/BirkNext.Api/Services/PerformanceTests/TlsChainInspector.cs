using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>
/// Explains a TLS trust failure of the k6 container. From the BirkNext host it opens one TLS handshake to the target, captures the chain the
/// server presents and ALWAYS rejects it (nothing is ever trusted or sent), then answers: does Windows trust it, would the configured CA bundle
/// let the container trust it — built like k6/Go does, without downloading certificates — and which layer fails: the target certificate
/// (expired, wrong host), the server chain (an intermediate the server does not send; Windows fetches it, k6 does not), or the container CA
/// bundle / default store (the root, e.g. an organization or HTTPS-inspection CA, is missing). Public certificate facts only.
/// </summary>
public static class TlsChainInspector
{
    public static async Task<TlsChainDiagnostics> InspectAsync(Uri origin, X509Certificate2Collection? bundle, TimeSpan timeout, CancellationToken ct = default,
        Func<DateTimeOffset>? now = null, string? connectHost = null)
    {
        var at = now?.Invoke() ?? DateTimeOffset.UtcNow;
        if (!string.Equals(origin.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            return new() { Detail = "The target is not HTTPS: no certificate chain.", FailedLayer = TlsTrustLayers.Unknown, CheckedAt = at };

        X509Certificate2? leaf = null;
        var sent = new List<X509Certificate2>();
        var built = new List<X509Certificate2>();
        var errors = SslPolicyErrors.None;
        var statuses = new List<X509ChainStatusFlags>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(connectHost ?? origin.Host, origin.Port, deadline.Token);
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, certificate, chain, policyErrors) =>
            {
                if (certificate is not null) leaf = new X509Certificate2(certificate);
                errors = policyErrors;
                if (chain is not null)
                {
                    sent.AddRange(chain.ChainPolicy.ExtraStore.Select(c => new X509Certificate2(c)));
                    built.AddRange(chain.ChainElements.Select(e => new X509Certificate2(e.Certificate)));
                    statuses.AddRange(chain.ChainStatus.Select(s => s.Status));
                }
                return false; // inspection only: never accept, never send application data
            });
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = origin.Host, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, deadline.Token);
            }
            catch (AuthenticationException) { /* expected: the inspector rejects every certificate */ }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new() { Detail = "The target did not complete a TLS handshake in time.", CheckedAt = at };
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return new() { Detail = "The BirkNext host could not open a TLS connection to the target (the container may still reach it through a proxy).", CheckedAt = at };
        }

        try
        {
            if (leaf is null) return new() { Detail = "The target presented no certificate.", CheckedAt = at };
            if (built.Count == 0) built.Add(new X509Certificate2(leaf));
            var presented = built.Select(c => Info(c)).ToList();
            var expired = presented.Any(c => c.ExpiredAt(at));
            var hostnameMatches = !errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
            var hostTrusted = errors == SslPolicyErrors.None;
            var hostDetail = hostTrusted ? "Windows trusts this chain."
                : errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ? "Windows rejects it: the certificate does not match the host name."
                : statuses.Contains(X509ChainStatusFlags.NotTimeValid) ? "Windows rejects it: a certificate is outside its validity period."
                : statuses.Contains(X509ChainStatusFlags.PartialChain) ? "Windows cannot build the chain either (an intermediate is missing)."
                : "Windows does not trust the root of this chain.";

            // What the server itself sends besides the leaf; the issuer of the leaf must be among them unless the leaf is issued by a root.
            var sentIntermediates = sent.Where(c => c.Thumbprint != leaf.Thumbprint).ToList();
            var leafIssuerSent = sentIntermediates.Any(c => c.SubjectName.RawData.SequenceEqual(leaf.IssuerName.RawData));
            var leafIssuerIsRoot = built.Count >= 2 && built[1].SubjectName.RawData.SequenceEqual(built[1].IssuerName.RawData);
            var missingIntermediate = !SelfSigned(leaf) && !leafIssuerSent && !leafIssuerIsRoot;
            // The root to trust: the self-signed top of the chain, or — when the host could not reach it — the issuer the topmost certificate names.
            var top = built.LastOrDefault(c => SelfSigned(c)) ?? built[^1];
            var rootName = SelfSigned(top) ? top.Subject : top.Issuer;

            bool? bundleTrusted = null;
            var bundleDetail = "No CA bundle is configured: the container uses its default CA store.";
            if (bundle is { Count: > 0 })
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(bundle);
                chain.ChainPolicy.ExtraStore.AddRange(bundle);
                chain.ChainPolicy.ExtraStore.AddRange(sentIntermediates.ToArray());
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.DisableCertificateDownloads = true; // like the container: no AIA fetching
                chain.ChainPolicy.VerificationTime = at.UtcDateTime;
                bundleTrusted = chain.Build(leaf);
                bundleDetail = bundleTrusted == true
                    ? "The configured CA bundle (with the certificates the server sends) lets the container trust this chain."
                    : $"The configured CA bundle does not complete the chain: {string.Join(", ", chain.ChainStatus.Select(s => s.Status).Distinct())}.";
            }

            var (layer, guidance) = Classify(origin.Host, presented, expired, hostnameMatches, hostTrusted, bundleTrusted, missingIntermediate, rootName, SelfSigned(top) ? Info(top).Sha256 : null, leaf);
            return new TlsChainDiagnostics
            {
                Reached = true, Certificates = presented, SentByServer = 1 + sentIntermediates.Count, HostnameMatches = hostnameMatches, Expired = expired,
                HostTrusted = hostTrusted, HostDetail = hostDetail, BundleTrusted = bundleTrusted, BundleDetail = bundleDetail, MissingIntermediate = missingIntermediate,
                FailedLayer = layer, Guidance = guidance, CheckedAt = at,
                Detail = $"{origin.Host} presents {presented.Count} certificate{(presented.Count == 1 ? "" : "s")} (leaf: {presented[0].Subject}; issuer: {presented[0].Issuer}).",
            };
        }
        finally
        {
            leaf?.Dispose();
            foreach (var c in sent.Concat(built)) c.Dispose();
        }
    }

    private static (string Layer, string Guidance) Classify(string host, IReadOnlyList<TlsCertificateInfo> presented, bool expired, bool hostnameMatches, bool hostTrusted,
        bool? bundleTrusted, bool missingIntermediate, string root, string? rootSha256, X509Certificate2 leaf)
    {
        if (expired)
            return (TlsTrustLayers.TargetCertificate, $"A certificate presented by {host} is expired or not yet valid ({string.Join("; ", presented.Where(c => c.ExpiredAt(DateTimeOffset.UtcNow)).Select(c => $"{c.Subject}: valid {c.NotBefore:yyyy-MM-dd}–{c.NotAfter:yyyy-MM-dd}"))}). The target must renew it; a CA bundle cannot fix this.");
        if (!hostnameMatches)
            return (TlsTrustLayers.TargetCertificate, $"The certificate ({presented[0].Subject}) is not issued for {host}. Use the host name the certificate names, or have the target fixed; a CA bundle cannot fix this.");
        if (missingIntermediate && bundleTrusted != true)
            return (TlsTrustLayers.ServerChain, $"The server does not send its intermediate certificate (issuer “{leaf.Issuer}”). Windows downloads it automatically; the k6 container does not. Add that intermediate (and its root) to the CA bundle as PEM, or have the server send the full chain.");
        if (bundleTrusted == true)
            return (TlsTrustLayers.Unknown, "The configured CA bundle would trust the chain the host sees. If the container still fails, it sees a different certificate — typically an HTTPS-inspection proxy between the Podman machine and the target: export that proxy's CA certificate and add it to the bundle.");
        if (bundleTrusted == false)
            return (TlsTrustLayers.ContainerCaBundle, $"The configured CA bundle does not contain the root “{root}”{(rootSha256 is null ? "" : $" (SHA-256 {rootSha256})")}. Add that CA certificate (PEM, public certificate only) to the bundle, save, and run the container network check again.");
        return hostTrusted
            ? (TlsTrustLayers.ContainerDefaultStore, $"Windows trusts this chain through the root “{root}”, which the container's default CA store does not contain (typically an organization CA or an HTTPS-inspection CA). Export that root certificate as PEM and configure it as the CA bundle ({PerformanceCaBundleInspector.ConfigurationKey}).")
            : (TlsTrustLayers.ContainerDefaultStore, $"Neither Windows nor the container trusts the root “{root}”. Obtain the approved CA certificate from your administrators and configure it as the CA bundle ({PerformanceCaBundleInspector.ConfigurationKey}); TLS verification is never disabled.");
    }

    private static bool SelfSigned(X509Certificate2 c) => c.SubjectName.RawData.SequenceEqual(c.IssuerName.RawData);

    public static TlsCertificateInfo Info(X509Certificate2 c) => new(c.Subject, c.Issuer, new DateTimeOffset(c.NotBefore.ToUniversalTime(), TimeSpan.Zero),
        new DateTimeOffset(c.NotAfter.ToUniversalTime(), TimeSpan.Zero), Convert.ToHexString(SHA256.HashData(c.RawData)).ToLowerInvariant()[..16],
        c.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault()?.CertificateAuthority ?? false, SelfSigned(c));

    /// <summary>The certificates of a (validated) PEM bundle file, or null when none is configured or it cannot be read.</summary>
    public static X509Certificate2Collection? LoadBundle(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var collection = new X509Certificate2Collection();
            collection.ImportFromPem(File.ReadAllText(path));
            return collection;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException) { return null; }
    }
}
