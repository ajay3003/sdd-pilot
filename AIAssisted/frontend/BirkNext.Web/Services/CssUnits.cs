using System.Globalization;

namespace BirkNext.Web.Services;

/// <summary>
/// CSS numbers are culture-invariant. A width written with the browser's culture ("11,1%" in Norwegian) is invalid CSS: the browser ignores it and a
/// bar's fill takes the full width — 11.1 % rendered as 100 %. Every computed style value goes through here.
/// </summary>
public static class CssUnits
{
    /// <summary>"11.1%": clamped to 0–100, invariant, at most two decimals.</summary>
    public static string Percent(double value) => (double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>An invariant number (for px, ARIA values and other numeric attributes).</summary>
    public static string Number(double value) => (double.IsFinite(value) ? value : 0).ToString("0.##", CultureInfo.InvariantCulture);
}
