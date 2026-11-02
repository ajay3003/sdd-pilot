using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.PerformanceTests;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Automatic local k6 trust repair, with generated certificates and a fake AIA fetcher only (no network, no live target): a missing intermediate is
/// retrieved through the leaf's AIA CA Issuers URI and accepted only when it is the leaf's real issuer and chains to an ALREADY trusted root. A wrong
/// or tampered intermediate, an unknown root, a self-signed/expired/wrong-host leaf and disallowed URI schemes are never worked around.
/// </summary>
public sealed class TlsIntermediateRepairTests : IDisposable
{
    private static readonly Uri Aia = new("http://aia.test/issuing.cer?token=secret");
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly X509Certificate2 _root, _intermediate;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public TlsIntermediateRepairTests()
    {
        _root = Authority($"CN=Repair Test Root {_run}", null);
        _intermediate = Authority($"CN=Repair Test Issuing CA {_run}", _root);
    }

    public void Dispose() { _root.Dispose(); _intermediate.Dispose(); }

    private sealed class FakeFetcher(Func<Uri, byte[]?> respond) : IAiaCertificateFetcher
    {
        public List<Uri> Requests { get; } = [];
        public Task<byte[]?> FetchAsync(Uri uri, CancellationToken ct) { Requests.Add(uri); return Task.FromResult(respond(uri)); }
    }

    internal static X509Certificate2 Authority(string subject, X509Certificate2? issuer, bool keyCertSign = true)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(keyCertSign ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (issuer is not null) request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        var from = DateTimeOffset.UtcNow.AddDays(-2);
        var to = issuer is null ? DateTimeOffset.UtcNow.AddYears(3) : issuer.NotAfter.AddDays(-1);
        using var cert = issuer is null ? request.CreateSelfSigned(from, to) : request.Create(issuer, from, to, Guid.NewGuid().ToByteArray()).CopyWithPrivateKey(key);
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    internal static X509Certificate2 Leaf(X509Certificate2 issuer, string host = "m2lbdev.example.test", Uri? aia = null, DateTimeOffset? notAfter = null, bool selfSigned = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        if (!selfSigned) request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(issuer, true, false));
        if (aia is not null) request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, [aia.ToString()]));
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = notAfter ?? DateTimeOffset.UtcNow.AddMonths(6);
        if (selfSigned) return request.CreateSelfSigned(from, to);
        if (to > issuer.NotAfter) to = issuer.NotAfter;
        if (to < from) from = issuer.NotBefore.AddMinutes(1);
        return request.Create(issuer, from, to, Guid.NewGuid().ToByteArray());
    }

    private static TlsChainInspector.Capture Sent(X509Certificate2 leaf, params X509Certificate2[] extra)
    {
        var capture = new TlsChainInspector.Capture { Leaf = leaf.RawData };
        capture.Sent.AddRange(extra.Select(c => c.RawData));
        return capture;
    }

    private X509Certificate2Collection Trusted(params X509Certificate2[] roots) => [.. roots.Select(r => new X509Certificate2(r.RawData))];

    [Fact]
    public async Task MissingIntermediate_WithATrustedRoot_IsRetrievedValidatedAndApplied_NeverTheRootOrTheLeaf()
    {
        using var leaf = Leaf(_intermediate, aia: Aia);
        var fetcher = new FakeFetcher(_ => _intermediate.RawData);

        var result = await TlsIntermediateRepair.RepairAsync(Sent(leaf), "m2lbdev.example.test", 443, Trusted(_root), fetcher, _now);

        result.Outcome.State.Should().Be(TlsRepairStates.Applied);
        result.Intermediates.Should().ContainSingle().Which.Thumbprint.Should().Be(_intermediate.Thumbprint, "only the missing intermediate is added");
        result.Intermediates.Should().NotContain(c => c.Thumbprint == _root.Thumbprint || c.Thumbprint == leaf.Thumbprint, "never the root, never the leaf");
        result.Outcome.IntermediateSubject.Should().Be(_intermediate.Subject);
        result.Outcome.RootSubject.Should().Be(_root.Subject);
        result.Outcome.SourceUri.Should().Be("http://aia.test/issuing.cer", "a query may carry tokens and is never recorded");
        result.Outcome.Detail.Should().Contain("Local k6 trust repair applied").And.Contain("server certificate chain is still incomplete");
        result.Outcome.Checks.Should().Contain(c => c.Contains("signature"));
        fetcher.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task AnUnrelatedCertificateFromTheAiaLocation_IsRejected()
    {
        using var other = Authority($"CN=Unrelated CA {_run}", _root);
        using var leaf = Leaf(_intermediate, aia: Aia);
        var result = await TlsIntermediateRepair.RepairAsync(Sent(leaf), "m2lbdev.example.test", 443, Trusted(_root), new FakeFetcher(_ => other.RawData), _now);
        result.Outcome.State.Should().Be(TlsRepairStates.Rejected);
        result.Outcome.Detail.Should().Contain("is not the issuer");
        result.Intermediates.Should().BeEmpty();
    }

    [Fact]
    public async Task ATamperedIntermediate_SameNameDifferentKey_FailsSignatureValidation()
    {
        using var forged = Authority($"CN=Repair Test Issuing CA {_run}", _root); // same subject, another key
        using var leaf = Leaf(_intermediate, aia: Aia);
        var result = await TlsIntermediateRepair.RepairAsync(Sent(leaf), "m2lbdev.example.test", 443, Trusted(_root), new FakeFetcher(_ => forged.RawData), _now);
        result.Outcome.State.Should().Be(TlsRepairStates.Rejected);
        result.Outcome.Detail.Should().ContainAny("signature", "key identifier");
        TlsIntermediateRepair.SignatureValid(leaf, forged).Should().BeFalse();
        TlsIntermediateRepair.SignatureValid(leaf, _intermediate).Should().BeTrue();
    }

    [Fact]
    public async Task AValidIntermediateUnderAnUnknownRoot_StopsTheRepair_NoRootIsEverTrusted()
    {
        using var otherRoot = Authority($"CN=Other Root {_run}", null);
        using var leaf = Leaf(_intermediate, aia: Aia);
        var fetcher = new FakeFetcher(uri => uri.AbsolutePath.Contains("root") ? _root.RawData : _intermediate.RawData);
        var result = await TlsIntermediateRepair.RepairAsync(Sent(leaf), "m2lbdev.example.test", 443, Trusted(otherRoot), fetcher, _now);
        result.Outcome.State.Should().Be(TlsRepairStates.Stopped);
        result.Outcome.Detail.Should().Contain("not currently trusted").And.Contain("never trusted automatically");
        result.Intermediates.Should().BeEmpty();
    }

    [Fact]
    public async Task SelfSignedExpiredOrWrongHostLeaves_AreNeverWorkedAround()
    {
        var fetcher = new FakeFetcher(_ => _intermediate.RawData);
        using var selfSigned = Leaf(_intermediate, selfSigned: true);
        (await TlsIntermediateRepair.RepairAsync(Sent(selfSigned), "m2lbdev.example.test", 443, Trusted(_root), fetcher, _now)).Outcome.State.Should().Be(TlsRepairStates.NotEligible);
        using var expired = Leaf(_intermediate, aia: Aia, notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        (await TlsIntermediateRepair.RepairAsync(Sent(expired), "m2lbdev.example.test", 443, Trusted(_root), fetcher, _now)).Outcome.Should()
            .Match<TlsRepairOutcome>(o => o.State == TlsRepairStates.NotEligible && o.Detail.Contains("expired"));
        using var wrongHost = Leaf(_intermediate, host: "other.example.test", aia: Aia);
        (await TlsIntermediateRepair.RepairAsync(Sent(wrongHost), "m2lbdev.example.test", 443, Trusted(_root), fetcher, _now)).Outcome.Should()
            .Match<TlsRepairOutcome>(o => o.State == TlsRepairStates.NotEligible && o.Detail.Contains("does not match"));
        fetcher.Requests.Should().BeEmpty("nothing is fetched for a certificate that cannot be repaired");
    }

    [Fact]
    public async Task AServerThatSendsTheCompleteChain_NeedsNoRepair()
    {
        using var leaf = Leaf(_intermediate, aia: Aia);
        var fetcher = new FakeFetcher(_ => _intermediate.RawData);
        var result = await TlsIntermediateRepair.RepairAsync(Sent(leaf, _intermediate), "m2lbdev.example.test", 443, Trusted(_root), fetcher, _now);
        result.Outcome.State.Should().Be(TlsRepairStates.NoLongerNeeded);
        fetcher.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task OnlyHttpAndHttpsAiaLocations_AndOnlyCertificateEncodings_AreAccepted()
    {
        using var fileLeaf = Leaf(_intermediate, aia: new Uri("file:///C:/Windows/System32/drivers/etc/hosts"));
        var fetcher = new FakeFetcher(_ => _intermediate.RawData);
        var result = await TlsIntermediateRepair.RepairAsync(Sent(fileLeaf), "m2lbdev.example.test", 443, Trusted(_root), fetcher, _now);
        result.Outcome.State.Should().Be(TlsRepairStates.Rejected);
        fetcher.Requests.Should().BeEmpty("a file:// location from certificate metadata is never read");

        TlsIntermediateRepair.Parse(_intermediate.Export(X509ContentType.Pfx)).Should().BeEmpty("a PKCS#12 body (key material) is never imported");
        TlsIntermediateRepair.Parse("<html>not a certificate</html>"u8.ToArray()).Should().BeEmpty();
        TlsIntermediateRepair.Parse(System.Text.Encoding.ASCII.GetBytes(_intermediate.ExportCertificatePem())).Should().ContainSingle();
        TlsIntermediateRepair.IsAllowedUri(new Uri("https://user:pw@aia.test/x.cer")).Should().BeFalse();
    }

    [Fact]
    public async Task AnIntermediateWithoutCertificateSigningUsage_IsRejected()
    {
        using var notCa = Authority($"CN=Repair Test Issuing CA {_run}", _root, keyCertSign: false);
        // Signed directly (CertificateRequest itself refuses an issuer without keyCertSign — a validator must refuse it too).
        using var leafKey = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest("CN=m2lbdev.example.test", leafKey, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("m2lbdev.example.test"); request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509AuthorityInformationAccessExtension(null, [Aia.ToString()]));
        using var issuedByNotCa = request.Create(notCa.SubjectName, X509SignatureGenerator.CreateForRSA(notCa.GetRSAPrivateKey()!, System.Security.Cryptography.RSASignaturePadding.Pkcs1),
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(3), Guid.NewGuid().ToByteArray());
        var result = await TlsIntermediateRepair.RepairAsync(Sent(issuedByNotCa), "m2lbdev.example.test", 443, Trusted(_root), new FakeFetcher(_ => notCa.RawData), _now);
        result.Outcome.State.Should().Be(TlsRepairStates.Rejected);
        result.Outcome.Detail.Should().Contain("certificate signing");
    }
}
