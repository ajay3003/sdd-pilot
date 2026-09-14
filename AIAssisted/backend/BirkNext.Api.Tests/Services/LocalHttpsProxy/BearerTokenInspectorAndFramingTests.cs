using System.Text;
using BirkNext.Api.Services.LocalHttpsProxy;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

public sealed class BearerTokenInspectorAndFramingTests
{
    [Fact]
    public void JwtMetadataIsDecodedWithoutExposingClaims()
    {
        var tenant = Guid.NewGuid();
        var exp = DateTimeOffset.UtcNow.AddMinutes(45).ToUnixTimeSeconds();
        var token = BearerTokenInspector.BuildUnsignedJwt(new { exp, iat = exp - 3600, iss = $"https://login.microsoftonline.com/{tenant}/v2.0", aud = "api://m2lb-dev", sub = "user", name = "Sensitive Person" });
        Assert.True(BearerTokenInspector.LooksLikeBearerToken(token));
        var metadata = BearerTokenInspector.Inspect(token);
        Assert.True(metadata.IsJwt);
        Assert.Equal("JWT", metadata.Format);
        Assert.Equal(exp, metadata.ExpiresAt!.Value.ToUnixTimeSeconds());
        Assert.Equal("login.microsoftonline.com", metadata.IssuerHost);
        Assert.Equal(tenant.ToString("D"), metadata.TenantId);
        Assert.True(metadata.HasAudience);
        Assert.DoesNotContain("Sensitive", metadata.ToString());
        Assert.DoesNotContain("user", metadata.ToString());
    }

    [Fact]
    public void OpaqueAndMalformedTokensAreHandledSafely()
    {
        var opaque = new string('a', 64);
        Assert.True(BearerTokenInspector.LooksLikeBearerToken(opaque));
        var metadata = BearerTokenInspector.Inspect(opaque);
        Assert.False(metadata.IsJwt);
        Assert.Equal("Opaque", metadata.Format);
        Assert.Null(metadata.ExpiresAt);
        Assert.False(BearerTokenInspector.Inspect("eyJ.notjson.x").IsJwt);
        Assert.False(BearerTokenInspector.LooksLikeBearerToken("too short"));
        Assert.False(BearerTokenInspector.LooksLikeBearerToken("has whitespace " + opaque));
        Assert.False(BearerTokenInspector.LooksLikeBearerToken(null));
    }

    [Fact]
    public void ResponseFramingFollowsHttpRules()
    {
        Assert.True(HttpHead.TryParse(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 10\r\n\r\n"), out var noContent));
        Assert.Equal(BodyKind.None, BodyFraming.ForResponse(noContent, "GET").Kind);
        Assert.True(HttpHead.TryParse(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\n"), out var ok));
        Assert.Equal(BodyKind.None, BodyFraming.ForResponse(ok, "HEAD").Kind);
        Assert.Equal(new BodyFraming(BodyKind.ContentLength, 10), BodyFraming.ForResponse(ok, "GET"));
        Assert.True(HttpHead.TryParse(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 10\r\n\r\n"), out var chunked));
        Assert.Equal(BodyKind.Chunked, BodyFraming.ForResponse(chunked, "GET").Kind);
        Assert.True(HttpHead.TryParse(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n"), out var close));
        Assert.Equal(BodyKind.UntilClose, BodyFraming.ForResponse(close, "GET").Kind);
        Assert.True(close.ConnectionClose);
        Assert.True(HttpHead.TryParse(Encoding.ASCII.GetBytes("GET /x HTTP/1.1\r\nHost: a\r\nAuthorization: Bearer abc\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n\r\n"), out var request));
        Assert.True(request.IsRequest);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/x", request.Target);
        Assert.True(request.IsUpgrade);
        Assert.Equal("Bearer abc", request.Header("authorization"));
        Assert.Equal(BodyKind.None, BodyFraming.ForRequest(request).Kind);
    }

    [Fact]
    public async Task BufferedReaderSplitsHeadsAndCopiesChunkedBodiesVerbatim()
    {
        var chunked = "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5;ext=1\r\nhello\r\n6\r\n world\r\n0\r\nTrailer: x\r\n\r\nGET /next HTTP/1.1\r\n\r\n";
        using var source = new MemoryStream(Encoding.ASCII.GetBytes(chunked));
        using var reader = new BufferedNetworkReader(source, 8);
        var head = await reader.ReadHeadAsync(4096, CancellationToken.None);
        Assert.True(HttpHead.TryParse(head!, out var response));
        Assert.True(response.IsChunked);
        using var body = new MemoryStream();
        await reader.CopyChunkedAsync(body, CancellationToken.None);
        Assert.Equal("5;ext=1\r\nhello\r\n6\r\n world\r\n0\r\nTrailer: x\r\n\r\n", Encoding.ASCII.GetString(body.ToArray()));
        var next = await reader.ReadHeadAsync(4096, CancellationToken.None);
        Assert.Equal("GET /next HTTP/1.1\r\n\r\n", Encoding.ASCII.GetString(next!));
        Assert.Null(await reader.ReadHeadAsync(4096, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedHeadIsRejected()
    {
        using var source = new MemoryStream(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nX: " + new string('a', 5000) + "\r\n\r\n"));
        using var reader = new BufferedNetworkReader(source, 64);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadHeadAsync(1024, CancellationToken.None));
    }
}
