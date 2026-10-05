using Path = System.IO.Path;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Integrations;
using BirkNext.PipelineReview;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.PipelineReview;

/// <summary>
/// Builds the Pipeline Review of ONE Source Analysis snapshot from its normalized evidence: CI/CD (pipelines, triggers, stages, jobs, steps,
/// artifacts, resources, template uses — analyzer v2), Architecture (components, shared libraries), Contracts and Infrastructure. Deterministic:
/// the same snapshot and rules version always give the same result. Nothing here parses YAML, reads source files or calls a CI system.
/// </summary>
public static class PipelineReviewBuilder
{
    public const string NeedsReanalysis = "This source snapshot was analyzed before CI/CD evidence v2 (job dependencies, conditions, artifacts, resources, template uses). Analyze the archive again in Source Analysis.";
    private static readonly PipelineStepKind[] DeployKinds = [PipelineStepKind.ApplicationDeploy, PipelineStepKind.InfrastructureDeploy, PipelineStepKind.DatabaseMigration];

    public static ValidationCategory? Category(PipelineStepKind kind) => kind switch
    {
        PipelineStepKind.UnitTest => ValidationCategory.Unit,
        PipelineStepKind.IntegrationTest => ValidationCategory.Integration,
        PipelineStepKind.FrontendTest => ValidationCategory.Frontend,
        PipelineStepKind.E2ETest => ValidationCategory.E2E,
        PipelineStepKind.AccessibilityTest => ValidationCategory.Accessibility,
        PipelineStepKind.Test => ValidationCategory.Test,
        PipelineStepKind.SmokeTest => ValidationCategory.Smoke,
        PipelineStepKind.ApiTest => ValidationCategory.Api,
        PipelineStepKind.ContractTest => ValidationCategory.Contract,
        PipelineStepKind.PerformanceTest => ValidationCategory.Performance,
        PipelineStepKind.HealthCheck => ValidationCategory.HealthCheck,
        PipelineStepKind.SecurityScan => ValidationCategory.Security,
        PipelineStepKind.DependencyScan => ValidationCategory.DependencyScan,
        PipelineStepKind.StaticAnalysis => ValidationCategory.StaticAnalysis,
        PipelineStepKind.InfrastructurePlan => ValidationCategory.InfrastructurePlan,
        PipelineStepKind.Build or PipelineStepKind.ContainerBuild => ValidationCategory.Build,
        _ => null,
    };

    /// <summary>A step's category after template parameters are substituted: an unclassified test is re-read from its (now literal) targets and name.</summary>
    internal static ValidationCategory? Category(EStep s) =>
        s.Step.Kind == PipelineStepKind.Test ? Category(BirkNext.Api.Services.SourceAnalysis.Evidence.PipelineAnalyzer.TestKind($"{string.Join(" ", s.Step.Targets)} {s.Step.Name}")) : Category(s.Step.Kind);

    public static bool IsTest(ValidationCategory c) => c is ValidationCategory.Unit or ValidationCategory.Integration or ValidationCategory.Api or ValidationCategory.E2E
        or ValidationCategory.Frontend or ValidationCategory.Accessibility or ValidationCategory.Performance or ValidationCategory.Contract or ValidationCategory.Smoke or ValidationCategory.Test;

    public static bool IsPostDeployCheck(ValidationCategory c) => IsTest(c) || c == ValidationCategory.HealthCheck;

    /// <summary>A readable step label: the display name, or the tool when the step has none ("script", "bash" …).</summary>
    internal static string Friendly(PipelineStep s) => s.Name is "script" or "bash" or "pwsh" or "powershell" || s.Name.Contains('@') ? s.Tool : s.Name;

    /// <summary>The environment as findings and the story name it: the environment type when known (QA, PROD), else the source name.</summary>
    public static string Display(DeploymentReview d) => d.EnvironmentKind is SourceEnvironmentKind.Custom or SourceEnvironmentKind.Default ? d.Environment : EnvironmentLabel(d.EnvironmentKind, d.Environment);

    internal static bool InfrastructureOnly(DeploymentReview d) => d.DeploysWhat.Count == 1 && d.DeploysWhat[0] == "Infrastructure";

    public static string EnvironmentLabel(SourceEnvironmentKind kind, string fallback) => kind switch
    {
        SourceEnvironmentKind.Development => "DEV", SourceEnvironmentKind.Test => "TEST", SourceEnvironmentKind.QA => "QA", SourceEnvironmentKind.Staging => "STAGING",
        SourceEnvironmentKind.Production => "PROD", SourceEnvironmentKind.Local => "LOCAL", _ => fallback,
    };

    private static int Rank(SourceEnvironmentKind kind) => kind switch
    {
        SourceEnvironmentKind.Local => 0, SourceEnvironmentKind.Development => 1, SourceEnvironmentKind.Test => 2, SourceEnvironmentKind.QA => 3, SourceEnvironmentKind.Staging => 4,
        SourceEnvironmentKind.Production => 5, _ => 6,
    };

    internal sealed class Context
    {
        public required IqrSourceSnapshot Snapshot { get; init; }
        public required List<EffectivePipeline> Pipelines { get; init; }
        public required Dictionary<string, PipelineDefinition> Definitions { get; init; }
        public required PipelineMetadataSummary Metadata { get; init; }
        public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string Pipeline, string Alias), (string? Target, ArchitectureEvidenceState State, string Basis)> Resources { get; } = [];
        public string Root { get; set; } = "";
        public List<DeploymentReview> Deployments { get; } = [];
        public Dictionary<string, (EffectivePipeline Pipeline, EJob Job, double Point)> DeploymentUnits { get; } = new(StringComparer.Ordinal);
        public List<PipelineReviewFinding> Findings { get; } = [];
        public EffectivePipeline? Pipeline(string id) => Pipelines.FirstOrDefault(p => p.Id == id);
        public string Name(string id) => Names.TryGetValue(id, out var n) ? n : id;
    }

    public static PipelineReviewResult Build(IqrSourceSnapshot snapshot, PipelineMetadataSummary? metadata = null)
    {
        var result = new PipelineReviewResult
        {
            SourceSnapshotId = snapshot.Id, SourceFingerprint = snapshot.EvidenceDomains?.SourceFingerprint ?? snapshot.Archive.Sha256, ArchiveName = snapshot.Archive.FileName,
            SourceAnalyzedAt = snapshot.AnalyzedAt, Metadata = metadata ?? new PipelineMetadataSummary { Detail = "Azure DevOps metadata was not requested." },
        };
        var cicd = snapshot.EvidenceDomains?.CiCd;
        if (cicd is null) return result with { State = "NeedsReanalysis", StateReason = NeedsReanalysis };
        result = result with { CiCdAnalyzerVersion = cicd.AnalyzerVersion };
        if (cicd.Pipelines.Count == 0) return result with { State = "NoPipelines", StateReason = cicd.StatusReason ?? "No pipeline definition was found in this snapshot." };
        if (cicd.AnalyzerVersion < PipelineReviewText.RequiredCiCdVersion) return result with { State = "NeedsReanalysis", StateReason = NeedsReanalysis };

        var definitions = cicd.Pipelines.GroupBy(p => p.File, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var runnable = cicd.Pipelines.Where(p => !p.IsTemplate).OrderBy(p => p.File, StringComparer.Ordinal).ToList();
        var ctx = new Context
        {
            Snapshot = snapshot, Definitions = definitions, Metadata = result.Metadata,
            Pipelines = runnable.Select(p => new EffectivePipeline(p, definitions)).ToList(),
        };
        foreach (var p in runnable)
            ctx.Names[p.Id] = result.Metadata.Definitions.FirstOrDefault(d => d.YamlPath is { } y && p.File.EndsWith(y.TrimStart('/'), StringComparison.OrdinalIgnoreCase))?.Name ?? p.Name;
        MapResources(ctx);
        ctx.Root = PipelinePathFilters.RepositoryRoot(cicd.Pipelines.Select(p => p.File).Concat(snapshot.Architecture?.Components.Select(c => c.SourceProject) ?? []),
            cicd.Pipelines.SelectMany(p => p.Triggers).SelectMany(t => t.PathsInclude.Concat(t.PathsExclude)));

        var pipelines = runnable.Select(p => Reviewed(ctx, p)).ToList()
            .Concat(cicd.Pipelines.Where(p => p.IsTemplate).Select(p => new ReviewedPipeline { Id = p.Id, Name = p.Name, File = p.File, Platform = p.Platform, IsTemplate = true, Role = "Template" })).ToList();
        foreach (var e in ctx.Pipelines) Deployments(ctx, e);
        var tests = Tests(ctx);
        var (progression, environments) = Progression(ctx);
        var dependencies = Dependencies(ctx);
        var coverage = PathCoverage(ctx);
        var unresolved = ctx.Pipelines.SelectMany(p => p.Unresolved).ToList();

        Findings(ctx, tests, progression, coverage, unresolved, cicd);
        var findings = ctx.Findings.OrderBy(f => f.Severity).ThenBy(f => f.Category).ThenBy(f => f.Title, StringComparer.Ordinal).ToList();
        var matrix = Matrix(ctx, tests, environments);
        var path = DeliveryPath(ctx, pipelines, progression, environments);
        var (nodes, edges) = Graph(ctx, dependencies);
        result = result with
        {
            Pipelines = pipelines, Deployments = ctx.Deployments, Tests = tests, TestMatrix = matrix, Environments = environments, Progression = progression,
            Dependencies = dependencies, PathCoverage = coverage, Findings = findings, UnresolvedTemplates = unresolved, DeliveryPath = path, Nodes = nodes, Edges = edges,
            Limitations = Limitations(ctx, cicd, unresolved),
        };
        return result with { Story = PipelineStoryWriter.Write(ctx, result) };
    }

    // ── Pipelines, roles, resources ───────────────────────────────────────────────────────────────────────────────────

    internal static string Role(PipelineDefinition p)
    {
        if (p.IsTemplate) return "Template";
        var push = p.Triggers.Any(t => t.Type == "push");
        var pr = p.Triggers.Any(t => t.Type == "pull-request");
        if (pr && !push) return "Pull-request validation";
        if (push) return pr ? "Continuous integration (also pull requests)" : "Continuous integration";
        if (p.Triggers.Any(t => t.Type == "pipeline-resource")) return "Downstream (pipeline trigger)";
        if (p.Triggers.Any(t => t.Type == "schedule")) return "Scheduled";
        return "Manual";
    }

    private static ReviewedPipeline Reviewed(Context ctx, PipelineDefinition p)
    {
        var e = ctx.Pipeline(p.Id)!;
        return new ReviewedPipeline
        {
            Id = p.Id, Name = ctx.Name(p.Id), File = p.File, Platform = p.Platform, Role = Role(p), Triggers = p.Triggers.Select(t => Trigger(ctx, p, t)).ToList(),
            ManualOnly = Role(p) == "Manual", Stages = e.Stages.Count, Jobs = e.Jobs.Count,
            Environments = e.Jobs.Select(j => j.Environment).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
            Artifacts = e.Steps.SelectMany(s => s.Step.ArtifactsPublished).Distinct(StringComparer.Ordinal).ToList(),
            DefinitionName = ctx.Names[p.Id] == p.Name ? null : ctx.Names[p.Id],
        };
    }

    internal static PipelineTriggerSummary Trigger(Context ctx, PipelineDefinition p, PipelineTrigger t)
    {
        string Branches() => t.BranchesInclude.Count == 0 ? "any branch" : string.Join(", ", t.BranchesInclude.Select(b => $"`{b}`"));
        string Paths() => t.PathsInclude.Count == 0 && t.PathsExclude.Count == 0 ? "" :
            $" (only changes {(t.PathsInclude.Count > 0 ? $"under {string.Join(", ", t.PathsInclude.Select(x => $"`{x}`"))}" : "anywhere")}{(t.PathsExclude.Count > 0 ? $", except {string.Join(", ", t.PathsExclude.Select(x => $"`{x}`"))}" : "")})";
        var text = t.Type switch
        {
            "push" when t.BranchesInclude.FirstOrDefault()?.StartsWith("(all branches", StringComparison.Ordinal) == true => "Pushes to any branch (no trigger key: Azure Pipelines' default CI trigger)",
            "push" => $"Pushes to {Branches()}{(t.BranchesExclude.Count > 0 ? $" (not {string.Join(", ", t.BranchesExclude.Select(b => $"`{b}`"))})" : "")}{Paths()}",
            "pull-request" => $"Pull requests to {Branches()}{Paths()}",
            "schedule" => $"Schedule `{t.Schedule}`{(t.BranchesInclude.Count > 0 ? $" on {Branches()}" : "")}",
            "pipeline-resource" => $"When pipeline `{t.BranchesInclude.FirstOrDefault()}` completes",
            "none" => t.Disables == "pull-request" ? "No pull-request trigger (`pr: none`)" : "No push trigger (`trigger: none`)",
            _ => t.Type,
        };
        return new PipelineTriggerSummary(t.Type, text, t.BranchesInclude, t.PathsInclude, t.PathsExclude, t.Schedule);
    }

    /// <summary>A pipeline resource names a CI pipeline by its CI-system name. It maps to a file in this snapshot through Azure DevOps metadata
    /// (Confirmed), an exact file-name match (Strongly supported) or a normalized name match (Inferred) — never by similarity alone otherwise.</summary>
    private static void MapResources(Context ctx)
    {
        static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        foreach (var e in ctx.Pipelines)
            foreach (var r in e.Definition.Resources.Where(r => r.Kind == "pipeline"))
            {
                var definition = ctx.Metadata.Definitions.FirstOrDefault(d => d.Name.Equals(r.Source, StringComparison.OrdinalIgnoreCase) && d.YamlPath is not null);
                var viaAdo = definition is null ? null : ctx.Pipelines.FirstOrDefault(p => p.Definition.File.EndsWith(definition.YamlPath!.TrimStart('/'), StringComparison.OrdinalIgnoreCase));
                var exact = ctx.Pipelines.Where(p => p != e && p.Definition.Name.Equals(r.Source, StringComparison.OrdinalIgnoreCase)).ToList();
                var normalized = ctx.Pipelines.Where(p => p != e && Norm(p.Definition.Name) == Norm(r.Source)).ToList();
                ctx.Resources[(e.Id, r.Alias)] = viaAdo is not null ? (viaAdo.Id, ArchitectureEvidenceState.Confirmed, $"Azure DevOps pipeline '{r.Source}' uses `{viaAdo.Definition.File}`")
                    : exact.Count == 1 ? (exact[0].Id, ArchitectureEvidenceState.StronglySupported, $"pipeline resource source '{r.Source}' matches file `{exact[0].Definition.File}` by name (Azure DevOps names not read)")
                    : normalized.Count == 1 ? (normalized[0].Id, ArchitectureEvidenceState.Inferred, $"pipeline resource source '{r.Source}' resembles file `{normalized[0].Definition.File}` (name match only)")
                    : (null, ArchitectureEvidenceState.Unresolved, $"pipeline resource source '{r.Source}' is not a pipeline file in this snapshot (another repository, or Azure DevOps names differ)");
            }
    }

    // ── Deployments, gates, artifacts ────────────────────────────────────────────────────────────────────────────────

    private static PipelineEvidenceRef Ref(EffectivePipeline e, EStep s) => new()
    {
        File = s.File, Line = s.Step.Line, Pipeline = e.Id, Stage = s.Stage, Job = s.JobKey[(s.JobKey.IndexOf('|') + 1)..], Step = s.Step.Name, Condition = s.Step.Condition,
        TemplateOrigin = s.Origin,
    };

    private static PipelineEvidenceRef Ref(EffectivePipeline e, EJob j) => new()
    {
        File = j.File, Line = j.Line, Pipeline = e.Id, Stage = j.Stage, Job = j.Name, Condition = j.Condition, TemplateOrigin = j.Origin,
    };

    private static bool StructureKnown(EffectivePipeline e) => e.Definition.Platform == PipelinePlatform.AzurePipelines;

    /// <summary>Validations before (with gate state), after, and not on the path of a point (job + position in it).</summary>
    internal static (List<ValidationGate> Before, List<ValidationGate> After, List<ValidationGate> NotGating) Gates(EffectivePipeline e, string jobKey, double point)
    {
        var before = new List<ValidationGate>();
        var after = new List<ValidationGate>();
        var not = new List<ValidationGate>();
        var job = e.Job(jobKey);
        var ancestors = e.Ancestors(jobKey);
        var deployTolerant = PipelineConditions.Tolerant(job?.Condition);
        foreach (var s in e.Steps)
        {
            if (Category(s) is not { } category) continue;
            var sJob = e.Job(s.JobKey);
            ValidationGate Gate(GateState state) => new(category, state, Friendly(s.Step), e.Id, s.Stage, sJob?.Name, s.Step.Condition ?? sJob?.Condition, Ref(e, s));
            GateState BeforeState(bool blocking) => PipelineConditions.Restricts(s.Step.Condition) ? GateState.Conditional
                : s.Step.ContinueOnError || sJob?.ContinueOnError == true || !blocking ? GateState.SoftGate : GateState.Gates;
            if (!StructureKnown(e)) { (s.JobKey == jobKey && s.Order < point ? before : not).Add(Gate(s.JobKey == jobKey && s.Order < point ? BeforeState(true) : GateState.Unknown)); continue; }
            if (s.JobKey == jobKey)
            {
                if (s.Order < point) before.Add(Gate(BeforeState(true)));
                else if (s.Order > point) after.Add(Gate(GateState.After));
            }
            else if (ancestors.TryGetValue(s.JobKey, out var blocking)) before.Add(Gate(BeforeState(blocking && !deployTolerant)));
            else if (e.Ancestors(s.JobKey).ContainsKey(jobKey)) after.Add(Gate(GateState.After));
            else not.Add(Gate(GateState.NotGating));
        }
        return (before, after, not);
    }

    private static bool IsDeployJob(EffectivePipeline e, EJob j) => j.Deployment || e.StepsOf(j.Key).Any(s => DeployKinds.Contains(s.Step.Kind));

    private static void Deployments(Context ctx, EffectivePipeline e)
    {
        foreach (var job in e.Jobs.Where(j => IsDeployJob(e, j)).OrderBy(j => e.Stage(j.Stage)?.Order ?? 0).ThenBy(j => j.Order))
        {
            var steps = e.StepsOf(job.Key).ToList();
            var deploySteps = steps.Where(s => DeployKinds.Contains(s.Step.Kind)).ToList();
            var point = deploySteps.Count > 0 ? deploySteps.Min(s => s.Order)
                : steps.Where(s => s.Step.StrategyPhase is { } p && (p.StartsWith("deploy", StringComparison.Ordinal) || p.StartsWith("routeTraffic", StringComparison.Ordinal))).Select(s => s.Order).DefaultIfEmpty(-1).Min();
            var (envName, kind, basis, state) = Environment(e, job, deploySteps);
            var id = $"{e.Id}#{job.Key}";
            var (before, after, _) = Gates(e, job.Key, point);
            var unresolved = new List<string>();
            if (e.HiddenOnPath(job.Key) is { } hidden)
            {
                unresolved.Add($"Part of the path is defined in template `{hidden.Template}`, which is not analyzed: validations there are not visible.");
                before.Add(new ValidationGate(ValidationCategory.Test, GateState.Unknown, $"behind template {hidden.Template}", e.Id, hidden.Stage ?? job.Stage, hidden.Job ?? job.Name, null, hidden.Evidence));
            }
            if (!StructureKnown(e)) unresolved.Add($"Job dependencies are analyzed for Azure Pipelines only; {SourceDomainText.Label(e.Definition.Platform)} ordering is not assessed.");
            if (envName.Contains("${{", StringComparison.Ordinal) || envName.Contains("$(", StringComparison.Ordinal)) unresolved.Add($"The environment name `{envName}` is an expression; it is not resolved.");
            var artifacts = Artifacts(ctx, e, job, point);
            before.AddRange(Inherited(ctx, artifacts));
            foreach (var a in artifacts.Where(a => a.State == ArchitectureEvidenceState.Unresolved)) unresolved.Add($"Artifact `{a.Artifact}`: {a.Basis}");
            var deployWhat = deploySteps.Select(s => s.Step.Kind switch { PipelineStepKind.InfrastructureDeploy => "Infrastructure", PipelineStepKind.DatabaseMigration => "Database", _ => "Application" })
                .DefaultIfEmpty("Application").Distinct().ToList();
            var review = new DeploymentReview
            {
                Id = id, Pipeline = e.Id, PipelineName = ctx.Name(e.Id), Stage = job.Stage, Job = job.Name, Environment = envName, EnvironmentKind = kind, EnvironmentBasis = basis, EnvironmentState = state,
                DeploysWhat = deployWhat, HowReached = HowReached(ctx, e, job), Condition = job.Condition ?? e.Stage(job.Stage)?.Condition,
                Before = before, After = after, Artifacts = artifacts,
                Approval = Approval(ctx, e, job, envName, point), Rollback = Rollback(e, job), Unresolved = unresolved, Evidence = Ref(e, job),
            };
            ctx.Deployments.Add(review);
            ctx.DeploymentUnits[id] = (e, job, point);
        }
    }

    private static string? JobKeyOf(UnresolvedTemplate u) => u.Level == "steps" ? EffectivePipeline.JobKey(u.Stage, u.Job ?? "(job)") : null;

    private static (string Name, SourceEnvironmentKind Kind, string Basis, ArchitectureEvidenceState State) Environment(EffectivePipeline e, EJob job, List<EStep> deploySteps)
    {
        if (job.Environment is { Length: > 0 } env)
        {
            var named = SourceEnvironments.FromName(env.Split('.')[0]);
            return (env, named?.Kind ?? SourceEnvironmentKind.Custom, named is null ? "environment key (its name states no known environment)" : "environment key; environment type from its name",
                named is null ? ArchitectureEvidenceState.Unresolved : ArchitectureEvidenceState.StronglySupported);
        }
        var stage = e.Stage(job.Stage);
        foreach (var candidate in new[] { stage?.Display, stage?.Name, job.Display, job.Name, deploySteps.FirstOrDefault()?.Step.Environment }
                     .Concat(deploySteps.SelectMany(s => s.Step.Targets).Select(t => Path.GetFileNameWithoutExtension(t))).Append(e.Definition.Name))
            if (SourceEnvironments.FromName(candidate) is { } fromName)
                return (EnvironmentLabel(fromName.Kind, fromName.Raw), fromName.Kind, $"named by `{candidate}` (no environment key)", ArchitectureEvidenceState.Inferred);
        return ("(unlabelled)", SourceEnvironmentKind.Custom, "no environment key or environment name in stage/job/pipeline names", ArchitectureEvidenceState.Unresolved);
    }

    private static string HowReached(Context ctx, EffectivePipeline e, EJob job)
    {
        var parts = new List<string>();
        var stageDeps = e.StageDependencies(job.Stage);
        if (stageDeps.Count > 0) parts.Add($"after stage {string.Join(", ", stageDeps.Select(s => $"`{s}`"))}{(e.Stage(job.Stage)?.DependsOnDeclared == false ? " (previous stage, implicit order)" : "")}");
        if (job.DependsOn.Count > 0) parts.Add($"after job {string.Join(", ", job.DependsOn.Select(s => $"`{s}`"))}");
        var triggers = e.Definition.Triggers.Select(t => Trigger(ctx, e.Definition, t).Text).ToList();
        parts.Add($"in pipeline `{ctx.Name(e.Id)}` ({(triggers.Count == 0 ? "no trigger" : string.Join("; ", triggers))})");
        if ((job.Condition ?? e.Stage(job.Stage)?.Condition) is { } c) parts.Add($"when `{c}`");
        return string.Join(", ", parts);
    }

    private static List<ArtifactLineageItem> Artifacts(Context ctx, EffectivePipeline e, EJob job, double point)
    {
        var items = new List<ArtifactLineageItem>();
        var jobSteps = e.StepsOf(job.Key).ToList();
        if (jobSteps.Any(s => s.Step.Kind == PipelineStepKind.InfrastructureDeploy) && !jobSteps.Any(s => s.Step.Kind is PipelineStepKind.ApplicationDeploy or PipelineStepKind.DatabaseMigration)
            && !jobSteps.Any(s => s.Step.ArtifactsConsumed.Count > 0))
            return [new ArtifactLineageItem { Artifact = "(infrastructure code)", Source = "repository checkout", ProducerPipeline = e.Id, ProducerStage = job.Stage, State = ArchitectureEvidenceState.Confirmed,
                Basis = "infrastructure is applied from this run's repository checkout" }];
        var steps = e.StepsOf(job.Key).Where(s => s.Order <= point || point < 0).ToList();
        var uses = steps.SelectMany(s => s.Step.ArtifactsConsumed).ToList();
        var ancestors = e.Ancestors(job.Key);
        bool Before(EStep s) => ancestors.ContainsKey(s.JobKey) || (s.JobKey == job.Key && s.Order < point);
        if (uses.Count == 0 && job.Deployment)
            uses.Add(new PipelineArtifactUse("(all artifacts)", "current (implicit)"));
        // Container deployments deploy an image: the image pushed earlier on the path (its tag is not statically derivable).
        var containerDeploy = e.StepsOf(job.Key).Any(s => s.Step.Kind == PipelineStepKind.ApplicationDeploy && s.Step.Tool is "Azure CLI deploy" or "kubectl apply" or "helm upgrade" or "Azure Container Apps" or "Helm" or "Kubernetes manifest task" or "kubectl task")
            && !e.StepsOf(job.Key).Any(s => s.Step.Tool is "Azure Web App" or "Azure App Service deploy" or "Azure Function App" or "Azure Static Web App");
        var push = e.Steps.Where(s => s.Step.Kind == PipelineStepKind.ContainerPush || (s.Step.Kind == PipelineStepKind.ContainerBuild && s.Step.Tool == "Docker task")).FirstOrDefault(Before);
        if (containerDeploy && push is not null)
        {
            items.Add(new ArtifactLineageItem { Artifact = "(container image)", Source = $"image pushed in this run of `{ctx.Name(e.Id)}`", ProducerPipeline = e.Id, ProducerStage = push.Stage,
                State = ArchitectureEvidenceState.StronglySupported, Basis = $"pushed by `{push.Step.Tool}` in stage `{push.Stage}` before the deployment; the deployed tag is not statically derivable" });
            uses.RemoveAll(u => u.Source == "current (implicit)");
        }
        foreach (var use in uses.Where(u => u.Source != "none"))
        {
            var name = use.Artifact;
            bool Publishes(EStep s) => s.Step.ArtifactsPublished.Count > 0 && (name == "(all artifacts)" || s.Step.ArtifactsPublished.Contains(name));
            if (use.Source.StartsWith("current", StringComparison.Ordinal))
            {
                var producers = e.Steps.Where(Publishes).Where(Before).ToList();
                var producer = producers.FirstOrDefault();
                if (name == "(all artifacts)" && producers.Count > 0) name = string.Join(", ", producers.SelectMany(p => p.Step.ArtifactsPublished).Distinct());
                items.Add(producer is null
                    ? new ArtifactLineageItem { Artifact = name, Source = $"current run of `{ctx.Name(e.Id)}`", State = ArchitectureEvidenceState.Unresolved,
                        Basis = use.Source.Contains("implicit") ? "deployment jobs download this run's artifacts by default, but no publish step was found before this deployment" : "no publish step for this artifact before this deployment" }
                    : new ArtifactLineageItem { Artifact = name,
                        Source = $"current run of `{ctx.Name(e.Id)}`", ProducerPipeline = e.Id, ProducerStage = producer.Stage,
                        State = use.Source.Contains("implicit") ? ArchitectureEvidenceState.StronglySupported : ArchitectureEvidenceState.Confirmed,
                        Basis = use.Source.Contains("implicit") ? $"deployment job downloads this run's artifacts by default; published in stage `{producer.Stage}`" : $"downloaded from this run; published in stage `{producer.Stage}`" });
                continue;
            }
            var alias = use.Source.StartsWith("pipeline:", StringComparison.Ordinal) ? null : use.Source;
            (string? Target, ArchitectureEvidenceState State, string Basis) mapping = alias is not null && ctx.Resources.TryGetValue((e.Id, alias), out var m) ? m
                : alias is null ? (ctx.Pipelines.FirstOrDefault(p => p.Definition.Name.Equals(use.Source[9..], StringComparison.OrdinalIgnoreCase))?.Id, ArchitectureEvidenceState.Inferred, $"specific pipeline '{use.Source[9..]}' matched by file name")
                : (null, ArchitectureEvidenceState.Unresolved, $"download source '{alias}' is not a declared pipeline resource");
            var target = mapping.Target is null ? null : ctx.Pipeline(mapping.Target);
            var published = target?.Steps.Where(Publishes).FirstOrDefault();
            items.Add(new ArtifactLineageItem
            {
                Artifact = name, Source = alias is null ? use.Source : $"pipeline resource `{alias}`{(target is null ? "" : $" → `{ctx.Name(target.Id)}`")}",
                ProducerPipeline = target?.Id, ProducerStage = published?.Stage,
                State = target is null ? ArchitectureEvidenceState.Unresolved : published is null ? ArchitectureEvidenceState.Inferred : mapping.State,
                Basis = target is null ? mapping.Basis : published is null ? $"{mapping.Basis}; that pipeline publishes no artifact named `{name}`" : $"{mapping.Basis}; published in stage `{published.Stage}` of that pipeline",
            });
        }
        // Built inside this pipeline's path without a publish/download: the deployment uses what this run builds (a separate build per pipeline).
        if (items.Count == 0 && e.Steps.Where(s => s.Step.Kind is PipelineStepKind.Build or PipelineStepKind.ContainerBuild or PipelineStepKind.Publish).Any(Before))
        {
            var build = e.Steps.First(s => s.Step.Kind is PipelineStepKind.Build or PipelineStepKind.ContainerBuild or PipelineStepKind.Publish && Before(s));
            items.Add(new ArtifactLineageItem { Artifact = "(build output)", Source = $"built in this run of `{ctx.Name(e.Id)}`", ProducerPipeline = e.Id, ProducerStage = build.Stage,
                State = ArchitectureEvidenceState.StronglySupported, Basis = $"built by `{build.Step.Tool}` before the deployment, without a published artifact" });
        }
        if (items.Count == 0)
            items.Add(new ArtifactLineageItem { Artifact = "(unknown)", Source = "unresolved", State = ArchitectureEvidenceState.Unresolved,
                Basis = "no artifact download, publish or build step was found on the path to this deployment" });
        return items;
    }

    /// <summary>Validations that gated the producer of a promoted artifact in another pipeline.</summary>
    private static IEnumerable<ValidationGate> Inherited(Context ctx, List<ArtifactLineageItem> artifacts)
    {
        foreach (var a in artifacts.Where(a => a.ProducerPipeline is not null && a.State != ArchitectureEvidenceState.Unresolved))
        {
            var producer = ctx.Pipeline(a.ProducerPipeline!)!;
            if (producer.Steps.FirstOrDefault(s => s.Step.ArtifactsPublished.Count > 0 && s.Stage == a.ProducerStage) is not { } publish) continue;
            if (a.Source.StartsWith("current", StringComparison.Ordinal)) continue;
            foreach (var g in Gates(producer, publish.JobKey, publish.Order).Before.Where(g => g.State is GateState.Gates or GateState.Conditional or GateState.SoftGate))
                yield return g with { State = g.State == GateState.Gates ? GateState.Inherited : g.State, Label = $"{g.Label} (in `{ctx.Name(producer.Id)}`)" };
        }
    }

    private static string Approval(Context ctx, EffectivePipeline e, EJob job, string environment, double point)
    {
        var manual = e.Steps.FirstOrDefault(s => s.Step.Kind == PipelineStepKind.ManualApproval && (e.Ancestors(job.Key).ContainsKey(s.JobKey) || (s.JobKey == job.Key && s.Order < point)));
        if (manual is not null) return $"A manual validation step (`{manual.Step.Name}`) pauses for a person before this deployment (defined in YAML; whether it was approved is not known).";
        if (!job.Deployment || job.Environment is null)
            return "No Azure DevOps environment is targeted, so environment approvals cannot apply; service-connection checks are not visible in YAML.";
        var checks = ctx.Metadata.Environments.FirstOrDefault(x => x.Name.Equals(job.Environment, StringComparison.OrdinalIgnoreCase));
        return checks is not null
            ? checks.Checks.Count == 0 ? $"Azure DevOps reports no checks configured on environment `{job.Environment}` (configuration, read-only)."
                : $"Azure DevOps reports checks on environment `{job.Environment}`: {string.Join(", ", checks.Checks)} (configured — not whether they were approved)."
            : $"Deployment job targets environment `{environment}`. {PipelineReviewText.ApprovalNotAssessable}";
    }

    private static string Rollback(EffectivePipeline e, EJob job)
    {
        var steps = e.StepsOf(job.Key).ToList();
        var descendants = e.Jobs.Where(j => e.Ancestors(j.Key).ContainsKey(job.Key)).Select(j => j.Key).ToHashSet(StringComparer.Ordinal);
        if (steps.Concat(e.Steps.Where(s => descendants.Contains(s.JobKey))).FirstOrDefault(s => s.Step.Kind == PipelineStepKind.Rollback) is { } rollback)
            return $"Rollback step `{rollback.Step.Tool}` (`{rollback.Step.Name}`) is defined.";
        if (steps.FirstOrDefault(s => s.Step.StrategyPhase?.Contains("failure", StringComparison.OrdinalIgnoreCase) == true) is { } failure)
            return $"The deployment strategy defines steps on failure (`{failure.Step.Name}`).";
        if (steps.FirstOrDefault(s => s.Step.Tool == "slot swap") is { } swap) return $"Deploys through a slot swap (`{swap.Step.Name}`), which allows swapping back; no explicit rollback step.";
        if (job.Strategy is "canary" or "rolling") return $"Uses the `{job.Strategy}` strategy; no explicit rollback step.";
        return "No rollback mechanism detected in pipeline source (this does not mean rollback is impossible).";
    }

    // ── Tests and matrix ─────────────────────────────────────────────────────────────────────────────────────────────

    private static List<ReviewedTest> Tests(Context ctx)
    {
        var list = new List<ReviewedTest>();
        foreach (var e in ctx.Pipelines)
            foreach (var s in e.Steps)
            {
                if (Category(s) is not { } category || category == ValidationCategory.Build) continue;
                var job = e.Job(s.JobKey);
                var gates = ctx.Deployments.Where(d => d.Before.Any(g => g.Evidence.Line == s.Step.Line && g.Evidence.File == s.File && g.State is GateState.Gates or GateState.Conditional or GateState.SoftGate or GateState.Inherited))
                    .Select(d => d.Id).ToList();
                var after = ctx.Deployments.Where(d => d.After.Any(g => g.Evidence.Line == s.Step.Line && g.Evidence.File == s.File)).Select(d => d.Id).ToList();
                var triggers = e.Definition.Triggers.Select(t => Trigger(ctx, e.Definition, t).Text).ToList();
                var condition = s.Step.Condition ?? job?.Condition ?? e.Stage(s.Stage)?.Condition;
                list.Add(new ReviewedTest
                {
                    Pipeline = e.Id, PipelineName = ctx.Name(e.Id), Category = category, Name = Friendly(s.Step), Tool = s.Step.Tool, Targets = s.Step.Targets, Stage = s.Stage, Job = job?.Name,
                    WhenItRuns = (triggers.Count == 0 ? "No trigger" : string.Join("; ", triggers)) + (condition is null ? "" : $" — only when `{condition}`"),
                    Condition = condition, ContinueOnError = s.Step.ContinueOnError || job?.ContinueOnError == true, Gates = gates, RunsAfter = after, Evidence = Ref(e, s),
                });
            }
        return list;
    }

    private static TestMatrix Matrix(Context ctx, List<ReviewedTest> tests, List<EnvironmentSummary> environments)
    {
        var columns = new List<string>();
        var prPipelines = ctx.Pipelines.Where(p => Role(p.Definition) == "Pull-request validation").Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        if (tests.Any(t => prPipelines.Contains(t.Pipeline))) columns.Add("PR");
        columns.AddRange(environments.Select(e => e.Environment));
        int Priority(GateState s) => s switch { GateState.Gates => 0, GateState.Inherited => 1, GateState.Conditional => 2, GateState.SoftGate => 3, GateState.After => 4, GateState.Unknown => 5, _ => 6 };
        var rows = new List<TestMatrixRow>();
        foreach (var category in tests.Select(t => t.Category).Concat(ctx.Deployments.SelectMany(d => d.Before.Concat(d.After)).Select(g => g.Category))
                     .Where(c => c != ValidationCategory.Build).Distinct().Order())
        {
            var cells = new Dictionary<string, GateState?>(StringComparer.Ordinal);
            if (columns.Contains("PR")) cells["PR"] = tests.Any(t => t.Category == category && prPipelines.Contains(t.Pipeline)) ? GateState.Gates : null;
            foreach (var env in environments)
            {
                var states = ctx.Deployments.Where(d => env.Deployments.Contains(d.Id)).SelectMany(d => d.Before.Concat(d.After)).Where(g => g.Category == category).Select(g => g.State).ToList();
                cells[env.Environment] = states.Count == 0 ? null : states.OrderBy(Priority).First();
            }
            var scheduledOnly = tests.Where(t => t.Category == category).ToList() is { Count: > 0 } all && all.All(t => Role(ctx.Pipeline(t.Pipeline)!.Definition) == "Scheduled");
            rows.Add(new TestMatrixRow(category, cells, scheduledOnly ? $"Scheduled only — does not gate normal delivery ({string.Join(", ", tests.Where(t => t.Category == category).Select(t => t.PipelineName).Distinct())})" : null));
        }
        var notes = new List<string> { "A cell shows how a validation relates to deployments to that environment: Gates (before, a failure stops it), Gates via promoted artifact, Conditional, May not block, Runs after, or Not assessable." };
        if (columns.Contains("PR")) notes.Add("PR: runs for pull requests. Whether a failure blocks merging depends on branch policies, which are not in YAML.");
        return new TestMatrix(columns, rows, notes);
    }

    // ── Environments, progression, dependencies ───────────────────────────────────────────────────────────────────────

    private static (List<EnvironmentProgressionStep>, List<EnvironmentSummary>) Progression(Context ctx)
    {
        string Label(DeploymentReview d) => d.EnvironmentKind is SourceEnvironmentKind.Custom or SourceEnvironmentKind.Default ? d.Environment : EnvironmentLabel(d.EnvironmentKind, d.Environment);
        var steps = new List<EnvironmentProgressionStep>();
        foreach (var (id, unit) in ctx.DeploymentUnits)
        {
            var d = ctx.Deployments.First(x => x.Id == id);
            foreach (var (otherId, other) in ctx.DeploymentUnits.Where(x => x.Key != id))
            {
                var o = ctx.Deployments.First(x => x.Id == otherId);
                if (Label(o) == Label(d)) continue;
                if (other.Pipeline == unit.Pipeline && unit.Pipeline.Ancestors(unit.Job.Key).ContainsKey(other.Job.Key))
                    steps.Add(new(Label(o), Label(d), ArchitectureEvidenceState.Confirmed, $"`{d.Stage ?? d.Job}` depends on `{o.Stage ?? o.Job}` in `{ctx.Name(unit.Pipeline.Id)}`"));
                else if (other.Pipeline != unit.Pipeline && unit.Pipeline.Definition.Resources.Any(r => r.TriggerDeclared && ctx.Resources.TryGetValue((unit.Pipeline.Id, r.Alias), out var m) && m.Target == other.Pipeline.Id))
                    steps.Add(new(Label(o), Label(d), ArchitectureEvidenceState.StronglySupported, $"`{ctx.Name(unit.Pipeline.Id)}` starts when `{ctx.Name(other.Pipeline.Id)}` completes"));
            }
        }
        var progression = steps.GroupBy(s => (s.From, s.To)).Select(g => g.OrderBy(s => s.State).First()).OrderBy(s => s.From, StringComparer.Ordinal).ThenBy(s => s.To, StringComparer.Ordinal).ToList();
        var environments = ctx.Deployments.GroupBy(Label).Select(g => new EnvironmentSummary
        {
            Environment = g.Key, Kind = g.First().EnvironmentKind, Deployments = g.Select(d => d.Id).ToList(), Pipelines = g.Select(d => d.Pipeline).Distinct().ToList(),
        }).ToList();
        // Order: topological over progression edges, ties by environment rank.
        var ordered = new List<EnvironmentSummary>();
        var remaining = environments.ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.Where(e => !progression.Any(p => p.To == e.Environment && remaining.Any(r => r.Environment == p.From))).OrderBy(e => Rank(e.Kind)).ThenBy(e => e.Environment, StringComparer.Ordinal).FirstOrDefault()
                ?? remaining.OrderBy(e => Rank(e.Kind)).First();
            ordered.Add(next);
            remaining.Remove(next);
        }
        return (progression, ordered);
    }

    private static List<PipelineDependency> Dependencies(Context ctx)
    {
        var list = new List<PipelineDependency>();
        foreach (var e in ctx.Pipelines)
        {
            foreach (var r in e.Definition.Resources)
            {
                var evidence = new PipelineEvidenceRef { File = e.Definition.File, Line = r.Line, Pipeline = e.Id, Note = $"resources.{r.Kind}s: {r.Alias}" };
                if (r.Kind == "repository")
                {
                    list.Add(new(r.Source, e.Id, r.TriggerDeclared ? PipelineEdgeKind.Triggers : PipelineEdgeKind.IncludesTemplate, ArchitectureEvidenceState.Confirmed,
                        r.TriggerDeclared ? $"changes in repository `{r.Source}` trigger `{ctx.Name(e.Id)}`" : $"repository resource `{r.Alias}` (`{r.Source}`{(r.Ref is null ? "" : $" @ {r.Ref}")}) — templates or code from another repository", evidence));
                    continue;
                }
                var (target, state, basis) = ctx.Resources[(e.Id, r.Alias)];
                var from = target ?? $"(external pipeline '{r.Source}')";
                if (r.TriggerDeclared) list.Add(new(from, e.Id, PipelineEdgeKind.Triggers, state, $"pipeline resource `{r.Alias}` with trigger{(r.TriggerBranches.Count > 0 ? $" on {string.Join(", ", r.TriggerBranches)}" : "")}; {basis}", evidence));
                var downloads = e.Steps.Where(s => s.Step.ArtifactsConsumed.Any(a => a.Source == r.Alias)).ToList();
                if (downloads.Count > 0)
                    list.Add(new(from, e.Id, PipelineEdgeKind.ConsumesArtifactFrom, state, $"downloads {string.Join(", ", downloads.SelectMany(d => d.Step.ArtifactsConsumed).Where(a => a.Source == r.Alias).Select(a => $"`{a.Artifact}`").Distinct())} from pipeline resource `{r.Alias}`; {basis}", Ref(e, downloads[0])));
                if (!r.TriggerDeclared && downloads.Count == 0)
                    list.Add(new(from, e.Id, PipelineEdgeKind.LikelyFollows, state is ArchitectureEvidenceState.Confirmed ? ArchitectureEvidenceState.StronglySupported : state,
                        $"pipeline resource `{r.Alias}` without a trigger: a run of it can be chosen when this pipeline starts; {basis}", evidence));
            }
            foreach (var use in e.Definition.TemplateUses.Where(u => u.Level != "variables").DistinctBy(u => u.Template))
                list.Add(new(e.Id, use.ResolvedPath ?? use.Template, PipelineEdgeKind.IncludesTemplate, use.Resolved ? ArchitectureEvidenceState.Confirmed : ArchitectureEvidenceState.Unresolved,
                    use.Resolved ? $"uses template `{use.ResolvedPath}` ({use.Level})" : $"uses template `{use.Template}` ({use.Level}) — not analyzed", new PipelineEvidenceRef { File = e.Definition.File, Line = use.Line, Pipeline = e.Id }));
        }
        // Infrastructure and application deployments in different pipelines.
        foreach (var infra in ctx.Deployments.Where(d => d.DeploysWhat.Contains("Infrastructure")))
            foreach (var app in ctx.Deployments.Where(d => d.Pipeline != infra.Pipeline && d.DeploysWhat.Contains("Application") && SameEnvironment(d, infra)))
            {
                var linked = list.Any(x => x.FromPipeline == infra.Pipeline && x.ToPipeline == app.Pipeline && x.Kind is PipelineEdgeKind.Triggers or PipelineEdgeKind.ConsumesArtifactFrom);
                if (linked) list.Add(new(infra.Pipeline, app.Pipeline, PipelineEdgeKind.DependsOn, ArchitectureEvidenceState.StronglySupported, $"infrastructure for {Display(app)} is deployed by `{infra.PipelineName}` before `{app.PipelineName}` runs", infra.Evidence));
            }
        return list.DistinctBy(d => (d.FromPipeline, d.ToPipeline, d.Kind, d.Basis)).ToList();
    }

    internal static bool SameEnvironment(DeploymentReview a, DeploymentReview b) =>
        a.EnvironmentKind is not (SourceEnvironmentKind.Custom or SourceEnvironmentKind.Default) ? a.EnvironmentKind == b.EnvironmentKind : a.Environment.Equals(b.Environment, StringComparison.OrdinalIgnoreCase);

    // ── Path coverage ─────────────────────────────────────────────────────────────────────────────────────────────────

    internal sealed record Area(string Name, string Path, string Kind, string? ComponentId);

    internal static List<Area> Areas(Context ctx)
    {
        var areas = new List<Area>();
        string Dir(string file) => PipelinePathFilters.Relative(file.Contains('/') ? file[..file.LastIndexOf('/')] : "", ctx.Root);
        var arch = ctx.Snapshot.Architecture;
        foreach (var c in arch?.Components.Where(c => c.ComponentType != ArchitectureComponentType.ExternalSystem && c.SourceProject.Length > 0) ?? [])
            areas.Add(new Area(c.Name, Dir(c.SourceProject), "Component", c.Id));
        foreach (var l in arch?.SharedLibraries.Where(l => l.Internal && l.SourceProject.Length > 0) ?? [])
            areas.Add(new Area(l.Name, Dir(l.SourceProject), "Shared library", l.Id));
        foreach (var c in ctx.Snapshot.EvidenceDomains?.Contracts.Contracts.Where(c => c.File.Length > 0).DistinctBy(c => Dir(c.File)) ?? [])
            areas.Add(new Area($"Contracts in {Dir(c.File)}", Dir(c.File), "Contract", null));
        foreach (var dir in ctx.Snapshot.EvidenceDomains?.Infrastructure.Resources.Select(r => Dir(r.File)).Distinct(StringComparer.Ordinal) ?? [])
            areas.Add(new Area($"Infrastructure in {dir}", dir, "Infrastructure", null));
        return areas.Where(a => a.Path.Length > 0).DistinctBy(a => (a.Path, a.Kind)).Take(80).ToList();
    }

    /// <summary>Pipelines relevant to an area: their path filters reach into it, or their build/test/IaC steps target it.</summary>
    internal static bool Relevant(Context ctx, EffectivePipeline e, Area area) =>
        (PipelinePathFilters.HasPathFilters(e.Definition) && PipelinePathFilters.Pipeline(e.Definition, area.Path).Coverage != TriggerCoverage.No)
        || e.Steps.Any(s => s.Step.Targets.Concat(s.Step.WorkingDirectory is { } w ? [w] : []).Select(t => PipelinePathFilters.Relative(PipelinePathFilters.Normalize(t), ctx.Root))
            .Any(t => t.Length > 0 && (t.StartsWith(area.Path + "/", StringComparison.OrdinalIgnoreCase) || t.Equals(area.Path, StringComparison.OrdinalIgnoreCase))));

    private static List<PathCoverageRow> PathCoverage(Context ctx)
    {
        var rows = new List<PathCoverageRow>();
        foreach (var area in Areas(ctx))
        {
            // A shared library's row also shows the pipelines that validate the components using it — whether those start on library changes is the question.
            var consumers = area.Kind == "Shared library"
                ? (ctx.Snapshot.Architecture?.SharedLibraries.FirstOrDefault(l => l.Id == area.ComponentId)?.ReferencedByComponents ?? []).Select(id => Areas(ctx).FirstOrDefault(a => a.ComponentId == id)).OfType<Area>().ToList()
                : [];
            var cells = ctx.Pipelines.Where(e => Role(e.Definition) != "Manual" && (Relevant(ctx, e, area) || !PipelinePathFilters.HasPathFilters(e.Definition) || consumers.Any(c => Relevant(ctx, e, c))))
                .Select(e =>
                {
                    var (coverage, basis) = Effective(ctx, e, area.Path);
                    return new PathCoverageCell(e.Id, coverage, basis, e.Steps.Select(s => Category(s.Step.Kind)).OfType<ValidationCategory>().Where(IsTest).Distinct().Order().ToList());
                }).ToList();
            rows.Add(new PathCoverageRow(area.Name, area.Path, area.Kind, cells, cells.Count == 0 ? ArchitectureEvidenceState.Unresolved : ArchitectureEvidenceState.Confirmed,
                cells.Count == 0 ? "No pipeline in this snapshot starts on changes here." : "Path filters evaluated against the folder; branch filters are not part of this check."));
        }
        return rows;
    }

    /// <summary>Change coverage including downstream pipelines: a pipeline started by another pipeline inherits that pipeline's coverage.</summary>
    internal static (TriggerCoverage Coverage, string Basis) Effective(Context ctx, EffectivePipeline e, string path, int depth = 0)
    {
        var direct = PipelinePathFilters.Pipeline(e.Definition, path);
        if (direct.Coverage != TriggerCoverage.No || depth > 4) return direct;
        foreach (var r in e.Definition.Resources.Where(r => r.TriggerDeclared))
            if (ctx.Resources.TryGetValue((e.Id, r.Alias), out var m) && m.Target is { } upstream && ctx.Pipeline(upstream) is { } u)
            {
                var via = Effective(ctx, u, path, depth + 1);
                if (via.Coverage != TriggerCoverage.No) return (via.Coverage, $"via `{ctx.Name(u.Id)}` (pipeline trigger): {via.Basis}");
            }
        return direct;
    }

    /// <summary>Which pipelines a change to a path starts, and what validates it before each environment those pipelines reach.</summary>
    public static PathProbeResult Probe(IqrSourceSnapshot snapshot, PipelineReviewResult review, string path, PipelineMetadataSummary? metadata = null)
    {
        var cicd = snapshot.EvidenceDomains?.CiCd;
        if (cicd is null || cicd.AnalyzerVersion < PipelineReviewText.RequiredCiCdVersion) return new PathProbeResult(path, [], [NeedsReanalysis]);
        var definitions = cicd.Pipelines.GroupBy(p => p.File, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var ctx = new Context { Snapshot = snapshot, Definitions = definitions, Metadata = metadata ?? review.Metadata,
            Pipelines = cicd.Pipelines.Where(p => !p.IsTemplate).Select(p => new EffectivePipeline(p, definitions)).ToList() };
        foreach (var p in review.Pipelines) ctx.Names[p.Id] = p.Name;
        MapResources(ctx);
        ctx.Root = PipelinePathFilters.RepositoryRoot(cicd.Pipelines.Select(p => p.File).Concat(snapshot.Architecture?.Components.Select(c => c.SourceProject) ?? []),
            cicd.Pipelines.SelectMany(p => p.Triggers).SelectMany(t => t.PathsInclude.Concat(t.PathsExclude)));
        var normalized = PipelinePathFilters.Relative(PipelinePathFilters.Normalize(path), ctx.Root);
        var result = new List<PathProbePipeline>();
        foreach (var e in ctx.Pipelines)
        {
            var direct = e.Definition.Triggers.Where(PipelinePathFilters.ChangeTriggered).Any(t => PipelinePathFilters.Starts(t, normalized));
            var (coverage, basis) = direct ? (TriggerCoverage.Yes, PipelinePathFilters.Pipeline(e.Definition, normalized.Contains('/') ? normalized[..normalized.LastIndexOf('/')] : normalized).Basis) : Effective(ctx, e, normalized);
            if (direct) basis = e.Definition.Triggers.Where(PipelinePathFilters.ChangeTriggered).Any(t => t.PathsInclude.Count > 0 || t.PathsExclude.Count > 0)
                ? $"path filter includes `{normalized}`" : "no path filter: every change starts it";
            var deployments = review.Deployments.Where(d => d.Pipeline == e.Id).Select(d => new PathProbeDeployment(d.Environment, d.Before)).ToList();
            result.Add(new PathProbePipeline(e.Id, ctx.Name(e.Id), coverage, basis, deployments));
        }
        return new PathProbeResult(normalized, result.OrderBy(p => p.Triggers).ThenBy(p => p.PipelineName, StringComparer.Ordinal).ToList(),
            ["Branch filters are not evaluated here: a change must also be on a branch the trigger includes.", "Pull-request triggers in YAML do not apply to Azure Repos, where branch policies start PR validation."]);
    }

    // ── Findings ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string FindingId(PipelineFindingCategory category, string key) =>
        $"{category}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..10].ToLowerInvariant()}";

    private static void Add(Context ctx, PipelineFindingCategory category, PipelineFindingSeverity severity, ArchitectureEvidenceState state, string key, string title, string why,
        string explanation, IEnumerable<string> pipelines, string? environment, IEnumerable<PipelineEvidenceRef> evidence, string action)
    {
        var id = FindingId(category, key);
        if (ctx.Findings.Any(f => f.Id == id)) return;
        ctx.Findings.Add(new PipelineReviewFinding
        {
            Id = id, Category = category, Severity = severity, EvidenceState = state, Title = title, WhyItMatters = why, Explanation = explanation, Pipelines = pipelines.Distinct().ToList(),
            Environment = environment, Evidence = evidence.Take(8).ToList(), SuggestedAction = action,
        });
    }

    private static bool Critical(SourceEnvironmentKind k) => k is SourceEnvironmentKind.Production or SourceEnvironmentKind.Staging or SourceEnvironmentKind.QA;

    private static void Findings(Context ctx, List<ReviewedTest> tests, List<EnvironmentProgressionStep> progression, List<PathCoverageRow> coverage, List<UnresolvedTemplate> unresolved, PipelineEvidence cicd)
    {
        foreach (var d in ctx.Deployments)
        {
            var (e, job, point) = ctx.DeploymentUnits[d.Id];
            var env = Display(d);
            var infraOnly = InfrastructureOnly(d);
            var before = d.Before.Where(g => IsTest(g.Category)).ToList();
            var gating = before.Where(g => g.State is GateState.Gates or GateState.Inherited).ToList();
            var opaque = d.Before.Any(g => g.State == GateState.Unknown);
            var structureUnknown = !StructureKnown(e);
            if (opaque || structureUnknown)
            {
                if (gating.Count == 0)
                    Add(ctx, PipelineFindingCategory.UnresolvedFlow, PipelineFindingSeverity.Info, ArchitectureEvidenceState.Unresolved, $"opaque|{d.Id}",
                        $"Test gating of {env} deployment cannot be assessed", "Steps behind an unresolved template or an unanalyzed platform may include tests — absence is not established.",
                        string.Join(" ", d.Unresolved), [e.Id], env, [d.Evidence], "Resolve the template (analyze its repository in Source Analysis) to assess what gates this deployment.");
            }
            else if (before.Count == 0)
            {
                var notGating = tests.Where(t => t.Pipeline == e.Id && IsTest(t.Category) && !t.Gates.Contains(d.Id) && !t.RunsAfter.Contains(d.Id)).ToList();
                if (notGating.Count > 0)
                    Add(ctx, PipelineFindingCategory.TestGatingGap, Critical(d.EnvironmentKind) ? PipelineFindingSeverity.High : PipelineFindingSeverity.Medium, ArchitectureEvidenceState.Confirmed, $"notgating|{d.Id}",
                        $"Tests exist but do not gate the {env} deployment",
                        "A failing test cannot stop this deployment: the deployment does not depend on the stage or job that runs the tests.",
                        $"{string.Join(", ", notGating.Select(t => $"{PipelineReviewText.Label(t.Category)} (`{t.Stage ?? t.Job}`)").Distinct())} run in `{d.PipelineName}`, but `{d.Stage ?? d.Job}` does not depend on them. {d.HowReached}.",
                        [e.Id], env, [d.Evidence, .. notGating.Select(t => t.Evidence)], $"Confirm whether the {env} deployment should depend on the test stage (dependsOn).");
                else
                    Add(ctx, PipelineFindingCategory.TestGatingGap, Critical(d.EnvironmentKind) ? PipelineFindingSeverity.High : PipelineFindingSeverity.Medium, ArchitectureEvidenceState.Confirmed, $"notests|{d.Id}",
                        $"{env} deployment is reachable without any detected test",
                        "Nothing on the path to this deployment runs tests, so a change can be deployed without automated validation.",
                        $"No test step was found before `{d.Stage ?? d.Job}` in `{d.PipelineName}`{(d.Artifacts.Any(a => a.State != ArchitectureEvidenceState.Unresolved && !a.Source.StartsWith("current", StringComparison.Ordinal)) ? " or in the pipeline that produced its artifact" : "")}. It runs {d.HowReached}.",
                        [e.Id], env, [d.Evidence], "Confirm which validation should run before this deployment.");
            }
            if (!opaque && !structureUnknown && before.Count > 0 && !infraOnly)
            {
                // A test category this pipeline runs, that neither gates nor follows this deployment ("Integration tests do not gate QA").
                var present = before.Select(g => g.Category).Concat(d.After.Select(g => g.Category)).ToHashSet();
                foreach (var missing in tests.Where(t => t.Pipeline == e.Id && IsTest(t.Category) && !present.Contains(t.Category) && !t.Gates.Contains(d.Id) && !t.RunsAfter.Contains(d.Id))
                             .GroupBy(t => t.Category))
                    Add(ctx, PipelineFindingCategory.TestGatingGap, Critical(d.EnvironmentKind) ? PipelineFindingSeverity.Medium : PipelineFindingSeverity.Low, ArchitectureEvidenceState.Confirmed,
                        $"category-notgating|{d.Id}|{missing.Key}",
                        $"{PipelineReviewText.Label(missing.Key)} do not gate the {env} deployment",
                        $"{PipelineReviewText.Label(missing.Key)} run in this pipeline, but the {env} deployment does not depend on them: it can proceed while they fail or before they run.",
                        $"{string.Join(", ", missing.Select(t => $"`{t.Name}` in `{t.Stage ?? t.Job}`").Distinct())}; `{d.Stage ?? d.Job}` runs {d.HowReached}.",
                        [e.Id], env, [d.Evidence, .. missing.Select(t => t.Evidence)], $"Confirm whether the {env} deployment should depend on the {PipelineReviewText.Label(missing.Key).ToLowerInvariant()}.");
            }
            foreach (var soft in before.Where(g => g.State == GateState.SoftGate))
                Add(ctx, PipelineFindingCategory.ConditionCoverageGap, Critical(d.EnvironmentKind) ? PipelineFindingSeverity.Medium : PipelineFindingSeverity.Low, ArchitectureEvidenceState.Confirmed, $"soft|{d.Id}|{soft.Evidence.Line}",
                    $"{PipelineReviewText.Label(soft.Category)} failure may not block the {env} deployment",
                    "continueOnError marks a failed step as succeeded-with-issues, and a tolerant condition runs after failures: the deployment can proceed after a test failure.",
                    $"`{soft.Label}` in `{soft.Stage ?? soft.Job}` {(soft.Evidence.Condition is { } c ? $"(condition `{c}`) " : "")}is on the path with continueOnError or a tolerant downstream condition.",
                    [e.Id], env, [soft.Evidence, d.Evidence], "Confirm the failure of this validation should not stop the deployment.");
            foreach (var conditional in before.Where(g => g.State == GateState.Conditional))
                Add(ctx, PipelineFindingCategory.ConditionCoverageGap, PipelineFindingSeverity.Low, ArchitectureEvidenceState.Confirmed, $"conditional|{d.Id}|{conditional.Evidence.Line}",
                    $"{PipelineReviewText.Label(conditional.Category)} only run under a condition before the {env} deployment",
                    "When the condition is false the step is skipped and the job still succeeds, so the deployment proceeds without it.",
                    $"`{conditional.Label}` runs only when `{conditional.Condition}`.", [e.Id], env, [conditional.Evidence, d.Evidence], "Confirm this validation may be skipped before deployment.");
            if (PipelineConditions.ReplacesSuccessCheck(job.Condition ?? e.Stage(job.Stage)?.Condition) && before.Count > 0)
                Add(ctx, PipelineFindingCategory.ConditionCoverageGap, Critical(d.EnvironmentKind) ? PipelineFindingSeverity.High : PipelineFindingSeverity.Medium, ArchitectureEvidenceState.Confirmed, $"bypass|{d.Id}",
                    $"{env} deployment can run even if earlier validation fails",
                    "A custom condition without succeeded() replaces the default success check, so the deployment does not wait for earlier stages to succeed.",
                    $"Condition `{job.Condition ?? e.Stage(job.Stage)?.Condition}` on `{d.Stage ?? d.Job}`.", [e.Id], env, [d.Evidence], "Add succeeded() to the condition if earlier failures should stop the deployment.");
            // Infrastructure or a database migration that gates an application deployment to the same environment: the application's checks are what follow.
            var gatesAnApp = (infraOnly || d.DeploysWhat.SequenceEqual(["Database"])) && ctx.Deployments.Any(a => a.Pipeline == d.Pipeline && a.DeploysWhat.Contains("Application") && SameEnvironment(a, d)
                && e.Ancestors(ctx.DeploymentUnits[a.Id].Job.Key).ContainsKey(job.Key));
            if (!opaque && !structureUnknown && !gatesAnApp && !d.After.Any(g => IsPostDeployCheck(g.Category)))
                Add(ctx, PipelineFindingCategory.PostDeployValidationGap, Critical(d.EnvironmentKind) ? PipelineFindingSeverity.Medium : PipelineFindingSeverity.Low, ArchitectureEvidenceState.Confirmed, $"post|{d.Id}",
                    $"No post-deployment validation detected after the {env} deployment in `{d.PipelineName}`",
                    "Without a smoke test, health check or test after deploying, a broken deployment is only noticed by people or monitoring.",
                    $"No test or health check runs after `{d.Stage ?? d.Job}` in `{d.PipelineName}` (later steps of the job, its strategy's post phases, or stages that depend on it).",
                    [e.Id], env, [d.Evidence], "Add or confirm a post-deployment check (smoke test, health check) for this environment.");
            if (d.EnvironmentKind == SourceEnvironmentKind.Production && d.Rollback.StartsWith("No rollback", StringComparison.Ordinal))
                Add(ctx, PipelineFindingCategory.RollbackGap, PipelineFindingSeverity.Info, ArchitectureEvidenceState.Confirmed, $"rollback|{d.Id}", $"No rollback mechanism detected for the {env} deployment",
                    "A defined rollback (slot swap, on-failure steps, rollout undo) makes recovery from a bad release faster. It may exist outside the pipeline.",
                    d.Rollback, [e.Id], env, [d.Evidence], "Confirm how a failed production release is rolled back.");
        }

        // Environment progression: production reachable without QA, QA and production in parallel.
        var prod = ctx.Deployments.Where(d => d.EnvironmentKind == SourceEnvironmentKind.Production && !InfrastructureOnly(d)).ToList();
        var qa = ctx.Deployments.Where(d => d.EnvironmentKind is SourceEnvironmentKind.QA or SourceEnvironmentKind.Staging or SourceEnvironmentKind.Test && !InfrastructureOnly(d)).ToList();
        foreach (var p in prod)
        {
            var (pe, pjob, _) = ctx.DeploymentUnits[p.Id];
            var ancestors = pe.Ancestors(pjob.Key);
            var samePipelineQa = qa.Where(q => q.Pipeline == p.Pipeline).ToList();
            var qaBefore = samePipelineQa.Any(q => ancestors.ContainsKey(ctx.DeploymentUnits[q.Id].Job.Key))
                || progression.Any(s => s.To == "PROD" && s.From is "QA" or "STAGING" or "TEST");
            if (samePipelineQa.Count > 0 && !qaBefore)
                Add(ctx, PipelineFindingCategory.EnvironmentProgressionGap, PipelineFindingSeverity.High, ArchitectureEvidenceState.Confirmed, $"parallel|{p.Id}",
                    "Production does not depend on the QA deployment", "Production can be deployed without the change first passing through QA in the same run.",
                    $"`{p.Stage ?? p.Job}` and `{samePipelineQa[0].Stage ?? samePipelineQa[0].Job}` in `{p.PipelineName}` have no dependency between them (they may run in parallel).",
                    [p.Pipeline], Display(p), [p.Evidence, samePipelineQa[0].Evidence], "Confirm production should depend on the QA stage.");
            else if (samePipelineQa.Count == 0 && qa.Count > 0 && !qaBefore)
                Add(ctx, PipelineFindingCategory.EnvironmentProgressionGap, PipelineFindingSeverity.Medium, ArchitectureEvidenceState.Unresolved, $"separate|{p.Id}",
                    "No detectable link from QA to Production", "Production is deployed by a different pipeline with no pipeline trigger or artifact relationship to the QA deployment; promotion order is decided outside the definitions.",
                    $"QA: `{qa[0].PipelineName}`; Production: `{p.PipelineName}`, which runs {p.HowReached}."
                        + (pe.Definition.Resources.FirstOrDefault(r => r.Kind == "pipeline" && ctx.Resources.TryGetValue((pe.Id, r.Alias), out var rm) && rm.Target == qa[0].Pipeline) is { } declared
                            ? $" `{p.PipelineName}` declares `{qa[0].PipelineName}` as pipeline resource `{declared.Alias}`{(declared.TriggerDeclared ? "" : " without a trigger")}, and does not download its artifacts." : ""),
                    [p.Pipeline, qa[0].Pipeline], Display(p), [p.Evidence, qa[0].Evidence],
                    "Confirm how a change is promoted from QA to Production (pipeline trigger, artifact resource, manual procedure).");
            else if (qa.Count == 0)
                Add(ctx, PipelineFindingCategory.EnvironmentProgressionGap, PipelineFindingSeverity.Medium, ArchitectureEvidenceState.StronglySupported, $"noqa|{p.Id}",
                    "Production deployment without a detected pre-production environment", "No QA, test or staging deployment was found in this snapshot before production.",
                    p.HowReached, [p.Pipeline], Display(p), [p.Evidence], "Confirm whether a pre-production environment exists (another repository or a manual process).");
        }

        // Artifact lineage: production vs the artifact QA tested.
        foreach (var p in prod)
            foreach (var q in qa.Where(q => q.EnvironmentKind is SourceEnvironmentKind.QA or SourceEnvironmentKind.Staging).Take(1))
            {
                string Producer(DeploymentReview d) => d.Artifacts.Where(a => a.State != ArchitectureEvidenceState.Unresolved && a.ProducerPipeline is not null)
                    .Select(a => $"{a.ProducerPipeline}|{a.ProducerStage}").FirstOrDefault() ?? "";
                var pp = Producer(p);
                var qp = Producer(q);
                if (pp.Length == 0)
                    Add(ctx, PipelineFindingCategory.ArtifactLineageGap, PipelineFindingSeverity.Medium, ArchitectureEvidenceState.Unresolved, $"lineage-unresolved|{p.Id}",
                        "Artifact lineage to Production unresolved", "It cannot be established from source that Production deploys the artifact that was tested in QA.",
                        string.Join(" ", p.Artifacts.Select(a => $"`{a.Artifact}`: {a.Basis}.")), [p.Pipeline, q.Pipeline], Display(p), [p.Evidence, q.Evidence],
                        "Confirm Production should promote the QA-tested artifact (for example a pipeline resource on the build that QA deployed).");
                else if (qp.Length > 0 && pp != qp)
                    Add(ctx, PipelineFindingCategory.ArtifactLineageGap, PipelineFindingSeverity.Medium, ArchitectureEvidenceState.StronglySupported, $"rebuild|{p.Id}",
                        "Production and QA appear to use different builds", "The artifact tested in QA may not be the same artifact deployed to Production.",
                        $"QA deploys output of `{ctx.Name(qp.Split('|')[0])}` stage `{qp.Split('|')[1]}`; Production deploys output of `{ctx.Name(pp.Split('|')[0])}` stage `{pp.Split('|')[1]}`.",
                        [p.Pipeline, q.Pipeline], Display(p), [p.Evidence, q.Evidence], "Confirm Production should promote the QA-tested artifact rather than rebuild.");
            }

        // Rebuild per environment: a higher environment deploys a separate build of the same source instead of the artifact a lower one tested.
        var appDeployments = ctx.Deployments.Where(d => d.DeploysWhat.Contains("Application") && Rank(d.EnvironmentKind) is > 0 and < 6).ToList();
        foreach (var higher in appDeployments)
        {
            var hp = higher.Artifacts.FirstOrDefault(a => a.ProducerPipeline is not null && a.State != ArchitectureEvidenceState.Unresolved);
            if (hp is null) continue;
            if (higher.EnvironmentKind == SourceEnvironmentKind.Production && qa.Count > 0) continue; // production vs QA lineage is reported above
            var higherTargets = BuildTargets(ctx, higher);
            // The closest lower environment that builds the same source (by build-target overlap), from a different producing run.
            var candidates = appDeployments.Where(l => Rank(l.EnvironmentKind) < Rank(higher.EnvironmentKind) && l.Pipeline != higher.Pipeline
                    && !(higher.EnvironmentKind == SourceEnvironmentKind.Production && l.EnvironmentKind is SourceEnvironmentKind.QA or SourceEnvironmentKind.Staging))
                .Select(l => (Lower: l, Producer: l.Artifacts.FirstOrDefault(a => a.ProducerPipeline is not null && a.State != ArchitectureEvidenceState.Unresolved), Targets: BuildTargets(ctx, l)))
                .Where(x => x.Producer is not null && x.Producer.ProducerPipeline != hp.ProducerPipeline)
                .Select(x => (x.Lower, x.Producer, Shared: x.Targets.Intersect(higherTargets, StringComparer.OrdinalIgnoreCase).ToList(),
                    Score: x.Targets.Intersect(higherTargets, StringComparer.OrdinalIgnoreCase).Count() / (double)Math.Max(1, x.Targets.Union(higherTargets, StringComparer.OrdinalIgnoreCase).Count())))
                .Where(x => x.Shared.Count > 0).ToList();
            if (candidates.Count == 0) continue;
            var nearest = candidates.Max(c => Rank(c.Lower.EnvironmentKind));
            var best = candidates.Where(c => Rank(c.Lower.EnvironmentKind) == nearest).ToList();
            var top = best.Max(c => c.Score);
            best = best.Where(c => Math.Abs(c.Score - top) < 1e-9).ToList();
            var lowerEnv = Display(best[0].Lower);
            Add(ctx, PipelineFindingCategory.ArtifactLineageGap, Critical(higher.EnvironmentKind) ? PipelineFindingSeverity.Medium : PipelineFindingSeverity.Low,
                best.Count == 1 ? ArchitectureEvidenceState.StronglySupported : ArchitectureEvidenceState.Inferred, $"rebuild-env|{higher.Id}",
                $"{Display(higher)} rebuilds instead of promoting the {lowerEnv}-tested build (`{higher.PipelineName}`)",
                $"The artifact deployed to {Display(higher)} may not be the one tested in {lowerEnv}: it is built again, from whatever source the {Display(higher)} run checks out.",
                $"{(best.Count == 1 ? $"`{best[0].Lower.PipelineName}`" : $"{string.Join(", ", best.Select(b => $"`{b.Lower.PipelineName}`"))} (equally close)")} and `{higher.PipelineName}` build the same source "
                    + $"({string.Join(", ", best[0].Shared.Take(3).Select(s => $"`{s}`"))}); {Display(higher)} deploys output of `{ctx.Name(hp.ProducerPipeline!)}` stage `{hp.ProducerStage}`. `{higher.PipelineName}` runs {higher.HowReached}.",
                [higher.Pipeline, .. best.Select(b => b.Lower.Pipeline)], Display(higher), [higher.Evidence, .. best.Select(b => b.Lower.Evidence)],
                $"Confirm whether {Display(higher)} should promote the {lowerEnv} artifact (for example a pipeline resource on `{best[0].Lower.PipelineName}`) or deliberately rebuild.");
        }

        // Infrastructure vs application sequence in the same pipeline and environment.
        foreach (var infra in ctx.Deployments.Where(d => d.DeploysWhat.Contains("Infrastructure")))
        {
            var (ie, ijob, ipoint) = ctx.DeploymentUnits[infra.Id];
            foreach (var app in ctx.Deployments.Where(a => a.Id != infra.Id && a.Pipeline == infra.Pipeline && a.DeploysWhat.Contains("Application") && SameEnvironment(a, infra)))
            {
                var (_, ajob, apoint) = ctx.DeploymentUnits[app.Id];
                var infraFirst = ie.Ancestors(ajob.Key).ContainsKey(ijob.Key) || (ajob.Key == ijob.Key && ipoint < apoint);
                var appFirst = ie.Ancestors(ijob.Key).ContainsKey(ajob.Key) || (ajob.Key == ijob.Key && apoint < ipoint);
                if (!infraFirst)
                    Add(ctx, PipelineFindingCategory.InfrastructureSequenceGap, PipelineFindingSeverity.Medium, ArchitectureEvidenceState.Confirmed, $"infra|{infra.Id}|{app.Id}",
                        appFirst ? $"Application deploys before infrastructure in {Display(app)}" : $"Infrastructure and application deployments to {Display(app)} are not ordered",
                        "If the application needs infrastructure changes deployed in the same run, it can start before they exist.",
                        $"`{infra.Stage ?? infra.Job}` (infrastructure) and `{app.Stage ?? app.Job}` (application) in `{infra.PipelineName}`.", [infra.Pipeline], Display(app), [infra.Evidence, app.Evidence],
                        "Confirm the intended infrastructure → application order.");
            }
            if (!ctx.Deployments.Any(a => a.Pipeline == infra.Pipeline && a.DeploysWhat.Contains("Application") && SameEnvironment(a, infra)))
                foreach (var app in ctx.Deployments.Where(a => a.Pipeline != infra.Pipeline && a.DeploysWhat.Contains("Application") && SameEnvironment(a, infra)).Take(1))
                {
                    var linked = ctx.Pipeline(app.Pipeline)!.Definition.Resources.Any(r => ctx.Resources.TryGetValue((app.Pipeline, r.Alias), out var m) && m.Target == infra.Pipeline)
                        || ie.Definition.Resources.Any(r => ctx.Resources.TryGetValue((infra.Pipeline, r.Alias), out var m) && m.Target == app.Pipeline);
                    if (!linked)
                        Add(ctx, PipelineFindingCategory.InfrastructureSequenceGap, PipelineFindingSeverity.Low, ArchitectureEvidenceState.Unresolved, $"infra-separate|{infra.Pipeline}|{app.Pipeline}|{app.EnvironmentKind}",
                            "Infrastructure and application deployment appear separate", "No explicit orchestration dependency between the infrastructure and application pipelines was detected.",
                            $"Infrastructure: `{infra.PipelineName}`; application: `{app.PipelineName}` ({Display(app)}).", [infra.Pipeline, app.Pipeline], Display(app), [infra.Evidence, app.Evidence],
                            "Confirm how infrastructure changes are deployed before the application that needs them.");
                }
            // Terraform plan before apply.
            var plan = ie.Steps.Where(s => s.Step.Kind == PipelineStepKind.InfrastructurePlan && s.Step.Tool.StartsWith("terraform", StringComparison.Ordinal))
                .Any(s => ie.Ancestors(ijob.Key).ContainsKey(s.JobKey) || (s.JobKey == ijob.Key && s.Order < ipoint));
            var terraformApply = ie.StepsOf(ijob.Key).Any(s => s.Step.Tool.StartsWith("terraform apply", StringComparison.Ordinal) || s.Step.Tool == "terraform apply");
            if (terraformApply && !plan)
                Add(ctx, PipelineFindingCategory.InfrastructureSequenceGap, PipelineFindingSeverity.Low, ArchitectureEvidenceState.Confirmed, $"tfplan|{infra.Id}",
                    $"terraform apply in {Display(infra)} without a preceding plan", "Without a reviewed plan, apply computes and applies changes in one step.",
                    $"`{infra.Stage ?? infra.Job}` in `{infra.PipelineName}`.", [infra.Pipeline], Display(infra), [infra.Evidence], "Confirm whether a plan step (and its review) should precede apply.");
        }

        // Templates.
        foreach (var u in unresolved.DistinctBy(u => (u.Pipeline, u.Template)))
            Add(ctx, PipelineFindingCategory.TemplateResolutionGap, PipelineFindingSeverity.Info, ArchitectureEvidenceState.Unresolved, $"template|{u.Pipeline}|{u.Template}",
                $"Template `{u.Template}` is not analyzed", "What the template runs (tests, deployments) is not visible; findings that depend on it are reported as not assessable instead.",
                u.Reason, [u.Pipeline], null, [u.Evidence], "Analyze the template's repository in Source Analysis, or confirm what it contains.");
        var used = ctx.Pipelines.SelectMany(p => p.TemplatesUsed).ToHashSet(StringComparer.Ordinal);
        foreach (var t in cicd.Pipelines.Where(p => p.IsTemplate && !used.Contains(p.File) && !cicd.Pipelines.Any(o => o.TemplateUses.Any(u => u.ResolvedPath == p.File))))
            Add(ctx, PipelineFindingCategory.TemplateResolutionGap, PipelineFindingSeverity.Info, ArchitectureEvidenceState.Inferred, $"unreferenced|{t.File}", $"Unreferenced pipeline template `{t.File}`",
                "No pipeline in this snapshot includes it. It may be used by pipelines in other repositories.", "No template use in this snapshot resolves to this file.", [t.Id], null,
                [new PipelineEvidenceRef { File = t.File, Pipeline = t.Id }], "Confirm whether the template is still used.");

        // Cross-pipeline resources pointing nowhere.
        foreach (var ((pipeline, alias), (target, state, basis)) in ctx.Resources.Where(r => r.Value.Target is null))
            Add(ctx, PipelineFindingCategory.CrossPipelineDependencyGap, PipelineFindingSeverity.Info, ArchitectureEvidenceState.Unresolved, $"resource|{pipeline}|{alias}",
                $"Pipeline resource `{alias}` could not be matched", "The upstream pipeline's tests and artifacts cannot be followed.", basis, [pipeline], null,
                [new PipelineEvidenceRef { File = ctx.Pipeline(pipeline)!.Definition.File, Pipeline = pipeline, Line = ctx.Pipeline(pipeline)!.Definition.Resources.First(r => r.Alias == alias).Line }],
                "Analyze the upstream repository, or connect Azure DevOps metadata so pipeline names can be mapped.");

        // Security and dependency scans that do not gate delivery.
        var scans = tests.Where(t => t.Category is ValidationCategory.Security or ValidationCategory.DependencyScan).ToList();
        if (ctx.Deployments.Count > 0)
        {
            var gatingScan = ctx.Deployments.SelectMany(d => d.Before).Any(g => g.Category is ValidationCategory.Security or ValidationCategory.DependencyScan && g.State is GateState.Gates or GateState.Inherited);
            if (scans.Count == 0)
                Add(ctx, PipelineFindingCategory.SecurityValidationGap, PipelineFindingSeverity.Low, ArchitectureEvidenceState.Confirmed, "noscan", "No security or dependency scan detected in delivery pipelines",
                    "Known-vulnerable dependencies or insecure configuration are not checked automatically before deployment. Scanning may happen elsewhere (repository security features, a separate system).",
                    "No security-scan or dependency-scan step was found in any pipeline of this snapshot.", ctx.Pipelines.Select(p => p.Id), null, [], "Confirm where security and dependency scanning happens.");
            else if (!gatingScan)
                Add(ctx, PipelineFindingCategory.SecurityValidationGap, PipelineFindingSeverity.Low, ArchitectureEvidenceState.Confirmed, "scan-notgating", "Security scans do not appear to gate normal delivery",
                    "The scans run, but not on the dependency path of any deployment (scheduled, conditional, soft or in a separate pipeline).",
                    string.Join(" ", scans.Select(s => $"`{s.Name}` in `{s.PipelineName}`: {s.WhenItRuns}{(s.ContinueOnError ? " (continueOnError)" : "")}.").Distinct().Take(4)),
                    scans.Select(s => s.Pipeline), null, scans.Select(s => s.Evidence), "Confirm whether a scan result should be able to stop a deployment.");
        }

        // Trigger coverage: shared libraries, contracts and infrastructure folders excluded by relevant pipelines' path filters.
        var areas = Areas(ctx);
        var arch = ctx.Snapshot.Architecture;
        foreach (var library in areas.Where(a => a.Kind == "Shared library"))
        {
            var consumers = arch?.SharedLibraries.FirstOrDefault(l => l.Id == library.ComponentId)?.ReferencedByComponents ?? [];
            foreach (var componentId in consumers)
            {
                if (areas.FirstOrDefault(a => a.ComponentId == componentId) is not { } component) continue;
                foreach (var e in ctx.Pipelines.Where(e => PipelinePathFilters.HasPathFilters(e.Definition) && Relevant(ctx, e, component)))
                    if (Effective(ctx, e, library.Path).Coverage == TriggerCoverage.No)
                        Add(ctx, PipelineFindingCategory.TriggerCoverageGap, PipelineFindingSeverity.Medium, ArchitectureEvidenceState.StronglySupported, $"lib|{library.Path}|{e.Id}",
                            $"Changes to shared library {library.Name} do not start `{ctx.Name(e.Id)}`",
                            $"{component.Name} uses {library.Name}, but the path filters of the pipeline that validates {component.Name} exclude the library: a library change can reach {component.Name} without that validation.",
                            $"{library.Name} (`{library.Path}`) is referenced by {component.Name} (`{component.Path}`). `{ctx.Name(e.Id)}`: {PipelinePathFilters.Pipeline(e.Definition, library.Path).Basis}.",
                            [e.Id], null, [new PipelineEvidenceRef { File = e.Definition.File, Pipeline = e.Id, Line = e.Definition.Triggers.FirstOrDefault(PipelinePathFilters.ChangeTriggered)?.Line ?? 0, Note = "trigger paths" }],
                            $"Review the path filters of `{ctx.Name(e.Id)}` (add `{library.Path}`).");
            }
        }
        foreach (var (consumer, producerFile, contractName, basis, state, evidence) in ContractConsumers(ctx))
        {
            var contractPath = PipelinePathFilters.Relative(producerFile, ctx.Root);
            if (areas.FirstOrDefault(a => a.Kind == "Component" && a.Name.Equals(consumer, StringComparison.OrdinalIgnoreCase)) is not { } component) continue;
            if (contractPath.StartsWith(component.Path + "/", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var e in ctx.Pipelines.Where(e => PipelinePathFilters.HasPathFilters(e.Definition) && Relevant(ctx, e, component)))
                if (Effective(ctx, e, contractPath).Coverage == TriggerCoverage.No && !e.Definition.Triggers.Where(PipelinePathFilters.ChangeTriggered).Any(t => PipelinePathFilters.Starts(t, contractPath)))
                    Add(ctx, PipelineFindingCategory.ContractValidationGap, PipelineFindingSeverity.Medium, state, $"contract|{producerFile}|{consumer}|{e.Id}",
                        $"A change to contract {contractName} may not start validation of consumer {component.Name}",
                        "The consumer's pipeline does not start when the contract it uses changes, so an incompatible contract change may not be built or tested against the consumer.",
                        $"`{producerFile}` is consumed by {component.Name} ({basis}). `{ctx.Name(e.Id)}`: {PipelinePathFilters.Pipeline(e.Definition, contractPath[..Math.Max(0, contractPath.LastIndexOf('/'))]).Basis}.",
                        [e.Id], null, [evidence, new PipelineEvidenceRef { File = e.Definition.File, Pipeline = e.Id, Line = e.Definition.Triggers.FirstOrDefault(PipelinePathFilters.ChangeTriggered)?.Line ?? 0, Note = "trigger paths" }],
                        $"Review whether `{ctx.Name(e.Id)}` should start on changes to `{contractPath}`.");
        }
        foreach (var iac in areas.Where(a => a.Kind == "Infrastructure"))
        {
            var deploying = ctx.Pipelines.Where(e => e.Steps.Any(s => s.Step.Kind is PipelineStepKind.InfrastructureDeploy or PipelineStepKind.InfrastructurePlan)
                && (Relevant(ctx, e, iac) || e.Steps.Any(s => s.Step.Kind is PipelineStepKind.InfrastructureDeploy or PipelineStepKind.InfrastructurePlan && s.Step.Targets.Count == 0))).ToList();
            if (deploying.Count == 0)
                Add(ctx, PipelineFindingCategory.TriggerCoverageGap, PipelineFindingSeverity.Info, ArchitectureEvidenceState.Unresolved, $"iac-none|{iac.Path}",
                    $"No pipeline in this snapshot plans or deploys {iac.Name}", "Infrastructure declared here may be deployed by another repository's pipeline, or manually.",
                    $"No infrastructure step targets `{iac.Path}`.", [], null, [], "Confirm which pipeline deploys this infrastructure.");
            foreach (var e in deploying.Where(e => PipelinePathFilters.HasPathFilters(e.Definition) && Effective(ctx, e, iac.Path).Coverage == TriggerCoverage.No))
                Add(ctx, PipelineFindingCategory.TriggerCoverageGap, PipelineFindingSeverity.Medium, ArchitectureEvidenceState.Confirmed, $"iac|{iac.Path}|{e.Id}",
                    $"Infrastructure changes under `{iac.Path}` do not start `{ctx.Name(e.Id)}`", "The pipeline that deploys this infrastructure does not run when it changes.",
                    PipelinePathFilters.Pipeline(e.Definition, iac.Path).Basis, [e.Id], null, [new PipelineEvidenceRef { File = e.Definition.File, Pipeline = e.Id, Note = "trigger paths" }],
                    $"Review the path filters of `{ctx.Name(e.Id)}`.");
        }
        foreach (var component in areas.Where(a => a.Kind == "Component"))
        {
            var row = coverage.FirstOrDefault(r => r.Path == component.Path && r.AreaKind == "Component");
            if (row is not null && row.Pipelines.Count > 0 && row.Pipelines.All(c => c.Triggers == TriggerCoverage.No) && ctx.Pipelines.Any(p => PipelinePathFilters.HasPathFilters(p.Definition)))
                Add(ctx, PipelineFindingCategory.TriggerCoverageGap, PipelineFindingSeverity.Medium, ArchitectureEvidenceState.Confirmed, $"component|{component.Path}",
                    $"Changes to {component.Name} start no pipeline", "A change to this component is not built or tested automatically.",
                    string.Join(" ", row.Pipelines.Select(c => $"`{ctx.Name(c.Pipeline)}`: {c.Basis}.")), row.Pipelines.Select(c => c.Pipeline), null, [], $"Review path filters for `{component.Path}`.");
        }

        // Several pipelines deploying one environment with different tools.
        foreach (var group in ctx.Deployments.Where(d => d.EnvironmentKind is not (SourceEnvironmentKind.Custom or SourceEnvironmentKind.Default)).GroupBy(d => d.Environment, StringComparer.OrdinalIgnoreCase))
        {
            var byPipeline = group.GroupBy(d => d.Pipeline).ToList();
            if (byPipeline.Count < 2) continue;
            var tools = byPipeline.Select(g => string.Join("+", g.SelectMany(d => ctx.DeploymentUnits[d.Id].Pipeline.StepsOf(ctx.DeploymentUnits[d.Id].Job.Key)).Where(s => DeployKinds.Contains(s.Step.Kind)).Select(s => s.Step.Tool).Distinct().Order())).Distinct().ToList();
            if (tools.Count > 1 && group.All(d => d.DeploysWhat.SequenceEqual(group.First().DeploysWhat)))
                Add(ctx, PipelineFindingCategory.EnvironmentProgressionGap, PipelineFindingSeverity.Low, ArchitectureEvidenceState.Inferred, $"conflict|{group.Key}",
                    $"Several pipelines deploy to {group.Key} differently", "Different deployment mechanics for one environment can make results differ between pipelines.",
                    string.Join("; ", byPipeline.Select(g => $"`{ctx.Name(g.Key)}`")) + $": {string.Join(" / ", tools)}.", byPipeline.Select(g => g.Key), group.Key, group.Select(d => d.Evidence), "Confirm both deployment paths are intended.");
        }
    }

    /// <summary>Consumer component → the producer contract file it uses: an OpenApiReference/generator config resolved to a contract file in the
    /// snapshot (Strongly supported), or a client-side copy matched to a producer contract of the same name (Inferred). Source Analysis owns both.</summary>
    internal static IEnumerable<(string Consumer, string ProducerFile, string Name, string Basis, ArchitectureEvidenceState State, PipelineEvidenceRef Evidence)> ContractConsumers(Context ctx)
    {
        var contracts = ctx.Snapshot.EvidenceDomains?.Contracts.Contracts ?? [];
        var producers = contracts.Where(c => c.Type != SourceContractType.GeneratedClient && c.ConsumerHints.Count == 0).ToList();
        foreach (var client in contracts.Where(c => c.ConsumerHints.Count > 0 && !c.ConsumerHints.Contains("Developer contract-test snapshot")))
        {
            var evidence = new PipelineEvidenceRef { File = client.File, Line = client.Line, Pipeline = "", Note = client.ProducerBasis };
            if (client.Type == SourceContractType.GeneratedClient)
            {
                var include = client.Id.Contains('#') ? client.Id[(client.Id.IndexOf('#') + 1)..] : null;
                if (include is null) continue;
                var dir = client.File.Contains('/') ? client.File[..client.File.LastIndexOf('/')] : "";
                var resolved = SourceArchitecture.ArchitectureInput.Normalize($"{dir}/{include.Replace('\\', '/')}");
                var producer = producers.FirstOrDefault(p => p.File.Equals(resolved, StringComparison.OrdinalIgnoreCase))
                    ?? producers.FirstOrDefault(p => p.File.EndsWith("/" + Path.GetFileName(resolved), StringComparison.OrdinalIgnoreCase) && producers.Count(x => x.File.EndsWith("/" + Path.GetFileName(resolved), StringComparison.OrdinalIgnoreCase)) == 1);
                var target = producer?.File ?? (resolved.StartsWith("..", StringComparison.Ordinal) ? null : resolved);
                if (target is null) continue;
                foreach (var consumer in client.ConsumerHints)
                    yield return (consumer, target, producer?.Name ?? Path.GetFileName(target), $"{client.ProducerBasis}; references `{include}`", ArchitectureEvidenceState.StronglySupported, evidence);
                continue;
            }
            foreach (var producer in producers.Where(p => p.Type == client.Type && p.Name.Equals(client.Name, StringComparison.OrdinalIgnoreCase) && p.File != client.File).Take(1))
                foreach (var consumer in client.ConsumerHints)
                    yield return (consumer, producer.File, producer.Name, $"client-side copy `{client.File}` has the same contract name", ArchitectureEvidenceState.Inferred, evidence);
        }
    }

    // ── Delivery path, graph, limitations ───────────────────────────────────────────────────────────────────────────

    /// <summary>Source folders the build steps on a deployment's path build (projects, working directories, Docker contexts).</summary>
    internal static HashSet<string> BuildTargets(Context ctx, DeploymentReview d)
    {
        var (e, job, point) = ctx.DeploymentUnits[d.Id];
        var ancestors = e.Ancestors(job.Key);
        return e.Steps.Where(s => s.Step.Kind is PipelineStepKind.Build or PipelineStepKind.ContainerBuild or PipelineStepKind.Publish && (ancestors.ContainsKey(s.JobKey) || (s.JobKey == job.Key && s.Order < point)))
            .SelectMany(s => s.Step.Targets.Concat(s.Step.WorkingDirectory is { } w ? [w] : []))
            .Select(t => PipelinePathFilters.Normalize(t)).Where(t => t.Length > 1 && !t.Contains("$(") && !t.Contains("${{") && t != ".")
            .Select(t => t.Contains('/') && System.Text.RegularExpressions.Regex.IsMatch(t, @"(\.(csproj|fsproj|sln|slnx|dll|json|ya?ml)|/Dockerfile[^/]*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? t[..t.LastIndexOf('/')] : t).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> DeliveryPath(Context ctx, List<ReviewedPipeline> pipelines, List<EnvironmentProgressionStep> progression, List<EnvironmentSummary> environments)
    {
        var path = new List<string>();
        static IEnumerable<string> Group(List<ReviewedPipeline> list, string label) => list.Count <= 2 ? list.Select(p => p.Name) : [$"{list.Count} {label}"];
        path.AddRange(Group(pipelines.Where(p => p.Role == "Pull-request validation").ToList(), "pull-request pipelines"));
        path.AddRange(Group(pipelines.Where(p => p.Role.StartsWith("Continuous integration", StringComparison.Ordinal) && (p.Artifacts.Count > 0 || ctx.Pipeline(p.Id)!.Steps.Any(s => s.Step.Kind is PipelineStepKind.Build or PipelineStepKind.ContainerBuild))).ToList(), "CI pipelines"));
        string? previous = null;
        foreach (var env in environments)
        {
            if (previous is not null && !progression.Any(s => s.From == previous && s.To == env.Environment) && !Reachable(progression, previous, env.Environment)) path.Add("⋯");
            path.Add(env.Environment);
            previous = env.Environment;
        }
        return path;
    }

    private static bool Reachable(List<EnvironmentProgressionStep> steps, string from, string to)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>([from]);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in steps.Where(s => s.From == current).Select(s => s.To).ToList())
            {
                if (next == to) return true;
                if (seen.Add(next)) queue.Enqueue(next);
            }
        }
        return false;
    }

    private static (List<PipelineGraphNode>, List<PipelineGraphEdge>) Graph(Context ctx, List<PipelineDependency> dependencies)
    {
        var nodes = new List<PipelineGraphNode>();
        var edges = new List<PipelineGraphEdge>();
        foreach (var e in ctx.Pipelines)
        {
            nodes.Add(new($"p:{e.Id}", PipelineNodeKind.Pipeline, ctx.Name(e.Id), e.Id, new PipelineEvidenceRef { File = e.Definition.File, Pipeline = e.Id }));
            foreach (var s in e.Stages)
            {
                var id = $"s:{e.Id}|{s.Name}";
                nodes.Add(new(id, PipelineNodeKind.Stage, s.Display ?? s.Name, e.Id, new PipelineEvidenceRef { File = s.File, Line = s.Line, Pipeline = e.Id, Stage = s.Name, Condition = s.Condition, TemplateOrigin = s.Origin }));
                foreach (var dep in e.StageDependencies(s.Name))
                    edges.Add(new($"s:{e.Id}|{dep}", id, PipelineEdgeKind.DependsOn, s.DependsOnDeclared ? ArchitectureEvidenceState.Confirmed : ArchitectureEvidenceState.StronglySupported,
                        s.DependsOnDeclared ? "dependsOn" : "previous stage (no dependsOn key)"));
            }
            foreach (var a in e.Steps.Where(s => s.Step.ArtifactsPublished.Count > 0))
                foreach (var name in a.Step.ArtifactsPublished)
                {
                    var id = $"a:{e.Id}|{name}";
                    if (nodes.All(n => n.Id != id)) nodes.Add(new(id, PipelineNodeKind.Artifact, name, e.Id, Ref(e, a)));
                    edges.Add(new($"s:{e.Id}|{a.Stage}", id, PipelineEdgeKind.Publishes, ArchitectureEvidenceState.Confirmed, a.Step.Name));
                }
        }
        foreach (var d in ctx.Deployments)
        {
            var env = $"e:{d.Environment}";
            if (nodes.All(n => n.Id != env)) nodes.Add(new(env, PipelineNodeKind.Environment, d.Environment, null, null));
            edges.Add(new($"s:{d.Pipeline}|{d.Stage}", env, PipelineEdgeKind.DeploysTo, d.EnvironmentState is ArchitectureEvidenceState.Unresolved ? ArchitectureEvidenceState.Inferred : ArchitectureEvidenceState.Confirmed, d.EnvironmentBasis));
        }
        foreach (var dep in dependencies.Where(d => d.Kind is PipelineEdgeKind.Triggers or PipelineEdgeKind.ConsumesArtifactFrom or PipelineEdgeKind.LikelyFollows))
            edges.Add(new($"p:{dep.FromPipeline}", $"p:{dep.ToPipeline}", dep.Kind, dep.State, dep.Basis));
        return (nodes, edges.DistinctBy(e => (e.FromId, e.ToId, e.Kind)).ToList());
    }

    private static List<string> Limitations(Context ctx, PipelineEvidence cicd, List<UnresolvedTemplate> unresolved)
    {
        var list = new List<string>
        {
            PipelineReviewText.Boundary,
            "Conditions, template expressions and variables are classified as written, never evaluated; a condition depending on runtime variables is treated as possibly false.",
            "Pull-request triggers in YAML apply to GitHub and Bitbucket repositories; Azure Repos starts PR validation from branch policies, which are not in YAML.",
        };
        if (ctx.Metadata.State != "Available") list.Add(PipelineReviewText.ApprovalNotAssessable);
        if (unresolved.Count > 0) list.Add($"{unresolved.Count} template use(s) are not analyzed (external repository or not in the snapshot); behaviour behind them is reported as not assessable.");
        if (ctx.Pipelines.Any(p => p.UnresolvedExpressions)) list.Add("Some stage, job or environment names are built from expressions that cannot be resolved statically.");
        if (ctx.Pipelines.Any(p => !StructureKnown(p))) list.Add("Job dependencies and conditions are read for Azure Pipelines only; GitHub Actions, GitLab and Jenkins pipelines are listed with their tests but their ordering is not assessed.");
        if (ctx.Deployments.Any(d => d.Artifacts.Any(a => a.State == ArchitectureEvidenceState.Unresolved))) list.Add("Container image tags and build numbers are not statically derivable; artifact lineage that depends on them is unresolved.");
        list.Add("Service connections are listed by name only; their permissions and health are not visible in source.");
        list.AddRange(cicd.Limitations.Where(l => !list.Contains(l)));
        return list;
    }
}
