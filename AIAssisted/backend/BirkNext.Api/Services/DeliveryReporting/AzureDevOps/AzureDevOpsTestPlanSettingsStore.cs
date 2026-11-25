using System.Text.Json;
using BirkNext.TestPlanning;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.DeliveryReporting.AzureDevOps;

/// <summary>
/// System Settings → Azure DevOps – Testplaner, machine-local (App_Data/azure-devops/test-plans.json, beside the stored connections): the system
/// default destination and the plan/suite creates whose answer was lost (pending until checked). Names and ids only — never a credential.
/// The per-project default lives in the project's export settings (<see cref="AdoExportProfile.DefaultDestination"/>).
/// </summary>
public sealed class AzureDevOpsTestPlanSettingsStore
{
    private sealed record State
    {
        public AdoSavedDestination? SystemDefault { get; init; }
        public List<AdoPendingCreation> Pending { get; init; } = [];
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();

    public AzureDevOpsTestPlanSettingsStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "test-plans.json");
    }

    private State Read()
    {
        if (!File.Exists(_path)) return new();
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(_path), Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private void Write(State state)
    {
        // Write-then-replace so a crash never leaves half a file.
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, _path, overwrite: true);
    }

    public AdoSavedDestination? SystemDefault() { lock (_gate) return Read().SystemDefault; }

    public AdoSavedDestination? SaveSystemDefault(AdoTestPlanDestination? destination, string actor, DateTimeOffset at)
    {
        lock (_gate)
        {
            var saved = destination is null ? null : new AdoSavedDestination(destination, actor, at);
            Write(Read() with { SystemDefault = saved });
            return saved;
        }
    }

    public List<AdoPendingCreation> Pending() { lock (_gate) return [.. Read().Pending.OrderBy(p => p.AttemptedAt)]; }

    /// <summary>The pending create with the same target (kind, organization, project, plan, parent and name), if any.</summary>
    public AdoPendingCreation? PendingFor(AdoPendingKind kind, string organization, string project, int? planId, int? parentSuiteId, string name)
    {
        lock (_gate) return Read().Pending.FirstOrDefault(p => Same(p, kind, organization, project, planId, parentSuiteId, name));
    }

    private static bool Same(AdoPendingCreation p, AdoPendingKind kind, string organization, string project, int? planId, int? parentSuiteId, string name) =>
        p.Kind == kind && string.Equals(p.Organization, organization, StringComparison.OrdinalIgnoreCase) && string.Equals(p.Project, project, StringComparison.OrdinalIgnoreCase)
        && p.PlanId == planId && p.ParentSuiteId == parentSuiteId && string.Equals(p.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase);

    public AdoPendingCreation AddPending(AdoPendingCreation pending)
    {
        lock (_gate)
        {
            var state = Read();
            Write(state with { Pending = [.. state.Pending.Where(p => p.Id != pending.Id), pending] });
            return pending;
        }
    }

    public AdoPendingCreation? GetPending(Guid id) { lock (_gate) return Read().Pending.FirstOrDefault(p => p.Id == id); }

    /// <summary>Reset everything only: removes the system default destination and the unchecked creates (the connections are not touched).</summary>
    public void Clear()
    {
        lock (_gate) { if (File.Exists(_path)) File.Delete(_path); }
    }

    public void RemovePending(Guid id)
    {
        lock (_gate)
        {
            var state = Read();
            if (state.Pending.All(p => p.Id != id)) return;
            Write(state with { Pending = [.. state.Pending.Where(p => p.Id != id)] });
        }
    }
}
