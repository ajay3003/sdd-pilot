using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// SRC-001 (M2LB (9) source analysis, 2026-11-27): Testoversikt says which import its numbers come from — the archive, when it was imported and its
/// SHA-256 — and warns when a NEWER import of the same application exists. Before, a dashboard built from an older import (for example one
/// without the project's own specs/) looked current; nothing showed which source version was behind «Prosjektnivå 8 / 92».
/// Never switches the project by itself: the tester chooses the newer import.
/// </summary>
public static class ManualTestingSourceBaseline
{
    public sealed record Baseline(SddProjectImportRecord Current, IReadOnlyList<SddProjectImportRecord> Newer)
    {
        public string Text => $"{Current.ArchiveFileName} · importert {Current.ImportedAt.ToLocalTime():dd.MM.yyyy HH:mm}" +
            (Current.ArchiveSha256.Length >= 12 ? $" · SHA-256 {Current.ArchiveSha256[..12]}" : "");
        public string? Warning => Newer.Count == 0 ? null
            : $"En nyere import av samme applikasjon finnes: {Newer[0].ArchiveFileName} (importert {Newer[0].ImportedAt.ToLocalTime():dd.MM.yyyy HH:mm})" +
              (Newer.Count > 1 ? $" og {Newer.Count - 1} til" : "") + ". Tallene her bygger på den eldre importen. Velg den nyere importen som prosjekt for å se gjeldende kilde.";
    }

    /// <summary>The import behind <paramref name="projectKey"/> («import:{id}») and the newer imports of the same application (newest first).
    /// Null when the project is not an import (no source version is known to show). An import without a recorded application is never compared.</summary>
    public static Baseline? Of(string? projectKey, IReadOnlyCollection<SddProjectImportRecord> imports)
    {
        if (projectKey is not { } key || !key.StartsWith("import:", StringComparison.Ordinal)) return null;
        var id = key["import:".Length..];
        if (imports.FirstOrDefault(i => string.Equals(i.ImportId, id, StringComparison.Ordinal)) is not { } current) return null;
        var newer = current.ApplicationId is { Length: > 0 } app
            ? imports.Where(i => i.ImportId != current.ImportId && string.Equals(i.ApplicationId, app, StringComparison.Ordinal) && i.ImportedAt > current.ImportedAt)
                .OrderByDescending(i => i.ImportedAt).ToList()
            : [];
        return new Baseline(current, newer);
    }
}
