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
        var edgeButton = cut.Find("[data-testid=su-edge-e-mm-skole]");
        (edgeButton.TagName, edgeButton.GetAttribute("type")).Should().Be(("BUTTON", "button"), "a native button: Enter and Space work in every browser (checked in browser acceptance)");
        edgeButton.Click();
        cut.FindAll("[data-testid=su-edge-detail]").Should().BeEmpty("selecting again toggles the details off");
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
    public void The_journey_puts_the_six_main_stages_in_order_with_one_connector_between_neighbours()
    {
        var j = P.BuildJourney(Seed());
        j.Stages.Select(s => s.Component.Id).Should().Equal("altinn", "mu", "mm", "skole", "periodelaas", "bufdata");
        j.Connectors.Select(c => c.Edge?.Id).Should().Equal("e-altinn-mu", "e-mu-mm", "e-mm-skole", "e-skole-periodelaas", "e-periodelaas-bufdata");
        j.Returns.Should().ContainSingle().Which.Edge.Id.Should().Be("e-mm-mu-ack", "the acknowledgment runs back on its own track, never across the stages");
        j.Returns[0].Should().Match<P.JourneyReturn>(r => r.Start == 1 && r.End == 2 && r.Track == 0);
        j.Groups.Select(g => g.Lane).Should().BeEquivalentTo(["person", "tjeneste", "utdanning"]);
        var education = j.Groups.Single(g => g.Lane == "utdanning");
        education.Members.Select(m => m.Id).Should().Equal("birk", "eventhub", "skoleadapter");
        education.Chain.Select(e => e.Id).Should().Equal("e-birk-eventhub", "e-eventhub-skoleadapter");
        education.Attachments.Should().ContainSingle(a => a.Edge.Id == "e-skoleadapter-skole" && a.IntoStage && a.StageId == "skole");
        j.Groups.Single(g => g.Lane == "person").Attachments.Should().ContainSingle(a => a.StageId == "skole" && !a.IntoStage, "SkoleService calls Person");
        // Every edge of the guide is placed exactly once: connector, return, bypass, chain, attachment (+ reverse) or unplaced.
        var placed = j.Connectors.Select(c => c.Edge?.Id).OfType<string>().Concat(j.Returns.Select(r => r.Edge.Id)).Concat(j.Bypasses.Select(e => e.Id))
            .Concat(j.Groups.SelectMany(g => g.Chain.Select(e => e.Id).Concat(g.Attachments.SelectMany(a => new[] { a.Edge.Id, a.Reverse?.Id }.OfType<string>()))))
            .Concat(j.Unplaced.Select(e => e.Id)).ToList();
        placed.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(Seed().Edges.Select(e => e.Id));
    }

    private static SystemGuideContent Synthetic(string[] main, (string Id, string Lane)[]? side = null, (string Id, string From, string To, GuideFlowKind Kind)[]? edges = null) => new()
    {
        Title = "Synthetic",
        Components = [.. main.Select(id => new GuideComponent { Id = id, Name = id.ToUpperInvariant() }), .. (side ?? []).Select(s => new GuideComponent { Id = s.Id, Name = s.Id, Lane = s.Lane })],
        Edges = [.. (edges ?? []).Select(e => new GuideEdge { Id = e.Id, From = e.From, To = e.To, Kind = e.Kind, DataFormat = "JSON", Transport = "HTTP" })],
        Flows = [new() { Id = "main", Kind = GuideFlowKind.MainReport, Path = [.. main] }],
    };

    [Fact]
    public void Synthetic_layouts_A_to_O_follow_independently_stated_rules()
    {
        const GuideFlowKind M = GuideFlowKind.MainReport, S = GuideFlowKind.SupportingData, A = GuideFlowKind.Acknowledgment, I = GuideFlowKind.IdentityLookup;
        // A. Simple linear flow: two stages, one connector.
        var a = P.BuildJourney(Synthetic(["a", "b"], edges: [("ab", "a", "b", M)]));
        (a.Stages.Count, a.Connectors.Single().Edge!.Id).Should().Be((2, "ab"));
        // B. Six stages: five connectors, in order.
        var six = new[] { "s1", "s2", "s3", "s4", "s5", "s6" };
        var b = P.BuildJourney(Synthetic(six, edges: [.. six.Zip(six.Skip(1)).Select(p => ($"{p.First}-{p.Second}", p.First, p.Second, M))]));
        b.Connectors.Select(c => c.FromIndex).Should().Equal(0, 1, 2, 3, 4);
        (P.StageColumns(6).Split("minmax(").Length - 1).Should().Be(11, "six stage columns alternate with five connector columns");
        (P.StageColumns(6).Split("1fr)").Length - 1).Should().Be(6, "only the stage columns take the free space");
        // C. Main flow plus one dependency → one group attached to its stage.
        var c = P.BuildJourney(Synthetic(["a", "b"], [("x", "x")], [("ab", "a", "b", M), ("bx", "b", "x", I)]));
        c.Groups.Single().Attachments.Single().StageId.Should().Be("b");
        // D. Multiple supporting branches → one group per lane, none in the main row.
        var d = P.BuildJourney(Synthetic(["a", "b", "c"], [("x", "x"), ("y", "y"), ("z", "z")], [("ab", "a", "b", M), ("bc", "b", "c", M), ("ax", "a", "x", I), ("by", "b", "y", I), ("zc", "z", "c", S)]));
        d.Groups.Should().HaveCount(3);
        d.Stages.Select(s => s.Component.Id).Should().Equal("a", "b", "c");
        // E. Bidirectional request/response → one relationship with its answer, not two crossing arrows.
        var e = P.BuildJourney(Synthetic(["a"], [("p", "p")], [("ap", "a", "p", I), ("pa", "p", "a", I)]));
        e.Groups.Single().Attachments.Should().ContainSingle().Which.Reverse!.Id.Should().Be("pa");
        // F. Acknowledgment return path → a return on track 0, not a connector.
        var f = P.BuildJourney(Synthetic(["a", "b", "c"], edges: [("ab", "a", "b", M), ("bc", "b", "c", M), ("ca", "c", "a", A)]));
        f.Returns.Single().Should().Match<P.JourneyReturn>(r => r.Edge.Id == "ca" && r.Start == 0 && r.End == 2 && r.Track == 0);
        // G. Long component names are kept whole (text wraps in HTML; nothing is clipped by the model).
        var longName = new string('L', 80);
        P.BuildJourney(new SystemGuideContent { Components = [new() { Id = "a", Name = longName }], Flows = [] }).Stages.Single().Component.Name.Should().Be(longName);
        // H. Missing optional node → a gap, never a fabricated connection.
        var h = P.BuildJourney(Synthetic(["a", "b", "c"], edges: [("ab", "a", "b", M)]));
        h.Connectors[1].Edge.Should().BeNull();
        // I. Multiple edges between the same stages → the first is the connector, the rest are not lost.
        var i = P.BuildJourney(Synthetic(["a", "b"], edges: [("ab1", "a", "b", M), ("ab2", "a", "b", S)]));
        i.Connectors.Single().Edge!.Id.Should().Be("ab1");
        // J. Statuses come from each component's own evidence (no shared verdict).
        P.Badge(P.Classes([new GuideEvidence { Class = GuideEvidenceClass.SourceImplemented }], 1)[0]).Label.Should().NotBe(P.Badge(GuideEvidenceClass.NeedsClarification).Label);
        // M. Large node count: twelve stages, eleven connectors, no overlap of return tracks.
        var many = Enumerable.Range(1, 12).Select(n => $"n{n}").ToArray();
        var m = P.BuildJourney(Synthetic(many, edges: [.. many.Zip(many.Skip(1)).Select(p => ($"{p.First}-{p.Second}", p.First, p.Second, M)), ("r1", "n5", "n2", A), ("r2", "n4", "n3", A), ("r3", "n9", "n7", A)]));
        m.Connectors.Should().HaveCount(11);
        m.Returns.Single(r => r.Edge.Id == "r2").Track.Should().Be(1, "it overlaps r1, so it gets the next track");
        m.Returns.Single(r => r.Edge.Id == "r3").Track.Should().Be(0, "it does not overlap r1");
        foreach (var g in m.Returns.GroupBy(r => r.Track))
            g.SelectMany(r => Enumerable.Range(r.Start, r.End - r.Start + 1)).Should().OnlyHaveUniqueItems("returns on one track never overlap");
        // N. Cyclic relationship inside a side lane is kept as its chain, without loops in the main row.
        var n = P.BuildJourney(Synthetic(["a"], [("x", "l"), ("y", "l")], [("xy", "x", "y", S), ("yx", "y", "x", S), ("ya", "y", "a", S)]));
        n.Groups.Single().Chain.Select(x => x.Id).Should().BeEquivalentTo(["xy", "yx"]);
        // O. Unknown/ambiguous relationship → listed as unplaced, never drawn.
        var o = P.BuildJourney(Synthetic(["a", "b"], edges: [("ab", "a", "b", M), ("ghost", "a", "nobody", S)]));
        o.Unplaced.Single().Id.Should().Be("ghost");
        // Forward skip → a bypass, so the primary row stays crossing-free.
        P.BuildJourney(Synthetic(["a", "b", "c"], edges: [("ab", "a", "b", M), ("bc", "b", "c", M), ("ac", "a", "c", S)])).Bypasses.Single().Id.Should().Be("ac");
        P.Short("Planlagt: Parquet + manifest").Should().Be("Parquet");
        P.Short("Kryptert XML (AES-256-CBC)").Should().Be("Kryptert XML");
    }

    [Fact]
    public void The_rendered_flow_has_six_stage_buttons_supporting_groups_and_a_text_alternative_from_the_same_model()
    {
        var cut = Render(new Api(View()));
        cut.FindAll("[data-testid=su-flow-main] .sfj-node").Select(b => b.GetAttribute("data-testid")).Should()
            .Equal("su-node-altinn", "su-node-mu", "su-node-mm", "su-node-skole", "su-node-periodelaas", "su-node-bufdata");
        cut.Find("[data-testid=su-node-mm] .sfj-resp").TextContent.Should().NotContain("…", "responsibilities wrap instead of being clipped");
        cut.FindAll("[data-testid^=su-group-]").Select(g => g.GetAttribute("data-testid")).Should().BeEquivalentTo(["su-group-person", "su-group-tjeneste", "su-group-utdanning"]);
        cut.Find("[data-testid=su-flow-text]").TextContent.Should().Contain("Altinn → MU → MM").And.Contain("Applikasjonskvittering");
        cut.Find("[data-testid=su-edge-e-mm-mu-ack]").GetAttribute("aria-label").Should().Contain("back to MU");
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

    [Fact]
    public void What_to_test_offers_Prepare_in_Test_Plan_Suggestions_for_one_or_several_scenarios_without_approving_anything()
    {
        var cut = Render(new Api(View()), "?tab=tests");
        cut.Find("[data-testid=su-prepare-selected]").GetAttribute("aria-disabled").Should().Be("true", "nothing selected yet");
        cut.Find("[data-testid=su-pick-gyldig-rapport]").Change(true);
        cut.Find("[data-testid=su-pick-manglende-barn]").Change(true);
        var link = cut.Find("[data-testid=su-prepare-selected]");
        link.GetAttribute("href").Should().Be("test-plan-suggestions?guide=skolenaervaer&scenarios=gyldig-rapport%2Cmanglende-barn");
        link.GetAttribute("aria-disabled").Should().BeNull();
        cut.Find("[data-testid=su-prepare-bar]").TextContent.Should().Contain("Nothing is approved by this");

        cut.Find("[data-testid=su-scenario-periodelaas] .su-card-head").Click();
        cut.Find("[data-testid=su-prepare-periodelaas]").GetAttribute("href").Should().Be("test-plan-suggestions?guide=skolenaervaer&scenarios=periodelaas");
        cut.Find("[data-testid=su-scenario-periodelaas]").TextContent.Should().Contain("Suggested", "the guide's own state is unchanged");
    }
}
