using BirkNext.DeliveryReporting;
using BirkNext.Interpretation;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>Functional Quality Scope tab: filters and grouping are pure rules, scenarios are never shown as formal test cases, coverage and
/// Test Data read through the shared status vocabulary, "why included" is explicit, related membership is a visible toggle, and custom-scope
/// edits (drag/drop or Add, Exclude) are sent as explicit references.</summary>
public sealed class FunctionalQualityScopeUiTests : BunitContext
{
    private static readonly Guid Project = Guid.NewGuid();

    private static ScopeItem I(string key, ScopeArtifactType type, string id, string title, string module = "SkoleService", QualityCategory category = QualityCategory.Functional,
        ScopeRelation relation = ScopeRelation.Direct, ScopeCoverage coverage = ScopeCoverage.NoKnownCoverage, string? parent = null, ScopeInclusion inclusion = ScopeInclusion.Smart,
        bool unavailable = false) =>
        new(key, type, id, title, category, "fq.functional.behaviour", module, "001-rapport", "Motta rapport", parent, "P1", relation, inclusion, $"Owned by module {module}",
            coverage, null, ScopeTestDataState.Unknown, TestabilityClassification.Unclassified, unavailable, "SkoleService/specs/001-rapport/spec.md", 12);

    private static readonly ScopeItem Story = I("story:SkoleService/001-rapport/US1", ScopeArtifactType.UserStory, "US1", "Skoletjenesten tar imot en rapport");
    private static readonly ScopeItem Scenario = I("story:SkoleService/001-rapport/S1", ScopeArtifactType.Scenario, "S1", "Mottak av gyldig rapport");
    private static readonly ScopeItem Criterion = I("ac:SkoleService/001-rapport/AC-1", ScopeArtifactType.AcceptanceCriterion, "AC-1", "Gitt en gyldig rapport", parent: Story.Key,
        coverage: ScopeCoverage.CandidateCoverageOnly);
    private static readonly ScopeItem Privacy = I("story:SkoleService/001-rapport/US2", ScopeArtifactType.UserStory, "US2", "Ikke vis kode 6", category: QualityCategory.Privacy,
        coverage: ScopeCoverage.AutomatedCoverage);
    private static readonly ScopeItem PersonStory = I("story:Person/003-person/US1", ScopeArtifactType.UserStory, "US1", "Personhendelser", module: "Person", relation: ScopeRelation.Upstream);

    private static ScopeResolution Resolution(ScopeDefinition scope, params ScopeItem[] items) => new(scope, Guid.NewGuid(), "Monday", FunctionalQualityClassifier.RuleSetVersion, items,
        new(items.Count(i => i.Type == ScopeArtifactType.UserStory), items.Count(i => i.Type == ScopeArtifactType.Scenario), items.Count(i => i.Type == ScopeArtifactType.AcceptanceCriterion), 0, 0,
            items.GroupBy(i => i.Category).ToDictionary(g => g.Key, g => g.Count()), items.GroupBy(i => i.Coverage).ToDictionary(g => g.Key, g => g.Count()), 1, items.Count(i => i.Unavailable)),
        [new("Person", ScopeRelation.Upstream, "Upstream: Person → Skoletjenesten on \"Person events\" (Skolenærvær domain profile)", true)], [], FunctionalQualityScopeText.Precedence);

    // ── Pure rules ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Filters_combine_and_grouping_follows_features()
    {
        ScopeItem[] items = [Story, Scenario, Criterion, Privacy, PersonStory];
        FunctionalQualityScopePresentation.Apply(items, new ScopeFilter { Category = QualityCategory.Privacy }).Should().Equal(Privacy);
        FunctionalQualityScopePresentation.Apply(items, new ScopeFilter { Module = "person" }).Should().Equal(PersonStory);
        FunctionalQualityScopePresentation.Apply(items, new ScopeFilter { Coverage = ScopeCoverage.CandidateCoverageOnly }).Should().Equal(Criterion);
        FunctionalQualityScopePresentation.Apply(items, new ScopeFilter { Type = ScopeArtifactType.Scenario }).Should().Equal(Scenario);
        FunctionalQualityScopePresentation.Apply(items, new ScopeFilter { Relation = ScopeRelation.Upstream, Search = "hendelse" }).Should().Equal(PersonStory);
        FunctionalQualityScopePresentation.Group(items).Select(g => $"{g.Module}/{g.FeatureKey}").Should().Equal("SkoleService/001-rapport", "Person/001-rapport");
    }

    [Fact]
    public void Statuses_use_the_shared_vocabulary_and_candidate_coverage_is_never_available()
    {
        FunctionalQualityScopePresentation.CoverageStatus(ScopeCoverage.AutomatedCoverage).Should().Be(SemanticStatus.Available);
        FunctionalQualityScopePresentation.CoverageStatus(ScopeCoverage.CandidateCoverageOnly).Should().Be(SemanticStatus.NeedsReview);
        FunctionalQualityScopePresentation.CoverageStatus(ScopeCoverage.NotAssessed).Should().Be(SemanticStatus.NotAssessed);
        FunctionalQualityScopePresentation.TestDataStatus(ScopeTestDataState.MissingRequired).Should().Be(SemanticStatus.Missing);
        FunctionalQualityScopePresentation.TestDataStatus(ScopeTestDataState.Unknown).Should().Be(SemanticStatus.Unknown);
        FunctionalQualityScopePresentation.Href("profile:skolenaervaer").Should().Be("delivery-reporting?tab=scope&scope=profile%3Askolenaervaer");
        DeliveryReportingPresentation.TabOrDefault("scope").Should().Be("scope");
    }

    // ── Panel ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private Mock<IFunctionalQualityScopeApiService> Api(ScopeResolution resolution, params AvailableScope[] available)
    {
        var api = new Mock<IFunctionalQualityScopeApiService>();
        api.Setup(a => a.AvailableAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync((available, (string?)null));
        api.Setup(a => a.ResolveAsync(Project, It.IsAny<ScopeQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync((resolution, (string?)null));
        api.Setup(a => a.SnapshotsAsync(Project, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Services.AddSingleton(api.Object);
        return api;
    }

    [Fact]
    public void The_deep_linked_scope_is_selected_and_items_show_type_reason_and_coverage()
    {
        var builtIn = new ScopeDefinition { Name = "Skolenærvær", Roots = ["profile:skolenaervaer"], BuiltIn = true };
        var api = Api(Resolution(builtIn, Story, Criterion, Scenario, Privacy), new AvailableScope("module:Person", "Person", "Module", "1 feature(s)"),
            new AvailableScope("profile:skolenaervaer", "Skolenærvær", "Domain flow", "2 module(s)"));
        var cut = Render<FunctionalQualityScopePanel>(p => p.Add(x => x.ProjectId, Project).Add(x => x.InitialScope, "profile:skolenaervaer"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=fqs-scope-name]").TextContent.Should().Be("Skolenærvær"));
        api.Verify(a => a.ResolveAsync(Project, It.Is<ScopeQuery>(q => q.ScopeKey == "profile:skolenaervaer" && !q.IncludeRelated && !q.IncludeTechnical), It.IsAny<CancellationToken>()));
        cut.Find("[data-testid=fqs-count-testcases]").TextContent.Should().Be("None recorded");
        cut.Find("[data-testid=fqs-count-scenarios]").TextContent.Should().Be("1");
        cut.Find("[data-testid=fqs-technical-excluded]").TextContent.Should().Contain("1 technical item(s) excluded");
        var scenario = cut.Find($"[data-key='{Scenario.Key}']");
        scenario.TextContent.Should().Contain("Acceptance scenario").And.NotContain("Formal test case");
        cut.Find($"[data-key='{Criterion.Key}'] [data-testid=fqs-coverage]").TextContent.Should().Contain("Candidate coverage only");
        cut.Find($"[data-key='{Criterion.Key}']").ClassList.Should().Contain("is-child");

        cut.Find($"[data-key='{Story.Key}'] [data-testid=fqs-why]").Click();
        cut.Find($"[data-key='{Story.Key}'] [data-testid=fqs-why-text]").TextContent.Should().Contain("Owned by module SkoleService").And.Contain("fq.functional.behaviour");

        cut.Find("[data-testid=fqs-category-Privacy]").Click();
        cut.FindAll("[data-testid=fqs-item]").Should().ContainSingle().Which.GetAttribute("data-key").Should().Be(Privacy.Key);
        cut.Find("[data-testid=fqs-related]").TextContent.Should().Contain("Person").And.Contain("Upstream");
        cut.FindAll("[data-testid=fqs-edit]").Should().BeEmpty("a built-in scope is not editable; clone it as a custom scope instead");
    }

    [Fact]
    public void Include_related_and_technical_are_explicit_toggles_sent_to_the_server()
    {
        var api = Api(Resolution(new ScopeDefinition { Name = "Person", BuiltIn = true }, PersonStory), new AvailableScope("module:Person", "Person", "Module", "1"));
        var cut = Render<FunctionalQualityScopePanel>(p => p.Add(x => x.ProjectId, Project));
        cut.WaitForElement("[data-testid=fqs-mode-related]");
        cut.Find("[data-testid=fqs-mode-related]").Change(true);
        cut.Find("[data-testid=fqs-technical]").Change(true);
        api.Verify(a => a.ResolveAsync(Project, It.Is<ScopeQuery>(q => q.IncludeRelated && q.IncludeTechnical), It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }

    [Fact]
    public void A_custom_scope_adds_by_drop_or_Add_and_excludes_through_explicit_references()
    {
        var id = Guid.NewGuid();
        var custom = new ScopeDefinition { Id = id, Name = "Release 1", Roots = ["module:SkoleService"], Version = 3 };
        var api = Api(Resolution(custom, Story), new AvailableScope("custom:" + id, "Release 1", "Custom", "Version 3", id));
        api.Setup(a => a.AllItemsAsync(Project, false, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Resolution(new ScopeDefinition { Name = "All items" }, Story, PersonStory), (string?)null));
        api.Setup(a => a.ReferenceAsync(Project, id, It.IsAny<string>(), It.IsAny<ScopeInclusion>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((custom with { Version = 4 }, (string?)null));
        var cut = Render<FunctionalQualityScopePanel>(p => p.Add(x => x.ProjectId, Project));

        cut.WaitForElement("[data-testid=fqs-edit]").Click();
        cut.WaitForElement("[data-testid=fqs-candidate]");
        cut.FindAll("[data-testid=fqs-candidate]").Should().ContainSingle("items already in the scope are not offered again")
            .Which.GetAttribute("data-key").Should().Be(PersonStory.Key);

        cut.Find("[data-testid=fqs-candidate]").TriggerEvent("ondragstart", new Microsoft.AspNetCore.Components.Web.DragEventArgs());
        cut.Find("[data-testid=fqs-dropzone]").TriggerEvent("ondrop", new Microsoft.AspNetCore.Components.Web.DragEventArgs());
        api.Verify(a => a.ReferenceAsync(Project, id, PersonStory.Key, ScopeInclusion.ExplicitInclude, It.IsAny<CancellationToken>()), Times.Once());

        cut.Find("[data-testid=fqs-add]").Click();
        api.Verify(a => a.ReferenceAsync(Project, id, PersonStory.Key, ScopeInclusion.ExplicitInclude, It.IsAny<CancellationToken>()), Times.Exactly(2));

        cut.Find($"[data-key='{Story.Key}'] [data-testid=fqs-exclude]").Click();
        api.Verify(a => a.ReferenceAsync(Project, id, Story.Key, ScopeInclusion.ExplicitExclude, It.IsAny<CancellationToken>()), Times.Once());
        cut.Find("[data-testid=fqs-notice]").TextContent.Should().Contain("Scope version 4");
    }

    [Fact]
    public void An_empty_manual_scope_still_accepts_its_first_item_by_drop()
    {
        var id = Guid.NewGuid();
        var manual = new ScopeDefinition { Id = id, Name = "Manual" };
        var api = Api(Resolution(manual), new AvailableScope("custom:" + id, "Manual", "Custom", "Version 1", id));
        api.Setup(a => a.AllItemsAsync(Project, false, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Resolution(new ScopeDefinition { Name = "All items" }, PersonStory), (string?)null));
        api.Setup(a => a.ReferenceAsync(Project, id, PersonStory.Key, ScopeInclusion.ExplicitInclude, It.IsAny<CancellationToken>()))
            .ReturnsAsync((manual with { Version = 2 }, (string?)null));
        var cut = Render<FunctionalQualityScopePanel>(p => p.Add(x => x.ProjectId, Project));

        cut.WaitForElement("[data-testid=fqs-edit]").Click();
        cut.WaitForElement("[data-testid=fqs-candidate]").TriggerEvent("ondragstart", new Microsoft.AspNetCore.Components.Web.DragEventArgs());
        cut.Find("[data-testid=fqs-dropzone]").TextContent.Should().Contain("Nothing is in this scope yet");
        cut.Find("[data-testid=fqs-dropzone]").TriggerEvent("ondrop", new Microsoft.AspNetCore.Components.Web.DragEventArgs());
        api.Verify(a => a.ReferenceAsync(Project, id, PersonStory.Key, ScopeInclusion.ExplicitInclude, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void An_unavailable_explicit_item_stays_visible_and_is_not_openable()
    {
        var gone = I("story:Person/003-person/US1", ScopeArtifactType.UserStory, "US1", "Unavailable in current source", module: "Person", relation: ScopeRelation.ExplicitlyIncluded,
            inclusion: ScopeInclusion.ExplicitInclude, unavailable: true, coverage: ScopeCoverage.NotAssessed);
        Api(Resolution(new ScopeDefinition { Id = Guid.NewGuid(), Name = "Manual" }, gone), new AvailableScope("module:Person", "Person", "Module", "1"));
        var cut = Render<FunctionalQualityScopePanel>(p => p.Add(x => x.ProjectId, Project));
        var row = cut.WaitForElement($"[data-key='{gone.Key}']");
        row.ClassList.Should().Contain("is-unavailable");
        row.QuerySelector("[data-testid=fqs-unavailable]")!.TextContent.Should().Contain("Unavailable in current source");
        row.QuerySelector("[data-testid=fqs-open]")!.HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=fqs-unavailable-count]").TextContent.Should().Contain("1 unavailable");
    }

    [Fact]
    public void Opening_an_item_loads_detail_lazily_and_saves_the_test_design()
    {
        var api = Api(Resolution(new ScopeDefinition { Name = "SkoleService", BuiltIn = true }, Story), new AvailableScope("module:SkoleService", "SkoleService", "Module", "1"));
        api.Setup(a => a.DetailAsync(Project, Story.Key, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScopeItemDetail(Story, "Skoletjenesten tar imot en rapport og lagrer fraværet", [Criterion], [], null));
        api.Setup(a => a.AssessAsync(Project, It.IsAny<TestDesignAssessmentView>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, TestDesignAssessmentView a, CancellationToken _) => (a with { ReviewedAt = DateTimeOffset.UnixEpoch }, (string?)null));
        var cut = Render<FunctionalQualityScopePanel>(p => p.Add(x => x.ProjectId, Project));
        cut.WaitForElement("[data-testid=fqs-open]");
        api.Verify(a => a.DetailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never(), "detail is lazy");

        cut.Find("[data-testid=fqs-open]").Click();
        cut.WaitForElement("[data-testid=fqs-detail]").TextContent.Should().Contain("lagrer fraværet").And.Contain("AC-1");
        cut.Find("[data-testid=fqs-testability]").Change("Manual");
        cut.Find("[data-testid=fqs-required-data]").Change("td-1, td-2");
        cut.Find("[data-testid=fqs-assessment]").Submit();
        api.Verify(a => a.AssessAsync(Project, It.Is<TestDesignAssessmentView>(v => v.ItemKey == Story.Key && v.Testability == TestabilityClassification.Manual
            && v.RequiredTestDataAssetIds.SequenceEqual(new[] { "td-1", "td-2" })), It.IsAny<CancellationToken>()), Times.Once());
    }

    // ── Scenario candidates (scenarios/1) ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scenario_drafts_cover_only_criteria_without_automated_tests_with_explainable_priority()
    {
        var gwt = I("ac:SkoleService/001-rapport/AC-2", ScopeArtifactType.AcceptanceCriterion, "AC-2", "**Given** a valid report, **When** it is received, **Then** it is stored.", parent: Story.Key);
        var duplicate = I("ac:SkoleService/001-rapport/AC-3", ScopeArtifactType.AcceptanceCriterion, "AC-3", "It is  STORED.", parent: Story.Key);
        var privacy = I("ac:SkoleService/001-rapport/AC-4", ScopeArtifactType.AcceptanceCriterion, "AC-4", "Kode 6 is hidden", category: QualityCategory.Privacy, parent: Story.Key);
        var automated = I("ac:SkoleService/001-rapport/AC-5", ScopeArtifactType.AcceptanceCriterion, "AC-5", "Automated", parent: Story.Key, coverage: ScopeCoverage.AutomatedCoverage);
        var gone = I("ac:SkoleService/001-rapport/AC-6", ScopeArtifactType.AcceptanceCriterion, "AC-6", "Gone", parent: Story.Key, unavailable: true);

        var drafts = TestScenarioIntelligence.Drafts(Resolution(new ScopeDefinition { Name = "S" }, Story, Criterion, gwt, duplicate, privacy, automated, gone));

        drafts.Select(d => d.Key).Should().Equal(["draft:" + Criterion.Key, "draft:" + gwt.Key, "draft:" + duplicate.Key, "draft:" + privacy.Key],
            "automated and unavailable criteria get no draft");
        var g = drafts[1];
        (g.Given, g.When, g.Then).Should().Be(("a valid report", "it is received", "it is stored."), "Given / When / Then only as the criterion states it");
        (g.Priority, g.PriorityReason).Should().Be((ScenarioPriority.High, "P1 user story with no known coverage"));
        g.DerivedFrom.Should().Be("SkoleService / 001-rapport / US1 / AC-2");
        drafts[0].Given.Should().BeNull("no Given/When/Then is invented");
        drafts[0].Priority.Should().Be(ScenarioPriority.Medium, "a candidate test may already cover it");
        drafts[0].Coverage.Should().StartWith("Candidate coverage only");
        drafts[2].DuplicateOf.Should().Be(g.Key, "same feature and same expected outcome");
        drafts[3].PriorityReason.Should().Be("Privacy criterion with no known coverage");

        var lowStory = Story with { Key = "story:x/US9", Priority = null };
        var reliability = I("ac:x/AC-9", ScopeArtifactType.AcceptanceCriterion, "AC-9", "Retries", category: QualityCategory.Reliability, parent: lowStory.Key);
        var plain = reliability with { Key = "ac:x/AC-10", Category = QualityCategory.Functional, Title = "A very long and complex criterion " + new string('x', 400) };
        TestScenarioIntelligence.Priority(reliability, lowStory).Should().Be((ScenarioPriority.Medium, "Reliability criterion"));
        TestScenarioIntelligence.Priority(plain, lowStory).Should().Be((ScenarioPriority.Low, "No story priority stated"), "length or complexity never raises priority");
    }

    [Fact]
    public void The_panel_shows_scenario_candidates_as_drafts_with_their_source()
    {
        var gwt = I("ac:SkoleService/001-rapport/AC-2", ScopeArtifactType.AcceptanceCriterion, "AC-2", "Given a valid report, when it is received, then it is stored.", parent: Story.Key);
        Api(Resolution(new ScopeDefinition { Name = "Skolenærvær", BuiltIn = true }, Story, Criterion, gwt, Privacy), new AvailableScope("module:SkoleService", "SkoleService", "Module", "1"));
        var cut = Render<FunctionalQualityScopePanel>(p => p.Add(x => x.ProjectId, Project).Add(x => x.InitialScope, "module:SkoleService"));

        cut.WaitForElement("[data-testid=fqs-scenarios]");
        cut.Find("[data-testid=fqs-scenarios] summary").TextContent.Should().Contain("2 drafts").And.Contain("1 high priority");
        cut.Find("[data-testid=fqs-scenarios]").TextContent.Should().Contain("not coverage");
        var items = cut.FindAll("[data-testid=fqs-scenario]");
        items[0].GetAttribute("data-priority").Should().Be("High");
        items[0].TextContent.Should().Contain("Given").And.Contain("a valid report").And.Contain("Derived from SkoleService / 001-rapport / US1 / AC-2");
    }

    // ── Acceptance journey: filter → requirement → criteria → tests → test → back, with filter and selection kept ─────────────

    private static ScopeTestLink Link(string name, ScopeTestLinkKind kind, string review = "NotReviewed") =>
        new($"tests/{name}->Verifies->US1", "env-dev", kind, $"tests/{name}", name, kind == ScopeTestLinkKind.Explicit ? "SkoleService.Tests/RapportTests.cs:42" : null,
            kind == ScopeTestLinkKind.Explicit ? "Explicit" : "Candidate", "High", "verifies.test-name-id", $"{name} names US1.", review,
            [new ScopeTestEvidence("TestName", "RapportTests.cs", $"[Fact] {name}")], "Execution evidence not linked — a discovered test is not an executed test.");

    private static readonly string LongText = "Som skoletjeneste vil jeg motta rapporten slik at fraværet registreres. " + string.Concat(Enumerable.Repeat("Rapporten valideres mot skjemaet og lagres. ", 10));

    private (IRenderedComponent<FunctionalQualityScopePanel> Cut, Mock<IFunctionalQualityScopeApiService> Api) Journey(Mock<ICanonicalInterpretationApiService>? review = null, string? startUrl = null)
    {
        var builtIn = new ScopeDefinition { Name = "Skolenærvær", Roots = ["profile:skolenaervaer"], BuiltIn = true };
        var api = Api(Resolution(builtIn, Story, Criterion, Scenario, Privacy, PersonStory), new AvailableScope("profile:skolenaervaer", "Skolenærvær", "Domain flow", "2 module(s)"));
        api.Setup(a => a.DetailAsync(Project, Privacy.Key, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ScopeItemDetail(Privacy, LongText, [Criterion], ["1 test(s) explicitly verify US2."], null)
        {
            TestsAssessed = true, Tests = [Link("Sends_report", ScopeTestLinkKind.Explicit), Link("Maybe_report", ScopeTestLinkKind.Candidate), Link("Not_report", ScopeTestLinkKind.Rejected, "Rejected")],
        });
        if (review is not null) Services.AddSingleton(review.Object);
        if (startUrl is not null) Services.GetRequiredService<NavigationManager>().NavigateTo(startUrl);
        var cut = Render<FunctionalQualityScopePanel>(p => p.Add(x => x.ProjectId, Project));
        cut.WaitForElement("[data-testid=fqs-item]");
        return (cut, api);
    }

    [Fact]
    public void Acceptance_journey_keeps_the_filter_and_selection_and_separates_explicit_tests_from_candidates()
    {
        var (cut, _) = Journey();
        var nav = Services.GetRequiredService<NavigationManager>();
        cut.FindAll("[data-testid=fqs-item]").Should().HaveCount(5);
        cut.Find("[data-testid=fqs-filter-category]").Change("Privacy");
        cut.FindAll("[data-testid=fqs-item]").Should().ContainSingle();
        cut.Find("[data-testid=fqs-shown]").TextContent.Should().StartWith("Showing 1 of 5 items");
        cut.Find("[data-testid=fqs-filter-chip]").TextContent.Should().Contain("Quality: Privacy");

        cut.Find("[data-testid=fqs-open]").Click();
        cut.WaitForElement("[data-testid=fqs-detail]");
        cut.Find($"[data-key='{Privacy.Key}']").ClassList.Should().Contain("is-selected");
        cut.Find("[data-testid=fqs-full-text]").ClassList.Should().Contain("is-clamped");
        cut.Find("[data-testid=fqs-expand-text]").Click();
        cut.Find("[data-testid=fqs-full-text]").ClassList.Should().NotContain("is-clamped");
        cut.Find("[data-testid=fqs-sec-criteria]").TextContent.Should().Contain("AC-1");

        cut.Find("[data-testid=fqs-tests-explicit]").TextContent.Should().Contain("Sends_report").And.NotContain("Maybe_report");
        cut.Find("[data-testid=fqs-tests-candidates]").TextContent.Should().Contain("Maybe_report").And.Contain("Candidate — not confirmed");
        cut.Find("[data-testid=fqs-tests-rejected]").TextContent.Should().Contain("Not_report").And.Contain("not coverage");

        cut.FindAll("[data-testid=fqs-test-open]")[0].Click();
        cut.Find("[data-testid=fqs-test-identity]").TextContent.Should().Be("tests/Sends_report");
        cut.Find("[data-testid=fqs-test-location]").TextContent.Should().Be("SkoleService.Tests/RapportTests.cs:42");
        cut.Find("[data-testid=fqs-test-relationship]").TextContent.Should().Contain("Explicitly linked").And.Contain("VERIFIES");
        cut.Find("[data-testid=fqs-test-execution]").TextContent.Should().Contain("not an executed test", "discovery is never execution");
        cut.Find("[data-testid=fqs-test-evidence]").TextContent.Should().Contain("RapportTests.cs");
        cut.Find("[data-testid=fqs-review-readonly]").TextContent.Should().Contain("Correlation Review", "without the review service the test detail is read-only, not a dead button");

        cut.Find("[data-testid=fqs-test-back]").Click();
        cut.Find("[data-testid=fqs-sec-tests]");
        cut.Find("[data-testid=fqs-detail-close]").Click();
        cut.FindAll("[data-testid=fqs-detail]").Should().BeEmpty();
        cut.FindAll("[data-testid=fqs-item]").Should().ContainSingle("the Privacy filter stays active");
        nav.Uri.Should().Contain("fq-quality=Privacy");

        cut.Find("[data-testid=fqs-open]").Click();
        cut.WaitForElement("[data-testid=fqs-detail]");
        nav.Uri.Should().Contain("fq-item=" + Uri.EscapeDataString(Privacy.Key));
    }

    [Fact]
    public void Returning_to_the_page_restores_the_filter_and_the_selected_item_from_the_url()
    {
        var (cut, api) = Journey(startUrl: $"delivery-reporting?tab=scope&fq-quality=Privacy&fq-item={Uri.EscapeDataString(Privacy.Key)}");
        cut.FindAll("[data-testid=fqs-item]").Should().ContainSingle().Which.GetAttribute("data-key").Should().Be(Privacy.Key);
        cut.WaitForAssertion(() => cut.Find("[data-testid=fqs-detail]").GetAttribute("data-key").Should().Be(Privacy.Key));
        api.Verify(a => a.ResolveAsync(Project, It.IsAny<ScopeQuery>(), It.IsAny<CancellationToken>()), Times.Once(), "restoring is filtering, never re-analysis");
    }

    [Fact]
    public void A_filter_that_hides_the_selected_item_clears_the_selection_instead_of_leaving_a_stale_detail()
    {
        var (cut, _) = Journey();
        cut.Find($"[data-key='{Privacy.Key}'] [data-testid=fqs-open]").Click();
        cut.WaitForElement("[data-testid=fqs-detail]");
        cut.Find("[data-testid=fqs-filter-module]").Change("Person");
        cut.FindAll("[data-testid=fqs-detail]").Should().BeEmpty();
        cut.Find("[data-testid=fqs-notice]").TextContent.Should().Contain("no longer selected");
        cut.Find("[data-testid=fqs-filter-chip]").Click();
        cut.FindAll("[data-testid=fqs-item]").Should().HaveCount(5);
    }

    [Fact]
    public void Advanced_filters_are_grouped_apart_and_open_when_one_is_active()
    {
        var (cut, _) = Journey();
        cut.Find("[data-testid=fqs-advanced]").HasAttribute("open").Should().BeFalse();
        cut.Find("[data-testid=fqs-filter-coverage]").Change("AutomatedCoverage");
        cut.Find("[data-testid=fqs-advanced]").HasAttribute("open").Should().BeTrue();
        cut.FindAll("[data-testid=fqs-item]").Should().ContainSingle().Which.GetAttribute("data-key").Should().Be(Privacy.Key);
        cut.Find("[data-testid=fqs-clear-filters]").Click();
        cut.FindAll("[data-testid=fqs-item]").Should().HaveCount(5);
    }

    [Fact]
    public void A_candidate_is_reviewed_through_the_shared_registry_and_the_item_is_re_read()
    {
        var review = new Mock<ICanonicalInterpretationApiService>();
        review.Setup(r => r.DecisionsAsync("env-dev", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<ReviewDecisionView>)[], (string?)null));
        review.Setup(r => r.DecideAsync("env-dev", It.IsAny<ReviewDecisionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new ReviewDecisionView(Guid.NewGuid(), 1, ReviewTargetKind.Relation, "k", ReviewDecisionOutcome.Rejected, "local user", DateTimeOffset.UnixEpoch,
                null, "fp", "rules", null, null, "{}"), (string?)null));
        var (cut, api) = Journey(review);
        cut.Find($"[data-key='{Privacy.Key}'] [data-testid=fqs-open]").Click();
        cut.WaitForElement("[data-testid=fqs-tests-candidates]");
        cut.FindAll("[data-testid=fqs-test-open]").Single(b => b.TextContent == "Maybe_report").Click();
        cut.Find("[data-testid=fqs-review-reject]").Click();
        review.Verify(r => r.DecideAsync("env-dev", It.Is<ReviewDecisionRequest>(q => q.TargetKind == ReviewTargetKind.Relation
            && q.TargetKey == "tests/Maybe_report->Verifies->US1" && q.Outcome == ReviewDecisionOutcome.Rejected), It.IsAny<CancellationToken>()), Times.Once());
        api.Verify(a => a.DetailAsync(Project, Privacy.Key, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2), "the classification is re-read from the registry, never patched locally");
    }

    // ── Gap 2: evidence environment; Gap 3: TRX execution per linked test; Gap 5: schemas; Gap 1: session restore ──────────────────────

    private static readonly ScopeEvidenceContext TwoEnvironments = new("env-dev", false,
        [new ScopeEvidenceEnvironment("env-dev", 20, 12), new ScopeEvidenceEnvironment("env-qa", 4, 3)], "Source relationships interpreted for this environment's source snapshots.");

    private (IRenderedComponent<FunctionalQualityScopePanel> Cut, Mock<IFunctionalQualityScopeApiService> Api) EvidenceJourney(ScopeItemDetail? detail = null,
        ScopeEvidenceContext? evidence = null, BirkNext.Web.Models.SddLifecycleState? lifecycle = null, ExplorerViewStateStore? store = null, string? startUrl = null)
    {
        if (store is not null) Services.AddSingleton(store);
        var builtIn = new ScopeDefinition { Name = "Skolenærvær", Roots = ["profile:skolenaervaer"], BuiltIn = true };
        var resolution = Resolution(builtIn, Story, Criterion, Privacy) with { Evidence = evidence ?? TwoEnvironments };
        var api = Api(resolution, new AvailableScope("profile:skolenaervaer", "Skolenærvær", "Domain flow", "2 module(s)"));
        api.Setup(a => a.ResolveAsync(Project, It.Is<ScopeQuery>(q => q.Environment == "env-qa"), It.IsAny<CancellationToken>()))
            .ReturnsAsync((resolution with { Evidence = TwoEnvironments with { Environment = "env-qa" } }, (string?)null));
        api.Setup(a => a.DetailAsync(Project, Privacy.Key, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(detail ?? new ScopeItemDetail(Privacy, "Ikke vis kode 6", [], [], null)
        {
            TestsAssessed = true, Evidence = evidence ?? TwoEnvironments,
        });
        if (lifecycle is not null)
        {
            var repo = new Mock<IWorkspaceArtifactRepository>();
            repo.Setup(r => r.SddLifecycle).Returns(lifecycle);
            Services.AddSingleton(repo.Object);
        }
        if (startUrl is not null) Services.GetRequiredService<NavigationManager>().NavigateTo(startUrl);
        var cut = Render<FunctionalQualityScopePanel>(p => p.Add(x => x.ProjectId, Project));
        cut.WaitForElement("[data-testid=fqs-item]");
        return (cut, api);
    }

    [Fact]
    public void The_evidence_environment_is_explicit_and_switching_it_re_reads_coverage_and_the_open_item_for_that_environment()
    {
        var (cut, api) = EvidenceJourney();
        var nav = Services.GetRequiredService<NavigationManager>();
        cut.Find("[data-testid=fqs-environment]").QuerySelectorAll("option").Select(o => o.GetAttribute("value")).Should().Equal(["env-dev", "env-qa", ScopeEnvironments.All]);
        cut.Find("[data-testid=fqs-evidence-context]").GetAttribute("data-environment").Should().Be("env-dev");
        cut.Find($"[data-key='{Privacy.Key}'] [data-testid=fqs-open]").Click();
        cut.WaitForElement("[data-testid=fqs-detail]");

        cut.Find("[data-testid=fqs-environment]").Change("env-qa");
        api.Verify(a => a.ResolveAsync(Project, It.Is<ScopeQuery>(q => q.Environment == "env-qa"), It.IsAny<CancellationToken>()), Times.Once(), "coverage is re-read for QA");
        api.Verify(a => a.DetailAsync(Project, Privacy.Key, "env-qa", It.IsAny<CancellationToken>()), Times.Once(), "the open item's tests and schemas follow the environment");
        cut.WaitForAssertion(() => cut.Find("[data-testid=fqs-evidence-context]").GetAttribute("data-environment").Should().Be("env-qa"));
        nav.Uri.Should().Contain("fq-env=env-qa");
    }

    [Fact]
    public void An_aggregate_over_all_environments_is_labelled_as_not_evidence_from_one_environment()
    {
        var all = TwoEnvironments with { Environment = null, CrossEnvironment = true, Basis = "All environments, aggregated." };
        var (cut, _) = EvidenceJourney(evidence: all);
        cut.Find("[data-testid=fqs-evidence-context]").ClassList.Should().Contain("fqs-cross-env");
        cut.Find("[data-testid=fqs-cross-env-warning]").TextContent.Should().Contain("not evidence from any one environment");
    }

    [Fact]
    public void Several_environments_and_none_chosen_ask_for_a_choice_instead_of_picking_one()
    {
        var none = TwoEnvironments with { Environment = null };
        var (cut, _) = EvidenceJourney(new ScopeItemDetail(Privacy, "x", [], [], null) { TestsAssessed = false, Evidence = none }, none);
        cut.Find("[data-testid=fqs-environment]").QuerySelectorAll("option")[0].TextContent.Should().Be("Choose an environment");
        cut.Find($"[data-key='{Privacy.Key}'] [data-testid=fqs-open]").Click();
        cut.WaitForElement("[data-testid=fqs-tests-not-assessed]").TextContent.Should().Contain("choose an evidence environment");
    }

    [Fact]
    public void Linked_tests_show_their_trx_result_with_run_metadata_and_a_failure_is_traceable_to_its_run()
    {
        var lifecycle = new BirkNext.Web.Models.SddLifecycleState();
        var artifact = new BirkNext.Web.Models.SddTestResultArtifact { FileName = "skole-dev.trx" };
        var run = new BirkNext.Web.Models.SddTestRunEvidence { ArtifactId = artifact.ArtifactId, ProviderRunId = "run-42", EnvironmentReference = "env-dev" };
        lifecycle.TestResultArtifacts.Add(artifact);
        lifecycle.TestRuns.Add(run);
        lifecycle.TestExecutions.Add(new BirkNext.Web.Models.SddTestExecutionEvidence
        {
            TestId = "tests/Sends_report", TestName = "Sends_report", Result = "Failed", ExecutionState = "Completed", CorrelationState = "Confirmed", RunId = run.RunId,
            ArtifactId = artifact.ArtifactId, EnvironmentReference = "env-dev", ExecutedAt = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero), ErrorMessage = "Expected 202",
        });
        var detail = new ScopeItemDetail(Privacy, "Ikke vis kode 6", [], [], null)
        {
            TestsAssessed = true, Evidence = TwoEnvironments,
            Tests = [Link("Sends_report", ScopeTestLinkKind.Explicit), Link("Maybe_report", ScopeTestLinkKind.Candidate)],
        };
        var (cut, _) = EvidenceJourney(detail, lifecycle: lifecycle);
        cut.Find($"[data-key='{Privacy.Key}'] [data-testid=fqs-open]").Click();
        cut.WaitForElement("[data-testid=fqs-tests-explicit]");
        cut.Find("[data-testid=fqs-tests-explicit] [data-testid=fqs-test-run]").TextContent.Should().Contain("Failed");
        cut.Find("[data-testid=fqs-tests-candidates] [data-testid=fqs-test-run]").TextContent.Should().Contain("Not executed",
            "results exist, none of them is this candidate test in this environment");
        cut.Find("[data-testid=fqs-tests-execution-summary]").TextContent.Should().Contain("1 failed").And.Contain("1 not executed");

        cut.FindAll("[data-testid=fqs-test-open]")[0].Click();
        cut.Find("[data-testid=fqs-test-execution]").GetAttribute("data-state").Should().Be("Observed");
        cut.Find("[data-testid=fqs-test-execution]").TextContent.Should().Contain("Failed in run run-42");
        var row = cut.Find("[data-testid=fqs-test-runs] [data-testid=fqs-run-row]");
        row.GetAttribute("data-outcome").Should().Be("Failed");
        row.GetAttribute("data-run").Should().Be(run.RunId.ToString());
        row.TextContent.Should().Contain("run-42").And.Contain("skole-dev.trx");
        cut.Find("[data-testid=fqs-test-selection-rule]").TextContent.Should().Contain("newest run");
        cut.Find("[data-testid=fqs-test-evidence-kind]").TextContent.Should().Contain("discovery, not execution");
    }

    [Fact]
    public void Without_imported_results_execution_is_shown_as_unavailable_not_as_untested_or_passed()
    {
        var detail = new ScopeItemDetail(Privacy, "x", [], [], null) { TestsAssessed = true, Evidence = TwoEnvironments, Tests = [Link("Sends_report", ScopeTestLinkKind.Explicit)] };
        var (cut, _) = EvidenceJourney(detail, lifecycle: new BirkNext.Web.Models.SddLifecycleState());
        cut.Find($"[data-key='{Privacy.Key}'] [data-testid=fqs-open]").Click();
        cut.WaitForElement("[data-testid=fqs-tests-execution-summary]").TextContent.Should().Contain("execution evidence unavailable");
        cut.Find("[data-testid=fqs-test-run]").TextContent.Should().Contain("Execution evidence unavailable");
    }

    [Fact]
    public void Schemas_separate_linked_from_candidate_never_claim_validation_and_open_the_exact_stored_contract()
    {
        var snapshot = Guid.NewGuid();
        ScopeSchemaLink Schema(string name, string kind, ScopeSchemaLinkKind link, string path) => new($"{kind}:{name}", name, kind, kind == "XmlSchema" ? "https://bufdir.no/x" : null, "2.0.1",
            "m2lb-language", snapshot, path, link, link == ScopeSchemaLinkKind.Explicit ? "Named in the requirement" : "Same module (SkoleService)", link == ScopeSchemaLinkKind.Explicit ? "High" : "Low",
            link == ScopeSchemaLinkKind.Explicit ? "schema.named-in-requirement" : "schema.module-ownership", "why", ScopeSchemaValidation.NotPerformed, "No validation result is linked.", "env-dev");
        var detail = new ScopeItemDetail(Privacy, "x", [], [], null)
        {
            TestsAssessed = true, Evidence = TwoEnvironments, SchemasAssessed = true,
            Schemas = [Schema("bufdir.skolenaervaer.v2.0.1", "XmlSchema", ScopeSchemaLinkKind.Explicit, "xsd/bufdir.skolenaervaer.v2.0.1.xsd"),
                       Schema("M2LB Skole API", "OpenApi", ScopeSchemaLinkKind.Candidate, "SkoleService/autodoc/openapi.yaml")],
        };
        var (cut, _) = EvidenceJourney(detail);
        cut.Find($"[data-key='{Privacy.Key}'] [data-testid=fqs-open]").Click();
        var linked = cut.WaitForElement("[data-testid=fqs-schemas-linked] [data-testid=fqs-schema]");
        linked.QuerySelector("[data-testid=fqs-schema-kind]")!.TextContent.Should().EndWith("Explicitly linked");
        linked.QuerySelector("[data-testid=fqs-schema-validation]")!.TextContent.Should().Contain("Validation not performed");
        linked.QuerySelector("[data-testid=fqs-schema-source]")!.TextContent.Should().Contain("xsd/bufdir.skolenaervaer.v2.0.1.xsd").And.Contain("m2lb-language");
        linked.QuerySelector("[data-testid=fqs-schema-open]")!.GetAttribute("href").Should()
            .Be($"schema-studio?snapshot={snapshot}&contract={Uri.EscapeDataString("xsd/bufdir.skolenaervaer.v2.0.1.xsd")}&environment=env-dev");
        cut.Find("[data-testid=fqs-schemas-candidates] [data-testid=fqs-schema-kind]").TextContent.Should().Contain("Candidate association");
        cut.Find("[data-testid=fqs-schemas-candidates] [data-testid=fqs-schema-open]").GetAttribute("href").Should().StartWith($"source-analysis?snapshot={snapshot}");
    }

    [Fact]
    public void Without_contract_evidence_schemas_are_unavailable_not_none()
    {
        var (cut, _) = EvidenceJourney();
        cut.Find($"[data-key='{Privacy.Key}'] [data-testid=fqs-open]").Click();
        cut.WaitForElement("[data-testid=fqs-schemas-not-assessed]").TextContent.Should().Contain("not \"none\"");
    }

    [Fact]
    public void Returning_through_a_plain_link_restores_this_sessions_filter_environment_and_selection()
    {
        var store = new ExplorerViewStateStore();
        store.Set($"fqs|{Project}", new Dictionary<string, object?> { ["fq-quality"] = "Privacy", ["fq-item"] = Privacy.Key, [ScopeFilterState.EnvironmentKey] = "env-dev" });
        var (cut, api) = EvidenceJourney(store: store, startUrl: "delivery-reporting?tab=scope");
        cut.FindAll("[data-testid=fqs-item]").Should().ContainSingle().Which.GetAttribute("data-key").Should().Be(Privacy.Key);
        cut.WaitForAssertion(() => cut.Find("[data-testid=fqs-detail]").GetAttribute("data-key").Should().Be(Privacy.Key));
        api.Verify(a => a.ResolveAsync(Project, It.Is<ScopeQuery>(q => q.Environment == "env-dev"), It.IsAny<CancellationToken>()), Times.Once());
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("fq-quality=Privacy").And.Contain("fq-env=env-dev");
    }

    [Fact]
    public void The_quality_filter_offers_integration_after_functional()
    {
        var (cut, _) = EvidenceJourney();
        cut.Find("[data-testid=fqs-filter-category]").QuerySelectorAll("option").Skip(1).Take(2).Select(o => o.GetAttribute("value"))
            .Should().Equal([nameof(QualityCategory.Functional), nameof(QualityCategory.Integration)]);
    }
}
