using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.Api.Tests.TestInfrastructure;
using BirkNext.PerformanceTests;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Request construction and response handling end to end against a local fake API (loopback only): value encoding per position, JSON/XML/text/
/// HTML/binary/empty/large/invalid responses, GraphQL data vs errors, JSON and header extraction feeding later steps, a missing output that stops
/// the consumer (never a literal placeholder or a sample value), redaction, and the k6 script/summary that execute the same rules under load.
/// </summary>
public sealed class PerformanceRequestResponseTests : IAsyncLifetime
{
    private readonly ConcurrentQueue<(string Method, string Path, string Query, string? ContentType, string Body, string? Authorization)> _received = new();
    private LoopbackHttpTestServer _server = null!;

    public Task InitializeAsync()
    {
        _server = new LoopbackHttpTestServer(Handle);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.StopAsync();

    private async Task Handle(HttpListenerContext c)
    {
        var path = c.Request.Url!.AbsolutePath;
        string body;
        using (var reader = new StreamReader(c.Request.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
        _received.Enqueue((c.Request.HttpMethod, path, c.Request.Url.Query, c.Request.ContentType, body, c.Request.Headers["Authorization"]));
        async Task Send(int status, string? type, string text, params (string, string)[] headers)
        {
            c.Response.StatusCode = status;
            if (type is not null) c.Response.ContentType = type;
            foreach (var (n, v) in headers) c.Response.Headers[n] = v;
            var bytes = Encoding.UTF8.GetBytes(text);
            c.Response.ContentLength64 = bytes.Length;
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
        switch (path)
        {
            case "/person" when c.Request.HttpMethod == "POST": await Send(201, "application/json", """{"id":123,"items":[1,2]}""", ("Location", "/person/123"), ("X-Request-Id", "req-7")); break;
            case "/person/123": await Send(200, "application/json; charset=utf-8", """{"id":123,"name":"Test Person"}"""); break;
            case "/tokens": await Send(200, "application/json", """{"id":1,"access_token":"secret-abc","nested":{"password":"p4ss"}}"""); break;
            case "/report" when c.Request.HttpMethod == "POST": await Send(202, "application/xml", "<Receipt xmlns=\"urn:r\"><Id>9</Id></Receipt>"); break;
            case "/report": await Send(200, "application/xml", "<Report><Id>7</Id><Password>p</Password></Report>"); break;
            case "/text": await Send(200, "text/plain", "plain ok"); break;
            case "/html": await Send(200, "text/html", "<html><script>alert(1)</script><p>hi</p></html>"); break;
            case "/empty": c.Response.StatusCode = 204; c.Response.Close(); break;
            case "/binary":
                c.Response.StatusCode = 200; c.Response.ContentType = "application/octet-stream";
                var bytes = new byte[] { 0, 1, 2, 3, 255 };
                c.Response.ContentLength64 = bytes.Length; await c.Response.OutputStream.WriteAsync(bytes); c.Response.Close(); break;
            case "/large": await Send(200, "application/json", "[" + string.Join(",", Enumerable.Repeat("\"" + new string('x', 1000) + "\"", 300)) + "]"); break;
            case "/badjson": await Send(200, "application/json", "{oops"); break;
            case "/badxml": await Send(200, "application/xml", "<a><b></a>"); break;
            case "/client-error": await Send(400, "application/problem+json", """{"title":"Bad request","status":400}"""); break;
            case "/server-error": await Send(500, "text/xml", "<Error><Code>E1</Code></Error>"); break;
            case "/graphql": await Send(200, "application/json", body.Contains("fail", StringComparison.Ordinal)
                ? """{"errors":[{"message":"Child not found"}],"data":null}""" : """{"data":{"child":{"id":"c1"}}}"""); break;
            default: await Send(404, "application/json", """{"title":"Not found"}"""); break;
        }
    }

    private sealed class Clients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All });
    }

    private PerformanceTrialService Service() => new(new Clients(), new PerformanceTestOptions(), new ServiceCollection().BuildServiceProvider());

    private PerformanceTestDefinition Definition(PerformanceTargetType type, params HttpPerformanceStep[] steps) => PerformanceTestReviewTests.Definition(d => d with
    {
        TargetOrigin = _server.Url("").TrimEnd('/'), EnvironmentType = "Development", TargetType = type, AuthenticationReference = null,
        Scenario = d.Scenario with { TestDataProfileId = null, Steps = [.. steps] },
    });

    private static HttpPerformanceStep Get(string id, string path, params StepOutput[] outputs) => new()
    {
        Id = id, Name = id, Method = "GET", RelativePath = path, ExpectedStatusCodes = [200], Outputs = outputs.Length == 0 ? null : [.. outputs],
    };

    private static ScenarioDataMapping Fixed(string variable, string value, string place = "path") => new(variable, [], TestDataDataType.SimpleValues, place) { Source = RuntimeValueSource.Fixed, FixedValue = value };

    private Task<PerformanceTrialResult> Trial(PerformanceTestDefinition d) => Service().RunAsync(new PerformanceTrialRequest { Definition = d }, CancellationToken.None);

    [Fact]
    public async Task Json_post_then_get_correlates_through_a_body_output_and_a_header_output()
    {
        var create = new HttpPerformanceStep
        {
            Id = "create", Name = "create", Method = "POST", RelativePath = "/person", PostIsSafeRead = true, ExpectedStatusCodes = [201],
            ContentType = "application/json", BodyTemplate = """{"name":"{name}","age":{age}}""",
            DataMappings = [Fixed("name", "Test \"Person\"", "body"), Fixed("age", "42", "body")],
            Outputs = [new StepOutput("personId", "$.id"), new StepOutput("location", "Location") { From = StepOutputSource.Header }],
        };
        var read = Get("read", "/person/{id}") with { DataMappings = [new ScenarioDataMapping("id", [], TestDataDataType.SimpleValues, "path") { Source = RuntimeValueSource.PreviousStep, FromStepId = "create", Output = "personId" }] };
        var result = await Trial(Definition(PerformanceTargetType.RestHttp, create, read));

        result.Message.Should().BeNull();
        var first = result.Steps[0];
        first.Should().Match<PerformanceTrialStep>(s => s.State == TrialStepState.Sent && s.ExpectedStatus == true && s.Response!.Status == 201 && s.Response.Format == PayloadFormat.Json);
        first.Request!.Headers.Should().ContainSingle(h => h.Name == "Content-Type").Which.Value.Should().Be("application/json");
        first.Outputs.Select(o => (o.Name, o.State, o.Value)).Should().Equal(("personId", OutputSampleState.Ready, "123"), ("location", OutputSampleState.Ready, "/person/123"));
        var post = _received.First(r => r.Method == "POST");
        JsonDocument.Parse(post.Body).RootElement.GetProperty("name").GetString().Should().Be("Test \"Person\"", "a value inside a JSON string is JSON-escaped, never blindly pasted");
        JsonDocument.Parse(post.Body).RootElement.GetProperty("age").GetInt32().Should().Be(42);
        post.ContentType.Should().StartWith("application/json");

        result.Steps[1].Should().Match<PerformanceTrialStep>(s => s.State == TrialStepState.Sent && s.Response!.Status == 200 && s.Request!.Url.EndsWith("/person/123"));
        result.Steps[1].Response!.Preview.Should().Contain("\"name\": \"Test Person\"", "JSON is pretty-printed");
    }

    [Fact]
    public async Task A_missing_output_stops_the_consumer_without_a_placeholder_or_sample_value()
    {
        var create = new HttpPerformanceStep
        {
            Id = "create", Name = "create", Method = "POST", RelativePath = "/person", PostIsSafeRead = true, ExpectedStatusCodes = [201],
            Outputs = [new StepOutput("personId", "$.personId"), new StepOutput("all", "$.items")],
        };
        var read = Get("read", "/person/{id}") with { DataMappings = [new ScenarioDataMapping("id", [], TestDataDataType.SimpleValues, "path") { Source = RuntimeValueSource.PreviousStep, FromStepId = "create", Output = "personId" }] };
        var result = await Trial(Definition(PerformanceTargetType.RestHttp, create, read));

        result.Steps[0].Outputs.Single(o => o.Name == "personId").Should().Match<PerformanceTrialOutput>(o => o.State == OutputSampleState.Missing && o.Problem == "$.personId is not in the response.");
        result.Steps[0].Outputs.Single(o => o.Name == "all").Should().Match<PerformanceTrialOutput>(o => o.State == OutputSampleState.NeedsReview && o.Value == null,
            "a list is not one value — never silently the first element");
        result.Steps[1].State.Should().Be(TrialStepState.Skipped);
        result.Steps[1].Reason.Should().StartWith("Runtime value unavailable for {id}");
        _received.Should().NotContain(r => r.Path.Contains('{') || r.Path.StartsWith("/person/", StringComparison.Ordinal), "the consumer is never sent with a literal placeholder");
    }

    [Fact]
    public async Task Response_formats_have_defined_behaviour()
    {
        var steps = new[] { "/text", "/html", "/empty", "/binary", "/large", "/badjson", "/badxml", "/report", "/client-error", "/server-error" }
            .Select((p, i) => Get($"s{i}", p) with { ExpectedStatusCodes = [200, 204] }).ToArray();
        var r = (await Trial(Definition(PerformanceTargetType.RestHttp, steps))).Steps.ToDictionary(s => steps[s.Index].RelativePath, s => s.Response!);

        r["/text"].Should().Match<PerformanceResponseSample>(x => x.Format == PayloadFormat.Text && x.Preview == "plain ok" && x.Capture == ResponseCaptureState.Available);
        r["/html"].Should().Match<PerformanceResponseSample>(x => x.Format == PayloadFormat.Html && x.Preview!.Contains("<script>"), "HTML is kept as text for escaped display, never rendered");
        r["/empty"].Should().Match<PerformanceResponseSample>(x => x.Status == 204 && x.Format == PayloadFormat.None && x.CaptureNote == "No response body." && x.Preview == null);
        r["/binary"].Should().Match<PerformanceResponseSample>(x => x.Format == PayloadFormat.Binary && x.SizeBytes == 5 && x.Preview == null && x.Capture == ResponseCaptureState.MetadataOnly);
        r["/large"].Should().Match<PerformanceResponseSample>(x => x.Capture == ResponseCaptureState.Truncated && x.PreviewBytes <= PerformanceRequestResponse.ResponsePreviewBytes && x.SizeBytes > 300_000);
        r["/large"].CaptureNote.Should().Be("Displaying 32 KB of 293.8 KB. Too large to parse; shown as text.");
        r["/badjson"].Should().Match<PerformanceResponseSample>(x => x.ParseError == "Invalid JSON response" && x.Preview == "{oops");
        r["/badxml"].Should().Match<PerformanceResponseSample>(x => x.ParseError == "Invalid XML response" && x.Preview == "<a><b></a>");
        r["/report"].Should().Match<PerformanceResponseSample>(x => x.Format == PayloadFormat.Xml && x.Redacted && x.Preview!.Contains("<Id>7</Id>") && x.Preview.Contains("<Password>[REDACTED]</Password>"));
        r["/client-error"].Should().Match<PerformanceResponseSample>(x => x.Status == 400 && x.Format == PayloadFormat.Json && x.Preview!.Contains("Bad request"));
        r["/server-error"].Should().Match<PerformanceResponseSample>(x => x.Status == 500 && x.Format == PayloadFormat.Xml && x.Preview!.Contains("<Code>E1</Code>"));
    }

    [Fact]
    public async Task Xml_post_escapes_values_sends_its_content_type_and_xml_extraction_is_reported_unsupported()
    {
        var post = new HttpPerformanceStep
        {
            Id = "report", Name = "report", Method = "POST", RelativePath = "/report", PostIsSafeRead = true, ExpectedStatusCodes = [202], ContentType = "application/xml",
            BodyTemplate = "<Report><Name>{name}</Name></Report>", DataMappings = [Fixed("name", "A&B <x>", "body")], Outputs = [new StepOutput("receipt", "$.Id")],
        };
        var step = (await Trial(Definition(PerformanceTargetType.RestHttp, post))).Steps.Single();
        var sent = _received.Single(r => r.Method == "POST");
        sent.ContentType.Should().StartWith("application/xml");
        sent.Body.Should().Be("<Report><Name>A&amp;B &lt;x&gt;</Name></Report>", "XML text is escaped, never corrupted by naive replacement");
        step.Response!.Should().Match<PerformanceResponseSample>(x => x.Status == 202 && x.Format == PayloadFormat.Xml && x.Preview!.Contains("urn:r"));
        step.Outputs.Single().Should().Match<PerformanceTrialOutput>(o => o.State == OutputSampleState.Unsupported && o.Problem!.Contains("not supported by the k6 load engine"));
    }

    [Fact]
    public async Task GraphQL_variables_use_runtime_values_and_errors_are_separate_from_the_http_status()
    {
        HttpPerformanceStep Query(string id, string child) => new()
        {
            Id = id, Name = id, Method = "POST", RelativePath = "/graphql", ExpectedStatusCodes = [200],
            GraphQlQuery = "query Child($id: ID!) { child(id: $id) { id } }", GraphQlOperationName = "Child", GraphQlVariablesJson = """{"id":"{childId}"}""",
            DataMappings = [Fixed("childId", child, "variable")],
        };
        var steps = (await Trial(Definition(PerformanceTargetType.GraphQlHttp, Query("ok", "c1"), Query("bad", "fail")))).Steps;
        steps[0].Response!.Should().Match<PerformanceResponseSample>(x => x.Status == 200 && x.Format == PayloadFormat.GraphQl && x.GraphQlErrors == 0 && x.GraphQlData == true);
        steps[1].Response!.Should().Match<PerformanceResponseSample>(x => x.Status == 200 && x.GraphQlErrors == 1 && x.GraphQlData == false && x.GraphQlErrorMessages.Single() == "Child not found");
        steps[0].Request!.Should().Match<PerformanceRequestSample>(r => r.GraphQlQuery!.StartsWith("query Child") && r.GraphQlVariables == """{"id":"c1"}""" && r.Body == null);
        JsonDocument.Parse(_received.First().Body).RootElement.GetProperty("variables").GetProperty("id").GetString().Should().Be("c1");
    }

    [Fact]
    public async Task Samples_are_redacted_and_credentials_or_authenticated_steps_are_never_sent()
    {
        var tokens = Get("tokens", "/tokens");
        var withAuthHeader = Get("auth-header", "/text") with { Headers = [new("Authorization", "Bearer x")] };
        var authenticated = Get("authenticated", "/text") with { Authentication = StepAuthenticationMode.Override, AuthenticationReference = "client-credentials" };
        var steps = (await Trial(Definition(PerformanceTargetType.RestHttp, tokens, withAuthHeader, authenticated))).Steps;

        steps[0].Response!.Should().Match<PerformanceResponseSample>(x => x.Redacted && x.Capture == ResponseCaptureState.Redacted && !x.Preview!.Contains("secret-abc") && !x.Preview.Contains("p4ss"));
        steps[1].State.Should().Be(TrialStepState.Blocked);
        steps[2].Should().Match<PerformanceTrialStep>(s => s.State == TrialStepState.Blocked && s.Reason!.Contains("does not issue credentials"));
        _received.Should().ContainSingle("only the unauthenticated step was sent").Which.Authorization.Should().BeNull();
    }

    [Fact]
    public async Task Query_values_are_encoded_and_unsafe_environments_send_nothing()
    {
        var search = Get("search", "/text") with { QueryParameters = [new("q", "{q}")], DataMappings = [Fixed("q", "a b&c", "query")] };
        (await Trial(Definition(PerformanceTargetType.RestHttp, search))).Steps.Single().State.Should().Be(TrialStepState.Sent);
        _received.Single().Query.Should().Be("?q=a%20b%26c");

        var production = Definition(PerformanceTargetType.RestHttp, search) with { EnvironmentType = "Production" };
        (await Trial(production)).Message.Should().Be("Production environments cannot be used for performance tests.");
        _received.Should().HaveCount(1);
    }

    // ── Shared rules: preview, body validation, outputs ────────────────────────────────────────────────────────────────

    [Fact]
    public void Preview_is_structured_shows_scenario_values_and_keeps_test_data_as_placeholders()
    {
        var step = new HttpPerformanceStep
        {
            Id = "p", Name = "p", Method = "POST", RelativePath = "/person/{id}", PostIsSafeRead = true, ContentType = "application/json", BodyTemplate = """{"child":"{childId}"}""",
            QueryParameters = [new("mode", "{mode}")], Headers = [new("X-Trace", "t1")],
            DataMappings = [Fixed("id", "12345"), new ScenarioDataMapping("childId", ["child-a"], TestDataDataType.SimpleValues, "body"), Fixed("mode", "full", "query")],
        };
        var pv = PerformanceRequestResponse.Preview(Definition(PerformanceTargetType.RestHttp, step), 0);
        pv.Url.Should().EndWith("/person/12345?mode=full");
        pv.Body.Should().Be("""{"child":"{childId}"}""", "Test Data values are never shown");
        pv.Masked.Should().Equal("childId");
        pv.Headers.Select(h => $"{h.Name}: {h.Value}").Should().Equal("Content-Type: application/json", "X-Trace: t1");
        pv.Format.Should().Be(PayloadFormat.Json);
    }

    [Fact]
    public void Body_and_output_rules_block_what_cannot_execute_safely()
    {
        HttpPerformanceStep Body(string type, string body) => new() { Method = "POST", PostIsSafeRead = true, RelativePath = "/x", ContentType = type, BodyTemplate = body };
        PerformanceRequestResponse.BodyProblem(Body("application/json", """{"a":{v},"b":"{w}"}"""), false).Should().BeNull();
        PerformanceRequestResponse.BodyProblem(Body("application/json", """{"a":"""), false).Should().Be("the request body is not valid JSON for application/json");
        PerformanceRequestResponse.BodyProblem(Body("application/xml", "<a>{v}</a>"), false).Should().BeNull();
        PerformanceRequestResponse.BodyProblem(Body("application/xml", "<a><b></a>"), false).Should().Be("the request body is not well-formed XML for application/xml");
        PerformanceRequestResponse.BodyProblem(Body("application/xml", "<!DOCTYPE a [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><a>&x;</a>"), false).Should().NotBeNull("DTDs are prohibited");
        PerformanceRequestResponse.BodyProblem(Body("application/json", "{payload}"), false).Should().BeNull("a whole-payload placeholder");

        PerformanceRequestResponse.OutputProblem(new StepOutput("personId", "$.id")).Should().BeNull();
        PerformanceRequestResponse.OutputProblem(new StepOutput("loc", "Location") { From = StepOutputSource.Header }).Should().BeNull();
        PerformanceRequestResponse.OutputProblem(new StepOutput("token", "$.access_token")).Should().Contain("credential");
        PerformanceRequestResponse.OutputProblem(new StepOutput("auth", "$.data.access_token")).Should().Contain("credential");
        PerformanceRequestResponse.OutputProblem(new StepOutput("c", "Set-Cookie") { From = StepOutputSource.Header }).Should().Contain("credential");
        PerformanceRequestResponse.OutputProblem(new StepOutput("x", "/Person/Id/text()")).Should().Be("output 'x' needs a JSON path such as $.id");

        var bad = Definition(PerformanceTargetType.RestHttp, Body("application/json", "{oops") with { Name = "b", Outputs = [new StepOutput("refresh", "$.refresh_token")] });
        var issues = PerformanceTestSafety.Check(bad, null, new PerformanceTestOptions());
        issues.Should().Contain(i => i.Key == "body" && i.State == PerformanceReadinessState.InvalidScenario);
        issues.Should().Contain(i => i.Key == "output" && i.Message.Contains("credential"));
        PerformanceScenarioRuntime.Readiness(bad, 0).Status.Should().Be(StepStatus.Blocked);
    }

    // ── k6 runtime: the same rules under load ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void K6_script_encodes_per_position_skips_unresolved_requests_and_scopes_outputs_per_iteration()
    {
        var create = new HttpPerformanceStep
        {
            Id = "create", Name = "create", Method = "POST", RelativePath = "/person", PostIsSafeRead = true, ContentType = "application/xml", BodyTemplate = "<P>{name}</P>",
            DataMappings = [Fixed("name", "a", "body")], Outputs = [new StepOutput("personId", "$.id"), new StepOutput("loc", "Location") { From = StepOutputSource.Header }],
        };
        var script = K6ScriptGenerator.Generate(Definition(PerformanceTargetType.RestHttp, create), null, "/out.json");
        var json = script[(script.IndexOf("const STEPS = ", StringComparison.Ordinal) + 14)..script.IndexOf(";\nconst ROWS", StringComparison.Ordinal)];
        var step = JsonDocument.Parse(json).RootElement[0];
        step.GetProperty("bodyMode").GetString().Should().Be("xml");
        step.GetProperty("outputs")[1].EnumerateArray().Select(e => e.GetString()).Should().Equal("loc", "Location", "header");
        script.Should().Contain("if (miss.length > 0) { UNRESOLVED.add(1, { name: s.name }); continue; }", "an unresolved value is a counted failure, not a request with a literal placeholder");
        script.Should().Contain("if (v !== undefined && v !== null && typeof v === 'object') v = undefined;", "a list or object is not one value");
        script.Should().Contain("export default function () {\n  const r = row();\n  const ctx = {};", "outputs live in the iteration of one VU only");
        script.Should().Contain("discardResponseBodies: true", "load runs never retain response bodies");
        script.Should().Contain("const GRAPHQL = false;").And.Contain("birknext_unresolved_value");
        K6ScriptGenerator.Generate(Definition(PerformanceTargetType.GraphQlHttp, Get("g", "/graphql") with { Method = "POST", GraphQlQuery = "query Q { a }" }), null, "/o.json")
            .Should().Contain("const GRAPHQL = true;").And.Contain("GRAPHQL_ERRORS.add(1, { name: s.name })");
    }

    [Fact]
    public void Summary_reports_graphql_errors_unresolved_values_and_missing_extractions_per_step()
    {
        var summary = """
            {"metrics":{
              "http_reqs":{"values":{"count":10,"rate":1}},"http_req_duration":{"values":{"avg":10,"p(95)":20}},"http_req_failed":{"values":{"rate":0,"passes":0,"fails":10}},
              "http_reqs{name:a}":{"values":{"count":10,"rate":1}},"http_req_duration{name:a}":{"values":{"p(95)":20}},"http_req_failed{name:a}":{"values":{"rate":0}},
              "birknext_graphql_errors":{"values":{"count":3}},"birknext_graphql_errors{name:a}":{"values":{"count":3}},
              "birknext_unresolved_value{name:a}":{"values":{"count":0}},"birknext_extraction_missing":{"values":{"count":2}},"birknext_extraction_missing{name:a}":{"values":{"count":2}}
            },"state":{"testRunDurationMs":1000}}
            """;
        var (metrics, error) = K6SummaryParser.Parse(summary, ["a"]);
        error.Should().BeNull();
        metrics!.Steps.Single().Should().Match<PerformanceStepMetrics>(s => s.GraphQlErrors == 3 && s.ExtractionMissing == 2 && s.Unresolved == null);
        (metrics.GraphQlErrors, metrics.ExtractionMissing, metrics.Unresolved).Should().Be((3L, 2L, (long?)null));
        JsonSerializer.Serialize(new PerformanceStepMetrics("a", 1, 0, 1, 1, 1)).Should().NotContain("GraphQlErrors", "clean runs and older runs serialize unchanged");
    }
}
