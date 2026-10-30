using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ExternalIntegrations;

/// <summary>
/// Target Environment → Connected Systems: the environment-specific external systems (provider instances). The Integration Catalog keeps
/// the logical integrations (producer → consumer) reviewed in IQR; an external integration is the system configuration they run against and is
/// referenced, never copied. Viewing and detecting never write; create/update/delete are explicit. Altinn is surfaced from its typed Target
/// Environment settings even before an integration record exists (one Altinn system per environment).
/// </summary>
public sealed partial class ExternalIntegrationService(AppDbContext db, IEnumerable<IExternalProviderHandler> handlers, IIntegrationCatalogService catalog,
    AltinnSessionStore session, AltinnEnvironmentSettingsCache altinnSettings, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const string AltinnSuffix = ":altinn";

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSlug();

    public IExternalProviderHandler Handler(ExternalProviderType type) =>
        handlers.FirstOrDefault(handler => handler.Type == type) ?? new GenericProviderHandler(type, catalog, session);

    public async Task<IReadOnlyList<ExternalIntegration>> ListAsync(string environmentId, CancellationToken ct)
    {
        var rows = await db.ExternalIntegrations.AsNoTracking().Where(row => row.EnvironmentId == environmentId).ToListAsync(ct);
        var list = rows.Select(row => JsonSerializer.Deserialize<ExternalIntegration>(row.DocumentJson, Json)).OfType<ExternalIntegration>().ToList();
        // Altinn configured on the Target Environment before External Integrations existed is the environment's Altinn system.
        if (!list.Any(item => item.ProviderType == ExternalProviderType.Altinn) && altinnSettings.Get(environmentId) is { } saved
            && (saved.AppIdentifier is not null || saved.Unattended.ClientId is not null))
            list.Add(new ExternalIntegration
            {
                Id = environmentId + AltinnSuffix, TargetEnvironmentId = environmentId, Name = $"Altinn – {saved.AppIdentifier ?? "app"}", ProviderType = ExternalProviderType.Altinn,
                EnvironmentName = saved.Environment?.ToString(), Provenance = new() { ["*"] = "Configured in Target Environment → Connected Systems → Altinn" },
                CreatedAt = saved.UpdatedAt ?? clock.GetUtcNow(), UpdatedAt = saved.UpdatedAt ?? clock.GetUtcNow(),
            });
        return [.. list.OrderBy(item => item.ProviderType).ThenBy(item => item.Name, StringComparer.Ordinal)];
    }

    public async Task<ExternalIntegration?> GetAsync(string environmentId, string id, CancellationToken ct) =>
        (await ListAsync(environmentId, ct)).FirstOrDefault(item => item.Id == id);

    public async Task<ExternalIntegrationsView> ViewAsync(string environmentId, bool detect, CancellationToken ct)
    {
        var list = await ListAsync(environmentId, ct);
        var views = new List<ExternalIntegrationView>();
        foreach (var item in list) views.Add(await Handler(item.ProviderType).AssessAsync(item, detect, ct));
        var proposals = new List<ExternalIntegrationProposal>();
        if (detect) foreach (var handler in handlers) proposals.AddRange(await handler.ProposeAsync(environmentId, list, ct));
        return new(environmentId, views, proposals, ExternalProviderCatalog.Descriptors);
    }

    public async Task<ExternalIntegrationView?> ViewOneAsync(string environmentId, string id, bool detect, CancellationToken ct) =>
        await GetAsync(environmentId, id, ct) is { } item ? await Handler(item.ProviderType).AssessAsync(item, detect, ct) : null;

    /// <summary>Creates or updates an integration after its provider validated every setting. Returns the problems instead of saving.</summary>
    public async Task<(ExternalIntegration? Integration, IReadOnlyList<string> Problems)> SaveAsync(string environmentId, string? id, ExternalIntegrationRequest request,
        string provenance, CancellationToken ct)
    {
        if (!AltinnEnvironmentSettingsStore.ValidEnvironmentId(environmentId)) return (null, ["A valid Target Environment id is required."]);
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 120 || ProviderFieldValidation.LooksLikeCredential(name)) return (null, ["A name (at most 120 characters) is required."]);
        var handler = Handler(request.ProviderType);
        var settings = (request.Settings ?? []).Where(entry => !string.IsNullOrWhiteSpace(entry.Value)).ToDictionary(entry => entry.Key, entry => entry.Value.Trim(), StringComparer.Ordinal);
        var problems = handler.Validate(settings).ToList();
        var existing = await ListAsync(environmentId, ct);
        var current = id is null ? null : existing.FirstOrDefault(item => item.Id == id);
        if (id is not null && current is null) return (null, ["The integration does not exist in this environment."]);
        if (current is not null && current.ProviderType != request.ProviderType) problems.Add("The provider of an existing integration cannot change; add a new integration instead.");
        var descriptor = ExternalProviderCatalog.Descriptor(request.ProviderType);
        if (current is null && existing.Count(item => item.ProviderType == request.ProviderType) >= descriptor.MaxPerEnvironment)
            problems.Add($"This environment already has {descriptor.MaxPerEnvironment} {descriptor.DisplayName} integration(s).");
        if (problems.Count > 0) return (null, problems);

        var now = clock.GetUtcNow();
        var integrationId = current?.Id ?? (request.ProviderType == ExternalProviderType.Altinn ? environmentId + AltinnSuffix
            : $"{environmentId}:{request.ProviderType.ToString().ToLowerInvariant()}:{NonSlug().Replace(name.ToLowerInvariant(), "-").Trim('-')}");
        if (current is null && existing.Any(item => item.Id == integrationId)) return (null, ["An integration with this name already exists."]);
        var provenanceMap = new Dictionary<string, string>(current?.Provenance ?? [], StringComparer.Ordinal);
        var before = current is null ? [] : await handler.CurrentSettingsAsync(current, ct);
        foreach (var (key, value) in settings) if (before.GetValueOrDefault(key) != value) provenanceMap[key] = provenance;
        // Provider-typed settings (Altinn) live in their own record; the generic document keeps the rest.
        var typed = request.ProviderType == ExternalProviderType.Altinn;
        var integration = new ExternalIntegration
        {
            Id = integrationId, TargetEnvironmentId = environmentId, Name = name, ProviderType = request.ProviderType, EnvironmentName = request.EnvironmentName?.Trim(),
            Enabled = request.Enabled, Settings = typed ? [] : settings, Provenance = provenanceMap, EvidenceReferences = current?.EvidenceReferences ?? [],
            // Test Data bindings are owned by the Test Data section; a settings save never drops or changes them.
            TestDataBindings = current?.TestDataBindings,
            CreatedAt = current?.CreatedAt ?? now, UpdatedAt = now,
        };
        if (handler is not GenericProviderHandler && await handler.ApplyAsync(integration, settings, ct) is { Count: > 0 } applyProblems) return (null, applyProblems);
        await PersistAsync(integration, ct);
        return (integration, []);
    }

    /// <summary>Stores the generic document (in the caller's unit of work when one is open).</summary>
    public async Task PersistAsync(ExternalIntegration integration, CancellationToken ct, bool save = true)
    {
        var json = JsonSerializer.Serialize(integration with { Settings = integration.Settings.Where(entry => !IsSecretKey(integration.ProviderType, entry.Key)).ToDictionary() }, Json);
        var row = await db.ExternalIntegrations.SingleOrDefaultAsync(r => r.Id == integration.Id, ct);
        if (row is null) db.ExternalIntegrations.Add(new ExternalIntegrationRecord { Id = integration.Id, EnvironmentId = integration.TargetEnvironmentId, DocumentJson = json, UpdatedAt = integration.UpdatedAt });
        else { row.DocumentJson = json; row.UpdatedAt = integration.UpdatedAt; }
        if (save) await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// After a rolled-back unit of work: forget tracked entities and reload the environment's Altinn settings from the database, so the
    /// execution policy never reads a value that was never committed.
    /// </summary>
    public async Task ResyncAfterRollbackAsync(string environmentId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        altinnSettings.Remove(environmentId);
        var row = await db.AltinnEnvironmentSettings.AsNoTracking().SingleOrDefaultAsync(r => r.EnvironmentId == environmentId, ct);
        if (row is not null && JsonSerializer.Deserialize<AltinnEnvironmentSettings>(row.DocumentJson, Json) is { } saved) altinnSettings.Set(environmentId, saved);
    }

    public async Task<bool> DeleteAsync(string environmentId, string id, CancellationToken ct)
    {
        var row = await db.ExternalIntegrations.SingleOrDefaultAsync(r => r.Id == id && r.EnvironmentId == environmentId, ct);
        if (row is null) return false;
        db.ExternalIntegrations.Remove(row);
        await db.SaveChangesAsync(ct);
        session.ClearSecret($"integration:{id}");
        return true;
    }

    /// <summary>A generic provider's secret for this session (memory only). Altinn uses its environment secret.</summary>
    public string? SetSecret(ExternalIntegration integration, string? secret) =>
        integration.ProviderType == ExternalProviderType.Altinn ? session.SetSecret(integration.TargetEnvironmentId, secret) : session.SetSecret(GenericProviderHandler.SecretKey(integration), secret);

    private static bool IsSecretKey(ExternalProviderType type, string key) =>
        ExternalProviderCatalog.Descriptor(type).Fields.Any(field => field.Secret && field.Key == key);
}
