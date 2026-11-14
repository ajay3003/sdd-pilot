using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.ScopeRegistry;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.TestCoverage;
using BirkNext.Integrations;
using BirkNext.Scopes;
using BirkNext.TestCoverage;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.TestCoverage;

/// <summary>
/// Project-declared Integration Journeys (persisted, project-scoped, names only) feed Test Coverage association candidates only by exact component
/// chain; the Scope Registry lists and checks them as Journey members. Test Data consumer references of a QA test give its asset id, bound revision
/// and readiness: an unresolved (deprecated / changed / invalid) revision is never "Linked", a registered need without a binding is
/// RequiredButMissing, and nothing recorded stays NotAssessed. The source fixture mirrors scratchpad acc/make-journey-fixture.py.
/// </summary>
public sealed class TestCoverageJourneyDeclarationTests : IDisposable
{
    private const string Project = "import:acc-journeys";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "birknext-declared-journeys-" + Guid.NewGuid().ToString("N"));
    private readonly BirkNext.Api.Data.AppDbContext _db = TestCoverageFixture.Db();
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private DeclaredIntegrationJourneyStore Store() => new(_dir, NullLogger<DeclaredIntegrationJourneyStore>.Instance);

    private static DeclaredIntegrationJourneyRequest Request(string name, params string[] components) =>
        new() { ProjectKey = Project, DisplayName = name, Components = [.. components], DeclaredBy = "Kari", Description = "Acceptance" };

    private static readonly string[] Notification = ["Orders.Adapter", "Orders.Api", "order-events", "Orders.Notifier"];
    private static readonly string[] Archive = ["Orders.Adapter", "Orders.Api", "order-archive", "Orders.Archiver"];

    // ── Declared journeys: owner store ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_declared_journey_is_persisted_per_project_with_a_stable_id_and_never_overwritten()
    {
        var store = Store();
        var (journey, _, error) = store.Declare(Request("Order notification", [.. Notification, "Orders.Notifier"]), Now);
        error.Should().BeNull();
        journey!.Id.Should().Be($"project.{Project}:order-notification");
        journey.Components.Should().Equal(Notification, "consecutive duplicates collapse");

        var (_, existing, conflict) = store.Declare(Request("Order  Notification!", "A", "B"), Now);
        existing!.Id.Should().Be(journey.Id, "the same slug in the same project is the same journey");
        conflict.Should().Contain("already declares");
        store.Declare(Request("Order notification", "A", "B") with { ProjectKey = "import:other" }, Now).Journey.Should().NotBeNull("another project has its own journeys");

        var reopened = Store();
        reopened.ForProject(Project).Should().ContainSingle().Which.Should().Match<DeclaredIntegrationJourney>(j => j.Id == journey.Id && j.DeclaredBy == "Kari" && j.Components.SequenceEqual(Notification));
        reopened.ForProject("import:other").Should().ContainSingle();
        reopened.ForProject(null).Should().BeEmpty();
        reopened.Remove(Project, "order-notification").Should().BeTrue();
        Store().ForProject(Project).Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "Load a project")]
    [InlineData(Project, "at least two components")]
    public void Invalid_declarations_are_refused_and_nothing_is_stored(string projectKey, string reason)
    {
        var store = Store();
        store.Declare(Request("Single", "Orders.Api") with { ProjectKey = projectKey }, Now).Error.Should().Contain(reason);
        store.Declare(Request("Url", "Orders.Api", "https://orders.example/api"), Now).Error.Should().Contain("never a URL");
        store.Declare(Request("Secret", "Orders.Api", "Endpoint=sb://x;SharedAccessKey=abc"), Now).Error.Should().NotBeNull();
        store.Declare(Request("Nobody", "A", "B") with { DeclaredBy = " " }, Now).Error.Should().Contain("Who declares");
        store.All().Should().BeEmpty();
    }

    // ── Test Coverage candidates from the declared chain (real Source Analysis pipeline) ───────────────────────────────────

    private static (string, string)[] JourneyFixtureFiles()
    {
        const string web = """<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";
        const string worker = """<Project Sdk="Microsoft.NET.Sdk.Worker"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{0}</Project>""";
        static string Pkg(params string[] p) => "<ItemGroup>" + string.Concat(p.Select(x => $"""<PackageReference Include="{x}" Version="1.0.0" />""")) + "</ItemGroup>";
        static string TestProject(string reference, params string[] p) =>
            $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>{Pkg(["xunit", "Microsoft.NET.Test.Sdk", "Moq", .. p])}<ItemGroup><ProjectReference Include="{reference}" /></ItemGroup></Project>""";
        return
        [
            ("Orders/AppHost/AppHost.csproj", """<Project Sdk="Aspire.AppHost.Sdk/9.0.0"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"""),
            ("Orders/AppHost/AppHost.cs", "var builder = DistributedApplication.CreateBuilder(args);\nvar api = builder.AddProject<Projects.Orders_Api>(\"orders-api\");\nvar adapter = builder.AddProject<Projects.Orders_Adapter>(\"orders-adapter\")\n    .WithEnvironment(\"OrdersApi__BaseUrl\", api.GetEndpoint(\"http\"))\n    .WaitFor(api);\nbuilder.Build().Run();\n"),
            ("Orders/Orders.Adapter/Orders.Adapter.csproj", string.Format(worker, "")),
            ("Orders/Orders.Adapter/Program.cs", "var b = Host.CreateApplicationBuilder(args);\nb.Services.AddHttpClient<OrdersApiClient>(c => c.BaseAddress = new Uri(b.Configuration[\"OrdersApi:BaseUrl\"]!));\nb.Build().Run();\n"),
            ("Orders/Orders.Adapter/OrdersApiClient.cs", "public sealed class OrdersApiClient(HttpClient http) { public Task<HttpResponseMessage> CreateAsync(OrderRequest r) => http.PostAsJsonAsync(\"/orders\", r); }\npublic sealed record OrderRequest(string OrderId, decimal Amount);\n"),
            ("Orders/Orders.Api/Orders.Api.csproj", string.Format(web, Pkg("Microsoft.EntityFrameworkCore.SqlServer", "Azure.Messaging.ServiceBus"))),
            ("Orders/Orders.Api/Program.cs", "var builder = WebApplication.CreateBuilder(args);\nbuilder.Services.AddDbContext<OrdersDbContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString(\"Orders\")));\nvar app = builder.Build();\napp.MapPost(\"/orders\", (OrderService s) => s.CreateAsync());\napp.Run();\n"),
            ("Orders/Orders.Api/OrderEvents.cs", "public sealed class OrderEvents(ServiceBusClient client) { public ServiceBusSender Sender => client.CreateSender(\"order-events\"); }\n"),
            ("Orders/Orders.Api/OrderAudit.cs", "public sealed class OrderAudit(ServiceBusClient client) { public ServiceBusSender Sender => client.CreateSender(\"order-audit\"); }\n"),
            ("Orders/Orders.Api/OrdersDbContext.cs", "public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> o) : DbContext(o) { public DbSet<Order> Orders => Set<Order>(); }\npublic sealed class Order { public string Id { get; set; } = \"\"; }\n"),
            ("Orders/Orders.Api/OrderService.cs", "public interface IOrderRepository { Task<bool> ExistsAsync(string id); Task AddAsync(Order o); }\npublic sealed class DuplicateOrderException : Exception { }\npublic sealed class OrderService(IOrderRepository repository)\n{\n    public async Task CreateAsync(string id)\n    {\n        if (await repository.ExistsAsync(id)) throw new DuplicateOrderException();\n        await repository.AddAsync(new Order { Id = id });\n    }\n}\n"),
            ("Orders/Orders.Notifier/Orders.Notifier.csproj", string.Format(worker, Pkg("Azure.Messaging.ServiceBus"))),
            ("Orders/Orders.Notifier/Program.cs", "var b = Host.CreateApplicationBuilder(args);\nvar client = new ServiceBusClient(b.Configuration[\"ServiceBus:Namespace\"], credential);\nvar processor = client.CreateProcessor(\"order-events\", \"notifier\");\nb.Build().Run();\n"),
            ("Orders/Orders.Auditor/Orders.Auditor.csproj", string.Format(worker, Pkg("Azure.Messaging.ServiceBus"))),
            ("Orders/Orders.Auditor/Program.cs", "var b = Host.CreateApplicationBuilder(args);\nvar client = new ServiceBusClient(b.Configuration[\"ServiceBus:Namespace\"], credential);\nvar processor = client.CreateProcessor(\"order-audit\", \"auditor\");\nb.Build().Run();\n"),
            ("Orders/tests/Orders.Acceptance.Tests/Orders.Acceptance.Tests.csproj", TestProject("../../Orders.Api/Orders.Api.csproj", "Microsoft.AspNetCore.Mvc.Testing")),
            ("Orders/tests/Orders.Acceptance.Tests/OrderAcceptanceTests.cs", "namespace Orders.Acceptance.Tests;\npublic sealed class OrderAcceptanceTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>\n{\n    [Fact]\n    public async Task PlaceOrder_NotifiesCustomer()\n    {\n        var response = await factory.CreateClient().PostAsJsonAsync(\"/orders\", new { id = \"QA-1\" });\n        Assert.Equal(HttpStatusCode.Created, response.StatusCode);\n    }\n\n    [Fact]\n    public async Task CancelOrder_RejectsShippedOrder()\n    {\n        var response = await factory.CreateClient().DeleteAsync(\"/orders/QA-2\");\n        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);\n    }\n}\n"),
        ];
    }

    private const string MatchingTitle = "Orders.Adapter → Orders.Api → order-events → Orders.Notifier";

    [Fact]
    public async Task Only_the_declared_chain_of_this_project_proposes_a_candidate_with_both_chains_as_evidence()
    {
        var snapshot = await TestCoverageFixture.AnalyzeAsync(_db, TestCoverageFixture.Zip(JourneyFixtureFiles()), "journey-fixture.zip");
        var store = Store();
        store.Declare(Request("Order notification", Notification), Now).Error.Should().BeNull();
        store.Declare(Request("Order archive", Archive), Now).Error.Should().BeNull();
        var service = new TestCoverageReviewService(_db, new ReviewSourceEvidenceProvider(new IqrSourceStore(_db)), declaredJourneys: store);

        var r = (await service.RunAsync(new TestCoverageReviewRequest { CurrentSnapshotId = snapshot.Id, Workspace = new CoverageWorkspaceEvidence(), ProjectKey = Project })).Result!;

        var matching = r.Journeys.Single(j => j.Title == MatchingTitle);
        var candidate = matching.AssociationCandidates.Should().ContainSingle().Subject;
        (candidate.IntegrationJourneyId, candidate.Match, candidate.DisplayName).Should().Be(($"project.{Project}:order-notification", "Equal", "Order notification"));
        candidate.SourceChain.Should().Equal(Notification);
        candidate.DeclaredChain.Should().Equal(Notification);
        r.Journeys.Should().Contain(j => j.Id != matching.Id && j.Title.StartsWith("Orders.Adapter → Orders.Api", StringComparison.Ordinal),
            "a similar source journey (same prefix, other boundary) exists");
        r.Journeys.Where(j => j.Id != matching.Id).Should().OnlyContain(j => j.AssociationCandidates.Count == 0, "a similar chain or a similar name is never a candidate");
        r.Journeys.SelectMany(j => j.AssociationCandidates).Should().NotContain(c => c.IntegrationJourneyId.EndsWith(":order-archive", StringComparison.Ordinal));

        var other = (await service.RunAsync(new TestCoverageReviewRequest { CurrentSnapshotId = snapshot.Id, ProjectKey = "import:other" })).Result!;
        other.Journeys.SelectMany(j => j.AssociationCandidates).Should().BeEmpty("another project's declared journeys are never read");
        var none = (await service.RunAsync(new TestCoverageReviewRequest { CurrentSnapshotId = snapshot.Id })).Result!;
        none.Journeys.SelectMany(j => j.AssociationCandidates).Should().BeEmpty("without a project nothing declared is read");
    }

    // ── Scope Registry: declared journeys are Journey members ─────────────────────────────────────────────────────────────

    sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    private static ScopeMemberCatalogService Catalog(DeclaredIntegrationJourneyStore? declared)
    {
        var db = new CanonicalInterpretationDbContext(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"declared-scope-{Guid.NewGuid():N}").Options);
        return new ScopeMemberCatalogService(new CanonicalInterpretationService(db, new NoSources(), TimeProvider.System), declaredJourneys: declared);
    }

    [Fact]
    public async Task The_scope_catalog_lists_and_checks_declared_journeys_by_their_project_scoped_id()
    {
        var store = Store();
        var (journey, _, _) = store.Declare(Request("Order notification", Notification), Now);
        var catalog = Catalog(store);

        var page = await catalog.SearchAsync(ScopeMemberKind.Journey, "env-1", null, 0, 25, default);
        page.Available.Should().BeTrue();
        var item = page.Items.Should().ContainSingle().Subject;
        (item.TargetId, item.Label, item.SourceModule).Should().Be((journey!.Id, "Order notification", ScopeMemberCatalogService.DeclaredJourneyModule));
        item.Description.Should().Contain(Project).And.Contain("Orders.Adapter → Orders.Api → order-events → Orders.Notifier");
        (await catalog.SearchAsync(ScopeMemberKind.Journey, "env-1", "notification", 0, 25, default)).Items.Should().ContainSingle();

        static ScopeMember M(string id) => new() { Kind = ScopeMemberKind.Journey, TargetId = id, Label = id };
        var statuses = await catalog.CheckAsync([M(journey.Id), M($"project.{Project}:invented")], "env-1", default);
        statuses[0].Should().Match<ScopeMemberStatus>(s => s.Check == ScopeMemberCheck.Current && s.CurrentLabel == "Order notification");
        statuses[1].Check.Should().Be(ScopeMemberCheck.Missing);

        store.Remove(Project, journey.JourneyId);
        (await catalog.CheckAsync([M(journey.Id)], "env-1", default)).Single().Check.Should().Be(ScopeMemberCheck.Missing, "a removed journey reads as missing; the scope is not changed");
        (await Catalog(null).SearchAsync(ScopeMemberKind.Journey, "env-1", null, 0, 25, default)).Available.Should().BeFalse();
    }

    // ── Test Data consumer references of QA tests ─────────────────────────────────────────────────────────────────────────

    private static TestDataConsumer Consumer(string qaId, string? project = Project) =>
        new() { ConsumerType = CoverageTestDataConsumers.ConsumerType, ConsumerId = CoverageTestDataConsumers.ConsumerId(project, qaId), DisplayName = qaId };

    private static readonly TestDataAssetSummary Customer = new() { Id = "authored:order-customer", AssetId = "tda_customer", Alias = "order-customer", Fingerprint = "sha-rev-2" };

    private static QaTestCandidate Qa(string id) => new() { Id = id, Title = id, Source = "QA automated test (Orders.Acceptance.Tests)", Ownership = TestOwnership.QaAutomated };

    [Fact]
    public void Consumer_references_give_asset_id_revision_and_readiness_and_absence_stays_not_assessed()
    {
        var requirementPlace = new ConsumerTestDataRequirement { Id = "req-place", Consumer = Consumer("place"), Usage = "Order customer", RequestedDataType = TestDataDataType.JsonPayload };
        var requirementCancel = new ConsumerTestDataRequirement { Id = "req-cancel", Consumer = Consumer("cancel"), Usage = "Shipped order", RequestedDataType = TestDataDataType.JsonPayload };
        var usages = new List<TestDataUsageReference>
        {
            new() { Id = "use-1", AssetId = Customer.Id, AssetFingerprint = "sha-rev-2", Consumer = Consumer("place"), Usage = "Order customer", RequirementId = "req-place", Status = TestDataUsageStatus.Resolved },
            new() { Id = "use-2", AssetId = Customer.Id, AssetFingerprint = "sha-rev-1", Consumer = Consumer("stale"), Usage = "Order customer", Status = TestDataUsageStatus.NeedsReview, StatusReason = "The asset changed after it was bound." },
            new() { Id = "use-3", AssetId = Customer.Id, AssetFingerprint = "sha-rev-2", Consumer = Consumer("place", "import:other"), Usage = "Other project" },
        };

        var qa = TestCoverageDataLinks.WithConsumerReferences([Qa("place"), Qa("cancel"), Qa("stale"), Qa("list")], Project, usages, [requirementPlace, requirementCancel], [Customer])
            .ToDictionary(q => q.Id);

        qa["place"].Should().Match<QaTestCandidate>(q => q.DataNeed == CoverageDataNeed.Linked && q.TestData.Count == 1);
        qa["place"].TestData[0].Should().Match<CoverageTestDataLink>(l => l.AssetId == "tda_customer" && l.Alias == "order-customer" && l.Fingerprint == "sha-rev-2" && l.Resolved
            && l.Source.StartsWith(TestCoverageDataLinks.ConsumerSource, StringComparison.Ordinal));
        qa["cancel"].Should().Match<QaTestCandidate>(q => q.DataNeed == CoverageDataNeed.RequiredButMissing && q.TestData.Count == 0 && q.DataNeedBasis!.Contains("no asset is bound"));
        qa["stale"].Should().Match<QaTestCandidate>(q => q.DataNeed == CoverageDataNeed.LinkNeedsReview && !q.TestData.Single().Resolved && q.DataNeedBasis!.Contains("changed after it was bound"),
            "an unresolved revision is shown, never counted as linked");
        qa["list"].Should().Match<QaTestCandidate>(q => q.DataNeed == CoverageDataNeed.NotAssessed && q.TestData.Count == 0 && q.DataNeedBasis == null, "no reference is never 'no data needed'");
    }

    [Fact]
    public void Removing_the_binding_turns_a_linked_test_back_into_a_stated_need()
    {
        var requirement = new ConsumerTestDataRequirement { Id = "req-place", Consumer = Consumer("place"), Usage = "Order customer", RequestedDataType = TestDataDataType.JsonPayload };
        var bound = new TestDataUsageReference { Id = "use-1", AssetId = Customer.Id, AssetFingerprint = "sha-rev-2", Consumer = Consumer("place"), RequirementId = "req-place", Usage = "Order customer" };

        TestCoverageDataLinks.WithConsumerReferences([Qa("place")], Project, [bound], [requirement], [Customer]).Single().DataNeed.Should().Be(CoverageDataNeed.Linked);
        TestCoverageDataLinks.WithConsumerReferences([Qa("place")], Project, [], [requirement], [Customer]).Single().DataNeed.Should().Be(CoverageDataNeed.RequiredButMissing);
        TestCoverageDataLinks.WithConsumerReferences([Qa("place")], Project, [bound with { Status = TestDataUsageStatus.NeedsReview, StatusReason = "The asset is deprecated." }], [requirement], [Customer])
            .Single().Should().Match<QaTestCandidate>(q => q.DataNeed == CoverageDataNeed.LinkNeedsReview && q.TestData.Single().StateReason == "The asset is deprecated.");
        TestCoverageDataLinks.WithConsumerReferences([Qa("place")], Project, [], [requirement with { Required = false }], [Customer]).Single().DataNeed
            .Should().Be(CoverageDataNeed.NotAssessed, "an optional need without a binding states no requirement");
    }
}
