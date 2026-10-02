using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BirkNext.SourceArchitecture;

namespace BirkNext.SourceDomains;

// ── Infrastructure identity, environments and configured-vs-source comparison (shared by every consumer) ─────────────────
// One normalization of resource identity (kind, name, parent, host suffix) and of environment labels, so no consumer re-derives
// "-qa- means QA" or "x.servicebus.windows.net means namespace x" on its own. Comparison states are neutral: a difference is "Differs"
// (needs review), several declarations are "Multiple source candidates" (needs selection), nothing declared is "No source declaration"
// (may be managed elsewhere). Nothing here writes a configured value: a candidate is a suggestion a person may choose to use.
//   Declared (source) ≠ Configured (target/review settings) ≠ Observed (runtime) ≠ Verified (checked behaviour).

/// <summary>Provider-neutral resource kinds consumers match against (adapters map provider types onto them).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InfrastructureResourceKind
{
    Unknown, MessagingNamespace, EventHub, ConsumerGroup, ServiceBusTopic, ServiceBusQueue, ServiceBusSubscription, Queue, Topic, StorageAccount, BlobContainer,
    TelemetryComponent, LogWorkspace, DiagnosticSetting, Alert, ManagedIdentity, RoleAssignment, PrivateEndpoint, Network, DatabaseServer, Database, Cache, SecretStore,
    ComputeApp, ContainerRegistry, ApiGateway,
    /// <summary>Specific namespace kinds; <see cref="MessagingNamespace"/> (a *.servicebus.windows.net host) matches either.</summary>
    EventHubNamespace, ServiceBusNamespace,
}

/// <summary>A resource's identity as consumers compare it: provider, kind, type, name, parent (namespace/server/topic), environment, source id.</summary>
public sealed record InfrastructureResourceIdentity(string Provider, InfrastructureResourceKind Kind, string ResourceType, string? Name, string? Parent, SourceEnvironmentLabel? Environment, string SourceId);

public static class InfrastructureIdentity
{
    private static readonly (string Prefix, InfrastructureResourceKind Kind)[] Types =
    [
        ("azurerm_eventhub_namespace", InfrastructureResourceKind.EventHubNamespace), ("azurerm_eventhub_consumer_group", InfrastructureResourceKind.ConsumerGroup),
        ("azurerm_eventhub", InfrastructureResourceKind.EventHub), ("azurerm_servicebus_namespace", InfrastructureResourceKind.ServiceBusNamespace),
        ("azurerm_servicebus_topic", InfrastructureResourceKind.ServiceBusTopic), ("azurerm_servicebus_queue", InfrastructureResourceKind.ServiceBusQueue),
        ("azurerm_servicebus_subscription", InfrastructureResourceKind.ServiceBusSubscription), ("azurerm_storage_account", InfrastructureResourceKind.StorageAccount),
        ("azurerm_storage_container", InfrastructureResourceKind.BlobContainer), ("azurerm_application_insights", InfrastructureResourceKind.TelemetryComponent),
        ("azurerm_log_analytics_workspace", InfrastructureResourceKind.LogWorkspace), ("azurerm_monitor_diagnostic_setting", InfrastructureResourceKind.DiagnosticSetting),
        ("azurerm_monitor_metric_alert", InfrastructureResourceKind.Alert), ("azurerm_monitor_scheduled_query", InfrastructureResourceKind.Alert),
        ("azurerm_user_assigned_identity", InfrastructureResourceKind.ManagedIdentity), ("azurerm_role_assignment", InfrastructureResourceKind.RoleAssignment),
        ("azurerm_private_endpoint", InfrastructureResourceKind.PrivateEndpoint), ("azurerm_virtual_network", InfrastructureResourceKind.Network), ("azurerm_subnet", InfrastructureResourceKind.Network),
        ("azurerm_postgresql_flexible_server_database", InfrastructureResourceKind.Database), ("azurerm_postgresql", InfrastructureResourceKind.DatabaseServer),
        ("azurerm_mssql_database", InfrastructureResourceKind.Database), ("azurerm_mssql_server", InfrastructureResourceKind.DatabaseServer), ("azurerm_cosmosdb_account", InfrastructureResourceKind.DatabaseServer),
        ("azurerm_redis_cache", InfrastructureResourceKind.Cache), ("azurerm_key_vault", InfrastructureResourceKind.SecretStore),
        ("azurerm_linux_web_app", InfrastructureResourceKind.ComputeApp), ("azurerm_windows_web_app", InfrastructureResourceKind.ComputeApp), ("azurerm_container_app", InfrastructureResourceKind.ComputeApp),
        ("azurerm_linux_function_app", InfrastructureResourceKind.ComputeApp), ("azurerm_windows_function_app", InfrastructureResourceKind.ComputeApp),
        ("azurerm_container_registry", InfrastructureResourceKind.ContainerRegistry), ("azurerm_api_management", InfrastructureResourceKind.ApiGateway),
        ("aws_sqs_queue", InfrastructureResourceKind.Queue), ("aws_sns_topic", InfrastructureResourceKind.Topic), ("aws_s3_bucket", InfrastructureResourceKind.StorageAccount),
        ("aws_kinesis_stream", InfrastructureResourceKind.EventHub), ("aws_db_instance", InfrastructureResourceKind.DatabaseServer), ("aws_rds_cluster", InfrastructureResourceKind.DatabaseServer),
        ("aws_iam_role_policy_attachment", InfrastructureResourceKind.RoleAssignment), ("aws_cloudwatch_metric_alarm", InfrastructureResourceKind.Alert), ("aws_secretsmanager_secret", InfrastructureResourceKind.SecretStore),
        ("google_pubsub_topic", InfrastructureResourceKind.Topic), ("google_pubsub_subscription", InfrastructureResourceKind.ServiceBusSubscription), ("google_storage_bucket", InfrastructureResourceKind.StorageAccount),
        ("google_sql_database_instance", InfrastructureResourceKind.DatabaseServer), ("google_secret_manager_secret", InfrastructureResourceKind.SecretStore),
        ("Microsoft.EventHub/namespaces/eventhubs/consumergroups", InfrastructureResourceKind.ConsumerGroup), ("Microsoft.EventHub/namespaces/eventhubs", InfrastructureResourceKind.EventHub),
        ("Microsoft.EventHub/namespaces", InfrastructureResourceKind.EventHubNamespace), ("Microsoft.ServiceBus/namespaces/topics/subscriptions", InfrastructureResourceKind.ServiceBusSubscription),
        ("Microsoft.ServiceBus/namespaces/topics", InfrastructureResourceKind.ServiceBusTopic), ("Microsoft.ServiceBus/namespaces/queues", InfrastructureResourceKind.ServiceBusQueue),
        ("Microsoft.ServiceBus/namespaces", InfrastructureResourceKind.ServiceBusNamespace), ("Microsoft.Storage/storageAccounts/blobServices/containers", InfrastructureResourceKind.BlobContainer),
        ("Microsoft.Storage/storageAccounts", InfrastructureResourceKind.StorageAccount), ("Microsoft.Insights/components", InfrastructureResourceKind.TelemetryComponent),
        ("Microsoft.Insights/diagnosticSettings", InfrastructureResourceKind.DiagnosticSetting), ("Microsoft.OperationalInsights/workspaces", InfrastructureResourceKind.LogWorkspace),
        ("Microsoft.ManagedIdentity", InfrastructureResourceKind.ManagedIdentity), ("Microsoft.Authorization/roleAssignments", InfrastructureResourceKind.RoleAssignment),
        ("Microsoft.KeyVault/vaults", InfrastructureResourceKind.SecretStore), ("Microsoft.Network/privateEndpoints", InfrastructureResourceKind.PrivateEndpoint),
        ("Microsoft.App/containerApps", InfrastructureResourceKind.ComputeApp), ("Microsoft.Web/sites", InfrastructureResourceKind.ComputeApp),
    ];

    /// <summary>Host suffix → the kind whose declared name is the host's first label (the one host normalization every consumer uses).</summary>
    public static readonly (string Suffix, InfrastructureResourceKind Kind)[] HostSuffixes =
    [
        (".servicebus.windows.net", InfrastructureResourceKind.MessagingNamespace), (".blob.core.windows.net", InfrastructureResourceKind.StorageAccount),
        (".queue.core.windows.net", InfrastructureResourceKind.StorageAccount), (".table.core.windows.net", InfrastructureResourceKind.StorageAccount),
        (".dfs.core.windows.net", InfrastructureResourceKind.StorageAccount), (".database.windows.net", InfrastructureResourceKind.DatabaseServer),
        (".postgres.database.azure.com", InfrastructureResourceKind.DatabaseServer), (".mysql.database.azure.com", InfrastructureResourceKind.DatabaseServer),
        (".documents.azure.com", InfrastructureResourceKind.DatabaseServer), (".vault.azure.net", InfrastructureResourceKind.SecretStore),
        (".redis.cache.windows.net", InfrastructureResourceKind.Cache), (".azurecr.io", InfrastructureResourceKind.ContainerRegistry),
        (".azurewebsites.net", InfrastructureResourceKind.ComputeApp), (".azure-api.net", InfrastructureResourceKind.ApiGateway), (".s3.amazonaws.com", InfrastructureResourceKind.StorageAccount),
    ];

    public static InfrastructureResourceKind Kind(InfrastructureResource r) =>
        r.Format == InfrastructureFormat.Kubernetes ? InfrastructureResourceKind.Unknown
        : Types.Where(t => r.ResourceType.StartsWith(t.Prefix, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.Prefix.Length).Select(t => t.Kind).FirstOrDefault();

    /// <summary>Whether a resource is of a kind; the generic messaging namespace (from a host) matches Event Hubs and Service Bus namespaces.</summary>
    public static bool IsKind(InfrastructureResource r, InfrastructureResourceKind kind) => Kind(r) is var k && (k == kind
        || (kind == InfrastructureResourceKind.MessagingNamespace && k is InfrastructureResourceKind.EventHubNamespace or InfrastructureResourceKind.ServiceBusNamespace));

    public static InfrastructureResourceIdentity Identity(InfrastructureResource r) => new(r.Provider, Kind(r), r.ResourceType, r.DeclaredName, Parent(r), r.Environment, r.Id);

    /// <summary>The declared parent (namespace of a topic, topic of a subscription, hub of a consumer group) as a resource id, when statically referenced.</summary>
    public static string? Parent(InfrastructureResource r) =>
        r.Settings.FirstOrDefault(s => s.Key is "namespace_id" or "topic_id" or "eventhub_id" or "server_id" or "storage_account_id")?.Value is { } v && v.StartsWith("→ ", StringComparison.Ordinal) ? v[2..] : null;

    /// <summary>A configured value reduced to the comparable resource name: "https://acct.blob.core.windows.net/" → "acct",
    /// "ns.servicebus.windows.net" → "ns", "sb://ns.servicebus.windows.net/" → "ns". Case-insensitive.</summary>
    public static string? NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (Uri.TryCreate(v, UriKind.Absolute, out var uri) && uri.Host.Length > 0) v = uri.Host;
        v = v.TrimEnd('/', '.').ToLowerInvariant();
        foreach (var (suffix, _) in HostSuffixes)
            if (v.EndsWith(suffix, StringComparison.Ordinal)) return v[..^suffix.Length];
        return v;
    }

    public static InfrastructureResourceKind? HostKind(string host) =>
        HostSuffixes.Where(h => host.EndsWith(h.Suffix, StringComparison.OrdinalIgnoreCase)).Select(h => (InfrastructureResourceKind?)h.Kind).FirstOrDefault();
}

/// <summary>The one environment-label normalization (dev/development, qa/uat, prod/production …) every consumer shares; the raw label is preserved.</summary>
public static class SourceEnvironments
{
    public static SourceEnvironmentLabel Normalize(string raw)
    {
        var r = raw.Trim();
        var kind = r.ToLowerInvariant() switch
        {
            "" or "default" or "base" or "common" => SourceEnvironmentKind.Default,
            "dev" or "development" or "develop" => SourceEnvironmentKind.Development,
            "local" or "localhost" or "docker" => SourceEnvironmentKind.Local,
            "test" or "tst" or "testing" or "systest" or "sit" => SourceEnvironmentKind.Test,
            "qa" or "uat" or "acceptance" => SourceEnvironmentKind.QA,
            "stage" or "staging" or "stg" or "preprod" or "pre-prod" or "preproduction" => SourceEnvironmentKind.Staging,
            "prod" or "production" or "prd" or "live" => SourceEnvironmentKind.Production,
            _ => SourceEnvironmentKind.Custom,
        };
        return kind == SourceEnvironmentKind.Default ? SourceEnvironmentLabel.Default : new(kind, r);
    }

    /// <summary>The environment a free-form name (file name, target environment name) states by one of its tokens; null when none does.
    /// A name is a naming convention — callers treat a match as inferred, never confirmed.</summary>
    public static SourceEnvironmentLabel? FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var token in Regex.Split(name, @"[.\-_ /]").Reverse())
        {
            var env = Normalize(token);
            if (env.Kind is not (SourceEnvironmentKind.Default or SourceEnvironmentKind.Custom)) return env;
        }
        return null;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceComparisonState { NoConfiguredValue, Matches, Differs, MultipleSourceCandidates, NoSourceDeclaration, SourceUnavailable, Unresolved }

/// <summary>One declared resource offered as a source candidate (a suggestion, never a configured value). NameBasis says how the name and
/// environment were decided ("tfvars …" is explicit; "environment folder" is inferred; "declared name (defaults)" is not environment-specific).</summary>
public sealed record SourceResourceCandidate(string ResourceId, string Name, InfrastructureResourceKind Kind, string ResourceType, SourceEnvironmentLabel? Environment,
    string NameBasis, string File, int Line, ArchitectureEvidenceState EvidenceState);

/// <summary>A configured value compared with what the selected source snapshot declares. Read-only: nothing is saved by a comparison.</summary>
public sealed record SourceInfrastructureComparison
{
    public string Field { get; init; } = "";
    public string? ConfiguredValue { get; init; }
    public InfrastructureResourceKind Kind { get; init; }
    public SourceComparisonState State { get; init; }
    public SourceResourceCandidate? Match { get; init; }
    public List<SourceResourceCandidate> Candidates { get; init; } = [];
    public Guid? SourceSnapshotId { get; init; }
    public string? SourceFingerprint { get; init; }
    public int AnalyzerVersion { get; init; }
    /// <summary>How the target environment was matched to source environments.</summary>
    public string EnvironmentBasis { get; init; } = "";
    public string Detail { get; init; } = "";
    public string RuntimeState { get; init; } = "Runtime existence is assessed separately (Observed), never by this comparison.";
}

public static class SourceInfrastructureComparer
{
    public const string Boundary = "Configured values compared with infrastructure declared in the selected source snapshot. Declared ≠ deployed; nothing was changed from source.";
    public const string HelpText = "Declared in source means this resource or configuration was found in the selected Source Analysis snapshot. It does not verify that the resource exists in the deployed environment.";

    public static string Label(SourceComparisonState state) => state switch
    {
        SourceComparisonState.NoConfiguredValue => "Not configured — source suggestion available",
        SourceComparisonState.Matches => "Matches source",
        SourceComparisonState.Differs => "Source differs — needs review",
        SourceComparisonState.MultipleSourceCandidates => "Multiple source candidates — needs selection",
        SourceComparisonState.NoSourceDeclaration => "No source declaration",
        SourceComparisonState.SourceUnavailable => "Source evidence unavailable",
        _ => "Source name unresolved",
    };

    /// <summary>
    /// Candidates of one kind, scoped to an environment when possible: names from that environment's variable file first (explicit), then
    /// resources in that environment's folder (inferred), then declarations not tied to any environment. Never another environment's
    /// declaration by list order. A parent name (namespace for a topic) narrows by the declared parent when it is statically known.
    /// </summary>
    public static (List<SourceResourceCandidate> Candidates, string EnvironmentBasis) Candidates(InfrastructureEvidence evidence, InfrastructureResourceKind kind,
        SourceEnvironmentKind? environment, string? parent = null)
    {
        bool ParentMatches(InfrastructureResource r)
        {
            if (parent is null || InfrastructureIdentity.Parent(r) is not { } parentId || evidence.Resources.FirstOrDefault(x => x.Id == parentId) is not { } p) return true;
            var names = p.EnvironmentNames.Select(n => n.Name).Append(p.DeclaredName).OfType<string>();
            return !names.Any() || names.Any(n => string.Equals(n, InfrastructureIdentity.NormalizeName(parent), StringComparison.OrdinalIgnoreCase));
        }
        var resources = evidence.Resources.Where(r => r.Kind == "resource" && InfrastructureIdentity.IsKind(r, kind) && ParentMatches(r)).ToList();
        SourceResourceCandidate C(InfrastructureResource r, string? name, SourceEnvironmentLabel? env, string basis) =>
            new(r.Id, name ?? $"(unresolved: {r.LogicalName})", kind, r.ResourceType, env, basis, r.File, r.Line, name is null ? ArchitectureEvidenceState.Unresolved : r.EvidenceState);
        if (environment is { } target and not (SourceEnvironmentKind.Default or SourceEnvironmentKind.Custom))
        {
            var explicitNames = resources.SelectMany(r => r.EnvironmentNames.Where(n => n.Environment.Kind == target).Select(n => C(r, n.Name, n.Environment, n.Basis))).ToList();
            if (explicitNames.Count > 0) return (explicitNames, "explicit environment variable file");
            var folder = resources.Where(r => r.Environment?.Kind == target).Select(r => C(r, r.DeclaredName, r.Environment, "environment folder (inferred)")).ToList();
            if (folder.Count > 0) return (folder, "environment folder (inferred)");
            var neutral = resources.Where(r => r.Environment is null && r.EnvironmentNames.Count == 0).Select(r => C(r, r.DeclaredName, null, "declared name (not environment-specific)")).ToList();
            return (neutral, "not environment-specific");
        }
        return (resources.Select(r => C(r, r.DeclaredName, r.Environment, r.Environment is null ? "declared name (defaults)" : "environment folder (inferred)"))
            .Concat(resources.SelectMany(r => r.EnvironmentNames.Select(n => C(r, n.Name, n.Environment, n.Basis)))).DistinctBy(c => (c.ResourceId, c.Name)).ToList(), "no target environment");
    }

    public static SourceInfrastructureComparison Compare(string field, string? configured, InfrastructureEvidence? evidence, Guid? snapshotId, InfrastructureResourceKind kind,
        SourceEnvironmentKind? environment, string? parent = null)
    {
        var result = new SourceInfrastructureComparison
        {
            Field = field, ConfiguredValue = configured, Kind = kind, SourceSnapshotId = snapshotId, SourceFingerprint = evidence?.SourceFingerprint, AnalyzerVersion = evidence?.AnalyzerVersion ?? 0,
        };
        if (evidence is null || evidence.Status is not (SourceDomainStatus.Complete or SourceDomainStatus.Partial))
            return result with { State = SourceComparisonState.SourceUnavailable, Detail = evidence is null ? "No Source Analysis infrastructure evidence for the selected source." : $"Infrastructure analysis: {SourceDomainText.Label(evidence.Status)}." };
        var (candidates, basis) = Candidates(evidence, kind, environment, parent);
        return Evaluate(result with { Candidates = candidates, EnvironmentBasis = basis });
    }

    /// <summary>The comparison state of a configured value against already-scoped candidates (used again when a person edits the value).</summary>
    public static SourceInfrastructureComparison Evaluate(SourceInfrastructureComparison result)
    {
        if (result.State == SourceComparisonState.SourceUnavailable) return result;
        var candidates = result.Candidates;
        result = result with { Match = null };
        if (candidates.Count == 0) return result with { State = SourceComparisonState.NoSourceDeclaration, Detail = "No matching declaration in the selected source — it may be managed elsewhere." };
        var configured = result.ConfiguredValue;
        var resolved = candidates.Where(c => c.EvidenceState != ArchitectureEvidenceState.Unresolved).ToList();
        var wanted = InfrastructureIdentity.NormalizeName(configured);
        if (wanted is null)
            return result with
            {
                State = resolved.Count == 0 ? SourceComparisonState.Unresolved : resolved.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 ? SourceComparisonState.MultipleSourceCandidates : SourceComparisonState.NoConfiguredValue,
                Detail = "Nothing is configured; source declarations are offered as suggestions only.",
            };
        var match = resolved.FirstOrDefault(c => string.Equals(InfrastructureIdentity.NormalizeName(c.Name), wanted, StringComparison.Ordinal));
        if (match is not null) return result with { State = SourceComparisonState.Matches, Match = match, Detail = $"Configured value matches the {match.ResourceType} declared in {match.File}." };
        if (resolved.Count == 0) return result with { State = SourceComparisonState.Unresolved, Detail = "Declared names could not be resolved statically, so the configured value cannot be compared." };
        return result with { State = SourceComparisonState.Differs, Detail = $"Configured value is not among {resolved.Count} declared name(s); review which is intended. Not a failure." };
    }
}

/// <summary>One configured value of a consumer's subject (integration platform, target environment field …) compared with source evidence.
/// Stored with the consumer's result, so the comparison keeps the exact snapshot, fingerprint and analyzer version it was made with.</summary>
public sealed record ConfiguredSourceComparison(string SubjectId, string SubjectName, string? ItemId, SourceInfrastructureComparison Comparison);

/// <summary>Source suggestions for one configured field (read-only): the comparison plus which snapshot it came from. A UI may offer
/// "Use detected value"; only an explicit save by a person turns a suggestion into a configured value.</summary>
public sealed record SourceInfrastructureSuggestion
{
    public bool SourceAnalysisEnabled { get; init; } = true;
    public SourceInfrastructureComparison Comparison { get; init; } = new();
    public Guid? SnapshotId { get; init; }
    public string? ArchiveName { get; init; }
    public string? Fingerprint { get; init; }
    public DateTimeOffset? AnalyzedAt { get; init; }
    /// <summary>How the snapshot was chosen ("selected snapshot", "newest snapshot with infrastructure evidence").</summary>
    public string? SnapshotBasis { get; init; }
}
