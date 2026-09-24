using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Pre-run state grouping shared by the banner, domain copy and engine summary. Never parses reason text.</summary>
public sealed class FrontendQualityPreRunEngineSummary(IReadOnlyList<FrontendQualityCapabilityRow> rows)
{
    public IReadOnlyList<FrontendQualityCapabilityRow> Limitations => rows.Where(r => r.IsActive && !r.IsAvailable).ToList();
    public bool AllRequiredAvailable => rows.Any(r => r.Policy == FrontendQualityEngineRequirement.Required)
        && rows.Where(r => r.Policy == FrontendQualityEngineRequirement.Required)
            .All(r => r.State is FrontendQualityCapabilityState.Enabled or FrontendQualityCapabilityState.Ready);

    public string Counts => string.Join(" · ", rows
        .GroupBy(r => (r.Policy, r.State))
        .OrderBy(g => FrontendQualityCapabilityStates.IsDisabled(g.Key.State) ? 1 : 0)
        .ThenBy(g => g.Key.Policy == FrontendQualityEngineRequirement.Required ? 0 : 1)
        .Select(g => $"{g.Count()} {(g.Key.Policy == FrontendQualityEngineRequirement.Required ? "required" : "optional")} {FrontendQualityCapabilityStates.Label(g.Key.State).ToLowerInvariant()}"));

    public string LimitationCounts => string.Join("; ", Limitations.GroupBy(r => r.State)
        .Select(g => $"{g.Count()} {FrontendQualityCapabilityStates.Label(g.Key).ToLowerInvariant()}: {Names(g)}"));

    public string LimitationSentences => string.Join(" ", Limitations.GroupBy(r => r.State)
        .Select(g => $"{Names(g)} {Predicate(g.Key, g.Count() == 1)}."));

    private static string Predicate(FrontendQualityCapabilityState state, bool singular) => state switch
    {
        FrontendQualityCapabilityState.RequiresBrowserSession => $"{(singular ? "requires" : "require")} a browser session",
        FrontendQualityCapabilityState.RequiresAuthenticatedContext => $"{(singular ? "requires" : "require")} an authenticated session",
        _ => $"{(singular ? "is" : "are")} {FrontendQualityCapabilityStates.Label(state).ToLowerInvariant()}",
    };

    public static string Names(IEnumerable<FrontendQualityCapabilityRow> rows)
    {
        var names = rows.Select(r => r.DisplayName).ToList();
        return names.Count < 2 ? string.Join("", names)
            : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }
}
