using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.CriticalE2E.Suggestions;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.TestData;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using BirkNext.TestData;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// suggest-v3: search → open a result (the route comes from NavigateTo behind a component callback), filter scenarios, Test Data placeholders
/// for form fields, and approval that maps each placeholder to an approved Test Data alias checked by the same rules a run applies. The
/// fixture is SYNTHETIC, hand-written in the shape of a Blazor/Radzen frontend; the expected facts are written from it, not read back.
/// </summary>
public sealed class FunctionalFlowSuggestionTests : IDisposable
{
    private const string PersonPage = """
        @page "/person"
        <PageTitle>Personsøk</PageTitle>
        <PersonSearchComponent OnChildSelected="@NavigateToChild" OnSearchCleared="@OnCleared" />
        @code {
            private void NavigateToChild(Guid childId)
            {
                NavigationManager.NavigateTo($"/person/{childId}");
            }
            private void OnCleared() { }
        }
        """;

    private const string SearchComponent = """
        <input aria-label="Søk etter barn" @oninput="HandleInput" />
        <RadzenButton Text="Søk" Click="SearchAsync" />
        """;

    private const string DetailPage = """
        @page "/person/{BarnId:guid}"
        <h1>Barn</h1>
        """;

    private const string AuditPage = """
        @page "/admin/audit-log"
        <PageTitle>Revisjonslogg</PageTitle>
        <AuditLogFilterPanel OnFilterChanged="@Apply" />
        <RadzenDataGrid Data="@_rows" />
        @code {
            private void Apply(AuditFilter f)
            {
                NavigationManager.NavigateTo($"/admin/audit-log{BuildQueryString(f, 0)}");
            }
        }
        """;

    private const string FilterPanel = """
        <RadzenTextBox aria-label="Filtrer på aktør-ID" Change="EmitChange" />
        <RadzenDatePicker aria-label="Filtrer fra dato" Change="EmitChange" />
        <RadzenDropDown aria-label="Filtrer på handlingstype" Data="@_types" Change="EmitChange" />
        """;

    private const string FormPage = """
        @page "/register"
        <PageTitle>Registrer</PageTitle>
        <EditForm Model="model" OnValidSubmit="Save">
            <label for="fnr">Fødselsnummer</label>
            <InputText id="fnr" @bind-Value="model.Fnr" required />
            <label for="navn">Navn</label>
            <InputText id="navn" @bind-Value="model.Navn" required />
            <button type="submit">Lagre</button>
        </EditForm>
        """;

    private const string LoginPage = """
        @page "/login"
        <PageTitle>Logg inn</PageTitle>
        <EditForm Model="login" OnValidSubmit="SignIn">
            <label for="user">Brukernavn</label>
            <InputText id="user" @bind-Value="login.User" required />
            <label for="pw">Passord</label>
            <input id="pw" type="password" @bind="login.Password" required />
            <button type="submit">Logg inn</button>
        </EditForm>
        """;

    private static SourceUiSurface Surface() => SourceUiExtractor.Extract(
    [
        new SourceFile("Web/Pages/PersonPage.razor", PersonPage), new SourceFile("Web/Shared/PersonSearchComponent.razor", SearchComponent),
        new SourceFile("Web/Pages/PersonDetailPage.razor", DetailPage), new SourceFile("Web/Pages/AuditLogPage.razor", AuditPage),
        new SourceFile("Web/Shared/AuditLogFilterPanel.razor", FilterPanel), new SourceFile("Web/Pages/Register.razor", FormPage), new SourceFile("Web/Pages/Login.razor", LoginPage),
    ], []);

    private static List<TestSuggestion> Generate() => SuggestionEngine.Generate("synth.web", Guid.NewGuid(), Surface(), []);

    [Fact]
    public void Navigation_behind_a_component_callback_is_recorded_as_a_fact()
    {
        var surface = Surface();
        var person = surface.Pages.Single(p => p.File == "Web/Pages/PersonPage.razor");
        person.Navigations.Should().ContainSingle().Which.Should().Be(new SourceUiNavigation("NavigateToChild", "/person/{childId}", "Web/Pages/PersonPage.razor", 7));
        person.ComponentUses.Single().Callbacks.Should().Equal(new SourceUiCallback("OnChildSelected", "NavigateToChild"), new SourceUiCallback("OnSearchCleared", "OnCleared"));
        person.ResultNavigations.Should().ContainSingle().Which.Should().Be(new SourceUiResultNavigation("PersonSearchComponent", "OnChildSelected", "NavigateToChild", "/person/{childId}", "Web/Pages/PersonPage.razor", 7));

        // An interpolation that builds the query string is not part of the path.
        surface.Pages.Single(p => p.File == "Web/Pages/AuditLogPage.razor").Navigations.Single().Route.Should().Be("/admin/audit-log");
        // "Filtrer" (Norwegian) is a filter, like "filter".
        surface.Pages.Single(p => p.File == "Web/Pages/AuditLogPage.razor").Controls.Where(c => c.Label?.StartsWith("Filtrer", StringComparison.Ordinal) == true)
            .Should().HaveCount(3).And.OnlyContain(c => c.Intent == SourceUiIntent.Filter);
    }

    [Fact]
    public void A_search_whose_result_opens_a_record_gets_a_search_then_open_suggestion_with_the_route_from_source()
    {
        var open = Generate().Single(s => s.Kind == SuggestionKind.SearchOpensResult);

        open.Route.Should().Be("/person");
        open.Category.Should().Be(SuggestionCategory.UiFunctional);
        open.Title.Should().Be("Personsøk: open a found person");
        open.Steps.Select(s => s.Action).Should().Equal(CompanionActionKind.Navigate, CompanionActionKind.Fill, CompanionActionKind.Click, CompanionActionKind.Click, CompanionActionKind.AssertRoute);
        open.Steps[3].ControlId.Should().BeNull("which element opens the record depends on the Test Data record");
        open.Steps[4].Should().Match<SuggestedStep>(s => s.IsFinalAssertion && s.Expected == "/person/" && s.Match == CompanionTextMatch.Contains);
        open.ExpectedResult.Should().Contain("/person/{childId}");
        open.ExpectedBasis.Should().Contain("OnChildSelected").And.Contain("NavigateToChild").And.Contain("Web/Pages/PersonPage.razor:7");
        open.AutomationBlocker.Should().Contain("depends on the Test Data record");
        open.DataVariables.Should().ContainSingle().Which.Variable.Should().Be("search.known-person");
        SuggestionEngine.States(open, false).Should().NotContain(SuggestionState.NeedsExpectedResult, "the expected route is stated by the source");
    }

    [Fact]
    public void Filter_controls_get_filter_scenarios_with_safe_values()
    {
        var filters = Generate().Where(s => s.Kind == SuggestionKind.FilterList).ToList();

        filters.Select(f => f.Title).Should().BeEquivalentTo(
            "Revisjonslogg: filter by \"Filtrer på aktør-ID\"", "Revisjonslogg: filter by \"Filtrer fra dato\"", "Revisjonslogg: filter by \"Filtrer på handlingstype\"");
        filters.Should().OnlyContain(f => f.Category == SuggestionCategory.UiFunctional && f.ExpectedResult == null && f.ExpectedHint != null);

        var actor = filters.Single(f => f.Title.Contains("aktør"));
        actor.Steps[1].Value.Should().Be("${data:filter.filtrer-pa-aktor-id}", "an identifier-like filter value comes from approved Test Data");
        actor.TestDataNeeds.Should().ContainSingle();
        actor.DataVariables.Single().Variable.Should().Be("filter.filtrer-pa-aktor-id");
        var date = filters.Single(f => f.Title.Contains("dato"));
        (date.Steps[1].Value, date.TestDataNeeds.Count, date.TestDataState).Should().Be(("2026-01-15", 0, SuggestionTestDataState.SyntheticGenerated));
        var type = filters.Single(f => f.Title.Contains("handlingstype"));
        type.Steps[1].Action.Should().Be(CompanionActionKind.Select);
        type.Limitations.Should().Contain(l => l.Contains("Radzen dropdown is not a native <select>"));
    }

    [Fact]
    public void Identifier_form_fields_use_Test_Data_placeholders_and_credentials_block_automation()
    {
        var all = Generate();
        var register = all.Single(s => s.Route == "/register" && s.Kind == SuggestionKind.ValidSubmission);
        register.Steps.Single(s => s.Description == "Fill Fødselsnummer").Value.Should().Be("${data:form.fodselsnummer}");
        register.Steps.Single(s => s.Description == "Fill Navn").Value.Should().Be("Synthetic");
        register.DataVariables.Select(v => v.Variable).Should().Equal("form.fodselsnummer");
        register.AutomationBlocker.Should().BeNull();

        var login = all.Single(s => s.Route == "/login" && s.Kind == SuggestionKind.ValidSubmission);
        login.Steps.Should().NotContain(s => s.Description == "Fill Passord", "a credential is never a suggested value or placeholder");
        login.AutomationBlocker.Should().Contain("credential field");
        login.TestDataNeeds.Should().Contain(n => n.Contains("Passord") && n.Contains("credential"));
    }

    [Fact]
    public void A_role_search_hint_names_role_details_not_a_persons()
    {
        var surface = SourceUiExtractor.Extract([new SourceFile("Web/Pages/Roles.razor", """
            @page "/roles"
            <PageTitle>Roller</PageTitle>
            <input aria-label="Søk etter roller" @oninput="HandleInput" />
            <RadzenButton Text="Søk" Click="SearchAsync" />
            """)], []);
        SuggestionEngine.Generate("synth.web", Guid.NewGuid(), surface, []).Single(s => s.Kind == SuggestionKind.Search)
            .ExpectedHint.Should().Contain("role name").And.NotContain("date of birth");
    }

    // ── Approval maps placeholders to approved Test Data ─────────────────────────────────────────────────────────────────────────────

    private sealed class Provider(params TestDataAsset[] assets) : ITestDataProvider
    {
        public string Id => "configured";
        public string Label => "Configured";
        public string Description => "";
        public IReadOnlyList<TestDataAsset> Assets() => assets;
    }

    private static TestDataAsset Asset(string alias, string value, TestDataApproval approval = TestDataApproval.Approved) =>
        new() { Id = alias, Alias = alias, Kind = TestDataKind.Scalar, Approval = approval, Value = new TestDataValue(value), Source = "Configured" };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-flowsug-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("flowsug-" + Guid.NewGuid()).Options);
    public void Dispose() { _db.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task Approval_maps_each_placeholder_to_approved_Test_Data_and_never_stores_the_value()
    {
        var s = new IqrSourceSnapshot { Id = Guid.NewGuid(), IntegrationId = IqrSourceStore.SourceAnalysisOwner, Archive = new SourceArchive("Synth.Web.zip", new string('c', 64), 7), AnalyzedAt = DateTimeOffset.UtcNow, UiSurface = Surface() };
        _db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = s.Id, IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = s.AnalyzedAt, EvidenceJson = JsonSerializer.Serialize(s, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        _db.SaveChanges();
        var catalog = new TestDataCatalog([new Provider(Asset("synthetic-child-a", "Barn Syntetisk A"), Asset("draft-child", "Barn Utkast", TestDataApproval.Unapproved))]);
        var critical = new Mock<ICriticalE2EService>();
        critical.Setup(c => c.SaveFlow(It.IsAny<CriticalE2EFlowDefinition>())).Returns((CriticalE2EFlowDefinition f) => f with { Id = "flow-1", Version = 1 });
        var reviews = new CriticalE2ESuggestionStore(Path.Combine(_dir, "rev"));
        var svc = new CriticalE2ESuggestionService(new IqrSourceStore(_db), new CriticalE2EStore(Path.Combine(_dir, "e2e"), NullLogger<CriticalE2EStore>.Instance),
            critical.Object, reviews, new Mock<IBrowserCompanionService>(MockBehavior.Strict).Object, TimeProvider.System, null, catalog);

        var generated = (await svc.GenerateAsync(new(s.Id, "env-1"), default))!;
        var known = generated.Suggestions.Single(x => x.Kind == SuggestionKind.Search && x.Route == "/person");
        // Locators as a person accepted them after verification (the companion part is covered elsewhere).
        reviews.Update(known.SuggestionId, s.Id, r => r with
        {
            SnapshotId = s.Id,
            Decisions = [.. known.Steps.Where(st => st.ControlId is not null).Select(st => new StepDecision(st.StepId, LocatorDecision.Accepted, st.Locators[0].Selector, "Local session", DateTimeOffset.UtcNow, null))],
        });

        var missing = await svc.ApproveAsync(new(s.Id, known.SuggestionId, "env-1", "p1", "/person", CriticalE2EOutcomeKind.UrlEquals), default);
        missing.Flow.Should().BeNull();
        missing.Problems.Should().ContainSingle().Which.Should().StartWith("Choose approved Test Data for ${data:search.known-person}");

        var unapproved = await svc.ApproveAsync(new(s.Id, known.SuggestionId, "env-1", "p1", "/person", CriticalE2EOutcomeKind.UrlEquals, [new("search.known-person", "draft-child")]), default);
        unapproved.Flow.Should().BeNull("the same Test Data rules as a run apply before anything is saved");
        unapproved.Problems.Should().ContainSingle().Which.Should().Contain("draft-child is unapproved");
        critical.Verify(c => c.SaveFlow(It.IsAny<CriticalE2EFlowDefinition>()), Times.Never);

        var approved = await svc.ApproveAsync(new(s.Id, known.SuggestionId, "env-1", "p1", "/person", CriticalE2EOutcomeKind.UrlEquals, [new("search.known-person", "synthetic-child-a")]), default);
        approved.Problems.Should().BeEmpty();
        var mapping = approved.Flow!.DataMappings.Should().ContainSingle().Subject;
        (mapping.Variable, mapping.AssetAliases.Single(), mapping.DataType).Should().Be(("search.known-person", "synthetic-child-a", TestDataDataType.SimpleValues));
        approved.Flow.Steps.Single(st => st.BrowserAction == CompanionActionKind.Fill).Value.Should().Be("${data:search.known-person}");
        JsonSerializer.Serialize(approved.Flow).Should().NotContain("Barn Syntetisk A", "the value stays in Test Data and is revealed only while a run executes");

        var open = generated.Suggestions.Single(x => x.Kind == SuggestionKind.SearchOpensResult);
        (await svc.ApproveAsync(new(s.Id, open.SuggestionId, "env-1", "p1", null, null, [new("search.known-person", "synthetic-child-a")]), default))
            .Problems.Should().Contain(p => p.Contains("depends on the Test Data record"));
    }
}
