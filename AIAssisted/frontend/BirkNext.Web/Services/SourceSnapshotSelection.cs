using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// The Source Analysis snapshot a picker selected, fetched by id when it is chosen: lists carry summaries, so the evidence of one snapshot is
/// loaded only for the snapshot a person picks. Loading and a failed fetch are explicit states — never "this snapshot has no evidence".
/// </summary>
public sealed class SourceSnapshotSelection(IIntegrationCatalogApiService api)
{
    public string Selected { get; private set; } = "";
    public IqrSourceSnapshot? Snapshot { get; private set; }
    public bool Loading { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Selects a snapshot by its id (blank clears) and fetches it. A later selection wins over an earlier one still loading.</summary>
    public async Task SelectAsync(string? value)
    {
        Selected = value ?? "";
        Snapshot = null; Error = null; Loading = false;
        if (!Guid.TryParse(Selected, out var id)) return;
        Loading = true;
        try
        {
            var snapshot = await api.GetSourceSnapshotAsync(id);
            if (Selected != value) return;
            Snapshot = snapshot;
            if (snapshot is null) Error = "This source snapshot is no longer stored.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (Selected == value) Error = BackendRequestClassifier.FromException("Source analysis", ex).UserMessage;
        }
        finally { if (Selected == value) Loading = false; }
    }
}
