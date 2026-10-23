using BirkNext.Integrations;
using BirkNext.TestData;
using BirkNext.Web.Components;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// The Test Data catalog in the browser: an overview of counts, lifecycle status/scope/data type per asset, explicit approval of the shown
/// version, requested Test Data from generic consumers (including one the page has never heard of), matching that suggests and never binds,
/// binding only on an explicit choice, and candidates registered from review (never approved, never generated). No value is ever shown.
/// </summary>
public sealed class TestDataCatalogUiTests : BunitContext
{
    private const string Secret = "SECRET-VALUE";

    private sealed class Api : ITestDataApiService
    {
        public List<TestDataAssetSummary> Assets { get; } = [];
        public List<ConsumerTestDataRequirement> Requirements { get; } = [];
        public List<TestDataUsageReference> Bound { get; } = [];
        public List<string> Approved { get; } = [];
        public List<TestDataRegistrationRequest> Registered { get; } = [];
        public List<ConsumerTestDataRequirement> Added { get; } = [];

        public Task<(TestDataCatalogView? Catalog, string? Error)> CatalogAsync(CancellationToken ct = default) =>
            Task.FromResult<(TestDataCatalogView?, string?)>((new([.. Assets], [], []), null));
        public Task<(TestDataAssetDetail? Detail, string? Error)> AssetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<(TestDataAssetDetail?, string?)>((new TestDataAssetDetail(Assets.Single(a => a.Id == id), null, "Sensitive: the value stays in the backend and is never shown."), null));
        public Task<(TestDataConsumerView? View, string? Error)> ConsumersAsync(CancellationToken ct = default) =>
            Task.FromResult<(TestDataConsumerView?, string?)>((new([.. Requirements], [.. Bound], new(Assets.Count(a => a.Status == TestDataAssetStatus.Approved), 1, 0, 0, Requirements.Count, 0, Requirements.Count, 0, 0)), null));
        public Task<string?> ApproveAsync(string assetId, CancellationToken ct = default) { Approved.Add(assetId); return Task.FromResult<string?>(null); }
        public Task<(ConsumerTestDataRequirement? Requirement, string? Error)> AddRequirementAsync(ConsumerTestDataRequirement requirement, CancellationToken ct = default)
        {
            var stored = requirement with { Id = "req-" + (Added.Count + 1) };
            Added.Add(stored);
            return Task.FromResult<(ConsumerTestDataRequirement?, string?)>((stored, null));
        }
        public Task<(TestDataUsageReference? Usage, string? Error)> BindAsync(TestDataUsageReference usage, CancellationToken ct = default)
        {
            Bound.Add(usage);
            return Task.FromResult<(TestDataUsageReference?, string?)>((usage, null));
        }
        public Task<(string? AssetId, string? Error)> RegisterAsync(TestDataRegistrationRequest request, CancellationToken ct = default)
        {
            Registered.Add(request);
            return Task.FromResult<(string?, string?)>(("registered:" + request.Alias, null));
        }
        public Task<IReadOnlyList<TestDataCandidate>?> CandidatesAsync(Guid snapshotId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TestDataCandidate>?>(
            [
                new("op:api:POST /reports:body", TestDataCandidateOrigin.ContractEvidence, "Report request body", "application/json", TestDataIntent.Positive, null,
                    ["title: string (required)", "fodselsnummer: string = " + TestDataLifecycle.ControlledReference], [], "api/openapi.json", null)
                    { Reason = "Proposed because OpenAPI operation POST /reports requires Report.", DataType = TestDataDataType.JsonPayload },
            ]);
    }

    private readonly Api _api = new();

    public TestDataCatalogUiTests() => Services.AddSingleton<ITestDataApiService>(_api);

    private static TestDataAssetSummary Scalar(string alias, TestDataAssetStatus status, string? environment = null, int consumers = 0) => new()
    {
        Id = "configured:" + alias, Alias = alias, Kind = TestDataKind.Scalar, ValueType = TestDataValueType.String, Environment = environment,
        Approval = status == TestDataAssetStatus.Approved ? TestDataApproval.Approved : TestDataApproval.Unapproved, Sensitivity = TestDataSensitivity.Sensitive,
        Source = "Configured catalog", SourceId = "configured", DisplayValue = Secret, ValueAvailable = true,
        DataType = TestDataDataType.SimpleValues, Status = status, ContractStatus = TestDataContractStatus.NotApplicable, Fields = ["personId"], Consumers = consumers,
    };

    private IRenderedComponent<TestDataPage> Page()
    {
        var page = Render<TestDataPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=td-page]").GetAttribute("aria-busy").Should().Be("false"));
        return page;
    }

    [Fact]
    public void Overview_and_rows_show_status_scope_type_and_consumers_never_a_value()
    {
        _api.Assets.Add(Scalar("person-id", TestDataAssetStatus.Approved, consumers: 2));
        _api.Assets.Add(Scalar("qa-person", TestDataAssetStatus.NeedsReview, environment: "QA"));
        var page = Page();

        page.Find("[data-testid=td-overview-approved]").TextContent.Should().Be("1");
        var row = page.FindAll("[data-testid=td-asset]").Single(r => r.GetAttribute("data-alias") == "person-id");
        row.QuerySelector("[data-testid=td-status]")!.TextContent.Should().Be("Approved");
        row.QuerySelector("[data-testid=td-scope]")!.TextContent.Should().Be("Shared");
        row.QuerySelector("[data-testid=td-data-type]")!.TextContent.Should().Be("Simple values / parameters");
        row.QuerySelector("[data-testid=td-consumers]")!.TextContent.Should().Contain("2");
        page.FindAll("[data-testid=td-asset]").Single(r => r.GetAttribute("data-alias") == "qa-person").QuerySelector("[data-testid=td-status]")!.TextContent.Should().Be("Needs review");
        page.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public void Approve_is_an_explicit_action_on_the_shown_version()
    {
        _api.Assets.Add(Scalar("draft-id", TestDataAssetStatus.Draft));
        var page = Page();
        page.Find("[data-testid=td-toggle]").Click();
        page.WaitForAssertion(() => page.Find("[data-testid=td-approve]"));
        _api.Approved.Should().BeEmpty("opening an asset never approves it");
        page.Find("[data-testid=td-approve]").Click();
        _api.Approved.Should().Equal("configured:draft-id");
        page.Find("[data-testid=td-action-status]").TextContent.Should().Contain("approved (this version");
        page.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public void Requested_test_data_from_an_unknown_consumer_type_matches_without_binding_and_binds_only_on_choice()
    {
        _api.Assets.Add(Scalar("person-id", TestDataAssetStatus.Approved));
        _api.Assets.Add(Scalar("qa-person", TestDataAssetStatus.Approved, environment: "QA"));
        _api.Requirements.Add(new()
        {
            Id = "req-1", Consumer = new() { ConsumerType = "Quantum Review", ConsumerId = "qr-7", DisplayName = "Quantum check", Parent = "Future module" },
            Usage = "Path parameter", RequestedDataType = TestDataDataType.SimpleValues, RequestedFields = ["personId"], EnvironmentId = "TT02",
        });
        var page = Page();

        var requirement = page.Find("[data-testid=td-requirement]");
        requirement.TextContent.Should().Contain("Future module › Quantum check").And.Contain("Quantum Review").And.Contain("TT02");
        requirement.QuerySelector("[data-testid=td-requirement-status]")!.TextContent.Should().Be("Missing");
        page.Find("[data-testid=td-requirement-find]").Click();
        var matches = page.FindAll("[data-testid=td-match]");
        matches.Select(m => m.GetAttribute("data-state")).Should().BeEquivalentTo(["Compatible", "Conflict"]);
        matches.Single(m => m.GetAttribute("data-state") == "Conflict").QuerySelectorAll("[data-testid=td-bind]").Should().BeEmpty("an asset scoped to another environment is never offered for binding");
        _api.Bound.Should().BeEmpty("matching never binds");

        page.Find("[data-testid=td-bind]").Click();
        _api.Bound.Should().ContainSingle().Which.Should().Match<TestDataUsageReference>(u => u.AssetId == "configured:person-id" && u.RequirementId == "req-1" && u.Consumer.ConsumerType == "Quantum Review");
        page.Markup.Should().NotContain(Secret);
    }

    [Fact]
    public void A_possible_match_needs_confirmation_before_it_can_be_bound()
    {
        _api.Assets.Add(Scalar("anything", TestDataAssetStatus.Approved) with { Fields = ["other"] });
        _api.Requirements.Add(new() { Id = "req-1", Consumer = new() { ConsumerType = "Journey", ConsumerId = "j1", DisplayName = "J1" }, Usage = "Form value", RequestedDataType = TestDataDataType.SimpleValues, RequestedFields = ["kommune"] });
        var page = Page();
        page.Find("[data-testid=td-requirement-find]").Click();
        page.Find("[data-testid=td-match]").GetAttribute("data-state").Should().Be("Possible");
        page.FindAll("[data-testid=td-bind]").Should().BeEmpty();
        page.Find("[data-testid=td-confirm]").Click();
        page.Find("[data-testid=td-bind]").Click();
        _api.Bound.Should().ContainSingle();
    }

    [Fact]
    public void A_consumer_deep_link_registers_its_requirement_and_binds_only_when_chosen()
    {
        _api.Assets.Add(Scalar("person-id", TestDataAssetStatus.Approved));
        _api.Assets.Add(Scalar("draft-id", TestDataAssetStatus.Draft));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/test-data?consumer=flow-checker&consumerType=Flow&operation=GET%20%2Fp%2F%7BpersonId%7D&variable=personId&in=path&dataType=SimpleValues");
        var page = Page();

        page.Find("#td-request-heading").TextContent.Should().Be("Requested by Flow checker", "the label comes from the consumer id — Test Data keeps no consumer list");
        page.FindAll("[data-testid=td-request-match]").Select(m => m.GetAttribute("data-state")).Should().BeEquivalentTo(["Compatible", "Conflict"]);
        _api.Added.Should().BeEmpty();
        _api.Bound.Should().BeEmpty();

        page.Find("[data-testid=td-request-bind]").Click();
        _api.Added.Should().ContainSingle().Which.Should().Match<ConsumerTestDataRequirement>(r => r.Consumer.ConsumerType == "Flow" && r.Usage == "Path parameter" && r.RequestedFields.SequenceEqual(new[] { "personId" }));
        _api.Bound.Should().ContainSingle().Which.RequirementId.Should().Be("req-1");
        page.Find("[data-testid=td-request-status]").TextContent.Should().Contain("person-id bound to this requirement");
    }

    [Fact]
    public void Candidates_panel_registers_a_reviewed_candidate_with_its_snapshot_and_never_approves_it()
    {
        var snapshot = Guid.Parse("7d000000-0000-0000-0000-0000000000b1");
        Services.AddSingleton<IIntegrationCatalogApiService>(new BirkNext.Web.Tests.Integration.FakeIntegrationCatalogApi
        {
            SourceSnapshots = [new() { Id = snapshot, Archive = new("receiver.zip", "a", 1), AnalyzedAt = DateTimeOffset.Parse("2026-10-01T08:00:00Z") }],
        });
        var registered = new List<string>();
        var cut = Render<TestDataCandidatesPanel>(p => p.Add(x => x.Scopes, [("env-qa", "QA (Test)")]).Add(x => x.OnRegistered, (string id) => registered.Add(id)));
        cut.Find("[data-testid=td-candidates-snapshot]").Change(snapshot.ToString());

        cut.Find("[data-testid=td-candidate-reason]").TextContent.Should().Contain("requires Report");
        cut.Find("[data-testid=td-candidate-type]").TextContent.Should().Be("JSON payload");
        cut.Find("[data-testid=td-candidate-review]").Click();
        cut.Find("[data-testid=td-candidate-structure]").TextContent.Should().Contain(TestDataLifecycle.ControlledReference);
        cut.Find("[data-testid=td-register-alias]").GetAttribute("value").Should().Be("report-request-body");
        cut.Find("[data-testid=td-register-scope]").Change("env-qa");
        cut.Find("[data-testid=td-register]").Click();

        _api.Registered.Should().ContainSingle().Which.Should().Match<TestDataRegistrationRequest>(r =>
            r.SnapshotId == snapshot && r.CandidateId == "op:api:POST /reports:body" && r.EnvironmentId == "env-qa" && r.DataType == TestDataDataType.JsonPayload && r.Fields.Count == 0);
        _api.Approved.Should().BeEmpty("registration never approves");
        registered.Should().Equal("registered:report-request-body");
        cut.Find("[data-testid=td-candidates-status]").TextContent.Should().Contain("Needs review");
    }
}
