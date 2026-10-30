using BirkNext.PerformanceTests;
using BirkNext.TestData;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// A step reads REQUEST → RESPONSE: a structured, redacted request preview (endpoint, query, headers, body, authentication kept apart), a response
/// card with the expected status, extraction (JSON path or header; engine capabilities stated) and, after "Send validation request", a bounded
/// sample with the values each output extracts. Results show correlation and GraphQL failures of a load run without any response body.
/// </summary>
public sealed class PerformanceRequestResponseUiTests : BunitContext
{
    private PerformanceTestDefinition _d = new();
    private PerformanceTestDefinition? _trialSent;

    public PerformanceRequestResponseUiTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static ScenarioDataMapping Fixed(string variable, string value, string place) => new(variable, [], TestDataDataType.SimpleValues, place) { Source = RuntimeValueSource.Fixed, FixedValue = value };

    private static readonly HttpPerformanceStep Create = new()
    {
        Id = "create", Name = "create-person", Method = "POST", RelativePath = "/person", PostIsSafeRead = true, ExpectedStatusCodes = [201], ContentType = "application/json",
        BodyTemplate = """{"name":"{name}","child":"{childId}"}""", Headers = [new("X-Trace", "t-1")],
        DataMappings = [Fixed("name", "Test Person", "body"), new ScenarioDataMapping("childId", ["child-a"], TestDataDataType.SimpleValues, "body")],
        Outputs = [new StepOutput("personId", "$.id")],
    };

    private static readonly HttpPerformanceStep Read = new()
    {
        Id = "read", Name = "read-person", Method = "GET", RelativePath = "/person/{id}", ExpectedStatusCodes = [200],
        DataMappings = [new ScenarioDataMapping("id", [], TestDataDataType.SimpleValues, "path") { Source = RuntimeValueSource.PreviousStep, FromStepId = "create", Output = "personId" }],
    };

    private static PerformanceTrialResult Trial => new()
    {
        At = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero),
        Steps =
        [
            new PerformanceTrialStep
            {
                StepId = "create", Index = 0, Name = "create-person", State = TrialStepState.Sent, ExpectedStatus = true,
                Request = new PerformanceRequestSample { Method = "POST", Url = "https://app.example.test/person", Body = """{"name":"Test Person","child":"{childId}"}""", Masked = ["childId"] },
                Response = new PerformanceResponseSample
                {
                    Status = 201, Reason = "Created", ContentType = "application/json", Format = PayloadFormat.Json, DurationMs = 142, SizeBytes = 1843, Capture = ResponseCaptureState.Available,
                    Preview = "{\n  \"id\": 12345\n}", Headers = [new("Location", "/person/12345")],
                },
                Outputs = [new PerformanceTrialOutput("personId", StepOutputSource.Body, "$.id", OutputSampleState.Ready, "12345", null)],
            },
            new PerformanceTrialStep { StepId = "read", Index = 1, Name = "read-person", State = TrialStepState.Skipped, Reason = "Runtime value unavailable for {id}." },
        ],
    };

    private IRenderedComponent<PerformanceScenarioSteps> Render(PerformanceTargetType type = PerformanceTargetType.RestHttp, params HttpPerformanceStep[] steps)
    {
        _d = new PerformanceTestDefinition
        {
            Id = "def-1", Name = "Person", TargetOrigin = "https://app.example.test", EnvironmentName = "Development", TargetType = type,
            Scenario = new PerformanceScenario { Name = "Person flow", Steps = [.. steps] },
        };
        IRenderedComponent<PerformanceScenarioSteps>? cut = null;
        cut = Render<PerformanceScenarioSteps>(p => p.Add(x => x.Definition, _d)
            .Add(x => x.OnTrial, (Func<PerformanceTestDefinition, Task<PerformanceApiResult<PerformanceTrialResult>>>)(d => { _trialSent = d; return Task.FromResult(PerformanceApiResult<PerformanceTrialResult>.Ok(Trial)); }))
            .Add(x => x.OnChange, (PerformanceTestDefinition next) => { _d = next; cut?.Render(q => q.Add(x => x.Definition, next)); }));
        return cut;
    }

    private static AngleSharp.Dom.IElement Step(IRenderedComponent<PerformanceScenarioSteps> cut, string id) => cut.Find($"[data-testid=pt-step][data-step-id={id}]");

    [Fact]
    public void Request_card_is_structured_with_badges_and_keeps_test_data_and_authentication_apart()
    {
        var cut = Render(steps: [Create, Read]);
        var create = Step(cut, "create");
        create.QuerySelector("[data-testid=pt-request-summary]")!.TextContent.Should().Contain("POST").And.Contain("JSON").And.Contain("2 runtime values");
        create.QuerySelector("[data-testid=pt-request-url]")!.TextContent.Should().Be("POST https://app.example.test/person");
        create.QuerySelector("[data-testid=pt-request-headers]")!.TextContent.Should().Be("Content-Type: application/json · X-Trace: t-1");
        create.QuerySelector("[data-testid=pt-request-auth]")!.TextContent.Should().Contain("Authentication").And.Contain("never a request header or runtime value");
        create.QuerySelector("[data-testid=pt-request-body]")!.TextContent.Should().Contain("\"name\": \"Test Person\"").And.Contain("\"child\": \"{childId}\"", "Test Data values are never shown");
        create.QuerySelector("[data-testid=pt-request-masked]")!.TextContent.Should().Be("{childId}");
        create.QuerySelector("[data-testid=pt-request-body]")!.GetAttribute("tabindex").Should().Be("0");
        Step(cut, "read").QuerySelector("[data-testid=pt-request-url]")!.TextContent.Should().Be("GET https://app.example.test/person/{id}");
        create.QuerySelector("[data-testid=pt-response-expected]")!.TextContent.Should().Be("Expected status 201");
        create.QuerySelector("[data-testid=pt-response-none]")!.TextContent.Should().Contain("load runs keep metrics and status only");
    }

    [Fact]
    public void Validation_request_shows_the_response_and_what_each_output_extracts()
    {
        var cut = Render(steps: [Create, Read]);
        cut.Find("[data-testid=pss-trial-send]").Click();
        _trialSent!.Scenario.Steps.Should().HaveCount(2);
        var create = Step(cut, "create");
        create.QuerySelector("[data-testid=pt-response-summary]")!.TextContent.Should().Contain("201 Created").And.Contain("JSON").And.Contain("142 ms").And.Contain("1.8 KB").And.Contain("Sample retained");
        create.QuerySelector("[data-testid=pt-response-preview]")!.TextContent.Should().Contain("\"id\": 12345");
        var output = create.QuerySelector("[data-testid=pt-trial-output]")!;
        (output.GetAttribute("data-state"), output.TextContent).Should().Match<(string?, string)>(o => o.Item1 == "Ready" && o.Item2.Contains("$.id") && o.Item2.Contains("12345"));
        create.QuerySelector("[data-testid=pt-trial-request]")!.TextContent.Should().Contain("Test Data values hidden: {childId}");
        Step(cut, "read").QuerySelector("[data-testid=pt-trial-reason]")!.TextContent.Should().Be("Not sent · Runtime value unavailable for {id}.");
        cut.Find("[data-testid=pt-step-message]").TextContent.Should().Be("Validation request: 1 step(s) sent, 1 not sent.");
        _d.Scenario.Steps[0].DataMappings!.Should().NotContain(m => m.FixedValue == "12345", "a sample value is never saved as a runtime value");
    }

    [Fact]
    public void GraphQL_response_keeps_http_status_and_graphql_errors_apart()
    {
        var gql = new HttpPerformanceStep { Id = "g", Name = "child", Method = "POST", RelativePath = "/graphql", ExpectedStatusCodes = [200], GraphQlQuery = "query Q { child { id } }", GraphQlVariablesJson = """{"id":"{childId}"}""" };
        var cut = Render(PerformanceTargetType.GraphQlHttp, gql);
        cut.Find("[data-testid=pt-request-graphql]").TextContent.Should().Be("query Q { child { id } }");
        cut.Find("[data-testid=pt-request-variables]").TextContent.Should().Contain("\"id\": \"{childId}\"");
        cut.FindAll("[data-testid=pt-request-body]").Should().BeEmpty("query and variables are shown structurally, not as one JSON body");
    }

    [Fact]
    public void Outputs_read_a_json_path_or_a_header_state_engine_limits_and_block_credentials()
    {
        var cut = Render(steps: [Create]);
        var create = Step(cut, "create");
        create.QuerySelector("[data-testid=pt-output-capabilities]")!.TextContent.Should().Contain("JSON body path · Supported").And.Contain("Response header · Supported").And.Contain("XML / XPath · Unavailable");
        cut.Find("[data-testid=pt-output-name]").Input("location");
        cut.Find("[data-testid=pt-output-from]").Change("Header");
        cut.Find("[data-testid=pt-output-path]").GetAttribute("value").Should().Be("Location");
        cut.Find("[data-testid=pt-output-add]").Click();
        _d.Scenario.Steps[0].Outputs!.Should().ContainSingle(o => o.Name == "location" && o.From == StepOutputSource.Header && o.Path == "Location");

        cut.Find("[data-testid=pt-output-name]").Input("token");
        cut.Find("[data-testid=pt-output-path]").Input("$.access_token");
        cut.Find("[data-testid=pt-output-error]").TextContent.Should().Contain("credential");
        cut.Find("[data-testid=pt-output-add]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Request_editor_offers_content_types_query_and_headers_but_never_an_authorization_header()
    {
        var cut = Render(steps: [Create]);
        cut.Find("[data-testid=pt-step-edit]").Click();
        cut.FindAll("[data-testid=pt-step-content-type] option").Select(o => o.GetAttribute("value")).Should().Equal(
            "application/json", "application/xml", "text/xml", "text/plain", "application/x-www-form-urlencoded");
        cut.Find("[data-testid=pt-step-content-type]").Change("application/xml");
        _d.Scenario.Steps[0].ContentType.Should().Be("application/xml");
        cut.Find("[data-testid=pt-step-body-error]").TextContent.Should().Be("the request body is not well-formed XML for application/xml");

        cut.Find("[data-testid=pt-step-header-name]").Input("Authorization");
        cut.Find("[data-testid=pt-step-header-error]").TextContent.Should().Contain("authentication adds it");
        cut.Find("[data-testid=pt-step-header-add]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=pt-step-query-name]").Input("page");
        cut.Find("[data-testid=pt-step-query-value]").Input("{page}");
        cut.Find("[data-testid=pt-step-query-add]").Click();
        _d.Scenario.Steps[0].QueryParameters.Should().ContainSingle(q => q.Name == "page" && q.Value == "{page}");
    }

    [Fact]
    public void Results_show_correlation_and_graphql_failures_without_any_response_body()
    {
        var run = new PerformanceTestRun
        {
            RunId = Guid.NewGuid(), DefinitionId = "def-1", DefinitionVersion = 1, State = PerformanceRunState.Completed, Verdict = PerformanceQualityVerdict.Fail,
            DefinitionSnapshot = new PerformanceTestDefinition { Id = "def-1", Name = "P", Scenario = new PerformanceScenario { Steps = [Create, Read] } },
            Metrics = new PerformanceMetrics
            {
                RequestCount = 100, SuccessfulRequests = 100, Unresolved = 40, ExtractionMissing = 40, GraphQlErrors = null,
                Steps = [new PerformanceStepMetrics("create-person", 100, 0, 10, 20, 5) { ExtractionMissing = 40 }, new PerformanceStepMetrics("read-person", 60, 0, 10, 20, 3) { Unresolved = 40 }],
            },
        };
        var cut = Render<PerformanceRunResults>(p => p.Add(x => x.Run, run).Add(x => x.Current, run.DefinitionSnapshot));
        cut.Find("[data-testid=pt-result-correlation]").TextContent.Should().Be("40 step execution(s) not sent — a runtime value did not resolve · 40 response(s) without an expected output");
        cut.Find("[data-testid=pt-result-response-state]").TextContent.Should().Contain("Response body not retained").And.Contain("Capture disabled for load runs");
        cut.Find("[data-testid=pt-result-op-unresolved]").TextContent.Should().StartWith("40");
    }
}
