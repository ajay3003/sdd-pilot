using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BirkNext.TestData;

namespace BirkNext.PerformanceTests;

// ── Scenario runtime values and step dependencies ────────────────────────────────────────────────────────────────────────
// Discovery says which operations exist; the executable step owns how each dynamic parameter gets its value. A {placeholder} is a dynamic
// parameter — it needs a runtime value source, which may be an auto-filled value (contract default/example or a chosen observed value), a fixed
// value, an earlier step's output, a scenario variable, Test Data or a generated value. Test Data is optional — one source among them, never implied
// by the placeholder itself. Pure and deterministic; nothing runs here.

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

/// <summary>State of one status dimension of a step (Definition, Runtime values, Execution).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepDimensionState { Ready, NeedsConfiguration, Blocked, NotApplicable }

public sealed record StepDimension(StepDimensionState State, string Text);

/// <summary>
/// The authentication a step is configured with, against what k6 can actually execute. Configured ≠ executable: a Target Environment
/// authentication is an attended (interactive browser) sign-in; k6 needs a non-interactive credential, which BirkNext does not issue.
/// </summary>
public sealed record StepExecutionCredential(string Reference, string Source, bool Interactive, bool PerformanceCompatible, string Problem);

/// <summary>A step's three separate statuses: what it is (Definition), where its values come from (Runtime values) and whether k6 can run it
/// (Execution). A step can be fully configured and still not executable.</summary>
public sealed record StepDimensions(StepDimension Definition, StepDimension RuntimeValues, StepDimension Execution, StepExecutionCredential? Credential)
{
    /// <summary>Definition and runtime values are ready, but execution is blocked: "Configured · execution blocked", never a green Ready.</summary>
    public bool ConfiguredButExecutionBlocked => Definition.State == StepDimensionState.Ready && RuntimeValues.State is StepDimensionState.Ready or StepDimensionState.NotApplicable
        && Execution.State == StepDimensionState.Blocked;
}

/// <summary>NotRequired: no authentication observed or declared. Configured: a configuration is referenced. Missing: required, nothing configured
/// (a default None never counts). IntentionallyUnauthenticated: an explicit, still-valid override. OverrideOutdated: the override no longer matches.</summary>
public enum StepAuthenticationState { NotRequired, Configured, Missing, IntentionallyUnauthenticated, OverrideOutdated }

/// <summary>One enabled step's authentication requirement against its effective configuration (names and templated identities only).</summary>
public sealed record StepAuthenticationRequirement(int Index, string Step, string Operation, string Identity, string? Requirement, string? Effective, StepAuthenticationState State)
{
    public bool Blocking => State is StepAuthenticationState.Missing or StepAuthenticationState.OverrideOutdated;
}

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
        RuntimeValueSource.AutoFilled => "Auto-filled value",
        _ => "Generated value",
    };

    /// <summary>"Auto-filled · OpenAPI example"; other sources their plain label.</summary>
    public static string SourceLabel(ScenarioDataMapping m) =>
        m.Source == RuntimeValueSource.AutoFilled && m.ValueOrigin is { } origin ? $"Auto-filled · {origin}" : SourceLabel(m.Source);

    /// <summary>
    /// Why a scenario value does not fit the declared type: integer, number, boolean, uuid, date and date-time (OpenAPI "type:format" or GraphQL
    /// Int/Float/Boolean/ID), and declared enum values. Unknown or string types accept any value. Null when valid.
    /// </summary>
    public static string? TypeProblem(string value, string? declaredType, IReadOnlyList<string>? allowed = null)
    {
        if (allowed is { Count: > 0 } && !allowed.Contains(value, StringComparer.Ordinal)) return $"'{Short(value)}' is not an allowed value ({string.Join(", ", allowed.Take(8))}{(allowed.Count > 8 ? ", …" : "")})";
        var type = (declaredType ?? "").Trim('!', '[', ']').ToLowerInvariant();
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var ok = type switch
        {
            var t when t.StartsWith("integer", StringComparison.Ordinal) || t == "int" || t == "long" => long.TryParse(value, System.Globalization.NumberStyles.AllowLeadingSign, inv, out _),
            var t when t.StartsWith("number", StringComparison.Ordinal) || t is "float" or "decimal" => decimal.TryParse(value, System.Globalization.NumberStyles.Float, inv, out _),
            var t when t.StartsWith("boolean", StringComparison.Ordinal) => value is "true" or "false",
            "string:uuid" or "uuid" => Guid.TryParse(value, out _),
            "string:date" or "date" => DateOnly.TryParseExact(value, "yyyy-MM-dd", inv, System.Globalization.DateTimeStyles.None, out _),
            "string:date-time" or "datetime" => DateTimeOffset.TryParse(value, inv, System.Globalization.DateTimeStyles.None, out _),
            _ => true,
        };
        return ok ? null : $"'{Short(value)}' is not a valid {TypeLabel(type)}";
    }

    private static string Short(string v) => v.Length > 40 ? v[..40] + "…" : v;

    private static string TypeLabel(string type) => type switch
    {
        var t when t.StartsWith("integer", StringComparison.Ordinal) || t is "int" or "long" => "integer",
        var t when t.StartsWith("number", StringComparison.Ordinal) || t is "float" or "decimal" => "number",
        var t when t.StartsWith("boolean", StringComparison.Ordinal) => "boolean (true/false)",
        "string:uuid" or "uuid" => "UUID",
        "string:date" or "date" => "date (YYYY-MM-DD)",
        _ => "date-time (ISO 8601)",
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

    /// <summary>
    /// The sources offered for a parameter, scenario values first and Test Data (optional) after them. An auto-filled value is offered only when one
    /// exists (<paramref name="autoFilled"/>): it is evidence-derived, never typed in.
    /// </summary>
    public static IReadOnlyList<RuntimeValueSource> SourcesFor(RuntimeParameter p, bool autoFilled = false) =>
    [
        .. autoFilled ? new[] { RuntimeValueSource.AutoFilled } : [],
        RuntimeValueSource.Fixed, RuntimeValueSource.PreviousStep, RuntimeValueSource.ScenarioVariable, RuntimeValueSource.TestData,
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

    // ── Execution credential (the one rule: step card, scenario summary, Readiness tab and backend start) ──────────────────────────────────

    /// <summary>The Target Environment's own (attended, interactive) authentication: not executable by k6.</summary>
    public const string TargetEnvironmentAuthentication = "target-environment";

    /// <summary>
    /// The API credential the Local HTTPS proxy captured from the person's own browser session for this Target Environment (memory only, bound to
    /// the environment's approved hosts, expires with the token). The one source k6 can execute: the backend leases it for one run, checks it is
    /// present, in scope and valid for the whole run, and hands it to k6 only through the container's process environment.
    /// </summary>
    public const string TargetEnvironmentApiCredential = "target-environment-api";

    public const string ApiCredentialNote =
        "Uses the API credential the Local HTTPS proxy captured from your browser session for this Target Environment. The run is blocked unless " +
        "it is present when the run starts, covers the target host and stays valid for the whole run. It acts as your own account.";

    /// <summary>Why a configured authentication cannot run in k6 (Readiness wording; the step uses <see cref="ExecutionCredentialMissing"/>).</summary>
    public const string NoExecutionCredential =
        "Target Environment authentication is configured for attended/browser use, but no approved non-interactive credential is available to k6. " +
        "Interactive or MFA sign-ins are never simulated. Use an unauthenticated test endpoint or run this test outside BirkNext.";

    public const string ExecutionCredentialMissing =
        "Target Environment authentication is configured, but no non-interactive credential is available for performance execution.";

    /// <summary>Display name of an authentication reference (never a token).</summary>
    public static string AuthenticationSourceName(string reference) => reference switch
    {
        TargetEnvironmentAuthentication => "Target Environment",
        TargetEnvironmentApiCredential => "Target Environment API credential (Local HTTPS proxy)",
        _ => reference,
    };

    /// <summary>The configured authentication of an executed step and whether k6 can execute it; null when the step runs without authentication.
    /// Only the proxy-captured API credential is executable (its presence and lifetime are checked when the run starts); attended sign-in never is.</summary>
    public static StepExecutionCredential? ExecutionCredential(PerformanceTestDefinition d, HttpPerformanceStep s) =>
        s.Disabled || EffectiveAuthentication(d, s) is not { } reference ? null
            : reference == TargetEnvironmentApiCredential
                ? new(reference, AuthenticationSourceName(reference), Interactive: false, PerformanceCompatible: true, ApiCredentialNote)
                : new(reference, AuthenticationSourceName(reference), Interactive: reference == TargetEnvironmentAuthentication, PerformanceCompatible: false, ExecutionCredentialMissing);

    /// <summary>True when an executed step sends the proxy-captured API credential (the run then needs it leased).</summary>
    public static bool UsesApiCredential(PerformanceTestDefinition d) =>
        d.Scenario.Steps.Any(s => !s.Disabled && EffectiveAuthentication(d, s) == TargetEnvironmentApiCredential);

    /// <summary>Why the captured credential cannot serve this run (null = it can): present, in scope and valid until the run's end plus a margin.</summary>
    public static string? ApiCredentialProblem(bool available, bool inScope, DateTimeOffset? expiresAt, DateTimeOffset now, int runSeconds) =>
        !available ? "No API credential has been captured for this Target Environment. Start the Local HTTPS proxy (Target Environments → Authentication) and use the application in the proxy browser, then run again."
        : !inScope ? "The captured API credential does not cover the scenario's target host (it is limited to the Target Environment's approved HTTPS hosts)."
        : expiresAt is { } end && end < now.AddSeconds(runSeconds + ApiCredentialMarginSeconds)
            ? $"The captured API credential expires at {end.UtcDateTime:HH:mm} UTC, before this run would end. Use the application in the proxy browser to refresh it, or shorten the run."
        : null;

    public const int ApiCredentialMarginSeconds = 120;

    /// <summary>True when an executed step (or, with no steps, the default) uses an authentication k6 cannot execute.</summary>
    public static bool ExecutionCredentialBlocked(PerformanceTestDefinition d) =>
        d.Scenario.Steps.Any(s => ExecutionCredential(d, s) is { PerformanceCompatible: false }) || d.Scenario.Steps.Count == 0 && d.AuthenticationReference is not null;

    // ── Authentication readiness (the one evaluator: candidate, step, scenario summary, Readiness tab, Run, backend start) ─────────────────────
    // A requirement comes from evidence: authentication observed by Endpoint Discovery (on the step when it was created, or in the persisted
    // observation history now) or declared by the contract. It is satisfied by a configured authentication, or by an explicit, still-valid
    // unauthenticated override. A default None is NEVER a confirmation.

    /// <summary>The Endpoint Discovery identity of a step: its recorded identity, else "METHOD /route/{id}" (every {param} and identifier-like segment templated).</summary>
    public static string ObservationIdentity(HttpPerformanceStep s) => s.Source?.EvidenceIdentity is { Length: > 0 } identity ? identity
        : $"{(s.Method ?? "GET").Trim().ToUpperInvariant()} {PerformanceScenarioEngine.Template(System.Text.RegularExpressions.Regex.Replace(s.RelativePath ?? "", "\\{[^}/]+\\}", "{id}"))}";

    public static IReadOnlyList<StepAuthenticationRequirement> AuthenticationRequirements(PerformanceTestDefinition d, IReadOnlySet<string>? observedAuthentication = null)
    {
        var raw = d.Scenario.Steps.Select((s, i) => (s, i)).Where(x => !x.s.Disabled).Select(x =>
        {
            var identity = ObservationIdentity(x.s);
            var requirement = x.s.Source?.ObservedAuthentication
                ?? (observedAuthentication?.Contains(identity) == true ? "Authentication observed by Endpoint Discovery" : null);
            return (x.s, x.i, identity, requirement, effective: EffectiveAuthentication(d, x.s));
        }).ToList();
        var fingerprint = Fingerprint(raw.Where(r => r.requirement is not null && r.effective is null).Select(r => (r.identity, r.requirement!)));
        var overridden = d.UnauthenticatedOverride is { } o && o.EvidenceFingerprint.Length > 0 && o.EvidenceFingerprint == fingerprint;
        return raw.Select(r => new StepAuthenticationRequirement(r.i, r.s.Name, $"{(r.s.Method ?? "").ToUpperInvariant()} {r.s.RelativePath}".Trim(), r.identity, r.requirement, r.effective,
            r.requirement is null ? StepAuthenticationState.NotRequired
            : r.effective is not null ? StepAuthenticationState.Configured
            : overridden ? StepAuthenticationState.IntentionallyUnauthenticated
            : d.UnauthenticatedOverride is not null ? StepAuthenticationState.OverrideOutdated
            : StepAuthenticationState.Missing)).ToList();
    }

    /// <summary>Identities + requirements (templated routes only — no value, no credential), order-independent.</summary>
    private static string Fingerprint(IEnumerable<(string Identity, string Requirement)> required) =>
        string.Join("\n", required.Select(r => $"{r.Identity}|{r.Requirement}").Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>The explicit decision to run every authentication-requiring, unconfigured operation without authentication, as of now.</summary>
    public static UnauthenticatedExecutionOverride AcknowledgeUnauthenticated(PerformanceTestDefinition d, IReadOnlySet<string>? observedAuthentication, DateTimeOffset now, string? actor)
    {
        var missing = AuthenticationRequirements(d with { UnauthenticatedOverride = null }, observedAuthentication)
            .Where(r => r.State == StepAuthenticationState.Missing).ToList();
        return new UnauthenticatedExecutionOverride
        {
            AcknowledgedAt = now, AcknowledgedBy = actor, Operations = [.. missing.Select(r => r.Identity).Distinct(StringComparer.Ordinal)],
            EvidenceFingerprint = Fingerprint(missing.Select(r => (r.Identity, r.Requirement!))),
        };
    }

    public static string AuthenticationRequirementProblem(StepAuthenticationRequirement r) => r.State switch
    {
        StepAuthenticationState.OverrideOutdated =>
            $"{r.Operation}: {r.Requirement} — the earlier decision to run without authentication no longer matches the scenario or endpoint evidence; configure authentication or confirm again",
        _ => $"{r.Operation}: {r.Requirement}, but no authentication is configured for this operation (None is not a confirmation)",
    };

    public static string AuthenticationLabel(PerformanceTestDefinition d, HttpPerformanceStep s) => s.Authentication switch
    {
        StepAuthenticationMode.None => "None",
        StepAuthenticationMode.Override => s.AuthenticationReference is null ? "Override · not chosen" : $"Override · {AuthenticationSourceName(s.AuthenticationReference)} authentication",
        _ => d.AuthenticationReference is null ? "Inherit scenario default (None)" : $"Inherit scenario default ({AuthenticationSourceName(d.AuthenticationReference)} authentication)",
    };

    private static string Step(int index) => $"Step {index + 1}";

    /// <summary>Why a parameter has no usable value in this position of the scenario — null when it resolves.</summary>
    /// <remarks><paramref name="knownAliases"/> (the Test Data catalog, when the caller has it): a mapped alias that no longer exists is reported as a
    /// missing reference — never silently treated as mapped, never replaced by another asset.</remarks>
    public static string? Problem(PerformanceScenario scenario, int stepIndex, RuntimeParameter p, ScenarioDataMapping? m, IReadOnlySet<string>? knownAliases = null)
    {
        if (m is null) return "Runtime value missing — choose a value source";
        switch (m.Source)
        {
            case RuntimeValueSource.TestData:
                if (m.AssetAliases.Count == 0) return "Test Data asset not chosen";
                return knownAliases is not null && m.AssetAliases.FirstOrDefault(a => !knownAliases.Contains(a)) is { } missing
                    ? $"Test Data reference missing: {missing} no longer exists — remap this value" : null;
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
                var shape = scenario.Steps[stepIndex].Source?.Parameters?.FirstOrDefault(x => x.Name == p.Variable);
                return string.IsNullOrEmpty(m.FixedValue) ? "Fixed value is empty" : TypeProblem(m.FixedValue, m.DeclaredType ?? shape?.Type, m.AllowedValues ?? shape?.Enum);
            case RuntimeValueSource.AutoFilled:
                if (string.IsNullOrEmpty(m.FixedValue)) return "Runtime value missing — choose a value source";
                if (m.NeedsReview) return $"Needs review: the {m.ValueOrigin ?? "suggested"} value and values observed by Live Discovery may differ — confirm it or choose another source";
                // A value observed at runtime is a candidate, not approved Test Data: it never resolves a parameter by itself. Map approved
                // Test Data, or enter it as a fixed value when it is approved for this environment (an explicit decision).
                if (m.ValueOrigin == RuntimeValueOrigins.LiveDiscovery)
                    return $"Observed value is a candidate, not approved Test Data — map approved Test Data to {{{p.Variable}}} or enter it as a fixed value if it is approved for this environment";
                return TypeProblem(m.FixedValue, m.DeclaredType, m.AllowedValues);
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
    public static IReadOnlyList<RuntimeValueRow> Rows(PerformanceScenario scenario, int stepIndex, IReadOnlySet<string>? knownAliases = null)
    {
        var step = scenario.Steps[stepIndex];
        return [.. Parameters(step).Select(p =>
        {
            var m = step.DataMappings?.FirstOrDefault(x => x.Variable == p.Variable);
            return new RuntimeValueRow(p, m, m is null ? "Not configured" : SourceLabel(m), m is null ? "—" : ValueText(scenario, m), Problem(scenario, stepIndex, p, m, knownAliases));
        })];
    }

    /// <summary>The value/reference shown for a mapping — an alias, step output, variable, fixed value or generator; never a Test Data value.</summary>
    public static string ValueText(PerformanceScenario scenario, ScenarioDataMapping m) => m.Source switch
    {
        RuntimeValueSource.TestData => m.AssetAliases.Count == 0 ? "—" : string.Join(", ", m.AssetAliases) + (m.FromObservedSample ? " (from reviewed observed sample)" : ""),
        RuntimeValueSource.PreviousStep => IndexOf(scenario.Steps, m.FromStepId) is var i and >= 0 ? $"{Step(i)} · {scenario.Steps[i].Name} · {m.Output}" : $"(missing step) · {m.Output}",
        RuntimeValueSource.ScenarioVariable => $"{{{{{m.ScenarioVariable}}}}}",
        RuntimeValueSource.Fixed => m.FixedValue ?? "",
        RuntimeValueSource.AutoFilled => m.FixedValue ?? "—",
        _ => m.Generator is { } g ? GeneratorLabel(g) : "—",
    };

    /// <summary>Scenario summary: runtime values resolved of all executed parameters, and distinct Test Data assets used (Test Data is optional).</summary>
    public static (int Resolved, int Total, int TestDataAssets) ValuesSummary(PerformanceScenario scenario, IReadOnlySet<string>? knownAliases = null)
    {
        var rows = Enumerable.Range(0, scenario.Steps.Count).Where(i => !scenario.Steps[i].Disabled).SelectMany(i => Rows(scenario, i, knownAliases)).ToList();
        var assets = rows.Where(r => r.Mapping?.Source == RuntimeValueSource.TestData).SelectMany(r => r.Mapping!.AssetAliases)
            .Concat((scenario.Variables ?? []).Where(v => v.Source == RuntimeValueSource.TestData && v.AssetAlias is not null
                && rows.Any(r => r.Mapping?.Source == RuntimeValueSource.ScenarioVariable && r.Mapping.ScenarioVariable == v.Name)).Select(v => v.AssetAlias!))
            .Distinct(StringComparer.Ordinal).Count();
        return (rows.Count(r => r.Resolved), rows.Count, assets);
    }

    /// <summary>
    /// One primary status per step: Disabled; Blocked (performance safety: write method, REST POST not marked read-only, missing/invalid path,
    /// invalid ordering); Needs configuration (an unresolved runtime value); otherwise Ready. Reasons are precise ("Step 2 · brukerId: …").
    /// </summary>
    public static StepReadiness Readiness(PerformanceTestDefinition d, int index, IReadOnlySet<string>? knownAliases = null, IReadOnlySet<string>? observedAuthentication = null)
    {
        var s = d.Scenario.Steps[index];
        if (s.Disabled) return new(StepStatus.Disabled, [PerformanceDiscoveryDrafts.IsDraft(s) ? $"{PerformanceDiscoveryDrafts.Label} — created from Endpoint Discovery; not run until accepted" : "Disabled — stays in the definition but does not run"]);
        var blocked = new List<string>();
        var method = (s.Method ?? "").Trim().ToUpperInvariant();
        if (PerformanceTestRules.BlockedMethods.Contains(method)) blocked.Add($"{Step(index)}: {method} changes data — performance tests use read-only operations");
        else if (method == "POST" && d.TargetType != PerformanceTargetType.GraphQlHttp && !s.PostIsSafeRead)
            blocked.Add($"{Step(index)}: a REST POST runs only when marked \"This POST only reads\"");
        if (string.IsNullOrWhiteSpace(s.RelativePath)) blocked.Add($"{Step(index)}: enter the request path");
        else if (!s.RelativePath.StartsWith('/')) blocked.Add($"{Step(index)}: the path must start with '/'");
        var rows = Rows(d.Scenario, index, knownAliases);
        blocked.AddRange(rows.Where(r => r.Problem?.Contains("invalid execution order", StringComparison.Ordinal) == true).Select(r => $"{Step(index)} · {r.Parameter.Variable}: {r.Problem}"));
        foreach (var o in s.Outputs ?? [])
            if (PerformanceRequestResponse.OutputProblem(o) is { } outputProblem) blocked.Add($"{Step(index)}: {outputProblem}");
        if (PerformanceRequestResponse.BodyProblem(s, d.TargetType == PerformanceTargetType.GraphQlHttp) is { } bodyProblem) blocked.Add($"{Step(index)}: {bodyProblem}");
        if (blocked.Count > 0) return new(StepStatus.Blocked, blocked);
        var open = rows.Where(r => !r.Resolved).Select(r => $"{Step(index)} · {r.Parameter.Variable}: {r.Problem}").ToList();
        if (s.Authentication == StepAuthenticationMode.Override && s.AuthenticationReference is null) open.Add($"{Step(index)}: authentication override has no configuration");
        if (AuthenticationRequirements(d, observedAuthentication).FirstOrDefault(r => r.Index == index) is { State: StepAuthenticationState.Missing or StepAuthenticationState.OverrideOutdated } auth)
            open.Add($"{Step(index)}: authentication required — {AuthenticationRequirementProblem(auth)}");
        return open.Count > 0 ? new(StepStatus.NeedsConfiguration, open) : new(StepStatus.Ready, []);
    }

    /// <summary>
    /// The step's Definition / Runtime values / Execution statuses, from the same rules as <see cref="Readiness"/> (which stays the one primary
    /// configuration status). Execution is blocked by a configured authentication k6 cannot execute, and needs configuration while an
    /// authentication requirement is unmet.
    /// </summary>
    public static StepDimensions Dimensions(PerformanceTestDefinition d, int index, IReadOnlySet<string>? knownAliases = null, IReadOnlySet<string>? observedAuthentication = null)
    {
        var s = d.Scenario.Steps[index];
        var readiness = Readiness(d, index, knownAliases, observedAuthentication);
        if (readiness.Status == StepStatus.Disabled)
        {
            var off = new StepDimension(StepDimensionState.NotApplicable, "Disabled");
            return new(off, off, off, null);
        }
        var credential = ExecutionCredential(d, s);
        var definition = readiness.Status == StepStatus.Blocked ? new StepDimension(StepDimensionState.Blocked, readiness.Reasons[0]) : new StepDimension(StepDimensionState.Ready, "Ready");
        var rows = Rows(d.Scenario, index, knownAliases);
        var unresolved = rows.Count(r => !r.Resolved);
        var sources = string.Join(", ", rows.Where(r => r.Mapping is not null).Select(r => r.SourceLabel).Distinct(StringComparer.Ordinal));
        var values = rows.Count == 0 ? new StepDimension(StepDimensionState.NotApplicable, "No dynamic parameters")
            : unresolved > 0 ? new StepDimension(StepDimensionState.NeedsConfiguration, $"{unresolved} of {rows.Count} unresolved")
            : new StepDimension(StepDimensionState.Ready, sources.Length > 0 ? $"Ready — {sources}" : "Ready");
        var requirement = AuthenticationRequirements(d, observedAuthentication).FirstOrDefault(r => r.Index == index);
        var execution = credential is { PerformanceCompatible: false } ? new StepDimension(StepDimensionState.Blocked, "Blocked — authentication")
            : s.Authentication == StepAuthenticationMode.Override && s.AuthenticationReference is null ? new StepDimension(StepDimensionState.NeedsConfiguration, "Authentication override has no configuration")
            : requirement is { State: StepAuthenticationState.Missing or StepAuthenticationState.OverrideOutdated } ? new StepDimension(StepDimensionState.NeedsConfiguration, "Authentication required")
            : new StepDimension(StepDimensionState.Ready, "Ready");
        return new(definition, values, execution, credential);
    }

    public static string StatusLabel(StepStatus status) => status switch
    {
        StepStatus.NeedsConfiguration => "Needs configuration",
        _ => status.ToString(),
    };

    /// <summary>Scenario-level summary of the step statuses: Blocked wins, then Needs configuration; Ready only with at least one enabled step.</summary>
    public static StepReadiness ScenarioReadiness(PerformanceTestDefinition d, IReadOnlySet<string>? knownAliases = null, IReadOnlySet<string>? observedAuthentication = null)
    {
        var steps = Enumerable.Range(0, d.Scenario.Steps.Count).Select(i => Readiness(d, i, knownAliases, observedAuthentication)).ToList();
        var reasons = steps.SelectMany(r => r.Status == StepStatus.Disabled ? [] : r.Reasons).ToList();
        reasons.AddRange((d.Scenario.Variables ?? []).Select(VariableProblem).OfType<string>());
        var status = steps.Any(r => r.Status == StepStatus.Blocked) ? StepStatus.Blocked
            : steps.Any(r => r.Status == StepStatus.NeedsConfiguration) || reasons.Count > 0 ? StepStatus.NeedsConfiguration
            : steps.Any(r => r.Status == StepStatus.Ready) ? StepStatus.Ready : StepStatus.NeedsConfiguration;
        if (steps.All(r => r.Status == StepStatus.Disabled)) reasons.Insert(0, d.Scenario.Steps.Count == 0 ? "The scenario has no steps" : d.Scenario.Steps.Any(PerformanceDiscoveryDrafts.IsDraft) ? "Every step is disabled or a discovered draft awaiting review" : "Every step is disabled");
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
