using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.ContractEvolution;

/// <summary>
/// A domain's business rules on top of the structural engine: what a change means (a new code-list value, a changed identity structure).
/// Return null when the rules say nothing about a change; the change then stays semantically Not evaluated, never Compatible.
/// </summary>
public interface IContractSemanticRules
{
    ContractSemanticAssessment? Assess(ContractChange change);
}

/// <summary>
/// Contract impact: compares a contract's current snapshot with its baseline, classifies each change, and walks a data-driven dependency
/// graph from the changed contract through every boundary that produces, transports, consumes or maps it — following a mapping only for
/// the paths it maps. Structural and semantic compatibility are kept apart; the overall result is the worse of the two. A boundary whose
/// facts are not known is Not evaluated; an impact that only might arrive through a mapping is Potential, never a confirmed failure.
/// </summary>
public static class ContractImpactAnalyzer
{
    private sealed record Pending(string ContractId, List<ContractChange> Changes, bool Potential, bool Unknown, int Depth, string Origin);

    public static ContractImpactResult Analyze(ContractSnapshot? previous, ContractSnapshot current, ContractDependencyGraph graph, IContractSemanticRules? semantic = null)
    {
        var contractId = current.Descriptor.ContractId;
        if (previous is null)
        {
            var reached = Reachable(graph, contractId);
            return new ContractImpactResult
            {
                Contract = current.Descriptor, CurrentFingerprint = current.Fingerprint, BaselineAvailable = false, Overall = ContractCompatibility.NotEvaluated,
                Boundaries = reached.Select(item => Impact(item.Boundary, ContractCompatibility.NotEvaluated, ContractCompatibility.NotEvaluated, false, item.Depth,
                    ["No approved baseline of this contract exists, so no change can be evaluated."])).ToList(),
                Unknowns = ["No approved baseline: compare after a baseline has been approved."],
            };
        }

        var changes = ContractDiff.Compare(previous, current);
        var semantics = changes.ToDictionary(change => change, change => semantic?.Assess(change)
            ?? new ContractSemanticAssessment(ContractCompatibility.NotEvaluated, "No domain rule covers this change."));
        var semanticsByChange = new Dictionary<ContractChange, ContractSemanticAssessment>(semantics);
        var assessments = changes.Select(change => new ContractChangeAssessment(change,
            ContractCompatibilityRules.Structural(change, ContractPerspective.Producer, change.Path.Length > 0 ? [change.Path] : null).Compatibility,
            ContractCompatibilityRules.Structural(change, ContractPerspective.Consumer, change.Path.Length > 0 ? [change.Path] : null).Compatibility,
            semantics[change].Compatibility, semantics[change].Reason)).ToList();

        var boundaries = new List<ContractBoundaryImpact>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Pending>();
        queue.Enqueue(new(contractId, changes, false, false, 0, contractId));
        while (queue.TryDequeue(out var pending))
        {
            if (!visited.Add(pending.ContractId)) continue;
            foreach (var boundary in graph.Boundaries.Where(item => string.Equals(item.ContractId, pending.ContractId, StringComparison.Ordinal)))
            {
                boundaries.Add(Evaluate(boundary, pending, current, semanticsByChange));
                if (boundary.Role != ContractBoundaryRole.Maps || boundary.OutputContractId is not { } output) continue;
                if (pending.Unknown || (pending.Changes.Count > 0 && boundary.Mapping is null))
                    queue.Enqueue(new(output, [], true, true, pending.Depth + 1, boundary.Label));
                else if (pending.Changes.Count == 0)
                    queue.Enqueue(new(output, [], pending.Potential, false, pending.Depth + 1, boundary.Label));
                else
                {
                    var mapped = pending.Changes.Where(change => change.Path.Length == 0 || boundary.Mapping!.Keys.Any(key => ContractCompatibilityRules.Covers(key, change.Path))).ToList();
                    if (boundary.PassThrough && mapped.All(change => change.Path.Length > 0 && change.Change != ContractChangeClass.FieldRemoved))
                    {
                        // A declared pass-through carries each mapped change onto its target paths: downstream sees exactly that change.
                        var carried = mapped.SelectMany(change => Translate(change, boundary.Mapping!)).ToList();
                        foreach (var (translated, original) in carried) semanticsByChange[translated] = semanticsByChange[original];
                        queue.Enqueue(new(output, [.. carried.Select(item => item.Translated)], pending.Potential, false, pending.Depth + 1, boundary.Label));
                    }
                    else queue.Enqueue(new(output, mapped, pending.Potential || mapped.Count > 0, false, pending.Depth + 1, boundary.Label));
                }
            }
        }

        var first = boundaries.FirstOrDefault(item => item.Overall == ContractCompatibility.Breaking && !item.Potential);
        var overall = boundaries.Count > 0
            ? ContractCompatibilityRules.Worst(boundaries.Select(item => item.Overall))
            : ContractCompatibilityRules.Worst(assessments.Select(item => ContractCompatibilityRules.Worst([item.ProducerStructural, item.ConsumerStructural, item.Semantic])));
        return new ContractImpactResult
        {
            Contract = current.Descriptor, PreviousFingerprint = previous.Fingerprint, CurrentFingerprint = current.Fingerprint, BaselineAvailable = true,
            Changed = previous.Fingerprint != current.Fingerprint, Changes = assessments, Boundaries = boundaries, FirstIncompatibleBoundary = first,
            Overall = previous.Fingerprint == current.Fingerprint ? ContractCompatibility.Compatible : overall,
            Unknowns = [.. boundaries.Where(item => item.Overall == ContractCompatibility.NotEvaluated).Select(item => $"{item.Label}: {item.Reasons.FirstOrDefault()}")],
        };
    }

    private static ContractBoundaryImpact Evaluate(ContractBoundary boundary, Pending pending, ContractSnapshot current,
        IReadOnlyDictionary<ContractChange, ContractSemanticAssessment> semantics)
    {
        if (pending.Unknown)
            return Impact(boundary, ContractCompatibility.NotEvaluated, ContractCompatibility.NotEvaluated, true, pending.Depth,
                [$"{pending.Origin}: the mapping is not known, so whether the change reaches this boundary is not evaluated."]);
        if (pending.Changes.Count == 0)
            return Impact(boundary, ContractCompatibility.Compatible, ContractCompatibility.Compatible, pending.Potential, pending.Depth,
                [pending.Depth == 0 ? "The contract is unchanged." : $"No changed data reaches this contract through {pending.Origin}."]);

        var semantic = ContractCompatibilityRules.Worst(pending.Changes.Select(change => semantics[change].Compatibility));
        var semanticReasons = pending.Changes.Where(change => semantics[change].Compatibility != ContractCompatibility.Compatible)
            .Select(change => $"{Label(change)}: {semantics[change].Reason}").Distinct().Take(10).ToList();

        if (pending.Potential)
            return Impact(boundary, ContractCompatibility.NeedsReview, semantic, true, pending.Depth,
                [$"Changed data reaches this contract through {pending.Origin} ({string.Join(", ", pending.Changes.Select(Label).Distinct().Take(5))}); this boundary may be affected.", .. semanticReasons]);

        switch (boundary.Role)
        {
            case ContractBoundaryRole.Transports:
                return Impact(boundary, ContractCompatibility.Compatible, ContractCompatibility.Compatible, false, pending.Depth,
                    ["Transport only: the message is carried without its structure being read."]);
            case ContractBoundaryRole.Produces:
            case ContractBoundaryRole.Consumes:
            {
                var perspective = boundary.Role == ContractBoundaryRole.Produces ? ContractPerspective.Producer : ContractPerspective.Consumer;
                var results = pending.Changes.Select(change => (change, result: ContractCompatibilityRules.Structural(change, perspective, boundary.Paths, boundary.UnknownMembers))).ToList();
                var reasons = results.Where(item => item.result.Compatibility != ContractCompatibility.Compatible).Select(item => $"{Label(item.change)}: {item.result.Reason}").Distinct().Take(10).ToList();
                var structural = ContractCompatibilityRules.Worst(results.Select(item => item.result.Compatibility));
                if (boundary.Role == ContractBoundaryRole.Consumes && boundary.SupportedVersions is { } supported
                    && !supported.Contains(current.Descriptor.Version ?? "", StringComparer.Ordinal))
                {
                    structural = ContractCompatibilityRules.Worst([structural, ContractCompatibility.NeedsReview]);
                    reasons.Insert(0, $"Contract version {current.Descriptor.Version ?? "(not declared)"} is not among the versions this consumer declares ({string.Join(", ", supported)}).");
                }
                return Impact(boundary, structural, semantic, false, pending.Depth, reasons.Count > 0 ? [.. reasons, .. semanticReasons] : ["Structurally compatible for this party.", .. semanticReasons]);
            }
            default:
            {
                if (boundary.Mapping is not { } mapping)
                    return Impact(boundary, ContractCompatibility.NotEvaluated, semantic, false, pending.Depth, ["The mapping is not known, so its compatibility is not evaluated.", .. semanticReasons]);
                var results = pending.Changes.Select(change => (change, result: MappingStructural(change, mapping, boundary))).ToList();
                var reasons = results.Where(item => item.result.Compatibility != ContractCompatibility.Compatible).Select(item => $"{Label(item.change)}: {item.result.Reason}").Distinct().Take(10).ToList();
                return Impact(boundary, ContractCompatibilityRules.Worst(results.Select(item => item.result.Compatibility)), semantic, false, pending.Depth,
                    reasons.Count > 0 ? [.. reasons, .. semanticReasons] : ["No mapped source path is affected.", .. semanticReasons]);
            }
        }
    }

    /// <summary>A mapping's input side: removed mapped sources break it, changed mapped sources need review, required unmapped data is reported.</summary>
    private static (ContractCompatibility Compatibility, string Reason) MappingStructural(ContractChange change, Dictionary<string, List<string>> mapping, ContractBoundary boundary)
    {
        var key = change.Path.Length == 0 ? null : mapping.Keys.FirstOrDefault(item => ContractCompatibilityRules.Covers(item, change.Path) || ContractCompatibilityRules.Covers(change.Path, item));
        var target = key is null ? "" : $" (mapped to {string.Join(", ", mapping[key])})";
        return change.Change switch
        {
            ContractChangeClass.NamespaceChanged or ContractChangeClass.VersionChanged or ContractChangeClass.None => (ContractCompatibility.NeedsReview, "The mapping's input contract changed as a whole."),
            ContractChangeClass.FieldRemoved when key is not null => (ContractCompatibility.Breaking, $"Mapped source {change.Path} was removed{target}."),
            ContractChangeClass.FieldAddedRequired when key is null => (ContractCompatibility.NeedsReview, $"Required {change.Path} is not mapped; its data does not reach {boundary.OutputContractId ?? "the output"}."),
            ContractChangeClass.FieldAddedOptional when key is null => (ContractCompatibility.Compatible, $"{change.Path} is not mapped (structurally ignored)."),
            _ when key is not null && boundary.PassThrough => (ContractCompatibility.Compatible, $"Mapped source {change.Path} is carried unchanged{target}; its consumers are evaluated on it."),
            _ when key is not null => (ContractCompatibility.NeedsReview, $"Mapped source {change.Path} changed{target}."),
            _ => (ContractCompatibility.Compatible, $"{change.Path} is not mapped."),
        };
    }

    /// <summary>A mapped change expressed on each target path of a pass-through mapping (a mapped ancestor carries the descendant along).</summary>
    private static IEnumerable<(ContractChange Translated, ContractChange Original)> Translate(ContractChange change, Dictionary<string, List<string>> mapping)
    {
        foreach (var (source, targets) in mapping)
        {
            var normalized = ContractSnapshots.NormalizePath(source);
            if (!ContractCompatibilityRules.Covers(normalized, change.Path)) continue;
            var rest = change.Path.Length > normalized.Length ? change.Path[normalized.Length..] : "";
            foreach (var target in targets)
                yield return (change with { Path = ContractSnapshots.NormalizePath(target) + rest, Detail = $"{change.Detail} (carried to {ContractSnapshots.NormalizePath(target) + rest})" }, change);
        }
    }

    private static List<(ContractBoundary Boundary, int Depth)> Reachable(ContractDependencyGraph graph, string contractId)
    {
        var result = new List<(ContractBoundary, int)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string, int)>([(contractId, 0)]);
        while (queue.TryDequeue(out var item))
        {
            if (!visited.Add(item.Item1)) continue;
            foreach (var boundary in graph.Boundaries.Where(b => string.Equals(b.ContractId, item.Item1, StringComparison.Ordinal)))
            {
                result.Add((boundary, item.Item2));
                if (boundary.Role == ContractBoundaryRole.Maps && boundary.OutputContractId is { } output) queue.Enqueue((output, item.Item2 + 1));
            }
        }
        return result;
    }

    private static ContractBoundaryImpact Impact(ContractBoundary boundary, ContractCompatibility structural, ContractCompatibility semantic, bool potential, int depth, List<string> reasons) =>
        new(boundary.Id, boundary.Label, boundary.Role, boundary.ContractId, structural, semantic,
            boundary.Role == ContractBoundaryRole.Transports ? structural : ContractCompatibilityRules.Worst([structural, semantic]), potential, depth, reasons, boundary.Evidence);

    private static string Label(ContractChange change) => change.Path.Length == 0 ? change.Change.ToString() : $"{change.Change} {change.Path}";
}
