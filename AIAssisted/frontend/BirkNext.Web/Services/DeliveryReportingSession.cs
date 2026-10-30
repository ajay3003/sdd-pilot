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

    public void Clear(Guid projectId, Guid? stagingId = null)
    {
        lock (_pending)
        {
            if (_pending.TryGetValue(projectId, out var p) && (stagingId is null || p.Preview.StagingId == stagingId)) _pending.Remove(projectId);
        }
        Changed?.Invoke();
    }
}
