namespace BirkNext.Web.Shared.Components.Loading;

/// <summary>
/// Stale-response protection for a page or section that reloads when its context changes (project, source, environment, module). Each
/// load takes a ticket; beginning a new load cancels the previous ticket's token, and a response may be applied only while its ticket is
/// still current — an older, slower response never replaces a newer one. Disposing the owner (unmount) makes every ticket stale.
///
/// Usage:
///   var load = _loads.Begin();
///   var data = await Api.GetAsync(id, load.Token);
///   if (!load.IsCurrent) return;           // a newer load started, or the component is gone
///   _data = data;
///   ...
///   finally { if (load.IsCurrent) _loading = false; }
/// </summary>
public sealed class LoadSequence : IDisposable
{
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>The generation of the newest load (0 before the first).</summary>
    public int Generation { get; private set; }

    /// <summary>True while the newest load has not ended (<see cref="End"/>), i.e. a section showing it should present loading.</summary>
    public bool IsLoading { get; private set; }

    public LoadTicket Begin()
    {
        Cancel();
        _cts = _disposed ? null : new CancellationTokenSource();
        IsLoading = !_disposed;
        return new(this, ++Generation, _cts?.Token ?? new CancellationToken(true));
    }

    /// <summary>Ends the load if it is still the newest; a stale ticket changes nothing (the newer load still owns the indicator).</summary>
    public bool End(LoadTicket ticket)
    {
        if (!IsCurrent(ticket)) return false;
        IsLoading = false;
        return true;
    }

    public bool IsCurrent(LoadTicket ticket) => !_disposed && ReferenceEquals(ticket.Owner, this) && ticket.Generation == Generation;

    private void Cancel()
    {
        if (_cts is null) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        _disposed = true;
        IsLoading = false;
        Cancel();
    }
}

/// <summary>One load of a <see cref="LoadSequence"/>: its generation and a token cancelled when a newer load begins or the owner is disposed.</summary>
public readonly record struct LoadTicket(LoadSequence Owner, int Generation, CancellationToken Token)
{
    public bool IsCurrent => Owner.IsCurrent(this);
}
