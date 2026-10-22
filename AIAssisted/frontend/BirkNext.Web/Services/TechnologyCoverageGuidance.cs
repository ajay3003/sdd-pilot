using BirkNext.Applicability;
using BirkNext.Technology;

namespace BirkNext.Web.Services;

/// <summary>A next step that opens an existing page. Never a fabricated route: every href here is a page BirkNext has.</summary>
public sealed record CoverageAction(string Label, string Href);

/// <summary>One line of "What needs attention", counted from the evaluated reviews and the snapshot inventory.</summary>
public sealed record CoverageAttention(string Key, int Count, string Text, bool Neutral);

/// <summary>A detected language whose source BirkNext does not analyze, with the bounded file-name evidence the inventory kept.</summary>
public sealed record UnanalyzedTechnology(DetectedTechnology Technology, IReadOnlyList<string> AvailableCapabilities);

/// <summary>The three separate dimensions of one review for this project: does it apply, can it run now, how much can it cover.</summary>
public sealed record ReviewDimensions(string Applicability, string Readiness, string Coverage);

/// <summary>"What needs attention" split by meaning: setup the user can do, coverage limits of BirkNext, and reviews that simply do not apply.</summary>
public sealed record CoverageAttentionGroups(IReadOnlyList<CoverageAttention> SetupRequired, IReadOnlyList<CoverageAttention> CoverageLimitations, IReadOnlyList<CoverageAttention> NotApplicable);

/// <summary>
/// Presentation-only guidance for Technology &amp; Analysis Coverage: wording, next actions and attention counts derived from the existing
/// applicability results and support registry. It never changes detection, applicability or support semantics; a BirkNext capability gap is
/// worded as a tool limitation ("Detection only", "Not available in BirkNext"), never as a project problem.
/// </summary>
public static class TechnologyCoverageGuidance
{
    public const string TargetEnvironments = "admin/system-settings?section=target-environments";

    /// <summary>Overall support of a detected technology, worded for the project view. Uses the registry's deterministic <c>Overall</c>.</summary>
    public static string ProjectSupport(SupportLevel overall) => overall switch
    {
        SupportLevel.Full => "Supported",
        SupportLevel.Partial => "Partial",
        SupportLevel.NotApplicable => "Not applicable",
        _ => "Detection only",
    };

    /// <summary>One capability dimension, worded as what BirkNext offers (a missing capability is BirkNext's, not the project's).</summary>
    public static string CapabilityLabel(SupportLevel level) => level switch
    {
        SupportLevel.Full => "Supported",
        SupportLevel.Partial => "Partial",
        SupportLevel.Planned => "Planned (not available yet)",
        SupportLevel.Unsupported => "Not available in BirkNext",
        _ => "Not applicable",
    };

    /// <summary>Filter key of a support level: Supported, Partial or None (detection only / not available).</summary>
    public static string SupportKey(SupportLevel level) => level switch
    {
        SupportLevel.Full => "Supported",
        SupportLevel.Partial => "Partial",
        SupportLevel.NotApplicable => "NotApplicable",
        _ => "None",
    };

    /// <summary>The first sentence of the registry's limitation text: the one thing a tester needs on the row; the rest is in details.</summary>
    public static string MainLimitation(TechnologySupportDescriptor? descriptor)
    {
        var text = descriptor?.Limitations?.Trim() ?? "";
        if (text.Length == 0) return "—";
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        return end > 0 ? text[..(end + 1)] : text;
    }

    /// <summary>
    /// Applicability, readiness and coverage of one review, from its single evaluated status (no new evaluation). Applicable says the review
    /// fits the project; readiness says whether it can run now; coverage says how much of the project it can assess.
    /// </summary>
    public static ReviewDimensions Dimensions(ApplicabilityStatus status) => status switch
    {
        ApplicabilityStatus.Applicable => new("Applicable", "Ready", "Full"),
        ApplicabilityStatus.PartiallyApplicable => new("Applicable", "Ready", "Partial"),
        ApplicabilityStatus.NeedsConfiguration => new("Applicable", "Needs configuration", "Not assessed"),
        ApplicabilityStatus.NeedsRefresh => new("Applicable", "Needs refresh", "Not assessed"),
        ApplicabilityStatus.NotEnoughEvidence => new("Not determined", "Needs evidence", "Not assessed"),
        ApplicabilityStatus.Unsupported => new("Applicable", "Not supported by BirkNext", "Not assessed"),
        _ => new("Not applicable", "—", "—"),
    };

    /// <summary>Ready / needs setup / partial / not applicable counts for the compact readiness summary.</summary>
    public static (int Ready, int NeedSetup, int Partial, int NotApplicable) ReadinessCounts(IEnumerable<ReviewApplicability?> reviews)
    {
        var statuses = reviews.Where(r => r is not null).Select(r => r!.Status).ToList();
        return (statuses.Count(s => s is ApplicabilityStatus.Applicable or ApplicabilityStatus.PartiallyApplicable),
            statuses.Count(s => s is ApplicabilityStatus.NeedsConfiguration or ApplicabilityStatus.NeedsRefresh or ApplicabilityStatus.NotEnoughEvidence),
            statuses.Count(s => s is ApplicabilityStatus.PartiallyApplicable),
            statuses.Count(s => s is ApplicabilityStatus.NotApplicable));
    }

    /// <summary>"What needs attention" grouped by meaning; Not applicable is never a setup item or a limitation.</summary>
    public static CoverageAttentionGroups AttentionGroups(IEnumerable<ReviewApplicability?> reviews, SourceTechnologyCoverage? source)
    {
        var all = Attention(reviews, source);
        return new([.. all.Where(a => a.Key is "needs-configuration" or "needs-refresh" or "not-enough-evidence")],
            [.. all.Where(a => a.Key is "partial" or "unanalyzed" or "unsupported")],
            [.. all.Where(a => a.Key == "not-applicable")]);
    }

    /// <summary>Source-scoped detection wording: "Detected in source" for direct evidence, "Inferred from source" when derived from structure.</summary>
    public static string Evidence(DetectionConfidence confidence) => confidence switch
    {
        DetectionConfidence.Confirmed => "Detected in source",
        DetectionConfidence.StronglySupported => "Detected in source (several signals)",
        DetectionConfidence.Inferred => "Inferred from source",
        _ => "Unresolved",
    };

    /// <summary>The capability dimensions BirkNext supports for a technology ("Source analysis · Runtime observation (partial)").</summary>
    public static string WhatWorks(TechnologySupportDescriptor? descriptor)
    {
        if (descriptor is null) return "—";
        var parts = descriptor.Dimensions.Where(d => TechnologySupportRegistry.IsSupported(d.Level))
            .Select(d => TechnologySupportRegistry.Label(d.Dimension) + (d.Level == SupportLevel.Partial ? " (partial)" : "")).ToList();
        return parts.Count == 0 ? "Detection only" : string.Join(" · ", parts);
    }

    /// <summary>Archive files that are neither analyzed nor counted as unanalyzed source: documents, images, binaries and other formats Source
    /// Analysis does not read. The model has no finer breakdown, so none is invented.</summary>
    public static int OutsideSourceScope(SourceTechnologyCoverage source) => Math.Max(0, source.TotalFiles - source.AnalyzedFiles - source.UnsupportedSourceFiles);

    /// <summary>The wording when a technology has no next page: specific, never a dead-end "No BirkNext action".</summary>
    public static string NoActionText(DetectedTechnology technology, TechnologySupportDescriptor? descriptor) =>
        descriptor is null ? "Not in the BirkNext support registry"
        : technology.Area == TechnologyArea.Language && !TechnologySupportRegistry.IsSupported(descriptor.SourceAnalysis) ? "No Source Analysis action available"
        : "No setup required";

    public static CoverageAction? ReviewAction(ReviewDescriptor review, ReviewApplicability? applicability)
    {
        if (applicability is null) return null;
        return applicability.Status switch
        {
            ApplicabilityStatus.NeedsConfiguration => review.ReviewId switch
            {
                "frontend-quality-review" or "critical-e2e-regression" => new("Configure target application", TargetEnvironments + "&tab=endpoint"),
                "api-quality-review" or "performance-test-review" => new("Configure API target", TargetEnvironments + "&tab=endpoint"),
                "integration-quality-review" => new("Configure integrations", TargetEnvironments + "&tab=integrations"),
                _ => new("Open Target Environments", TargetEnvironments),
            },
            ApplicabilityStatus.NeedsRefresh => new("Re-analyze in Source Analysis", "source-analysis"),
            ApplicabilityStatus.NotEnoughEvidence => review.ReviewId switch
            {
                "quality-review" => new("Import a project with documents", "project-import"),
                "source-analysis" or "pipeline-review" or "dependency-review" => new("Analyze source in Source Analysis", "source-analysis"),
                "integration-quality-review" => new("Configure integrations", TargetEnvironments + "&tab=integrations"),
                _ => null,
            },
            ApplicabilityStatus.Applicable or ApplicabilityStatus.PartiallyApplicable => new($"Open {review.DisplayName}", review.Route),
            // Not applicable / unsupported: nothing to do for the project. Domain extensions are never offered as a one-click action here.
            _ => null,
        };
    }

    /// <summary>Counts per review state plus unanalyzed source files. Neutral lines (not applicable, tool limitation) are informative only.</summary>
    public static IReadOnlyList<CoverageAttention> Attention(IEnumerable<ReviewApplicability?> reviews, SourceTechnologyCoverage? source)
    {
        var statuses = reviews.Where(r => r is not null).Select(r => r!.Status).ToList();
        var items = new List<CoverageAttention>();
        void Add(string key, ApplicabilityStatus status, string singular, string plural, bool neutral = false)
        {
            var count = statuses.Count(s => s == status);
            if (count > 0) items.Add(new(key, count, $"{count} {(count == 1 ? singular : plural)}", neutral));
        }
        Add("needs-configuration", ApplicabilityStatus.NeedsConfiguration, "review needs configuration", "reviews need configuration");
        Add("needs-refresh", ApplicabilityStatus.NeedsRefresh, "review needs a fresh source analysis", "reviews need a fresh source analysis");
        Add("partial", ApplicabilityStatus.PartiallyApplicable, "review has partial coverage", "reviews have partial coverage");
        Add("not-enough-evidence", ApplicabilityStatus.NotEnoughEvidence, "review does not have enough evidence yet", "reviews do not have enough evidence yet");
        if (source is { UnsupportedSourceFiles: > 0 } s)
            items.Add(new("unanalyzed", s.UnsupportedSourceFiles, $"{s.UnsupportedSourceFiles} source file{(s.UnsupportedSourceFiles == 1 ? " was" : "s were")} not analyzed (BirkNext limitation)", true));
        Add("unsupported", ApplicabilityStatus.Unsupported, "review is limited by BirkNext support (tool limitation)", "reviews are limited by BirkNext support (tool limitation)", neutral: true);
        Add("not-applicable", ApplicabilityStatus.NotApplicable, "review does not apply to this project", "reviews do not apply to this project", neutral: true);
        return items;
    }

    /// <summary>Source Analysis split: detected languages BirkNext analyzes vs detects only.</summary>
    public static (IReadOnlyList<string> Analyzed, IReadOnlyList<string> DetectionOnly) SourceAnalysisSplit(SourceTechnologyCoverage? source)
    {
        var languages = source?.Technologies.Where(t => t.Area == TechnologyArea.Language).ToList() ?? [];
        bool Analyzed(DetectedTechnology t) => TechnologySupportRegistry.Find(t.TechnologyId) is { } d && TechnologySupportRegistry.IsSupported(d.SourceAnalysis);
        return ([.. languages.Where(Analyzed).Select(t => t.DisplayName)], [.. languages.Where(t => !Analyzed(t)).Select(t => t.DisplayName)]);
    }

    /// <summary>Languages counted in "Source files not analyzed" (same rule as the inventory: detected, source analysis not supported).</summary>
    public static IReadOnlyList<UnanalyzedTechnology> Unanalyzed(SourceTechnologyCoverage? source) =>
        [.. (source?.Technologies ?? [])
            .Where(t => t.Area == TechnologyArea.Language && TechnologySupportRegistry.Find(t.TechnologyId) is { } d && !TechnologySupportRegistry.IsSupported(d.SourceAnalysis))
            .Select(t => new UnanalyzedTechnology(t, [.. TechnologySupportRegistry.Find(t.TechnologyId)!.Dimensions
                .Where(x => TechnologySupportRegistry.IsSupported(x.Level)).Select(x => TechnologySupportRegistry.Label(x.Dimension))]))];

    /// <summary>A useful next page for a detected technology, or null when BirkNext has nothing further for it (tool limitation). Contract
    /// evidence opens where it is reviewed: XSD in Schema Studio, other contracts in Source Analysis → Contracts (source evidence, not a runtime
    /// API review); a runtime-only frontend opens its target setup.</summary>
    public static CoverageAction? TechnologyAction(DetectedTechnology technology, TechnologySupportDescriptor? descriptor, Guid? snapshotId = null)
    {
        if (descriptor is null || descriptor.Overall is SupportLevel.Unsupported or SupportLevel.NotApplicable) return null;
        if (technology.TechnologyId == "iac.terraform") return new("Open Infrastructure as Code", "source-analysis?area=quality-evidence&view=infrastructure");
        if (technology.TechnologyId == "contract.xsd") return new("Open Schema Studio", "schema-studio");
        if (technology.Area == TechnologyArea.Contract)
            return new("Open contracts in Source Analysis", "source-analysis?area=quality-evidence&view=contracts" + (snapshotId is { } id ? $"&snapshot={id:D}" : ""));
        if (technology.Area == TechnologyArea.Framework && !TechnologySupportRegistry.IsSupported(descriptor.SourceAnalysis) && TechnologySupportRegistry.IsSupported(descriptor.RuntimeObservation))
            return new("Configure target application", TargetEnvironments + "&tab=endpoint");
        if (technology.Area == TechnologyArea.Integration && TechnologySupportRegistry.IsSupported(descriptor.Configuration))
            return new("Configure integrations", TargetEnvironments + "&tab=integrations");
        if (technology.Area == TechnologyArea.Pipeline) return new("Open Pipeline Review", "pipeline-review");
        if (technology.Area == TechnologyArea.Dependency) return new("Open Dependency Review", "dependency-review");
        if (TechnologySupportRegistry.IsSupported(descriptor.SourceAnalysis)) return new("Open Source Analysis", "source-analysis");
        return null;
    }

    /// <summary>Support reference filter. Without a capability the state applies to the overall level; with one, to that dimension.</summary>
    public static bool MatchesReference(TechnologySupportDescriptor t, string? search, TechnologyArea? area, SupportDimension? capability, string? state)
    {
        if (area is { } a && t.Area != a) return false;
        if (!string.IsNullOrWhiteSpace(search)
            && !t.DisplayName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
            && !t.TechnologyId.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
            && !t.ProviderIds.Any(p => p.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))) return false;
        var level = capability is { } c ? t.Level(c) : t.Overall;
        if (capability is not null && string.IsNullOrEmpty(state) && level == SupportLevel.NotApplicable) return false;
        return string.IsNullOrEmpty(state) || SupportKey(level) == state;
    }
}
