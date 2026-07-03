using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.Web.Models;
using Microsoft.JSInterop;

namespace BirkNext.Web.Services;

public interface IExtractionSessionService
{
    Task<ExtractionSessionSnapshot?> LoadAsync();
    Task SaveAsync(ExtractionSessionSnapshot snapshot);
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
            if (!_stateManager.IsValidForCurrentWorkspace(_loadedForWorkspaceId))
                return null;

            var json = await _js.InvokeAsync<string?>("birkNextStorage.getItem", StorageKey);
            if (string.IsNullOrEmpty(json))
                return null;

            var snapshot = JsonSerializer.Deserialize<ExtractionSessionSnapshot>(json, JsonOptions);
            if (snapshot is null)
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
        try
        {
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await _js.InvokeVoidAsync("birkNextStorage.setItem", StorageKey, json);
        }
        catch
        {
            // Storage write failure is non-fatal — session just won't persist
        }
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
