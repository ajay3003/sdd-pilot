using System.Net;
using System.Text;
using System.Text.Json;

namespace BirkNext.Web.PlaywrightTests.Fixtures;

/// <summary>
/// A local stand-in for a self-hosted LibreTranslate server (http://127.0.0.1:&lt;port&gt;/translate; nothing external). It speaks the documented
/// request (<c>q</c> as an array, <c>source</c>, <c>target</c>, <c>format</c>) and answers <c>translatedText</c> as an array: each text is
/// translated with a small fixed phrase table and marked "[nb]" so a test can tell machine text from a person's. Every request body is
/// recorded, so a test can prove what was sent (only plain-language pieces, never an identifier). GET /languages lists English and Norwegian.
/// </summary>
public sealed class LibreTranslateStandIn : IAsyncDisposable
{
    public const string PortVariable = "BIRKNEXT_PLAYWRIGHT_TRANSLATE_PORT";
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public List<string> Bodies { get; } = [];

    private static readonly (string En, string Nb)[] Phrases =
    [
        ("Child past compulsory school age when the period locks", "Barn over opplæringspliktig alder når perioden låses"),
        ("Prepare the starting situation in the test environment:", "Klargjør startsituasjonen i testmiljøet:"),
        ("The starting situation is in place before the action (check the prepared data and state).", "Startsituasjonen er på plass før handlingen (kontroller klargjorte data og tilstand)."),
        ("Carry out or trigger:", "Utfør eller utløs:"),
        ("A child past compulsory school age", "Et barn over opplæringspliktig alder"),
        ("a child past compulsory school age", "et barn over opplæringspliktig alder"),
        ("the period locks", "perioden låses"),
        ("the child is not expected", "barnet er ikke forventet"),
        ("Confirm that", "Bekreft at"),
    ];

    public LibreTranslateStandIn(int port) => _listener.Prefixes.Add($"http://127.0.0.1:{port}/");

    public static int? ConfiguredPort => int.TryParse(Environment.GetEnvironmentVariable(PortVariable), out var p) ? p : null;

    public static string Translate(string text)
    {
        var t = text;
        foreach (var (en, nb) in Phrases) t = t.Replace(en, nb, StringComparison.Ordinal);
        return "[nb] " + t;
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
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(() => HandleAsync(context));
            }
        });
    }

    private async Task HandleAsync(HttpListenerContext c)
    {
        var (status, body) = (200, "");
        try
        {
            using var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8);
            var request = await reader.ReadToEndAsync();
            lock (Bodies) Bodies.Add(request);
            if (c.Request.HttpMethod == "GET" && c.Request.Url!.AbsolutePath == "/languages")
                body = """[{"code":"en","name":"English","targets":["en","nb"]},{"code":"nb","name":"Norwegian","targets":["en","nb"]}]""";
            else if (c.Request.HttpMethod != "POST" || c.Request.Url!.AbsolutePath != "/translate") (status, body) = (404, """{"error":"Not found"}""");
            else
            {
                var json = JsonDocument.Parse(request).RootElement;
                if (json.GetProperty("target").GetString() != "nb") (status, body) = (400, """{"error":"Target language not supported"}""");
                else body = JsonSerializer.Serialize(new { translatedText = json.GetProperty("q").EnumerateArray().Select(q => Translate(q.GetString() ?? "")).ToList() });
            }
        }
        catch (JsonException) { (status, body) = (400, """{"error":"Invalid request"}"""); }
        var bytes = Encoding.UTF8.GetBytes(body);
        c.Response.StatusCode = status;
        c.Response.ContentType = "application/json";
        c.Response.ContentLength64 = bytes.Length;
        try { await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close(); }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
        if (_loop is not null) try { await _loop; } catch (Exception e) when (e is ObjectDisposedException or HttpListenerException) { }
    }
}
