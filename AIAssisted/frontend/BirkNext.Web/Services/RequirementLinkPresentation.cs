using BirkNext.TestCoverage;
using BirkNext.TestPlanning;

namespace BirkNext.Web.Services;

/// <summary>
/// "Linked requirements" of a saved test case in Test Plan Suggestions: the requirements its own evidence names (automatically identified) and the
/// ones a tester linked by hand (manually confirmed), each checked against the project's current specification. Pure; no I/O. The same validity
/// words as Test Coverage and Requirements Traceability (PreparedCaseTraceability), so all three read alike.
/// </summary>
public static class RequirementLinkPresentation
{
    public const int SearchLimit = 8;
    public const string AutomaticLabel = "Automatically identified";
    public const string ManualLabel = "Manually confirmed";

    /// <param name="Validity">Plain words: in the current specification, no longer in it, or not checked.</param>
    /// <param name="Saved">False for a manual link added in this session and not saved yet.</param>
    public sealed record Row(string RequirementId, string Title, string? SpecificationPath, bool Automatic, bool Manual, string Validity, string Tone, bool Saved,
        string? LinkedBy, DateTimeOffset? LinkedAt)
    {
        public string Origin => Automatic && Manual ? $"{AutomaticLabel} · {ManualLabel}" : Manual ? ManualLabel : AutomaticLabel;
    }

    /// <summary>The project's specification requirements as link options: null when no specification is selected or readable (links cannot be
    /// checked), empty when the selected specification names no requirement (links to it are not found).</summary>
    public static List<ProjectRequirementOption>? Options(CoverageWorkspaceEvidence? evidence)
    {
        if (evidence is not { SpecificationAvailable: true } || evidence.SpecificationArtifacts.FirstOrDefault()?.Path is not { Length: > 0 } path) return null;
        return [.. evidence.Requirements.Where(r => !string.IsNullOrWhiteSpace(r.Id)).DistinctBy(r => r.Id.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(r => new ProjectRequirementOption(r.Id.Trim(), string.IsNullOrWhiteSpace(r.Text) ? r.Id.Trim() : r.Text.Trim(), path))];
    }

    /// <summary>Why the requirement list is empty, in plain words (null when there are requirements).</summary>
    public static string? EmptyReason(CoverageWorkspaceEvidence? evidence, IReadOnlyCollection<ProjectRequirementOption>? options) =>
        options is { Count: > 0 } ? null
        : evidence is null ? "The project's specification could not be read, so requirements cannot be linked right now."
        : !evidence.SpecificationAvailable || evidence.SpecificationArtifacts.Count == 0
            ? "This project has no selected specification, so there are no requirements to link. Choose the specification in the Specification Explorer."
        : "The project's specification names no requirements (no requirement ids such as FR-001 were found), so there is nothing to link.";

    /// <summary>Automatic links only for a case prepared from the specification (a guide's business rules and a journey's declared ids are not
    /// specification requirements); manual links as recorded. One row per requirement, both origins shown when both apply.</summary>
    public static List<Row> Rows(TestCaseDraft c, IReadOnlyCollection<ProjectRequirementOption>? options)
    {
        var known = options is null ? null : options.Select(o => Key(o.RequirementId, o.SpecificationPath)).ToHashSet();
        var ids = options?.Select(o => o.RequirementId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var titles = options?.GroupBy(o => o.RequirementId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
        var rows = new List<Row>();
        if (IsSpecificationCase(c))
            foreach (var r in c.Generated.Requirements.Where(r => !string.IsNullOrWhiteSpace(r.RuleId)).DistinctBy(r => r.RuleId.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                var id = r.RuleId.Trim();
                var (validity, tone) = ids is null ? NotChecked : ids.Contains(id) ? Current : NotFound;
                rows.Add(new(id, titles?.GetValueOrDefault(id) ?? r.Title, null, true, false, validity, tone, true, null, null));
            }
        foreach (var m in c.RequirementLinks.Where(m => !string.IsNullOrWhiteSpace(m.RequirementId)))
        {
            var id = m.RequirementId.Trim();
            var (validity, tone) = known is null ? NotChecked : known.Contains(Key(id, m.SpecificationPath)) ? Current : NotFound;
            var saved = m.LinkedAt != default;
            var existing = rows.FindIndex(x => x.Automatic && string.Equals(x.RequirementId, id, StringComparison.OrdinalIgnoreCase));
            var row = new Row(id, titles?.GetValueOrDefault(id) ?? (string.IsNullOrWhiteSpace(m.Title) ? id : m.Title), m.SpecificationPath, existing >= 0, true, validity, tone, saved,
                string.IsNullOrWhiteSpace(m.LinkedBy) ? null : m.LinkedBy, saved ? m.LinkedAt : null);
            if (existing >= 0) rows[existing] = row; else rows.Add(row);
        }
        return rows;
    }

    /// <summary>Requirements matching the search (id or text, any case), not yet manually linked to the case; at most <see cref="SearchLimit"/>.</summary>
    public static List<ProjectRequirementOption> Search(IReadOnlyCollection<ProjectRequirementOption> options, TestCaseDraft c, string? query)
    {
        var linked = c.RequirementLinks.Select(m => Key(m.RequirementId, m.SpecificationPath)).ToHashSet();
        var q = query?.Trim() ?? "";
        return [.. options.Where(o => !linked.Contains(Key(o.RequirementId, o.SpecificationPath)))
            .Where(o => q.Length == 0 || o.RequirementId.Contains(q, StringComparison.OrdinalIgnoreCase) || o.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Take(SearchLimit)];
    }

    /// <summary>Adds a manual link (who and when are recorded by the server when saved). An automatic link to the same requirement stays as it is.</summary>
    public static TestCaseDraft Link(TestCaseDraft c, ProjectRequirementOption option) =>
        c.RequirementLinks.Any(m => Key(m.RequirementId, m.SpecificationPath) == Key(option.RequirementId, option.SpecificationPath)) ? c
            : c with { RequirementLinks = [.. c.RequirementLinks, new ManualRequirementLink { RequirementId = option.RequirementId, SpecificationPath = option.SpecificationPath, Title = option.Title }] };

    /// <summary>Removes the manual link only; the case's own evidence (an automatic link) is never removed.</summary>
    public static TestCaseDraft Unlink(TestCaseDraft c, string requirementId, string? specificationPath) =>
        c with { RequirementLinks = [.. c.RequirementLinks.Where(m => Key(m.RequirementId, m.SpecificationPath) != Key(requirementId, specificationPath))] };

    public static bool IsSpecificationCase(TestCaseDraft c) =>
        c.Source.Kind is not (ScenarioSourceKind.SystemGuide or ScenarioSourceKind.Manual) && string.Equals(c.Generated.Capability, "specification", StringComparison.Ordinal);

    public static string Key(string id, string? path) => $"{id.Trim().ToUpperInvariant()}|{(path ?? "").Replace('\\', '/').Trim().TrimStart('/').ToUpperInvariant()}";

    private static readonly (string, string) Current = ("In the current specification", "ok");
    private static readonly (string, string) NotFound = ("Not found in the current specification — removed or renamed; not counted", "warn");
    private static readonly (string, string) NotChecked = ("Not checked — the project's specification is not loaded", "muted");

    /// <summary>Plain words for a traced link's validity (Test Coverage / Requirements Traceability).</summary>
    public static string ValidityLabel(PreparedLinkValidity? validity) => validity switch
    {
        PreparedLinkValidity.Current => "In the reviewed specification",
        PreparedLinkValidity.RequirementNotFound => "No longer in the reviewed specification",
        PreparedLinkValidity.OtherSpecification => "Another specification file",
        PreparedLinkValidity.NotChecked => "Not checked",
        PreparedLinkValidity.NotARequirement => "Not a specification requirement",
        _ => "",
    };

    public static string OriginLabel(PreparedCaseLinkBasis basis) => basis switch
    {
        PreparedCaseLinkBasis.ManualConfirmation => ManualLabel,
        PreparedCaseLinkBasis.SpecificationEvidence => AutomaticLabel,
        PreparedCaseLinkBasis.JourneyReference => "Declared by an integration journey",
        _ => "Guide business rule",
    };
}
