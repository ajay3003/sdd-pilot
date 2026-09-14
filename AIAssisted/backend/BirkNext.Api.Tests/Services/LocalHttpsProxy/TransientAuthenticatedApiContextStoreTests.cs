using System.Reflection;
using System.Text.Json;
using BirkNext.Api.Services.LocalHttpsProxy;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

/// <summary>Memory-only credential store: binding, expiry (T), wipe (Q/R/S), never serialized (K), no extraction API (J).</summary>
public sealed class TransientAuthenticatedApiContextStoreTests
{
    private const string Fp = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly string Token = BearerTokenInspector.BuildUnsignedJwt(new { exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), aud = "api://dev" });
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly ApprovedHostSet _scope = ApprovedHostSet.Create("https://app.example.test/", ["api.example.test"]);

    private TransientAuthenticatedApiContextStore Store() => new(() => _now);
    private static ITransientCredentialSink Sink(TransientAuthenticatedApiContextStore store) => store;

    [Fact]
    public void StoredCredentialIsAvailableOnlyForTheSameProfileAndFingerprint()
    {
        using var store = Store();
        Sink(store).Store("dev", Fp, "api.example.test", _scope, Token, _now.AddMinutes(30), "JWT");
        Assert.True(store.IsAuthenticatedApiContextAvailable("dev", Fp));
        Assert.False(store.IsAuthenticatedApiContextAvailable("dev", new string('B', 64)));
        Assert.False(store.IsAuthenticatedApiContextAvailable("qa", Fp));
        var descriptor = store.Describe("dev", Fp)!;
        Assert.Equal("api.example.test", descriptor.ObservedHost);
        Assert.Equal("JWT", descriptor.Format);
        Assert.Contains("api.example.test:443", descriptor.ApprovedAuthorities);
    }

    [Fact]
    public void ExpiryInvalidatesTheContext()
    {
        using var store = Store();
        Sink(store).Store("dev", Fp, "api.example.test", _scope, Token, _now.AddMinutes(30), "JWT");
        _now = _now.AddMinutes(31);
        Assert.False(store.IsAuthenticatedApiContextAvailable("dev", Fp));
        Assert.Null(store.Describe("dev", Fp));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/x");
        Assert.False(Sink(store).TryApply("dev", Fp, request));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void PurgeRemovesExpiredEntriesOnly()
    {
        using var store = Store();
        Sink(store).Store("dev", Fp, "api.example.test", _scope, Token, _now.AddMinutes(5), "JWT");
        Sink(store).Store("qa", Fp, "api.example.test", _scope, Token, _now.AddMinutes(60), "JWT");
        _now = _now.AddMinutes(10);
        Assert.Equal(1, store.PurgeExpired());
        Assert.False(store.IsAuthenticatedApiContextAvailable("dev", Fp));
        Assert.True(store.IsAuthenticatedApiContextAvailable("qa", Fp));
    }

    [Fact]
    public void InvalidateWipesImmediately()
    {
        using var store = Store();
        Sink(store).Store("dev", Fp, "api.example.test", _scope, Token, _now.AddMinutes(30), "JWT");
        store.Invalidate("dev");
        Assert.False(store.IsAuthenticatedApiContextAvailable("dev", Fp));
        Sink(store).Store("dev", Fp, "api.example.test", _scope, Token, _now.AddMinutes(30), "JWT");
        store.InvalidateAll();
        Assert.False(store.IsAuthenticatedApiContextAvailable("dev", Fp));
    }

    [Fact]
    public void ApplyOnlyToApprovedHttpsAuthoritiesOfTheBoundScope()
    {
        using var store = Store();
        Sink(store).Store("dev", Fp, "api.example.test", _scope, Token, _now.AddMinutes(30), "JWT");
        using var approved = new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/v1/me");
        Assert.True(Sink(store).TryApply("dev", Fp, approved));
        Assert.Equal("Bearer", approved.Headers.Authorization!.Scheme);
        Assert.Equal(Token, approved.Headers.Authorization.Parameter);
        foreach (var url in new[] { "https://other.example.test/v1/me", "http://api.example.test/v1/me", "https://api.example.test:8443/v1/me" })
        {
            using var rejected = new HttpRequestMessage(HttpMethod.Get, url);
            Assert.False(Sink(store).TryApply("dev", Fp, rejected));
            Assert.Null(rejected.Headers.Authorization);
        }
    }

    [Fact]
    public void MalformedCredentialsAreNeverStored()
    {
        using var store = Store();
        Assert.Throws<ArgumentException>(() => Sink(store).Store("dev", Fp, "api.example.test", _scope, "short", _now.AddMinutes(30), "Opaque"));
        Assert.Throws<ArgumentException>(() => Sink(store).Store("dev", Fp, "api.example.test", _scope, "has spaces " + Token, _now.AddMinutes(30), "Opaque"));
        Assert.Throws<ArgumentException>(() => Sink(store).Store("", Fp, "api.example.test", _scope, Token, _now.AddMinutes(30), "JWT"));
    }

    [Fact]
    public void CredentialIsNeverSerializedAndNoPublicMemberReturnsIt()
    {
        using var store = Store();
        Sink(store).Store("dev", Fp, "api.example.test", _scope, Token, _now.AddMinutes(30), "JWT");
        var descriptorJson = JsonSerializer.Serialize(store.Describe("dev", Fp));
        Assert.DoesNotContain(Token, descriptorJson);
        Assert.DoesNotContain("eyJ", descriptorJson);
        Assert.DoesNotContain(Token, store.ToString());

        // The read-only contract used by the rest of the application offers no way to extract the credential.
        var members = typeof(ITransientAuthenticatedApiContextStore).GetMembers().Select(m => m.Name);
        Assert.DoesNotContain(members, m => m.Contains("Token", StringComparison.OrdinalIgnoreCase) || m.Contains("Bearer", StringComparison.OrdinalIgnoreCase) || m.Contains("Reveal", StringComparison.OrdinalIgnoreCase));
        var publicStoreMembers = typeof(TransientAuthenticatedApiContextStore).GetMembers(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name);
        Assert.DoesNotContain(publicStoreMembers, m => m.Contains("Token", StringComparison.OrdinalIgnoreCase) || m.Contains("Bearer", StringComparison.OrdinalIgnoreCase) || m.Contains("Reveal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(AuthenticatedApiContextDescriptor).GetProperties().Select(p => p.Name), p => p.Contains("Token", StringComparison.OrdinalIgnoreCase) || p.Contains("Secret", StringComparison.OrdinalIgnoreCase));
    }
}
