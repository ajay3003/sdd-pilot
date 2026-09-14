using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>Storage and trust operations for the dedicated inspection root. Private keys never leave OS-protected storage or process memory.</summary>
public interface IProxyCertificateStore
{
    bool PersistenceSupported { get; }
    bool TrustManagementSupported { get; }
    X509Certificate2? LoadAuthority(string subjectName, DateTimeOffset now);
    void SaveAuthority(X509Certificate2 authorityWithPrivateKey);
    void DeleteAuthority(string thumbprint);
    bool IsTrusted(string thumbprint);
    void AddTrust(X509Certificate2 publicCertificate);
    void RemoveTrust(string thumbprint);
}

public interface IProxyCertificateAuthority
{
    string SubjectName { get; }
    ProxyCertificateStatus Status();
    /// <summary>Loads or generates the dedicated root. Never a shared or static key.</summary>
    X509Certificate2 EnsureAuthority();
    /// <summary>Public part of the current root for chain building. Null until generated.</summary>
    X509Certificate2? AuthorityPublicCertificate { get; }
    /// <summary>Issues (or returns the cached) short-lived leaf for a syntactically valid, interceptable DNS host.</summary>
    X509Certificate2 IssueLeaf(string host);
    /// <summary>Adds the root to the current user's trust store. Explicit user action only.</summary>
    ProxyCertificateStatus Install();
    /// <summary>Removes trust and the stored root so no undocumented trust anchor remains.</summary>
    ProxyCertificateStatus Remove();
}

/// <summary>
/// Dedicated "BirkNext DEV HTTPS Inspection CA": generated per workstation (RSA 2048, one year), stored only in the current user's
/// certificate store (OS-protected, non-exportable) when supported, otherwise kept in memory for the process lifetime. Leaf
/// certificates are issued only for approved hosts, live at most 24 hours and stay in process memory. No private key is ever logged,
/// written to disk by BirkNext, or shipped with the product.
/// </summary>
public sealed class ProxyCertificateAuthority(IProxyCertificateStore store, Func<DateTimeOffset>? clock = null, ILogger<ProxyCertificateAuthority>? logger = null, string? commonNameOverride = null) : IProxyCertificateAuthority, IDisposable
{
    public const string AuthorityCommonName = "BirkNext DEV HTTPS Inspection CA";
    public static readonly string AuthoritySubject = $"CN={AuthorityCommonName}, O=BirkNext, OU=DEV testing only";
    public static readonly TimeSpan AuthorityLifetime = TimeSpan.FromDays(365);
    public static readonly TimeSpan LeafLifetime = TimeSpan.FromHours(24);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _gate = new();
    private readonly Dictionary<string, X509Certificate2> _leaves = new(StringComparer.Ordinal);
    // Fixed for the product (one stable identity per workstation); a test may supply a unique name so its ephemeral CA never collides
    // with another test's same-named-but-different-keyed CA in the SChannel chain caches.
    private readonly string _subject = commonNameOverride is null ? AuthoritySubject : $"CN={commonNameOverride}, O=BirkNext, OU=DEV testing only";
    private X509Certificate2? _authority;
    private X509Certificate2? _authorityPublic;

    public string SubjectName => _subject;
    public X509Certificate2? AuthorityPublicCertificate { get { lock (_gate) return _authorityPublic; } }

    public ProxyCertificateStatus Status()
    {
        lock (_gate)
        {
            try
            {
                var now = _clock();
                var authority = _authority ?? store.LoadAuthority(_subject, now);
                if (authority is null)
                    return new()
                    {
                        State = ProxyCertificateTrustState.NotGenerated, Subject = _subject, InstallSupported = store.TrustManagementSupported,
                        Guidance = "The dedicated BirkNext DEV inspection root is generated when the proxy starts. Trust must then be installed explicitly before Edge accepts intercepted connections."
                    };
                if (_authority is null) Adopt(authority);
                if (authority.NotAfter <= now.UtcDateTime)
                    return Describe(authority, ProxyCertificateTrustState.Expired, "The inspection root has expired. Remove the old test certificate; a new root is generated on the next proxy start and must be trusted again.");
                var trusted = store.IsTrusted(authority.Thumbprint);
                return Describe(authority, trusted ? ProxyCertificateTrustState.Trusted : ProxyCertificateTrustState.NotTrusted,
                    trusted ? "The BirkNext DEV inspection root is trusted for the current Windows user. Remove it when authenticated proxy testing is no longer needed."
                            : store.TrustManagementSupported
                                ? "Not trusted: use Install test certificate (Windows asks you to confirm), or import the root manually into Current User > Trusted Root Certification Authorities."
                                : "Not trusted: import the root manually into the current user trusted root store; automatic installation is not supported on this platform.");
            }
            catch (Exception ex) when (ex is CryptographicException or System.Security.SecurityException or PlatformNotSupportedException or IOException)
            {
                logger?.LogWarning("Proxy certificate status check failed with {ExceptionType}.", ex.GetType().Name);
                return new() { State = ProxyCertificateTrustState.Unknown, Subject = _subject, InstallSupported = false, Guidance = "The certificate store could not be read." };
            }
        }
    }

    public X509Certificate2 EnsureAuthority()
    {
        lock (_gate)
        {
            var now = _clock();
            if (_authority is not null && _authority.NotAfter > now.UtcDateTime.AddHours(1)) return _authority;
            var loaded = store.LoadAuthority(_subject, now);
            if (loaded is not null && loaded.HasPrivateKey && loaded.NotAfter > now.UtcDateTime.AddHours(1)) { Adopt(loaded); return _authority!; }

            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(new X500DistinguishedName(_subject), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            var notBefore = now.AddMinutes(-5);
            using var selfSigned = request.CreateSelfSigned(notBefore, notBefore + AuthorityLifetime);
            // Re-import so the private key is usable by SslStream/SChannel; the store decides how (and whether) it persists.
            var usable = new X509Certificate2(selfSigned.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
            if (store.PersistenceSupported)
            {
                try { store.SaveAuthority(usable); }
                catch (Exception ex) when (ex is CryptographicException or System.Security.SecurityException or PlatformNotSupportedException)
                {
                    logger?.LogWarning("Inspection root could not be persisted ({ExceptionType}); using an in-memory root for this session.", ex.GetType().Name);
                }
            }
            Adopt(usable);
            logger?.LogInformation("Generated dedicated inspection root {Subject} (thumbprint {Thumbprint}).", _subject, usable.Thumbprint);
            return _authority!;
        }
    }

    public X509Certificate2 IssueLeaf(string host)
    {
        if (!ApprovedHostSet.IsInterceptableHost(host)) throw new ArgumentException("Leaf certificates are issued only for interceptable DNS hosts.");
        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        lock (_gate)
        {
            var authority = EnsureAuthority();
            var now = _clock();
            if (_leaves.TryGetValue(name, out var cached) && cached.NotAfter > now.UtcDateTime.AddMinutes(30)) return cached;
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(new X500DistinguishedName($"CN={name}, O=BirkNext DEV HTTPS inspection"), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName(name);
            request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // serverAuth only
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] &= 0x7F;
            var notBefore = now.AddMinutes(-5);
            var notAfter = new DateTimeOffset(authority.NotAfter.ToUniversalTime()).AddMinutes(-1);
            if (now + LeafLifetime < notAfter) notAfter = now + LeafLifetime;
            using var signed = request.Create(authority, notBefore, notAfter, serial);
            using var withKey = signed.CopyWithPrivateKey(rsa);
            var usable = new X509Certificate2(withKey.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
            if (_leaves.TryGetValue(name, out var previous)) previous.Dispose();
            _leaves[name] = usable;
            return usable;
        }
    }

    public ProxyCertificateStatus Install()
    {
        lock (_gate)
        {
            var authority = EnsureAuthority();
            if (!store.TrustManagementSupported) return Status();
            store.AddTrust(new X509Certificate2(authority.Export(X509ContentType.Cert)));
            logger?.LogInformation("Inspection root trust installed for the current user (thumbprint {Thumbprint}).", authority.Thumbprint);
            return Status();
        }
    }

    public ProxyCertificateStatus Remove()
    {
        lock (_gate)
        {
            var authority = _authority ?? store.LoadAuthority(_subject, _clock());
            if (authority is null) return Status();
            var thumbprint = authority.Thumbprint;
            if (store.TrustManagementSupported) store.RemoveTrust(thumbprint);
            if (store.PersistenceSupported) store.DeleteAuthority(thumbprint);
            foreach (var leaf in _leaves.Values) leaf.Dispose();
            _leaves.Clear();
            _authority?.Dispose(); _authority = null;
            _authorityPublic?.Dispose(); _authorityPublic = null;
            logger?.LogInformation("Inspection root removed (thumbprint {Thumbprint}).", thumbprint);
            return Status();
        }
    }

    private void Adopt(X509Certificate2 authority)
    {
        if (!ReferenceEquals(_authority, authority)) { _authority?.Dispose(); _authority = authority; }
        _authorityPublic?.Dispose();
        _authorityPublic = new X509Certificate2(authority.Export(X509ContentType.Cert));
        foreach (var leaf in _leaves.Values) leaf.Dispose();
        _leaves.Clear();
    }

    private ProxyCertificateStatus Describe(X509Certificate2 authority, ProxyCertificateTrustState state, string guidance) => new()
    {
        State = state, Subject = authority.Subject, Thumbprint = authority.Thumbprint, NotAfter = new DateTimeOffset(authority.NotAfter.ToUniversalTime()),
        InstallSupported = store.TrustManagementSupported, Guidance = guidance
    };

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var leaf in _leaves.Values) leaf.Dispose();
            _leaves.Clear();
            _authority?.Dispose(); _authorityPublic?.Dispose();
            _authority = null; _authorityPublic = null;
        }
    }
}

/// <summary>Current-user certificate stores on Windows: root kept in Personal (non-exportable key), trust in Trusted Root (Windows confirms the change with the user).</summary>
public sealed class WindowsUserCertificateStore : IProxyCertificateStore
{
    public bool PersistenceSupported => OperatingSystem.IsWindows();
    public bool TrustManagementSupported => OperatingSystem.IsWindows();

    public X509Certificate2? LoadAuthority(string subjectName, DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        return store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, subjectName, false)
            .Where(c => c.HasPrivateKey && c.NotAfter > now.UtcDateTime)
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();
    }

    [SupportedOSPlatform("windows")]
    public void SaveAuthority(X509Certificate2 authorityWithPrivateKey)
    {
        // Non-exportable, user key set: the private key stays in the user's DPAPI-protected key container.
        var persisted = new X509Certificate2(authorityWithPrivateKey.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet)
        { FriendlyName = ProxyCertificateAuthority.AuthorityCommonName };
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persisted);
    }

    public void DeleteAuthority(string thumbprint)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
        foreach (var certificate in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false)) store.Remove(certificate);
    }

    public bool IsTrusted(string thumbprint)
    {
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.Root, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                if (store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).Count > 0) return true;
            }
            catch (CryptographicException) { }
        }
        return false;
    }

    public void AddTrust(X509Certificate2 publicCertificate)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(publicCertificate); // Windows shows its own confirmation dialog for a new user root.
    }

    public void RemoveTrust(string thumbprint)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
        foreach (var certificate in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false)) store.Remove(certificate);
    }
}

/// <summary>Process-memory store: no persistence beyond the process, trust recorded in memory only (non-Windows hosts and tests).</summary>
public sealed class EphemeralCertificateStore : IProxyCertificateStore
{
    private readonly object _gate = new();
    private X509Certificate2? _authority;
    private readonly HashSet<string> _trusted = new(StringComparer.OrdinalIgnoreCase);

    public bool PersistenceSupported => true;
    public bool TrustManagementSupported { get; init; } = true;

    public X509Certificate2? LoadAuthority(string subjectName, DateTimeOffset now)
    {
        lock (_gate) return _authority is not null && _authority.Subject == subjectName && _authority.NotAfter > now.UtcDateTime ? _authority : null;
    }
    public void SaveAuthority(X509Certificate2 authorityWithPrivateKey) { lock (_gate) _authority = authorityWithPrivateKey; }
    public void DeleteAuthority(string thumbprint) { lock (_gate) if (_authority?.Thumbprint == thumbprint) _authority = null; }
    public bool IsTrusted(string thumbprint) { lock (_gate) return _trusted.Contains(thumbprint); }
    public void AddTrust(X509Certificate2 publicCertificate) { lock (_gate) _trusted.Add(publicCertificate.Thumbprint); }
    public void RemoveTrust(string thumbprint) { lock (_gate) _trusted.Remove(thumbprint); }
}
