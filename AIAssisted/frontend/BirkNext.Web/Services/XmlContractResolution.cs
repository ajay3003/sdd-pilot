using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>How a source snapshot or contract was chosen. <see cref="NeedsDecision"/>: several equally valid candidates and nothing decides.</summary>
public enum XmlSelectionState { None, NeedsDecision, Preselected, Bound, Explicit }

/// <summary>One snapshot that declares XSDs for the test context, with the evidence that can select it. The analysis time is display only.</summary>
public sealed record XmlSourceOption(Guid SnapshotId, string ArchiveFileName, DateTimeOffset? AnalyzedAt, bool Bound, string? BoundIntegration,
    bool Recognized, bool ProjectBound, bool ContractConfirmed, int MatchingContracts)
{
    public string ShortId => SnapshotId.ToString("N")[..8];

    /// <summary>"Tiltak 2.zip · Analyzed 2026-10-29 07:15 UTC · snapshot 148f9899 · bound in IQR".</summary>
    public string Label => $"{ArchiveFileName}{(AnalyzedAt is { } at ? $" · Analyzed {at.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "")} · snapshot {ShortId}"
        + (ProjectBound ? " · bound in Project setup" : Bound ? " · bound in IQR" : Recognized ? " · recognized source" : "")
        + (MatchingContracts == 0 ? " · no matching XSD" : "");
}

/// <summary>An explicit, saved choice (references only).</summary>
public sealed record XmlExplicitSelection(Guid SnapshotId, string? File);

/// <summary>
/// The canonical source snapshot + contract resolution for an XML fixture context: the chosen snapshot and contract with their state and
/// reason, every candidate, and ambiguity. One resolver for Test Data (and its readiness); it reads only what the backend already resolved
/// for the Test Pack template (IQR bindings, verified contract relationships, source-role recognition, exact contract-name match) plus the
/// Target Environment → Project bindings and an explicit saved choice.
/// </summary>
public sealed record XmlContractSelection(
    IReadOnlyList<XmlSourceOption> Snapshots,
    XmlSourceOption? Snapshot,
    XmlSelectionState SnapshotState,
    string? SnapshotReason,
    IReadOnlyList<IntegrationTemplateSchemaCandidate> Contracts,
    IntegrationTemplateSchemaCandidate? Contract,
    XmlSelectionState ContractState,
    string? ContractReason,
    IReadOnlyList<IntegrationTemplateSchemaCandidate> OtherContracts)
{
    public bool Resolved => Snapshot is not null && Contract is not null && ContractState != XmlSelectionState.NeedsDecision;
}

public static class XmlContractResolution
{
    /// <summary>
    /// Precedence (first level with candidates decides; one candidate is chosen, several are a decision, never the newest):
    /// 1 explicit saved selection · 2 confirmed contract relationship on an IQR-bound snapshot · 3 Target Environment → Project binding ·
    /// 4 IQR binding of the integration · 5 exact form-data-type ↔ schema match in the recognized source · 6/7 the only snapshot declaring the
    /// contract · 8 Needs decision.
    /// </summary>
    public static XmlContractSelection Resolve(IntegrationTemplateView context, XmlExplicitSelection? chosen = null, IReadOnlyCollection<Guid>? projectBound = null)
    {
        projectBound ??= [];
        var matching = context.SchemaCandidates.DistinctBy(c => (c.SnapshotId, c.File)).ToList();
        var other = context.OtherSchemaCandidates.DistinctBy(c => (c.SnapshotId, c.File)).ToList();
        var snapshots = matching.Concat(other).GroupBy(c => c.SnapshotId).Select(g =>
        {
            var first = g.First();
            var named = g.Where(c => c.MatchesContract).ToList();
            return new XmlSourceOption(g.Key, first.ArchiveFileName, first.AnalyzedAt, g.Any(c => c.Bound), g.Select(c => c.BoundIntegration).FirstOrDefault(n => n is not null),
                g.Any(c => c.Recognized), projectBound.Contains(g.Key), named.Any(c => c.ContractConfirmed && c.Bound), named.Count);
        })
        // Order: evidence strength, then name — never the analysis date.
        .OrderByDescending(s => s.ProjectBound).ThenByDescending(s => s.ContractConfirmed).ThenByDescending(s => s.Bound).ThenByDescending(s => s.Recognized)
        .ThenByDescending(s => s.MatchingContracts > 0).ThenBy(s => s.ArchiveFileName, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.SnapshotId).ToList();

        var flow = context.Flow ?? context.DisplayName;
        (XmlSourceOption? Snapshot, XmlSelectionState State, string? Reason) Level(Func<XmlSourceOption, bool> qualifies, XmlSelectionState state, Func<XmlSourceOption, string> reason)
        {
            var hits = snapshots.Where(s => s.MatchingContracts > 0 && qualifies(s)).ToList();
            return hits.Count switch
            {
                0 => (null, XmlSelectionState.None, null),
                1 => (hits[0], state, reason(hits[0])),
                _ => (null, XmlSelectionState.NeedsDecision, $"{hits.Count} compatible source snapshots found — review required"),
            };
        }

        XmlSourceOption? snapshot = null;
        var snapshotState = XmlSelectionState.None;
        string? snapshotReason = null;
        if (chosen is not null && snapshots.FirstOrDefault(s => s.SnapshotId == chosen.SnapshotId) is { } explicitSnapshot)
            (snapshot, snapshotState, snapshotReason) = (explicitSnapshot, XmlSelectionState.Explicit, "Selected explicitly in Test Data");
        else
        {
            foreach (var (qualifies, state, reason) in new (Func<XmlSourceOption, bool>, XmlSelectionState, Func<XmlSourceOption, string>)[]
            {
                (s => s.ContractConfirmed, XmlSelectionState.Bound, s => $"Bound from confirmed integration contract ({s.BoundIntegration ?? flow})"),
                (s => s.ProjectBound, XmlSelectionState.Bound, _ => "Bound from Project setup (Target Environment → Project)"),
                (s => s.Bound, XmlSelectionState.Bound, s => $"Bound from {s.BoundIntegration ?? flow} integration evidence (IQR)"),
                (s => s.Recognized, XmlSelectionState.Preselected, _ => $"Preselected from {flow} integration evidence: the form data type matches the consumer schema"),
                (_ => true, XmlSelectionState.Preselected, _ => "Preselected: the only source snapshot that declares this contract"),
            })
            {
                var (hit, hitState, hitReason) = Level(qualifies, state, reason);
                if (hitState == XmlSelectionState.None) continue;
                (snapshot, snapshotState, snapshotReason) = (hit, hitState, hitReason);
                break;
            }
        }

        var contracts = snapshot is null ? [] : matching.Where(c => c.SnapshotId == snapshot.SnapshotId).OrderBy(c => c.File, StringComparer.Ordinal).ToList();
        var others = snapshot is null ? other : other.Where(c => c.SnapshotId == snapshot.SnapshotId).ToList();
        IntegrationTemplateSchemaCandidate? contract = null;
        var contractState = XmlSelectionState.None;
        string? contractReason = snapshot is null ? null : "No XSD in this snapshot is named as the contract — choose one under Show all schemas";
        if (snapshot is not null && chosen?.SnapshotId == snapshot.SnapshotId && chosen.File is { } file
            && contracts.Concat(others).FirstOrDefault(c => c.File == file) is { } explicitContract)
            (contract, contractState, contractReason) = (explicitContract, XmlSelectionState.Explicit, "Selected explicitly in Test Data");
        else if (contracts.Count == 1)
            (contract, contractState, contractReason) = (contracts[0],
                contracts[0].ContractConfirmed ? XmlSelectionState.Bound : snapshotState == XmlSelectionState.Explicit ? XmlSelectionState.Preselected : snapshotState,
                contracts[0].ContractConfirmed ? $"Confirmed consumer contract ({contracts[0].BoundIntegration ?? flow})" : $"Exact form data type ↔ schema match ({context.SchemaName})");
        else if (contracts.Count > 1)
            (contractState, contractReason) = (XmlSelectionState.NeedsDecision, $"{contracts.Count} compatible contracts found — review required");
        return new(snapshots, snapshot, snapshotState, snapshotReason, contracts, contract, contractState, contractReason, others);
    }

    public static string StateLabel(XmlSelectionState state) => state switch
    {
        XmlSelectionState.Explicit => "Explicit",
        XmlSelectionState.Bound => "Bound",
        XmlSelectionState.Preselected => "Preselected",
        XmlSelectionState.NeedsDecision => "Needs decision",
        _ => "Not available",
    };
}
