using Bunit;
using BirkNext.DeliveryReporting;
using BirkNext.Interpretation;
using BirkNext.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static BirkNext.Web.Tests.Pages.DeliveryReportingPageTests;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Delivery Overview Source + Functional scope: the whole overview (metrics, checklist, states, change, quality, attention) follows the scope the
/// server resolved; ids live in the URL; unknown ids and sources absent from the baseline are stated; clearing returns the global view.
/// </summary>
public sealed class DeliveryOverviewScopeTests : BunitContext
{
    private const string Adr = "capability:decision:adr-029", Language = "package:m2lb-language", Main = "package:m2lb -main";
    private const string Autorisasjon = "autorisasjonservice::005-translate-adr-029", Frontend = "frontend::007-translate-adr-029",
        Placements = "tjenesteservice::004-child-placements-period", Polish = "frontend::003-designer-polish";
    private static readonly HashSet<string> Translation = [Autorisasjon, Frontend];

    private readonly FakeApi _api = new();
    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    public DeliveryOverviewScopeTests()
    {
        _api.Report = Report();
        _api.Scope = Server;
        Services.AddSingleton<IDeliveryReportingApiService>(_api);
    }

    private static DeliveryStoryRow Row(string key, string title, string module, int done, int total, bool isNew) => new()
    {
        FeatureKey = key, Title = title, Module = module, CurrentStatus = done == total ? DeliveryStatus.TasksComplete : DeliveryStatus.InProgress,
        CurrentTaskCompleted = done, CurrentTaskTotal = total, NewFeature = isNew, Movement = isNew ? DeliveryMovement.Progressed : DeliveryMovement.NoSignificantChange,
        BaselineTaskCompleted = isNew ? 0 : done, BaselineTaskTotal = isNew ? 0 : total, Evidence = new() { SpecPath = $"{module}/spec.md", TasksPath = $"{module}/tasks.md" },
    };

    private static WeeklyDeliveryReport Report() => SampleReport() with
    {
        Baseline = new() { Label = "M2LB-main", EffectiveAt = new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero), SourceType = DeliverySourceType.ZipUpload, SourceFileName = "M2LB -main.zip" },
        Current = new() { Label = "M2LB-language", EffectiveAt = new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero), SourceType = DeliverySourceType.ZipUpload, SourceFileName = "M2LB-language.zip" },
        Stories =
        [
            Row(Autorisasjon, "Translate Autorisasjon to English (ADR-029)", "AutorisasjonService", 4, 10, true),
            Row(Frontend, "Translate Frontend to English (ADR-029)", "Frontend", 2, 2, true),
            Row(Placements, "Children in Placements per Period", "TjenesteService", 5, 9, false),
            Row(Polish, "Designer Feedback Polish", "Frontend", 3, 3, false),
        ],
        Attention =
        [
            new(DeliveryAttentionKind.BrokenTraceability, DeliveryAttentionSeverity.Review, "Children in Placements per Period", "US2 has no traceable tasks.", Placements),
            new(DeliveryAttentionKind.UnmappedArtifact, DeliveryAttentionSeverity.Review, "Task file outside a feature", "A tasks.md file is not inside a feature folder."),
        ],
        ComparisonWarnings = ["Same-day comparison: both snapshots are from one day."],
        Metrics = [new("new-features", "New features", 2), new("tasks-completed", "Tasks completed this period", 0), new("inventory-features", "Features", 4) { Group = DeliveryMetricGroups.Inventory }],
    };

    /// <summary>The server's answer: membership by id (translation features implement the ADR-029 change intent), projected with the shared rule.</summary>
    private DeliveryScopeView Server(CanonicalScopeFilter f)
    {
        var report = _api.Report;
        var view = new DeliveryScopeView
        {
            Filter = f,
            Sources = [new(Language, "M2LB-language", "Imported from M2LB-language.zip", 4, 0), new(Main, "M2LB -main", "Imported from M2LB -main.zip", 0, 2)],
            Scopes = [new(Adr, "ADR-029: Translate English", FunctionalScopeKind.ChangeIntent, "Change intent from ADR-029", 2, 0, 1)],
            Candidates = f.FunctionalScopeId == Adr ? [new("tjenesteservice::005-translate-source-english", "Translate the Tjeneste module to English", "Implements → ADR-029 (candidate — awaiting review)")] : [],
        };
        if (!f.IsScoped) return view with { Report = report };
        var invalid = f.FunctionalScopeId is { } s && s != Adr ? new List<string> { "Selected functional scope is not available in this snapshot." } : [];
        var keys = invalid.Count > 0 ? [] : report.Stories.Select(x => x.FeatureKey)
            .Where(k => f.SourceId is null || f.SourceId == Language).Where(k => f.FunctionalScopeId is null || Translation.Contains(k)).ToHashSet();
        var scope = new DeliveryReportScope { SourceId = f.SourceId, SourceName = f.SourceId == Language ? "M2LB-language" : null, FunctionalScopeId = f.FunctionalScopeId,
            FunctionalScopeName = invalid.Count == 0 && f.FunctionalScopeId is not null ? "ADR-029: Translate English" : null, SourceInBaseline = f.SourceId is null or Main,
            CurrentFeatureKeys = [.. keys] };
        return view with
        {
            InvalidFilters = invalid, Scope = scope, Report = DeliveryReportScoping.Project(report, keys, new HashSet<string>(), scope),
            Members = [.. keys.Select(k => new DeliveryScopeMember(k, k, "Implements → ADR-029: Translate English (declared in the feature)"))],
        };
    }

    private IRenderedComponent<BirkNext.Web.Pages.DeliveryOverview> Open(string query = "")
    {
        Nav.NavigateTo($"delivery-overview?project={ProjectId}{query}");
        var page = Render<BirkNext.Web.Pages.DeliveryOverview>();
        page.WaitForAssertion(() => page.Find("[data-testid=dd-header]"));
        return page;
    }

    private static string Kpi(IRenderedComponent<BirkNext.Web.Pages.DeliveryOverview> page, string key) =>
        page.Find($"[data-testid=dd-kpi][data-key={key}] .dd-kpi-value").TextContent;

    [Fact]
    public void Default_IsTheWholeProject_WithEverySourceAndScopeOffered()
    {
        var page = Open();

        page.FindAll("[data-testid=do-item]").Should().HaveCount(4);
        Kpi(page, "features").Should().Be("4");
        page.Find("[data-testid=dd-scope-source]").TextContent.Should().Be("All sources · 2 evidence sources");
        page.Find("[data-testid=dd-scope-scope]").TextContent.Should().Be("All functional scopes");
        page.FindAll("[data-testid=do-source-filter] option").Select(o => o.GetAttribute("value")).Should().Equal("", Language, Main);
        page.FindAll("[data-testid=do-scope-filter] option").Select(o => o.GetAttribute("value")).Should().Equal("", Adr);
        page.Find("[data-testid=do-scope-filter] optgroup").GetAttribute("label").Should().Be("Change intent");
        page.FindAll("[data-testid=do-chips]").Should().BeEmpty();
        page.FindAll("[data-testid=do-why]").Should().BeEmpty();
    }

    [Fact]
    public void FunctionalScope_RecalculatesTheWholeOverview_AndExplainsEachFeature()
    {
        var page = Open($"&scope={Uri.EscapeDataString(Adr)}");

        page.WaitForAssertion(() => page.FindAll("[data-testid=do-item]").Should().HaveCount(2));
        _api.ScopeRequests[^1].FunctionalScopeId.Should().Be(Adr, "the scope id — never its label — is sent");
        Kpi(page, "features").Should().Be("2");
        Kpi(page, "checklist").Should().Be("50%");
        Kpi(page, "open").Should().Be("6");
        page.Find("[data-testid=dd-checklist] svg").GetAttribute("aria-label").Should().Be("6 of 12 tasks checked, 6 open", "the donut follows the scope, not 14/24");
        page.FindAll("[data-testid=dd-change-bar]").Single(b => b.GetAttribute("data-key") == "new-features").TextContent.Should().Contain("2");
        page.Find("[data-testid=dd-quality]").TextContent.Should().Contain("2 of 2 features have spec.md");
        page.Find("[data-testid=dd-scope-scope]").TextContent.Should().Be("ADR-029: Translate English");
        page.FindAll("[data-testid=do-why]").Should().HaveCount(2).And.OnlyContain(w => w.TextContent.Contains("declared in the feature"));
        page.Find("[data-testid=do-scope-candidates]").TextContent.Should().Contain("awaiting review, not counted");

        var attention = page.Find("[data-testid=dd-attention]").TextContent;
        attention.Should().NotContain("Children in Placements", "an unrelated feature's issue is not this scope's");
        page.FindAll("[data-testid=dd-attention-group]").Select(g => g.QuerySelector("strong")!.TextContent)
            .Should().Contain([DeliveryDashboard.SnapshotLevelLabel, DeliveryDashboard.ComparisonLevelLabel]);
        page.Find("[data-testid=do-chip-scope]").TextContent.Should().Contain("ADR-029: Translate English");
    }

    [Fact]
    public void SourceAbsentFromTheBaseline_SaysChangeIsNotComparable_InsteadOfZeros()
    {
        var page = Open($"&source={Uri.EscapeDataString(Language)}");

        page.WaitForAssertion(() => page.Find("[data-testid=dd-change-not-comparable]").TextContent.Should().Contain("M2LB-language is not present in the baseline"));
        page.FindAll("[data-testid=dd-change-features]").Should().BeEmpty();
        page.Find("[data-testid=dd-scope-source]").TextContent.Should().Be("M2LB-language");
        page.FindAll("[data-testid=do-item]").Should().HaveCount(4);
    }

    [Fact]
    public void Module_NarrowsTheScopedOverview_WithTheSameProjection()
    {
        var page = Open($"&scope={Uri.EscapeDataString(Adr)}&module=Frontend");

        page.WaitForAssertion(() => page.FindAll("[data-testid=do-item]").Should().ContainSingle());
        Kpi(page, "features").Should().Be("1");
        page.Find("[data-testid=dd-scope-module]").TextContent.Should().Be("Frontend");
        page.Find("[data-testid=do-item]").TextContent.Should().Contain("Translate Frontend to English");
    }

    [Fact]
    public void UnknownScope_IsReportedExplicitly_WithClearActions_NeverSwitched()
    {
        var page = Open("&scope=capability%3Adecision%3Aadr-999");

        page.WaitForAssertion(() => page.Find("[data-testid=do-invalid-filter]").TextContent.Should().Be("Selected functional scope is not available in this snapshot."));
        page.Find("[data-testid=do-empty-scope]").TextContent.Should().Contain("No Delivery features match this Source + Functional scope.");
        page.FindAll("[data-testid=do-item]").Should().BeEmpty();
        page.Find("[data-testid=do-scope-filter]").GetAttribute("value").Should().Be("capability:decision:adr-999", "the selection is kept and shown as unavailable");

        page.Find("[data-testid=do-clear-scope]").Click();
        Nav.Uri.Should().NotContain("scope=");
        page.WaitForAssertion(() => page.FindAll("[data-testid=do-item]").Should().HaveCount(4));
    }

    [Fact]
    public void Selection_IsKeptInTheUrlById_AndClearFiltersReturnsTheGlobalView()
    {
        var page = Open();

        page.Find("[data-testid=do-scope-filter]").Change(Adr);
        Nav.Uri.Should().Contain($"scope={Uri.EscapeDataString(Adr)}");
        page.WaitForAssertion(() => page.FindAll("[data-testid=do-item]").Should().HaveCount(2));
        page.Find("[data-testid=do-source-filter]").Change(Language);
        Nav.Uri.Should().Contain($"source={Uri.EscapeDataString(Language)}").And.Contain("scope=");

        page.Find("[data-testid=do-clear-filters]").Click();
        Nav.Uri.Should().EndWith($"delivery-overview?project={ProjectId}");
        page.WaitForAssertion(() => page.FindAll("[data-testid=do-item]").Should().HaveCount(4));
        Kpi(page, "features").Should().Be("4");
    }

    [Fact]
    public void WithoutTheScopeEndpoint_TheRequestedScopeIsSaidToBeUnapplied()
    {
        _api.Scope = null;
        var page = Open($"&scope={Uri.EscapeDataString(Adr)}");

        page.WaitForAssertion(() => page.Find("[data-testid=do-scope-error]").TextContent.Should().Contain("could not be read"));
        page.FindAll("[data-testid=do-item]").Should().HaveCount(4);
    }
}
