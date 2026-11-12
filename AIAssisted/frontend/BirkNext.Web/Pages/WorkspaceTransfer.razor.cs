using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.WorkspaceTransfer;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace BirkNext.Web.Pages;

/// <summary>
/// Backup &amp; Transfer: four areas (overview, export, import, backup) linked by ?area= (and ?kind= for export). The backend owns every decision;
/// this page only supplies the browser-held Target Environment list/profile and creates an imported profile as a disabled draft.
/// </summary>
public partial class WorkspaceTransfer : IDisposable
{
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    [Inject] public IWorkspaceTransferApiService Api { get; set; } = default!;
    [Inject] public IFrontendAnalysisSettingsService Settings { get; set; } = default!;
    [Inject] public NavigationManager Nav { get; set; } = default!;
    [Inject] public IJSRuntime Js { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "area")] public string? AreaQuery { get; set; }
    [SupplyParameterFromQuery(Name = "kind")] public string? KindQuery { get; set; }

    internal static readonly (string Key, string Label, string Hint)[] Areas =
    [
        ("overview", "Overview", "what to do, recent activity"),
        ("export", "Export", "project, integration or diagnostic package"),
        ("import", "Import", "preview, map, apply"),
        ("backup", "Backup & Restore", "full backup, isolated restore"),
    ];

    internal static readonly (string Area, string? Kind, string Title, string Text)[] Actions =
    [
        ("backup", null, "Create full backup", "Encrypted copy of every BirkNext table and managed file, for disaster recovery."),
        ("export", "project", "Export project", "Sources, contracts, tests, suites and review history of one project."),
        ("export", "integration", "Export integration", "Connected Systems, topology, stage sources, browser tests and Test Data of one environment."),
        ("import", null, "Import package", "Preview what changes, map the environment, then apply — nothing activates."),
        ("backup", null, "Restore backup", "Restore into a new isolated database and verify it, without touching this installation."),
    ];

    private bool _busy;
    private string? _error;
    private string? _message;
    private CancellationTokenSource _cts = new();

    // export
    private TransferScopesView? _scopes;
    private TransferKind _kind = TransferKind.ProjectWorkspace;
    private string _scopeSearch = "";
    private string? _projectKey;
    private string? _environmentId;
    private string? _suiteKey;
    private string? _flowId;
    private bool _includeEvidence;
    private bool _includeProfile = true;
    private bool _acknowledge;
    private bool _allowPartial;
    private ExportPreview? _exportPreview;
    private ExportResult? _exportResult;

    // import
    private ImportPreview? _importPreview;
    private ImportReport? _importReport;
    private readonly Dictionary<string, ImportAction> _decisions = [];
    private EnvironmentMappingAction _envAction = EnvironmentMappingAction.LeaveUnresolved;
    private string? _targetEnv;
    private bool _profileCreated;

    // backup
    private BackupStatusView? _status;
    private IReadOnlyList<BackupSummary> _backups = [];
    private IReadOnlyList<RestoreReport> _restores = [];
    private string _passphrase = "";
    private string _passphrase2 = "";
    private string? _note;
    private string? _openBackup;
    private string _restorePassphrase = "";
    private BackupInspection? _inspection;

    private IReadOnlyList<TransferOperationSummary> _operations = [];

    internal string Area => Areas.Any(a => a.Key == AreaQuery) ? AreaQuery! : "overview";
    internal string AreaHref(string key) => key == "overview" ? "workspace-transfer" : $"workspace-transfer?area={key}";
    internal static string ActionHref(string area, string? kind) => kind is null ? $"workspace-transfer?area={area}" : $"workspace-transfer?area={area}&kind={kind}";

    protected override async Task OnParametersSetAsync()
    {
        _error = null;
        if (!Settings.IsLoaded) await Settings.LoadAsync(Js);
        if (KindQuery is "integration") _kind = TransferKind.IntegrationWorkspace;
        else if (KindQuery is "diagnostic") _kind = TransferKind.Diagnostic;
        else if (KindQuery is "project") _kind = TransferKind.ProjectWorkspace;
        switch (Area)
        {
            case "overview": await LoadOperations(); break;
            case "export": if (_scopes is null) await LoadScopes(); break;
            case "backup": await LoadBackups(); break;
        }
    }

    private async Task Run(Func<CancellationToken, Task> action)
    {
        _busy = true;
        _error = null;
        _message = null;
        try { await action(_cts.Token); }
        catch (OperationCanceledException) { }
        finally { _busy = false; }
    }

    private Task LoadOperations() => Run(async ct => { var (ops, e) = await Api.OperationsAsync(ct); _operations = ops; _error = e; });

    private Task LoadScopes() => Run(async ct =>
    {
        var (scopes, e) = await Api.ScopesAsync(Settings.Settings.Profiles.Select(p => p.Id).ToList(), ct);
        _scopes = scopes;
        _error = e;
    });

    private async Task LoadBackups() => await Run(async ct =>
    {
        var (status, e1) = await Api.BackupStatusAsync(ct);
        var (backups, e2) = await Api.BackupsAsync(ct);
        var (restores, e3) = await Api.RestoresAsync(ct);
        (_status, _backups, _restores, _error) = (status, backups, restores, e1 ?? e2 ?? e3);
    });

    // ───── export ─────

    internal IEnumerable<TransferProjectScopeView> FilteredProjects => (_scopes?.Projects ?? []).Where(p => Matches(p.DisplayName) || Matches(p.ProjectKey));
    internal IEnumerable<TransferEnvironmentScopeView> FilteredEnvironments => (_scopes?.Environments ?? []).Where(e => Matches(EnvironmentName(e.EnvironmentId)) || Matches(e.EnvironmentId));
    private bool Matches(string text) => string.IsNullOrWhiteSpace(_scopeSearch) || text.Contains(_scopeSearch.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The profile's display name when this browser knows the environment; otherwise its id (never a guessed name).</summary>
    internal string EnvironmentName(string id) => Settings.Settings.Profiles.FirstOrDefault(p => p.Id == id)?.Name ?? id;

    private bool CanPreview => _kind switch
    {
        TransferKind.ProjectWorkspace => _projectKey is not null,
        TransferKind.IntegrationWorkspace => _environmentId is not null,
        _ => !string.IsNullOrWhiteSpace(_suiteKey) || !string.IsNullOrWhiteSpace(_flowId),
    };

    private void ChooseKind(TransferKind kind)
    {
        _kind = kind;
        _scopeSearch = "";
        ResetExport();
    }

    private void ResetExport()
    {
        _exportPreview = null;
        _exportResult = null;
        _acknowledge = false;
        _allowPartial = false;
    }

    private ExportRequest BuildRequest() => new()
    {
        Kind = _kind,
        ProjectKey = _kind == TransferKind.ProjectWorkspace ? _projectKey : null,
        EnvironmentId = _kind == TransferKind.IntegrationWorkspace ? _environmentId : null,
        SuiteKey = _kind == TransferKind.Diagnostic && !string.IsNullOrWhiteSpace(_suiteKey) ? _suiteKey.Trim() : null,
        FlowId = _kind == TransferKind.Diagnostic && !string.IsNullOrWhiteSpace(_flowId) ? _flowId.Trim() : null,
        IncludeEvidence = _kind != TransferKind.Diagnostic && _includeEvidence,
        // The browser holds Target Environment profiles: secrets are [JsonIgnore] there and the server sanitises again.
        TargetProfileJson = _kind == TransferKind.IntegrationWorkspace && _includeProfile && Settings.Settings.Profiles.FirstOrDefault(p => p.Id == _environmentId) is { } profile
            ? JsonSerializer.Serialize(profile) : null,
        AcknowledgeSensitive = _acknowledge,
        AllowPartial = _allowPartial,
    };

    private Task PreviewExport() => Run(async ct =>
    {
        _exportResult = null;
        // An acknowledgement covers the findings the tester saw; a new review can find others, so it starts unacknowledged.
        _acknowledge = false;
        _allowPartial = false;
        var (preview, e) = await Api.PreviewExportAsync(BuildRequest(), ct);
        _exportPreview = preview;
        _error = e;
    });

    private Task CreatePackage() => Run(async ct =>
    {
        var (result, e) = await Api.ExportAsync(BuildRequest(), ct);
        _exportResult = result;
        _error = e;
        if (result is not null) _message = "Package created. Download it and import it in the other BirkNext.";
    });

    // ───── import ─────

    private Task StagePackage(InputFileChangeEventArgs e) => Run(async ct =>
    {
        _importPreview = null;
        _importReport = null;
        _decisions.Clear();
        _envAction = EnvironmentMappingAction.LeaveUnresolved;
        _targetEnv = null;
        _profileCreated = false;
        if (e.File.Size > MaxPackageBytes) { _error = $"The package is larger than {MaxPackageBytes / (1 << 20)} MB."; return; }
        await using var stream = e.File.OpenReadStream(MaxPackageBytes, ct);
        var (staged, error) = await Api.StageAsync(stream, ct);
        if (staged is null) { _error = error; return; }
        var candidates = Settings.Settings.Profiles.Select(p => new EnvironmentCandidate(p.Id, p.Name, p.EnvironmentType.ToString())).ToList();
        var (preview, previewError) = await Api.PreviewImportAsync(staged.OperationId, candidates, ct);
        _importPreview = preview;
        _error = previewError;
    });

    internal ImportAction Decision(ImportItem item) => _decisions.TryGetValue(item.Key, out var a) ? a : item.DefaultAction;

    private Task ApplyImport() => Run(async ct =>
    {
        var preview = _importPreview!;
        var (report, e) = await Api.ApplyImportAsync(new ImportApplyRequest
        {
            OperationId = preview.OperationId, PlanFingerprint = preview.PlanFingerprint, Decisions = new(_decisions), EnvironmentAction = _envAction,
            TargetEnvironmentId = _envAction == EnvironmentMappingAction.ReuseVerified ? _targetEnv : null,
        }, ct);
        _importReport = report;
        _error = e;
    });

    /// <summary>Creates the imported Target Environment as a NEW profile with the backend's environment id. Verification and adoption state are
    /// never carried over; the name says it needs revalidation.</summary>
    private async Task CreateDraftProfile()
    {
        if (_importReport?.ProfileDraftJson is not { } json || _importReport.DestinationEnvironmentId is not { } id) return;
        if (Settings.Settings.Profiles.Any(p => p.Id == id)) { _message = "This environment already exists in this browser."; _profileCreated = true; return; }
        try
        {
            var profile = JsonSerializer.Deserialize<FrontendAnalysisProfile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            profile.Id = id;
            profile.Name = $"{(string.IsNullOrWhiteSpace(profile.Name) ? "Imported environment" : profile.Name)} (imported – revalidate)";
            profile.ManualVerification = null;
            profile.AuthenticationAdoption = null;
            Settings.Settings.Profiles.Add(profile);
            await Settings.SaveAsync(Js);
            _profileCreated = true;
            _message = "Draft Target Environment created. Open Target Environments to set up credentials and check the target before use.";
        }
        catch (JsonException) { _error = "The imported profile could not be read; create the environment by hand."; }
    }

    // ───── backup ─────

    private bool _showPassphrase;
    private bool _creating;

    private bool PassphraseMismatch => _passphrase2.Length > 0 && _passphrase != _passphrase2;
    private bool PassphraseTooShort => _passphrase.Length is > 0 and < 12;
    private BackupReadiness? Readiness => _status?.Readiness;
    private bool BackupReady => Readiness?.Backup == BackupReadinessStatus.Ready;
    private bool RestoreReady => Readiness?.Restore == BackupReadinessStatus.Ready;

    /// <summary>Why the primary action cannot run now, or null. Shown next to the disabled button (never a silent disabled state).</summary>
    private string? CreateBlockedReason =>
        _status is null ? "Checking whether backups can run…"
        : !BackupReady ? (Readiness is null ? "Backups cannot run yet: readiness could not be read." : "Backups cannot run yet: resolve the blocker shown above, then Retry detection.")
        : _passphrase.Length < 12 ? "Enter a passphrase of at least 12 characters."
        : _passphrase != _passphrase2 ? "Repeat the same passphrase."
        : null;

    private static string ReadyClass(BackupReadinessStatus status) => status switch
    {
        BackupReadinessStatus.Ready => "wtx-ok",
        BackupReadinessStatus.NotChecked => "wtx-neutral",
        BackupReadinessStatus.Incompatible or BackupReadinessStatus.Blocked => "wtx-warn",
        _ => "wtx-bad",
    };

    private static string ReadyLabel(BackupReadinessStatus status) => status switch
    {
        BackupReadinessStatus.Ready => "Ready",
        BackupReadinessStatus.Blocked => "Blocked",
        BackupReadinessStatus.Unavailable => "Unavailable",
        BackupReadinessStatus.Incompatible => "Incompatible",
        _ => "Not checked",
    };

    private bool ToolsMissing => Readiness?.Items.Any(i => i.Key is "pg_dump" && i.Status == BackupReadinessStatus.Unavailable) == true;
    private string? ToolSummary => Readiness?.DumpToolVersion is { } dv ? $"{dv.Replace("pg_dump (PostgreSQL) ", "pg_dump ")} · {Readiness.ToolProvider}" : Readiness?.ToolProvider;
    private string PassphraseState => PassphraseMismatch ? "The passphrases are different." : PassphraseTooShort ? "Too short: at least 12 characters." : "";

    private BackupReadinessStatus Item(string key) => Readiness?.Items.FirstOrDefault(i => i.Key == key)?.Status ?? BackupReadinessStatus.NotChecked;
    private BackupReadinessStatus ToolsStatus => Readiness is null ? BackupReadinessStatus.NotChecked
        : Readiness.Items.FirstOrDefault(i => i.Key == "tools")?.Status ?? Worst(Item("pg_dump"), Item("compatibility"), Readiness.Items.Any(i => i.Key == "identity") ? Item("identity") : BackupReadinessStatus.Ready);

    private static BackupReadinessStatus Worst(params BackupReadinessStatus[] all) =>
        all.FirstOrDefault(s => s is BackupReadinessStatus.Blocked or BackupReadinessStatus.Unavailable or BackupReadinessStatus.Incompatible,
            all.Contains(BackupReadinessStatus.NotChecked) ? BackupReadinessStatus.NotChecked : BackupReadinessStatus.Ready);

    private Task RetryDetection() => Run(async ct =>
    {
        var (status, e) = await Api.BackupStatusAsync(ct);
        (_status, _error) = (status ?? _status, e);
        if (status is not null) _message = BackupReady ? "Backup tools found and verified." : "Checked again: backups still cannot run.";
    });

    private Task CreateBackup() => Run(async ct =>
    {
        if (CreateBlockedReason is { } blocked) { _error = blocked; return; }
        _creating = true;
        try { await CreateBackupCore(ct); } finally { _creating = false; }
    });

    private async Task CreateBackupCore(CancellationToken ct)
    {
        var (result, e) = await Api.CreateBackupAsync(_passphrase, _note, ct);
        _error = e;
        if (result is null) return;
        (_passphrase, _passphrase2, _note) = ("", "", null);
        _message = $"Backup created: {result.Tables} tables, {result.Rows:N0} rows, {result.ManagedFiles} managed files ({Pretty(result.BackupCompleteness)}).{(result.Warnings.Count > 0 ? " " + string.Join(" ", result.Warnings) : "")}";
        var (backups, _) = await Api.BackupsAsync(ct);
        _backups = backups;
    }

    private void OpenBackup(string fileName)
    {
        _openBackup = _openBackup == fileName ? null : fileName;
        _inspection = null;
        _restorePassphrase = "";
    }

    private Task InspectBackup(string fileName) => Run(async ct =>
    {
        var (inspection, e) = await Api.InspectBackupAsync(fileName, string.IsNullOrEmpty(_restorePassphrase) ? null : _restorePassphrase, ct);
        _inspection = inspection;
        _error = e;
    });

    private Task Restore(string fileName) => Run(async ct =>
    {
        var (report, e) = await Api.RestoreAsync(fileName, _restorePassphrase, ct);
        _error = e;
        _restorePassphrase = "";
        if (report is not null) _message = $"Restore {Pretty(report.Verdict)}{(string.IsNullOrEmpty(report.RestoredDatabase) ? "" : $" into {report.RestoredDatabase}")}. The running installation is unchanged.";
        var (restores, _) = await Api.RestoresAsync(ct);
        _restores = restores;
    });

    // ───── presentation helpers ─────

    internal static string Pretty(string? value) => string.IsNullOrEmpty(value) ? "" :
        string.Join(' ', System.Text.RegularExpressions.Regex.Replace(value.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((w, i) => i == 0 ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w.ToLowerInvariant()));

    internal static string TypeName(TransferKind kind) => kind switch
    {
        TransferKind.ProjectWorkspace => "Project", TransferKind.IntegrationWorkspace => "Integration", TransferKind.Diagnostic => "Diagnostic", _ => "Backup",
    };

    internal static string TypeLabel(string type) => type switch
    {
        PackageTypeNames.Snapshot => "source snapshot", PackageTypeNames.Contract => "contract", PackageTypeNames.ParquetRules => "Parquet rule set",
        PackageTypeNames.Suite => "integration suite", PackageTypeNames.Composite => "composite source context", PackageTypeNames.Association => "project association",
        PackageTypeNames.ConnectedSystems => "Connected Systems setup", PackageTypeNames.Platform => "platform", PackageTypeNames.Integration => "integration definition",
        PackageTypeNames.EvidenceBinding => "source binding", PackageTypeNames.ContractArtifact => "contract artifact", PackageTypeNames.GraphQlSchema => "GraphQL schema",
        PackageTypeNames.MessageFlows => "message flows", PackageTypeNames.Flow => "browser test", PackageTypeNames.Module => "browser test module",
        PackageTypeNames.TestDataAsset => "Test Data asset", PackageTypeNames.TestDataValues => "Test Data values", PackageTypeNames.Profile => "Target Environment profile",
        PackageTypeNames.History => "historical record", PackageTypeNames.SourceDecision => "source decision history", _ => type,
    };

    internal static string DependencyLabel(DependencyStatus s) => s switch
    {
        DependencyStatus.ExternalReference => "Referenced", DependencyStatus.Missing => "Missing", DependencyStatus.Optional => "Optional",
        DependencyStatus.Unauthorized => "Not permitted", DependencyStatus.Unsupported => "Unsupported", _ => "Included",
    };

    internal static int StatusOrder(ImportStatus s) => s switch
    {
        ImportStatus.ConflictingContent => 0, ImportStatus.Unauthorized => 1, ImportStatus.Unsupported => 2, ImportStatus.MissingDependency => 3,
        ImportStatus.EnvironmentRebindRequired => 4, ImportStatus.NewerRevision => 5, ImportStatus.OlderRevision => 6, ImportStatus.New => 7, _ => 8,
    };

    internal static string StatusClass(ImportStatus s) => s switch
    {
        ImportStatus.New or ImportStatus.Identical => "wtx-ok", ImportStatus.NewerRevision or ImportStatus.OlderRevision or ImportStatus.EnvironmentRebindRequired => "wtx-info",
        ImportStatus.MissingDependency => "wtx-warn", _ => "wtx-bad",
    };

    internal static string CheckClass(TransferCheckStatus s) => s switch { TransferCheckStatus.Pass => "wtx-ok", TransferCheckStatus.Warning => "wtx-warn", TransferCheckStatus.Fail => "wtx-bad", _ => "wtx-neutral" };
    internal static string ReconcileClass(ReconciliationStatus s) => s switch { ReconciliationStatus.Resolved => "wtx-ok", ReconciliationStatus.NeedsRevalidation => "wtx-info", ReconciliationStatus.Unresolved => "wtx-warn", _ => "wtx-bad" };
    /// <summary>Colour by meaning, not by word: "Yes" is good for SOURCE_READY and bad for EXECUTOR_UNAVAILABLE.</summary>
    internal static string ReadinessClass(ReadinessDimension r) => r.Status switch
    {
        "Not applicable" => "wtx-neutral",
        "Yes" when r.Dimension == "SOURCE_READY" => "wtx-ok",
        "No" when r.Dimension is "EXECUTOR_UNAVAILABLE" or "OBSERVER_UNAVAILABLE" or "DOMAIN_DECISION_REQUIRED" => "wtx-ok",
        "Imported, disabled" => "wtx-info",
        _ => "wtx-warn",
    };
    internal static string OperationClass(string state) => state switch { "COMPLETED" or "CREATED" => "wtx-ok", "PREVIEWED" or "STAGED" or "APPLYING" or "APPROVED" => "wtx-info", _ => "wtx-bad" };
    internal static string Size(long bytes) => bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):N1} MB" : $"{Math.Max(1, bytes / 1024):N0} KB";
    internal static string ShortCommit(string commit) => commit.Length >= 12 && commit != "unknown" ? commit[..12] : commit;

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

/// <summary>The package object type names (mirrors the backend's PackageTypes; only used for labels).</summary>
public static class PackageTypeNames
{
    public const string Snapshot = "source-snapshot", Contract = "contract", ParquetRules = "parquet-rule-set", Suite = "integration-suite", Composite = "composite-source-context",
        Association = "source-association", ConnectedSystems = "connected-systems", Platform = "catalog-platform", Integration = "catalog-integration", EvidenceBinding = "evidence-binding",
        ContractArtifact = "contract-artifact", GraphQlSchema = "graphql-schema", MessageFlows = "message-flows", Flow = "critical-e2e-flow", Module = "critical-e2e-module",
        TestDataAsset = "test-data-asset", TestDataValues = "test-data-values", Profile = "environment-profile", History = "historical-record",
        SourceDecision = "source-decision";
}
