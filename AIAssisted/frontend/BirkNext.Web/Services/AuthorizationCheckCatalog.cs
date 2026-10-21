using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BirkNext.AiCodeReview;
using BirkNext.ApiReview;
using BirkNext.Integrations;
using BirkNext.RuntimeSecurity;

namespace BirkNext.Web.Services;

/// <summary>Why one authorization check can or cannot run now.</summary>
public enum AuthorizationCheckReadiness
{
    Ready,
    NeedsIdentity,
    NeedsConfirmation,
    UnsafeOperationApprovalRequired,
    BlockedByExecutionPermission,
    NotApplicable,
}

/// <summary>
/// One authorization check as a tester reads it: one operation, one identity, one expected outcome. Backed by the existing
/// <see cref="AuthorizationScenario"/> model — a configured scenario contributes one check per identity; a suggestion is a scenario that
/// has not been added to the Target Environment yet.
/// </summary>
public sealed record AuthorizationCheckItem(
    string Key,
    string Api,
    string? TargetId,
    string Operation,
    string Identity,
    AuthorizationExpectation Expected,
    AuthorizationCheckOrigin Origin,
    AuthorizationCheckConfirmation Confirmation,
    AuthorizationCheckReadiness Readiness,
    string Reason,
    bool Configured,
    AuthorizationScenario Scenario,
    string? Evidence);

/// <summary>Counts the pre-run summary and the editor show; every number is a count of checks (operation × identity), never of scenarios.</summary>
public sealed record AuthorizationCheckSummary(IReadOnlyList<AuthorizationCheckItem> Items)
{
    public IReadOnlyList<AuthorizationCheckItem> Configured => Items.Where(i => i.Configured).ToList();
    public IReadOnlyList<AuthorizationCheckItem> Suggested => Items.Where(i => !i.Configured).ToList();
    public int Available => Items.Count(i => i.Readiness != AuthorizationCheckReadiness.NotApplicable);
    public int Ready => Items.Count(i => i.Readiness == AuthorizationCheckReadiness.Ready);
    public int NeedIdentity => Items.Count(i => i.Readiness == AuthorizationCheckReadiness.NeedsIdentity);
    public int NeedConfirmation => Items.Count(i => i.Readiness == AuthorizationCheckReadiness.NeedsConfirmation);
    public int Blocked => Items.Count(i => i.Readiness is AuthorizationCheckReadiness.BlockedByExecutionPermission or AuthorizationCheckReadiness.UnsafeOperationApprovalRequired);

    /// <summary>"4 available · 2 ready · 2 need identity mapping" — actual counts, zero parts omitted.</summary>
    public string Headline
    {
        get
        {
            if (Available == 0) return "No checks available";
            var parts = new List<string> { $"{Available} available" };
            parts.Add($"{Ready} ready");
            if (NeedIdentity > 0) parts.Add($"{NeedIdentity} need{(NeedIdentity == 1 ? "s" : "")} identity mapping");
            if (NeedConfirmation > 0) parts.Add($"{NeedConfirmation} need{(NeedConfirmation == 1 ? "s" : "")} confirmation");
            if (Blocked > 0) parts.Add($"{Blocked} blocked");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// The authorization checks of one Target Environment and API scope: the configured scenarios plus suggestions from three kinds of evidence —
/// a generic baseline (an unauthenticated request to a protected API is denied), literal source authorization metadata, and Test Pack
/// declarations. Rules it keeps:
/// <list type="bullet">
/// <item>No business rule is guessed. An endpoint that is only "authenticated" gets the baseline and nothing else; a role or policy check is
/// suggested only when the source names the policy or role, and stays Needs confirmation until its owner confirms it.</item>
/// <item>Identities are role labels mapped to execution aliases by the Target Environment; no credential is read or stored here.</item>
/// <item>Availability never grants execution: a check is Ready only when the server's active-execution gate allows authorization checks.</item>
/// <item>Only safe requests (REST GET/HEAD, GraphQL queries) are ever runnable; anything else is reported, never sent.</item>
/// </list>
/// </summary>
public static class AuthorizationCheckCatalog
{
    public const string BaselineGraphQlQuery = "{ __typename }";

    /// <param name="executionPermitted">The server's decision for active authorization checks on this Target Environment (null: not known yet).</param>
    public static AuthorizationCheckSummary Build(
        RuntimeSecurityExpectations? settings,
        IReadOnlyList<ApiReviewTarget> targets,
        IReadOnlyList<CodeEndpointAuthorization>? sourceEndpoints = null,
        IReadOnlyList<TestPackAuthorizationExpectation>? packExpectations = null,
        bool? executionPermitted = null,
        string? executionReason = null)
    {
        settings ??= new RuntimeSecurityExpectations();
        var configured = settings.AuthorizationScenarios;
        var items = new List<AuthorizationCheckItem>();
        foreach (var scenario in configured)
            items.AddRange(Expand(scenario, true, targets, settings.AuthorizationIdentities, executionPermitted, executionReason));

        var configuredKeys = configured.Select(Signature).ToHashSet(StringComparer.Ordinal);
        foreach (var suggestion in Suggestions(targets, sourceEndpoints, packExpectations).Where(s => !configuredKeys.Contains(Signature(s))))
            items.AddRange(Expand(suggestion, false, targets, settings.AuthorizationIdentities, executionPermitted, executionReason));
        return new(items);
    }

    /// <summary>Every suggested scenario for the selected targets, deduplicated; none of them is stored until a person adds it.</summary>
    public static IReadOnlyList<AuthorizationScenario> Suggestions(
        IReadOnlyList<ApiReviewTarget> targets,
        IReadOnlyList<CodeEndpointAuthorization>? sourceEndpoints = null,
        IReadOnlyList<TestPackAuthorizationExpectation>? packExpectations = null)
    {
        var result = new List<AuthorizationScenario>();
        foreach (var target in targets.Where(t => t.AuthRequired))
            if (Baseline(target) is { } baseline) result.Add(baseline);
        foreach (var endpoint in sourceEndpoints ?? [])
            result.AddRange(FromSource(endpoint, targets));
        foreach (var expectation in packExpectations ?? [])
            if (FromTestPack(expectation, targets) is { } pack) result.Add(pack);
        return result.GroupBy(Signature, StringComparer.Ordinal).Select(g => g.First()).ToList();
    }

    // ── Baseline ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A protected API must deny an unauthenticated request. Confirmed by definition (it restates the target's own authentication
    /// requirement), anonymous needs no identity mapping. REST uses the first observed safe GET without path parameters; GraphQL the
    /// introspection-free <c>{ __typename }</c> query. Null when no safe request exists — nothing is invented.
    /// </summary>
    public static AuthorizationScenario? Baseline(ApiReviewTarget target)
    {
        string url;
        string operation;
        if (target.ApiType == ApiReviewTargetType.GraphQl)
        {
            url = target.Url;
            operation = "GraphQL { __typename }";
        }
        else
        {
            var op = target.Operations.FirstOrDefault(o => o.Method == "GET" && !o.Historical && !o.Path.Contains('{'));
            if (op is null) return null;
            url = target.Origin + op.Path;
            operation = $"GET {op.Path}";
        }
        return new AuthorizationScenario
        {
            ScenarioId = Id("baseline", target.TargetId, operation),
            DisplayName = $"Unauthenticated access to {operation}",
            ApiType = target.ApiType == ApiReviewTargetType.GraphQl ? AuthorizationScenarioApiType.GraphQl : AuthorizationScenarioApiType.Rest,
            Url = url,
            Method = "GET",
            GraphQlQuery = target.ApiType == ApiReviewTargetType.GraphQl ? BaselineGraphQlQuery : null,
            Identities = [new() { Alias = AuthorizationIdentityAliases.Anonymous, Role = "Anonymous", Expected = AuthorizationExpectation.Deny }],
            Origin = AuthorizationCheckOrigin.PredefinedBaseline,
            Confirmation = AuthorizationCheckConfirmation.Confirmed,
            Evidence = "The API requires authentication.",
            TargetId = target.TargetId,
        };
    }

    // ── Source ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Suggestions from literal source metadata, matched to a selected target's operation by method and route template:
    /// a named policy or role → an identity holding it is allowed and an identity without it is denied; [AllowAnonymous] → anonymous is allowed.
    /// "Authenticated" alone adds nothing (the baseline covers it). Every suggestion needs confirmation; role identities need mapping.
    /// </summary>
    public static IReadOnlyList<AuthorizationScenario> FromSource(CodeEndpointAuthorization endpoint, IReadOnlyList<ApiReviewTarget> targets)
    {
        var names = (endpoint.Policies ?? []).Select(p => (Label: p, Kind: "policy")).Concat((endpoint.Roles ?? []).Select(r => (Label: r, Kind: "role"))).ToList();
        if (names.Count == 0 && !endpoint.AllowAnonymous) return [];
        if (endpoint.Kind != "HTTP" || endpoint.Route is null) return [];
        var method = endpoint.Key.Split('|') is var parts && parts.Length > 2 && parts[2] != "minimal"
            ? parts[2].Replace("Http", "", StringComparison.Ordinal).ToUpperInvariant()
            : endpoint.Display.Split(' ')[0].ToUpperInvariant();
        var match = Match(targets, method, endpoint.Route);
        if (match is null) return [];
        var (target, path) = match.Value;
        var operation = $"{method} {endpoint.Route}";
        var file = endpoint.File.Split('/', '\\').Last();
        var evidenceBasis = names.Count > 0
            ? string.Join(", ", names.Select(n => $"{n.Kind} \"{n.Label}\""))
            : "[AllowAnonymous]";

        AuthorizationScenario Scenario(string kind, List<AuthorizationScenarioIdentity> identities) => new()
        {
            ScenarioId = Id("source", target.TargetId, operation + "|" + kind),
            DisplayName = $"{operation} — {kind}",
            ApiType = AuthorizationScenarioApiType.Rest,
            Url = target.Origin + path,
            Method = method,
            Identities = identities,
            Origin = AuthorizationCheckOrigin.SourceDerived,
            Confirmation = AuthorizationCheckConfirmation.NeedsConfirmation,
            Evidence = $"{evidenceBasis} · {file}:{endpoint.Line}",
            TargetId = target.TargetId,
        };

        var result = new List<AuthorizationScenario>();
        foreach (var (label, kind) in names)
            result.Add(Scenario($"{kind} {label}",
            [
                new() { Alias = "", Role = label, Expected = AuthorizationExpectation.Allow },
                new() { Alias = "", Role = $"Without {label}", Expected = AuthorizationExpectation.Deny },
            ]));
        if (endpoint.AllowAnonymous && names.Count == 0)
            result.Add(Scenario("anonymous access", [new() { Alias = AuthorizationIdentityAliases.Anonymous, Role = "Anonymous", Expected = AuthorizationExpectation.Allow }]));
        return result;
    }

    // ── Test Pack ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A pack expectation for a selected target. Confirmed when the pack says it is authoritative; an empty role is anonymous.</summary>
    public static AuthorizationScenario? FromTestPack(TestPackAuthorizationExpectation expectation, IReadOnlyList<ApiReviewTarget> targets)
    {
        var target = targets.FirstOrDefault(t => Contains(t.ServiceName, expectation.ApiSelector) || Contains(t.BasePath, expectation.ApiSelector)
            || Contains(ApiReviewPresentation.DisplayName(t), expectation.ApiSelector));
        if (target is null || string.IsNullOrWhiteSpace(expectation.ApiSelector)) return null;
        var graphQl = target.ApiType == ApiReviewTargetType.GraphQl;
        string url, operation;
        if (graphQl)
        {
            if (string.IsNullOrWhiteSpace(expectation.GraphQlQuery)) return null;
            url = target.Url;
            operation = "GraphQL " + Compact(expectation.GraphQlQuery);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(expectation.Path)) return null;
            var match = Match([target], expectation.Method.ToUpperInvariant(), expectation.Path);
            url = target.Origin + (match?.Path ?? expectation.Path);
            operation = $"{expectation.Method.ToUpperInvariant()} {expectation.Path}";
        }
        var anonymous = string.IsNullOrWhiteSpace(expectation.Role);
        return new AuthorizationScenario
        {
            ScenarioId = Id("pack", target.TargetId, operation + "|" + expectation.Role + "|" + expectation.Expected),
            DisplayName = $"{operation} — {(anonymous ? "anonymous" : expectation.Role)}",
            ApiType = graphQl ? AuthorizationScenarioApiType.GraphQl : AuthorizationScenarioApiType.Rest,
            Url = url,
            Method = graphQl ? "GET" : expectation.Method.ToUpperInvariant(),
            GraphQlQuery = graphQl ? expectation.GraphQlQuery : null,
            Identities = [new()
            {
                Alias = anonymous ? AuthorizationIdentityAliases.Anonymous : "",
                Role = anonymous ? "Anonymous" : expectation.Role,
                Expected = expectation.Expected,
            }],
            Origin = AuthorizationCheckOrigin.TestPack,
            Confirmation = expectation.Confirmed ? AuthorizationCheckConfirmation.Confirmed : AuthorizationCheckConfirmation.NeedsConfirmation,
            Evidence = string.IsNullOrWhiteSpace(expectation.Note) ? "Declared by the Test Pack." : expectation.Note,
            TargetId = target.TargetId,
        };
    }

    // ── Identity mapping and execution ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The scenario as it would be sent: every role without an alias gets the Target Environment's mapped alias. Unmapped roles stay empty, which
    /// the shared validation (and the backend) refuse — a check never runs as an identity nobody chose.
    /// </summary>
    public static AuthorizationScenario Resolve(AuthorizationScenario scenario, IReadOnlyList<AuthorizationIdentityMapping> mappings)
    {
        var copy = System.Text.Json.JsonSerializer.Deserialize<AuthorizationScenario>(System.Text.Json.JsonSerializer.Serialize(scenario))!;
        foreach (var identity in copy.Identities.Where(i => string.IsNullOrWhiteSpace(i.Alias)))
            identity.Alias = MappedAlias(identity.Role, mappings) ?? "";
        return copy;
    }

    public static string? MappedAlias(string role, IReadOnlyList<AuthorizationIdentityMapping> mappings) =>
        mappings.FirstOrDefault(m => string.Equals(m.Role.Trim(), role.Trim(), StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Alias))?.Alias.Trim();

    /// <summary>
    /// What an authorization run sends: each scenario with at least one Ready check, reduced to its Ready identities and resolved to their mapped
    /// aliases. Checks that need confirmation or an identity are left out rather than sent half-configured.
    /// </summary>
    public static List<AuthorizationScenario> Runnable(AuthorizationCheckSummary summary, IReadOnlyList<AuthorizationIdentityMapping> mappings) =>
        summary.Items.Where(i => i.Readiness == AuthorizationCheckReadiness.Ready).GroupBy(i => i.Scenario).Select(group =>
        {
            var resolved = Resolve(group.Key, mappings);
            var ready = group.Select(i => (i.Identity, i.Expected)).ToHashSet();
            resolved.Identities = resolved.Identities.Where(id => ready.Contains((string.IsNullOrWhiteSpace(id.Role) ? id.Alias : id.Role, id.Expected))).ToList();
            return resolved;
        }).ToList();

    /// <summary>The roles the checks need that the Target Environment has not mapped to a test identity yet.</summary>
    public static IReadOnlyList<string> UnmappedRoles(AuthorizationCheckSummary summary) =>
        summary.Items.Where(i => i.Readiness == AuthorizationCheckReadiness.NeedsIdentity).Select(i => i.Identity).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static IEnumerable<AuthorizationCheckItem> Expand(AuthorizationScenario scenario, bool configured, IReadOnlyList<ApiReviewTarget> targets,
        IReadOnlyList<AuthorizationIdentityMapping> mappings, bool? executionPermitted, string? executionReason)
    {
        var target = targets.FirstOrDefault(t => t.TargetId == scenario.TargetId)
            ?? targets.FirstOrDefault(t => Uri.TryCreate(scenario.Url, UriKind.Absolute, out var u) && string.Equals(t.Origin, u.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
                && u.AbsolutePath.StartsWith(t.BasePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
        var api = target is null ? HostOf(scenario.Url) : ApiReviewPresentation.DisplayName(target);
        var operation = scenario.ApiType == AuthorizationScenarioApiType.GraphQl
            ? "GraphQL " + Compact(scenario.GraphQlQuery ?? "")
            : $"{scenario.Method.ToUpperInvariant()} {PathOf(scenario.Url)}";
        foreach (var identity in scenario.Identities)
        {
            var alias = string.IsNullOrWhiteSpace(identity.Alias) ? MappedAlias(identity.Role, mappings) : identity.Alias;
            var label = string.IsNullOrWhiteSpace(identity.Role) ? identity.Alias : identity.Role;
            var (readiness, reason) = Readiness(scenario, alias, target is not null, executionPermitted, executionReason);
            yield return new(
                $"{scenario.ScenarioId}|{label}|{identity.Expected}", api, target?.TargetId ?? scenario.TargetId, operation, label, identity.Expected,
                scenario.Origin, scenario.Confirmation, readiness, reason, configured, scenario, scenario.Evidence);
        }
    }

    private static (AuthorizationCheckReadiness, string) Readiness(AuthorizationScenario scenario, string? alias, bool targetSelected, bool? executionPermitted, string? executionReason)
    {
        if (!targetSelected) return (AuthorizationCheckReadiness.NotApplicable, "Its API is not in the selected review scope.");
        var unsafeRequest = scenario.ApiType == AuthorizationScenarioApiType.Rest
            ? scenario.Method.Trim().ToUpperInvariant() is not ("GET" or "HEAD")
            : Regex.IsMatch(scenario.GraphQlQuery ?? "", @"\b(mutation|subscription)\b", RegexOptions.IgnoreCase);
        if (unsafeRequest)
            return (AuthorizationCheckReadiness.UnsafeOperationApprovalRequired,
                "Not a safe request. Authorization checks send GET/HEAD or GraphQL queries only; this operation is never sent automatically.");
        if (scenario.Confirmation == AuthorizationCheckConfirmation.NeedsConfirmation)
            return (AuthorizationCheckReadiness.NeedsConfirmation, "Suggested from evidence. Confirm the expected outcome before it can run.");
        if (string.IsNullOrWhiteSpace(alias))
            return (AuthorizationCheckReadiness.NeedsIdentity, "Map this role to a test identity in the Target Environment.");
        if (executionPermitted != true)
            return (AuthorizationCheckReadiness.BlockedByExecutionPermission,
                executionReason ?? "Active authorization checks are not permitted for this Target Environment.");
        return (AuthorizationCheckReadiness.Ready, "Will run with the next authorization check run.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The operation of a selected target whose observed path matches the route template (case-insensitive, {params} match one segment).</summary>
    private static (ApiReviewTarget Target, string Path)? Match(IReadOnlyList<ApiReviewTarget> targets, string method, string route)
    {
        var template = "^" + Regex.Replace(Regex.Escape(route.TrimEnd('/')), @"\\\{[^/]+?\}", "[^/]+") + "/?$";
        foreach (var target in targets.Where(t => t.ApiType == ApiReviewTargetType.Rest))
            foreach (var op in target.Operations.Where(o => o.Method == method))
                if (Regex.IsMatch(op.Path, template, RegexOptions.IgnoreCase)) return (target, op.Path);
        return null;
    }

    public static string Signature(AuthorizationScenario s) =>
        string.Join("|", s.ApiType, s.Method.ToUpperInvariant(), s.Url.ToLowerInvariant(), Compact(s.GraphQlQuery ?? ""),
            string.Join(",", s.Identities.Select(i => $"{i.Role.ToLowerInvariant()}={i.Expected}")));

    private static string Id(string prefix, string targetId, string operation) =>
        $"{prefix}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(targetId + "|" + operation)), 0, 5).ToLowerInvariant()}";

    private static bool Contains(string? value, string selector) => value?.Contains(selector.Trim(), StringComparison.OrdinalIgnoreCase) == true;
    private static string Compact(string text) => Regex.Replace(text.Trim(), @"\s+", " ") is var c && c.Length > 60 ? c[..57] + "…" : c;
    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
    private static string PathOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;

    public static string OriginLabel(AuthorizationCheckOrigin origin) => origin switch
    {
        AuthorizationCheckOrigin.PredefinedBaseline => "Predefined baseline",
        AuthorizationCheckOrigin.SourceDerived => "Source-derived",
        AuthorizationCheckOrigin.ContractDerived => "Contract-derived",
        AuthorizationCheckOrigin.TestPack => "Test Pack",
        _ => "Manual",
    };

    public static string ConfirmationLabel(AuthorizationCheckItem item) => item.Readiness switch
    {
        AuthorizationCheckReadiness.NeedsIdentity => "Identity mapping required",
        AuthorizationCheckReadiness.UnsafeOperationApprovalRequired or AuthorizationCheckReadiness.NotApplicable => "Not executable",
        _ => item.Confirmation == AuthorizationCheckConfirmation.Confirmed ? "Confirmed expectation" : "Needs confirmation",
    };

    public static string ReadinessLabel(AuthorizationCheckReadiness readiness) => readiness switch
    {
        AuthorizationCheckReadiness.Ready => "Ready",
        AuthorizationCheckReadiness.NeedsIdentity => "Needs identity",
        AuthorizationCheckReadiness.NeedsConfirmation => "Needs confirmation",
        AuthorizationCheckReadiness.UnsafeOperationApprovalRequired => "Unsafe operation approval required",
        AuthorizationCheckReadiness.BlockedByExecutionPermission => "Blocked by execution permission",
        _ => "Not applicable",
    };

    public static string ExpectedLabel(AuthorizationExpectation expected) => expected == AuthorizationExpectation.Allow ? "Allowed" : "Denied";
}
