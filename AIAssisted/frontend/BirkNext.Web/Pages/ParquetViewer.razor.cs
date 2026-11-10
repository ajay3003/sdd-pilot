using System.Globalization;
using System.Text.Json;
using BirkNext.ExecutionEvidence;
using BirkNext.Parquet;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;
using BirkNext.Web.Services;

namespace BirkNext.Web.Pages;

public partial class ParquetViewer
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly (string Key, string Label)[] Areas = [("overview", "Quality Overview"), ("validation", "Validation"), ("explore", "Explore & Compare"), ("evidence", "Evidence & History")];
    private static readonly (ParquetRuleKind Kind, string Label)[] EditorKinds =
    [
        (ParquetRuleKind.NotNull, "Required (never empty)"), (ParquetRuleKind.RequiredWhen, "Required when another column has a value"), (ParquetRuleKind.Unique, "Unique (one or more columns)"),
        (ParquetRuleKind.AllowedValues, "Allowed values"), (ParquetRuleKind.Range, "Range (number or date)"), (ParquetRuleKind.Pattern, "Date/period format"), (ParquetRuleKind.RecordCount, "Row count"),
    ];
    private const string RecentKey = "birknext:parquet-recent";
    private const int PageSize = 50;

    [SupplyParameterFromQuery(Name = "area")] public string? AreaParam { get; set; }

    private string _area = "overview", _validationView = "rules", _exploreView = "data", _baselineMode = "file", _recordFilter = "Changed";
    private ParquetOverview? _current, _baselineFile;
    private List<ParquetOverview> _recent = [];
    private ContractDiscovery? _discovery;
    private ContractAssociation? _association;
    private string? _projectFilter;
    private readonly Dictionary<string, string> _declared = new(StringComparer.Ordinal);
    private List<ParquetRule> _rules = [];
    private string _ruleSetName = "Rules";
    private readonly HashSet<string> _approve = new(StringComparer.Ordinal);
    private ParquetQualityResult? _result;
    private string? _selectedRule;
    private ParquetManifestSummary? _manifest;
    private string? _manifestText;
    // Data
    private ParquetRowsResult? _rows;
    private long _offset;
    private bool _fullScan, _filterOpen;
    private List<ParquetFilter> _filters = [];
    private List<string> _columns = [];
    private string? _columnSearch, _fColumn, _fValue, _fValue2, _drillFrom;
    private ParquetFilterOperator _fOp = ParquetFilterOperator.Equals;
    private int? _detailRow;
    // Compare
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private Guid? _baselineEvidenceId;
    private ParquetBaselineResult? _baseline;
    // Rule editor
    private bool _editor;
    private ParquetRuleKind _rKind = ParquetRuleKind.NotNull;
    private string? _rColumn, _rExpected, _rMin, _rMax, _rValues, _rWhenColumn, _rWhenValues, _ruleError;
    private readonly HashSet<string> _rColumns = new(StringComparer.Ordinal);
    private ParquetRuleResult? _preview;
    // Evidence
    private List<ParquetInspectionEvidence>? _saved;
    private ParquetInspectionEvidence? _openEvidence;
    private IReadOnlyList<ExecutionRunSummary>? _runs;
    private string? _runSearch, _manualReason, _note, _profileId, _error, _busyText;
    private DateOnly? _runFrom;
    private Guid? _runId, _deleteId;
    private bool _manual, _busy;

    protected override async Task OnInitializedAsync()
    {
        try { _profileId = (await ContextFactory.GetActiveContextAsync())?.ActiveProfileOrNull?.Id; } catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { }
        if (Areas.Any(a => a.Key == AreaParam)) _area = AreaParam!;
        await LoadRecentAsync();
        if (_area == "evidence") await LoadEvidenceAsync();
    }

    // ── Files ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task OpenAsync(InputFileChangeEventArgs e)
    {
        _error = null;
        if (e.File.Size > ParquetApiService.MaxUploadBytes) { _error = "The file exceeds 64 MB (the upload limit). Nothing was uploaded."; return; }
        await Busy("Opening the file…", async () =>
        {
            await using var stream = e.File.OpenReadStream(ParquetApiService.MaxUploadBytes);
            var (overview, error) = await Api.OpenAsync(e.File.Name, stream);
            if (overview is null) { _error = error; return; }
            await StartAsync(overview);
            await RememberAsync(overview.SessionId);
        });
    }

    private async Task ResumeAsync(ParquetOverview overview) => await Busy("Opening the file…", () => StartAsync(overview));

    private async Task StartAsync(ParquetOverview overview)
    {
        _current = overview;
        _discovery = null; _association = null; _result = null; _rules = []; _approve.Clear(); _declared.Clear(); _manifest = null; _baseline = null; _baselineFile = null;
        _rows = null; _offset = 0; _filters = []; _drillFrom = null; _keys.Clear(); _selectedRule = null; _projectFilter = null;
        _columns = overview.Columns.Where(c => c.Readable).Take(8).Select(c => c.Path).ToList();
        if (_area == "evidence") _area = "overview";
        var (discovery, error) = await Api.DiscoverAsync(overview.SessionId, null);
        _discovery = discovery;
        if (error is not null) _error = error;
        // Only an authoritative, unambiguous match is proposed automatically (still to be confirmed by the tester).
        if (discovery is { Outcome: ContractMatchKind.AuthoritativeContract, Candidates: [var top, ..] }) await UseCandidateAsync(top);
        else await RefreshAsync();
    }

    private async Task CloseFileAsync()
    {
        if (_current is { } c) await Api.CloseAsync(c.SessionId);
        if (_baselineFile is { } b) await Api.CloseAsync(b.SessionId);
        _current = null; _baselineFile = null; _result = null; _association = null; _discovery = null;
        await LoadRecentAsync();
    }

    private async Task LoadRecentAsync()
    {
        try
        {
            var raw = await JS.InvokeAsync<string?>("localStorage.getItem", RecentKey);
            var ids = string.IsNullOrWhiteSpace(raw) ? [] : JsonSerializer.Deserialize<List<Guid>>(raw) ?? [];
            if (ids.Count == 0) { _recent = []; return; }
            var (sessions, _) = await Api.SessionsAsync(ids);
            _recent = sessions ?? [];
        }
        catch (Exception ex) when (ex is JSException or JsonException or InvalidOperationException or TaskCanceledException) { _recent = []; }
    }

    private async Task RememberAsync(Guid id)
    {
        try
        {
            var raw = await JS.InvokeAsync<string?>("localStorage.getItem", RecentKey);
            var ids = (string.IsNullOrWhiteSpace(raw) ? [] : JsonSerializer.Deserialize<List<Guid>>(raw) ?? []).Where(x => x != id).Prepend(id).Take(10).ToList();
            await JS.InvokeVoidAsync("localStorage.setItem", RecentKey, JsonSerializer.Serialize(ids));
        }
        catch (Exception ex) when (ex is JSException or JsonException or InvalidOperationException or TaskCanceledException) { }
    }

    private async Task GoAsync(string area)
    {
        _area = area;
        if (area == "evidence") await LoadEvidenceAsync();
        if (area == "explore" && _exploreView == "data" && _rows is null) await LoadRowsAsync(0);
        Nav.NavigateTo(Nav.GetUriWithQueryParameter("area", area), replace: true);
    }

    // ── Contract ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task FilterProjectAsync(string? project)
    {
        if (_current is null) return;
        _projectFilter = string.IsNullOrWhiteSpace(project) ? null : project;
        await Busy("Looking for contracts…", async () => { (_discovery, var error) = await Api.DiscoverAsync(_current.SessionId, _projectFilter); _error = error; });
    }

    private async Task SelectAsync(ContractCandidate candidate) => await Busy("Checking the contract…", () => UseCandidateAsync(candidate));

    private async Task UseCandidateAsync(ContractCandidate c)
    {
        if (_current is null) return;
        var requested = new ContractAssociation
        {
            ProjectKey = c.Source.ProjectKey, SnapshotId = c.Source.SnapshotId, ContractKey = c.Contract.Key, ContractVersion = c.Contract.Version,
            DocumentPath = c.Contract.DocumentPath, DocumentSha256 = c.Contract.DocumentSha256,
        };
        var (association, error) = await Api.AssociateAsync(_current.SessionId, requested);
        if (association is null) { _error = error; return; }
        _association = association; _declared.Clear();
        await GenerateAndReviewAsync();
    }

    private void Declare(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual)) _declared.Remove(expected); else _declared[expected] = actual;
    }

    private async Task ApplyMappingsAsync()
    {
        if (_current is null || _association is null) return;
        await Busy("Applying the declared mappings…", async () =>
        {
            var requested = _association with { ColumnMappings = [.. _declared.Select(kv => new ColumnMapping(kv.Value, kv.Key, "Declared", "declared by the tester"))] };
            var (association, error) = await Api.AssociateAsync(_current.SessionId, requested);
            if (association is null) { _error = error; return; }
            _association = association with { ReviewStatus = _association.ReviewStatus };
            _declared.Clear();
            await GenerateAndReviewAsync();
        });
    }

    private async Task ConfirmAsync()
    {
        if (_association is null) return;
        _association = _association with { ReviewStatus = "Confirmed" };
        await Busy("Updating the review…", RefreshAsync);
    }

    private async Task ClearContractAsync()
    {
        _association = null;
        await Busy("Generating schema rules…", GenerateAndReviewAsync);
    }

    private async Task AttachManifestAsync()
    {
        if (_current is null || string.IsNullOrWhiteSpace(_manifestText)) return;
        await Busy("Attaching the manifest…", async () =>
        {
            var (manifest, error) = await Api.AttachManifestAsync(_current.SessionId, _manifestText);
            if (manifest is null) { _error = error; return; }
            _manifest = manifest; _manifestText = null;
            (_discovery, _) = await Api.DiscoverAsync(_current.SessionId, _projectFilter);
            await RefreshAsync();
        });
    }

    // ── Rules and validation ─────────────────────────────────────────────────────────────────────────────────────────────

    private async Task GenerateAndReviewAsync()
    {
        if (_current is null) return;
        var (generation, error) = await Api.GenerateAsync(_current.SessionId, _association);
        if (generation is null) { _error = error; return; }
        var own = _rules.Where(r => r.Origin == RuleOrigin.UserAuthored).ToList();
        _rules = [.. generation.Rules, .. own.Where(o => generation.Rules.All(g => g.Id != o.Id))];
        _ruleSetName = generation.RuleSetName;
        _approve.Clear();
        await RefreshAsync();
    }

    private async Task RegenerateAsync() => await Busy("Generating rules…", GenerateAndReviewAsync);

    /// <summary>Runs the active rules (the backend skips rules awaiting review) and refreshes coverage, dimensions and statistics.</summary>
    private async Task RefreshAsync()
    {
        if (_current is null) return;
        var (result, error) = await Api.ReviewAsync(_current.SessionId, new ParquetQualityRequest { Association = _association, Rules = new ParquetRuleSet { Name = _ruleSetName, Rules = [.. _rules] } });
        if (result is null) { _error = error; return; }
        _result = result; _error = null;
        if (result.Manifest is not null) _manifest = result.Manifest;
    }

    private async Task RunAsync() => await Busy("Validating every row…", RefreshAsync);

    private async Task ProfileAsync() => await Busy("Profiling every row…", RefreshAsync);

    private void ToggleApprove(string id, bool on) { if (on) _approve.Add(id); else _approve.Remove(id); }

    private async Task ApproveAsync(bool reject)
    {
        if (_current is null || _association is null || _approve.Count == 0) return;
        await Busy(reject ? "Recording the rejection…" : "Approving the rules…", async () =>
        {
            var rules = _rules.Where(r => r.Origin != RuleOrigin.SchemaDerived && (r.Review != RuleReviewState.NeedsReview || _approve.Contains(r.Id)))
                .Select(r => _approve.Contains(r.Id) ? r with { Review = reject ? RuleReviewState.Rejected : RuleReviewState.Active } : r).ToList();
            var (version, error) = await Api.ApproveRulesAsync(_current.SessionId, new ParquetApprovalRequest(_association,
                new ParquetRuleSetApproval(_association.ProjectKey, _association.ContractKey, _association.ContractVersion, _ruleSetName, rules, null)));
            if (version is null) { _error = error; return; }
            await GenerateAndReviewAsync();
        });
    }

    private void OpenEditor() { _editor = true; _preview = null; _ruleError = null; _validationView = "rules"; }

    private ParquetRule? BuildRule()
    {
        var lines = (string? s) => (s ?? "").Split(['\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        var id = $"own.{_rules.Count(r => r.Origin == RuleOrigin.UserAuthored) + 1}-{_rKind.ToString().ToLowerInvariant()}";
        var rule = _rKind switch
        {
            ParquetRuleKind.Unique => new ParquetRule { Id = id, Kind = _rKind, Columns = [.. _rColumns] },
            ParquetRuleKind.RecordCount => new ParquetRule { Id = id, Kind = _rKind, Expected = _rExpected },
            ParquetRuleKind.AllowedValues => new ParquetRule { Id = id, Kind = _rKind, Column = _rColumn, Values = lines(_rValues) },
            ParquetRuleKind.Range => new ParquetRule { Id = id, Kind = _rKind, Column = _rColumn, Min = string.IsNullOrWhiteSpace(_rMin) ? null : _rMin.Trim(), Max = string.IsNullOrWhiteSpace(_rMax) ? null : _rMax.Trim() },
            ParquetRuleKind.Pattern => new ParquetRule { Id = id, Kind = _rKind, Column = _rColumn, Expected = _rExpected },
            ParquetRuleKind.RequiredWhen => new ParquetRule { Id = id, Kind = _rKind, Column = _rColumn, WhenColumn = _rWhenColumn, WhenValues = lines(_rWhenValues) },
            _ => new ParquetRule { Id = id, Kind = _rKind, Column = _rColumn },
        };
        _ruleError = Problem(rule);
        return _ruleError is null ? rule with { Origin = RuleOrigin.UserAuthored, Severity = "Medium", Provenance = "authored by the tester" } : null;
    }

    private static string? Problem(ParquetRule r) => r.Kind switch
    {
        ParquetRuleKind.Unique when r.Columns.Count == 0 => "Choose one or more columns.",
        ParquetRuleKind.RecordCount when !long.TryParse(r.Expected, NumberStyles.Integer, Inv, out var n) || n < 0 => "Give a whole number of rows.",
        ParquetRuleKind.AllowedValues when string.IsNullOrWhiteSpace(r.Column) || r.Values.Count == 0 => "Choose a column and at least one value.",
        ParquetRuleKind.Range when string.IsNullOrWhiteSpace(r.Column) || (r.Min is null && r.Max is null) => "Choose a column and at least one bound.",
        ParquetRuleKind.Pattern when string.IsNullOrWhiteSpace(r.Column) || string.IsNullOrWhiteSpace(r.Expected) => "Choose a column and a format.",
        ParquetRuleKind.RequiredWhen when string.IsNullOrWhiteSpace(r.Column) || string.IsNullOrWhiteSpace(r.WhenColumn) || r.WhenValues.Count == 0 => "Choose the column, the condition column and its values.",
        ParquetRuleKind.NotNull when string.IsNullOrWhiteSpace(r.Column) => "Choose a column.",
        _ => null,
    };

    /// <summary>Runs only this rule (nothing is saved) so the tester sees what it does before adding it.</summary>
    private async Task PreviewRuleAsync()
    {
        if (_current is null || BuildRule() is not { } rule) return;
        await Busy("Checking the rule…", async () =>
        {
            var (validation, error) = await Api.ValidateAsync(_current.SessionId, new ParquetRuleSet { Name = "preview", Rules = [rule] });
            _preview = validation?.Results.FirstOrDefault();
            _ruleError = error;
        });
    }

    private void AddRule()
    {
        if (BuildRule() is not { } rule) return;
        _rules.Add(rule);
        _editor = false; _preview = null; _rColumn = null; _rExpected = null; _rMin = null; _rMax = null; _rValues = null; _rWhenColumn = null; _rWhenValues = null; _rColumns.Clear();
    }

    private void RemoveRule(string id) { _rules.RemoveAll(r => r.Id == id && r.Origin == RuleOrigin.UserAuthored); _selectedRule = null; }

    private async Task DrillAsync(ParquetRule rule, ParquetRuleResult result)
    {
        _filters = [.. result.DrillFilters];
        _drillFrom = RuleName(rule);
        _fullScan = true;
        _area = "explore"; _exploreView = "data";
        foreach (var f in _filters.Where(f => !_columns.Contains(f.Column))) _columns.Insert(0, f.Column);
        await LoadRowsAsync(0);
    }

    // ── Data ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task ExploreAsync(string view)
    {
        _exploreView = view;
        if (view == "data" && _rows is null) await LoadRowsAsync(0);
        if (view == "compare" && _saved is null) await LoadEvidenceAsync();
    }

    private void ToggleColumn(string path, bool on) { if (on && !_columns.Contains(path)) _columns.Add(path); else if (!on) _columns.Remove(path); }

    private async Task LoadRowsAsync(long offset)
    {
        if (_current is null) return;
        await Busy(_fullScan && _filters.Count > 0 ? "Scanning the whole file…" : "Reading rows…", async () =>
        {
            var (rows, error) = await Api.RowsAsync(_current.SessionId, new ParquetRowsRequest { Offset = offset, Count = PageSize, Columns = [.. _columns], Filters = [.. _filters], FullScan = _fullScan && _filters.Count > 0 });
            if (rows is not null) { _rows = rows; _offset = offset; _detailRow = null; }
            _error = error;
        });
    }

    private bool HasNext(ParquetOverview o, ParquetRowsResult r) => _fullScan && _filters.Count > 0 ? r.Truncated : _offset + PageSize < o.Rows;

    private async Task SetScopeAsync(bool full) { _fullScan = full; await LoadRowsAsync(0); }

    private async Task AddFilterAsync()
    {
        if (string.IsNullOrWhiteSpace(_fColumn)) return;
        var values = _fOp is ParquetFilterOperator.In or ParquetFilterOperator.NotIn ? (_fValue ?? "").Split(['\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList() : null;
        if (_fOp is not (ParquetFilterOperator.IsNull or ParquetFilterOperator.NotNull) && string.IsNullOrWhiteSpace(_fValue)) { _error = "Give a value for the filter."; return; }
        if (_fOp == ParquetFilterOperator.Between && string.IsNullOrWhiteSpace(_fValue2)) { _error = "Give both ends of the range."; return; }
        _filters.Add(new ParquetFilter(_fColumn, _fOp, values is null ? _fValue?.Trim() : null, _fOp == ParquetFilterOperator.Between ? _fValue2?.Trim() : null, values));
        _fValue = null; _fValue2 = null; _filterOpen = false; _error = null;
        await LoadRowsAsync(0);
    }

    private async Task RemoveFilterAsync(ParquetFilter f)
    {
        _filters.Remove(f);
        if (_filters.Count == 0) _drillFrom = null;
        await LoadRowsAsync(0);
    }

    private static IEnumerable<ParquetFilterOperator> Operators(ParquetOverview o, string column)
    {
        var c = o.Columns.FirstOrDefault(x => x.Path == column);
        var text = c is { PhysicalType: "BYTE_ARRAY" } && c.LogicalType is null or "STRING" or "ENUM" or "JSON";
        return text
            ? [ParquetFilterOperator.Equals, ParquetFilterOperator.NotEquals, ParquetFilterOperator.Contains, ParquetFilterOperator.In, ParquetFilterOperator.NotIn, ParquetFilterOperator.IsNull, ParquetFilterOperator.NotNull]
            : [ParquetFilterOperator.Equals, ParquetFilterOperator.NotEquals, ParquetFilterOperator.GreaterThan, ParquetFilterOperator.LessThan, ParquetFilterOperator.Between, ParquetFilterOperator.In, ParquetFilterOperator.NotIn, ParquetFilterOperator.IsNull, ParquetFilterOperator.NotNull];
    }

    private static string OperatorLabel(ParquetFilterOperator op) => op switch
    {
        ParquetFilterOperator.Equals => "equals", ParquetFilterOperator.NotEquals => "is not", ParquetFilterOperator.Contains => "contains", ParquetFilterOperator.GreaterThan => "greater than",
        ParquetFilterOperator.LessThan => "less than", ParquetFilterOperator.Between => "between", ParquetFilterOperator.In => "is one of", ParquetFilterOperator.NotIn => "is none of",
        ParquetFilterOperator.IsNull => "is empty", ParquetFilterOperator.NotNull => "is not empty", _ => op.ToString(),
    };

    private static string FilterText(ParquetFilter f) => f.Operator switch
    {
        ParquetFilterOperator.IsNull or ParquetFilterOperator.NotNull => $"{f.Column} {OperatorLabel(f.Operator)}",
        ParquetFilterOperator.Between => $"{f.Column} between {f.Value} and {f.Value2}",
        ParquetFilterOperator.In or ParquetFilterOperator.NotIn => $"{f.Column} {OperatorLabel(f.Operator)} {string.Join(", ", (f.Values ?? []).Take(6))}{((f.Values?.Count ?? 0) > 6 ? "…" : "")}",
        _ => $"{f.Column} {OperatorLabel(f.Operator)} {f.Value}",
    };

    // ── Compare ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task OpenBaselineAsync(InputFileChangeEventArgs e)
    {
        if (e.File.Size > ParquetApiService.MaxUploadBytes) { _error = "The baseline file exceeds 64 MB."; return; }
        await Busy("Opening the baseline…", async () =>
        {
            await using var stream = e.File.OpenReadStream(ParquetApiService.MaxUploadBytes);
            var (overview, error) = await Api.OpenAsync(e.File.Name, stream);
            if (overview is null) { _error = error; return; }
            if (_baselineFile is { } old) await Api.CloseAsync(old.SessionId);
            _baselineFile = overview; _baseline = null;
        });
    }

    private async Task SelectEvidenceBaselineAsync()
    {
        _baselineMode = "evidence";
        if (_saved is null) await LoadEvidenceAsync();
    }

    /// <summary>Changes the comparison direction; every result of the old current file is cleared (they described the other file).</summary>
    private async Task SwapAsync()
    {
        if (_current is null || _baselineFile is null) return;
        var (newCurrent, newBaseline, association) = (_baselineFile, _current, _association);
        await Busy("Switching files…", async () =>
        {
            await StartAsync(newCurrent);
            _baselineFile = newBaseline;
            if (association is not null && _association is null && _current is { } cur)
            {
                // Keep the chosen contract for the other file when the server accepts it for that file too.
                var (resolved, _) = await Api.AssociateAsync(cur.SessionId, association with { ColumnMappings = [.. association.ColumnMappings.Where(m => m.Provenance == "Declared")] });
                if (resolved is not null) { _association = resolved with { ReviewStatus = "Proposed" }; await GenerateAndReviewAsync(); }
            }
            _area = "explore"; _exploreView = "compare";
        });
    }

    private async Task CompareAsync()
    {
        if (_current is null) return;
        await Busy("Comparing with the baseline…", async () =>
        {
            var (result, error) = await Api.BaselineAsync(new ParquetBaselineRequest
            {
                CurrentSessionId = _current.SessionId, BaselineSessionId = _baselineMode == "file" ? _baselineFile?.SessionId : null,
                BaselineEvidenceId = _baselineMode == "evidence" ? _baselineEvidenceId : null, EnvironmentId = _profileId, KeyColumns = [.. _keys],
                Quality = new ParquetQualityRequest { Association = _association, Rules = new ParquetRuleSet { Name = _ruleSetName, Rules = [.. _rules] } },
            });
            _baseline = result; _error = error; _recordFilter = "Changed";
        });
    }

    // ── Evidence ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task LoadEvidenceAsync()
    {
        if (_profileId is null) return;
        var (saved, error) = await Api.EvidenceAsync(_profileId);
        _saved = saved; if (error is not null) _error = error;
    }

    private async Task SearchRunsAsync()
    {
        if (_profileId is null) return;
        await Busy("Finding runs…", async () =>
        {
            var (runs, error) = await Runs.RunsAsync(new ExecutionRunFilter(Search: _runSearch, From: _runFrom, EnvironmentId: _profileId));
            _runs = runs; _error = error;
        });
    }

    private async Task SaveEvidenceAsync()
    {
        if (_current is null || _profileId is null) return;
        await Busy("Saving evidence (the server re-runs the review)…", async () =>
        {
            var request = new ParquetEvidenceRequest(_current.SessionId, _profileId, new ParquetRuleSet { Name = _ruleSetName, Rules = [.. _rules] },
                _baseline is not null && _baselineMode == "file" ? _baselineFile?.SessionId : null, null, _runId, _note)
            {
                Association = _association, KeyColumns = [.. _keys], BaselineEvidenceId = _baseline is not null && _baselineMode == "evidence" ? _baselineEvidenceId : null,
                ManualLink = _manual, ManualReason = _manualReason,
            };
            var (saved, error) = await Api.SaveEvidenceAsync(request);
            _error = error;
            if (saved is null) return;
            _openEvidence = saved;
            await LoadEvidenceAsync();
        });
    }

    private async Task OpenEvidenceAsync(Guid id)
    {
        if (_profileId is null) return;
        var (e, error) = await Api.EvidenceDetailAsync(_profileId, id);
        _openEvidence = e; _error = error;
    }

    private async Task ReportAsync(Guid id)
    {
        if (_profileId is null) return;
        var (bytes, error) = await Api.ReportAsync(_profileId, id);
        if (bytes is not null) await JS.InvokeVoidAsync("downloadBase64File", $"parquet-quality-report-{id:N}.md", Convert.ToBase64String(bytes), "text/markdown");
        _error = error;
    }

    private async Task ExportAsync(Guid id)
    {
        if (_profileId is null) return;
        var (bytes, error) = await Api.ExportEvidenceAsync(_profileId, id);
        if (bytes is not null) await JS.InvokeVoidAsync("downloadBase64File", $"parquet-inspection-{id:N}.json", Convert.ToBase64String(bytes), "application/json");
        _error = error;
    }

    private async Task ApproveBaselineAsync(Guid id)
    {
        if (_profileId is null) return;
        var (e, error) = await Api.ApproveBaselineAsync(_profileId, id);
        if (e is not null) _openEvidence = e;
        _error = error;
        await LoadEvidenceAsync();
    }

    private async Task DeleteAsync(Guid id)
    {
        if (_profileId is null) return;
        if (_deleteId != id) { _deleteId = id; return; }
        _deleteId = null;
        _error = await Api.DeleteEvidenceAsync(_profileId, id);
        if (_openEvidence?.Id == id) _openEvidence = null;
        await LoadEvidenceAsync();
    }

    // ── Overview helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private List<QualityDimension> Dimensions() => _result?.Dimensions is { Count: > 0 } d ? d
        : [new("File readable", _current is null ? QualityState.NotAssessed : QualityState.Pass, "The file opened."), new("Schema compatible", QualityState.NotAssessed, "Not assessed."),
           new("Data valid", QualityState.NotAssessed, "Not assessed."), new("Business rules verified", QualityState.NotAssessed, "Not assessed."), new("E2E evidence verified", QualityState.NotAssessed, "Not assessed.")];

    private int Cov(RequirementStatus s) => _result?.Coverage.Count(c => c.Status == s) ?? 0;

    private string NextAction() => _discovery is { Candidates.Count: > 0 } && _association is null ? "Select contract" : _result?.NextAction ?? "Run validation";

    private async Task NextActionAsync()
    {
        switch (NextAction())
        {
            case "Select contract": _area = "overview"; break;
            case "Confirm contract": await ConfirmAsync(); break;
            case "Generate rules": await RegenerateAsync(); break;
            case "Run validation": _area = "validation"; await RunAsync(); break;
            case "Review findings": _area = "validation"; _validationView = _rules.Any(r => r.Review == RuleReviewState.NeedsReview) ? "rules" : "requirements"; break;
            default: await GoAsync("evidence"); break;
        }
    }

    private IEnumerable<string> Prerequisites()
    {
        if (_association is null) yield return _discovery is { Candidates.Count: > 0 } ? "Choose the dataset contract this file must satisfy." : "No dataset contract is available: requirement coverage cannot be assessed.";
        else if (_association.ReviewStatus != "Confirmed") yield return "Confirm the contract association.";
        var waiting = _rules.Count(r => r.Review == RuleReviewState.NeedsReview);
        if (waiting > 0) yield return $"{waiting} generated business rule(s) await a test lead's approval.";
        if (_association is not null && _manifest is null) yield return "No delivery manifest attached: manifest checks are not assessed.";
        if (_profileId is null) yield return "No Target Environment selected: evidence cannot be saved.";
        yield return "Link an execution run when saving evidence to assess end-to-end evidence.";
    }

    private ParquetRuleResult? Result(string ruleId) => _result?.Validation?.Results.FirstOrDefault(r => r.RuleId == ruleId);

    private static string RuleName(ParquetRule r) => r.Name ?? r.Kind switch
    {
        ParquetRuleKind.ColumnExists => $"{r.Column} is present", ParquetRuleKind.TypeIs => $"{r.Column} has type {r.Expected}", ParquetRuleKind.NotNull => $"{r.Column} is never empty",
        ParquetRuleKind.Unique => $"{string.Join(" + ", r.Columns.Count > 0 ? r.Columns : [r.Column ?? ""])} is unique", ParquetRuleKind.AllowedValues => $"{r.Column} uses only allowed values",
        ParquetRuleKind.Range => $"{r.Column} within {r.Min ?? "−∞"} … {r.Max ?? "+∞"}", ParquetRuleKind.RecordCount => $"{r.Expected} rows", ParquetRuleKind.Pattern => $"{r.Column} has format {r.Expected}",
        ParquetRuleKind.NullUnless => $"{r.Column} empty unless {r.WhenColumn} = {string.Join("/", r.WhenValues)}", ParquetRuleKind.RequiredWhen => $"{r.Column} set when {r.WhenColumn} = {string.Join("/", r.WhenValues)}",
        ParquetRuleKind.OnlyWith => $"{string.Join(", ", r.Values)} only with {r.WhenColumn} = {string.Join("/", r.WhenValues)}",
        ParquetRuleKind.SumIdentity => $"{string.Join(" + ", r.Values)} = {r.Expected} per {string.Join(", ", r.GroupColumns)}", ParquetRuleKind.RollUp => $"{r.Column} values add up to {r.Expected}",
        ParquetRuleKind.SortedBy => $"Sorted by {string.Join(", ", r.Columns)}", ParquetRuleKind.RowGroupCount => $"{r.Expected} row group(s)", ParquetRuleKind.Compression => $"{r.Expected} compression",
        ParquetRuleKind.Manifest => "Matches the delivery manifest", ParquetRuleKind.NoIdentifierValues => "No personal identification numbers", ParquetRuleKind.NoProtectedCanary => "No protected-identity canary",
        _ => r.Kind.ToString(),
    };

    private static bool FullScan(ParquetRule r) => r.Kind is not (ParquetRuleKind.ColumnExists or ParquetRuleKind.TypeIs or ParquetRuleKind.RecordCount or ParquetRuleKind.RowGroupCount or ParquetRuleKind.Compression);
    private static string Scope(ParquetRule r) => r.Kind switch { ParquetRuleKind.ColumnExists or ParquetRuleKind.TypeIs => "Schema", ParquetRuleKind.RecordCount or ParquetRuleKind.RowGroupCount or ParquetRuleKind.Compression => "Metadata", ParquetRuleKind.Manifest => "Manifest + full scan", _ => "Full scan" };
    private static string Origin(RuleOrigin o) => o switch { RuleOrigin.ContractDerived => "Contract", RuleOrigin.SchemaDerived => "Schema", RuleOrigin.ApprovedBusinessRule => "Approved business rule", _ => "Your rule" };
    private static string ReviewLabel(RuleReviewState s) => s switch { RuleReviewState.Active => "Active", RuleReviewState.NeedsReview => "Needs review", RuleReviewState.Rejected => "Rejected", _ => "Not supported" };
    private static string PillClass(QualityState s) => s switch { QualityState.Pass => "pq-pill-pass", QualityState.Fail => "pq-pill-fail", QualityState.Partial => "pq-pill-warn", _ => "pq-pill-neutral" };
    private static string StateLabel(QualityState s) => s switch { QualityState.Pass => "Pass", QualityState.Fail => "Fail", QualityState.Partial => "Partial", QualityState.Blocked => "Blocked", _ => "Not assessed" };
    private static string CoverageClass(RequirementStatus s) => s switch { RequirementStatus.CoveredPass => "pq-pill-pass", RequirementStatus.CoveredFail => "pq-pill-fail", RequirementStatus.Blocked => "pq-pill-warn", _ => "pq-pill-neutral" };
    private static string BaselineClassCss(BaselineClass c) => c switch { BaselineClass.Regression or BaselineClass.Incompatible => "pq-pill-fail", BaselineClass.Improvement or BaselineClass.Unchanged => "pq-pill-pass", BaselineClass.NotComparable => "pq-pill-neutral", _ => "pq-pill-warn" };
    private static string Words(string pascal) => string.Concat(pascal.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : ch.ToString()));
    private static string Friendly(ParquetColumnSummary c) => c.LogicalType switch { "STRING" => "text", "DATE" => "date", null => c.PhysicalType switch { "INT32" or "INT64" => "whole number", "DOUBLE" or "FLOAT" => "number", "BOOLEAN" => "yes/no", var p => p }, var l => l.ToLowerInvariant() } + $" ({c.PhysicalType})";
    private static string Pct(long part, long whole) => whole == 0 ? "0%" : (100.0 * part / whole).ToString("0.#", Inv) + "%";
    private static string Bytes(long n) => n < 1024 ? $"{n} B" : n < 1024 * 1024 ? $"{(n / 1024.0).ToString("0.#", Inv)} KB" : $"{(n / 1048576.0).ToString("0.#", Inv)} MB";
    private static string Utc(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", Inv) + " UTC";

    private async Task Busy(string text, Func<Task> work)
    {
        _busy = true; _busyText = text; _error = null;
        try { await work(); }
        finally { _busy = false; _busyText = null; }
    }

    /// <summary>Sessions stay open until they expire or the tester closes them (recent files); the page does not close them on navigation.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
