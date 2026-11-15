using BirkNext.TestCoverage;
using BirkNext.TestPlanning;
using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using L = BirkNext.Web.Services.RequirementLinkPresentation;
using P = BirkNext.Web.Services.TestPlanSuggestionsPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Manual requirement linking in Test Plan Suggestions: a tester links a saved case to requirements of the project's own specification (search, add,
/// remove), sees automatic and manual origins apart and whether each requirement is still in the specification, and the save carries the links
/// with the project's requirement catalog. The shared Prepared test cases panel shows origin, validity, requirements without test design and never
/// an execution.
/// </summary>
public sealed class RequirementLinkingTests : BunitContext
{
    private const string Spec = "library/specs/001-loans/spec.md";
    private static readonly Guid DraftId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private SaveTestPlanDraftRequest? _saved;

    private static readonly ProjectRequirementOption[] Options =
    [
        new("FR-001", "System MUST register a loan.", Spec), new("FR-002", "System MUST email a receipt.", Spec), new("FR-003", "System MUST block a suspended member.", Spec),
    ];

    private static TestCaseDraft Case(params ManualRequirementLink[] links) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.ProjectDiscovery, Owner = "library", ScenarioId = "tsc-borrow", Fingerprint = "f" }, SuiteId = "suite:area:loans",
        Generated = new TestCaseContent
        {
            Title = "Borrow a book", Purpose = "Borrow", Steps = ["Choose a book"], StepExpected = ["The loan is registered"], ExpectedResult = "The loan is registered",
            ExpectedBasis = ExpectedResultBasis.Specification, Capability = "specification", Requirements = [new("FR-001", "FR-001", null)],
            Evidence = [new("requirement", $"{Spec}#FR-001")],
        },
        RequirementLinks = [.. links],
    };

    private static ManualRequirementLink Saved(string id, string path = Spec) =>
        new() { RequirementId = id, SpecificationPath = path, Title = $"{id} when linked", LinkedBy = "anna", LinkedAt = new DateTimeOffset(2026, 11, 14, 9, 0, 0, TimeSpan.Zero) };

    private static TestPlanDraft Draft(TestCaseDraft c) => new()
    {
        Id = DraftId, GuideKey = "project:library", ProjectKey = "library", ProjectName = "Library", PlanName = "Library – test plan", Revision = 4,
        Suites = [new("suite:area:loans", "Loans", "area", null)], Cases = [c],
    };

    private IRenderedComponent<TestPlanSuggestions> Render(TestPlanDraft draft, bool specification = true, bool canWrite = true)
    {
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        var api = new Mock<ITestPlanSuggestionsApiService>();
        api.Setup(x => x.ProjectAsync("library", It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync((new TestPlanWorkspace
        {
            ProjectKey = "library", GuideKey = "project:library", CanWrite = canWrite, RuleVersion = "tps-1",
            Drafts = [new(DraftId, "project:library", draft.PlanName, null, draft.Revision, 1, 0, 0, 0, 1, 1, "t", DateTimeOffset.UtcNow)],
        }, (string?)null));
        api.Setup(x => x.DraftAsync(DraftId, It.IsAny<CancellationToken>())).ReturnsAsync((draft, (string?)null));
        api.Setup(x => x.SaveAsync(DraftId, It.IsAny<SaveTestPlanDraftRequest>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, SaveTestPlanDraftRequest, CancellationToken>((_, r, _) => _saved = r)
            .ReturnsAsync((Guid _, SaveTestPlanDraftRequest r, CancellationToken _) => (draft with
            {
                Revision = draft.Revision + 1,
                Cases = [.. draft.Cases.Select(c => c with { RequirementLinks = [.. (r.Cases.Single().RequirementLinks ?? []).Select(l => l.LinkedAt == default ? l with { LinkedBy = "anna", LinkedAt = DateTimeOffset.UtcNow } : l)] })],
            }, (string?)null));
        Services.AddSingleton(api.Object);

        var artifact = new ExplorerArtifact("a1", WorkspaceArtifactType.Specification, "spec.md", "spec.md", Spec, ExplorerArtifactSource.Workspace, "ProjectImport", 1, 1, "Draft",
            ExplorerArtifactCurrentness.Current, null);
        var explorers = new Mock<IArtifactExplorerContext>();
        explorers.Setup(c => c.GetStateAsync(It.IsAny<WorkspaceArtifactType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkspaceArtifactType role, CancellationToken _) => role == WorkspaceArtifactType.Specification && specification
                ? new ArtifactExplorerState(role, ExplorerArtifactStatus.Loaded, "library", "Library", [artifact], artifact, ExplorerSelectionReason.Explicit, "# Spec\n")
                : new ArtifactExplorerState(role, ExplorerArtifactStatus.Empty, "library", "Library", []));
        Services.AddSingleton(explorers.Object);
        Services.AddSingleton<IWorkspaceSessionService>(new WorkspaceArtifactRepository());
        var context = new Mock<IReviewContextProvider>();
        context.Setup(x => x.GetCurrent()).Returns(new ReviewContext
        {
            Specification = new SpecificationSemanticModel { Requirements = [.. Options.Select(o => new SemanticRequirement { Id = o.RequirementId, Text = o.Title })] },
        });
        Services.AddSingleton(context.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.GetRequiredService<NavigationManager>().NavigateTo($"test-plan-suggestions?draft={DraftId}&area=prepare&case=tsc-borrow");
        var cut = Render<TestPlanSuggestions>();
        cut.WaitForAssertion(() => cut.Find("[data-testid=tps-links-tsc-borrow]"));
        return cut;
    }

    private static IReadOnlyList<AngleSharp.Dom.IElement> Links(IRenderedComponent<TestPlanSuggestions> cut) => cut.FindAll("[data-testid=tps-link]");

    [Fact]
    public void A_tester_searches_links_two_requirements_sees_them_unsaved_and_the_save_carries_the_links_and_the_projects_catalog()
    {
        var cut = Render(Draft(Case()));

        var automatic = Links(cut).Single();
        automatic.GetAttribute("data-requirement").Should().Be("FR-001");
        automatic.GetAttribute("data-origin").Should().Be("automatic");
        automatic.QuerySelector("[data-testid=tps-link-origin]")!.TextContent.Should().Be("Automatically identified");
        automatic.QuerySelector("[data-testid=tps-link-validity]")!.TextContent.Should().Be("In the current specification");
        automatic.QuerySelector("[data-testid=tps-link-remove]").Should().BeNull("the case's own evidence is never removed by hand");
        cut.FindAll("[data-testid=tps-unsaved]").Should().BeEmpty();

        cut.Find("[data-testid=tps-link-search-tsc-borrow]").Input("email");
        cut.FindAll("[data-testid=tps-link-add]").Select(b => b.GetAttribute("data-requirement")).Should().Equal("FR-002");
        cut.Find("[data-testid=tps-link-add]").Click();
        cut.Find("[data-testid=tps-link-search-tsc-borrow]").Input("block");
        cut.Find("[data-testid=tps-link-add]").Click();

        Links(cut).Select(l => (l.GetAttribute("data-requirement"), l.GetAttribute("data-origin"))).Should().Equal(("FR-001", "automatic"), ("FR-002", "manual"), ("FR-003", "manual"));
        cut.FindAll("[data-testid=tps-link-unsaved]").Should().HaveCount(2);
        cut.FindAll("[data-testid=tps-unsaved]").Should().NotBeEmpty("a link is an unsaved change until the draft is saved");

        cut.Find("[data-testid=tps-save]").Click();

        cut.WaitForAssertion(() => _saved.Should().NotBeNull());
        _saved!.Cases.Single().RequirementLinks!.Select(l => (l.RequirementId, l.SpecificationPath)).Should().Equal(("FR-002", Spec), ("FR-003", Spec));
        _saved.RequirementCatalog!.ProjectKey.Should().Be("library");
        _saved.RequirementCatalog.Requirements.Select(r => r.RequirementId).Should().Equal("FR-001", "FR-002", "FR-003");
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=tps-unsaved]").Should().BeEmpty());
        cut.FindAll("[data-testid=tps-link-unsaved]").Should().BeEmpty();
        cut.FindAll("[data-testid=tps-link-by]").Should().HaveCount(2);
    }

    [Fact]
    public void Removing_a_manual_link_keeps_the_automatic_one_and_a_link_to_a_removed_requirement_is_flagged()
    {
        var cut = Render(Draft(Case(Saved("FR-001"), Saved("FR-009"))));

        var rows = Links(cut);
        rows.Select(l => (l.GetAttribute("data-requirement"), l.GetAttribute("data-origin"))).Should().Equal(("FR-001", "both"), ("FR-009", "manual"));
        rows[0].QuerySelector("[data-testid=tps-link-origin]")!.TextContent.Should().Be("Automatically identified · Manually confirmed");
        rows[0].QuerySelector("[data-testid=tps-link-by]")!.TextContent.Should().Contain("anna").And.Contain("2026-11-14");
        rows[1].QuerySelector("[data-testid=tps-link-validity]")!.TextContent.Should().StartWith("Not found in the current specification");

        rows[0].QuerySelector("[data-testid=tps-link-remove]")!.Click();

        Links(cut).Select(l => (l.GetAttribute("data-requirement"), l.GetAttribute("data-origin"))).Should().Equal(("FR-001", "automatic"), ("FR-009", "manual"));
        cut.FindAll("[data-testid=tps-unsaved]").Should().NotBeEmpty();
        cut.Find("[data-testid=tps-save]").Click();
        cut.WaitForAssertion(() => _saved!.Cases.Single().RequirementLinks!.Select(l => l.RequirementId).Should().Equal("FR-009"));
    }

    [Fact]
    public void Without_a_selected_specification_the_section_explains_there_is_nothing_to_link_and_offers_no_free_text()
    {
        var cut = Render(Draft(Case()), specification: false);

        cut.Find("[data-testid=tps-links-unavailable]").TextContent.Should().Contain("no selected specification");
        cut.FindAll("[data-testid=tps-link-search-tsc-borrow]").Should().BeEmpty();
        Links(cut).Single().QuerySelector("[data-testid=tps-link-validity]")!.TextContent.Should().StartWith("Not checked");
    }

    [Fact]
    public void A_read_only_user_sees_the_links_but_cannot_change_them()
    {
        var cut = Render(Draft(Case(Saved("FR-002"))), canWrite: false);

        Links(cut).Should().HaveCount(2);
        cut.FindAll("[data-testid=tps-link-remove]").Should().BeEmpty();
        cut.FindAll("[data-testid=tps-link-search-tsc-borrow]").Should().BeEmpty();
    }

    [Fact]
    public void The_link_controls_are_labelled_buttons_and_a_search_field_reachable_by_keyboard()
    {
        var cut = Render(Draft(Case(Saved("FR-002"))));

        var search = cut.Find("[data-testid=tps-link-search-tsc-borrow]");
        search.GetAttribute("type").Should().Be("search");
        search.Closest("label")!.TextContent.Should().Contain("Find a requirement to link");
        cut.FindAll("[data-testid=tps-link-add]").Should().OnlyContain(b => b.TagName == "BUTTON" && b.GetAttribute("type") == "button" && b.GetAttribute("aria-label")!.StartsWith("Link FR-"));
        cut.Find("[data-testid=tps-link-remove]").GetAttribute("aria-label").Should().Be("Remove the manual link to FR-002");
        cut.Find("[data-testid=tps-links-tsc-borrow] h4").TextContent.Should().Be("Linked requirements");
    }

    // ── Presentation rules (pure) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Options_come_only_from_the_selected_specification_and_an_empty_list_says_why()
    {
        var evidence = new CoverageWorkspaceEvidence
        {
            SpecificationAvailable = true, SpecificationArtifacts = [new("a1", Spec, null, "fp")], Requirements = [new("FR-001", "Register", null), new("FR-001", "dup", null), new(" ", "blank", null)],
        };
        L.Options(evidence).Should().Equal(new ProjectRequirementOption("FR-001", "Register", Spec));
        L.EmptyReason(evidence, L.Options(evidence)).Should().BeNull();
        L.Options(evidence with { SpecificationArtifacts = [] }).Should().BeNull("a requirement is identified by id and specification file: without the file nothing can be checked");
        L.Options(null).Should().BeNull();
        L.Options(evidence with { Requirements = [] }).Should().BeEmpty("a selected specification without requirements: links to it are not found");
        L.EmptyReason(evidence with { SpecificationAvailable = false }, null).Should().Contain("no selected specification");
        L.EmptyReason(evidence with { Requirements = [] }, []).Should().Contain("names no requirements");
        L.EmptyReason(null, null).Should().Contain("could not be read");
    }

    [Fact]
    public void Search_matches_id_or_text_skips_linked_requirements_and_link_and_unlink_touch_manual_links_only()
    {
        var c = Case();
        L.Search(Options, c, "fr-00").Should().HaveCount(3);
        L.Search(Options, c, "RECEIPT").Single().RequirementId.Should().Be("FR-002");
        L.Search(Options, c, "nothing like it").Should().BeEmpty();

        var linked = L.Link(L.Link(c, Options[1]), Options[1]);
        linked.RequirementLinks.Should().ContainSingle("linking twice is one link");
        L.Search(Options, linked, "").Select(o => o.RequirementId).Should().Equal("FR-001", "FR-003");
        linked.Generated.Should().Be(c.Generated, "the case's evidence is never rewritten");

        var both = L.Link(linked, Options[0]);
        L.Rows(both, Options).Select(r => (r.RequirementId, r.Automatic, r.Manual)).Should().Equal(("FR-001", true, true), ("FR-002", false, true));
        var unlinked = L.Unlink(both, "fr-001", "/" + Spec);
        unlinked.RequirementLinks.Select(l => l.RequirementId).Should().Equal("FR-002");
        L.Rows(unlinked, Options).First().Should().Match<L.Row>(r => r.RequirementId == "FR-001" && r.Automatic && !r.Manual);
    }

    [Fact]
    public void A_guide_or_hand_written_case_has_no_automatic_requirement_and_the_save_request_keeps_links_per_case()
    {
        var guide = Case() with { Source = new ScenarioReference { Kind = ScenarioSourceKind.SystemGuide, Owner = "g", ScenarioId = "s" } };
        L.Rows(guide, Options).Should().BeEmpty("a guide's rule ids are business rules, not specification requirements (MT-7)");
        var manual = new TestCaseDraft { Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = "library", ScenarioId = "manual-1" }, Generated = new() { Title = "Log in" } };
        L.Rows(manual, Options).Should().BeEmpty();

        var request = P.ToSaveRequest(Draft(Case(Saved("FR-002"))), requirements: Options);
        request.Cases.Single().RequirementLinks.Should().ContainSingle().Which.LinkedBy.Should().Be("anna");
        request.RequirementCatalog!.Requirements.Should().HaveCount(3);
        P.ToSaveRequest(Draft(Case()) with { ProjectKey = null }).Should().Match<SaveTestPlanDraftRequest>(r => r.RequirementCatalog == null && r.Cases.Single().RequirementLinks == null,
            "a guide plan sends no requirement links");
    }

    // ── The shared panel (Test Coverage and Requirements Traceability) ────────────────────────────────────────────

    [Fact]
    public void The_panel_shows_origin_validity_titles_requirements_without_test_design_and_never_an_execution()
    {
        var key = $"{DraftId:N}/manual-1";
        var view = new PreparedCaseTraceView
        {
            ProjectKey = "library", SpecificationPaths = [Spec], Plans = 1, Cases = 1, LinkedCases = 1, RequirementsWithPreparedCaseOnly = 1, SpecificationRequirements = 3, StaleManualLinks = 1,
            WithoutTestDesign = [new("FR-001", "System MUST register a loan."), new("FR-003", null)],
            CaseList =
            [
                new()
                {
                    CaseKey = key, DraftId = DraftId, CaseId = "manual-1", Title = "Log in", Stage = PreparedCaseStage.Prepared, StageLabel = "Prepared", SourceKind = ScenarioSourceKind.Manual,
                    Href = "test-plan-suggestions?case=manual-1",
                    Requirements =
                    [
                        new("FR-002", Spec, PreparedCaseLinkBasis.ManualConfirmation, true, "Linked by anna to FR-002 (manually confirmed).", "System MUST email a receipt.", PreparedLinkValidity.Current, "anna"),
                        new("FR-009", Spec, PreparedCaseLinkBasis.ManualConfirmation, false, "Linked by anna, but FR-009 is no longer in it.", "Old", PreparedLinkValidity.RequirementNotFound, "anna"),
                    ],
                },
            ],
            Requirements = [new() { RequirementId = "FR-002", Title = "System MUST email a receipt.", PreparedCases = 1, ManualCases = 1, State = "Prepared, not approved yet", CaseKeys = [key] }],
        };

        var cut = Render<PreparedCaseTracePanel>(p => p.Add(x => x.View, view));

        cut.Find("[data-testid=pct-req-total]").TextContent.Should().Be("3");
        cut.Find("[data-testid=pct-req-none]").TextContent.Should().Be("2");
        cut.Find("[data-testid=pct-executed]").TextContent.Should().StartWith("0");
        cut.Find("[data-testid=pct-explain]").TextContent.Should().Contain("1 of 3 requirements").And.Contain("None of these cases has been executed in a manual run yet");
        var row = cut.Find("[data-testid=pct-requirement]");
        row.QuerySelector("[data-testid=pct-requirement-title]")!.TextContent.Should().Be("System MUST email a receipt.");
        row.QuerySelector("[data-testid=pct-requirement-origin]")!.TextContent.Should().Be("Manually confirmed (1)");
        row.QuerySelector("[data-testid=pct-requirement-execution]")!.TextContent.Should().Be("Not executed");
        cut.FindAll("[data-testid=pct-gap]").Select(g => g.GetAttribute("data-requirement")).Should().Equal("FR-001", "FR-003");
        var links = cut.FindAll("[data-testid=pct-case-link]");
        links.Select(l => (l.GetAttribute("data-origin"), l.GetAttribute("data-counted"))).Should().Equal(("manual", "true"), ("manual", "false"));
        links[1].QuerySelector("[data-testid=pct-case-link-validity]")!.TextContent.Should().Be("No longer in the reviewed specification");
        cut.Find("[data-testid=pct-passed]").TextContent.Should().Be("0", "no manual run passed: designing or linking a case never counts as a pass");
        cut.Markup.Should().NotContain("Covered\"");
    }

    [Fact]
    public void An_older_result_without_link_origins_still_renders_as_automatic_evidence()
    {
        var key = $"{DraftId:N}/tsc-borrow";
        var view = new PreparedCaseTraceView
        {
            ProjectKey = "library", Cases = 1, LinkedCases = 1, RequirementsWithApprovedCase = 1,
            CaseList = [new() { CaseKey = key, Title = "Borrow", Stage = PreparedCaseStage.Approved, StageLabel = "Approved", Href = "x",
                Requirements = [new("FR-001", Spec, PreparedCaseLinkBasis.SpecificationEvidence, true, "Prepared from the spec.")] }],
            Requirements = [new() { RequirementId = "FR-001", ApprovedCases = 1, State = "Approved test case", CaseKeys = [key] }],
        };

        var cut = Render<PreparedCaseTracePanel>(p => p.Add(x => x.View, view));

        cut.Find("[data-testid=pct-requirement-origin]").TextContent.Should().Be("Automatically identified");
        cut.FindAll("[data-testid=pct-case-link-validity]").Should().BeEmpty("an older result has no validity to show");
        cut.FindAll("[data-testid=pct-req-none]").Should().BeEmpty("without the specification's requirement count no gap is claimed");
    }
}
