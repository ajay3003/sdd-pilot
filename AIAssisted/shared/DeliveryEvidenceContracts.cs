using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using BirkNext.PipelineReview;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Delivery;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Shared Pipeline & Delivery Evidence. One parser (the Source Analysis CI/CD analyzer) stores pipeline definitions once per immutable
// snapshot; one derivation (template composition, gates, artifact lineage — the delivery rules Pipeline Review runs) turns them into
// delivery facts; every consumer reads this normalized evidence instead of re-reading YAML or re-deriving gates. Source is never runtime:
// a deployment defined ≠ deployment executed, a test step ≠ a passed test, an artifact definition ≠ a published artifact, and a pipeline
// environment label is not a Target Environment until canonically mapped. Optional Azure DevOps metadata is a separate provider.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryCapabilitySupport { Supported, Partial, NotSupported, Optional }

public sealed record DeliveryEvidenceProviderDescriptor(string Key, string Name, string Kind, DeliveryCapabilitySupport Support, bool RequiresNetwork,
    string Proves, string CannotProve);

public static class DeliveryEvidenceProviders
{
    public static IReadOnlyList<DeliveryEvidenceProviderDescriptor> All { get; } =
    [
        new("azure-pipelines-yaml", "Azure Pipelines YAML", "Source", DeliveryCapabilitySupport.Supported, false,
            "Triggers, stages, jobs, steps, local templates with literal parameters, conditions (as written), dependsOn, continueOnError, artifacts published/consumed, pipeline resources, deployment jobs and environments, approvals, variable names and variable-group names.",
            "Whether anything ran, passed, deployed or was published; runtime expressions and remote templates are not evaluated; secret values are never read."),
        new("github-actions", "GitHub Actions workflows", "Source", DeliveryCapabilitySupport.Partial, false,
            "Triggers, jobs, environments and run commands by pattern.", "Reusable workflows are not expanded; no run history."),
        new("gitlab-ci", "GitLab CI", "Source", DeliveryCapabilitySupport.Partial, false,
            "Stages, jobs, environments and scripts by pattern.", "Includes are not resolved; no run history."),
        new("jenkins", "Jenkinsfile", "Source", DeliveryCapabilitySupport.Partial, false,
            "Declarative stage names and sh/bat commands by pattern.", "Groovy is not evaluated; no run history."),
        new("delivery-derivation", "Delivery derivation", "Derived", DeliveryCapabilitySupport.Supported, false,
            "Template composition, deployment dependency paths, test gating (only from dependsOn/conditions/explicit gates — never from ordering), artifact promotion vs rebuild, post-deploy validation, environment progression.",
            "Gating hidden behind unresolved templates or runtime expressions is Unknown, never assumed."),
        new("azure-devops-metadata", "Azure DevOps metadata (optional)", "Runtime metadata", DeliveryCapabilitySupport.Optional, true,
            "Read-only definition names/paths, environments and their configured checks.",
            "Run history, stage results, published artifacts and deployment outcomes are not read; source-only review works fully without it."),
    ];

    public static DeliveryEvidenceProviderDescriptor Get(string key) => All.First(p => p.Key == key);

    public static string ForPlatform(PipelinePlatform platform) => platform switch
    {
        PipelinePlatform.AzurePipelines => "azure-pipelines-yaml",
        PipelinePlatform.GitHubActions => "github-actions",
        PipelinePlatform.GitLabCi => "gitlab-ci",
        _ => "jenkins",
    };
}

// ── Normalized delivery model ───────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArtifactStrategy { PromotedFromEarlierStage, BuiltInSameRun, RebuiltForEnvironment, Unknown, NoArtifact }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliveryRuntimeState { NoRuntimeMetadata, RunFound, StageExecuted, ArtifactPublished, DeploymentExecuted, TestResultAvailable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EnvironmentMappingState { Mapped, StrongCandidate, NeedsConfirmation, Ambiguous, Unmapped }

public sealed record DeliveryProvenance(string File, int Line, string Pipeline, string? Stage, string? Job, string? TemplateOrigin, string Provider);

/// <summary>A check of one category against one deployment: before it (gates / conditional / soft / not gating) or after it (post-deploy).</summary>
public sealed record DeliveryGate(ValidationCategory Category, string Label, GateState State, string StateText, DeliveryProvenance Provenance);

/// <summary>A deployment as the source defines it. <see cref="State"/> is always "Deployment defined": it never says the deployment ran.</summary>
public sealed record DeliveryDeployment(string Id, string Pipeline, string PipelineName, string? Stage, string Job, string EnvironmentLabel, SourceEnvironmentKind EnvironmentKind,
    IReadOnlyList<string> DeploysWhat, ArtifactStrategy Artifacts, string ArtifactBasis, IReadOnlyList<DeliveryGate> Before, IReadOnlyList<DeliveryGate> PostDeploy,
    string Approval, string? Condition, DeliveryProvenance Provenance, string State = "Deployment defined");

/// <summary>A test step as configured. <see cref="Gating"/> says which deployments it gates (from source relationships); it is never executed here.</summary>
public sealed record DeliveryTestDefinition(string Name, ValidationCategory Category, string Pipeline, string? Stage, string? Job, IReadOnlyList<string> Targets,
    IReadOnlyList<string> GatesDeployments, bool PostDeploy, bool ContinueOnError, string? Condition, string Gating, string Execution = "Configured — not executed by analysis");

public sealed record DeliveryArtifact(string Artifact, string Deployment, string Source, string? ProducerPipeline, string? ProducerStage, string State, string Basis);

public sealed record DeliveryPipeline(string Id, string Name, string File, PipelinePlatform Platform, bool IsTemplate, string Role, IReadOnlyList<string> Triggers,
    IReadOnlyList<string> Environments, string DefinitionFingerprint);

/// <summary>Normalized delivery evidence of one immutable source snapshot (derivation rules version recorded). Runtime metadata, when present,
/// is listed separately and never changes a source fact.</summary>
public sealed record DeliveryEvidence
{
    public Guid SnapshotId { get; init; }
    public string Archive { get; init; } = "";
    public int CiCdAnalyzerVersion { get; init; }
    public int RulesVersion { get; init; }
    public string State { get; init; } = "";
    public List<DeliveryPipeline> Pipelines { get; init; } = [];
    public List<DeliveryDeployment> Deployments { get; init; } = [];
    public List<DeliveryTestDefinition> Tests { get; init; } = [];
    public List<DeliveryArtifact> Artifacts { get; init; } = [];
    public List<string> UnresolvedTemplates { get; init; } = [];
    public DeliveryRuntimeState Runtime { get; init; } = DeliveryRuntimeState.NoRuntimeMetadata;
    public string RuntimeText { get; init; } = "No runtime metadata: run history, stage results, published artifacts and deployment outcomes are not read.";
    public List<string> Limitations { get; init; } = [];
}

// ── Projection from the delivery derivation ─────────────────────────────────────────────────────────────────────────────

public static class DeliveryEvidenceProjection
{
    public static string GateText(GateState state) => state switch
    {
        GateState.Gates => "Gate configured",
        GateState.Inherited => "Gate configured (inherited via promoted artifact)",
        GateState.Conditional => "Conditionally configured",
        GateState.SoftGate => "Configured · failure tolerated",
        GateState.After => "Post-deploy validation configured",
        GateState.NotGating => "Configured · does not gate deployment",
        _ => "Not assessed (unresolved template or expression)",
    };

    private static DeliveryProvenance P(PipelineEvidenceRef e, PipelinePlatform platform) =>
        new(e.File, e.Line, e.Pipeline, e.Stage, e.Job, e.TemplateOrigin, DeliveryEvidenceProviders.ForPlatform(platform));

    /// <summary>The normalized delivery evidence of a derived pipeline review (no YAML is read; the review itself reads only stored evidence).</summary>
    public static DeliveryEvidence From(PipelineReviewResult review, IReadOnlyList<PipelineDefinition>? definitions = null)
    {
        var platforms = review.Pipelines.ToDictionary(p => p.Id, p => p.Platform, StringComparer.Ordinal);
        PipelinePlatform Platform(string id) => platforms.TryGetValue(id, out var p) ? p : PipelinePlatform.AzurePipelines;
        var fingerprints = (definitions ?? []).ToDictionary(d => d.Id, DeliveryEvidenceQueries.DefinitionFingerprint, StringComparer.Ordinal);
        var deployments = review.Deployments.Select(d =>
        {
            var (strategy, basis) = Strategy(d, review);
            return new DeliveryDeployment(d.Id, d.Pipeline, d.PipelineName, d.Stage, d.Job, d.Environment, d.EnvironmentKind, d.DeploysWhat, strategy, basis,
                [.. d.Before.Select(g => new DeliveryGate(g.Category, g.Label, g.State, GateText(g.State), P(g.Evidence, Platform(d.Pipeline))))],
                [.. d.After.Select(g => new DeliveryGate(g.Category, g.Label, g.State, GateText(GateState.After), P(g.Evidence, Platform(d.Pipeline))))],
                d.Approval, d.Condition, P(d.Evidence, Platform(d.Pipeline)));
        }).ToList();
        var envOf = deployments.ToDictionary(d => d.Id, d => d.EnvironmentLabel, StringComparer.Ordinal);
        return new DeliveryEvidence
        {
            SnapshotId = review.SourceSnapshotId, Archive = review.ArchiveName, CiCdAnalyzerVersion = review.CiCdAnalyzerVersion, RulesVersion = review.RulesVersion, State = review.State,
            Pipelines = [.. review.Pipelines.Select(p => new DeliveryPipeline(p.Id, p.Name, p.File, p.Platform, p.IsTemplate, p.Role, [.. p.Triggers.Select(t => t.Text)], p.Environments,
                fingerprints.GetValueOrDefault(p.Id) ?? ""))],
            Deployments = deployments,
            Tests = [.. review.Tests.Select(t =>
            {
                var gates = t.Gates.Select(id => envOf.GetValueOrDefault(id) ?? id).Distinct(StringComparer.Ordinal).ToList();
                var post = deployments.Any(d => d.PostDeploy.Any(g => g.Label == t.Name && g.Provenance.Pipeline == t.Pipeline));
                return new DeliveryTestDefinition(t.Name, t.Category, t.Pipeline, t.Stage, t.Job, t.Targets, gates, post, t.ContinueOnError, t.Condition,
                    gates.Count > 0 ? $"Gates deployment to {string.Join(", ", gates)}" : post ? "Post-deploy validation" : "Does not gate a deployment");
            })],
            Artifacts = [.. review.Deployments.SelectMany(d => d.Artifacts.Select(a => new DeliveryArtifact(a.Artifact, d.Id, a.Source, a.ProducerPipeline, a.ProducerStage, a.State.ToString(), a.Basis)))],
            UnresolvedTemplates = [.. review.UnresolvedTemplates.Select(u => $"{u.Template} ({u.Reason})")],
            Runtime = DeliveryRuntimeState.NoRuntimeMetadata,
            RuntimeText = review.Metadata is { State: "Available" }
                ? "Azure DevOps metadata read (definitions, environments, checks) — run history, stage results and deployment outcomes are not read."
                : "No runtime metadata: run history, stage results, published artifacts and deployment outcomes are not read.",
            Limitations = review.Limitations,
        };
    }

    /// <summary>Promotion vs rebuild from artifact lineage and the review's rebuild findings — never inferred without an artifact link.</summary>
    private static (ArtifactStrategy, string) Strategy(DeploymentReview d, PipelineReviewResult review)
    {
        var env = d.Environment;
        if (review.Findings.Any(f => f.Id == PipelineFindingIds.For(PipelineFindingCategory.ArtifactLineageGap, $"rebuild|{d.Id}")
                || f.Id == PipelineFindingIds.For(PipelineFindingCategory.ArtifactLineageGap, $"rebuild-env|{d.Id}")))
            return (ArtifactStrategy.RebuiltForEnvironment, $"Source shows {env} builds its own artifact rather than reusing the one tested earlier.");
        if (d.Artifacts.Count == 0) return (ArtifactStrategy.NoArtifact, "No artifact is consumed by this deployment in source.");
        if (d.Artifacts.Any(a => a.Source.StartsWith("pipeline resource", StringComparison.Ordinal) && a.State is ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported))
            return (ArtifactStrategy.PromotedFromEarlierStage, "Consumes an artifact produced by another pipeline run (pipeline resource) — promotion of the same build.");
        if (d.Artifacts.Any(a => a.Source.StartsWith("current run", StringComparison.Ordinal)))
            return (ArtifactStrategy.BuiltInSameRun, "Consumes an artifact built earlier in the same run.");
        return (ArtifactStrategy.Unknown, "The artifact's producer is not resolved from source.");
    }
}

// ── Queries ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed record EnvironmentMapping(string Label, EnvironmentMappingState State, string? Target, string Text);

/// <summary>Evidence ladder for one environment: each rung separate, never collapsed.</summary>
public sealed record EnvironmentLadder(string Environment, string Declared, string Configured, string DeliveryPath, string DeploymentObserved, string RuntimeObserved, string Validated);

public sealed record DeliveryPipelineChange(string Pipeline, string Kind, string Detail, string Classification);

public static class DeliveryEvidenceQueries
{
    /// <summary>Stable identity of one definition's content: a run of an earlier definition never describes the current YAML.</summary>
    public static string DefinitionFingerprint(PipelineDefinition d)
    {
        var text = string.Join("|", d.Stages.Select(s => $"{s.Name}:{string.Join(",", s.DependsOn)}:{s.Condition}"))
            + "#" + string.Join("|", d.Steps.Select(s => $"{s.Stage}/{s.Job}/{s.Kind}/{s.Name}/{s.Condition}/{s.ContinueOnError}"))
            + "#" + string.Join("|", d.Triggers.Select(t => $"{t.Type}:{string.Join(",", t.BranchesInclude)}:{string.Join(",", t.PathsInclude)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..16];
    }

    /// <summary>Whether runtime run evidence describes the current definition: only when it was recorded for the same definition fingerprint.</summary>
    public static (bool Applies, string Text) RunApplies(string? runDefinitionFingerprint, DeliveryPipeline current) =>
        runDefinitionFingerprint is null ? (false, "The run does not record which pipeline definition it ran; it is not linked to the current source.")
        : string.Equals(runDefinitionFingerprint, current.DefinitionFingerprint, StringComparison.Ordinal) ? (true, "The run belongs to this pipeline definition.")
        : (false, "The run belongs to an earlier pipeline definition; the current YAML has not run as far as this evidence shows.");

    public static IEnumerable<DeliveryDeployment> DeploymentsFor(DeliveryEvidence e, string environmentLabel) =>
        e.Deployments.Where(d => string.Equals(d.EnvironmentLabel, environmentLabel, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<DeliveryGate> QualityGates(DeliveryEvidence e, params ValidationCategory[] categories) =>
        [.. e.Deployments.SelectMany(d => d.Before.Concat(d.PostDeploy)).Where(g => categories.Length == 0 || categories.Contains(g.Category))];

    public static IReadOnlyList<DeliveryTestDefinition> TestsFor(DeliveryEvidence e, string projectOrPath) =>
        [.. e.Tests.Where(t => t.Targets.Any(x => x.Contains(projectOrPath, StringComparison.OrdinalIgnoreCase) || projectOrPath.Contains(System.IO.Path.GetFileNameWithoutExtension(x), StringComparison.OrdinalIgnoreCase) && System.IO.Path.GetFileNameWithoutExtension(x).Length > 3))];

    /// <summary>Pipeline environment label → Target Environment: Mapped only through an explicit mapping; an exact name is a strong candidate,
    /// a matching kind needs confirmation, several matches are ambiguous. A "qa" label never maps itself.</summary>
    public static EnvironmentMapping Map(string label, SourceEnvironmentKind kind, IReadOnlyList<(string Name, string Kind)> targets, IReadOnlyDictionary<string, string>? explicitMappings = null)
    {
        if (explicitMappings is not null && explicitMappings.TryGetValue(label, out var mapped))
            return new(label, EnvironmentMappingState.Mapped, mapped, $"Mapped explicitly to {mapped}.");
        var exact = targets.Where(t => string.Equals(t.Name, label, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return new(label, EnvironmentMappingState.StrongCandidate, exact[0].Name, $"Same name as Target Environment {exact[0].Name} — strong candidate; confirm the mapping.");
        var byKind = targets.Where(t => kind is not (SourceEnvironmentKind.Default or SourceEnvironmentKind.Custom) && KindMatches(kind, t.Kind)).ToList();
        return byKind.Count switch
        {
            1 => new(label, EnvironmentMappingState.NeedsConfirmation, byKind[0].Name, $"Pipeline label {label} and Target Environment {byKind[0].Name} are both {kind} — needs confirmation."),
            > 1 => new(label, EnvironmentMappingState.Ambiguous, null, $"{byKind.Count} Target Environments could be {label}; none is chosen."),
            _ => new(label, EnvironmentMappingState.Unmapped, null, $"No Target Environment corresponds to pipeline environment {label}."),
        };
    }

    private static bool KindMatches(SourceEnvironmentKind kind, string targetKind) => kind switch
    {
        SourceEnvironmentKind.Development => targetKind.Equals("Development", StringComparison.OrdinalIgnoreCase),
        SourceEnvironmentKind.Test or SourceEnvironmentKind.QA => targetKind.Equals("Test", StringComparison.OrdinalIgnoreCase) || targetKind.Equals("QA", StringComparison.OrdinalIgnoreCase),
        SourceEnvironmentKind.Staging => targetKind.Equals("Staging", StringComparison.OrdinalIgnoreCase),
        SourceEnvironmentKind.Production => targetKind.Equals("Production", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>Declared → Configured → Delivery path defined → Deployment observed → Runtime observed → Validated, each from its own evidence.</summary>
    public static EnvironmentLadder Ladder(string environment, bool declaredInSource, bool configuredTarget, DeliveryEvidence? delivery, bool runtimeObserved = false, bool validated = false)
    {
        var deployments = delivery is null ? [] : DeploymentsFor(delivery, environment).ToList();
        return new(environment,
            declaredInSource ? "Declared in source" : "Not declared in source",
            configuredTarget ? "Configured (Target Environment)" : "Not configured",
            deployments.Count > 0 ? $"Delivery path defined ({deployments.Count} deployment(s) in {string.Join(", ", deployments.Select(d => d.PipelineName).Distinct())})" : "No delivery path in source",
            delivery?.Runtime is DeliveryRuntimeState.DeploymentExecuted ? "Deployment observed" : "Not observed (no run evidence)",
            runtimeObserved ? "Runtime observed" : "Not assessed",
            validated ? "Validated" : "Not assessed");
    }

    /// <summary>Declared pipeline changes between two definitions of the same pipeline, each classified cautiously.</summary>
    public static IReadOnlyList<DeliveryPipelineChange> Changes(PipelineDefinition before, PipelineDefinition after)
    {
        var result = new List<DeliveryPipelineChange>();
        void Add(string kind, string detail, string classification) => result.Add(new(after.Name, kind, detail, classification));
        var envBefore = before.Environments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var envAfter = after.Environments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var e in envAfter.Except(envBefore)) Add("Deployment target added", e, "Delivery path changed — review");
        foreach (var e in envBefore.Except(envAfter)) Add("Deployment target removed", e, "Delivery path changed — review");
        static bool IsTest(PipelineStepKind k) => k is PipelineStepKind.UnitTest or PipelineStepKind.IntegrationTest or PipelineStepKind.FrontendTest or PipelineStepKind.E2ETest
            or PipelineStepKind.AccessibilityTest or PipelineStepKind.Test or PipelineStepKind.SmokeTest or PipelineStepKind.ApiTest or PipelineStepKind.ContractTest or PipelineStepKind.PerformanceTest
            or PipelineStepKind.HealthCheck or PipelineStepKind.SecurityScan or PipelineStepKind.DependencyScan;
        var tb = before.Steps.Where(s => IsTest(s.Kind)).Select(s => $"{s.Kind}:{s.Name}").ToHashSet(StringComparer.Ordinal);
        var ta = after.Steps.Where(s => IsTest(s.Kind)).Select(s => $"{s.Kind}:{s.Name}").ToHashSet(StringComparer.Ordinal);
        foreach (var s in tb.Except(ta)) Add("Validation step removed", s, "Potential gate removed — review deployment gating");
        foreach (var s in ta.Except(tb)) Add("Validation step added", s, "Validation added — gating depends on dependencies/conditions");
        var condBefore = before.Steps.Where(s => s.ContinueOnError).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var s in after.Steps.Where(s => s.ContinueOnError && !condBefore.Contains(s.Name))) Add("Failure now tolerated", s.Name, "Potential gate weakened — review");
        if (string.Join(",", before.Triggers.Select(t => t.Type + string.Join("", t.BranchesInclude) + string.Join("", t.PathsInclude)))
            != string.Join(",", after.Triggers.Select(t => t.Type + string.Join("", t.BranchesInclude) + string.Join("", t.PathsInclude))))
            Add("Trigger changed", string.Join("; ", after.Triggers.Select(t => t.Type)), "When the pipeline starts changed — review");
        if (before.ApprovalsDeclared && !after.ApprovalsDeclared) Add("Approval removed", after.Name, "Potential gate removed — review");
        if (!before.Artifacts.OrderBy(x => x).SequenceEqual(after.Artifacts.OrderBy(x => x))) Add("Artifacts changed", string.Join(", ", after.Artifacts), "Artifact lineage may change — review");
        return result;
    }

    /// <summary>Contract validation steps declared in pipelines (consumed by Contract Evidence): configured, never a validation result.</summary>
    public static IReadOnlyList<PipelineStep> ContractValidationSteps(PipelineEvidence evidence) =>
        [.. evidence.Pipelines.SelectMany(p => p.Steps).Where(s => s.Kind == PipelineStepKind.ContractTest)];
}

public sealed record DeliveryRequirementEvidence(string Control, string Implementation, string Execution);

public static class DeliveryTraceability
{
    /// <summary>Delivery evidence for a requirement: a gate configured / a deployment path defined — implementation in source; execution "Not assessed".</summary>
    public static IReadOnlyList<DeliveryRequirementEvidence> For(DeliveryEvidence e, string requirement)
    {
        var text = requirement.ToLowerInvariant();
        var result = new List<DeliveryRequirementEvidence>();
        if (text.Contains("test") && (text.Contains("deploy") || text.Contains("release") || text.Contains("before")))
        {
            var gated = e.Deployments.Where(d => d.Before.Any(g => g.State is GateState.Gates or GateState.Inherited)).ToList();
            result.Add(new("Tests gate deployment", gated.Count == 0 ? "No deployment is gated by tests in source" : $"{gated.Count} of {e.Deployments.Count} deployment(s) gated: {string.Join(", ", gated.Select(d => d.EnvironmentLabel).Distinct())}", "Not assessed"));
        }
        if (text.Contains("smoke") || text.Contains("health") || text.Contains("post-deploy") || text.Contains("after deploy"))
        {
            var post = e.Deployments.Where(d => d.PostDeploy.Count > 0).ToList();
            result.Add(new("Post-deploy validation", post.Count == 0 ? "No post-deploy validation in source" : $"Configured after: {string.Join(", ", post.Select(d => d.EnvironmentLabel).Distinct())}", "Not assessed"));
        }
        if (text.Contains("approv"))
            result.Add(new("Deployment approval", string.Join("; ", e.Deployments.Select(d => $"{d.EnvironmentLabel}: {d.Approval}").Distinct()), "Not assessed"));
        if (text.Contains("same artifact") || text.Contains("promot") || text.Contains("build once"))
            result.Add(new("Artifact promotion", string.Join("; ", e.Deployments.Select(d => $"{d.EnvironmentLabel}: {d.Artifacts}").Distinct()), "Not assessed"));
        return result;
    }
}

public static class DeliveryDocumentation
{
    /// <summary>Markdown delivery summary from normalized evidence — pipelines, deployments with gates and artifact strategy, post-deploy checks.
    /// No YAML, variable values or secrets.</summary>
    public static string Markdown(DeliveryEvidence e)
    {
        var lines = new List<string> { $"## Delivery — {e.Archive}", "", "Declared in pipeline source; nothing here says a stage ran, a test passed or a deployment happened.", "" };
        lines.Add($"{e.Pipelines.Count(p => !p.IsTemplate)} runnable pipeline(s), {e.Pipelines.Count(p => p.IsTemplate)} template(s), {e.Deployments.Count} deployment definition(s).");
        lines.Add("");
        foreach (var d in e.Deployments)
        {
            lines.Add($"### {d.EnvironmentLabel} — {d.PipelineName}");
            lines.Add($"- Artifact: {d.Artifacts} ({d.ArtifactBasis})");
            lines.Add($"- Before: {(d.Before.Count == 0 ? "no validation on the dependency path" : string.Join(", ", d.Before.Select(g => $"{g.Label} ({g.StateText})")))}");
            lines.Add($"- After: {(d.PostDeploy.Count == 0 ? "no post-deploy validation" : string.Join(", ", d.PostDeploy.Select(g => g.Label)))}");
            lines.Add($"- Approval: {d.Approval}");
            lines.Add("");
        }
        lines.Add(e.RuntimeText);
        return string.Join("\n", lines).TrimEnd() + "\n";
    }
}
