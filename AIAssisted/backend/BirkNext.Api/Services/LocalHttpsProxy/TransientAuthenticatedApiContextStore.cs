using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>Non-secret description of an in-memory authenticated API context.</summary>
public sealed record AuthenticatedApiContextDescriptor(string ObservedHost, DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt, string Format, IReadOnlyList<string> ApprovedAuthorities);

/// <summary>
/// Read-only surface for the rest of the application. Deliberately offers no way to obtain the credential: callers ask whether an
/// authenticated API context exists and delegate execution to <see cref="IAuthenticatedApiExecutionService"/>.
/// </summary>
public interface ITransientAuthenticatedApiContextStore
{
    bool IsAuthenticatedApiContextAvailable(string profileId, string contextFingerprint);
    AuthenticatedApiContextDescriptor? Describe(string profileId, string contextFingerprint);
    void Invalidate(string profileId);
    void InvalidateAll();
    int PurgeExpired();
}

/// <summary>Write/apply surface. Only the proxy session (store) and the execution service (apply) use it.</summary>
internal interface ITransientCredentialSink
{
    void Store(string profileId, string contextFingerprint, string observedHost, ApprovedHostSet scope, string bearerToken, DateTimeOffset expiresAt, string format);
    /// <summary>Applies the credential to an HTTPS request for an approved authority of the same environment. False when unavailable or out of scope.</summary>
    bool TryApply(string profileId, string contextFingerprint, HttpRequestMessage request);
}

/// <summary>
/// Memory-only holder of observed Bearer credentials, one per Target Environment profile, bound to the environment's context
/// fingerprint and approved authorities, with automatic expiry. Entries are zeroed on invalidation. Nothing here is serializable:
/// the secret lives in a private byte array of a private class with no accessor.
/// </summary>
public sealed class TransientAuthenticatedApiContextStore(Func<DateTimeOffset>? clock = null) : ITransientAuthenticatedApiContextStore, ITransientCredentialSink, IDisposable
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool IsAuthenticatedApiContextAvailable(string profileId, string contextFingerprint) => Current(profileId, contextFingerprint) is not null;

    public AuthenticatedApiContextDescriptor? Describe(string profileId, string contextFingerprint) =>
        Current(profileId, contextFingerprint) is { } entry ? new(entry.ObservedHost, entry.ObservedAt, entry.ExpiresAt, entry.Format, entry.Scope.Authorities) : null;

    public void Invalidate(string profileId) { if (_entries.TryRemove(profileId, out var entry)) entry.Wipe(); }

    public void InvalidateAll() { foreach (var key in _entries.Keys.ToArray()) Invalidate(key); }

    public int PurgeExpired()
    {
        var now = _clock();
        var purged = 0;
        foreach (var pair in _entries)
            if (pair.Value.ExpiresAt <= now && _entries.TryRemove(pair.Key, out var entry)) { entry.Wipe(); purged++; }
        return purged;
    }

    void ITransientCredentialSink.Store(string profileId, string contextFingerprint, string observedHost, ApprovedHostSet scope, string bearerToken, DateTimeOffset expiresAt, string format)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(contextFingerprint) || !BearerTokenInspector.LooksLikeBearerToken(bearerToken))
            throw new ArgumentException("A profile, context fingerprint and well-formed bearer credential are required.");
        var entry = new Entry(contextFingerprint, observedHost, scope, Encoding.UTF8.GetBytes(bearerToken), _clock(), expiresAt, format);
        _entries.AddOrUpdate(profileId, entry, (_, old) => { old.Wipe(); return entry; });
    }

    bool ITransientCredentialSink.TryApply(string profileId, string contextFingerprint, HttpRequestMessage request)
    {
        var entry = Current(profileId, contextFingerprint);
        if (entry is null || request.RequestUri is not { IsAbsoluteUri: true } uri || !entry.Scope.ContainsUri(uri)) return false;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.Reveal());
        return true;
    }

    private Entry? Current(string profileId, string contextFingerprint)
    {
        if (!_entries.TryGetValue(profileId, out var entry)) return null;
        if (entry.ExpiresAt <= _clock()) { Invalidate(profileId); return null; }
        return string.Equals(entry.ContextFingerprint, contextFingerprint, StringComparison.Ordinal) ? entry : null;
    }

    public void Dispose() => InvalidateAll();

    private sealed class Entry(string contextFingerprint, string observedHost, ApprovedHostSet scope, byte[] secret, DateTimeOffset observedAt, DateTimeOffset expiresAt, string format)
    {
        private readonly byte[] _secret = secret;
        private volatile bool _wiped;
        public string ContextFingerprint { get; } = contextFingerprint;
        public string ObservedHost { get; } = observedHost;
        public ApprovedHostSet Scope { get; } = scope;
        public DateTimeOffset ObservedAt { get; } = observedAt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public string Format { get; } = format;
        /// <summary>Only the credential sink calls this, immediately before writing an Authorization header on an approved request.</summary>
        public string Reveal() => _wiped ? throw new InvalidOperationException("Credential has been wiped.") : Encoding.UTF8.GetString(_secret);
        public void Wipe() { _wiped = true; CryptographicOperations.ZeroMemory(_secret); }
        public override string ToString() => "TransientAuthenticatedApiContext";
    }
}
