using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Integrations;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed record SourceUploadFailure(string Code, string Stage, string Message, string? EntryPath = null,
    long? Actual = null, long? Limit = null)
{
    public string Guidance => Code switch
    {
        "SOURCE_UPLOAD_TIMEOUT" => "Check the snapshot list before retrying; the server may have completed the analysis after the request timed out.",
        "NO_ACTIVE_ENVIRONMENT" => "Select or create a Target Environment, then retry the upload.",
        "ARCHIVE_TOO_LARGE" => "Choose a ZIP archive smaller than 50 MB.",
        "ARCHIVE_EXPANDED_SIZE_EXCEEDED" => "Remove generated or unnecessary files and create a smaller source archive.",
        "ARCHIVE_TOO_MANY_ENTRIES" => "Remove generated folders such as build output, package caches, or dependencies, then retry.",
        "ARCHIVE_ENTRY_TOO_LARGE" => "Reduce or remove the oversized file, then retry.",
        "ARCHIVE_PATH_TRAVERSAL" or "ARCHIVE_ABSOLUTE_PATH" or "ARCHIVE_INVALID_PATH" => "Rebuild the ZIP from the project folder so every entry stays inside the archive root.",
        "ARCHIVE_DUPLICATE_PATH" => "Remove duplicate entries that map to the same path, including case-only duplicates.",
        "ARCHIVE_SYMLINK_UNSUPPORTED" => "Replace symbolic links with regular files or folders before creating the ZIP.",
        "ARCHIVE_ENCRYPTED_UNSUPPORTED" => "Remove encryption from the ZIP and retry.",
        "ARCHIVE_EMPTY" or "ARCHIVE_EMPTY_UPLOAD" => "Choose a ZIP that contains at least one file.",
        "ARCHIVE_INVALID_ZIP" => "Re-create the ZIP and make sure the archive finishes writing before upload.",
        "SOURCE_ANALYSIS_FAILED" => "The archive passed validation. Retry the analysis or reduce the archive if it contains many large files.",
        "SOURCE_SNAPSHOT_SAVE_FAILED" => "The archive passed validation, but the snapshot was not saved. Retry the upload later.",
        "UPLOAD_INVALID_FORM" or "UPLOAD_MULTIPART_REQUIRED" or "UPLOAD_FILE_COUNT_INVALID" => "Choose one ZIP file and retry the upload.",
        "IMPORT_STAGING_EXPIRED" => "Preview expired. Re-import the project: choose the ZIP again. Nothing from the expired upload was activated.",
        "ARCHIVE_UNSUPPORTED_FORMAT" => "Choose a .zip archive of the project folder.",
        _ => "Review the reason above, correct the archive or environment, and retry. Existing snapshots are unchanged.",
    };

    public string StageLabel => Stage switch
    {
        "prerequisite" => "Environment prerequisite",
        "upload" => "Upload",
        "validation" => "Archive validation",
        "extraction" => "Archive reading",
        "analysis" => "Source analysis",
        "persistence" => "Snapshot save",
        _ => "Source upload",
    };
}

/// <summary>
/// Client for the backend integration catalog (Target Environment → Integrations) and Integration Quality Review over it.
/// The catalog is persisted by the backend; this client never holds a secret — authentication is a mechanism name.
/// </summary>
public interface IIntegrationCatalogApiService
{
    Task<IReadOnlyList<IqrSourceSnapshot>> ListSourceSnapshotsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([]);
    /// <summary>Read-only source suggestions for one configured field (Source Analysis Infrastructure evidence). Null when unavailable.</summary>
    Task<BirkNext.SourceDomains.SourceInfrastructureSuggestion?> InfrastructureSuggestionAsync(string environmentId, BirkNext.SourceDomains.InfrastructureResourceKind kind, string field, string? configured, string? targetEnvironment, string? parent, CancellationToken ct = default) => Task.FromResult<BirkNext.SourceDomains.SourceInfrastructureSuggestion?>(null);
    Task<(IqrSourceSnapshot? Snapshot, string? Error)> AnalyzeSourceSnapshotAsync(string environmentId, string fileName, Stream content, CancellationToken ct = default) =>
        Task.FromResult<(IqrSourceSnapshot?, string?)>((null, "Source Analysis is unavailable."));
    async Task<(IqrSourceSnapshot? Snapshot, SourceUploadFailure? Error)> AnalyzeSourceSnapshotDetailedAsync(string environmentId, string fileName, Stream content, CancellationToken ct = default)
    {
        var (snapshot, error) = await AnalyzeSourceSnapshotAsync(environmentId, fileName, content, ct);
        return (snapshot, error is null ? null : new("SOURCE_UPLOAD_FAILED", "upload", error));
    }
    /// <summary>Source Analysis snapshots for binding one to an integration (read-only metadata; no upload).</summary>
    Task<ReviewSourceOptions> IqrSourceScopeAsync(string environmentId, Guid? primary, CancellationToken ct = default) => Task.FromResult(new ReviewSourceOptions());
    Task<IntegrationReviewResult> RunWithSourceAsync(FrontendAnalysisProfile profile, IReadOnlyList<IqrSourceSelection> selections, CancellationToken ct = default) =>
        selections.Count == 0 ? RunAsync(profile, ct) : throw new InvalidOperationException("Source snapshot selection is unavailable.");
    Task<IntegrationMappingEvidenceCheck> CheckMappingAsync(string environmentId, string integrationId, CancellationToken ct = default);
    /// <summary>The environment's application-messaging (Wolverine) evidence, or null when no source was analyzed.</summary>
    Task<ApplicationMessagingEvidenceSet?> ApplicationMessagingAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Uploads source archives for read-only analysis; returns the new evidence or the reason nothing was stored.</summary>
    /// <summary>Source Analysis snapshots for application messaging (read-only metadata).</summary>
    Task<ReviewSourceOptions> ApplicationMessagingSourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default) => Task.FromResult(new ReviewSourceOptions());
    /// <summary>Builds the application-messaging evidence from exactly these Source Analysis snapshots (no upload).</summary>
    Task<(ApplicationMessagingEvidenceSet? Set, string? Error)> UseApplicationMessagingSourceAsync(string environmentId, ReviewSourceScopeRequest scope, CancellationToken ct = default) =>
        Task.FromResult<(ApplicationMessagingEvidenceSet?, string?)>((null, "Application messaging is unavailable."));
    Task<ApplicationMessagingEvidenceSet> BindApplicationMessagingAsync(string environmentId, string applicationId, string? consumer, CancellationToken ct = default);
    /// <summary>Read-only "Test Service Bus" of one Service Bus platform (topology, code routes and — when configured — runtime metadata).</summary>
    Task<ServiceBusEvidenceCheck> CheckServiceBusAsync(string environmentId, string platformId, CancellationToken ct = default);
    Task<IntegrationCatalog> GetCatalogAsync(FrontendAnalysisProfile profile, CancellationToken ct = default);
    /// <summary>Explicitly applies a project integration template (add-missing only). The only way template records reach an environment.</summary>
    Task<IntegrationCatalog> ApplyTemplateAsync(string environmentId, string templateId, CancellationToken ct = default) =>
        Task.FromException<IntegrationCatalog>(new NotSupportedException("Integration templates are unavailable."));
    Task<IntegrationDefinition> CreateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default);
    Task<IntegrationDefinition> UpdateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default);
    Task<IntegrationDefinition> SetEnabledAsync(string environmentId, string id, bool enabled, CancellationToken ct = default);
    Task DeleteAsync(string environmentId, string id, CancellationToken ct = default);
    Task<IntegrationPlatform> UpdatePlatformAsync(string environmentId, IntegrationPlatform platform, CancellationToken ct = default);
    /// <summary>Imports integrations still stored in the browser Target Environment profile. The backend imports once per environment.</summary>
    Task<int> ImportLegacyAsync(FrontendAnalysisProfile profile, CancellationToken ct = default);
    Task<IReadOnlyList<IntegrationContractArtifact>> ContractsAsync(string environmentId, CancellationToken ct = default);
    /// <summary>Uploads a trusted JSON Schema for one side of an integration. Returns the stored metadata, or the validation reason.</summary>
    Task<(IntegrationContractArtifact? Artifact, string? Error)> SaveContractAsync(IntegrationContractUpload upload, CancellationToken ct = default);
    Task RemoveContractAsync(string environmentId, string integrationId, IntegrationContractRole role, CancellationToken ct = default);
    Task<IntegrationReviewReadiness> ReadinessAsync(FrontendAnalysisProfile profile, CancellationToken ct = default);
    Task<IntegrationReviewResult> RunAsync(FrontendAnalysisProfile profile, CancellationToken ct = default);
    Task<IReadOnlyList<IntegrationReviewRunSummary>> HistoryAsync(string environmentId, CancellationToken ct = default);
    Task<IntegrationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default);
    Task<IntegrationMessageFlowPackage> GetMessageFlowReviewAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new IntegrationMessageFlowPackage(
        new MessageFlowDefinition { EnvironmentId = environmentId, Name = "Message flow review" }, new AltinnTestConfiguration(), new(false, 0, 0, [], []), [], []));
    Task<IntegrationMessageFlowPackage> SaveMessageFlowReviewAsync(string environmentId, IntegrationMessageFlowPackage package, CancellationToken ct = default) => Task.FromException<IntegrationMessageFlowPackage>(new NotSupportedException("Message flow configuration is unavailable."));
    Task<MessageFlowSourceContractOptions?> MessageFlowSourceContractsAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult<MessageFlowSourceContractOptions?>(null);

    // SCIM identity provisioning. Default members keep other implementations (test fakes) valid; the backend client overrides them.
    /// <summary>The environment's latest SCIM source analysis and stored safe-check history.</summary>
    Task<ScimEvidenceOverview> ScimOverviewAsync(string environmentId, CancellationToken ct = default) => Task.FromResult(new ScimEvidenceOverview());
    /// <summary>Source integrations: the latest source snapshot's discovery reconciled with the catalog. Source-only; never writes or confirms.</summary>
    Task<SourceIntegrationsReport?> SourceIntegrationsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<SourceIntegrationsReport?>(null);
    /// <summary>"Discover from source": re-runs discovery on the stored source snapshot (no runtime call, no archive re-processing).</summary>
    Task<SourceIntegrationsReport?> DiscoverSourceIntegrationsAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<SourceIntegrationsReport?>(null);
    /// <summary>Uploads repository archives for read-only SCIM source analysis.</summary>
    /// <summary>Source Analysis snapshots for SCIM provisioning (read-only metadata).</summary>
    Task<ReviewSourceOptions> ScimSourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default) => Task.FromResult(new ReviewSourceOptions());
    /// <summary>Records the SCIM source evidence of exactly this Source Analysis snapshot (no upload).</summary>
    Task<(ScimSourceEvidence? Evidence, string? Error)> UseScimSourceAsync(string environmentId, ReviewSourceScopeRequest scope, CancellationToken ct = default) =>
        Task.FromResult<(ScimSourceEvidence?, string?)>((null, "SCIM evidence is unavailable."));
    /// <summary>"Run safe SCIM checks": GET-only runtime checks plus source/configuration evidence. Never mutates or lists users.</summary>
    Task<(ScimEvidenceCheck? Check, string? Error)> RunScimChecksAsync(FrontendAnalysisProfile profile, string platformId, CancellationToken ct = default) =>
        Task.FromResult<(ScimEvidenceCheck?, string?)>((null, "SCIM checks are not available."));
    Task<ScimEvidenceCheck?> ScimCheckAsync(Guid runId, CancellationToken ct = default) => Task.FromResult<ScimEvidenceCheck?>(null);
}

public sealed class IntegrationCatalogApiService : IIntegrationCatalogApiService
{
    private static readonly TimeSpan SourceUploadTimeout = TimeSpan.FromMinutes(5);
    private readonly HttpClient http;

    public IntegrationCatalogApiService(HttpClient http)
    {
        this.http = http;
        // Source Analysis performs deterministic analysis before returning its snapshot. The real
        // 2,165-entry M2LB archive takes about two minutes on the current host, beyond HttpClient's
        // 100-second default. Keep the longer timeout scoped to this typed integration client.
        this.http.Timeout = SourceUploadTimeout;
    }

    public async Task<BirkNext.SourceDomains.SourceInfrastructureSuggestion?> InfrastructureSuggestionAsync(string environmentId, BirkNext.SourceDomains.InfrastructureResourceKind kind, string field, string? configured, string? targetEnvironment, string? parent, CancellationToken ct = default)
    {
        static string Q(string name, string? value) => value is null ? "" : $"&{name}={Uri.EscapeDataString(value)}";
        try { return await http.GetFromJsonAsync<BirkNext.SourceDomains.SourceInfrastructureSuggestion>($"api/source-analysis/infrastructure-suggestions?{Env(environmentId)}&kind={kind}{Q("field", field)}{Q("targetEnvironment", targetEnvironment)}{Q("parent", parent)}", Json, ct); }
        catch (HttpRequestException) { return null; }
    }
    public async Task<IReadOnlyList<IqrSourceSnapshot>> ListSourceSnapshotsAsync(string environmentId, CancellationToken ct = default) => await http.GetFromJsonAsync<List<IqrSourceSnapshot>>($"api/source-analysis?{Env(environmentId)}", Json, ct) ?? [];
    public async Task<(IqrSourceSnapshot? Snapshot, string? Error)> AnalyzeSourceSnapshotAsync(string environmentId, string fileName, Stream content, CancellationToken ct = default)
    {
        var (snapshot, failure) = await AnalyzeSourceSnapshotDetailedAsync(environmentId, fileName, content, ct);
        return (snapshot, failure?.Message);
    }
    public Task<(IqrSourceSnapshot? Snapshot, SourceUploadFailure? Error)> AnalyzeSourceSnapshotDetailedAsync(string environmentId, string fileName, Stream content, CancellationToken ct = default) =>
        UploadSource($"api/source-analysis/snapshots?{Env(environmentId)}", fileName, content, ct);
    public async Task<ReviewSourceOptions> IqrSourceScopeAsync(string environmentId, Guid? primary, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ReviewSourceOptions>($"api/integration-review/source/scope?{Env(environmentId)}{(primary is { } id ? $"&primary={id}" : "")}", Json, ct) ?? new();
    private async Task<(IqrSourceSnapshot? Snapshot, SourceUploadFailure? Error)> UploadSource(string route, string fileName, Stream content, CancellationToken ct)
    {
        using var body = new MultipartFormDataContent();
        body.Add(new StreamContent(content), "file", fileName);
        using var response = await http.PostAsync(route, body, ct);
        if (!response.IsSuccessStatusCode) return (null, await ReadSourceUploadFailure(response, ct));
        var snapshot = await response.Content.ReadFromJsonAsync<IqrSourceSnapshot>(Json, ct);
        return snapshot is null
            ? (null, new("SOURCE_SNAPSHOT_RESPONSE_INVALID", "persistence", "The upload completed, but no source snapshot was returned."))
            : (snapshot, null);
    }

    /// <summary>Maps an upload failure response (Source Analysis and Project Import share the contract) to a safe, coded failure.</summary>
    internal static async Task<SourceUploadFailure> ReadSourceUploadFailure(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                var code = StringValue(root, "code");
                var detail = StringValue(root, "message") ?? StringValue(root, "detail");
                var extensions = Property(root, "extensions");
                code ??= extensions is { ValueKind: JsonValueKind.Object } ? StringValue(extensions.Value, "code") : null;
                detail ??= extensions is { ValueKind: JsonValueKind.Object }
                    ? StringValue(extensions.Value, "message") ?? StringValue(extensions.Value, "detail")
                    : null;

                // ASP.NET ProblemDetails uses `detail`, while the upload endpoint's
                // compact contract uses `message`. Extensions can be flattened or nested.
                if (!string.IsNullOrWhiteSpace(code))
                {
                    var stage = StringValue(root, "stage")
                        ?? (extensions is { ValueKind: JsonValueKind.Object } ? StringValue(extensions.Value, "stage") : null)
                        ?? StageForCode(code, status);
                    var entry = StringValue(root, "entryPath")
                        ?? (extensions is { ValueKind: JsonValueKind.Object } ? StringValue(extensions.Value, "entryPath") : null);
                    var actual = Int64Value(root, "actual") ?? (extensions is { ValueKind: JsonValueKind.Object } ? Int64Value(extensions.Value, "actual") : null);
                    var limit = Int64Value(root, "limit") ?? (extensions is { ValueKind: JsonValueKind.Object } ? Int64Value(extensions.Value, "limit") : null);
                    return new(code, NormalizeStage(stage), SafeMessage(code, detail), SafeEntryPath(entry), actual, limit);
                }

                var legacy = MapKnownLegacyMessage(body, status);
                if (legacy is not null) return legacy;

                var request = MapRequestValidationProblem(root, status);
                if (request is not null) return request;
            }
        }
        catch (JsonException) { }
        var mappedLegacy = MapKnownLegacyMessage(body, status);
        if (mappedLegacy is not null) return mappedLegacy;
        return StatusFallback(status);
    }

    private static JsonElement? Property(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        ? value.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: not JsonValueKind.Undefined } property
            ? property.Value : null
        : null;

    private static string? StringValue(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.String } property
        ? property.GetString() : null;

    private static long? Int64Value(JsonElement value, string name) => Property(value, name) is { ValueKind: JsonValueKind.Number } property && property.TryGetInt64(out var actual)
        ? actual : null;

    private static string StageForCode(string code, int status) => code switch
    {
        "NO_ACTIVE_ENVIRONMENT" => "prerequisite",
        "SOURCE_SNAPSHOT_SAVE_FAILED" or "SOURCE_SNAPSHOT_RESPONSE_INVALID" => "persistence",
        "SOURCE_ANALYSIS_FAILED" or "SOURCE_ANALYSIS_UNAVAILABLE" or "SOURCE_SERVER_ERROR" => "analysis",
        "ARCHIVE_EXTRACTION_FAILED" => "extraction",
        _ => status == 422 ? "analysis" : "validation",
    };

    private static string NormalizeStage(string stage) => stage.ToLowerInvariant() switch
    {
        "archivevalidation" or "validation" => "validation",
        "prerequisite" => "prerequisite",
        "extraction" or "archivereading" => "extraction",
        "snapshotcreation" or "persistence" => "persistence",
        "analysis" => "analysis",
        "upload" => "upload",
        _ => stage,
    };

    private static string SafeMessage(string code, string? detail) => IsSafeDetail(detail)
        ? detail!
        : code switch
        {
            "ARCHIVE_INVALID_ZIP" => "The uploaded file is not a valid ZIP archive or is incomplete.",
            "ARCHIVE_UNSUPPORTED_FORMAT" => "Choose a ZIP archive for Source Analysis.",
            "ARCHIVE_EMPTY_UPLOAD" or "ARCHIVE_EMPTY" => "The uploaded ZIP contains no files.",
            "UPLOAD_MULTIPART_REQUIRED" or "UPLOAD_INVALID_FORM" => "The ZIP upload could not be read. Choose the file again and retry.",
            "UPLOAD_FILE_COUNT_INVALID" => "Choose exactly one ZIP archive to upload.",
            "ARCHIVE_PATH_TRAVERSAL" => "The archive contains a path that escapes the project root.",
            "ARCHIVE_ABSOLUTE_PATH" or "ARCHIVE_UNC_PATH" => "The archive contains an absolute path; archive entries must stay inside the project root.",
            "ARCHIVE_INVALID_PATH" => "The archive contains a path that cannot be safely used across platforms.",
            "ARCHIVE_PATH_TOO_LONG" => "An archive entry path exceeds the supported safety limit.",
            "ARCHIVE_SYMLINK_UNSUPPORTED" => "Symbolic links are not supported in source archives.",
            "ARCHIVE_TOO_LARGE" => "The ZIP archive exceeds the 50 MB compressed size limit.",
            "ARCHIVE_EXPANDED_SIZE_EXCEEDED" => "The archive expands beyond the allowed source-analysis limit.",
            "ARCHIVE_TOO_MANY_ENTRIES" => "The archive contains more entries than Source Analysis allows.",
            "ARCHIVE_ENTRY_TOO_LARGE" => "An archive entry exceeds the per-file analysis limit.",
            "ARCHIVE_DUPLICATE_PATH" => "The archive contains entries that resolve to the same path.",
            "ARCHIVE_ENCRYPTED_UNSUPPORTED" => "Encrypted ZIP entries or unsupported compression methods are not supported.",
            _ => "The source upload could not be completed. Correct the issue and retry.",
        };

    private static bool IsSafeDetail(string? detail) => !string.IsNullOrWhiteSpace(detail)
        && !detail.Contains(" at ", StringComparison.OrdinalIgnoreCase)
        && !detail.Contains("Exception", StringComparison.OrdinalIgnoreCase)
        && !detail.Contains("\\\\", StringComparison.Ordinal)
        && !detail.Contains("/tmp/", StringComparison.OrdinalIgnoreCase)
        && !detail.Contains("\\temp\\", StringComparison.OrdinalIgnoreCase)
        && !detail.Contains("C:\\", StringComparison.OrdinalIgnoreCase)
        && !detail.Contains("\\Users\\", StringComparison.OrdinalIgnoreCase);

    private static string? SafeEntryPath(string? entry) => string.IsNullOrWhiteSpace(entry)
        || entry.Contains(':')
        || entry.StartsWith('/')
        || entry.StartsWith('\\')
        || entry.Contains('\0')
        ? null
        : entry;

    /// <summary>
    /// ASP.NET request validation (ProblemDetails with an <c>errors</c> object) rejects the request before the upload endpoint runs:
    /// the archive was never read, so this is never <c>ARCHIVE_REJECTED</c>. A missing environment id means no active Target Environment.
    /// </summary>
    private static SourceUploadFailure? MapRequestValidationProblem(JsonElement root, int status)
    {
        if (status != 400 || Property(root, "errors") is not { ValueKind: JsonValueKind.Object } errors) return null;
        return errors.EnumerateObject().Any(e => e.Name.Equals("environmentId", StringComparison.OrdinalIgnoreCase))
            ? new("NO_ACTIVE_ENVIRONMENT", "prerequisite", "Select or create a Target Environment before uploading source.")
            : new("UPLOAD_REQUEST_INVALID", "upload", "The upload request was incomplete, so the archive was not read. Reload the page and retry.");
    }

    private static SourceUploadFailure? MapKnownLegacyMessage(string body, int status)
    {
        // Translate only deterministic legacy phrases into safe messages; never show
        // arbitrary server text, exception messages, or filesystem paths to the user.
        if (status is not (400 or 413)) return null;
        if (body.Contains("not a valid zip", StringComparison.OrdinalIgnoreCase)
            || body.Contains("invalid or incomplete zip", StringComparison.OrdinalIgnoreCase))
            return new("ARCHIVE_INVALID_ZIP", "validation", SafeMessage("ARCHIVE_INVALID_ZIP", null));
        if (body.Contains("path traversal", StringComparison.OrdinalIgnoreCase) || body.Contains("escapes the project root", StringComparison.OrdinalIgnoreCase))
            return new("ARCHIVE_PATH_TRAVERSAL", "validation", SafeMessage("ARCHIVE_PATH_TRAVERSAL", null));
        if (body.Contains("encrypted", StringComparison.OrdinalIgnoreCase) || body.Contains("unsupported compression", StringComparison.OrdinalIgnoreCase))
            return new("ARCHIVE_ENCRYPTED_UNSUPPORTED", "validation", SafeMessage("ARCHIVE_ENCRYPTED_UNSUPPORTED", null));
        if (body.Contains("too many entr", StringComparison.OrdinalIgnoreCase))
            return new("ARCHIVE_TOO_MANY_ENTRIES", "validation", SafeMessage("ARCHIVE_TOO_MANY_ENTRIES", null));
        if (body.Contains("expanded size", StringComparison.OrdinalIgnoreCase) || body.Contains("expands beyond", StringComparison.OrdinalIgnoreCase))
            return new("ARCHIVE_EXPANDED_SIZE_EXCEEDED", "validation", SafeMessage("ARCHIVE_EXPANDED_SIZE_EXCEEDED", null));
        if (body.Contains("exceeds the 50 mb", StringComparison.OrdinalIgnoreCase) || status == 413)
            return new("ARCHIVE_TOO_LARGE", "upload", SafeMessage("ARCHIVE_TOO_LARGE", null), Limit: 50L * 1024 * 1024);
        if (body.Contains("duplicate path", StringComparison.OrdinalIgnoreCase))
            return new("ARCHIVE_DUPLICATE_PATH", "validation", SafeMessage("ARCHIVE_DUPLICATE_PATH", null));
        return null;
    }

    private static SourceUploadFailure StatusFallback(int status) => status switch
    {
        413 => new("ARCHIVE_TOO_LARGE", "upload", "Upload exceeds the 50 MB compressed archive limit.", Actual: null, Limit: 50L * 1024 * 1024),
        400 => new("ARCHIVE_REJECTED", "validation", "The source archive was rejected, but the server did not return a structured reason. Try creating the ZIP again or contact support with the error code."),
        422 => new("SOURCE_ANALYSIS_FAILED", "analysis", "The archive passed validation, but source analysis could not complete. Retry the analysis or choose a smaller archive."),
        >= 500 => new("SOURCE_SERVER_ERROR", "analysis", "Source upload failed unexpectedly. No new snapshot was selected."),
        _ => new("SOURCE_UPLOAD_FAILED", "upload", "Source upload could not be completed. Existing snapshots are unchanged."),
    };
    public async Task<IntegrationReviewResult> RunWithSourceAsync(FrontendAnalysisProfile profile, IReadOnlyList<IqrSourceSelection> selections, CancellationToken ct = default)
    {
        var request = new IntegrationReviewRunRequest { EnvironmentId = profile.Id, EnvironmentName = profile.Name, SourceSelections = selections.ToList() };
        var scope = $"environmentType={Uri.EscapeDataString(profile.EnvironmentType.ToString())}&targetUrl={Uri.EscapeDataString(profile.TargetUrl ?? "")}";
        return await Read<IntegrationReviewResult>(await http.PostAsJsonAsync($"api/integration-review/run?{scope}", request, Json, ct), ct);
    }
    public async Task<IntegrationMappingEvidenceCheck> CheckMappingAsync(string environmentId, string integrationId, CancellationToken ct = default) =>
        await Read<IntegrationMappingEvidenceCheck>(await http.PostAsync($"api/integrations/{Uri.EscapeDataString(integrationId)}/mapping-evidence?{Env(environmentId)}", null, ct), ct);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ApplicationMessagingEvidenceSet?> ApplicationMessagingAsync(string environmentId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/integrations/application-messaging?{Env(environmentId)}", ct);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == System.Net.HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<ApplicationMessagingEvidenceSet>(Json, ct);
    }

    public async Task<ReviewSourceOptions> ApplicationMessagingSourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ReviewSourceOptions>($"api/integrations/application-messaging/source-scope?{Env(environmentId)}{ReviewSourceQuery.Of(scope)}", Json, ct) ?? new();

    public async Task<(ApplicationMessagingEvidenceSet? Set, string? Error)> UseApplicationMessagingSourceAsync(string environmentId, ReviewSourceScopeRequest scope, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/integrations/application-messaging/source-scope?{Env(environmentId)}", scope, Json, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return (null, (await response.Content.ReadAsStringAsync(ct)).Trim('"'));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ApplicationMessagingEvidenceSet>(Json, ct), null);
    }

    public async Task<SourceIntegrationsReport?> SourceIntegrationsAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<SourceIntegrationsReport>($"api/integrations/source-integrations?{Env(environmentId)}", Json, ct);

    public async Task<SourceIntegrationsReport?> DiscoverSourceIntegrationsAsync(string environmentId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/integrations/source-integrations/discover?{Env(environmentId)}", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SourceIntegrationsReport>(Json, ct);
    }

    public async Task<ScimEvidenceOverview> ScimOverviewAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ScimEvidenceOverview>($"api/integrations/scim?{Env(environmentId)}", Json, ct) ?? new ScimEvidenceOverview();

    public async Task<ReviewSourceOptions> ScimSourceScopeAsync(string environmentId, ReviewSourceScopeRequest? scope, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<ReviewSourceOptions>($"api/integrations/scim/source-scope?{Env(environmentId)}{ReviewSourceQuery.Of(scope)}", Json, ct) ?? new();

    public async Task<(ScimSourceEvidence? Evidence, string? Error)> UseScimSourceAsync(string environmentId, ReviewSourceScopeRequest scope, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"api/integrations/scim/source-scope?{Env(environmentId)}", scope, Json, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return (null, (await response.Content.ReadAsStringAsync(ct)).Trim('"'));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ScimSourceEvidence>(Json, ct), null);
    }

    public async Task<(ScimEvidenceCheck? Check, string? Error)> RunScimChecksAsync(FrontendAnalysisProfile profile, string platformId, CancellationToken ct = default)
    {
        using var response = await http.PostAsync($"api/integrations/scim/{Uri.EscapeDataString(platformId)}/checks?{Scope(profile)}", null, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) return (null, (await response.Content.ReadAsStringAsync(ct)).Trim('"'));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ScimEvidenceCheck>(Json, ct), null);
    }

    public async Task<ScimEvidenceCheck?> ScimCheckAsync(Guid runId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/integrations/scim/checks/{runId}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ScimEvidenceCheck>(Json, ct);
    }

    public async Task<ServiceBusEvidenceCheck> CheckServiceBusAsync(string environmentId, string platformId, CancellationToken ct = default) =>
        await Read<ServiceBusEvidenceCheck>(await http.PostAsync($"api/integrations/platforms/{Uri.EscapeDataString(platformId)}/servicebus-evidence?{Env(environmentId)}", null, ct), ct);

    public async Task<ApplicationMessagingEvidenceSet> BindApplicationMessagingAsync(string environmentId, string applicationId, string? consumer, CancellationToken ct = default) =>
        await Read<ApplicationMessagingEvidenceSet>(await http.PutAsJsonAsync($"api/integrations/application-messaging/{Uri.EscapeDataString(applicationId)}/binding?{Env(environmentId)}", new { consumer }, Json, ct), ct);

    /// <summary>Environment identity plus the two facts the backend uses to decide whether the known M2LB DEV seed applies.</summary>
    private static string Scope(FrontendAnalysisProfile profile) =>
        $"environmentId={Uri.EscapeDataString(profile.Id)}&environmentType={Uri.EscapeDataString(profile.EnvironmentType.ToString())}&targetUrl={Uri.EscapeDataString(profile.TargetUrl ?? "")}";

    private static string Env(string environmentId) => $"environmentId={Uri.EscapeDataString(environmentId)}";

    public async Task<IntegrationCatalog> GetCatalogAsync(FrontendAnalysisProfile profile, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IntegrationCatalog>($"api/integrations?{Scope(profile)}", Json, ct) ?? new IntegrationCatalog { EnvironmentId = profile.Id };

    public async Task<IntegrationCatalog> ApplyTemplateAsync(string environmentId, string templateId, CancellationToken ct = default) =>
        await Read<IntegrationCatalog>(await http.PostAsync($"api/integrations/templates/{Uri.EscapeDataString(templateId)}/apply?{Env(environmentId)}", null, ct), ct);

    public async Task<IntegrationDefinition> CreateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default) =>
        await Read<IntegrationDefinition>(await http.PostAsJsonAsync($"api/integrations?{Env(environmentId)}", definition, Json, ct), ct);

    public async Task<IntegrationDefinition> UpdateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default) =>
        await Read<IntegrationDefinition>(await http.PutAsJsonAsync($"api/integrations/{Uri.EscapeDataString(definition.Id)}?{Env(environmentId)}", definition, Json, ct), ct);

    public async Task<IntegrationDefinition> SetEnabledAsync(string environmentId, string id, bool enabled, CancellationToken ct = default) =>
        await Read<IntegrationDefinition>(await http.PostAsync($"api/integrations/{Uri.EscapeDataString(id)}/enabled?{Env(environmentId)}&enabled={(enabled ? "true" : "false")}", null, ct), ct);

    public async Task DeleteAsync(string environmentId, string id, CancellationToken ct = default) =>
        (await http.DeleteAsync($"api/integrations/{Uri.EscapeDataString(id)}?{Env(environmentId)}", ct)).EnsureSuccessStatusCode();

    public async Task<IntegrationPlatform> UpdatePlatformAsync(string environmentId, IntegrationPlatform platform, CancellationToken ct = default) =>
        await Read<IntegrationPlatform>(await http.PutAsJsonAsync($"api/integrations/platforms/{Uri.EscapeDataString(platform.Id)}?{Env(environmentId)}", platform, Json, ct), ct);

    public async Task<IReadOnlyList<IntegrationContractArtifact>> ContractsAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<IntegrationContractArtifact>>($"api/integrations/contracts?{Env(environmentId)}", Json, ct) ?? [];

    public async Task<(IntegrationContractArtifact? Artifact, string? Error)> SaveContractAsync(IntegrationContractUpload upload, CancellationToken ct = default)
    {
        using var response = await http.PutAsJsonAsync("api/integrations/contracts", upload, Json, ct);
        if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<IntegrationContractArtifact>(Json, ct), null);
        if ((int)response.StatusCode == 400)
        {
            var problem = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Json, ct);
            return (null, problem.TryGetProperty("message", out var message) ? message.GetString() : "The contract was rejected.");
        }
        return (null, $"The contract could not be saved (HTTP {(int)response.StatusCode}).");
    }

    public async Task RemoveContractAsync(string environmentId, string integrationId, IntegrationContractRole role, CancellationToken ct = default)
    {
        using var response = await http.DeleteAsync($"api/integrations/contracts?{Env(environmentId)}&integrationId={Uri.EscapeDataString(integrationId)}&role={role}", ct);
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 404) response.EnsureSuccessStatusCode();
    }

    public async Task<int> ImportLegacyAsync(FrontendAnalysisProfile profile, CancellationToken ct = default)
    {
        if (profile.Integrations.Count == 0) return 0;
        var response = await http.PostAsJsonAsync($"api/integrations/import-legacy?{Env(profile.Id)}", profile.Integrations, Json, ct);
        return await Read<int>(response, ct);
    }

    public async Task<IntegrationReviewReadiness> ReadinessAsync(FrontendAnalysisProfile profile, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IntegrationReviewReadiness>($"api/integration-review/readiness?{Scope(profile)}", Json, ct) ?? new IntegrationReviewReadiness { EnvironmentId = profile.Id };

    public async Task<IntegrationReviewResult> RunAsync(FrontendAnalysisProfile profile, CancellationToken ct = default)
    {
        var request = new IntegrationReviewRunRequest { EnvironmentId = profile.Id, EnvironmentName = profile.Name };
        var scope = $"environmentType={Uri.EscapeDataString(profile.EnvironmentType.ToString())}&targetUrl={Uri.EscapeDataString(profile.TargetUrl ?? "")}";
        return await Read<IntegrationReviewResult>(await http.PostAsJsonAsync($"api/integration-review/run?{scope}", request, Json, ct), ct);
    }

    public async Task<IReadOnlyList<IntegrationReviewRunSummary>> HistoryAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<List<IntegrationReviewRunSummary>>($"api/integration-review/runs?{Env(environmentId)}", Json, ct) ?? [];

    public async Task<IntegrationReviewResult?> GetRunAsync(Guid runId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IntegrationReviewResult>($"api/integration-review/runs/{runId}", Json, ct);

    public async Task<IntegrationMessageFlowPackage> GetMessageFlowReviewAsync(string environmentId, CancellationToken ct = default) =>
        await http.GetFromJsonAsync<IntegrationMessageFlowPackage>($"api/integration-message-flow/{Uri.EscapeDataString(environmentId)}", Json, ct)
        ?? new IntegrationMessageFlowPackage(new MessageFlowDefinition { EnvironmentId = environmentId, Name = "Message flow review" }, new(), new(false, 0, 0, [], []), [], []);

    public async Task<IntegrationMessageFlowPackage> SaveMessageFlowReviewAsync(string environmentId, IntegrationMessageFlowPackage package, CancellationToken ct = default) =>
        await Read<IntegrationMessageFlowPackage>(await http.PutAsJsonAsync($"api/integration-message-flow/{Uri.EscapeDataString(environmentId)}", package, Json, ct), ct);

    public async Task<MessageFlowSourceContractOptions?> MessageFlowSourceContractsAsync(string environmentId, Guid snapshotId, CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"api/integration-message-flow/{Uri.EscapeDataString(environmentId)}/source-contracts/{snapshotId}", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MessageFlowSourceContractOptions>(Json, ct);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
    }
}
