using BirkNext.DatabaseArchitecture;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;

namespace BirkNext.Web.Services;

public sealed record SourceAreaMetric(string Label, int Value);

/// <summary>One landing-page card. Counts come from that area's own model; an area that was not analyzed or is unsupported has no counts.</summary>
public sealed record SourceAreaCard(string Area, string Title, string Description, string Status, string Tone, string? Reason,
    IReadOnlyList<SourceAreaMetric> Metrics, string Action, string Limitation, Guid? AnalysisId);

/// <summary>
/// Source Analysis landing page, read from one stored source snapshot only. Architecture (<see cref="ArchitectureSnapshot"/>) and Database
/// (<see cref="DatabaseArchitectureSnapshot"/>) stay separate models: each card is built from its own model of the SAME source snapshot.
/// Nothing is re-analyzed and nothing live is contacted. Status labels are the models' own — never Pass/Fail.
/// </summary>
public static class SourceAnalysisOverview
{
    public const string SafetyNote = "Source only: no live Azure, database, messaging or HTTP connection is used.";
    // The page carries the full source-only note; cards keep a short form (full text in the Architecture / Database areas).
    public const string ArchitectureCardLimitation = "Source-derived only · deployment/runtime not verified";
    public const string DatabaseCardLimitation = "Source-derived only · deployed schema not verified";

    /// <summary>What each area will provide once the uploaded archive is analyzed (shown while analyzing — never as zero counts).</summary>
    public static readonly IReadOnlyList<string> ArchitectureProvides = ["Components", "Dependencies", "Messaging channels", "Datastores", "External systems"];
    public static readonly IReadOnlyList<string> DatabaseProvides = ["Database candidates", "Tables / entities", "Relationships", "Indexes", "Schema changes"];

    public static string ShortFingerprint(string sha256) => string.IsNullOrEmpty(sha256) ? "Unknown" : sha256.Length <= 8 ? sha256 : sha256[..8] + "…";

    public static string OptionLabel(IqrSourceSnapshot s) => $"{s.Archive.FileName} · {Utc(s.AnalyzedAt)} · {ShortFingerprint(s.Archive.Sha256)}";

    public static string Utc(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") + " UTC";

    /// <summary>The snapshot's own source-evidence status (Failed means the analyzer found no production project in the archive).</summary>
    public static string SourceEvidence(SourceAnalysisStatus status) => status switch
    {
        SourceAnalysisStatus.Ready => "Ready",
        SourceAnalysisStatus.Partial => "Partial",
        _ => "No production project found",
    };

    public static string StatusLabel(ArchitectureStatus status) => status == ArchitectureStatus.NeedsReview ? "Needs review" : status.ToString();
    public static string StatusLabel(DatabaseAnalysisStatus status) => status == DatabaseAnalysisStatus.NeedsReview ? "Needs review" : status.ToString();

    private static string Tone(string status) => status switch { "Complete" => "complete", "Partial" or "Needs review" => "partial", _ => "muted" };

    public static SourceAreaCard Architecture(IqrSourceSnapshot source)
    {
        const string title = "Architecture", description = "Source-derived topology: components, dependencies, messaging and data stores.", action = "Open Architecture";
        if (source.Architecture is not { } a)
            return new("Architecture", title, description, "Not analyzed", "muted", "This source snapshot predates architecture extraction.", [], "View architecture details", ArchitectureCardLimitation, null);
        var status = StatusLabel(a.Status);
        if (a.Status == ArchitectureStatus.Unsupported)
            return new("Architecture", title, description, status, Tone(status), a.Limitations.FirstOrDefault(x => x != ArchitectureSnapshot.SourceLimitation) ?? "No supported project type was found in this source snapshot.", [], "View architecture details", ArchitectureCardLimitation, a.SnapshotId);
        var unresolved = a.UnresolvedItems.Count();
        // Counts are the structured metrics; no sentence repeats them.
        List<SourceAreaMetric> metrics =
        [
            new("Components", a.Components.Count), new("Dependencies", a.Dependencies.Count), new("Messaging channels", a.MessagingChannels.Count),
            new("Data stores", a.DataStores.Count), new("External systems", a.ExternalSystems.Count),
        ];
        if (unresolved > 0) metrics.Add(new("Unresolved dependencies", unresolved));
        return new("Architecture", title, description, status, Tone(status), null, metrics, action, ArchitectureCardLimitation, a.SnapshotId);
    }

    public static SourceAreaCard Observability(IqrSourceSnapshot source)
    {
        const string title = "Observability", description = "Source-derived correlation and tracing, logging quality and telemetry configuration.", action = "Open Observability";
        if (source.Observability is not { } o)
            return new("Observability", title, description, "Not analyzed", "muted", "This source snapshot predates observability analysis.", [], "View observability details", SourceObservabilityPresentation.CardLimitation, null);
        var status = StatusLabel(o.Status);
        if (o.Status == ArchitectureStatus.Unsupported)
            return new("Observability", title, description, status, Tone(status), o.UnsupportedEvidence.FirstOrDefault() ?? "No supported source language was found in this source snapshot.", [],
                "View observability details", SourceObservabilityPresentation.CardLimitation, o.Id);
        List<SourceAreaMetric> metrics =
        [
            new("Components with tracing", o.Correlation.ComponentsWithTracing), new("Correlation boundaries", o.Boundaries.Count),
            new("Log calls", SourceObservabilityPresentation.LogCalls(o)), new("Findings needing review", SourceObservabilityPresentation.NeedsReview(o)),
        ];
        if (o.Correlation.PropagationUnresolved > 0) metrics.Add(new("Unresolved boundaries", o.Correlation.PropagationUnresolved));
        return new("Observability", title, description, status, Tone(status), null, metrics, action, SourceObservabilityPresentation.CardLimitation, o.Id);
    }

    public static SourceAreaCard Database(IqrSourceSnapshot source)
    {
        const string title = "Database", description = "Source-derived database design: candidates, tables/entities, relationships and indexes.", action = "Open Database Diagram";
        if (source.DatabaseArchitecture is not { } d)
            return new("Database", title, description, "Not analyzed", "muted", "This source snapshot predates database extraction.", [], "View database details", DatabaseCardLimitation, null);
        var status = StatusLabel(d.Status);
        if (d.Status == DatabaseAnalysisStatus.Unsupported)
            return new("Database", title, description, status, Tone(status), d.Diagnostics.FirstOrDefault(x => x != DatabaseArchitectureSnapshot.SourceLimitation) ?? "No supported database declaration was found in this source snapshot.", [], "View database details", DatabaseCardLimitation, d.SnapshotId);
        // Same counting as the Database workspace overview.
        var tables = d.Databases.SelectMany(x => x.Schemas.SelectMany(s => s.Tables)).ToList();
        List<SourceAreaMetric> metrics =
        [
            new("Database candidates", d.Databases.Count), new("Tables / entities", tables.Count), new("Relationships", tables.Sum(t => t.Relationships.Count)),
            new("Indexes", tables.Sum(t => t.Indexes.Count)),
        ];
        if (d.UnresolvedEvidence.Count > 0) metrics.Add(new("Unresolved evidence", d.UnresolvedEvidence.Count));
        if (d.Conflicts.Count > 0) metrics.Add(new("Conflicts", d.Conflicts.Count));
        return new("Database", title, description, status, Tone(status), null, metrics, action, DatabaseCardLimitation, d.SnapshotId);
    }
}
