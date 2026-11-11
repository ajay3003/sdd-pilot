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
                Assertions = [new("s", AssertionKind.StatusCode, AssertionOperator.Equals, null, "200", "200", AssertionOutcome.Passed, "HTTP status: expected 200, actual 200.")],
                ContractValidation = [new("Request", ContractValidationState.Valid, "children-api@v1", "#/paths/~1children~1{id}/get", "The request matches the contract (parameters and body).", [], []),
                    new("Response", ContractValidationState.Invalid, "children-api@v1", "#/paths/~1children~1{id}/get/responses/200/content/application~1json/schema", "1 contract violation(s) in the response (response 200).",
                        [new("Type", "$.age", "#/components/schemas/Child/properties/age/type", "type", "Expected integer, got string.")], ["discriminator"])] },
            new() { CallId = Gql, Sequence = 2, State = ExecutionState.Failed, Definition = new() { Name = "gql partial", Protocol = ExecutionProtocol.GraphQl, Path = "/graphql", GraphQlQuery = "query p { a }" },
                Attempts = [new() { AttemptNumber = 1, Transport = TransportResult.ResponseReceived, Request = new() { Method = "POST", Url = "http://localhost:5/graphql", GraphQlOperationType = "query" },
                    Response = new() { StatusCode = 200, GraphQlHasData = true, GraphQlErrorCount = 1, GraphQlErrorMessages = ["School unavailable"], GraphQlErrorPaths = ["school"] } }],
                Assertions = [new("ok", AssertionKind.StatusCode, AssertionOperator.Equals, null, "200", "200", AssertionOutcome.Passed, "ok"),
                              new("e", AssertionKind.GraphQlErrorCount, AssertionOperator.Equals, null, "0", "1", AssertionOutcome.Failed, "GraphQL error count: expected 0, actual 1.")] },
            new() { CallId = Blocked, Sequence = 3, State = ExecutionState.Blocked, Reason = "Mutating call not confirmed.", Definition = new() { Name = "create", Method = "POST", Path = "/children" } },
        ];
        public bool RunProtected { get; set; }
        public ExecutionRunFilter? LastFilter { get; private set; }
        public List<(Guid Run, bool Protected)> Protections { get; } = [];
        public List<ExecutionDeletionRequest> Deletions { get; } = [];
        public List<IReadOnlyList<Guid>> Exports { get; } = [];
        public List<(Guid Run, Guid Call)> BodyRemovals { get; } = [];
        public List<ExecutionRetentionSettings> RetentionSaves { get; } = [];
        public List<int> RetentionApplies { get; } = [];
        private ExecutionRetentionSettings _retention = new(RetentionMode.Disabled, null);
        public Task<(ExecutionStorageUsage? Usage, string? Error)> StorageAsync(string? environmentId = null, CancellationToken ct = default) =>
            Task.FromResult<(ExecutionStorageUsage?, string?)>((new(2, 5, 6, 48_000, 262_144, RunProtected ? 1 : 0, DateTimeOffset.UtcNow.AddDays(-40), DateTimeOffset.UtcNow, 1, 20_000,
                "PostgreSQL (BirkNext database): execution_runs and execution_calls.", "Stored bytes are measured. No evidence files exist."), null));
        public Task<(ExecutionIntegrityReport? Report, string? Error)> IntegrityAsync(Guid runId, CancellationToken ct = default) =>
            Task.FromResult<(ExecutionIntegrityReport?, string?)>((new(runId, 3, 4, 0, []), null));
        public Task<string?> ProtectAsync(Guid runId, bool isProtected, CancellationToken ct = default) { Protections.Add((runId, isProtected)); RunProtected = isProtected; return Task.FromResult<string?>(null); }
        public Task<(ExecutionDeletionResult? Result, string? Error)> DeleteRunsAsync(ExecutionDeletionRequest request, CancellationToken ct = default)
        {
            Deletions.Add(request);
            return Task.FromResult<(ExecutionDeletionResult?, string?)>((new(request.AllEligible ? 1 : request.RunIds.Count - (RunProtected ? 1 : 0), 3, 4_000,
                RunProtected ? [new(Run, "Protected: remove the protection first.")] : [], "Deleted 1 run(s) with 3 call(s) (4,000 bytes of stored evidence)."), null));
        }
        public Task<(ExecutionRetentionPreview? Preview, string? Error)> DeletionPreviewAsync(string environmentId, CancellationToken ct = default) =>
            Task.FromResult<(ExecutionRetentionPreview?, string?)>((new(_retention, null, 1, 3, 4_000, RunProtected ? 1 : 0, 0, [Run], $"Every finished, unprotected run of {environmentId}."), null));
        public Task<(int Removed, string? Error)> RemoveBodiesAsync(Guid runId, Guid callId, CancellationToken ct = default) { BodyRemovals.Add((runId, callId)); return Task.FromResult((2, (string?)null)); }
        public Task<(ExecutionRetentionSettings? Settings, string? Error)> RetentionAsync(CancellationToken ct = default) => Task.FromResult<(ExecutionRetentionSettings?, string?)>((_retention, null));
        public Task<(ExecutionRetentionSettings? Settings, string? Error)> SaveRetentionAsync(ExecutionRetentionSettings settings, CancellationToken ct = default)
        {
            RetentionSaves.Add(settings);
            _retention = settings with { UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "Local session" };
            return Task.FromResult<(ExecutionRetentionSettings?, string?)>((_retention, null));
        }
        public Task<(ExecutionRetentionPreview? Preview, string? Error)> RetentionPreviewAsync(CancellationToken ct = default) =>
            Task.FromResult<(ExecutionRetentionPreview?, string?)>((new(_retention, DateTimeOffset.UtcNow.AddDays(-30), 1, 3, 20_000, 1, 0, [Guid.NewGuid()], "Runs started before the cutoff that are finished and not protected. Nothing is deleted until you apply it."), null));
        public Task<(ExecutionDeletionResult? Result, string? Error)> ApplyRetentionAsync(int confirm, CancellationToken ct = default)
        {
            RetentionApplies.Add(confirm);
            return Task.FromResult<(ExecutionDeletionResult?, string?)>((new(1, 3, 20_000, [], "Deleted 1 run(s) with 3 call(s) (20,000 bytes of stored evidence)."), null));
        }
        public Task<(IReadOnlyList<ExecutionAuditEntry>? Entries, string? Error)> AuditAsync(CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<ExecutionAuditEntry>?, string?)>(([new(Guid.NewGuid(), DateTimeOffset.UtcNow, "Exported", "run x", 1, 3, 4_000, "Local session", null)], null));
        public Task<(byte[]? Zip, string? Error)> ExportAsync(IReadOnlyList<Guid> runIds, CancellationToken ct = default) { Exports.Add(runIds); return Task.FromResult<(byte[]?, string?)>(([80, 75, 3, 4], null)); }
        private ExecutionRunSummary Summary => new()
        {
            RunId = Run, Name = "Synthetic run", EnvironmentId = "local", EnvironmentType = "Local", TargetOrigin = "http://localhost:5/", State = ExecutionState.Failed,
            StartedAt = DateTimeOffset.UtcNow, Counts = new(3, 2, 1, 1, 0, 0, 1, 0, 0, 0, 0, 3), CallsWithCompleteEvidence = 3, WireTransmissions = 5, RedactionVersion = "ee-redaction-v1",
            Protected = RunProtected, Definitions = 2, DatasetRows = 1, AuthenticationSessions = ["attended-session:local#g1", "attended-session:local#g2"], ReauthenticationWaitSeconds = 120,
            AuthenticationMode = "Attended: the tester's own session (Local HTTPS proxy)",
        };
        public Task<(IReadOnlyList<ExecutionRunSummary>? Runs, string? Error)> RunsAsync(ExecutionRunFilter? filter = null, CancellationToken ct = default)
        {
            LastFilter = filter;
            return Task.FromResult<(IReadOnlyList<ExecutionRunSummary>?, string?)>(([Summary, Summary with { RunId = Guid.NewGuid(), Name = "Second run", Protected = false }], null));
        }
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
        public ExecutionManifest? Manifest { get; set; }
        public ExecutionApplicability? Applicability { get; set; }
        public Task<(ExecutionManifest? Manifest, string? Error)> ManifestAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<(ExecutionManifest?, string?)>((Manifest, null));
        public Task<(ExecutionApplicability? Applicability, string? Error)> ApplicabilityAsync(Guid runId, CancellationToken ct = default) =>
            Task.FromResult<(ExecutionApplicability?, string?)>((Applicability, null));
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
    public void The_Contract_tab_shows_request_and_response_validation_with_source_paths_and_unevaluated_keywords()
    {
        var page = Open($"?run={Run}&call={Ok}&tab=contract");
        var reports = page.FindAll("[data-testid=xr-contract-report]");
        reports.Select(r => (r.GetAttribute("data-direction"), r.GetAttribute("data-state"))).Should().Equal(("Request", "Valid"), ("Response", "Invalid"));
        page.Find("[data-testid=xr-contract-findings]").TextContent.Should().Contain("$.age").And.Contain("Expected integer, got string.").And.Contain("#/components/schemas/Child/properties/age/type");
        page.Find("[data-testid=xr-contract-unsupported]").TextContent.Should().Be("Not evaluated: discriminator");
        _api.Starts.Should().Be(0);
    }

    [Fact]
    public void Storage_is_measured_and_retention_is_disabled_until_set_previewed_and_applied_with_a_typed_count()
    {
        var page = Open("?area=storage");
        page.Find("[data-testid=xr-kpi-stored] .xr-kpi-value").TextContent.Should().Be("46.9 KB");
        page.Find("[data-testid=xr-kpi-tables] .xr-kpi-value").TextContent.Should().Be("256.0 KB");
        page.Find("[data-testid=xr-storage-location]").TextContent.Should().Contain("PostgreSQL").And.Contain("No evidence files exist");
        page.Find("[data-testid=xr-retention-preview]").HasAttribute("disabled").Should().BeTrue("retention is disabled by default");
        page.Find("[data-testid=xr-retention-mode]").Change("Days");
        page.Find("[data-testid=xr-retention-preset]").Change("30");
        page.Find("[data-testid=xr-retention-save]").Click();
        page.WaitForAssertion(() => _api.RetentionSaves.Should().ContainSingle().Which.Should().Be(new ExecutionRetentionSettings(RetentionMode.Days, 30)));
        page.Find("[data-testid=xr-retention-preview]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=xr-retention-counts]").TextContent.Should().Contain("1 run(s), 3 call(s)").And.Contain("Kept: 1 protected"));
        _api.RetentionApplies.Should().BeEmpty("a preview deletes nothing");
        page.Find("[data-testid=xr-retention-apply]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=xr-retention-typed]").Input("1");
        page.Find("[data-testid=xr-retention-apply]").Click();
        page.WaitForAssertion(() => _api.RetentionApplies.Should().Equal(1));
        page.Find("[data-testid=xr-message]").TextContent.Should().Contain("Deleted 1 run(s)");
        page.Find("[data-testid=xr-audit-entry]").TextContent.Should().Contain("Exported");
        _api.Starts.Should().Be(0);
    }

    [Fact]
    public void History_filters_live_in_the_url_and_selected_runs_are_exported_or_deleted_only_after_typing_their_number()
    {
        JSInterop.SetupVoid("downloadBase64File", _ => true);
        var page = Open("?hq=synthetic&hstate=Failed&hfrom=2026-11-01&hprot=true");
        _api.LastFilter.Should().Be(new ExecutionRunFilter("synthetic", "Failed", new DateOnly(2026, 11, 1), null, true));
        page.Find("[data-testid=xr-run-protected]").HasAttribute("checked").Should().BeTrue();
        page.Find("[data-testid=xr-select-all]").Change(true);
        page.Find("[data-testid=xr-selected-count]").TextContent.Should().Be("2 selected");
        page.Find("[data-testid=xr-export-selected]").Click();
        page.WaitForAssertion(() => _api.Exports.Should().ContainSingle().Which.Should().HaveCount(2));
        JSInterop.VerifyInvoke("downloadBase64File");

        page.Find("[data-testid=xr-delete-selected]").Click();
        var confirm = page.Find("[data-testid=xr-delete-confirm-button]");
        confirm.HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=xr-delete-typed]").Input("3");
        page.Find("[data-testid=xr-delete-confirm-button]").HasAttribute("disabled").Should().BeTrue("the number must be the number shown");
        page.Find("[data-testid=xr-delete-typed]").Input("2");
        page.Find("[data-testid=xr-delete-confirm-button]").Click();
        page.WaitForAssertion(() => _api.Deletions.Should().ContainSingle().Which.Should().Match<ExecutionDeletionRequest>(r => r.RunIds.Count == 2 && r.Confirm == 2 && !r.AllEligible));
    }

    [Fact]
    public void Deleting_all_eligible_runs_shows_what_is_kept_and_a_protected_run_cannot_be_deleted_from_its_page()
    {
        var page = Open("?area=storage");
        page.Find("[data-testid=xr-delete-eligible]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=xr-eligible-preview]").TextContent.Should().Contain("1 finished, unprotected run(s) with 3 call(s)"));
        page.Find("[data-testid=xr-delete-typed]").Input("1");
        page.Find("[data-testid=xr-delete-confirm-button]").Click();
        page.WaitForAssertion(() => _api.Deletions.Should().ContainSingle().Which.Should().Match<ExecutionDeletionRequest>(r => r.AllEligible && r.EnvironmentId == "local" && r.Confirm == 1));

        _api.RunProtected = true;
        page = Open($"?run={Run}");
        page.Find("[data-testid=xr-run-protected-pill]").Should().NotBeNull();
        page.Find("[data-testid=xr-delete-run]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=xr-protect]").Click();
        page.WaitForAssertion(() => _api.Protections.Should().Equal((Run, false)));
        page.Find("[data-testid=xr-verify]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=xr-integrity]").GetAttribute("data-intact").Should().Be("true"));
        page.Find("[data-testid=xr-run-identity]").TextContent.Should().Contain("2 definition(s)").And.Contain("1 dataset row(s)").And.Contain("attended-session:local#g1, attended-session:local#g2")
            .And.Contain("waits up to 120 s");
    }

    [Fact]
    public void A_call_shows_its_authentication_outcome_row_values_and_outputs_and_bodies_are_removed_only_after_confirming()
    {
        _api.Calls[0] = _api.Calls[0] with
        {
            Authentication = CallAuthenticationOutcome.Authenticated,
            Definition = _api.Calls[0].Definition with
            {
                DatasetRow = "children@v3#r-007", StepId = "read", DependsOn = ["create"],
                ResolvedValues = [new("age", "Int32", "row r-007 of children@v3", "7"), new("steps.create.childId", "Integer", "step create → childId", "8")],
            },
            Outputs = [new("name", OutputSource.JsonBody, "$.name", "String", true, "Kari", null)],
            Attempts = [.. _api.Calls[0].Attempts.Select(a => a with { AuthenticationSession = "attended-session:local#g2" })],
        };
        var page = Open($"?run={Run}");
        page.FindAll("[data-testid=xr-call-auth]")[0].TextContent.Should().Be("Authenticated");
        page.FindAll("[data-testid=xr-call-row-ref]")[0].TextContent.Should().Be("step read");
        page = Open($"?run={Run}&call={Ok}&tab=evidence");
        page.Find("[data-testid=xr-call-auth-outcome]").TextContent.Should().Be("Authenticated · attended-session:local#g2");
        page.Find("[data-testid=xr-dataset-row]").TextContent.Should().Be("children@v3#r-007");
        page.Find("[data-testid=xr-step]").TextContent.Should().Be("read · depends on create");
        page.FindAll("[data-testid=xr-resolved-row]").Select(r => r.TextContent).Should().HaveCount(2).And.Contain(t => t.Contains("Int32") && t.Contains("row r-007 of children@v3"));
        page.Find("[data-testid=xr-output]").GetAttribute("data-found").Should().Be("true");
        page.Find("[data-testid=xr-remove-bodies]").Click();
        _api.BodyRemovals.Should().BeEmpty("the first click asks for confirmation");
        page.Find("[data-testid=xr-remove-bodies]").Click();
        page.WaitForAssertion(() => _api.BodyRemovals.Should().Equal((Run, Ok)));
    }

    [Fact]
    public void History_lists_saved_runs_and_opening_one_shows_its_counts_without_executing()
    {
        var page = Open("?area=storage");
        page.FindAll("[data-testid=xr-run-row]")[0].GetAttribute("data-state").Should().Be("Failed");
        page = Open($"?run={Run}");
        page.Find("[data-testid=xr-kpi-planned] .xr-kpi-value").TextContent.Should().Be("3");
        page.Find("[data-testid=xr-kpi-attempts] .xr-kpi-value").TextContent.Should().Be("3");
        page.Find("[data-testid=xr-kpi-attempts] [data-testid=xr-kpi-note]").TextContent.Should().Be("5 sent on the wire", "stack-level resends are visible, not hidden behind the attempt count");
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

    [Fact]
    public void Only_the_selected_tab_is_in_the_tab_order_under_a_Norwegian_culture()
    {
        // Regression: an int tabindex was culture-formatted, and nb-NO in the browser (ICU) writes −1 with U+2212, which browsers ignore — every
        // tab became a tab stop. Windows NLS uses a hyphen for nb-NO, so the test pins the ICU negative sign explicitly.
        var norwegian = (System.Globalization.CultureInfo)new System.Globalization.CultureInfo("nb-NO").Clone();
        norwegian.NumberFormat.NegativeSign = "\u2212";
        var (culture, fallback) = (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.DefaultThreadCurrentCulture);
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.DefaultThreadCurrentCulture = norwegian;
        try
        {
            var page = Open($"?run={Run}&call={Ok}&tab=evidence");
            page.FindAll("[role=tab]").Select(t => (t.GetAttribute("data-testid"), t.GetAttribute("tabindex"))).Should().Equal(
                ("xr-tab-request", "-1"), ("xr-tab-response", "-1"), ("xr-tab-assertions", "-1"), ("xr-tab-attempts", "-1"), ("xr-tab-contract", "-1"), ("xr-tab-evidence", "0"));
        }
        finally { (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.DefaultThreadCurrentCulture) = (culture, fallback); }
    }

    [Fact]
    public void A_historical_result_keeps_its_outcome_and_shows_what_it_ran_against_and_what_moved()
    {
        var contract = new ManifestDependency { Kind = ManifestDependencyKind.Contract, Ref = "children-api@v1", Key = "children-api", Version = 1, ContentFingerprint = new string('a', 64),
            Origin = ManifestDependencyOrigin.OwnerResolved, Resolution = ManifestResolution.Resolved };
        var test = new ManifestDependency { Kind = ManifestDependencyKind.ContractTest, Ref = "contract-test:aa@r2", Version = 2, ContentFingerprint = new string('b', 64),
            Origin = ManifestDependencyOrigin.OwnerResolved, Resolution = ManifestResolution.Resolved };
        _api.Manifest = new() { ManifestId = Guid.NewGuid(), Owner = ExecutionManifestSchema.ExecutionEvidenceOwner, RunId = Run, Build = new("1.4.0", "0123456789abcdef"),
            DependencyFingerprint = new string('c', 64), Dependencies = [contract, test] };
        _api.Applicability = new() { RunId = Run, ResultState = ExecutionState.Failed, Applicability = ResultApplicability.Historical,
            Explanation = "Historical: contract children-api is now v2 (the run used v1). The result stays valid for what it ran against.",
            Dependencies = [new(contract, DependencyCurrencyState.Superseded, "children-api@v2", null, "contract children-api is now v2"), new(test, DependencyCurrencyState.Unchanged, test.Ref, null, "current")] };

        var page = Open($"?run={Run}");
        var panel = page.Find("[data-testid=xr-manifest]");
        panel.GetAttribute("data-applicability").Should().Be("Historical");
        page.Find("[data-testid=xr-applicability]").TextContent.Should().Be("Historical result");
        page.Find("[data-testid=xr-applicability-text]").TextContent.Should().Contain("The failed result is unchanged").And.Contain("now v2");
        page.Find("[data-testid=xr-run-state]").TextContent.Should().Be("Failed", "the run's own outcome is never rewritten");
        var rows = page.FindAll("[data-testid=xr-manifest-row]");
        rows.Should().HaveCount(2);
        rows[0].GetAttribute("data-state").Should().Be("Superseded");
        rows[0].TextContent.Should().Contain("Superseded → children-api@v2").And.Contain("BirkNext (materialised)");
        page.Find("[data-testid=xr-manifest-build]").TextContent.Should().Contain("0123456789ab").And.Contain("execution-manifest/1");
        _api.Starts.Should().Be(0, "reading a result never executes");
    }

    [Fact]
    public void A_run_without_a_manifest_says_its_currency_is_unknown_and_never_claims_current()
    {
        _api.Applicability = new() { RunId = Run, ResultState = ExecutionState.Failed, Applicability = ResultApplicability.Unknown,
            Explanation = "This run has no execution manifest (it was created before manifests were recorded)." };
        var page = Open($"?run={Run}");
        page.Find("[data-testid=xr-manifest]").GetAttribute("data-applicability").Should().Be("Unknown");
        page.Find("[data-testid=xr-applicability]").TextContent.Should().Be("Currency unknown");
        page.FindAll("[data-testid=xr-manifest-table]").Should().BeEmpty();
    }
}
