using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BirkNext.Web.PlaywrightTests.Fixtures;

/// <summary>
/// Local stand-in API for Contract Testing (http://localhost:&lt;free port&gt;; nothing external) that speaks both protocols:
/// <list type="bullet">
/// <item>REST: its OpenAPI contract at /openapi.json (v1) and /openapi-v2.json (v2: the required <c>name</c> is renamed to
/// <c>fullName</c> — a breaking change), GET /api/children and GET /api/children/{id}</item>
/// <item>GraphQL: its schema at /schema.graphql and the endpoint POST /graphql (<c>child(id)</c>, <c>children</c>)</item>
/// </list>
/// <see cref="Broken"/> = true answers against the contract: REST gives <c>id</c> as a number and leaves out the required <c>name</c>;
/// GraphQL gives <c>name: null</c> for a non-null field and <c>age</c> as a string. False answers exactly as documented. Any id is found.
/// Every request is recorded (method + path) so a test can prove what was sent.
/// </summary>
public sealed class ContractStandIn : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public volatile bool Broken;
    public string Url { get; }
    public List<string> Requests { get; } = [];

    public ContractStandIn()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://localhost:{port}";
        _listener.Prefixes.Add(Url + "/");
    }

    private static string Contract(bool v2) => $$"""
        { "openapi": "3.0.3", "info": { "title": "Children REST stand-in", "version": "{{(v2 ? "2.0" : "1.0")}}" },
          "paths": {
            "/api/children": { "get": { "operationId": "listChildren", "responses": {
              "200": { "description": "ok", "content": { "application/json": { "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Child" } } } } } } } },
            "/api/children/{id}": { "get": { "operationId": "getChild",
              "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ],
              "responses": {
                "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Child" } } } },
                "404": { "description": "not found" } } } }
          },
          "components": { "schemas": {
            "Child": { "type": "object", "required": ["id", "{{(v2 ? "fullName" : "name")}}"], "properties": {
              "id": { "type": "string" }, "{{(v2 ? "fullName" : "name")}}": { "type": "string" }, "age": { "type": "integer" } } } } } }
        """;

    public const string Schema = """
        type Query {
          child(id: ID!): Child
          children: [Child!]!
        }

        type Child {
          id: ID!
          name: String!
          age: Int
        }
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
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(() => HandleAsync(context));
            }
        });
    }

    private string RestChild(string id) => Broken ? $$"""{ "id": 4711, "age": 9 }""" : $$"""{ "id": "{{id}}", "name": "Synthetic child", "age": 9 }""";

    private string GraphQlChild(string id) => Broken ? $$"""{ "id": "{{id}}", "name": null, "age": "nine" }""" : $$"""{ "id": "{{id}}", "name": "Synthetic child", "age": 9 }""";

    private async Task HandleAsync(HttpListenerContext c)
    {
        var path = c.Request.Url!.AbsolutePath;
        lock (Requests) Requests.Add($"{c.Request.HttpMethod} {path}");
        var (status, type, body) = (200, "application/json", "");
        try
        {
            switch (c.Request.HttpMethod, path)
            {
                case ("GET", "/openapi.json"): body = Contract(false); break;
                case ("GET", "/openapi-v2.json"): body = Contract(true); break;
                case ("GET", "/schema.graphql"): (type, body) = ("text/plain", Schema); break;
                case ("GET", "/api/children"): body = $"[{RestChild("c-1")}]"; break;
                case ("GET", _) when path.StartsWith("/api/children/", StringComparison.Ordinal): body = RestChild(Uri.UnescapeDataString(path["/api/children/".Length..])); break;
                case ("POST", "/graphql"):
                    using (var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8))
                    using (var doc = JsonDocument.Parse(await reader.ReadToEndAsync()))
                    {
                        var query = doc.RootElement.GetProperty("query").GetString() ?? "";
                        var variables = doc.RootElement.TryGetProperty("variables", out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
                        if (query.Contains("child(", StringComparison.Ordinal) || query.Contains("child {", StringComparison.Ordinal))
                        {
                            // Like a real server: the required argument missing (or null) is a validation error with no data.
                            var id = variables.ValueKind == JsonValueKind.Object && variables.TryGetProperty("id", out var idValue) && idValue.ValueKind is JsonValueKind.String or JsonValueKind.Number
                                ? idValue.ToString() : null;
                            body = id is null || !query.Contains("child(", StringComparison.Ordinal)
                                ? """{ "errors": [ { "message": "The argument `id` is required.", "extensions": { "code": "HC0018" } } ] }"""
                                : $$"""{ "data": { "child": {{GraphQlChild(id)}} } }""";
                        }
                        else if (query.Contains("children", StringComparison.Ordinal)) body = $$"""{ "data": { "children": [{{GraphQlChild("c-1")}}] } }""";
                        else (status, body) = (400, """{ "errors": [ { "message": "Unknown field", "extensions": { "code": "HC0020" } } ] }""");
                    }
                    break;
                default: (status, body) = (404, """{ "error": "not found" }"""); break;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            (status, body) = (400, """{ "errors": [ { "message": "bad request" } ] }""");
        }
        try
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            c.Response.StatusCode = status;
            c.Response.ContentType = type;
            c.Response.ContentLength64 = bytes.Length;
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
        catch (Exception) { /* the client went away */ }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
        if (_loop is not null) { try { await _loop; } catch (Exception) { } }
        _stop.Dispose();
    }
}
