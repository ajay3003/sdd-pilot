using BirkNext.CriticalE2E;

namespace BirkNext.Web.Models;

/// <summary>
/// A mutable working copy of a <see cref="CriticalE2EFlowDefinition"/> for the editor.
///
/// The definition itself is an immutable record, which is right for something that gets compared, stored and sent over
/// the wire, and wrong for something two-way bound to a form. Rather than loosen the contract for the sake of one
/// screen, the editor edits this and produces a definition on save.
/// </summary>
public sealed class CriticalE2EFlowDraft
{
    public string Id { get; set; } = "";
    public string Module { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public CriticalE2EFlowKind Kind { get; set; } = CriticalE2EFlowKind.Critical;
    public CriticalE2EExecutionMode Mode { get; set; } = CriticalE2EExecutionMode.CompanionBrowser;
    public bool Enabled { get; set; } = true;
    public bool RequiredForRelease { get; set; }
    public string AutomationBoundary { get; set; } = "";
    public string TestDataPolicy { get; set; } = "";
    public int TimeoutMs { get; set; } = 120_000;
    public List<CriticalE2EStepDefinition> Steps { get; set; } = [];

    public static CriticalE2EFlowDraft From(CriticalE2EFlowDefinition flow) => new()
    {
        Id = flow.Id, Module = flow.Module, Name = flow.Name, Description = flow.Description, Kind = flow.Kind, Mode = flow.Mode,
        Enabled = flow.Enabled, RequiredForRelease = flow.RequiredForRelease, AutomationBoundary = flow.AutomationBoundary,
        TestDataPolicy = flow.TestDataPolicy, TimeoutMs = flow.TimeoutMs, Steps = WithStableIds(flow.Steps),
    };

    public CriticalE2EFlowDefinition ToDefinition(string profileId, string environmentId) => new()
    {
        Id = Id, Module = Module.Trim(), Name = Name.Trim(), Description = Description, Kind = Kind, Mode = Mode,
        ProfileId = profileId, EnvironmentId = environmentId, Enabled = Enabled, RequiredForRelease = RequiredForRelease,
        AutomationBoundary = AutomationBoundary.Trim(), TestDataPolicy = TestDataPolicy.Trim(), TimeoutMs = TimeoutMs,
        // A companion browser flow always needs a human to sign in first; an integration flow reuses a context BirkNext
        // already holds. Deriving it removes a field nobody would ever set differently.
        AuthenticationRequirement = Mode == CriticalE2EExecutionMode.CompanionBrowser
            ? CriticalE2EAuthenticationRequirement.ManualBrowserLogin
            : CriticalE2EAuthenticationRequirement.ExistingApiContext,
        Steps = [.. Steps],
    };

    /// <summary>The definition's own rule, asked of the draft, so the editor refuses exactly what a run would refuse.</summary>
    public string? Problem(string profileId, string environmentId) => ToDefinition(profileId, environmentId).ConfigurationProblem();

    private static List<CriticalE2EStepDefinition> WithStableIds(IEnumerable<CriticalE2EStepDefinition> steps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return steps.Select(step =>
        {
            if (!string.IsNullOrWhiteSpace(step.StepId) && seen.Add(step.StepId)) return step;
            var id = $"step-{Guid.NewGuid():N}";
            seen.Add(id);
            return step with { StepId = id };
        }).ToList();
    }

    // Array position is the persisted execution order. Move the record, never reconstruct its contents.
    public bool MoveStep(string stepId, int destination)
    {
        var index = Steps.FindIndex(s => s.StepId == stepId);
        if (index < 0 || destination < 0 || destination >= Steps.Count || index == destination) return false;
        var step = Steps[index];
        Steps.RemoveAt(index);
        Steps.Insert(destination, step);
        return true;
    }

    public CriticalE2EStepDefinition InsertStep(int index)
    {
        var id = $"step-{Guid.NewGuid():N}";
        var step = Mode == CriticalE2EExecutionMode.CompanionBrowser
            ? new CriticalE2EStepDefinition { StepId = id, BrowserAction = CompanionActionKind.Click, Selector = new CompanionSelector() }
            : new CriticalE2EStepDefinition { StepId = id, IntegrationAction = CriticalE2EIntegrationKind.Http, Method = "GET", Expect = new CriticalE2EExpectation() };
        Steps.Insert(index, step);
        return step;
    }
}
