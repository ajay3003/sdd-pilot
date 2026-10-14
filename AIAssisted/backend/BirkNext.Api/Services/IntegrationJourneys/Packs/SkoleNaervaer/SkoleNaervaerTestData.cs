using System.Text.RegularExpressions;
using BirkNext.Api.Services.AltinnTesting;

namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

/// <summary>
/// Resolves a report run's test identities from approved test data only — the approved synthetic TEST reporters and approved synthetic
/// children in the local execution configuration. A value is resolved automatically only when the choice is deterministic (exactly one usable
/// entry, or exactly one marked default); otherwise the tester chooses. Nothing is generated, invented or looked up: resolving is not
/// creating, and a declared known child is not a verified one. Identifiers stay here; callers get aliases, except the run's own form builder.
/// </summary>
public sealed partial class SkoleNaervaerTestData(AltinnTestingOptions options)
{
    /// <summary>The provenance shown for values resolved here.</summary>
    public const string Source = "Approved test data";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();

    /// <summary>Why an approved child cannot be used, or null. Never contains an identifier.</summary>
    public string? ChildProblem(SyntheticChildOptions child)
    {
        if (!AliasPattern().IsMatch(child.Alias)) return "The synthetic child alias must be lowercase letters, digits and hyphens.";
        if (options.SyntheticChildren.Count(item => string.Equals(item.Alias, child.Alias, StringComparison.Ordinal)) > 1) return $"Synthetic child {child.Alias}: the alias is configured more than once.";
        var identifier = child.TestPersonIdentifier?.Trim();
        if (!SyntheticPersonIdentifier.IsSynthetic(identifier))
            return $"Synthetic child {child.Alias}: the identifier is missing or not a synthetic Test-Norge identifier (month + 80).";
        if (options.UnattendedIdPortenTest.Reporters.FirstOrDefault(reporter => string.Equals(reporter.TestIdPersonIdentifier?.Trim(), identifier, StringComparison.Ordinal)) is { } same)
            return $"Synthetic child {child.Alias} is the same person as reporter {same.Alias}; the reporter and the child must be different persons.";
        return null;
    }

    /// <summary>The usable approved children for a scenario that needs a known (registered) or an unknown child.</summary>
    public IReadOnlyList<SyntheticChildOptions> Children(bool knownToPerson) =>
        options.SyntheticChildren.Where(child => child.KnownToPerson == knownToPerson && ChildProblem(child) is null).ToList();

    /// <summary>The child to preselect, or null when none is usable or several are and no single default decides.</summary>
    public SyntheticChildOptions? DefaultChild(bool knownToPerson)
    {
        var children = Children(knownToPerson);
        if (children.Count == 1) return children[0];
        var defaults = children.Where(child => child.Default).ToList();
        return defaults.Count == 1 ? defaults[0] : null;
    }

    /// <summary>The identifier behind an approved alias for the run's form, or null. Only the form builder uses it; it is never returned to a client.</summary>
    public string? ChildIdentifier(string? alias, bool knownToPerson) =>
        Children(knownToPerson).FirstOrDefault(child => string.Equals(child.Alias, alias, StringComparison.Ordinal))?.TestPersonIdentifier?.Trim();

    /// <summary>The first configuration problem among the approved children, or null.</summary>
    public string? FirstChildProblem => options.SyntheticChildren.Select(ChildProblem).FirstOrDefault(problem => problem is not null);

    /// <summary>The usable (configured and profile-confirmed) approved reporters.</summary>
    public IReadOnlyList<SyntheticReporterOptions> Reporters =>
        options.UnattendedIdPortenTest.Reporters.Where(reporter => AltinnTestingPolicy.ReporterProblem(reporter) is null).ToList();

    /// <summary>The reporter alias to preselect: the only usable approved reporter, else null (the tester chooses).</summary>
    public string? DefaultReporter => Reporters.Count == 1 ? Reporters[0].Alias : null;
}
