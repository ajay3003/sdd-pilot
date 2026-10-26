using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Constitution rule identity for documentation traceability. A rule is referenced by its written ID (PP-01 = PP-001 = pp-1); a rule whose
/// heading has no ID gets a placeholder (PRINCIPLE-001, STANDARD-…, CONSTRAINT-…, GOV-…) that no document can cite, so its reference coverage
/// cannot be determined deterministically — it is reported as "no deterministic mapping", never as a missing reference.
/// </summary>
public static partial class ConstitutionRuleIds
{
    public static bool IsPlaceholder(string id) => Placeholder().IsMatch(id ?? "");

    /// <summary>The rule has no citable ID: a placeholder primary ID and no written alias.</summary>
    public static bool HasNoCitableId(ConstitutionRule rule) => IsPlaceholder(rule.RuleId) && rule.Aliases.All(IsPlaceholder);

    public static string Normalize(string id) => Numbered().Match(id ?? "") is { Success: true } m
        ? $"{m.Groups[1].Value.ToUpperInvariant()}-{int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)}" : (id ?? "").Trim().ToUpperInvariant();

    /// <summary>Compares rule IDs in one spelling.</summary>
    public static readonly IEqualityComparer<string> Comparer = new IdComparer();

    private sealed class IdComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => string.Equals(Normalize(x ?? ""), Normalize(y ?? ""), StringComparison.Ordinal);
        public int GetHashCode(string obj) => Normalize(obj).GetHashCode(StringComparison.Ordinal);
    }

    [GeneratedRegex(@"^(PRINCIPLE|STANDARD|CONSTRAINT|GOV|GUIDELINE|RULE)-\d{3}$", RegexOptions.IgnoreCase)]
    private static partial Regex Placeholder();
    [GeneratedRegex(@"^\s*([A-Z]{1,4}(?:-C)?)-?\s*(\d{1,4})\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Numbered();
}
