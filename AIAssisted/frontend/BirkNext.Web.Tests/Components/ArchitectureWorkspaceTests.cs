using BirkNext.DatabaseArchitecture;
using BirkNext.SourceArchitecture;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>Source Analysis → Architecture: views, filters, structural tables, node/edge evidence, upstream/downstream, path, database drilldown, changes, export.</summary>
public sealed class ArchitectureWorkspaceTests : BunitContext
{
    private static ArchitectureEvidence Ev(string file, int line, string what) => new(ArchitectureEvidenceKind.ApplicationSource, file, line, "Program", "HttpClient", what);

    /// <summary>Storefront → (HTTP) Catalog.Api → (DB) catalog database; Catalog.Api → (publish) topic → (consume) Search.Worker; Storefront → unresolved; Catalog.Api → library.</summary>
    internal static ArchitectureSnapshot Model() => new()
    {
        SourceSnapshotId = Guid.Parse("11111111-2222-3333-4444-555555555555"), SourceFingerprint = "fp-generic", Status = ArchitectureStatus.Partial, AnalyzerVersion = 1,
        Technologies = [new("ASP.NET Core", "Hosting", ["component:Storefront", "component:Catalog.Api"]), new("Azure Service Bus", "Messaging", ["component:Catalog.Api"])],
        Components =
        [
            new() { Id = "component:Storefront", Name = "Storefront", ComponentType = ArchitectureComponentType.Frontend, RuntimeType = "Blazor WebAssembly (browser)", Module = "Shop", SourceProject = "Shop/Storefront/Storefront.csproj", Technologies = ["ASP.NET Core"] },
            new() { Id = "component:Catalog.Api", Name = "Catalog.Api", ComponentType = ArchitectureComponentType.Api, RuntimeType = "ASP.NET Core web app", Module = "Shop", SourceProject = "Shop/Catalog.Api/Catalog.Api.csproj", Technologies = ["ASP.NET Core", "Azure Service Bus"] },
            new() { Id = "component:Search.Worker", Name = "Search.Worker", ComponentType = ArchitectureComponentType.Worker, RuntimeType = ".NET worker", Module = "Search", SourceProject = "Search/Search.Worker/Search.Worker.csproj" },
        ],
        Dependencies =
        [
            new() { Id = "d-http", FromComponentId = "component:Storefront", ToId = "component:Catalog.Api", TargetReference = "CatalogApi:BaseUrl", DependencyType = ArchitectureDependencyType.Http, Protocol = "HTTP",
                ConfigurationReference = "CatalogApi:BaseUrl", Framework = "Named HttpClient", EvidenceState = ArchitectureEvidenceState.StronglySupported, Confidence = "Application registration + wiring of CatalogApi:BaseUrl",
                Evidence = [Ev("Shop/Storefront/Program.cs", 2, "Named HttpClient registration"), new(ArchitectureEvidenceKind.DevelopmentOrchestration, "Shop/AppHost/AppHost.cs", 4, "storefront", "Aspire", "WithEnvironment (local development orchestration)")] },
            new() { Id = "d-unres", FromComponentId = "component:Storefront", TargetReference = "Billing:BaseUrl", DependencyType = ArchitectureDependencyType.Http, Protocol = "HTTP", EvidenceState = ArchitectureEvidenceState.Unresolved, Confidence = "Client registered; the target is not identified", Diagnostics = ["Target not resolved — nothing was guessed from names."] },
            new() { Id = "d-db", FromComponentId = "component:Catalog.Api", ToId = "datastore:catalog", DependencyType = ArchitectureDependencyType.DatabaseReadWrite, Protocol = "SqlServer", EvidenceState = ArchitectureEvidenceState.Confirmed, Confidence = "DbContext CatalogDbContext with UseSqlServer" },
            new() { Id = "d-pub", FromComponentId = "component:Catalog.Api", ToId = "channel:servicebus:catalog.changed", DependencyType = ArchitectureDependencyType.ServiceBusPublish, Protocol = "AMQP", EvidenceState = ArchitectureEvidenceState.Confirmed, Confidence = "Entity named in source" },
            new() { Id = "d-sub", FromComponentId = "component:Search.Worker", ToId = "channel:servicebus:catalog.changed", DependencyType = ArchitectureDependencyType.ServiceBusConsume, Protocol = "AMQP", Direction = "Inbound", EvidenceState = ArchitectureEvidenceState.Inferred, Confidence = "Entity name from configuration" },
            new() { Id = "d-lib", FromComponentId = "component:Catalog.Api", ToId = "library:Shared.Contracts", DependencyType = ArchitectureDependencyType.ProjectReference, Protocol = "Build-time reference", EvidenceState = ArchitectureEvidenceState.Confirmed, Confidence = "Explicit ProjectReference (build-time; not a runtime call)" },
            new() { Id = "d-orch", FromComponentId = "component:Storefront", ToId = "component:Catalog.Api", DependencyType = ArchitectureDependencyType.OrchestrationDependency, Protocol = "Development orchestration", EvidenceState = ArchitectureEvidenceState.Inferred, Confidence = "AppHost waits for this component locally" },
        ],
        MessagingChannels = [new() { Id = "channel:servicebus:catalog.changed", Name = "catalog.changed", Type = MessagingChannelType.ServiceBusTopic, EntityName = "catalog.changed", Subscription = "search-sub",
            Producers = [new("component:Catalog.Api", ChannelRole.Producer, ArchitectureEvidenceState.Confirmed, null, [])], Consumers = [new("component:Search.Worker", ChannelRole.Consumer, ArchitectureEvidenceState.Inferred, "Wolverine", [])], Confidence = ArchitectureEvidenceState.Confirmed }],
        DataStores = [new() { Id = "datastore:catalog", LogicalName = "CatalogDb", StoreType = DataStoreType.SqlServer, Usage = "Application database", ReferencedByComponents = ["component:Catalog.Api"], ConnectionReference = "ConnectionStrings:CatalogDb", DbContext = "CatalogDbContext", DatabaseModelId = "db-catalog" }],
        SharedLibraries = [new("library:Shared.Contracts", "Shared.Contracts", "Shop/Shared.Contracts/Shared.Contracts.csproj", ["component:Catalog.Api"], true)],
        ConfigurationReferences = [new("component:Storefront", "CatalogApi:BaseUrl", "Endpoint", ["Shop/Storefront/appsettings.json"], [])],
        Diagnostics = [new("Unresolved target", "Storefront: Http target not identified (Billing:BaseUrl)."), new("Local orchestration only", ".NET Aspire AppHost wiring is development orchestration evidence.")],
        Limitations = [ArchitectureSnapshot.SourceLimitation],
    };

    private string? _openedDatabase;

    public ArchitectureWorkspaceTests()
    {
        Services.AddSingleton<IReportExportService, ReportExportService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/architectureGraph.js");
        JSInterop.SetupModule("./js/databaseDiagram.js");
    }

    private IRenderedComponent<ArchitectureWorkspace> RenderModel(IReadOnlyList<(string, ArchitectureSnapshot)>? previous = null) => Render<ArchitectureWorkspace>(p => p
        .Add(c => c.Snapshot, Model()).Add(c => c.Previous, previous ?? []).Add(c => c.OpenDatabase, (string id) => _openedDatabase = id));

    private static void Tab(IRenderedComponent<ArchitectureWorkspace> cut, string tab) => cut.Find($"[data-testid=arch-tab-{tab}]").Click();

    [Fact]
    public void Overview_StatesTheSourceOnlyBoundary_CountsAndDiagnostics_WithoutPassFail()
    {
        var cut = RenderModel();
        cut.Find("[data-testid=arch-limitation]").TextContent.Should().Be(ArchitectureSnapshot.SourceLimitation);
        cut.Find("[data-testid=arch-status]").TextContent.Should().Be("Partial");
        var overview = cut.Find("[data-testid=arch-overview]").TextContent;
        overview.Should().Contain("Components3").And.Contain("Messaging channels1").And.Contain("Datastores1").And.Contain("Unresolved1").And.Contain("Inferred1");
        cut.Find("[data-testid=arch-diagnostics]").TextContent.Should().Contain("limitations, not defects").And.Contain("Billing:BaseUrl");
        cut.Markup.Should().NotContain(">Passed<").And.NotContain(">Failed<");
        cut.FindAll("nav[aria-label='Architecture views'] button").Select(b => b.TextContent).Take(7).Should().Equal(["Overview", "System", "Integrations", "Messaging", "Data", "Dependencies", "Changes"]);
    }

    [Fact]
    public void SystemView_ShowsRuntimeRelationshipsOnly_AndLibrariesOnlyOnRequest()
    {
        var a = Model();
        var system = ArchitecturePresentation.Build(a, SourceArchitectureView.System, new());
        system.Edges.Select(e => e.DependencyId).Should().BeEquivalentTo(["d-http", "d-unres", "d-db", "d-pub", "d-sub"], "project references and orchestration are not runtime relationships");
        system.Nodes.Should().Contain(n => n.Id == "unresolved:component:Storefront" && n.Name == "Unresolved targets (1)" && n.Kind == "unresolved", "one grouped node per component");
        ArchitecturePresentation.UnresolvedOf(a, "unresolved-edge:component:Storefront").Single().TargetReference.Should().Be("Billing:BaseUrl");
        system.Edges.Single(e => e.DependencyId == "d-sub").Should().Match<ArchitectureGraphEdge>(e => e.From == "channel:servicebus:catalog.changed" && e.To == "component:Search.Worker", "a consumer is drawn channel → consumer");
        system.Edges.Single(e => e.DependencyId == "d-sub").Label.Should().Be("Service Bus consume (inferred)", "state is in the text, not only in the line style");
        ArchitecturePresentation.Build(a, SourceArchitectureView.System, new() { ShowLibraries = true }).Nodes.Should().Contain(n => n.Id == "library:Shared.Contracts");
        ArchitecturePresentation.Build(a, SourceArchitectureView.System, new() { ConfirmedOnly = true }).Edges.Select(e => e.DependencyId).Should().BeEquivalentTo(["d-http", "d-db", "d-pub"]);
        ArchitecturePresentation.Build(a, SourceArchitectureView.System, new() { HideUnresolved = true }).Nodes.Should().NotContain(n => n.Kind == "unresolved");
        ArchitecturePresentation.Build(a, SourceArchitectureView.System, new() { IncludeInferred = false }).Edges.Should().NotContain(e => e.DependencyId == "d-sub");
        ArchitecturePresentation.Build(a, SourceArchitectureView.System, new() { ComponentType = "Worker" }).Nodes.Where(n => n.Kind == "component").Select(n => n.Name).Should().Equal(["Search.Worker"]);
        ArchitecturePresentation.Build(a, SourceArchitectureView.System, new() { Protocol = "Http" }).Edges.Should().OnlyContain(e => e.DependencyId.StartsWith("d-http") || e.DependencyId == "d-unres");
        var many = a with { Dependencies = [.. a.Dependencies, a.Dependencies.Single(d => d.Id == "d-unres") with { Id = "d-unres2", TargetReference = "Tax:BaseUrl" }] };
        var grouped = ArchitecturePresentation.Build(many, SourceArchitectureView.System, new());
        grouped.Nodes.Count(n => n.Kind == "unresolved").Should().Be(1);
        grouped.Edges.Single(e => e.To == "unresolved:component:Storefront").Label.Should().Be("2 unresolved");
    }

    [Fact]
    public void MessagingAndDataViews_StayFocused_AndDataNeverShowsTables()
    {
        var a = Model();
        var messaging = ArchitecturePresentation.Build(a, SourceArchitectureView.Messaging, new());
        messaging.Nodes.Select(n => n.Id).Should().BeEquivalentTo(["component:Catalog.Api", "component:Search.Worker", "channel:servicebus:catalog.changed"]);
        var data = ArchitecturePresentation.Build(a, SourceArchitectureView.Data, new());
        data.Nodes.Select(n => n.Id).Should().BeEquivalentTo(["component:Catalog.Api", "datastore:catalog"]);
        data.Nodes.Single(n => n.Kind == "datastore").Subtitle.Should().Be("SQL Server · Application database");
    }

    [Fact]
    public void UpstreamDownstreamAndPath_UseOnlyExtractedRelationships()
    {
        var g = ArchitecturePresentation.Build(Model(), SourceArchitectureView.System, new());
        ArchitecturePresentation.Reachable(g, "component:Catalog.Api", downstream: true, 1).Should().BeEquivalentTo(["component:Catalog.Api", "datastore:catalog", "channel:servicebus:catalog.changed"]);
        ArchitecturePresentation.Reachable(g, "component:Catalog.Api", downstream: true, 999).Should().Contain("component:Search.Worker");
        ArchitecturePresentation.Reachable(g, "component:Catalog.Api", downstream: false, 999).Should().BeEquivalentTo(["component:Catalog.Api", "component:Storefront"]);
        ArchitecturePresentation.FindPath(g, "component:Storefront", "component:Search.Worker").Should().Equal(["component:Storefront", "component:Catalog.Api", "channel:servicebus:catalog.changed", "component:Search.Worker"]);
        ArchitecturePresentation.FindPath(g, "component:Search.Worker", "component:Storefront").Should().BeNull();
        ArchitecturePresentation.NoPath.Should().Be("No source-derived path found.");
        ArchitecturePresentation.Search(Model(), "catalogapi:baseurl").Select(n => n.Id).Should().Equal(["component:Storefront"], "configuration keys lead to the component that reads them");
    }

    [Fact]
    public void SystemTab_EdgeAndNodeDetailsCarryEvidence_AndUpstreamDownstreamPathWork()
    {
        var cut = RenderModel();
        Tab(cut, "system");
        cut.Find("[data-testid=arch-filters]").TextContent.Should().Contain("Component type").And.Contain("Confirmed only").And.Contain("Include inferred").And.Contain("Hide unresolved").And.Contain("Libraries");
        cut.Find("[data-testid=arch-canvas]").GetAttribute("aria-label").Should().Contain("table below lists the same relationships");
        var edgeTable = cut.Find("[data-testid=arch-edge-table]");
        edgeTable.QuerySelectorAll("tbody tr").Length.Should().Be(5);
        cut.FindAll("[data-testid=arch-edge-table] button").First(b => b.TextContent == "HTTP").Click();
        var edge = cut.Find("[data-testid=arch-edge-detail]");
        edge.TextContent.Should().Contain("Storefront → Catalog.Api").And.Contain("CatalogApi:BaseUrl").And.Contain("DevelopmentOrchestration").And.Contain("Shop/Storefront/Program.cs:2")
            .And.Contain("not observed runtime traffic");
        cut.Find("[data-testid=arch-edge-state]").TextContent.Should().Be("Strongly supported");

        cut.Find("[data-testid=arch-search]").Input("Catalog");
        cut.Find("[data-testid=arch-search-results]").TextContent.Should().Contain("Catalog.Api").And.Contain("CatalogDb");
        cut.FindAll("[data-testid=arch-search-results] button").First(b => b.TextContent == "Catalog.Api").Click();
        var node = cut.Find("[data-testid=arch-node-detail]");
        node.TextContent.Should().Contain("Outgoing").And.Contain("DB read/write").And.Contain("Incoming").And.Contain("Shop/Catalog.Api/Catalog.Api.csproj");
        cut.Find("[data-testid=arch-downstream]").Click();
        cut.Find("[data-testid=arch-message]").TextContent.Should().Contain("Downstream of Catalog.Api: 2 node(s)");
        cut.Find("[data-testid=arch-upstream]").Click();
        cut.Find("[data-testid=arch-message]").TextContent.Should().Contain("Upstream of Catalog.Api: 1 node(s)");

        cut.Find("[data-testid=arch-path] select").Change("component:Search.Worker");
        cut.FindAll("[data-testid=arch-path] select")[1].Change("component:Storefront");
        cut.Find("[data-testid=arch-find-path]").Click();
        cut.Find("[data-testid=arch-path-result]").TextContent.Should().Be("No source-derived path found.");
        cut.Find("[data-testid=arch-path] select").Change("component:Storefront");
        cut.FindAll("[data-testid=arch-path] select")[1].Change("component:Search.Worker");
        cut.Find("[data-testid=arch-find-path]").Click();
        cut.Find("[data-testid=arch-path-result]").TextContent.Should().Be("Storefront → Catalog.Api → catalog.changed → Search.Worker");
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "render");
    }

    [Fact]
    public void DataTab_LinksToTheSeparateDatabaseArea_OnlyWhenLinked()
    {
        var cut = RenderModel();
        Tab(cut, "data");
        cut.Find("[data-testid=arch-data-table]").TextContent.Should().Contain("CatalogDb").And.Contain("Available").And.Contain("ConnectionStrings:CatalogDb");
        cut.Find("[data-testid=arch-data-table] button").Click();
        cut.Find("[data-testid=arch-db-link-state]").TextContent.Should().Be("Available");
        cut.Find("[data-testid=arch-node-detail]").TextContent.Should().Contain("Tables are shown only in the separate Database area");
        cut.Find("[data-testid=arch-open-database]").Click();
        _openedDatabase.Should().Be("db-catalog");
    }

    [Fact]
    public void MessagingTab_TableShowsProducersConsumersAndFramework()
    {
        var cut = RenderModel();
        Tab(cut, "messaging");
        cut.Find("[data-testid=arch-messaging-table]").TextContent.Should().Contain("catalog.changed").And.Contain("Service Bus topic").And.Contain("Catalog.Api (Confirmed)")
            .And.Contain("Search.Worker (Inferred, Wolverine)").And.Contain("search-sub");
    }

    [Fact]
    public void DependenciesTab_IsTheSearchableStructuralAlternative()
    {
        var cut = RenderModel();
        Tab(cut, "dependencies");
        cut.FindAll("[data-testid=arch-dep-row]").Should().HaveCount(7, "every extracted dependency, including build-time and orchestration ones");
        cut.Find("[data-testid=arch-dep-search]").Input("Billing");
        var row = cut.Find("[data-testid=arch-dep-row]");
        row.GetAttribute("data-state").Should().Be("Unresolved");
        row.TextContent.Should().Contain("Unresolved: Billing:BaseUrl").And.Contain("No");
        row.QuerySelector("button")!.Click();
        cut.Find("[data-testid=arch-edge-detail]").TextContent.Should().Contain("nothing was guessed from names");
    }

    [Fact]
    public void Changes_AreSourceArchitectureChanges_NeverDeploymentDrift()
    {
        var cut = RenderModel();
        Tab(cut, "changes");
        cut.Find("[data-testid=arch-no-previous]").TextContent.Should().Contain("Another analyzed source snapshot is required");
        var older = Model() with { SnapshotId = Guid.NewGuid(), Components = Model().Components.Where(c => c.Name != "Search.Worker").ToList(), Dependencies = Model().Dependencies.Where(d => d.Id != "d-sub").ToList() };
        var with = RenderModel([("older.zip", older)]);
        Tab(with, "changes");
        with.Find("[data-testid=arch-change-label]").TextContent.Should().Contain("not deployment drift");
        var changes = with.FindAll("[data-testid=arch-changes] li");
        changes.Should().Contain(li => li.GetAttribute("data-kind") == "Added" && li.TextContent.Contains("Component") && li.TextContent.Contains("Search.Worker"));
        changes.Should().Contain(li => li.GetAttribute("data-kind") == "Added" && li.TextContent.Contains("Dependency"));
    }

    [Fact]
    public void Export_CarriesSummaryEvidenceUnresolvedAndLimitations_WithoutTables()
    {
        var html = new ReportExportService().ExportSourceArchitecture(Model());
        var body = html[html.IndexOf("<body", StringComparison.Ordinal)..];
        body.Should().Contain(ArchitectureSnapshot.SourceLimitation).And.Contain("fp-generic").And.Contain("Storefront").And.Contain("Strongly supported")
            .And.Contain("Unresolved: Billing:BaseUrl").And.Contain("catalog.changed").And.Contain("Datastores (no tables").And.Contain("Local orchestration only");
    }

    [Fact]
    public void DatabaseWorkspace_FocusParameterOpensTheLinkedDatabaseDiagram()
    {
        var database = new DatabaseArchitectureSnapshot { Databases = [new DatabaseModel { Id = "db-catalog", LogicalName = "CatalogDb" }, new DatabaseModel { Id = "db-other", LogicalName = "Other" }] };
        var cut = Render<DatabaseWorkspace>(p => p.Add(c => c.Snapshot, database).Add(c => c.FocusDatabaseId, "db-catalog"));
        cut.FindAll("button").Should().Contain(b => b.TextContent == "Fit to screen", "the diagram opens");
        cut.FindAll("select").First().GetAttribute("value").Should().Be("db-catalog");
        Render<DatabaseWorkspace>(p => p.Add(c => c.Snapshot, database)).FindAll("button").Should().NotContain(b => b.TextContent == "Fit to screen", "without a focus the Database area opens as before");
    }
}
