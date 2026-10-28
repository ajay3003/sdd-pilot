using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BirkNext.Delivery;
using BirkNext.LocalHttpsProxy;
using BirkNext.SourceDomains;
using BirkNext.TestData;

namespace BirkNext.PerformanceTests;

// ── Smart Scenario Engine ────────────────────────────────────────────────────────────────────────────────────────────────
// Contract tells what is valid (Contract Evidence of an explicitly chosen Source Analysis snapshot). Live/Endpoint Discovery tells what is
// actually used (observations: counts and timestamps, never tokens, cookies, header values or observed identifiers). Test Data supplies safe
// values (central catalog, referenced by alias). The user approves what becomes load. Manual stays available. Pure and deterministic: the same
// inputs always give the same candidates, reasons and order — no opaque ranking, no execution, nothing persisted here.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioOperationOrigin { OpenApi, GraphQl, Observed, Manual }

/// <summary>Never collapsed: declared and observed are different evidence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioEvidence { DeclaredAndObserved, DeclaredOnly, ObservedOnly, Manual }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioRecommendationLevel { Recommended, Candidate, ReviewRequired, NotRunnable }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioContractScopeState { Selected, Resolved, Candidate, Unresolved, NoContracts }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioDataMatchState { Recommended, Possible, Missing }

/// <summary>
/// The one parameter requirement model for every candidate source: name, location (path, query, header, body, GraphQL variable), data type and
/// declared type/schema, whether it is required, where the requirement comes from (<see cref="ScenarioNeedSources"/>) and whether Live Discovery
/// observed a concrete value for it. An observed value is evidence only: it is never shown by default, never copied into a scenario and never
/// satisfies Test Data readiness — the requirement stays until approved Test Data is mapped.
/// </summary>
public sealed record ScenarioDataNeed(string Variable, string In, TestDataDataType DataType, bool Required, string? Schema, string? Type,
    string Source = ScenarioNeedSources.Declared, bool ObservedValueAvailable = false);

/// <summary>Where a requirement comes from.</summary>
public static class ScenarioNeedSources
{
    public const string Declared = "Declared contract";
    /// <summary>A runtime path segment Live Discovery normalised to a placeholder ("/brukere/{id}/barn").</summary>
    public const string Observed = "Live Discovery (normalised path segment)";
    /// <summary>A variable of a captured (redacted) GraphQL query document.</summary>
    public const string CapturedQuery = "Captured GraphQL query document";
}

/// <summary>Whether running the operation needs Test Data. "Not required" only when the evidence proves no runtime value is needed.</summary>
public static class ScenarioTestDataStates
{
    public const string NotRequired = "Not required";
    public const string Required = "Required";
    /// <summary>The evidence does not show the operation's inputs (e.g. only a GraphQL operation name was observed).</summary>
    public const string Unknown = "Unknown";
}

/// <summary>What kind of traffic an operation is: normal application/API workload, a diagnostic/smoke endpoint, or technical/background traffic
/// (SPA shell, appsettings*.json, static resources, configuration/bootstrap). Classification only — evidence is never deleted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScenarioTrafficKind { Application, DiagnosticSmoke, TechnicalBackground }

/// <summary>GraphQL evidence kept separate: operation name, query document state, variables, declared schema match, observations, authentication.</summary>
public sealed record ScenarioGraphQlEvidence(string OperationName, string Kind, string DocumentState, string? Document, IReadOnlyList<(string Name, string Type)> Variables,
    bool DeclaredSchemaMatch, int ObservedCount, bool AuthObserved);

public static class ScenarioGraphQlDocumentStates
{
    public const string Captured = "Captured (literals redacted)";
    public const string NotCaptured = "Not captured";
    public const string OverLimit = "Not retained (over the size limit; hash only)";
}

/// <summary>One logical API contract (e.g. "Autorisasjon GraphQL"): the preferred evidence file, supporting copies, and whether they agree.
/// Disagreeing variants are never merged silently — the state is "Needs decision" and a person chooses the file.</summary>
public sealed record ScenarioContractGroup(string Key, string Name, SourceContractType Type, SourceContract Canonical, IReadOnlyList<SourceContract> Supporting, string State, int Operations)
{
    public const string Consistent = "Consistent", NeedsDecision = "Needs decision", SingleSource = "Single source";
    public IEnumerable<SourceContract> All => [Canonical, .. Supporting];
}

/// <summary>Observation summary of an operation: metadata only. A count is relevance evidence, never a workload (44 calls ≠ 44 req/s).</summary>
public sealed record ScenarioObservation(int Count, DateTimeOffset LastSeen, bool AuthObserved, int LastStatus, IReadOnlyList<string> Pages, int Variants);

/// <summary>Central Test Data matching one need: approved assets of the right data type and environment (name affinity ranks, never decides alone).</summary>
public sealed record ScenarioDataMatch(ScenarioDataNeed Need, ScenarioDataMatchState State, IReadOnlyList<string> Aliases, string? Recommended, string Detail);

/// <summary>A scenario candidate: one operation with its evidence, needs and an explainable recommendation. A proposal, never an active test.</summary>
public sealed record ScenarioOperation
{
    public string Key { get; init; } = "";
    public ScenarioOperationOrigin Origin { get; init; }
    public ScenarioEvidence Evidence { get; init; }
    public string Method { get; init; } = "GET";
    /// <summary>Relative path template ("/api/person/{personId}"); null for a GraphQL field.</summary>
    public string? Path { get; init; }
    public string Name { get; init; } = "";
    public string? OperationId { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<ContractParameter> Parameters { get; init; } = [];
    public string? RequestContentType { get; init; }
    public string? RequestSchema { get; init; }
    public IReadOnlyList<string> ResponseCodes { get; init; } = [];
    public IReadOnlyList<string> Security { get; init; } = [];
    /// <summary>GraphQL: "query" or "mutation" and the field's result type.</summary>
    public string? GraphQlKind { get; init; }
    public string? ResultType { get; init; }
    public string? ContractId { get; init; }
    public string? ContractName { get; init; }
    public string? ContractFile { get; init; }
    public Guid? SnapshotId { get; init; }
    public string? SnapshotName { get; init; }
    public ScenarioObservation? Observation { get; init; }
    /// <summary>Baseline read, Get by id, List/search, Pagination, Filter, Create, Update, Delete, GraphQL query, GraphQL mutation.</summary>
    public string Use { get; init; } = "";
    public IReadOnlyList<ScenarioDataNeed> Needs { get; init; } = [];
    public IReadOnlyList<ScenarioDataMatch> Data { get; init; } = [];
    public bool RequiresAuthentication { get; init; }
    /// <summary><see cref="ScenarioTestDataStates"/>.</summary>
    public string TestDataState { get; init; } = ScenarioTestDataStates.NotRequired;
    public ScenarioTrafficKind Traffic { get; init; }
    /// <summary>Deterministic evidence tier (1 = declared + observed application operation … 6 = technical/background). Lower ranks first.</summary>
    public int Tier { get; init; }
    public ScenarioGraphQlEvidence? GraphQl { get; init; }
    /// <summary>Observed GraphQL with a captured query document: offered as "Review &amp; create GraphQL definition", never added directly.</summary>
    public bool ProposedDefinition { get; init; }
    public bool Runnable { get; init; }
    public string? NotRunnableReason { get; init; }
    public ScenarioRecommendationLevel Level { get; init; }
    public int Score { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
    /// <summary>Readable, value-free shape of the declared operation (for drift review): method, path, parameters, body, responses, security.</summary>
    public string Signature { get; init; } = "";
    public bool Detailed { get; init; }
}

/// <summary>
/// Safe provenance of the values Live Discovery observed for one path placeholder. <see cref="Values"/> are evidence for an explicit "Review
/// observed value for Test Data" only — never rendered by default, never put in a URL, log, export or scenario, and never Test Data by themselves.
/// </summary>
public sealed record ObservedValueEvidence(string Variable, string In, string Template, int Observations, DateTimeOffset? LastSeen, int SuccessfulObservations,
    bool AuthenticatedTraffic, IReadOnlyList<string> Values)
{
    public override string ToString() => $"{Variable} ({In}) · {Observations} observation(s) · value hidden";
}

public sealed record ScenarioContractScope(ScenarioContractScopeState State, string Summary, IReadOnlyList<SourceContract> Relevant, IReadOnlyList<SourceContract> Other);

/// <summary>A Source Analysis snapshot that holds API contracts — listed for an explicit choice (never auto-selected).</summary>
public sealed record ScenarioSourceSnapshot(Guid Id, string Archive, DateTimeOffset AnalyzedAt, int OpenApiContracts, int GraphQlContracts);

/// <summary>The API contracts (OpenAPI, GraphQL schema) of one exact snapshot, as stored by Source Analysis.</summary>
public sealed record ScenarioContractCatalog(Guid SnapshotId, string Archive, DateTimeOffset AnalyzedAt, IReadOnlyList<SourceContract> Contracts);

/// <param name="ObservationsOnTarget">Discovery records on the target (raw, before templating).</param>
/// <param name="ObservedEndpoints">Unique runtime endpoints on the target (method + templated path, or GraphQL operation).</param>
/// <param name="CorrelatedOperations">Declared operations matched by at least one observation across the contracts in scope.</param>
public sealed record ScenarioCatalog(ScenarioContractScope Scope, IReadOnlyList<ScenarioOperation> Operations, int ObservationsOnTarget, int ObservedEndpoints = 0, int CorrelatedOperations = 0);

/// <summary>A difference between the declared operation a step was built from and the same operation in a newer analysis of the same source.</summary>
public sealed record ScenarioDrift(string Section, string Before, string After);

public static partial class PerformanceScenarioEngine
{
    private static readonly HashSet<string> ReadMethods = ["GET", "HEAD", "OPTIONS"];
    private static readonly HashSet<string> GraphQlScalars = new(StringComparer.Ordinal) { "Int", "Float", "String", "Boolean", "ID" };

    public static string EvidenceLabel(ScenarioEvidence evidence) => evidence switch
    {
        ScenarioEvidence.DeclaredAndObserved => "Declared + Observed",
        ScenarioEvidence.DeclaredOnly => "Declared only",
        ScenarioEvidence.ObservedOnly => "Observed only",
        _ => "Manual",
    };

    public static string EvidenceMeaning(ScenarioEvidence evidence) => evidence switch
    {
        ScenarioEvidence.DeclaredOnly => "Not observed in the current discovery window (not \"unused\").",
        ScenarioEvidence.ObservedOnly => "Potential undocumented API — not declared in the selected contract (not a defect by itself).",
        ScenarioEvidence.DeclaredAndObserved => "Declared in the contract and observed on the target.",
        _ => "Entered manually.",
    };

    public static string LevelLabel(ScenarioRecommendationLevel level) => level switch
    {
        ScenarioRecommendationLevel.Recommended => "Recommended",
        ScenarioRecommendationLevel.Candidate => "Candidate",
        ScenarioRecommendationLevel.ReviewRequired => "Review required",
        _ => "Not runnable",
    };

    public static bool IsApiContract(SourceContract contract) =>
        contract.Type is SourceContractType.OpenApi or SourceContractType.GraphQlSchema && contract.Operations.Count > 0;

    /// <summary>Observed API traffic to the target (same host and port); static assets, telemetry and authentication traffic are not operations.</summary>
    public static IReadOnlyList<ObservedNetworkEndpoint> OnTarget(IEnumerable<ObservedNetworkEndpoint> observed, string targetOrigin)
    {
        if (!Uri.TryCreate(targetOrigin, UriKind.Absolute, out var target)) return [];
        return [.. observed.Where(o => string.Equals(o.Host, target.Host, StringComparison.OrdinalIgnoreCase) && (o.Port == target.Port || o.Port == 0)
            && o.Category is ObservedTrafficCategory.Rest or ObservedTrafficCategory.GraphQl or ObservedTrafficCategory.OtherHttp && o.Method.Length > 0)];
    }

    // ── Path templates ────────────────────────────────────────────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^(\d+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{16,}|(?=[A-Za-z0-9_-]*\d)[A-Za-z0-9_-]{12,})$")]
    private static partial Regex IdentifierLike();

    /// <summary>
    /// An observed path as a template: every segment that looks like an identifier (number, GUID, long hex/token) becomes {id}, {id2} … so an
    /// observed identifier is never copied into a scenario. "/api/person/12345" → "/api/person/{id}".
    /// </summary>
    public static string Template(string observedPath)
    {
        var segments = observedPath.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = 0;
        return "/" + string.Join("/", segments.Select(s => IdentifierLike().IsMatch(Uri.UnescapeDataString(s)) ? $"{{id{(++index == 1 ? "" : index.ToString(System.Globalization.CultureInfo.InvariantCulture))}}}" : s));
    }

    /// <summary>Does an observed path (or observed template) match a declared template? Placeholders match any segment; an observed path may carry
    /// a base-path prefix (up to two segments, e.g. /api from the server URL). Never requires literal equality of parameter values.</summary>
    public static bool Matches(string template, string observedPath)
    {
        var t = template.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var o = observedPath.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (o.Length < t.Length || o.Length - t.Length > 2) return false;
        var offset = o.Length - t.Length;
        for (var i = 0; i < t.Length; i++)
        {
            var declared = t[i];
            if (declared.StartsWith('{') && declared.EndsWith('}')) { if (o[i + offset].Length == 0) return false; continue; }
            if (!string.Equals(declared, o[i + offset], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    // ── Contract scoping ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Which contracts of the snapshot belong to the target: a person's selection wins; contracts whose operations match traffic observed on
    /// the target are resolved; a single contract naming the target component is a candidate; otherwise unresolved — a monorepo's contracts are
    /// never all presented as equally relevant.
    /// </summary>
    public static ScenarioContractScope Scope(IReadOnlyList<SourceContract> contracts, string targetOrigin, IReadOnlyList<ObservedNetworkEndpoint> onTarget,
        IReadOnlyCollection<string>? selectedIds)
    {
        var api = contracts.Where(IsApiContract).ToList();
        if (api.Count == 0)
            return new(ScenarioContractScopeState.NoContracts, "No OpenAPI or GraphQL contract with operations in this snapshot.", [], []);
        List<SourceContract> Others(IEnumerable<SourceContract> relevant) { var ids = relevant.Select(c => c.Id).ToHashSet(StringComparer.Ordinal); return [.. api.Where(c => !ids.Contains(c.Id))]; }
        if (selectedIds is { Count: > 0 })
        {
            var selected = api.Where(c => selectedIds.Contains(c.Id)).ToList();
            if (selected.Count > 0) return new(ScenarioContractScopeState.Selected, $"{selected.Count} contract{(selected.Count == 1 ? "" : "s")} selected for this target.", selected, Others(selected));
        }
        var observedMatch = api.Select(c => (Contract: c, Hits: ObservedHits(c, onTarget))).Where(x => x.Hits > 0).OrderByDescending(x => x.Hits).ToList();
        if (observedMatch.Count > 0)
        {
            var relevant = observedMatch.Select(x => x.Contract).ToList();
            return new(ScenarioContractScopeState.Resolved,
                $"{string.Join(", ", relevant.Select(c => c.Name))} — operations observed on the target ({observedMatch.Sum(x => x.Hits)} correlated).", relevant, Others(relevant));
        }
        var tokens = DeliveryTargetScope.TargetTokens(targetOrigin);
        var named = tokens.Count == 0 ? [] : api.Where(c => tokens.All(t => DeliveryTargetScope.Words(c.Name).Contains(t) || DeliveryTargetScope.Words(c.Producer).Contains(t)
            || DeliveryTargetScope.Words(c.File).Contains(t))).ToList();
        if (named.Count == 1)
            return new(ScenarioContractScopeState.Candidate, $"{named[0].Name} names the target component — candidate; confirm by selecting it.", named, Others(named));
        return new(ScenarioContractScopeState.Unresolved,
            $"Contract source available · target-specific contract unresolved ({api.Count} API contract{(api.Count == 1 ? "" : "s")} in the snapshot; select the target's contract).", [], api);
    }

    private static int ObservedHits(SourceContract contract, IReadOnlyList<ObservedNetworkEndpoint> onTarget) => contract.Type == SourceContractType.OpenApi
        ? contract.Operations.Count(op => op.Path is { } path && onTarget.Any(o => o.Category != ObservedTrafficCategory.GraphQl && string.Equals(o.Method, op.Kind, StringComparison.OrdinalIgnoreCase) && Matches(path, o.Path)))
        : contract.Operations.Count(op => onTarget.Any(o => GraphQlMatches(op, o)));

    private static string Field(ContractOperation op) => op.OperationId ?? op.Name.Split(' ').Last();

    private static bool GraphQlMatches(ContractOperation op, ObservedNetworkEndpoint o) =>
        o.Category == ObservedTrafficCategory.GraphQl && o.OperationName is { Length: > 0 } name
        && (string.Equals(name, Field(op), StringComparison.OrdinalIgnoreCase) || o.GraphQlDocuments.Any(d => Regex.IsMatch(d.Document, $@"[{{\s]{Regex.Escape(Field(op))}\s*[({{]")));

    // ── Catalog ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <param name="authenticated">The definition has an authentication reference (operations that require auth are otherwise "review required").</param>
    public static ScenarioCatalog Build(IReadOnlyList<SourceContract> contracts, Guid? snapshotId, string? snapshotName, string targetOrigin, string? environment,
        IEnumerable<ObservedNetworkEndpoint> observed, IReadOnlyList<TestDataAssetSummary> assets, IReadOnlyCollection<string>? selectedContractIds, bool authenticated)
    {
        var onTarget = OnTarget(observed, targetOrigin);
        var scope = Scope(contracts, targetOrigin, onTarget, selectedContractIds);
        var operations = new List<ScenarioOperation>();
        var correlated = new HashSet<ObservedNetworkEndpoint>();
        // Each observed REST call counts once, for the most specific declared template (most literal segments): /api/person/search is the search
        // operation, not also /api/person/{personId}.
        var restTemplates = scope.Relevant.Where(c => c.Type == SourceContractType.OpenApi).SelectMany(c => c.Operations.Where(o => o.Path is not null).Select(o => (Method: o.Kind, Path: o.Path!))).ToList();
        string? BestTemplate(ObservedNetworkEndpoint o) => restTemplates.Where(t => string.Equals(t.Method, o.Method, StringComparison.OrdinalIgnoreCase) && Matches(t.Path, o.Path))
            .OrderByDescending(t => t.Path.Split('/').Count(seg => seg.Length > 0 && !seg.StartsWith('{'))).ThenBy(t => t.Path, StringComparer.Ordinal).Select(t => t.Path).FirstOrDefault();
        foreach (var contract in scope.Relevant)
        {
            var types = contract.Types.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var op in contract.Operations)
            {
                var graphQl = contract.Type == SourceContractType.GraphQlSchema;
                var matches = graphQl ? onTarget.Where(o => GraphQlMatches(op, o)).ToList()
                    : op.Path is { } p ? onTarget.Where(o => o.Category != ObservedTrafficCategory.GraphQl && string.Equals(o.Method, op.Kind, StringComparison.OrdinalIgnoreCase) && BestTemplate(o) == p).ToList() : [];
                correlated.UnionWith(matches);
                var declared = Declared(contract, op, types, graphQl, Observe(matches), snapshotId, snapshotName);
                // A path value the runtime used is available as evidence (hidden); the requirement itself stays.
                if (declared.Observation is not null)
                    declared = declared with { Needs = [.. declared.Needs.Select(n => n.In == "path" ? n with { ObservedValueAvailable = true } : n)] };
                if (graphQl && matches.Count > 0)
                    declared = declared with { GraphQl = GraphQlEvidence(Field(op), op.Kind, matches, declaredSchema: true) };
                operations.Add(declared);
            }
        }
        // Observed only: traffic on the target that no declared operation explains (templated — observed identifiers never kept).
        foreach (var group in onTarget.Where(o => !correlated.Contains(o) && o.Category != ObservedTrafficCategory.GraphQl)
            .GroupBy(o => (Method: o.Method.ToUpperInvariant(), Path: Template(o.Path))).OrderBy(g => g.Key.Path, StringComparer.Ordinal).ThenBy(g => g.Key.Method, StringComparer.Ordinal))
        {
            var placeholders = Placeholders(group.Key.Path).ToList();
            var observedOnly = new ScenarioOperation
            {
                Key = $"observed:{group.Key.Method} {group.Key.Path}", Origin = ScenarioOperationOrigin.Observed, Evidence = ScenarioEvidence.ObservedOnly,
                Method = group.Key.Method, Path = group.Key.Path, Name = $"{group.Key.Method} {group.Key.Path}", Observation = Observe([.. group]),
                Parameters = [.. placeholders.Select(n => new ContractParameter(n, "path", true, null))], Use = UseOf(group.Key.Method, group.Key.Path, [], null),
                Signature = $"{group.Key.Method} {group.Key.Path}",
            };
            // Same requirement model as declared operations: every normalised runtime placeholder is a path input that needs Test Data. The
            // concrete value was observed (evidence, hidden) — it never makes the requirement go away.
            operations.Add(observedOnly with { Needs = [.. Needs(observedOnly).Select(n => n with { Source = ScenarioNeedSources.Observed, ObservedValueAvailable = true })] });
        }
        foreach (var group in onTarget.Where(o => !correlated.Contains(o) && o.Category == ObservedTrafficCategory.GraphQl && o.OperationName is { Length: > 0 })
            .GroupBy(o => (Kind: o.OperationType == GraphQlOperationType.Mutation ? "mutation" : "query", o.OperationName!)).OrderBy(g => g.Key.Item2, StringComparer.Ordinal))
        {
            var evidence = GraphQlEvidence(group.Key.Item2, group.Key.Kind, [.. group], declaredSchema: false);
            var observedGraphQl = new ScenarioOperation
            {
                Key = $"observed:graphql {group.Key.Kind} {group.Key.Item2}", Origin = ScenarioOperationOrigin.Observed, Evidence = ScenarioEvidence.ObservedOnly, Method = "POST",
                Name = $"{group.Key.Kind} {group.Key.Item2}", GraphQlKind = group.Key.Kind, Observation = Observe([.. group]), Use = group.Key.Kind == "mutation" ? "GraphQL mutation" : "GraphQL query",
                Signature = $"graphql {group.Key.Kind} {group.Key.Item2}", GraphQl = evidence,
                Parameters = [.. evidence.Variables.Select(v => new ContractParameter(v.Name, "argument", v.Type.EndsWith('!'), v.Type))],
            };
            // Variables come only from a captured query document; without one the inputs are unknown (never "no Test Data needed").
            operations.Add(observedGraphQl with
            {
                Needs = [.. evidence.Variables.Select(v => new ScenarioDataNeed(v.Name, "variable", GraphQlScalars.Contains(v.Type.Trim('!', '[', ']')) ? TestDataDataType.SimpleValues : TestDataDataType.JsonPayload,
                    v.Type.EndsWith('!'), null, v.Type, ScenarioNeedSources.CapturedQuery))],
            });
        }
        var finished = operations.Select(op => Finish(op, assets, environment, authenticated)).ToList();
        var observedEndpoints = onTarget.Where(o => o.Category != ObservedTrafficCategory.GraphQl).Select(o => $"{o.Method.ToUpperInvariant()} {Template(o.Path)}")
            .Concat(onTarget.Where(o => o.Category == ObservedTrafficCategory.GraphQl).Select(o => $"graphql {o.OperationName ?? o.Path}")).Distinct(StringComparer.Ordinal).Count();
        var correlatedOperations = operations.Count(o => o.Evidence == ScenarioEvidence.DeclaredAndObserved);
        return new ScenarioCatalog(scope, [.. Order(finished)], onTarget.Count, observedEndpoints, correlatedOperations);
    }

    /// <summary>
    /// Deterministic ranking (no AI): evidence tier first — 1 declared + observed application operation, 2 declared only, 4 observed only,
    /// 5 diagnostic/smoke, 6 technical/background — then recommendation level, score, observed count and name.
    /// </summary>
    public static IEnumerable<ScenarioOperation> Order(IEnumerable<ScenarioOperation> operations) =>
        operations.OrderBy(o => o.Tier).ThenBy(o => o.Level).ThenByDescending(o => o.Score).ThenByDescending(o => o.Observation?.Count ?? 0).ThenBy(o => o.Name, StringComparer.Ordinal);

    public static int TierOf(ScenarioEvidence evidence, ScenarioTrafficKind traffic) => traffic switch
    {
        ScenarioTrafficKind.TechnicalBackground => 6,
        ScenarioTrafficKind.DiagnosticSmoke => 5,
        _ => evidence switch { ScenarioEvidence.DeclaredAndObserved => 1, ScenarioEvidence.DeclaredOnly => 2, ScenarioEvidence.ObservedOnly => 4, _ => 3 },
    };

    private static readonly HashSet<string> HealthSegments = new(StringComparer.OrdinalIgnoreCase) { "health", "healthz", "healthcheck", "health-check", "ping", "alive", "liveness", "readiness" };
    private static readonly HashSet<string> TechnicalPrefixes = new(StringComparer.OrdinalIgnoreCase) { "_framework", "_content", ".well-known", "static", "assets", "_next", "favicon.ico", "robots.txt" };
    private static readonly HashSet<string> TechnicalExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".json", ".js", ".mjs", ".css", ".map", ".ico", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".woff", ".woff2", ".ttf", ".html", ".htm", ".txt", ".xml", ".webmanifest", ".wasm", ".dll", ".pdb", ".br", ".gz" };
    private static readonly HashSet<string> BootstrapNames = new(StringComparer.OrdinalIgnoreCase) { "config", "configuration", "bootstrap", "settings", "manifest", "env", "environment", "runtime-config" };

    /// <summary>
    /// Traffic kind of a REST path: technical/background (SPA shell "/", appsettings*.json, static resources, configuration/bootstrap), diagnostic/
    /// smoke (/health, /health/live, /health/ready, /ping …) or application. Deterministic; a classification never removes evidence.
    /// </summary>
    public static ScenarioTrafficKind Classify(string? path)
    {
        if (path is null) return ScenarioTrafficKind.Application; // GraphQL field
        var segments = path.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return ScenarioTrafficKind.TechnicalBackground;
        var last = segments[^1];
        if (last.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) || TechnicalPrefixes.Contains(segments[0])
            || TechnicalExtensions.Contains(System.IO.Path.GetExtension(last)) || segments.Length <= 2 && BootstrapNames.Contains(last))
            return ScenarioTrafficKind.TechnicalBackground;
        if (segments.Any(HealthSegments.Contains)) return ScenarioTrafficKind.DiagnosticSmoke;
        return ScenarioTrafficKind.Application;
    }

    public static string TrafficLabel(ScenarioTrafficKind kind) => kind switch
    {
        ScenarioTrafficKind.DiagnosticSmoke => "Diagnostic / smoke",
        ScenarioTrafficKind.TechnicalBackground => "Technical / background",
        _ => "Application / API",
    };

    [GeneratedRegex(@"^\s*(query|mutation|subscription)\b\s*[_A-Za-z0-9]*\s*\((?<defs>[^)]*)\)")]
    private static partial Regex OperationVariables();
    [GeneratedRegex(@"\$(?<name>[_A-Za-z][_A-Za-z0-9]*)\s*:\s*(?<type>[\[\]!_A-Za-z0-9]+)")]
    private static partial Regex VariableDefinition();

    /// <summary>Variables declared by a GraphQL operation document ("query HentBarn($id: ID!)" → id: ID!). Empty when the document declares none.</summary>
    public static IReadOnlyList<(string Name, string Type)> GraphQlVariables(string document) =>
        OperationVariables().Match(document) is { Success: true } m
            ? [.. VariableDefinition().Matches(m.Groups["defs"].Value).Select(v => (v.Groups["name"].Value, v.Groups["type"].Value)).DistinctBy(v => v.Item1)]
            : [];

    /// <summary>What Live Discovery retained for a GraphQL operation: the most recent kept document (redacted), never variables' values.</summary>
    private static ScenarioGraphQlEvidence GraphQlEvidence(string operationName, string kind, IReadOnlyList<ObservedNetworkEndpoint> matches, bool declaredSchema)
    {
        var documents = matches.SelectMany(m => m.GraphQlDocuments).OrderByDescending(d => d.LastObservedAt).ToList();
        var kept = documents.FirstOrDefault(d => d.Document.Length > 0);
        var state = kept is not null ? ScenarioGraphQlDocumentStates.Captured
            : documents.Count > 0 ? ScenarioGraphQlDocumentStates.OverLimit : ScenarioGraphQlDocumentStates.NotCaptured;
        return new(operationName, kind, state, kept?.Document, kept is null ? [] : GraphQlVariables(kept.Document), declaredSchema,
            matches.Sum(m => m.Count), matches.Any(m => m.AuthObserved));
    }

    /// <summary>
    /// The observed values behind one {placeholder} of a REST candidate: the matching discovery records on the target (same method, path matching
    /// the template) and the concrete segment at the placeholder's position. Query strings are never retained by discovery, so only path
    /// placeholders can have an observed value.
    /// </summary>
    public static ObservedValueEvidence? ObservedValues(ScenarioOperation op, string variable, IEnumerable<ObservedNetworkEndpoint> observed, string targetOrigin)
    {
        if (op.Path is not { } template || op.GraphQlKind is not null) return null;
        var t = template.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var position = Array.FindIndex(t, seg => seg == $"{{{variable}}}");
        if (position < 0) return null;
        var records = OnTarget(observed, targetOrigin).Where(o => o.Category != ObservedTrafficCategory.GraphQl && string.Equals(o.Method, op.Method, StringComparison.OrdinalIgnoreCase)
            && (op.Origin == ScenarioOperationOrigin.Observed ? Template(o.Path) == template : Matches(template, o.Path))).ToList();
        if (records.Count == 0) return null;
        var values = records.OrderByDescending(r => r.LastObservedAt).Select(r =>
        {
            var o = r.Path.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
            var offset = o.Length - t.Length;
            return offset >= 0 && position + offset < o.Length ? Uri.UnescapeDataString(o[position + offset]) : null;
        }).OfType<string>().Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).Take(20).ToList();
        return new ObservedValueEvidence(variable, "path", template, records.Sum(r => r.Count), records.Max(r => r.LastObservedAt),
            records.Where(r => r.LastStatus is >= 200 and < 400).Sum(r => r.Count), records.Any(r => r.AuthObserved), values);
    }

    // ── Logical contracts ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static int CanonicalRank(SourceContract c)
    {
        var file = c.File.Replace('\\', '/').ToLowerInvariant();
        var rank = 0;
        if (!file.Contains("/autodoc/", StringComparison.Ordinal)) rank += 1;
        if (file.Contains("snapshot", StringComparison.Ordinal) || file.Contains("copy", StringComparison.Ordinal) || file.Contains("frontend", StringComparison.Ordinal)
            || file.Contains("node_modules", StringComparison.Ordinal) || file.Contains("generated", StringComparison.Ordinal)) rank += 2;
        if (c.Producer is null) rank += 1;
        return rank;
    }

    private static int IndexOf(IReadOnlyList<SourceContract> contracts, SourceContract c)
    {
        for (var i = 0; i < contracts.Count; i++) if (ReferenceEquals(contracts[i], c)) return i;
        return int.MaxValue;
    }

    private static HashSet<string> OperationNames(SourceContract c) =>
        [.. c.Operations.Select(o => c.Type == SourceContractType.GraphQlSchema ? $"{o.Kind} {Field(o)}" : $"{o.Kind.ToUpperInvariant()} {o.Path}")];

    /// <summary>
    /// Groups the API contracts of a snapshot into logical contracts: variants of the same API (same type, mostly the same operations — e.g.
    /// schema.graphql, schema.snapshot.graphql and a frontend SDL copy) form one group with a preferred (canonical) file and supporting files.
    /// Identical operation sets = Consistent; any difference = Needs decision (never merged or chosen silently). Deterministic.
    /// </summary>
    public static IReadOnlyList<ScenarioContractGroup> ContractGroups(IReadOnlyList<SourceContract> contracts)
    {
        var groups = new List<(SourceContract Canonical, List<SourceContract> Members, HashSet<string> Ops)>();
        foreach (var c in contracts.Where(IsApiContract).OrderBy(CanonicalRank).ThenBy(c => c.File.Length).ThenBy(c => c.File, StringComparer.Ordinal))
        {
            var ops = OperationNames(c);
            var home = groups.FirstOrDefault(g => g.Canonical.Type == c.Type && g.Ops.Count > 0 && ops.Count > 0
                && (double)g.Ops.Intersect(ops).Count() / Math.Min(g.Ops.Count, ops.Count) >= 0.6);
            if (home.Canonical is null) groups.Add((c, [c], ops));
            else home.Members.Add(c);
        }
        return [.. groups.Select(g =>
        {
            var supporting = g.Members.Skip(1).ToList();
            var state = supporting.Count == 0 ? ScenarioContractGroup.SingleSource
                : supporting.All(s => OperationNames(s).SetEquals(g.Ops)) ? ScenarioContractGroup.Consistent : ScenarioContractGroup.NeedsDecision;
            var name = $"{g.Canonical.Producer ?? g.Canonical.Name} {(g.Canonical.Type == SourceContractType.GraphQlSchema ? "GraphQL" : "REST")}";
            return new ScenarioContractGroup(g.Canonical.Id, name, g.Canonical.Type, g.Canonical, supporting, state, g.Canonical.Operations.Count);
        }).OrderBy(g => g.All.Min(c => IndexOf(contracts, c)))];
    }

    private static ScenarioObservation? Observe(List<ObservedNetworkEndpoint> matches) => matches.Count == 0 ? null
        : new(matches.Sum(m => m.Count), matches.Max(m => m.LastObservedAt), matches.Any(m => m.AuthObserved), matches.OrderByDescending(m => m.LastObservedAt).First().LastStatus,
            [.. matches.Select(m => m.PagePath).OfType<string>().Distinct(StringComparer.Ordinal).Take(5)], matches.Count);

    /// <summary>One declared operation as a candidate shape (no observation, no Test Data): for drift review of saved steps.</summary>
    public static ScenarioOperation Describe(SourceContract contract, ContractOperation op, Guid? snapshotId = null, string? snapshotName = null) =>
        Declared(contract, op, contract.Types.GroupBy(t => t.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal),
            contract.Type == SourceContractType.GraphQlSchema, null, snapshotId, snapshotName);

    private static ScenarioOperation Declared(SourceContract contract, ContractOperation op, Dictionary<string, ContractTypeShape> types, bool graphQl, ScenarioObservation? observation,
        Guid? snapshotId, string? snapshotName)
    {
        var parameters = op.ParameterDetails.Count > 0 || op.Detailed ? op.ParameterDetails
            : op.Parameters.Select(n => new ContractParameter(n, graphQl ? "argument" : op.Path?.Contains($"{{{n}}}", StringComparison.Ordinal) == true ? "path" : "query", false, null)).ToList();
        if (!graphQl && op.Path is { } path)
            parameters = [.. parameters, .. Placeholders(path).Where(n => parameters.All(p => p.Name != n)).Select(n => new ContractParameter(n, "path", true, null))];
        var method = graphQl ? "POST" : op.Kind.ToUpperInvariant();
        var content = op.RequestContentTypes.FirstOrDefault();
        var result = new ScenarioOperation
        {
            Key = graphQl ? $"graphql:{contract.Id}#{op.Kind} {Field(op)}" : $"openapi:{contract.Id}#{method} {op.Path}",
            Origin = graphQl ? ScenarioOperationOrigin.GraphQl : ScenarioOperationOrigin.OpenApi,
            Evidence = observation is null ? ScenarioEvidence.DeclaredOnly : ScenarioEvidence.DeclaredAndObserved,
            Method = method, Path = graphQl ? null : op.Path, Name = op.Name, OperationId = op.OperationId, Summary = op.Summary, Tags = op.Tags, Parameters = parameters,
            RequestContentType = content, RequestSchema = op.RequestSchema, ResponseCodes = op.ResponseCodes, Security = op.Security,
            GraphQlKind = graphQl ? op.Kind : null, ResultType = op.ResultType, ContractId = contract.Id, ContractName = contract.Name, ContractFile = contract.File,
            SnapshotId = snapshotId, SnapshotName = snapshotName, Observation = observation, Detailed = op.Detailed,
            Use = graphQl ? op.Kind == "mutation" ? "GraphQL mutation" : "GraphQL query" : UseOf(method, op.Path ?? "/", parameters, content),
        };
        return result with { Needs = Needs(result, types), Signature = Signature(result) };
    }

    private static IEnumerable<string> Placeholders(string path) => Regex.Matches(path, @"\{([^{}/]+)\}").Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal);

    private static string UseOf(string method, string path, IReadOnlyList<ContractParameter> parameters, string? contentType)
    {
        var query = parameters.Where(p => p.In == "query").Select(p => p.Name.ToLowerInvariant()).ToList();
        return method switch
        {
            "GET" when query.Any(q => q is "page" or "size" or "limit" or "offset" or "skip" or "take" or "pagesize" or "pagenumber") => "Pagination",
            "GET" when query.Any(q => q is "q" or "query" or "search" or "term") || path.Contains("search", StringComparison.OrdinalIgnoreCase) => "List/search",
            "GET" when path.TrimEnd('/').EndsWith('}') => "Get by id",
            "GET" when query.Count > 0 => "Filter",
            "GET" when path.Contains('{') => "Get by id",
            "GET" or "HEAD" or "OPTIONS" => "Baseline read",
            "POST" when path.Contains("search", StringComparison.OrdinalIgnoreCase) || path.Contains("query", StringComparison.OrdinalIgnoreCase) => "Search (POST read)",
            "POST" => contentType is null ? "Create" : "Create (payload)",
            "PUT" or "PATCH" => "Update",
            "DELETE" => "Delete",
            _ => "Operation",
        };
    }

    /// <summary>What data the operation needs (path/required query/header parameters, request body, GraphQL arguments) — the operation tells the
    /// shape; Test Data provides the approved asset.</summary>
    public static IReadOnlyList<ScenarioDataNeed> Needs(ScenarioOperation op, IReadOnlyDictionary<string, ContractTypeShape>? types = null)
    {
        var needs = new List<ScenarioDataNeed>();
        if (op.Origin == ScenarioOperationOrigin.GraphQl)
        {
            foreach (var argument in op.Parameters.Where(p => p.Required))
            {
                var named = (argument.Type ?? "").Trim('!', '[', ']');
                var input = types is not null && types.TryGetValue(named, out var shape) && shape.Kind is "input_object";
                needs.Add(new(argument.Name, "variable", input ? TestDataDataType.JsonPayload : TestDataDataType.SimpleValues, true, input ? named : null, argument.Type));
            }
            return needs;
        }
        // Path and required query parameters (the load generator fills {placeholders} in the path, query and body; required headers are reviewed manually).
        foreach (var p in op.Parameters.Where(p => p.In == "path" || p.Required && p.In == "query"))
            needs.Add(new(p.Name, p.In, TestDataDataType.SimpleValues, true, null, p.Type));
        if (op.RequestContentType is { } content)
            needs.Add(new("body", "body", TestDataDataTypes.ForMediaType(content), true, op.RequestSchema, content));
        return needs;
    }

    private static readonly Regex Words = new(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+", RegexOptions.Compiled);

    private static HashSet<string> Tokens(string? text) => [.. Words.Matches(text ?? "").Select(m => m.Value.ToLowerInvariant()).Where(w => w.Length > 1 && w != "id")];

    /// <summary>Approved central Test Data for a need: right data type, usable in the environment. A single asset whose alias/label/capability
    /// names the variable is recommended; several (or only type matches) are possible matches a person chooses from; none = missing.</summary>
    public static ScenarioDataMatch Match(ScenarioDataNeed need, IReadOnlyList<TestDataAssetSummary> assets, string? environment)
    {
        var usable = assets.Where(a => a.Approval == TestDataApproval.Approved && TestDataDataTypes.Of(a) == need.DataType
            && (a.Environment is null || environment is null || string.Equals(a.Environment, environment, StringComparison.OrdinalIgnoreCase))).ToList();
        if (usable.Count == 0)
            return new(need, ScenarioDataMatchState.Missing, [], null, $"Test Data missing for {Placeholder(need)} ({TestDataDataTypes.Label(need.DataType)}).");
        var want = Tokens(need.Variable);
        if (need.Schema is { } schema) want.UnionWith(Tokens(schema));
        var named = usable.Where(a => want.Count > 0 && (Tokens(a.Alias).Overlaps(want) || Tokens(a.Label).Overlaps(want) || a.Capabilities.Any(c => Tokens(c).Overlaps(want))
            || Tokens(a.EntityType).Overlaps(want))).OrderBy(a => a.Alias, StringComparer.Ordinal).ToList();
        var ordered = named.Concat(usable.Except(named).OrderBy(a => a.Alias, StringComparer.Ordinal)).Select(a => a.Alias).ToList();
        return named.Count == 1
            ? new(need, ScenarioDataMatchState.Recommended, ordered, named[0].Alias, $"Recommended Test Data: {named[0].Alias} for {Placeholder(need)}.")
            : new(need, ScenarioDataMatchState.Possible, ordered, null,
                $"{ordered.Count} approved {TestDataDataTypes.Label(need.DataType)} asset{(ordered.Count == 1 ? "" : "s")} could supply {Placeholder(need)} — choose one.");
    }

    public static string Placeholder(ScenarioDataNeed need) => need.In == "body" ? "the request body" : need.In == "variable" ? $"${need.Variable}" : $"{{{need.Variable}}}";

    /// <summary>Readiness to run (performance safety: read-only methods, GraphQL queries only) and a transparent, deterministic recommendation.</summary>
    private static ScenarioOperation Finish(ScenarioOperation op, IReadOnlyList<TestDataAssetSummary> assets, string? environment, bool authenticated)
    {
        var data = op.Needs.Select(n => Match(n, assets, environment)).ToList();
        var (runnable, why) = op.Origin == ScenarioOperationOrigin.GraphQl || op.GraphQlKind is not null
            ? op.GraphQlKind == "query" ? (true, (string?)null) : (false, $"GraphQL {op.GraphQlKind ?? "operation"}s are blocked by performance safety (queries only).")
            : ReadMethods.Contains(op.Method) ? (true, null)
            : op.Method == "POST" ? (false, "A POST is a write unless it only reads: add it and mark the step \"POST only reads\" if that is true; otherwise performance safety blocks it.")
            : (false, $"{op.Method} changes data; performance tests use read-only operations.");
        var proposed = false;
        if (op.Origin == ScenarioOperationOrigin.Observed && op.GraphQlKind is not null)
        {
            if (op.GraphQlKind != "query") (runnable, why) = (false, $"GraphQL {op.GraphQlKind}s are blocked by performance safety (queries only).");
            else if (op.GraphQl is { DocumentState: ScenarioGraphQlDocumentStates.Captured })
                (runnable, why, proposed) = (false, "Captured query document, not declared in a schema: review it and create a GraphQL definition (proposed manual definition).", true);
            else (runnable, why) = (false, "The operation name was observed, but the GraphQL query document was not retained. BirkNext cannot construct an executable test safely.");
        }
        else if (runnable && op.GraphQlKind is not null && op.Needs.Any(n => n.DataType == TestDataDataType.JsonPayload))
            (runnable, why) = (false, "Input-object GraphQL variables are not supported by the load generator yet (scalar variables only).");
        else if (runnable && op.Needs.Any(n => n.In == "body" && n.DataType == TestDataDataType.FileFixture))
            (runnable, why) = (false, "File upload bodies are not supported by the load generator; reference the fixture from a manual step outside performance tests.");
        var requiresAuth = op.Security.Count > 0 || op.Observation?.AuthObserved == true;
        var reasons = new List<string>();
        var score = 0;
        if (op.Evidence is ScenarioEvidence.DeclaredAndObserved or ScenarioEvidence.DeclaredOnly)
        { score += 3; reasons.Add($"declared in {(op.Origin == ScenarioOperationOrigin.GraphQl ? "GraphQL schema" : "OpenAPI")} · {op.ContractName ?? op.ContractFile}{(op.SnapshotName is null ? "" : $" ({op.SnapshotName})")}"); }
        if (op.Observation is { } seen)
        {
            score += 3; reasons.Add($"observed {seen.Count}× (last seen {seen.LastSeen.ToUniversalTime():yyyy-MM-dd HH:mm} UTC)");
            if (seen.Count >= 5) score += 1;
            if (seen.LastStatus is >= 200 and < 400) { score += 1; reasons.Add($"successful response observed (HTTP {seen.LastStatus})"); }
        }
        if (op.Evidence == ScenarioEvidence.DeclaredOnly) reasons.Add("not observed in the current discovery window");
        if (op.Evidence == ScenarioEvidence.ObservedOnly) reasons.Add("observed only — potential undocumented API");
        var missing = data.Where(d => d.State == ScenarioDataMatchState.Missing).ToList();
        // "Not required" only when the evidence proves it: a declared operation without inputs, or a captured query declaring no variables.
        var testData = op.Needs.Count > 0 ? ScenarioTestDataStates.Required
            : op.Origin == ScenarioOperationOrigin.Observed && op.GraphQlKind is not null && op.GraphQl?.DocumentState != ScenarioGraphQlDocumentStates.Captured ? ScenarioTestDataStates.Unknown
            : ScenarioTestDataStates.NotRequired;
        if (testData == ScenarioTestDataStates.Unknown) reasons.Add("Test Data unknown — the query document (and its variables) was not captured");
        else if (op.Needs.Count == 0) reasons.Add("Test Data not required");
        else if (missing.Count == 0) { score += 2; reasons.Add($"Test Data available for {string.Join(", ", data.Select(d => Placeholder(d.Need)))}"); }
        else reasons.Add(string.Join(" ", missing.Select(m => m.Detail)));
        if (requiresAuth) { if (authenticated) { score += 1; reasons.Add("authentication reference configured"); } else reasons.Add($"requires authentication ({(op.Observation?.AuthObserved == true ? "bearer observed" : string.Join(", ", op.Security))}) — not configured"); }
        var traffic = Classify(op.Path);
        if (traffic == ScenarioTrafficKind.TechnicalBackground) reasons.Add("technical/background traffic (not a normal workload)");
        else if (traffic == ScenarioTrafficKind.DiagnosticSmoke) reasons.Add("diagnostic/smoke endpoint (not a business baseline)");
        var level = proposed ? ScenarioRecommendationLevel.ReviewRequired
            : !runnable ? ScenarioRecommendationLevel.NotRunnable
            : op.Evidence == ScenarioEvidence.ObservedOnly || missing.Count > 0 || requiresAuth && !authenticated || traffic != ScenarioTrafficKind.Application ? ScenarioRecommendationLevel.ReviewRequired
            : op.Evidence == ScenarioEvidence.DeclaredAndObserved ? ScenarioRecommendationLevel.Recommended
            : ScenarioRecommendationLevel.Candidate;
        return op with
        {
            Data = data, Runnable = runnable, NotRunnableReason = why, RequiresAuthentication = requiresAuth, Level = level, Score = score, Reasons = reasons,
            TestDataState = testData, Traffic = traffic, Tier = TierOf(op.Evidence, traffic), ProposedDefinition = proposed,
        };
    }

    /// <summary>"Recommended because declared + observed + Test Data available".</summary>
    public static string Because(ScenarioOperation op) => $"{LevelLabel(op.Level)} because {string.Join(" · ", op.Reasons)}";

    // ── Steps ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The value-free signature of a declared operation, compared section by section for drift review.</summary>
    public static string Signature(ScenarioOperation op) => string.Join(" | ", new[]
    {
        op.GraphQlKind is { } kind ? $"graphql {kind} {op.OperationId ?? op.Name}" : $"{op.Method} {op.Path}",
        "params: " + string.Join(", ", op.Parameters.OrderBy(p => p.In, StringComparer.Ordinal).ThenBy(p => p.Name, StringComparer.Ordinal).Select(p => $"{p.Name}({p.In}{(p.Required ? ",required" : "")}{(p.Type is null ? "" : "," + p.Type)})")),
        "body: " + (op.RequestContentType is null ? "-" : $"{op.RequestContentType}{(op.RequestSchema is null ? "" : " " + op.RequestSchema)}"),
        "responses: " + string.Join(",", op.ResponseCodes.OrderBy(c => c, StringComparer.Ordinal)),
        "auth: " + string.Join(",", op.Security.OrderBy(c => c, StringComparer.Ordinal)),
        op.ResultType is null ? "" : "result: " + op.ResultType,
    }.Where(s => s.Length > 0));

    /// <summary>Differences between a stored signature and the current one, by section (params, body, responses, auth …).</summary>
    public static IReadOnlyList<ScenarioDrift> Drift(string? before, string? after)
    {
        if (before is null || after is null || before == after) return [];
        static Dictionary<string, string> Sections(string signature) => signature.Split(" | ").Select((part, i) => part.Contains(": ") && i > 0 ? (part[..part.IndexOf(": ", StringComparison.Ordinal)], part[(part.IndexOf(": ", StringComparison.Ordinal) + 2)..]) : ("operation", part))
            .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.Ordinal);
        var b = Sections(before); var a = Sections(after);
        return [.. b.Keys.Union(a.Keys).Where(k => b.GetValueOrDefault(k) != a.GetValueOrDefault(k)).Select(k => new ScenarioDrift(k, b.GetValueOrDefault(k) ?? "-", a.GetValueOrDefault(k) ?? "-"))];
    }

    public static string DriftText(IReadOnlyList<ScenarioDrift> drift) => drift.Count == 0 ? "" : "Contract changed · review required: " + string.Join("; ", drift.Select(d => d.Section switch
    {
        "body" => $"request body changed ({d.Before} → {d.After})",
        "params" => $"parameters changed ({d.Before} → {d.After})",
        "auth" => $"authentication changed ({d.Before} → {d.After})",
        "responses" => $"documented responses changed ({d.Before} → {d.After})",
        _ => $"{d.Section} changed ({d.Before} → {d.After})",
    }));

    /// <summary>
    /// A configured step from a candidate: relative path with {placeholders}, documented 2xx response suggested (never every documented code),
    /// default think time, a GraphQL query document with $variables (no literal values), and Test Data mapped by alias — the user may change it.
    /// </summary>
    public static HttpPerformanceStep ToStep(ScenarioOperation op, IReadOnlyDictionary<string, string>? chosenAliases = null, IReadOnlyCollection<ContractTypeShape>? types = null)
    {
        var success = op.ResponseCodes.Select(c => int.TryParse(c, out var code) ? code : 0).Where(c => c is >= 200 and < 300).OrderBy(c => c).ToList();
        var mappings = op.Data.Select(d => (Need: d.Need, Alias: chosenAliases?.GetValueOrDefault(d.Need.Variable) ?? d.Recommended))
            .Where(x => x.Alias is not null).Select(x => new ScenarioDataMapping(x.Need.Variable, [x.Alias!], x.Need.DataType, x.Need.In)).ToList();
        var name = Slug(op.OperationId ?? (op.Path is null ? op.Name.Split(' ').Last() : string.Join("-", op.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => !s.StartsWith('{')).TakeLast(2))));
        var source = new ScenarioStepSource
        {
            OperationKey = op.Key, Origin = op.Origin, Evidence = op.Evidence, ContractId = op.ContractId, ContractName = op.ContractName, ContractFile = op.ContractFile,
            SnapshotId = op.SnapshotId, OperationFingerprint = op.Signature, ObservedCount = op.Observation?.Count ?? 0, ObservedLastSeen = op.Observation?.LastSeen,
        };
        if (op.GraphQlKind is not null)
        {
            var arguments = op.Parameters.Where(p => p.Required || op.Needs.Any(n => n.Variable == p.Name)).ToList();
            var named = (op.ResultType ?? "").Trim('!', '[', ']');
            var scalar = GraphQlScalars.Contains(named) || types?.Any(t => t.Name == named && t.Kind is "scalar" or "enum") == true;
            var field = op.OperationId ?? op.Name.Split(' ').Last();
            var signature = arguments.Count == 0 ? "" : "(" + string.Join(", ", arguments.Select(a => $"${a.Name}: {a.Type ?? "String"}")) + ")";
            var call = arguments.Count == 0 ? "" : "(" + string.Join(", ", arguments.Select(a => $"{a.Name}: ${a.Name}")) + ")";
            var operationName = "Perf" + char.ToUpperInvariant(field[0]) + field[1..];
            return new HttpPerformanceStep
            {
                Name = name, Method = "POST", RelativePath = "/graphql", ExpectedStatusCodes = [200], ThinkTimeMs = 500,
                GraphQlQuery = $"{op.GraphQlKind} {operationName}{signature} {{ {field}{call}{(scalar ? "" : " { __typename }")} }}", GraphQlOperationName = operationName,
                GraphQlVariablesJson = arguments.Count == 0 ? null : JsonSerializer.Serialize(arguments.ToDictionary(a => a.Name, a => $"{{{a.Name}}}")),
                Source = source, DataMappings = mappings.Count == 0 ? null : mappings,
            };
        }
        var body = op.Needs.FirstOrDefault(n => n.In == "body");
        var query = op.Parameters.Where(p => p.In == "query" && p.Required).Select(p => new PerformanceNameValue(p.Name, $"{{{p.Name}}}")).ToList();
        return new HttpPerformanceStep
        {
            Name = name, Method = op.Method, RelativePath = op.Path ?? "/", ExpectedStatusCodes = success.Count > 0 ? [success[0]] : [200], ThinkTimeMs = 500,
            QueryParameters = query, ContentType = body is null ? null : op.RequestContentType, BodyTemplate = body is null ? null : "{body}",
            Source = source, DataMappings = mappings.Count == 0 ? null : mappings,
        };
    }

    /// <summary>
    /// The proposed step of "Review &amp; create GraphQL definition": the captured (literal-redacted) query document as the step's query and every
    /// variable as a {placeholder} mapped later to approved Test Data. No header, cookie, token or observed value is copied. Null without a document.
    /// </summary>
    public static HttpPerformanceStep? ProposedGraphQlStep(ScenarioOperation op)
    {
        if (op.GraphQl is not { DocumentState: ScenarioGraphQlDocumentStates.Captured, Document: { Length: > 0 } document } evidence) return null;
        return new HttpPerformanceStep
        {
            Name = Slug(evidence.OperationName), Method = "POST", RelativePath = "/graphql", ExpectedStatusCodes = [200], ThinkTimeMs = 500,
            GraphQlQuery = document, GraphQlOperationName = evidence.OperationName,
            GraphQlVariablesJson = evidence.Variables.Count == 0 ? null : JsonSerializer.Serialize(evidence.Variables.ToDictionary(v => v.Name, v => $"{{{v.Name}}}")),
            Source = new ScenarioStepSource
            {
                OperationKey = op.Key, Origin = op.Origin, Evidence = op.Evidence, OperationFingerprint = op.Signature,
                ObservedCount = op.Observation?.Count ?? 0, ObservedLastSeen = op.Observation?.LastSeen,
            },
        };
    }

    private static string Slug(string text)
    {
        var slug = Regex.Replace(text.Trim(), @"[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
        return slug.Length == 0 ? "step" : slug.Length > 60 ? slug[..60] : slug;
    }

    /// <summary>
    /// A suggested scenario name from the selected operations' own words: "Person lookup baseline", "Person search baseline", "GraphQL child lookup".
    /// Only words the evidence contains (resource segments, operation ids); never invented business meaning.
    /// </summary>
    public static string SuggestName(IReadOnlyList<ScenarioOperation> selected)
    {
        if (selected.Count == 0) return "Main scenario";
        var first = selected[0];
        string Resource(ScenarioOperation op) => op.Path is { } path
            ? path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(s => !s.StartsWith('{') && s.Length > 1 && !Regex.IsMatch(s, @"^v\d+$") && s != "api") ?? "api"
            : Regex.Replace(op.OperationId ?? op.Name.Split(' ').Last(), "^(get|find|list|search|fetch|hent|sok|soek)", "", RegexOptions.IgnoreCase);
        var resource = Resource(first);
        resource = resource.Length == 0 ? "api" : char.ToUpperInvariant(resource[0]) + resource[1..];
        var verb = first.Use switch
        {
            "Get by id" => "lookup", "List/search" or "Search (POST read)" => "search", "Pagination" => "list", "Filter" => "filter", "GraphQL query" => "query",
            "Create" or "Create (payload)" => "create", "Update" => "update", _ => "read",
        };
        var prefix = first.GraphQlKind is not null ? "GraphQL " : "";
        var name = $"{prefix}{(prefix.Length > 0 ? resource.ToLowerInvariant() : resource)} {verb} baseline";
        return selected.Count > 1 ? $"{name} (+{selected.Count - 1} step{(selected.Count == 2 ? "" : "s")})" : char.ToUpperInvariant(name[0]) + name[1..];
    }

    /// <summary>Test Data deep link with the exact requirement: operation, variables, data type, content type, schema, environment, consumer.</summary>
    public static string TestDataHref(ScenarioOperation op, ScenarioDataNeed need, string? environment, string? definitionId)
    {
        static string E(string? v) => Uri.EscapeDataString(v ?? "");
        return $"/test-data?consumer=performance-test-review{(definitionId is null ? "" : $"&consumerId={E(definitionId)}")}&operation={E(op.Name)}&variable={E(need.Variable)}"
            + $"&dataType={need.DataType}&in={E(need.In)}{(need.Type is null ? "" : $"&contentType={E(need.Type)}")}{(need.Schema is null ? "" : $"&schema={E(need.Schema)}")}"
            + $"{(environment is null ? "" : $"&environment={E(environment)}")}";
    }
}
