using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace BirkNext.Web.Tests.Packaging;

/// <summary>
/// WCAG AA contrast (4.5:1 for normal text) for the styles an axe-core scan of the published app found below it (2026-11-06): guided-step
/// actions dimmed by opacity (3.55:1), AQR capability details on a tinted row (4.35:1) and Delivery ready/review badges (4.38 / 4.42:1).
/// The ratios are computed from the colours in the stylesheets, alpha-blended over white as rendered.
/// </summary>
public sealed partial class ContrastStyleTests
{
    private static string Web(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AIAssisted", "frontend", "BirkNext.Web", "BirkNext.Web.csproj"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine([directory?.FullName ?? throw new DirectoryNotFoundException("BirkNext.Web"), "AIAssisted", "frontend", "BirkNext.Web", .. parts]));
    }

    private static (double R, double G, double B) Hex(string hex) =>
        (int.Parse(hex[1..3], NumberStyles.HexNumber) / 255d, int.Parse(hex[3..5], NumberStyles.HexNumber) / 255d, int.Parse(hex[5..7], NumberStyles.HexNumber) / 255d);

    private static (double R, double G, double B) OverWhite(int r, int g, int b, double alpha) =>
        ((r / 255d) * alpha + (1 - alpha), (g / 255d) * alpha + (1 - alpha), (b / 255d) * alpha + (1 - alpha));

    private static double Luminance((double R, double G, double B) c)
    {
        static double Channel(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Ratio((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static string Tone(string css, string tone) =>
        Regex.Match(css, $@"\.dd-tone-{tone} \{{ --tone: (#[0-9a-fA-F]{{6}});").Groups[1].Value;

    [Fact]
    public void GuidedSteps_AreNeverDimmedWithOpacity()
    {
        var css = Web("Components", "GuidedStepList.razor.css");
        StepRules().Matches(css).Select(m => m.Value).Should().NotContain(rule => rule.Contains("opacity"), "opacity dims the step's text and action below AA");
    }

    [Fact]
    public void AqrCapabilityDetails_MeetAaOnTheirTintedRow()
    {
        Web("Pages", "ApiQualityReview.razor.css").Should().Contain(".aqr-capability-detail { overflow-wrap: anywhere; color: var(--clr-text-muted, #475569); }");
        Web("wwwroot", "css", "design-tokens.css").Should().Contain("--clr-text-muted:       #475569;");
        Ratio(Hex("#475569"), Hex("#fef2f2")).Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Theory]
    [InlineData("ready", 21, 128, 61, 0.10)]
    [InlineData("review", 217, 119, 6, 0.12)]
    public void DeliveryBadgeTones_MeetAaOnTheirOwnTint(string tone, int r, int g, int b, double alpha)
    {
        var color = Tone(Web("wwwroot", "css", "delivery-intelligence.css"), tone);
        color.Should().NotBeEmpty();
        Ratio(Hex(color), OverWhite(r, g, b, alpha)).Should().BeGreaterThanOrEqualTo(4.5, $"the {tone} badge text sits on its own tinted background");
    }

    [GeneratedRegex(@"\.gsl-step[^{]*\{[^}]*\}")]
    private static partial Regex StepRules();
}
