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

    public ExtractionSessionService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<ExtractionSessionSnapshot?> LoadAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("birkNextStorage.getItem", StorageKey);
            if (string.IsNullOrEmpty(json))
                return null;

            var snapshot = JsonSerializer.Deserialize<ExtractionSessionSnapshot>(json, JsonOptions);
            if (snapshot is null)
                return null;

            return IsExpired(snapshot) ? null : snapshot;
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
