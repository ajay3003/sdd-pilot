using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.IntegrationSuites;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.IntegrationSuites.Composite;

/// <summary>
/// Proposes the composite transaction plan (pure): one continuous chain across the stages' contracts. The first stage with a create operation
/// executes; every later stage is observed through a read operation whose path parameter is a DOCUMENTED response field of an earlier step (the
/// explicit identifier mapping — never a guess from timing); a stage with action words (e.g. a period lock) gets its state-changing operation.
/// Observers wait boundedly for asynchronous outcomes; the plan is only a proposal and becomes a Draft chain that a person approves.
/// </summary>
public static class CompositePlanner
{
    public sealed record StageContract(string ContractKey, int Version, SourceContract Document, IReadOnlyDictionary<string, Guid> PositiveTests);

    public sealed record StageInput(string StageId, string Label, StageSourceRequirement Requirement, IReadOnlyList<StageContract> Contracts);

    public const int ObserveAttempts = 5;
    public const int ObserveDelayMs = 2000;
    public static readonly int[] ObserveOnStatus = [404, 409, 425];
    private static readonly string[] EntryWords = ["submission", "submissions", "submit", "report", "reports", "rapport", "rapporter", "melding", "meldinger", "message", "messages", "instance", "instances", "innsending"];

    public static CompositePlan Plan(IReadOnlyList<StageInput> stages)
    {
        var steps = new List<CompositePlanStep>();
        var missing = new List<string>();
        var available = new List<(string StepId, string StageId, ContractTypeShape? Type)>();
        var n = 0;
        foreach (var stage in stages)
        {
            if (stage.Contracts.Count == 0) { missing.Add($"{stage.Label}: no REST contract in the stage's source — the stage cannot be executed or observed by a contract test."); continue; }
            CompositePlanStep? step = null;
            if (steps.Count == 0)
                step = Entry(stage, ++n);
            else if (stage.Requirement.ActionWords.Count > 0)
                step = Action(stage, ++n, available);
            if (step is null && steps.Count > 0)
                step = Observer(stage, steps.Count + 1, available);
            if (step is null)
            {
                missing.Add(steps.Count == 0
                    ? $"{stage.Label}: no create operation (POST to a collection) to start the transaction."
                    : $"{stage.Label}: no read operation whose path parameter is a documented response field of an earlier stage — the stage is NOT_OBSERVABLE.");
                continue;
            }
            n = steps.Count + 1;
            step = step with { StepId = $"s{steps.Count + 1}-{Slug(stage.StageId)}" };
            if (step.TestId is null) missing.Add($"{stage.Label}: {step.Operation} has no positive contract test — generate tests first.");
            // Re-point later references: FromStep was computed against the provisional id list, which equals the final ids by construction.
            steps.Add(step);
            available.Add((step.StepId, stage.StageId, ResponseType(stage, step)));
        }
        var confidence = steps.Count == 0 ? "None" : steps.All(s => s.FromField is null || s.Parameter is not null && Same(s.FromField, s.Parameter)) ? "High (identifier names match exactly)" : "Medium (an identifier is matched by the id convention)";
        return new CompositePlan { Steps = steps, Missing = missing, Confidence = confidence, State = "Proposed" };
    }

    private static CompositePlanStep? Entry(StageInput stage, int index)
    {
        var options = stage.Contracts.SelectMany(c => c.Document.Operations.Where(o => o.Kind.Equals("POST", StringComparison.OrdinalIgnoreCase) && o.Path is { Length: > 0 } && !o.Path.TrimEnd('/').EndsWith('}'))
            .Select(o => (Contract: c, Op: o))).OrderByDescending(x => Words(x.Op.Path!).Any(w => EntryWords.Contains(w))).ThenBy(x => x.Op.Path, StringComparer.Ordinal).ToList();
        if (options.Count == 0) return null;
        var (contract, op) = options[0];
        var type = Type(contract.Document, op, "201", "202", "200");
        return new CompositePlanStep
        {
            StepId = $"s{index}", StageId = stage.StageId, Role = "Executor", TestId = contract.PositiveTests.TryGetValue(op.Name, out var t) ? t : null,
            ContractRef = ContractIdentity.ContractRef(contract.ContractKey, contract.Version), Operation = op.Name, Mutating = true,
            ExtractField = IdFields(type, stage.Requirement.CorrelationFields).FirstOrDefault(),
            Evidence = [$"{op.Name} creates the transaction (POST to a collection){(options.Count > 1 ? $"; {options.Count - 1} other create operation(s) not chosen" : "")}",
                        type is null ? "No documented response type: nothing can be correlated downstream." : $"Documented response {type.Name}: {string.Join(", ", type.Fields.Select(f => f.Name).Take(8))}"],
        };
    }

    private static CompositePlanStep? Action(StageInput stage, int index, List<(string StepId, string StageId, ContractTypeShape? Type)> available)
    {
        foreach (var c in stage.Contracts)
            foreach (var op in c.Document.Operations.Where(o => o.Kind.ToUpperInvariant() is "POST" or "PUT" or "PATCH" && o.Path is { Length: > 0 }))
            {
                var words = Words(op.Path!);
                if (!stage.Requirement.ActionWords.Any(a => words.Contains(ArchitectureRuleEvaluator.Normalize(a)))) continue;
                var parameters = PathParameters(op.Path!);
                var link = parameters.Count == 1 ? Link(available, parameters[0]) : default;
                if (parameters.Count > 1 || parameters.Count == 1 && link.Field is null) continue;
                var type = Type(c.Document, op, "200", "201", "202");
                return new CompositePlanStep
                {
                    StepId = $"s{index}", StageId = stage.StageId, Role = "Action", TestId = c.PositiveTests.TryGetValue(op.Name, out var t) ? t : null,
                    ContractRef = ContractIdentity.ContractRef(c.ContractKey, c.Version), Operation = op.Name, Mutating = true,
                    FromStep = link.StepId, FromField = link.Field?.Name, Parameter = parameters.FirstOrDefault(),
                    EchoField = link.Field is null ? null : type?.Fields.FirstOrDefault(f => Same(f.Name, link.Field.Name))?.Name,
                    ExtractField = IdFields(type, stage.Requirement.CorrelationFields).FirstOrDefault(),
                    Evidence = [$"{op.Name} is the stage's state-changing action (path word: {string.Join(", ", stage.Requirement.ActionWords.Where(a => words.Contains(ArchitectureRuleEvaluator.Normalize(a))))})",
                                .. link.Field is null ? Array.Empty<string>() : [$"{{{parameters[0]}}} ← response field \"{link.Field.Name}\" of {link.StepId}"]],
                };
            }
        return null;
    }

    private static CompositePlanStep? Observer(StageInput stage, int index, List<(string StepId, string StageId, ContractTypeShape? Type)> available)
    {
        foreach (var c in stage.Contracts)
            foreach (var op in c.Document.Operations.Where(o => o.Kind.Equals("GET", StringComparison.OrdinalIgnoreCase) && o.Path is { Length: > 0 }).OrderBy(o => o.Path, StringComparer.Ordinal))
            {
                var parameters = PathParameters(op.Path!);
                if (parameters.Count != 1) continue;
                var link = Link(available, parameters[0]);
                if (link.Field is null) continue;
                var type = Type(c.Document, op, "200");
                var echo = type?.Fields.FirstOrDefault(f => Same(f.Name, link.Field.Name))?.Name;
                return new CompositePlanStep
                {
                    StepId = $"s{index}", StageId = stage.StageId, Role = "Observer", TestId = c.PositiveTests.TryGetValue(op.Name, out var t) ? t : null,
                    ContractRef = ContractIdentity.ContractRef(c.ContractKey, c.Version), Operation = op.Name, Mutating = false,
                    FromStep = link.StepId, FromField = link.Field.Name, Parameter = parameters[0], EchoField = echo,
                    ExtractField = IdFields(type, stage.Requirement.CorrelationFields).FirstOrDefault(f => !Same(f, link.Field.Name)) ?? IdFields(type, stage.Requirement.CorrelationFields).FirstOrDefault(),
                    ObserveAttempts = ObserveAttempts, ObserveDelayMs = ObserveDelayMs,
                    Evidence = [$"{{{parameters[0]}}} of {op.Name} ← documented response field \"{link.Field.Name}\" of {link.StepId}",
                                echo is null ? "The response does not echo the upstream identifier: correlation is carried, not confirmed." : $"The response echoes \"{echo}\": correlation is checked against the upstream value.",
                                $"Waits for the asynchronous outcome: up to {ObserveAttempts} reads {ObserveDelayMs} ms apart while it answers {string.Join("/", ObserveOnStatus)}."],
                };
            }
        return null;
    }

    /// <summary>The latest earlier step whose documented response provides a field for the parameter (same name, or "id" for "{x}Id").</summary>
    private static (string? StepId, ContractField? Field) Link(List<(string StepId, string StageId, ContractTypeShape? Type)> available, string parameter)
    {
        for (var i = available.Count - 1; i >= 0; i--)
            if (FieldFor(available[i].Type, parameter) is { } f) return (available[i].StepId, f);
        return (null, null);
    }

    private static ContractField? FieldFor(ContractTypeShape? type, string parameter) =>
        type?.Fields.FirstOrDefault(f => Same(f.Name, parameter))
        ?? (parameter.Equals("id", StringComparison.OrdinalIgnoreCase) || parameter.EndsWith("Id", StringComparison.Ordinal) ? type?.Fields.FirstOrDefault(f => f.Name.Equals("id", StringComparison.OrdinalIgnoreCase)) : null);

    private static bool Same(string a, string b) => ArchitectureRuleEvaluator.Normalize(a) == ArchitectureRuleEvaluator.Normalize(b);

    private static IEnumerable<string> IdFields(ContractTypeShape? type, IReadOnlyList<string> correlation) =>
        type is null ? [] : type.Fields.Select(f => f.Name)
            .Where(n => correlation.Any(c => Same(c, n)) || n.EndsWith("Id", StringComparison.Ordinal) || n.Equals("id", StringComparison.OrdinalIgnoreCase) || Same(n, "period") || Same(n, "periode"))
            .OrderByDescending(n => correlation.Any(c => Same(c, n)));

    private static ContractTypeShape? Type(SourceContract doc, ContractOperation op, params string[] codes)
    {
        foreach (var code in codes)
            if (op.ResponseSchemas.TryGetValue(code, out var label) && !label.StartsWith('[') && doc.Types.FirstOrDefault(t => t.Name == label) is { } shape) return shape;
        return null;
    }

    private static ContractTypeShape? ResponseType(StageInput stage, CompositePlanStep step)
    {
        foreach (var c in stage.Contracts)
            if (c.Document.Operations.FirstOrDefault(o => o.Name == step.Operation) is { } op)
                return Type(c.Document, op, step.Role == "Observer" ? ["200"] : ["201", "202", "200"]);
        return null;
    }

    public static List<string> PathParameters(string path) =>
        [.. path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s.Length > 2 && s[0] == '{' && s[^1] == '}').Select(s => s[1..^1])];

    private static HashSet<string> Words(string path) => [.. path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => !s.StartsWith('{')).SelectMany(ArchitectureRuleEvaluator.Words)];

    private static string Slug(string id) => new([.. id.Where(char.IsLetterOrDigit)]);

    /// <summary>The Contract Testing chain steps of an approved-to-be plan (linear: each step depends on the previous one).</summary>
    public static List<ContractChainStep> ChainSteps(CompositePlan plan)
    {
        var result = new List<ContractChainStep>();
        string? previous = null;
        foreach (var s in plan.Steps.Where(s => s.TestId is not null))
        {
            // Every field a later step takes from this one, plus its own identifier; the echoed upstream identifier under "echo_<field>".
            var fields = plan.Steps.Where(x => x.FromStep == s.StepId && x.FromField is not null).Select(x => x.FromField!)
                .Concat(s.ExtractField is { } own ? [own] : []).Distinct(StringComparer.Ordinal).ToList();
            var extract = fields.Select(f => new OutputExtraction(Output(f), OutputSource.JsonBody, "$." + f)).ToList();
            if (s.EchoField is { } e) extract.Add(new OutputExtraction(EchoOutput(e), OutputSource.JsonBody, "$." + e));
            result.Add(new ContractChainStep
            {
                StepId = s.StepId, TestId = s.TestId!.Value, Extract = extract,
                Inputs = s.FromStep is { } from && s.FromField is { } field && s.Parameter is { } p ? [new ContractChainInput(p, from, Output(field))] : [],
                DependsOn = previous is null || s.FromStep == previous ? [] : [previous],
                Observe = s.Role == "Observer" ? new ChainObservation(s.ObserveAttempts, s.ObserveDelayMs, [.. ObserveOnStatus]) : null,
            });
            previous = s.StepId;
        }
        return result;
    }

    /// <summary>Output names are identifiers (letters, digits, '_').</summary>
    public static string EchoOutput(string field) => "echo_" + Output(field);

    public static string Output(string field) => new([.. field.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]);
}
