using BirkNext.ExecutionEvidence;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>Execution Results: run history, run KPIs, filters kept in the URL, call evidence tabs, GraphQL errors, blocked calls, attempt comparison. Reading never executes.</summary>
public sealed class ExecutionResultsPageTests : BunitContext
{
    private static readonly Guid Run = Guid.NewGuid();
    private static readonly Guid Ok = Guid.NewGuid(), Gql = Guid.NewGuid(), Blocked = Guid.NewGuid();

    private sealed class Api : IExecutionEvidenceApiService
    {
        public List<string> Queries { get; } = [];
        public int Starts { get; private set; }
        public List<ExecutionCallRecord> Calls { get; } =
        [
            new() { CallId = Ok, Sequence = 1, State = ExecutionState.Passed, Definition = new() { Name = "get child", Method = "GET", Path = "/children/1", TestDataRefs = ["child-age@v2"] },
                Attempts =
                [
                    new() { AttemptNumber = 1, Transport = TransportResult.ResponseReceived, RetryReason = null,
                        Request = new() { Method = "GET", Url = "http://localhost:5/children/1", Headers = [new("Authorization", "••••••", true), new("Accept", "application/json")] },
                        Response = new() { StatusCode = 503, DurationMs = 4, Body = new() { ContentType = "application/json", ByteCount = 2, Text = "{}", Completeness = CaptureCompleteness.ExactRedacted } } },
                    new() { AttemptNumber = 2, Transport = TransportResult.ResponseReceived, RetryReason = "Retry after HTTP 503",
                        Request = new() { Method = "GET", Url = "http://localhost:5/children/1", Headers = [new("Authorization", "••••••", true)] },
                        Response = new() { StatusCode = 200, DurationMs = 7, Body = new() { ContentType = "application/json", ByteCount = 30, Text = """{"name":"Kari","password":"••••••"}""",
                            Completeness = CaptureCompleteness.ExactRedacted, RedactedFields = 1, Sha256 = "abc" } } },
                ],
                Assertions = [new("s", AssertionKind.StatusCode, AssertionOperator.Equals, null, "200", "200", AssertionOutcome.Passed, "HTTP status: expected 200, actual 200.")] },
            new() { CallId = Gql, Sequence = 2, State = ExecutionState.Failed, Definition = new() { Name = "gql partial", Protocol = ExecutionProtocol.GraphQl, Path = "/graphql", GraphQlQuery = "query p { a }" },
                Attempts = [new() { AttemptNumber = 1, Transport = TransportResult.ResponseReceived, Request = new() { Method = "POST", Url = "http://localhost:5/graphql", GraphQlOperationType = "query" },
                    Response = new() { StatusCode = 200, GraphQlHasData = true, GraphQlErrorCount = 1, GraphQlErrorMessages = ["School unavailable"], GraphQlErrorPaths = ["school"] } }],
                Assertions = [new("ok", AssertionKind.StatusCode, AssertionOperator.Equals, null, "200", "200", AssertionOutcome.Passed, "ok"),
                              new("e", AssertionKind.GraphQlErrorCount, AssertionOperator.Equals, null, "0", "1", AssertionOutcome.Failed, "GraphQL error count: expected 0, actual 1.")] },
            new() { CallId = Blocked, Sequence = 3, State = ExecutionState.Blocked, Reason = "Mutating call not confirmed.", Definition = new() { Name = "create", Method = "POST", Path = "/children" } },
        ];
        private ExecutionRunSummary Summary => new()
        {
            RunId = Run, Name = "Synthetic run", EnvironmentId = "local", EnvironmentType = "Local", TargetOrigin = "http://localhost:5/", State = ExecutionState.Failed,
            StartedAt = DateTimeOffset.UtcNow, Counts = new(3, 2, 1, 1, 0, 0, 1, 0, 0, 0, 0, 3), CallsWithCompleteEvidence = 3, WireTransmissions = 5, RedactionVersion = "ee-redaction-v1",
        };
        public Task<(IReadOnlyList<ExecutionRunSummary>? Runs, string? Error)> RunsAsync(CancellationToken ct = default) => Task.FromResult<(IReadOnlyList<ExecutionRunSummary>?, string?)>(([Summary], null));
        public Task<(ExecutionRunDetail? Detail, string? Error)> RunAsync(Guid runId, int skip, int take, string? state, string? protocol, string? method, string? search, string sort, CancellationToken ct = default)
        {
            Queries.Add($"{state}|{protocol}|{method}|{search}|{sort}|{skip}");
            var calls = Calls.Where(c => state is null || c.State.ToString() == state).ToList();
            return Task.FromResult<(ExecutionRunDetail?, string?)>((new(Summary, calls, calls.Count, skip, take), null));
        }
        public Task<(ExecutionCallRecord? Call, string? Error)> CallAsync(Guid runId, Guid callId, CancellationToken ct = default) =>
            Task.FromResult<(ExecutionCallRecord?, string?)>((Calls.Single(c => c.CallId == callId), null));
        public Task<(AttemptComparison? Comparison, string? Error)> CompareAsync(Guid runA, Guid callA, int attemptA, Guid runB, Guid callB, int attemptB, CancellationToken ct = default) =>
            Task.FromResult<(AttemptComparison?, string?)>((new(runA, callA, attemptA, runB, callB, attemptB, ["HTTP status: 503 → 200"]), null));
        public Task<(ExecutionRunStartResult? Result, string? Error)> StartAsync(ExecutionRunRequest request, CancellationToken ct = default) { Starts++; return Task.FromResult<(ExecutionRunStartResult?, string?)>((null, null)); }
        public string ExportUrl(Guid runId) => $"http://localhost/api/execution-evidence/runs/{runId}/export";
    }

    private readonly Api _api = new();
    public ExecutionResultsPageTests() => Services.AddSingleton<IExecutionEvidenceApiService>(_api);

    private IRenderedComponent<ExecutionResults> Open(string query)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("execution-results" + query);
        var page = Render<ExecutionResults>();
        page.WaitForAssertion(() => page.Find("[data-testid=xr-page]").GetAttribute("aria-busy").Should().Be("false"));
        return page;
    }

    [Fact]
    public void History_lists_saved_runs_and_opening_one_shows_its_counts_without_executing()
    {
        var page = Open("");
        page.Find("[data-testid=xr-run-row]").GetAttribute("data-state").Should().Be("Failed");
        page = Open($"?run={Run}");
        page.Find("[data-testid=xr-kpi-planned] .xr-kpi-value").TextContent.Should().Be("3");
        page.Find("[data-testid=xr-kpi-attempts] .xr-kpi-value").TextContent.Should().Be("3");
        page.Find("[data-testid=xr-kpi-attempts]").TextContent.Should().Contain("5 sent on the wire", "stack-level resends are visible, not hidden behind the attempt count");
        page.FindAll("[data-testid=xr-call-row]").Should().HaveCount(3);
        page.FindAll("[data-testid=xr-call-row]")[2].QuerySelector("[data-testid=xr-call-http]")!.TextContent.Should().Be("not sent");
        page.Find("[data-testid=xr-export]").GetAttribute("href").Should().EndWith($"/runs/{Run}/export");
        _api.Starts.Should().Be(0, "opening results never starts a run");
    }

    [Fact]
    public void Filters_live_in_the_url_and_back_to_calls_keeps_them()
    {
        var page = Open($"?run={Run}&state=Failed&sort=duration");
        _api.Queries.Last().Should().Be("Failed||||duration|0");
        page.FindAll("[data-testid=xr-call-row]").Should().ContainSingle();
        page.Find("[data-testid=xr-call-open]").GetAttribute("href").Should().Contain("state=Failed").And.Contain("sort=duration").And.Contain($"call={Gql}");
        page = Open($"?run={Run}&state=Failed&sort=duration&call={Gql}");
        page.Find("[data-testid=xr-back-calls]").GetAttribute("href").Should().Contain("state=Failed").And.Contain("sort=duration").And.NotContain("call=");
    }

    [Fact]
    public void A_call_shows_the_request_sent_the_response_received_redaction_and_failed_assertions_first()
    {
        var page = Open($"?run={Run}&call={Ok}");
        page.Find("[data-testid=xr-request-line]").TextContent.Should().Contain("GET").And.Contain("/children/1");
        page.FindAll("[data-testid=xr-redacted]").Should().NotBeEmpty();
        page = Open($"?run={Run}&call={Ok}&tab=response");
        page.Find("[data-testid=xr-response-status]").TextContent.Should().Contain("HTTP 200");
        page.Find("[data-testid=xr-response-body-meta]").TextContent.Should().Contain("1 value(s) redacted");
        page.Find("[data-testid=xr-response-body]").TextContent.Should().Contain("\"name\": \"Kari\"", "JSON is indented for reading");
        page = Open($"?run={Run}&call={Gql}&tab=response");
        page.Find("[data-testid=xr-graphql]").TextContent.Should().Contain("1 error(s)").And.Contain("partial result");
        page.Find("[data-testid=xr-graphql-errors]").TextContent.Should().Contain("School unavailable").And.Contain("path school");
        page = Open($"?run={Run}&call={Gql}&tab=assertions");
        page.FindAll("[data-testid=xr-assertion]")[0].GetAttribute("data-outcome").Should().Be("Failed");
    }

    [Fact]
    public void Attempts_show_retries_and_can_be_compared_and_blocked_calls_say_they_were_never_sent()
    {
        var page = Open($"?run={Run}&call={Ok}&tab=attempts");
        page.FindAll("[data-testid=xr-attempt]").Should().HaveCount(2);
        page.FindAll("[data-testid=xr-attempt]")[1].TextContent.Should().Contain("Retry after HTTP 503");
        page.FindAll("[data-testid=xr-compare-pick]")[0].Change(true);
        page.FindAll("[data-testid=xr-compare-pick]")[1].Change(true);   // re-found after the re-render
        page.Find("[data-testid=xr-compare]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=xr-comparison]").TextContent.Should().Contain("503 → 200"));
        page = Open($"?run={Run}&call={Ok}&tab=evidence");
        page.Find("[data-testid=xr-test-data]").TextContent.Should().Be("child-age@v2");
        page = Open($"?run={Run}&call={Blocked}");
        page.Find("[data-testid=xr-not-sent]").TextContent.Should().Contain("blocked before any request");
    }
}
