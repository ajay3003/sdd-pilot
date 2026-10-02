using System.Text.RegularExpressions;
using BirkNext.PipelineReview;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.PipelineReview;

internal sealed record EStage(string Name, string? Display, List<string> DependsOn, bool DependsOnDeclared, string? Condition, double Order, string File, int Line, string? Origin);

internal sealed record EJob(string Key, string? Stage, string Name, string? Display, List<string> DependsOn, bool DependsOnDeclared, string? Condition, bool Deployment,
    string? Environment, string? Strategy, bool ContinueOnError, double Order, string File, int Line, string? Origin);

internal sealed record EStep(PipelineStep Step, string? Stage, string JobKey, double Order, string File, string? Origin);

/// <summary>
/// A runnable pipeline as Azure Pipelines would expand it: its own stages/jobs/steps plus those of the LOCAL templates it includes (resolved by
/// Source Analysis), with literal template parameters substituted and every item keeping its origin file. Built only from CI/CD evidence —
/// no YAML is read here. Templates that are external or not in the snapshot stay opaque and are recorded, so nothing behind them is called absent.
/// </summary>
internal sealed class EffectivePipeline
{
    private const int MaxDepth = 6;
    private static readonly Regex ParameterReference = new(@"\$\{\{\s*parameters\.([A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    public PipelineDefinition Definition { get; }
    public List<EStage> Stages { get; } = [];
    public List<EJob> Jobs { get; } = [];
    public List<EStep> Steps { get; } = [];
    public List<UnresolvedTemplate> Unresolved { get; } = [];
    public HashSet<string> OpaqueJobs { get; } = new(StringComparer.Ordinal);
    public HashSet<string> OpaqueStages { get; } = new(StringComparer.Ordinal);
    /// <summary>The whole body comes from an unresolved extends/stages template.</summary>
    public bool OpaqueRoot { get; private set; }
    public HashSet<string> TemplatesUsed { get; } = new(StringComparer.Ordinal);
    public bool UnresolvedExpressions { get; private set; }

    public string Id => Definition.Id;
    public string Name => Definition.Name;

    public EffectivePipeline(PipelineDefinition definition, IReadOnlyDictionary<string, PipelineDefinition> templates)
    {
        Definition = definition;
        Add(definition, null, null, [], null, 0, 0, templates);
        Normalize();
    }

    public static string JobKey(string? stage, string job) => $"{stage ?? ""}|{job}";

    private string Sub(string? value, Dictionary<string, string> parameters)
    {
        if (value is null) return "";
        var result = ParameterReference.Replace(value, m => parameters.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
        if (result.Contains("${{", StringComparison.Ordinal) || result.Contains("$(", StringComparison.Ordinal)) UnresolvedExpressions = true;
        return result;
    }

    private string? SubN(string? value, Dictionary<string, string> parameters) => value is null ? null : Sub(value, parameters);

    private void Add(PipelineDefinition def, string? outerStage, string? outerJob, Dictionary<string, string> parameters, string? origin, double baseOrder, int depth,
        IReadOnlyDictionary<string, PipelineDefinition> templates)
    {
        double O(double inner) => depth == 0 ? inner : baseOrder + (inner + 1) / Math.Pow(100, depth);
        foreach (var s in def.Stages)
            Stages.Add(new EStage(Sub(s.Name, parameters), SubN(s.DisplayName, parameters), s.DependsOn.Select(d => Sub(d, parameters)).ToList(), s.DependsOnDeclared,
                SubN(s.Condition, parameters), O(s.Order), def.File, s.Line, origin));
        foreach (var j in def.JobDetails)
        {
            var stage = j.Stage is null ? outerStage : Sub(j.Stage, parameters);
            var name = Sub(j.Name, parameters);
            Jobs.Add(new EJob(JobKey(stage, name), stage, name, SubN(j.DisplayName, parameters), j.DependsOn.Select(d => Sub(d, parameters)).ToList(), j.DependsOnDeclared,
                SubN(j.Condition, parameters), j.Deployment, SubN(j.Environment, parameters), j.Strategy, j.ContinueOnError, O(j.Order), def.File, j.Line, origin));
        }
        foreach (var step in def.Steps.Where(s => s.Kind != PipelineStepKind.Template))
        {
            var stage = step.Stage is null ? outerStage : Sub(step.Stage, parameters);
            var job = step.Job is null ? outerJob ?? "(job)" : Sub(step.Job, parameters);
            var substituted = step with
            {
                Name = Sub(step.Name, parameters), Environment = SubN(step.Environment, parameters), Condition = SubN(step.Condition, parameters),
                Targets = step.Targets.Select(t => Sub(t, parameters)).ToList(), ArtifactsPublished = step.ArtifactsPublished.Select(a => Sub(a, parameters)).ToList(),
                ArtifactsConsumed = step.ArtifactsConsumed.Select(a => a with { Artifact = Sub(a.Artifact, parameters), Source = Sub(a.Source, parameters) }).ToList(),
            };
            // Steps of a steps-template are placed at the template's position in the including job.
            var order = step.Job is null && outerJob is not null ? O(step.Order) : depth == 0 ? step.Order : O(step.Order);
            Steps.Add(new EStep(substituted, stage, JobKey(stage, job), order, def.File, origin));
        }
        foreach (var use in def.TemplateUses.Where(u => u.Level != "variables"))
        {
            var stage = use.Stage is null ? outerStage : Sub(use.Stage, parameters);
            var job = use.Job is null ? outerJob : Sub(use.Job, parameters);
            // A template sees only its own parameters: its declared defaults, overridden by the literal values the use passes.
            var merged = new Dictionary<string, string>(StringComparer.Ordinal);
            if (use.ResolvedPath is not null && templates.TryGetValue(use.ResolvedPath, out var declaring)) foreach (var (k, v) in declaring.ParameterDefaults) merged[k] = v;
            foreach (var (k, v) in use.Parameters) merged[k] = Sub(v, parameters);
            var evidence = new PipelineEvidenceRef { File = def.File, Line = use.Line, Pipeline = Definition.Id, Stage = stage, Job = job, TemplateOrigin = origin };
            var key = use.ResolvedPath ?? use.Template;
            if (use.ResolvedPath is null || !templates.TryGetValue(use.ResolvedPath, out var template) || depth >= MaxDepth)
            {
                var reason = use.RepositoryAlias is { } alias && !alias.Equals("self", StringComparison.OrdinalIgnoreCase)
                    ? $"External template from repository resource '{alias}' — not analyzed (add a Source Analysis snapshot of that repository)."
                    : use.ResolvedPath is null ? "The template path is not in this snapshot or is built from an expression."
                    : depth >= MaxDepth ? "Template nesting deeper than six levels was not expanded." : "The file is not recognized as a pipeline template.";
                Unresolved.Add(new UnresolvedTemplate(Definition.Id, use.Template, use.Level, stage, job, reason, evidence));
                switch (use.Level)
                {
                    case "steps": OpaqueJobs.Add(JobKey(stage, job ?? "(job)")); break;
                    case "jobs": OpaqueStages.Add(stage ?? ""); break;
                    default: OpaqueRoot = true; break;
                }
                continue;
            }
            TemplatesUsed.Add(key);
            var order = depth == 0 ? use.Order : O(use.Order);
            Add(template, use.Level is "jobs" or "steps" ? stage : null, use.Level == "steps" ? job ?? "(job)" : null, merged, template.File, order, depth + 1, templates);
        }
    }

    /// <summary>Order stages, synthesize jobs for steps that name no declared job, and implicit stages for jobs of unnamed stages.</summary>
    private void Normalize()
    {
        Stages.Sort((a, b) => a.Order.CompareTo(b.Order));
        var stageNames = Stages.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var step in Steps)
            if (Jobs.All(j => j.Key != step.JobKey))
            {
                var name = step.JobKey[(step.JobKey.IndexOf('|') + 1)..];
                Jobs.Add(new EJob(step.JobKey, step.Stage, name, null, [], false, null, false, step.Step.Environment, null, false, step.Order, step.File, step.Step.Line, step.Origin));
            }
        foreach (var job in Jobs.Where(j => j.Stage is not null && !stageNames.Contains(j.Stage)).ToList())
        {
            Stages.Add(new EStage(job.Stage!, null, [], false, null, job.Order, job.File, job.Line, job.Origin));
            stageNames.Add(job.Stage!);
        }
        Stages.Sort((a, b) => a.Order.CompareTo(b.Order));
        Steps.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    // ── Dependency structure (Azure semantics) ──────────────────────────────────────────────────────────────────────

    /// <summary>A stage without dependsOn runs after the previous stage; "dependsOn: []" runs first/in parallel.</summary>
    public IReadOnlyList<string> StageDependencies(string? stage)
    {
        if (stage is null) return [];
        var index = Stages.FindIndex(s => s.Name == stage);
        if (index < 0) return [];
        var s = Stages[index];
        return s.DependsOnDeclared ? s.DependsOn : index > 0 ? [Stages[index - 1].Name] : [];
    }

    public EStage? Stage(string? name) => name is null ? null : Stages.FirstOrDefault(s => s.Name == name);
    public EJob? Job(string key) => Jobs.FirstOrDefault(j => j.Key == key);
    public IEnumerable<EStep> StepsOf(string jobKey) => Steps.Where(s => s.JobKey == jobKey);

    /// <summary>Every job that must finish before <paramref name="jobKey"/> starts, with whether a failure there stops it (all links on some path
    /// block) — the transitive closure over job dependencies in the stage and stage dependencies.</summary>
    public Dictionary<string, bool> Ancestors(string jobKey)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        var queue = new Queue<(string Key, bool Blocking)>();
        queue.Enqueue((jobKey, true));
        var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
        while (queue.Count > 0)
        {
            var (key, blocking) = queue.Dequeue();
            if (seen.TryGetValue(key, out var was) && (was || !blocking)) continue;
            seen[key] = blocking;
            if (Job(key) is not { } job) continue;
            var jobBlocks = !PipelineConditions.Tolerant(job.Condition);
            foreach (var dep in job.DependsOn)
            {
                var depKey = JobKey(job.Stage, dep);
                var b = blocking && jobBlocks;
                if (!result.TryGetValue(depKey, out var existing) || (!existing && b)) result[depKey] = b;
                queue.Enqueue((depKey, b));
            }
            var stageBlocks = !PipelineConditions.Tolerant(Stage(job.Stage)?.Condition);
            foreach (var depStage in StageDependencies(job.Stage))
                foreach (var depJob in Jobs.Where(j => j.Stage == depStage))
                {
                    var b = blocking && stageBlocks;
                    if (!result.TryGetValue(depJob.Key, out var existing) || (!existing && b)) result[depJob.Key] = b;
                    queue.Enqueue((depJob.Key, b));
                }
        }
        result.Remove(jobKey);
        return result;
    }

    public bool Opaque(string jobKey) => OpaqueRoot || OpaqueJobs.Contains(jobKey) || (Job(jobKey)?.Stage is { } s && OpaqueStages.Contains(s));

    /// <summary>Stages that must finish before <paramref name="stage"/> (transitive).</summary>
    public HashSet<string> AncestorStages(string? stage)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(StageDependencies(stage));
        while (queue.Count > 0)
        {
            var s = queue.Dequeue();
            if (!result.Add(s)) continue;
            foreach (var d in StageDependencies(s)) queue.Enqueue(d);
        }
        return result;
    }

    /// <summary>The unresolved template that hides part of the path to a job: on the job, an ancestor job, or an ancestor stage (whose jobs it defines).</summary>
    public UnresolvedTemplate? HiddenOnPath(string jobKey)
    {
        if (OpaqueRoot) return Unresolved.FirstOrDefault(u => u.Level is "extends" or "stages");
        var job = Job(jobKey);
        var stages = AncestorStages(job?.Stage);
        if (job?.Stage is { } own) stages.Add(own);
        var jobs = Ancestors(jobKey).Keys.Append(jobKey).ToHashSet(StringComparer.Ordinal);
        return Unresolved.FirstOrDefault(u => u.Level == "jobs" && stages.Contains(u.Stage ?? ""))
            ?? Unresolved.FirstOrDefault(u => u.Level == "steps" && jobs.Contains(JobKey(u.Stage, u.Job ?? "(job)")));
    }
}

/// <summary>Condition semantics as Azure Pipelines documents them; conditions are classified as written, never evaluated.</summary>
internal static class PipelineConditions
{
    /// <summary>True when the item can run although a dependency failed: always(), succeededOrFailed(), failed(), canceled(), or a custom condition
    /// that does not include succeeded() (a custom condition replaces the default succeeded()).</summary>
    public static bool Tolerant(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) return false;
        var c = condition.Replace(" ", "").ToLowerInvariant();
        if (c.Contains("always()") || c.Contains("succeededorfailed(")) return true;
        if (c.Contains("succeeded(") || c.Contains("not(failed())") || c.Contains("not(canceled())") && c.Contains("succeeded")) return false;
        if (c.Contains("in(dependencies.") && (c.Contains("'failed'") || c.Contains("'skipped'") || c.Contains("'succeededwithissues'"))) return true;
        return true;
    }

    /// <summary>True when the condition can make the item NOT run in normal delivery (anything beyond the status functions).</summary>
    public static bool Restricts(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) return false;
        var c = condition.Replace(" ", "").ToLowerInvariant();
        var stripped = Regex.Replace(c, @"(succeeded|always|succeededorfailed|failed|canceled)\(\)", "").Replace("and(", "").Replace("not(", "").Replace(",", "").Replace(")", "");
        return stripped.Length > 0;
    }

    /// <summary>True when a custom condition drops the default success check (Azure's documented behaviour) — not always()/failed() written on purpose.</summary>
    public static bool ReplacesSuccessCheck(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition)) return false;
        var c = condition.Replace(" ", "").ToLowerInvariant();
        return !Regex.IsMatch(c, @"(succeeded|always|succeededorfailed|failed|canceled)\(") && !c.Contains("dependencies.");
    }
}
