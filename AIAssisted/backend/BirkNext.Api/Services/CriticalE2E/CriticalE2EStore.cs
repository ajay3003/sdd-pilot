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
    /// <summary>Modules BirkNext knows about for an environment, so coverage can report a module with no flow at all.</summary>
    IReadOnlyList<string> Modules(string environmentId);
    void SetModules(string environmentId, IReadOnlyList<string> modules);
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
    private readonly ILogger<CriticalE2EStore> _logger;
    private readonly Dictionary<string, CriticalE2EFlowDefinition> _flows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CriticalE2ERunResult> _history = [];
    private readonly Dictionary<string, List<string>> _modules = new(StringComparer.OrdinalIgnoreCase);

    public CriticalE2EStore(IHostEnvironment environment, ILogger<CriticalE2EStore> logger)
        : this(Path.Combine(environment.ContentRootPath, "App_Data", "critical-e2e"), logger) { }

    public CriticalE2EStore(string directory, ILogger<CriticalE2EStore> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(directory);
        _flowsPath = Path.Combine(directory, "flows.json");
        _historyPath = Path.Combine(directory, "history.json");
        foreach (var flow in Read<List<CriticalE2EFlowDefinition>>(_flowsPath) ?? []) _flows[flow.Id] = flow;
        _history.AddRange(Read<List<CriticalE2ERunResult>>(_historyPath) ?? []);
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
            _flows[stored.Id] = stored;
            Write(_flowsPath, _flows.Values.ToList());
        }
        return stored;
    }

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

    public IReadOnlyList<string> Modules(string environmentId)
    {
        lock (_gate) return _modules.TryGetValue(environmentId ?? "", out var modules) ? modules.ToList() : [];
    }

    public void SetModules(string environmentId, IReadOnlyList<string> modules)
    {
        lock (_gate)
            _modules[environmentId ?? ""] = modules.Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();
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
