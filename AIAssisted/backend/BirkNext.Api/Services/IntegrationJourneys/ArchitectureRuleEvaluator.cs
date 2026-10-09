using System.Globalization;
using System.Text;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>
/// Evaluates project-neutral responsibility rules against a Source Analysis architecture snapshot. Subjects are components selected by name
/// tokens; a dependency counts only when the architecture extractor recorded code evidence for it (DI-registered or typed HTTP/GraphQL client,
/// call wiring, project/package reference) whose resolved target component — or the client/configuration reference the source names — matches
/// the target tokens. A file or folder that merely resembles a name never counts. Absence of a component is Not assessed, never Conforms; an
/// outbound HTTP/GraphQL dependency the extractor could not attribute makes the rule Not assessed instead of guessing.
/// </summary>
public static class ArchitectureRuleEvaluator
{
    private static readonly ArchitectureDependencyType[] DependencyTypes =
        [ArchitectureDependencyType.Http, ArchitectureDependencyType.GraphQl, ArchitectureDependencyType.ProjectReference, ArchitectureDependencyType.PackageDependency];

    public static ArchitectureRuleReport Evaluate(string packId, IReadOnlyList<ArchitectureResponsibilityRule> rules, IqrSourceSnapshot? snapshot)
    {
        if (snapshot is null)
            return new(packId, null, null, rules.Select(rule => NotAssessed(rule, "Not source verified: no Source Analysis snapshot is available.", "")).ToArray(),
                ["Upload the relevant source in Source Analysis; architecture rules are evaluated only against analyzed source."]);
        var provenance = $"Source Analysis snapshot {snapshot.Archive.FileName} (SHA-256 {Short(snapshot.Archive.Sha256)}), architecture extractor. {ArchitectureSnapshot.SourceLimitation}";
        if (snapshot.Architecture is not { } architecture)
            return new(packId, snapshot.Id, snapshot.Archive.FileName,
                rules.Select(rule => NotAssessed(rule, "Not source verified: the selected snapshot has no architecture analysis.", provenance)).ToArray(), []);

        var findings = rules.Select(rule => EvaluateRule(rule, architecture, provenance)).ToArray();
        return new(packId, snapshot.Id, snapshot.Archive.FileName, findings,
            ["Rules are evaluated on source structure only. They do not prove which calls happen at runtime.", "Potential deviation means the cited source must be reviewed; it is not a confirmed violation."]);
    }

    private static ArchitectureRuleFinding EvaluateRule(ArchitectureResponsibilityRule rule, ArchitectureSnapshot architecture, string provenance)
    {
        var subjects = architecture.Components.Where(component => component.ComponentType != ArchitectureComponentType.ExternalSystem && MatchesComponent(component, rule.SubjectTokens)).ToArray();
        if (subjects.Length == 0)
            return NotAssessed(rule, $"Not source verified: no {rule.SubjectLabel} component is present in this snapshot.", provenance);

        var targetIds = architecture.Components.Where(component => MatchesComponent(component, rule.TargetTokens)).Select(component => component.Id)
            .Concat(architecture.ExternalSystems.Where(system => MatchesAny(rule.TargetTokens, system.Name, system.ConfigurationReference)).Select(system => system.Id))
            .ToHashSet(StringComparer.Ordinal);
        var subjectIds = subjects.Select(component => component.Id).ToHashSet(StringComparer.Ordinal);
        var outbound = architecture.Dependencies.Where(dependency => subjectIds.Contains(dependency.FromComponentId) && DependencyTypes.Contains(dependency.DependencyType)
            && !string.Equals(dependency.Direction, "Inbound", StringComparison.OrdinalIgnoreCase)).ToArray();
        var matches = outbound.Where(dependency => (dependency.ToId is { } to && targetIds.Contains(to))
            || MatchesAny(rule.TargetTokens, dependency.TargetReference, dependency.ConfigurationReference, dependency.ContractReference)).ToArray();
        var unattributable = outbound.Count(dependency => dependency.DependencyType is ArchitectureDependencyType.Http or ArchitectureDependencyType.GraphQl
            && !dependency.IsResolved && string.IsNullOrWhiteSpace(dependency.TargetReference) && string.IsNullOrWhiteSpace(dependency.ConfigurationReference));
        var evidence = matches.SelectMany(dependency => dependency.Evidence.Select(item =>
            new ArchitectureRuleEvidenceItem(item.File, item.Line, item.Symbol, $"{dependency.DependencyType} dependency ({item.Extractor}): {item.Explanation}"))).Take(20).ToArray();
        var subjectNames = string.Join(", ", subjects.Select(component => component.Name));

        if (rule.Expectation == ArchitectureRuleExpectation.MustNotDependOn)
        {
            if (matches.Length > 0)
                return Finding(rule, ArchitectureRuleOutcome.PotentialDeviation,
                    $"Potential deviation: {subjectNames} has {matches.Length} outbound dependency record(s) on {rule.TargetLabel}. Review the cited source; this is not a confirmed violation.", evidence, provenance);
            if (unattributable > 0)
                return NotAssessed(rule, $"{subjectNames} has {unattributable} outbound HTTP/GraphQL dependency record(s) whose target the extractor could not attribute, so the absence of a {rule.TargetLabel} dependency cannot be asserted.", provenance);
            return Finding(rule, ArchitectureRuleOutcome.Conforms,
                $"Conforms: {outbound.Length} outbound dependency record(s) of {subjectNames} were inspected; none targets {rule.TargetLabel}.", [], provenance);
        }

        if (matches.Length > 0)
            return Finding(rule, ArchitectureRuleOutcome.Conforms, $"Conforms: {subjectNames} has {matches.Length} dependency record(s) on {rule.TargetLabel}.", evidence, provenance);
        if (unattributable > 0)
            return NotAssessed(rule, $"No attributed {rule.TargetLabel} dependency was found, but {unattributable} outbound HTTP/GraphQL dependency record(s) of {subjectNames} could not be attributed.", provenance);
        return Finding(rule, ArchitectureRuleOutcome.PotentialDeviation,
            $"Potential deviation: no dependency of {subjectNames} on {rule.TargetLabel} was found in this snapshot. Review whether the responsibility lives elsewhere.", [], provenance);
    }

    private static ArchitectureRuleFinding NotAssessed(ArchitectureResponsibilityRule rule, string detail, string provenance) =>
        Finding(rule, ArchitectureRuleOutcome.NotAssessed, detail, [], provenance);

    private static ArchitectureRuleFinding Finding(ArchitectureResponsibilityRule rule, ArchitectureRuleOutcome outcome, string detail,
        IReadOnlyList<ArchitectureRuleEvidenceItem> evidence, string provenance) =>
        new(rule.RuleId, rule.Title, rule.SubjectLabel, rule.TargetLabel, rule.Expectation, outcome, detail, evidence, provenance);

    private static bool MatchesComponent(ArchitectureComponent component, IReadOnlyList<string> tokens) =>
        MatchesAny(tokens, component.Name, component.LogicalName, component.SourceProject);

    /// <summary>A rule token matches a name segment (split on separators) or a camel-case word of it, compared case- and diacritic-insensitively.</summary>
    internal static bool MatchesAny(IReadOnlyList<string> tokens, params string?[] values)
    {
        var words = values.Where(value => !string.IsNullOrWhiteSpace(value)).SelectMany(value => Words(value!)).ToHashSet(StringComparer.Ordinal);
        return tokens.Select(Normalize).Any(token => token.Length > 0 && words.Contains(token));
    }

    internal static IEnumerable<string> Words(string value)
    {
        foreach (var segment in value.Split(['.', '-', '_', '/', '\\', ' ', ':', '(', ')', ',', '@'], StringSplitOptions.RemoveEmptyEntries))
        {
            yield return Normalize(segment);
            var current = new StringBuilder();
            for (var i = 0; i < segment.Length; i++)
            {
                var c = segment[i];
                var boundary = i > 0 && char.IsUpper(c) && (char.IsLower(segment[i - 1]) || (i + 1 < segment.Length && char.IsLower(segment[i + 1])));
                if (boundary && current.Length > 0) { yield return Normalize(current.ToString()); current.Clear(); }
                current.Append(c);
            }
            if (current.Length > 0) yield return Normalize(current.ToString());
        }
    }

    internal static string Normalize(string value)
    {
        var folded = value.Trim().ToLowerInvariant().Replace("æ", "ae", StringComparison.Ordinal).Replace("ø", "o", StringComparison.Ordinal).Replace("å", "a", StringComparison.Ordinal);
        var decomposed = folded.Normalize(NormalizationForm.FormD);
        return new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)).ToArray());
    }

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] + "…" : sha;
}
