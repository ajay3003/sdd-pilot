using System.Text.Json;
using Path = System.IO.Path;   // HotChocolate contributes a Path type through the global usings
using System.Text.Json.Serialization;
using BirkNext.CriticalE2E;

namespace BirkNext.Api.Services.CriticalE2E;

public interface ICriticalE2EStore
{
    IReadOnlyList<CriticalE2EFlowDefinition> Flows(string environmentId);
    CriticalE2EFlowDefinition? Flow(string flowId);
    CriticalE2EFlowDefinition Save(CriticalE2EFlowDefinition flow);
    bool Delete(string flowId);
    IReadOnlyList<CriticalE2ERunResult> History(string environmentId, int limit = 100);
    void Record(CriticalE2ERunResult result);
    /// <summary>
    /// Registered delivery modules of an environment: the only modules release coverage counts. Registration is explicit
    /// (module administration); a label typed on a flow never registers one.
    /// </summary>
    IReadOnlyList<string> Modules(string environmentId) => [.. RegisteredModules(environmentId).Select(m => m.Name)];
    IReadOnlyList<CriticalE2ERegisteredModule> RegisteredModules(string environmentId) => [];
    /// <summary>Registers a module (idempotent by name). Returns false when it already was.</summary>
    bool RegisterModule(string environmentId, CriticalE2ERegisteredModule module) => false;
    bool UnregisterModule(string environmentId, string name) => false;
    /// <summary>Every recorded run of one flow, newest first, archived included.</summary>
    IReadOnlyList<CriticalE2ERunResult> FlowHistory(string flowId);
    /// <summary>Sets (or, with null, removes) the archive marker on the named runs of one flow and nothing else. Returns how many changed.</summary>
    int SetArchived(string flowId, IReadOnlyCollection<string> runIds, DateTimeOffset? archivedAt, string? reason);
    /// <summary>Permanently removes the named runs of one flow. Returns how many were removed.</summary>
    int RemoveRuns(string flowId, IReadOnlyCollection<string> runIds);
    /// <summary>Local data reset: removes every flow, run and known module (memory and the App_Data files). Returns the number of flows and runs removed.</summary>
    int ClearAll() => 0;
}

/// <summary>
/// Flow definitions and run history on disk, as two small JSON files per installation.
///
/// Deliberately not a database table: this is workstation-local QA configuration and evidence, it has no relational
/// shape, and a migration is a poor trade for it. What matters is that a result survives a restart, because a release
/// gate whose evidence evaporates when the backend recycles is not a gate.
///
/// Nothing sensitive is written. Results carry sanitized summaries, observed routes and short observed values; tokens,
/// cookies, headers and payloads never reach this type, because they never leave the boundaries that produced them.
/// </summary>
public sealed class CriticalE2EStore : ICriticalE2EStore
{
    /// <summary>Run history kept per environment. Old evidence stops being evidence and starts being clutter.</summary>
    public const int MaxHistoryPerEnvironment = 200;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _flowsPath;
    private readonly string _historyPath;
    private readonly string _modulesPath;
    private readonly ILogger<CriticalE2EStore> _logger;
    private readonly Dictionary<string, CriticalE2EFlowDefinition> _flows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CriticalE2ERunResult> _history = [];
    private readonly Dictionary<string, List<CriticalE2ERegisteredModule>> _modules = new(StringComparer.OrdinalIgnoreCase);

    public CriticalE2EStore(IHostEnvironment environment, ILogger<CriticalE2EStore> logger)
        : this(Path.Combine(environment.ContentRootPath, "App_Data", "critical-e2e"), logger) { }

    public CriticalE2EStore(string directory, ILogger<CriticalE2EStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(directory);
        _flowsPath = Path.Combine(directory, "flows.json");
        _historyPath = Path.Combine(directory, "history.json");
        _modulesPath = Path.Combine(directory, "modules.json");
        foreach (var flow in Read<List<CriticalE2EFlowDefinition>>(_flowsPath) ?? []) _flows[flow.Id] = flow;
        _history.AddRange(Read<List<CriticalE2ERunResult>>(_historyPath) ?? []);
        foreach (var (environmentId, modules) in Read<Dictionary<string, List<CriticalE2ERegisteredModule>>>(_modulesPath) ?? []) _modules[environmentId] = modules;
    }

    public IReadOnlyList<CriticalE2EFlowDefinition> Flows(string environmentId)
    {
        lock (_gate)
            return _flows.Values
                .Where(f => string.IsNullOrWhiteSpace(environmentId) || string.Equals(f.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.Module, StringComparer.CurrentCultureIgnoreCase).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
    }

    public CriticalE2EFlowDefinition? Flow(string flowId)
    {
        lock (_gate) return _flows.GetValueOrDefault(flowId ?? "");
    }

    public CriticalE2EFlowDefinition Save(CriticalE2EFlowDefinition flow)
    {
        var stored = string.IsNullOrWhiteSpace(flow.Id)
            ? flow with { Id = $"flow-{Guid.NewGuid().ToString("N")[..10]}" }
            : flow;
        // Step ids are what a command id is built from, so a flow with duplicate or missing ones would make two steps
        // indistinguishable in the transport's idempotency check.
        stored = stored with { Steps = Number(stored.Steps) };
        lock (_gate)
        {
            // Any change to the definition (steps, routes, selectors, outcome, Test Data, boundary, module, release state …) is
            // a new version, so earlier runs stay tied to the definition they actually executed.
            if (_flows.TryGetValue(stored.Id, out var existing))
                stored = stored with { Version = Material(existing) == Material(stored) ? existing.Version : Math.Max(existing.Version, stored.Version) + 1 };
            _flows[stored.Id] = stored;
            Write(_flowsPath, _flows.Values.ToList());
        }
        return stored;
    }

    /// <summary>The definition without its bookkeeping (version, timestamp): what makes two saves the same definition.</summary>
    private static string Material(CriticalE2EFlowDefinition flow) => JsonSerializer.Serialize(flow with { Version = 0, UpdatedAt = null }, Json);

    private static List<CriticalE2EStepDefinition> Number(List<CriticalE2EStepDefinition> steps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return steps.Select((step, index) =>
        {
            var id = string.IsNullOrWhiteSpace(step.StepId) ? $"step-{index + 1}" : step.StepId.Trim();
            while (!seen.Add(id)) id = $"{id}-{index + 1}";
            return step with { StepId = id };
        }).ToList();
    }

    public bool Delete(string flowId)
    {
        lock (_gate)
        {
            if (!_flows.Remove(flowId ?? "")) return false;
            Write(_flowsPath, _flows.Values.ToList());
            return true;
        }
    }

    public IReadOnlyList<CriticalE2ERunResult> History(string environmentId, int limit = 100)
    {
        lock (_gate)
            return _history
                .Where(r => string.IsNullOrWhiteSpace(environmentId) || string.Equals(r.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.StartedAt).Take(Math.Clamp(limit, 1, MaxHistoryPerEnvironment)).ToList();
    }

    public void Record(CriticalE2ERunResult result)
    {
        lock (_gate)
        {
            _history.Add(result);
            foreach (var group in _history.GroupBy(r => r.EnvironmentId, StringComparer.OrdinalIgnoreCase).ToList())
                foreach (var stale in group.OrderByDescending(r => r.StartedAt).Skip(MaxHistoryPerEnvironment).ToList())
                    _history.Remove(stale);
            Write(_historyPath, _history);
        }
    }

    public IReadOnlyList<CriticalE2ERunResult> FlowHistory(string flowId)
    {
        lock (_gate) return _history.Where(r => r.FlowId == flowId).OrderByDescending(r => r.StartedAt).ToList();
    }

    public int SetArchived(string flowId, IReadOnlyCollection<string> runIds, DateTimeOffset? archivedAt, string? reason)
    {
        var ids = runIds.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            var changed = 0;
            for (var i = 0; i < _history.Count; i++)
            {
                var run = _history[i];
                if (run.FlowId != flowId || !ids.Contains(run.RunId) || run.Archived == (archivedAt is not null)) continue;
                // Only the archive marker moves. Status, steps, timestamps and build are the evidence and stay as recorded.
                _history[i] = run with { ArchivedAt = archivedAt, ArchiveReason = archivedAt is null ? null : reason };
                changed++;
            }
            if (changed > 0) Write(_historyPath, _history);
            return changed;
        }
    }

    public int RemoveRuns(string flowId, IReadOnlyCollection<string> runIds)
    {
        var ids = runIds.ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            var removed = _history.RemoveAll(r => r.FlowId == flowId && ids.Contains(r.RunId));
            if (removed > 0) Write(_historyPath, _history);
            return removed;
        }
    }

    public IReadOnlyList<string> Modules(string environmentId) => [.. RegisteredModules(environmentId).Select(m => m.Name)];

    public IReadOnlyList<CriticalE2ERegisteredModule> RegisteredModules(string environmentId)
    {
        lock (_gate) return _modules.TryGetValue(environmentId ?? "", out var modules) ? [.. modules.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)] : [];
    }

    public bool RegisterModule(string environmentId, CriticalE2ERegisteredModule module)
    {
        var name = module.Name.Trim();
        if (name.Length == 0 || name.Length > 80) throw new ArgumentException("A module name is required (at most 80 characters).");
        lock (_gate)
        {
            var modules = _modules.TryGetValue(environmentId ?? "", out var existing) ? existing : _modules[environmentId ?? ""] = [];
            if (modules.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))) return false;
            if (modules.Count >= 64) throw new ArgumentException("At most 64 modules can be registered for an environment.");
            modules.Add(module with { Name = name });
            Write(_modulesPath, _modules);
            return true;
        }
    }

    public bool UnregisterModule(string environmentId, string name)
    {
        lock (_gate)
        {
            if (!_modules.TryGetValue(environmentId ?? "", out var modules) || modules.RemoveAll(m => string.Equals(m.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) == 0) return false;
            Write(_modulesPath, _modules);
            return true;
        }
    }

    private T? Read<T>(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default; }
        catch (Exception ex)
        {
            // A corrupt file must not stop the backend from starting; it is QA history, not the system of record.
            _logger.LogWarning(ex, "Critical E2E store could not read {Path}; starting empty", Path.GetFileName(path));
            return default;
        }
    }

    public int ClearAll()
    {
        lock (_flows)
        {
            var removed = _flows.Count + _history.Count;
            _flows.Clear();
            _history.Clear();
            _modules.Clear();
            Write(_flowsPath, new List<CriticalE2EFlowDefinition>());
            Write(_historyPath, new List<CriticalE2ERunResult>());
            Write(_modulesPath, new Dictionary<string, List<CriticalE2ERegisteredModule>>());
            return removed;
        }
    }

    private void Write<T>(string path, T value)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, path, overwrite: true);   // a half-written history file is worse than an old one
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Critical E2E store could not write {Path}", Path.GetFileName(path));
        }
    }
}
