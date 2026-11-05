using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>
/// Per-area outcomes of one stored snapshot, read from the models the snapshot already holds (nothing is re-analyzed). The snapshot's own
/// <see cref="IqrSourceSnapshot.Status"/> is the application-code analyzer's outcome only: Failed there means no parseable production project,
/// while architecture and the evidence domains (Infrastructure as Code, configuration, CI/CD, contracts) are analyzed independently.
/// </summary>
public static class SourceSetupAreas
{
    public const string ApplicationCode = "Application code", Architecture = "Architecture", Infrastructure = "Infrastructure as Code",
        Configuration = "Configuration", CiCd = "CI/CD", Contracts = "Contracts";

    public static IReadOnlyList<SourceSetupArea> Of(IqrSourceSnapshot snapshot)
    {
        var domains = snapshot.EvidenceDomains;
        return
        [
            new(ApplicationCode, snapshot.Status switch
            {
                SourceAnalysisStatus.Ready => "Complete",
                SourceAnalysisStatus.Partial => "Partial",
                // No production project at all is "not detected" (e.g. an infrastructure-only repository); production projects that could not be
                // read are a failed analysis.
                _ => snapshot.Projects.Any(project => !project.IsTest) ? "Failed" : "NotDetected",
            } is var code && code != "NotDetected" && snapshot.Projects.Where(project => !project.IsTest).All(IsDocumentationSample)
                // Only sample/template projects inside a documentation tree (an infrastructure repository's docs/templates/…/sample): no application code.
                ? "NotDetected" : code),
            new(Architecture, snapshot.Architecture?.Status switch
            {
                null => "NotAnalyzed",
                ArchitectureStatus.Complete => "Complete",
                ArchitectureStatus.Unsupported => "Unsupported",
                _ => "Partial",
            }),
            new(Infrastructure, Domain(domains?.Infrastructure)),
            new(Configuration, Domain(domains?.Configuration)),
            new(CiCd, Domain(domains?.CiCd)),
            new(Contracts, Domain(domains?.Contracts)),
        ];
    }

    /// <summary>
    /// The snapshot's analysis outcome over the areas it actually contains — the one rule the Snapshot Registry and every consumer share. An
    /// area that is not present (NotDetected) or was not run (NotAnalyzed) does not count: a Terraform repository without application code is
    /// not a failed analysis. Every present area complete → Completed; every present area failed → Failed; otherwise Partial (an Unsupported
    /// area is an analyzer limitation and keeps the outcome Partial, never Completed). Nothing present → Unknown.
    /// </summary>
    public static BirkNext.Snapshots.SnapshotProcessingStatus Overall(IReadOnlyList<SourceSetupArea> areas)
    {
        var present = areas.Where(area => area.Status is not ("NotDetected" or "NotAnalyzed")).ToList();
        if (present.Count == 0) return BirkNext.Snapshots.SnapshotProcessingStatus.Unknown;
        if (present.All(area => area.Status == "Complete")) return BirkNext.Snapshots.SnapshotProcessingStatus.Completed;
        if (present.All(area => area.Status == "Failed")) return BirkNext.Snapshots.SnapshotProcessingStatus.Failed;
        return BirkNext.Snapshots.SnapshotProcessingStatus.Partial;
    }

    /// <summary>Production projects the analyzer classified from their project file as a web/service host (Web SDK) or an Event Hub
    /// application — the deterministic "application, not only a library" evidence used for package classification.</summary>
    public static int ServiceProjects(IqrSourceSnapshot snapshot) => snapshot.Projects.Count(project => !project.IsTest && !IsDocumentationSample(project)
        && (project.Classification.StartsWith("Application/service", StringComparison.Ordinal) || project.Classification.StartsWith("Event Hub SDK", StringComparison.Ordinal)));

    /// <summary>
    /// A project inside a documentation tree (a folder named docs, doc or documentation): a template or sample that documentation ships, never a
    /// production application of the package. Structural only — package classification (application candidate vs evidence) reads it; the
    /// Source Analysis analyzers are unchanged.
    /// </summary>
    public static bool IsDocumentationSample(SourceProject project)
    {
        var folders = project.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return folders[..^1].Any(f => f.Equals("docs", StringComparison.OrdinalIgnoreCase) || f.Equals("doc", StringComparison.OrdinalIgnoreCase)
            || f.Equals("documentation", StringComparison.OrdinalIgnoreCase));
    }

    private static string Domain(SourceDomainResult? result) => result?.Status switch
    {
        null => "NotAnalyzed",
        SourceDomainStatus.FailedAnalysis => "Failed",
        var status => status.ToString()!,
    };
}
