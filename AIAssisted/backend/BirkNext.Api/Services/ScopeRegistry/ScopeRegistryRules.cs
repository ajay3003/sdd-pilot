using System.Security.Cryptography;
using System.Text;
using BirkNext.Scopes;

namespace BirkNext.Api.Services.ScopeRegistry;

/// <summary>
/// The pure rules of the shared Scope Registry: member normalization (one entry per typed id), content fingerprint, revision comparison and
/// multi-scope selection (union of confirmed members, de-duplicated, with attribution and per-scope counts). No persistence, no network.
/// </summary>
public static class ScopeRegistryRules
{
    public const int MaxMembers = 2000;

    /// <summary>Trims, drops empty ids, keeps one entry per typed id (the last one wins, so an edit replaces), stable order.</summary>
    public static List<ScopeMember> Normalize(IEnumerable<ScopeMember> members) =>
        [.. members.Where(m => !string.IsNullOrWhiteSpace(m.TargetId))
            .Select(m => m with
            {
                TargetId = m.TargetId.Trim(), Label = (m.Label ?? "").Trim() is { Length: > 0 } l ? l[..Math.Min(300, l.Length)] : m.TargetId.Trim(),
                Provenance = string.IsNullOrWhiteSpace(m.Provenance) ? "Selected by a person" : m.Provenance.Trim()[..Math.Min(300, m.Provenance.Trim().Length)],
                Rationale = string.IsNullOrWhiteSpace(m.Rationale) ? null : m.Rationale.Trim()[..Math.Min(1000, m.Rationale.Trim().Length)],
            })
            .GroupBy(m => m.Key, StringComparer.Ordinal).Select(g => g.Last())
            .OrderBy(m => m.Kind).ThenBy(m => m.TargetId, StringComparer.Ordinal).Take(MaxMembers)];

    /// <summary>Identity of a revision's membership: kind, id and inclusion only (labels and provenance wording do not change it).</summary>
    public static string Fingerprint(IEnumerable<ScopeMember> members)
    {
        var text = string.Join("\n", Normalize(members).Select(m => $"{m.Kind}|{m.TargetId}|{m.Inclusion}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public static ScopeComparison Compare(Guid scopeId, ScopeRevisionView from, ScopeRevisionView to)
    {
        var before = from.Members.ToDictionary(m => m.Key, StringComparer.Ordinal);
        var after = to.Members.ToDictionary(m => m.Key, StringComparer.Ordinal);
        var differences = new List<ScopeMemberDifference>();
        foreach (var key in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            var b = before.GetValueOrDefault(key);
            var a = after.GetValueOrDefault(key);
            var change = b is null ? ScopeMemberChange.Added : a is null ? ScopeMemberChange.Removed
                : b.Inclusion != a.Inclusion ? ScopeMemberChange.InclusionChanged : ScopeMemberChange.Unchanged;
            differences.Add(new(change, b, a));
        }
        return new(scopeId, from.Number, to.Number, differences);
    }

    /// <summary>
    /// The confirmed members of the chosen scope revisions as one list: each typed id once, attributed to every scope that contributed it.
    /// Candidates and exclusions are never members. Per-scope counts are kept so overlapping scopes can be shown without double counting.
    /// </summary>
    public static ScopeSelection Select(string projectKey, IEnumerable<(ScopeSummary Scope, ScopeRevisionView Revision)> chosen, IEnumerable<string>? notices = null)
    {
        var members = new Dictionary<string, (ScopeMember Member, List<ScopeContribution> By)>(StringComparer.Ordinal);
        var contributions = new List<ScopeContribution>();
        var perScope = new Dictionary<Guid, int>();
        foreach (var (scope, revision) in chosen)
        {
            var contribution = new ScopeContribution(scope.Id, scope.Name, revision.Number);
            contributions.Add(contribution);
            var confirmed = revision.Members.Where(m => m.Inclusion == ScopeMemberInclusion.Confirmed).ToList();
            perScope[scope.Id] = confirmed.Count;
            foreach (var m in confirmed)
            {
                if (!members.TryGetValue(m.Key, out var entry)) members[m.Key] = entry = (m, []);
                entry.By.Add(contribution);
            }
        }
        return new ScopeSelection
        {
            ProjectKey = projectKey, Scopes = contributions, PerScopeCounts = perScope, Notices = [.. notices ?? []],
            Members = [.. members.Values.OrderBy(v => v.Member.Kind).ThenBy(v => v.Member.TargetId, StringComparer.Ordinal).Select(v => new ScopeSelectionMember(v.Member, v.By))],
        };
    }
}
