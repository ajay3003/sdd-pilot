using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.ExecutionEvidence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Api.Tests.Services.ExecutionEvidence;

/// <summary>
/// Execution evidence through the real HTTP client pipeline against a synthetic loopback target: capture at the transport boundary, redaction
/// before persistence, attempts vs logical calls, GraphQL envelopes, assertions, blocked-before-send, recovery after restart, export.
/// The server's own observations are the oracle for what was actually transmitted.
/// </summary>
public sealed class ExecutionEvidenceTests : IAsyncLifetime
{
    private LoopbackHttpTestServer _server = null!;
    private readonly ConcurrentQueue<(string Method, string Path, string Body, string? Correlation, string? Authorization)> _seen = new();
    private int _flaky;
    private ServiceProvider _services = null!;

    public Task InitializeAsync()
    {
        _server = new LoopbackHttpTestServer(Handle);
        _server.Start();
        var services = new ServiceCollection();
        var db = $"ee-{Guid.NewGuid():N}";
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(db));
        services.AddScoped<ExecutionEvidenceStore>();
        services.AddLogging();
        services.AddTransient<EvidenceCaptureHandler>();
        services.AddHttpClient(ExecutionRunService.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .AddHttpMessageHandler<EvidenceCaptureHandler>();
        services.AddSingleton<IApiEnvironmentSafetyPolicy>(ApiEnvironmentSafetyPolicy.Default);
        services.AddSingleton<ExecutionRunService>();
        _services = services.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() { await _server.StopAsync(); await _services.DisposeAsync(); }

    private async Task Handle(HttpListenerContext c)
    {
        var path = c.Request.Url!.AbsolutePath;
        string body;
        using (var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
        _seen.Enqueue((c.Request.HttpMethod, path, body, c.Request.Headers[ExecutionRunService.CorrelationHeader], c.Request.Headers["Authorization"]));
        async Task Send(int status, string? type, string text)
        {
            c.Response.StatusCode = status;
            if (type is not null) c.Response.ContentType = type;
            var bytes = Encoding.UTF8.GetBytes(text);
            c.Response.ContentLength64 = bytes.Length;
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
        switch (path)
        {
            case "/children/missing": await Send(404, "application/json", """{"error":"not found"}"""); break;
            case "/children/1": await Send(200, "application/json", """{"id":1,"name":"Kari Testesen","password":"s3cret-pw","nin":"01017012345"}"""); break;
            case "/children" when c.Request.HttpMethod == "POST":
                await Send(body.Contains("\"name\"", StringComparison.Ordinal) ? 201 : 400, "application/json", body.Contains("\"name\"", StringComparison.Ordinal) ? """{"id":2}""" : """{"error":"name is required"}""");
                break;
            case "/empty": c.Response.StatusCode = 204; c.Response.Close(); break;
            case "/badjson": await Send(200, "application/json", "{not json"); break;
            case "/flaky": await Send(Interlocked.Increment(ref _flaky) <= 2 ? 503 : 200, "application/json", """{"ok":true}"""); break;
            case "/slow": await Task.Delay(2000); await Send(200, "application/json", "{}"); break;
            case "/graphql":
                if (body.Contains("forbidden", StringComparison.Ordinal)) await Send(200, "application/json", """{"data":null,"errors":[{"message":"Forbidden","path":["child"],"extensions":{"code":"FORBIDDEN"}}]}""");
                else if (body.Contains("partial", StringComparison.Ordinal)) await Send(200, "application/json", """{"data":{"child":{"id":"1"},"school":null},"errors":[{"message":"School unavailable","path":["school"]}]}""");
                else await Send(200, "application/json", """{"data":{"child":{"id":"1","name":"Kari"}}}""");
                break;
            default: await Send(404, "text/plain", "no route"); break;
        }
    }

    private ExecutionRunRequest Request(params ExecutionCallDefinition[] calls) => new()
    {
        Name = "Synthetic run", EnvironmentId = "local-synthetic", TargetUrl = _server.Url("/"), EnvironmentType = "Local", Calls = [.. calls],
    };

    private static ExecutionCallDefinition Get(string name, string path, params AssertionDefinition[] a) =>
        new() { Name = name, Protocol = ExecutionProtocol.Rest, Method = "GET", Path = path, Assertions = [.. a] };
    private static AssertionDefinition Status(string expected) => new($"status-{expected}", AssertionKind.StatusCode, AssertionOperator.Equals, null, expected);

    private async Task<ExecutionRunDetail> RunAsync(ExecutionRunRequest request)
    {
        var start = await _services.GetRequiredService<ExecutionRunService>().StartAsync(request, "test", default);
        start.RunId.Should().NotBeNull(start.Message);
        for (var i = 0; i < 200 && ExecutionRunService.IsRunning(start.RunId!.Value); i++) await Task.Delay(50);
        using var scope = _services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>().GetAsync(start.RunId!.Value, new(Take: 200), default))!;
    }

    [Fact]
    public async Task A_mixed_run_records_calls_attempts_requests_responses_and_assertions_as_transmitted()
    {
        var detail = await RunAsync(Request(
            Get("child 1", "/children/1", Status("200"), new AssertionDefinition("name", AssertionKind.JsonPath, AssertionOperator.Equals, "name", "Kari Testesen")),
            Get("expected 404", "/children/missing", Status("404")),
            Get("assertion fails", "/children/1", Status("201")),
            Get("no assertions", "/children/1"),
            Get("no content", "/empty", Status("204")),
            Get("invalid json", "/badjson", new AssertionDefinition("id", AssertionKind.JsonPath, AssertionOperator.Exists, "id")),
            Get("flaky", "/flaky", Status("200")) with { Retry = new(3, [503]) },
            new ExecutionCallDefinition { Name = "gql ok", Protocol = ExecutionProtocol.GraphQl, Path = "/graphql", GraphQlQuery = "query { child(id: 1) { id name } }",
                Assertions = [new("data", AssertionKind.GraphQlHasData, AssertionOperator.Equals, null, "true"), new("errors", AssertionKind.GraphQlErrorCount, AssertionOperator.Equals, null, "0")] },
            new ExecutionCallDefinition { Name = "gql expected forbidden", Protocol = ExecutionProtocol.GraphQl, Path = "/graphql", GraphQlQuery = "query forbidden { child(id: 9) { id } }",
                Assertions = [Status("200"), new("code", AssertionKind.GraphQlErrorCode, AssertionOperator.Contains, null, "FORBIDDEN")] },
            new ExecutionCallDefinition { Name = "gql 200 with errors is not success", Protocol = ExecutionProtocol.GraphQl, Path = "/graphql", GraphQlQuery = "query partial { child(id: 1) { id } school { id } }",
                Assertions = [Status("200"), new("errors", AssertionKind.GraphQlErrorCount, AssertionOperator.Equals, null, "0")] }));

        var byName = detail.Calls.ToDictionary(c => c.Definition.Name);
        detail.TotalCalls.Should().Be(10);
        byName["child 1"].State.Should().Be(ExecutionState.Passed);
        byName["expected 404"].State.Should().Be(ExecutionState.Passed, "404 is a pass when the assertion expects it");
        byName["assertion fails"].State.Should().Be(ExecutionState.Failed);
        byName["no assertions"].State.Should().Be(ExecutionState.NotAssessed, "no assertions never means pass");
        byName["no content"].State.Should().Be(ExecutionState.Passed);
        byName["no content"].Attempts[0].Response!.Body!.Completeness.Should().Be(CaptureCompleteness.Empty);
        byName["invalid json"].State.Should().Be(ExecutionState.Failed);
        byName["invalid json"].Attempts[0].Response!.InvalidJson.Should().BeTrue();
        byName["flaky"].Attempts.Should().HaveCount(3, "each retry is a physical attempt of one logical call");
        byName["flaky"].Attempts[1].RetryReason.Should().Be("Retry after HTTP 503");
        byName["flaky"].State.Should().Be(ExecutionState.Passed);
        byName["gql ok"].State.Should().Be(ExecutionState.Passed);
        byName["gql expected forbidden"].State.Should().Be(ExecutionState.Passed, "the negative test expects the GraphQL error");
        byName["gql 200 with errors is not success"].State.Should().Be(ExecutionState.Failed);
        byName["gql 200 with errors is not success"].Attempts[0].Response!.GraphQlHasData.Should().BeTrue();

        // Counts reconcile: 10 logical calls, 12 physical attempts; the server saw exactly the sent attempts, each with its correlation id.
        detail.Run.Counts.Planned.Should().Be(10);
        detail.Run.Counts.Attempts.Should().Be(12);
        _seen.Should().HaveCount(12);
        _seen.Select(s => s.Correlation).Should().OnlyHaveUniqueItems().And.OnlyContain(x => x != null);
        detail.Run.State.Should().Be(ExecutionState.Failed);
        detail.Run.CallsWithCompleteEvidence.Should().Be(10);

        // Redaction before persistence: the response password and the identity number never reach the stored document.
        var stored = byName["child 1"].Attempts[0].Response!.Body!;
        stored.Text.Should().NotContain("s3cret-pw").And.NotContain("01017012345").And.Contain("Kari Testesen");
        stored.RedactedFields.Should().Be(2);
        stored.ByteCount.Should().Be(Encoding.UTF8.GetByteCount("""{"id":1,"name":"Kari Testesen","password":"s3cret-pw","nin":"01017012345"}"""), "the byte count is what was received");
        // The captured GraphQL request equals what the server received.
        var sentGql = _seen.First(s => s.Body.Contains("forbidden", StringComparison.Ordinal)).Body;
        byName["gql expected forbidden"].Attempts[0].Request!.Body!.Text.Should().Be(sentGql);
        byName["gql expected forbidden"].Attempts[0].Request!.GraphQlOperationType.Should().Be("query");
    }

    [Fact]
    public async Task Transport_failures_and_timeouts_have_a_request_but_never_an_invented_response()
    {
        var closed = new Uri("http://localhost:1/");
        var start = await _services.GetRequiredService<ExecutionRunService>().StartAsync(Request(Get("down", "/x", Status("200"))) with { TargetUrl = closed.ToString() }, "test", default);
        for (var i = 0; i < 200 && ExecutionRunService.IsRunning(start.RunId!.Value); i++) await Task.Delay(50);
        using (var scope = _services.CreateScope())
        {
            var call = (await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>().GetAsync(start.RunId!.Value, new(), default))!.Calls.Single();
            call.State.Should().Be(ExecutionState.Error);
            call.Attempts.Single().Transport.Should().Be(TransportResult.ConnectionFailed);
            call.Attempts.Single().Request.Should().NotBeNull();
            call.Attempts.Single().Response.Should().BeNull();
            call.Assertions.Single().Outcome.Should().Be(AssertionOutcome.NotAssessed);
        }
        var slow = await RunAsync(Request(Get("slow", "/slow", Status("200")) with { TimeoutMs = 500 }));
        slow.Calls.Single().State.Should().Be(ExecutionState.TimedOut);
        slow.Calls.Single().Attempts.Single().Response.Should().BeNull();
    }

    [Fact]
    public async Task Unsafe_targets_mutations_credentials_and_subscriptions_are_blocked_before_anything_is_sent()
    {
        var service = _services.GetRequiredService<ExecutionRunService>();
        var production = await service.StartAsync(Request(Get("x", "/children/1")) with { EnvironmentType = "Production" }, "test", default);
        production.State.Should().Be(ExecutionState.Blocked);
        var post = new ExecutionCallDefinition { Name = "create", Protocol = ExecutionProtocol.Rest, Method = "POST", Path = "/children", Body = """{"name":"Ola"}""" };
        var detail = await RunAsync(Request(
            post,
            Get("credential", "/children/1") with { Headers = new(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "Bearer abcdefghijkl" } },
            Get("absolute", "//evil.example/x"),
            new ExecutionCallDefinition { Name = "sub", Protocol = ExecutionProtocol.GraphQl, Path = "/graphql", GraphQlQuery = "subscription { changed { id } }" },
            Get("allowed", "/children/1", Status("200"))));
        var byName = detail.Calls.ToDictionary(c => c.Definition.Name);
        byName["create"].State.Should().Be(ExecutionState.Blocked); byName["create"].Reason.Should().Contain("explicitly confirmed");
        byName["credential"].State.Should().Be(ExecutionState.Blocked); byName["credential"].Definition.Headers.Should().NotContainKey("Authorization");
        byName["absolute"].State.Should().Be(ExecutionState.Blocked);
        byName["sub"].State.Should().Be(ExecutionState.Blocked); byName["sub"].Reason.Should().Contain("subscriptions");
        foreach (var name in new[] { "create", "credential", "absolute", "sub" }) byName[name].Attempts.Should().BeEmpty("a blocked call has no transmitted exchange");
        _seen.Should().ContainSingle("only the allowed call reached the server").Which.Authorization.Should().BeNull();

        var confirmed = await RunAsync(Request(post) with { ConfirmMutatingCalls = true });
        confirmed.Calls.Single().Attempts.Single().Response!.StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task After_a_restart_running_runs_become_interrupted_without_resending()
    {
        var runId = Guid.NewGuid();
        using (var scope = _services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>();
            await store.CreateAsync(new ExecutionRunSummary { RunId = runId, Name = "crashed", EnvironmentId = "e", State = ExecutionState.Running, StartedAt = DateTimeOffset.UtcNow },
                [new ExecutionCallRecord { CallId = Guid.NewGuid(), Sequence = 1, State = ExecutionState.Running, Definition = Get("a", "/children/1") },
                 new ExecutionCallRecord { CallId = Guid.NewGuid(), Sequence = 2, State = ExecutionState.Planned, Definition = Get("b", "/children/1") }], default);
            (await store.RecoverInterruptedAsync(default)).Should().Be(1);
            var detail = (await store.GetAsync(runId, new(), default))!;
            detail.Run.State.Should().Be(ExecutionState.Interrupted);
            detail.Calls.Should().OnlyContain(c => c.State == ExecutionState.Interrupted);
            detail.Calls[1].Reason.Should().Contain("never sent");
        }
        _seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Export_contains_only_redacted_stored_evidence_with_verifiable_hashes()
    {
        var detail = await RunAsync(Request(Get("child 1", "/children/1", Status("200"))));
        using var scope = _services.CreateScope();
        var zip = (await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceStore>().ExportAsync(detail.Run.RunId, default))!;
        using var archive = new ZipArchive(new MemoryStream(zip));
        archive.Entries.Select(e => e.FullName).Should().BeEquivalentTo(["manifest.json", "calls/0001.json", "hashes.json"]);
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(new StreamReader(archive.GetEntry("hashes.json")!.Open()).ReadToEnd())!;
        foreach (var entry in archive.Entries.Where(e => e.FullName != "hashes.json"))
        {
            using var ms = new MemoryStream(); entry.Open().CopyTo(ms);
            Convert.ToHexString(SHA256.HashData(ms.ToArray())).ToLowerInvariant().Should().Be(hashes[entry.FullName]);
            Encoding.UTF8.GetString(ms.ToArray()).Should().NotContain("s3cret-pw").And.NotContain("01017012345");
        }
    }

    [Fact]
    public void Redaction_masks_credentials_before_storage_and_keeps_binary_and_unsafe_bodies_metadata_only()
    {
        ExecutionEvidenceRedaction.Headers([new("Authorization", ["Bearer x"]), new("Cookie", ["s=1"]), new("X-Trace", ["abc"])])
            .Should().BeEquivalentTo([new CapturedHeader("Authorization", ExecutionEvidenceRedaction.Mask, true), new CapturedHeader("Cookie", ExecutionEvidenceRedaction.Mask, true), new CapturedHeader("X-Trace", "abc")]);
        ExecutionEvidenceRedaction.Url(new Uri("http://localhost:5/a?access_token=abc&page=2&password=x")).Should().NotContain("abc").And.Contain("page=2").And.NotContain("password=x");
        var json = ExecutionEvidenceRedaction.Body(Encoding.UTF8.GetBytes("""{"user":{"clientSecret":"z","name":"n"},"vars":{"apiKey":"k"},"t":"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig"}"""), 99, "application/json", CapturePolicy.BoundedBody, 1000, false);
        json.Text.Should().NotContain("\"z\"").And.NotContain("\"k\"").And.NotContain("eyJhbGci"); json.RedactedFields.Should().Be(3);
        ExecutionEvidenceRedaction.Body(Encoding.UTF8.GetBytes("<r><Password>p</Password><Id>1</Id></r>"), 40, "application/xml", CapturePolicy.BoundedBody, 1000, false).Text
            .Should().Contain("<Password>••••••</Password>").And.Contain("<Id>1</Id>");
        ExecutionEvidenceRedaction.Body([1, 2, 3], 3, "application/octet-stream", CapturePolicy.FullPermittedBody, 1000, false).Completeness.Should().Be(CaptureCompleteness.MetadataOnly);
        ExecutionEvidenceRedaction.Body(Encoding.UTF8.GetBytes("""{"a":"""), 5_000_000, "application/json", CapturePolicy.BoundedBody, 1000, readTruncated: true)
            .Completeness.Should().Be(CaptureCompleteness.MetadataOnly, "a truncated JSON body cannot be field-redacted, so it is not stored");
        var big = ExecutionEvidenceRedaction.Body(Encoding.UTF8.GetBytes(new string('a', 5000)), 5000, "text/plain", CapturePolicy.BoundedBody, 1000, false);
        big.Completeness.Should().Be(CaptureCompleteness.Truncated); big.Text!.Length.Should().Be(1000);
        ExecutionEvidenceRedaction.Body(Encoding.UTF8.GetBytes("x"), 1, "text/plain", CapturePolicy.MetadataOnly, 1000, false).Text.Should().BeNull();
    }
}
