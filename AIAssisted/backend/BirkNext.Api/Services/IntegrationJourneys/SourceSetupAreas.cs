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
            }),
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

    private static string Domain(SourceDomainResult? result) => result?.Status switch
    {
        null => "NotAnalyzed",
        SourceDomainStatus.FailedAnalysis => "Failed",
        var status => status.ToString()!,
    };
}
