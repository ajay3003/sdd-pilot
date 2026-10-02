using System.Text.RegularExpressions;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;
using BirkNext.SourceObservability;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Stage 3 — cross-domain links between evidence of the SAME snapshot: application ↔ infrastructure, application ↔ datastore declarations,
/// configuration ↔ infrastructure, pipeline ↔ infrastructure, pipeline ↔ tests, component ↔ contract and component ↔ telemetry, plus environment
/// mappings and the observability evidence layers. Strong links need an exact identity (entity name, host, path); a type-only match is
/// Inferred; nothing matching is Unresolved — neutral ("no matching declaration in the selected source; it may be managed elsewhere"),
/// never "missing" or "failed". Links are source facts; none says anything is connected, deployed or flowing at runtime.
/// </summary>
internal sealed class CrossDomainLinker : ISourceEvidenceDomainAnalyzer
{
    public const int Version = 1;
    public DomainAnalyzerInfo Info { get; } = SourceEvidenceAnalyzer.Info(SourceEvidenceDomain.CrossDomain, "Cross-domain linker", Version, 3, ["Architecture", "Observability", "Infrastructure", "Configuration", "CI/CD", "Contracts"],
        [SourceEvidenceDomain.Infrastructure, SourceEvidenceDomain.Configuration, SourceEvidenceDomain.CiCd, SourceEvidenceDomain.Contracts],
        ["Typed source-evidence links", "Unresolved links", "Environment mappings", "Observability evidence layers"]);

    public const string UnresolvedBasis = "No matching declaration found in the selected source — it may be managed elsewhere (another repository or outside IaC).";

    public void Failed(SourceEvidenceContext context, string reason) => context.CrossDomain = context.Envelope(new CrossDomainEvidence
    { Status = SourceDomainStatus.FailedAnalysis, StatusReason = reason, Limitations = [SourceDomainText.SourceBoundary] }, SourceEvidenceDomain.CrossDomain, Version);


    public void Analyze(SourceEvidenceContext context, CancellationToken ct)
    {
        var links = new List<SourceEvidenceLink>();
        var infra = context.Infrastructure?.Resources.Where(r => r.Kind == "resource").ToList() ?? [];
        var hasInfra = infra.Count > 0;
        var limitations = new List<string> { SourceDomainText.SourceBoundary, "A link is a source-derived relationship. It does not prove connectivity, deployment, processing or telemetry delivery." };
        if (!hasInfra) limitations.Add("No infrastructure declarations in the selected source, so application and configuration references are not linked to infrastructure (not counted as unresolved).");
        void Add(SourceEvidenceLinkType type, string fromId, string fromLabel, string? toId, string toLabel, ArchitectureEvidenceState state, string basis) =>
            links.Add(new SourceEvidenceLink { Id = $"{type}:{fromId}->{toId ?? "?"}:{links.Count}", Type = type, FromId = fromId, FromLabel = fromLabel, ToId = toId, ToLabel = toLabel, State = state, Basis = basis });
        // A declaration is named X when its default name or one of its per-environment names is X (exact, case-insensitive).
        static bool Named(InfrastructureResource r, string name) => r.EnvironmentNames.Select(n => n.Name).Append(r.DeclaredName).Any(n => n is not null && n.Equals(name, StringComparison.OrdinalIgnoreCase));

        // Application (Architecture channels) ↔ messaging declarations: exact entity names only.
        foreach (var channel in context.Architecture?.MessagingChannels ?? [])
        {
            ct.ThrowIfCancellationRequested();
            if (!hasInfra || channel.NameUnresolved || channel.EntityName is null) continue;
            var names = new[] { channel.EntityName }.Concat(channel.EnvironmentVariants.Values).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var matches = infra.Where(r => r.Category == InfrastructureCategory.Messaging && names.Any(n => Named(r, n))).ToList();
            foreach (var m in matches) Add(SourceEvidenceLinkType.ApplicationUsesInfrastructureResource, $"arch:channel:{channel.Id}", channel.Name, m.Id, $"{m.CategoryDetail} {m.DeclaredName}",
                ArchitectureEvidenceState.StronglySupported, "Application source and infrastructure declaration name the same messaging entity (exact name).");
            if (matches.Count == 0) Add(SourceEvidenceLinkType.ApplicationUsesInfrastructureResource, $"arch:channel:{channel.Id}", channel.Name, null, channel.EntityName, ArchitectureEvidenceState.Unresolved, UnresolvedBasis);
        }

        // Application datastores ↔ database/cache/storage declarations: engine match only (Inferred); several candidates are not guessed between.
        foreach (var store in context.Architecture?.DataStores ?? [])
        {
            if (!hasInfra) continue;
            // The server/account-level declaration of the engine (databases, containers, firewall rules … are sub-resources, not candidates).
            var engine = store.StoreType switch
            {
                DataStoreType.PostgreSql => @"^(azurerm_postgresql(_flexible)?_server|Microsoft\.DBforPostgreSQL/(flexibleServers|servers)|google_sql_database_instance)$",
                DataStoreType.SqlServer => @"^(azurerm_mssql_server|azurerm_sql_server|Microsoft\.Sql/servers)$",
                DataStoreType.Cosmos => @"^(azurerm_cosmosdb_account|Microsoft\.DocumentDB/databaseAccounts)$",
                DataStoreType.Redis => @"^(azurerm_redis_cache|Microsoft\.Cache/redis|aws_elasticache_(cluster|replication_group)|google_redis_instance)$",
                DataStoreType.BlobStorage => @"^(azurerm_storage_account|Microsoft\.Storage/storageAccounts|aws_s3_bucket|google_storage_bucket)$",
                _ => null,
            };
            if (engine is null) continue;
            var candidates = infra.Where(r => Regex.IsMatch(r.ResourceType, engine, RegexOptions.IgnoreCase)).ToList();
            if (candidates.Count == 1)
                Add(SourceEvidenceLinkType.ApplicationUsesDatastore, $"arch:datastore:{store.Id}", store.LogicalName, candidates[0].Id, $"{candidates[0].CategoryDetail} {candidates[0].DeclaredName ?? candidates[0].LogicalName}",
                    ArchitectureEvidenceState.Inferred, "The only declared resource of this datastore engine; not linked by name. Configured ≠ connected.");
            else Add(SourceEvidenceLinkType.ApplicationUsesDatastore, $"arch:datastore:{store.Id}", store.LogicalName, null, $"{store.StoreType}",
                ArchitectureEvidenceState.Unresolved, candidates.Count > 1 ? $"{candidates.Count} declarations of this engine; none identified by name." : UnresolvedBasis);
        }

        // Configuration ↔ infrastructure: normalized hosts (first label = declared name, matching service suffix) and exact entity names.
        foreach (var entry in context.Configuration?.Entries ?? [])
        {
            if (!hasInfra) break;
            foreach (var reference in entry.References)
            {
                var kind = reference[..reference.IndexOf(':')];
                var value = reference[(reference.IndexOf(':') + 1)..];
                List<InfrastructureResource> matches = [];
                var cloudHost = false;
                if (kind == "host" && InfrastructureIdentity.HostKind(value) is { } hostKind)
                {
                    // The shared host normalization: first label of a known service host = declared resource name of that kind.
                    cloudHost = true;
                    var label = InfrastructureIdentity.NormalizeName(value)!;
                    matches.AddRange(infra.Where(r => InfrastructureIdentity.IsKind(r, hostKind) && Named(r, label)));
                }
                else if (kind == "account") matches.AddRange(infra.Where(r => r.Category is InfrastructureCategory.Storage or InfrastructureCategory.Database && Named(r, value)));
                else if (kind == "entity") matches.AddRange(infra.Where(r => r.Category is InfrastructureCategory.Messaging or InfrastructureCategory.Storage && Named(r, value)));
                foreach (var m in matches.DistinctBy(m => m.Id))
                    Add(SourceEvidenceLinkType.ConfigurationReferencesInfrastructureResource, $"config:{entry.Id}", $"{entry.Key} ({entry.File})", m.Id, $"{m.CategoryDetail} {m.DeclaredName}",
                        ArchitectureEvidenceState.StronglySupported, kind == "host" ? "Configured host matches the declared resource name and service type." : "Configured entity name matches the declared resource name.");
                if (matches.Count == 0 && (cloudHost || (kind == "entity" && entry.Category == ConfigurationCategory.Messaging)))
                    Add(SourceEvidenceLinkType.ConfigurationReferencesInfrastructureResource, $"config:{entry.Id}", $"{entry.Key} ({entry.File})", null, value, ArchitectureEvidenceState.Unresolved, UnresolvedBasis);
            }
        }

        // Pipeline ↔ infrastructure: an IaC step naming a directory/template that holds declarations is Confirmed; an IaC step without a target is Inferred.
        foreach (var pipeline in context.CiCd?.Pipelines ?? [])
            foreach (var step in pipeline.Steps.Where(s => s.Kind is PipelineStepKind.InfrastructureDeploy or PipelineStepKind.InfrastructurePlan or PipelineStepKind.ApplicationDeploy
                && (s.Tool.Contains("terraform", StringComparison.OrdinalIgnoreCase) || s.Tool.Contains("Bicep", StringComparison.OrdinalIgnoreCase) || s.Tool.Contains("ARM", StringComparison.Ordinal) || s.Tool.Contains("helm", StringComparison.OrdinalIgnoreCase) || s.Tool.Contains("kubectl", StringComparison.OrdinalIgnoreCase) || s.Tool.Contains("Kubernetes", StringComparison.Ordinal))))
            {
                var targets = step.Targets.Concat(step.WorkingDirectory is { } wd ? [wd] : []).Select(t => t.Trim('/', '.').Replace('\\', '/')).Where(t => t.Length > 0).Distinct().ToList();
                var files = context.Infrastructure?.Resources.Select(r => r.File).Concat(context.Infrastructure.Modules.Select(m => m.File)).Distinct().ToList() ?? [];
                var matched = targets.Select(t => (Target: t, Files: files.Where(f => f.StartsWith(t + "/", StringComparison.OrdinalIgnoreCase) || f.Equals(t, StringComparison.OrdinalIgnoreCase) || f.Contains("/" + t + "/", StringComparison.OrdinalIgnoreCase) || f.EndsWith("/" + t, StringComparison.OrdinalIgnoreCase)).ToList()))
                    .Where(x => x.Files.Count > 0).ToList();
                if (matched.Count > 0)
                    foreach (var (target, _) in matched) Add(SourceEvidenceLinkType.PipelineDeploysInfrastructure, $"pipeline:{step.Id}", $"{pipeline.Name}: {step.Name}", $"infra:path:{target}", target,
                        ArchitectureEvidenceState.Confirmed, $"Pipeline step ({step.Tool}) targets this IaC path explicitly. Defined in pipeline; deployment not verified.");
                else if (hasInfra && targets.Count == 0)
                    Add(SourceEvidenceLinkType.PipelineDeploysInfrastructure, $"pipeline:{step.Id}", $"{pipeline.Name}: {step.Name}", "infra:path:.", "(IaC in this snapshot)", ArchitectureEvidenceState.Inferred,
                        $"Pipeline step ({step.Tool}) names no path; the snapshot holds IaC declarations.");
                else if (step.Kind is PipelineStepKind.InfrastructureDeploy or PipelineStepKind.InfrastructurePlan)
                    Add(SourceEvidenceLinkType.PipelineDeploysInfrastructure, $"pipeline:{step.Id}", $"{pipeline.Name}: {step.Name}", null, targets.FirstOrDefault() ?? "(no path)", ArchitectureEvidenceState.Unresolved, UnresolvedBasis);
            }

        // Pipeline ↔ tests: a test step naming a test project that exists in the snapshot.
        foreach (var pipeline in context.CiCd?.Pipelines ?? [])
            foreach (var step in pipeline.Steps.Where(s => s.Kind is PipelineStepKind.Test or PipelineStepKind.UnitTest or PipelineStepKind.IntegrationTest or PipelineStepKind.E2ETest or PipelineStepKind.FrontendTest or PipelineStepKind.AccessibilityTest))
                foreach (var target in step.Targets.Where(t => t.EndsWith("proj", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
                {
                    var full = step.WorkingDirectory is { } wd ? SourceArchitecture.ArchitectureInput.Normalize($"{wd}/{target}") : target;
                    var project = context.Input.Projects.FirstOrDefault(p => p.Path.Equals(full, StringComparison.OrdinalIgnoreCase) || p.Path.EndsWith("/" + target.TrimStart('.', '/'), StringComparison.OrdinalIgnoreCase));
                    Add(SourceEvidenceLinkType.PipelineRunsTests, $"pipeline:{step.Id}", $"{pipeline.Name}: {SourceDomainText.Label(step.Kind)}", project is null ? null : $"project:{project.Path}", project?.Name ?? target,
                        project is null ? ArchitectureEvidenceState.Unresolved : ArchitectureEvidenceState.Confirmed,
                        project is null ? "Test target not found in the selected source." : "Pipeline test step names this test project. The pipeline intends to run it; results are not read.");
                }

        // Component ↔ contract (producer / consumer).
        foreach (var contract in context.Contracts?.Contracts ?? [])
        {
            var components = context.Architecture?.Components ?? [];
            if (contract.Producer is { } producer && components.FirstOrDefault(c => c.Name == producer) is { } p)
                Add(SourceEvidenceLinkType.ContractProducedByComponent, $"contract:{contract.Id}", contract.Name, $"arch:component:{p.Id}", p.Name, contract.EvidenceState, contract.ProducerBasis ?? "");
            foreach (var consumer in contract.ConsumerHints)
                if (components.FirstOrDefault(c => c.Name == consumer) is { } c)
                    Add(SourceEvidenceLinkType.ContractConsumedByComponent, $"contract:{contract.Id}", contract.Name, $"arch:component:{c.Id}", c.Name, contract.EvidenceState, contract.ProducerBasis ?? "");
        }

        // Component ↔ telemetry: an exporter registered in code + an observability resource declared in IaC (only one candidate → Inferred chain).
        var sinks = infra.Where(r => r.Category == InfrastructureCategory.Observability && (r.ResourceType.Contains("application_insights", StringComparison.OrdinalIgnoreCase) || r.ResourceType.Contains("log_analytics", StringComparison.OrdinalIgnoreCase)
            || r.ResourceType.StartsWith("Microsoft.Insights/components", StringComparison.OrdinalIgnoreCase) || r.ResourceType.Contains("cloudwatch_log", StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var component in context.Observability?.Components.Where(c => c.TelemetryExporters.Count > 0) ?? [])
        {
            var azure = component.TelemetryExporters.Any(e => e.Contains("Azure Monitor", StringComparison.Ordinal) || e.Contains("Application Insights", StringComparison.Ordinal));
            // An Azure Monitor / Application Insights exporter targets an Application Insights resource (its Log Analytics workspace sits behind it).
            var insights = sinks.Where(s => s.ResourceType.Contains("application_insights", StringComparison.OrdinalIgnoreCase) || s.ResourceType.StartsWith("Microsoft.Insights/components", StringComparison.OrdinalIgnoreCase)).ToList();
            var candidates = azure && insights.Count > 0 ? insights : sinks;
            if (!hasInfra) continue;
            if (candidates.Count >= 1)
                Add(SourceEvidenceLinkType.TelemetryConfiguredForComponent, $"arch:component:{component.ComponentId}", component.Name, candidates[0].Id, $"{candidates[0].CategoryDetail} {candidates[0].DeclaredName ?? candidates[0].LogicalName}",
                    candidates.Count == 1 ? ArchitectureEvidenceState.Inferred : ArchitectureEvidenceState.Unresolved,
                    candidates.Count == 1 ? "Exporter registered in code and one telemetry sink declared in IaC; not linked by name. Telemetry delivery not assessed." : $"{candidates.Count} telemetry sinks declared; not linked by name.");
            else Add(SourceEvidenceLinkType.TelemetryConfiguredForComponent, $"arch:component:{component.ComponentId}", component.Name, null, string.Join(", ", component.TelemetryExporters.Take(2)), ArchitectureEvidenceState.Unresolved, UnresolvedBasis);
        }

        var mappings = Environments(context);
        context.CrossDomain = context.Envelope(new CrossDomainEvidence
        {
            Status = links.Count == 0 && mappings.Count == 0 ? SourceDomainStatus.NotDetected : links.Any(l => !l.Resolved) ? SourceDomainStatus.Partial : SourceDomainStatus.Complete,
            StatusReason = links.Count == 0 && mappings.Count == 0 ? "No relationships between source-evidence domains were found in the selected source." : null,
            Links = links, EnvironmentMappings = mappings, ObservabilityLayers = Layers(context), Limitations = limitations,
        }, SourceEvidenceDomain.CrossDomain, Version);
    }

    /// <summary>One row per environment kind across formats. Confirmed only when a pipeline step explicitly passes a var-file of that environment;
    /// two or more formats naming it → Inferred (same normalized name); one format only → Unresolved (nothing to map to).</summary>
    private static List<SourceEnvironmentMapping> Environments(SourceEvidenceContext context)
    {
        var config = (context.Configuration?.Files ?? []).Where(f => f.Environment.Kind != SourceEnvironmentKind.Default).Select(f => (f.Environment, f.Path)).ToList();
        var infra = (context.Infrastructure?.Environments ?? []).Where(e => e.Label.Kind != SourceEnvironmentKind.Default).SelectMany(e => e.Files.Select(f => (e.Label, File: f))).ToList();
        var pipeline = (context.CiCd?.Pipelines ?? []).SelectMany(p => p.Environments.Select(e => (Label: SourceFileClassifier.Environment(e), Raw: e))
            .Concat(p.IsTemplate ? [] : SourceFileClassifier.EnvironmentFromName(p.File.Split('/').Last()) is { } env ? [(env, $"{p.Name} (file name)")] : [])).ToList();
        var kinds = config.Select(c => c.Environment.Kind).Concat(infra.Select(i => i.Label.Kind)).Concat(pipeline.Select(p => p.Label.Kind))
            .Where(k => k is not (SourceEnvironmentKind.Default or SourceEnvironmentKind.Custom)).Distinct().Order().ToList();
        var result = new List<SourceEnvironmentMapping>();
        foreach (var kind in kinds)
        {
            var c = config.Where(x => x.Environment.Kind == kind).ToList();
            var i = infra.Where(x => x.Label.Kind == kind).ToList();
            var p = pipeline.Where(x => x.Label.Kind == kind).ToList();
            var formats = (c.Count > 0 ? 1 : 0) + (i.Count > 0 ? 1 : 0) + (p.Count > 0 ? 1 : 0);
            var explicitVarFile = (context.CiCd?.Pipelines ?? []).SelectMany(x => x.Steps).Any(s => s.Targets.Any(t => i.Any(f => f.File.EndsWith(t.TrimStart('.', '/'), StringComparison.OrdinalIgnoreCase) && t.Contains(".tfvars", StringComparison.OrdinalIgnoreCase))));
            result.Add(new SourceEnvironmentMapping
            {
                Kind = kind, RawLabels = c.Select(x => x.Environment.Raw).Concat(i.Select(x => x.Label.Raw)).Concat(p.Select(x => x.Label.Raw)).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ConfigurationFiles = c.Select(x => x.Path).Distinct().Take(20).ToList(), InfrastructureFiles = i.Select(x => x.File).Distinct().Take(20).ToList(),
                PipelineEnvironments = p.Select(x => x.Raw).Distinct().Take(20).ToList(),
                State = explicitVarFile ? ArchitectureEvidenceState.Confirmed : formats >= 2 ? ArchitectureEvidenceState.Inferred : ArchitectureEvidenceState.Unresolved,
                Basis = explicitVarFile ? "A pipeline step passes this environment's var-file explicitly." : formats >= 2 ? "Same normalized environment name in several formats; no explicit link between them." : "Named in one format only.",
            });
        }
        return result;
    }

    private static List<SourceObservabilityLayer> Layers(SourceEvidenceContext context)
    {
        var o = context.Observability;
        var code = o is null ? 0 : o.Components.Count(c => c.TracingTechnologies.Count > 0 || c.TelemetryExporters.Count > 0);
        var config = context.Configuration?.Entries.Count(e => e.Category == ConfigurationCategory.Observability) ?? 0;
        var infra = context.Infrastructure?.Resources.Count(r => r.Category == InfrastructureCategory.Observability) ?? 0;
        var pipeline = context.CiCd?.Pipelines.Sum(p => p.VariableNames.Count(v => Regex.IsMatch(v, @"(?i)(insights|otel|otlp|telemetry|loganalytics|monitor)"))) ?? 0;
        static string State(int n, bool analyzed) => !analyzed ? "Not analyzed" : n > 0 ? "Detected" : "Not detected in selected source";
        return
        [
            new("Application code", State(code, o is not null), o is null ? "Observability analysis not available for this snapshot." : $"{code} component(s) register tracing or a telemetry exporter in code.", code),
            new("Configuration", State(config, context.Configuration is not null), $"{config} observability configuration entr{(config == 1 ? "y" : "ies")} (exporters, sampling, service names, log levels, connection references).", config),
            new("Infrastructure", State(infra, context.Infrastructure is not null), $"{infra} observability resource declaration(s) (telemetry sinks, diagnostic settings, alerts, dashboards).", infra),
            new("CI/CD", State(pipeline, context.CiCd is not null), $"{pipeline} telemetry-related pipeline variable(s).", pipeline),
            new("Runtime telemetry", SourceDomainText.RuntimeNotAssessed, "Telemetry delivery is a runtime question; Source Analysis never claims it.", 0),
        ];
    }
}
