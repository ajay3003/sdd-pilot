using BirkNext.CriticalE2E;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// "Select from browser" in the flow editor: the tester clicks the element in the application and the editor stores the
/// strongest selector that identified it uniquely — or nothing, with the reason. It never guesses and never stores HTML.
/// </summary>
public sealed class CriticalE2EFlowEditorPickTests : BunitContext
{
    private sealed class PickApi(Func<CriticalE2EElementPickRequest, CriticalE2EElementPickResult> answer) : ICriticalE2EApiService
    {
        public List<CriticalE2EElementPickRequest> Requests { get; } = [];
        public Task<CriticalE2EOverview> OverviewAsync(CriticalE2EOverviewRequest request, CancellationToken ct = default) => Task.FromResult(new CriticalE2EOverview());
        public Task<List<CriticalE2EFlowDefinition>> FlowsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new List<CriticalE2EFlowDefinition>());
        public Task<CriticalE2EFlowDefinition> SaveFlowAsync(CriticalE2EFlowDefinition flow, CancellationToken ct = default) => Task.FromResult(flow);
        public Task DeleteFlowAsync(string flowId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken ct = default) => Task.FromResult(new CriticalE2ERunBatchResult());
        public Task<CriticalE2EElementPickResult> PickElementAsync(CriticalE2EElementPickRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static readonly CriticalE2EEngineStatus Ready = new() { State = CriticalE2EEngineState.Ready, Message = "Select an element in the paired browser." };

    private static CriticalE2EElementPickResult Picked(CompanionSelector? recommended, bool visible = true, bool enabled = true) => new()
    {
        Status = CriticalE2EStatus.Passed, Outcome = CriticalE2EPickOutcome.Picked,
        Element = new CompanionElementDescriptor
        {
            PageOrigin = "https://m2lbdev.bufetat.no", PageRoute = "/plassering", TagName = "button", Role = "button",
            AccessibleName = "Ny plassering", Visible = visible, Enabled = enabled, Recommended = recommended,
            Candidates =
            [
                new() { Selector = new CompanionSelector { Kind = CompanionSelectorKind.Role, Role = "button", Name = "Ny plassering" }, MatchCount = 1, Unique = recommended is not null },
                new() { Selector = new CompanionSelector { Kind = CompanionSelectorKind.Text, Value = "Ny plassering" }, MatchCount = 2, Unique = false },
            ],
        },
    };

    private static CriticalE2EFlowDefinition Flow() => new()
    {
        Id = "m02", Module = "M02", Name = "Foreta plassering", Mode = CriticalE2EExecutionMode.CompanionBrowser, ProfileId = "dev",
        Steps =
        [
            new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.Navigate, Value = "/plassering" },
            new CriticalE2EStepDefinition { StepId = "s2", BrowserAction = CompanionActionKind.Click, Selector = new CompanionSelector(), IsFinalAssertion = true },
        ],
    };

    private IRenderedComponent<CriticalE2EFlowEditor> Editor(PickApi api, CriticalE2EEngineStatus? pick, CriticalE2EFlowDefinition? flow = null, Action? onCheck = null)
    {
        Services.AddSingleton<ICriticalE2EApiService>(api);
        return Render<CriticalE2EFlowEditor>(p => p
            .Add(e => e.Flow, flow ?? Flow()).Add(e => e.ProfileId, "dev").Add(e => e.EnvironmentId, "dev")
            .Add(e => e.EnvironmentType, "Development").Add(e => e.ElementPick, pick)
            .Add(e => e.OnCheckElementPick, () => onCheck?.Invoke()));
    }

    [Fact]
    public void APickStoresTheRecommendedSelectorAndSaysWhatWasPicked()
    {
        var api = new PickApi(_ => Picked(new CompanionSelector { Kind = CompanionSelectorKind.Role, Role = "button", Name = "Ny plassering" }));
        var cut = Editor(api, Ready);

        cut.Find("[data-testid=e2e-step-pick-1]").Click();

        api.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(new CriticalE2EElementPickRequest
        {
            ProfileId = "dev", EnvironmentId = "dev", EnvironmentType = "Development", TimeoutMs = 45_000,
        });
        cut.Find("[data-testid=e2e-step-selector-kind-1]").GetAttribute("value").Should().Be("Role");
        cut.Find("[data-testid=e2e-step-selector-role-1]").GetAttribute("value").Should().Be("button");
        cut.Find("[data-testid=e2e-step-selector-name-1]").GetAttribute("value").Should().Be("Ny plassering");
        // Human-readable identity first; the strategy, uniqueness and where it was captured beside it.
        cut.Find("[data-testid=e2e-step-identity-1]").TextContent.Should().Be("Button \u2014 Ny plassering");
        cut.Find("[data-testid=e2e-step-strategy-1]").TextContent.Should().Contain("Selector: Role + name").And.Contain("Unique: Yes").And.Contain("Captured on /plassering");
        cut.Find("[data-testid=e2e-step-pick-note-1]").TextContent.Trim().Should().Be("Selected Button \u2014 Ny plassering.");
        cut.Find("[data-testid=e2e-step-pick-1]").TextContent.Trim().Should().Be("Change selection");
        cut.FindAll("[data-testid=e2e-editor-problem]").Should().BeEmpty("the picked step is complete");
    }

    [Fact]
    public void WithoutAUniqueSelectorNothingIsStored()
    {
        var cut = Editor(new PickApi(_ => Picked(recommended: null)), Ready);

        cut.Find("[data-testid=e2e-step-pick-1]").Click();

        cut.Find("[data-testid=e2e-step-selector-1]").GetAttribute("value").Should().BeEmpty();
        // The only candidate that matched several elements matched 2: that is an ambiguity, said as such.
        cut.Find("[data-testid=e2e-step-pick-note-1]").TextContent.Should()
            .Be("Cannot use this selector. 2 matching elements were found. Choose a more specific element or selector. No action was performed.");
        cut.Find("[data-testid=e2e-step-pick-note-1]").GetAttribute("role").Should().Be("alert", "the refusal is announced");
        cut.Find("[data-testid=e2e-editor-problem]").TextContent.Should().Be("A browser step has no element to act on.");
    }

    [Fact]
    public void AHiddenOrDisabledPickIsStoredButFlagged()
    {
        var cut = Editor(new PickApi(_ => Picked(new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "lagre" }, enabled: false)), Ready);

        cut.Find("[data-testid=e2e-step-pick-1]").Click();

        cut.Find("[data-testid=e2e-step-pick-note-1]").TextContent.Trim().Should().EndWith("It is currently disabled.");
    }

    [Theory]
    [InlineData(CriticalE2EPickOutcome.Blocked, "The Browser Companion is not connected.", "The Browser Companion is not connected.", true)]
    [InlineData(CriticalE2EPickOutcome.Cancelled, "Selection cancelled.", "Selection cancelled. Nothing was changed.", false)]
    [InlineData(CriticalE2EPickOutcome.TimedOut, "No element was selected before the picker timed out.", "No element was selected in time. Nothing was changed.", false)]
    [InlineData(CriticalE2EPickOutcome.Unsupported, "The paired Browser Companion does not support element picking. Reload the extension to update it.", "does not support element picking", true)]
    [InlineData(CriticalE2EPickOutcome.Failed, "boom", "Element picking failed: boom", false)]
    public void AnythingButAPickLeavesTheStepAloneAndSaysWhy(CriticalE2EPickOutcome outcome, string message, string shown, bool setupLink)
    {
        var cut = Editor(new PickApi(_ => new CriticalE2EElementPickResult
        {
            Status = outcome == CriticalE2EPickOutcome.Cancelled ? CriticalE2EStatus.Cancelled : CriticalE2EStatus.Blocked, Outcome = outcome, Message = message,
        }), Ready);

        cut.Find("[data-testid=e2e-step-pick-1]").Click();

        var note = cut.Find("[data-testid=e2e-step-pick-note-1]");
        note.TextContent.Should().Contain(shown);
        note.QuerySelectorAll("a").Any(a => a.TextContent == "Open Browser Companion setup").Should().Be(setupLink);
        cut.Find("[data-testid=e2e-step-selector-1]").GetAttribute("value").Should().BeEmpty();
        cut.Find("[data-testid=e2e-step-identity-1]").TextContent.Should().Be("No element selected");
    }

    [Fact]
    public void WhenPickingIsUnavailableTheButtonSaysWhyAndCanRecheck()
    {
        var checkedAgain = 0;
        var api = new PickApi(_ => throw new InvalidOperationException("must not be called"));
        var cut = Editor(api, new CriticalE2EEngineStatus { State = CriticalE2EEngineState.RequiresBrowserSession, Message = "No approved application page is open in the paired browser." },
            onCheck: () => checkedAgain++);

        var button = cut.Find("[data-testid=e2e-step-pick-1]");
        button.HasAttribute("disabled").Should().BeTrue();
        button.GetAttribute("aria-describedby").Should().Be("e2e-pick-unavailable");
        cut.Find("#e2e-pick-unavailable").TextContent.Should().Contain("No approved application page is open in the paired browser.");

        cut.Find("[data-testid=e2e-pick-recheck]").Click();
        checkedAgain.Should().Be(1);
        api.Requests.Should().BeEmpty();
    }

    [Fact]
    public void OnlyStepsThatTargetAnElementOfferPicking_AndPickIsNeverAStepAction()
    {
        var cut = Editor(new PickApi(_ => new CriticalE2EElementPickResult()), Ready);

        cut.FindAll("[data-testid=e2e-step-pick-0]").Should().BeEmpty("Navigate by route needs no element");
        cut.FindAll("[data-testid=e2e-step-pick-1]").Should().ContainSingle();
        cut.FindAll("[data-testid=e2e-step-action-1] option").Select(o => o.GetAttribute("value")).Should().NotContain("PickElement");
    }

    [Fact]
    public void ARoleSelectorIsAuthoredAsRoleAndName_AndSavedThatWay()
    {
        CriticalE2EFlowDefinition? saved = null;
        Services.AddSingleton<ICriticalE2EApiService>(new PickApi(_ => new CriticalE2EElementPickResult()));
        var flow = Flow() with { Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.AssertVisible, IsFinalAssertion = true,
            Selector = new CompanionSelector { Kind = CompanionSelectorKind.Role } }] };
        var cut = Render<CriticalE2EFlowEditor>(p => p.Add(e => e.Flow, flow).Add(e => e.ProfileId, "dev").Add(e => e.EnvironmentId, "dev")
            .Add(e => e.ElementPick, Ready).Add(e => e.OnSave, (CriticalE2EFlowDefinition f) => saved = f));

        cut.Find("[data-testid=e2e-step-selector-details-0-toggle]").Click();
        cut.Find("[data-testid=e2e-step-selector-role-0]").Change("button");
        cut.Find("[data-testid=e2e-step-selector-name-0]").Change("Lagre");
        cut.Find("[data-testid=e2e-editor-save]").Click();

        saved!.Steps.Single().Selector.Should().Be(new CompanionSelector { Kind = CompanionSelectorKind.Role, Role = "button", Name = "Lagre" });
    }

    [Fact]
    public void UnsavedEditsSurviveAParentRerenderWithTheSameFlow()
    {
        var flow = Flow();
        var cut = Editor(new PickApi(_ => new CriticalE2EElementPickResult()), Ready, flow);
        cut.Find("[data-testid=e2e-editor-name]").Change("Foreta plassering (endret)");

        cut.Render(p => p.Add(e => e.Flow, flow).Add(e => e.ElementPick, Ready with { Message = "refreshed" }));

        cut.Find("[data-testid=e2e-editor-name]").GetAttribute("value").Should().Be("Foreta plassering (endret)");
    }

    [Fact]
    public void SelectorDetailsAreCollapsedAndListTheCandidates()
    {
        var cut = Editor(new PickApi(_ => Picked(new CompanionSelector { Kind = CompanionSelectorKind.Role, Role = "button", Name = "Ny plassering" })), Ready);
        cut.Find("[data-testid=e2e-step-pick-1]").Click();

        var toggle = cut.Find("[data-testid=e2e-step-selector-details-1-toggle]");
        toggle.GetAttribute("aria-expanded").Should().Be("false", "the selector is secondary to the identity");
        cut.Find("[data-testid=e2e-step-selector-details-1-body]").HasAttribute("hidden").Should().BeTrue();
        toggle.Click();
        cut.Find("[data-testid=e2e-step-candidates-1]").TextContent.Should().Contain("Role + name").And.Contain("1 (unique)").And.Contain("Text");
        // The captured route is context, not a guard: the UI must not claim protection that does not exist.
        cut.Find("[data-testid=e2e-step-route-note-1]").TextContent.Should().Be("The element was selected on /plassering. The route is not stored with the step, so it is not enforced when the flow runs.");
    }

    [Fact]
    public void ACssOnlyTargetIsMarkedFragile_ButCanBeSaved()
    {
        var flow = Flow() with { Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.Click, IsFinalAssertion = true,
            Selector = new CompanionSelector { Kind = CompanionSelectorKind.Css, Value = "div.role-list > div:nth-of-type(3)" } }] };
        var cut = Editor(new PickApi(_ => new CriticalE2EElementPickResult()), Ready, flow);

        cut.Find("[data-testid=e2e-step-identity-0]").TextContent.Should().Be("Element at a structural CSS path", "a CSS path is never the identity shown");
        cut.Find("[data-testid=e2e-step-fragile-0]").TextContent.Should().StartWith("Selector may be fragile.");
        cut.Find("[data-testid=e2e-editor-save]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void ALabelPickedInputReadsAsTheInputAndItsLabel()
    {
        var api = new PickApi(_ => new CriticalE2EElementPickResult
        {
            Status = CriticalE2EStatus.Passed, Outcome = CriticalE2EPickOutcome.Picked,
            Element = new CompanionElementDescriptor
            {
                PageOrigin = "https://m2lbdev.bufetat.no", PageRoute = "/admin/general-roles", TagName = "input", InputType = "text",
                Label = "Fra dato", Visible = true, Enabled = true,
                Candidates = [new() { Selector = new CompanionSelector { Kind = CompanionSelectorKind.Label, Value = "Fra dato" }, MatchCount = 1, Unique = true }],
                Recommended = new CompanionSelector { Kind = CompanionSelectorKind.Label, Value = "Fra dato" },
            },
        });
        var flow = Flow() with { Steps = [new CriticalE2EStepDefinition { StepId = "s1", BrowserAction = CompanionActionKind.Fill, Value = "2026-09-24", IsFinalAssertion = true, Selector = new CompanionSelector() }] };
        var cut = Editor(api, Ready, flow);

        cut.Find("[data-testid=e2e-step-pick-0]").Click();

        cut.Find("[data-testid=e2e-step-identity-0]").TextContent.Should().Be("Input \u2014 Fra dato");
        cut.Find("[data-testid=e2e-step-strategy-0]").TextContent.Should().Contain("Selector: Label");
        cut.Find("[data-testid=e2e-step-value-0]").GetAttribute("value").Should().Be("2026-09-24", "Fill keeps its value field beside the target");
    }

    [Fact]
    public void WhilePickingTheStepSaysSelecting()
    {
        var gate = new TaskCompletionSource<CriticalE2EElementPickResult>();
        Services.AddSingleton<ICriticalE2EApiService>(new SlowPickApi(gate.Task));
        var cut = Render<CriticalE2EFlowEditor>(p => p.Add(e => e.Flow, Flow()).Add(e => e.ProfileId, "dev").Add(e => e.EnvironmentId, "dev").Add(e => e.ElementPick, Ready));

        cut.Find("[data-testid=e2e-step-pick-1]").Click();

        cut.Find("[data-testid=e2e-step-pick-1]").TextContent.Trim().Should().Be("Waiting for selection in the browser\u2026");
        cut.Find("[data-testid=e2e-step-pick-selecting-1]").GetAttribute("role").Should().Be("status");
        gate.SetResult(new CriticalE2EElementPickResult { Status = CriticalE2EStatus.Cancelled, Outcome = CriticalE2EPickOutcome.Cancelled, Message = "Selection cancelled." });
        cut.WaitForAssertion(() => cut.FindAll("[data-testid=e2e-step-pick-selecting-1]").Should().BeEmpty());
    }

    [Fact]
    public void RouteStepsTakeARouteNotAnElement()
    {
        var cut = Editor(new PickApi(_ => new CriticalE2EElementPickResult()), Ready);
        cut.Find("[data-testid=e2e-step-value-0]").GetAttribute("placeholder").Should().Be("/admin/general-roles");
        cut.Find("[data-testid=e2e-step-0]").TextContent.Should().Contain("Route");
        cut.FindAll("[data-testid=e2e-step-target-summary-0]").Should().BeEmpty();

        cut.Find("[data-testid=e2e-step-action-1]").Change("AssertRoute");
        cut.FindAll("[data-testid=e2e-step-pick-1]").Should().BeEmpty("a route assertion has no element to pick");
        cut.Find("[data-testid=e2e-step-1]").TextContent.Should().Contain("Expected route");
    }

    [Fact]
    public void ThePurposeAndExecutionAreHeaderChoices_WithHumanLabels()
    {
        var cut = Editor(new PickApi(_ => new CriticalE2EElementPickResult()), Ready);
        cut.FindAll("[data-testid=e2e-editor-mode] option").Select(o => o.TextContent).Should().Equal("Attended browser", "Automated API");
        cut.FindAll("[data-testid=e2e-editor-kind] option").Select(o => o.TextContent).Should().Equal("Critical user flow", "Smoke / diagnostic");
        cut.Find("[data-testid=e2e-editor-details-toggle]").GetAttribute("aria-expanded").Should().Be("false");
    }

    private sealed class SlowPickApi(Task<CriticalE2EElementPickResult> result) : ICriticalE2EApiService
    {
        public Task<CriticalE2EOverview> OverviewAsync(CriticalE2EOverviewRequest request, CancellationToken ct = default) => Task.FromResult(new CriticalE2EOverview());
        public Task<List<CriticalE2EFlowDefinition>> FlowsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new List<CriticalE2EFlowDefinition>());
        public Task<CriticalE2EFlowDefinition> SaveFlowAsync(CriticalE2EFlowDefinition flow, CancellationToken ct = default) => Task.FromResult(flow);
        public Task DeleteFlowAsync(string flowId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CriticalE2ERunBatchResult> RunAsync(CriticalE2ERunFlowRequest request, CancellationToken ct = default) => Task.FromResult(new CriticalE2ERunBatchResult());
        public Task<CriticalE2EElementPickResult> PickElementAsync(CriticalE2EElementPickRequest request, CancellationToken ct = default) => result;
    }
}
