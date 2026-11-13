using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.CriticalE2E.Suggestions;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.CriticalE2E;
using BirkNext.Integrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services.CriticalE2E;

/// <summary>
/// Deep frontend functional discovery. The real behaviour of a page often lives in a component it composes, with component-library controls
/// (RadzenButton Click=, an input with @oninput/@onkeyup) — not in page-level @onclick. The fixture below is SYNTHETIC and written by hand in
/// that shape (a /person page composing a search component that composes a result list); the expected facts are the oracle, written from the
/// fixture, not read back from the extractor. Suggestions stay drafts: search scenarios carry Test Data needs and expected-result HINTS, never
/// invented assertions; a manual tester completes them without the Edge Companion.
/// </summary>
public sealed class DeepFrontendDiscoveryTests : IDisposable
{
    private const string PersonPage = """
        @page "/person"
        @attribute [Authorize]
        <PageTitle>Personsøk</PageTitle>
        <h1>Finn person</h1>
        <PersonSearchComponent OnSelected="Open" />
        <RadzenButton Text="Åpne siste" Click="OpenRecent" />
        @code {
            private void Open(object p) { }
            private void OpenRecent() { }
        }
        """;

    private const string SearchComponent = """
        <div class="search">
            <input class="search-input" aria-label="Søk etter person" placeholder="Navn" @oninput="HandleInput" @onkeyup="HandleEnter" />
            <RadzenButton Text="Søk" aria-label="Start søk" Click="SearchAsync" Disabled="@_isSearching" />
            <RadzenButton Text="Nullstill" Click="ClearSearch" ButtonStyle="ButtonStyle.Light" />
        </div>
        @if (_searchResult is { IsSuccess: false })
        {
            <div class="alert">@_searchResult.ErrorMessage</div>
        }
        @if (_searched && _results.Count == 0)
        {
            <p class="empty">Ingen treff</p>
        }
        <PersonListComponent Items="_results" />
        @code {
            private bool _isSearching, _searched;
        }
        """;

    private const string ListComponent = """
        <RadzenDataGrid Data="@Items" EmptyText="Ingen personer funnet">
        </RadzenDataGrid>
        @foreach (var p in Items)
        {
            <RadzenButton Text="Vis detaljer" Click="() => Show(p)" />
        }
        """;

    private static SourceUiSurface Surface(bool withComponents = true) => SourceUiExtractor.Extract(
        withComponents
            ? [new SourceFile("Web/Pages/BarnSearchPage.razor", PersonPage), new SourceFile("Web/Shared/Components/PersonSearchComponent.razor", SearchComponent),
               new SourceFile("Web/Shared/Components/PersonListComponent.razor", ListComponent)]
            : [new SourceFile("Web/Pages/BarnSearchPage.razor", PersonPage)], []);

    [Fact]
    public void A_page_takes_the_controls_of_the_components_it_composes_including_library_controls()
    {
        var page = Surface().Pages.Single(p => p.File == "Web/Pages/BarnSearchPage.razor");

        page.ComposedFrom.Should().Equal("Web/Shared/Components/PersonSearchComponent.razor", "Web/Shared/Components/PersonListComponent.razor");
        var input = page.Controls.Single(c => c.Kind == SourceUiControlKind.TextInput);
        (input.Label, input.Handler, input.KeyHandler, input.Intent, input.SourceFile)
            .Should().Be(("Søk etter person", "HandleInput", "HandleEnter", SourceUiIntent.Search, "Web/Shared/Components/PersonSearchComponent.razor"));
        var search = page.Controls.Single(c => c.Text == "Søk");
        (search.Kind, search.Library, search.Handler, search.DisabledWhen, search.Intent, search.Label)
            .Should().Be((SourceUiControlKind.Button, "Radzen", "SearchAsync", "_isSearching", SourceUiIntent.Search, "Start søk"));
        page.Controls.Single(c => c.Text == "Nullstill").Intent.Should().Be(SourceUiIntent.Clear);
        page.Controls.Single(c => c.Text == "Åpne siste").Should().Match<SourceUiControl>(c => c.Intent == SourceUiIntent.Open && c.SourceFile == null && c.Handler == "OpenRecent");
        page.Controls.Single(c => c.Text == "Vis detaljer").Should().Match<SourceUiControl>(c => c.Repeated && c.Handler == "Show", "inside @foreach in the nested component; a lambda's method name is the fact");
        page.Controls.Should().ContainSingle(c => c.Kind == SourceUiControlKind.ResultList && c.Text == "RadzenDataGrid");
        page.Messages.Should().Contain(m => m.Kind == SourceUiMessageKind.EmptyResult && m.Text == "Ingen treff" && m.Conditional)
            .And.Contain(m => m.Kind == SourceUiMessageKind.EmptyResult && m.Text == "Ingen personer funnet");
        page.Limitations.Should().Contain(l => l.Contains("2 composed component(s)") && l.Contains("PersonSearchComponent"));
        JsonSerializer.Serialize(Surface()).Should().NotContain("<RadzenButton").And.NotContain("@oninput", "facts only, never markup");
    }

    [Fact]
    public void A_search_page_gets_functional_search_suggestions_not_only_page_opens()
    {
        var all = SuggestionEngine.Generate("synth.m2lb", Guid.NewGuid(), Surface(), []);
        var mine = all.Where(s => s.Route == "/person").ToList();

        mine.Select(s => s.Kind).Should().Contain([SuggestionKind.PageOpens, SuggestionKind.Search, SuggestionKind.SearchNoMatch, SuggestionKind.SearchEmpty, SuggestionKind.ClearSearch, SuggestionKind.UserAction]);
        mine.Single(s => s.Kind == SuggestionKind.PageOpens).Category.Should().Be(SuggestionCategory.UiSmoke);
        mine.Where(s => s.Kind != SuggestionKind.PageOpens).Should().OnlyContain(s => s.Category == SuggestionCategory.UiFunctional, "UI source alone never yields Business E2E");

        var known = mine.Single(s => s.Kind == SuggestionKind.Search);
        known.Title.Should().Be("Personsøk: search finds a known person");
        known.Steps.Select(s => s.Action).Should().Equal(CompanionActionKind.Navigate, CompanionActionKind.Fill, CompanionActionKind.Click);
        known.Steps[1].Value.Should().Be("${data:search.known-person}", "a known person comes from approved Test Data, never generated");
        known.TestDataNeeds.Should().ContainSingle(n => n.Contains("known synthetic person") && n.Contains("never a real person"));
        known.TestDataState.Should().Be(SuggestionTestDataState.NeedsApprovedTestData);
        known.Steps.Should().NotContain(s => s.IsFinalAssertion, "what a hit shows is not stated in source");
        known.ExpectedResult.Should().BeNull();
        known.ExpectedHint.Should().Contain("known person");
        known.SourceRefs.Should().Contain(r => r.StartsWith("Web/Shared/Components/PersonSearchComponent.razor:", StringComparison.Ordinal), "the fact's own file, not the page");
        known.Limitations.Should().Contain(l => l.Contains("HandleEnter") && l.Contains("no key action"));
        known.Steps[2].Locators.Should().Contain(l => l.Selector.Kind == CompanionSelectorKind.Role && l.Selector.Name == "Søk");
        SuggestionEngine.States(known, false).Should().Contain([SuggestionState.NeedsExpectedResult, SuggestionState.NeedsTestData]);

        var noMatch = mine.Single(s => s.Kind == SuggestionKind.SearchNoMatch);
        noMatch.Steps[1].Value.Should().Be("ZzSyntheticNoMatch");
        noMatch.TestDataState.Should().Be(SuggestionTestDataState.SyntheticGenerated);
        noMatch.ExpectedHint.Should().Be("\"Ingen treff\" is shown.", "the first empty-result text declared in source (composition order), offered as a hint");
        noMatch.ExpectedHintBasis.Should().Contain("PersonSearchComponent.razor").And.Contain("shown conditionally");
        noMatch.Steps.Should().NotContain(s => s.IsFinalAssertion, "a hint is never an assertion");

        mine.Single(s => s.Kind == SuggestionKind.SearchEmpty).ExpectedHint.Should().Contain("Disabled=\"_isSearching\"");
        mine.Single(s => s.Kind == SuggestionKind.ClearSearch).Steps.Last().Description.Should().Be("Click \"Nullstill\"");
        mine.Should().ContainSingle(s => s.Kind == SuggestionKind.UserAction && s.Title == "Personsøk: \"Åpne siste\"", "the repeated \"Vis detaljer\" is not an action of the page");
        all.Select(s => s.SuggestionId).Should().OnlyHaveUniqueItems();
        all.Should().OnlyContain(s => s.RuleVersion == SuggestionEngine.RuleVersion);
    }

    [Fact]
    public void Without_the_component_file_the_page_says_so_and_nothing_is_invented()
    {
        var page = Surface(withComponents: false).Pages.Single();
        page.ComponentUses.Should().ContainSingle(u => u.Component == "PersonSearchComponent");
        page.ComposedFrom.Should().BeEmpty();
        var all = SuggestionEngine.Generate("synth.m2lb", Guid.NewGuid(), Surface(withComponents: false), []);
        all.Should().NotContain(s => s.Kind == SuggestionKind.Search);
    }

    [Fact]
    public void Composition_is_bounded_cycle_safe_and_refuses_ambiguous_component_names()
    {
        var surface = SourceUiExtractor.Extract(
        [
            new SourceFile("Web/Pages/Home.razor", "@page \"/home\"\n<h1>Hjem</h1>\n<LoopA />\n<Dup />"),
            new SourceFile("Web/Shared/LoopA.razor", "<LoopB />\n<button type=\"button\" @onclick=\"A\">A</button>"),
            new SourceFile("Web/Shared/LoopB.razor", "<LoopA />\n<button type=\"button\" @onclick=\"B\">B</button>"),
            new SourceFile("Web/Shared/One/Dup.razor", "<button type=\"button\" @onclick=\"X\">X</button>"),
            new SourceFile("Web/Shared/Two/Dup.razor", "<button type=\"button\" @onclick=\"Y\">Y</button>"),
        ], []);
        var home = surface.Pages.Single(p => p.File == "Web/Pages/Home.razor");
        home.ComposedFrom.Should().Equal("Web/Shared/LoopA.razor", "Web/Shared/LoopB.razor");
        home.Controls.Select(c => c.Text).Should().Contain(["A", "B"]).And.NotContain(["X", "Y"]);
        home.Limitations.Should().Contain(l => l.Contains("<Dup>") && l.Contains("ambiguous"));
    }

    // ── Manual review: no Edge Companion, no source ─────────────────────────────────────────────────────────────────────────────────────

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bn-deep-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase("deep-" + Guid.NewGuid()).Options);
    public void Dispose() { _db.Dispose(); try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public async Task A_manual_tester_completes_a_search_suggestion_without_the_companion()
    {
        var s = new IqrSourceSnapshot { Id = Guid.NewGuid(), IntegrationId = IqrSourceStore.SourceAnalysisOwner, Archive = new SourceArchive("Synth.M2lb.zip", new string('b', 64), 3), AnalyzedAt = DateTimeOffset.UtcNow, UiSurface = Surface() };
        _db.IqrSourceSnapshots.Add(new IqrSourceSnapshotRecord { Id = s.Id, IntegrationId = IqrSourceStore.SourceAnalysisOwner, AnalyzedAt = s.AnalyzedAt, EvidenceJson = JsonSerializer.Serialize(s, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
        _db.SaveChanges();
        var companion = new Mock<IBrowserCompanionService>(MockBehavior.Strict);   // never touched: manual review needs no companion
        var svc = new CriticalE2ESuggestionService(new IqrSourceStore(_db), new CriticalE2EStore(Path.Combine(_dir, "e2e"), NullLogger<CriticalE2EStore>.Instance),
            Mock.Of<ICriticalE2EService>(), new CriticalE2ESuggestionStore(Path.Combine(_dir, "rev")), companion.Object, TimeProvider.System);

        var generated = (await svc.GenerateAsync(new(s.Id, "env-1"), default))!;
        generated.Summary.UiFunctional.Should().Be(generated.Suggestions.Count(x => x.Category == SuggestionCategory.UiFunctional)).And.BeGreaterThan(4);
        var known = generated.Suggestions.Single(x => x.Kind == SuggestionKind.Search);

        (await svc.ManualReviewAsync(new(s.Id, known.SuggestionId, "env-1", null, "test-data: barn-a", null, ManualReviewDecision.ReadyForManualTest), default))
            .Problem.Should().Contain("expected result");
        (await svc.ManualReviewAsync(new(s.Id, known.SuggestionId, "env-1", "Barn A is listed", null, null, ManualReviewDecision.ReadyForManualTest), default))
            .Problem.Should().Contain("Name the Test Data");
        (await svc.ManualReviewAsync(new(s.Id, known.SuggestionId, "env-1", "Barn A is listed", "12345678901", null, ManualReviewDecision.ReadyForManualTest), default))
            .Problem.Should().Contain("national identity number", "personal identifiers are never stored in a review");

        var (ready, problem) = await svc.ManualReviewAsync(new(s.Id, known.SuggestionId, "env-1", "Barn A (synthetic) is listed with birth year 2015", "Test Data alias barn-a", "Enter also works", ManualReviewDecision.ReadyForManualTest), default);
        problem.Should().BeNull();
        ready!.States.Should().Contain(SuggestionState.ReadyForManualTest).And.Contain(SuggestionState.NeedsExpectedResult, "manual readiness never makes it an automated test");
        ready.TestDataState.Should().Be(SuggestionTestDataState.ProvidedByReviewer);
        ready.ManualReview!.ExpectedResult.Should().StartWith("Barn A");
        (await svc.ApproveAsync(new(s.Id, known.SuggestionId, "env-1", "p1", null, null), default)).Flow.Should().BeNull("automation approval still needs verified locators and Test Data values");

        var again = (await svc.GenerateAsync(new(s.Id, "env-1"), default))!;
        again.Summary.ReadyForManualTest.Should().Be(1);
        var empty = again.Suggestions.Single(x => x.Kind == SuggestionKind.SearchEmpty);
        (await svc.ManualReviewAsync(new(s.Id, empty.SuggestionId, "env-1", null, null, "Not relevant: the button is disabled", ManualReviewDecision.Rejected), default))
            .Suggestion!.States.Should().Equal(SuggestionState.Rejected);
        companion.VerifyNoOtherCalls();
    }
}
