using System.Globalization;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.SourceDomains;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Contract Testing: import by URL, the operations explorer, draft generation, readiness with missing Test Data values, the dataset picker and
/// pinned binding, running only selected tests (data-changing calls need the explicit confirmation), the latest result linking to Execution
/// Results, and the version comparison with per-test impact. Opening the page never runs anything.
/// </summary>
public sealed class ContractTestingPageTests : BunitContext
{
    private static readonly Guid GetTest = Guid.NewGuid(), PostTest = Guid.NewGuid(), Run = Guid.NewGuid(), Call = Guid.NewGuid();

    private sealed class Api : IContractTestingApiService
    {
        public List<string> Log { get; } = [];
        public ContractImportRequest? Imported { get; private set; }
        public ContractTestRunRequest? Ran { get; private set; }
        public ContractTestBindRequest? Bound { get; private set; }
        public ContractTestEnvironment? Environment { get; private set; }
        public bool HasContract { get; set; } = true;
        public bool Bind { get; set; }
        public bool GraphQl { get; set; }

        private static readonly ApiContractSummary GqlSummary = new(Guid.NewGuid(), "children-graphql", "Children GraphQL", ApiContractProtocol.GraphQl, 1, 2, 1, DateTimeOffset.UtcNow);
        private static readonly SourceContract GqlDocument = new()
        {
            Id = "children-graphql", Name = "Children GraphQL", Type = SourceContractType.GraphQlSchema,
            Operations =
            [
                new("query child", "query", null, ["id"]) { OperationId = "child", ParameterDetails = [new("id", "argument", true, "ID!")], ResultType = "Child", Detailed = true },
                new("mutation createChild", "mutation", null, ["input"]) { OperationId = "createChild", ParameterDetails = [new("input", "argument", true, "NewChildInput!")], ResultType = "Child!", Detailed = true },
            ],
        };
        private static readonly ContractTestView GqlTest = new(new ContractTestDefinition
        {
            Id = Guid.NewGuid(), ContractKey = "children-graphql", ContractVersion = 1, Operation = "mutation createChild", Name = "mutation createChild — returns data without GraphQL errors",
            Template = new() { Protocol = ExecutionProtocol.GraphQl, Method = "POST", Path = "/graphql", GraphQlQuery = "mutation CreateChild($input: NewChildInput!) { createChild(input: $input) { id name } }",
                GraphQlVariablesJson = """{"input":{"name":"{{dataset.name}}"}}""", ExpectedOutcome = "HTTP 200 with data and no GraphQL errors" },
            Inputs = [new("name", "input input", true, "String!")], Rationale = "An HTTP 200 that carries GraphQL errors fails.",
        }, new(ContractTestReadinessState.NotReady, [new("Test Data", ReadinessItemState.Missing, "Select a dataset that provides name.")], ["name"]), null, null);

        private static readonly ApiContractSummary Summary = new(Guid.NewGuid(), "children-api", "Children API", ApiContractProtocol.Rest, 2, 3, 2, DateTimeOffset.UtcNow);
        private static SourceContract Document(int v) => new()
        {
            Id = "children-api", Name = "Children API", Type = SourceContractType.OpenApi,
            Operations =
            [
                new("GET /children/{id}", "GET", "/children/{id}", ["id"]) { ParameterDetails = [new("id", "path", true, "integer")], ResponseCodes = ["200", "404"], ResponseSchemas = new() { ["200"] = "Child" }, Detailed = true },
                new("POST /children", "POST", "/children", []) { RequestContentTypes = ["application/json"], RequestBodyRequired = true, RequestSchema = "NewChild", ResponseCodes = ["201", "400"], Detailed = true },
                new("GET /admin/reports", "GET", "/admin/reports", []) { Security = ["bearer (bearer)"], ResponseCodes = ["200"], Detailed = true },
            ],
        };
        private static ApiContractVersionInfo Info(int v) => new(v, new string('a', 64), v == 1 ? ContractVersionSource.Upload : ContractVersionSource.Url, v == 1 ? "children.yaml" : "http://localhost:5288/openapi.json",
            v == 1 ? "yaml" : "json", DateTimeOffset.UtcNow, 3, $"{v}.0", DomainSupport.Partial, ["YAML OpenAPI read by the subset reader; $ref chains are not resolved."]);

        private ContractTestView Get => new(new ContractTestDefinition
        {
            Id = GetTest, ContractKey = "children-api", ContractVersion = 1, Operation = "GET /children/{id}", Name = "GET /children/{id} — succeeds with valid data", Kind = ContractTestKind.Positive,
            Template = new() { Method = "GET", Path = "/children/{{dataset.id}}", ExpectedOutcome = "HTTP 200" }, Inputs = [new("id", "path", true, "integer")],
            Binding = Bind ? new("children-complete", 1) : null, Rationale = "Sends every required value from the bound Test Data and expects HTTP 200.",
        }, new(Bind ? ContractTestReadinessState.Ready : ContractTestReadinessState.NotReady,
            [new("Contract", ReadinessItemState.Ok, "Generated from v1."), Bind ? new("Test Data", ReadinessItemState.Ok, "children-complete@v1 provides every required value.") : new("Test Data", ReadinessItemState.Missing, "Select a dataset that provides id.")],
            Bind ? [] : ["id"]),
            new(GetTest, "GET /children/{id} — succeeds with valid data", "GET /children/{id}", 1, 2, ContractTestImpactLevel.Review, ["Response type Child.age (field removed) — check the expected outcome."]),
            Bind ? new(Run, Call, ExecutionState.Passed, 200, DateTimeOffset.UtcNow, "contract-test:x@r2", "children-api@v1#GET /children/{id}", ["children-complete@v1"]) : null);

        private static ContractTestView Post => new(new ContractTestDefinition
        {
            Id = PostTest, ContractKey = "children-api", ContractVersion = 2, Operation = "POST /children", Name = "POST /children — succeeds with valid data", Kind = ContractTestKind.Positive,
            Template = new() { Method = "POST", Path = "/children", Body = """{"name":"{{dataset.name}}"}""", ExpectedOutcome = "HTTP 201" }, Inputs = [new("name", "body", true, "string")],
        }, new(ContractTestReadinessState.Blocked, [new("Target Environment", ReadinessItemState.Blocked, "Production is refused.")], []), null, null);

        public Task<(IReadOnlyList<ApiContractSummary>? Contracts, string? Error)> ContractsAsync(CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<ApiContractSummary>?, string?)>((HasContract ? [GraphQl ? GqlSummary : Summary] : [], null));
        public Task<(ApiContractDetail? Detail, string? Error)> ContractAsync(string key, int? version, CancellationToken ct = default)
        {
            Log.Add($"contract {key} {version}");
            var v = version ?? 2;
            if (GraphQl) return Task.FromResult<(ApiContractDetail?, string?)>((new(GqlSummary, [Info(1) with { Format = "graphql" }], 1, GqlDocument), null));
            return Task.FromResult<(ApiContractDetail?, string?)>((new(Summary, [Info(2), Info(1)], v, Document(v)), null));
        }
        public Task<(ContractImportResult? Result, string? Error)> ImportAsync(ContractImportRequest request, CancellationToken ct = default)
        {
            Imported = request; HasContract = true;
            return Task.FromResult<(ContractImportResult?, string?)>((new(true, false, "children-api", 1, "Imported Children API as version 1 (3 operations).", []), null));
        }
        public Task<(ContractVersionDiff? Diff, string? Error)> DiffAsync(string key, int from, int to, CancellationToken ct = default)
        {
            Log.Add($"diff {from} {to}");
            return Task.FromResult<(ContractVersionDiff?, string?)>((new(key, from, to,
                [new(SourceEvidenceDomain.Contracts, SourceEvidenceChangeKind.Removed, "Operation", "children-api|GET /admin/reports", "Children API", "GET /admin/reports", ContractChangeClass.OperationRemoved),
                 new(SourceEvidenceDomain.Contracts, SourceEvidenceChangeKind.Added, "Operation detail", "children-api|GET /children|param:query.school", "Children API", "GET /children: required query parameter school added", ContractChangeClass.ParameterAddedRequired)],
                [new(GetTest, "GET /children/{id} — succeeds with valid data", "GET /children/{id}", 1, 2, ContractTestImpactLevel.Review, ["Response type Child.age (field removed)."]),
                 new(Guid.NewGuid(), "GET /admin/reports — succeeds with valid data", "GET /admin/reports", 1, 2, ContractTestImpactLevel.Broken, ["GET /admin/reports no longer exists in v2."])]), null));
        }
        public Task<(int Created, IReadOnlyList<string> Skipped, string? Error)> GenerateAsync(string key, int? version, IReadOnlyList<string>? operations, CancellationToken ct = default)
        {
            Log.Add($"generate {version} {(operations is null ? "all" : string.Join(",", operations))}");
            return Task.FromResult<(int, IReadOnlyList<string>, string?)>((4, [], null));
        }
        public Task<(IReadOnlyList<ContractTestView>? Tests, string? Error)> TestsAsync(string key, ContractTestEnvironment? environment, CancellationToken ct = default)
        {
            Environment = environment;
            return Task.FromResult<(IReadOnlyList<ContractTestView>?, string?)>((GraphQl ? [GqlTest] : [Get, Post], null));
        }
        public Task<(IReadOnlyList<ContractDatasetOption>? Options, string? Error)> DatasetsAsync(Guid testId, string? environmentId, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<ContractDatasetOption>?, string?)>(([
                new("children-complete", "Complete children", "Dataset", 1, 2, true, ["age", "id", "name"], [], [], null, null),
                new("children-no-id", null, "Key-value collection", 3, 3, false, ["name"], ["id"], [], "local-synthetic", null),
                new("children-draft", null, "Dataset", null, 1, false, ["id"], [], [], null, "No approved version: approve it in Test Data first."),
            ], null));
        public Task<string?> BindAsync(Guid testId, ContractTestBindRequest request, CancellationToken ct = default) { Bound = request; Bind = true; return Task.FromResult<string?>(null); }
        public Task<string?> RegenerateAsync(Guid testId, CancellationToken ct = default) { Log.Add($"regenerate {testId}"); return Task.FromResult<string?>(null); }
        public Task<string?> DeleteAsync(Guid testId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public List<ContractEndpointBindingSave> Bindings { get; } = [];
        public List<ApiServiceCredentialSave> Credentials { get; } = [];
        public Task<(ContractBindingResponse? Binding, string? Error)> BindingAsync(string key, string environmentId, CancellationToken ct = default) =>
            Task.FromResult<(ContractBindingResponse?, string?)>((new(Bindings.LastOrDefault() is { } b ? new(key, b.EnvironmentId, b.BasePath, b.GraphQlPath, b.Authentication, DateTimeOffset.UtcNow) : null,
                [new("https://api.example.test/v1", "/v1")]), null));
        public Task<string?> SaveBindingAsync(string key, ContractEndpointBindingSave save, CancellationToken ct = default) { Bindings.Add(save); return Task.FromResult<string?>(null); }
        public Task<(ApiServiceCredentialView? Credential, string? Error)> CredentialAsync(string environmentId, CancellationToken ct = default) =>
            Task.FromResult<(ApiServiceCredentialView?, string?)>((Credentials.LastOrDefault() is { } c ? new(environmentId, c.Kind, $"api-credential:{environmentId}:{c.Kind}:v{Credentials.Count}", c.TokenUrl, c.ClientId, c.Scope, c.HeaderName, true, Credentials.Count, DateTimeOffset.UtcNow) : null, null));
        public Task<string?> SaveCredentialAsync(ApiServiceCredentialSave save, CancellationToken ct = default) { Credentials.Add(save); return Task.FromResult<string?>(null); }
        public Task<string?> DeleteCredentialAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<(ContractTestRunResult? Result, string? Error)> RunAsync(ContractTestRunRequest request, CancellationToken ct = default)
        {
            Ran = request;
            return Task.FromResult<(ContractTestRunResult?, string?)>((new(new(Run, ExecutionState.Running, "Run started: 1 call(s) will be sent, 0 blocked.", []), []), null));
        }
    }

    private readonly Api _api = new();
    private readonly FrontendAnalysisContext _context = new()
    {
        ActiveProfile = new FrontendAnalysisProfile { Id = "local-synthetic", Name = "Local synthetic", EnvironmentType = FrontendEnvironmentType.Local }, TargetUrl = "http://localhost:5288/",
    };

    public ContractTestingPageTests()
    {
        Services.AddSingleton<IContractTestingApiService>(_api);
        var factory = new Mock<IFrontendAnalysisContextFactory>();
        factory.Setup(f => f.GetActiveContextAsync()).ReturnsAsync(() => _context);
        Services.AddSingleton(factory.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<BirkNext.Web.Pages.ContractTesting> Open(string query)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("api-quality-review/contracts" + query);
        var page = Render<BirkNext.Web.Pages.ContractTesting>();
        page.WaitForAssertion(() => page.Find("[data-testid=ct-page]").GetAttribute("aria-busy").Should().Be("false"));
        return page;
    }

    [Fact]
    public void Import_by_URL_sends_the_URL_only_and_opens_the_imported_contract()
    {
        _api.HasContract = false;
        var page = Open("");
        page.Find("[data-testid=ct-contracts-empty]").TextContent.Should().Contain("No contracts yet");
        page.Find("[data-testid=ct-import-url-mode]").Change(true);
        page.Find("[data-testid=ct-import-url]").Input("http://localhost:5288/openapi.json");
        page.Find("[data-testid=ct-import-submit]").Click();
        page.WaitForAssertion(() => _api.Imported.Should().NotBeNull());
        _api.Imported!.Url.Should().Be("http://localhost:5288/openapi.json");
        _api.Imported.Content.Should().BeNull();
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("contract=children-api").And.Contain("view=operations");
    }

    /// <summary>CDT-DEF-1 (browser acceptance CT-03): typing a URL did not enable Import until the field lost focus (bound on change).</summary>
    [Fact]
    public void Typing_a_URL_enables_Import_without_leaving_the_field()
    {
        _api.HasContract = false;
        var page = Open("");
        page.Find("[data-testid=ct-import-url-mode]").Change(true);
        page.Find("[data-testid=ct-import-submit]").HasAttribute("disabled").Should().BeTrue();
        page.Find("[data-testid=ct-import-url]").Input("http://localhost:5288/openapi.json");
        page.Find("[data-testid=ct-import-submit]").HasAttribute("disabled").Should().BeFalse("the button reflects what is typed, not what was last committed on blur");
    }

    [Fact]
    public void The_operations_explorer_shows_identity_inputs_responses_and_security_and_generates_drafts()
    {
        var page = Open("?contract=children-api");
        page.FindAll("[data-testid=ct-op-row]").Select(r => r.GetAttribute("data-op")).Should().Equal("GET /children/{id}", "POST /children", "GET /admin/reports");
        var rows = page.FindAll("[data-testid=ct-op-row]");
        rows[0].TextContent.Should().Contain("id").And.Contain("path · integer · required").And.Contain("200 Child");
        rows[1].TextContent.Should().Contain("NewChild").And.Contain("application/json · required");
        rows[2].TextContent.Should().Contain("bearer (bearer)");
        page.Find("[data-testid=ct-version-source]").TextContent.Should().Contain("From URL");
        page.Find("[data-testid=ct-limitations]").TextContent.Should().Contain("subset reader");
        page.Find("[data-testid=ct-generate-all]").Click();
        page.WaitForAssertion(() => _api.Log.Should().Contain("generate 2 all"));
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("view=tests");
        _api.Ran.Should().BeNull("generating never runs anything");
    }

    [Fact]
    public void Tests_show_readiness_with_missing_values_and_the_picker_pins_the_approved_version()
    {
        var page = Open($"?contract=children-api&view=tests&test={GetTest}");
        _api.Environment.Should().BeEquivalentTo(new { EnvironmentId = "local-synthetic", TargetUrl = "http://localhost:5288/", EnvironmentType = "Local", ProfileId = "local-synthetic" });
        _api.Environment!.ContextFingerprint.Should().NotBeNullOrEmpty("the non-secret proxy identity lets the backend find an attended session");
        var row = page.FindAll("[data-testid=ct-test-row]").Single(r => r.GetAttribute("data-op") == "GET /children/{id}");
        row.QuerySelector("[data-testid=ct-readiness]")!.TextContent.Should().Be("Not ready");
        row.QuerySelector("[data-testid=ct-missing]")!.TextContent.Should().Be("Missing: id");
        row.QuerySelector("[data-testid=ct-impact]")!.TextContent.Should().Be("Review in v2");
        page.Find("[data-testid=ct-detail-impact]").TextContent.Should().Contain("Child.age");

        var options = page.FindAll("[data-testid=ct-option]");
        options.Select(o => o.GetAttribute("data-alias")).Should().Equal("children-complete", "children-no-id", "children-draft");
        options[1].QuerySelector("[data-testid=ct-option-missing]")!.TextContent.Should().Be("Missing required: id");
        options[2].QuerySelector("[data-testid=ct-option-use]")!.HasAttribute("disabled").Should().BeTrue("an unapproved dataset cannot be pinned");
        options[0].QuerySelector("[data-testid=ct-option-use]")!.TextContent.Should().Be("Use children-complete@v1");

        options[0].QuerySelector("[data-testid=ct-option-use]")!.Click();
        page.WaitForAssertion(() => _api.Bound.Should().Be(new ContractTestBindRequest("children-complete", 1)));
        page.WaitForAssertion(() => page.Find("[data-testid=ct-binding]").TextContent.Should().Be("Pinned to children-complete@v1."));
        page.Find("[data-testid=ct-message]").TextContent.Should().Contain("Runs use exactly this version");
    }

    [Fact]
    public void Run_sends_only_the_selected_tests_with_the_confirmation_and_base_path_and_links_the_evidence()
    {
        _api.Bind = true;
        var page = Open("?contract=children-api&view=tests");
        page.Find("[data-testid=ct-run]").HasAttribute("disabled").Should().BeTrue("nothing is selected");
        page.Find("[data-testid=ct-select-ready]").Change(true);
        page.Find("[data-testid=ct-base-path]").Input("/api");
        page.Find("[data-testid=ct-run]").Click();
        page.WaitForAssertion(() => _api.Ran.Should().NotBeNull());
        _api.Ran!.TestIds.Should().Equal(GetTest);
        _api.Ran.ConfirmMutatingCalls.Should().BeFalse("data-changing calls are confirmed only explicitly");
        _api.Ran.BasePath.Should().Be("/api");
        page.Find("[data-testid=ct-run-link]").GetAttribute("href").Should().Be($"execution-results?run={Run}");
        var last = page.FindAll("[data-testid=ct-test-row]").Single(r => r.GetAttribute("data-op") == "GET /children/{id}").QuerySelector("[data-testid=ct-last]")!;
        last.GetAttribute("href").Should().Be($"execution-results?run={Run}&call={Call}");
        last.TextContent.Should().Contain("Passed").And.Contain("HTTP 200");
    }

    [Fact]
    public void Without_a_Target_Environment_tests_can_be_prepared_but_not_run()
    {
        _context.ActiveProfile = new FrontendAnalysisProfile();
        _api.Bind = true;
        var page = Open("?contract=children-api&view=tests");
        page.Find("[data-testid=ct-env-missing]").TextContent.Should().Contain("select a Target Environment");
        page.Find("[data-testid=ct-select-ready]").Change(true);
        page.Find("[data-testid=ct-run]").HasAttribute("disabled").Should().BeTrue();
        _api.Environment.Should().BeNull();
    }

    [Fact]
    public void A_GraphQL_contract_shows_root_fields_result_types_and_the_query_document_with_variables()
    {
        _api.GraphQl = true;
        var page = Open("?contract=children-graphql");
        page.Find("[data-testid=ct-contract-link]").ParentElement!.TextContent.Should().Contain("GraphQL · v1");
        var rows = page.FindAll("[data-testid=ct-op-row]");
        rows.Select(r => r.GetAttribute("data-op")).Should().Equal("query child", "mutation createChild");
        rows[0].TextContent.Should().Contain("id").And.Contain("argument · ID! · required").And.Contain("returns Child");
        page = Open($"?contract=children-graphql&view=tests&test={GqlTestId}");
        page.Find("[data-testid=ct-test-row]").TextContent.Should().Contain("changes data", "a mutation needs the data-changing confirmation");
        page.Find("[data-testid=ct-template]").TextContent.Should().Contain("mutation CreateChild($input: NewChildInput!)").And.Contain("variables {\"input\":{\"name\":\"{{dataset.name}}\"}}");
    }

    private Guid GqlTestId => _api.TestsAsync("children-graphql", null).Result.Tests![0].Test.Id;

    [Fact]
    public void The_endpoint_panel_binds_a_server_path_and_authentication_and_saves_an_unattended_secret_write_only()
    {
        var page = Open("?contract=children-api");
        page.Find("[data-testid=cep-proposal]").TextContent.Should().Be("/v1");
        page.Find("[data-testid=cep-proposal]").Click();
        page.Find("[data-testid=cep-auth]").Change(ExecutionAuthenticationKind.ClientCredentials.ToString());
        page.Find("[data-testid=cep-save]").Click();
        page.WaitForAssertion(() => _api.Bindings.Should().ContainSingle().Which.Should().Be(new ContractEndpointBindingSave("local-synthetic", "/v1", null, ExecutionAuthenticationKind.ClientCredentials)));
        page.Find("[data-testid=cep-credential-state]").TextContent.Should().Be("None configured.");
        page.Find("[data-testid=cep-token-url]").Input("http://localhost:5288/oauth/token");
        page.Find("[data-testid=cep-client-id]").Input("synthetic-client");
        page.Find("[data-testid=cep-secret]").Input("synthetic-secret-value");
        page.Find("[data-testid=cep-credential-save]").Click();
        page.WaitForAssertion(() => _api.Credentials.Should().ContainSingle());
        _api.Credentials[0].Secret.Should().Be("synthetic-secret-value");
        page.WaitForAssertion(() => page.Find("[data-testid=cep-credential-state]").TextContent.Should().Contain("api-credential:local-synthetic:ClientCredentials:v1").And.Contain("encrypted"));
        page.Find("[data-testid=cep-secret]").GetAttribute("value").Should().BeNullOrEmpty("the secret is write-only and cleared after saving");
        page.Markup.Should().NotContain("synthetic-secret-value");
    }

    [Fact]
    public void Changes_compare_versions_and_rank_the_impact_on_tests()
    {
        var page = Open("?contract=children-api&view=changes");
        _api.Log.Should().Contain("diff 1 2");
        page.FindAll("[data-testid=ct-change-row]").Select(r => r.GetAttribute("data-class")).Should().Equal("OperationRemoved", "ParameterAddedRequired");
        page.FindAll("[data-testid=ct-change-row]")[1].TextContent.Should().Contain("Required parameter added").And.Contain("Potentially breaking");
        page.FindAll("[data-testid=ct-impact-row]").Select(r => r.GetAttribute("data-level")).Should().Equal("Broken", "Review");
    }

    [Fact]
    public void Only_the_selected_view_tab_is_in_the_tab_order_under_a_Norwegian_culture()
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo("nb-NO").Clone();
        culture.NumberFormat.NegativeSign = "−";
        var (current, defaultCulture) = (CultureInfo.CurrentCulture, CultureInfo.DefaultThreadCurrentCulture);
        try
        {
            CultureInfo.CurrentCulture = culture; CultureInfo.DefaultThreadCurrentCulture = culture;
            var page = Open("?contract=children-api&view=tests");
            page.FindAll("[role=tab]").Select(t => t.GetAttribute("tabindex")).Should().Equal("-1", "0", "-1");
        }
        finally { CultureInfo.CurrentCulture = current; CultureInfo.DefaultThreadCurrentCulture = defaultCulture; }
    }
}
