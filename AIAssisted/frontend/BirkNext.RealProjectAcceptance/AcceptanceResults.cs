using System.Text.Json.Serialization;

namespace BirkNext.RealProjectAcceptance;

/// <summary>Did the feature run? Never a quality verdict.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionStatus { Completed, Partial, Blocked, Failed, NotApplicable, NotAvailable }

/// <summary>How much of the feature's own claim was verified against real evidence. NotVerified is a truthful outcome, not a failure.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceState { Verified, PartiallyVerified, NotVerified, NotApplicable }

/// <summary>What the feature observed. Unavailable is never zero; no applicable data is never a pass.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataState { RealDataObserved, NoApplicableData, NotAssessed, Unsupported }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProvenanceState { Traced, PartiallyTraced, Missing, NotApplicable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExportState { Exported, Failed, NotAttempted, NotSupported }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserState { Rendered, Failed, NotUsed }

/// <summary>Defect = a true BirkNext defect (fails the run). Warning/Info are recorded without failing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AcceptanceFindingSeverity { Defect, Warning, Info }

public sealed record AcceptanceFinding(AcceptanceFindingSeverity Severity, string Code, string Message);

/// <summary>Summary bucket of one feature (the report counts these; there is no overall quality percentage).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AcceptanceSummaryCategory { Completed, Partial, NotApplicable, NotVerified, Blocked, Failed }

public sealed record FeatureAcceptanceResult
{
    public string FeatureId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Area { get; init; } = "";
    public string Route { get; init; } = "";
    public AcceptanceType AcceptanceType { get; init; }
    public ExecutionStatus ExecutionStatus { get; init; } = ExecutionStatus.NotAvailable;
    public EvidenceState EvidenceState { get; init; } = EvidenceState.NotVerified;
    public DataState DataState { get; init; } = DataState.NotAssessed;
    public ProvenanceState ProvenanceState { get; init; } = ProvenanceState.NotApplicable;
    public ExportState ExportState { get; init; } = ExportState.NotAttempted;
    public BrowserState BrowserState { get; init; } = BrowserState.NotUsed;
    /// <summary>Observed safe facts (counts, names, states) — never source excerpts, secrets or personal data.</summary>
    public Dictionary<string, string> Observations { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Technology/contract/artifact names the feature observed, for semantic expectations.</summary>
    public List<string> Facts { get; init; } = [];
    public List<AcceptanceFinding> Findings { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    public List<string> Screenshots { get; init; } = [];
    public TimeSpan Duration { get; init; }

    [JsonIgnore] public bool HasDefect => Findings.Any(f => f.Severity == AcceptanceFindingSeverity.Defect);
    [JsonIgnore] public IEnumerable<AcceptanceFinding> Warnings => Findings.Where(f => f.Severity == AcceptanceFindingSeverity.Warning);

    public AcceptanceSummaryCategory Category =>
        HasDefect || ExecutionStatus == ExecutionStatus.Failed ? AcceptanceSummaryCategory.Failed
        : ExecutionStatus == ExecutionStatus.Blocked ? AcceptanceSummaryCategory.Blocked
        : ExecutionStatus == ExecutionStatus.NotApplicable || EvidenceState == EvidenceState.NotApplicable ? AcceptanceSummaryCategory.NotApplicable
        : EvidenceState == EvidenceState.NotVerified || ExecutionStatus == ExecutionStatus.NotAvailable ? AcceptanceSummaryCategory.NotVerified
        : ExecutionStatus == ExecutionStatus.Partial || EvidenceState == EvidenceState.PartiallyVerified ? AcceptanceSummaryCategory.Partial
        : AcceptanceSummaryCategory.Completed;
}

/// <summary>The identity every feature must agree on: one import, one workspace, one source snapshot per run.</summary>
public sealed record WorkspaceIdentity
{
    public string? ProjectName { get; init; }
    public string? WorkspaceId { get; init; }
    public string? ImportId { get; init; }
    public string? SourceSnapshotId { get; init; }
    public string? ArchiveSha256 { get; init; }
    public int? ImportedDocuments { get; init; }
    public bool SourceDetected { get; init; }
}

public sealed record RealProjectAcceptanceResult
{
    public string DatasetId { get; init; } = "";
    public string DatasetDisplayName { get; init; } = "";
    public ArchiveFingerprint? Archive { get; init; }
    public bool HashMatched { get; init; }
    public DatasetPreparationState PreparationState { get; init; }
    public string PreparationMessage { get; init; } = "";
    public AcceptanceMode Mode { get; init; }
    public string? BirkNextCommit { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public WorkspaceIdentity Workspace { get; init; } = new();
    public bool RuntimeProfileConfigured { get; init; }
    public bool ActiveSendAllowed { get; init; }
    public List<FeatureAcceptanceResult> Features { get; init; } = [];
    public List<AcceptanceFinding> RunFindings { get; init; } = [];

    public Dictionary<AcceptanceSummaryCategory, int> Summary =>
        Enum.GetValues<AcceptanceSummaryCategory>().ToDictionary(c => c, c => Features.Count(f => f.Category == c));

    /// <summary>The run fails only for true defects; NotVerified/NotApplicable/unsupported are truthful outcomes.</summary>
    [JsonIgnore] public bool Succeeded => PreparationState == DatasetPreparationState.Ready && !RunFindings.Any(f => f.Severity == AcceptanceFindingSeverity.Defect) && Features.All(f => f.Category != AcceptanceSummaryCategory.Failed);

    [JsonIgnore] public IEnumerable<(string FeatureId, AcceptanceFinding Finding)> Defects =>
        RunFindings.Where(f => f.Severity == AcceptanceFindingSeverity.Defect).Select(f => ("run", f))
            .Concat(Features.SelectMany(r => r.Findings.Where(f => f.Severity == AcceptanceFindingSeverity.Defect).Select(f => (r.FeatureId, f))));
}
