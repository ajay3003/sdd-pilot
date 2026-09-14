using BirkNext.Api.Services.LocalHttpsProxy;

namespace BirkNext.Api.Tests.Services.LocalHttpsProxy;

/// <summary>Central redaction must strip credentials BEFORE anything reaches logs, status or evidence (tests N, O, P and the log-audit patterns).</summary>
public sealed class SensitiveDataRedactorTests
{
    private const string Jwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwiZXhwIjo5OTk5OTk5OTk5fQ.c2lnbmF0dXJlLXNpZ25hdHVyZS1zaWduYXR1cmU";

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("X-Api-Key")]
    [InlineData("x-functions-key")]
    public void SensitiveHeaderValuesAreMasked(string name)
    {
        Assert.True(SensitiveDataRedactor.IsSensitiveHeader(name));
        Assert.Equal(SensitiveDataRedactor.Mask, SensitiveDataRedactor.RedactHeaderValue(name, "Bearer " + Jwt));
        var line = SensitiveDataRedactor.RedactHeaderLine($"{name}: secret-value-{Jwt}");
        Assert.Equal($"{name}: {SensitiveDataRedactor.Mask}", line);
        Assert.DoesNotContain("eyJ", line);
    }

    [Fact]
    public void AuthorizationHeaderRedacted()
    {
        var text = SensitiveDataRedactor.RedactText($"GET /api HTTP/1.1\r\nHost: a\r\nAuthorization: Bearer {Jwt}\r\nAccept: */*");
        Assert.DoesNotContain(Jwt, text);
        Assert.DoesNotContain("eyJ", text);
        Assert.Contains("Authorization: [REDACTED]", text);
        Assert.Contains("Accept: */*", text);
    }

    [Fact]
    public void CookieAndSetCookieRedacted()
    {
        var text = SensitiveDataRedactor.RedactText("Cookie: session=abc123; other=1\r\nSet-Cookie: auth=xyz; HttpOnly");
        Assert.DoesNotContain("abc123", text);
        Assert.DoesNotContain("xyz", text);
        Assert.Contains("Cookie: [REDACTED]", text);
        Assert.Contains("Set-Cookie: [REDACTED]", text);
    }

    [Fact]
    public void BearerValuesJwtFragmentsAndTokenQueryParametersRedactedInFreeText()
    {
        var text = SensitiveDataRedactor.RedactText($"failed with bearer {Jwt} at https://h/cb?code=AQAB123&state=ok&access_token={Jwt} and fragment eyJraWQiOiIxIn0");
        Assert.DoesNotContain(Jwt, text);
        Assert.DoesNotContain("eyJ", text);
        Assert.DoesNotContain("AQAB123", text);
        Assert.Contains("state=ok", text);
        Assert.Contains("code=[REDACTED]", text);
        Assert.Contains("access_token=[REDACTED]", text);
    }

    [Fact]
    public void NonSensitiveHeadersPassThroughAndRedactHeadersKeepsOrder()
    {
        var redacted = SensitiveDataRedactor.RedactHeaders([new("Host", "api.test"), new("Authorization", "Bearer " + Jwt), new("Content-Type", "application/json"), new("Cookie", "a=b")]);
        Assert.Equal(["Host", "Authorization", "Content-Type", "Cookie"], redacted.Select(h => h.Key));
        Assert.Equal("api.test", redacted[0].Value);
        Assert.Equal(SensitiveDataRedactor.Mask, redacted[1].Value);
        Assert.Equal("application/json", redacted[2].Value);
        Assert.Equal(SensitiveDataRedactor.Mask, redacted[3].Value);
    }

    [Fact]
    public void EmptyAndNullAreSafe()
    {
        Assert.Equal("", SensitiveDataRedactor.RedactText(null));
        Assert.Equal("", SensitiveDataRedactor.RedactText(""));
        Assert.False(SensitiveDataRedactor.IsSensitiveHeader(null));
    }
}
