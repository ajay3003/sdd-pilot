using BirkNext.DatabaseArchitecture;
using BirkNext.SourceArchitecture;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Source Analysis detail hierarchy — summary first, structured detail, evidence drill-down — for Architecture and Database. Presentation
/// only: every count comes from the snapshot, every evidence item stays reachable, states keep their model meaning. Generic fixtures.
/// </summary>
public sealed class SourceAnalysisDetailUxTests : BunitContext
{
    public SourceAnalysisDetailUxTests()
    {
        Services.AddSingleton<IReportExportService, ReportExportService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/architectureGraph.js");
        JSInterop.SetupModule("./js/databaseDiagram.js");
    }

    private static ArchitectureSnapshot Arch()
    {
        var a = ArchitectureWorkspaceTests.Model();
        return a with
        {
            Technologies = [.. Enumerable.Range(1, 10).Select(i => new ArchitectureTechnology($"Tech {i:00}", "Hosting", [.. Enumerable.Range(0, i % 4).Select(n => $"component:{n}")]))],
            Diagnostics = [new("Unresolved target", "Storefront: Http target not identified (Billing:BaseUrl).", "component:Storefront|Http|Billing:BaseUrl"),
                new("Unresolved target", "Catalog.Api: Http target not identified (Tax:BaseUrl).", "component:Catalog.Api|Http|Tax:BaseUrl"),
                new("Consumer not found", "orders: no consumer found in this source snapshot.", "channel:servicebus:catalog.changed"), new("Deployment not assessed", "Source architecture only.")],
            Limitations = [ArchitectureSnapshot.SourceLimitation, "No dependency detected does not mean no dependency exists."],
        };
    }

    private IRenderedComponent<ArchitectureWorkspace> RenderArch(Action? choose = null) => Render<ArchitectureWorkspace>(p =>
    {
        p.Add(c => c.Snapshot, Arch());
        if (choose is not null) p.Add(c => c.ChooseSnapshot, choose);
    });

    // ── Architecture ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DependencyEvidence_StatesAreExclusiveAndAddUp_TargetIdentificationIsASeparateDimension()
    {
        var a = Arch();
        var e = ArchitectureEvidenceSummary.Dependencies(a);
        e.All.Should().Be(7);
        e.Runtime.Should().Be(a.Dependencies.Count(d => ArchitecturePresentation.IsRuntime(d.DependencyType)));
        e.StateTotal.Should().Be(e.Runtime, "each dependency has exactly one evidence state");
        (e.Confirmed, e.StronglySupported, e.Inferred, e.Unresolved, e.Conflict).Should().Be((2, 1, 1, 1, 0));
        e.NonRuntime.Should().Be(2, "the project reference and the orchestration link are not runtime relationships");
        e.TargetNotIdentified.Should().Be(a.UnresolvedItems.Count());
        // An Unresolved-state dependency can still point to a node, so the two dimensions are not interchangeable.
        var pointing = a with { Dependencies = [.. a.Dependencies, new ArchitectureDependency { Id = "x", FromComponentId = "component:Storefront", ToId = "channel:unresolved", DependencyType = ArchitectureDependencyType.ServiceBusPublish, EvidenceState = ArchitectureEvidenceState.Unresolved }] };
        var p = ArchitectureEvidenceSummary.Dependencies(pointing);
        (p.Unresolved, p.TargetNotIdentified).Should().Be((2, 1));

        var cut = RenderArch();
        cut.Find("[data-testid=arch-dep-evidence-note]").TextContent.Should().Contain($"{e.Runtime} runtime dependencies").And.Contain("add up to");
        cut.FindAll("[data-testid=arch-dep-states] > div").Select(d => d.GetAttribute("data-state")).Should().Equal("Confirmed", "StronglySupported", "Inferred", "Unresolved", "Conflict");
        cut.Find("[data-testid=arch-dep-target]").TextContent.Should().Contain("Separate dimension").And.Contain($"{e.TargetNotIdentified} of {e.All}");
    }

    [Fact]
    public void Overview_IsSummaryFirst_TechnologiesCompact_UnresolvedGrouped_LimitationsSeparate()
    {
        var cut = RenderArch();
        cut.FindAll("[data-testid=arch-technologies] > ul [data-testid=arch-tech]").Should().HaveCount(8, "the first eight are shown");
        cut.Find("[data-testid=arch-tech]").TextContent.Should().Contain("Tech 03", "most components first");
        cut.Find("[data-testid=arch-tech-all-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll("[data-testid=arch-tech]").Should().HaveCount(8, "the rest is not rendered while collapsed");
        cut.Find("[data-testid=arch-tech-all-toggle]").Click();
        cut.FindAll("[data-testid=arch-tech]").Should().HaveCount(10, "the full inventory stays reachable");

        cut.Find("[data-testid=arch-unresolved-count]").TextContent.Should().Be("4");
        cut.FindAll("[data-testid=arch-unresolved-groups] > div").Select(d => (d.GetAttribute("data-kind"), d.QuerySelector("dd")!.TextContent))
            .Should().Equal(("Unresolved target", "2"), ("Consumer not found", "1"), ("Deployment not assessed", "1"));
        cut.FindAll("[data-testid=arch-unresolved-row]").Should().BeEmpty("raw unresolved rows are a drill-down");
        cut.Find("[data-testid=arch-limitations]").TextContent.Should().Contain("Analysis limitations").And.Contain("2");
        cut.Find("[data-testid=arch-limitations-detail-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=arch-diagnostics]").TextContent.Should().NotContain("No dependency detected", "general limitations are not unresolved evidence");
    }

    [Fact]
    public void UnresolvedEvidence_IsAFilterableTable_WithEveryItem()
    {
        var cut = RenderArch();
        cut.Find("[data-testid=arch-unresolved-detail-toggle]").Click();
        cut.FindAll("[data-testid=arch-unresolved-row]").Should().HaveCount(4);
        cut.FindAll("[data-testid=arch-unresolved-row]")[0].TextContent.Should().Contain("Storefront");
        cut.Find("[data-testid=arch-unresolved-type]").Change("Unresolved target");
        cut.FindAll("[data-testid=arch-unresolved-row]").Should().HaveCount(2);
        cut.Find("[data-testid=arch-unresolved-component]").Change("component:Catalog.Api");
        cut.FindAll("[data-testid=arch-unresolved-row]").Single().TextContent.Should().Contain("Tax:BaseUrl");
        cut.Find("[data-testid=arch-unresolved-type]").Change("");
        cut.Find("[data-testid=arch-unresolved-component]").Change("");
        cut.Find("[data-testid=arch-unresolved-search]").Input("consumer");
        cut.Find("[data-testid=arch-unresolved-shown]").TextContent.Should().Be("1 of 4 shown");
    }

    [Theory]
    [InlineData("system", "Show relationships in this view")]
    [InlineData("messaging", "Show channels")]
    [InlineData("data", "Show datastore usage")]
    public void GraphViews_KeepTheGraphPrimary_SupportingTableCollapsedWithItsCount(string tab, string label)
    {
        var cut = RenderArch();
        cut.Find($"[data-testid=arch-tab-{tab}]").Click();
        cut.Find("[data-testid=arch-canvas]").Should().NotBeNull();
        var toggle = cut.Find("[data-testid=arch-view-table-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.TextContent.Should().Contain(label).And.MatchRegex(@"· \d+");
        cut.Find("[data-testid=arch-view-table-body]").HasAttribute("hidden").Should().BeTrue();
        toggle.Click();
        cut.Find("[data-testid=arch-view-table-body]").HasAttribute("hidden").Should().BeFalse();
        cut.FindAll("[data-testid=arch-toolbar] [role=group]").Select(g => g.GetAttribute("aria-label")).Should().Equal("View", "Layout", "Export", "Selection");
        cut.FindAll("[data-testid=arch-filters] [role=group]").Should().HaveCount(3, "search, filter and evidence groups");
        cut.Find("[data-testid=arch-legend]").TextContent.Should().Contain("Inferred").And.Contain("Unresolved").And.Contain("Conflict").And.Contain("Confirmed / strongly supported");
    }

    [Fact]
    public void Dependencies_DefaultRowIsStructural_EvidenceAndConfidenceInTheDetail()
    {
        var cut = RenderArch();
        cut.Find("[data-testid=arch-tab-dependencies]").Click();
        cut.FindAll("[data-testid=arch-dependencies] thead th").Select(t => t.TextContent).Should().Equal("From", "To / target reference", "Type", "Protocol", "State", "Source");
        cut.FindAll("[data-testid=arch-dep-row]").Should().HaveCount(7, "every dependency remains");
        var http = cut.FindAll("[data-testid=arch-dep-row]").First(r => r.TextContent.Contains("Catalog.Api") && r.TextContent.Contains("Http"));
        http.TextContent.Should().NotContain("Application registration", "confidence is in the detail, not the row");
        var source = http.QuerySelector("button")!;
        source.GetAttribute("title").Should().Be("Shop/Storefront/Program.cs:2");
        source.GetAttribute("aria-label").Should().Contain("source Shop/Storefront/Program.cs:2");
        source.Click();
        cut.Find("[data-testid=arch-edge-detail]").TextContent.Should().Contain("Application registration").And.Contain("Named HttpClient registration");
    }

    [Fact]
    public void Changes_EmptyState_ExplainsTheComparison_AndOffersTheSnapshotSelector()
    {
        var chose = false;
        var cut = RenderArch(() => chose = true);
        cut.Find("[data-testid=arch-tab-changes]").Click();
        cut.Find("[data-testid=arch-no-previous]").TextContent.Should().Contain("components, dependencies, messaging, data stores").And.Contain("not deployment drift");
        cut.Find("[data-testid=arch-choose-snapshot]").Click();
        chose.Should().BeTrue();
    }

    // ── Database ────────────────────────────────────────────────────────────────────────────────────────────────────

    private static DatabaseEvidence Ev(string type, string path, int line = 1) => new(Guid.Empty, "fp", "Shop.Data", path, "Symbol", line, line, "EF Core syntax", type, $"{type} explanation", DatabaseEvidenceState.Unresolved);

    private static DatabaseArchitectureSnapshot Db() => new()
    {
        SourceFingerprint = "0123456789abcdef", Status = DatabaseAnalysisStatus.Partial, TechnologiesDetected = ["EF Core", "PostgreSQL"],
        Databases =
        [
            new() { Id = "db-shop", LogicalName = "ShopDbContext", Provider = "PostgreSQL", SourceProject = "Shop/Shop.Data/Shop.Data.csproj", EvidenceState = DatabaseEvidenceState.Confirmed, Schemas =
            [
                new() { Name = "sales", Tables =
                [
                    new() { Id = "t-customer", LogicalName = "Customer", SchemaName = "sales", EntityTypeName = "CustomerEntity", SourceProject = "Shop/Shop.Data", EvidenceState = DatabaseEvidenceState.Confirmed, Confidence = "Explicit source assertion",
                        Columns = [new() { Name = "Id", IsPrimaryKey = true }, new() { Name = "Name" }], PrimaryKey = new() { Columns = ["Id"] }, Indexes = [new() { Name = "ix_name", Columns = ["Name"] }],
                        Evidence = [new(Guid.Empty, "fp", "Shop.Data", "Shop/Shop.Data/Customer.cs", "Customer", 3, 9, "EF Core syntax", "Entity", "DbSet<Customer>", DatabaseEvidenceState.Confirmed)] },
                    new() { Id = "t-order", LogicalName = "Order", SchemaName = "sales", SourceProject = "Shop/Shop.Data", EvidenceState = DatabaseEvidenceState.ObservedFromMigration, Columns = [new() { Name = "CustomerId", IsForeignKey = true }],
                        Relationships =
                        [
                            new() { Id = "r1", FromTable = "t-order", ToTable = "t-customer", FromColumns = ["CustomerId"], ToColumns = ["Id"], Cardinality = "N:1", EvidenceState = DatabaseEvidenceState.Confirmed, Confidence = "HasForeignKey", DeleteBehavior = "Cascade",
                                Evidence = [new(Guid.Empty, "fp", "Shop.Data", "Shop/Shop.Data/Migrations/001_Init.cs", "Init", 40, 44, "EF migration syntax", "ForeignKey", "AddForeignKey", DatabaseEvidenceState.ObservedFromMigration)] },
                            new() { Id = "r2", FromTable = "t-order", ToTable = "t-customer", FromColumns = ["BuyerId"], ToColumns = ["Id"], Cardinality = "N:1", EvidenceState = DatabaseEvidenceState.Unresolved, Confidence = "Navigation without FK" },
                            new() { Id = "r3", FromTable = "t-order", ToTable = "t-customer", FromColumns = ["PayerId"], ToColumns = ["Id"], Cardinality = "N:1", EvidenceState = DatabaseEvidenceState.ObservedFromDDL, Confidence = "DDL" },
                        ] },
                ] },
            ] },
            new() { Id = "db-ddl", LogicalName = "DDL source group", Provider = "SQL Server", SourceProject = "Shop/.sql", EvidenceState = DatabaseEvidenceState.ObservedFromDDL, Schemas = [new() { Name = null, Tables = [] }] },
        ],
        UnresolvedEvidence = [Ev("Migration InsertData", "Shop/Shop.Data/Migrations/001_Init.cs"), Ev("Migration InsertData", "Shop/Shop.Data/Migrations/002_Seed.cs"), Ev("HasForeignKey", "Shop/Shop.Data/ShopDbContext.cs", 12)],
        Diagnostics = ["Physical database identity unknown for ShopDbContext; contexts remain separate candidates.", "Migration operations are ordered by source path, not deployment history."],
    };

    private IRenderedComponent<DatabaseWorkspace> RenderDb(DatabaseArchitectureSnapshot? previous = null, Action? choose = null) => Render<DatabaseWorkspace>(p =>
    {
        p.Add(c => c.Snapshot, Db()).Add(c => c.Previous, previous);
        if (choose is not null) p.Add(c => c.ChooseSnapshot, choose);
    });

    private static Dictionary<string, string> Metrics(IRenderedComponent<DatabaseWorkspace> cut) =>
        cut.FindAll("[data-testid=db-overview] > div").ToDictionary(d => d.QuerySelector("dt")!.TextContent, d => d.QuerySelector("dd")!.TextContent);

    [Fact]
    public void DatabaseOverview_IsAMetricDashboard_WithCandidatesTable_AndNeutralUnknownPhysicalDatabase()
    {
        var cut = RenderDb();
        Metrics(cut).Should().Equal(new Dictionary<string, string>
        {
            ["Database candidates"] = "2", ["Schemas"] = "2", ["Tables / entities"] = "2", ["Columns"] = "3", ["Relationships"] = "3", ["Indexes"] = "1", ["Unresolved evidence"] = "3", ["Conflicts"] = "0",
        });
        cut.Find("[data-testid=db-status]").TextContent.Should().Be("Partial");
        cut.FindAll("[data-testid=db-candidate-row]").Select(r => r.QuerySelector("th")!.TextContent).Should().Equal("ShopDbContext", "DDL source group");
        cut.FindAll("[data-testid=db-physical-unknown]").Should().HaveCount(2).And.OnlyContain(e => e.TextContent == "Unknown" && e.ClassList.Contains("db-muted"), "unknown is neutral, not an error");
        cut.Find("[data-testid=db-candidates]").TextContent.Should().Contain("This is not an error");
        cut.FindAll("[data-testid=db-candidate-row]")[1].TextContent.Should().Contain("Observed from DDL");
        cut.Find("[data-testid=db-technologies]").TextContent.Should().Contain("EF Core").And.Contain("PostgreSQL");
        cut.FindAll("button").Select(b => b.TextContent).Should().Contain(["Open Diagram", "View Tables", "View Relationships"]);
        cut.Find("[data-testid=db-open-diagram]").Click();
        cut.FindAll("button").Should().Contain(b => b.TextContent == "Fit to screen");
    }

    [Fact]
    public void RelationshipEvidence_ListsEveryState_AndAddsUpToTheTotal()
    {
        var states = DatabaseEvidenceSummary.RelationshipStates(Db());
        states.Sum(s => s.Count).Should().Be(3, "every relationship has exactly one state");
        var cut = RenderDb();
        cut.FindAll("[data-testid=db-rel-states] > div").Select(d => (d.QuerySelector("dt")!.TextContent, d.QuerySelector("dd")!.TextContent))
            .Should().Equal(("Confirmed", "1"), ("Observed from DDL", "1"), ("Unresolved", "1"));
        cut.Find("[data-testid=db-rel-evidence]").TextContent.Should().Contain("add up to 3");
    }

    [Fact]
    public void DatabaseUnresolvedEvidence_GroupedByType_FilterableDetail_LimitationsSeparateAndCollapsed()
    {
        var cut = RenderDb();
        cut.FindAll("[data-testid=db-unresolved-groups] > div").Select(d => (d.QuerySelector("dt")!.TextContent, d.QuerySelector("dd")!.TextContent)).Should().Equal(("Migration InsertData", "2"), ("HasForeignKey", "1"));
        cut.FindAll("[data-testid=db-unresolved-row]").Should().BeEmpty();
        cut.Find("[data-testid=db-limitations-detail-toggle]").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find("[data-testid=db-limitations-detail-body]").HasAttribute("hidden").Should().BeTrue();
        cut.Find("[data-testid=db-unresolved]").TextContent.Should().NotContain("ordered by source path", "general limitations are not unresolved evidence");
        cut.Find("[data-testid=db-unresolved-detail-toggle]").Click();
        cut.FindAll("[data-testid=db-unresolved-row]").Should().HaveCount(3);
        cut.Find("[data-testid=db-unresolved-type]").Change("HasForeignKey");
        cut.FindAll("[data-testid=db-unresolved-row]").Single().TextContent.Should().Contain("ShopDbContext.cs:12");
        cut.Find("[data-testid=db-unresolved-shown]").TextContent.Should().Be("1 of 3 shown");
    }

    [Fact]
    public void DiagramToolbar_AndFilters_AreGrouped_WithTheSameControls()
    {
        var cut = RenderDb();
        cut.Find("[data-testid=db-tab-diagram]").Click();
        cut.FindAll("[data-testid=db-toolbar] [role=group]").Select(g => g.GetAttribute("aria-label")).Should().Equal("View", "Selection", "Display", "Layout", "Export");
        cut.FindAll("[data-testid=db-toolbar] button").Select(b => b.TextContent).Should().Equal("Fit to screen", "Reset layout", "Focus selected table", "Expand/collapse selected table", "Save layout", "Export SVG");
        cut.FindAll("[data-testid=db-toolbar] [aria-label=Selection] button").Should().OnlyContain(b => b.HasAttribute("disabled"), "selection actions need a selected table");
        cut.FindAll("[data-testid=db-toolbar] [aria-label=Display] label").Select(l => l.TextContent.Trim()).Should().Equal("Relationships", "PK/FK only", "All columns");
        cut.FindAll("[data-testid=db-filters] [role=group]").Should().HaveCount(3);
        cut.Find("[data-testid=db-filters]").TextContent.Should().Contain("Clear related focus").And.Contain("Hide isolated tables");
        cut.Find("[data-testid=db-legend]").TextContent.Should().Contain("Observed from migration/DDL or unresolved", "the legend states what a dotted line means in the diagram");
    }

    [Fact]
    public void DatabaseDiagram_IsALabelledGroup_NeverAnImageWithFocusableChildren()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BirkNext.Web", "wwwroot", "js", "databaseDiagram.js"))) dir = dir.Parent;
        var script = File.ReadAllText(Path.Combine(dir!.FullName, "BirkNext.Web", "wwwroot", "js", "databaseDiagram.js"));
        script.Should().Contain("role: 'group'").And.NotContain("role: 'img'", "table nodes are focusable buttons");
        script.Should().Contain("tabindex: 0, role: 'button'", "tables stay keyboard-selectable");
    }

    [Fact]
    public void Tables_LeadWithTheEntityName_ContextSecondary_EvidenceOnDemand()
    {
        var cut = RenderDb();
        cut.Find("[data-testid=db-tab-tables]").Click();
        cut.FindAll("[data-testid=db-tables] thead th").Take(7).Select(t => t.TextContent).Should().Equal("Table / entity", "Columns", "PK", "FKs", "Indexes", "Evidence", "Project");
        var customer = cut.FindAll("[data-testid=db-table-row]").Single(r => r.GetAttribute("data-table") == "t-customer");
        customer.QuerySelector(".db-name")!.TextContent.Should().Be("Customer");
        customer.QuerySelector(".db-context")!.TextContent.Should().Be("ShopDbContext · sales · CustomerEntity");
        customer.TextContent.Should().Contain("Confirmed").And.Contain("Explicit source assertion");
        cut.FindAll("[data-testid=db-table-row]").Single(r => r.GetAttribute("data-table") == "t-order").TextContent.Should().Contain("Observed from migration");
        cut.FindAll("[data-testid=db-table-evidence]").Should().BeEmpty();
        var toggle = customer.QuerySelector("[data-testid=db-table-evidence-toggle]")!;
        toggle.GetAttribute("aria-expanded").Should().Be("false");
        toggle.Click();
        cut.Find("[data-testid=db-table-evidence]").TextContent.Should().Contain("Shop/Shop.Data/Customer.cs:3");
        cut.Find("input[type=search]").Input("CustomerId");
        cut.FindAll("[data-testid=db-table-row]").Single().GetAttribute("data-table").Should().Be("t-order");
    }

    [Fact]
    public void Relationships_AreScannable_SourceEvidenceExpands_StatesUnchanged()
    {
        var cut = RenderDb();
        cut.Find("[data-testid=db-tab-relationships]").Click();
        cut.FindAll("[data-testid=db-relationships] thead th").Select(t => t.TextContent).Should().Equal("From", "From column(s)", "To", "To column(s)", "Cardinality", "Evidence", "Source");
        var rows = cut.FindAll("[data-testid=db-relationship-row]");
        rows.Should().HaveCount(3, "no relationship is dropped");
        rows.Select(r => r.QuerySelector(".db-pill")!.TextContent).Should().Equal("Confirmed", "Unresolved", "Observed from DDL");
        cut.FindAll("[data-testid=db-relationship-evidence]").Should().BeEmpty();
        rows[0].TextContent.Should().NotContain("001_Init.cs", "source paths are a drill-down");
        rows[0].QuerySelector("[data-testid=db-relationship-evidence-toggle]")!.Click();
        cut.Find("[data-testid=db-relationship-evidence]").TextContent.Should().Contain("Delete behavior: Cascade").And.Contain("Shop/Shop.Data/Migrations/001_Init.cs:40");
        cut.Find("input[type=checkbox]").Change(true);
        cut.FindAll("[data-testid=db-relationship-row]").Should().BeEmpty("Confirmed only keeps its existing meaning: it also filters tables, and Order is observed from a migration");
    }

    [Fact]
    public void DatabaseChanges_EmptyState_AndExistingComparisonWithPrevious()
    {
        var chose = false;
        var cut = RenderDb(choose: () => chose = true);
        cut.Find("[data-testid=db-tab-changes]").Click();
        cut.Find("[data-testid=db-no-previous]").TextContent.Should().Contain("added and removed tables").And.Contain("not deployment drift");
        cut.Find("[data-testid=db-choose-snapshot]").Click();
        chose.Should().BeTrue();

        var previous = Db();
        previous.Databases[0].Schemas[0].Tables.RemoveAt(1);
        var with = RenderDb(previous);
        with.Find("[data-testid=db-tab-changes]").Click();
        with.FindAll("[data-testid=db-changes] li").Select(l => l.TextContent).Should().Contain(t => t.StartsWith("Added table · Order"));
    }

    [Fact]
    public void UnsupportedAreas_ShowNotAssessed_NeverFakeZeroes()
    {
        var db = Render<DatabaseWorkspace>(p => p.Add(c => c.Snapshot, new DatabaseArchitectureSnapshot { Status = DatabaseAnalysisStatus.Unsupported, Diagnostics = ["No EF Core model, migration or SQL DDL was found."] }));
        db.FindAll("[data-testid=db-overview] dd").Should().OnlyContain(d => d.TextContent == "Not assessed");
        db.Find("[data-testid=db-not-assessed]").TextContent.Should().Contain("not assessed (not zero)");
        var arch = Render<ArchitectureWorkspace>(p => p.Add(c => c.Snapshot, new ArchitectureSnapshot { Status = ArchitectureStatus.Unsupported }));
        arch.FindAll("[data-testid=arch-overview] dd").Concat(arch.FindAll("[data-testid=arch-dep-states] dd")).Should().OnlyContain(d => d.TextContent == "Not assessed");
        // A Complete analysis that found nothing is a real zero.
        Render<ArchitectureWorkspace>(p => p.Add(c => c.Snapshot, new ArchitectureSnapshot { Status = ArchitectureStatus.Complete })).FindAll("[data-testid=arch-overview] dd").Should().OnlyContain(d => d.TextContent == "0");
    }

    [Fact]
    public void ExportsStayComplete_RegardlessOfCollapsedUi()
    {
        var html = new ReportExportService().ExportSourceArchitecture(Arch());
        html.Should().Contain("Billing:BaseUrl").And.Contain("Consumer not found").And.Contain(ArchitectureSnapshot.SourceLimitation);
    }
}
