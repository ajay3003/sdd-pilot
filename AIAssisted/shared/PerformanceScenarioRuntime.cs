using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BirkNext.TestData;

namespace BirkNext.PerformanceTests;

// ── Scenario runtime values and step dependencies ────────────────────────────────────────────────────────────────────────
// Discovery says which operations exist; the executable step owns how each dynamic parameter gets its value. A {placeholder} is a dynamic
// parameter — it needs a runtime value source, which may be Test Data, an earlier step's output, a scenario variable, a fixed value or a
// generated value. Test Data is one source among them, never implied by the placeholder itself. Pure and deterministic; nothing runs here.

/// <summary>One primary state per executable step.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepStatus { Ready, NeedsConfiguration, Blocked, Disabled }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DependencyProblem { None, ProducerLater, ProducerMissing, ProducerDisabled, OutputMissing }

/// <summary>A step-to-step reference: <paramref name="ConsumerIndex"/>'s <paramref name="Variable"/> uses <paramref name="Output"/> of the producer.</summary>
public sealed record StepDependency(string ConsumerId, int ConsumerIndex, string Variable, string ProducerId, int ProducerIndex, string Output, DependencyProblem Problem);

/// <summary>A dynamic parameter of a step: name, location (path, query, header, body, variable) and the data shape it needs.</summary>
public sealed record RuntimeParameter(string Variable, string In, TestDataDataType DataType);

/// <summary>One row of a step's Runtime values: the parameter, its mapping (if any) and whether it resolves; <see cref="Problem"/> says precisely why not.</summary>
public sealed record RuntimeValueRow(RuntimeParameter Parameter, ScenarioDataMapping? Mapping, string SourceLabel, string ValueText, string? Problem)
{
    public bool Resolved => Problem is null;
}

public sealed record StepReadiness(StepStatus Status, IReadOnlyList<string> Reasons);

/// <summary>A suggested (never applied) value source for an unresolved parameter.</summary>
public sealed record RuntimeValueSuggestion(RuntimeValueSource Source, string Text, string? Alias = null, string? FromStepId = null, string? Output = null);

public static partial class PerformanceScenarioRuntime
{
    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")] private static partial Regex PlaceholderPattern();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,59}$")] private static partial Regex NamePattern();
    /// <summary>"$.id", "$.data.items[0].id" — dot members and numeric indexes only (no filters, no scripting).</summary>
    [GeneratedRegex(@"^\$(\.[A-Za-z_][A-Za-z0-9_]*|\[\d{1,4}\])+$")] private static partial Regex OutputPathPattern();
    /// <summary>Parameters whose meaning is a per-request technical value (never a business identifier).</summary>
    [GeneratedRegex(@"(?i)(correlation|request|trace|span|idempotency|nonce|timestamp)")] private static partial Regex GeneratedName();

    public static bool ValidName(string? name) => name is not null && NamePattern().IsMatch(name);
    public static bool ValidOutputPath(string? path) => path is not null && path.Length <= 200 && OutputPathPattern().IsMatch(path);

    /// <summary>"$.data.items[0].id" → "data.items.0.id" (the k6 Response.json selector syntax).</summary>
    public static string Selector(string path) => Regex.Replace(path[1..], @"\[(\d+)\]", ".$1").TrimStart('.');

    /// <summary>The dynamic parameters of a step, in order of appearance: path, query values, header values, body, GraphQL variables.</summary>
    public static IReadOnlyList<RuntimeParameter> Parameters(HttpPerformanceStep step)
    {
        var found = new List<RuntimeParameter>();
        void Add(string? text, string place, TestDataDataType type)
        {
            foreach (Match m in PlaceholderPattern().Matches(text ?? ""))
                if (found.All(f => f.Variable != m.Groups[1].Value)) found.Add(new(m.Groups[1].Value, place, type));
        }
        Add(step.RelativePath, "path", TestDataDataType.SimpleValues);
        foreach (var q in step.QueryParameters) Add(q.Value, "query", TestDataDataType.SimpleValues);
        foreach (var h in step.Headers) Add(h.Value, "header", TestDataDataType.SimpleValues);
        if (step.BodyTemplate is { } body)
        {
            // A body that is exactly one placeholder is a payload (JSON/XML asset); a placeholder inside a body is a scalar.
            var whole = PlaceholderPattern().Match(body.Trim()) is { Success: true } m && m.Value == body.Trim();
            var payload = TestDataDataTypes.ForMediaType(step.ContentType ?? "application/json");
            Add(body, "body", whole ? payload == TestDataDataType.SimpleValues ? TestDataDataType.JsonPayload : payload : TestDataDataType.SimpleValues);
        }
        Add(step.GraphQlVariablesJson, "variable", TestDataDataType.SimpleValues);
        return found;
    }

    public static string SourceLabel(RuntimeValueSource source) => source switch
    {
        RuntimeValueSource.TestData => "Test Data",
        RuntimeValueSource.PreviousStep => "Previous step output",
        RuntimeValueSource.ScenarioVariable => "Scenario variable",
        RuntimeValueSource.Fixed => "Fixed value",
        _ => "Generated value",
    };

    public static string GeneratorLabel(GeneratedValueKind kind) => kind switch
    {
        GeneratedValueKind.Uuid => "UUID",
        GeneratedValueKind.Timestamp => "Timestamp (ISO 8601)",
        _ => "Random number",
    };

    /// <summary>
    /// Generated values are valid only for per-request technical values: query/header parameters, or a path/variable parameter whose name says it
    /// is a correlation/request/trace/idempotency/timestamp value. Never for a body or a business identifier where approved Test Data is expected.
    /// </summary>
    public static bool GeneratedAllowed(RuntimeParameter p) => p.DataType == TestDataDataType.SimpleValues && p.In switch
    {
        "query" or "header" => true,
        "path" or "variable" => GeneratedName().IsMatch(p.Variable),
        _ => false,
    };

    /// <summary>The sources offered for a parameter. A payload body takes Test Data (payload asset), a fixed body or a previous-step value.</summary>
    public static IReadOnlyList<RuntimeValueSource> SourcesFor(RuntimeParameter p) =>
    [
        RuntimeValueSource.TestData, RuntimeValueSource.PreviousStep, RuntimeValueSource.ScenarioVariable, RuntimeValueSource.Fixed,
        .. GeneratedAllowed(p) ? new[] { RuntimeValueSource.Generated } : [],
    ];

    private static int IndexOf(IReadOnlyList<HttpPerformanceStep> steps, string? id)
    {
        for (var i = 0; i < steps.Count; i++) if (steps[i].Id == id) return i;
        return -1;
    }

    /// <summary>Every step-to-step reference of the scenario, with the problem it has in the current order (None when valid).</summary>
    public static IReadOnlyList<StepDependency> Dependencies(IReadOnlyList<HttpPerformanceStep> steps)
    {
        var result = new List<StepDependency>();
        for (var i = 0; i < steps.Count; i++)
        {
            foreach (var m in steps[i].DataMappings ?? [])
            {
                if (m.Source != RuntimeValueSource.PreviousStep) continue;
                var producer = IndexOf(steps, m.FromStepId);
                var output = m.Output ?? "";
                var problem = producer < 0 ? DependencyProblem.ProducerMissing
                    : producer >= i ? DependencyProblem.ProducerLater
                    : steps[producer].Disabled ? DependencyProblem.ProducerDisabled
                    : steps[producer].Outputs?.Any(o => o.Name == output) != true ? DependencyProblem.OutputMissing
                    : DependencyProblem.None;
                result.Add(new(steps[i].Id, i, m.Variable, m.FromStepId ?? "", producer, output, problem));
            }
        }
        return result;
    }

    public static IReadOnlyList<StepDependency> Dependencies(PerformanceScenario scenario) => Dependencies(scenario.Steps);

    /// <summary>The order after moving one step (the list itself is never changed here).</summary>
    public static List<HttpPerformanceStep> Moved(IReadOnlyList<HttpPerformanceStep> steps, int from, int to)
    {
        var list = steps.ToList();
        if (from < 0 || from >= list.Count || to < 0 || to >= list.Count || from == to) return list;
        var step = list[from];
        list.RemoveAt(from);
        list.Insert(to, step);
        return list;
    }

    /// <summary>References a move would break: valid now, but the producer would execute after the consumer.</summary>
    public static IReadOnlyList<StepDependency> ReorderConflicts(IReadOnlyList<HttpPerformanceStep> steps, int from, int to)
    {
        var before = Dependencies(steps).Where(d => d.Problem != DependencyProblem.ProducerLater).Select(d => (d.ConsumerId, d.Variable)).ToHashSet();
        return [.. Dependencies(Moved(steps, from, to)).Where(d => d.Problem == DependencyProblem.ProducerLater && before.Contains((d.ConsumerId, d.Variable)))];
    }

    /// <summary>
    /// "Move dependency with this step where safe": moving <paramref name="from"/> to <paramref name="to"/> while keeping every producer it needs
    /// directly before it, in their current relative order. Null when that would itself break another reference.
    /// </summary>
    public static List<HttpPerformanceStep>? MoveWithDependencies(IReadOnlyList<HttpPerformanceStep> steps, int from, int to)
    {
        if (from < 0 || from >= steps.Count) return null;
        var moving = steps[from];
        var needed = new HashSet<string>(StringComparer.Ordinal);
        void Collect(HttpPerformanceStep s)
        {
            foreach (var m in (s.DataMappings ?? []).Where(m => m.Source == RuntimeValueSource.PreviousStep && m.FromStepId is not null))
                if (steps.FirstOrDefault(x => x.Id == m.FromStepId) is { } p && needed.Add(p.Id)) Collect(p);
        }
        Collect(moving);
        var group = steps.Where(s => needed.Contains(s.Id)).Append(moving).ToList();
        var rest = steps.Where(s => !needed.Contains(s.Id) && s.Id != moving.Id).ToList();
        var anchor = Math.Clamp(to > from ? to - group.Count + 1 : to, 0, rest.Count);
        var result = rest.Take(anchor).Concat(group).Concat(rest.Skip(anchor)).ToList();
        return Dependencies(result).Any(d => d.Problem == DependencyProblem.ProducerLater) ? null : result;
    }

    /// <summary>Later mappings that read an output of this step (shown before delete/disable — never left broken silently).</summary>
    public static IReadOnlyList<StepDependency> Dependents(IReadOnlyList<HttpPerformanceStep> steps, string stepId) =>
        [.. Dependencies(steps).Where(d => d.ProducerId == stepId)];

    /// <summary>A copy with its configuration, runtime mappings, outputs, auth override, delay and expectations — and a new identity.</summary>
    public static HttpPerformanceStep Duplicate(HttpPerformanceStep step, IEnumerable<string> existingNames)
    {
        var names = existingNames.ToHashSet(StringComparer.Ordinal);
        var stem = step.Name.Length > 52 ? step.Name[..52] : step.Name;
        var name = $"{stem}-copy";
        for (var n = 2; names.Contains(name); n++) name = $"{stem}-copy-{n}";
        return step with
        {
            Id = Guid.NewGuid().ToString("N")[..12], Name = name,
            Headers = [.. step.Headers], QueryParameters = [.. step.QueryParameters], ExpectedStatusCodes = [.. step.ExpectedStatusCodes],
            DataMappings = step.DataMappings is null ? null : [.. step.DataMappings.Select(m => m with { AssetAliases = [.. m.AssetAliases] })],
            Outputs = step.Outputs is null ? null : [.. step.Outputs],
        };
    }

    /// <summary>A manual step: same executable step model, nothing pre-filled that could run (the path is entered by the person).</summary>
    public static HttpPerformanceStep ManualStep(int number, bool graphQl) => new()
    {
        Name = $"manual-step-{number}", Method = graphQl ? "POST" : "GET", RelativePath = graphQl ? "/graphql" : "", ExpectedStatusCodes = [200], ThinkTimeMs = 500,
    };

    /// <summary>The authentication configuration a step uses: its override, none, or the definition default.</summary>
    public static string? EffectiveAuthentication(PerformanceTestDefinition d, HttpPerformanceStep s) => s.Authentication switch
    {
        StepAuthenticationMode.None => null,
        StepAuthenticationMode.Override => s.AuthenticationReference,
        _ => d.AuthenticationReference,
    };

    public static string AuthenticationLabel(PerformanceTestDefinition d, HttpPerformanceStep s) => s.Authentication switch
    {
        StepAuthenticationMode.None => "None",
        StepAuthenticationMode.Override => s.AuthenticationReference is null ? "Override · not chosen" : $"Override · {s.AuthenticationReference}",
        _ => d.AuthenticationReference is null ? "Inherit scenario default (None)" : $"Inherit scenario default ({d.AuthenticationReference})",
    };

    private static string Step(int index) => $"Step {index + 1}";

    /// <summary>Why a parameter has no usable value in this position of the scenario — null when it resolves.</summary>
    public static string? Problem(PerformanceScenario scenario, int stepIndex, RuntimeParameter p, ScenarioDataMapping? m)
    {
        if (m is null) return "Runtime value source not configured";
        switch (m.Source)
        {
            case RuntimeValueSource.TestData:
                return m.AssetAliases.Count == 0 ? "Test Data asset not chosen" : null;
            case RuntimeValueSource.PreviousStep:
                var producer = IndexOf(scenario.Steps, m.FromStepId);
                if (producer < 0) return "References a step that is no longer in the scenario";
                if (producer >= stepIndex) return $"References output from {Step(producer)}, which executes later — invalid execution order";
                if (scenario.Steps[producer].Disabled) return $"References output from {Step(producer)}, which is disabled";
                if (scenario.Steps[producer].Outputs?.Any(o => o.Name == m.Output) != true) return $"{Step(producer)} has no output '{m.Output}'";
                return null;
            case RuntimeValueSource.ScenarioVariable:
                var variable = scenario.Variables?.FirstOrDefault(v => v.Name == m.ScenarioVariable);
                if (variable is null) return $"Scenario variable '{m.ScenarioVariable}' is not defined";
                return VariableProblem(variable);
            case RuntimeValueSource.Fixed:
                return string.IsNullOrEmpty(m.FixedValue) ? "Fixed value is empty" : null;
            default:
                if (!GeneratedAllowed(p)) return "A generated value is not valid for this parameter — use Test Data or another source";
                return m.Generator is null ? "Generator not chosen" : null;
        }
    }

    /// <summary>Why a scenario variable is unusable — null when it is configured.</summary>
    public static string? VariableProblem(ScenarioVariable v) => !ValidName(v.Name) ? "Variable names use letters, digits and '_' (1–60 characters)"
        : v.Source switch
        {
            RuntimeValueSource.Fixed => string.IsNullOrEmpty(v.Value) ? $"Scenario variable '{v.Name}' has no value" : null,
            RuntimeValueSource.TestData => string.IsNullOrEmpty(v.AssetAlias) ? $"Scenario variable '{v.Name}' has no Test Data asset" : null,
            RuntimeValueSource.Generated => v.Generator is null ? $"Scenario variable '{v.Name}' has no generator" : null,
            _ => $"Scenario variable '{v.Name}' must be fixed, generated or Test Data",
        };

    /// <summary>The Runtime values rows of one step.</summary>
    public static IReadOnlyList<RuntimeValueRow> Rows(PerformanceScenario scenario, int stepIndex)
    {
        var step = scenario.Steps[stepIndex];
        return [.. Parameters(step).Select(p =>
        {
            var m = step.DataMappings?.FirstOrDefault(x => x.Variable == p.Variable);
            return new RuntimeValueRow(p, m, m is null ? "Not configured" : SourceLabel(m.Source), m is null ? "—" : ValueText(scenario, m), Problem(scenario, stepIndex, p, m));
        })];
    }

    /// <summary>The value/reference shown for a mapping — an alias, step output, variable, fixed value or generator; never a Test Data value.</summary>
    public static string ValueText(PerformanceScenario scenario, ScenarioDataMapping m) => m.Source switch
    {
        RuntimeValueSource.TestData => m.AssetAliases.Count == 0 ? "—" : string.Join(", ", m.AssetAliases) + (m.FromObservedSample ? " (from reviewed observed sample)" : ""),
        RuntimeValueSource.PreviousStep => IndexOf(scenario.Steps, m.FromStepId) is var i and >= 0 ? $"{Step(i)} · {scenario.Steps[i].Name} · {m.Output}" : $"(missing step) · {m.Output}",
        RuntimeValueSource.ScenarioVariable => $"{{{{{m.ScenarioVariable}}}}}",
        RuntimeValueSource.Fixed => m.FixedValue ?? "",
        _ => m.Generator is { } g ? GeneratorLabel(g) : "—",
    };

    /// <summary>
    /// One primary status per step: Disabled; Blocked (performance safety: write method, REST POST not marked read-only, missing/invalid path,
    /// invalid ordering); Needs configuration (an unresolved runtime value); otherwise Ready. Reasons are precise ("Step 2 · brukerId: …").
    /// </summary>
    public static StepReadiness Readiness(PerformanceTestDefinition d, int index)
    {
        var s = d.Scenario.Steps[index];
        if (s.Disabled) return new(StepStatus.Disabled, ["Disabled — stays in the definition but does not run"]);
        var blocked = new List<string>();
        var method = (s.Method ?? "").Trim().ToUpperInvariant();
        if (PerformanceTestRules.BlockedMethods.Contains(method)) blocked.Add($"{Step(index)}: {method} changes data — performance tests use read-only operations");
        else if (method == "POST" && d.TargetType != PerformanceTargetType.GraphQlHttp && !s.PostIsSafeRead)
            blocked.Add($"{Step(index)}: a REST POST runs only when marked \"This POST only reads\"");
        if (string.IsNullOrWhiteSpace(s.RelativePath)) blocked.Add($"{Step(index)}: enter the request path");
        else if (!s.RelativePath.StartsWith('/')) blocked.Add($"{Step(index)}: the path must start with '/'");
        var rows = Rows(d.Scenario, index);
        blocked.AddRange(rows.Where(r => r.Problem?.Contains("invalid execution order", StringComparison.Ordinal) == true).Select(r => $"{Step(index)} · {r.Parameter.Variable}: {r.Problem}"));
        foreach (var o in s.Outputs ?? [])
            if (!ValidName(o.Name) || !ValidOutputPath(o.Path)) blocked.Add($"{Step(index)}: output '{o.Name}' needs a name and a JSON path such as $.id");
        if (blocked.Count > 0) return new(StepStatus.Blocked, blocked);
        var open = rows.Where(r => !r.Resolved).Select(r => $"{Step(index)} · {r.Parameter.Variable}: {r.Problem}").ToList();
        if (s.Authentication == StepAuthenticationMode.Override && s.AuthenticationReference is null) open.Add($"{Step(index)}: authentication override has no configuration");
        return open.Count > 0 ? new(StepStatus.NeedsConfiguration, open) : new(StepStatus.Ready, []);
    }

    public static string StatusLabel(StepStatus status) => status switch
    {
        StepStatus.NeedsConfiguration => "Needs configuration",
        _ => status.ToString(),
    };

    /// <summary>Scenario-level summary of the step statuses: Blocked wins, then Needs configuration; Ready only with at least one enabled step.</summary>
    public static StepReadiness ScenarioReadiness(PerformanceTestDefinition d)
    {
        var steps = Enumerable.Range(0, d.Scenario.Steps.Count).Select(i => Readiness(d, i)).ToList();
        var reasons = steps.SelectMany(r => r.Status == StepStatus.Disabled ? [] : r.Reasons).ToList();
        reasons.AddRange((d.Scenario.Variables ?? []).Select(VariableProblem).OfType<string>());
        var status = steps.Any(r => r.Status == StepStatus.Blocked) ? StepStatus.Blocked
            : steps.Any(r => r.Status == StepStatus.NeedsConfiguration) || reasons.Count > 0 ? StepStatus.NeedsConfiguration
            : steps.Any(r => r.Status == StepStatus.Ready) ? StepStatus.Ready : StepStatus.NeedsConfiguration;
        if (steps.All(r => r.Status == StepStatus.Disabled)) reasons.Insert(0, d.Scenario.Steps.Count == 0 ? "The scenario has no steps" : "Every step is disabled");
        return new(status, reasons);
    }

    /// <summary>
    /// A suggested source for an unresolved parameter (applied only by an explicit "Use"): an earlier enabled step's output with the same name,
    /// else a single approved Test Data asset whose alias/label/capability names the parameter. Never an observed value.
    /// </summary>
    public static RuntimeValueSuggestion? Suggest(PerformanceScenario scenario, int stepIndex, RuntimeParameter p, IReadOnlyList<TestDataAssetSummary> assets, string? environment)
    {
        for (var i = stepIndex - 1; i >= 0; i--)
        {
            var s = scenario.Steps[i];
            if (!s.Disabled && s.Outputs?.FirstOrDefault(o => string.Equals(o.Name, p.Variable, StringComparison.OrdinalIgnoreCase)) is { } o)
                return new(RuntimeValueSource.PreviousStep, $"{Step(i)} · {s.Name} produces {o.Name}", FromStepId: s.Id, Output: o.Name);
        }
        var need = new ScenarioDataNeed(p.Variable, p.In, p.DataType, true, null, null);
        var match = PerformanceScenarioEngine.Match(need, assets, environment);
        return match.Recommended is { } alias ? new(RuntimeValueSource.TestData, $"Existing Test Data match: {alias}", Alias: alias) : null;
    }

    /// <summary>Approved assets compatible with a parameter: same data shape (scalar ↔ simple values, body ↔ payload), usable in the environment.</summary>
    public static IReadOnlyList<TestDataAssetSummary> CompatibleAssets(RuntimeParameter p, IReadOnlyList<TestDataAssetSummary> assets, string? environment) =>
        [.. assets.Where(a => a.Approval == TestDataApproval.Approved && TestDataDataTypes.Of(a) == p.DataType
            && (a.Environment is null || environment is null || string.Equals(a.Environment, environment, StringComparison.OrdinalIgnoreCase))).OrderBy(a => a.Alias, StringComparer.Ordinal)];
}
