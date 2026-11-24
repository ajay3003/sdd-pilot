using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Manual Testing chooses the specification where the tester works (2026-11-19): Module, Feature and — when a feature has several —
/// Specification dropdowns that set the same choice the explorers save, and a line naming the specification in use. Nothing is chosen
/// automatically: an unchosen level shows "Choose …".
/// </summary>
public sealed class SpecificationScopePickerTests : BunitContext
{
    private readonly Mock<IArtifactExplorerContext> _explorer = new();
    private readonly Mock<IWorkspaceAutoSaveService> _save = new();
    private ArtifactExplorerState _state = null!;

    private static ExplorerArtifact Spec(string id, string path, string title, string module) =>
        new(id, WorkspaceArtifactType.Specification, title, "spec.md", path, ExplorerArtifactSource.SampleProject, null, null, 1, "Discovered",
            ExplorerArtifactCurrentness.Current, null, Module: module);

    private static readonly IReadOnlyList<ArtifactModuleRoleState> Modules =
        [new("HendelseAdapter", 4, null, null, Packs: 4), new("SkoleService", 2, "s", ExplorerSelectionReason.OnlyArtifact)];

    private static readonly IReadOnlyList<ArtifactPackSummary> Packs =
        [new("HendelseAdapter", "001-adapter", "001", "BiRK Hendelsesadapter", [WorkspaceArtifactType.Specification], 5),
         new("HendelseAdapter", "004-translate", "004", "Translate HendelseAdapter", [WorkspaceArtifactType.Specification], 6)];

    private IRenderedComponent<SpecificationScopePicker> Open(ArtifactExplorerState state, Action? changed = null)
    {
        _state = state;
        _explorer.Setup(x => x.GetStateAsync(WorkspaceArtifactType.Specification, It.IsAny<CancellationToken>())).ReturnsAsync(() => _state);
        _save.Setup(x => x.FlushAsync()).ReturnsAsync(true);
        Services.AddSingleton(_explorer.Object);
        Services.AddSingleton(_save.Object);
        return Render<SpecificationScopePicker>(p => p.Add(x => x.Changed, () => changed?.Invoke()));
    }

    private static ArtifactExplorerState State(ExplorerArtifactStatus status, IReadOnlyList<ExplorerArtifact>? artifacts = null, ExplorerArtifact? selected = null,
        string? module = null, string? pack = null, bool packRequired = false, bool packs = false) =>
        new(WorkspaceArtifactType.Specification, status, "m2lb", "M2LB", artifacts ?? [], selected, Modules: Modules, Module: module,
            Packs: packs ? Packs : null, Pack: pack, PackRequired: packRequired);

    [Fact]
    public void Without_a_chosen_module_it_asks_for_one_and_choosing_it_sets_the_shared_choice()
    {
        var changed = 0;
        var cut = Open(State(ExplorerArtifactStatus.SelectionRequired), () => changed++);
        cut.Find("[data-testid=ssp-current]").GetAttribute("data-state").Should().Be("module");
        cut.FindAll("[data-testid=ssp-module] option").Select(o => o.TextContent).Should().Equal("Choose a module…", "HendelseAdapter — 4 features", "SkoleService — 2 specifications");
        cut.FindAll("[data-testid=ssp-feature]").Should().BeEmpty("a feature list waits for the module");

        _state = State(ExplorerArtifactStatus.SelectionRequired, module: "HendelseAdapter", packRequired: true, packs: true);
        cut.Find("[data-testid=ssp-module]").Change("HendelseAdapter");

        _explorer.Verify(x => x.SelectModuleAsync("HendelseAdapter", It.IsAny<CancellationToken>()));
        _save.Verify(x => x.FlushAsync(), "the choice is saved like one made in the explorers");
        _save.Verify(x => x.SaveNowAsync(), Times.Never, "the auto-save already saves the change: no second full save");
        changed.Should().Be(1);
        cut.Find("[data-testid=ssp-current]").GetAttribute("data-state").Should().Be("feature");
        cut.FindAll("[data-testid=ssp-feature] option").Select(o => o.TextContent).Should().Equal("Choose a feature…", "BiRK Hendelsesadapter (001-adapter)", "Translate HendelseAdapter (004-translate)");
    }

    [Fact]
    public void Choosing_a_feature_names_the_specification_in_use_and_points_to_a_new_discovery()
    {
        var cut = Open(State(ExplorerArtifactStatus.SelectionRequired, module: "HendelseAdapter", packRequired: true, packs: true));
        var spec = Spec("h4", "HendelseAdapter/specs/004-translate/spec.md", "Translate HendelseAdapter", "HendelseAdapter");
        _state = State(ExplorerArtifactStatus.Loaded, [spec], spec, module: "HendelseAdapter", pack: "004-translate", packs: true);

        cut.Find("[data-testid=ssp-feature]").Change("004-translate");

        _explorer.Verify(x => x.SelectPackAsync("004-translate", "HendelseAdapter", It.IsAny<CancellationToken>()));
        cut.Find("[data-testid=ssp-current]").TextContent.Should().Contain("Translate HendelseAdapter").And.Contain("HendelseAdapter/specs/004-translate/spec.md");
        cut.Find("[data-testid=ssp-feature]").GetAttribute("value").Should().Be("004-translate");
        cut.FindAll("[data-testid=ssp-specification]").Should().BeEmpty("one specification needs no third list");
        cut.Find("[data-testid=ssp-changed]").TextContent.Should().Contain("came from the previous one");
        cut.Find("[data-testid=ssp-discover]").GetAttribute("href").Should().Be("test-plan-suggestions?area=choose&discover=now", "Discover now runs it on arrival");
    }

    [Fact]
    public void Where_discovery_runs_on_the_page_Discover_now_runs_it_there()
    {
        var spec = Spec("h4", "HendelseAdapter/specs/004-translate/spec.md", "Translate HendelseAdapter", "HendelseAdapter");
        _state = State(ExplorerArtifactStatus.SelectionRequired, module: "HendelseAdapter", packRequired: true, packs: true);
        _explorer.Setup(x => x.GetStateAsync(WorkspaceArtifactType.Specification, It.IsAny<CancellationToken>())).ReturnsAsync(() => _state);
        _save.Setup(x => x.FlushAsync()).ReturnsAsync(true);
        Services.AddSingleton(_explorer.Object);
        Services.AddSingleton(_save.Object);
        var discovered = 0;
        var cut = Render<SpecificationScopePicker>(p => p.Add(x => x.Discover, () => discovered++));
        _state = State(ExplorerArtifactStatus.Loaded, [spec], spec, module: "HendelseAdapter", pack: "004-translate", packs: true);

        cut.Find("[data-testid=ssp-feature]").Change("004-translate");
        cut.Find("[data-testid=ssp-discover]").Click();

        discovered.Should().Be(1);
        cut.FindAll("[data-testid=ssp-changed]").Should().BeEmpty("the notice is answered");
    }

    [Fact]
    public void Several_specifications_in_a_module_are_a_third_list_and_choosing_one_selects_it()
    {
        var main = Spec("s", "SkoleService/specs/001-skoleservice/spec.md", "SkoleService", "SkoleService");
        var other = Spec("d", "SkoleService/docs/avhengigheter.md", "Avhengigheter", "SkoleService");
        var cut = Open(State(ExplorerArtifactStatus.Loaded, [main, other], main, module: "SkoleService"));

        cut.FindAll("[data-testid=ssp-specification] option").Select(o => o.TextContent).Should().HaveCount(2);
        cut.Find("[data-testid=ssp-specification]").Change("d");

        _explorer.Verify(x => x.Select(WorkspaceArtifactType.Specification, "d", null));
    }

    [Fact]
    public void The_project_level_module_can_be_chosen_although_its_key_is_empty()
    {
        var withProject = State(ExplorerArtifactStatus.SelectionRequired) with { Modules = [.. Modules, new ArtifactModuleRoleState(ArtifactModules.ProjectLevel, 12, null, null, Packs: 4)] };
        var cut = Open(withProject);
        var option = cut.FindAll("[data-testid=ssp-module] option").Last();
        option.GetAttribute("value").Should().NotBeNullOrEmpty("an empty value would be the placeholder");
        cut.Find("[data-testid=ssp-module]").Change(option.GetAttribute("value"));
        _explorer.Verify(x => x.SelectModuleAsync(ArtifactModules.ProjectLevel, It.IsAny<CancellationToken>()));
    }

    [Fact]
    public void A_feature_without_its_own_specification_says_so()
    {
        var cut = Open(State(ExplorerArtifactStatus.Empty, module: "HendelseAdapter", pack: "001-adapter", packs: true));
        cut.Find("[data-testid=ssp-current]").GetAttribute("data-state").Should().Be("none");
        cut.Find("[data-testid=ssp-current]").TextContent.Should().Contain("BiRK Hendelsesadapter has no specification of its own");
    }

    // ── 2026-11-24: choosing the second specification of SkoleService ───────────────────────────────────────

    private static readonly ExplorerArtifact Main = Spec("s1", "SkoleService/specs/001-skoleservice/spec.md", "Feature Specification: SkoleService", "SkoleService");
    private static readonly ExplorerArtifact Dependencies = Spec("s2", "SkoleService/docs/avhengigheter-bestilling.md", "Avhengigheter for SkoleService før go-live", "SkoleService");

    private static ArtifactExplorerState Skole(ExplorerArtifact selected) => State(ExplorerArtifactStatus.Loaded, [Main, Dependencies], selected, module: "SkoleService");

    [Fact]
    public void The_second_specification_stays_chosen_and_switching_back_and_forth_works()
    {
        var changed = 0;
        var cut = Open(Skole(Main), () => changed++);
        _explorer.Setup(x => x.Select(WorkspaceArtifactType.Specification, It.IsAny<string>(), null))
            .Callback<WorkspaceArtifactType, string, string?>((_, id, _) => _state = Skole(id == "s2" ? Dependencies : Main));

        foreach (var (id, name) in new[] { ("s2", "Avhengigheter"), ("s1", "Feature Specification"), ("s2", "Avhengigheter") })
        {
            cut.Find("[data-testid=ssp-specification]").Change(id);
            cut.Find("[data-testid=ssp-specification]").GetAttribute("value").Should().Be(id);
            cut.Find("[data-testid=ssp-current]").TextContent.Should().Contain(name);
            cut.Find("[data-testid=ssp-module]").GetAttribute("value").Should().Be("SkoleService", "the module stays chosen");
            cut.FindAll("[data-testid=ssp-refused]").Should().BeEmpty();
        }
        changed.Should().Be(3);
    }

    [Fact]
    public void A_choice_that_cannot_be_used_is_said_plainly_and_the_dropdown_shows_the_specification_in_use()
    {
        var cut = Open(Skole(Main));
        // The explorers keep using the first specification (for example a document that is no longer available).
        _explorer.Setup(x => x.Select(WorkspaceArtifactType.Specification, It.IsAny<string>(), null));

        cut.Find("[data-testid=ssp-specification]").Change("s2");

        cut.Find("[data-testid=ssp-refused]").TextContent.Should().Contain("«Avhengigheter for SkoleService før go-live» kunne ikke brukes her")
            .And.Contain("«Feature Specification: SkoleService» brukes fortsatt");
        cut.Find("[data-testid=ssp-specification]").GetAttribute("value").Should().Be("s1", "the dropdown is redrawn from the real state");
        cut.Find("[data-testid=ssp-current]").TextContent.Should().Contain("Feature Specification");
    }

    [Fact]
    public void A_choice_that_could_not_be_saved_is_reported()
    {
        var cut = Open(Skole(Main));
        _save.Setup(x => x.FlushAsync()).ReturnsAsync(false);
        _explorer.Setup(x => x.Select(WorkspaceArtifactType.Specification, It.IsAny<string>(), null)).Callback(() => _state = Skole(Dependencies));

        cut.Find("[data-testid=ssp-specification]").Change("s2");

        cut.WaitForAssertion(() => cut.Find("[data-testid=ssp-save-warning]").TextContent.Should().Contain("could not be saved"));
        cut.Find("[data-testid=ssp-specification]").GetAttribute("value").Should().Be("s2", "the choice is still used in this session");
    }

    [Fact]
    public void The_pages_actions_sit_in_the_same_row_as_the_dropdowns()
    {
        _state = Skole(Main);
        _explorer.Setup(x => x.GetStateAsync(WorkspaceArtifactType.Specification, It.IsAny<CancellationToken>())).ReturnsAsync(() => _state);
        Services.AddSingleton(_explorer.Object);
        Services.AddSingleton(_save.Object);
        var cut = Render<SpecificationScopePicker>(p => p.Add(x => x.Actions, (Microsoft.AspNetCore.Components.RenderFragment)(b =>
        {
            b.OpenElement(0, "button"); b.AddAttribute(1, "data-testid", "page-action"); b.AddContent(2, "Oppdag på nytt"); b.CloseElement();
        })));

        var row = cut.Find(".ssp-row");
        row.QuerySelector("[data-testid=ssp-module]").Should().NotBeNull();
        row.QuerySelector("[data-testid=ssp-specification]").Should().NotBeNull();
        row.QuerySelector("[data-testid=ssp-actions] [data-testid=page-action]")!.TextContent.Should().Be("Oppdag på nytt");
    }

    [Fact]
    public void With_nothing_to_choose_the_pages_actions_keep_their_place()
    {
        _state = new ArtifactExplorerState(WorkspaceArtifactType.Specification, ExplorerArtifactStatus.Empty, "p", "P", []);
        _explorer.Setup(x => x.GetStateAsync(WorkspaceArtifactType.Specification, It.IsAny<CancellationToken>())).ReturnsAsync(() => _state);
        Services.AddSingleton(_explorer.Object);
        var cut = Render<SpecificationScopePicker>(p => p.Add(x => x.Actions, (Microsoft.AspNetCore.Components.RenderFragment)(b =>
        {
            b.OpenElement(0, "button"); b.AddAttribute(1, "data-testid", "page-action"); b.AddContent(2, "Oppdag scenarioer"); b.CloseElement();
        })));

        cut.Find("[data-testid=ssp-actions] [data-testid=page-action]").Should().NotBeNull();
        cut.FindAll("[data-testid=ssp-specification]").Should().BeEmpty();
    }
}
