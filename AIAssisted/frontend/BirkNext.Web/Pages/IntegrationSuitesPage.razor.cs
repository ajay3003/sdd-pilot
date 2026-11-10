using BirkNext.ContractTesting;
using BirkNext.Integrations;
using BirkNext.IntegrationSuites;
using BirkNext.Web.Models;
using Microsoft.AspNetCore.Components;

namespace BirkNext.Web.Pages;

public partial class IntegrationSuitesPage
{
    private static readonly (string Key, string Label)[] Areas =
        [("sources", "1 · Integration & Sources"), ("generate", "2 · Generate & Review"), ("readiness", "3 · Resolve Readiness"), ("execute", "4 · Execute & Evidence")];
    private static readonly string[] Tabs = ["Readiness", "Flow", "Configuration", "Test data", "Tests", "XML", "Kode 6/7", "Review", "Execution"];
    private const int MaxListed = 8;

    private List<IqrSourceSnapshotSummary>? _snapshots;
    private List<IntegrationSuiteScope>? _scopes;
    private List<IntegrationSuiteSummary>? _suites;
    private List<CompositeContextSummary>? _contexts;
    private IntegrationSuite? _suite;
    private CompositeDiscovery? _discovery;
    private CompositeSourceContext? _context;
    private CompositePreflight? _preflight;
    private CompositeRunView? _run;
    private string _area = "sources";
    private string _snapshotId = "", _snapshotFilter = "";
    private string _scopeId = "skolenaervaer", _compositeScope = "skolenaervaer";
    private string _modules = "";
    private bool _negatives = true, _security = true, _testData = true, _allowPartial, _showOperations;
    private bool _busy, _authorized, _confirmMutating;
    private string? _error, _message;
    private string _tab = Tabs[0];
    private string _ownerFilter = "All";
    private int _runUntil = -1;
    private readonly HashSet<Guid> _selected = [];
    private ContractTestEnvironment? _environment;
    private string? _environmentName;

    // Composite selection being edited: stage → chosen snapshot, snapshot → approval, manual associations (with reasons), accepted transitions.
    private readonly Dictionary<string, Guid> _chosen = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, CompositeApproval> _approval = [];
    private readonly Dictionary<Guid, string> _manualReasons = [];
    private readonly Dictionary<string, string> _acceptReason = new(StringComparer.Ordinal);
    private string _manualFilter = "", _manualStage = "altinn", _manualReason = "";

    [SupplyParameterFromQuery(Name = "area")] public string? AreaQuery { get; set; }
    [SupplyParameterFromQuery(Name = "snapshot")] public string? SnapshotQuery { get; set; }
    [SupplyParameterFromQuery(Name = "scope")] public string? ScopeQuery { get; set; }
    [SupplyParameterFromQuery(Name = "modules")] public string? ModulesQuery { get; set; }
    [SupplyParameterFromQuery(Name = "context")] public string? ContextQuery { get; set; }
    [SupplyParameterFromQuery(Name = "suite")] public string? SuiteQuery { get; set; }

    private IqrSourceSnapshotSummary? Picked => Guid.TryParse(_snapshotId, out var id) ? _snapshots?.FirstOrDefault(s => s.Id == id) : null;
    private IntegrationSuiteScope? ScopeDef => _scopes?.FirstOrDefault(s => s.ScopeId == _scopeId);
    private IEnumerable<IqrSourceSnapshotSummary> Matching => (_snapshots ?? []).Where(s => _snapshotFilter.Length == 0 || s.Archive.FileName.Contains(_snapshotFilter.Trim(), StringComparison.OrdinalIgnoreCase));
    private int FilteredCount => Matching.Count();
    private List<IqrSourceSnapshotSummary> FilteredSnapshots
    {
        get
        {
            var list = Matching.Take(MaxListed).ToList();
            if (Picked is { } p && list.All(s => s.Id != p.Id)) list.Insert(0, p);
            return list;
        }
    }
    private IEnumerable<IqrSourceSnapshotSummary> ManualMatches => _manualFilter.Trim().Length < 2 ? [] :
        (_snapshots ?? []).Where(s => s.Archive.FileName.Contains(_manualFilter.Trim(), StringComparison.OrdinalIgnoreCase)).Take(MaxListed);

    private string AreaHref(string key) => $"integration-suites?area={key}{(_context is { } c ? $"&context={c.Id:D}" : "")}{(_suite is { } s ? $"&suite={s.Id:D}" : "")}";

    protected override async Task OnInitializedAsync()
    {
        var (snapshots, e1) = await Api.SnapshotsAsync();
        var (scopes, e2) = await Api.ScopesAsync();
        var (suites, e3) = await Api.ListAsync();
        var (contexts, _) = await Api.ContextsAsync();
        _snapshots = snapshots ?? []; _scopes = scopes ?? []; _suites = suites ?? []; _contexts = contexts ?? [];
        _error = e1 ?? e2 ?? e3;
        if (ScopeQuery is { Length: > 0 } scope && _scopes.Any(s => s.ScopeId == scope)) _scopeId = scope;
        if (_scopes.Count > 0 && _scopes.All(s => s.ScopeId != _scopeId)) _scopeId = _scopes[0].ScopeId;
        if (Guid.TryParse(SnapshotQuery, out var handedOff) && _snapshots.Any(s => s.Id == handedOff)) { _snapshotId = handedOff.ToString("D"); _area = "generate"; }
        if (!string.IsNullOrWhiteSpace(ModulesQuery)) _modules = ModulesQuery.Trim();
        if (Areas.Any(a => a.Key == AreaQuery)) _area = AreaQuery!;
        if (Guid.TryParse(ContextQuery, out var contextId)) await OpenContextAsync(contextId, keepArea: true);
        if (Guid.TryParse(SuiteQuery, out var suiteId)) { (_suite, _) = await Api.GetAsync(suiteId); }
        try
        {
            var context = await ContextFactory.GetActiveContextAsync();
            if (context.ActiveProfileOrNull is { } profile && !string.IsNullOrWhiteSpace(context.TargetUrl))
            {
                var identity = ReviewAuthenticationIdentity.ForContext(context);
                _environmentName = profile.Name;
                _environment = new()
                {
                    EnvironmentId = profile.Id, TargetUrl = context.TargetUrl, EnvironmentType = profile.EnvironmentType.ToString(), IsProduction = profile.EnvironmentType == FrontendEnvironmentType.Production,
                    ProfileId = identity.ProfileId, ContextFingerprint = identity.ContextFingerprint,
                };
            }
        }
        catch (Exception) { _environment = null; }
    }

    private void SetArea(string key)
    {
        _area = key;
        try { Navigation.NavigateTo(AreaHref(key), new NavigationOptions { ReplaceHistoryEntry = true }); } catch (NavigationException) { }
    }

    // ── Stage strip ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record StripStage(string Id, string Label, string State, string StateLabel, string Detail);

    private List<StripStage> StripStages
    {
        get
        {
            if (_area is "readiness" or "execute" or "generate" && _suite?.Composite is { } composite)
                return [.. composite.Stages.Select(s => new StripStage(s.StageId, s.Label, s.Coverage == CompositeCoverageLevel.Verified ? "Ready" : s.Readiness.ToString(),
                    s.Coverage == CompositeCoverageLevel.Verified ? "Verified" : Readiness(s.Readiness), $"{string.Join("; ", s.Sources)} · executor: {s.Executor} · observer: {s.Observer}"))];
            if (_discovery is { } d)
                return [.. d.Stages.Select(s =>
                {
                    var chosen = Chosen(s.StageId);
                    var cls = chosen is { } id ? _approval.GetValueOrDefault(id) == CompositeApproval.Approved ? CompositeMatchClass.Confirmed : CompositeMatchClass.Candidate
                        : s.Candidates.Count == 0 && s.ReferenceSource is not null ? CompositeMatchClass.Confirmed : s.Candidates.Count == 0 ? CompositeMatchClass.Missing : s.Class == CompositeMatchClass.Ambiguous ? CompositeMatchClass.Ambiguous : CompositeMatchClass.Candidate;
                    return new StripStage(s.StageId, s.Label, ClassState(cls), Class(cls), chosen is { } c2 ? $"{s.Candidates.FirstOrDefault(x => x.SnapshotId == c2)?.ProjectKey}" : s.Explanation);
                })];
            if (_context is { } ctx)
                return [.. ctx.StageMappings.Select(m => new StripStage(m.StageId, m.Label, ClassState(m.Class), Class(m.Class), string.Join("; ", m.Evidence.Take(2))))];
            return [];
        }
    }

    // ── Integration & Sources ───────────────────────────────────────────────────────────────────────────────────────────────

    private Guid? Chosen(string stageId) => _chosen.TryGetValue(stageId, out var id) ? id : null;

    private void Choose(string stageId, Guid? snapshotId)
    {
        if (snapshotId is { } id) { _chosen[stageId] = id; _approval.TryAdd(id, CompositeApproval.Proposed); }
        else _chosen.Remove(stageId);
    }

    private CompositeApproval Approval(Guid snapshotId) => _approval.GetValueOrDefault(snapshotId, CompositeApproval.Proposed);
    private void SetApproval(Guid snapshotId, CompositeApproval approval) => _approval[snapshotId] = approval;

    private sealed record SelectedSource(Guid SnapshotId, string ProjectKey, string ArchiveFileName, List<string> Stages);

    private List<SelectedSource> SelectedSources => [.. _chosen.GroupBy(kv => kv.Value).Select(g =>
    {
        var candidate = _discovery?.Stages.SelectMany(s => s.Candidates).FirstOrDefault(c => c.SnapshotId == g.Key);
        var summary = _snapshots?.FirstOrDefault(s => s.Id == g.Key);
        var association = _context?.Snapshots.FirstOrDefault(a => a.SnapshotId == g.Key);
        return new SelectedSource(g.Key, candidate?.ProjectKey ?? association?.ProjectKey ?? summary?.Archive.FileName ?? g.Key.ToString("D")[..8],
            candidate?.ArchiveFileName ?? association?.ArchiveFileName ?? summary?.Archive.FileName ?? "", [.. g.Select(kv => kv.Key)]);
    })];

    private async Task DiscoverAsync()
    {
        _busy = true; _error = null; _message = null;
        var (discovery, error) = await Api.DiscoverAsync(_compositeScope, _context?.Id);
        _error = error;
        if (discovery is not null)
        {
            _discovery = discovery;
            // Keep the context's choices; otherwise propose the single candidate of a stage (never an ambiguous one).
            foreach (var st in discovery.Stages)
                if (!_chosen.ContainsKey(st.StageId) && st.Class is CompositeMatchClass.Candidate or CompositeMatchClass.Confirmed && st.Candidates.Count == 1)
                    Choose(st.StageId, st.Candidates[0].SnapshotId);
            _message = $"{discovery.Stages.Count(s => s.Candidates.Count > 0)} of {discovery.Stages.Count} stage(s) have candidate sources ({discovery.SnapshotsConsidered} snapshot(s) considered). Review and approve them.";
        }
        _busy = false;
    }

    private async Task OpenContextAsync(Guid id, bool keepArea = false)
    {
        _busy = true; _error = null;
        var (context, error) = await Api.ContextAsync(id);
        _error = error;
        if (context is not null) LoadContext(context);
        if (!keepArea) _area = "sources";
        if (context is not null && !keepArea) SetArea(_area); // the link already names it on first load
        _busy = false;
    }

    /// <summary>The current revision of the open context's key when the open one is older (a bookmarked or shared link) — saving from an old revision conflicts.</summary>
    private CompositeContextSummary? NewerRevision => _context is { } c
        ? _contexts?.Where(x => x.ContextKey == c.ContextKey && x.Revision > c.Revision).OrderByDescending(x => x.Revision).FirstOrDefault()
        : null;

    private void LoadContext(CompositeSourceContext context)
    {
        _context = context;
        _compositeScope = context.ScopeId;
        _chosen.Clear(); _approval.Clear(); _manualReasons.Clear();
        foreach (var a in context.Snapshots.Where(a => a.Approval != CompositeApproval.Rejected))
        {
            foreach (var s in a.Stages) _chosen[s] = a.SnapshotId;
            _approval[a.SnapshotId] = a.Approval;
            if (a.Reason is { Length: > 0 } r) _manualReasons[a.SnapshotId] = r;
        }
    }

    private void NewContext()
    {
        _context = null; _discovery = null; _chosen.Clear(); _approval.Clear(); _manualReasons.Clear(); _acceptReason.Clear();
        _message = "New context: discover the sources and choose one per stage.";
    }

    private void UseNewer(CompositeSourceChange change)
    {
        foreach (var stage in change.AffectedStages) _chosen[stage] = change.NewerSnapshotId;
        _approval[change.NewerSnapshotId] = CompositeApproval.Proposed;
        _message = $"{change.ProjectKey}: the newer snapshot is selected for {string.Join(", ", change.AffectedStages)} and needs approval again. Save to create the next revision.";
    }

    private void AddManual(Guid snapshotId)
    {
        if (string.IsNullOrWhiteSpace(_manualReason)) return;
        _chosen[_manualStage] = snapshotId;
        _approval[snapshotId] = CompositeApproval.Proposed;
        _manualReasons[snapshotId] = _manualReason.Trim();
    }

    private async Task SaveContextAsync(IReadOnlyList<CompositeTransitionAcceptance>? acceptances = null)
    {
        _busy = true; _error = null; _message = null;
        var request = new CompositeContextSaveRequest
        {
            ScopeId = _compositeScope, OwningProject = _context?.OwningProject, ExpectedRevision = _context?.Revision,
            Associations = [.. SelectedSources.Select(s => new CompositeAssociationInput(s.SnapshotId, s.Stages, Approval(s.SnapshotId), _manualReasons.GetValueOrDefault(s.SnapshotId)))],
            Acceptances = [.. (acceptances ?? []).Concat(_context?.Compatibility.Where(t => t.Accepted && t.AcceptedReason is not null).Select(t => new CompositeTransitionAcceptance(t.Key, t.AcceptedReason!)) ?? [])
                .DistinctBy(a => a.TransitionKey)],
        };
        var (result, error) = await Api.SaveContextAsync(request);
        if (error is not null) _error = error;
        else if (result?.Context is { } context)
        {
            LoadContext(context);
            SetArea(_area);
            _message = result.Unchanged ? $"Nothing changed: revision {context.Revision} is current." : $"Revision {context.Revision} saved. {string.Join(" ", context.Changes.Take(3))}";
            _contexts = (await Api.ContextsAsync()).Contexts ?? _contexts;
        }
        else if (result is not null) _error = string.Join(" ", result.Problems);
        _busy = false;
    }

    private Task SaveContextAsync() => SaveContextAsync(null);

    private async Task AcceptAsync(string key)
    {
        if (_acceptReason.GetValueOrDefault(key) is not { Length: > 0 } reason) return;
        await SaveContextAsync([new CompositeTransitionAcceptance(key, reason.Trim())]);
    }

    // ── Generate & Review ───────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task GenerateCompositeAsync()
    {
        if (_context is not { } context) return;
        _busy = true; _error = null; _message = null;
        var (result, error) = await Api.GenerateAsync(new IntegrationSuiteGenerateRequest
        {
            ScopeId = context.ScopeId, CompositeContextId = context.Id, AllowPartial = _allowPartial, IncludeNegatives = _negatives, IncludeSecurity = _security, GenerateTestData = _testData,
        });
        await GeneratedAsync(result, error);
        _busy = false;
    }

    private async Task GenerateAsync()
    {
        if (Picked is not { } snapshot) return;
        _busy = true; _error = null; _message = null;
        var (result, error) = await Api.GenerateAsync(new IntegrationSuiteGenerateRequest
        {
            SnapshotId = snapshot.Id, ScopeId = _scopeId, IncludeNegatives = _negatives, IncludeSecurity = _security, GenerateTestData = _testData,
            Modules = [.. _modules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        });
        await GeneratedAsync(result, error);
        _busy = false;
    }

    private async Task GeneratedAsync(IntegrationSuiteGenerateResult? result, string? error)
    {
        if (error is not null || result?.Error is not null) { _error = error ?? result!.Error; return; }
        if (result?.Suite is not { } suite) return;
        _suite = suite; _tab = Tabs[0]; _selected.Clear(); _run = null; _preflight = null; _runUntil = -1;
        _message = result.Reused ? $"Nothing changed since revision {suite.Revision}; the existing suite is shown (no duplicates were created)." : $"Revision {suite.Revision} generated as a draft. Nothing was sent.";
        _suites = (await Api.ListAsync()).Suites ?? _suites;
    }

    private async Task OpenAsync(Guid id)
    {
        _busy = true; _error = null; _message = null;
        (_suite, _error) = await Api.GetAsync(id);
        _selected.Clear(); _tab = Tabs[0]; _run = null; _preflight = null; _runUntil = -1;
        if (_suite?.Composite is { } c && (_context is null || _context.Id != c.ContextId) && (await Api.ContextAsync(c.ContextId)).Context is { } ctx) LoadContext(ctx);
        _busy = false;
    }

    private async Task ReviewAsync((string Key, string Decision) review)
    {
        if (_suite is not { } suite) return;
        _busy = true; _error = await Api.ReviewAsync(suite.Id, new IntegrationSuiteReviewRequest(review.Key, review.Decision, null));
        if (_error is null) (_suite, _error) = await Api.GetAsync(suite.Id);
        _busy = false;
    }

    private async Task ExecuteAsync(bool confirmMutating)
    {
        if (_suite is not { } suite || _environment is not { } env) return;
        _busy = true; _error = null; _message = null;
        var (result, error) = await Api.ExecuteAsync(suite.Id, new IntegrationSuiteExecuteRequest
        {
            TestIds = [.. _selected], EnvironmentId = env.EnvironmentId, TargetUrl = env.TargetUrl, EnvironmentType = env.EnvironmentType, IsProduction = env.IsProduction,
            ProfileId = env.ProfileId, ContextFingerprint = env.ContextFingerprint, ConfirmMutatingCalls = confirmMutating, Authorized = true,
        });
        if (error is not null) _error = error;
        else if (result is not null)
        {
            _message = result.NotRun.Count > 0 ? $"{result.Message} Not run: {result.NotRun.Count}." : result.Message;
            if (result.RunId is not null) (_suite, _) = await Api.GetAsync(suite.Id);
        }
        _busy = false;
    }

    private void Toggle((Guid Id, bool On) t) { if (t.On) _selected.Add(t.Id); else _selected.Remove(t.Id); }

    // ── Execute & Evidence ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> Owners(SuiteComposite c) => [.. new[] { "All" }.Concat(c.Blockers.Select(b => b.Owner).Distinct(StringComparer.Ordinal))];

    private static string StopLabel(SuiteComposite c, int i) => i == PlanStages(c).Count - 1 ? "Whole plan" : $"Up to {PlanStages(c)[i]}";
    private static string SuiteTitle(IntegrationSuite s) => $"{s.Scope.DisplayName} · revision {s.Revision}{(s.Composite is { } c ? $" · {c.Sources.Count} sources" : "")}";

    private static List<string> PlanStages(SuiteComposite c) => [.. c.Plan.Steps.Select(s => s.StageId).Distinct(StringComparer.Ordinal)];

    private CompositeRunRequest RunRequest(bool authorized)
    {
        var stages = _suite?.Composite is { } c ? PlanStages(c) : [];
        var until = _runUntil < 0 || _runUntil >= stages.Count ? stages.Count - 1 : _runUntil;
        return new CompositeRunRequest
        {
            Stages = until == stages.Count - 1 ? [] : [.. stages.Take(until + 1)], EnvironmentId = _environment?.EnvironmentId ?? "", TargetUrl = _environment?.TargetUrl ?? "",
            EnvironmentType = _environment?.EnvironmentType ?? "", IsProduction = _environment?.IsProduction ?? false, ProfileId = _environment?.ProfileId,
            ContextFingerprint = _environment?.ContextFingerprint, ConfirmMutatingCalls = _confirmMutating, Authorized = authorized,
        };
    }

    private async Task ApprovePlanAsync()
    {
        if (_suite is not { } suite) return;
        _busy = true; _error = null;
        var (plan, error) = await Api.ApprovePlanAsync(suite.Id);
        _error = error;
        if (plan is not null) { (_suite, _) = await Api.GetAsync(suite.Id); _message = "The transaction plan is approved. Runs still need explicit authorisation."; }
        _busy = false;
    }

    private async Task PreflightAsync()
    {
        if (_suite is not { } suite) return;
        _busy = true; _error = null;
        (_preflight, _error) = await Api.PreflightAsync(suite.Id, RunRequest(authorized: true));
        _busy = false;
    }

    private async Task RunCompositeAsync()
    {
        if (_suite is not { } suite || !_authorized) return;
        _busy = true; _error = null; _message = null;
        var (result, error) = await Api.RunCompositeAsync(suite.Id, RunRequest(authorized: true));
        _authorized = false; // authorisation is per run
        if (error is not null) _error = error;
        else if (result is not null)
        {
            _preflight = result.Preflight;
            if (result.RunId is { } runId)
            {
                _message = $"Run started ({result.Preflight.Kind}). Stages are observed with bounded waiting; refresh the run to follow it.";
                (_suite, _) = await Api.GetAsync(suite.Id);
                await OpenRunAsync(runId);
            }
            else _error = result.Message;
        }
        _busy = false;
    }

    private async Task OpenRunAsync(Guid runId)
    {
        if (_suite is not { } suite) return;
        (_run, var error) = await Api.CompositeRunAsync(suite.Id, runId);
        _error = error;
    }

    // ── Labels ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static int CountTriage(IntegrationSuite s, SuiteTriage t) => s.Tests.Count(x => x.Triage == t) + s.Security.Count(x => x.Triage == t) + s.Stages.Count(x => x.Triage == t);
    private static string Label(SuiteStatus s) => IntegrationSuiteDetail.Label(s);
    private static string Label(SuiteItemReadiness r) => IntegrationSuiteDetail.Label(r);
    private static string Outcome(SuiteExecutionOutcome o) => o switch
    {
        SuiteExecutionOutcome.NotExecuted => "Not executed", SuiteExecutionOutcome.PartiallyVerified => "Partially verified", _ => o.ToString(),
    };
    private static string Class(CompositeMatchClass c) => c switch { CompositeMatchClass.Confirmed => "Confirmed", CompositeMatchClass.Candidate => "Candidate", CompositeMatchClass.Ambiguous => "Ambiguous", _ => "Missing" };
    private static string ClassState(CompositeMatchClass c) => c switch { CompositeMatchClass.Confirmed => "Ready", CompositeMatchClass.Missing => "Blocked", _ => "NeedsReview" };
    private static string Readiness(CompositeReadiness r) => r switch
    {
        CompositeReadiness.NeedsReview => "Needs review", CompositeReadiness.NotAssessed => "Not assessed", CompositeReadiness.NotSupported => "Not supported", _ => r.ToString(),
    };
    private static string Compat(TransitionCompatibility c) => c switch
    {
        TransitionCompatibility.Compatible => "Compatible", TransitionCompatibility.PotentiallyCompatible => "Potentially compatible", TransitionCompatibility.Incompatible => "Incompatible",
        TransitionCompatibility.MissingEvidence => "Missing evidence", _ => "Not assessed",
    };
    private static string CompatState(TransitionCompatibility c) => c switch
    {
        TransitionCompatibility.Compatible => "Ready", TransitionCompatibility.Incompatible => "Blocked", _ => "NeedsReview",
    };
    private static string Verdict(CompositeRunVerdict v) => v switch
    {
        CompositeRunVerdict.FullE2EVerified => "FULL_E2E_VERIFIED", CompositeRunVerdict.SubflowVerified => "SUBFLOW_VERIFIED", CompositeRunVerdict.WrongCorrelation => "WRONG_CORRELATION",
        CompositeRunVerdict.UnknownOutcome => "UNKNOWN_OUTCOME", _ => v.ToString().ToUpperInvariant(),
    };
    private static string VerdictState(CompositeRunVerdict v) => v switch
    {
        CompositeRunVerdict.FullE2EVerified or CompositeRunVerdict.SubflowVerified => "Ready", CompositeRunVerdict.Running or CompositeRunVerdict.Incomplete => "NeedsReview", _ => "Blocked",
    };
    private static string StageState(CompositeStageState s) => s switch
    {
        CompositeStageState.Succeeded => "Ready", CompositeStageState.Failed or CompositeStageState.UnknownOutcome => "Blocked", _ => "NeedsReview",
    };
    private static string Short(string sha) => IntegrationSuiteDetail.Short(sha);
    private static string Utc(DateTimeOffset t) => IntegrationSuiteDetail.Utc(t);
    private static string Slug(string t) => new([.. t.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]);
}
