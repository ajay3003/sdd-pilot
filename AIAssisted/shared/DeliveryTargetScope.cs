using System.Text.Json.Serialization;
using BirkNext.PipelineReview;
using BirkNext.SourceDomains;

namespace BirkNext.Delivery;

// Delivery evidence scoped to ONE target (a performance target, a reviewed application). A Source Analysis snapshot of a monorepo defines many
// pipelines; only those that deliver the target under test are its evidence. Correlation is cautious: a confirmed mapping wins; the target host
// named in the pipeline evidence is a strong candidate; a component name shared by several pipelines is ambiguous — and an unresolved mapping is
// shown as unresolved, never as "every pipeline in the snapshot". Configured in pipeline source ≠ executed ≠ passed.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryTargetMappingState { Confirmed, StrongCandidate, Candidate, Ambiguous, Unresolved, NoPipelines }

/// <summary>One relevant pipeline and what it configures for the category (e.g. Performance) on the target's environment path.</summary>
public sealed record DeliveryTargetPipeline(string PipelineId, string PipelineName, string File, IReadOnlyList<string> Environments, string Reason)
{
    /// <summary>Gates of the category configured before the target environment's deployments (configured, never executed/passed here).</summary>
    public IReadOnlyList<DeliveryGate> Gates { get; init; } = [];
    /// <summary>Test steps of the category the pipeline configures.</summary>
    public IReadOnlyList<DeliveryTestDefinition> TestSteps { get; init; } = [];
    public IReadOnlyList<DeliveryGate> PostDeploy { get; init; } = [];
}

public sealed record DeliveryTargetScopeResult
{
    public DeliveryTargetMappingState State { get; init; }
    public string Summary { get; init; } = "";
    /// <summary>Primary evidence: confirmed or (strong) candidate pipelines for the target. Empty when unresolved.</summary>
    public IReadOnlyList<DeliveryTargetPipeline> Relevant { get; init; } = [];
    /// <summary>Pipelines that could be the target's (shared component name); a person confirms.</summary>
    public IReadOnlyList<DeliveryTargetPipeline> Candidates { get; init; } = [];
    /// <summary>Every other pipeline in the source: secondary, never presented as the target's.</summary>
    public IReadOnlyList<DeliveryTargetPipeline> Other { get; init; } = [];
    /// <summary>"Configured · not executed/observed" unless runtime metadata is present.</summary>
    public string Execution { get; init; } = "";
}

public static class DeliveryTargetScope
{
    private static readonly HashSet<string> EnvironmentWords = new(StringComparer.OrdinalIgnoreCase)
        { "dev", "develop", "development", "utv", "test", "tst", "qa", "at", "st", "int", "stage", "staging", "stg", "rc", "preprod", "prod", "production", "tt02", "local", "sandbox", "www", "app", "api" };

    /// <summary>The component tokens of a target origin: the first host label without environment words ("m2lbdev.bufetat.no" → ["m2lb"]).</summary>
    public static IReadOnlyList<string> TargetTokens(string targetOrigin)
    {
        if (!Uri.TryCreate(targetOrigin, UriKind.Absolute, out var uri)) return [];
        var label = uri.Host.Split('.')[0].ToLowerInvariant();
        var parts = label.Split('-', '_').Where(p => p.Length > 0).ToList();
        var tokens = new List<string>();
        foreach (var part in parts)
        {
            if (EnvironmentWords.Contains(part)) continue;
            var stripped = part;
            foreach (var env in EnvironmentWords.Where(w => w.Length >= 3 || w == "qa").OrderByDescending(w => w.Length))
                if (stripped.Length > env.Length + 1 && stripped.EndsWith(env, StringComparison.Ordinal)) { stripped = stripped[..^env.Length]; break; }
            if (stripped.Length >= 2) tokens.Add(stripped);
        }
        return tokens.Distinct().ToList();
    }

    /// <summary>Lower-case words of a name ("M2LB.Person.Api" → m2lb, person, api; "SkoleAdapter" → skole, adapter, skoleadapter).</summary>
    public static IReadOnlySet<string> Words(string? text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return words;
        foreach (var raw in text.Split(['.', '-', '_', '/', '\\', ' ', ':', '(', ')', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            words.Add(raw.ToLowerInvariant());
            var start = 0;
            for (var i = 1; i <= raw.Length; i++)
                if (i == raw.Length || char.IsUpper(raw[i]) && char.IsLower(raw[i - 1]))
                { words.Add(raw[start..i].ToLowerInvariant()); start = i; }
        }
        return words;
    }

    public static DeliveryTargetScopeResult Resolve(DeliveryEvidence evidence, string targetOrigin, string environmentName, string environmentType,
        IReadOnlyCollection<string> confirmedPipelineIds, ValidationCategory category)
    {
        var pipelines = evidence.Pipelines.Where(p => !p.IsTemplate).ToList();
        var execution = evidence.Runtime == DeliveryRuntimeState.NoRuntimeMetadata ? "Configured · not executed/observed" : evidence.RuntimeText;
        if (pipelines.Count == 0)
            return new() { State = DeliveryTargetMappingState.NoPipelines, Summary = "No runnable pipeline definitions in this source snapshot.", Execution = execution };
        var host = Uri.TryCreate(targetOrigin, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : "";
        var hostLabel = host.Split('.')[0];
        var tokens = TargetTokens(targetOrigin);
        var targets = new List<(string Name, string Kind)> { (environmentName, environmentType) };

        DeliveryTargetPipeline Describe(DeliveryPipeline p, string reason)
        {
            // Only deployments on the target's environment path count for its gates (a Production gate is not a Development gate).
            var deployments = evidence.Deployments.Where(d => d.Pipeline == p.Id).ToList();
            var onPath = deployments.Where(d => DeliveryEvidenceQueries.Map(d.EnvironmentLabel, d.EnvironmentKind, targets).State is not EnvironmentMappingState.Unmapped).ToList();
            if (onPath.Count == 0) onPath = deployments;
            return new(p.Id, p.Name, p.File, p.Environments, reason)
            {
                Gates = [.. onPath.SelectMany(d => d.Before).Where(g => g.Category == category)],
                PostDeploy = [.. onPath.SelectMany(d => d.PostDeploy).Where(g => g.Category == category)],
                TestSteps = [.. evidence.Tests.Where(t => t.Pipeline == p.Id && t.Category == category)],
            };
        }

        IReadOnlySet<string> PipelineWords(DeliveryPipeline p)
        {
            var words = new HashSet<string>(Words(p.Name).Concat(Words(System.IO.Path.GetFileNameWithoutExtension(p.File))), StringComparer.Ordinal);
            foreach (var d in evidence.Deployments.Where(d => d.Pipeline == p.Id))
                foreach (var what in d.DeploysWhat) words.UnionWith(Words(what));
            return words;
        }

        bool NamesHost(DeliveryPipeline p) => host.Length > 0 && (
            p.Name.Contains(host, StringComparison.OrdinalIgnoreCase) || p.File.Contains(hostLabel, StringComparison.OrdinalIgnoreCase)
            || evidence.Deployments.Where(d => d.Pipeline == p.Id).Any(d => d.DeploysWhat.Any(w => w.Contains(host, StringComparison.OrdinalIgnoreCase) || w.Equals(hostLabel, StringComparison.OrdinalIgnoreCase))));

        var confirmed = pipelines.Where(p => confirmedPipelineIds.Contains(p.Id, StringComparer.Ordinal)).ToList();
        if (confirmed.Count > 0)
            return Result(DeliveryTargetMappingState.Confirmed, $"{confirmed.Count} pipeline{(confirmed.Count == 1 ? "" : "s")} confirmed as delivering {host}.",
                confirmed.Select(p => Describe(p, "Confirmed by a person for this target.")).ToList(), []);

        var namingHost = pipelines.Where(NamesHost).ToList();
        if (namingHost.Count == 1)
            return Result(DeliveryTargetMappingState.StrongCandidate, $"{namingHost[0].Name} names {host} — strong candidate; confirm the mapping.",
                [Describe(namingHost[0], $"The pipeline evidence names {host}.")], []);

        var matching = tokens.Count == 0 ? [] : pipelines.Where(p => tokens.All(PipelineWords(p).Contains)).ToList();
        var candidates = (namingHost.Count > 1 ? namingHost : matching).ToList();
        if (candidates.Count == 1)
            return Result(DeliveryTargetMappingState.Candidate, $"{candidates[0].Name} matches the target component {string.Join(" ", tokens)} — candidate; confirm the mapping.",
                [Describe(candidates[0], $"Pipeline name matches the target component \"{string.Join(" ", tokens)}\".")], []);
        if (candidates.Count > 1)
            return Result(DeliveryTargetMappingState.Ambiguous,
                $"Pipeline source available · target-specific pipeline mapping unresolved: {candidates.Count} pipelines share the component name \"{string.Join(" ", tokens)}\" and none names {host}.",
                [], candidates.Select(p => Describe(p, $"Shares the component name \"{string.Join(" ", tokens)}\" — not proof it delivers {host}.")).ToList());
        return Result(DeliveryTargetMappingState.Unresolved, $"Pipeline source available · target-specific pipeline mapping unresolved: no pipeline names {host} or its component.", [], []);

        DeliveryTargetScopeResult Result(DeliveryTargetMappingState state, string summary, List<DeliveryTargetPipeline> relevant, List<DeliveryTargetPipeline> candidateList)
        {
            var used = relevant.Concat(candidateList).Select(p => p.PipelineId).ToHashSet(StringComparer.Ordinal);
            return new()
            {
                State = state, Summary = summary, Relevant = relevant, Candidates = candidateList, Execution = execution,
                Other = pipelines.Where(p => !used.Contains(p.Id)).Select(p => Describe(p, "Another pipeline in this source.")).ToList(),
            };
        }
    }

    /// <summary>"Performance test step configured · gate configured · Configured · not executed/observed" for one pipeline.</summary>
    public static string GateSummary(DeliveryTargetPipeline p, string execution) => string.Join(" · ", new[]
    {
        p.TestSteps.Count > 0 ? $"{p.TestSteps.Count} test step{(p.TestSteps.Count == 1 ? "" : "s")} configured" : "No test step configured",
        p.Gates.Count > 0 ? string.Join(", ", p.Gates.Select(g => $"{g.Label} — {g.StateText}").Distinct()) : "No gate before the deployment",
        execution,
    });
}
