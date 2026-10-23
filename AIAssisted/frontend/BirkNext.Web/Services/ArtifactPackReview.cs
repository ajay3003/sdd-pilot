using BirkNext.Web.Models;
using BirkNext.Web.Services.Explorers;

namespace BirkNext.Web.Services;

/// <summary>
/// Document Quality Review of one artifact pack: the review ran on that pack's own artifacts plus the shared ones that apply to it — never a
/// sibling pack's. Which roles were present is recorded as it was ("Artifacts present"), never as a mandatory 5/5.
/// </summary>
public sealed record ArtifactPackReviewResult(
    ArtifactPackSummary Pack,
    IReadOnlyList<ArtifactPackReviewInput> Inputs,
    QualityReviewReport? Report,
    string? Error = null)
{
    /// <summary>Specification, plan and tasks are all present for the pack (own or shared); otherwise the pack is partial.</summary>
    public bool Complete => ArtifactPackReview.CoreRoles.All(r => Inputs.Any(i => i.Role == r));
    public string CompletenessLabel => Complete ? "Complete" : "Partial";
    public int Findings => Report?.TotalFindings ?? 0;
    /// <summary>The pack-level score, or null when nothing in the pack could be assessed.</summary>
    public double? Score => Report is { AssessedPacks: > 0 } r ? r.OverallScore : null;
}

/// <summary>One artifact a pack review read: its role, where it applies (pack or shared) and its path.</summary>
public sealed record ArtifactPackReviewInput(WorkspaceArtifactType Role, ArtifactScope Scope, string Path);

/// <summary>Every pack of a module reviewed independently, then aggregated. The aggregate is labelled as such and never replaces a pack's score.</summary>
public sealed record ArtifactPackReviewSummary(IReadOnlyList<ArtifactPackReviewResult> Packs)
{
    public int Assessed => Packs.Count(p => p.Score is not null);
    public int CompleteCount => Packs.Count(p => p.Complete);
    public int PartialCount => Packs.Count - CompleteCount;
    public int TotalFindings => Packs.Sum(p => p.Findings);

    /// <summary>The module aggregate: the mean of the assessed packs' scores. Null when no pack could be assessed.</summary>
    public double? AggregateScore => Assessed == 0 ? null : Packs.Where(p => p.Score is not null).Average(p => p.Score!.Value);

    /// <summary>"3 artifact packs assessed · 2 complete · 1 partial · 78 findings across 3 packs".</summary>
    public string Headline =>
        $"{Assessed} artifact pack{(Assessed == 1 ? "" : "s")} assessed · {CompleteCount} complete · {PartialCount} partial · " +
        $"{TotalFindings} finding{(TotalFindings == 1 ? "" : "s")} across {Packs.Count} pack{(Packs.Count == 1 ? "" : "s")}";
}

public static class ArtifactPackReview
{
    /// <summary>"PackLocal", "ModuleShared" or "ProjectShared" — how an input applies to the reviewed pack.</summary>
    public static string ScopeName(ArtifactScope scope) => scope switch
    {
        ArtifactScope.Pack => "PackLocal",
        ArtifactScope.Module => "ModuleShared",
        _ => "ProjectShared",
    };

    public static string ScopeLabel(string scope) => scope switch
    {
        "PackLocal" => "Pack-local",
        "ModuleShared" => "Shared module artifact",
        "ProjectShared" => "Shared project artifact",
        _ => scope,
    };

    /// <summary>The exact inputs of a review from the explorer states it read: role, artifact id, path, pack, scope and content fingerprint.</summary>
    public static List<SddReviewInput> InputsOf(IEnumerable<ArtifactExplorerState> states) =>
        [.. states.Where(s => s is { Status: ExplorerArtifactStatus.Loaded, Selected: not null } && !string.IsNullOrEmpty(s.Content))
            .Select(s => new SddReviewInput
            {
                Role = s.Role.ToString(), ArtifactId = s.Selected!.Id, Path = s.Selected.SourcePath ?? s.Selected.FileName,
                Module = s.HasModules ? s.Module : null, Pack = s.HasPacks ? s.Pack : null,
                Scope = ScopeName(ArtifactPacks.ScopeOf(s.Selected)), Fingerprint = ArtifactFingerprint.Compute(s.Content!),
            })];

    /// <summary>The roles that make a pack complete for review. Constitution and data model are optional (and often shared).</summary>
    public static readonly IReadOnlyList<WorkspaceArtifactType> CoreRoles =
        [WorkspaceArtifactType.Specification, WorkspaceArtifactType.Plan, WorkspaceArtifactType.Tasks];

    /// <summary>
    /// Runs the selected review packs once per artifact pack, each on that pack's resolved inputs (<see cref="IArtifactExplorerContext.GetPackStateAsync"/>).
    /// A pack whose role is ambiguous within the pack is not assessed (the user chooses first); the others still run.
    /// </summary>
    public static async Task<ArtifactPackReviewSummary> RunAsync(IArtifactExplorerContext context, IQualityReviewService review,
        IReadOnlyList<ArtifactPackSummary> packs, IReadOnlyList<string> reviewPackIds, CancellationToken cancellationToken = default)
    {
        var results = new List<ArtifactPackReviewResult>();
        foreach (var pack in packs)
        {
            var states = new Dictionary<WorkspaceArtifactType, ArtifactExplorerState>();
            foreach (var role in CurrentWorkspaceSnapshot.WorkflowRoles)
                states[role] = await context.GetPackStateAsync(role, pack.Pack, cancellationToken);

            var inputs = states.Values.Where(s => s is { Status: ExplorerArtifactStatus.Loaded, Selected: not null } && !string.IsNullOrEmpty(s.Content))
                .Select(s => new ArtifactPackReviewInput(s.Role, ArtifactPacks.ScopeOf(s.Selected!), s.Selected!.SourcePath ?? s.Selected.FileName)).ToList();
            if (states.Values.FirstOrDefault(s => s.AmbiguousInPack) is { } ambiguous)
            {
                results.Add(new ArtifactPackReviewResult(pack, inputs, null,
                    $"Not assessed: pack {pack.Pack} has {ambiguous.Artifacts.Count} {ArtifactExplorerRoles.Label(ambiguous.Role)} artifacts. Choose one in the explorer first."));
                continue;
            }
            string? Text(WorkspaceArtifactType role) => states[role] is { Status: ExplorerArtifactStatus.Loaded, Content: { Length: > 0 } c } ? c : null;
            try
            {
                var report = await review.RunAsync(Text(WorkspaceArtifactType.Constitution), Text(WorkspaceArtifactType.Specification),
                    Text(WorkspaceArtifactType.Plan), Text(WorkspaceArtifactType.Tasks), Text(WorkspaceArtifactType.DataModel), [.. reviewPackIds]);
                results.Add(new ArtifactPackReviewResult(pack, inputs, report));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new ArtifactPackReviewResult(pack, inputs, null, $"The review of pack {pack.Pack} failed: {ex.Message}"));
            }
        }
        return new ArtifactPackReviewSummary(results);
    }
}
