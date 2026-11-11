using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BirkNext.WorkspaceTransfer;

namespace BirkNext.Web.Services;

public sealed record TransferProjectScopeView(string ProjectKey, string DisplayName, int Snapshots, int Contracts, int Suites, int CompositeContexts, DateTimeOffset LatestAnalysis);
public sealed record TransferEnvironmentScopeView(string EnvironmentId, int ConnectedSystems, int Integrations, int SourceBindings, int Flows, int Suites);
public sealed record TransferScopesView(IReadOnlyList<TransferProjectScopeView> Projects, IReadOnlyList<TransferEnvironmentScopeView> Environments);
public sealed record BackupStatusView(bool ToolsConfigured, string? ToolProblem, string? DumpToolVersion, string? RestoreToolVersion, string Mode, string BackupDirectory,
    IReadOnlyList<string> ManagedFiles, IReadOnlyList<BackupExclusion> Excluded, string ApplicationVersion, string ApplicationCommit, BackupReadiness? Readiness = null);
public sealed record StagedPackageView(Guid OperationId, string Sha256, long Bytes);

/// <summary>Backup &amp; Transfer: a thin client over api/workspace-transfer. Every decision (closure, classification, sanitising) is made by the
/// backend; the browser only supplies its Target Environment list and profile, and creates an imported profile as a disabled draft.</summary>
public interface IWorkspaceTransferApiService
{
    Task<(BackupStatusView? Status, string? Error)> BackupStatusAsync(CancellationToken ct = default);
    Task<(IReadOnlyList<BackupSummary> Backups, string? Error)> BackupsAsync(CancellationToken ct = default);
    Task<(BackupCreateResult? Result, string? Error)> CreateBackupAsync(string passphrase, string? note, CancellationToken ct = default);
    Task<(BackupInspection? Inspection, string? Error)> InspectBackupAsync(string fileName, string? passphrase, CancellationToken ct = default);
    Task<(RestoreReport? Report, string? Error)> RestoreAsync(string fileName, string passphrase, CancellationToken ct = default);
    Task<(IReadOnlyList<RestoreReport> Reports, string? Error)> RestoresAsync(CancellationToken ct = default);
    Task<(TransferScopesView? Scopes, string? Error)> ScopesAsync(IReadOnlyList<string> knownEnvironmentIds, CancellationToken ct = default);
    Task<(ExportPreview? Preview, string? Error)> PreviewExportAsync(ExportRequest request, CancellationToken ct = default);
    Task<(ExportResult? Result, string? Error)> ExportAsync(ExportRequest request, CancellationToken ct = default);
    Task<(StagedPackageView? Staged, string? Error)> StageAsync(Stream package, CancellationToken ct = default);
    Task<(ImportPreview? Preview, string? Error)> PreviewImportAsync(Guid operationId, IReadOnlyList<EnvironmentCandidate> candidates, CancellationToken ct = default);
    Task<(ImportReport? Report, string? Error)> ApplyImportAsync(ImportApplyRequest request, CancellationToken ct = default);
    Task<(IReadOnlyList<TransferOperationSummary> Operations, string? Error)> OperationsAsync(CancellationToken ct = default);
    string PackageDownloadUrl(string fileName);
    string BackupDownloadUrl(string fileName);
}

public sealed class WorkspaceTransferApiService(HttpClient http) : IWorkspaceTransferApiService
{
    public const string Title = "Backup & Transfer";
    private const string Root = "api/workspace-transfer";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public Task<(BackupStatusView?, string?)> BackupStatusAsync(CancellationToken ct = default) => Get<BackupStatusView>($"{Root}/backups/status", ct);
    public async Task<(IReadOnlyList<BackupSummary>, string?)> BackupsAsync(CancellationToken ct = default) { var (v, e) = await Get<List<BackupSummary>>($"{Root}/backups", ct); return (v ?? [], e); }
    public Task<(BackupCreateResult?, string?)> CreateBackupAsync(string passphrase, string? note, CancellationToken ct = default) =>
        Post<BackupCreateResult>($"{Root}/backups", new BackupCreateRequest(passphrase, note), ct);
    public Task<(BackupInspection?, string?)> InspectBackupAsync(string fileName, string? passphrase, CancellationToken ct = default) =>
        Post<BackupInspection>($"{Root}/backups/{Uri.EscapeDataString(fileName)}/inspect", new { passphrase }, ct);
    public Task<(RestoreReport?, string?)> RestoreAsync(string fileName, string passphrase, CancellationToken ct = default) =>
        Post<RestoreReport>($"{Root}/restores", new RestoreRequest(fileName, passphrase), ct);
    public async Task<(IReadOnlyList<RestoreReport>, string?)> RestoresAsync(CancellationToken ct = default) { var (v, e) = await Get<List<RestoreReport>>($"{Root}/restores", ct); return (v ?? [], e); }
    public Task<(TransferScopesView?, string?)> ScopesAsync(IReadOnlyList<string> knownEnvironmentIds, CancellationToken ct = default) =>
        Post<TransferScopesView>($"{Root}/packages/scopes", new { knownEnvironmentIds }, ct);
    public Task<(ExportPreview?, string?)> PreviewExportAsync(ExportRequest request, CancellationToken ct = default) => Post<ExportPreview>($"{Root}/packages/export/preview", request, ct);
    public Task<(ExportResult?, string?)> ExportAsync(ExportRequest request, CancellationToken ct = default) => Post<ExportResult>($"{Root}/packages/export", request, ct);

    public async Task<(StagedPackageView?, string?)> StageAsync(Stream package, CancellationToken ct = default)
    {
        try
        {
            using var content = new StreamContent(package);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await http.PostAsync($"{Root}/packages/import", content, ct);
            return await Read<StagedPackageView>(response, ct);
        }
        catch (HttpRequestException ex) { return (null, $"The backend is not reachable ({ex.Message})."); }
    }

    public Task<(ImportPreview?, string?)> PreviewImportAsync(Guid operationId, IReadOnlyList<EnvironmentCandidate> candidates, CancellationToken ct = default) =>
        Post<ImportPreview>($"{Root}/packages/import/{operationId}/preview", new { candidates }, ct);
    public Task<(ImportReport?, string?)> ApplyImportAsync(ImportApplyRequest request, CancellationToken ct = default) =>
        Post<ImportReport>($"{Root}/packages/import/{request.OperationId}/apply", request, ct);
    public async Task<(IReadOnlyList<TransferOperationSummary>, string?)> OperationsAsync(CancellationToken ct = default) { var (v, e) = await Get<List<TransferOperationSummary>>($"{Root}/operations?take=20", ct); return (v ?? [], e); }

    public string PackageDownloadUrl(string fileName) => new Uri(http.BaseAddress!, $"{Root}/packages/exports/{Uri.EscapeDataString(fileName)}/download").ToString();
    public string BackupDownloadUrl(string fileName) => new Uri(http.BaseAddress!, $"{Root}/backups/{Uri.EscapeDataString(fileName)}/download").ToString();

    private async Task<(T?, string?)> Get<T>(string url, CancellationToken ct)
    {
        try { using var response = await http.GetAsync(url, ct); return await Read<T>(response, ct); }
        catch (HttpRequestException ex) { return (default, $"The backend is not reachable ({ex.Message})."); }
    }

    private async Task<(T?, string?)> Post<T>(string url, object body, CancellationToken ct)
    {
        try { using var response = await http.PostAsJsonAsync(url, body, Json, ct); return await Read<T>(response, ct); }
        catch (HttpRequestException ex) { return (default, $"The backend is not reachable ({ex.Message})."); }
    }

    /// <summary>Errors come back as {code, message}; the message is safe to show (the backend never puts a secret in it).</summary>
    internal static async Task<(T?, string?)> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct), null);
        if ((int)response.StatusCode is 401 or 403) return (default, "You do not have the Backup & Transfer permission.");
        try
        {
            var error = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)) return (default, m.GetString());
        }
        catch (JsonException) { }
        return (default, $"The request failed ({(int)response.StatusCode}).");
    }
}
