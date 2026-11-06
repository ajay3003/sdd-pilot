using System.Net;
using System.Net.Http.Json;
using BirkNext.Api.Services.LocalSecurity;
using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>
/// Localhost security (docs/security/localhost-session-design-2026-11-06.md): loopback-only binding, REST and GraphQL closed to anonymous callers,
/// the launch-code bootstrap (proof of possession, throttled, frontend-only), session revocation, CSRF and Host checks, extension origins confined,
/// and no secret in a response.
/// </summary>
public sealed class LocalSessionSecurityTests
{
    private const string AnyApiRead = "/api/performance-tests/providers";

    private static WebApplicationFactory<Program> Host() => TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();

    private static HttpRequestMessage Post(string url, object body, string? origin = LocalSessionTestClient.FrontendOrigin, bool clientHeader = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (clientHeader) request.Headers.Add(LocalSessionService.ClientHeader, "tests");
        return request;
    }

    // ── Binding ─────────────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://localhost:5000", true)]
    [InlineData("http://127.0.0.1:5000;http://[::1]:5001", true)]
    [InlineData("http://0.0.0.0:5000", false)]
    [InlineData("http://*:5000", false)]
    [InlineData("http://+:5000", false)]
    [InlineData("http://[::]:5000", false)]
    [InlineData("http://192.168.1.20:5000", false)]
    [InlineData("http://my-pc:5000", false)]
    public void Only_loopback_urls_are_accepted(string urls, bool loopback) =>
        LoopbackBinding.Violations(urls).Should().HaveCount(loopback ? 0 : 1);

    [Fact]
    public void The_api_refuses_to_start_on_a_lan_address_unless_explicitly_allowed()
    {
        using var widened = Host().WithWebHostBuilder(b => b.UseSetting("urls", "http://0.0.0.0:5000"));
        var start = () => widened.CreateClient();
        start.Should().Throw<InvalidOperationException>().WithMessage("*listens on localhost only*0.0.0.0*");

        using var allowed = Host().WithWebHostBuilder(b => { b.UseSetting("urls", "http://0.0.0.0:5000"); b.UseSetting(LoopbackBinding.AllowSetting, "true"); });
        allowed.Invoking(f => f.CreateClient()).Should().NotThrow("a deliberate non-local deployment is an explicit choice");
    }

    // ── Anonymous callers ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Anonymous_rest_reads_and_mutations_and_graphql_are_rejected()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var read = await client.GetAsync(AnyApiRead);
        read.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        read.Headers.WwwAuthenticate.ToString().Should().Be(LocalSessionService.ChallengeScheme);
        (await client.SendAsync(Post("/api/test-data/canonical-fixtures", new { }))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Post("/graphql", new { query = "mutation { __typename }" }))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Post("/graphql", new { query = "{ __typename }" }))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "reads are sensitive too");
    }

    // ── Bootstrap ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_valid_code_from_the_frontend_gives_an_httponly_strict_cookie_and_never_echoes_a_secret()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var code = factory.Services.GetRequiredService<LocalSessionService>().CurrentCode;
        (await client.GetFromJsonAsync<LocalSessionStatus>("/api/local-session"))!.Connected.Should().BeFalse();

        var response = await client.SendAsync(Post("/api/local-session", new { code }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        cookie.Should().StartWith(LocalSessionService.CookieName + "=").And.Contain("httponly").And.Contain("samesite=strict");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(code).And.NotContain(cookie.Split('=')[1].Split(';')[0], "the session token travels only in the HttpOnly cookie");

        (await client.GetFromJsonAsync<LocalSessionStatus>("/api/local-session"))!.Connected.Should().BeTrue();
        (await client.GetAsync(AnyApiRead)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record LocalSessionStatus(bool Connected);

    [Fact]
    public async Task The_bootstrap_cannot_be_obtained_by_another_web_origin_a_wrong_code_or_repeated_guessing()
    {
        await using var factory = Host();
        using var client = factory.CreateClient();
        var code = factory.Services.GetRequiredService<LocalSessionService>().CurrentCode;

        var foreign = await client.SendAsync(Post("/api/local-session", new { code }, origin: "http://localhost:8080"));
        foreign.StatusCode.Should().Be(HttpStatusCode.Forbidden, "another local web app (same-site) never gets a session, even with the code");
        foreign.Headers.Contains("Set-Cookie").Should().BeFalse();
        (await client.SendAsync(Post("/api/local-session", new { code }, origin: "https://evil.example"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Post("/api/local-session", new { code }, clientHeader: false))).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        for (var i = 0; i < 10; i++)
            (await client.SendAsync(Post("/api/local-session", new { code = "AAAA-AAAA-AAAA" }))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.SendAsync(Post("/api/local-session", new { code }))).StatusCode.Should().Be((HttpStatusCode)429, "guessing is throttled — even the right code waits");
        (await client.GetAsync(AnyApiRead)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_revoked_or_rotated_session_is_rejected()
    {
        await using var factory = Host();
        using var first = factory.CreateLocalSessionClient();
        (await first.GetAsync(AnyApiRead)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.DeleteAsync("/api/local-session")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await first.GetAsync(AnyApiRead)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "disconnected");

        var service = factory.Services.GetRequiredService<LocalSessionService>();
        var oldCode = service.CurrentCode;
        using var second = factory.CreateLocalSessionClient();
        using var third = factory.CreateLocalSessionClient();
        (await second.PostAsync("/api/local-session/rotate", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        service.CurrentCode.Should().NotBe(oldCode);
        (await third.GetAsync(AnyApiRead)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "rotating the code revokes every session");
        File.ReadAllText(service.CodePath).Should().Be(service.CurrentCode, "the launcher reads the new code from the same file");
    }

    // ── CSRF, Host, extension origins ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cross_origin_or_unmarked_state_changes_with_a_valid_cookie_are_rejected()
    {
        await using var factory = Host();
        using var client = factory.CreateLocalSessionClient(withOrigin: false);
        (await client.SendAsync(Post("/api/test-data/canonical-fixtures", new { }, origin: "http://localhost:8080"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.SendAsync(Post("/api/test-data/canonical-fixtures", new { }, origin: null))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "no Origin, no state change");
        client.DefaultRequestHeaders.Remove(LocalSessionService.ClientHeader);
        using var unmarked = new HttpRequestMessage(HttpMethod.Post, "/graphql") { Content = JsonContent.Create(new { query = "{ __typename }" }) };
        unmarked.Headers.Add("Origin", LocalSessionTestClient.FrontendOrigin);
        (await client.SendAsync(unmarked)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "GraphQL needs the client header (forces a CORS preflight)");
        (await client.SendAsync(Post("/graphql", new { query = "{ __typename }" }))).StatusCode.Should().NotBe(HttpStatusCode.Forbidden).And.NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Requests_addressed_to_another_host_name_are_rejected()
    {
        await using var factory = Host();
        using var client = factory.CreateLocalSessionClient();
        using var rebound = new HttpRequestMessage(HttpMethod.Get, AnyApiRead);
        rebound.Headers.Host = "attacker.example";
        (await client.SendAsync(rebound)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "DNS rebinding: the Host must be a loopback name");
        using var loopbackIp = new HttpRequestMessage(HttpMethod.Get, AnyApiRead);
        loopbackIp.Headers.Host = "127.0.0.1:5000";
        (await client.SendAsync(loopbackIp)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Extension_origins_are_confined_to_the_companion_extension_routes()
    {
        await using var factory = Host();
        using var client = factory.CreateLocalSessionClient(withOrigin: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, AnyApiRead);
        request.Headers.Add("Origin", "chrome-extension://abcdefghijklmnopabcdefghijklmnop");
        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // The extension routes stay reachable without a browser session; their own caller filter (loopback peer + origin) decides.
        var companion = await client.SendAsync(Post("/api/browser-companion/extension/validate", new { }, origin: "chrome-extension://abcdefghijklmnopabcdefghijklmnop"));
        (await companion.Content.ReadAsStringAsync()).Should().NotContain("Connect this browser").And.NotContain("Extension origins may only");
    }
}
