using BirkNext.Integrations;
using BirkNext.TestData;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// Connected System → Test Data: requirements with Bound / Suggested / Needs selection / Missing states; a single strong match is preselected
/// (announced) but saved only by "Apply selected"; ambiguity is never preselected; a missing requirement offers Create Test Data; a system without
/// requirements says Not required. The section shows aliases only — never values.
/// </summary>
public sealed class ConnectedSystemTestDataPanelTests : BunitContext
{
    private readonly Mock<IExternalIntegrationsApiService> _api = new();
    private ConnectedSystemTestDataUpdate? _sent;

    private static readonly ConnectedSystemTestDataRequirement Reporter = new("reporter", "Reporter identity", TestDataSemanticRoles.Reporter, TestDataDataType.SimpleValues, true,
        [TestDataContexts.AltinnIntake], "Which TestID reporter signs in and submits.");
    private static readonly ConnectedSystemTestDataRequirement Child = new("child", "Child identity", TestDataSemanticRoles.Child, TestDataDataType.SimpleValues, true,
        [TestDataContexts.AltinnIntake], "The child the report is about.");

    private static TestDataAssetSummary Asset(string alias, string role, string? env = null, bool isDefault = false) => new()
    {
        Id = alias, Alias = alias, Label = alias, Kind = TestDataKind.EntityReference, Approval = TestDataApproval.Approved, Status = TestDataAssetStatus.Approved,
        DataType = TestDataDataType.SimpleValues, SemanticRole = role, AppliesTo = [TestDataContexts.AltinnIntake], Environment = env, IsDefault = isDefault,
    };

    private static ExternalIntegration System(List<ConnectedSystemTestDataBinding>? bindings = null) => new()
    {
        Id = "tt02:altinn", TargetEnvironmentId = "tt02", Name = "Skolenærvær – Altinn intake", EnvironmentName = "TT02", TestDataBindings = bindings,
    };

    private IRenderedComponent<ConnectedSystemTestDataPanel> Render(ConnectedSystemTestDataView view)
    {
        _api.Setup(a => a.BindTestDataAsync("tt02", "tt02:altinn", It.IsAny<ConnectedSystemTestDataUpdate>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, ConnectedSystemTestDataUpdate u, CancellationToken _) => _sent = u)
            .ReturnsAsync((string _, string _, ConnectedSystemTestDataUpdate u, CancellationToken _) => new ActiveEventApiResult<ConnectedSystemTestDataView>(
                ConnectedSystemTestDataMatcher.View(System([.. u.Changes.Select(c => new ConnectedSystemTestDataBinding(c.RequirementId, c.AssetAlias!))]), [Reporter, Child],
                    [Asset("reporter-default", TestDataSemanticRoles.Reporter, "TT02"), Asset("child-default", TestDataSemanticRoles.Child)]), null));
        Services.AddSingleton(_api.Object);
        return Render<ConnectedSystemTestDataPanel>(p => p.Add(x => x.EnvironmentId, "tt02").Add(x => x.IntegrationId, "tt02:altinn").Add(x => x.View, view));
    }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<ConnectedSystemTestDataPanel> cut, string id) => cut.Find($"[data-testid=cstd-row][data-requirement={id}]");

    [Fact]
    public void Strong_unique_suggestions_are_preselected_announced_and_saved_only_by_apply()
    {
        var assets = new[] { Asset("reporter-default", TestDataSemanticRoles.Reporter, "TT02"), Asset("child-default", TestDataSemanticRoles.Child) };
        var cut = Render(ConnectedSystemTestDataMatcher.View(System(), [Reporter, Child], assets));
        Row(cut, "reporter").QuerySelector("[data-testid=cstd-suggestion]")!.TextContent.Should().Contain("reporter-default").And.Contain("not saved yet");
        Row(cut, "child").QuerySelector("[data-testid=cstd-suggestion]")!.TextContent.Should().Contain("child-default", "never swapped");
        cut.FindAll("[data-testid=cstd-apply-check]").Should().OnlyContain(c => c.HasAttribute("checked"));
        cut.Find("[data-testid=cstd-message]").TextContent.Should().Be("2 strong Test Data suggestion(s) preselected — not saved until you apply.");
        _api.Verify(a => a.BindTestDataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ConnectedSystemTestDataUpdate>(), It.IsAny<CancellationToken>()), Times.Never);

        cut.Find("[data-testid=cstd-apply]").Click();
        _sent!.Changes.Select(c => (c.RequirementId, c.AssetAlias)).Should().BeEquivalentTo([("reporter", (string?)"reporter-default"), ("child", (string?)"child-default")]);
        cut.Find("[data-testid=cstd-status]").TextContent.Should().Be("Ready");
        Row(cut, "reporter").QuerySelector("[data-testid=cstd-bound]")!.TextContent.Should().StartWith("reporter-default");
        cut.Find("[data-testid=cstd-message]").TextContent.Should().Be("Applied 2 Test Data binding(s).");
    }

    [Fact]
    public void Ambiguous_matches_need_a_selection_and_are_never_preselected()
    {
        var assets = new[] { Asset("reporter-default", TestDataSemanticRoles.Reporter, "TT02", isDefault: true), Asset("reporter-a", TestDataSemanticRoles.Reporter, "TT02"), Asset("child-default", TestDataSemanticRoles.Child) };
        var cut = Render(ConnectedSystemTestDataMatcher.View(System(), [Reporter], assets));
        var row = Row(cut, "reporter");
        row.GetAttribute("data-state").Should().Be("NeedsSelection");
        row.QuerySelector("[data-testid=cstd-detail]")!.TextContent.Should().Be("2 compatible assets found — needs selection. Default: reporter-default.");
        cut.FindAll("[data-testid=cstd-apply-check]").Should().BeEmpty();
        cut.Find("[data-testid=cstd-apply]").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("[data-testid=cstd-select] option").Select(o => o.GetAttribute("value")).Should().Equal("", "reporter-default", "reporter-a");
        cut.Find("[data-testid=cstd-select]").Change("reporter-a");
        cut.Find("[data-testid=cstd-apply]").Click();
        _sent!.Changes.Single().Should().Be(new ConnectedSystemTestDataBindingChange("reporter", "reporter-a"));
    }

    [Fact]
    public void Missing_requirements_offer_create_test_data_and_no_requirements_say_not_required()
    {
        var cut = Render(ConnectedSystemTestDataMatcher.View(System(), [Child], []));
        Row(cut, "child").QuerySelector("[data-testid=cstd-detail]")!.TextContent.Should().Be("No compatible Child Test Data found.");
        Row(cut, "child").QuerySelector("[data-testid=cstd-create]")!.GetAttribute("href").Should().Be("/test-data?role=Child&appliesTo=altinn-intake");
        cut.Find("[data-testid=cstd-status]").TextContent.Should().Be("Missing");
        cut.Find("[data-testid=cstd-reason]").TextContent.Should().Be("Child identity: Missing");

        var none = Render<ConnectedSystemTestDataPanel>(p => p.Add(x => x.EnvironmentId, "tt02").Add(x => x.IntegrationId, "x").Add(x => x.View, ConnectedSystemTestDataMatcher.View(System(), [], [])));
        none.Find("[data-testid=cstd-not-required]").TextContent.Should().StartWith("Not required");
        none.Find("[data-testid=cstd-status]").TextContent.Should().Be("Not required");
    }

    [Fact]
    public void A_bound_asset_that_no_longer_exists_is_a_missing_reference_with_a_replacement_to_apply()
    {
        var view = ConnectedSystemTestDataMatcher.View(System([new("reporter", "reporter-custom")]), [Reporter], [Asset("reporter-default", TestDataSemanticRoles.Reporter, "TT02")]);
        var cut = Render(view);
        Row(cut, "reporter").GetAttribute("data-state").Should().Be("MissingReference");
        Row(cut, "reporter").QuerySelector("[data-testid=cstd-detail]")!.TextContent.Should().Be("Previously bound Test Data (reporter-custom) no longer exists. Suggested replacement available.");
        cut.Find("[data-testid=cstd-apply-check]").HasAttribute("checked").Should().BeFalse("a replacement is never applied automatically");
    }
}
