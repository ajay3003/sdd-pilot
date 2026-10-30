namespace BirkNext.Web.Services.Operations;

/// <summary>Lifecycle of a background operation. Completed, Failed and Cancelled are terminal: nothing moves out of them.</summary>
public enum OperationStatus { Queued, Preparing, Running, Cancelling, Completed, Failed, Cancelled }

/// <summary>What runs. New kinds (Source Analysis, Schema Studio, E2E …) are added here when they move onto the operation manager.</summary>
public enum OperationKind { ProjectImportUpload, ProjectImport, DeliveryReportGeneration, DeliveryReportExport, DeliveryAzureDevOpsImport }

/// <summary>
/// The context captured when the operation starts — never re-read from the current selection while it runs, so switching project, Workspace
/// View or page never changes whose operation it is.
/// </summary>
public sealed record OperationContext(string ProjectLabel, string? ProjectKey = null)
{
    public string? Repository { get; init; }
    public string? Branch { get; init; }
    public string? Revision { get; init; }
    public string? Baseline { get; init; }
}

/// <summary>A downloadable result kept by a completed export, so it can be downloaded again from the Activity Center.</summary>
public sealed record OperationFile(string FileName, string ContentType, string Content);

/// <summary>An immutable view of one operation. Progress is shown as a fraction only when <see cref="Total"/> is known (measured, never guessed).</summary>
public sealed record OperationSnapshot
{
    public Guid Id { get; init; }
    public OperationKind Kind { get; init; }
    public string Title { get; init; } = "";
    public OperationContext Context { get; init; } = new("");
    public OperationStatus Status { get; init; }
    public string Stage { get; init; } = "";
    public string? Detail { get; init; }
    public long? Done { get; init; }
    public long? Total { get; init; }
    /// <summary>"bytes", "documents", "stages" …: what Done/Total count.</summary>
    public string? Unit { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    /// <summary>A safe, user-facing message (never an exception text, credential or token).</summary>
    public string? Error { get; init; }
    public bool CanCancel { get; init; }
    public bool CanRetry { get; init; }
    /// <summary>Where the operation is shown in full (the initiating page).</summary>
    public string DetailsRoute { get; init; } = "";
    /// <summary>Where the result is (a report, the imported project …) once completed.</summary>
    public string? ResultRoute { get; init; }
    public string? ResultLabel { get; init; }
    public OperationFile? File { get; init; }
    public Guid? RetryOf { get; init; }
    public string? IdempotencyKey { get; init; }

    public bool IsActive => Status is OperationStatus.Queued or OperationStatus.Preparing or OperationStatus.Running or OperationStatus.Cancelling;
    public int? Percent => Total is > 0 && Done is { } done ? (int)Math.Clamp(done * 100 / Total.Value, 0, 100) : null;
}

/// <summary>What the work reports while it runs.</summary>
public interface IOperationProgress
{
    void Stage(string stage, string? detail = null, long? done = null, long? total = null, string? unit = null);
    /// <summary>The point of no return (results are being written): from here on the operation can no longer be cancelled. False when a
    /// cancellation already arrived — the work must then stop without writing anything.</summary>
    bool BeginFinalization(string stage, string? detail = null);
}

/// <summary>How the work ended. A success carries where its result is; a failure a safe message.</summary>
public sealed record OperationOutcome(bool Succeeded, string? Error = null, string? ResultRoute = null, string? ResultLabel = null, OperationFile? File = null, bool CanRetry = false)
{
    public static OperationOutcome Success(string? resultRoute = null, string? resultLabel = null, OperationFile? file = null) => new(true, null, resultRoute, resultLabel, file);
    public static OperationOutcome Failure(string error, bool canRetry = true) => new(false, error, CanRetry: canRetry);
}

public sealed record OperationRequest(OperationKind Kind, string Title, OperationContext Context, string DetailsRoute)
{
    /// <summary>While an operation with the same key is active, starting again returns that operation instead of a second one.</summary>
    public string? IdempotencyKey { get; init; }
    public bool Cancellable { get; init; } = true;
    public Guid? RetryOf { get; init; }
}

/// <summary>The started (or already running, when <see cref="Duplicate"/>) operation and its completion.</summary>
public sealed record OperationRun(OperationSnapshot Operation, Task<OperationSnapshot> Completion, bool Duplicate);

/// <summary>
/// The application-level owner of long-running work (Activity Center). The work runs here, not in the page that started it: a page that is left
/// only stops receiving <see cref="Changed"/> — it never cancels the operation. Only <see cref="Cancel"/> (an explicit user action) does.
/// <para>One instance per browser tab (scoped = the WASM application). Survives in-app navigation, Workspace View and project switches;
/// not a browser refresh or closing the tab (the client-side part of the work lives in this tab). Completed, failed and cancelled operations
/// are kept for <see cref="Retention"/> (at most <see cref="MaxFinished"/>).</para>
/// </summary>
public sealed class BackgroundOperationService(TimeProvider? clock = null)
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(2);
    public const int MaxFinished = 20;

    private sealed class Entry
    {
        public required OperationSnapshot Snapshot;
        public required CancellationTokenSource Cancellation;
        public bool Finalizing;
        public TaskCompletionSource<OperationSnapshot> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    /// <summary>Raised after any operation changed. Subscribers re-read <see cref="Operations"/>; unsubscribing never affects the work.</summary>
    public event Action? Changed;

    /// <summary>Active operations first (newest first), then finished ones (newest first).</summary>
    public IReadOnlyList<OperationSnapshot> Operations
    {
        get
        {
            lock (_gate)
            {
                Prune();
                return [.. _entries.Select(e => e.Snapshot).OrderByDescending(s => s.IsActive).ThenByDescending(s => s.StartedAt)];
            }
        }
    }

    public IReadOnlyList<OperationSnapshot> Active => [.. Operations.Where(o => o.IsActive)];

    public OperationSnapshot? Find(Guid id) { lock (_gate) return _entries.FirstOrDefault(e => e.Snapshot.Id == id)?.Snapshot; }

    public OperationSnapshot? FindActive(string idempotencyKey) { lock (_gate) return _entries.FirstOrDefault(e => e.Snapshot.IsActive && e.Snapshot.IdempotencyKey == idempotencyKey)?.Snapshot; }

    /// <summary>The newest operation of a kind (active or finished) for a context key, e.g. to restore a page.</summary>
    public OperationSnapshot? Latest(OperationKind kind, string? projectKey = null) =>
        Operations.Where(o => o.Kind == kind && (projectKey is null || o.Context.ProjectKey == projectKey)).OrderByDescending(o => o.StartedAt).FirstOrDefault();

    /// <summary>Starts <paramref name="work"/> as an operation, or returns the active one with the same idempotency key.</summary>
    public OperationRun Start(OperationRequest request, Func<IOperationProgress, CancellationToken, Task<OperationOutcome>> work)
    {
        Entry entry;
        lock (_gate)
        {
            if (request.IdempotencyKey is { } key && _entries.FirstOrDefault(e => e.Snapshot.IsActive && e.Snapshot.IdempotencyKey == key) is { } running)
                return new(running.Snapshot, running.Done.Task, Duplicate: true);
            entry = new Entry
            {
                Cancellation = new CancellationTokenSource(),
                Snapshot = new OperationSnapshot
                {
                    Id = Guid.NewGuid(), Kind = request.Kind, Title = request.Title, Context = request.Context, Status = OperationStatus.Queued, Stage = "Queued",
                    StartedAt = _clock.GetUtcNow(), CanCancel = request.Cancellable, DetailsRoute = request.DetailsRoute, IdempotencyKey = request.IdempotencyKey, RetryOf = request.RetryOf,
                },
            };
            _entries.Add(entry);
        }
        Notify();
        _ = RunAsync(entry, work);
        return new(entry.Snapshot, entry.Done.Task, Duplicate: false);
    }

    /// <summary>Explicit cancellation of one operation. False when it is unknown, finished, not cancellable or already finalizing its results.</summary>
    public bool Cancel(Guid id)
    {
        Entry? entry;
        lock (_gate)
        {
            entry = _entries.FirstOrDefault(e => e.Snapshot.Id == id);
            if (entry is null || !entry.Snapshot.IsActive || !entry.Snapshot.CanCancel || entry.Finalizing) return false;
            if (!Transition(entry, OperationStatus.Cancelling, s => s with { Detail = "Cancellation requested…" })) return false;
        }
        Notify();
        entry.Cancellation.Cancel();
        return true;
    }

    /// <summary>Removes a finished operation from the list (an active one cannot be dismissed — cancel it first).</summary>
    public bool Dismiss(Guid id)
    {
        lock (_gate)
        {
            if (_entries.FirstOrDefault(e => e.Snapshot.Id == id) is not { } entry || entry.Snapshot.IsActive) return false;
            _entries.Remove(entry);
        }
        Notify();
        return true;
    }

    /// <summary>Valid lifecycle moves. Terminal states never change; a cancelling operation cannot go back to running.</summary>
    public static bool CanTransition(OperationStatus from, OperationStatus to) => (from, to) switch
    {
        (OperationStatus.Queued, OperationStatus.Preparing or OperationStatus.Running or OperationStatus.Cancelling or OperationStatus.Failed or OperationStatus.Cancelled) => true,
        (OperationStatus.Preparing, OperationStatus.Running or OperationStatus.Cancelling or OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled) => true,
        (OperationStatus.Running, OperationStatus.Running or OperationStatus.Cancelling or OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled) => true,
        (OperationStatus.Cancelling, OperationStatus.Cancelled or OperationStatus.Failed) => true,
        _ => false,
    };

    private async Task RunAsync(Entry entry, Func<IOperationProgress, CancellationToken, Task<OperationOutcome>> work)
    {
        var ct = entry.Cancellation.Token;
        lock (_gate) Transition(entry, OperationStatus.Preparing, s => s with { Stage = "Preparing" });
        Notify();
        OperationOutcome outcome;
        try { outcome = await work(new Reporter(this, entry), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { outcome = new(false, "Cancelled."); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never the exception text: it can carry URLs, paths or response bodies.
            outcome = OperationOutcome.Failure($"The operation stopped unexpectedly ({ex.GetType().Name}).");
        }
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (ct.IsCancellationRequested && !entry.Finalizing)
                // A cancelled operation never publishes a success (the work must not have written results before finalizing).
                Transition(entry, OperationStatus.Cancelled, s => s with { CompletedAt = now, Stage = "Cancelled", Detail = "Cancelled by the user. No results were published.", CanCancel = false, CanRetry = true });
            else if (outcome.Succeeded)
                Transition(entry, OperationStatus.Completed, s => s with
                {
                    CompletedAt = now, Stage = "Completed", Detail = s.Detail, CanCancel = false, ResultRoute = outcome.ResultRoute ?? s.DetailsRoute, ResultLabel = outcome.ResultLabel,
                    File = outcome.File, Done = s.Total ?? s.Done,
                });
            else
                Transition(entry, OperationStatus.Failed, s => s with { CompletedAt = now, Error = outcome.Error ?? "The operation failed.", CanCancel = false, CanRetry = outcome.CanRetry });
            entry.Cancellation.Dispose();
        }
        Notify();
        entry.Done.TrySetResult(entry.Snapshot);
    }

    private bool Transition(Entry entry, OperationStatus to, Func<OperationSnapshot, OperationSnapshot> change)
    {
        if (!CanTransition(entry.Snapshot.Status, to)) return false;
        entry.Snapshot = change(entry.Snapshot) with { Status = to };
        return true;
    }

    private void Prune()
    {
        var cutoff = _clock.GetUtcNow() - Retention;
        _entries.RemoveAll(e => !e.Snapshot.IsActive && e.Snapshot.CompletedAt < cutoff);
        var finished = _entries.Where(e => !e.Snapshot.IsActive).OrderByDescending(e => e.Snapshot.CompletedAt).Skip(MaxFinished).ToList();
        foreach (var e in finished) _entries.Remove(e);
    }

    private void Notify()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* a subscriber's failure never affects the operation */ }
    }

    private sealed class Reporter(BackgroundOperationService owner, Entry entry) : IOperationProgress
    {
        public void Stage(string stage, string? detail = null, long? done = null, long? total = null, string? unit = null)
        {
            lock (owner._gate)
            {
                if (entry.Snapshot.Status == OperationStatus.Cancelling)
                    entry.Snapshot = entry.Snapshot with { Done = done, Total = total, Unit = unit };
                else if (!owner.Transition(entry, OperationStatus.Running, s => s with { Stage = stage, Detail = detail, Done = done, Total = total, Unit = unit })) return;
            }
            owner.Notify();
        }

        public bool BeginFinalization(string stage, string? detail = null)
        {
            lock (owner._gate)
            {
                if (entry.Cancellation.IsCancellationRequested) return false;
                entry.Finalizing = true;
                owner.Transition(entry, OperationStatus.Running, s => s with { Stage = stage, Detail = detail, CanCancel = false, Done = null, Total = null, Unit = null });
            }
            owner.Notify();
            return true;
        }
    }
}
