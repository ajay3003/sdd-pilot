using BirkNext.PipelineReview;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.PipelineReview;

/// <summary>
/// The delivery story: numbered, plain-language sentences generated deterministically from the analyzed graph (no AI). Every sentence carries the
/// evidence it rests on and how sure it is. Names from source are in `backticks`. Only sections with evidence appear.
/// </summary>
internal static class PipelineStoryWriter
{
    private static int RoleRank(string role) => role switch
    {
        "Pull-request validation" => 0, _ when role.StartsWith("Continuous integration", StringComparison.Ordinal) => 1, "Downstream (pipeline trigger)" => 2, "Scheduled" => 3, _ => 4,
    };

    public static List<FlowStoryStep> Write(PipelineReviewBuilder.Context ctx, PipelineReviewResult result)
    {
        var steps = new List<FlowStoryStep>();
        void Say(FlowSection section, string text, ArchitectureEvidenceState state, IEnumerable<PipelineEvidenceRef> evidence, string? detail = null) =>
            steps.Add(new FlowStoryStep(steps.Count + 1, section, text, state, evidence.ToList(), detail));

        var ordered = ctx.Pipelines.OrderBy(p => RoleRank(PipelineReviewBuilder.Role(p.Definition))).ThenBy(p => Depth(ctx, p)).ThenBy(p => ctx.Name(p.Id), StringComparer.Ordinal).ToList();
        foreach (var e in ordered)
        {
            var name = ctx.Name(e.Id);
            var file = new PipelineEvidenceRef { File = e.Definition.File, Pipeline = e.Id, Line = e.Definition.Triggers.FirstOrDefault()?.Line ?? 0 };
            var role = PipelineReviewBuilder.Role(e.Definition);
            var triggers = e.Definition.Triggers.Select(t => PipelineReviewBuilder.Trigger(ctx, e.Definition, t).Text).ToList();
            switch (role)
            {
                case "Pull-request validation":
                    Say(FlowSection.Trigger, $"{Join(triggers.Where(t => t.StartsWith("Pull", StringComparison.Ordinal)))} run {name} (`{e.Definition.File}`).", ArchitectureEvidenceState.Confirmed, [file]);
                    break;
                case "Downstream (pipeline trigger)":
                    foreach (var r in e.Definition.Resources.Where(r => r.Kind == "pipeline" && r.TriggerDeclared))
                    {
                        var (target, state, basis) = ctx.Resources[(e.Id, r.Alias)];
                        Say(FlowSection.Trigger, target is null
                                ? $"{name} starts when pipeline `{r.Source}` completes; that pipeline is not in this snapshot."
                                : $"{name} starts when {ctx.Name(target)} completes (pipeline resource `{r.Alias}`).",
                            state, [file with { Line = r.Line, Note = basis }], basis);
                    }
                    break;
                case "Scheduled":
                    Say(FlowSection.Trigger, $"{name} runs on a schedule ({Join(triggers)}); it does not start on code changes.", ArchitectureEvidenceState.Confirmed, [file]);
                    break;
                case "Manual":
                    Say(FlowSection.Trigger, $"{name} has no automatic trigger; it appears to require manual start.", ArchitectureEvidenceState.Confirmed, [file]);
                    break;
                default:
                    Say(FlowSection.Trigger, $"{Join(triggers)} start {name} (`{e.Definition.File}`).", ArchitectureEvidenceState.Confirmed, [file]);
                    break;
            }

            // Build and validation stages in execution order; parallel stages are named together.
            var deploymentStages = ctx.Deployments.Where(d => d.Pipeline == e.Id).Select(d => d.Stage).ToHashSet(StringComparer.Ordinal);
            var told = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stage in e.Stages.Where(s => !deploymentStages.Contains(s.Name)))
            {
                if (told.Contains(stage.Name)) continue;
                var parallel = e.Stages.Where(o => o.Name != stage.Name && !deploymentStages.Contains(o.Name) && !told.Contains(o.Name)
                    && e.StageDependencies(o.Name).OrderBy(x => x).SequenceEqual(e.StageDependencies(stage.Name).OrderBy(x => x))).ToList();
                foreach (var s in new[] { stage }.Concat(parallel))
                {
                    told.Add(s.Name);
                    var stageSteps = e.Steps.Where(x => x.Stage == s.Name).ToList();
                    var what = Describe(e, stageSteps);
                    var opaqueTemplate = e.Unresolved.FirstOrDefault(u => u.Stage == s.Name);
                    var artifacts = stageSteps.SelectMany(x => x.Step.ArtifactsPublished).Distinct().ToList();
                    var deps = e.StageDependencies(s.Name);
                    var text = $"Stage `{s.Display ?? s.Name}`{(deps.Count > 0 ? $" (after `{string.Join("`, `", deps)}`)" : "")} {(what.Length > 0 ? what + (opaqueTemplate is null ? "" : $", plus steps from template `{opaqueTemplate.Template}` (not analyzed)") : opaqueTemplate is not null ? $"runs steps from template `{opaqueTemplate.Template}`, which is not analyzed" : "runs no recognized build or test step")}"
                        + (artifacts.Count > 0 ? $"; it publishes artifact {string.Join(", ", artifacts.Select(a => $"`{a}`"))}" : "") + (s.Condition is { } c ? $" — only when `{c}`" : "") + ".";
                    Say(stageSteps.Any(x => PipelineReviewBuilder.Category(x) is { } cat && PipelineReviewBuilder.IsTest(cat)) ? FlowSection.Validation : FlowSection.Build, text,
                        s.Origin is null ? ArchitectureEvidenceState.Confirmed : ArchitectureEvidenceState.StronglySupported,
                        [new PipelineEvidenceRef { File = s.File, Line = s.Line, Pipeline = e.Id, Stage = s.Name, Condition = s.Condition, TemplateOrigin = s.Origin }]);
                }
                if (parallel.Count > 0)
                    Say(FlowSection.Build, $"Stages {string.Join(" and ", new[] { stage }.Concat(parallel).Select(x => $"`{x.Display ?? x.Name}`"))} do not depend on each other and can run in parallel.",
                        ArchitectureEvidenceState.Confirmed, [new PipelineEvidenceRef { File = stage.File, Line = stage.Line, Pipeline = e.Id, Stage = stage.Name }]);
            }
            if (e.Stages.Count == 0 && e.Steps.Count > 0 && ctx.Deployments.All(d => d.Pipeline != e.Id))
                Say(FlowSection.Build, $"It {Describe(e, e.Steps)}.", ArchitectureEvidenceState.Confirmed, [file]);

            foreach (var d in ctx.Deployments.Where(d => d.Pipeline == e.Id))
            {
                var section = d.EnvironmentKind == SourceEnvironmentKind.Production ? FlowSection.Production : FlowSection.Deployment;
                var tests = d.Before.Where(g => PipelineReviewBuilder.IsTest(g.Category)).ToList();
                var gating = tests.Where(g => g.State is GateState.Gates or GateState.Inherited).Select(g => PipelineReviewText.Label(g.Category)).Distinct().ToList();
                var weak = tests.Where(g => g.State is GateState.Conditional or GateState.SoftGate).Select(g => $"{PipelineReviewText.Label(g.Category)} ({PipelineReviewText.Label(g.State).ToLowerInvariant()})").Distinct().ToList();
                var where = d.Stage is null ? $"Job `{d.Job}`" : $"Stage `{e.Stage(d.Stage)?.Display ?? d.Stage}`";
                var notGating = ctx.Findings.Where(f => f.Category == PipelineFindingCategory.TestGatingGap && f.Evidence.FirstOrDefault() == d.Evidence && f.Title.Contains(" do not gate", StringComparison.Ordinal))
                    .Select(f => f.Title[..f.Title.IndexOf(" do not gate", StringComparison.Ordinal)]).ToList();
                var infraFirst = ctx.Deployments.Where(i => i.Id != d.Id && PipelineReviewBuilder.InfrastructureOnly(i) && i.Pipeline == d.Pipeline && PipelineReviewBuilder.SameEnvironment(i, d)
                    && e.Ancestors(ctx.DeploymentUnits[d.Id].Job.Key).ContainsKey(ctx.DeploymentUnits[i.Id].Job.Key)).ToList();
                var after = e.StageDependencies(d.Stage) is { Count: > 0 } deps ? $" after `{string.Join("`, `", deps)}`" : "";
                var what = string.Join(" and ", d.DeploysWhat.Select(w => w.ToLowerInvariant()));
                var envText = d.EnvironmentKind is SourceEnvironmentKind.Custom or SourceEnvironmentKind.Default || PipelineReviewBuilder.Display(d) == d.Environment ? $"`{d.Environment}`" : $"{PipelineReviewBuilder.Display(d)} (`{d.Environment}`)";
                var text = $"{where} deploys {what} to {envText}{after}. "
                    + (d.Before.Any(g => g.State == GateState.Unknown) ? "Part of its path comes from a template that is not analyzed, so its gating is not assessable."
                        : gating.Count > 0 ? $"Gated by: {string.Join(", ", gating)}{(weak.Count > 0 ? $"; also {string.Join(", ", weak)}" : "")}."
                        : weak.Count > 0 ? $"Only weakly gated: {string.Join(", ", weak)}."
                        : ctx.Findings.Any(f => f.Category == PipelineFindingCategory.TestGatingGap && f.Evidence.Contains(d.Evidence) && f.Title.StartsWith("Tests exist", StringComparison.Ordinal))
                            ? "Tests run in this pipeline but do not appear on its dependency path." : "No test was detected on its dependency path.")
                    + (notGating.Count > 0 ? $" {string.Join(" and ", notGating)} run in this pipeline but are not on its path." : "")
                    + (infraFirst.Count > 0 ? $" Infrastructure for {PipelineReviewBuilder.Display(d)} (`{infraFirst[0].Stage ?? infraFirst[0].Job}`) is deployed before it." : "")
                    + (PipelineConditions.Restricts(d.Condition) ? $" Runs only when `{d.Condition}`." : "");
                Say(section, text, d.EnvironmentState is ArchitectureEvidenceState.Unresolved ? ArchitectureEvidenceState.Inferred : ArchitectureEvidenceState.Confirmed, [d.Evidence], d.HowReached);
                foreach (var a in d.Artifacts.Where(a => a.Artifact is not ("(unknown)" or "(infrastructure code)")))
                    Say(FlowSection.Artifact, a.State == ArchitectureEvidenceState.Unresolved
                            ? $"The artifact deployed to `{d.Environment}` could not be traced to a producer: {a.Basis}."
                            : a.Artifact == "(build output)" ? $"It deploys what this run builds in stage `{a.ProducerStage}` (no published artifact)."
                            : a.Artifact == "(container image)" ? $"It deploys the container image pushed in stage `{a.ProducerStage}` of this run; the deployed tag is not statically derivable."
                            : $"It deploys {(a.Artifact == "(all artifacts)" ? "the artifacts" : $"artifact `{a.Artifact}`")} from the {a.Source}{(a.ProducerStage is { } ps ? $" (stage `{ps}`)" : "")}.",
                        a.State, [d.Evidence with { Note = a.Basis }], a.Basis);
                var post = d.After.Where(g => PipelineReviewBuilder.IsPostDeployCheck(g.Category)).Select(g => PipelineReviewText.Label(g.Category)).Distinct().ToList();
                if (!d.Before.Any(g => g.State == GateState.Unknown) && !((PipelineReviewBuilder.InfrastructureOnly(d) || d.DeploysWhat.SequenceEqual(new[] { "Database" })) && post.Count == 0))
                    Say(FlowSection.PostDeployment, post.Count > 0 ? $"After deploying to {PipelineReviewBuilder.Display(d)}: {string.Join(", ", post)}." : $"No post-deployment validation was detected after the {PipelineReviewBuilder.Display(d)} deployment.",
                        ArchitectureEvidenceState.Confirmed, [d.Evidence]);
            }
        }

        // Promotion across environments and pipelines.
        var qa = ctx.Deployments.FirstOrDefault(d => d.EnvironmentKind is SourceEnvironmentKind.QA or SourceEnvironmentKind.Staging);
        foreach (var prod in ctx.Deployments.Where(d => d.EnvironmentKind == SourceEnvironmentKind.Production))
        {
            if (qa is null) continue;
            if (prod.Pipeline != qa.Pipeline)
            {
                Say(FlowSection.Promotion, $"Production is handled by a separate pipeline, {prod.PipelineName}.", ArchitectureEvidenceState.Confirmed, [prod.Evidence]);
                var linked = result.Progression.FirstOrDefault(s => s.To == "PROD");
                var resource = result.Dependencies.FirstOrDefault(x => x.FromPipeline == qa.Pipeline && x.ToPipeline == prod.Pipeline && x.Kind == PipelineEdgeKind.LikelyFollows);
                Say(FlowSection.Promotion, linked is not null ? $"It follows {linked.From}: {linked.Basis}."
                        : resource is not null ? $"{prod.PipelineName} declares {qa.PipelineName} as a pipeline resource, but without a trigger and without downloading its artifacts: which run it follows is chosen when it is started."
                        : "No explicit pipeline or artifact relationship between QA and Production was detected.",
                    linked?.State ?? (resource is not null ? ArchitectureEvidenceState.Inferred : ArchitectureEvidenceState.Unresolved), [prod.Evidence, qa.Evidence]);
            }
            var lineage = ctx.Findings.FirstOrDefault(f => f.Category == PipelineFindingCategory.ArtifactLineageGap && f.Evidence.Contains(prod.Evidence));
            if (lineage is not null) Say(FlowSection.Promotion, $"{lineage.Title}: {lineage.WhyItMatters}", lineage.EvidenceState, lineage.Evidence);
            else if (prod.Artifacts.Any(a => a.State is ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported) && prod.Pipeline == qa.Pipeline)
                Say(FlowSection.Promotion, "Production deploys the artifact of the same run that was deployed to QA (build once, promote).", ArchitectureEvidenceState.StronglySupported, [prod.Evidence, qa.Evidence]);
        }
        return steps;
    }

    private static int Depth(PipelineReviewBuilder.Context ctx, EffectivePipeline e, int guard = 0) =>
        guard > 5 ? 0 : e.Definition.Resources.Where(r => r.TriggerDeclared).Select(r => ctx.Resources.TryGetValue((e.Id, r.Alias), out var m) && m.Target is { } t && ctx.Pipeline(t) is { } u ? 1 + Depth(ctx, u, guard + 1) : 1).DefaultIfEmpty(0).Max();

    private static string Join(IEnumerable<string> parts)
    {
        var list = parts.ToList();
        return list.Count == 0 ? "Its triggers" : string.Join("; ", list);
    }

    /// <summary>"builds (dotnet build) and runs unit tests (dotnet test)" — from step kinds, in order of first appearance.</summary>
    private static string Describe(EffectivePipeline e, IEnumerable<EStep> steps)
    {
        var parts = new List<string>();
        foreach (var group in steps.Where(s => s.Step.Kind != PipelineStepKind.Other).GroupBy(Phrase).Where(g => g.Key.Length > 0))
        {
            var condition = group.Select(s => s.Step.Condition ?? e.Job(s.JobKey)?.Condition).FirstOrDefault(c => c is not null);
            var soft = group.Any(s => s.Step.ContinueOnError || e.Job(s.JobKey)?.ContinueOnError == true);
            parts.Add($"{group.Key} ({string.Join(", ", group.Select(s => s.Step.Tool).Distinct().Take(3).Select(t => $"`{t}`"))}{(group.Any(s => s.Origin is not null) ? $", from `{group.First(s => s.Origin is not null).Origin}`" : "")})"
                + (PipelineConditions.Restricts(condition) ? $" only when `{condition}`" : condition is not null && PipelineConditions.Tolerant(condition) ? " (also after earlier failures)" : "")
                + (soft ? " (continueOnError)" : ""));
        }
        return parts.Count switch { 0 => "", 1 => parts[0], _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1] };
    }

    private static string Phrase(EStep s) => PipelineReviewBuilder.Category(s) is { } c && PipelineReviewBuilder.IsTest(c) ? $"runs {PipelineReviewText.Label(c).ToLowerInvariant()}"
        : s.Step.Kind == PipelineStepKind.Publish && s.Step.Tool.StartsWith("dotnet", StringComparison.Ordinal) ? "packages the application" : Phrase(s.Step.Kind);

    private static string Phrase(PipelineStepKind kind) => kind switch
    {
        PipelineStepKind.Build => "builds",
        PipelineStepKind.ContainerBuild => "builds a container image",
        PipelineStepKind.ContainerPush => "pushes the image",
        PipelineStepKind.Publish => "",
        PipelineStepKind.InfrastructurePlan => "plans infrastructure",
        PipelineStepKind.SecurityScan => "runs a security scan",
        PipelineStepKind.DependencyScan => "scans dependencies",
        PipelineStepKind.StaticAnalysis => "runs static analysis",
        PipelineStepKind.Sbom => "generates an SBOM",
        _ when PipelineReviewBuilder.Category(kind) is { } c && PipelineReviewBuilder.IsTest(c) => $"runs {PipelineReviewText.Label(c).ToLowerInvariant()}",
        _ => "",
    };
}
