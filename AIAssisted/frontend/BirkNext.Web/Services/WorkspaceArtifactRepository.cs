using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Shared in-memory repository for the active Studio session.
/// Cleared on browser refresh by design (no persistent storage required).
/// Registered as singleton for both IWorkspaceArtifactRepository and IWorkspaceSessionService.
/// </summary>
public sealed class WorkspaceArtifactRepository : IWorkspaceSessionService
{
    private readonly Dictionary<WorkspaceArtifactType, WorkspaceArtifact> _artifacts = new();
    public SddLifecycleState SddLifecycle { get; private set; } = new();

    public event EventHandler? ReviewContextRebuildNeeded;
    public event EventHandler? ProjectSelectionChanged;

    private string? _projectName;
    /// <summary>
    /// For Sample Projects: stores the CANONICAL LOWERCASE SLUG (e.g., "autorisasjon").
    /// NOT the display name ("Autorisasjon"). Used for identity-only persistence and restoration.
    /// Fires ProjectSelectionChanged to trigger auto-save when changed.
    /// </summary>
    public string? ProjectName
    {
        get => _projectName;
        set
        {
            if (_projectName != value)
            {
                _projectName = value;
                // Fire ProjectSelectionChanged so AutoSave persists the new project identity
                ProjectSelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Convenience alias for ProjectName. Represents the canonical project identifier (slug for Sample Projects).
    /// </summary>
    public string? CurrentProject
    {
        get => ProjectName;
        set => ProjectName = value;
    }

    // ── IWorkspaceSessionService convenience properties ───────────────────────

    public WorkspaceArtifact? Constitution => Get(WorkspaceArtifactType.Constitution);
    public WorkspaceArtifact? Specification => Get(WorkspaceArtifactType.Specification);
    public WorkspaceArtifact? Plan         => Get(WorkspaceArtifactType.Plan);
    public WorkspaceArtifact? Tasks        => Get(WorkspaceArtifactType.Tasks);
    public WorkspaceArtifact? DataModel    => Get(WorkspaceArtifactType.DataModel);

    // ── IWorkspaceArtifactRepository (WorkspaceArtifactType) ─────────────────

    public void Set(WorkspaceArtifactType type, string text,
                    string? fileName = null, string? sourcePath = null, DateTime? lastModified = null)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            CaptureRevision(type, text, fileName, sourcePath);
            var hash = RuntimeHelpers.GetHashCode(this);
            System.Diagnostics.Debug.WriteLine($"DIAG: [Repository] Set({type}) hash={hash}");
            _artifacts[type] = new WorkspaceArtifact(text, DateTime.UtcNow, fileName, sourcePath, lastModified);
        }
    }

    public WorkspaceArtifact? Get(WorkspaceArtifactType type)
        => _artifacts.TryGetValue(type, out var a) ? a : null;

    public IReadOnlyList<SddArtifactRevision> GetArtifactRevisions(WorkspaceArtifactType type) =>
        SddLifecycle.Revisions.Where(x => x.Role == type.ToString())
            .OrderByDescending(x => x.IsCurrentSelection)
            .ThenByDescending(x => x.CapturedAt)
            .ToList();

    public WorkspaceArtifact? GetRevision(Guid revisionId)
    {
        var revision = SddLifecycle.Revisions.FirstOrDefault(x => x.RevisionId == revisionId);
        return revision is null ? null : new WorkspaceArtifact(revision.Content, revision.CapturedAt.UtcDateTime, revision.FileName, revision.SourceReference);
    }

    /// <summary>
    /// Makes the revision the current selection for its role within its workspace scope. Selection is not authority:
    /// the revision's authority and any baseline are unchanged. Only the manual workspace (no project) also replaces the
    /// role's session artifact, because a selected Sample Project's documents are resolved on demand, never copied.
    /// </summary>
    public void SelectRevision(Guid revisionId)
    {
        var revision = SddLifecycle.Revisions.FirstOrDefault(x => x.RevisionId == revisionId)
            ?? throw new InvalidOperationException("Artifact revision was not found.");
        if (!Enum.TryParse<WorkspaceArtifactType>(revision.Role, out var role))
            throw new InvalidOperationException("Artifact revision has an unsupported role.");
        foreach (var item in SddLifecycle.Revisions.Where(x => x.Role == revision.Role && x.WorkspaceScope == revision.WorkspaceScope))
            item.IsCurrentSelection = item.RevisionId == revisionId;
        if (revision.WorkspaceScope is null)
            _artifacts[role] = new WorkspaceArtifact(revision.Content, revision.CapturedAt.UtcDateTime, revision.FileName, revision.SourceReference);
        NotifyArtifactsChanged();
    }

    /// <summary>
    /// Adds imported content as an artifact revision of <paramref name="type"/> in <paramref name="workspaceScope"/>.
    /// Content already present for that role and scope reuses its revision. Revisions are numbered per document
    /// (scope, role and file name). Authority starts as Unknown: importing never approves or baselines anything.
    /// </summary>
    public SddArtifactRevision? AddArtifactRevision(WorkspaceArtifactType type, string text, string? fileName, string? sourcePath,
        string? workspaceScope, string? origin, bool select)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var role = type.ToString();
        var name = string.IsNullOrWhiteSpace(fileName) ? role : fileName.Trim();
        var fingerprint = ArtifactFingerprint.Compute(text);
        var revision = SddLifecycle.Revisions.FirstOrDefault(x => x.Role == role && x.WorkspaceScope == workspaceScope && x.Fingerprint == fingerprint);
        if (revision is null)
        {
            revision = new SddArtifactRevision
            {
                Role = role, FileName = name, SourceReference = sourcePath,
                Content = text, Fingerprint = fingerprint,
                Revision = SddLifecycle.Revisions
                    .Where(x => x.Role == role && x.WorkspaceScope == workspaceScope && string.Equals(x.FileName, name, StringComparison.Ordinal))
                    .Select(x => x.Revision).DefaultIfEmpty(0).Max() + 1,
                Authority = "Unknown", CapturedAt = DateTimeOffset.UtcNow,
                WorkspaceScope = workspaceScope, Origin = origin
            };
            SddLifecycle.Revisions.Add(revision);
        }
        if (select) SelectRevision(revision.RevisionId);
        return revision;
    }

    public bool Has(WorkspaceArtifactType type) => _artifacts.ContainsKey(type);

    public void Clear(WorkspaceArtifactType type)
    {
        _artifacts.Remove(type);
        foreach (var revision in SddLifecycle.Revisions.Where(x => x.Role == type.ToString() && x.WorkspaceScope is null && x.IsCurrentSelection))
            revision.IsCurrentSelection = false;
    }

    public IEnumerable<(WorkspaceArtifactType Type, WorkspaceArtifact Artifact)> GetAllArtifacts()
    {
        var hash = RuntimeHelpers.GetHashCode(this);
        var count = _artifacts.Count;
        System.Diagnostics.Debug.WriteLine($"DIAG: [Repository] GetAllArtifacts() hash={hash}, count={count}");
        return _artifacts.Select(kvp => (kvp.Key, kvp.Value));
    }

    // ── IWorkspaceSessionService (WorkspaceArtifactKind) ─────────────────────
    // WorkspaceArtifactKind and WorkspaceArtifactType share identical integer values,
    // so the cast is safe for all defined members.

    public void Set(WorkspaceArtifactKind kind, string text,
                    string? fileName = null, string? sourcePath = null, DateTime? lastModified = null)
        => Set((WorkspaceArtifactType)(int)kind, text, fileName, sourcePath, lastModified);

    public WorkspaceArtifact? Get(WorkspaceArtifactKind kind)
        => Get((WorkspaceArtifactType)(int)kind);

    public bool Has(WorkspaceArtifactKind kind) => Has((WorkspaceArtifactType)(int)kind);

    public void Clear(WorkspaceArtifactKind kind) => Clear((WorkspaceArtifactType)(int)kind);

    /// <summary>
    /// Clear all workspace state: project identity and all artifacts.
    /// Called by ApplicationRuntimeResetService after backend database reset.
    /// </summary>
    public void ClearAll()
    {
        ProjectName = null;
        _artifacts.Clear();
        SddLifecycle = new();
        NotifyArtifactsChanged();
    }

    public void RestoreSddLifecycle(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            SddLifecycle = JsonSerializer.Deserialize<SddLifecycleState>(json) ?? new();
            SddLifecycle.Revisions ??= [];
            SddLifecycle.Questions ??= [];
            SddLifecycle.Decisions ??= [];
            SddLifecycle.Links ??= [];
            SddLifecycle.ImplementationEvidence ??= [];
            SddLifecycle.TestEvidence ??= [];
            SddLifecycle.TestExecutions ??= [];
            SddLifecycle.SourceSnapshots ??= [];
            SddLifecycle.QualityReviewRuns ??= [];
            SddLifecycle.ReviewRuns ??= [];
            SddLifecycle.RequirementSnapshots ??= [];
            SddLifecycle.RequirementChanges ??= [];
            SddLifecycle.Baselines ??= [];
            SddLifecycle.ExplorerSelections ??= [];
            foreach (var evidence in SddLifecycle.ImplementationEvidence) evidence.TargetResolutions ??= [];
            foreach (var (type, artifact) in GetAllArtifacts())
                CaptureRevision(type, artifact.Text, artifact.FileName, artifact.SourcePath);
        }
        catch (JsonException)
        {
            SddLifecycle = new();
        }
    }

    public void ResetSddLifecycle() => SddLifecycle = new();

    public void SetArtifactAuthority(Guid revisionId, string authority)
    {
        var allowed = new[] { "Draft", "Candidate", "Baseline", "Approved", "Superseded", "RejectedAlternative", "Historical", "Unknown" };
        if (!allowed.Contains(authority, StringComparer.Ordinal)) throw new ArgumentException("Unsupported authority state.", nameof(authority));
        var revision = SddLifecycle.Revisions.FirstOrDefault(x => x.RevisionId == revisionId)
            ?? throw new InvalidOperationException("Artifact revision was not found.");
        if (authority == "Superseded" && revision.IsCurrentSelection)
            throw new InvalidOperationException("Select a replacement revision before superseding the current selection.");
        if (authority == "Baseline")
        {
            foreach (var other in SddLifecycle.Revisions.Where(x => x.Role == revision.Role && x.RevisionId != revisionId && x.Authority == "Baseline"))
                other.Authority = "Historical";
        }
        revision.Authority = authority;
        if (authority == "Superseded") revision.IsCurrentSelection = false;
    }

    /// <summary>Captures the exact revisions explicitly marked Baseline. New drafts/selections cannot alter this manifest.</summary>
    public SddBaselineManifest CaptureBaseline(string label)
    {
        var ids = SddLifecycle.Revisions.Where(x => x.Authority == "Baseline").Select(x => x.RevisionId).Order().ToList();
        if (ids.Count == 0) throw new InvalidOperationException("Mark at least one artifact revision as Baseline before capturing a baseline.");
        var now = DateTimeOffset.UtcNow;
        foreach (var prior in SddLifecycle.Baselines.Where(x => x.Status == "Current")) { prior.Status = "Historical"; prior.SupersededAt = now; }
        var manifest = new SddBaselineManifest { Label = string.IsNullOrWhiteSpace(label) ? "Baseline" : label.Trim(), ArtifactRevisionIds = ids, CreatedAt = now };
        SddLifecycle.Baselines.Add(manifest);
        ProjectSelectionChanged?.Invoke(this, EventArgs.Empty);
        return manifest;
    }

    private void CaptureRevision(WorkspaceArtifactType type, string text, string? fileName, string? sourcePath)
    {
        var fingerprint = ArtifactFingerprint.Compute(text);
        var role = type.ToString();
        // Set() is the manual workspace's session artifact, so it captures into the unscoped (no project) revisions only.
        var current = SddLifecycle.Revisions.FirstOrDefault(x => x.Role == role && x.WorkspaceScope is null && x.IsCurrentSelection);
        if (current?.Fingerprint == fingerprint) return;

        if (current is not null) current.IsCurrentSelection = false;
        // Content seen before (switching A → B → A) re-selects its existing revision instead of storing another full copy: revisions are captured
        // for new fingerprints only, so repeated workspace switching cannot grow the lifecycle (memory and persisted JSON) without bound.
        var existing = SddLifecycle.Revisions.LastOrDefault(x => x.Role == role && x.WorkspaceScope is null && x.Fingerprint == fingerprint);
        if (existing is not null) { existing.IsCurrentSelection = true; return; }
        SddLifecycle.Revisions.Add(new SddArtifactRevision
        {
            Role = role,
            FileName = fileName ?? role,
            SourceReference = sourcePath,
            Content = text,
            Fingerprint = fingerprint,
            Revision = SddLifecycle.Revisions.Where(x => x.Role == role).Select(x => x.Revision).DefaultIfEmpty(0).Max() + 1,
            IsCurrentSelection = true,
            CapturedAt = DateTimeOffset.UtcNow
        });
    }

    public void NotifyArtifactsChanged()
    {
        ReviewContextRebuildNeeded?.Invoke(this, EventArgs.Empty);
    }
}
