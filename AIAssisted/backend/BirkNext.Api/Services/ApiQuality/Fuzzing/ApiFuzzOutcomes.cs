using BirkNext.ApiReview;
using BirkNext.Standards;

namespace BirkNext.Api.Services.ApiQuality.Fuzzing;

/// <summary>What came back for one fuzz request: structural evidence only. The body was scanned transiently and discarded.</summary>
public sealed record ApiFuzzResponse(bool Executed, int StatusCode, string? ContentType, double? ElapsedMs, bool? JsonValid, int? GraphQlErrors, bool? GraphQlHasData,
    IReadOnlyList<string> Leaks, bool Timeout, string Message);

/// <summary>
/// Turns one response into a typed outcome. A 4xx is not a defect: rejecting invalid input is the expected behaviour. When the contract
/// declares response codes, a rejection status the contract does not declare is a contract violation (not a security finding); without
/// declared codes every 3xx/4xx counts as handled. GraphQL is judged by its envelope (errors/data), never by HTTP status alone.
/// </summary>
public static class ApiFuzzOutcomeClassifier
{
    public static ApiFuzzCaseResult Classify(ApiFuzzCase fuzzCase, ApiFuzzResponse response, IReadOnlyCollection<string>? declaredResponseCodes)
    {
        ApiFuzzCaseResult Result(ApiFuzzOutcome outcome, string note) => new()
        {
            CaseId = fuzzCase.CaseId, Executed = response.Executed, StatusCode = response.Executed ? response.StatusCode : null, ElapsedMs = response.ElapsedMs,
            ContentType = response.ContentType, Outcome = outcome, GraphQlErrors = response.GraphQlErrors, GraphQlHasData = response.GraphQlHasData,
            LeakIndicators = [.. response.Leaks], Note = note,
        };
        if (!response.Executed)
            return response.Timeout ? Result(ApiFuzzOutcome.Timeout, response.Message) : Result(ApiFuzzOutcome.ConnectionFailure, response.Message);
        var status = response.StatusCode;
        if (status == 401) return Result(ApiFuzzOutcome.AuthenticationBlocked, "HTTP 401: rejected by authentication before input validation; the case says nothing about validation.");
        if (status == 403) return Result(ApiFuzzOutcome.AuthorizationBlocked, "HTTP 403: rejected by access control before input validation; the case says nothing about validation.");
        if (status >= 500) return Result(ApiFuzzOutcome.Unexpected5xx, $"HTTP {status} after bounded invalid input: the input was not handled as a client error.");
        if (response.Leaks.Count > 0) return Result(ApiFuzzOutcome.PotentialInformationLeak, $"HTTP {status}; the response contains internal-detail indicators: {string.Join(", ", response.Leaks)}.");

        if (fuzzCase.Protocol == ApiFuzzProtocol.GraphQl)
        {
            if (response.GraphQlErrors is > 0 && response.GraphQlHasData != true) return Result(ApiFuzzOutcome.HandledValidation, $"HTTP {status}; GraphQL errors without data: the document was rejected.");
            if (response.GraphQlErrors is > 0) return Result(ApiFuzzOutcome.HandledValidation, $"HTTP {status}; GraphQL errors reported alongside partial data.");
            if (response.GraphQlHasData == true) return Result(ApiFuzzOutcome.UnexpectedAcceptance, $"HTTP {status}; data without errors for a document that violates the schema.");
            if (status is >= 400 and < 500) return Result(ApiFuzzOutcome.HandledValidation, $"HTTP {status}; rejected at the HTTP level without a GraphQL envelope.");
            return Result(ApiFuzzOutcome.ContractViolation, $"HTTP {status}; the response is not a GraphQL result document.");
        }

        if (status is >= 200 and < 300)
            return fuzzCase.ExpectedBehavior == ApiFuzzExpectedBehavior.AcceptOrReject
                ? Result(ApiFuzzOutcome.HandledValidation, $"HTTP {status}; the tolerated input was accepted (acceptance is not a defect for this case).")
                : Result(ApiFuzzOutcome.UnexpectedAcceptance, $"HTTP {status}; input that violates the contract was accepted as a valid request.");
        if (declaredResponseCodes is { Count: > 0 } declared && !Declares(declared, status))
            return Result(ApiFuzzOutcome.ContractViolation, $"HTTP {status} is not a response the contract declares for this operation ({string.Join(", ", declared.Take(8))}).");
        return Result(ApiFuzzOutcome.HandledValidation, $"HTTP {status}; the invalid input was rejected.");
    }

    /// <summary>Exact code, a range ("4XX") or "default".</summary>
    internal static bool Declares(IReadOnlyCollection<string> declared, int status) =>
        declared.Any(c => c == status.ToString() || string.Equals(c, $"{status / 100}XX", StringComparison.OrdinalIgnoreCase) || string.Equals(c, "default", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Equivalent cases become one logical finding with several evidence cases. Keys: rule + target + operation (5xx, acceptance, contract
/// violation) and rule + target + indicator set (a leak is usually one root cause — an error page or filter — whichever operation shows it).
/// Severities are conservative: a fuzz finding never starts above Medium.
/// </summary>
public static class ApiFuzzFindingAggregator
{
    private const int MaxEvidence = 10;

    public static List<ApiFuzzFinding> Aggregate(IReadOnlyList<ApiFuzzCase> cases, IReadOnlyList<ApiFuzzCaseResult> results)
    {
        var byId = cases.ToDictionary(c => c.CaseId, StringComparer.Ordinal);
        var groups = new Dictionary<string, (string Rule, ApiFuzzCase First, List<(ApiFuzzCase Case, ApiFuzzCaseResult Result)> Items)>(StringComparer.Ordinal);
        void Add(string rule, string key, ApiFuzzCase c, ApiFuzzCaseResult r)
        {
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = (rule, c, []);
            g.Items.Add((c, r));
        }
        foreach (var r in results)
        {
            if (!byId.TryGetValue(r.CaseId, out var c)) continue;
            if (r.LeakIndicators.Count > 0)
                Add("fuzz-information-leak", $"fuzz-information-leak|{c.TargetId}|{string.Join("+", r.LeakIndicators.OrderBy(l => l, StringComparer.Ordinal))}", c, r);
            switch (r.Outcome)
            {
                case ApiFuzzOutcome.Unexpected5xx: Add("fuzz-unexpected-5xx", $"fuzz-unexpected-5xx|{c.TargetId}|{c.OperationId}", c, r); break;
                case ApiFuzzOutcome.UnexpectedAcceptance: Add("fuzz-unexpected-acceptance", $"fuzz-unexpected-acceptance|{c.TargetId}|{c.OperationId}", c, r); break;
                case ApiFuzzOutcome.ContractViolation: Add("fuzz-contract-violation", $"fuzz-contract-violation|{c.TargetId}|{c.OperationId}", c, r); break;
            }
        }
        return groups.OrderBy(g => Rank(g.Value.Rule)).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var (rule, first, items) = g.Value;
            var evidence = items.Take(MaxEvidence).Select(i => $"{i.Case.CaseId} · {i.Case.MutationType} · {i.Case.Parameter} ({i.Case.Location}) → {(i.Result.StatusCode is { } s ? $"HTTP {s}" : i.Result.Outcome.ToString())}").ToList();
            if (items.Count > MaxEvidence) evidence.Add($"(+{items.Count - MaxEvidence} more case(s))");
            var operations = items.Select(i => i.Case.OperationDisplay).Distinct(StringComparer.Ordinal).ToList();
            var (severity, title, description, recommendation) = rule switch
            {
                "fuzz-unexpected-5xx" => (ApiReviewSeverity.Medium, "Invalid input causes a server error",
                    "Bounded invalid or boundary input produced an HTTP 5xx instead of a client error. Unhandled input is a robustness defect; exploitability is not asserted.",
                    "Validate the input and return 400/422 (ProblemDetails) for invalid values."),
                "fuzz-information-leak" => (ApiReviewSeverity.Medium, "Error responses expose internal details",
                    $"Responses to invalid input contain internal-detail indicators ({string.Join(", ", items.SelectMany(i => i.Result.LeakIndicators).Distinct(StringComparer.Ordinal))}). The text itself was not stored.",
                    "Return ProblemDetails without stack traces, exception types, SQL or paths; log details server-side."),
                "fuzz-unexpected-acceptance" => (ApiReviewSeverity.Low, "Input that violates the contract is accepted",
                    "A request that violates the published contract (type, enum, format or bounds) was answered as a valid request. This is a contract/validation gap, not by itself a vulnerability.",
                    "Enforce the documented constraints, or correct the contract if the API intentionally accepts these values."),
                _ => (ApiReviewSeverity.Low, "Rejection status not declared by the contract",
                    "Invalid input was rejected with a status code the OpenAPI contract does not declare for this operation.",
                    "Document the error responses (for example 400/404/422) or align the implementation with the contract."),
            };
            return new ApiFuzzFinding
            {
                LogicalKey = g.Key, RuleId = rule, Severity = severity, TargetId = first.TargetId, Operation = string.Join(", ", operations.Take(5)) + (operations.Count > 5 ? $" (+{operations.Count - 5})" : ""),
                Title = title, Description = description, Recommendation = recommendation, CaseIds = items.Select(i => i.Case.CaseId).Distinct(StringComparer.Ordinal).ToList(),
                Evidence = evidence, StandardsReferences = StandardsReferenceMappings.ForApiRule(rule).ToList(),
            };
        }).ToList();
    }

    private static int Rank(string rule) => rule switch { "fuzz-unexpected-5xx" => 0, "fuzz-information-leak" => 1, "fuzz-unexpected-acceptance" => 2, _ => 3 };
}

/// <summary>
/// Central backend request budget for one fuzz run: total and per-operation caps, one request at a time, a fixed delay between requests.
/// Nothing is sent without <see cref="TryAcquireAsync"/> succeeding; there is no loop that can outlive the budget.
/// </summary>
public sealed class ApiFuzzRequestBudget(ApiFuzzingSettings settings, TimeProvider? clock = null) : IDisposable
{
    private readonly SemaphoreSlim concurrency = new(Math.Clamp(settings.MaxConcurrency, 1, 1), 1);
    private readonly Dictionary<string, int> perOperation = new(StringComparer.Ordinal);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private DateTimeOffset? lastSent;

    public int Used { get; private set; }
    public int MaxInFlightObserved { get; private set; }
    private int inFlight;

    /// <summary>False when the total or per-operation cap is reached. Waits for the delay since the previous request and a free slot.</summary>
    public async Task<bool> TryAcquireAsync(string operationId, CancellationToken ct)
    {
        if (Used >= settings.MaxTotalRequests) return false;
        if (perOperation.GetValueOrDefault(operationId) >= settings.MaxCasesPerOperation) return false;
        await concurrency.WaitAsync(ct);
        try
        {
            if (lastSent is { } previous)
            {
                var wait = TimeSpan.FromMilliseconds(settings.RequestDelayMs) - (time.GetUtcNow() - previous);
                if (wait > TimeSpan.Zero) await Task.Delay(wait, time, ct);
            }
        }
        catch
        {
            concurrency.Release();
            throw;
        }
        Used++;
        perOperation[operationId] = perOperation.GetValueOrDefault(operationId) + 1;
        inFlight++;
        MaxInFlightObserved = Math.Max(MaxInFlightObserved, inFlight);
        return true;
    }

    public void Release()
    {
        inFlight--;
        lastSent = time.GetUtcNow();
        concurrency.Release();
    }

    public void Dispose() => concurrency.Dispose();
}
