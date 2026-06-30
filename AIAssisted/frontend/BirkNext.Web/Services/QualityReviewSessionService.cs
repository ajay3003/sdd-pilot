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
    public IReadOnlyDictionary<WorkspaceArtifactKind, string> ArtifactSnapshot => _artifactSnapshot;
    public bool HasResult => Report is not null;

    public void MarkRunning(
        IEnumerable<string> selectedPackIds,
        string? projectName,
        IReadOnlyDictionary<WorkspaceArtifactKind, string> artifactSnapshot)
    {
        Status = AnalysisSessionStatus.Running;
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
        IReadOnlyDictionary<WorkspaceArtifactKind, string> artifactSnapshot)
    {
        Status = AnalysisSessionStatus.Completed;
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
