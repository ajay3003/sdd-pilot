using System.Security.Cryptography;
using System.Text;
using BirkNext.Specifications;

namespace BirkNext.Api.Services.Specifications.OpenSpec;

/// <summary>
/// The OpenSpec identity namespace (rule set <see cref="Version"/>), separate from every Spec Kit identity. Deterministic, documented normalization:
/// <list type="number">
/// <item>Name normalization: Unicode NFKC, every run of whitespace → one space, trimmed. Comparison form = normalized name, lower-case (invariant).</item>
/// <item>Slug: comparison form; every letter or digit (any script, so æ/ø/å stay) is kept, every other run of characters becomes one «-», trimmed of «-».
/// Longer than 60 characters → the first 60 + «-» + 8 hex of SHA-256(comparison form). Empty → «x-» + 8 hex.</item>
/// <item>Capability key: the folder path below <c>specs/</c>, each segment slugged, joined with «.» («billing/Invoices» → «billing.invoices»).</item>
/// <item>Scope: empty when the import has one openspec/ root; otherwise the root's folders (below a common archive folder) slugged and joined with «.»,
/// followed by «~». Two roots never share identities.</item>
/// <item>Requirement key = «os:» + scope + capability + «/» + slug(requirement name). Scenario key = requirement key + «/» + slug(scenario name).
/// Change key = «osc:» + scope + («archive/» for an archived change) + slug(change folder).</item>
/// </list>
/// The key is a LOGICAL identity: editing a requirement's text keeps its key (its <c>TextSha256</c> revision changes). A rename or a move to another
/// capability is a new key; the old key is never reassigned. Keys are BirkNext keys («os:» prefix) and are never shown as source-authored identifiers.
/// Title similarity is never used to relate two keys.
/// </summary>
public static class OpenSpecIdentity
{
    public const string Version = "openspec/1";

    public static string NormalizeName(string? name)
    {
        var text = (name ?? "").Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
        }
        return sb.ToString();
    }

    public static string Comparable(string? name) => NormalizeName(name).ToLowerInvariant();

    public static string Slug(string? name)
    {
        var comparable = Comparable(name);
        var sb = new StringBuilder(comparable.Length);
        var dash = false;
        foreach (var ch in comparable)
        {
            if (char.IsLetterOrDigit(ch)) { if (dash && sb.Length > 0) sb.Append('-'); dash = false; sb.Append(ch); }
            else dash = true;
        }
        var slug = sb.ToString();
        if (slug.Length == 0) return "x-" + Hash8(comparable);
        return slug.Length <= 60 ? slug : slug[..60].TrimEnd('-') + "-" + Hash8(comparable);
    }

    public static string CapabilityKey(IEnumerable<string> folders) => string.Join('.', folders.Select(Slug));

    public static string ScopePrefix(string? scope) => string.IsNullOrEmpty(scope) ? "" : scope + "~";

    public static string ScopeOf(IEnumerable<string> rootFolders) => string.Join('.', rootFolders.Select(Slug));

    public static string RequirementKey(string? scope, string capability, string requirementName) =>
        $"{SpecificationFormatMarker.OpenSpecKeyPrefix}{ScopePrefix(scope)}{capability}/{Slug(requirementName)}";

    public static string ScenarioKey(string requirementKey, string scenarioName) => $"{requirementKey}/{Slug(scenarioName)}";

    public static string ChangeKey(string? scope, string changeFolder, bool archived) => $"osc:{ScopePrefix(scope)}{(archived ? "archive/" : "")}{Slug(changeFolder)}";

    /// <summary>The key discovery turns into a source identity (see <c>QaTestCandidate.SourceIdentityKey</c>).</summary>
    public static string SourceIdentityKey(string key) => $"{Version}|{key}";

    public static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string Hash8(string text) => Sha(text)[..8];
}
