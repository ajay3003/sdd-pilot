using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>Tester-facing words for the Payload &amp; Mapping Flow. Every level keeps its own meaning; none is a pass or a fail by itself.</summary>
public static class PayloadFlowPresentation
{
    public static string Level(FlowStageEvidenceLevel level) => level switch
    {
        FlowStageEvidenceLevel.Expected => "Expected",
        FlowStageEvidenceLevel.Generated => "Generated",
        FlowStageEvidenceLevel.Submitted => "Submitted",
        FlowStageEvidenceLevel.Observed => "Observed",
        FlowStageEvidenceLevel.Confirmed => "Confirmed",
        FlowStageEvidenceLevel.NotObserved => "Not observed",
        FlowStageEvidenceLevel.NotEvaluated => "Not evaluated",
        FlowStageEvidenceLevel.Unavailable => "Unavailable",
        _ => "Failed",
    };

    public static string Meaning(FlowStageEvidenceLevel level) => level switch
    {
        FlowStageEvidenceLevel.Expected => "what the contract, configuration or design says should happen; nothing was observed",
        FlowStageEvidenceLevel.Generated => "built by BirkNext and not sent",
        FlowStageEvidenceLevel.Submitted => "sent and accepted by the receiving API",
        FlowStageEvidenceLevel.Observed => "seen at runtime by an evidence reader",
        FlowStageEvidenceLevel.Confirmed => "read back from the system itself",
        FlowStageEvidenceLevel.NotObserved => "no runtime evidence for this stage; this is not a failure",
        FlowStageEvidenceLevel.NotEvaluated => "this run did not get far enough to say",
        FlowStageEvidenceLevel.Unavailable => "the evidence source could not be reached",
        _ => "the stage answered with a failure",
    };

    public static string Mapping(PayloadMappingStatus status) => status switch
    {
        PayloadMappingStatus.NotObserved => "Not observed",
        _ => status.ToString(),
    };

    public static string Provenance(PayloadProvenance provenance) => provenance switch
    {
        PayloadProvenance.Observed => "Observed",
        PayloadProvenance.Configured => "Configured",
        PayloadProvenance.SourceDerived => "Source-derived",
        _ => "Expected / documented",
    };

    public sealed record DifferenceRow(string Path, string Expected, string Observed, string Kind);

    /// <summary>Field-level comparison by path, only when both sides exist. A changed value is reported, not judged.</summary>
    public static IReadOnlyList<DifferenceRow> Difference(IReadOnlyList<PayloadFieldValue> expected, IReadOnlyList<PayloadFieldValue> observed)
    {
        var rows = new List<DifferenceRow>();
        foreach (var field in expected)
        {
            var match = observed.FirstOrDefault(item => item.Path == field.Path);
            rows.Add(match is null ? new(field.Path, field.Value, "—", "Removed")
                : new(field.Path, field.Value, match.Value, string.Equals(field.Value, match.Value, StringComparison.Ordinal) ? "Unchanged" : "Changed"));
        }
        rows.AddRange(observed.Where(item => expected.All(field => field.Path != item.Path)).Select(item => new DifferenceRow(item.Path, "—", item.Value, "Added")));
        return rows;
    }
}
