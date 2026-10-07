using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

public interface IExtractionSessionService
{
    Task<ExtractionSessionSnapshot?> LoadAsync();
    Task SaveAsync(ExtractionSessionSnapshot snapshot);
    Task<SpecificationAnalysisCacheEntry?> GetSpecificationAnalysisAsync(string artifactKey, string analyzerVersion);
    Task SaveSpecificationAnalysisAsync(SpecificationAnalysisCacheEntry entry);
    Task ClearAsync();
    bool IsExpired(ExtractionSessionSnapshot snapshot);
}

public sealed class ExtractionSessionService : IExtractionSessionService
{
    private const string StorageKey = "birknext:extraction:session";
    private static readonly TimeSpan SessionExpiry = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IJSRuntime _js;
    private readonly IWorkspaceStateManager _stateManager;
    private readonly SemaphoreSlim _storageWriteLock = new(1, 1);
    private Guid? _loadedForWorkspaceId;

    public ExtractionSessionService(IJSRuntime js, IWorkspaceStateManager stateManager)
    {
        _js = js;
        _stateManager = stateManager;
        _stateManager.WorkspaceChanged += OnWorkspaceChanged;
    }

    private void OnWorkspaceChanged(Guid? newWorkspaceId)
    {
        // Clear cached extraction if workspace changed
        _loadedForWorkspaceId = null;
    }

    public async Task<ExtractionSessionSnapshot?> LoadAsync()
    {
        try
        {
            // If workspace changed since we loaded, invalidate cache
            if ((_stateManager.CurrentWorkspaceId is not null || _loadedForWorkspaceId is not null)
                && !_stateManager.IsValidForCurrentWorkspace(_loadedForWorkspaceId))
                return null;

            var json = await _js.InvokeAsync<string?>("birkNextStorage.getItem", StorageKey);
            if (string.IsNullOrEmpty(json))
                return null;

            var snapshot = JsonSerializer.Deserialize<ExtractionSessionSnapshot>(json, JsonOptions);
            if (snapshot is null)
                return null;

            // A cache-only root is not an interactive extraction session.
            if (snapshot.Candidates.Count == 0
                && string.IsNullOrWhiteSpace(snapshot.SpecMarkdown)
                && snapshot.SpecificationAnalyses.Count > 0)
                return null;

            if (IsExpired(snapshot))
                return null;

            _loadedForWorkspaceId = _stateManager.CurrentWorkspaceId;
            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(ExtractionSessionSnapshot snapshot)
    {
        await _storageWriteLock.WaitAsync();
        try
        {
            // Normal extraction-session saves must preserve the explorer analysis cache.
            ExtractionSessionSnapshot? existing = null;
            try { existing = await ReadSnapshotAsync(); } catch { }
            if (existing is not null && existing.SpecificationAnalyses.Count > 0)
                snapshot.SpecificationAnalyses = existing.SpecificationAnalyses;
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await _js.InvokeVoidAsync("birkNextStorage.setItem", StorageKey, json);
        }
        catch
        {
            // Storage write failure is non-fatal — session just won't persist
        }
        finally
        {
            _storageWriteLock.Release();
        }
    }

    public async Task<SpecificationAnalysisCacheEntry?> GetSpecificationAnalysisAsync(string artifactKey, string analyzerVersion)
    {
        try
        {
            // Analysis entries have their own bounded retention and remain reusable even
            // when the separate interactive extraction session expires.
            var snapshot = await ReadSnapshotAsync();
            return snapshot?.SpecificationAnalyses.LastOrDefault(entry =>
                entry.ArtifactKey == artifactKey && entry.AnalyzerVersion == analyzerVersion);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveSpecificationAnalysisAsync(SpecificationAnalysisCacheEntry entry)
    {
        await _storageWriteLock.WaitAsync();
        try
        {
            var snapshot = await ReadSnapshotAsync() ?? new ExtractionSessionSnapshot();
            snapshot.SpecificationAnalyses.RemoveAll(existing =>
                existing.ArtifactKey == entry.ArtifactKey && existing.AnalyzerVersion == entry.AnalyzerVersion);
            snapshot.SpecificationAnalyses.Add(entry);
            // Bound local storage growth while retaining recent artifact history.
            snapshot.SpecificationAnalyses = snapshot.SpecificationAnalyses
                .OrderByDescending(existing => existing.AnalyzedAt)
                .Take(20)
                .ToList();
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await _js.InvokeVoidAsync("birkNextStorage.setItem", StorageKey, json);
        }
        catch
        {
            // Analysis is still usable in memory if browser storage is unavailable.
        }
        finally
        {
            _storageWriteLock.Release();
        }
    }

    private async Task<ExtractionSessionSnapshot?> ReadSnapshotAsync()
    {
        var json = await _js.InvokeAsync<string?>("birkNextStorage.getItem", StorageKey);
        return string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<ExtractionSessionSnapshot>(json, JsonOptions);
    }

    public async Task ClearAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("birkNextStorage.removeItem", StorageKey);
        }
        catch
        {
            // Non-fatal
        }
    }

    public bool IsExpired(ExtractionSessionSnapshot snapshot)
        => DateTimeOffset.UtcNow - snapshot.Timestamp > SessionExpiry;
}
