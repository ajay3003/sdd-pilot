using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BirkNext.Web.PlaywrightTests.Fixtures;

/// <summary>
/// A local stand-in for Azure DevOps (http://127.0.0.1:&lt;port&gt;/&lt;organization&gt;/…; nothing external), for the browser walkthrough of Azure DevOps –
/// Test Plans and «Overfør testplan». The isolated BirkNext under test reaches it only through the development-only
/// <c>DeliveryReporting:AzureDevOps:DevelopmentBaseUrl</c> seam. It keeps projects, test plans, suites, test case work items, suite membership and
/// the last result per test case in memory, counts every write, and can refuse the next write to a path — so a walkthrough can prove what was
/// (and was not) written without touching a real organization.
/// </summary>
public sealed class AzureDevOpsStandIn : IAsyncDisposable
{
    public const string PortVariable = "BIRKNEXT_PLAYWRIGHT_ADO_PORT";
    public const string Organization = "verifyorg";
    public static int? ConfiguredPort => int.TryParse(Environment.GetEnvironmentVariable(PortVariable), out var p) ? p : null;

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _next = 80000;

    public readonly List<(int Id, string Name, int Root)> Plans = [];
    public readonly List<(int Id, string Name, int? Parent, int Plan)> Suites = [];
    public readonly Dictionary<int, (int Rev, string Title, string Steps)> Items = [];
    public readonly Dictionary<int, List<int>> SuiteCases = [];
    public readonly Dictionary<int, (string Outcome, int Run, int Result, DateTimeOffset At, string By)> LastResults = [];
    public readonly List<string> Writes = [];
    public (string Path, HttpStatusCode Status)? Refuse;

    public AzureDevOpsStandIn(int port) => _listener.Prefixes.Add($"http://127.0.0.1:{port}/");

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext c;
                try { c = await _listener.GetContextAsync(); } catch (Exception) when (_stop.IsCancellationRequested) { break; } catch (HttpListenerException) { break; }
                try { await HandleAsync(c); } catch (Exception) { try { c.Response.StatusCode = 500; c.Response.Close(); } catch (Exception) { } }
            }
        });
    }

    public int WriteCount { get { lock (Writes) return Writes.Count; } }

    private static async Task Send(HttpListenerContext c, object body, int status = 200)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        c.Response.StatusCode = status;
        c.Response.ContentType = "application/json";
        c.Response.ContentLength64 = bytes.Length;
        await c.Response.OutputStream.WriteAsync(bytes);
        c.Response.Close();
    }

    private async Task HandleAsync(HttpListenerContext c)
    {
        if (c.Request.Headers["Authorization"] is not { } auth || !auth.StartsWith("Basic ", StringComparison.Ordinal)) { await Send(c, new { }, 401); return; }
        var method = c.Request.HttpMethod;
        var path = c.Request.Url!.AbsolutePath;
        var body = c.Request.HasEntityBody ? await new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEndAsync() : null;
        var write = method is "POST" or "PATCH" && !path.EndsWith("/wiql", StringComparison.Ordinal);
        int? refused = null;
        lock (this)
        {
            if (write)
            {
                lock (Writes) Writes.Add($"{method} {path}");
                if (Refuse is { } r && path.Contains(r.Path, StringComparison.Ordinal)) { Refuse = null; refused = (int)r.Status; }
            }
        }
        if (refused is { } refusal) { await Send(c, new { message = "refused" }, refusal); return; }
        var (answer, status) = Handle(method, path, body);
        await Send(c, answer, status);
    }

    private (object Body, int Status) Handle(string method, string path, string? body)
    {
        lock (this)
        {
            var parts = path.Trim('/').Split('/');
            var org = $"/{Organization}";
            if (path == $"{org}/_apis/projects") return (new { value = new[] { new { id = "p1", name = "M2LB" }, new { id = "p2", name = "Testdokumentasjon" } } }, 200);
            if (path == $"{org}/M2LB/_apis/testplan/plans" && method == "GET")
                return (new { value = Plans.Select(p => new { id = p.Id, name = p.Name, state = "Active", rootSuite = new { id = p.Root } }) }, 200);
            if (path == $"{org}/Testdokumentasjon/_apis/testplan/plans") return (new { value = Array.Empty<object>() }, 200);
            if (path == $"{org}/M2LB/_apis/testplan/plans" && method == "POST")
            {
                var name = JsonNode.Parse(body!)!["name"]!.ToString();
                var (id, root) = (_next++, _next++);
                Plans.Add((id, name, root));
                Suites.Add((root, name, null, id));
                return (new { id, name, state = "Active", rootSuite = new { id = root } }, 200);
            }
            if (parts.Length == 7 && parts[4] == "Plans" && parts[6] == "suites")
            {
                var plan = int.Parse(parts[5]);
                if (method == "GET")
                    return (new { value = Suites.Where(s => s.Plan == plan).Select(s => new { id = s.Id, name = s.Name, suiteType = "staticTestSuite", parentSuite = s.Parent is { } p ? new { id = p } : null }) }, 200);
                var node = JsonNode.Parse(body!)!;
                var id = _next++;
                var parent = node["parentSuite"]!["id"]!.GetValue<int>();
                Suites.Add((id, node["name"]!.ToString(), parent, plan));
                return (new { id, name = node["name"]!.ToString(), suiteType = "staticTestSuite", parentSuite = new { id = parent } }, 200);
            }
            if (parts.Length == 9 && parts[6] == "Suites")
            {
                var suite = int.Parse(parts[7]);
                if (Suites.All(s => s.Id != suite)) return (new { }, 404);
                var list = SuiteCases.TryGetValue(suite, out var l) ? l : SuiteCases[suite] = [];
                if (parts[8] == "TestPoint")
                    return (new { value = list.Select((id, i) => new
                    {
                        id = 60000 + i, testCaseReference = new { id, name = Items.TryGetValue(id, out var w) ? w.Title : "" }, configuration = new { name = "Windows 11" },
                        results = LastResults.TryGetValue(id, out var r)
                            ? (object)new { outcome = r.Outcome, lastTestRunId = r.Run, lastResultId = r.Result, lastResultDetails = new { dateCompleted = r.At, runBy = new { displayName = r.By } } }
                            : new { outcome = "Unspecified" },
                    }) }, 200);
                if (method == "GET") return (new { value = list.Select(i => new { workItem = new { id = i, name = Items.TryGetValue(i, out var w) ? w.Title : "" } }) }, 200);
                foreach (var w in JsonNode.Parse(body!)!.AsArray()) { var id = w!["workItem"]!["id"]!.GetValue<int>(); if (!list.Contains(id)) list.Add(id); }
                return (new { value = Array.Empty<object>() }, 200);
            }
            if (path == $"{org}/M2LB/_apis/wit/workitems/$Test%20Case" && method == "POST")
            {
                var ops = JsonNode.Parse(body!)!.AsArray();
                string F(string field) => ops.FirstOrDefault(o => o!["path"]!.ToString() == "/fields/" + field)?["value"]?.ToString() ?? "";
                var id = _next++;
                Items[id] = (1, F("System.Title"), F("Microsoft.VSTS.TCM.Steps"));
                return (Item(id), 200);
            }
            if (parts.Length == 5 && parts[1] == "_apis" && parts[3] == "workitems")
            {
                var id = int.Parse(parts[4]);
                if (!Items.TryGetValue(id, out var w)) return (new { }, 404);
                if (method == "GET") return (Item(id), 200);
                var ops = JsonNode.Parse(body!)!.AsArray();
                if (ops.First(o => o!["op"]!.ToString() == "test")!["value"]!.GetValue<int>() != w.Rev) return (new { message = "rev" }, 412);
                string? F(string field) => ops.FirstOrDefault(o => o!["path"]!.ToString() == "/fields/" + field)?["value"]?.ToString();
                Items[id] = (w.Rev + 1, F("System.Title") ?? w.Title, F("Microsoft.VSTS.TCM.Steps") ?? w.Steps);
                return (Item(id), 200);
            }
            return (new { message = "not found" }, 404);
        }
    }

    private object Item(int id)
    {
        var w = Items[id];
        return new { id, rev = w.Rev, fields = new Dictionary<string, object> { ["System.Title"] = w.Title, ["System.WorkItemType"] = "Test Case", ["System.TeamProject"] = "M2LB" } };
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(2000));
    }
}
