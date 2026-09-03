using BirkNext.Web.Shared.Components.Buttons;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

public class SecondaryButtonTests : BunitContext
{
    [Fact]
    public void DisabledButtonKeepsNativeSemanticsAndDoesNotInvokeAction()
    {
        var invocations = 0;
        var cut = Render<SecondaryButton>(parameters => parameters
            .Add(p => p.Disabled, true)
            .Add(p => p.OnClick, () => invocations++)
            .AddChildContent("Export JSON"));

        var button = cut.Find("button.btn-secondary");
        button.HasAttribute("disabled").Should().BeTrue();
        button.Click();
        invocations.Should().Be(0);
    }
}
