using System.Net;
using System.Net.Http.Json;
using BirkNext.Api.Services.LocalSecurity;
using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>
/// Audit 2026-11-15 SEC-01: BirkNext has no project-level authorization, so its only supported deployment is one person on one computer. These
/// tests pin the fail-closed boundary that keeps a shared multi-user deployment from running silently: non-loopback listeners never start (the
/// former opt-out included), a request from another computer is refused whatever it presents (session cookie, bearer token, a forged Host), and a
/// request relayed by a proxy — the other way to share one instance — is refused too. Loopback use is unchanged.
/// </summary>
public sealed class LocalDeploymentBoundaryTests
{
    // A read that starts no external process (2026-11-24): the earlier "/api/performance-tests/providers" ran the real podman CLI (version, info,
    // image inspect; 30 s each) and timed out the 100 s test client whenever a full suite kept podman busy. The boundary rules are the same for any API route.
    private const string AnyApiRead = "/api/system/build-info";

    private static WebApplicationFactory<Program> Host(IPAddress? peer = null) =>
        peer is null ? TestHostConfiguration.CreateDefaultHostWithEnginesDisabled()
            : TestHostConfiguration.CreateDefaultHostWithEnginesDisabled().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new Peer(peer))));

    /// <summary>Sets the socket peer like Kestrel would (TestServer leaves it empty). Not a header: nothing in the request can change it.</summary>
    private sealed class Peer(IPAddress remote) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = remote;
                context.Connection.LocalIpAddress = IPAddress.Loopback;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000")]
    [InlineData("http://192.168.1.20:5000")]
    [InlineData("http://*:5000")]
    public void A_shared_listener_does_not_start_even_with_the_former_opt_out(string urls)
    {
        using var shared = Host().WithWebHostBuilder(b => { b.UseSetting("urls", urls); b.UseSetting(LoopbackBinding.AllowSetting, "true"); });
        shared.Invoking(f => f.CreateClient()).Should().Throw<InvalidOperationException>()
            .WithMessage("*project-level authorization*", "a shared deployment would let every signed-in caller read and change every project");
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.5")]
    [InlineData("203.0.113.7")]
    [InlineData("::ffff:192.168.1.20")]
    public async Task A_request_from_another_computer_is_refused_before_authentication(string remote)
    {
        await using var factory = Host(IPAddress.Parse(remote));
        using var client = factory.CreateClient();
        var anonymous = await client.GetAsync(AnyApiRead);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await anonymous.Content.ReadAsStringAsync()).Should().Contain("this computer");

        var bearer = new HttpRequestMessage(HttpMethod.Get, AnyApiRead);
        bearer.Headers.TryAddWithoutValidation("Authorization", "Bearer any.token.value");
        (await client.SendAsync(bearer)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "a token from another computer is not a way in either");

        var bootstrap = new HttpRequestMessage(HttpMethod.Post, "/api/local-session") { Content = JsonContent.Create(new { code = factory.Services.GetRequiredService<LocalSessionService>().CurrentCode }) };
        bootstrap.Headers.Add("Origin", LocalSessionTestClient.FrontendOrigin);
        bootstrap.Headers.Add(LocalSessionService.ClientHeader, "tests");
        (await client.SendAsync(bootstrap)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "even the right launch code does not open a session for another computer");
    }

    [Theory]
    [InlineData("X-Forwarded-For", "127.0.0.1")]
    [InlineData("X-Forwarded-For", "192.168.1.20")]
    [InlineData("Forwarded", "for=192.168.1.20;host=birknext.example")]
    [InlineData("X-Forwarded-Host", "birknext.example")]
    public async Task A_request_relayed_by_a_proxy_is_refused_even_from_loopback(string header, string value)
    {
        await using var factory = Host(IPAddress.Loopback);
        using var client = factory.CreateLocalSessionClient();
        (await client.GetAsync(AnyApiRead)).StatusCode.Should().Be(HttpStatusCode.OK, "the same session works directly");
        var relayed = new HttpRequestMessage(HttpMethod.Get, AnyApiRead);
        relayed.Headers.TryAddWithoutValidation(header, value);
        var response = await client.SendAsync(relayed);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a proxy in front of BirkNext would turn one person's instance into a shared one");
        (await response.Content.ReadAsStringAsync()).Should().Contain("proxy");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task Local_use_is_unchanged(string remote)
    {
        await using var factory = Host(IPAddress.Parse(remote));
        using var client = factory.CreateLocalSessionClient();
        (await client.GetAsync(AnyApiRead)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_forged_localhost_host_header_from_another_computer_does_not_help()
    {
        await using var factory = Host(IPAddress.Parse("192.168.1.20"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        (await client.GetAsync(AnyApiRead)).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the Host header is chosen by the caller; the socket peer is not");
    }

    [Fact]
    public void The_peer_rule_accepts_only_loopback_addresses()
    {
        LocalRequestGuardMiddleware.IsLocalPeer(IPAddress.Loopback).Should().BeTrue();
        LocalRequestGuardMiddleware.IsLocalPeer(IPAddress.IPv6Loopback).Should().BeTrue();
        LocalRequestGuardMiddleware.IsLocalPeer(IPAddress.Parse("::ffff:127.0.0.1")).Should().BeTrue();
        LocalRequestGuardMiddleware.IsLocalPeer(IPAddress.Parse("192.168.1.20")).Should().BeFalse();
        LocalRequestGuardMiddleware.IsLocalPeer(IPAddress.Any).Should().BeFalse();
        LocalRequestGuardMiddleware.IsLocalPeer(null).Should().BeTrue("no IP peer = an in-process host or a local pipe, never another computer");
    }
}
