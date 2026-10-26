using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public enum AnalysisSessionStatus
{
    NotRun,
    Running,
    Completed,
    Failed
}

public sealed class QualityReviewSessionService
{
    private readonly List<string> _selectedPackIds = [];
    private readonly Dictionary<WorkspaceArtifactKind, string> _artifactSnapshot = [];

    public AnalysisSessionStatus Status { get; private set; } = AnalysisSessionStatus.NotRun;
    public QualityReviewReport? Report { get; private set; }
    public string? ProjectName { get; private set; }
    public DateTimeOffset? RunTimestamp { get; private set; }
    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<string> SelectedPackIds => _selectedPackIds;
    /// <summary>The exact inputs of the run (pack, scope, path, fingerprint) — kept with the result, so a later selection never rewrites it.</summary>
    public IReadOnlyList<SddReviewInput> ReviewInputs { get; private set; } = [];
    public IReadOnlyDictionary<WorkspaceArtifactKind, string> ArtifactSnapshot => _artifactSnapshot;
    public bool HasResult => Report is not null;

    /// <summary>
    /// Whether a saved result describes the inputs viewed now: the same role, artifact and content fingerprint for each input. A result for
    /// another artifact pack (or changed documents) is never shown as current. A result saved without input records cannot be compared: it is
    /// restored as before only outside Artifact Packs — never for a viewed pack, which must match its own inputs.
    /// </summary>
    public static bool InputsMatch(IReadOnlyList<SddReviewInput> saved, IReadOnlyList<SddReviewInput> current) =>
        saved.Count == 0 ? current.All(i => i.Pack is null) : saved.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(current.Select(Key).Order(StringComparer.Ordinal));

    private static string Key(SddReviewInput input) => $"{input.Role}|{input.ArtifactId}|{input.Fingerprint}";

    public void MarkRunning(
        IEnumerable<string> selectedPackIds,
        string? projectName,
        IReadOnlyDictionary<WorkspaceArtifactKind, string> artifactSnapshot,
        IReadOnlyList<SddReviewInput>? inputs = null)
    {
        Status = AnalysisSessionStatus.Running;
        ReviewInputs = inputs ?? [];
        Report = null;
        ProjectName = projectName;
        RunTimestamp = DateTimeOffset.UtcNow;
        ErrorMessage = null;
        ReplaceSelectedPacks(selectedPackIds);
        ReplaceArtifactSnapshot(artifactSnapshot);
    }

    public void SaveResult(
        QualityReviewReport report,
        IEnumerable<string> selectedPackIds,
        string? projectName,
        IReadOnlyDictionary<WorkspaceArtifactKind, string> artifactSnapshot,
        IReadOnlyList<SddReviewInput>? inputs = null)
    {
        Status = AnalysisSessionStatus.Completed;
        ReviewInputs = inputs ?? ReviewInputs;
        Report = report;
        ProjectName = projectName;
        RunTimestamp = report.RunAt;
        ErrorMessage = null;
        ReplaceSelectedPacks(selectedPackIds);
        ReplaceArtifactSnapshot(artifactSnapshot);
    }

    public void MarkFailed(
        IEnumerable<string> selectedPackIds,
        string? projectName,
        IReadOnlyDictionary<WorkspaceArtifactKind, string> artifactSnapshot,
        string errorMessage)
    {
        Status = AnalysisSessionStatus.Failed;
        Report = null;
        ProjectName = projectName;
        RunTimestamp = DateTimeOffset.UtcNow;
        ErrorMessage = errorMessage;
        ReplaceSelectedPacks(selectedPackIds);
        ReplaceArtifactSnapshot(artifactSnapshot);
    }

    public void Clear()
    {
        Status = AnalysisSessionStatus.NotRun;
        Report = null;
        ProjectName = null;
        RunTimestamp = null;
        ErrorMessage = null;
        ReviewInputs = [];
        _selectedPackIds.Clear();
        _artifactSnapshot.Clear();
    }

    private void ReplaceSelectedPacks(IEnumerable<string> selectedPackIds)
    {
        _selectedPackIds.Clear();
        _selectedPackIds.AddRange(selectedPackIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private void ReplaceArtifactSnapshot(IReadOnlyDictionary<WorkspaceArtifactKind, string> artifactSnapshot)
    {
        _artifactSnapshot.Clear();
        foreach (var (kind, text) in artifactSnapshot)
        {
            if (!string.IsNullOrWhiteSpace(text))
                _artifactSnapshot[kind] = text;
        }
    }
}
