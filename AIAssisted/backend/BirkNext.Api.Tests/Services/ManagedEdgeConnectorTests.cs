using System.Net;
using System.Net.Sockets;
using System.Text;
using BirkNext.Api.Services.ManagedEdge;
using BirkNext.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace BirkNext.Api.Tests.Services;

public sealed class ManagedEdgeConnectorTests
{
    [Theory]
    [InlineData("http://192.168.1.2:9222/json/version")]
    [InlineData("http://127.0.0.1:9223/json/version")]
    public async Task DiscoveryRedirectsAreNeverFollowed(string location)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var response = Serve(listener, $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try { await Assert.ThrowsAsync<HttpRequestException>(() => new ManagedEdgeConnector().ConnectAsync($"http://127.0.0.1:{port}", default)); await response; }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData("ws://192.168.1.2:9222/devtools/browser/id")]
    [InlineData("ws://example.com:9222/devtools/browser/id")]
    [InlineData("ws://127.0.0.1:1/devtools/browser/id")]
    public async Task AdvertisedWebSocketMustRemainAtValidatedEndpoint(string socket)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var body = "{\"webSocketDebuggerUrl\":\"" + socket + "\"}";
        var response = Serve(listener, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
        try { await Assert.ThrowsAsync<ArgumentException>(() => new ManagedEdgeConnector().ConnectAsync($"http://127.0.0.1:{port}", default)); await response; }
        finally { listener.Stop(); }
    }

    private static async Task Serve(TcpListener listener, string response)
    {
        using var peer = await listener.AcceptTcpClientAsync();
        var stream = peer.GetStream();
        await stream.ReadAsync(new byte[8192]);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost", "http://localhost:5173", true)]
    [InlineData("192.168.1.2", "localhost", "http://localhost:5173", false)]
    [InlineData("127.0.0.1", "evil.test", "http://localhost:5173", false)]
    [InlineData("127.0.0.1", "localhost", "https://evil.test", false)]
    [InlineData("127.0.0.1", "localhost", "", false)]
    public void OnlyLocalFrontendMayUseBridge(string peer, string host, string origin, bool allowed)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        http.Request.Host = new HostString(host);
        http.Request.Headers.Origin = origin;
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), new object());
        new ManagedEdgeLocalCallerFilter(new ConfigurationBuilder().Build()).OnActionExecuting(context);
        Assert.Equal(allowed, context.Result is null);
    }
}
