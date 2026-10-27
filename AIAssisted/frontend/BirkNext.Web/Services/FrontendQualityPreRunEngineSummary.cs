using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Pre-run state grouping shared by the banner, domain copy and engine summary. Never parses reason text.</summary>
public sealed class FrontendQualityPreRunEngineSummary(IReadOnlyList<FrontendQualityCapabilityRow> rows)
{
    public IReadOnlyList<FrontendQualityCapabilityRow> Limitations => rows.Where(r => r.IsActive && !r.IsAvailable).ToList();
    public bool AllRequiredAvailable => rows.Any(r => r.Policy == FrontendQualityEngineRequirement.Required)
        && rows.Where(r => r.Policy == FrontendQualityEngineRequirement.Required)
            .All(r => r.State is FrontendQualityCapabilityState.Enabled or FrontendQualityCapabilityState.Ready);

    private static bool Available(FrontendQualityCapabilityRow r) => r.State is FrontendQualityCapabilityState.Enabled or FrontendQualityCapabilityState.Ready;

    /// <summary>Required capabilities that are switched on but cannot run now. Only these (or an explicit policy) make the pre-run state Limited.</summary>
    public IReadOnlyList<FrontendQualityCapabilityRow> RequiredLimitations => Limitations.Where(r => r.Policy == FrontendQualityEngineRequirement.Required).ToList();

    /// <summary>Optional capabilities that are switched on but cannot run now: neutral, non-blocking.</summary>
    public IReadOnlyList<FrontendQualityCapabilityRow> OptionalLimitations => Limitations.Where(r => r.Policy != FrontendQualityEngineRequirement.Required).ToList();

    /// <summary>"Required: 2/2 available".</summary>
    public string RequiredLine
    {
        get
        {
            var required = rows.Where(r => r.Policy == FrontendQualityEngineRequirement.Required).ToList();
            var disabled = required.Count(r => FrontendQualityCapabilityStates.IsDisabled(r.State));
            var checking = required.Count(r => r.State == FrontendQualityCapabilityState.Checking);
            var unavailable = required.Count(r => r.IsActive && !r.IsAvailable && r.State != FrontendQualityCapabilityState.Checking);
            return $"Required: {required.Count(Available)}/{required.Count} available"
                + (checking > 0 ? $" · {checking} checking" : "")
                + (unavailable > 0 ? $" · {unavailable} unavailable" : "")
                + (disabled > 0 ? $" · {disabled} required disabled" : "");
        }
    }

    /// <summary>"Optional: 4 available · 2 unavailable (non-blocking) · 1 off". Availability, never execution.</summary>
    public string OptionalLine
    {
        get
        {
            var optional = rows.Where(r => r.Policy != FrontendQualityEngineRequirement.Required).ToList();
            var off = optional.Count(r => FrontendQualityCapabilityStates.IsDisabled(r.State));
            var checking = optional.Count(r => r.State == FrontendQualityCapabilityState.Checking);
            var unavailable = optional.Count(r => r.IsActive && !r.IsAvailable && r.State != FrontendQualityCapabilityState.Checking);
            return $"Optional: {optional.Count(Available)} available"
                + (checking > 0 ? $" · {checking} checking" : "")
                + (unavailable > 0 ? $" · {unavailable} unavailable (non-blocking)" : "")
                + (off > 0 ? $" · {off} off" : "");
        }
    }

    public string Counts => RequiredLine + " · " + OptionalLine;

    public string LimitationCounts => string.Join("; ", Limitations.GroupBy(r => r.State)
        .Select(g => $"{g.Count()} {FrontendQualityCapabilityStates.Label(g.Key).ToLowerInvariant()}: {Names(g)}"));

    public string LimitationSentences => string.Join(" ", Limitations.GroupBy(r => r.State)
        .Select(g => $"{Names(g)} {Predicate(g.Key, g.Count() == 1)}."));

    /// <summary>The banner's reading: the same sentences, stated as the current runtime condition.</summary>
    public string BannerSentences => string.Join(" ", Limitations.GroupBy(r => r.State)
        .Select(g => $"{Names(g)} {Predicate(g.Key, g.Count() == 1, current: true)}."));

    private static string Predicate(FrontendQualityCapabilityState state, bool singular, bool current = false) => state switch
    {
        FrontendQualityCapabilityState.Unavailable when current => $"{(singular ? "is" : "are")} currently unavailable",
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
