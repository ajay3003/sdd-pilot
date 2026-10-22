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

    /// <summary>The fourth prerequisite card: its status chip and tone are the one statement of the settings check.</summary>
    private static void SettingsCard(IRenderedComponent<Component> cut, string tone, string status)
    {
        cut.Find("[data-testid=auth-card-auth-settings]").ClassList.Should().Contain("ar-tone-" + tone);
        cut.Find("[data-testid=auth-status-auth-settings]").TextContent.Should().Be(status);
    }

    [Fact]
    public void AuthenticationCards_TrackCheckApplyCancelAndSave()
    {
        var cut = Open();
        OpenTab(cut, "Authentication");
        SettingsCard(cut, "muted", "Not checked");
        Card(cut, "authentication-configuration", "saved", "Saved configuration");
        cut.Find("[data-testid=auth-action-auth-settings]").Click();
        SettingsCard(cut, "needs-action", "Review required (5 differences)");
        SaveCalls().Should().Be(0);
        UseAllDetected(cut);
        SettingsCard(cut, "ready", "Matches draft — not saved yet");
        Card(cut, "authentication-configuration", "draft", "Unsaved changes");
        cut.Find(".fa-page-unsaved").TextContent.Should().Be("Unsaved changes");
        ButtonDisabled(cut, "Save changes").Should().BeFalse();
        SaveCalls().Should().Be(0);
        Click(cut, "Cancel");
        SettingsCard(cut, "needs-action", "Review required (5 differences)");
        Card(cut, "authentication-configuration", "saved", "Saved configuration");
        SaveCalls().Should().Be(0);
        UseAllDetected(cut);
        Click(cut, "Save changes");
        cut.WaitForAssertion(() => SettingsCard(cut, "ready", "Matches saved configuration"));
        cut.FindAll("[data-testid=authentication-differences], [data-testid=authentication-apply-selected]").Should().BeEmpty();
        SaveCalls().Should().Be(1);
    }

    [Fact]
    public void MatchingConfiguration_OffersNothingToApply()
    {
        var cut = Open($$"""
        { "authenticationType": "MicrosoftEntraId", "expectedAuthority": "{{Authority}}", "expectedTenant": "{{Tenant}}",
          "expectedClientId": "{{ClientId}}", "allowedRedirectUrls": ["{{Redirect}}"] }
        """);
        OpenTab(cut, "Authentication");
        cut.Find("[data-testid=auth-action-auth-settings]").Click();
        SettingsCard(cut, "ready", "Matches saved configuration");
        cut.FindAll("[data-testid=authentication-differences], [data-testid=authentication-apply-selected]").Should().BeEmpty();
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
        // The method is part of the saved authentication configuration, so the configuration card — the one
        // card that owns saved-versus-draft — carries the unsaved state. It is no longer echoed on a second card.
        Card(cut, "authentication-configuration", "draft", "Unsaved changes");
        cut.FindAll("[data-testid='authenticated-testing-method']").Should().BeEmpty();
        Card(cut, "manual-only-panel", "muted", "Not recorded"); // an unrecorded optional sign-in check is neutral, never a required step
        SaveCalls().Should().Be(0);
        Click(cut, "Cancel");
        Card(cut, "authentication-configuration", "saved", "Saved configuration");
        Click(cut, "Edit Environment");
        Click(cut, "Cancel");
        cut.FindAll(".fa-page-unsaved").Should().BeEmpty();
    }

    [Fact]
    public void DetectedEndpointProposals_AreNotShownOnAnyTab()
    {
        // The "Discovered API Endpoints" proposal UI (Apply REST/GraphQL/Swagger/Health) was removed entirely; detection still
        // records the endpoints in the result model, but they are no longer surfaced as apply-to-config proposals anywhere.
        var cut = Open();
        Click(cut, "Detect settings");
        foreach (var tab in new[] { "Target Application", "Endpoint Discovery" })
        {
            OpenTab(cut, tab);
            cut.Markup.Should().NotContain("Discovered API Endpoints");
            foreach (var action in new[] { "Apply REST", "Apply GraphQL", "Apply Swagger", "Apply Health" })
                cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == action);
        }
    }

    [Fact]
    public void DivergentAuthenticationEdit_RequiresRecheck_AndCancelRestoresTheReview()
    {
        var cut = DetectAndApply(Open());
        cut.Find("#configured-authority").Change("https://login.microsoftonline.com/other");
        SettingsCard(cut, "needs-action", "Recheck required");
        cut.Find("[data-testid=auth-card-auth-settings]").TextContent.Should().Contain("Authentication settings changed since the last check");
        cut.FindAll("[data-testid=authentication-differences], [data-testid=authentication-apply-selected]").Should().BeEmpty("a stale comparison is never reviewed or applied");
        Click(cut, "Cancel");
        SettingsCard(cut, "needs-action", "Review required (5 differences)");
        SaveCalls().Should().Be(0);
    }
}
