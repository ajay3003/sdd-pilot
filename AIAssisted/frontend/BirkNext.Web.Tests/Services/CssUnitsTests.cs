using System.Globalization;
using BirkNext.Web.Services;
using BirkNext.Web.Shared.Components.Indicators;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Hardening (NEXT42, TRACE-01): a coverage of 11.1 % rendered as a full green bar in a Norwegian browser, because the width was written
/// with the current culture ("11,1%" — invalid CSS, so the fill took the full width). CSS numbers are invariant everywhere.</summary>
public sealed class CssUnitsTests : BunitContext
{
    [Fact]
    public void Percentages_are_invariant_and_clamped_whatever_the_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nb-NO");
            CssUnits.Percent(100.0 * 2 / 18).Should().Be("11.11%");
            CssUnits.Percent(150).Should().Be("100%");
            CssUnits.Percent(double.NaN).Should().Be("0%");
            CssUnits.Number(12.5).Should().Be("12.5");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void The_shared_progress_bar_renders_a_valid_width_in_a_Norwegian_browser()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nb-NO");
            var cut = Render<ProgressBar>(p => p.Add(x => x.Value, 11.1));
            cut.Markup.Should().Contain("width:11.1%").And.NotContain("11,1%");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
