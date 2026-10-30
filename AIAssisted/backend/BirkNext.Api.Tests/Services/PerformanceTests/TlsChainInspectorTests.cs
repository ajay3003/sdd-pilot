using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.PerformanceTests;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Container TLS trust diagnostics against a local TLS server (loopback only) presenting generated chains: trusted with the right CA bundle,
/// untrusted (root missing from the bundle), incomplete (server omits its intermediate — Windows would download it, k6 does not), expired and
/// wrong-host certificates, and no bundle. The inspector always rejects the certificate; it never trusts or sends anything.
/// </summary>
public sealed class TlsChainInspectorTests : IDisposable
{
    private readonly X509Certificate2 _root, _intermediate, _otherRoot;
    private readonly List<IDisposable> _dispose = [];

    public TlsChainInspectorTests()
    {
        // Unique names per test: the Windows chain engine matches issuers by name and caches certificates within the process.
        var run = Guid.NewGuid().ToString("N")[..8];
        _root = Authority($"CN=BirkNext Test Root CA {run}", null, null);
        _intermediate = Authority($"CN=BirkNext Test Issuing CA {run}", _root, null);
        _otherRoot = Authority($"CN=Some Other Root CA {run}", null, null);
    }

    public void Dispose()
    {
        foreach (var d in _dispose) d.Dispose();
        _root.Dispose(); _intermediate.Dispose(); _otherRoot.Dispose();
    }

    private static X509Certificate2 Authority(string subject, X509Certificate2? issuer, DateTimeOffset? notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var from = DateTimeOffset.UtcNow.AddDays(-2);
        var to = notAfter ?? DateTimeOffset.UtcNow.AddYears(2);
        if (issuer is not null && to > issuer.NotAfter) to = issuer.NotAfter;
        using var cert = issuer is null ? request.CreateSelfSigned(from, to) : request.Create(issuer, from, to, Guid.NewGuid().ToByteArray()).CopyWithPrivateKey(key);
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    private static X509Certificate2 Leaf(X509Certificate2 issuer, string host = "localhost", DateTimeOffset? notAfter = null, DateTimeOffset? notBefore = null)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var from = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        var to = notAfter ?? DateTimeOffset.UtcNow.AddMonths(6);
        if (to > issuer.NotAfter) to = issuer.NotAfter;
        if (from < issuer.NotBefore) from = issuer.NotBefore;
        using var cert = request.Create(issuer, from, to, Guid.NewGuid().ToByteArray()).CopyWithPrivateKey(key);
        return new X509Certificate2(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>A one-connection TLS server on loopback presenting <paramref name="leaf"/> and <paramref name="extra"/> (the chain it sends).</summary>
    private int Serve(X509Certificate2 leaf, params X509Certificate2[] extra)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _dispose.Add(new Stopper(listener));
        var context = SslStreamCertificateContext.Create(leaf, new X509Certificate2Collection(extra), offline: true);
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var ssl = new SslStream(client.GetStream());
                try { await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = context }); } catch (Exception) { }
            }
            catch (Exception) { }
        });
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class Stopper(TcpListener listener) : IDisposable { public void Dispose() => listener.Stop(); }

    private static X509Certificate2Collection Bundle(params X509Certificate2[] certificates) =>
        new(certificates.Select(c => new X509Certificate2(c.Export(X509ContentType.Cert))).ToArray());

    private static Task<TlsChainDiagnostics> Inspect(int port, X509Certificate2Collection? bundle, string host = "localhost") =>
        TlsChainInspector.InspectAsync(new Uri($"https://{host}:{port}/"), bundle, TimeSpan.FromSeconds(10), connectHost: "127.0.0.1");

    [Fact]
    public async Task TrustedChain_WithTheRightBundle_IsTrustedByTheContainerSimulation()
    {
        using var leaf = Leaf(_intermediate);
        var result = await Inspect(Serve(leaf, _intermediate), Bundle(_root));

        result.Reached.Should().BeTrue(result.Detail);
        result.BundleTrusted.Should().BeTrue(result.BundleDetail);
        result.MissingIntermediate.Should().BeFalse();
        result.Certificates[0].Subject.Should().Be("CN=localhost");
        result.Certificates[0].Issuer.Should().StartWith("CN=BirkNext Test Issuing CA");
        result.Certificates.Should().OnlyContain(c => c.NotAfter > DateTimeOffset.UtcNow);
        result.HostTrusted.Should().BeFalse("the generated test root is not in the Windows store");
        result.FailedLayer.Should().Be(TlsTrustLayers.Unknown, "the bundle is sufficient for the chain the host sees");
    }

    [Fact]
    public async Task UntrustedChain_RootMissingFromTheBundle_NamesTheContainerBundleAndTheRoot()
    {
        using var leaf = Leaf(_intermediate);
        var result = await Inspect(Serve(leaf, _intermediate), Bundle(_otherRoot));

        result.BundleTrusted.Should().BeFalse();
        result.FailedLayer.Should().Be(TlsTrustLayers.ContainerCaBundle);
        result.Guidance.Should().Contain("does not contain the root").And.Contain("PEM");
    }

    [Fact]
    public async Task IncompleteChain_ServerOmitsItsIntermediate_IsTheServerChainLayer()
    {
        using var leaf = Leaf(_intermediate);
        var result = await Inspect(Serve(leaf), Bundle(_root));

        result.MissingIntermediate.Should().BeTrue();
        result.SentByServer.Should().Be(1);
        result.BundleTrusted.Should().BeFalse("like k6, the simulation downloads nothing");
        result.FailedLayer.Should().Be(TlsTrustLayers.ServerChain);
        result.Guidance.Should().Contain("does not send its intermediate").And.Contain("BirkNext Test Issuing CA");
    }

    [Fact]
    public async Task ExpiredCertificate_IsTheTargetCertificateLayer_ABundleCannotFixIt()
    {
        using var leaf = Leaf(_intermediate, notBefore: DateTimeOffset.UtcNow.AddDays(-2).AddMinutes(1), notAfter: DateTimeOffset.UtcNow.AddMinutes(-5));
        var result = await Inspect(Serve(leaf, _intermediate), Bundle(_root));

        result.Expired.Should().BeTrue();
        result.FailedLayer.Should().Be(TlsTrustLayers.TargetCertificate);
        result.Guidance.Should().Contain("expired").And.Contain("cannot fix");
    }

    [Fact]
    public async Task WrongHost_IsTheTargetCertificateLayer()
    {
        using var leaf = Leaf(_intermediate, host: "other.example.test");
        var result = await Inspect(Serve(leaf, _intermediate), Bundle(_root));
        result.HostnameMatches.Should().BeFalse();
        result.FailedLayer.Should().Be(TlsTrustLayers.TargetCertificate);
    }

    [Fact]
    public async Task NoBundle_PointsToTheContainerDefaultStore_AndTheRootToExport()
    {
        using var leaf = Leaf(_intermediate);
        var result = await Inspect(Serve(leaf, _intermediate), null);
        result.BundleTrusted.Should().BeNull();
        result.FailedLayer.Should().Be(TlsTrustLayers.ContainerDefaultStore);
        result.Guidance.Should().Contain("BirkNext Test Root CA").And.Contain(PerformanceCaBundleInspector.ConfigurationKey);
    }

    [Fact]
    public async Task UnreachableAndHttpTargets_AreReportedWithoutAChain()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        (await Inspect(port, null)).Reached.Should().BeFalse();
        (await TlsChainInspector.InspectAsync(new Uri("http://localhost/"), null, TimeSpan.FromSeconds(1))).Detail.Should().Contain("not HTTPS");
    }

    [Fact]
    public void Bundle_WithOnlyALeaf_IsRejected_AndExpiredCasAreReported()
    {
        var dir = Path.Combine(Path.GetTempPath(), "birknext-ca-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using var leaf = Leaf(_intermediate);
            var leafOnly = Path.Combine(dir, "leaf.pem");
            File.WriteAllText(leafOnly, leaf.ExportCertificatePem());
            PerformanceCaBundleInspector.Inspect(leafOnly).Status.Should().Be(PerformanceCaBundleState.Invalid);
            PerformanceCaBundleInspector.Inspect(leafOnly).Detail.Should().Contain("no CA certificate");

            using var expired = Authority("CN=Expired CA", null, DateTimeOffset.UtcNow.AddMinutes(-1));
            var mixed = Path.Combine(dir, "bundle.pem");
            File.WriteAllText(mixed, _root.ExportCertificatePem() + "\n" + expired.ExportCertificatePem());
            var state = PerformanceCaBundleInspector.Inspect(mixed);
            state.Status.Should().Be(PerformanceCaBundleState.Ready);
            state.Detail.Should().Contain("1 certificate is expired");

            var keyFile = Path.Combine(dir, "key.pem");
            using var key = RSA.Create(2048);
            File.WriteAllText(keyFile, _root.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem());
            PerformanceCaBundleInspector.Inspect(keyFile).Detail.Should().Contain("private key");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Stages_KeepHttpNotReached_WhenTlsFails()
    {
        var stages = PerformanceNetworkStages.From(new ContainerTlsValidation { TargetOrigin = "https://m2lbdev.example.test", ProbeState = PerformanceProbeStates.TlsTrustFailure });
        stages.Single(s => s.Key == "tls").State.Should().Be("Failed");
        stages.Single(s => s.Key == "http").State.Should().Be("Not reached");
        stages.Single(s => s.Key == "dns").State.Should().Be("Passed");
    }
}
