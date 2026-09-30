using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Pure presentation of a source snapshot's integration path. Field names and contracts only — never a value. Developer tests are shown as
/// existing evidence (never "passed"); runtime delivery and subscriber processing always stay "Not assessed" here.
/// </summary>
public static class IqrPathPresentation
{
    public sealed record FieldRow(string Field, bool Sensitive, string Adapter, string Ingestion, string Domain, string Events, string Transformation, string Coverage, string Gap, string Minimization);
    public sealed record HopRow(string From, string To, string Mechanism, string Source, string Developer, string Runtime, string Note);

    public static string Stage(SourceStageKind kind) => kind switch
    {
        SourceStageKind.CdcField => "CDC field",
        SourceStageKind.AdapterModel => "Adapter model",
        SourceStageKind.IngestionRequest => "Ingestion request",
        SourceStageKind.IngestionDto => "Ingestion DTO",
        SourceStageKind.DomainEntity => "Domain entity",
        SourceStageKind.DomainEvent => "Domain event",
        SourceStageKind.EventEnvelope => "Event envelope",
        SourceStageKind.Outbox => "Outbox",
        SourceStageKind.ServiceBus => "Service Bus",
        _ => "Other",
    };

    public static string Transformation(FieldTransformation t) => t switch
    {
        FieldTransformation.PassThrough => "Pass-through",
        FieldTransformation.Booleanized => "Booleanized (flag only)",
        FieldTransformation.MetadataOnly => "Metadata only (field name)",
        FieldTransformation.FilteredIntentionally => "Filtered intentionally",
        FieldTransformation.NotResolved => "Not resolved",
        var other => other.ToString(),
    };

    /// <summary>"Developer test exists (2: contract, integration)" / "Source trace only" — never "passed".</summary>
    public static string Coverage(IEnumerable<SourceCoverageStatus> statuses, int tests)
    {
        var list = statuses.ToList();
        var layers = list.Select(s => s switch
        {
            SourceCoverageStatus.DeveloperUnitCovered => "unit",
            SourceCoverageStatus.DeveloperIntegrationCovered => "integration",
            SourceCoverageStatus.DeveloperContractCovered => "contract",
            _ => null,
        }).OfType<string>().Distinct().ToList();
        return layers.Count > 0 ? $"Developer test exists ({tests}: {string.Join(", ", layers)})" : "Source trace only";
    }

    private static string Steps(FieldTrace f, params SourceStageKind[] kinds)
    {
        var steps = f.Steps.Where(s => kinds.Contains(s.Stage)).ToList();
        if (steps.Count == 0) return "—";
        var shown = steps.Take(3).Select(s => $"{s.TypeName}.{s.Field}{(s.Transformation is FieldTransformation.PassThrough ? "" : $" ({Transformation(s.Transformation)})")}");
        return string.Join("; ", shown) + (steps.Count > 3 ? $" (+{steps.Count - 3})" : "");
    }

    /// <summary>The field's most telling transformation: reduction, filtering or loss before generic pass-through.</summary>
    public static string KeyTransformation(FieldTrace f)
    {
        if (f.Minimization == "Intentional reduction / metadata projection") return "Reduced to boolean/metadata before Service Bus";
        if (f.Minimization == "Potential data-minimization issue") return "Raw value emitted in an event";
        if (f.Steps.Any(s => s.Transformation == FieldTransformation.FilteredIntentionally)) return "Filtered intentionally";
        if (f.Steps.Any(s => s.Transformation == FieldTransformation.Dropped)) return "Dropped at a boundary (reason not resolved)";
        var order = new[] { FieldTransformation.Booleanized, FieldTransformation.MetadataOnly, FieldTransformation.Derived, FieldTransformation.Converted, FieldTransformation.Conditional, FieldTransformation.Defaulted, FieldTransformation.Renamed, FieldTransformation.PassThrough };
        var kind = order.FirstOrDefault(k => f.Steps.Any(s => s.Transformation == k), FieldTransformation.NotResolved);
        return f.Steps.Count == 0 ? "Not resolved" : Transformation(kind);
    }

    /// <summary>Sensitive fields first, then by name.</summary>
    public static IReadOnlyList<FieldRow> Fields(IntegrationPathEvidence path) => path.Fields
        .OrderByDescending(f => f.Sensitive).ThenBy(f => f.Key, StringComparer.Ordinal)
        .Select(f => new FieldRow(f.Key, f.Sensitive, Steps(f, SourceStageKind.AdapterModel), Steps(f, SourceStageKind.IngestionRequest, SourceStageKind.IngestionDto),
            Steps(f, SourceStageKind.DomainEntity), Steps(f, SourceStageKind.DomainEvent), KeyTransformation(f), Coverage(f.Coverage, f.DeveloperTestIds.Count),
            string.IsNullOrWhiteSpace(f.Gap) ? "None found at the source level" : f.Gap, f.Minimization)).ToList();

    public static IReadOnlyList<HopRow> Hops(IntegrationPathEvidence path) => path.Hops
        .Select(h => new HopRow(h.From, h.To, h.Mechanism, h.SourceState, Coverage(h.Coverage, h.DeveloperTestIds.Count), h.RuntimeState, h.Note)).ToList();

    /// <summary>The ordered chain of stage kinds present, e.g. "CDC field → Adapter model → … → Service Bus".</summary>
    public static string Chain(IntegrationPathEvidence path) =>
        string.Join(" → ", path.Stages.Select(s => s.Kind).Where(k => k != SourceStageKind.Other).Distinct().OrderBy(k => k).Select(Stage));

    public static string GapKind(SourceCoverageStatus kind) => kind switch
    {
        SourceCoverageStatus.CrossLayerGap => "Cross-layer gap",
        SourceCoverageStatus.RuntimeGap => "Runtime gap",
        SourceCoverageStatus.E2EGap => "E2E gap",
        SourceCoverageStatus.ManualVerification => "Manual verification",
        SourceCoverageStatus.SourceEvidenceOnly => "Source trace only",
        _ => kind.ToString(),
    };

    public static string TestName(IqrSourceSnapshot snapshot, string id) =>
        snapshot.Tests.FirstOrDefault(t => t.Id == id) is { } t ? $"{t.Class}.{t.Method} ({t.Layer.ToString().ToLowerInvariant()})" : "Developer test";
}
