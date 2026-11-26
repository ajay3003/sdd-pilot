using System.Text.RegularExpressions;

namespace BirkNext.TestPlanning;

/// <summary>
/// The specification a scenario belongs to when it records no specification file of its own (2026-11-26), read ONLY from the files its evidence
/// cites: a cited file inside a Spec Kit specification folder (<c>SkoleService/specs/001-skoleservice/contracts/rapportinntak.md</c>) names that
/// folder's <c>spec.md</c>. Every cited specification folder must agree; files outside a specification folder (source code, tests) never decide it;
/// the title is never used. Null when nothing qualifies or two folders disagree. Used for attribution (module, «kommer fra»), never to compare
/// sources: a scenario that was not read from the specification is not compared against it.
/// </summary>
public static partial class SpecificationFolderEvidence
{
    [GeneratedRegex(@"(?<![\w.\-])(?<folder>[\w.\-]+/specs/[\w.\-]+)/[^\s:#|,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex CitedFile();

    /// <summary>The <c>spec.md</c> of the single specification folder the references cite; null when none or several.</summary>
    public static string? SpecificationOf(IEnumerable<string?> references)
    {
        var folders = references.Where(r => !string.IsNullOrEmpty(r))
            .SelectMany(r => CitedFile().Matches(r!.Replace('\\', '/')).Select(m => m.Groups["folder"].Value))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList();
        return folders.Count == 1 ? folders[0] + "/spec.md" : null;
    }
}
