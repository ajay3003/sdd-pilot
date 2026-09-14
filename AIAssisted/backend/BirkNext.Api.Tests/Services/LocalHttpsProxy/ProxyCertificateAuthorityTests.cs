using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.LocalHttpsProxy;
using Microsoft.Extensions.Logging;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

/// <summary>Certificate lifecycle (section 34): identity, no static key, approved-host leaves only, expiry, trust state, cleanup, no key material in logs.</summary>
public sealed class ProxyCertificateAuthorityTests
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly EphemeralCertificateStore _store = new();
    private readonly CapturingLogger<ProxyCertificateAuthority> _log = new();

    private ProxyCertificateAuthority Authority() => new(_store, () => _now, _log);

    [Fact]
    public void AuthorityHasIntendedIdentityAndConstraints()
    {
        using var authority = Authority();
        var certificate = authority.EnsureAuthority();
        Assert.Contains("CN=BirkNext DEV HTTPS Inspection CA", certificate.Subject);
        Assert.Contains("OU=DEV testing only", certificate.Subject);
        Assert.True(certificate.HasPrivateKey);
        var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(constraints.CertificateAuthority);
        Assert.True(constraints.Critical);
        Assert.InRange(certificate.NotAfter.ToUniversalTime(), _now.UtcDateTime.AddDays(364), _now.UtcDateTime.AddDays(366));
        Assert.Equal(ProxyCertificateTrustState.NotTrusted, authority.Status().State);
    }

    [Fact]
    public void EveryWorkstationGetsItsOwnKeyNeverAStaticOne()
    {
        using var first = new ProxyCertificateAuthority(new EphemeralCertificateStore(), () => _now);
        using var second = new ProxyCertificateAuthority(new EphemeralCertificateStore(), () => _now);
        var a = first.EnsureAuthority();
        var b = second.EnsureAuthority();
        Assert.NotEqual(a.Thumbprint, b.Thumbprint);
        Assert.NotEqual(a.GetPublicKeyString(), b.GetPublicKeyString());
        Assert.NotEqual(a.SerialNumber, b.SerialNumber);
    }

    [Fact]
    public void AuthorityIsReusedFromTheStoreAcrossInstances()
    {
        using var first = Authority();
        var thumbprint = first.EnsureAuthority().Thumbprint;
        using var second = Authority();
        Assert.Equal(thumbprint, second.EnsureAuthority().Thumbprint);
    }

    [Fact]
    public void LeafIsIssuedOnlyForInterceptableHostsAndChainsToTheAuthority()
    {
        using var authority = Authority();
        var root = authority.EnsureAuthority();
        var leaf = authority.IssueLeaf("api.example.test");
        Assert.True(leaf.HasPrivateKey);
        Assert.Equal(root.Subject, leaf.Issuer);
        Assert.Contains("CN=api.example.test", leaf.Subject);
        var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("api.example.test", san.EnumerateDnsNames());
        Assert.False(leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.True(leaf.NotAfter.ToUniversalTime() <= _now.UtcDateTime.AddHours(24).AddMinutes(1));
        Assert.True(leaf.MatchesHostname("api.example.test"));
        Assert.False(leaf.MatchesHostname("other.example.test"));

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority.AuthorityPublicCertificate!);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = _now.UtcDateTime;
        Assert.True(chain.Build(leaf), string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation)));

        Assert.Same(leaf, authority.IssueLeaf("API.example.test."));
        foreach (var host in new[] { "login.microsoftonline.com", "m2lbdev-bufetat-no.access.mcas.ms", "localhost", "127.0.0.1", "*.example.test", "bad host", "" })
            Assert.Throws<ArgumentException>(() => authority.IssueLeaf(host));
    }

    [Fact]
    public void ExpiredAuthorityIsReportedAndReplacedOnNextUse()
    {
        using var authority = Authority();
        var original = authority.EnsureAuthority().Thumbprint;
        _now = _now.AddDays(366);
        var status = authority.Status();
        Assert.Equal(ProxyCertificateTrustState.Expired, status.State);
        Assert.Contains("expired", status.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(original, authority.EnsureAuthority().Thumbprint);
        Assert.Equal(ProxyCertificateTrustState.NotTrusted, authority.Status().State);
    }

    [Fact]
    public void TrustStateFollowsInstallAndRemoveAndRemoveLeavesNoTrustAnchor()
    {
        using var authority = Authority();
        Assert.Equal(ProxyCertificateTrustState.NotGenerated, authority.Status().State);
        authority.EnsureAuthority();
        var untrusted = authority.Status();
        Assert.Equal(ProxyCertificateTrustState.NotTrusted, untrusted.State);
        Assert.True(untrusted.InstallSupported);
        Assert.Contains("Install test certificate", untrusted.Guidance);

        var installed = authority.Install();
        Assert.Equal(ProxyCertificateTrustState.Trusted, installed.State);
        Assert.True(_store.IsTrusted(installed.Thumbprint!));

        var removed = authority.Remove();
        Assert.Equal(ProxyCertificateTrustState.NotGenerated, removed.State);
        Assert.False(_store.IsTrusted(installed.Thumbprint!));
        Assert.Null(_store.LoadAuthority(ProxyCertificateAuthority.AuthoritySubject, _now));
        Assert.Null(authority.AuthorityPublicCertificate);
    }

    [Fact]
    public void InstallIsUnavailableWhenTheStoreCannotManageTrust()
    {
        using var authority = new ProxyCertificateAuthority(new EphemeralCertificateStore { TrustManagementSupported = false }, () => _now);
        authority.EnsureAuthority();
        var status = authority.Install();
        Assert.Equal(ProxyCertificateTrustState.NotTrusted, status.State);
        Assert.False(status.InstallSupported);
        Assert.Contains("manually", status.Guidance);
    }

    [Fact]
    public void PrivateKeyMaterialNeverReachesLogsOrStatus()
    {
        using var authority = Authority();
        var root = authority.EnsureAuthority();
        var leaf = authority.IssueLeaf("api.example.test");
        authority.Install();
        Assert.True(root.HasPrivateKey);
        Assert.True(leaf.HasPrivateKey);
        var statusJson = JsonSerializer.Serialize(authority.Status());
        // Key material is a long base64/DER blob; logs and status may only carry subject, thumbprint and dates.
        var keyBlob = new System.Text.RegularExpressions.Regex("[A-Za-z0-9+/=_-]{100,}");
        foreach (var text in _log.Messages.Append(statusJson))
        {
            Assert.DoesNotContain("PRIVATE KEY", text);
            Assert.DoesNotContain("RSA", text);
            Assert.DoesNotMatch(keyBlob, text);
        }
        Assert.Contains(_log.Messages, m => m.Contains("Generated dedicated inspection root") && m.Contains(root.Thumbprint));
        Assert.DoesNotContain("privateKey", statusJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(root.Thumbprint, statusJson);
    }
}

/// <summary>Captures formatted log messages so tests can prove secrets never reach the log pipeline.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<string> _messages = [];
    public IReadOnlyList<string> Messages { get { lock (_messages) return _messages.ToList(); } }
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var text = formatter(state, exception) + (exception is null ? "" : " " + exception);
        lock (_messages) _messages.Add(text);
    }
    private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
}
