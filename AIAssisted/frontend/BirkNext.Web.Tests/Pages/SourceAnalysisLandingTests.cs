using BirkNext.DatabaseArchitecture;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Source Analysis landing workspace: a purposeful empty state, the current snapshot, and Architecture + Database cards built from their
/// own (separate) models of the SAME selected snapshot. Model status labels only (Partial stays Partial); an area that was not analyzed or
/// is unsupported shows no counts; drill-down keeps the selected snapshot. Generic fixture — no project-specific names.
/// </summary>
public sealed class SourceAnalysisLandingTests : BunitContext
{
    private readonly Mock<IIntegrationCatalogApiService> _api = new();
    private List<IqrSourceSnapshot> _snapshots = [];
    private int _uploads;

    public SourceAnalysisLandingTests()
    {
        _api.Setup(a => a.ListSourceSnapshotsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => _snapshots.ToList());
        _api.Setup(a => a.AnalyzeSourceSnapshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { _uploads++; var s = Snapshot("shop-api.zip", new string('e', 64), DateTimeOffset.Parse("2026-09-30T12:00:00Z"), Arch(ArchitectureStatus.Complete, 2), Db(DatabaseAnalysisStatus.Complete, 1, 0)); _snapshots.Insert(0, s); return (s, (string?)null); });
        var context = new Mock<IFrontendAnalysisContextFactory>();
        context.Setup(c => c.GetActiveContextAsync()).ReturnsAsync(new FrontendAnalysisContext { ActiveProfile = new FrontendAnalysisProfile { Id = "dev", Name = "Dev" } });
        Services.AddSingleton(_api.Object);
        Services.AddSingleton(context.Object);
        Services.AddSingleton<IReportExportService, ReportExportService>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static ArchitectureSnapshot Arch(ArchitectureStatus status, int components, int unresolved = 0) => new()
    {
        Status = status, SourceFingerprint = "fp",
        Components = [.. Enumerable.Range(1, components).Select(i => new ArchitectureComponent { Id = $"c{i}", Name = $"Service {i}" })],
        Dependencies = [.. Enumerable.Range(1, components).Select(i => new ArchitectureDependency { Id = $"d{i}", FromComponentId = $"c{i}", ToId = i <= unresolved ? null : "c1" })],
        MessagingChannels = [new MessagingChannel { Id = "m1", Name = "orders" }],
        DataStores = [new ArchitectureDataStoreReference { Id = "s1", LogicalName = "Orders" }],
        Limitations = status == ArchitectureStatus.Unsupported ? ["No supported .NET project was found."] : [],
    };

    private static DatabaseArchitectureSnapshot Db(DatabaseAnalysisStatus status, int tables, int unresolved)
    {
        var tableModels = Enumerable.Range(1, tables).Select(i => new TableModel
        {
            Id = $"t{i}", LogicalName = $"Table{i}", Indexes = [new IndexModel { Name = $"ix{i}" }],
            Relationships = i > 1 ? [new RelationshipModel { Id = $"r{i}", FromTable = $"t{i}", ToTable = "t1" }] : [],
        }).ToList();
        return new DatabaseArchitectureSnapshot
        {
            Status = status, SourceFingerprint = "fp",
            Databases = tables == 0 ? [] : [new DatabaseModel { Id = "db1", LogicalName = "ShopDb", Schemas = [new SchemaModel { Name = "dbo", Tables = tableModels }] }],
            UnresolvedEvidence = [.. Enumerable.Range(1, unresolved).Select(i => new DatabaseEvidence(Guid.Empty, "fp", "Shop.Data", "Model.cs", "Sym", i, i, "ef", "t", "x", DatabaseEvidenceState.Unresolved))],
            Diagnostics = status == DatabaseAnalysisStatus.Unsupported ? ["No EF Core model, migration or SQL DDL was found."] : [],
        };
    }

    private static IqrSourceSnapshot Snapshot(string file, string sha, DateTimeOffset at, ArchitectureSnapshot? arch, DatabaseArchitectureSnapshot? db, SourceAnalysisStatus status = SourceAnalysisStatus.Ready,
        BirkNext.SourceObservability.SourceObservabilitySnapshot? observability = null)
    {
        var id = Guid.NewGuid();
        if (arch is not null) arch = arch with { SourceSnapshotId = id };
        if (db is not null) db.SourceSnapshotId = id;
        return new IqrSourceSnapshot { Id = id, IntegrationId = "source-analysis", Archive = new SourceArchive(file, sha, 120), AnalyzedAt = at, Status = status, Architecture = arch, DatabaseArchitecture = db,
            Observability = observability is null ? null : observability with { SourceSnapshotId = id } };
    }

    private static IReadOnlyDictionary<string, string> Metrics(IRenderedComponent<SourceAnalysis> cut, string area) =>
        cut.FindAll($"[data-testid=sa-area-card][data-area={area}] [data-testid=sa-metric]").ToDictionary(m => m.GetAttribute("data-metric")!, m => m.QuerySelector("dd")!.TextContent);

    private static string Status(IRenderedComponent<SourceAnalysis> cut, string area) => cut.Find($"[data-testid=sa-area-card][data-area={area}] [data-testid=sa-area-status]").TextContent;

    [Fact]
    public void AReviewCanAskToBeReturnedToButOnlyAnAllowListedInternalRoute()
    {
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("source-analysis?returnTo=security-classification-review");
        var cut = Render<SourceAnalysis>();
        cut.Find("[data-testid=sa-return-link]").GetAttribute("href").Should().Be("security-classification-review");
        cut.Find("[data-testid=sa-return-link]").TextContent.Should().Be("Back to Security Classification");

        nav.NavigateTo("source-analysis?returnTo=https%3A%2F%2Fevil.example.test");
        Render<SourceAnalysis>().FindAll("[data-testid=sa-return]").Should().BeEmpty("an arbitrary URL is never followed");
    }

    [Fact]
    public void EmptyState_IsPurposeful_WithNoFakeCounts()
    {
        var cut = Render<SourceAnalysis>();

        cut.Find("[data-testid=sa-safety]").TextContent.Should().Be("Source only: no live Azure, database, messaging or HTTP connection is used.");
        cut.Find("#sa-empty-heading").TextContent.Should().Be("No source snapshot selected");
        cut.FindAll("[data-testid=sa-preview] li strong").Select(s => s.TextContent).Should().Equal("Architecture", "Database structure", "Observability", "Source evidence", "Snapshot changes");
        cut.Find("[data-testid=sa-empty-upload]").GetAttribute("aria-label").Should().Be("Upload source ZIP (maximum 50 MB)");
        cut.Find("[data-testid=sa-upload]").GetAttribute("accept").Should().Be(".zip");
        cut.Find("[data-testid=sa-choose-existing]").HasAttribute("disabled").Should().BeTrue("there is no stored snapshot to choose");
        cut.Find("#sa-no-snapshots").TextContent.Should().Contain("No stored snapshot yet");
        cut.FindAll("[data-testid=sa-area-card]").Should().BeEmpty();
        cut.FindAll("[data-testid=sa-metric]").Should().BeEmpty();
    }

    [Fact]
    public void ExistingSnapshotsAreChoosable_WithNameDateAndShortFingerprint()
    {
        _snapshots = [Snapshot("shop-api.zip", "c850a1b2" + new string('0', 56), DateTimeOffset.Parse("2026-09-29T08:15:00Z"), Arch(ArchitectureStatus.Complete, 2), Db(DatabaseAnalysisStatus.Complete, 1, 0))];
        var cut = Render<SourceAnalysis>();

        cut.FindAll("[data-testid=source-snapshot] option").Select(o => o.TextContent).Should().Equal("Select a source snapshot", "shop-api.zip · 2026-09-29 08:15 UTC · c850a1b2…");
        cut.Find("[data-testid=source-snapshot]").Change("");
        cut.Find("[data-testid=sa-choose-existing]").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-testid=sa-choose-existing]").Click();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextFocusElement" && (string)i.Arguments[0]! == "sa-snapshot-select");
    }

    [Fact]
    public void SelectedSnapshot_ShowsSummaryAndBothCardsFromThatSnapshot()
    {
        var current = Snapshot("shop-api.zip", "c850a1b2" + new string('0', 56), DateTimeOffset.Parse("2026-09-29T08:15:00Z"), Arch(ArchitectureStatus.Partial, 3, unresolved: 1), Db(DatabaseAnalysisStatus.Partial, 4, unresolved: 5),
            observability: Components.ObservabilityWorkspaceTests.Fixture());
        _snapshots = [current];
        var cut = Render<SourceAnalysis>();

        cut.Find("[data-testid=area-overview]").GetAttribute("aria-pressed").Should().Be("true", "the landing page opens on the overview");
        cut.Find("[data-testid=sa-current]").GetAttribute("data-snapshot").Should().Be(current.Id.ToString());
        (cut.Find("[data-testid=sa-current-archive]").TextContent, cut.Find("[data-testid=sa-current-fingerprint]").TextContent, cut.Find("[data-testid=sa-current-analyzed]").TextContent, cut.Find("[data-testid=sa-current-files]").TextContent)
            .Should().Be(("shop-api.zip", "c850a1b2…", "2026-09-29 08:15 UTC", "120"));
        cut.Find("[data-testid=sa-current-fingerprint]").GetAttribute("title").Should().Be(current.Archive.Sha256);

        cut.FindAll("[data-testid=sa-area-card]").Select(c => c.GetAttribute("data-area")).Should().Equal("Architecture", "Database", "Observability");
        cut.FindAll("[data-testid=sa-area-card] h3").Should().HaveCount(3);
        Status(cut, "Architecture").Should().Be("Partial");
        Status(cut, "Database").Should().Be("Partial", "Partial is never Failed");
        cut.Find("[data-testid=sa-overview-areas]").TextContent.Should().NotContain("Failed");
        Metrics(cut, "Architecture").Should().Equal(new Dictionary<string, string> { ["Components"] = "3", ["Dependencies"] = "3", ["Messaging channels"] = "1", ["Data stores"] = "1", ["External systems"] = "0", ["Unresolved dependencies"] = "1" });
        Metrics(cut, "Database").Should().Equal(new Dictionary<string, string> { ["Database candidates"] = "1", ["Tables / entities"] = "4", ["Relationships"] = "3", ["Indexes"] = "4", ["Unresolved evidence"] = "5" });
        cut.FindAll("[data-testid=sa-area-card] [data-testid=sa-area-reason]").Should().BeEmpty("the unresolved counts are metrics; no sentence repeats them");
        cut.Find("[data-testid=sa-area-card][data-area=Architecture]").GetAttribute("data-analysis").Should().Be(current.Architecture!.SnapshotId.ToString());
        cut.Find("[data-testid=sa-area-card][data-area=Database]").GetAttribute("data-analysis").Should().Be(current.DatabaseArchitecture!.SnapshotId.ToString());
        cut.Find("[data-testid=sa-area-card][data-area=Architecture] [data-testid=sa-area-limitation]").TextContent.Should().Be("Source-derived only · deployment/runtime not verified");
        cut.Find("[data-testid=sa-area-card][data-area=Database] [data-testid=sa-area-limitation]").TextContent.Should().Be("Source-derived only · deployed schema not verified");
        cut.Find("[data-testid=sa-current-evidence]").TextContent.Should().Be("Ready", "the source-evidence status sits next to the archive name");
        cut.FindAll("[data-testid=sa-current] dt").Select(d => d.TextContent).Should().Equal("Fingerprint", "Analyzed", "Files", "Commit");
        (cut.Find("[data-testid=sa-open-architecture]").TextContent, cut.Find("[data-testid=sa-open-database]").TextContent).Should().Be(("Open Architecture", "Open Database Diagram"));
    }

    [Fact]
    public void UnsupportedOrMissingAreas_AreIndependent_AndShowNoCounts()
    {
        _snapshots = [Snapshot("worker.zip", new string('a', 64), DateTimeOffset.Parse("2026-09-29T08:15:00Z"), Arch(ArchitectureStatus.Complete, 2), Db(DatabaseAnalysisStatus.Unsupported, 0, 0))];
        var cut = Render<SourceAnalysis>();

        Status(cut, "Architecture").Should().Be("Complete");
        Metrics(cut, "Architecture").Should().ContainKey("Components");
        Status(cut, "Database").Should().Be("Unsupported");
        Metrics(cut, "Database").Should().BeEmpty("unsupported is not zero tables");
        cut.Find("[data-testid=sa-area-card][data-area=Database] [data-testid=sa-area-reason]").TextContent.Should().Be("No EF Core model, migration or SQL DDL was found.");

        _snapshots = [Snapshot("legacy.zip", new string('b', 64), DateTimeOffset.Parse("2026-08-01T08:15:00Z"), null, Db(DatabaseAnalysisStatus.Complete, 2, 0))];
        cut = Render<SourceAnalysis>();

        Status(cut, "Architecture").Should().Be("Not analyzed");
        Metrics(cut, "Architecture").Should().BeEmpty();
        Status(cut, "Database").Should().Be("Complete");
        Metrics(cut, "Database")["Tables / entities"].Should().Be("2");
        cut.Find("[data-testid=sa-open-architecture]").TextContent.Should().Be("View architecture details");
        cut.Find("[data-testid=sa-open-architecture]").Click();
        cut.Find("[data-testid=architecture-missing]").TextContent.Should().Contain("predates architecture extraction");
    }

    [Fact]
    public void Drilldown_KeepsTheSelectedSnapshot_AndOpensTheDiagram()
    {
        var older = Snapshot("shop-api.zip", new string('1', 64), DateTimeOffset.Parse("2026-09-01T08:00:00Z"), Arch(ArchitectureStatus.Complete, 5), Db(DatabaseAnalysisStatus.Complete, 7, 0));
        var newer = Snapshot("shop-api.zip", new string('2', 64), DateTimeOffset.Parse("2026-09-29T08:00:00Z"), Arch(ArchitectureStatus.Complete, 2), Db(DatabaseAnalysisStatus.Complete, 3, 0));
        _snapshots = [newer, older];
        var cut = Render<SourceAnalysis>();
        cut.Find("[data-testid=source-snapshot]").Change(older.Id.ToString());

        Metrics(cut, "Architecture")["Components"].Should().Be("5", "the summary follows the selected snapshot");
        Metrics(cut, "Database")["Tables / entities"].Should().Be("7", "both cards read the same selected snapshot");

        cut.Find("[data-testid=sa-open-architecture]").Click();
        cut.Find("[data-testid=area-architecture]").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid=architecture-workspace]").TextContent.Should().Contain(older.Architecture!.SourceFingerprint);
        cut.FindAll("[data-testid=sa-overview-areas]").Should().BeEmpty();
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextFocusElement" && (string)i.Arguments[0]! == "sa-area-Architecture");

        cut.Find("[data-testid=area-overview]").Click();
        cut.Find("[data-testid=sa-open-database]").Click();
        var database = cut.Find("[data-testid=database-workspace]");
        database.QuerySelector("button[aria-pressed]")!.TextContent.Should().Be("Diagram", "Open Database Diagram lands on the diagram");
        cut.Find("[data-testid=source-snapshot]").GetAttribute("value").Should().Be(older.Id.ToString());

        cut.Find("[data-testid=area-overview]").Click();
        cut.Find("[data-testid=area-database]").Click();
        cut.Find("[data-testid=database-workspace] button[aria-pressed]").TextContent.Should().Be("Overview", "the Database tab itself opens on its overview");
    }

    [Fact]
    public void Upload_SelectsTheNewSnapshot_OnTheOverview_WithoutReanalyzingOnLoad()
    {
        var cut = Render<SourceAnalysis>();
        _api.Verify(a => a.AnalyzeSourceSnapshotAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never, "opening the page only reads stored snapshots");

        cut.FindComponents<InputFile>()[0].UploadFiles(InputFileContent.CreateFromBinary([1, 2, 3], "shop-api.zip"));

        _uploads.Should().Be(1);
        cut.WaitForAssertion(() => cut.Find("[data-testid=sa-current-archive]").TextContent.Should().Be("shop-api.zip"));
        cut.Find("[data-testid=area-overview]").GetAttribute("aria-pressed").Should().Be("true");
        Status(cut, "Architecture").Should().Be("Complete");
        JSInterop.Invocations.Should().Contain(i => i.Identifier == "birknextFocusElement" && (string)i.Arguments[0]! == "sa-current-heading");
    }

    [Fact]
    public void Presentation_UsesModelLabelsOnly()
    {
        var snapshot = Snapshot("x.zip", "abc", DateTimeOffset.UnixEpoch, Arch(ArchitectureStatus.NeedsReview, 1), Db(DatabaseAnalysisStatus.NeedsReview, 1, 0), SourceAnalysisStatus.Failed);
        SourceAnalysisOverview.Architecture(snapshot).Status.Should().Be("Needs review");
        SourceAnalysisOverview.Database(snapshot).Status.Should().Be("Needs review");
        SourceAnalysisOverview.ShortFingerprint("abc").Should().Be("abc");
        var limitationOnly = Snapshot("y.zip", "abc", DateTimeOffset.UnixEpoch, Arch(ArchitectureStatus.Complete, 1), Db(DatabaseAnalysisStatus.Unsupported, 0, 0));
        limitationOnly.DatabaseArchitecture!.Diagnostics = [DatabaseArchitectureSnapshot.SourceLimitation];
        SourceAnalysisOverview.Database(limitationOnly).Reason.Should().Be("No supported database declaration was found in this source snapshot.", "the source limitation is not a reason");
        SourceAnalysisOverview.SourceEvidence(SourceAnalysisStatus.Failed).Should().Be("No production project found");
        new[] { SourceAnalysisOverview.Architecture(snapshot), SourceAnalysisOverview.Database(snapshot) }.Select(c => c.Status).Should().NotContain(s => s == "Failed" || s == "Pass");
    }
}
