namespace BirkNext.Web.Services;

/// <summary>Whether this session's lifecycle holds its document bodies.</summary>
public enum WorkspaceDocumentsState
{
    /// <summary>Every body is in the session (nothing deferred, or loaded).</summary>
    Available,
    /// <summary>Restored from the bootstrap manifest: the bodies are on the server and are loaded when a reader needs them.</summary>
    Deferred,
    Loading,
    /// <summary>The bodies could not be loaded. They still exist; a reader shows the failure (never "no documents") and may retry.</summary>
    Failed,
}

/// <summary>
/// Loads the document bodies of a workspace restored from its bootstrap manifest, once, when something reads document content (an explorer,
/// a review, the dashboard) — never at application start. Pages that read no documents never cause the download.
/// </summary>
public interface IWorkspaceDocumentHydration
{
    WorkspaceDocumentsState State { get; }
    /// <summary>Why the last load failed (Failed only).</summary>
    string? Error { get; }
    /// <summary>The bodies are not in the session yet (Deferred, Loading or Failed).</summary>
    bool IsDeferred => State is not WorkspaceDocumentsState.Available;
    /// <summary>Loads the bodies when deferred (concurrent callers share one load). True when they are available. After a failed load every
    /// reader gets the failure at once — nothing retries on its own; <see cref="RetryAsync"/> does, when a person asks.</summary>
    Task<bool> EnsureAsync(CancellationToken cancellationToken = default);
    /// <summary>Loads the bodies again after a failure (Retry).</summary>
    Task<bool> RetryAsync(CancellationToken cancellationToken = default);
    event Action? Changed;
}

public sealed class WorkspaceDocumentHydration(WorkspaceArtifactRepository repository, IServiceProvider services, ILogger<WorkspaceDocumentHydration> logger)
    : IWorkspaceDocumentHydration
{
    private readonly object _gate = new();
    private Task<bool>? _loading;
    private Guid? _loadedFor;
    private string? _error;

    public event Action? Changed;

    public WorkspaceDocumentsState State
    {
        get
        {
            if (repository.DeferredDocumentsWorkspaceId is null) return WorkspaceDocumentsState.Available;
            lock (_gate) return _loading is { IsCompleted: false } ? WorkspaceDocumentsState.Loading : _error is not null ? WorkspaceDocumentsState.Failed : WorkspaceDocumentsState.Deferred;
        }
    }

    public string? Error => repository.DeferredDocumentsWorkspaceId is null ? null : _error;

    public Task<bool> EnsureAsync(CancellationToken cancellationToken = default) => Ensure(retry: false, cancellationToken);

    public Task<bool> RetryAsync(CancellationToken cancellationToken = default) => Ensure(retry: true, cancellationToken);

    private Task<bool> Ensure(bool retry, CancellationToken cancellationToken)
    {
        if (repository.DeferredDocumentsWorkspaceId is not { } workspaceId) return Task.FromResult(true);
        Task<bool> loading;
        lock (_gate)
        {
            // Every reader of a failed load sees the failure (many readers retrying on their own would repeat the request in a loop).
            if (_error is not null && !retry && _loadedFor == workspaceId) return Task.FromResult(false);
            if (_loading is null || _loading.IsCompleted || _loadedFor != workspaceId)
            {
                _loadedFor = workspaceId;
                _error = null;
                _loading = LoadAsync(workspaceId);
            }
            loading = _loading;
        }
        Changed?.Invoke();
        // The shared load is never cancelled by one reader leaving; that reader only stops waiting.
        return loading.WaitAsync(cancellationToken);
    }

    private async Task<bool> LoadAsync(Guid workspaceId)
    {
        await Task.Yield();
        try
        {
            var persistence = services.GetRequiredService<IWorkspacePersistenceApiService>();
            var documents = await persistence.LoadDocumentsAsync(workspaceId)
                ?? throw new InvalidOperationException("The saved workspace is no longer stored.");
            // Another workspace was restored, cleared or reset meanwhile: these bodies belong to the one that was deferred, not to it.
            if (repository.DeferredDocumentsWorkspaceId != workspaceId) return repository.DeferredDocumentsWorkspaceId is null;
            repository.ApplyDocuments(documents);
            lock (_gate) _error = null;
            logger.LogInformation("Workspace documents loaded: {Revisions} revisions, {Generated} generated documents", documents.Revisions.Count, documents.GeneratedDocuments.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Workspace documents could not be loaded");
            lock (_gate) _error = BackendRequestClassifier.IsRequestFailure(ex) ? BackendRequestClassifier.FromException("Project documents", ex).UserMessage : ex.Message;
            Changed?.Invoke();
            return false;
        }
        // The review context was not built from a manifest (it would have been empty); it is built now, from the bodies.
        if (services.GetService<IReviewContextProvider>() is { } reviewContext) await reviewContext.RebuildAsync();
        Changed?.Invoke();
        return true;
    }
}
