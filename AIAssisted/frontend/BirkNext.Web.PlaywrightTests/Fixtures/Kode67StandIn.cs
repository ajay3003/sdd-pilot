using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BirkNext.Web.PlaywrightTests.Fixtures;

/// <summary>
/// Local stand-in for the M2LB Person module's GraphQL endpoint (http://localhost:&lt;free port&gt;/graphql; nothing external) and a synthetic
/// M2LB source package, for M01 – Gradert tilgang (Kode 6/7). Three synthetic children (level 0 Ingen, level 2 Kode 7, level 3 Kode 6) and two
/// synthetic tokens. It speaks the Person module's current English schema and answers only the three queries BirkNext may send (getBarn,
/// searchBarn by exact BiRK id, getAuditLog); like Hot Chocolate it rejects the earlier Norwegian field names with errors and no data. It records
/// every request so a test can prove no other document, mutation or broad search was sent.
/// <para><see cref="Broken"/> = true reproduces graded-access defects of the kind the M2LB audit found:</para>
/// <list type="bullet">
/// <item>the profile query hides every graded child, so an identity WITH graded access gets nothing (positive control fails)</item>
/// <item>the Kode 7 child answers an unauthorized identity with an AUTH_NOT_AUTHORIZED error where a nonexistent id answers null, so the
/// child's existence can be inferred</item>
/// <item>the Kode 6 child's revision log is returned to an identity without graded access</item>
/// <item>the search counts before filtering: the hidden Kode 6 child shows up in totaltAntall</item>
/// </list>
/// <see cref="Broken"/> = false: unauthorized identities get exactly what a nonexistent child gives, authorized identities get the children.
/// </summary>
public sealed class Kode67StandIn : IAsyncDisposable
{
    public static readonly Guid Ungraded = Guid.Parse("5e7e0000-0000-4000-8000-000000000000");
    public static readonly Guid Kode7 = Guid.Parse("5e7e0000-0000-4000-8000-000000000002");
    public static readonly Guid Kode6 = Guid.Parse("5e7e0000-0000-4000-8000-000000000003");
    public static readonly IReadOnlyDictionary<int, (Guid Id, string BirkId)> Children = new Dictionary<int, (Guid, string)>
    {
        [0] = (Ungraded, "SYNTH-K0"), [2] = (Kode7, "SYNTH-K7"), [3] = (Kode6, "SYNTH-K6"),
    };
    public const string UnauthorizedToken = "synthetic-token-without-graded-access";
    public const string AuthorizedToken = "synthetic-token-with-graded-access";

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public volatile bool Broken = true;
    public string Url { get; }
    public string Endpoint => Url + "/graphql";
    /// <summary>Every request: the operation's root field and whether the token was known. Never kept beyond the test.</summary>
    public List<(string Field, string Document, bool KnownToken)> Requests { get; } = [];

    public Kode67StandIn()
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
                catch (ObjectDisposedException) { return; }
                _ = Task.Run(() => HandleAsync(context));
            }
        });
    }

    private static int LevelOf(Guid id) => id == Kode6 ? 3 : id == Kode7 ? 2 : 0;
    private static bool Exists(Guid id) => id == Ungraded || id == Kode7 || id == Kode6;

    private async Task HandleAsync(HttpListenerContext c)
    {
        string body;
        var status = 200;
        try
        {
            if (c.Request.HttpMethod != "POST" || c.Request.Url!.AbsolutePath != "/graphql") { status = 404; body = "{\"errors\":[{\"message\":\"not found\"}]}"; }
            else
            {
                using var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8);
                using var doc = JsonDocument.Parse(await reader.ReadToEndAsync());
                var query = doc.RootElement.GetProperty("query").GetString() ?? "";
                var variables = doc.RootElement.TryGetProperty("variables", out var v) ? v.Clone() : default;
                var token = c.Request.Headers["Authorization"] is { } auth && auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : null;
                var known = token is UnauthorizedToken or AuthorizedToken;
                var authorized = token == AuthorizedToken;
                var field = query.Contains("getBarn(") ? "getBarn" : query.Contains("searchBarn(") ? "searchBarn" : query.Contains("getAuditLog(") ? "getAuditLog" : "other";
                lock (Requests) Requests.Add((field, query, known));
                if (!known) { status = 401; body = ""; }
                else if (field == "other") { status = 400; body = "{\"errors\":[{\"message\":\"The field does not exist on the type Query.\",\"extensions\":{\"code\":\"HC0020\"}}]}"; }
                else body = field switch
                {
                    "getBarn" => Profile(variables.GetProperty("id").GetGuid(), authorized),
                    "searchBarn" => Search(variables.GetProperty("birkId").GetString(), authorized),
                    _ => Audit(variables.GetProperty("id").GetGuid(), authorized),
                };
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            status = 400;
            body = "{\"errors\":[{\"message\":\"bad request\"}]}";
        }
        try
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            c.Response.StatusCode = status;
            c.Response.ContentType = "application/json";
            c.Response.ContentLength64 = bytes.Length;
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
        catch (Exception) { /* the client went away */ }
    }

    private string Profile(Guid id, bool authorized)
    {
        const string none = "{\"data\":{\"getBarn\":null}}";
        if (!Exists(id)) return none;
        var level = LevelOf(id);
        var data = $"{{\"data\":{{\"getBarn\":{{\"barnRegistreringId\":\"{id}\",\"sikkerhetsnivaaCode\":{level}}}}}}}";
        if (level < 2) return data;
        if (!Broken) return authorized ? data : none;
        // Broken: the resolver's own check answers Kode 7 with a distinguishable error, and the repository hides every graded child.
        if (!authorized && level == 2) return "{\"data\":{\"getBarn\":null},\"errors\":[{\"message\":\"Ingen tilgang\",\"extensions\":{\"code\":\"AUTH_NOT_AUTHORIZED\"}}]}";
        return none;
    }

    private string Search(string? birkId, bool authorized)
    {
        var match = Children.Values.Where(c => c.BirkId == birkId).Select(c => c.Id).ToList();
        var visible = match.Where(id => LevelOf(id) < 2 || authorized).ToList();
        var total = Broken ? match.Count : visible.Count; // Broken: counted before the graded filter
        return $"{{\"data\":{{\"searchBarn\":{{\"totalCount\":{total},\"results\":[{string.Join(",", visible.Select(id => $"{{\"barnRegistreringId\":\"{id}\"}}"))}]}}}}}}";
    }

    private string Audit(Guid id, bool authorized)
    {
        if (!Exists(id)) return "{\"data\":{\"getAuditLog\":null}}";
        var level = LevelOf(id);
        var visible = level < 2 || authorized || Broken && level == 3; // Broken: the Kode 6 revision log has no graded check
        return visible ? "{\"data\":{\"getAuditLog\":{\"totalCount\":2}}}" : "{\"data\":{\"getAuditLog\":null}}";
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
        if (_loop is not null) { try { await _loop; } catch (Exception) { } }
        _stop.Dispose();
    }

    // ── Synthetic M2LB source (Person module + Person adapter), the shape the M01 analyzer reads ─────────────────────────

    /// <summary>A synthetic M2LB source ZIP (no real code, no identities): the classification seed, the CDC adapter with its guard, the Person
    /// module's profile, search and resolvers. It deliberately carries the audited source defects (constant guard input, graded profile
    /// filter, an unguarded resolver) so the static review has something to find.</summary>
    public static byte[] SourcePackage(string root) => Zip(Files(root));

    private static IEnumerable<(string Path, string Text)> Files(string root)
    {
        yield return ($"{root}/PersonAdapter/src/Adapter/M2LB.PersonAdapter.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        yield return ($"{root}/Person/src/Person/M2LB.Person.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        yield return ($"{root}/Person/src/Person/SikkerhetsnivaaType.cs", """
            namespace M2LB.Person;
            public class SikkerhetsnivaaType
            {
                public Guid SikkerhetsnivaaTypeId { get; set; }
                public int Nivaa { get; set; }
                public string Verdi { get; set; } = "";
                public string? BiRKKode { get; set; }
                public string? ElementsKode { get; set; }
                public string Beskrivelse { get; set; } = "";
                public bool KreverGradertTilgang { get; set; }
            }
            """);
        yield return ($"{root}/Person/src/Person/PersonDbContext.cs", """
            namespace M2LB.Person;
            public class PersonDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder mb)
                {
                    mb.Entity<SikkerhetsnivaaType>(e => { e.HasIndex(s => s.Nivaa).IsUnique(); });
                    mb.Entity<SikkerhetsnivaaType>().HasData(
                        new SikkerhetsnivaaType { Nivaa = 0, Verdi = "Ingen", Beskrivelse = "Ingen", KreverGradertTilgang = false },
                        new SikkerhetsnivaaType { Nivaa = 1, Verdi = "SkjultAdresse", Beskrivelse = "Skjult adresse", KreverGradertTilgang = false },
                        new SikkerhetsnivaaType { Nivaa = 2, Verdi = "Kode7", BiRKKode = "Kode 7", ElementsKode = "K1", Beskrivelse = "Kode 7", KreverGradertTilgang = true },
                        new SikkerhetsnivaaType { Nivaa = 3, Verdi = "Kode6", BiRKKode = "Kode 6", ElementsKode = "K2", Beskrivelse = "Kode 6", KreverGradertTilgang = true });
                }
            }
            """);
        yield return ($"{root}/PersonAdapter/src/Adapter/CdcEvent.cs", "public sealed record CdcEvent(string Operasjon, string Tabellnavn, int Sikkerhetsnivaa, JsonElement Payload);");
        yield return ($"{root}/PersonAdapter/src/Adapter/CdcProcessorWorker.cs", """
            public sealed class CdcProcessorWorker
            {
                private static CdcEvent? Deserialize(BinaryData body)
                {
                    using var doc = JsonDocument.Parse(body);
                    var envelope = doc.RootElement.GetProperty("payload");
                    var op = envelope.GetProperty("op").GetString() ?? string.Empty;
                    var recordField = op == "d" ? "before" : "after";
                    if (!envelope.TryGetProperty(recordField, out var record)) return null;
                    return new CdcEvent(
                        Operasjon: op,
                        Tabellnavn: "Barn",
                        Sikkerhetsnivaa: 0, // not present in Debezium envelope
                        Payload: record.Clone());
                }
            }
            """);
        yield return ($"{root}/PersonAdapter/src/Adapter/SecurityClassificationGuard.cs", """
            public sealed class SecurityClassificationGuard
            {
                private static readonly Counter<long> _kode67Counter = _meter.CreateCounter<long>("birk.kode67.rejections");
                public GuardResult Evaluate(CdcEvent cdcEvent)
                {
                    if (cdcEvent.Sikkerhetsnivaa is not (2 or 3)) return GuardResult.Allowed;
                    _logger.LogCritical("Kode 6/7 record rejected — tabellnavn: {Tabellnavn}", cdcEvent.Tabellnavn);
                    _alerts.RaiseKode67Alert(cdcEvent.Tabellnavn, DateTimeOffset.UtcNow);
                    _kode67Counter.Add(1);
                    return GuardResult.Rejected;
                }
            }
            """);
        yield return ($"{root}/PersonAdapter/src/Adapter/CdcRouter.cs", """
            public sealed class CdcRouter
            {
                public Task<RoutingResult> RouteAsync(CdcEvent cdcEvent, CancellationToken ct)
                {
                    if (guard.Evaluate(cdcEvent) == GuardResult.Rejected)
                        return Task.FromResult(new RoutingResult(RoutingOutcome.Rejected));
                    if (cdcEvent.OperationType == OperationType.Delete)
                        return Task.FromResult(new RoutingResult(RoutingOutcome.Discarded));
                    var record = childMapper.Map(cdcEvent);
                    return Task.FromResult(new RoutingResult(RoutingOutcome.Delivered));
                }
            }
            """);
        yield return ($"{root}/PersonAdapter/src/Adapter/ChildRegistrationMapper.cs", """
            public sealed partial class ChildRegistrationMapper
            {
                public ChildRegistrationRecord? Map(CdcEvent cdcEvent)
                {
                    var sikkerhetsnivaa = GetInt(cdcEvent.Payload, "Sikkerhetsnivå");
                    return new ChildRegistrationRecord(MapSikkerhetsnivaa(sikkerhetsnivaa));
                }
                private static Guid MapSikkerhetsnivaa(int? nivaa) => nivaa switch
                {
                    0 => new Guid("d1000000-0000-0000-0000-000000000000"), // Ingen
                    2 => new Guid("d1000000-0000-0000-0000-000000000002"), // Kode7
                    3 => new Guid("d1000000-0000-0000-0000-000000000003"), // Kode6
                    _ => new Guid("d1000000-0000-0000-0000-000000000000"), // default Ingen
                };
            }
            """);
        yield return ($"{root}/Person/src/Person/BarnProfileService.cs", """
            public class BarnProfileService
            {
                public async Task<BarnProfil> HentBarnProfilAsync(Guid barnRegistreringId, Guid brukerId, CancellationToken ct = default)
                {
                    var barn = await _repository.HentBarnProfilAsync(barnRegistreringId, ct);
                    if (barn is null) throw new PersonNotFoundException(barnRegistreringId);
                    if (barn.SikkerhetsnivaaType?.KreverGradertTilgang == true)
                    {
                        var harGradertTilgang = await _autorisasjonClient.EvaluerOperasjon(brukerId, "Person:SeGradertBarn", barnRegistreringId);
                        if (!harGradertTilgang) throw new PersonNotFoundException(barnRegistreringId);
                    }
                    await _leseloggPublisher.PublishAsync(new LeseloggHendelse(), ct);
                    return MapTilProfil(barn);
                }
            }
            """);
        yield return ($"{root}/Person/src/Person/PersonRepository.cs", """
            public class PersonRepository
            {
                public async Task<(IReadOnlyList<BarnSoekResultat> Resultater, int TotaltAntall)> SoekBarnAsync(BarnSoekKriterier kriterier, IReadOnlyList<Guid> grantedChildIds, CancellationToken ct = default)
                {
                    var query = _db.Persons.Join(_db.SikkerhetsnivaaTyper, pb => pb.b.SikkerhetsnivaaTypeId, s => s.SikkerhetsnivaaTypeId, (pb, s) => new { pb.p, pb.b, s });
                    query = query.Where(x => !x.s.KreverGradertTilgang || grantedChildIds.Contains(x.b.BarnRegistreringId));
                    var totaltAntall = await query.CountAsync(ct);
                    return (await query.Skip(0).Take(20).ToListAsync(ct), totaltAntall);
                }
                public async Task<Barn?> HentBarnProfilAsync(Guid barnRegistreringId, CancellationToken ct = default)
                    => await _db.BarnRegistreringer.Include(b => b.SikkerhetsnivaaType)
                        .Where(b => b.SikkerhetsnivaaType != null && !b.SikkerhetsnivaaType.KreverGradertTilgang)
                        .FirstOrDefaultAsync(b => b.BarnRegistreringId == barnRegistreringId, ct);
                public async Task<Guid?> HentBarnRegistreringIdFraEksternIdAsync(int eksternId, CancellationToken ct = default)
                    => await _db.BarnRegistreringer.Where(b => b.EksternId == eksternId).Select(b => (Guid?)b.BarnRegistreringId).FirstOrDefaultAsync(ct);
            }
            """);
        yield return ($"{root}/Person/src/Person/AutorisasjonClient.cs", """
            public class AutorisasjonClient : IAutorisasjonClient
            {
                public async Task<bool> EvaluerOperasjon(Guid brukerId, string operasjonId, Guid? barnRegistreringId = null)
                {
                    try { var response = await Post(); return response?.Tillatt ?? false; }
                    catch (Exception ex) { throw new AutorisasjonException("utilgjengelig", ex); }
                }
                public async Task<IReadOnlyList<Guid>> HentGradertBarntilganger(Guid brukerId)
                {
                    var response = await Get();
                    return response?.Barn.Select(b => b.BarnId).ToList() ?? [];
                }
            }
            """);
        yield return ($"{root}/Person/src/Person/Resolvers.cs", """
            [ExtendObjectType("Query")]
            public class BarnProfilQueryResolver
            {
                public async Task<BarnProfilGql?> HentBarnAsync(Guid barnRegistreringId, [Service] IGraphQLAuthMiddleware auth, [Service] BarnProfileService profileService, CancellationToken ct)
                {
                    await auth.KrevOperasjon("Person:SeBarnProfil", barnRegistreringId);
                    try { return Map(await profileService.HentBarnProfilAsync(barnRegistreringId, auth.HentBrukerId(), ct)); }
                    catch (PersonNotFoundException) { return null; }
                }
            }
            [ExtendObjectType("Query")]
            public class BirkIdQueryResolver
            {
                public async Task<Guid?> HentBarnRegistreringIdFraEksternIdAsync(int eksternId, [Service] IPersonRepository repository, CancellationToken ct)
                {
                    // no auth call here
                    return await repository.HentBarnRegistreringIdFraEksternIdAsync(eksternId, ct);
                }
            }
            """);
        yield return ($"{root}/Person/src/Person/Program.cs", "var app = builder.Build();\napp.MapGraphQL();\napp.Run();");
        yield return ($"{root}/PersonAdapter/tests/Adapter.Tests/Adapter.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup></Project>");
        yield return ($"{root}/PersonAdapter/tests/Adapter.Tests/SecurityClassificationGuardTests.cs", """
            public class SecurityClassificationGuardTests
            {
                private static CdcEvent MakeEvent(int level) => new("c", "Barn", level, JsonDocument.Parse("{}").RootElement);
                [Theory] public void Evaluate_LevelTwoOrThree_ReturnsRejected(int level) { }
            }
            """);
    }

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
