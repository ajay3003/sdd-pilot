using BirkNext.DatabaseArchitecture;
using BirkNext.SourceArchitecture;

namespace BirkNext.Web.Services;

public sealed record EvidenceGroup(string Label, int Count);

/// <summary>Shared wording for the source-only boundary: short on screen, full text in the details.</summary>
public static class SourceBoundary
{
    public const string Architecture = "Source-derived only — deployment/runtime not verified.";
    public const string Database = "Source-derived only — deployed schema not verified.";
    public const string ChangesNotDrift = "Source comparison reflects source declarations only. It is not deployment drift and says nothing about runtime traffic.";
    public static string ShortFingerprint(string fingerprint) => SourceAnalysisOverview.ShortFingerprint(fingerprint);
}

/// <summary>
/// Architecture evidence summaries, counted from the snapshot itself (no re-analysis, no text classification):
/// every dependency has exactly ONE evidence state, so the state counts add up to the runtime-dependency total; whether the target was
/// identified is a separate dimension (an Unresolved-state dependency can still point to an unresolved channel node). Unresolved evidence
/// is grouped by the extractor-assigned diagnostic kind.
/// </summary>
public static class ArchitectureEvidenceSummary
{
    public sealed record DependencyEvidence(int All, int Runtime, int NonRuntime, int Confirmed, int StronglySupported, int Inferred, int Unresolved, int Conflict, int TargetNotIdentified)
    {
        public int StateTotal => Confirmed + StronglySupported + Inferred + Unresolved + Conflict;
    }

    public static DependencyEvidence Dependencies(ArchitectureSnapshot a)
    {
        var runtime = a.Dependencies.Where(d => ArchitecturePresentation.IsRuntime(d.DependencyType)).ToList();
        int Count(ArchitectureEvidenceState s) => runtime.Count(d => d.EvidenceState == s);
        return new(a.Dependencies.Count, runtime.Count, a.Dependencies.Count - runtime.Count, Count(ArchitectureEvidenceState.Confirmed), Count(ArchitectureEvidenceState.StronglySupported),
            Count(ArchitectureEvidenceState.Inferred), Count(ArchitectureEvidenceState.Unresolved), Count(ArchitectureEvidenceState.Conflict), a.UnresolvedItems.Count());
    }

    /// <summary>Unresolved evidence (extractor diagnostics) grouped by its kind, largest first.</summary>
    public static IReadOnlyList<EvidenceGroup> Groups(ArchitectureSnapshot a) =>
        a.Diagnostics.GroupBy(d => d.Kind).Select(g => new EvidenceGroup(g.Key, g.Count())).OrderByDescending(g => g.Count).ThenBy(g => g.Label, StringComparer.Ordinal).ToList();

    /// <summary>The model element a diagnostic is about (its subject id up to the first '|'), or null.</summary>
    public static string? SubjectId(ArchitectureDiagnostic d) => d.SubjectId is { Length: > 0 } s ? s.Split('|')[0] : null;

    /// <summary>Technologies with the most components first (stable by name), so the first few on screen are the important ones.</summary>
    public static IReadOnlyList<ArchitectureTechnology> Technologies(ArchitectureSnapshot a) =>
        a.Technologies.OrderByDescending(t => t.Components.Count).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
}

/// <summary>Database evidence summaries from the snapshot's own facts: relationship states (one per relationship), unresolved evidence by type.</summary>
public static class DatabaseEvidenceSummary
{
    public static string StateLabel(DatabaseEvidenceState state) => state switch
    {
        DatabaseEvidenceState.ObservedFromMigration => "Observed from migration",
        DatabaseEvidenceState.ObservedFromDDL => "Observed from DDL",
        _ => state.ToString(),
    };

    public static List<TableModel> Tables(DatabaseArchitectureSnapshot d) => d.Databases.SelectMany(x => x.Schemas.SelectMany(s => s.Tables)).ToList();

    /// <summary>Each relationship has exactly one evidence state; these counts add up to the relationship total. States without relationships are omitted.</summary>
    public static IReadOnlyList<(DatabaseEvidenceState State, int Count)> RelationshipStates(DatabaseArchitectureSnapshot d)
    {
        var relationships = Tables(d).SelectMany(t => t.Relationships).ToList();
        return Enum.GetValues<DatabaseEvidenceState>().Select(s => (s, relationships.Count(r => r.EvidenceState == s))).Where(x => x.Item2 > 0).ToList();
    }

    public static IReadOnlyList<EvidenceGroup> UnresolvedGroups(DatabaseArchitectureSnapshot d) =>
        d.UnresolvedEvidence.GroupBy(e => e.Type).Select(g => new EvidenceGroup(g.Key, g.Count())).OrderByDescending(g => g.Count).ThenBy(g => g.Label, StringComparer.Ordinal).ToList();
}
