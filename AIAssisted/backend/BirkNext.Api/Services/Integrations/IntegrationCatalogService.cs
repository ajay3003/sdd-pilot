using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.IntegrationQuality;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Integrations;

/// <summary>
/// Target Environment → Integrations persistence: the configured, expected integrations Integration Quality Review runs
/// from. Owns configuration only — observed traffic stays with Endpoint Discovery, browser evidence with Browser Discovery.
/// </summary>
public interface IIntegrationCatalogService
{
    /// <summary>Reads the catalog of one environment. Never applies a project template: an environment only receives template records after
    /// <see cref="ApplyTemplateAsync"/>. An environment that already has a template applied receives that template's add-missing upgrades.
    /// Environment type and target URL only produce a template SUGGESTION.</summary>
    Task<IntegrationCatalog> GetAsync(string environmentId, string? environmentType, string? targetUrl, CancellationToken ct = default);
    /// <summary>
    /// Explicitly applies a project integration template (add-missing only; edited records are never overwritten). Null for an unknown
    /// template; <see cref="IntegrationTemplateApplyResult.Blocked"/> when the environment is not the one the template is for (enforced here).
    /// </summary>
    Task<IntegrationTemplateApplyResult?> ApplyTemplateAsync(string environmentId, string templateId, string? environmentType, string? targetUrl, CancellationToken ct = default);
    Task<IntegrationDefinition> CreateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default);
    Task<IntegrationDefinition?> UpdateAsync(string environmentId, string id, IntegrationDefinition definition, CancellationToken ct = default);
    Task<IntegrationDefinition?> SetEnabledAsync(string environmentId, string id, bool enabled, CancellationToken ct = default);
    Task<bool> DeleteAsync(string environmentId, string id, CancellationToken ct = default);
    Task<IntegrationPlatform?> UpdatePlatformAsync(string environmentId, string id, IntegrationPlatform platform, CancellationToken ct = default);
    /// <summary>One-time import of integrations previously stored in the browser Target Environment profile. Never overwrites a record.</summary>
    Task<int> ImportLegacyAsync(string environmentId, IReadOnlyList<IntegrationConfigDto> legacy, CancellationToken ct = default);
}

/// <summary>The outcome of applying a template: the catalog afterwards, or why the environment may not receive it (nothing was written).</summary>
public sealed record IntegrationTemplateApplyResult(IntegrationCatalog? Catalog, string? Blocked);

public sealed class IntegrationCatalogService(AppDbContext db, ILogger<IntegrationCatalogService> logger, TimeProvider? clock = null) : IIntegrationCatalogService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<IntegrationCatalog> GetAsync(string environmentId, string? environmentType, string? targetUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId)) return new IntegrationCatalog();
        var notices = new List<string>();
        // Only an environment where a person applied the M2LB template (or an existing workspace that already carries it) is upgraded.
        // A host that merely resembles M2LB never mutates the catalog: it only yields a suggestion below. Read-only checks (no environment
        // type) never upgrade either.
        var state = environmentType is null ? null : await db.IntegrationEnvironmentStates.AsNoTracking().FirstOrDefaultAsync(s => s.EnvironmentId == environmentId, ct);
        // Upgrades are DEV values too: only a Development environment that already carries the helper receives them (a renamed DEV host
        // still does; QA and Production never do).
        if (state is { SeedName: M2lbDevIntegrationSeed.Name, SeedVersion: < M2lbDevIntegrationSeed.Version } && M2lbDevIntegrationSeed.MayUpgrade(environmentType))
        {
            var added = await AttachSeedAsync(environmentId, ct);
            if (added > 0) notices.Add($"Updated the applied M2LB DEV template: {added} missing record{(added == 1 ? "" : "s")} added.");
        }
        return await ReadAsync(environmentId, notices, ct, M2lbDevIntegrationSeed.AppliesTo(environmentType, targetUrl));
    }

    public async Task<IntegrationTemplateApplyResult?> ApplyTemplateAsync(string environmentId, string templateId, string? environmentType, string? targetUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || !string.Equals(templateId, M2lbDevIntegrationSeed.Name, StringComparison.Ordinal)) return null;
        if (M2lbDevIntegrationSeed.ApplyBlock(environmentType, targetUrl) is { } blocked)
        {
            logger.LogWarning("Integration template {Template} refused for {EnvironmentId}: {Reason}", templateId, environmentId, blocked);
            return new IntegrationTemplateApplyResult(null, blocked);
        }
        var added = await AttachSeedAsync(environmentId, ct);
        logger.LogInformation("Integration template {Template} applied explicitly to {EnvironmentId}.", templateId, environmentId);
        return new IntegrationTemplateApplyResult(await ReadAsync(environmentId, [added > 0
            ? $"Applied the {M2lbDevIntegrationSeed.TemplateName}: {added} record{(added == 1 ? "" : "s")} added. Existing records were left unchanged."
            : $"The {M2lbDevIntegrationSeed.TemplateName} is already applied; nothing was changed."], ct, false), null);
    }

    /// <summary>Adds seed records whose stable id is missing. Existing records — edited or not — are left exactly as they are.</summary>
    private async Task<int> AttachSeedAsync(string environmentId, CancellationToken ct)
    {
        var state = await db.IntegrationEnvironmentStates.FindAsync([environmentId], ct);
        if (state is { SeedVersion: >= M2lbDevIntegrationSeed.Version, SeedName: M2lbDevIntegrationSeed.Name }) return 0;

        var now = _clock.GetUtcNow();
        var existingPlatforms = await db.IntegrationPlatforms.Where(p => p.EnvironmentId == environmentId).Select(p => p.Id).ToListAsync(ct);
        var existingIntegrations = await db.IntegrationDefinitions.Where(d => d.EnvironmentId == environmentId).Select(d => d.Id).ToListAsync(ct);
        var added = 0;
        // A v1 environment already had the Event Hub seed; it only gains what v2 adds (the Service Bus platform), so records a person
        // deleted since are not brought back.
        var upgrade = state is { SeedName: M2lbDevIntegrationSeed.Name, SeedVersion: >= 1 };
        if (!upgrade)
        {
            var platform = M2lbDevIntegrationSeed.Platform(environmentId, now);
            if (!existingPlatforms.Contains(platform.Id)) { db.IntegrationPlatforms.Add(ToRecord(platform)); added++; }
            foreach (var definition in M2lbDevIntegrationSeed.Integrations(environmentId, now).Where(d => !existingIntegrations.Contains(d.Id)))
            {
                db.IntegrationDefinitions.Add(ToRecord(definition));
                added++;
            }
        }
        // v2 added the Service Bus platform and v3 the SCIM identity provisioning platform: each is added once, never re-added after deletion
        // (an environment already at that version had it; if it is missing now, a person deleted it).
        var serviceBus = M2lbDevIntegrationSeed.ServiceBusPlatform(environmentId, now);
        if (state is not { SeedName: M2lbDevIntegrationSeed.Name, SeedVersion: >= 2 } && !existingPlatforms.Contains(serviceBus.Id)) { db.IntegrationPlatforms.Add(ToRecord(serviceBus)); added++; }
        var scim = M2lbDevIntegrationSeed.ScimPlatform(environmentId, now);
        if (state is not { SeedName: M2lbDevIntegrationSeed.Name, SeedVersion: >= 3 } && !existingPlatforms.Contains(scim.Id)) { db.IntegrationPlatforms.Add(ToRecord(scim)); added++; }
        // v5 normalizes the region label of unedited seeded Service Bus / SCIM platforms ("nwe" → "Norway East"); nothing else changes.
        if (upgrade && state is { SeedVersion: < 5 })
            foreach (var record in await db.IntegrationPlatforms.Where(p => p.EnvironmentId == environmentId && (p.Id == M2lbDevIntegrationSeed.ServiceBusPlatformId || p.Id == M2lbDevIntegrationSeed.ScimPlatformId)).ToListAsync(ct))
            {
                var current = FromRecord(record);
                var normalized = M2lbDevIntegrationSeed.WithCanonicalRegion(current);
                if (normalized != current) { record.DocumentJson = JsonSerializer.Serialize(normalized with { UpdatedAt = now }, Json); record.UpdatedAt = now; }
            }
        // v4 fills the verified Event Hub runtime-evidence defaults into an existing seeded platform: missing values only, never an
        // overwrite; a person-edited platform that already has runtime settings is left exactly as it is (see WithRuntimeDefaults).
        if (upgrade && state is { SeedVersion: < 4 }
            && await db.IntegrationPlatforms.FirstOrDefaultAsync(p => p.EnvironmentId == environmentId && p.Id == M2lbDevIntegrationSeed.PlatformId, ct) is { } eventHub)
        {
            var current = FromRecord(eventHub);
            var upgraded = M2lbDevIntegrationSeed.WithRuntimeDefaults(current);
            if (upgraded != current)
            {
                eventHub.DocumentJson = JsonSerializer.Serialize(upgraded with { UpdatedAt = now }, Json);
                eventHub.UpdatedAt = now;
                logger.LogInformation("Integration catalog seed v4 filled missing Event Hub runtime-evidence defaults on {PlatformId} for {EnvironmentId}.", eventHub.Id, environmentId);
            }
        }
        if (state is null) db.IntegrationEnvironmentStates.Add(new IntegrationEnvironmentStateRecord { EnvironmentId = environmentId, SeedVersion = M2lbDevIntegrationSeed.Version, SeedName = M2lbDevIntegrationSeed.Name, UpdatedAt = now });
        else { state.SeedVersion = M2lbDevIntegrationSeed.Version; state.SeedName = M2lbDevIntegrationSeed.Name; state.UpdatedAt = now; }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Integration catalog seed {Seed} v{Version} attached to environment {EnvironmentId}: {Added} record(s) added.", M2lbDevIntegrationSeed.Name, M2lbDevIntegrationSeed.Version, environmentId, added);
        return added;
    }

    private async Task<IntegrationCatalog> ReadAsync(string environmentId, List<string> notices, CancellationToken ct, bool suggestM2lb = false)
    {
        var state = await db.IntegrationEnvironmentStates.AsNoTracking().FirstOrDefaultAsync(s => s.EnvironmentId == environmentId, ct);
        var applied = state?.SeedName is { Length: > 0 } name ? name : null;
        var m2lbApplied = applied == M2lbDevIntegrationSeed.Name;
        var platforms = await db.IntegrationPlatforms.AsNoTracking().Where(p => p.EnvironmentId == environmentId).OrderBy(p => p.Name).ToListAsync(ct);
        var definitions = await db.IntegrationDefinitions.AsNoTracking().Where(d => d.EnvironmentId == environmentId).ToListAsync(ct);
        return new IntegrationCatalog
        {
            EnvironmentId = environmentId,
            Platforms = platforms.Select(FromRecord).ToList(),
            // Seeded records first, then everything else; each group by name.
            Integrations = definitions.Select(FromRecord)
                .OrderBy(d => d.Origin == IntegrationRecordOrigin.Seed ? 0 : 1)
                .ThenBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(),
            Notices = notices,
            AppliedTemplateId = applied,
            // Offered only to the environment it is for: QA and Production never see the DEV helper (the server refuses it as well).
            Templates = m2lbApplied || !suggestM2lb ? [] : [new IntegrationTemplateOffer(M2lbDevIntegrationSeed.Name, M2lbDevIntegrationSeed.TemplateName, M2lbDevIntegrationSeed.TemplateDescription,
                true, $"The target is the M2LB DEV frontend ({M2lbDevIntegrationSeed.FrontendHost}).") { Items = M2lbDevIntegrationSeed.Items() }],
            DomainExtensions = m2lbApplied ? [BirkNext.Technology.DomainExtensionIds.M2lbChildSecurityClassification] : [],
        };
    }

    public async Task<IntegrationDefinition> CreateAsync(string environmentId, IntegrationDefinition definition, CancellationToken ct = default)
    {
        var records = await db.IntegrationDefinitions.Where(d => d.EnvironmentId == environmentId).ToListAsync(ct);
        var existing = records.Select(d => d.Id).ToList();
        var id = string.IsNullOrWhiteSpace(definition.Id) ? UniqueId(environmentId, definition, existing) : definition.Id;
        if (existing.Contains(id)) throw new InvalidOperationException($"An integration with id '{id}' already exists.");
        // The same logical integration (kind + platform + channel) under another id is a duplicate, not a second integration.
        if (IntegrationIdentity.Key(definition) is { } key && records.Select(FromRecord).FirstOrDefault(d => IntegrationIdentity.Key(d) == key) is { } duplicate)
            throw new InvalidOperationException($"{duplicate.DisplayName} already configures the channel {definition.EndpointOrTopic}; edit it instead of adding a duplicate.");
        // A record added from a source suggestion keeps that origin; its mapping is never confirmed by being added (a person confirms later).
        var fromSource = definition.Origin == IntegrationRecordOrigin.SourceSuggestion;
        var consumer = fromSource && definition.Consumer.MappingState == ConsumerMappingState.Confirmed
            ? definition.Consumer with { MappingState = ConsumerMappingState.Suggested, MappingConfirmedAt = null } : definition.Consumer;
        var origin = fromSource ? IntegrationRecordOrigin.SourceSuggestion
            : definition.Origin == IntegrationRecordOrigin.Template && !string.IsNullOrWhiteSpace(definition.TemplateKey) ? IntegrationRecordOrigin.Template : IntegrationRecordOrigin.Manual;
        var created = definition with
        {
            // A template is setup guidance: the record keeps that origin and the template key, never a later template version's values.
            Id = id, EnvironmentId = environmentId, Consumer = consumer,
            Origin = origin, TemplateKey = origin == IntegrationRecordOrigin.Template ? definition.TemplateKey!.Trim() : null,
            UserModified = true, UpdatedAt = _clock.GetUtcNow(),
        };
        db.IntegrationDefinitions.Add(ToRecord(created));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Integration {IntegrationId} created in environment {EnvironmentId} (platform {PlatformId}).", id, environmentId, created.PlatformId);
        return created;
    }

    public async Task<IntegrationDefinition?> UpdateAsync(string environmentId, string id, IntegrationDefinition definition, CancellationToken ct = default)
    {
        var record = await db.IntegrationDefinitions.FindAsync([environmentId, id], ct);
        if (record is null) return null;
        var current = FromRecord(record);
        // Identity and provenance are not editable; everything else is the user's.
        var updated = definition with { Id = id, EnvironmentId = environmentId, Origin = current.Origin, TemplateKey = current.TemplateKey, UserModified = true, UpdatedAt = _clock.GetUtcNow() };
        Apply(record, updated);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Integration {IntegrationId} updated in environment {EnvironmentId} (platform {PlatformId}).", id, environmentId, updated.PlatformId);
        return updated;
    }

    public async Task<IntegrationDefinition?> SetEnabledAsync(string environmentId, string id, bool enabled, CancellationToken ct = default)
    {
        var record = await db.IntegrationDefinitions.FindAsync([environmentId, id], ct);
        if (record is null) return null;
        var updated = FromRecord(record) with { Enabled = enabled, UserModified = true, UpdatedAt = _clock.GetUtcNow() };
        Apply(record, updated);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Integration {IntegrationId} in environment {EnvironmentId} {State}.", id, environmentId, enabled ? "enabled" : "disabled");
        return updated;
    }

    public async Task<bool> DeleteAsync(string environmentId, string id, CancellationToken ct = default)
    {
        var record = await db.IntegrationDefinitions.FindAsync([environmentId, id], ct);
        if (record is null) return false;
        db.IntegrationDefinitions.Remove(record);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Integration {IntegrationId} deleted from environment {EnvironmentId}.", id, environmentId);
        return true;
    }

    public async Task<IntegrationPlatform?> UpdatePlatformAsync(string environmentId, string id, IntegrationPlatform platform, CancellationToken ct = default)
    {
        var record = await db.IntegrationPlatforms.FindAsync([environmentId, id], ct);
        if (record is null) return null;
        var current = FromRecord(record);
        var updated = platform with { Id = id, EnvironmentId = environmentId, Origin = current.Origin, UserModified = true, UpdatedAt = _clock.GetUtcNow() };
        record.Name = updated.Name;
        record.DocumentJson = JsonSerializer.Serialize(updated, Json);
        record.UserModified = true;
        record.UpdatedAt = updated.UpdatedAt;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Integration platform {PlatformId} updated in environment {EnvironmentId}.", id, environmentId);
        return updated;
    }

    public async Task<int> ImportLegacyAsync(string environmentId, IReadOnlyList<IntegrationConfigDto> legacy, CancellationToken ct = default)
    {
        var state = await db.IntegrationEnvironmentStates.FindAsync([environmentId], ct);
        if (state?.LegacyImportedAt is not null) return 0;
        var now = _clock.GetUtcNow();
        var existing = await db.IntegrationDefinitions.Where(d => d.EnvironmentId == environmentId).Select(d => d.Id).ToListAsync(ct);
        var imported = 0;
        foreach (var config in legacy)
        {
            var definition = FromLegacy(environmentId, config, now);
            if (existing.Contains(definition.Id)) continue;
            db.IntegrationDefinitions.Add(ToRecord(definition));
            existing.Add(definition.Id);
            imported++;
        }
        if (state is null) db.IntegrationEnvironmentStates.Add(new IntegrationEnvironmentStateRecord { EnvironmentId = environmentId, LegacyImportedAt = now, UpdatedAt = now });
        else { state.LegacyImportedAt = now; state.UpdatedAt = now; }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Imported {Imported} browser-stored integration(s) into environment {EnvironmentId}.", imported, environmentId);
        return imported;
    }

    /// <summary>
    /// Browser-profile integration → catalog record. The legacy "consumer" field held the consumer GROUP; a "$Default" that
    /// came from the old code-suggested defaults is dropped rather than carried as if someone had configured it.
    /// </summary>
    public static IntegrationDefinition FromLegacy(string environmentId, IntegrationConfigDto config, DateTimeOffset now)
    {
        var kind = config.Type switch
        {
            IntegrationType.EventHub => IntegrationKind.EventHub,
            IntegrationType.ServiceBus => IntegrationKind.ServiceBus,
            IntegrationType.REST or IntegrationType.GraphQL or IntegrationType.SOAP => IntegrationKind.HttpApi,
            IntegrationType.File => IntegrationKind.File,
            _ => IntegrationKind.Other,
        };
        var consumerGroup = config.ConfigurationSource == IntegrationConfigurationSource.CodeSuggested && config.Consumer == "$Default" ? null : config.Consumer;
        var auth = config.AuthType switch
        {
            IntegrationAuthType.ManagedIdentity => IntegrationAuthMechanism.ManagedIdentity,
            IntegrationAuthType.SasToken or IntegrationAuthType.ConnectionString => IntegrationAuthMechanism.Sas,
            IntegrationAuthType.ApiKey => IntegrationAuthMechanism.ApiKey,
            IntegrationAuthType.None => IntegrationAuthMechanism.NotConfigured,
            _ => IntegrationAuthMechanism.Other,
        };
        return new IntegrationDefinition
        {
            Id = string.IsNullOrWhiteSpace(config.Id) ? $"{environmentId}:{kind}:{Slug(config.Name)}".ToLowerInvariant() : $"imported:{config.Id}",
            EnvironmentId = environmentId, DisplayName = string.IsNullOrWhiteSpace(config.Name) ? "Imported integration" : config.Name, Kind = kind, Enabled = config.Enabled,
            EndpointOrTopic = kind == IntegrationKind.HttpApi ? config.Endpoint : config.Resource ?? config.Endpoint,
            ConsumerGroup = consumerGroup,
            Producer = config.LogicalProducerService,
            Consumer = new IntegrationConsumer
            {
                DisplayName = config.LogicalConsumerService,
                MappingState = string.IsNullOrWhiteSpace(config.LogicalConsumerService) ? ConsumerMappingState.NeedsConfirmation
                    : config.ConfigurationSource == IntegrationConfigurationSource.Manual ? ConsumerMappingState.Confirmed : ConsumerMappingState.Suggested,
                MappingSource = string.IsNullOrWhiteSpace(config.LogicalConsumerService) ? null : "Imported from the browser Target Environment profile",
            },
            ConsumerAuthentication = auth,
            ProducerContractReference = config.ProducerContractSourceLocation ?? config.ContractSourceLocation,
            ConsumerContractReference = config.ConsumerContractSourceLocation,
            ContractRelationship = !string.IsNullOrWhiteSpace(config.ProducerContractSourceLocation ?? config.ContractSourceLocation) && !string.IsNullOrWhiteSpace(config.ConsumerContractSourceLocation) ? ContractRelationshipState.BothContractsAvailable
                : !string.IsNullOrWhiteSpace(config.ProducerContractSourceLocation ?? config.ContractSourceLocation) ? ContractRelationshipState.ProducerContractAvailable
                : !string.IsNullOrWhiteSpace(config.ConsumerContractSourceLocation) ? ContractRelationshipState.ConsumerContractAvailable
                : ContractRelationshipState.NotConfigured,
            HealthUrl = config.HealthUrl, WorkerUrl = config.WorkerUrl, MonitoringUrl = config.MonitoringUrl, TechnicalOwner = config.Owner,
            Origin = IntegrationRecordOrigin.ImportedFromBrowserProfile, UpdatedAt = now,
        };
    }

    private static string UniqueId(string environmentId, IntegrationDefinition definition, List<string> existing)
    {
        var baseId = $"{environmentId}:{definition.Kind}:{Slug(definition.DisplayName)}".ToLowerInvariant();
        var id = baseId;
        for (var i = 2; existing.Contains(id); i++) id = $"{baseId}-{i}";
        return id;
    }

    private static string Slug(string? value) => Regex.Replace((value ?? "integration").Trim(), "[^A-Za-z0-9._-]+", "-").Trim('-') is { Length: > 0 } s ? s : "integration";

    public static IntegrationPlatform FromRecord(IntegrationPlatformRecord record) =>
        (JsonSerializer.Deserialize<IntegrationPlatform>(record.DocumentJson, Json) ?? new IntegrationPlatform()) with { Id = record.Id, EnvironmentId = record.EnvironmentId, UserModified = record.UserModified };

    public static IntegrationDefinition FromRecord(IntegrationDefinitionRecord record) =>
        (JsonSerializer.Deserialize<IntegrationDefinition>(record.DocumentJson, Json) ?? new IntegrationDefinition()) with
        { Id = record.Id, EnvironmentId = record.EnvironmentId, Enabled = record.Enabled, UserModified = record.UserModified };

    private static IntegrationPlatformRecord ToRecord(IntegrationPlatform platform) => new()
    {
        EnvironmentId = platform.EnvironmentId, Id = platform.Id, Name = platform.Name, DocumentJson = JsonSerializer.Serialize(platform, Json),
        UserModified = platform.UserModified, UpdatedAt = platform.UpdatedAt,
    };

    private static IntegrationDefinitionRecord ToRecord(IntegrationDefinition definition)
    {
        var record = new IntegrationDefinitionRecord { EnvironmentId = definition.EnvironmentId, Id = definition.Id };
        Apply(record, definition);
        return record;
    }

    private static void Apply(IntegrationDefinitionRecord record, IntegrationDefinition definition)
    {
        record.PlatformId = definition.PlatformId;
        record.DisplayName = definition.DisplayName;
        record.Enabled = definition.Enabled;
        record.DocumentJson = JsonSerializer.Serialize(definition, Json);
        record.UserModified = definition.UserModified;
        record.UpdatedAt = definition.UpdatedAt;
    }
}
