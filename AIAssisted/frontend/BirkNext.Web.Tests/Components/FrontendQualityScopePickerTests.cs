using BirkNext.Web.Components;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

public sealed class FrontendQualityScopePickerTests : BunitContext
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopeIsSavedOnlyOnApply(bool apply)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/frontendReviewScope.js").Mode = JSRuntimeMode.Loose;
        var saved = FrontendReviewAccessScope.PublicOnly;
        var calls = 0;
        var cut = Render<FrontendQualityScopePicker>(p => p
            .Add(c => c.Selected, saved)
            .Add(c => c.Verification, "Manual verification required")
            .Add(c => c.TestingContext, "Ready")
            .Add(c => c.SelectedChanged, scope => { saved = scope; calls++; }));
        cut.Find("[data-testid=fqr-change-scope]").Click();
        cut.FindAll("input").Should().HaveCount(3);
        cut.Find("input[value=PublicAndAuthenticated]").Change("PublicAndAuthenticated");
        cut.FindAll("dialog button")[apply ? 1 : 0].Click();
        calls.Should().Be(apply ? 1 : 0);
        saved.Should().Be(apply ? FrontendReviewAccessScope.PublicAndAuthenticated : FrontendReviewAccessScope.PublicOnly);
        cut.Markup.Should().Contain("Manual verification required");
    }
}
