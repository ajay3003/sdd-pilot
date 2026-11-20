using System.Text.RegularExpressions;

namespace BirkNext.Web.Services;

/// <summary>
/// Source-aware XSD choice for Schema Studio (2026-11-18). The first file in path order used to be pre-selected — for a Tiltak 2-style
/// archive a referral schema unrelated to the message flow. Schemas are now ranked by what the snapshot's source shows about them, and one is
/// pre-selected only when it is the single best-evidenced schema; a tie or no evidence leaves the choice to the tester, with the reason.
/// Static evidence only: "referenced by validation code" is never "verified in a runtime test".
/// </summary>
public static partial class SchemaSourceSelection
{
    public const string ValidationTier = "ReferencedByValidationCode", SourceTier = "ReferencedBySource", DeclaredTier = "Declared";

    /// <summary>One XSD with its evidence score (higher = stronger source evidence that something uses it).</summary>
    public sealed record RankedSchema(SourceSchemaFile File, int Score, string Tier);

    /// <summary>The choice to pre-select, or none; <see cref="Message"/> says why (always, so the tester knows what was and was not decided).</summary>
    public sealed record Recommendation(SourceSchemaFile? Selected, string Message, bool Ambiguous, IReadOnlyList<SourceSchemaFile> Tied);

    public static int Score(SourceSchemaFile file)
    {
        var kinds = (file.Usage?.References ?? []).Select(r => r.Kind).ToHashSet(StringComparer.Ordinal);
        var score = 0;
        if (kinds.Contains("Validator")) score += 100;
        if (kinds.Contains("Validator (schema registry)")) score += 60;
        if (kinds.Contains("Configuration reference")) score += 40;
        if (kinds.Contains("Code reference")) score += 25;
        if (kinds.Contains("Embedded resource")) score += 10;
        if (kinds.Contains("Test code reference")) score += 20;
        if (kinds.Contains("Schema registry entry")) score += 5;
        return score;
    }

    public static string TierOf(SourceSchemaFile file) => file.Usage?.Tier ?? DeclaredTier;

    public static string TierLabel(string tier) => tier switch
    {
        ValidationTier => "Referenced by validation code",
        SourceTier => "Referenced in source (configuration, code or tests)",
        _ => "Declared in source only",
    };

    /// <summary>All schemas, strongest evidence first; equal evidence by logical family, newest version first, then path.</summary>
    public static IReadOnlyList<RankedSchema> Rank(IEnumerable<SourceSchemaFile> files) => [.. files
        .Select(f => new RankedSchema(f, Score(f), TierOf(f)))
        .OrderByDescending(r => r.Score)
        .ThenBy(r => Family(r.File.Path), StringComparer.OrdinalIgnoreCase)
        .ThenByDescending(r => VersionKey(r.File))
        .ThenBy(r => r.File.Path, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// An XSD of the snapshot a person bound to an integration (Integrations → contract, or a message flow's XML Schema contract): the Source
    /// Analysis contract id ("xsd:&lt;path&gt;") and who uses it. A person's choice for this snapshot, so it outranks static source evidence.
    /// </summary>
    public sealed record BoundSchema(string SourceContractId, string UsedBy);

    /// <summary>Whether a Source Analysis XSD contract id names this file (the id is "xsd:" + the path, with unusual characters made safe).</summary>
    public static bool Names(string sourceContractId, SourceSchemaFile file)
    {
        if (!sourceContractId.StartsWith("xsd:", StringComparison.Ordinal)) return false;
        static string Norm(string path) => path.Replace('\\', '/').TrimStart('/');
        var named = Norm(sourceContractId[4..]);
        return string.Equals(named, Norm(file.Path), StringComparison.OrdinalIgnoreCase)
            || string.Equals(named, UnsafeCharacters().Replace(Norm(file.Path), "_"), StringComparison.OrdinalIgnoreCase);
    }

    public static Recommendation Recommend(IReadOnlyList<SourceSchemaFile> files, IReadOnlyList<BoundSchema>? bound = null)
    {
        var ranked = Rank(files.Where(f => f.Path.EndsWith(".xsd", StringComparison.OrdinalIgnoreCase)));
        if (ranked.Count == 0) return new(null, "This snapshot contains no XSD files.", false, []);
        // A schema bound to an integration of this very snapshot is what the tester already chose to test with: pre-select it when it is the
        // only one; several bound schemas are a tie the tester decides (never the first of them).
        var used = ranked.Select(r => (r.File, By: (bound ?? []).Where(b => Names(b.SourceContractId, r.File)).Select(b => b.UsedBy).Distinct(StringComparer.Ordinal).ToList()))
            .Where(x => x.By.Count > 0).ToList();
        if (used.Count == 1)
        {
            var (file, by) = used[0];
            return new(file, $"Pre-selected {FileName(file.Path)}{(VersionOf(file) is { } bv ? $" (version {bv})" : "")}: {string.Join(", ", by)} "
                + $"{(by.Count == 1 ? "uses" : "use")} it for this snapshot (chosen in Integrations). Source evidence: {TierLabel(TierOf(file)).ToLowerInvariant()}. "
                + (file.Usage?.RuntimeVerification ?? "Not verified at runtime."), false, []);
        }
        if (used.Count > 1)
            return new(null, $"{used.Count} schemas of this snapshot are bound to integrations, so none is pre-selected: "
                + string.Join("; ", used.Take(4).Select(u => $"{FileName(u.File.Path)} ({string.Join(", ", u.By)})")) + (used.Count > 4 ? " …" : "") + ".", true, [.. used.Select(u => u.File)]);
        var best = ranked[0];
        if (best.Score == 0)
            return new(null, "No XSD in this snapshot is named by validation code, configuration or tests, so none is pre-selected. Choose the schema to work on.", false, []);
        var tied = ranked.Where(r => r.Score == best.Score).Select(r => r.File).ToList();
        if (tied.Count > 1)
            return new(null, $"{tied.Count} schemas have equally strong source evidence ({TierLabel(best.Tier).ToLowerInvariant()}), so none is pre-selected: "
                + string.Join(", ", tied.Take(4).Select(f => FileName(f.Path))) + (tied.Count > 4 ? " …" : "") + ".", true, tied);
        var usage = best.File.Usage;
        var others = ranked.Skip(1).Where(r => r.Tier == ValidationTier).Select(r => FileName(r.File.Path)).Take(3).ToList();
        var message = $"Pre-selected {FileName(best.File.Path)}{(VersionOf(best.File) is { } v ? $" (version {v})" : "")}: {TierLabel(best.Tier).ToLowerInvariant()} — {usage?.TierBasis}."
            + (others.Count > 0 ? $" Also referenced by validation code: {string.Join(", ", others)}." : "")
            + $" {usage?.RuntimeVerification ?? "Not verified at runtime."}";
        return new(best.File, message, false, []);
    }

    /// <summary>The logical family of a schema file: its name without version, variant and extension (bufdir.barnevern.tiltak.skolenaervaer).</summary>
    public static string Family(string path)
    {
        var name = FileName(path);
        name = XsdExtension().Replace(name, "");
        name = VersionSuffix().Replace(name, "");
        name = VariantSuffix().Replace(name, "");
        return name;
    }

    /// <summary>"flat" / "altinn" / "standard": flat and non-flat variants of the same version are different schemas.</summary>
    public static string Variant(string path)
    {
        var name = FileName(path).ToLowerInvariant();
        return name.Contains(".flat") || name.Contains("_flat") ? "flat" : name.Contains("altinn") ? "altinn" : "standard";
    }

    public static string? VersionOf(SourceSchemaFile file) =>
        file.Usage?.Version ?? (VersionInName().Match(FileName(file.Path)) is { Success: true } m ? m.Groups[1].Value.Replace('_', '.') : null);

    private static Version VersionKey(SourceSchemaFile file) =>
        System.Version.TryParse(VersionOf(file) is { } v && v.Count(c => c == '.') >= 1 ? v : "0.0", out var parsed) ? parsed : new Version(0, 0);

    [GeneratedRegex(@"\.xsd$", RegexOptions.IgnoreCase)] private static partial Regex XsdExtension();
    // The characters Source Analysis keeps in a contract id's path (SourceEvidenceRedaction.SafePath); others become "_".
    [GeneratedRegex(@"[^\p{L}\p{N}_./<>\[\]{}*~^=@$() `:+?,\-#!|]")] private static partial Regex UnsafeCharacters();
    [GeneratedRegex(@"[._-]v?\d+([._]\d+)*([._-](flat|altinn|m2lb))*$", RegexOptions.IgnoreCase)] private static partial Regex VersionSuffix();
    [GeneratedRegex(@"[._-](flat|altinn|m2lb)$", RegexOptions.IgnoreCase)] private static partial Regex VariantSuffix();
    [GeneratedRegex(@"v(\d+(?:[._]\d+)+)", RegexOptions.IgnoreCase)] private static partial Regex VersionInName();

    private static string FileName(string path) => System.IO.Path.GetFileName(path.Replace('\\', '/'));
}
