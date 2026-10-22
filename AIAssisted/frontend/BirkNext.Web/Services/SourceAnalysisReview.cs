using BirkNext.DatabaseArchitecture;
using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.SourceObservability;

namespace BirkNext.Web.Services;

/// <summary>
/// Source Analysis information architecture: four top-level areas (Overview, Architecture, Data, Quality Evidence) and the five quality-evidence
/// domains under Quality Evidence. Legacy area names (Database, Observability, Infrastructure, Configuration, CI/CD, Contracts) map onto them, so
/// every old deep link keeps working.
/// </summary>
public static class SourceAnalysisAreas
{
    public const string Overview = "Overview", Architecture = "Architecture", Data = "Data", Quality = "Quality Evidence";
    public static readonly IReadOnlyList<string> Top = [Overview, Architecture, Data, Quality];
    public static readonly IReadOnlyList<string> QualityDomains = ["Observability", "Infrastructure", "Configuration", "CI/CD", "Contracts", "Dependencies"];

    /// <summary>Area (and quality domain) of an <c>?area=</c> value, new or legacy; unknown values open the Overview.</summary>
    public static (string Area, string? Domain) Resolve(string? area, string? view)
    {
        string? Domain(string? value) => QualityDomains.FirstOrDefault(d => string.Equals(d, value, StringComparison.OrdinalIgnoreCase) || string.Equals(Slug(d), value, StringComparison.OrdinalIgnoreCase));
        return area?.Trim() switch
        {
            null or "" => (Overview, null),
            var a when a.Equals(Overview, StringComparison.OrdinalIgnoreCase) => (Overview, null),
            var a when a.Equals(Architecture, StringComparison.OrdinalIgnoreCase) => (Architecture, null),
            var a when a.Equals(Data, StringComparison.OrdinalIgnoreCase) || a.Equals("Database", StringComparison.OrdinalIgnoreCase) => (Data, null),
            var a when a.Equals(Quality, StringComparison.OrdinalIgnoreCase) || a.Equals("quality", StringComparison.OrdinalIgnoreCase)
                || a.Equals("QualityEvidence", StringComparison.OrdinalIgnoreCase) || a.Equals("quality-evidence", StringComparison.OrdinalIgnoreCase) => (Quality, Domain(view)),
            var a when Domain(a) is { } legacy => (Quality, legacy),
            _ => (Overview, null),
        };
    }

    /// <summary>URL value of an area (<c>?area=</c>).</summary>
    public static string AreaParam(string area) => area == Quality ? "quality-evidence" : area;

    /// <summary>URL value of a quality domain (<c>?view=</c>).</summary>
    public static string Slug(string domain) => domain switch { "CI/CD" => "cicd", _ => domain.ToLowerInvariant() };
}

/// <summary>Readable unresolved-dependency wording: what was found and why the target is unresolved. A target is never guessed.</summary>
public static class ArchitectureReview
{
    /// <summary>"HttpClient 'PersonApi'" → HttpClient "PersonApi"; a configuration key stays as written.</summary>
    public static string TargetLabel(ArchitectureDependency d)
    {
        var reference = d.TargetReference ?? d.ConfigurationReference ?? d.DependencyType.ToString();
        var named = System.Text.RegularExpressions.Regex.Match(reference, "^HttpClient '([^']+)'$");
        return named.Success ? $"HttpClient \"{named.Groups[1].Value}\"" : reference;
    }

    /// <summary>Why a target is unresolved, from the dependency's own state: conflicting candidates, or no matching target host/base address in source.</summary>
    public static string UnresolvedReason(ArchitectureDependency d) =>
        d.ConflictingTargets.Count > 0 ? $"{d.ConflictingTargets.Count} candidate targets — none chosen"
        : d.ConfigurationReference is { Length: > 0 } key ? $"No component is wired to {key} in this source"
        : "Matching target host/base address not identified in this source";

    /// <summary>
    /// Reason category of an unresolved dependency, from the dependency's own fields only (never from text): conflicting candidates; evidence that
    /// exists only in local orchestration or compose files; a messaging entity whose name is not resolved; an HTTP/GraphQL base address not
    /// identified; otherwise the target component. Null when the target was resolved.
    /// </summary>
    public static string? ReasonCategory(ArchitectureDependency d)
    {
        // An Unresolved-state dependency can still point to an unresolved node (a channel whose name is not resolved): it keeps a reason.
        if (d.ToId is not null && d.ConflictingTargets.Count == 0 && d.EvidenceState != ArchitectureEvidenceState.Unresolved) return null;
        if (d.ConflictingTargets.Count > 0) return ReasonConflict;
        if (d.Evidence.Count > 0 && d.Evidence.All(e => e.Kind == ArchitectureEvidenceKind.DevelopmentOrchestration)) return ReasonLocalOrchestration;
        if (d.Evidence.Count > 0 && d.Evidence.All(e => e.Kind == ArchitectureEvidenceKind.ContainerCompose)) return ReasonCompose;
        return d.DependencyType switch
        {
            ArchitectureDependencyType.EventHubProduce or ArchitectureDependencyType.EventHubConsume or ArchitectureDependencyType.ServiceBusPublish
                or ArchitectureDependencyType.ServiceBusConsume => ReasonChannel,
            ArchitectureDependencyType.Http or ArchitectureDependencyType.GraphQl => ReasonBaseUrl,
            ArchitectureDependencyType.PackageDependency or ArchitectureDependencyType.ProjectReference => ReasonExternal,
            _ => ReasonTarget,
        };
    }

    public const string ReasonTarget = "Target component unresolved", ReasonChannel = "Channel/entity name unresolved", ReasonBaseUrl = "Base URL unresolved",
        ReasonConflict = "Conflict", ReasonLocalOrchestration = "Local orchestration only", ReasonCompose = "Compose evidence only", ReasonExternal = "External dependency";

    /// <summary>Plain state wording for one dependency: a dependency whose target is unknown was still detected.</summary>
    public static string StateText(ArchitectureDependency d) =>
        d.EvidenceState == ArchitectureEvidenceState.Unresolved || d.ToId is null ? "Relationship detected · target unresolved" : ArchitecturePresentation.StateLabel(d.EvidenceState);

    /// <summary>The extractor's unresolved-evidence kinds grouped by cause, so missing source relationships, ambiguous configuration and runtime
    /// scope are not mixed. Unknown kinds fall under "Other analyzer evidence".</summary>
    public static string CauseGroup(string kind) => kind switch
    {
        "Unresolved target" or "Producer not found" or "Consumer not found" => "Missing relationship evidence",
        "Channel name unresolved" or "Conflict" => "Ambiguous configuration",
        "Deployment not assessed" or "Local orchestration only" => "Outside Source Analysis runtime scope",
        _ => "Other analyzer evidence",
    };

    public static readonly IReadOnlyList<string> CauseGroups = ["Missing relationship evidence", "Ambiguous configuration", "Outside Source Analysis runtime scope", "Other analyzer evidence"];

    /// <summary>The primary reason for a non-complete architecture status (actual data).</summary>
    public static string? StatusReason(ArchitectureSnapshot a)
    {
        if (a.Status is ArchitectureStatus.Complete) return null;
        if (a.Status is ArchitectureStatus.Unsupported) return "Not assessed: no supported project type was found in this source.";
        var dep = ArchitectureEvidenceSummary.Dependencies(a);
        return dep.Unresolved > 0 ? SourceAnalysisReview.Plural(dep.Unresolved, "dependency target unresolved", "dependency targets unresolved")
            : a.Diagnostics.Count > 0 ? SourceAnalysisReview.Plural(a.Diagnostics.Count, "evidence item needs review", "evidence items need review")
            : "Some source constructs could not be interpreted";
    }
}

/// <summary>One summary row: a domain's status and the one reason that explains a non-complete status, plus where to review it.</summary>
public sealed record SourceSummaryRow(string Key, string Title, string Status, string? Reason, string Area, string? Domain, string? Detail = null);

/// <summary>One item a person may want to review in one area: unresolved source relationships, declarations that need interpretation, evidence
/// needing review, classification gaps or heuristic parses needing confirmation. Counts of different areas are never added together: they
/// are analyzer evidence of different kinds, not application defects.</summary>
public sealed record SourceAttentionItem(string Key, int Count, string Text, string Area, string? Domain, string? View = null)
{
    /// <summary>The area the item belongs to (a quality-evidence domain, or the top-level area).</summary>
    public string AreaTitle => Domain ?? Area;
}

/// <summary>Why the selected snapshot's analysis coverage is Partial, from the snapshot's own counts and limitations (never a failure).</summary>
public sealed record SnapshotCoverage(string Status, IReadOnlyList<string> Reasons, IReadOnlyList<string> Limitations);

/// <summary>Grouped change counts of one area (Components, Dependencies, …) for summary-first change views.</summary>
public sealed record SourceChangeGroup(string Label, int Added, int Removed, int Changed);

/// <summary>
/// Decision-first summaries for Source Analysis, computed only from the selected stored snapshot. Every status that is not Complete carries its
/// primary reason from the actual data; findings (source evidence that suggests something needs review) are kept apart from analyzer limitations
/// (what an analyzer could not interpret) — a limitation is never presented as an application defect.
/// </summary>
public static class SourceAnalysisReview
{
    public const string NotDetectedText = "Not detected in this source";

    public static SourceSummaryRow Architecture(IqrSourceSnapshot s)
    {
        if (s.Architecture is not { } a) return new("architecture", "Architecture", "Not assessed", "This snapshot predates architecture extraction.", SourceAnalysisAreas.Architecture, null);
        var status = SourceAnalysisOverview.StatusLabel(a.Status);
        if (a.Status == ArchitectureStatus.Unsupported) return new("architecture", "Architecture", "Not assessed", "No supported project type was found in this source.", SourceAnalysisAreas.Architecture, null);
        var reason = ArchitectureReview.StatusReason(a);
        return new("architecture", "Architecture", status, reason, SourceAnalysisAreas.Architecture, null,
            $"{Plural(a.Components.Count, "component", "components")} · {Plural(a.MessagingChannels.Count, "messaging channel", "messaging channels")}");
    }

    public static SourceSummaryRow Data(IqrSourceSnapshot s)
    {
        if (s.DatabaseArchitecture is not { } d) return new("data", "Data", "Not assessed", "This snapshot predates database extraction.", SourceAnalysisAreas.Data, null);
        if (d.Status == DatabaseAnalysisStatus.Unsupported) return new("data", "Data", "Not detected", "No supported database declaration was found in this source.", SourceAnalysisAreas.Data, null);
        var status = SourceAnalysisOverview.StatusLabel(d.Status);
        var reason = d.Status == DatabaseAnalysisStatus.Complete ? null
            : d.Conflicts.Count > 0 ? Plural(d.Conflicts.Count, "conflicting source declaration", "conflicting source declarations")
            : d.UnresolvedEvidence.Count > 0 ? NotFullyResolved(d.UnresolvedEvidence.Count)
            : "Some declarations could not be interpreted";
        return new("data", "Data", status, reason, SourceAnalysisAreas.Data, null,
            $"{Plural(d.Databases.Count, "database candidate", "database candidates")} · {Plural(DatabaseEvidenceSummary.Tables(d).Count, "table / entity", "tables / entities")}");
    }

    public static SourceSummaryRow Observability(IqrSourceSnapshot s)
    {
        if (s.Observability is not { } o) return new("observability", "Observability", "Not assessed", "This snapshot predates observability analysis.", SourceAnalysisAreas.Quality, "Observability");
        if (o.Status == ArchitectureStatus.Unsupported) return new("observability", "Observability", "Not assessed", "No supported source language was found.", SourceAnalysisAreas.Quality, "Observability");
        var review = SourceObservabilityPresentation.NeedsReview(o);
        var status = SourceAnalysisOverview.StatusLabel(o.Status);
        var reason = o.Status == ArchitectureStatus.Complete ? null : review > 0 ? EvidenceNeedsReview(review) : "Some evidence could only be read partially";
        return new("observability", "Observability", status, reason, SourceAnalysisAreas.Quality, "Observability");
    }

    /// <summary>Dependency evidence the snapshot stored at analysis time (declarations, managers, produced packages, Renovate policy files).</summary>
    public static SourceSummaryRow Dependencies(IqrSourceSnapshot s)
    {
        if (s.DependencyEvidence is not { } d) return new("dependencies", "Dependencies", "Not assessed", "This snapshot predates dependency evidence. Analyze the archive again in Source Analysis.", SourceAnalysisAreas.Quality, "Dependencies");
        if (d.Dependencies.Count == 0 && d.PublishedPackages.Count == 0) return new("dependencies", "Dependencies", "Not detected", "No supported dependency declaration in this source.", SourceAnalysisAreas.Quality, "Dependencies");
        var packages = d.Dependencies.Select(x => $"{x.Manager}:{x.PackageName}".ToLowerInvariant()).Distinct().Count();
        return new("dependencies", "Dependencies", "Complete", d.UnsupportedManifests.Count > 0 ? $"{d.UnsupportedManifests.Count} manifest type(s) not read natively" : null, SourceAnalysisAreas.Quality, "Dependencies",
            $"{Plural(packages, "declared package", "declared packages")} · {Plural(d.PublishedPackages.Count, "produced package", "produced packages")}");
    }

    public static SourceSummaryRow Domain(IqrSourceSnapshot s, string domain)
    {
        if (domain == "Observability") return Observability(s);
        if (domain == "Dependencies") return Dependencies(s);
        var key = SourceAnalysisAreas.Slug(domain);
        if (s.EvidenceDomains is not { } e) return new(key, domain, "Not assessed", "This snapshot predates the source-evidence domains.", SourceAnalysisAreas.Quality, domain);
        SourceDomainResult result = domain switch { "Infrastructure" => e.Infrastructure, "Configuration" => e.Configuration, "CI/CD" => e.CiCd, _ => e.Contracts };
        var status = SourceDomainText.Label(result.Status);
        if (result.Status == SourceDomainStatus.NotDetected) return new(key, domain, status, NotDetectedText, SourceAnalysisAreas.Quality, domain);
        if (!SourceEvidenceDomainsPresentation.Assessed(result)) return new(key, domain, status, result.StatusReason, SourceAnalysisAreas.Quality, domain);
        var (reason, detail) = domain switch
        {
            "Configuration" => (Unknown(e.Configuration) > 0 ? NeedsClassification(Unknown(e.Configuration)) : result.StatusReason,
                $"{Plural(e.Configuration.Entries.Count, "entry", "entries")} · {Plural(e.Configuration.Files.Count, "file", "files")}"),
            "CI/CD" => (CiCdReason(e.CiCd), Plural(e.CiCd.Pipelines.Count(p => !p.IsTemplate), "pipeline", "pipelines")),
            "Contracts" => (Heuristic(e.Contracts) > 0 ? HeuristicNeedsConfirmation(Heuristic(e.Contracts)) : result.StatusReason,
                $"{Plural(e.Contracts.Contracts.Count, "contract", "contracts")} detected"),
            _ => (result.StatusReason, Plural(e.Infrastructure.Resources.Count(r => r.Kind == "resource"), "resource", "resources")
                + (e.Infrastructure.Scripts.Count > 0 ? $" · {Plural(e.Infrastructure.Scripts.Count, "infrastructure script", "infrastructure scripts")} · {Plural(e.Infrastructure.Checks.Count, "source check", "source checks")}" : "")),
        };
        return new(key, domain, status, result.Status == SourceDomainStatus.Complete ? null : reason ?? "Some evidence could only be read partially", SourceAnalysisAreas.Quality, domain, detail);
    }

    /// <summary>Overview rows in reading order: Architecture, Data, Observability.</summary>
    public static IReadOnlyList<SourceSummaryRow> CoreRows(IqrSourceSnapshot s) => [Architecture(s), Data(s), Observability(s)];

    /// <summary>Quality evidence rows other than Observability (shown once under "Quality evidence" on the Overview).</summary>
    public static IReadOnlyList<SourceSummaryRow> EvidenceRows(IqrSourceSnapshot s) => [.. new[] { "Configuration", "CI/CD", "Contracts", "Infrastructure" }.Select(d => Domain(s, d))];

    /// <summary>All five quality-evidence domains (the Quality Evidence landing).</summary>
    public static IReadOnlyList<SourceSummaryRow> QualityRows(IqrSourceSnapshot s) => [.. SourceAnalysisAreas.QualityDomains.Select(d => Domain(s, d))];

    /// <summary>The primary reason a CI/CD analysis is not complete, from the pipelines themselves: unclassified steps, then pipelines without a CI
    /// trigger; only when neither applies, the short statement of what static reading cannot evaluate.</summary>
    public static string CiCdReason(PipelineEvidence p)
    {
        var other = p.Pipelines.Sum(x => x.Steps.Count(s => s.Kind == PipelineStepKind.Other));
        if (other > 0) return $"{Plural(other, "pipeline step needs", "pipeline steps need")} classification";
        var untriggered = p.Pipelines.Count(x => !x.IsTemplate && x.Triggers.All(t => t.Type is "none" or "manual"));
        if (untriggered > 0) return $"{Plural(untriggered, "pipeline has", "pipelines have")} no CI trigger detected";
        return "Templates, expressions and conditions are not evaluated";
    }

    public static int Unknown(ConfigurationEvidence c) => c.Entries.Count(e => e.Category == ConfigurationCategory.Unknown);
    public static int Heuristic(ContractEvidence k) => k.Contracts.Count(c => c.ParseSupport != DomainSupport.Supported);

    // One wording per kind of review item, shared by the summary rows, the review list and the area pages.
    public static string NotFullyResolved(int n) => Plural(n, "source declaration not fully resolved", "source declarations not fully resolved");
    public static string EvidenceNeedsReview(int n) => Plural(n, "evidence item needs review", "evidence items need review");
    public static string NeedsClassification(int n) => Plural(n, "entry needs classification", "entries need classification");
    public static string HeuristicNeedsConfirmation(int n) => Plural(n, "heuristic parse needs confirmation", "heuristic parses need confirmation");

    /// <summary>Review items per area: source evidence a person may want to look at. Analyzer limitations are listed separately, and counts of
    /// different areas are never summed into one number.</summary>
    public static IReadOnlyList<SourceAttentionItem> Attention(IqrSourceSnapshot s)
    {
        var items = new List<SourceAttentionItem>();
        if (s.Architecture is { } a && a.Status != ArchitectureStatus.Unsupported && ArchitectureEvidenceSummary.Dependencies(a) is { Unresolved: > 0 } dep)
            items.Add(new("architecture-unresolved", dep.Unresolved, Plural(dep.Unresolved, "dependency detected with an unresolved target", "dependencies detected with unresolved targets"), SourceAnalysisAreas.Architecture, null, "Dependencies"));
        if (s.DatabaseArchitecture is { } d && d.Status != DatabaseAnalysisStatus.Unsupported)
        {
            if (d.UnresolvedEvidence.Count > 0) items.Add(new("data-unresolved", d.UnresolvedEvidence.Count, NotFullyResolved(d.UnresolvedEvidence.Count), SourceAnalysisAreas.Data, null));
            if (d.Conflicts.Count > 0) items.Add(new("data-conflicts", d.Conflicts.Count, Plural(d.Conflicts.Count, "conflicting source declaration", "conflicting source declarations"), SourceAnalysisAreas.Data, null));
        }
        if (s.Observability is { } o && SourceObservabilityPresentation.NeedsReview(o) is > 0 and var review)
            items.Add(new("observability-findings", review, EvidenceNeedsReview(review), SourceAnalysisAreas.Quality, "Observability"));
        if (s.EvidenceDomains is { } e)
        {
            if (SourceEvidenceDomainsPresentation.Assessed(e.Configuration) && Unknown(e.Configuration) > 0)
                items.Add(new("configuration-uncategorized", Unknown(e.Configuration), NeedsClassification(Unknown(e.Configuration)), SourceAnalysisAreas.Quality, "Configuration"));
            if (e.Configuration.Conflicts.Count > 0)
                items.Add(new("configuration-conflicts", e.Configuration.Conflicts.Count, Plural(e.Configuration.Conflicts.Count, "potential configuration conflict", "potential configuration conflicts"), SourceAnalysisAreas.Quality, "Configuration"));
            if (SourceEvidenceDomainsPresentation.Assessed(e.Contracts) && Heuristic(e.Contracts) > 0)
                items.Add(new("contracts-review", Heuristic(e.Contracts), HeuristicNeedsConfirmation(Heuristic(e.Contracts)), SourceAnalysisAreas.Quality, "Contracts"));
        }
        return items;
    }

    /// <summary>Areas with something to review, in reading order (an area appears once even with several items).</summary>
    public static IReadOnlyList<(string Area, IReadOnlyList<SourceAttentionItem> Items)> ReviewAreas(IqrSourceSnapshot s) =>
        [.. Attention(s).GroupBy(i => i.AreaTitle).Select(g => (g.Key, (IReadOnlyList<SourceAttentionItem>)g.ToList()))];

    /// <summary>Why the snapshot's analysis coverage is Partial, from its own counts (never a failure): source files in languages Source Analysis
    /// detects but does not analyze, heuristic parsing, analyzer limitations; runtime state is always outside Source Analysis.</summary>
    public static SnapshotCoverage Coverage(IqrSourceSnapshot s)
    {
        var reasons = new List<string>();
        var coverage = s.TechnologyCoverage;
        if (coverage is { UnsupportedSourceFiles: > 0 } c) reasons.Add($"{Plural(c.UnsupportedSourceFiles, "source file is", "source files are")} in a language Source Analysis detects but does not analyze");
        var heuristic = s.EvidenceDomains is { } e ? Heuristic(e.Contracts) : 0;
        if (heuristic > 0) reasons.Add($"{Plural(heuristic, "contract", "contracts")} required heuristic parsing");
        var limitations = s.Limitations.Concat(coverage?.Limitations ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (limitations.Count > 0) reasons.Add($"{Plural(limitations.Count, "analyzer limitation applies", "analyzer limitations apply")} to this snapshot");
        reasons.Add("Runtime and deployment state are outside Source Analysis");
        var status = s.Status switch { SourceAnalysisStatus.Ready => "Complete", SourceAnalysisStatus.Partial => "Partial", _ => "Not assessed" };
        return new(status, reasons, limitations);
    }

    /// <summary>Analyzer limitations of the snapshot (what analyzers could not interpret) — collapsed by default, never an application defect.</summary>
    public static int Limitations(IqrSourceSnapshot s) =>
        (s.Architecture?.Limitations.Count ?? 0) + (s.DatabaseArchitecture?.Diagnostics.Count ?? 0) + (s.Observability?.Limitations.Count ?? 0)
        + (s.EvidenceDomains?.Domains.Sum(d => d.Limitations.Count) ?? 0);

    /// <summary>Contracts needing review and why: partial/heuristic parsing, no owning component, unsupported constructs.</summary>
    public static IReadOnlyList<(SourceContract Contract, string Reason)> ContractsNeedingReview(ContractEvidence k) =>
        [.. k.Contracts.Select(c => (c, Reason: c.ParseSupport == DomainSupport.Unsupported ? "Not parsed — needs confirmation"
                : c.ParseSupport == DomainSupport.Partial ? "Heuristic parsing — needs confirmation"
                : c.Producer is null && c.ConsumerHints.Count == 0 ? "Producer/owner not stated in source"
                : c.EvidenceState is ArchitectureEvidenceState.Conflict ? "Ambiguous owner — needs review" : null))
            .Where(x => x.Reason is not null).Select(x => (x.c, x.Reason!))];

    /// <summary>Who uses a contract, from source evidence only: the producing component, consumer hints and cross-domain links.</summary>
    public static (IReadOnlyList<string> ProducedBy, IReadOnlyList<string> UsedBy) Usage(SourceContract c, CrossDomainEvidence links)
    {
        var produced = new List<string>();
        if (c.Producer is { } p) produced.Add(p);
        produced.AddRange(links.Links.Where(l => l.Type == SourceEvidenceLinkType.ContractProducedByComponent && l.FromId == c.Id && l.Resolved).Select(l => l.ToLabel));
        var used = c.ConsumerHints.Concat(links.Links.Where(l => l.Type == SourceEvidenceLinkType.ContractConsumedByComponent && l.FromId == c.Id && l.Resolved).Select(l => l.ToLabel));
        return ([.. produced.Distinct(StringComparer.Ordinal)], [.. used.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>Plain-language parsing method: Structured (a supported structured reader) or Heuristic (some constructs interpreted heuristically).
    /// A method, never a validity verdict.</summary>
    public static string Parsing(DomainSupport support) => support switch
    {
        DomainSupport.Supported => "Structured",
        DomainSupport.Partial => "Heuristic",
        _ => "Not parsed",
    };

    /// <summary>Review state of one contract, kept apart from its existence, ownership, usage and parsing method: heuristic or unparsed contracts
    /// need confirmation; an ambiguous owner needs review; otherwise no action.</summary>
    public static string ContractReviewState(SourceContract c) =>
        c.ParseSupport != DomainSupport.Supported ? "Needs confirmation"
        : c.EvidenceState is ArchitectureEvidenceState.Conflict ? "Needs review"
        : "No action";

    public static IReadOnlyList<SourceChangeGroup> ArchitectureChanges(IEnumerable<ArchitectureChange> changes) =>
        [.. changes.GroupBy(c => c.Area).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new SourceChangeGroup(g.Key,
            g.Count(c => c.Kind == ArchitectureChangeKind.Added), g.Count(c => c.Kind == ArchitectureChangeKind.Removed), g.Count(c => c.Kind == ArchitectureChangeKind.Changed)))];

    /// <summary>"Added table" → (Added, Tables); groups database changes by object kind.</summary>
    public static (string Verb, string Group) DatabaseChangeKind(string kind)
    {
        var parts = kind.Split(' ', 2);
        var group = parts.Length < 2 ? "Other" : parts[1].ToLowerInvariant() switch
        {
            "table" => "Tables", "column" => "Columns", "fk" => "Relationships", "index" => "Indexes", "key" => "Keys", _ => "Other",
        };
        return (parts[0], group);
    }

    public static IReadOnlyList<SourceChangeGroup> DatabaseChanges(IEnumerable<DatabaseSchemaChange> changes) =>
        [.. changes.Select(c => (Kind: DatabaseChangeKind(c.Kind), c)).GroupBy(x => x.Kind.Group)
            .OrderBy(g => Array.IndexOf(new[] { "Tables", "Columns", "Relationships", "Indexes", "Keys", "Other" }, g.Key))
            .Select(g => new SourceChangeGroup(g.Key, g.Count(x => x.Kind.Verb == "Added"), g.Count(x => x.Kind.Verb == "Removed"), g.Count(x => x.Kind.Verb == "Changed")))];

    public static IReadOnlyList<SourceChangeGroup> EvidenceChanges(IEnumerable<SourceEvidenceChange> changes) =>
        [.. changes.GroupBy(c => c.Area).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new SourceChangeGroup(g.Key,
            g.Count(c => c.Kind == SourceEvidenceChangeKind.Added), g.Count(c => c.Kind == SourceEvidenceChangeKind.Removed), g.Count(c => c.Kind == SourceEvidenceChangeKind.Changed)))];

    /// <summary>"+3 added · −1 removed · ~2 changed", zero parts omitted.</summary>
    public static string ChangeText(SourceChangeGroup g) =>
        string.Join(" · ", new[] { g.Added > 0 ? $"+{g.Added} added" : null, g.Removed > 0 ? $"−{g.Removed} removed" : null, g.Changed > 0 ? $"~{g.Changed} changed" : null }.OfType<string>());

    /// <summary>A path shortened to its file name (the full path stays in the evidence detail).</summary>
    public static string FileName(string? path) => string.IsNullOrEmpty(path) ? "—" : path.Replace('\\', '/').Split('/')[^1];

    public static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";
}
