using BirkNext.PerformanceTests;

namespace BirkNext.ApiReview;

/// <summary>How an observed operation relates to the declared contract operations. Candidate and Ambiguous are never promoted silently.</summary>
public enum OperationCorrelationConfidence { Confirmed, Candidate, Ambiguous, Unmatched }

/// <summary>
/// Evidence levels, kept distinct everywhere: Declared (in an imported contract), Observed (a captured runtime request), Correlated (declared
/// and observed associated with evidence), Verified (an explicit check established a property), Not verified. An observed HTTP 200 shows
/// transport-level success of that request only — never contract compatibility, authorization or business correctness.
/// </summary>
public enum ApiEvidenceLevel { Declared, Observed, Correlated, Verified, NotVerified }

public sealed record DeclaredApiOperation(string Method, string PathTemplate, string? ContractName = null);

/// <summary>A runtime observation (Endpoint Discovery): path without query, status, whether a bearer was present (never the token).</summary>
public sealed record ObservedApiOperation(string Method, string Path, int LastStatus, bool AuthObserved, int Count, DateTimeOffset LastObservedAt);

public sealed record ApiOperationCorrelationRow(ObservedApiOperation Observed, IReadOnlyList<DeclaredApiOperation> Declared, OperationCorrelationConfidence Confidence, string Reason)
{
    public ApiEvidenceLevel Level => Confidence == OperationCorrelationConfidence.Confirmed ? ApiEvidenceLevel.Correlated : ApiEvidenceLevel.Observed;
}

public sealed record ApiOperationCorrelationResult(IReadOnlyList<ApiOperationCorrelationRow> Rows, IReadOnlyList<DeclaredApiOperation> UnobservedDeclared, int DeclaredCount)
{
    public int Observed => Rows.Count;
    public int Correlated => Rows.Count(r => r.Confidence == OperationCorrelationConfidence.Confirmed);
    public int Candidates => Rows.Count(r => r.Confidence == OperationCorrelationConfidence.Candidate);
    public int Ambiguous => Rows.Count(r => r.Confidence == OperationCorrelationConfidence.Ambiguous);
    public int Unmatched => Rows.Count(r => r.Confidence == OperationCorrelationConfidence.Unmatched);
}

/// <summary>
/// The one REST correlation engine shared by API Quality Review, Performance Test Review and other consumers: method + path template with the
/// same normalization as the performance scenario engine (<see cref="PerformanceScenarioEngine.Matches"/>: placeholders match any segment,
/// query and trailing slash ignored, version segments are literal). The most specific template (most literal segments) wins; a tie is
/// Ambiguous; a match that needs an assumed base-path prefix (e.g. a reverse proxy's /api) is only a Candidate unless the target's base path
/// explains it. Similar names never make a match.
/// </summary>
public static class ApiOperationCorrelation
{
    public static ApiOperationCorrelationResult Correlate(IReadOnlyList<DeclaredApiOperation> declared, IReadOnlyList<ObservedApiOperation> observed, string? basePath = null)
    {
        var prefix = (basePath ?? "").Trim().TrimEnd('/');
        var rows = new List<ApiOperationCorrelationRow>();
        var used = new HashSet<DeclaredApiOperation>();
        foreach (var o in observed)
        {
            var path = prefix.Length > 1 && o.Path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ? o.Path[prefix.Length..] : o.Path;
            var segments = Segments(path).Length;
            var matches = declared.Where(d => string.Equals(d.Method, o.Method, StringComparison.OrdinalIgnoreCase) && PerformanceScenarioEngine.Matches(d.PathTemplate, path))
                .Select(d => (Declared: d, Literal: Segments(d.PathTemplate).Count(s => !s.StartsWith('{')), Offset: segments - Segments(d.PathTemplate).Length)).ToList();
            if (matches.Count == 0)
            {
                var otherMethod = declared.Any(d => PerformanceScenarioEngine.Matches(d.PathTemplate, path));
                rows.Add(new(o, [], OperationCorrelationConfidence.Unmatched, otherMethod
                    ? $"The path is declared, but not for {o.Method.ToUpperInvariant()}."
                    : "No declared operation matches this method and path."));
                continue;
            }
            var best = matches.Max(m => m.Literal);
            var top = matches.Where(m => m.Literal == best).ToList();
            if (top.Count > 1)
            {
                rows.Add(new(o, [.. top.Select(t => t.Declared)], OperationCorrelationConfidence.Ambiguous,
                    $"{top.Count} declared operations match equally ({string.Join(", ", top.Select(t => t.Declared.PathTemplate))}); confirm which one explicitly."));
                continue;
            }
            var match = top[0];
            used.Add(match.Declared);
            rows.Add(match.Offset == 0
                ? new(o, [match.Declared], OperationCorrelationConfidence.Confirmed, $"Method and path match {match.Declared.Method.ToUpperInvariant()} {match.Declared.PathTemplate}.")
                : new(o, [match.Declared], OperationCorrelationConfidence.Candidate,
                    $"Matches {match.Declared.PathTemplate} only if “/{string.Join('/', Segments(path).Take(match.Offset))}” is a base path (e.g. a reverse-proxy prefix) — review."));
        }
        var unobserved = declared.Where(d => !used.Contains(d) && rows.All(r => !r.Declared.Contains(d))).ToList();
        return new(rows, unobserved, declared.Count);
    }

    private static string[] Segments(string path) => path.Split('?', '#')[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
}
