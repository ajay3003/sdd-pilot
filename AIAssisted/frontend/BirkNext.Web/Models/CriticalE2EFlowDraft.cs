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
        TestDataPolicy = flow.TestDataPolicy, TimeoutMs = flow.TimeoutMs, Steps = [.. flow.Steps],
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
}
