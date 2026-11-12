using System.Net;
using System.Net.Http.Json;
using BirkNext.Api.Controllers;
using BirkNext.Api.Services.LocalSecurity;
using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>
/// Automatic local access for the tester package (docs/hardening/birknext-tester-distribution-implementation.md): the launcher exchanges the launch
/// code for a one-time bootstrap token; the browser it opens exchanges that token once for the normal session cookie. Expectations come from the
/// security design: proof of possession, launcher-only, no browser origin, single use, short expiry, invalidated by rotation, never the code in a
/// response or URL.
/// </summary>
public sealed class LocalSessionBootstrapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-boot-" + Guid.NewGuid().ToString("N"));

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    [Fact]
    public void A_bootstrap_token_needs_the_code_works_once_and_expires()
    {
        var clock = new Clock();
        var sessions = new LocalSessionService(_dir, clock, NullLogger<LocalSessionService>.Instance);
        sessions.IssueBootstrap("WRONG-CODE-0000").Result.Should().Be(LocalSessionService.ExchangeResult.Rejected);

        var (issued, token) = sessions.IssueBootstrap(sessions.CurrentCode);
        issued.Should().Be(LocalSessionService.ExchangeResult.Accepted);
        token.Should().HaveLength(64).And.NotContain(LocalSessionService.Normalize(sessions.CurrentCode), "the token is not the code");

        var (first, session) = sessions.ExchangeBootstrap(token);
        first.Should().Be(LocalSessionService.ExchangeResult.Accepted);
        sessions.Validate(session).Should().BeTrue("it yields a normal session");
        sessions.ExchangeBootstrap(token).Result.Should().Be(LocalSessionService.ExchangeResult.Rejected, "single use");

        var (_, late) = sessions.IssueBootstrap(sessions.CurrentCode);
        clock.Now += LocalSessionService.BootstrapLifetime + TimeSpan.FromSeconds(1);
        sessions.ExchangeBootstrap(late).Result.Should().Be(LocalSessionService.ExchangeResult.Rejected, "expired after two minutes");
    }

    [Fact]
    public void Rotating_the_code_invalidates_unused_tokens_and_guessing_is_throttled()
    {
        var sessions = new LocalSessionService(_dir, new Clock(), NullLogger<LocalSessionService>.Instance);
        var (_, token) = sessions.IssueBootstrap(sessions.CurrentCode);
        sessions.Rotate();
        sessions.ExchangeBootstrap(token).Result.Should().Be(LocalSessionService.ExchangeResult.Rejected);
        for (var i = 0; i < 10; i++) sessions.ExchangeBootstrap(new string('a', 64));
        sessions.ExchangeBootstrap(new string('b', 64)).Result.Should().Be(LocalSessionService.ExchangeResult.Throttled);
    }

    [Fact]
    public async Task Only_the_launcher_without_a_browser_origin_gets_a_token_and_only_the_frontend_can_use_it()
    {
        using var host = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        var code = host.Services.GetRequiredService<LocalSessionService>().CurrentCode;
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        async Task<HttpResponseMessage> Bootstrap(string? origin, string? clientHeader, string body)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "api/local-session/bootstrap") { Content = JsonContent.Create(new { code = body }) };
            if (origin is not null) r.Headers.Add("Origin", origin);
            if (clientHeader is not null) r.Headers.Add(LocalSessionService.ClientHeader, clientHeader);
            return await client.SendAsync(r);
        }

        (await Bootstrap(LocalSessionTestClient.FrontendOrigin, LocalSessionController.LauncherClient, code)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "a browser always sends Origin");
        (await Bootstrap("https://evil.example", LocalSessionController.LauncherClient, code)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Bootstrap(null, "web", code)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "only the launcher client");
        (await Bootstrap(null, LocalSessionController.LauncherClient, "NOPE-NOPE-NOPE")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var ok = await Bootstrap(null, LocalSessionController.LauncherClient, code);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ok.Content.ReadAsStringAsync();
        body.Should().NotContain(code, "the code is never echoed");
        var token = (await ok.Content.ReadFromJsonAsync<LocalSessionController.BootstrapResponse>())!.Token;

        // The token is accepted only where the code is: by the BirkNext frontend (Origin + client header), once.
        var foreign = new HttpRequestMessage(HttpMethod.Post, "api/local-session") { Content = JsonContent.Create(new { bootstrapToken = token }) };
        foreign.Headers.Add("Origin", "https://evil.example"); foreign.Headers.Add(LocalSessionService.ClientHeader, "web");
        (await client.SendAsync(foreign)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        HttpRequestMessage Exchange()
        {
            var r = new HttpRequestMessage(HttpMethod.Post, "api/local-session") { Content = JsonContent.Create(new { bootstrapToken = token }) };
            r.Headers.Add("Origin", LocalSessionTestClient.FrontendOrigin); r.Headers.Add(LocalSessionService.ClientHeader, "web");
            return r;
        }
        using var exchanged = await client.SendAsync(Exchange());
        exchanged.StatusCode.Should().Be(HttpStatusCode.OK);
        exchanged.Headers.GetValues("Set-Cookie").Single().Should().Contain("httponly").And.Contain("samesite=strict");
        (await client.SendAsync(Exchange())).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a used token never works twice");
    }
}
