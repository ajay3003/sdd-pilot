using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BirkNext.PerformanceTests;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>Downloads a CA Issuers certificate named by a certificate's AIA extension. Implementations enforce scheme, size, timeout and redirect limits.</summary>
public interface IAiaCertificateFetcher
{
    /// <summary>The raw response body, or null when it could not be retrieved within the limits (never throws for network failures).</summary>
    Task<byte[]?> FetchAsync(Uri uri, CancellationToken ct);
}

/// <summary>
/// HTTP(S) AIA fetcher: http/https only, at most <see cref="MaxRedirects"/> redirects (each re-checked), <see cref="MaxBytes"/> bytes, a 10 s timeout,
/// no cookies or credentials. The body is only ever parsed as certificate data — never executed or stored as is.
/// </summary>
public sealed class HttpAiaCertificateFetcher(HttpClient http) : IAiaCertificateFetcher
{
    public const int MaxBytes = 64 * 1024, MaxRedirects = 2;

    public static HttpMessageHandler Handler() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null, ConnectTimeout = TimeSpan.FromSeconds(5) };

    public async Task<byte[]?> FetchAsync(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!TlsIntermediateRepair.IsAllowedUri(uri)) return null;
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxBytes) return null;
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
                {
                    if (buffer.Length + read > MaxBytes) return null;
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) { return null; }
    }
}

/// <summary>
/// Automatic local k6 trust repair for a server that omits its intermediate(s). Only an INTERMEDIATE is ever added: it is located through the
/// AIA CA Issuers URI of the certificate it issued (never a web search), and accepted only when every check passes — issuer name, AKI/SKI, the
/// child's signature verified with the candidate's public key, CA basic constraint, keyCertSign usage, validity, and a complete chain that ends at
/// a root ALREADY in the trusted set (the container's default store and an approved configured CA bundle). The leaf is never trusted, no root
/// is ever added, hostname and validity checks are never relaxed. Revocation is not checked (as by k6 itself). The server stays misconfigured.
/// </summary>
public static class TlsIntermediateRepair
{
    public const int MaxDepth = 3;
    private const string AiaOid = "1.3.6.1.5.5.7.1.1", CaIssuersOid = "1.3.6.1.5.5.7.48.2";

    public sealed record Result(TlsRepairOutcome Outcome, IReadOnlyList<X509Certificate2> Intermediates);

    public static bool IsAllowedUri(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(uri.UserInfo);

    /// <summary>The URI as recorded: scheme, host, port and path only (a query or fragment may carry tokens and is never stored or logged).</summary>
    public static string Safe(Uri uri) => uri.GetLeftPart(UriPartial.Path);

    public static async Task<Result> RepairAsync(TlsChainInspector.Capture capture, string host, int port, X509Certificate2Collection trustedRoots,
        IAiaCertificateFetcher fetcher, DateTimeOffset now, CancellationToken ct = default)
    {
        var checks = new List<string>();
        TlsRepairOutcome Outcome(string state, string detail) => new() { State = state, Detail = detail, Host = host, Port = port, Checks = [.. checks] };
        if (capture.Leaf is null) return new(Outcome(TlsRepairStates.NotEligible, "No certificate was captured from the target."), []);
        using var leaf = new X509Certificate2(capture.Leaf);
        var sent = capture.Sent.Select(der => new X509Certificate2(der)).ToList();
        var acquired = new List<X509Certificate2>();
        try
        {
            // Never a workaround for these: the certificate itself is wrong.
            if (SelfSigned(leaf)) return new(Outcome(TlsRepairStates.NotEligible, "The target presents a self-signed certificate: it is never trusted automatically."), []);
            if (!Valid(leaf, now)) return new(Outcome(TlsRepairStates.NotEligible, "The target certificate is expired or not yet valid: renew it; it is never worked around."), []);
            if (!leaf.MatchesHostname(host)) return new(Outcome(TlsRepairStates.NotEligible, $"The target certificate does not match {host}: it is never worked around."), []);
            checks.Add($"Leaf {leaf.Subject} is valid now and matches {host}.");

            var current = leaf;
            string? source = null;
            DateTimeOffset? retrievedAt = null;
            for (var depth = 0; depth < MaxDepth && !Builds(leaf, sent, acquired, trustedRoots, now, out _); depth++)
            {
                if (sent.Concat(acquired).FirstOrDefault(c => c.SubjectName.RawData.SequenceEqual(current.IssuerName.RawData) && !ReferenceEquals(c, current)) is { } known)
                { current = known; continue; }
                if (trustedRoots.Any(r => r.SubjectName.RawData.SequenceEqual(current.IssuerName.RawData))) break; // issued by a trusted root: nothing to fetch
                var uris = CaIssuers(current).ToList();
                if (uris.Count == 0)
                    return new(Outcome(TlsRepairStates.Stopped, ReferenceEquals(current, leaf)
                        ? $"{current.Subject} names no CA Issuers (AIA) location for its issuer “{current.Issuer}”: the missing intermediate cannot be located safely."
                        : $"Automatic repair stopped: the chain requires a root CA (“{current.Issuer}”) that is not currently trusted. A root is never trusted automatically — configure the approved corporate/private CA."), []);
                X509Certificate2? accepted = null;
                string? lastReason = null;
                foreach (var uri in uris.Take(2))
                {
                    if (!IsAllowedUri(uri)) { lastReason = $"AIA location {uri.Scheme}: is not allowed (http/https only)."; continue; }
                    var body = await fetcher.FetchAsync(uri, ct);
                    if (body is null) { lastReason = $"No certificate could be retrieved from {Safe(uri)} within the limits."; continue; }
                    foreach (var candidate in Parse(body))
                    {
                        var reason = Rejection(current, candidate, now);
                        if (reason is null) { accepted = candidate; source = Safe(uri); retrievedAt = now; break; }
                        lastReason = reason;
                        candidate.Dispose();
                    }
                    if (accepted is not null) break;
                }
                if (accepted is null)
                    return new(Outcome(TlsRepairStates.Rejected, $"The certificate retrieved for issuer “{current.Issuer}” was rejected: {lastReason ?? "no certificate in the response."}"), []);
                if (SelfSigned(accepted))
                {
                    var trusted = trustedRoots.Any(r => r.Thumbprint == accepted.Thumbprint);
                    accepted.Dispose();
                    if (!trusted)
                        return new(Outcome(TlsRepairStates.Stopped, "Automatic repair stopped: the chain requires a root CA that is not currently trusted. A root is never trusted automatically — configure the approved corporate/private CA."), []);
                    break;
                }
                checks.Add($"Intermediate {accepted.Subject}: issuer name, key identifier, signature of {current.Subject}, CA constraint, key usage and validity verified.");
                acquired.Add(accepted);
                current = accepted;
            }

            if (!Builds(leaf, sent, acquired, trustedRoots, now, out var root))
                return new(Outcome(TlsRepairStates.Stopped, "Automatic repair stopped: no complete chain to a root CA that is already trusted by the k6 container or the approved CA bundle. A root is never trusted automatically — configure the approved corporate/private CA."), []);
            if (acquired.Count == 0)
                return new(Outcome(TlsRepairStates.NoLongerNeeded, "The server's chain already builds to a trusted root: no repair is needed."), []);
            checks.Add($"Complete chain to the already trusted root {root!.Subject}; host name still validated.");
            var first = acquired[0];
            var bundleSha = Convert.ToHexString(SHA256.HashData(acquired.SelectMany(c => c.RawData).ToArray())).ToLowerInvariant();
            return new(new TlsRepairOutcome
            {
                State = TlsRepairStates.Applied, Host = host, Port = port, Checks = [.. checks],
                Detail = "Local k6 trust repair applied: the missing intermediate certificate was retrieved and validated against an existing trusted root. The server certificate chain is still incomplete — configure the server to send the complete chain.",
                IntermediateSubject = first.Subject, IntermediateIssuer = first.Issuer, IntermediateSha256 = Sha(first), IntermediateNotAfter = new DateTimeOffset(first.NotAfter.ToUniversalTime(), TimeSpan.Zero),
                SourceUri = source, RetrievedAt = retrievedAt, ValidatedAt = now, RootSubject = root.Subject, RootSha256 = Sha(root), LeafSha256 = Sha(leaf), BundleSha256 = bundleSha,
            }, acquired);
        }
        catch
        {
            foreach (var c in acquired) c.Dispose();
            throw;
        }
        finally
        {
            foreach (var c in sent) c.Dispose();
        }
    }

    /// <summary>Why <paramref name="candidate"/> is not the issuer of <paramref name="child"/> (null = every check passed).</summary>
    internal static string? Rejection(X509Certificate2 child, X509Certificate2 candidate, DateTimeOffset now)
    {
        if (!candidate.SubjectName.RawData.SequenceEqual(child.IssuerName.RawData)) return $"its subject “{candidate.Subject}” is not the issuer “{child.Issuer}”.";
        var aki = child.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().FirstOrDefault()?.KeyIdentifier;
        var ski = candidate.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifier;
        if (aki is { } a && ski is { } s && !Convert.ToHexString(a.Span).Equals(s, StringComparison.OrdinalIgnoreCase)) return "its subject key identifier does not match the authority key identifier.";
        if (!SignatureValid(child, candidate)) return "the signature does not verify with its public key (wrong or tampered certificate).";
        if (candidate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault() is not { CertificateAuthority: true }) return "it is not a CA certificate (basic constraints).";
        if (candidate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault() is { } usage && !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
            return "its key usage does not permit certificate signing.";
        if (!Valid(candidate, now)) return "it is expired or not yet valid.";
        return null;
    }

    /// <summary>Verifies the child's signature (from its own DER) with the candidate's public key. RSA (PKCS#1 v1.5, PSS) and ECDSA.</summary>
    internal static bool SignatureValid(X509Certificate2 child, X509Certificate2 issuer)
    {
        try
        {
            var reader = new AsnReader(child.RawData, AsnEncodingRules.DER).ReadSequence();
            var tbs = reader.ReadEncodedValue().ToArray();
            var algorithm = reader.ReadSequence().ReadObjectIdentifier();
            var signature = reader.ReadBitString(out _);
            HashAlgorithmName? hash = algorithm switch
            {
                "1.2.840.113549.1.1.11" or "1.2.840.10045.4.3.2" => HashAlgorithmName.SHA256,
                "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3" => HashAlgorithmName.SHA384,
                "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4" => HashAlgorithmName.SHA512,
                _ => null,
            };
            if (hash is { } h)
            {
                if (issuer.GetRSAPublicKey() is { } rsa) using (rsa) return rsa.VerifyData(tbs, signature, h, RSASignaturePadding.Pkcs1);
                if (issuer.GetECDsaPublicKey() is { } ec) using (ec) return ec.VerifyData(tbs, signature, h, DSASignatureFormat.Rfc3279DerSequence);
                return false;
            }
            // RSASSA-PSS: verify with the chain engine against the single candidate (signature only; trust is decided separately).
            if (algorithm == "1.2.840.113549.1.1.10")
            {
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(issuer);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.DisableCertificateDownloads = true;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority | X509VerificationFlags.IgnoreNotTimeValid;
                chain.Build(child);
                return !chain.ChainStatus.Any(s => s.Status is X509ChainStatusFlags.NotSignatureValid);
            }
            return false;
        }
        catch (Exception ex) when (ex is AsnContentException or CryptographicException) { return false; }
    }

    /// <summary>A complete chain from the leaf (with the sent and acquired intermediates, no downloads) to a root already in <paramref name="trustedRoots"/>.</summary>
    internal static bool Builds(X509Certificate2 leaf, IEnumerable<X509Certificate2> sent, IEnumerable<X509Certificate2> acquired, X509Certificate2Collection trustedRoots,
        DateTimeOffset now, out X509Certificate2? root)
    {
        root = null;
        if (trustedRoots.Count == 0) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(trustedRoots);
        var available = sent.Concat(acquired).ToArray();
        chain.ChainPolicy.ExtraStore.AddRange(available);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        if (!chain.Build(leaf)) return false;
        var top = chain.ChainElements[^1].Certificate;
        if (!trustedRoots.Any(r => r.Thumbprint == top.Thumbprint)) return false;
        // The host's chain engine may complete the chain from its own intermediate cache (Windows does, even with downloads disabled). The
        // container has only what the server sends plus what a repair adds: every intermediate must come from those, else it does not build there.
        for (var i = 1; i < chain.ChainElements.Count - 1; i++)
            if (available.All(c => c.Thumbprint != chain.ChainElements[i].Certificate.Thumbprint)) return false;
        root = new X509Certificate2(top.RawData);
        return true;
    }

    /// <summary>The CA Issuers URIs of the certificate's Authority Information Access extension.</summary>
    internal static IEnumerable<Uri> CaIssuers(X509Certificate2 certificate)
    {
        if (certificate.Extensions[AiaOid] is not { } extension) yield break;
        var uris = new List<Uri>();
        try
        {
            var sequence = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
            while (sequence.HasData)
            {
                var description = sequence.ReadSequence();
                var method = description.ReadObjectIdentifier();
                var tag = description.PeekTag();
                if (method == CaIssuersOid && tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 6)))
                {
                    var text = description.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6));
                    if (Uri.TryCreate(text, UriKind.Absolute, out var uri)) uris.Add(uri);
                }
            }
        }
        catch (AsnContentException) { }
        foreach (var uri in uris) yield return uri;
    }

    /// <summary>Certificates in a DER, PEM or PKCS#7 (.p7c) body; anything else yields nothing.</summary>
    internal static IReadOnlyList<X509Certificate2> Parse(byte[] body)
    {
        try
        {
            var text = System.Text.Encoding.ASCII.GetString(body);
            if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            {
                var pem = new X509Certificate2Collection();
                pem.ImportFromPem(text);
                return [.. pem];
            }
            // DER certificate or PKCS#7 only: a PKCS#12/PFX body (key material) is never imported.
            switch (X509Certificate2.GetCertContentType(body))
            {
                case X509ContentType.Cert: return [new X509Certificate2(body)];
                case X509ContentType.Pkcs7:
                    var collection = new X509Certificate2Collection();
                    collection.Import(body);
                    return [.. collection];
                default: return [];
            }
        }
        catch (CryptographicException) { return []; }
    }

    private static bool SelfSigned(X509Certificate2 c) => c.SubjectName.RawData.SequenceEqual(c.IssuerName.RawData);
    private static bool Valid(X509Certificate2 c, DateTimeOffset now) => now.UtcDateTime >= c.NotBefore.ToUniversalTime() && now.UtcDateTime <= c.NotAfter.ToUniversalTime();
    private static string Sha(X509Certificate2 c) => Convert.ToHexString(SHA256.HashData(c.RawData)).ToLowerInvariant()[..16];
}
