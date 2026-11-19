using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BirkNext.Web.PlaywrightTests.Fixtures;

/// <summary>
/// Local stand-in REST API for API Quality Review (http://localhost:&lt;free port&gt;; nothing external). It publishes its OpenAPI contract at
/// /openapi.json and serves /api/children and /health. <see cref="Broken"/> = true gives deliberate, recognisable defects; false fixes them:
/// <list type="bullet">
/// <item>/api/children: a number where the contract says string and a required property missing; a 2 s delay; no X-Content-Type-Options and
/// no Cache-Control (fixed: the documented shape, fast, the security headers present)</item>
/// <item>/health: 500 (fixed: 200)</item>
/// <item>an unknown route: 500 with a .NET stack trace (fixed: 404 with a plain JSON error)</item>
/// <item>CORS preflight: any origin allowed with credentials (fixed: no CORS for foreign origins)</item>
/// </list>
/// The contract itself documents no security scheme in both modes. Response bodies never contain the stand-in's own URL.
/// </summary>
public sealed class ApiStandIn : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public volatile bool Broken = true;
    public string Url { get; }

    public ApiStandIn()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://localhost:{port}";
        _listener.Prefixes.Add(Url + "/");
    }

    private const string Contract = """
        { "openapi": "3.0.3", "info": { "title": "Children API (stand-in)", "version": "1.0" },
          "paths": {
            "/api/children": { "get": { "operationId": "listChildren", "responses": {
              "200": { "description": "ok", "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Child" } } } } },
              "500": { "description": "error" } } } },
            "/health": { "get": { "operationId": "health", "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Health" } } } } } } }
          },
          "components": { "schemas": {
            "Child": { "type": "object", "required": ["id", "name"], "properties": { "id": { "type": "string" }, "name": { "type": "string" } } },
            "Health": { "type": "object", "required": ["status"], "properties": { "status": { "type": "string" } } } } } }
        """;

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) when (_stop.IsCancellationRequested) { return; }
                catch (HttpListenerException) { return; }
                _ = Task.Run(() => HandleAsync(context));
            }
        });
    }

    private async Task HandleAsync(HttpListenerContext c)
    {
        var path = c.Request.Url!.AbsolutePath;
        var broken = Broken;
        var r = c.Response;
        if (!broken)
        {
            r.Headers["X-Content-Type-Options"] = "nosniff";
            r.Headers["Cache-Control"] = "no-store";
            r.Headers["Referrer-Policy"] = "no-referrer";
            r.Headers["Content-Security-Policy"] = "default-src 'none'";
            r.Headers["Permissions-Policy"] = "geolocation=()";
        }
        if (c.Request.HttpMethod == "OPTIONS")
        {
            if (broken && c.Request.Headers["Origin"] is { } origin)
            {
                r.Headers["Access-Control-Allow-Origin"] = "*";
                r.Headers["Access-Control-Allow-Credentials"] = "true";
                r.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE";
                r.Headers["Access-Control-Allow-Headers"] = "*";
                _ = origin;
            }
            r.StatusCode = 204;
            r.Close();
            return;
        }
        if (broken && c.Request.Headers["Origin"] is not null)
        {
            r.Headers["Access-Control-Allow-Origin"] = "*";
            r.Headers["Access-Control-Allow-Credentials"] = "true";
        }
        var (status, body) = path switch
        {
            "/openapi.json" => (200, Contract),
            "/api/children" when broken => (200, """[{ "id": 1001 }, { "id": 1002 }]"""),
            "/api/children" => (200, """[{ "id": "c-1001", "name": "Synthetic child A" }, { "id": "c-1002", "name": "Synthetic child B" }]"""),
            "/health" when broken => (500, """{ "status": "Unhealthy" }"""),
            "/health" => (200, """{ "status": "Healthy" }"""),
            _ when broken => (500, "System.InvalidOperationException: Sequence contains no elements\n   at Children.Api.ChildEndpoints.Get(String id) in C:\\src\\Children.Api\\ChildEndpoints.cs:line 42\n   at Microsoft.AspNetCore.Routing.EndpointMiddleware.Invoke(HttpContext context)"),
            _ => (404, """{ "error": "Not found" }"""),
        };
        if (broken && path == "/api/children") await Task.Delay(2000);
        r.StatusCode = status;
        r.ContentType = body.StartsWith('{') || body.StartsWith('[') ? "application/json" : "text/plain";
        var bytes = Encoding.UTF8.GetBytes(body);
        r.ContentLength64 = bytes.Length;
        await r.OutputStream.WriteAsync(bytes);
        r.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(2000));
    }
}
