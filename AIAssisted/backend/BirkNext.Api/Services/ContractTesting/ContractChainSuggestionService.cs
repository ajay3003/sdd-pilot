using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Data;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.SourceDomains;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ContractTesting;

/// <summary>
/// Evidence-backed chain suggestions for one contract (REST): a create operation (POST on a collection path) whose documented 2xx response type has
/// a field that names the item path parameter of later operations on the same collection (<c>POST /orders</c> → <c>{ id }</c> →
/// <c>GET /orders/{id}</c>, <c>PUT /orders/{orderId}</c> with <c>orderId</c>/<c>id</c>); and list → details when the list response is an array of a
/// type with that field. The binding needs a typed response field — a field that merely looks like an id in an unrelated type, or an untyped
/// response, is never used. Delete is never added (no automatic destructive step). Suggestions are never run; accepting one creates a Draft chain
/// through the existing chain store, which refuses to run drafts.
/// </summary>
public sealed class ContractChainSuggestionService(AppDbContext db, ContractRegistryService registry, ContractTestService tests, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<IReadOnlyList<ChainSuggestion>?> SuggestAsync(string key, ContractTestEnvironment? environment, CancellationToken ct)
    {
        if (await registry.LatestVersionAsync(key, ct) is not { } version || await registry.DocumentAsync(key, version, ct) is not { } doc) return null;
        var views = await tests.ListAsync(key, environment, ct);
        var chains = await tests.ChainsAsync(ct);
        var rejected = (await db.ContractReviewDecisions.AsNoTracking().Where(d => d.Kind == "chain-reject").Select(d => d.SubjectKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var result = new List<ChainSuggestion>();
        var ops = doc.Operations.Where(o => o.Path is { Length: > 0 }).ToList();
        foreach (var create in ops.Where(o => o.Kind.Equals("POST", StringComparison.OrdinalIgnoreCase) && !o.Path!.TrimEnd('/').EndsWith('}')))
        {
            var collection = create.Path!.TrimEnd('/');
            var items = ops.Where(o => ItemParameter(collection, o.Path!) is not null).ToList();
            if (items.Count == 0) continue;
            var type = ResponseType(doc, create, "201", "200");
            foreach (var read in items.Where(o => o.Kind.Equals("GET", StringComparison.OrdinalIgnoreCase)))
            {
                var parameter = ItemParameter(collection, read.Path!)!;
                if (FieldFor(type, parameter) is not { } field) continue;
                var update = items.FirstOrDefault(o => o.Kind.ToUpperInvariant() is "PUT" or "PATCH" && ItemParameter(collection, o.Path!) is { } p2 && FieldFor(type, p2) is not null);
                var steps = new List<(ContractOperation Op, string Parameter)> { (create, ""), };
                if (update is not null) steps.Add((update, ItemParameter(collection, update.Path!)!));
                steps.Add((read, parameter));
                var purpose = update is null ? $"Create then read: {create.Name} → {read.Name}" : $"Create, update, read: {create.Name} → {update.Name} → {read.Name}";
                var evidence = new List<string>
                {
                    $"{create.Name} documents a {string.Join("/", create.ResponseCodes.Where(c => c is "201" or "200"))} response of type {type!.Name} with field \"{field.Name}\" ({field.Type})",
                    $"{read.Name} is the item path of the same collection {collection} with path parameter {{{parameter}}}",
                };
                if (update is not null) evidence.Add($"{update.Name} updates the same item ({{{ItemParameter(collection, update.Path!)}}})");
                result.Add(Build(key, purpose, steps, field, "$." + field.Name, evidence, field.Name.Equals(parameter, StringComparison.OrdinalIgnoreCase) ? "High" : "Medium", views, chains, rejected));
            }
        }
        // List → details: a GET collection returning an array of a type whose field names the item parameter.
        foreach (var list in ops.Where(o => o.Kind.Equals("GET", StringComparison.OrdinalIgnoreCase) && !o.Path!.TrimEnd('/').EndsWith('}')))
        {
            var collection = list.Path!.TrimEnd('/');
            if (!(list.ResponseSchemas.TryGetValue("200", out var label) && label.StartsWith('[') && label.EndsWith(']'))) continue;
            var itemType = doc.Types.FirstOrDefault(t => t.Name == label[1..^1]);
            foreach (var detail in ops.Where(o => o.Kind.Equals("GET", StringComparison.OrdinalIgnoreCase) && ItemParameter(collection, o.Path!) is not null))
            {
                var parameter = ItemParameter(collection, detail.Path!)!;
                if (FieldFor(itemType, parameter) is not { } field) continue;
                result.Add(Build(key, $"Search then details: {list.Name} → {detail.Name}", [(list, ""), (detail, parameter)], field, $"$[0].{field.Name}",
                    [$"{list.Name} documents a 200 array of {itemType!.Name} with field \"{field.Name}\" ({field.Type})", $"{detail.Name} is the item path of {collection} with {{{parameter}}}",
                     "Uses the first listed item: the list must not be empty in the target environment."], "Medium", views, chains, rejected));
            }
        }
        return [.. result.Where(s => !rejected.Contains(s.Id)).DistinctBy(s => s.Id)];
    }

    /// <summary>The item path parameter when <paramref name="path"/> is exactly <paramref name="collection"/>/{param}; otherwise null.</summary>
    private static string? ItemParameter(string collection, string path)
    {
        var p = path.TrimEnd('/');
        if (!p.StartsWith(collection + "/", StringComparison.Ordinal)) return null;
        var rest = p[(collection.Length + 1)..];
        return rest.Length > 2 && rest[0] == '{' && rest[^1] == '}' && !rest.Contains('/') ? rest[1..^1] : null;
    }

    private static ContractTypeShape? ResponseType(SourceContract doc, ContractOperation op, params string[] codes)
    {
        foreach (var code in codes)
            if (op.ResponseSchemas.TryGetValue(code, out var label) && !label.StartsWith('[') && doc.Types.FirstOrDefault(t => t.Name == label) is { } shape) return shape;
        return null;
    }

    /// <summary>The response field that provides a path parameter: the same name, or "id" for "{x}Id"/"{id}". Only from a documented response type.</summary>
    private static ContractField? FieldFor(ContractTypeShape? type, string parameter) =>
        type?.Fields.FirstOrDefault(f => f.Name.Equals(parameter, StringComparison.OrdinalIgnoreCase))
        ?? (parameter.Equals("id", StringComparison.OrdinalIgnoreCase) || parameter.EndsWith("Id", StringComparison.Ordinal) || parameter.EndsWith("_id", StringComparison.Ordinal)
            ? type?.Fields.FirstOrDefault(f => f.Name.Equals("id", StringComparison.OrdinalIgnoreCase)) : null);

    private static ChainSuggestion Build(string key, string purpose, List<(ContractOperation Op, string Parameter)> steps, ContractField field, string jsonPath, List<string> evidence,
        string confidence, IReadOnlyList<ContractTestView> views, IReadOnlyList<ContractTestChain> chains, HashSet<string> rejected)
    {
        var id = "sugg-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key + "|" + string.Join(">", steps.Select(s => s.Op.Name)) + "|" + jsonPath)))[..16].ToLowerInvariant();
        var missing = new List<string>();
        var built = new List<ChainSuggestionStep>();
        for (var i = 0; i < steps.Count; i++)
        {
            var (op, parameter) = steps[i];
            var test = views.Where(v => v.Test.Operation == op.Name && v.Test.Kind == ContractTestKind.Positive && v.Test.ReviewState != ContractTestReviewState.Retired)
                .OrderByDescending(v => v.Test.ContractVersion).FirstOrDefault();
            if (test is null) missing.Add($"{op.Name} has no positive test: generate tests first.");
            else if (test.Readiness.State != ContractTestReadinessState.Ready)
                missing.Add($"{op.Name}: {string.Join(" ", test.Readiness.Items.Where(x => x.State is ReadinessItemState.Missing or ReadinessItemState.Blocked).Select(x => x.Text).Take(2))}");
            if (test?.Test.Binding is { } b && i > 0) missing.Add($"{op.Name} is bound to {b.Alias}@v{b.Version}: a data-driven chain runs once per chain (one row per step), not once per dataset row.");
            var mutating = op.Kind.ToUpperInvariant() is "POST" or "PUT" or "PATCH" or "DELETE";
            built.Add(new($"step{i + 1}", op.Name, test?.Test.Id, mutating,
                i == 0 ? [new OutputExtraction(field.Name, OutputSource.JsonBody, jsonPath, field.Type.StartsWith("integer", StringComparison.Ordinal) ? "Integer" : field.Type.StartsWith("number", StringComparison.Ordinal) ? "Number" : "String")] : [],
                i == 0 ? [] : [new ContractChainInput(parameter, "step1", field.Name)]));
        }
        var accepted = chains.FirstOrDefault(c => c.SuggestionId == id);
        return new(id, purpose, built, evidence, confidence, missing, built.Any(s => s.Mutating), missing.Count == 0, accepted?.Id.ToString("D"));
    }

    /// <summary>Accepts a suggestion as a Draft chain (needs every step's test); idempotent: an accepted suggestion returns its chain.</summary>
    public async Task<ContractChainSaveResult> AcceptAsync(string key, string suggestionId, CancellationToken ct)
    {
        var suggestion = (await SuggestAsync(key, null, ct) ?? []).FirstOrDefault(s => s.Id == suggestionId);
        if (suggestion is null) return new(null, ["This suggestion is no longer offered for the contract."]);
        if (suggestion.AcceptedChainId is { } existing && Guid.TryParse(existing, out var chainId)) return new(await tests.ChainAsync(chainId, null, ct), []);
        if (suggestion.Steps.Any(s => s.TestId is null)) return new(null, ["Every step needs a positive test: generate the contract's tests first."]);
        return await tests.SaveChainAsync(null, new ContractChainSave
        {
            Name = suggestion.Purpose, Description = "Suggested from contract evidence: " + string.Join(" · ", suggestion.Evidence), State = ContractChainState.Draft, SuggestionId = suggestion.Id,
            Steps = [.. suggestion.Steps.Select(s => new ContractChainStep { StepId = s.StepId, TestId = s.TestId!.Value, Extract = s.Extract, Inputs = s.Inputs })],
        }, ct);
    }

    /// <summary>Rejects a suggestion (recorded; not offered again).</summary>
    public async Task RejectAsync(string key, string suggestionId, string by, CancellationToken ct)
    {
        db.ContractReviewDecisions.Add(new Models.ContractReviewDecisionRow
        {
            Id = Guid.NewGuid(), Kind = "chain-reject", ContractId = await db.ApiContracts.AsNoTracking().Where(c => c.Key == key).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct),
            SubjectKey = suggestionId, Detail = $"Chain suggestion {suggestionId} of {key} rejected.", DecidedBy = string.IsNullOrWhiteSpace(by) ? "local user" : by, DecidedAt = _clock.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }
}
