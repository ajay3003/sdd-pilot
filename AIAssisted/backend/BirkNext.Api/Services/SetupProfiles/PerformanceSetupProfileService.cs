using System.Globalization;
using System.Text.Json;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.Integrations;
using BirkNext.PerformanceTests;
using BirkNext.TestData;

namespace BirkNext.Api.Services.SetupProfiles;

/// <summary>The Performance Test Review's current Target Environment, sent with a profile request (the target is never taken from a profile).</summary>
public sealed record PerformanceSetupTarget(string TargetOrigin, string EnvironmentType, string EnvironmentName);

/// <summary>
/// Setup profiles for Performance Test Review: the same parser, schema and safety rules as test-pack profiles (secrets, identities, trust and
/// evidence are rejected), describing DESIRED state only — scenario, workload, thresholds, resource-observation preference and the target
/// reference. Readiness, network/TLS results, run results, baseline promotion and execution approval are evidence and are never imported or
/// exported. Applying saves a definition version through the normal store (earlier runs keep their version); it never runs anything.
/// </summary>
public sealed class PerformanceSetupProfileService(PerformanceTestStore store, PerformanceTestOptions options, ILogger<PerformanceSetupProfileService> logger, TimeProvider? clock = null)
{
    private const string Owner = "Performance Test Review (definition)";
    public static readonly IReadOnlyList<string> AlwaysManual = ["executionApproval", "containerNetworkCheck"];

    public static readonly IReadOnlyList<string> ExportExcluded =
        ["Readiness results and container network/TLS checks", "Run results, metrics and threshold outcomes", "Baseline promotion", "Execution approval",
         "Runtime credentials and authentication secrets", "CA certificate content (only whether a corporate CA bundle is required)"];

    private static readonly Dictionary<string, PerformanceMetric> Metrics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["p50Latency"] = PerformanceMetric.LatencyP50Ms, ["p90Latency"] = PerformanceMetric.LatencyP90Ms, ["p95Latency"] = PerformanceMetric.LatencyP95Ms,
        ["p99Latency"] = PerformanceMetric.LatencyP99Ms, ["meanLatency"] = PerformanceMetric.LatencyMeanMs, ["maxLatency"] = PerformanceMetric.LatencyMaxMs,
        ["errorRate"] = PerformanceMetric.ErrorRatePercent, ["throughput"] = PerformanceMetric.ThroughputRps, ["failedRequests"] = PerformanceMetric.FailedRequests,
    };

    private static readonly Dictionary<string, ThresholdOperator> Operators = new(StringComparer.Ordinal)
    {
        ["<"] = ThresholdOperator.LessThan, ["<="] = ThresholdOperator.LessThanOrEqual, [">"] = ThresholdOperator.GreaterThan, [">="] = ThresholdOperator.GreaterThanOrEqual,
    };

    private static readonly Dictionary<string, PerformanceTargetType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rest-http"] = PerformanceTargetType.RestHttp, ["graphql-http"] = PerformanceTargetType.GraphQlHttp, ["generic-http"] = PerformanceTargetType.GenericHttp,
    };

    private sealed record Plan(SetupProfilePreview Preview, PerformanceTestDefinition? Candidate);

    public async Task<SetupProfilePreview> PreviewAsync(string environmentId, SetupProfileRequest request, PerformanceSetupTarget target, CancellationToken ct) =>
        (await PlanAsync(environmentId, request.FileName, request.Content, target, ct)).Preview;

    public async Task<SetupApplyResult> ApplyAsync(string environmentId, SetupApplyRequest request, PerformanceSetupTarget target, CancellationToken ct)
    {
        var plan = await PlanAsync(environmentId, request.FileName, request.Content, target, ct);
        if (!plan.Preview.Valid) return new SetupApplyResult { Issues = plan.Preview.Issues };
        var results = new List<SetupApplyItemResult>();
        foreach (var change in plan.Preview.Changes)
        {
            if (!request.SelectedChangeIds.Contains(change.Id))
            {
                if (change.Classification == SetupChangeClassification.Same) results.Add(new(change.Id, change.Setting, SetupApplyOutcome.AlreadyMatched, "Already configured."));
                continue;
            }
            if (change.Id != "performance.definition" || plan.Candidate is null || !change.SafeToApply
                || change.RequiresConfirmation && !(request.ConfirmedChangeIds ?? []).Contains(change.Id))
            {
                results.Add(new(change.Id, change.Setting, change.Classification == SetupChangeClassification.Same ? SetupApplyOutcome.AlreadyMatched : SetupApplyOutcome.Refused,
                    change.RequiresConfirmation ? "Requires explicit confirmation; nothing was changed." : change.Reason));
                continue;
            }
            var saved = await store.SaveDefinitionAsync(environmentId, plan.Candidate, (clock ?? TimeProvider.System).GetUtcNow(), ct);
            results.Add(new(change.Id, change.Setting, SetupApplyOutcome.Applied, $"Saved {saved.Name} as v{saved.Version}. Nothing was run; check readiness next."));
        }
        logger.LogInformation("Performance setup profile {File} applied to {Environment}: {Applied} applied, {Rejected} rejected field(s).",
            System.IO.Path.GetFileName(request.FileName), environmentId, results.Count(r => r.Outcome == SetupApplyOutcome.Applied), plan.Preview.Summary.Rejected);
        return new SetupApplyResult { Items = results, ManualRemaining = plan.Preview.ManualRequirements.Count, Issues = plan.Preview.Issues };
    }

    private async Task<Plan> PlanAsync(string environmentId, string fileName, string content, PerformanceSetupTarget target, CancellationToken ct)
    {
        var (root, format, issues) = SetupProfileParser.Parse(fileName, content ?? "");
        if (root is not null) issues.AddRange(SetupProfileSchema.Validate(root));
        var performance = root?["performance"];
        if (root is not null && performance is not { IsMap: true })
            issues.Add(new("performance", SetupIssueSeverity.Error, "The profile has no \"performance\" section (scenario, workload, thresholds)."));
        var basic = new SetupProfilePreview
        {
            Format = format, ProfileName = root?["profile"]?.Str("name"), TestPack = root?["profile"]?.Str("testPack"),
            Version = int.TryParse(root?.Str("version"), out var version) ? version : null, Issues = issues,
        };
        if (root is null || performance is not { IsMap: true } || issues.Any(i => i.Severity == SetupIssueSeverity.Error))
            return new Plan(basic with { Valid = false, Summary = Summarize([], issues, 0) }, null);

        var candidate = Candidate(environmentId, performance, issues, target);
        var production = PerformanceTestSafety.EnvironmentBlock(target.EnvironmentType) is not null && target.EnvironmentType.Trim().Equals("Production", StringComparison.OrdinalIgnoreCase);
        var targetRef = performance.Str("targetRef");
        string? notice = production ? "The current environment is Production: a performance setup profile is never applied to Production."
            : targetRef is not null && !string.Equals(targetRef, environmentId, StringComparison.OrdinalIgnoreCase) && !string.Equals(targetRef, target.EnvironmentName, StringComparison.OrdinalIgnoreCase)
                ? $"Profile targets {targetRef} · current Target Environment is {target.EnvironmentName}. The target origin always comes from the current Target Environment; applying requires confirmation." : null;
        var safety = PerformanceTestSafety.Check(candidate, null, options).Where(i => i.Key != "authentication").ToList();
        var existing = (await store.DefinitionsAsync(environmentId, false, ct)).FirstOrDefault(d => !d.Archived && string.Equals(d.Name, candidate.Name, StringComparison.OrdinalIgnoreCase));
        var proposed = $"{candidate.Name}: {candidate.Scenario.Steps.Count} step(s) ({string.Join(", ", candidate.Scenario.Steps.Take(3).Select(s => $"{s.Method} {s.RelativePath}"))}), "
            + $"{candidate.Workload.Purpose} {candidate.Workload.Mode} {candidate.Workload.VirtualUsers} VUs, {candidate.Thresholds.Count} threshold(s)";
        SetupChange change;
        if (production || safety.Count > 0)
            change = new SetupChange
            {
                Id = "performance.definition", Owner = Owner, Setting = $"Performance definition {candidate.Name}", Proposed = proposed, Kind = SetupValueKind.DesiredConfiguration,
                Classification = SetupChangeClassification.CannotImport, SafeToApply = false,
                Reason = notice ?? $"Cannot be imported: {string.Join(" ", safety.Select(s => s.Message))}",
            };
        else if (existing is not null && SameContent(existing, candidate))
            change = new SetupChange
            {
                Id = "performance.definition", Owner = Owner, Setting = $"Performance definition {candidate.Name}", Current = $"v{existing.Version}", Proposed = proposed,
                Kind = SetupValueKind.DesiredConfiguration, Classification = SetupChangeClassification.Same, Reason = "The saved definition already has this scenario, workload and thresholds.",
            };
        else
        {
            var update = existing is not null;
            change = new SetupChange
            {
                Id = "performance.definition", Owner = Owner, Setting = $"Performance definition {candidate.Name}", Current = update ? $"v{existing!.Version}" : null, Proposed = proposed,
                Kind = SetupValueKind.DesiredConfiguration, SafeToApply = true, Destination = "Performance Test Review definition (new version)", Provenance = "Imported setup profile",
                Classification = update || notice is not null ? SetupChangeClassification.RequiresConfirmation : SetupChangeClassification.New,
                RequiresConfirmation = update || notice is not null, Preselected = !update && notice is null,
                Reason = update ? $"Saving creates v{existing!.Version + 1}; earlier runs keep the version they ran with. Readiness is evaluated again; nothing runs."
                    : "Creates a new saved definition (v1). Readiness is evaluated separately; nothing runs.",
            };
            if (update) candidate = candidate with { Id = existing!.Id, DeliveryPipelineIds = existing.DeliveryPipelineIds, DriftPolicies = existing.DriftPolicies };
        }
        var changes = new List<SetupChange> { change };
        if (targetRef is not null)
            changes.Add(new SetupChange
            {
                Id = "performance.targetRef", Owner = "Target Environment", Setting = "Target reference", Proposed = targetRef, Kind = SetupValueKind.ReferenceMetadata,
                Classification = SetupChangeClassification.ReferenceOnly,
                Reason = $"Reference only: the target origin is {target.TargetOrigin} from the current Target Environment; a profile never sets a destination.",
            });
        var manual = AlwaysManual.ToList();
        if (performance["localExecution"]?.Str("caBundleRequired") is { } required && required.Equals("true", StringComparison.OrdinalIgnoreCase))
            manual.Add($"corporateCaBundle ({PerformanceCaBundleInspector.ConfigurationKey})");
        manual.AddRange(root["manualRequirements"]?.Items?.Select(n => n.Scalar).OfType<string>() ?? []);
        manual = manual.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new Plan(basic with { Valid = true, Changes = changes, ManualRequirements = manual, EnvironmentNotice = notice, Summary = Summarize(changes, issues, manual.Count) },
            change.SafeToApply ? candidate : null);
    }

    private static SetupProfileSummary Summarize(IReadOnlyList<SetupChange> changes, IReadOnlyList<SetupProfileIssue> issues, int manual) => new(
        changes.Count(c => c.Classification != SetupChangeClassification.ReferenceOnly), changes.Count(c => c.Preselected),
        changes.Count(c => c.Classification == SetupChangeClassification.Same), changes.Count(c => c.Classification == SetupChangeClassification.Conflict),
        changes.Count(c => c.Classification == SetupChangeClassification.RequiresConfirmation), changes.Count(c => c.Classification == SetupChangeClassification.CannotImport),
        changes.Count(c => c.Classification == SetupChangeClassification.ReferenceOnly), issues.Count(i => i.Severity == SetupIssueSeverity.Rejected), manual);

    /// <summary>The definition a profile describes, on the current Target Environment. Unusable values are reported, never guessed.</summary>
    private static PerformanceTestDefinition Candidate(string environmentId, SetupNode p, List<SetupProfileIssue> issues, PerformanceSetupTarget target)
    {
        bool Ok(SetupNode? node) => node is not null && SetupProfileSchema.Usable(node.Path, issues);
        int? Int(SetupNode? node, string key) => Ok(node?[key]) && int.TryParse(node![key]!.Scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        var scenario = p["scenario"];
        var type = scenario?.Str("type") is { } t && Types.TryGetValue(t, out var parsedType) ? parsedType : PerformanceTargetType.RestHttp;
        if (scenario?.Str("type") is { } unknownType && !Types.ContainsKey(unknownType))
            issues.Add(new("performance.scenario.type", SetupIssueSeverity.Warning, $"Unknown scenario type \"{SetupProfileSchema.Safe(unknownType)}\"; rest-http is used."));
        var steps = new List<HttpPerformanceStep>();
        foreach (var (step, index) in (scenario?["steps"]?.Items ?? []).Select((n, i) => (n, i)))
        {
            if (!step.IsMap || !Ok(step)) continue;
            var codes = step["expectedStatusCodes"]?.Items?.Select(n => int.TryParse(n.Scalar, out var code) ? code : 0).Where(c => c > 0).ToList();
            // An operation reference is resolved only when it carries the declared method + path (or GraphQL field); otherwise the step is not
            // imported and is reported "Requires review" — it is never silently turned into an arbitrary manual path.
            var reference = step["operationRef"] is { IsMap: true } r && Ok(r) ? r : null;
            var method = (step.Str("method") ?? reference?.Str("method") ?? "GET").ToUpperInvariant();
            var path = step.Str("path") ?? reference?.Str("path");
            if (reference is not null && path is null && reference.Str("field") is null)
            {
                issues.Add(new($"performance.scenario.steps[{index}].operationRef", SetupIssueSeverity.Warning,
                    $"Requires review: operation {SetupProfileSchema.Safe(reference.Str("operationId") ?? "(unnamed)")} cannot be resolved without its declared method and path; the step is not imported."));
                continue;
            }
            var origin = reference?.Str("type")?.ToLowerInvariant() switch { "openapi" => ScenarioOperationOrigin.OpenApi, "graphql" => ScenarioOperationOrigin.GraphQl, "observed" => ScenarioOperationOrigin.Observed, _ => ScenarioOperationOrigin.Manual };
            var data = step["testDataRef"] is { IsMap: true } dataRef && Ok(dataRef) ? dataRef : null;
            var mappings = (data?["mappings"]?.Map ?? []).Where(m => m.Value.Scalar is { Length: > 0 } && Ok(m.Value))
                .Select(m => new ScenarioDataMapping(m.Key, [.. m.Value.Scalar!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)], m.Key == "body" ? TestDataDataType.JsonPayload : TestDataDataType.SimpleValues, m.Key == "body" ? "body" : origin == ScenarioOperationOrigin.GraphQl ? "variable" : (path ?? "").Contains($"{{{m.Key}}}", StringComparison.Ordinal) ? "path" : "query"))
                .ToList();
            steps.Add(new HttpPerformanceStep
            {
                Name = step.Str("name") ?? $"step-{steps.Count + 1}", Method = method, RelativePath = path ?? "/graphql",
                ExpectedStatusCodes = codes is { Count: > 0 } ? codes : [200], ThinkTimeMs = Int(step, "thinkTimeMs"), GraphQlQuery = Ok(step["graphqlQuery"]) ? step.Str("graphqlQuery") : null,
                Source = reference is null ? null : new ScenarioStepSource
                {
                    OperationKey = $"profile:{origin.ToString().ToLowerInvariant()}#{reference.Str("operationId") ?? reference.Str("field") ?? $"{method} {path}"}",
                    Origin = origin, Evidence = ScenarioEvidence.Manual, ContractName = reference.Str("contract"),
                },
                DataMappings = mappings.Count == 0 ? null : mappings,
            });
        }
        var selection = scenario?.Str("selection") is { } sel && Enum.TryParse<TestDataSelection>(sel.Replace("-", ""), true, out var parsedSelection) ? parsedSelection : (TestDataSelection?)null;
        var w = p["workload"];
        var defaults = new PerformanceWorkload();
        var workload = new PerformanceWorkload
        {
            Purpose = w?.Str("purpose") is { } purpose && Enum.TryParse<WorkloadPurpose>(purpose, true, out var pp) ? pp : defaults.Purpose,
            Mode = w?.Str("mode")?.Replace("-", "") is { } mode && Enum.TryParse<WorkloadMode>(mode, true, out var mm) ? mm : defaults.Mode,
            VirtualUsers = Int(w, "virtualUsers") ?? defaults.VirtualUsers,
            RequestsPerSecond = Ok(w?["requestsPerSecond"]) && double.TryParse(w!.Str("requestsPerSecond"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rps) ? rps : null,
            WarmupSeconds = Int(w, "warmUpSeconds") ?? defaults.WarmupSeconds, RampUpSeconds = Int(w, "rampUpSeconds") ?? defaults.RampUpSeconds,
            SteadyStateSeconds = Int(w, "steadyStateSeconds") ?? defaults.SteadyStateSeconds, RampDownSeconds = Int(w, "rampDownSeconds") ?? defaults.RampDownSeconds,
        };
        var thresholds = new List<PerformanceThreshold>();
        foreach (var (node, index) in (p["thresholds"]?.Items ?? []).Select((n, i) => (n, i)))
        {
            if (!node.IsMap || !Ok(node)) continue;
            if (node.Str("metric") is not { } metric || !Metrics.TryGetValue(metric, out var parsedMetric))
            { issues.Add(new($"performance.thresholds[{index}].metric", SetupIssueSeverity.Warning, $"Unknown threshold metric; supported: {string.Join(", ", Metrics.Keys)}. The threshold is ignored.")); continue; }
            if (!Operators.TryGetValue(node.Str("operator") ?? "<=", out var op) || !double.TryParse(node.Str("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            { issues.Add(new($"performance.thresholds[{index}]", SetupIssueSeverity.Warning, "A threshold needs an operator (<, <=, >, >=) and a numeric value. The threshold is ignored.")); continue; }
            thresholds.Add(new PerformanceThreshold
            {
                Metric = parsedMetric, Operator = op, Value = value,
                Severity = string.Equals(node.Str("severity"), "advisory", StringComparison.OrdinalIgnoreCase) ? ThresholdSeverity.Advisory : ThresholdSeverity.Required,
            });
        }
        var resources = p["resourceObservation"]?.Str("enabled") is { } enabled && enabled.Equals("true", StringComparison.OrdinalIgnoreCase)
            ? new ResourceObservationConfiguration { Enabled = true } : null;
        return new PerformanceTestDefinition
        {
            Id = Guid.NewGuid().ToString("N"), EnvironmentId = environmentId, Name = p["definition"]?.Str("name") is { Length: > 0 } name ? name.Trim()[..Math.Min(120, name.Trim().Length)] : "Imported performance test",
            Description = p["definition"]?.Str("description"), TargetType = type, TargetOrigin = target.TargetOrigin, EnvironmentType = target.EnvironmentType, EnvironmentName = target.EnvironmentName,
            Scenario = new PerformanceScenario { Name = scenario?.Str("name") ?? "Main scenario", Steps = steps, DataSelection = selection }, Workload = workload, Thresholds = thresholds, ResourceObservation = resources,
        };
    }

    /// <summary>Scenario, workload, thresholds and resource preference equal (ids and names of generated parts ignored).</summary>
    private static bool SameContent(PerformanceTestDefinition a, PerformanceTestDefinition b)
    {
        static object Shape(PerformanceTestDefinition d) => new
        {
            d.TargetType, Steps = d.Scenario.Steps.Select(s => new { s.Name, s.Method, s.RelativePath, Codes = string.Join(",", s.ExpectedStatusCodes), s.ThinkTimeMs, s.GraphQlQuery,
                Data = string.Join(";", (s.DataMappings ?? []).Select(m => $"{m.Variable}={string.Join(",", m.AssetAliases)}")) }).ToList(), d.Scenario.DataSelection,
            d.Workload.Purpose, d.Workload.Mode, d.Workload.VirtualUsers, d.Workload.RequestsPerSecond, d.Workload.WarmupSeconds, d.Workload.RampUpSeconds, d.Workload.SteadyStateSeconds, d.Workload.RampDownSeconds,
            Thresholds = d.Thresholds.Select(t => new { t.Metric, t.Operator, t.Value, t.Severity }).ToList(), Resources = d.ResourceObservation?.Enabled == true,
        };
        return JsonSerializer.Serialize(Shape(a)) == JsonSerializer.Serialize(Shape(b));
    }

    // ── Export ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<SetupProfileExport?> ExportAsync(string environmentId, string? definitionId, SetupProfileFormat format, CancellationToken ct)
    {
        var definitions = await store.DefinitionsAsync(environmentId, false, ct);
        var d = definitions.FirstOrDefault(x => x.Id == definitionId) ?? (definitionId is null ? definitions.FirstOrDefault(x => !x.Archived) : null);
        if (d is null) return null;
        var document = Document(d, environmentId, PerformanceCaBundleInspector.Inspect(options.Container.EffectiveCaBundlePath).Configured);
        var text = format == SetupProfileFormat.Json ? JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }) : SetupProfileService.Yaml(document, 0);
        return new SetupProfileExport(format, $"performance-{SetupProfileService.Slug(d.Name)}-{SetupProfileService.Slug(d.EnvironmentName)}.setup.{(format == SetupProfileFormat.Json ? "json" : "yaml")}",
            text, ExportExcluded);
    }

    internal static Dictionary<string, object?> Document(PerformanceTestDefinition d, string environmentId, bool caBundleRequired) => new()
    {
        ["version"] = SetupProfileSchema.SupportedVersion,
        ["profile"] = SetupProfileService.Compact(new Dictionary<string, object?>
        {
            ["name"] = $"Performance {d.Name} {d.EnvironmentName}", ["testPack"] = PerformanceSetupModel.PackId, ["environment"] = d.EnvironmentName, ["environmentType"] = d.EnvironmentType,
        }),
        ["metadata"] = new Dictionary<string, object?> { ["generatedBy"] = "BirkNext", ["sanitized"] = true, ["containsSecrets"] = false },
        ["performance"] = SetupProfileService.Compact(new Dictionary<string, object?>
        {
            ["targetRef"] = environmentId,
            ["definition"] = SetupProfileService.Compact(new Dictionary<string, object?> { ["name"] = d.Name, ["description"] = d.Description }),
            ["scenario"] = SetupProfileService.Compact(new Dictionary<string, object?>
            {
                ["type"] = Types.First(t => t.Value == d.TargetType).Key, ["name"] = d.Scenario.Name,
                ["selection"] = d.Scenario.DataSelection is { } selection ? selection.ToString() : null,
                ["steps"] = d.Scenario.Steps.Where(s => !s.Disabled).Select(s => (object?)SetupProfileService.Compact(new Dictionary<string, object?>
                {
                    ["name"] = s.Name, ["method"] = s.Method, ["path"] = s.RelativePath, ["expectedStatusCodes"] = s.ExpectedStatusCodes.Cast<object?>().ToList(),
                    ["thinkTimeMs"] = s.ThinkTimeMs, ["graphqlQuery"] = s.GraphQlQuery,
                    // References only: the declared operation and the central Test Data aliases — never a Test Data value.
                    ["operationRef"] = s.Source is { } source && source.Origin != ScenarioOperationOrigin.Manual ? SetupProfileService.Compact(new Dictionary<string, object?>
                    {
                        ["type"] = source.Origin.ToString().ToLowerInvariant(), ["operationId"] = source.OperationKey[(source.OperationKey.IndexOf('#') + 1)..],
                        ["method"] = s.Method, ["path"] = s.RelativePath, ["contract"] = source.ContractName,
                    }) : null,
                    ["testDataRef"] = s.DataMappings is { Count: > 0 } mappings ? new Dictionary<string, object?>
                    {
                        ["mappings"] = mappings.ToDictionary(m => m.Variable, m => (object?)string.Join(",", m.AssetAliases)),
                    } : null,
                })).ToList(),
            }),
            ["workload"] = SetupProfileService.Compact(new Dictionary<string, object?>
            {
                ["purpose"] = d.Workload.Purpose.ToString().ToLowerInvariant(), ["mode"] = d.Workload.Mode == WorkloadMode.VirtualUsers ? "virtual-users" : "arrival-rate",
                ["virtualUsers"] = d.Workload.VirtualUsers, ["requestsPerSecond"] = d.Workload.RequestsPerSecond, ["warmUpSeconds"] = d.Workload.WarmupSeconds,
                ["rampUpSeconds"] = d.Workload.RampUpSeconds, ["steadyStateSeconds"] = d.Workload.SteadyStateSeconds, ["rampDownSeconds"] = d.Workload.RampDownSeconds,
            }),
            ["thresholds"] = d.Thresholds.Select(t => (object?)new Dictionary<string, object?>
            {
                ["metric"] = Metrics.First(m => m.Value == t.Metric).Key, ["operator"] = Operators.First(o => o.Value == t.Operator).Key, ["value"] = t.Value,
                ["unit"] = PerformanceTestRules.Unit(t.Metric) == "%" ? "percent" : PerformanceTestRules.Unit(t.Metric), ["severity"] = t.Severity.ToString().ToLowerInvariant(),
            }).ToList(),
            ["resourceObservation"] = new Dictionary<string, object?> { ["enabled"] = d.ResourceObservation?.Enabled == true },
            ["localExecution"] = new Dictionary<string, object?> { ["caBundleRequired"] = caBundleRequired },
        }),
        ["manualRequirements"] = AlwaysManual.Concat(caBundleRequired ? [$"corporateCaBundle ({PerformanceCaBundleInspector.ConfigurationKey})"] : []).Cast<object?>().ToList(),
    };

    public static string Example() => SetupProfileService.Yaml(Document(new PerformanceTestDefinition
    {
        Name = "Baseline check", EnvironmentName = "Development", EnvironmentType = "Development",
        Scenario = new PerformanceScenario { Name = "Main scenario", Steps = [new HttpPerformanceStep { Name = "home", Method = "GET", RelativePath = "/", ExpectedStatusCodes = [200], ThinkTimeMs = 500 }] },
        Thresholds =
        [
            new PerformanceThreshold { Metric = PerformanceMetric.LatencyP95Ms, Operator = ThresholdOperator.LessThanOrEqual, Value = 500 },
            new PerformanceThreshold { Metric = PerformanceMetric.ErrorRatePercent, Operator = ThresholdOperator.LessThanOrEqual, Value = 1 },
        ],
    }, "<target-environment-id>", false), 0);
}
