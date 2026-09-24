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
        Status = CriticalE2EStatus.Passed,
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
        cut.Find("[data-testid=e2e-step-pick-note-1]").TextContent.Should()
            .Be("Picked button “Ny plassering”. Stored role=button name=\"Ny plassering\" — unique on /plassering.");
        cut.FindAll("[data-testid=e2e-editor-problem]").Should().BeEmpty("the picked step is complete");
    }

    [Fact]
    public void WithoutAUniqueSelectorNothingIsStored()
    {
        var cut = Editor(new PickApi(_ => Picked(recommended: null)), Ready);

        cut.Find("[data-testid=e2e-step-pick-1]").Click();

        cut.Find("[data-testid=e2e-step-selector-1]").GetAttribute("value").Should().BeEmpty();
        cut.Find("[data-testid=e2e-step-pick-note-1]").TextContent.Should().Contain("nothing was stored").And.Contain("data-testid");
        cut.Find("[data-testid=e2e-editor-problem]").TextContent.Should().Be("A browser step has no element to act on.");
    }

    [Fact]
    public void AHiddenOrDisabledPickIsStoredButFlagged()
    {
        var cut = Editor(new PickApi(_ => Picked(new CompanionSelector { Kind = CompanionSelectorKind.TestId, Value = "lagre" }, enabled: false)), Ready);

        cut.Find("[data-testid=e2e-step-pick-1]").Click();

        cut.Find("[data-testid=e2e-step-pick-note-1]").TextContent.Should().EndWith("It is currently disabled.");
    }

    [Theory]
    [InlineData(CriticalE2EStatus.Blocked, "The Browser Companion is not connected.")]
    [InlineData(CriticalE2EStatus.Cancelled, "Selection cancelled.")]
    public void AnythingButAPickLeavesTheStepAloneAndSaysWhy(CriticalE2EStatus status, string message)
    {
        var cut = Editor(new PickApi(_ => new CriticalE2EElementPickResult { Status = status, Message = message }), Ready);

        cut.Find("[data-testid=e2e-step-pick-1]").Click();

        cut.Find("[data-testid=e2e-step-pick-note-1]").TextContent.Should().Be(message);
        cut.Find("[data-testid=e2e-step-selector-1]").GetAttribute("value").Should().BeEmpty();
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
}
