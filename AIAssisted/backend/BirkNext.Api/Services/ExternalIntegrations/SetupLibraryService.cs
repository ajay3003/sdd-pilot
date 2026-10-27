using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ExternalIntegrations;

/// <summary>
/// Setup Library: reusable, sanitized environment setup packages for any provider. A package is configuration intent, never the source of
/// truth; versions are immutable (a new version never mutates an earlier payload); applying always re-runs the destination preflight through
/// the same pipeline as a file import. The stored payload is re-serialized from the parsed, validated model, so only accepted portable values
/// are ever kept — never a secret, raw identity, trust or runtime evidence. Archive instead of delete.
/// </summary>
public sealed class SetupLibraryService(AppDbContext db, EnvironmentSetupService setup, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record ItemDocument(string? Description, SetupScope SetupType, string? EnvironmentType, string? SourceEnvironmentId,
        List<ExternalProviderType> Providers, List<string> TestPacks, string? LastPreflight, List<string> Sanitization);

    public async Task<IReadOnlyList<SetupLibraryItemView>> ListAsync(ExternalProviderType? provider, string? environmentType, string? testPack, SetupScope? setupType,
        SetupLibraryStatus? status, CancellationToken ct)
    {
        var items = new List<SetupLibraryItemView>();
        foreach (var row in await db.SetupLibraryItems.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct)) items.Add(await ViewAsync(row, ct));
        return [.. items.Where(item => (provider is null || item.Providers.Contains(provider.Value))
            && (environmentType is null || string.Equals(item.EnvironmentType, environmentType, StringComparison.OrdinalIgnoreCase))
            && (testPack is null || item.TestPacks.Contains(testPack, StringComparer.OrdinalIgnoreCase))
            && (setupType is null || item.SetupType == setupType) && (status is null ? item.Status != SetupLibraryStatus.Archived : item.Status == status))];
    }

    public async Task<SetupLibraryItemView?> GetAsync(Guid id, CancellationToken ct) =>
        await db.SetupLibraryItems.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) is { } row ? await ViewAsync(row, ct) : null;

    /// <summary>Saves a new library item (version 1) from the environment's current setup or from package content (validated first).</summary>
    public async Task<(SetupLibraryItemView? Item, string? Problem)> SaveAsync(SetupLibrarySaveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200) return (null, "A name (at most 200 characters) is required.");
        var (model, problem) = await ModelAsync(request, ct);
        if (model is null) return (null, problem);
        var now = clock.GetUtcNow();
        var row = new SetupLibraryItemRecord { Id = Guid.NewGuid(), Name = request.Name.Trim(), Status = nameof(SetupLibraryStatus.Ready), CreatedAt = now, UpdatedAt = now };
        row.DocumentJson = JsonSerializer.Serialize(Document(model, request), Json);
        db.SetupLibraryItems.Add(row);
        AddVersion(row.Id, 1, model with { Name = row.Name, Version = "1" }, request.ChangeSummary ?? "Created", now);
        await db.SaveChangesAsync(ct);
        return (await ViewAsync(row, ct), null);
    }

    /// <summary>Adds the next immutable version; earlier versions are never touched.</summary>
    public async Task<(SetupLibraryItemView? Item, string? Problem)> NewVersionAsync(Guid id, SetupLibrarySaveRequest request, CancellationToken ct)
    {
        var row = await db.SetupLibraryItems.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return (null, "The library item does not exist.");
        if (row.Status == nameof(SetupLibraryStatus.Archived)) return (null, "An archived item gets no new version.");
        var (model, problem) = await ModelAsync(request, ct);
        if (model is null) return (null, problem);
        var next = (await db.SetupLibraryVersions.Where(v => v.ItemId == id).MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0) + 1;
        var now = clock.GetUtcNow();
        AddVersion(id, next, model with { Name = row.Name, Version = next.ToString() }, request.ChangeSummary, now);
        var document = Read(row);
        row.DocumentJson = JsonSerializer.Serialize(document with { Providers = [.. model.Systems.Select(s => s.Provider).Distinct()], Sanitization = [.. SetupPackageSchema.Excluded] }, Json);
        row.Status = nameof(SetupLibraryStatus.Ready);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return (await ViewAsync(row, ct), null);
    }

    public async Task<SetupLibraryItemView?> DuplicateAsync(Guid id, string? name, CancellationToken ct)
    {
        var row = await db.SetupLibraryItems.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        var latest = row is null ? null : await Latest(id, ct);
        if (row is null || latest is null) return null;
        var now = clock.GetUtcNow();
        var copy = new SetupLibraryItemRecord { Id = Guid.NewGuid(), Name = string.IsNullOrWhiteSpace(name) ? $"{row.Name} (copy)" : name.Trim(), Status = nameof(SetupLibraryStatus.Draft), DocumentJson = row.DocumentJson, CreatedAt = now, UpdatedAt = now };
        db.SetupLibraryItems.Add(copy);
        db.SetupLibraryVersions.Add(new SetupLibraryVersionRecord { Id = Guid.NewGuid(), ItemId = copy.Id, VersionNumber = 1, SchemaVersion = latest.SchemaVersion, Payload = latest.Payload, ManifestJson = latest.ManifestJson, ChangeSummary = $"Duplicated from {row.Name} v{latest.VersionNumber}", CreatedAt = now });
        await db.SaveChangesAsync(ct);
        return await ViewAsync(copy, ct);
    }

    public async Task<bool> ArchiveAsync(Guid id, CancellationToken ct)
    {
        var row = await db.SetupLibraryItems.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return false;
        row.Status = nameof(SetupLibraryStatus.Archived);
        row.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The version's sanitized package in YAML or JSON — the same schema a file import reads (round trip).</summary>
    public async Task<SetupPackageExport?> ExportAsync(Guid id, int? version, SetupPackageFormat format, CancellationToken ct)
    {
        var record = await Version(id, version, ct);
        if (record is null) return null;
        var (model, _) = SetupPackageSchema.Parse("library.yaml", record.Payload);
        if (model is null) return null;
        var text = SetupPackageSchema.Serialize(model, format, record.CreatedAt);
        return new SetupPackageExport(format, $"{model.Id}-v{record.VersionNumber}.setup.{(format == SetupPackageFormat.Json ? "json" : "yaml")}", text, SetupPackageSchema.Excluded, model.Dependencies);
    }

    /// <summary>Preflight a version against a destination (always re-run; the last result is recorded as a hint, never as permanent validity).</summary>
    public async Task<SetupPreflightResult?> PreflightAsync(Guid id, int? version, string environmentId, CancellationToken ct)
    {
        var record = await Version(id, version, ct);
        if (record is null) return null;
        var result = await setup.PreflightAsync(environmentId, new SetupPreflightRequest(record.Payload, "library.yaml"), ct);
        await RecordPreflightAsync(id, $"{(result.CanApply ? "Ready" : "Blocked")} against {environmentId} · v{record.VersionNumber} · {clock.GetUtcNow():yyyy-MM-dd HH:mm} UTC", result.CanApply, ct);
        return result;
    }

    public async Task<SetupApplyPackageResult?> ApplyAsync(Guid id, int? version, string environmentId, Dictionary<string, bool>? decisions, CancellationToken ct)
    {
        var record = await Version(id, version, ct);
        var item = await db.SetupLibraryItems.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (record is null || item is null) return null;
        var result = await setup.ApplyAsync(environmentId, new SetupApplyPackageRequest(record.Payload, "library.yaml", ConflictDecisions: decisions, LibraryVersionId: record.Id),
            $"Applied from Setup Library: {item.Name} v{record.VersionNumber}", ct);
        await RecordPreflightAsync(id, $"{(result.Applied ? "Applied" : "Blocked")} to {environmentId} · v{record.VersionNumber} · {clock.GetUtcNow():yyyy-MM-dd HH:mm} UTC", result.Preflight.CanApply, ct);
        return result;
    }

    /// <summary>Differences between two versions: scope, providers, provider settings and dependencies. Secrets cannot differ: they never exist here.</summary>
    public async Task<IReadOnlyList<SetupVersionDifference>?> CompareAsync(Guid id, int from, int to, CancellationToken ct)
    {
        var a = await Version(id, from, ct);
        var b = await Version(id, to, ct);
        if (a is null || b is null) return null;
        var (left, _) = SetupPackageSchema.Parse("a.yaml", a.Payload);
        var (right, _) = SetupPackageSchema.Parse("b.yaml", b.Payload);
        if (left is null || right is null) return null;
        var differences = new List<SetupVersionDifference>();
        if (left.Scope != right.Scope) differences.Add(new("Scope", "Scope", left.Scope.ToString(), right.Scope.ToString()));
        foreach (var name in left.Systems.Select(s => s.Name).Union(right.Systems.Select(s => s.Name), StringComparer.OrdinalIgnoreCase))
        {
            var l = left.Systems.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            var r = right.Systems.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (l is null || r is null) { differences.Add(new("System", name, l?.Provider.ToString(), r?.Provider.ToString())); continue; }
            foreach (var key in l.Settings.Keys.Union(r.Settings.Keys).OrderBy(k => k, StringComparer.Ordinal))
                if (l.Settings.GetValueOrDefault(key) != r.Settings.GetValueOrDefault(key))
                    differences.Add(new($"{r.Provider} setting", $"{name} · {key}", l.Settings.GetValueOrDefault(key), r.Settings.GetValueOrDefault(key)));
        }
        string Key(SetupDependency d) => $"{d.Kind} {d.Reference}{(d.Role is null ? "" : $" ({d.Role})")} · {(d.Required ? "required" : "optional")}";
        var before = left.Dependencies.Select(Key).ToHashSet();
        var after = right.Dependencies.Select(Key).ToHashSet();
        differences.AddRange(after.Except(before).Select(item => new SetupVersionDifference("Dependency added", item, null, item)));
        differences.AddRange(before.Except(after).Select(item => new SetupVersionDifference("Dependency removed", item, item, null)));
        return differences;
    }

    private async Task<(SetupPackageModel? Model, string? Problem)> ModelAsync(SetupLibrarySaveRequest request, CancellationToken ct)
    {
        if (request.Content is { } content)
        {
            var (model, issues) = SetupPackageSchema.Parse("library.yaml", content);
            var error = issues.FirstOrDefault(issue => issue.Severity == SetupIssueSeverity.Error);
            return model is null || error is not null ? (null, error?.Message ?? "The package could not be read.") : (model, null);
        }
        if (string.IsNullOrWhiteSpace(request.EnvironmentId)) return (null, "Choose the environment to save from, or provide package content.");
        return await setup.BuildAsync(request.EnvironmentId, request.Scope, request.Provider, request.IntegrationId, request.Name, request.Description, request.TestPacks, ct);
    }

    private void AddVersion(Guid itemId, int number, SetupPackageModel model, string? summary, DateTimeOffset now) =>
        db.SetupLibraryVersions.Add(new SetupLibraryVersionRecord
        {
            Id = Guid.NewGuid(), ItemId = itemId, VersionNumber = number, SchemaVersion = SetupPackageSchema.SupportedVersion,
            // Re-serialized from the validated model: only accepted portable values are stored.
            Payload = SetupPackageSchema.Serialize(model, SetupPackageFormat.Yaml, now), ManifestJson = JsonSerializer.Serialize(model.Dependencies, Json),
            ChangeSummary = summary?.Trim() is { Length: > 0 } s ? s[..Math.Min(500, s.Length)] : null, CreatedAt = now,
        });

    private static ItemDocument Document(SetupPackageModel model, SetupLibrarySaveRequest request) =>
        new(request.Description?.Trim() ?? model.Description, model.Scope, model.EnvironmentType, request.EnvironmentId, [.. model.Systems.Select(s => s.Provider).Distinct()],
            [.. model.TestPacks.Concat(request.TestPacks ?? []).Distinct(StringComparer.OrdinalIgnoreCase)], null, [.. SetupPackageSchema.Excluded]);

    private static ItemDocument Read(SetupLibraryItemRecord row) => JsonSerializer.Deserialize<ItemDocument>(row.DocumentJson, Json) ?? new(null, SetupScope.Environment, null, null, [], [], null, []);

    private async Task RecordPreflightAsync(Guid id, string summary, bool ready, CancellationToken ct)
    {
        var row = await db.SetupLibraryItems.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (row is null) return;
        row.DocumentJson = JsonSerializer.Serialize(Read(row) with { LastPreflight = summary }, Json);
        if (row.Status != nameof(SetupLibraryStatus.Archived)) row.Status = ready ? nameof(SetupLibraryStatus.Ready) : nameof(SetupLibraryStatus.NeedsReview);
        await db.SaveChangesAsync(ct);
    }

    private Task<SetupLibraryVersionRecord?> Latest(Guid id, CancellationToken ct) =>
        db.SetupLibraryVersions.AsNoTracking().Where(v => v.ItemId == id).OrderByDescending(v => v.VersionNumber).FirstOrDefaultAsync(ct);

    private Task<SetupLibraryVersionRecord?> Version(Guid id, int? version, CancellationToken ct) =>
        version is null ? Latest(id, ct) : db.SetupLibraryVersions.AsNoTracking().SingleOrDefaultAsync(v => v.ItemId == id && v.VersionNumber == version, ct);

    private async Task<SetupLibraryItemView> ViewAsync(SetupLibraryItemRecord row, CancellationToken ct)
    {
        var document = Read(row);
        var versions = await db.SetupLibraryVersions.AsNoTracking().Where(v => v.ItemId == row.Id).OrderByDescending(v => v.VersionNumber).ToListAsync(ct);
        return new SetupLibraryItemView
        {
            Id = row.Id, Name = row.Name, Description = document.Description, SetupType = document.SetupType, EnvironmentType = document.EnvironmentType,
            SourceEnvironmentId = document.SourceEnvironmentId, Providers = document.Providers, TestPacks = document.TestPacks,
            Status = Enum.TryParse<SetupLibraryStatus>(row.Status, out var status) ? status : SetupLibraryStatus.Draft, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
            LastPreflight = document.LastPreflight, Sanitization = document.Sanitization,
            Versions = [.. versions.Select(v => new SetupLibraryVersionSummary(v.Id, v.VersionNumber, v.SchemaVersion, v.ChangeSummary, v.CreatedAt,
                JsonSerializer.Deserialize<List<SetupDependency>>(v.ManifestJson, Json) ?? [], SetupPackageSchema.Parse("v.yaml", v.Payload).Model?.Systems.Select(s => s.Provider).Distinct().ToList() ?? []))],
        };
    }
}
