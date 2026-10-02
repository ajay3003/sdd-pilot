using System.Text.RegularExpressions;
using BirkNext.Api.Services.ActiveCdcTests;
using BirkNext.CriticalE2E;
using BirkNext.PerformanceTests;
using HotChocolate.Language;

namespace BirkNext.Api.Services.PerformanceTests;

/// <summary>
/// Backend safety rules for performance tests — enforced on save, on readiness and again immediately before the provider starts; the UI is
/// never the guard. Production and unknown environments are refused; a host carrying a production marker (or a configured blocked host) is
/// refused whatever the claimed environment; destinations are relative paths under the configured target origin only; write methods and
/// GraphQL mutations are blocked; load is bounded by configured maximums.
/// </summary>
public static partial class PerformanceTestSafety
{
    public sealed record Issue(PerformanceReadinessState State, string Key, string Message);

    private static readonly string[] SensitiveHeaders = ["authorization", "cookie", "proxy-authorization", "x-api-key", "api-key", "x-auth-token", "x-csrf-token", "set-cookie"];

    /// <summary>Same allow-list as other active BirkNext automation (Local, Development, QA, Test, RC). Unknown = treated as production.</summary>
    public static string? EnvironmentBlock(string? environmentType) =>
        environmentType?.Trim().Equals("Production", StringComparison.OrdinalIgnoreCase) == true
            ? "Production environments cannot be used for performance tests."
            : CriticalE2EEnvironmentPolicy.AllowsAutomation(environmentType) ? null
            : string.IsNullOrWhiteSpace(environmentType) ? "The environment type is unknown; performance tests need an explicit non-production classification (Local, Development, QA, Test or RC)."
            : $"Performance tests are not permitted against a {environmentType.Trim()} environment; classify it as Local, Development, QA, Test or RC.";

    /// <summary>The target origin (scheme + host + port only) or why it is not acceptable. Host guards run whatever the environment claims.</summary>
    public static (Uri? Origin, string? Error) Origin(string? targetOrigin, string? environmentType, PerformanceTestOptions options)
    {
        if (!Uri.TryCreate(targetOrigin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return (null, "The target is not an absolute http(s) URL from the Target Environment.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) return (null, "The target URL must not contain credentials.");
        var host = uri.Host.ToLowerInvariant();
        var loopback = uri.IsLoopback;
        if (ActiveCdcPolicy.LooksLikeProduction(host))
            return (null, $"The target host {host} carries a production marker; production hosts cannot be load-tested, whatever the environment classification.");
        if (options.BlockedHosts.Any(p => HostMatches(host, p))) return (null, $"The target host {host} is blocked for performance tests in backend configuration.");
        if (options.AllowedHosts.Count > 0 && !loopback && !options.AllowedHosts.Any(p => HostMatches(host, p)))
            return (null, $"The target host {host} is not in the backend allow-list for performance tests (PerformanceTests:AllowedHosts).");
        return (new Uri(uri.GetLeftPart(UriPartial.Authority)), null);
    }

    private static bool HostMatches(string host, string pattern)
    {
        var p = pattern.Trim().ToLowerInvariant();
        return p.StartsWith("*.", StringComparison.Ordinal) ? host.EndsWith(p[1..], StringComparison.Ordinal) : host == p;
    }

    /// <summary>Every rule a definition must satisfy. Empty = safe to run (provider availability is checked separately).</summary>
    /// <summary>Resource observation may only name approved targets (no free-form container ids) and stay within the sampling/cooldown bounds.</summary>
    public static IEnumerable<Issue> ResourceIssues(PerformanceTestDefinition d, PerformanceTestOptions options)
    {
        if (d.ResourceObservation is not { Enabled: true } r) yield break;
        var o = options.Resources;
        Issue I(string message) => new(PerformanceReadinessState.NeedsConfiguration, "resources", message);
        var approved = Resources.ResourceObservationRegistry.ApprovedTargets(o, d.EnvironmentId, d.EnvironmentType).Select(t => t.Target.Id).ToHashSet(StringComparer.Ordinal);
        if (r.TargetComponentIds.Count == 0 && !r.ObserveLoadGenerator) yield return I("Resource observation is enabled but no component is selected.");
        if (r.TargetComponentIds.Count > 10) yield return I("At most 10 components can be observed in one run.");
        foreach (var id in r.TargetComponentIds.Where(id => !approved.Contains(id)).Distinct())
            yield return I($"'{(id.Length > 40 ? id[..40] : id)}' is not an approved resource target for this environment (targets are configured by the BirkNext administrator).");
        if (r.SampleIntervalSeconds < o.MinSampleIntervalSeconds || r.SampleIntervalSeconds > o.MaxSampleIntervalSeconds)
            yield return I($"The sample interval must be between {o.MinSampleIntervalSeconds} and {o.MaxSampleIntervalSeconds} seconds.");
        if (r.CooldownSeconds < 0 || r.CooldownSeconds > o.MaxCooldownSeconds) yield return I($"The cooldown must be between 0 and {o.MaxCooldownSeconds} seconds.");
        if (r.WarmupExclusionSeconds is { } wu && (wu < 0 || wu >= d.Workload.TotalSeconds)) yield return I("The warm-up exclusion must be shorter than the workload.");
        if (r.Policies.Count + r.DriftPolicies.Count > 50) yield return I("At most 50 resource policies are allowed.");
        foreach (var p in r.Policies)
        {
            if (new[] { p.AllowedAbsoluteGrowth, p.AllowedRelativeGrowthPercent, p.AllowedSlopePerMinute, p.MaxValue }.Any(v => v is < 0 || v is { } x && !double.IsFinite(x)))
                yield return I($"Resource policy for {ResourceFormat.Label(p.Metric)}: limits must be zero or positive.");
            if (p.MinimumObservationSeconds is < 0 || p.MinimumSampleCount is < 0) yield return I($"Resource policy for {ResourceFormat.Label(p.Metric)}: minimum evidence must be zero or positive.");
            if (p.TargetComponentId is { } t && !r.TargetComponentIds.Contains(t)) yield return I($"Resource policy for {ResourceFormat.Label(p.Metric)} names a component that is not observed.");
        }
        foreach (var p in r.DriftPolicies.Where(p => p.AllowedAbsoluteChange is < 0 || p.AllowedRelativeChangePercent is < 0))
            yield return I($"Resource drift policy for {ResourceFormat.Label(p.Metric)}: accepted change must be zero or positive.");
    }

    public static List<Issue> Check(PerformanceTestDefinition d, PerformanceTestDataProfile? data, PerformanceTestOptions options)
    {
        var issues = new List<Issue>();
        if (EnvironmentBlock(d.EnvironmentType) is { } env) issues.Add(new(PerformanceReadinessState.UnsafeEnvironment, "environment", env));
        var (_, originError) = Origin(d.TargetOrigin, d.EnvironmentType, options);
        if (originError is not null) issues.Add(new(PerformanceReadinessState.UnsafeEnvironment, "target", originError));
        if (d.AuthenticationReference is not null)
            issues.Add(new(PerformanceReadinessState.NeedsAuthentication, "authentication",
                "Authenticated performance tests need a non-interactive, approved test credential; BirkNext does not yet issue one for load tests (interactive or MFA logins are never simulated). Use an unauthenticated test endpoint or run this test outside BirkNext."));

        if (d.Scenario.Steps.Count == 0) issues.Add(new(PerformanceReadinessState.InvalidScenario, "scenario", "The scenario has no request steps."));
        if (d.Scenario.Steps.Count > 20) issues.Add(new(PerformanceReadinessState.InvalidScenario, "scenario", "A scenario may contain at most 20 steps."));
        foreach (var s in d.Scenario.Steps) issues.AddRange(StepIssues(d, s, data));

        if (d.Scenario.TestDataProfileId is not null)
        {
            if (data is null) issues.Add(new(PerformanceReadinessState.NeedsTestData, "testdata", "The selected test data profile does not exist."));
            else issues.AddRange(DataIssues(data, d));
        }
        else if (d.Scenario.Steps.Any(s => Placeholders(s).Any()))
            issues.Add(new(PerformanceReadinessState.NeedsTestData, "testdata", "The scenario uses {placeholders} but no approved test data profile is selected."));

        issues.AddRange(WorkloadIssues(d, options));
        issues.AddRange(ResourceIssues(d, options));
        return issues;
    }

    public static IEnumerable<string> Placeholders(HttpPerformanceStep s) =>
        PlaceholderPattern().Matches(string.Join("\n", new[] { s.RelativePath, s.BodyTemplate, s.GraphQlVariablesJson }.Concat(s.QueryParameters.Select(q => q.Value)).Where(t => t is not null)))
            .Select(m => m.Groups[1].Value).Distinct();

    private static IEnumerable<Issue> StepIssues(PerformanceTestDefinition d, HttpPerformanceStep s, PerformanceTestDataProfile? data)
    {
        var label = string.IsNullOrWhiteSpace(s.Name) ? "A step" : $"Step '{s.Name}'";
        if (!StepNamePattern().IsMatch(s.Name ?? ""))
            yield return new(PerformanceReadinessState.InvalidScenario, "step-name", $"{label}: names use letters, digits, spaces, '-', '_' or '.' (1–60 characters).");
        var method = (s.Method ?? "").Trim().ToUpperInvariant();
        if (PerformanceTestRules.BlockedMethods.Contains(method))
            yield return new(PerformanceReadinessState.InvalidScenario, "method", $"{label}: {method} is a write method; write and delete requests are blocked in performance tests.");
        else if (method == "POST")
        {
            if (d.TargetType == PerformanceTargetType.GraphQlHttp) { }
            else if (!s.PostIsSafeRead)
                yield return new(PerformanceReadinessState.InvalidScenario, "method", $"{label}: POST is only allowed for a read-only query/search API; mark the step as a safe read or use GET.");
        }
        else if (!PerformanceTestRules.ReadOnlyMethods.Contains(method))
            yield return new(PerformanceReadinessState.InvalidScenario, "method", $"{label}: the method '{s.Method}' is not supported.");
        if (d.TargetType == PerformanceTargetType.GraphQlHttp && method != "POST")
            yield return new(PerformanceReadinessState.InvalidScenario, "method", $"{label}: GraphQL operations are sent as POST.");

        var path = s.RelativePath ?? "";
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains("://", StringComparison.Ordinal) || path.Contains('\\')
            || path.Contains('?') || path.Contains('#') || path.Split('/').Any(seg => seg is ".." or ".") || path.Length > 500 || path.Any(char.IsControl))
            yield return new(PerformanceReadinessState.InvalidScenario, "path",
                $"{label}: the path must be a relative path under the configured target (starting with '/', no host, '..', query or fragment — use query parameters).");
        foreach (var h in s.Headers)
        {
            if (SensitiveHeaders.Contains(h.Name.Trim().ToLowerInvariant()))
                yield return new(PerformanceReadinessState.InvalidScenario, "header", $"{label}: the header '{h.Name}' can carry a credential and cannot be stored in a test definition.");
            else if (!HeaderNamePattern().IsMatch(h.Name) || h.Value.Any(char.IsControl) || h.Value.Length > 500)
                yield return new(PerformanceReadinessState.InvalidScenario, "header", $"{label}: the header '{h.Name}' is not a valid header name/value.");
        }
        if (s.ExpectedStatusCodes.Count == 0 || s.ExpectedStatusCodes.Any(c => c is < 100 or > 599))
            yield return new(PerformanceReadinessState.InvalidScenario, "status", $"{label}: expected status codes must be HTTP status codes (100–599).");
        if (s.ThinkTimeMs is < 0 or > 60_000)
            yield return new(PerformanceReadinessState.InvalidScenario, "think", $"{label}: think time must be between 0 and 60 000 ms.");
        if ((s.BodyTemplate?.Length ?? 0) > 16_384 || (s.GraphQlQuery?.Length ?? 0) > 16_384)
            yield return new(PerformanceReadinessState.InvalidScenario, "body", $"{label}: the request body is larger than 16 KB.");
        if (d.TargetType == PerformanceTargetType.GraphQlHttp && GraphQlIssue(s) is { } gql)
            yield return new(PerformanceReadinessState.InvalidScenario, "graphql", $"{label}: {gql}");
        if (data is not null && Placeholders(s).Except(data.Columns, StringComparer.Ordinal).ToList() is { Count: > 0 } missing)
            yield return new(PerformanceReadinessState.NeedsTestData, "placeholder", $"{label}: the test data profile has no column for {string.Join(", ", missing.Select(m => "{" + m + "}"))}.");
    }

    /// <summary>Query-only GraphQL, decided by parsing the document (Hot Chocolate parser) — never by a text heuristic.</summary>
    public static string? GraphQlIssue(HttpPerformanceStep s)
    {
        if (string.IsNullOrWhiteSpace(s.GraphQlQuery)) return "the GraphQL operation is empty.";
        DocumentNode document;
        try { document = Utf8GraphQLParser.Parse(s.GraphQlQuery); }
        catch (SyntaxException) { return "the GraphQL document could not be parsed."; }
        var operations = document.Definitions.OfType<OperationDefinitionNode>().ToList();
        if (operations.Count == 0) return "the GraphQL document has no operation.";
        if (operations.Any(o => o.Operation == OperationType.Mutation))
            return "the GraphQL operation is a mutation; mutations are blocked in the initial performance-test implementation.";
        if (operations.Any(o => o.Operation == OperationType.Subscription)) return "GraphQL subscriptions are not supported in performance tests.";
        if (operations.Count > 1 && string.IsNullOrWhiteSpace(s.GraphQlOperationName)) return "the document has several operations; name the one to run.";
        if (!string.IsNullOrWhiteSpace(s.GraphQlVariablesJson))
        {
            try { if (System.Text.Json.Nodes.JsonNode.Parse(s.GraphQlVariablesJson) is not System.Text.Json.Nodes.JsonObject) return "GraphQL variables must be a JSON object."; }
            catch (System.Text.Json.JsonException) { return "GraphQL variables are not valid JSON."; }
        }
        return null;
    }

    private static IEnumerable<Issue> DataIssues(PerformanceTestDataProfile data, PerformanceTestDefinition d)
    {
        if (!data.ApprovedSynthetic)
            yield return new(PerformanceReadinessState.NeedsTestData, "testdata", $"Test data profile '{data.Name}' is not confirmed as synthetic or approved test data.");
        if (data.Rows.Count == 0) yield return new(PerformanceReadinessState.NeedsTestData, "testdata", $"Test data profile '{data.Name}' has no rows.");
        foreach (var issue in DataValueIssues(data)) yield return issue;
        if (data.Selection == TestDataSelection.UniquePerVirtualUser && data.Rows.Count < (d.Workload.VirtualUsers ?? 1))
            yield return new(PerformanceReadinessState.NeedsTestData, "testdata", $"Unique-per-virtual-user selection needs at least {d.Workload.VirtualUsers} rows; the profile has {data.Rows.Count}.");
    }

    /// <summary>Values are short plain tokens (no quotes or markup) and never look like a national identity number (11 digits).</summary>
    public static IEnumerable<Issue> DataValueIssues(PerformanceTestDataProfile data)
    {
        if (data.Columns.Count == 0 || data.Columns.Count > 20 || data.Columns.Any(c => !ColumnPattern().IsMatch(c)) || data.Columns.Distinct().Count() != data.Columns.Count)
            yield return new(PerformanceReadinessState.NeedsTestData, "testdata", "Test data columns must be 1–20 unique names of letters, digits and '_' (starting with a letter or '_').");
        if (data.Rows.Count > 10_000) yield return new(PerformanceReadinessState.NeedsTestData, "testdata", "A test data profile may hold at most 10 000 rows.");
        var values = data.Rows.SelectMany(r => r).ToList();
        if (values.Any(v => !ValuePattern().IsMatch(v)))
            yield return new(PerformanceReadinessState.NeedsTestData, "testdata", "Test data values may contain letters, digits, spaces and - _ . @ : (at most 200 characters).");
        if (values.Any(v => IdentityNumberPattern().IsMatch(v)))
            yield return new(PerformanceReadinessState.NeedsTestData, "testdata", "A test data value looks like an 11-digit national identity number; use synthetic identifiers.");
    }

    public static IEnumerable<Issue> WorkloadIssues(PerformanceTestDefinition d, PerformanceTestOptions options)
    {
        var w = d.Workload;
        var limits = options.Limits(d.SafetyPolicy);
        var phases = new[] { w.WarmupSeconds, w.RampUpSeconds, w.SteadyStateSeconds, w.RampDownSeconds };
        if (phases.Any(p => p < 0)) { yield return new(PerformanceReadinessState.InvalidScenario, "workload", "Workload durations cannot be negative."); yield break; }
        if (w.SteadyStateSeconds < 10) yield return new(PerformanceReadinessState.InvalidScenario, "workload", "The steady state must last at least 10 seconds.");
        var total = (long)w.WarmupSeconds + w.RampUpSeconds + w.SteadyStateSeconds + w.RampDownSeconds;
        var max = w.Purpose == WorkloadPurpose.Soak ? limits.MaxSoakDurationSeconds : limits.MaxDurationSeconds;
        if (w.MaxDurationSeconds is { } m && (m <= 0 || total > m)) yield return new(PerformanceReadinessState.InvalidScenario, "workload", $"The phases last {total} s, beyond the workload's own maximum of {m} s.");
        if (total > max) yield return new(PerformanceReadinessState.Blocked, "limit-duration", $"The workload lasts {total} s; the safety limit for a {w.Purpose} test is {max} s.");
        if (w.Purpose == WorkloadPurpose.Stress && !limits.AllowStress) yield return new(PerformanceReadinessState.Blocked, "limit-purpose", "Stress tests are disabled for this BirkNext instance.");
        if (w.Purpose == WorkloadPurpose.Soak && !limits.AllowSoak) yield return new(PerformanceReadinessState.Blocked, "limit-purpose", "Soak tests are disabled for this BirkNext instance.");
        if (w.VirtualUsers is not { } vus || vus < 1) yield return new(PerformanceReadinessState.InvalidScenario, "workload", "Set a number of virtual users (at least 1).");
        else if (vus > limits.MaxVirtualUsers) yield return new(PerformanceReadinessState.Blocked, "limit-vus", $"{vus} virtual users exceed the safety limit of {limits.MaxVirtualUsers}.");
        if (w.Mode == WorkloadMode.ArrivalRate)
        {
            if (w.RequestsPerSecond is not { } rps || rps <= 0 || !double.IsFinite(rps)) yield return new(PerformanceReadinessState.InvalidScenario, "workload", "Set a target rate (requests per second) above 0.");
            else if (rps > limits.MaxRequestsPerSecond) yield return new(PerformanceReadinessState.Blocked, "limit-rate", $"{rps} req/s exceeds the safety limit of {limits.MaxRequestsPerSecond} req/s.");
            if (w.StartRequestsPerSecond is { } start && (start < 0 || !double.IsFinite(start) || start > (w.RequestsPerSecond ?? 0)))
                yield return new(PerformanceReadinessState.InvalidScenario, "workload", "The start rate must be between 0 and the target rate.");
        }
        var estimate = EstimatedMaxRequests(d);
        if (estimate > limits.MaxTotalRequests)
            yield return new(PerformanceReadinessState.Blocked, "limit-requests", $"The workload can issue up to ~{estimate:N0} requests; the safety limit is {limits.MaxTotalRequests:N0}. Add think time or reduce load.");
    }

    /// <summary>Upper bound of requests: exact for arrival rate (rate × time × steps); for virtual users assumes ≥ 50 ms per request plus think time.</summary>
    public static long EstimatedMaxRequests(PerformanceTestDefinition d)
    {
        var w = d.Workload;
        var steps = Math.Max(1, d.Scenario.Steps.Count);
        if (w.Mode == WorkloadMode.ArrivalRate)
        {
            var target = w.RequestsPerSecond ?? 0;
            var start = w.StartRequestsPerSecond ?? Math.Max(1, target / 10);
            var iterations = start * w.WarmupSeconds + (start + target) / 2 * w.RampUpSeconds + target * w.SteadyStateSeconds + target / 2 * w.RampDownSeconds;
            return (long)Math.Ceiling(iterations * steps);
        }
        var vus = w.VirtualUsers ?? 1;
        var iterationSeconds = d.Scenario.Steps.Sum(s => 0.05 + (s.ThinkTimeMs ?? 0) / 1000.0);
        if (iterationSeconds <= 0) iterationSeconds = 0.05;
        var vuSeconds = PerformanceTestRules.WarmupVirtualUsers(w) * (double)w.WarmupSeconds + (vus / 2.0) * w.RampUpSeconds + vus * (double)w.SteadyStateSeconds + vus / 2.0 * w.RampDownSeconds;
        return (long)Math.Ceiling(vuSeconds / iterationSeconds * steps);
    }

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")] internal static partial Regex PlaceholderPattern();
    [GeneratedRegex(@"^[A-Za-z0-9 _.\-]{1,60}$")] private static partial Regex StepNamePattern();
    [GeneratedRegex(@"^[A-Za-z0-9\-]{1,80}$")] private static partial Regex HeaderNamePattern();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,39}$")] private static partial Regex ColumnPattern();
    [GeneratedRegex(@"^[A-Za-z0-9 _.\-@:]{0,200}$")] private static partial Regex ValuePattern();
    [GeneratedRegex(@"^\d{11}$")] private static partial Regex IdentityNumberPattern();
}
