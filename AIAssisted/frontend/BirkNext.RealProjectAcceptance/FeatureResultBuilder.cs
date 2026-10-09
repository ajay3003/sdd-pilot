namespace BirkNext.RealProjectAcceptance;

/// <summary>Small helper features use to record observations and findings without repeating record plumbing.</summary>
public sealed class FeatureResultBuilder
{
    public ExecutionStatus Execution { get; set; } = ExecutionStatus.Completed;
    public EvidenceState Evidence { get; set; } = EvidenceState.Verified;
    public DataState Data { get; set; } = DataState.NotAssessed;
    public ProvenanceState Provenance { get; set; } = ProvenanceState.NotApplicable;
    public ExportState Export { get; set; } = ExportState.NotAttempted;
    public BrowserState Browser { get; set; } = BrowserState.NotUsed;
    public Dictionary<string, string> Observations { get; } = new(StringComparer.Ordinal);
    public List<string> Facts { get; } = [];
    public List<AcceptanceFinding> Findings { get; } = [];
    public List<string> Notes { get; } = [];
    public List<string> Screenshots { get; } = [];

    public FeatureResultBuilder Observe(string key, object? value) { Observations[key] = value?.ToString() ?? "—"; return this; }
    public FeatureResultBuilder Fact(params string[] facts) { Facts.AddRange(facts.Where(f => !string.IsNullOrWhiteSpace(f))); return this; }
    public FeatureResultBuilder Defect(string code, string message) { Findings.Add(new(AcceptanceFindingSeverity.Defect, code, message)); return this; }
    public FeatureResultBuilder Warn(string code, string message) { Findings.Add(new(AcceptanceFindingSeverity.Warning, code, message)); return this; }
    public FeatureResultBuilder Note(string note) { Notes.Add(note); return this; }

    /// <summary>Real data when the count is positive; otherwise the supplied empty state (never "zero means pass").</summary>
    public FeatureResultBuilder DataFromCount(int count, DataState whenEmpty = DataState.NoApplicableData)
    {
        Data = count > 0 ? DataState.RealDataObserved : whenEmpty;
        return this;
    }

    public FeatureAcceptanceResult Build() => new()
    {
        ExecutionStatus = Execution, EvidenceState = Evidence, DataState = Data, ProvenanceState = Provenance, ExportState = Export, BrowserState = Browser,
        Observations = new(Observations, StringComparer.Ordinal), Facts = [.. Facts], Findings = [.. Findings], Notes = [.. Notes], Screenshots = [.. Screenshots],
    };
}
