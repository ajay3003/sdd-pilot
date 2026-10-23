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
    public int Version { get; set; } = 1;
    public CriticalE2EFlowSource Source { get; set; } = CriticalE2EFlowSource.Manual;
    public string RequirementRef { get; set; } = "";
    /// <summary>The concrete business outcome; null until the author defines one.</summary>
    public CriticalE2EFinalOutcome? FinalOutcome { get; set; }
    // The structured boundary, edited field by field. AutomationBoundary stays as the legacy/explanatory note.
    public string BoundaryStart { get; set; } = "";
    public string BoundaryEnd { get; set; } = "";
    public string BoundaryDownstream { get; set; } = "";
    public bool BoundaryCoversDownstream { get; set; }
    public string BoundarySource { get; set; } = "Manual";
    public string? BoundaryNote { get; set; }
    /// <summary>Central Test Data aliases the flow uses (references only).</summary>
    public List<string> TestDataAliases { get; set; } = [];
    public List<CriticalE2EDataMapping> DataMappings { get; set; } = [];

    public static CriticalE2EFlowDraft From(CriticalE2EFlowDefinition flow) => new()
    {
        Id = flow.Id, Module = flow.Module, Name = flow.Name, Description = flow.Description, Kind = flow.Kind, Mode = flow.Mode,
        Enabled = flow.Enabled, RequiredForRelease = flow.RequiredForRelease, AutomationBoundary = flow.AutomationBoundary,
        TestDataPolicy = flow.TestDataPolicy, TimeoutMs = flow.TimeoutMs, Steps = WithStableIds(flow.Steps), Version = flow.Version,
        Source = flow.Source ?? CriticalE2EFlowSource.Manual, RequirementRef = flow.RequirementRef ?? "", FinalOutcome = flow.FinalOutcome,
        BoundaryStart = flow.Boundary?.Start ?? "", BoundaryEnd = flow.Boundary?.End ?? "", BoundaryDownstream = flow.Boundary?.Downstream ?? "",
        BoundaryCoversDownstream = flow.Boundary?.CoversDownstream ?? false, BoundarySource = flow.Boundary?.Source ?? "Manual", BoundaryNote = flow.Boundary?.Note,
        TestDataAliases = [.. flow.TestDataAliases ?? []], DataMappings = [.. flow.DataMappings ?? []],
    };

    /// <summary>The structured boundary, or null while neither end is stated.</summary>
    public CriticalE2EAutomationBoundary? Boundary =>
        BoundaryStart.Trim().Length == 0 && BoundaryEnd.Trim().Length == 0 && BoundaryDownstream.Trim().Length == 0 ? null : new()
        {
            Start = BoundaryStart.Trim(), End = BoundaryEnd.Trim(), Downstream = BoundaryDownstream.Trim(), CoversDownstream = BoundaryCoversDownstream,
            Source = BoundarySource, Note = BoundaryNote,
        };

    /// <summary>Applies a proposed boundary (e.g. from Integration Topology). The author can edit every field afterwards.</summary>
    public void ApplyBoundary(CriticalE2EAutomationBoundary boundary)
    {
        BoundaryStart = boundary.Start;
        BoundaryEnd = boundary.End;
        BoundaryDownstream = boundary.Downstream;
        BoundaryCoversDownstream = boundary.CoversDownstream;
        BoundarySource = boundary.Source;
        BoundaryNote = boundary.Note;
    }

    /// <summary>A field edited by hand after a suggestion: the provenance says so.</summary>
    public void BoundaryEdited()
    {
        if (BoundarySource is not ("Manual" or "") && !BoundarySource.StartsWith("Manual override", StringComparison.Ordinal))
            BoundarySource = $"Manual override (from {BoundarySource})";
    }

    /// <summary>The ${data:variable} placeholders the flow uses, in order of first use.</summary>
    public IReadOnlyList<string> Placeholders() =>
        [.. Steps.SelectMany(s => new[] { s.Value, s.Expected, s.ExecutionRoute, s.ExpectedDestination, s.Body, s.PathOrOperation })
            .Append(FinalOutcome?.Expected).SelectMany(TokensIn).Distinct(StringComparer.Ordinal)];

    private static IEnumerable<string> TokensIn(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var at = 0;
        while ((at = text.IndexOf("${data:", at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = text.IndexOf('}', at);
            if (end < 0) yield break;
            var name = text[(at + 7)..end];
            if (name.Length > 0) yield return name;
            at = end + 1;
        }
    }

    /// <summary>Maps a placeholder to central Test Data aliases (empty = explicitly unmapped). Replaces an earlier mapping of the same variable.</summary>
    public void Map(string variable, IReadOnlyList<string> aliases, BirkNext.TestData.TestDataDataType type, string target)
    {
        DataMappings.RemoveAll(m => string.Equals(m.Variable, variable, StringComparison.Ordinal));
        DataMappings.Add(new CriticalE2EDataMapping(variable, [.. aliases], type, target));
    }

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
        Version = Version,
        Source = Source,
        RequirementRef = string.IsNullOrWhiteSpace(RequirementRef) ? null : RequirementRef.Trim(),
        FinalOutcome = FinalOutcome,
        Boundary = Boundary,
        TestDataAliases = TestDataAliases.Count == 0 ? null : [.. TestDataAliases],
        // A mapping whose placeholder is no longer used is dropped, so nothing references data the flow does not need.
        DataMappings = DataMappings.Where(m => Placeholders().Contains(m.Variable)).ToList() is { Count: > 0 } used ? used : null,
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
