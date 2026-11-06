using System.Net;
using System.Net.Http.Json;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Local session in the browser (docs/security/localhost-session-design-2026-11-06.md): backend requests carry the session cookie and the client
/// header, other origins never do; a session challenge asks this browser to connect; nothing is retried; the gate exchanges the start-window code.
/// </summary>
public sealed class LocalSessionTests : BunitContext
{
    private static readonly Uri Backend = new("http://localhost:5000/");

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Challenge()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ParseAdd(LocalSessionState.ChallengeScheme);
        return response;
    }

    private static bool IncludesCredentials(HttpRequestMessage request) =>
        request.Options.TryGetValue(new HttpRequestOptionsKey<IDictionary<string, object>>("WebAssemblyFetchOptions"), out var fetch)
        && fetch.TryGetValue("credentials", out var value) && value?.ToString() == "include";

    [Fact]
    public async Task Backend_requests_carry_the_session_and_client_header_other_origins_never_do()
    {
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(new LocalSessionHandler(new LocalSessionBackend(Backend), new LocalSessionState()) { InnerHandler = stub });

        await client.GetAsync(new Uri(Backend, "api/test-data/catalog"));
        await client.GetAsync("https://target.example.test/api/health");

        IncludesCredentials(stub.Requests[0]).Should().BeTrue();
        stub.Requests[0].Headers.Contains(LocalSessionState.ClientHeader).Should().BeTrue();
        IncludesCredentials(stub.Requests[1]).Should().BeFalse("a target application never receives the BirkNext session");
        stub.Requests[1].Headers.Contains(LocalSessionState.ClientHeader).Should().BeFalse("no custom header: it would force a preflight on the target");
    }

    [Fact]
    public async Task A_session_challenge_asks_to_connect_and_nothing_is_retried()
    {
        var state = new LocalSessionState();
        var stub = new Stub(_ => Challenge());
        using var client = new HttpClient(new LocalSessionHandler(new LocalSessionBackend(Backend), state) { InnerHandler = stub });
        var response = await client.PostAsJsonAsync(new Uri(Backend, "api/test-data/canonical-fixtures"), new { });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        state.ConnectRequired.Should().BeTrue();
        stub.Requests.Should().HaveCount(1, "a mutation is never replayed implicitly");

        var other = new LocalSessionState();
        using var plain = new HttpClient(new LocalSessionHandler(new LocalSessionBackend(Backend), other) { InnerHandler = new Stub(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)) });
        await plain.GetAsync(new Uri(Backend, "api/active-events/runs"));
        other.ConnectRequired.Should().BeFalse("a 401 from a permission policy is not a missing local session");
    }

    [Fact]
    public void The_gate_exchanges_the_code_shows_a_wrong_code_and_reloads_once_connected()
    {
        var connected = false;
        var stub = new Stub(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { connected }) }
            : request.Content!.ReadAsStringAsync().Result.ToUpperInvariant().Contains("ABCD-EFGH-JKLM")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { connected = connected = true }) }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = JsonContent.Create(new { message = "That code is not right. Use the code shown in the BirkNext start window." }) });
        Services.AddSingleton(new LocalSessionState());
        Services.AddSingleton(new LocalSessionApiService(new HttpClient(stub) { BaseAddress = Backend }));

        var gate = Render<LocalSessionGate>();
        gate.WaitForAssertion(() => gate.Find("[data-testid=local-session-gate]"));
        gate.Find("#lsg-code").GetAttribute("aria-describedby").Should().Be("lsg-help");
        gate.Find("[data-testid=local-session-code]").Input("WRONG-CODE");
        gate.Find("form").Submit();
        gate.WaitForAssertion(() => gate.Find("[data-testid=local-session-error]").TextContent.Should().Contain("not right"));

        gate.Find("[data-testid=local-session-code]").Input("abcd-efgh-jklm");
        gate.Find("form").Submit();
        gate.WaitForAssertion(() => gate.FindAll("[data-testid=local-session-gate]").Should().BeEmpty());
        connected.Should().BeTrue();
        Services.GetRequiredService<BunitNavigationManager>().History.Should().Contain(h => h.Options.ForceLoad, "the page reloads after connecting");
    }

    [Fact]
    public void Without_the_session_services_the_gate_renders_nothing()
    {
        Render<LocalSessionGate>().Markup.Trim().Should().BeEmpty();
    }
}
