using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.SystemUnderstanding;
using SystemUnderstandingPage = BirkNext.Web.Pages.SystemUnderstanding;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using P = BirkNext.Web.Services.SystemUnderstandingPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// System Understanding: the curated Skolenærvær guide (the real seed file) renders as an overview, an interactive flow with node and connection
/// details, step-by-step explanations, data &amp; contracts with per-snapshot versions, rules and test guidance — with honest evidence labels,
/// no markup injection, no inert links, read-only for readers and the review lifecycle for authorized users. The layout and search are generic.
/// </summary>
public sealed class SystemUnderstandingPageTests : BunitContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    internal static SystemGuideContent Seed()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend", "BirkNext.Api"))) dir = dir.Parent;
        var file = Path.Combine(dir!.FullName, "backend", "BirkNext.Api", "Services", "SystemUnderstanding", "Guides", "skolenaervaer.guide.json");
        return JsonSerializer.Deserialize<SystemGuideContent>(File.ReadAllText(file), Json)!;
    }

    private sealed class Api(SystemGuideView? view, bool canEdit = false) : ISystemUnderstandingApiService
    {
        public List<string> Actions { get; } = [];
        public SaveGuideDraftRequest? Saved { get; private set; }
        public Task<(IReadOnlyList<SystemGuideSummary>? Guides, string? Error)> ListAsync(CancellationToken ct = default) => Task.FromResult<(IReadOnlyList<SystemGuideSummary>?, string?)>(
            view is null ? ([], null) : ([new SystemGuideSummary(view.GuideKey, view.Title, view.Purpose, view.ReviewState, null, 1, 0, 2)], null));
        public Task<(SystemGuideView? Guide, string? Error)> GetAsync(string guideKey, CancellationToken ct = default) =>
            Task.FromResult<(SystemGuideView?, string?)>((view! with { CanEdit = canEdit, CanApprove = canEdit }, null));
        public Task<(SystemGuideRevision? Revision, string? Error)> RevisionAsync(string guideKey, int revision, CancellationToken ct = default) => Task.FromResult<(SystemGuideRevision?, string?)>((view!.Shown, null));
        public Task<(SystemGuideRevision? Revision, string? Error)> SaveDraftAsync(string guideKey, SaveGuideDraftRequest request, CancellationToken ct = default)
        { Saved = request; return Task.FromResult<(SystemGuideRevision?, string?)>((view!.Shown! with { Revision = 2, Content = request.Content }, null)); }
        public Task<(SystemGuideRevision? Revision, string? Error)> TransitionAsync(string guideKey, int revision, string action, CancellationToken ct = default)
        { Actions.Add($"{action}:{revision}"); return Task.FromResult<(SystemGuideRevision?, string?)>((view!.Shown! with { State = GuideRevisionState.InReview }, null)); }
    }

    private static SystemGuideView View(SystemGuideContent? content = null, GuideRevisionState state = GuideRevisionState.Draft, List<GuideSourceChange>? changes = null)
    {
        var revision = new SystemGuideRevision { GuideKey = "skolenaervaer", Revision = 1, State = state, ContentFingerprint = new string('a', 64), Content = content ?? Seed(), CreatedBy = "BirkNext curated seed (not reviewed)",
            CreatedAt = DateTimeOffset.Parse("2026-11-11T12:00:00Z"), SourceBasis = [new("m2lb", null, null, "M2LB-applikasjonen: not imported")] };
        return new SystemGuideView
        {
            GuideKey = "skolenaervaer", Title = revision.Content.Title, Purpose = revision.Content.Purpose, Shown = revision, Draft = state == GuideRevisionState.Published ? null : revision,
            ReviewState = state == GuideRevisionState.Published ? "Published (revision 1)" : "Draft only — not reviewed", SourceChanges = changes ?? [],
            Revisions = [new(1, state, revision.ContentFingerprint, revision.CreatedBy, revision.CreatedAt, null, null, null, "seed")],
            CurrentSources = [new("m2lb", null, null, "M2LB-applikasjonen: not imported")],
        };
    }

    private IRenderedComponent<SystemUnderstandingPage> Render(Api api, string? query = null)
    {
        Services.AddSingleton<ISystemUnderstandingApiService>(api);
        JSInterop.Mode = JSRuntimeMode.Loose;
        if (query is not null) Services.GetRequiredService<NavigationManager>().NavigateTo("system-understanding" + query);
        var cut = Render<SystemUnderstandingPage>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=su]").GetAttribute("aria-busy").Should().Be("false"));
        return cut;
    }

    [Fact]
    public void A_new_tester_sees_purpose_overview_and_the_flow_first_without_a_false_verdict()
    {
        var cut = Render(new Api(View()));
        cut.Find("[data-testid=su-title]").TextContent.Should().Be("Skolenærvær");
        cut.Find("[data-testid=su-purpose]").TextContent.Should().Contain("Altinn").And.Contain("Bufdata");
        cut.Find("[data-testid=su-review]").TextContent.Should().Be("Draft only — not reviewed", "a seed is never presented as reviewed");
        cut.Find("[data-testid=su-business-purpose]").TextContent.Should().NotBeEmpty();
        foreach (var id in new[] { "altinn", "mu", "mm", "skole", "periodelaas", "bufdata", "person", "tjeneste", "birk", "eventhub", "skoleadapter" })
            cut.FindAll($"[data-testid=su-node-{id}]").Should().ContainSingle(id);
        cut.Find("[data-testid=su-edge-e-mm-mu-ack]").ClassList.Should().Contain("k-ack", "the acknowledgment is drawn differently from the main flow");
        cut.Find("[data-testid=su-edge-e-mm-skole]").ClassList.Should().Contain("k-main");
        cut.Find("[data-testid=su-edge-e-skole-person]").ClassList.Should().Contain("k-identity");
        cut.Markup.Should().NotContain("Healthy").And.NotContain("Verified ✓");
    }

    [Fact]
    public void Selecting_a_component_or_a_connection_explains_it_with_its_evidence()
    {
        var cut = Render(new Api(View()));
        cut.Find("[data-testid=su-node-person]").Click();
        var detail = cut.Find("[data-testid=su-node-detail]");
        detail.TextContent.Should().Contain("Person").And.Contain("holdes tilbake");
        detail.QuerySelectorAll(".gel-item").Should().NotBeEmpty();
        cut.Find("[data-testid=su-edge-e-mm-skole]").Click();
        cut.Find("[data-testid=su-edge-transport]").TextContent.Should().Contain("POST /api/skole/v1/skolenaervaer/rapporter");
        cut.Find("[data-testid=su-edge-format]").TextContent.Should().Contain("JSON");
        cut.Find("[data-testid=su-edge-detail]").TextContent.Should().Contain("v2.0.1", "the other source generation stays visible as uncertainty");
        cut.Find("[data-testid=su-edge-e-mm-skole]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        cut.FindAll("[data-testid=su-edge-detail]").Should().BeEmpty("Enter toggles the selection off again (keyboard operable)");
    }

    [Fact]
    public void Highlighting_a_side_flow_dims_the_rest_and_the_flow_exists_as_text()
    {
        var cut = Render(new Api(View()));
        cut.Find("[data-testid=su-flow-chip-utdanning]").Click();
        cut.Find("[data-testid=su-node-birk]").ClassList.Should().NotContain("is-dim");
        cut.Find("[data-testid=su-node-altinn]").ClassList.Should().Contain("is-dim");
        cut.Find("[data-testid=su-flow-text-utdanning]").TextContent.Should().Contain("BiRK").And.Contain("SkoleAdapter");
    }

    [Fact]
    public void Step_by_step_explains_trigger_failure_and_how_to_check_and_navigates_between_steps()
    {
        var cut = Render(new Api(View()), "?tab=steps");
        cut.FindAll("[data-testid^=su-step-]").Should().NotBeEmpty();
        cut.Find("[data-testid=su-step-11] .su-step-head").Click();
        var step = cut.Find("[data-testid=su-step-11]");
        step.TextContent.Should().Contain("XML til JSON").And.Contain("What can go wrong").And.Contain("D1");
        cut.Find("[data-testid=su-step-verify]").TextContent.Should().Contain("forventet JSON");
        cut.Find("[data-testid=su-step-next]").Click();
        cut.Find("[data-testid=su-step-12]").ClassList.Should().Contain("is-open");
        cut.Find("[data-testid=su-stage-s-person]").Click();
        cut.FindAll("[data-testid=su-step-01]").Should().BeEmpty("a stage filter shows only its steps");
        cut.Find("[data-testid=su-step-P1]").Should().NotBeNull();
    }

    [Fact]
    public void Data_and_contracts_keep_versions_per_snapshot_and_flag_the_mismatch_without_a_deployment_claim()
    {
        var cut = Render(new Api(View()), "?tab=data");
        var transfer = cut.Find("[data-testid=su-transfer-e-mm-skole]");
        transfer.TextContent.Should().Contain("/rapporter").And.Contain("/reports");
        cut.FindAll("[data-testid=su-version-mismatch]").Should().NotBeEmpty();
        cut.Find("[data-testid=su-version-mismatch]").TextContent.Should().Contain("Deployment compatibility: not verified");
        cut.FindAll("[data-testid=su-transfer-e-mu-mm] a").Should().OnlyContain(a => a.GetAttribute("href") == "schema-studio");
    }

    [Fact]
    public void Rules_separate_implementation_from_verification_and_test_guidance_is_not_coverage()
    {
        var cut = Render(new Api(View()), "?tab=rules");
        cut.Find("[data-testid=su-rule-r-ukjent-barn]").TextContent.Should().Contain("Implementation: In source").And.Contain("Verification: Not assessed");
        cut.Find("[data-testid=su-tab-tests]").Click();
        cut.Find("[data-testid=su-guidance-note]").TextContent.Should().Contain("not test coverage");
        cut.Find("[data-testid=su-scenario-bufdata-fil]").TextContent.Should().Contain("Needs clarification");
        cut.FindAll("[data-testid^=su-scenario-]").Should().NotContain(e => e.TextContent.Contains("Verified"));
    }

    [Fact]
    public void Search_finds_steps_across_accents_and_filters_the_lists()
    {
        var cut = Render(new Api(View()), "?tab=steps");
        cut.Find("[data-testid=su-search]").Input("periodelas");
        cut.Find("[data-testid=su-search-count]").TextContent.Should().MatchRegex(@"\d+ matches");
        cut.FindAll("[data-testid=su-step-L1]").Should().ContainSingle();
        cut.FindAll("[data-testid=su-step-01]").Should().BeEmpty();
    }

    [Fact]
    public void Imported_content_cannot_inject_markup_or_unsafe_links()
    {
        var seed = Seed();
        var hostile = seed with
        {
            Purpose = "<script>alert('x')</script><img src=x onerror=alert(1)>",
            Components = [.. seed.Components.Select(c => c.Id == "mm" ? c with { Evidence = [new GuideEvidence { Class = GuideEvidenceClass.Inferred, Note = "<b>bold</b>", Link = "javascript:alert(1)" }] } : c)],
        };
        var cut = Render(new Api(View(hostile)));
        cut.Find("[data-testid=su-purpose]").TextContent.Should().Contain("<script>", "text stays text");
        cut.FindAll("script, img[onerror]").Should().BeEmpty();
        cut.Find("[data-testid=su-node-mm]").Click();
        cut.Find("[data-testid=su-node-detail]").QuerySelectorAll("a").Should().NotContain(a => (a.GetAttribute("href") ?? "").StartsWith("javascript", StringComparison.Ordinal));
        cut.Find("[data-testid=su-node-detail]").InnerHtml.Should().Contain("&lt;b&gt;bold&lt;/b&gt;");
    }

    [Fact]
    public void Readers_cannot_edit_and_source_changes_say_the_guide_may_need_review()
    {
        var cut = Render(new Api(View(changes: [new("m2lb", "M2LB-applikasjonen: M2LB (7).zip", "Imported after this revision was written — the guide may need review", 12)])), "?tab=revisions");
        cut.Find("[data-testid=su-read-only]").TextContent.Should().Contain("Read only");
        cut.FindAll("[data-testid=su-edit], [data-testid=su-approve], [data-testid=su-publish]").Should().BeEmpty();
        cut.Find("[data-testid=su-changes]").TextContent.Should().Contain("may need review (1)");
        cut.Find("[data-testid=su-changes]").Click();
        cut.Find("[data-testid=su-source-changes]").TextContent.Should().Contain("12 claims");
    }

    [Fact]
    public async Task An_authorized_reviewer_edits_a_draft_and_submits_it_through_the_lifecycle()
    {
        var api = new Api(View(), canEdit: true);
        var cut = Render(api, "?tab=revisions");
        cut.Find("[data-testid=su-edit]").Click();
        cut.Find("[data-testid=su-edit-purpose]").Change("Ny formulering av formålet.");
        cut.Find("[data-testid=su-change-summary]").Change("Formål presisert");
        await cut.Find("[data-testid=su-save-draft]").ClickAsync(new());
        api.Saved!.Content.Purpose.Should().Be("Ny formulering av formålet.");
        api.Saved.ChangeSummary.Should().Be("Formål presisert");
        api.Saved.BaseFingerprint.Should().Be(new string('a', 64), "concurrent edits are detected against the draft the reviewer opened");
        cut.WaitForAssertion(() => cut.Find("[data-testid=su-message]").TextContent.Should().Contain("not published until"));
        await cut.Find("[data-testid=su-submit]").ClickAsync(new());
        api.Actions.Should().Equal("submit:1");
    }

    [Fact]
    public void A_published_guide_has_no_edit_of_the_published_revision_only_a_new_draft()
    {
        var cut = Render(new Api(View(state: GuideRevisionState.Published), canEdit: true), "?tab=revisions");
        cut.Find("[data-testid=su-review]").TextContent.Should().Be("Published (revision 1)");
        cut.Find("[data-testid=su-edit]").TextContent.Should().Be("Start a new draft");
        cut.FindAll("[data-testid=su-publish], [data-testid=su-approve]").Should().BeEmpty();
    }

    [Fact]
    public void Without_a_guide_the_page_says_so_honestly()
    {
        var cut = Render(new Api(null));
        cut.Find("[data-testid=su-empty]").TextContent.Should().Contain("No system guide available").And.Contain("not created from an import automatically");
    }

    // ── Presentation (generic) ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_layout_puts_the_main_journey_on_top_and_packs_side_flows_without_overlap()
    {
        var layout = P.Layout(Seed());
        var at = layout.Nodes.ToDictionary(n => n.Component.Id);
        new[] { "altinn", "mu", "mm", "skole", "periodelaas", "bufdata" }.Select(id => at[id].Row).Should().OnlyContain(r => r == 0);
        new[] { "altinn", "mu", "mm", "skole", "periodelaas", "bufdata" }.Select(id => at[id].Column).Should().BeInAscendingOrder();
        at["person"].Row.Should().BeGreaterThan(0);
        at["person"].Column.Should().Be(at["skole"].Column, "an identity lookup sits under the component that calls it");
        at["skoleadapter"].Column.Should().BeLessThan(at["skole"].Column, "a feeding lane leads in from the left");
        layout.Nodes.GroupBy(n => (n.Row, n.Column)).Should().OnlyContain(g => g.Count() == 1, "no two components share a cell");
        layout.Nodes.Max(n => n.Row).Should().BeLessThanOrEqualTo(2, "side flows are packed compactly");
        layout.Edges.Should().HaveCount(Seed().Edges.Count);
    }

    [Fact]
    public void The_layout_is_generic_for_another_system()
    {
        var other = new SystemGuideContent
        {
            Title = "Orders",
            Components = [new() { Id = "shop", Name = "Shop" }, new() { Id = "pay", Name = "Payment", Lane = "pay" }, new() { Id = "wms", Name = "Warehouse" }, new() { Id = "erp", Name = "ERP", Lane = "erp" }],
            Edges = [new() { Id = "a", From = "shop", To = "wms", Kind = GuideFlowKind.MainReport, DataFormat = "Avro (v3)" }, new() { Id = "b", From = "shop", To = "pay", Kind = GuideFlowKind.SupportingData, DataFormat = "JSON" },
                     new() { Id = "c", From = "erp", To = "wms", Kind = GuideFlowKind.SupportingData, DataFormat = "CSV" }],
            Flows = [new() { Id = "f", Path = ["shop", "wms"] }],
        };
        var layout = P.Layout(other);
        layout.Nodes.Should().HaveCount(4);
        layout.Nodes.GroupBy(n => (n.Row, n.Column)).Should().OnlyContain(g => g.Count() == 1);
        layout.Edges.Single(e => e.Edge.Id == "a").ShortLabel.Should().Be("Avro");
        P.Short("Planlagt: Parquet + manifest").Should().Be("Parquet");
        P.Short("Kryptert XML (AES-256-CBC)").Should().Be("Kryptert XML");
        P.Short("Applikasjonskvittering v2 (XML)").Length.Should().BeLessThanOrEqualTo(14, "labels fit between columns; details carry the full text");
    }

    [Fact]
    public void Evidence_badges_never_rely_on_colour_alone_and_open_questions_come_first()
    {
        foreach (var c in Enum.GetValues<GuideEvidenceClass>()) P.Badge(c).Glyph.Should().NotBeNullOrEmpty();
        P.Classes([new() { Class = GuideEvidenceClass.SourceImplemented }, new() { Class = GuideEvidenceClass.NeedsClarification }])
            .Should().Equal(GuideEvidenceClass.NeedsClarification, GuideEvidenceClass.SourceImplemented);
        P.Classes([]).Should().Equal(GuideEvidenceClass.NotAssessed);
        P.Badge(GuideEvidenceClass.SourceImplemented).Meaning.Should().Contain("not that the integration works");
    }
}
