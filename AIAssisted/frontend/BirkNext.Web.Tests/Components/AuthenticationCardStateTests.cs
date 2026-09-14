using System.Reflection;
using System.Text.Json;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;
using Component = BirkNext.Web.Components.FrontendAnalysisSettings;

namespace BirkNext.Web.Tests.Components;

public sealed partial class AuthenticationApplyDraftTests
{
    private static void Card(IRenderedComponent<Component> cut, string id, string state, string label)
    {
        var card = cut.Find($"[data-testid='{id}']");
        card.ClassList.Should().Contain("fa-state-" + state);
        card.TextContent.Should().Contain(label);
    }

    [Fact]
    public void AuthenticationCards_TrackDetectApplyCancelAndSave()
    {
        var cut = Open();
        OpenTab(cut, "Authentication");
        Card(cut, "authentication-discovery", "neutral", "Not detected");
        Card(cut, "authentication-configuration", "saved", "Saved configuration");
        Click(cut, "Detect settings");
        OpenTab(cut, "Authentication");
        Card(cut, "authentication-discovery", "detected", "Detected — proposal available");
        SaveCalls().Should().Be(0);
        Click(cut, "Apply authentication");
        Card(cut, "authentication-discovery", "draft", "Applied to draft");
        Card(cut, "authentication-configuration", "draft", "Unsaved changes");
        cut.Find(".fa-page-unsaved").TextContent.Should().Be("Unsaved changes");
        ButtonDisabled(cut, "Save changes").Should().BeFalse();
        SaveCalls().Should().Be(0);
        Click(cut, "Cancel");
        Card(cut, "authentication-discovery", "detected", "Detected — proposal available");
        Card(cut, "authentication-configuration", "saved", "Saved configuration");
        SaveCalls().Should().Be(0);
        Click(cut, "Apply authentication");
        Click(cut, "Save changes");
        cut.WaitForAssertion(() => Card(cut, "authentication-discovery", "saved", "Matches saved configuration"));
        ButtonDisabled(cut, "Apply authentication").Should().BeTrue();
        SaveCalls().Should().Be(1);
    }

    [Fact]
    public async Task MatchingProposal_HandlerIsNoOpEvenWhenInvokedDirectly()
    {
        var cut = Open($$"""
        { "authenticationType": "MicrosoftEntraId", "expectedAuthority": "{{Authority}}", "expectedTenant": "{{Tenant}}",
          "expectedClientId": "{{ClientId}}", "allowedRedirectUrls": ["{{Redirect}}"] }
        """);
        Click(cut, "Detect settings");
        OpenTab(cut, "Authentication");
        Card(cut, "authentication-discovery", "saved", "Matches saved configuration");
        ButtonDisabled(cut, "Apply authentication").Should().BeTrue();
        await cut.InvokeAsync(() => typeof(Component).GetMethod("ApplyDetectedAuthenticationSettings",
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(cut.Instance, null));
        cut.FindAll(".fa-detail-header-actions button").Should().NotContain(b => b.TextContent.Trim() == "Save changes" || b.TextContent.Trim() == "Cancel");
        SaveCalls().Should().Be(0);
    }

    [Fact]
    public void CleanEdit_CanCancel_AndMethodChangeHasDraftState()
    {
        var cut = Open();
        Click(cut, "Edit Environment");
        OpenTab(cut, "Authentication");
        ButtonDisabled(cut, "Save changes").Should().BeTrue();
        ButtonDisabled(cut, "Cancel").Should().BeFalse();
        Card(cut, "authentication-configuration", "saved", "Editing draft");
        cut.Find("#authenticated-testing-method-select").Change("ManualOnly");
        Card(cut, "authenticated-testing-method", "draft", "Unsaved change");
        Card(cut, "manual-only-panel", "needs-action", "Required");
        SaveCalls().Should().Be(0);
        Click(cut, "Cancel");
        Card(cut, "authenticated-testing-method", "saved", "Saved configuration");
        Click(cut, "Edit Environment");
        Click(cut, "Cancel");
        cut.FindAll(".fa-page-unsaved").Should().BeEmpty();
    }

    [Theory]
    [InlineData("Apply REST", "RestBaseUrl")]
    [InlineData("Apply GraphQL", "GraphQlEndpoint")]
    [InlineData("Apply Health", "HealthEndpoint")]
    [InlineData("Apply Swagger", "SwaggerUrl")]
    public void EndpointApply_IsDraftOnlyAndCancelReverts(string label, string field)
    {
        var cut = Open();
        var before = JsonSerializer.Serialize(Persisted());
        Click(cut, "Detect settings");
        Click(cut, label);
        ButtonDisabled(cut, "Save changes").Should().BeFalse();
        var draft = (FrontendAnalysisProfile)typeof(Component).GetField("_editProfile",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(cut.Instance)!;
        typeof(FrontendAnalysisProfile).GetProperty(field)!.GetValue(draft).Should().NotBeNull();
        SaveCalls().Should().Be(0);
        JsonSerializer.Serialize(Persisted()).Should().Be(before);
        Click(cut, "Cancel");
        SaveCalls().Should().Be(0);
        JsonSerializer.Serialize(Persisted()).Should().Be(before);
    }

    [Fact]
    public void DivergentAuthenticationEdit_BlocksStaleProposal_AndCancelRestoresEvidence()
    {
        var cut = DetectAndApply(Open());
        cut.Find("#configured-authority").Change("https://login.microsoftonline.com/other");
        Card(cut, "authentication-discovery", "needs-action", "Detection stale");
        ButtonDisabled(cut, "Apply authentication").Should().BeTrue();
        Click(cut, "Cancel");
        Card(cut, "authentication-discovery", "detected", "Detected — proposal available");
        SaveCalls().Should().Be(0);
    }
}
