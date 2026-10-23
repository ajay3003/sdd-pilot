using BirkNext.Integrations;
using BirkNext.LocalHttpsProxy;
using BirkNext.TestData;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Evidence-first M01 test context: resolves what is already known before anything is typed — the environment from the Target Environment
/// (DEV/QA only; Production never runnable), the Person GraphQL endpoint from the Target Environment and Endpoint Discovery (a disagreement is a
/// conflict, never a silent choice), test children per classification level and the two test identities from APPROVED Test Data references.
/// Opaque references only (no names, national ids or DUF), never a token, and never the test-lead approval. Read-only: nothing is saved.
/// </summary>
public static class ClassificationContextResolution
{
    public enum RowState { Ready, Missing, Conflict, RequiresApproval, NotAllowed }

    public sealed record Row(string Key, string Label, RowState State, string? Display, string Source);

    public sealed record Result(IReadOnlyList<Row> Rows, string? Environment, string? Endpoint, string? Unauthorized, string? Authorized, IReadOnlyDictionary<int, string> Children)
    {
        public string Summary =>
            $"{Rows.Count(r => r.State == RowState.Ready)} resolved · {Rows.Count(r => r.State is RowState.Missing or RowState.NotAllowed)} missing · {Rows.Count(r => r.State == RowState.Conflict)} conflict · {Rows.Count(r => r.State == RowState.RequiresApproval)} requires approval";
    }

    /// <summary>Test Data capabilities that mark an approved reference for this use (declared by the Test Data provider, never guessed from names).</summary>
    public static string LevelCapability(int level) => $"classification-level-{level}";
    public const string UnauthorizedCapability = "graded-access-unauthorized";
    public const string AuthorizedCapability = "graded-access-authorized";

    public static string Label(RowState state) => state switch
    {
        RowState.Ready => "Ready",
        RowState.Conflict => "Conflict · review required",
        RowState.RequiresApproval => "Requires approval",
        RowState.NotAllowed => "Not allowed",
        _ => "Missing",
    };

    public static Result Resolve(FrontendAnalysisProfile profile, EndpointDiscoverySnapshot? discovery, IReadOnlyList<TestDataAssetSummary> assets, IReadOnlyList<int> levels)
    {
        var rows = new List<Row>();
        var environment = profile.EnvironmentType switch
        {
            FrontendEnvironmentType.Development => "DEV",
            FrontendEnvironmentType.QA => "QA",
            _ => null,
        };
        rows.Add(new("environment", "Environment", environment is null ? RowState.NotAllowed : RowState.Ready, environment ?? profile.EnvironmentType.ToString(),
            environment is null ? "Target Environment · only DEV/QA can be tested; Production is never runnable" : $"Target Environment · {profile.Name}"));

        // Endpoint: the configured GraphQL endpoint and every distinct observed GraphQL endpoint; several different values are a conflict.
        var configured = string.IsNullOrWhiteSpace(profile.GraphQlEndpoint) ? null : profile.GraphQlEndpoint.Trim().TrimEnd('/');
        var observed = (discovery?.Shared ?? []).Where(endpoint => endpoint.Category == ObservedTrafficCategory.GraphQl || endpoint.Path.Contains("graphql", StringComparison.OrdinalIgnoreCase))
            .Select(endpoint => $"{endpoint.Scheme}://{endpoint.Host}{endpoint.Path}".TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var candidates = new[] { configured }.OfType<string>().Concat(observed).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var endpointValue = candidates.Count == 1 ? candidates[0] : null;
        rows.Add(new("endpoint", "Person GraphQL endpoint", candidates.Count switch { 0 => RowState.Missing, 1 => RowState.Ready, _ => RowState.Conflict },
            candidates.Count > 1 ? string.Join(" | ", candidates) : endpointValue,
            candidates.Count == 0 ? "Not configured in Target Environment and not observed by Endpoint Discovery"
            : configured is not null && observed.Contains(configured, StringComparer.OrdinalIgnoreCase) ? "Target Environment · confirmed by Endpoint Discovery"
            : configured is not null && observed.Count == 0 ? "Target Environment"
            : configured is null ? "Endpoint Discovery (this browser)" : "Target Environment and Endpoint Discovery disagree"));

        var approved = assets.Where(asset => asset.Approval == TestDataApproval.Approved
            && (asset.Environment is null || string.Equals(asset.Environment, profile.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(asset.Environment, environment, StringComparison.OrdinalIgnoreCase))).ToList();
        TestDataAssetSummary? One(string capability, out RowState state)
        {
            var matches = approved.Where(asset => asset.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase)).ToList();
            state = matches.Count switch { 0 => RowState.Missing, 1 => RowState.Ready, _ => RowState.Conflict };
            return matches.Count == 1 ? matches[0] : null;
        }
        var children = new Dictionary<int, string>();
        foreach (var level in levels.DefaultIfEmpty().Distinct().Where(level => levels.Count > 0))
        {
            var asset = One(LevelCapability(level), out var state);
            // Only an opaque GUID reference is used; anything else stays for a person to enter.
            var id = asset?.DisplayValue is { } value && Guid.TryParse(value, out _) ? value : null;
            if (id is not null) children[level] = id;
            rows.Add(new($"child-{level}", $"Level {level} test child", asset is not null && id is null ? RowState.Missing : state, asset is null ? null : $"{asset.Alias}{(id is null ? "" : $" · {id}")}",
                asset is null ? state == RowState.Conflict ? "Several approved Test Data references — choose one" : "No approved Test Data reference with this capability"
                : id is null ? "Approved Test Data reference without an opaque registration id" : $"Test Data · {asset.Source}"));
        }
        string? Identity(string key, string label, string capability)
        {
            var asset = One(capability, out var state);
            rows.Add(new(key, label, state, asset?.Alias, asset is null ? state == RowState.Conflict ? "Several approved identities — choose one" : "No approved Test Data identity alias" : $"Test Data · {asset.Source} (alias only; tokens are never stored)"));
            return asset?.Alias;
        }
        var unauthorized = Identity("unauthorized", "Unauthorized identity", UnauthorizedCapability);
        var authorized = Identity("authorized", "Authorized graded identity", AuthorizedCapability);
        rows.Add(new("approval", "Test-lead approval", RowState.RequiresApproval, null, "Manual: a test lead approves this exact context; never imported or resolved"));
        return new Result(rows, environment, endpointValue, unauthorized, authorized, children);
    }

    /// <summary>What an approval is bound to: environment, endpoint, test children and identities (values hashed, never shown).</summary>
    public static string Fingerprint(ClassificationTestContext context) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|",
        context.Environment, context.GraphQlEndpoint, context.UnauthorizedIdentityLabel, context.AuthorizedIdentityLabel,
        string.Join(",", context.TestChildren.OrderBy(child => child.Nivaa).Select(child => $"{child.Nivaa}:{child.BarnRegistreringId}:{child.BirkId}"))))))[..16];
}
