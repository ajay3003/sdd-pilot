using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

/// <summary>
/// Integration Quality Review's reading of the Source Analysis evidence domains stored on the selected snapshots (through
/// <see cref="SourceEvidenceQueries"/> — never a rescan): integration configuration (Configuration), network declarations (Connectivity),
/// message/event contracts (Contract), messaging declarations and app↔infrastructure links (Message flow), telemetry declarations and the
/// observability evidence layers (Observability), access assignments and security settings (Security). Everything is "Configured/Declared
/// in source"; runtime Observed evidence stays with the runtime adapters and is never replaced or upgraded by these lines.
/// </summary>
public static class IqrSourceDomainsReview
{
    public const string RuntimeLimitation = "Declared/configured in source only; deployed resources, reachability, effective permissions, message processing and telemetry delivery are not established by source";

    private static IEnumerable<IqrSourceSnapshot> With(IEnumerable<IqrSourceSnapshot> snapshots) => snapshots.Where(s => s.EvidenceDomains is not null);

    public static int EvidenceCount(IntegrationReviewDomain domain, IReadOnlyList<IqrSourceSnapshot> snapshots) => With(snapshots).Sum(s => domain switch
    {
        IntegrationReviewDomain.Configuration => SourceEvidenceQueries.IntegrationConfiguration(s).Items.Count,
        IntegrationReviewDomain.Connectivity => SourceEvidenceQueries.NetworkInfrastructure(s).Items.Count,
        IntegrationReviewDomain.Contract => SourceEvidenceQueries.MessageContracts(s).Items.Count,
        IntegrationReviewDomain.MessageFlow => SourceEvidenceQueries.MessagingInfrastructure(s).Items.Count,
        IntegrationReviewDomain.Observability => SourceEvidenceQueries.ObservabilityInfrastructure(s).Items.Count,
        IntegrationReviewDomain.Security => SourceEvidenceQueries.IdentityInfrastructure(s).Items.Count + SourceEvidenceQueries.SecuritySettings(s).Items.Count,
        _ => 0,
    });

    public static string? Available(IReadOnlyList<IqrSourceSnapshot> snapshots)
    {
        var list = With(snapshots).ToList();
        if (list.Count == 0) return null;
        var e = list.Select(s => s.EvidenceDomains!).ToList();
        return $"Source Analysis evidence domains available: {e.Sum(x => x.Infrastructure.Resources.Count)} declared resource(s), {e.Sum(x => x.Configuration.Entries.Count)} configuration entr(ies), "
            + $"{e.Sum(x => x.CiCd.Pipelines.Count)} pipeline(s), {e.Sum(x => x.Contracts.Contracts.Count)} contract(s); runtime state not established";
    }

    public static (List<string> Observed, List<string> Missing) Domain(IntegrationReviewDomain domain, IReadOnlyList<IqrSourceSnapshot> snapshots)
    {
        var observed = new List<string>();
        var missing = new List<string>();
        foreach (var s in With(snapshots))
        {
            var at = $"(source snapshot {s.Archive.Sha256[..Math.Min(8, s.Archive.Sha256.Length)]}, evidence v{s.EvidenceDomains!.Version})";
            switch (domain)
            {
                case IntegrationReviewDomain.Configuration:
                {
                    var c = SourceEvidenceQueries.IntegrationConfiguration(s);
                    if (c.Items.Count == 0) break;
                    var envs = c.Items.Select(i => SourceDomainText.Label(i.Environment)).Distinct().Order(StringComparer.Ordinal).ToList();
                    observed.Add($"Configured in source {at}: {c.Items.Count} integration configuration entr(ies) — {string.Join(", ", c.Items.GroupBy(i => i.Category).OrderBy(g => g.Key).Select(g => $"{g.Count()} {SourceDomainText.Label(g.Key).ToLowerInvariant()}"))}; environments {string.Join(", ", envs)}");
                    var sensitive = c.Items.Count(i => i.Sensitivity != ConfigurationSensitivity.None);
                    if (sensitive > 0) observed.Add($"Source configuration holds {sensitive} sensitive integration value(s) or secret reference(s); values are not read");
                    var conflicts = s.EvidenceDomains.Configuration.Conflicts.Count(x => c.Items.Any(i => i.NormalizedKey == x.NormalizedKey));
                    if (conflicts > 0) observed.Add($"Source configuration conflicts needing review: {conflicts} (same key and environment, different values)");
                    break;
                }
                case IntegrationReviewDomain.Connectivity:
                {
                    var n = SourceEvidenceQueries.NetworkInfrastructure(s);
                    if (n.Items.Count == 0) break;
                    var privateEndpoints = n.Items.Count(r => r.CategoryDetail == "Private endpoint" || r.ResourceType.Contains("vpc_endpoint", StringComparison.Ordinal));
                    var publicAccess = SourceEvidenceQueries.SecuritySettings(s).Items.Where(x => x.Setting.Key.StartsWith("public_network_access", StringComparison.Ordinal)).ToList();
                    observed.Add($"Declared in source {at}: {n.Items.Count} network declaration(s), {privateEndpoints} private endpoint(s)"
                        + (publicAccess.Count > 0 ? $"; public network access setting declared on {publicAccess.Count} resource(s) ({string.Join(", ", publicAccess.Select(x => x.Setting.Value).Distinct().Take(3))})" : ""));
                    missing.Add("Network reachability and private connectivity are not verified by source declarations");
                    break;
                }
                case IntegrationReviewDomain.Contract:
                {
                    var m = SourceEvidenceQueries.MessageContracts(s);
                    if (m.Items.Count == 0) break;
                    observed.Add($"Source contracts {at}: {string.Join(", ", m.Items.GroupBy(x => x.Type).OrderBy(g => g.Key).Select(g => $"{g.Count()} {SourceDomainText.Label(g.Key).ToLowerInvariant()}"))}");
                    missing.Add("A source contract does not prove runtime message compatibility or processing");
                    break;
                }
                case IntegrationReviewDomain.MessageFlow:
                {
                    var m = SourceEvidenceQueries.MessagingInfrastructure(s);
                    if (m.Items.Count == 0) break;
                    observed.Add($"Messaging declared in IaC {at}: {string.Join(", ", m.Items.GroupBy(r => r.CategoryDetail).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}"))}");
                    var links = SourceEvidenceQueries.Links(s, SourceEvidenceLinkType.ApplicationUsesInfrastructureResource, SourceEvidenceLinkType.ConfigurationReferencesInfrastructureResource).Items;
                    if (links.Count > 0) observed.Add($"Application/configuration ↔ messaging declarations: {links.Count(l => l.Resolved)} linked by exact name, {links.Count(l => !l.Resolved)} unresolved (may be managed elsewhere)");
                    missing.Add("Declared topics, queues and consumer groups do not prove a consumer reads or processes them");
                    break;
                }
                case IntegrationReviewDomain.Observability:
                {
                    var o = SourceEvidenceQueries.ObservabilityInfrastructure(s);
                    var layers = SourceEvidenceQueries.ObservabilityLayers(s).Items;
                    if (o.Items.Count == 0 && layers.All(l => l.Items == 0)) break;
                    observed.Add($"Source observability layers {at}: {string.Join("; ", layers.Select(l => $"{l.Layer} — {l.State}"))}");
                    missing.Add("Declared telemetry resources and diagnostic settings do not prove telemetry is delivered");
                    break;
                }
                case IntegrationReviewDomain.Security:
                {
                    var access = SourceEvidenceQueries.IdentityInfrastructure(s).Items;
                    var settings = SourceEvidenceQueries.SecuritySettings(s).Items.Where(x => x.Setting.Area == "Security").ToList();
                    if (access.Count == 0 && settings.Count == 0) break;
                    observed.Add($"Security declarations in IaC {at}: {access.Count} access assignment(s), {settings.Count} security setting(s) on {settings.Select(x => x.Resource.Id).Distinct().Count()} resource(s)");
                    missing.Add("Declared role assignments are not effective permissions; runtime identity access is assessed separately");
                    break;
                }
            }
        }
        return observed.Count == 0 ? ([], []) : (observed, [.. missing.Distinct(), RuntimeLimitation]);
    }
}
