using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace BirkNext.Api.Services.LocalHttpsProxy;

/// <summary>Non-secret description of an in-memory authenticated API context.</summary>
/// <param name="Generation">How many different credentials the profile has held since the backend started (1 = the first sign-in; it grows when
/// the application renews its token or the tester signs in again). Re-observing the same token keeps the generation.</param>
/// <param name="Identity">Non-reversible reference to the signed-in identity (see <see cref="BearerTokenMetadata.Identity"/>), or null.</param>
public sealed record AuthenticatedApiContextDescriptor(string ObservedHost, DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt, string Format, IReadOnlyList<string> ApprovedAuthorities,
    int Generation = 1, string? Identity = null);

/// <summary>Which attended credential was applied to a request: its generation, identity reference and expiry. Never the value.</summary>
public sealed record AttendedSessionStamp(int Generation, string? Identity, DateTimeOffset ExpiresAt);

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
    void Store(string profileId, string contextFingerprint, string observedHost, ApprovedHostSet scope, string bearerToken, DateTimeOffset expiresAt, string format, string? identity = null);
    /// <summary>Applies the credential to an HTTPS request for an approved authority of the same environment. False when unavailable or out of scope.</summary>
    bool TryApply(string profileId, string contextFingerprint, HttpRequestMessage request);
    /// <summary>As <see cref="TryApply(string, string, HttpRequestMessage)"/>, and says which credential generation was applied.</summary>
    bool TryApply(string profileId, string contextFingerprint, HttpRequestMessage request, out AttendedSessionStamp? stamp);
    /// <summary>Approved host set of the current (non-expired) context for the profile, so a review can pre-validate a target host without holding a runtime session. Null when no valid context exists.</summary>
    ApprovedHostSet? ScopeOf(string profileId, string contextFingerprint);
    /// <summary>
    /// The credential for one performance run against <paramref name="target"/> (an approved HTTPS authority of the same environment), with its
    /// expiry. Only the performance execution service calls it, to hand the value to its k6 container's process environment for that run.
    /// False when unavailable, expired or out of scope.
    /// </summary>
    bool TryLease(string profileId, string contextFingerprint, Uri target, out string bearerToken, out DateTimeOffset expiresAt);
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
    private readonly ConcurrentDictionary<string, int> _generations = new(StringComparer.Ordinal);

    public bool IsAuthenticatedApiContextAvailable(string profileId, string contextFingerprint) => Current(profileId, contextFingerprint) is not null;

    public AuthenticatedApiContextDescriptor? Describe(string profileId, string contextFingerprint) =>
        Current(profileId, contextFingerprint) is { } entry ? new(entry.ObservedHost, entry.ObservedAt, entry.ExpiresAt, entry.Format, entry.Scope.Authorities, entry.Generation, entry.Identity) : null;

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

    void ITransientCredentialSink.Store(string profileId, string contextFingerprint, string observedHost, ApprovedHostSet scope, string bearerToken, DateTimeOffset expiresAt, string format, string? identity)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(contextFingerprint) || !BearerTokenInspector.LooksLikeBearerToken(bearerToken))
            throw new ArgumentException("A profile, context fingerprint and well-formed bearer credential are required.");
        var bytes = Encoding.UTF8.GetBytes(bearerToken);
        var hash = SHA256.HashData(bytes);
        // The proxy re-stores the credential on every successful observed request: the same token keeps its generation, a new one increments it.
        var same = _entries.TryGetValue(profileId, out var previous) && previous.ContextFingerprint == contextFingerprint && CryptographicOperations.FixedTimeEquals(previous.TokenHash, hash);
        var generation = same ? previous!.Generation : _generations.AddOrUpdate(profileId, 1, (_, g) => g + 1);
        var entry = new Entry(contextFingerprint, observedHost, scope, bytes, _clock(), expiresAt, format, hash, generation, identity);
        _entries.AddOrUpdate(profileId, entry, (_, old) => { old.Wipe(); return entry; });
    }

    bool ITransientCredentialSink.TryApply(string profileId, string contextFingerprint, HttpRequestMessage request) =>
        ((ITransientCredentialSink)this).TryApply(profileId, contextFingerprint, request, out _);

    bool ITransientCredentialSink.TryApply(string profileId, string contextFingerprint, HttpRequestMessage request, out AttendedSessionStamp? stamp)
    {
        stamp = null;
        var entry = Current(profileId, contextFingerprint);
        if (entry is null || request.RequestUri is not { IsAbsoluteUri: true } uri || !entry.Scope.ContainsUri(uri)) return false;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", entry.Reveal());
        stamp = new(entry.Generation, entry.Identity, entry.ExpiresAt);
        return true;
    }

    ApprovedHostSet? ITransientCredentialSink.ScopeOf(string profileId, string contextFingerprint) => Current(profileId, contextFingerprint)?.Scope;

    bool ITransientCredentialSink.TryLease(string profileId, string contextFingerprint, Uri target, out string bearerToken, out DateTimeOffset expiresAt)
    {
        (bearerToken, expiresAt) = ("", default);
        var entry = Current(profileId, contextFingerprint);
        if (entry is null || !target.IsAbsoluteUri || !entry.Scope.ContainsUri(target)) return false;
        (bearerToken, expiresAt) = (entry.Reveal(), entry.ExpiresAt);
        return true;
    }

    private Entry? Current(string profileId, string contextFingerprint)
    {
        if (!_entries.TryGetValue(profileId, out var entry)) return null;
        if (entry.ExpiresAt <= _clock()) { Invalidate(profileId); return null; }
        return string.Equals(entry.ContextFingerprint, contextFingerprint, StringComparison.Ordinal) ? entry : null;
    }

    public void Dispose() => InvalidateAll();

    private sealed class Entry(string contextFingerprint, string observedHost, ApprovedHostSet scope, byte[] secret, DateTimeOffset observedAt, DateTimeOffset expiresAt, string format,
        byte[] tokenHash, int generation, string? identity)
    {
        public byte[] TokenHash { get; } = tokenHash;
        public int Generation { get; } = generation;
        public string? Identity { get; } = identity;
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
