using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BirkNext.Web.PlaywrightTests.Fixtures;

/// <summary>
/// Local stand-in for the whole Skolenærvær chain on one origin — Altinn → MU → MM → SkoleService → Person → period lock → Bufdata — with its
/// own ledger (the oracle). A port of the backend's CompositeSuiteTests.SyntheticIntegration: each hop becomes visible 600 ms after the
/// previous one (404 before), like asynchronous message handling. <see cref="Mode"/>: "normal", "mm-reject" (MM never sees the message) or
/// "wrong-correlation" (MM answers with another message id). Listens on http://localhost:&lt;free port&gt;/ only; nothing external is involved.
/// </summary>
public sealed class SkolenaervaerStandIn : IAsyncDisposable
{
    public sealed class Transaction
    {
        public string SubmissionId = "", MessageId = "", ReportId = "", Period = "", DeliveryId = "";
        public DateTimeOffset Submitted;
        public List<string> Transitions = [];
    }

    private const int HopMs = 600;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public volatile string Mode = "normal";
    public ConcurrentQueue<(string Method, string Path)> Ledger { get; } = new();
    public ConcurrentBag<Transaction> Transactions { get; } = [];
    public string Url { get; }

    public SkolenaervaerStandIn()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://localhost:{port}";
        _listener.Prefixes.Add(Url + "/");
    }

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

    public void Reset(string mode)
    {
        Mode = mode;
        Ledger.Clear();
        Transactions.Clear();
    }

    private async Task HandleAsync(HttpListenerContext c)
    {
        var path = c.Request.Url!.AbsolutePath;
        Ledger.Enqueue((c.Request.HttpMethod, path));
        var now = DateTimeOffset.UtcNow;
        object? body = null; var status = 404;
        var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Transaction? Find(Func<Transaction, bool> f) => Transactions.FirstOrDefault(f);
        bool Ready(Transaction t, int hops) => now - t.Submitted >= TimeSpan.FromMilliseconds(HopMs * hops);
        void Mark(Transaction t, string transition) { lock (t.Transitions) if (!t.Transitions.Contains(transition)) t.Transitions.Add(transition); }
        if (c.Request.HttpMethod == "POST" && path == "/altinn/submissions")
        {
            var tx = new Transaction { SubmissionId = Guid.NewGuid().ToString(), MessageId = "msg-" + Guid.NewGuid().ToString("N")[..12], ReportId = "rep-" + Guid.NewGuid().ToString("N")[..12], Period = "2026-10", Submitted = now };
            Transactions.Add(tx); Mark(tx, "submitted");
            status = 201; body = new { submissionId = tx.SubmissionId, instanceId = "inst-" + tx.SubmissionId[..8] };
        }
        else if (seg is ["mu", "messages", var sid] && Find(t => t.SubmissionId == sid) is { } mu && Ready(mu, 1))
        { Mark(mu, "mu-received"); status = 200; body = new { messageId = mu.MessageId, submissionId = mu.SubmissionId, status = "Stored" }; }
        else if (seg is ["mm", "messages", var mid] && Find(t => t.MessageId == mid) is { } mm && Ready(mm, 2) && Mode != "mm-reject")
        { Mark(mm, "mm-validated"); status = 200; body = new { messageId = Mode == "wrong-correlation" ? "msg-other" : mm.MessageId, reportId = mm.ReportId, validation = "Valid" }; }
        else if (seg is ["skole", "reports", var rid] && Find(t => t.ReportId == rid) is { } sk && Ready(sk, 3))
        { Mark(sk, "skole-ingested"); status = 200; body = new { reportId = sk.ReportId, messageId = sk.MessageId, period = sk.Period, status = "Received" }; }
        else if (seg is ["person", "lookups", var prid] && Find(t => t.ReportId == prid) is { } pe && pe.Transitions.Contains("skole-ingested"))
        { Mark(pe, "person-looked-up"); status = 200; body = new { reportId = pe.ReportId, found = "true" }; }
        else if (c.Request.HttpMethod == "POST" && seg is ["skole", "periods", var period, "lock"] && Find(t => t.Period == period && t.Transitions.Contains("person-looked-up") && t.DeliveryId == "") is { } lk)
        { lk.DeliveryId = "del-" + Guid.NewGuid().ToString("N")[..10]; lk.Submitted = now - TimeSpan.FromMilliseconds(HopMs * 3); Mark(lk, "period-locked"); status = 200; body = new { period, deliveryId = lk.DeliveryId }; }
        else if (seg is ["bufdata", "deliveries", var dp] && Find(t => t.Period == dp && t.DeliveryId != "") is { } bd && Ready(bd, 4))
        { Mark(bd, "bufdata-delivered"); status = 200; body = new { period = bd.Period, deliveryId = bd.DeliveryId, rows = "1" }; }
        c.Response.StatusCode = status; c.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body is null ? """{"error":"not found"}""" : JsonSerializer.Serialize(body));
        c.Response.ContentLength64 = bytes.Length;
        await c.Response.OutputStream.WriteAsync(bytes);
        c.Response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
        if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(2000));
    }

    // ── The four source packages of the chain (folder and project names drive stage discovery) ───────────────────────────────

    private const string Ns = "urn:synthetic:skolenaervaer.v2.0.1";
    private static string Xsd(string ns) => $"""<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="{ns}" xmlns="{ns}" elementFormDefault="qualified"><xs:element name="Fravaer"><xs:complexType><xs:sequence><xs:element name="Periode" type="xs:string"/></xs:sequence></xs:complexType></xs:element></xs:schema>""";
    private const string Csproj = "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
    private static string Code(string ns) => $"namespace {ns};\npublic static class Service\n{{\n    public static string Name() => \"{ns}\";\n}}\n";
    private static string Api(string title, string paths, string schemas) =>
        $$"""{ "openapi": "3.0.3", "info": { "title": "{{title}}", "version": "1.0" }, "components": { "schemas": { {{schemas}} } }, "paths": { {{paths}} } }""";
    private static string Get(string path, string param, string schema) =>
        "\"" + path + "\": " + $$"""{ "get": { "parameters": [ { "name": "{{param}}", "in": "path", "required": true, "schema": { "type": "string" } } ], "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/{{schema}}" } } } }, "404": { "description": "not yet" } } } }""";
    private static string Obj(string name, params string[] fields) =>
        "\"" + name + "\": { \"type\": \"object\", \"required\": [" + string.Join(",", fields.Select(f => $"\"{f}\"")) + "], \"properties\": { " + string.Join(",", fields.Select(f => $"\"{f}\": {{ \"type\": \"string\" }}")) + " } }";

    private static readonly string AltinnApi = Api("Synthetic Altinn app",
        """
        "/altinn/submissions": { "post": { "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/SubmissionRequest" } } } },
          "responses": { "201": { "description": "created", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Submission" } } } } } } }
        """,
        """
        "SubmissionRequest": { "type": "object", "required": ["childRef", "period", "reporterRef"], "properties": {
            "childRef": { "type": "string", "enum": ["SYN-CHILD-0001"] }, "period": { "type": "string", "enum": ["2026-10"] }, "reporterRef": { "type": "string", "enum": ["SYN-REPORTER-01"] } } },
        """ + Obj("Submission", "submissionId", "instanceId"));
    private static readonly string MuApi = Api("Synthetic MU", Get("/mu/messages/{submissionId}", "submissionId", "MuMessage"), Obj("MuMessage", "messageId", "submissionId", "status"));
    private static readonly string MmApi = Api("Synthetic MM", Get("/mm/messages/{messageId}", "messageId", "MmMessage"), Obj("MmMessage", "messageId", "reportId", "validation"));
    private static readonly string SkoleApi = Api("Synthetic Skoletjenesten",
        Get("/skole/reports/{reportId}", "reportId", "Report") + "," +
        """
        "/skole/periods/{period}/lock": { "post": { "parameters": [ { "name": "period", "in": "path", "required": true, "schema": { "type": "string" } } ],
          "responses": { "200": { "description": "locked", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Lock" } } } } } } }
        """, Obj("Report", "reportId", "messageId", "period", "status") + "," + Obj("Lock", "period", "deliveryId"));
    private static readonly string PersonApi = Api("Synthetic Person", Get("/person/lookups/{reportId}", "reportId", "Lookup"), Obj("Lookup", "reportId", "found"));
    private static readonly string BufdataApi = Api("Synthetic Bufdata delivery", Get("/bufdata/deliveries/{period}", "period", "Delivery"), Obj("Delivery", "period", "deliveryId", "rows"));

    /// <summary>A = Altinn app + MU, B = MM receiver, C = SkoleService + Person, D = Bufdata delivery.</summary>
    public static IReadOnlyList<(string FileName, byte[] Zip)> Packages() =>
    [
        ("package-a.zip", Zip([("Synth.Integration.sln", ""),
            ("Synth.AltinnApp/Synth.AltinnApp.csproj", Csproj), ("Synth.AltinnApp/Program.cs", Code("Synth_AltinnApp")), ("Synth.AltinnApp/openapi.json", AltinnApi),
            ("Synth.Meldingsutveksler.Service/Synth.Meldingsutveksler.Service.csproj", Csproj), ("Synth.Meldingsutveksler.Service/Program.cs", Code("Synth_Meldingsutveksler_Service")),
            ("Synth.Meldingsutveksler.Service/openapi.json", MuApi), ("Synth.Meldingsutveksler.Service/Schemas/skolenaervaer.xsd", Xsd(Ns))])),
        ("package-b.zip", Zip([("Synth.Handoff.sln", ""),
            ("Synth.MeldingsutvekslerReceiver/Synth.MeldingsutvekslerReceiver.csproj", Csproj), ("Synth.MeldingsutvekslerReceiver/Program.cs", Code("Synth_MeldingsutvekslerReceiver")),
            ("Synth.MeldingsutvekslerReceiver/openapi.json", MmApi), ("Synth.MeldingsutvekslerReceiver/Skjema/skolenaervaer.xsd", Xsd(Ns))])),
        ("package-c.zip", Zip([("Synth.Skole.sln", ""),
            ("SkoleService/Synth.SkoleService.Api/Synth.SkoleService.Api.csproj", Csproj), ("SkoleService/Synth.SkoleService.Api/Program.cs", Code("Synth_SkoleService_Api")), ("SkoleService/openapi.json", SkoleApi),
            ("Person/Synth.Person.Api/Synth.Person.Api.csproj", Csproj), ("Person/Synth.Person.Api/Program.cs", Code("Synth_Person_Api")), ("Person/openapi.json", PersonApi)])),
        ("package-d.zip", Zip([("Synth.Delivery.sln", ""),
            ("Synth.Bufdata.Delivery/Synth.Bufdata.Delivery.csproj", Csproj), ("Synth.Bufdata.Delivery/Program.cs", Code("Synth_Bufdata_Delivery")), ("Synth.Bufdata.Delivery/openapi.json", BufdataApi)])),
    ];

    /// <summary>The project the chain is tested in (a specification only; the services come from the four packages).</summary>
    public static byte[] Project(string name) => Zip([($"{name}/specs/001-skolenaervaer/spec.md",
        "# Feature Specification: Skolenaervaer reporting\n\n## Requirements\n\n### Functional Requirements\n\n- **FR-001**: The system MUST deliver an attendance report from Altinn to the school service and on to Bufdata.\n")]);

    private static byte[] Zip(IEnumerable<(string Path, string Text)> files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        return buffer.ToArray();
    }
}
