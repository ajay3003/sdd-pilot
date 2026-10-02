using BirkNext.Integrations;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Web.Services;

/// <summary>One compact landing row for a source-evidence domain. Metrics come from that domain's own model of the SAME snapshot.</summary>
public sealed record SourceDomainRow(string Area, string Title, string Description, string Status, string Tone, string? Reason, IReadOnlyList<SourceAreaMetric> Metrics);

/// <summary>Resolved / unresolved counts per cross-domain relationship type (the landing summary — links themselves are drill-down).</summary>
public sealed record SourceLinkSummary(SourceEvidenceLinkType Type, string Label, int Linked, int Unresolved);

/// <summary>
/// Source Analysis → Infrastructure, Configuration, CI/CD and Contracts, read from the selected snapshot's stored evidence domains (never
/// re-analyzed). Pure functions: status labels and tones, landing rows, filters and counts. Every state is a source fact: "declared",
/// "configured", "defined" — never deployed, connected, executed or compatible.
/// </summary>
public static class SourceEvidenceDomainsPresentation
{
    public const string Boundary = SourceDomainText.SourceBoundary;
    public static readonly IReadOnlyList<string> Areas = ["Infrastructure", "Configuration", "CI/CD", "Contracts"];
    public const string HistoricalNote = "This historical source snapshot predates the source-evidence domains. Upload the same source archive to create a new immutable analysis.";

    public static string Tone(SourceDomainStatus status) => status switch
    {
        SourceDomainStatus.Complete => "complete",
        SourceDomainStatus.Partial => "partial",
        SourceDomainStatus.FailedAnalysis => "attention",
        _ => "muted",
    };

    public static string Tone(ArchitectureEvidenceState state) => state switch
    {
        ArchitectureEvidenceState.Confirmed or ArchitectureEvidenceState.StronglySupported => "complete",
        ArchitectureEvidenceState.Inferred => "partial",
        ArchitectureEvidenceState.Conflict => "attention",
        _ => "muted",
    };

    public static string Tone(DomainSupport support) => support switch { DomainSupport.Supported => "complete", DomainSupport.Partial => "partial", _ => "muted" };

    public static string Slug(string area) => area switch { "CI/CD" => "cicd", "Infrastructure" => "infra", "Configuration" => "config", _ => area.ToLowerInvariant() };

    public static string Location(string file, int line) => line > 0 ? $"{file}:{line}" : file;

    /// <summary>"Not assessed" instead of a zero for a domain that could not run or found nothing to assess (a zero would read as a measurement).</summary>
    public static bool Assessed(SourceDomainResult r) => r.Status is SourceDomainStatus.Complete or SourceDomainStatus.Partial;

    // ── Landing rows ────────────────────────────────────────────────────────────────────────────────────────────────────

    public static IReadOnlyList<SourceDomainRow> Rows(IqrSourceSnapshot s) => Areas.Select(a => Row(s, a)).ToList();

    public static SourceDomainRow Row(IqrSourceSnapshot s, string area)
    {
        var (title, description) = area switch
        {
            "Infrastructure" => ("Infrastructure", "Infrastructure as Code: declared resources, identity, networking, messaging, datastores and telemetry."),
            "Configuration" => ("Configuration", "Application and environment configuration — keys, categories, environments; secret values never shown."),
            "CI/CD" => ("CI/CD", "Pipeline definitions: triggers, build/test/scan steps and deployment stages (defined, not executed)."),
            _ => ("Contracts", "OpenAPI, GraphQL, message and other schema contracts with producer/consumer hints."),
        };
        if (s.EvidenceDomains is not { } e) return new(area, title, description, "Not analyzed", "muted", "This source snapshot predates the source-evidence domains.", []);
        SourceDomainResult result = area switch { "Infrastructure" => e.Infrastructure, "Configuration" => e.Configuration, "CI/CD" => e.CiCd, _ => e.Contracts };
        var status = SourceDomainText.Label(result.Status);
        if (!Assessed(result)) return new(area, title, description, status, Tone(result.Status), result.StatusReason, []);
        List<SourceAreaMetric> metrics = area switch
        {
            "Infrastructure" => [new("Resources", e.Infrastructure.Resources.Count(r => r.Kind == "resource")), new("Modules", e.Infrastructure.Modules.Count),
                new("Access assignments", e.Infrastructure.AccessAssignments.Count), new("Environments", e.Infrastructure.Environments.Count)],
            "Configuration" => [new("Entries", e.Configuration.Entries.Count), new("Files", e.Configuration.Files.Count), new("Environments", NamedEnvironments(e.Configuration)),
                new("Sensitive values", e.Configuration.Entries.Count(x => x.Sensitivity != ConfigurationSensitivity.None))],
            "CI/CD" => [new("Pipelines", e.CiCd.Pipelines.Count(p => !p.IsTemplate)), new("Templates", e.CiCd.Pipelines.Count(p => p.IsTemplate)),
                new("Test steps", e.CiCd.Pipelines.Sum(p => p.Steps.Count(x => IsTest(x.Kind)))), new("Deployment stages", e.CiCd.Pipelines.Sum(p => p.Stages.Count(x => x.Deployment)))],
            _ => [new("Contracts", e.Contracts.Contracts.Count), new("REST / OpenAPI", e.Contracts.Contracts.Count(c => c.Type == SourceContractType.OpenApi)),
                new("GraphQL", e.Contracts.Contracts.Count(c => c.Type is SourceContractType.GraphQlSchema or SourceContractType.GraphQlOperations)),
                new("Messages / events", e.Contracts.Contracts.Count(c => c.Type is SourceContractType.MessageContract or SourceContractType.AsyncApi or SourceContractType.Protobuf))],
        };
        var technologies = result.Technologies.Count == 0 ? null : string.Join(" · ", result.Technologies.Take(4));
        return new(area, title, description, status, Tone(result.Status), technologies, metrics);
    }

    public static int NamedEnvironments(ConfigurationEvidence c) => c.Environments.Where(e => e.Kind != SourceEnvironmentKind.Default).Select(e => e.Kind).Distinct().Count();

    public static IReadOnlyList<SourceLinkSummary> LinkSummary(CrossDomainEvidence x) =>
        x.Links.GroupBy(l => l.Type).OrderBy(g => g.Key).Select(g => new SourceLinkSummary(g.Key, SourceDomainText.Label(g.Key), g.Count(l => l.Resolved), g.Count(l => !l.Resolved))).ToList();

    // ── Infrastructure ──────────────────────────────────────────────────────────────────────────────────────────────────

    public static readonly InfrastructureCategory[] NetworkCategories = [InfrastructureCategory.Networking, InfrastructureCategory.Dns, InfrastructureCategory.ApiGateway];
    public static readonly InfrastructureCategory[] DatastoreCategories = [InfrastructureCategory.Database, InfrastructureCategory.Storage, InfrastructureCategory.Cache];
    public static readonly InfrastructureCategory[] IdentityCategories = [InfrastructureCategory.Identity, InfrastructureCategory.AccessControl, InfrastructureCategory.SecretStore];

    public static int Count(InfrastructureEvidence i, params InfrastructureCategory[] categories) => i.Resources.Count(r => r.Kind == "resource" && categories.Contains(r.Category));

    public static IEnumerable<InfrastructureResource> FilterResources(IEnumerable<InfrastructureResource> resources, string category, string search) => resources.Where(r =>
        (category.Length == 0 || r.Category.ToString() == category)
        && (search.Length == 0 || $"{r.LogicalName} {r.DeclaredName} {r.ResourceType} {r.CategoryDetail} {r.File}".Contains(search, StringComparison.OrdinalIgnoreCase)));

    public static string ResourceLabel(InfrastructureEvidence i, string id) => i.Resources.FirstOrDefault(r => r.Id == id) is { } r ? $"{r.ResourceType}.{r.LogicalName}"
        : i.Modules.FirstOrDefault(m => m.Id == id) is { } m ? $"module {m.Name}" : id;

    // ── Configuration ───────────────────────────────────────────────────────────────────────────────────────────────────

    public sealed record ConfigFilter(string Category = "", string Environment = "", string Technology = "", string Sensitivity = "", string File = "", string Search = "");

    public static IEnumerable<ConfigurationEntry> Filter(IEnumerable<ConfigurationEntry> entries, ConfigFilter f) => entries.Where(e =>
        (f.Category.Length == 0 || e.Category.ToString() == f.Category)
        && (f.Environment.Length == 0 || EnvironmentKey(e.Environment) == f.Environment)
        && (f.Technology.Length == 0 || e.Technology == f.Technology)
        && (f.File.Length == 0 || e.File == f.File)
        && (f.Sensitivity.Length == 0 || (f.Sensitivity == "Sensitive" ? e.Sensitivity != ConfigurationSensitivity.None : e.Sensitivity == ConfigurationSensitivity.None))
        && (f.Search.Length == 0 || e.Key.Contains(f.Search, StringComparison.OrdinalIgnoreCase) || (e.ValuePreviewSafe?.Contains(f.Search, StringComparison.OrdinalIgnoreCase) ?? false)));

    public static string EnvironmentKey(SourceEnvironmentLabel e) => e.Kind == SourceEnvironmentKind.Default ? "Default" : $"{e.Kind}:{e.Raw.ToLowerInvariant()}";

    /// <summary>What the value column shows: a safe preview, or the KIND for anything sensitive — never the value.</summary>
    public static string ValueText(ConfigurationEntry e) => e.Sensitivity switch
    {
        ConfigurationSensitivity.Sensitive => e.ValueKind == ConfigurationValueKind.ConnectionString ? "Connection string detected — value not shown" : "Sensitive value — not shown",
        ConfigurationSensitivity.SecretReference => e.ValuePreviewSafe ?? "Secret reference",
        _ => e.ValuePreviewSafe ?? (e.ValueKind == ConfigurationValueKind.Empty ? "(empty)" : $"{SourceDomainText.Label(e.ValueKind)} — value not shown"),
    };

    // ── CI/CD ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static bool IsTest(PipelineStepKind k) => k is PipelineStepKind.Test or PipelineStepKind.UnitTest or PipelineStepKind.IntegrationTest or PipelineStepKind.FrontendTest
        or PipelineStepKind.E2ETest or PipelineStepKind.AccessibilityTest;
    public static bool IsCheck(PipelineStepKind k) => k is PipelineStepKind.DependencyScan or PipelineStepKind.SecurityScan or PipelineStepKind.Sbom or PipelineStepKind.StaticAnalysis;
    public static bool IsDeploy(PipelineStepKind k) => k is PipelineStepKind.ApplicationDeploy or PipelineStepKind.InfrastructureDeploy or PipelineStepKind.InfrastructurePlan or PipelineStepKind.DatabaseMigration;

    /// <summary>A pipeline's steps plus those of the templates it includes (resolved by file within the same snapshot, cycle-safe). The included
    /// steps are still definitions: nothing here says they ran.</summary>
    public static List<PipelineStep> EffectiveSteps(PipelineEvidence evidence, PipelineDefinition pipeline)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { pipeline.File };
        var steps = new List<PipelineStep>(pipeline.Steps.Where(s => s.Kind != PipelineStepKind.Template));
        var queue = new Queue<string>(pipeline.Templates);
        while (queue.Count > 0)
        {
            var file = queue.Dequeue();
            if (!seen.Add(file) || evidence.Pipelines.FirstOrDefault(p => p.File == file) is not { } template) continue;
            steps.AddRange(template.Steps.Where(s => s.Kind != PipelineStepKind.Template));
            foreach (var next in template.Templates) queue.Enqueue(next);
        }
        return steps;
    }

    public static string TriggerText(PipelineTrigger t)
    {
        var parts = new List<string> { t.Type switch { "pull-request" => "Pull request", "push" => "Push", "schedule" => "Schedule", "manual" => "Manual", "none" => "No CI trigger", "pipeline-resource" => "Another pipeline", _ => t.Type } };
        if (t.BranchesInclude.Count > 0) parts.Add($"branches {string.Join(", ", t.BranchesInclude)}");
        if (t.BranchesExclude.Count > 0) parts.Add($"excluding {string.Join(", ", t.BranchesExclude)}");
        if (t.PathsInclude.Count > 0) parts.Add($"paths {string.Join(", ", t.PathsInclude.Take(4))}{(t.PathsInclude.Count > 4 ? $" +{t.PathsInclude.Count - 4}" : "")}");
        if (t.PathsExclude.Count > 0) parts.Add($"ignoring {string.Join(", ", t.PathsExclude.Take(3))}");
        if (t.Schedule is { Length: > 0 } cron) parts.Add($"cron {cron}");
        return string.Join(" · ", parts);
    }

    // ── Contracts ───────────────────────────────────────────────────────────────────────────────────────────────────────

    public static string Owner(SourceContract c) => c.Producer is { } p ? $"Producer: {p}" : c.ConsumerHints.Count > 0 ? $"Consumer: {string.Join(", ", c.ConsumerHints)}" : "No owning component";

    public static IEnumerable<SourceContract> FilterContracts(IEnumerable<SourceContract> contracts, string type, string search) => contracts.Where(c =>
        (type.Length == 0 || c.Type.ToString() == type) && (search.Length == 0 || $"{c.Name} {c.File} {c.Producer} {string.Join(" ", c.ConsumerHints)}".Contains(search, StringComparison.OrdinalIgnoreCase)));

    // ── History ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The latest earlier evidence-domain analysis of the SAME repository (repository key, else archive name) — never another repository.</summary>
    public static (string Label, SourceEvidenceDomainsSnapshot Evidence)? Previous(IReadOnlyList<IqrSourceSnapshot> all, IqrSourceSnapshot current) =>
        all.Where(s => s.Id != current.Id && s.AnalyzedAt < current.AnalyzedAt && s.EvidenceDomains is not null
                && (s.Repository is { } ra && current.Repository is { } rb ? ra.Key == rb.Key : s.Archive.FileName == current.Archive.FileName))
            .OrderByDescending(s => s.AnalyzedAt).Select(s => ($"{s.Archive.FileName} · {SourceAnalysisOverview.Utc(s.AnalyzedAt)}", s.EvidenceDomains!))
            .Cast<(string, SourceEvidenceDomainsSnapshot)?>().FirstOrDefault();

    /// <summary>The configured form of a declared storage account: an Azure storage account becomes its Blob endpoint; other providers keep the name.</summary>
    public static string StorageEndpoint(SourceResourceCandidate c) =>
        c.ResourceType.StartsWith("azurerm_", StringComparison.Ordinal) || c.ResourceType.StartsWith("Microsoft.", StringComparison.Ordinal)
            ? $"https://{c.Name}.blob.core.windows.net/" : c.Name;
}
