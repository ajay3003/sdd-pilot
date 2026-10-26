using System.Text.RegularExpressions;

namespace BirkNext.Web.Services;

/// <summary>
/// The Spec-Kit specification header lines ("**Status**: Draft", "**Input**: User description: …", "**Feature Branch**: …", "**Created**: …")
/// as Specification Explorer shows them: status and feature summary for testers, branch and date as technical detail. Reads only the explicit
/// "**Label**: value" lines before the first section; a specification without them yields nothing. The authored text is not changed.
/// </summary>
public sealed partial record SpecificationHeader(string? Status, string? Summary, string? FeatureBranch, string? Created)
{
    public static readonly SpecificationHeader Empty = new(null, null, null, null);

    public bool IsEmpty => Status is null && Summary is null && FeatureBranch is null && Created is null;

    public static SpecificationHeader Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Empty;
        string? status = null, summary = null, branch = null, created = null;
        var sawTitle = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("# ", StringComparison.Ordinal)) { sawTitle = true; continue; }
            // The header ends at the first section heading after the title.
            if (sawTitle && line.StartsWith("##", StringComparison.Ordinal)) break;
            var match = Field().Match(line);
            if (!match.Success) continue;
            var label = match.Groups["label"].Value.Trim();
            var value = match.Groups["value"].Value.Trim().Trim('`').Trim();
            if (value.Length == 0) continue;
            if (label.Equals("Status", StringComparison.OrdinalIgnoreCase)) status ??= value;
            else if (label.Equals("Created", StringComparison.OrdinalIgnoreCase)) created ??= value;
            else if (label.Equals("Feature Branch", StringComparison.OrdinalIgnoreCase)) branch ??= value;
            else if (label.Equals("Input", StringComparison.OrdinalIgnoreCase))
                summary ??= InputPrefix().Replace(value, "").Trim().Trim('"', '“', '”').Trim();
        }
        return new(status, string.IsNullOrWhiteSpace(summary) ? null : summary, branch, created);
    }

    [GeneratedRegex(@"^[-*]?\s*\*\*(?<label>[^*]+)\*\*\s*:\s*(?<value>.*)$")]
    private static partial Regex Field();

    [GeneratedRegex(@"^User description\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex InputPrefix();
}
