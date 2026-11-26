using BirkNext.TestPlanning;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using M = BirkNext.Web.Services.ManualTestingPresentation;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// «3. Testplaner» (2026-11-25): the last Manual Testing tab owns the project's test plans — «+ Ny testplan» (a clean draft every time, named
/// «Testplan yyyy-MM-dd HH-mm-ss» in local time, its own request id so a double click or retry never creates two), the saved test plans and
/// «Overfør testplan» (file by default). The other tabs keep their focus; the plan page links here.
/// </summary>
public sealed class ManualTestingTestplanerTabTests : BunitContext
{
    private static readonly Guid PlanId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid NewId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private readonly Mock<ITestPlanSuggestionsApiService> _plans = new();
    private readonly List<CreateProjectTestPlanRequest> _requests = [];

    private static TestPlanDraftSummary Summary() =>
        new(PlanId, "project:library", "Library – test plan", null, 3, 2, 1, 0, 0, 0, 0, "anna", DateTimeOffset.UtcNow);

    private void Setup(bool canWrite = true, Func<CreateProjectTestPlanRequest, Task<(TestPlanDraft?, string?)>>? create = null)
    {
        Services.AddSingleton(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Projection(BirkNext.Web.Tests.Services.WorkspaceSnapshots.Loaded("Imported", "library", "Library")).Object);
        _plans.Setup(x => x.ProjectAsync("library", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanWorkspace { ProjectKey = "library", GuideKey = "project:library", CanWrite = canWrite, Drafts = [Summary()] }, (string?)null));
        _plans.Setup(x => x.LastDiscoveryAsync("library", It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(((ScenarioDiscoveryResult?)null, (string?)null));
        _plans.Setup(x => x.CreateProjectAsync(It.IsAny<CreateProjectTestPlanRequest>(), It.IsAny<CancellationToken>()))
            .Returns((CreateProjectTestPlanRequest r, CancellationToken _) =>
            {
                _requests.Add(r);
                return create?.Invoke(r) ?? Task.FromResult<(TestPlanDraft?, string?)>((new TestPlanDraft { Id = NewId, PlanName = r.PlanName ?? "", ProjectKey = "library" }, null));
            });
        _plans.Setup(x => x.DraftAsync(PlanId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new TestPlanDraft { Id = PlanId, PlanName = "Library – test plan", ProjectKey = "library" }, (string?)null));
        Services.AddSingleton(_plans.Object);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<ManualTesting> Open(string url = "manual-testing/plans")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<ManualTesting>();
        cut.WaitForAssertion(() => cut.Find("[aria-busy=false]"));
        return cut;
    }

    // ── Presentation ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Testplaner_is_the_third_and_last_tab()
    {
        M.Stages.Select(s => s.Id).Should().Equal("discover", "plan", "plans");
        M.Stages[^1].Title.Should().Be("Testplaner");
        M.Stages[^1].Route.Should().Be("manual-testing/plans");
        M.TransferRoute(PlanId).Should().Be($"manual-testing/plans?transfer={PlanId}");
    }

    [Fact]
    public void The_suggested_name_is_local_date_and_time_with_seconds()
    {
        M.SuggestedPlanName(new DateTime(2026, 11, 25, 9, 5, 7, DateTimeKind.Local)).Should().Be("Testplan 2026-11-25 09-05-07");
        M.SuggestedPlanName(new DateTime(2026, 11, 25, 23, 59, 59)).Should().Be("Testplan 2026-11-25 23-59-59");
        M.SuggestedPlanName(new DateTime(2026, 11, 25, 9, 5, 7)).Should().MatchRegex(@"^Testplan \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}$", "no colons: the name also works as a file name");
    }

    [Fact]
    public void An_old_export_link_opens_the_transfer_on_the_Testplaner_tab()
    {
        M.CanonicalRoute(null, PlanId.ToString(), "export", null, null).Should().Be(M.TransferRoute(PlanId));
        M.CanonicalRoute(null, null, "export", null, null).Should().Be(M.PlansRoute);
    }

    // ── Page ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_tab_shows_a_visible_primary_new_test_plan_button_and_the_saved_test_plans()
    {
        Setup();
        var cut = Open();

        cut.Find("[data-testid=twh-stage][data-stage=plans]").GetAttribute("aria-current").Should().Be("page");
        var button = cut.Find("[data-testid=mt-new-plan]");
        button.TextContent.Should().Be("+ Ny testplan");
        button.ClassList.Should().Contain("btn-primary");
        button.HasAttribute("disabled").Should().BeFalse();
        cut.Find("#mt-plans-title").TextContent.Should().Be("Lagrede testplaner");
        cut.Find("[data-testid=mt-plan-export]").GetAttribute("href").Should().Be(M.TransferRoute(PlanId));
        cut.Markup.Should().NotContain("Lagrede planer").And.NotContain("Ny plan<");
    }

    [Fact]
    public void Without_write_access_the_button_is_disabled_and_says_why()
    {
        Setup(canWrite: false);
        var cut = Open();

        cut.Find("[data-testid=mt-new-plan]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=mt-new-plan-why]").TextContent.Should().Contain("ikke tilgang til å opprette testplaner");
    }

    [Fact]
    public void A_new_test_plan_starts_clean_and_is_created_empty_with_its_own_request_id()
    {
        Setup();
        var cut = Open();

        cut.Find("[data-testid=mt-new-plan]").Click();
        var name = cut.Find("[data-testid=mt-new-plan-name]").GetAttribute("value");
        name.Should().MatchRegex(@"^Testplan \d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}$");
        cut.Find("[data-testid=mt-new-plan-create]").Click();

        cut.WaitForAssertion(() => cut.Find("[data-testid=mt-new-plan-done]").TextContent.Should().Contain("Testplan opprettet"));
        _requests.Should().ContainSingle();
        var request = _requests[0];
        request.AllowEmpty.Should().BeTrue("a new test plan starts without cases");
        request.CandidateIds.Should().BeEmpty("nothing of another plan is carried over");
        request.DraftId.Should().BeNull("a new test plan never adds to an existing one");
        request.CreationRequestId.Should().NotBeNullOrWhiteSpace();
        request.PlanName.Should().Be(name);
        cut.Find("[data-testid=mt-new-plan-open]").GetAttribute("href").Should().Be($"{M.PlanRoute}?draft={NewId}");
    }

    [Fact]
    public void Each_start_is_a_new_draft_with_a_new_request_id()
    {
        Setup();
        var cut = Open();

        cut.Find("[data-testid=mt-new-plan]").Click();
        cut.Find("[data-testid=mt-new-plan-create]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mt-new-plan-done]"));
        cut.Find("[data-testid=mt-new-plan]").Click();
        cut.Find("[data-testid=mt-new-plan-create]").Click();
        cut.WaitForAssertion(() => _requests.Should().HaveCount(2));

        _requests[0].CreationRequestId.Should().NotBe(_requests[1].CreationRequestId, "a second test plan is a second request");
    }

    [Fact]
    public void Cancel_creates_nothing()
    {
        Setup();
        var cut = Open();

        cut.Find("[data-testid=mt-new-plan]").Click();
        cut.Find("[data-testid=mt-new-plan-cancel]").Click();

        cut.FindAll("[data-testid=mt-new-plan-form]").Should().BeEmpty();
        _requests.Should().BeEmpty();
    }

    [Fact]
    public void A_failed_save_keeps_the_draft_and_a_retry_reuses_its_request_id()
    {
        var fail = true;
        Setup(create: r => Task.FromResult<(TestPlanDraft?, string?)>(fail ? (null, "Serveren svarte ikke.") : (new TestPlanDraft { Id = NewId, PlanName = r.PlanName ?? "" }, null)));
        var cut = Open();

        cut.Find("[data-testid=mt-new-plan]").Click();
        cut.Find("[data-testid=mt-new-plan-name]").Input("Regresjon uke 48");
        cut.Find("[data-testid=mt-new-plan-create]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mt-new-plan-error]"));
        cut.Find("[data-testid=mt-new-plan-name]").GetAttribute("value").Should().Be("Regresjon uke 48", "the tester's name is kept");

        fail = false;
        cut.Find("[data-testid=mt-new-plan-create]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mt-new-plan-done]"));
        _requests.Should().HaveCount(2);
        _requests[1].CreationRequestId.Should().Be(_requests[0].CreationRequestId, "a retry of the same draft is deduplicated on the server");
    }

    [Fact]
    public void A_double_click_sends_one_request()
    {
        var gate = new TaskCompletionSource<(TestPlanDraft?, string?)>();
        Setup(create: _ => gate.Task);
        var cut = Open();

        cut.Find("[data-testid=mt-new-plan]").Click();
        var create = cut.Find("[data-testid=mt-new-plan-create]");
        create.Click();
        cut.Find("[data-testid=mt-new-plan-create]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=mt-new-plan-create]").TextContent.Should().Be("Oppretter …");
        gate.SetResult((new TestPlanDraft { Id = NewId, PlanName = "x" }, null));
        cut.WaitForAssertion(() => cut.Find("[data-testid=mt-new-plan-done]"));

        _requests.Should().ContainSingle();
    }

    [Fact]
    public void Create_query_opens_the_form()
    {
        Setup();
        var cut = Open("manual-testing/plans?create=1");
        cut.Find("[data-testid=mt-new-plan-form]").Should().NotBeNull();
    }

    [Fact]
    public void Transfer_opens_for_the_plan_on_file_export()
    {
        Setup();
        var cut = Open($"manual-testing/plans?transfer={PlanId}");

        cut.WaitForAssertion(() => cut.Find("[data-testid=mt-transfer-panel]"));
        cut.Find("[data-testid=atp-panel]").GetAttribute("data-method").Should().Be("file", "file export is the default, every time");
        cut.Find("[data-testid=atp-method-file]").GetAttribute("aria-selected").Should().Be("true");
    }

    // ── Ny testplan: Fra mal / Helt tom / Fra valgte scenarioer (2026-11-26) ─────────────────────────────────────────

    [Fact]
    public void Fra_mal_is_the_default_shows_the_template_structure_and_sends_the_template_id()
    {
        Setup();
        var cut = Open();

        cut.Find("[data-testid=mt-new-plan]").Click();
        cut.Find("[data-testid=mt-new-plan-from-template]").HasAttribute("checked").Should().BeTrue("«Fra mal» is the default");
        cut.FindAll("[data-testid=mt-new-plan-template-suite]").Select(s => s.TextContent).Should().Equal("Ende til ende", "Integrasjon", "Komponent", "Uklassifisert");
        cut.Find("[data-testid=mt-new-plan-template-preview]").TextContent.Should().Contain("0 testtilfeller");
        cut.Find("[data-testid=mt-new-plan-template]").Change(TestPlanTemplateRules.IntegrationId);
        cut.FindAll("[data-testid=mt-new-plan-template-suite]").Select(s => (s.TextContent, s.GetAttribute("data-depth"))).Should().Equal(
            ("Integrasjoner", "0"), ("Tjenesteintegrasjoner", "1"), ("Meldingsflyt", "1"));
        cut.Find("[data-testid=mt-new-plan-create]").Click();

        cut.WaitForAssertion(() => _requests.Should().ContainSingle());
        (_requests[0].TemplateId, _requests[0].AllowEmpty, _requests[0].CandidateIds.Count).Should().Be((TestPlanTemplateRules.IntegrationId, true, 0));
    }

    [Fact]
    public void Helt_tom_testplan_sends_no_template_and_no_scenarios()
    {
        Setup();
        var cut = Open();

        cut.Find("[data-testid=mt-new-plan]").Click();
        cut.Find("[data-testid=mt-new-plan-empty]").Change(true);
        cut.FindAll("[data-testid=mt-new-plan-template]").Should().BeEmpty();
        cut.Find("[data-testid=mt-new-plan-create]").Click();

        cut.WaitForAssertion(() => _requests.Should().ContainSingle());
        (_requests[0].TemplateId, _requests[0].AllowEmpty, _requests[0].CandidateIds.Count).Should().Be(((string?)null, true, 0));
    }

    [Fact]
    public void Custom_templates_from_the_installation_are_offered_and_archived_ones_are_not()
    {
        _plans.Setup(x => x.TemplatesAsync(false, It.IsAny<CancellationToken>())).ReturnsAsync(([.. TestPlanTemplateRules.BuiltIn,
            new TestPlanTemplate { Id = "custom:1", Name = "Regresjon", Suites = [new("a", "Smoke")] },
            new TestPlanTemplate { Id = "custom:2", Name = "Gammel", Archived = true, Suites = [new("a", "X")] }], (string?)null));
        Setup();
        var cut = Open();

        cut.Find("[data-testid=mt-new-plan]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mt-new-plan-template]").TextContent.Should().Contain("Regresjon (egen mal)"));
        cut.Find("[data-testid=mt-new-plan-template]").TextContent.Should().NotContain("Gammel");
    }

    // ── «Kopier testplan» (2026-11-26) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Copying_a_plan_previews_what_is_copied_says_results_reset_and_creates_only_on_confirmation()
    {
        DuplicatePlanRequest? sent = null;
        _plans.Setup(x => x.DuplicatePreviewAsync(PlanId, It.IsAny<CancellationToken>())).ReturnsAsync((new DuplicatePlanPreview(PlanId, "Library – test plan", 4, 3, 2, 1, 1), (string?)null));
        _plans.Setup(x => x.DuplicateAsync(PlanId, It.IsAny<DuplicatePlanRequest>(), It.IsAny<CancellationToken>()))
            .Callback((Guid _, DuplicatePlanRequest r, CancellationToken _) => sent = r)
            .ReturnsAsync((new TestPlanDraft { Id = NewId, PlanName = "Sprint 13" }, (string?)null));
        Setup();
        var cut = Open();

        cut.Find("[data-testid=mt-plan-copy]").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid=mt-plan-copy-preview]").TextContent.Should().Contain("4 testgrupper · 3 scenarioer (1 uten testtilfelle) · 2 testtilfeller"));
        cut.Find("[data-testid=mt-plan-copy-reset]").TextContent.Should().Contain("Testresultater kopieres ikke. Alle scenarioer starter som Ikke kjørt.").And.Contain("må godkjennes på nytt");
        _plans.Verify(x => x.DuplicateAsync(It.IsAny<Guid>(), It.IsAny<DuplicatePlanRequest>(), It.IsAny<CancellationToken>()), Times.Never, "nothing is copied before confirmation");

        cut.Find("[data-testid=mt-plan-copy-name]").Input("Sprint 13");
        cut.Find("[data-testid=mt-plan-copy-sprint]").Input("Sprint 13");
        cut.Find("[data-testid=mt-plan-copy-confirm]").Click();
        cut.WaitForAssertion(() => sent.Should().NotBeNull());
        (sent!.PlanName, sent.SprintLabel).Should().Be(("Sprint 13", "Sprint 13"));
        sent.CreationRequestId.Should().NotBeNullOrWhiteSpace("a retry reuses the request id");
    }
}
