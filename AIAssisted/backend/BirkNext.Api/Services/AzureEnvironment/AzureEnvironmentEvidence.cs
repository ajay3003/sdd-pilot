using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.AzureEnvironment;
using BirkNext.SourceDomains;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.AzureEnvironment;

/// <summary>Immutable Azure snapshots per Target Environment. A snapshot is stored as one JSON document; it never contains a token.</summary>
public sealed class AzureEnvironmentSnapshotStore(AppDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task SaveAsync(AzureEnvironmentSnapshot snapshot, CancellationToken ct)
    {
        db.AzureEnvironmentSnapshots.Add(new AzureEnvironmentSnapshotRecord
        {
            Id = snapshot.Id, EnvironmentId = snapshot.EnvironmentId, CapturedAt = snapshot.CapturedAt, Status = snapshot.Status.ToString(),
            SummaryJson = JsonSerializer.Serialize(Summary(snapshot), Json), SnapshotJson = JsonSerializer.Serialize(snapshot, Json),
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AzureEnvironmentSnapshotSummary>> ListAsync(string environmentId, CancellationToken ct) =>
        (await db.AzureEnvironmentSnapshots.AsNoTracking().Where(r => r.EnvironmentId == environmentId).OrderByDescending(r => r.CapturedAt).Select(r => r.SummaryJson).ToListAsync(ct))
        .Select(j => JsonSerializer.Deserialize<AzureEnvironmentSnapshotSummary>(j, Json)!).ToList();

    public async Task<AzureEnvironmentSnapshot?> GetAsync(string environmentId, Guid id, CancellationToken ct) =>
        await db.AzureEnvironmentSnapshots.AsNoTracking().Where(r => r.Id == id && r.EnvironmentId == environmentId).Select(r => r.SnapshotJson).FirstOrDefaultAsync(ct) is { } json
            ? JsonSerializer.Deserialize<AzureEnvironmentSnapshot>(json, Json) : null;

    public async Task<AzureEnvironmentSnapshot?> LatestAsync(string environmentId, CancellationToken ct) =>
        await db.AzureEnvironmentSnapshots.AsNoTracking().Where(r => r.EnvironmentId == environmentId).OrderByDescending(r => r.CapturedAt).Select(r => r.SnapshotJson).FirstOrDefaultAsync(ct) is { } json
            ? JsonSerializer.Deserialize<AzureEnvironmentSnapshot>(json, Json) : null;

    public static AzureEnvironmentSnapshotSummary Summary(AzureEnvironmentSnapshot s) =>
        new(s.Id, s.EnvironmentId, s.CapturedAt, s.Status, s.Subscriptions.Select(x => x.DisplayName.Length > 0 ? x.DisplayName : x.Id).ToList(), s.Resources.Count, s.Relationships.Count,
            s.Scope.Environment?.Raw);
}

/// <summary>
/// The shared OBSERVED-evidence provider: what reviews (IQR, Security, Observability, Target Environment) read about the deployed Azure
/// environment, from an exact snapshot or the newest one of the Target Environment (stated in the result). Pure reads over stored snapshots —
/// nothing here calls Azure, and nothing writes a configured value.
/// </summary>
public interface IAzureEnvironmentEvidenceProvider
{
    bool Enabled { get; }
    Task<IReadOnlyList<AzureEnvironmentSnapshotSummary>> ListAsync(string environmentId, CancellationToken ct = default);
    Task<AzureEnvironmentSnapshot?> ResolveAsync(string environmentId, Guid? snapshotId, CancellationToken ct = default);
    Task<ObservedResourceLookup> LookupAsync(string environmentId, InfrastructureResourceKind kind, string? nameOrHost, string? parent, Guid? snapshotId, CancellationToken ct = default);
    Task<IReadOnlyList<AzureTargetSuggestion>> TargetSuggestionsAsync(string environmentId, Guid? snapshotId, CancellationToken ct = default);
}

public sealed class AzureEnvironmentEvidenceProvider(AzureEnvironmentSnapshotStore store, bool enabled = true) : IAzureEnvironmentEvidenceProvider
{
    public const string Disabled = "Azure Environment Analysis is disabled in Feature Visibility; observed Azure evidence is not offered. Stored snapshots are kept.";

    public static AzureEnvironmentEvidenceProvider FromConfiguration(AzureEnvironmentSnapshotStore store, IConfiguration configuration) =>
        new(store, configuration.GetSection("FeatureVisibility").GetValue("AzureEnvironmentAnalysis", true));

    public bool Enabled => enabled;

    public async Task<IReadOnlyList<AzureEnvironmentSnapshotSummary>> ListAsync(string environmentId, CancellationToken ct = default) =>
        enabled ? await store.ListAsync(environmentId, ct) : [];

    /// <summary>Exactly the requested snapshot (never substituted), or the newest of the environment when none is requested.</summary>
    public async Task<AzureEnvironmentSnapshot?> ResolveAsync(string environmentId, Guid? snapshotId, CancellationToken ct = default) =>
        !enabled ? null : snapshotId is { } id ? await store.GetAsync(environmentId, id, ct) : await store.LatestAsync(environmentId, ct);

    public async Task<ObservedResourceLookup> LookupAsync(string environmentId, InfrastructureResourceKind kind, string? nameOrHost, string? parent, Guid? snapshotId, CancellationToken ct = default)
    {
        if (!enabled) return new() { State = ObservedLookupState.Disabled, Kind = kind, Name = nameOrHost, Detail = Disabled };
        var snapshot = await ResolveAsync(environmentId, snapshotId, ct);
        return Lookup(snapshot, kind, nameOrHost, parent, snapshotId);
    }

    public static ObservedResourceLookup Lookup(AzureEnvironmentSnapshot? snapshot, InfrastructureResourceKind kind, string? nameOrHost, string? parent, Guid? requested = null)
    {
        var name = InfrastructureIdentity.NormalizeName(nameOrHost);
        var lookup = new ObservedResourceLookup { Kind = kind, Name = name, SnapshotId = snapshot?.Id, CapturedAt = snapshot?.CapturedAt };
        if (snapshot is null)
            return lookup with { State = ObservedLookupState.NoSnapshot, Detail = requested is null ? "No Azure Environment Analysis snapshot exists for this Target Environment." : "The selected Azure snapshot is unavailable; nothing is substituted." };
        if (name is null) return lookup with { State = ObservedLookupState.UnableToVerify, Detail = "No configured value to look up." };
        var parentName = InfrastructureIdentity.NormalizeName(parent);
        var matches = snapshot.Resources.Where(r => InfrastructureIdentity.Compatible(r.ResourceKind, kind) && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)
            && (parentName is null || r.ParentId is null || string.Equals(AzureIds.Name(r.ParentId), parentName, StringComparison.OrdinalIgnoreCase))).ToList();
        if (matches.Count > 1) return lookup with { State = ObservedLookupState.MultipleObserved, Detail = $"{matches.Count} observed resources have this name; the configured value does not say which." };
        if (matches.Count == 1)
        {
            var r = matches[0];
            return lookup with
            {
                State = ObservedLookupState.Observed, Resource = r, Observations = snapshot.Observations.Where(o => AzureIds.Same(o.ResourceId, r.Id)).ToList(),
                Relationships = snapshot.Relationships.Where(x => AzureIds.Same(x.FromId, r.Id) || AzureIds.Same(x.ToId, r.Id)).ToList(),
                Detail = $"Observed in Azure ({r.CategoryDetail}, {r.ResourceGroup}). {AzureEnvironmentText.ObservedNotVerified}.",
            };
        }
        var area = AzureEnvironmentCollector.AreaOf(snapshot.Resources.FirstOrDefault(r => r.ResourceKind == kind)?.Category ?? CategoryOf(kind));
        var blocked = snapshot.Capabilities.Any(c => (c.Area == area || c.Area == AzureCapabilityArea.ResourceInventory)
            && c.State is AzureCapabilityState.NotAuthorized or AzureCapabilityState.Failed or AzureCapabilityState.Partial or AzureCapabilityState.Throttled);
        return lookup with
        {
            State = blocked ? ObservedLookupState.UnableToVerify : ObservedLookupState.NotObserved,
            Detail = blocked ? $"Not observed, but {AzureEnvironmentText.Label(area)} was not fully readable in that snapshot." : "Not observed in the analyzed Azure scope (it may be outside the selected subscriptions or resource groups).",
        };
    }

    private static InfrastructureCategory CategoryOf(InfrastructureResourceKind kind) => kind switch
    {
        InfrastructureResourceKind.MessagingNamespace or InfrastructureResourceKind.EventHubNamespace or InfrastructureResourceKind.ServiceBusNamespace or InfrastructureResourceKind.EventHub
            or InfrastructureResourceKind.ConsumerGroup or InfrastructureResourceKind.ServiceBusTopic or InfrastructureResourceKind.ServiceBusQueue or InfrastructureResourceKind.ServiceBusSubscription => InfrastructureCategory.Messaging,
        InfrastructureResourceKind.StorageAccount or InfrastructureResourceKind.BlobContainer => InfrastructureCategory.Storage,
        InfrastructureResourceKind.DatabaseServer or InfrastructureResourceKind.Database => InfrastructureCategory.Database,
        InfrastructureResourceKind.TelemetryComponent or InfrastructureResourceKind.LogWorkspace or InfrastructureResourceKind.Alert => InfrastructureCategory.Observability,
        InfrastructureResourceKind.SecretStore => InfrastructureCategory.SecretStore,
        InfrastructureResourceKind.ManagedIdentity => InfrastructureCategory.Identity,
        InfrastructureResourceKind.ComputeApp => InfrastructureCategory.Compute,
        InfrastructureResourceKind.Network or InfrastructureResourceKind.PrivateEndpoint => InfrastructureCategory.Networking,
        _ => InfrastructureCategory.Other,
    };

    public async Task<IReadOnlyList<AzureTargetSuggestion>> TargetSuggestionsAsync(string environmentId, Guid? snapshotId, CancellationToken ct = default) =>
        await ResolveAsync(environmentId, snapshotId, ct) is { } snapshot ? TargetSuggestions(snapshot) : [];

    /// <summary>Observed values a person may copy into a Target Environment (application URLs, messaging and telemetry endpoints). Never saved here.</summary>
    public static IReadOnlyList<AzureTargetSuggestion> TargetSuggestions(AzureEnvironmentSnapshot snapshot)
    {
        var basis = $"Observed in the Azure snapshot of {snapshot.CapturedAt:yyyy-MM-dd HH:mm} UTC. Not saved: copy it into the Target Environment yourself if it is right.";
        string? P(ObservedResource r, string key) => r.Properties.FirstOrDefault(p => p.Key == key)?.Value;
        var list = new List<AzureTargetSuggestion>();
        foreach (var r in snapshot.Resources.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (r.ResourceKind == InfrastructureResourceKind.ComputeApp && (P(r, "defaultHostName") ?? P(r, "fqdn")) is { } host)
                list.Add(new("Application URL", $"https://{host}", r.Id, r.Name, basis));
            if (r.Type is "microsoft.eventhub/namespaces" or "microsoft.servicebus/namespaces" && P(r, "endpoint") is { } endpoint && Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
                list.Add(new(r.Type == "microsoft.eventhub/namespaces" ? "Event Hubs namespace host" : "Service Bus namespace host", uri.Host, r.Id, r.Name, basis));
            if (r.ResourceKind == InfrastructureResourceKind.TelemetryComponent) list.Add(new("Application Insights component", r.Name, r.Id, r.Name, basis));
        }
        return list.Take(100).ToList();
    }
}
