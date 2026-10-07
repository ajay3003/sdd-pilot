using System.Text.RegularExpressions;
using BirkNext.Web.Models;
using BirkNext.Web.Services.Explorers;

namespace BirkNext.Web.Services;

public enum PlanSpecificationReferenceStatus
{
    NamedOnly,
    Resolved,
    Ambiguous,
    Missing,
}

public enum PlanSpecificationReferenceMatch
{
    ExactSourcePath,
    UniqueFileName,
}

public sealed record PlanSpecificationReferenceResolution(
    PlanSpecificationReferenceStatus Status,
    ExplorerArtifact? Artifact = null,
    PlanSpecificationReferenceMatch? Match = null,
    string? Detail = null);

/// <summary>
/// Resolves the Plan's named Specification only to the currently selected role artifact. This establishes artifact identity,
/// not a traceability relationship, review decision, or coverage result.
/// </summary>
public static class PlanSpecificationReferenceResolver
{
    private static readonly Regex InputPathPattern = new(@"[\w.\-]+(?:[/\\][\w.\-]+)+", RegexOptions.Compiled);

    public static PlanSpecificationReferenceResolution Resolve(PlanDocument plan, ArtifactExplorerState? specificationState)
    {
        var specName = plan.SpecLink?.Trim();
        var input = plan.InputSource?.Trim();
        if (string.IsNullOrWhiteSpace(specName) && string.IsNullOrWhiteSpace(input))
            return new(PlanSpecificationReferenceStatus.NamedOnly, Detail: "The plan does not identify a source Specification.");

        if (specificationState is null)
            return new(PlanSpecificationReferenceStatus.NamedOnly, Detail: "No current Specification artifact state is available.");

        var references = new List<string>();
        if (!string.IsNullOrWhiteSpace(specName)) references.Add(CleanReference(specName));
        if (!string.IsNullOrWhiteSpace(input))
        {
            var pathMatch = InputPathPattern.Match(input);
            if (pathMatch.Success) references.Add(CleanReference(pathMatch.Value));
        }

        var pathReferences = references.Where(HasDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var referencePath in pathReferences)
        {
            var matches = specificationState.Artifacts
                .Where(a => !string.IsNullOrWhiteSpace(a.SourcePath) && PathsEqual(a.SourcePath!, referencePath))
                .ToList();
            if (matches.Count > 1)
                return new(PlanSpecificationReferenceStatus.Ambiguous, Detail: "More than one Specification artifact has the declared source path.");
            if (matches.Count == 1)
                return ResolveCurrent(specificationState, matches[0], PlanSpecificationReferenceMatch.ExactSourcePath);
        }

        // Filename-only identity is safe only when unique across all available Specification role artifacts.
        var fileNames = references
            .Where(r => !HasDirectory(r))
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var fileName in fileNames)
        {
            var matches = specificationState.Artifacts
                .Where(a => string.Equals(a.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > 1)
                return new(PlanSpecificationReferenceStatus.Ambiguous, Detail: "Multiple Specification artifacts have this filename; no artifact was chosen automatically.");
            if (matches.Count == 1)
                return ResolveCurrent(specificationState, matches[0], PlanSpecificationReferenceMatch.UniqueFileName);
        }

        if (specificationState.Status == ExplorerArtifactStatus.SelectionRequired)
            return new(PlanSpecificationReferenceStatus.Ambiguous, Detail: "Several Specification artifacts are available and none is currently selected.");
        if (specificationState.Status == ExplorerArtifactStatus.Empty)
            return new(PlanSpecificationReferenceStatus.Missing, Detail: "No matching Specification artifact is available in the current workspace.");

        return new(PlanSpecificationReferenceStatus.Missing, Detail: "No current Specification artifact matches the source named in the plan.");
    }

    private static PlanSpecificationReferenceResolution ResolveCurrent(
        ArtifactExplorerState state,
        ExplorerArtifact match,
        PlanSpecificationReferenceMatch matchKind)
    {
        if (state.Status == ExplorerArtifactStatus.Loaded && state.Selected?.Id == match.Id)
            return new(PlanSpecificationReferenceStatus.Resolved, match, matchKind);

        return new(PlanSpecificationReferenceStatus.NamedOnly, match, matchKind,
            "A matching artifact exists, but it is not the current Specification selection.");
    }

    private static string CleanReference(string value)
    {
        var cleaned = value.Trim().Trim('`', '\'', '"');
        if (cleaned.StartsWith("./", StringComparison.Ordinal)) cleaned = cleaned[2..];
        return cleaned.Replace('\\', '/').TrimStart('/');
    }

    private static bool HasDirectory(string value) => value.Contains('/');

    private static bool PathsEqual(string left, string right) =>
        string.Equals(CleanReference(left), CleanReference(right), StringComparison.OrdinalIgnoreCase);
}
