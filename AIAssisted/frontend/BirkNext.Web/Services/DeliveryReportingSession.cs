using BirkNext.DeliveryReporting;

namespace BirkNext.Web.Services;

/// <summary>
/// Delivery Reporting work that must outlive the page (this browser tab): an import preview that finished while the page was not shown (the
/// staged snapshot stays valid on the backend for 30 minutes) is kept per reporting project, so returning offers it instead of making the user
/// download and parse the archive again. Previews are never committed from here; the user still confirms the import.
/// </summary>
public sealed class DeliveryReportingSession(TimeProvider? clock = null)
{
    public static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<Guid, (DeliveryImportPreview Preview, string? Note, DateTimeOffset At)> _pending = [];

    public event Action? Changed;

    public void SetPending(Guid projectId, DeliveryImportPreview preview, string? note)
    {
        lock (_pending) _pending[projectId] = (preview, note, _clock.GetUtcNow());
        Changed?.Invoke();
    }

    /// <summary>The unconfirmed preview of a project, unless it is older than the staging lifetime.</summary>
    public (DeliveryImportPreview Preview, string? Note)? Pending(Guid projectId)
    {
        lock (_pending)
        {
            if (!_pending.TryGetValue(projectId, out var p)) return null;
            if (_clock.GetUtcNow() - p.At > PreviewLifetime) { _pending.Remove(projectId); return null; }
            return (p.Preview, p.Note);
        }
    }

    private readonly Dictionary<Guid, DeliveryScopeProvenance> _scopeProvenance = [];

    /// <summary>
    /// The shared Scope Registry selection (with resolved revision ids) currently applied in the Functional Quality Scope view of a project, so a
    /// report generated in this browser tab records which scope revisions were selected. Null clears it. Provenance only: it never narrows a report.
    /// </summary>
    public void SetScopeProvenance(Guid projectId, DeliveryScopeProvenance? provenance)
    {
        lock (_scopeProvenance)
        {
            if (provenance is { Scopes.Count: > 0 }) _scopeProvenance[projectId] = provenance;
            else _scopeProvenance.Remove(projectId);
        }
        Changed?.Invoke();
    }

    public DeliveryScopeProvenance? ScopeProvenance(Guid projectId)
    {
        lock (_scopeProvenance) return _scopeProvenance.TryGetValue(projectId, out var p) ? p : null;
    }

    /// <summary>After a Delivery Management (or full) reset: every staged preview is gone on the backend, so none is offered again.</summary>
    public void ClearAll()
    {
        lock (_scopeProvenance) _scopeProvenance.Clear();
        lock (_pending) _pending.Clear();
        Changed?.Invoke();
    }

    public void Clear(Guid projectId, Guid? stagingId = null)
    {
        lock (_pending)
        {
            if (_pending.TryGetValue(projectId, out var p) && (stagingId is null || p.Preview.StagingId == stagingId)) _pending.Remove(projectId);
        }
        Changed?.Invoke();
    }
}
