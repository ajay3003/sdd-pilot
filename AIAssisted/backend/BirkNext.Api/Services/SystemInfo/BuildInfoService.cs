using System.Security.Cryptography;
using System.Text.Json;
using BirkNext.Api.Data;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Data.DeliveryReporting;
using BirkNext.Api.Data.SnapshotRegistry;
using BirkNext.Api.Services.BrowserCompanion;
using BirkNext.BuildInfo;
using Microsoft.EntityFrameworkCore;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.SystemInfo;

/// <summary>
/// The one backend build-information provider. The identity is what is compiled into BirkNext.Api.dll (never a database or configuration
/// value). A packaged build-manifest.json is compared with it and any difference reported; the bundled and connected Browser Companion
/// versions and each database context's migration state are read, never changed (no migration is applied here).
/// </summary>
public sealed class BuildInfoService(IServiceScopeFactory scopes, IConfiguration configuration, BrowserCompanionService? companions = null, TimeProvider? clock = null)
{
    private static readonly Lazy<BirkNext.BuildInfo.BuildIdentity> Backend = new(() => BuildIdentityReader.FromAssembly(typeof(BuildInfoService).Assembly, "BirkNext.Api"));

    /// <summary>The running backend's compiled identity.</summary>
    public static BirkNext.BuildInfo.BuildIdentity Current => Backend.Value;

    /// <summary>Where a package keeps build-manifest.json: the package root, two levels above the backend (AIAssisted/backend).</summary>
    internal string ManifestPath => configuration["BuildInfo:ManifestPath"] is { Length: > 0 } configured ? configured
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "build-manifest.json"));

    internal string CompanionInfoPath => Path.Combine(AppContext.BaseDirectory, "BrowserCompanion", "build-info.json");

    public async Task<BuildInfoResponse> GetAsync(CancellationToken ct) => new()
    {
        Backend = Current, Manifest = CheckManifest(Current, ManifestPath), Companion = Companion(), Database = await DatabaseAsync(ct),
        EvaluatedAt = (clock ?? TimeProvider.System).GetUtcNow(),
    };

    /// <summary>Compares a packaged manifest with the running identity. Absent = NotPackaged (a source/developer run), never an error.</summary>
    public static ManifestCheck CheckManifest(BirkNext.BuildInfo.BuildIdentity running, string path)
    {
        if (!File.Exists(path)) return new(ManifestCheckStatus.NotPackaged, null, null, null, []);
        byte[] bytes;
        JsonElement root;
        try { bytes = File.ReadAllBytes(path); root = JsonDocument.Parse(bytes).RootElement; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(ManifestCheckStatus.Invalid, null, null, null, ["build-manifest.json cannot be read as JSON."]); }
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string? S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (S("manifestSchemaVersion") != "birknext-build-manifest/1") return new(ManifestCheckStatus.Invalid, S("buildVersion"), S("channel"), fingerprint, ["Unsupported build manifest schema."]);
        var differences = new List<string>();
        void D(string what, string? manifest, string? runningValue) { if (!string.Equals(manifest, runningValue, StringComparison.Ordinal)) differences.Add($"{what}: manifest {manifest ?? "—"}, running {runningValue ?? "—"}"); }
        D("Informational version", S("informationalVersion"), running.InformationalVersion);
        D("Build version", S("buildVersion"), running.BuildVersion);
        D("Channel", S("channel"), running.Channel.ToString());
        D("Pipeline run", S("pipelineRunId"), running.BuildId);
        D("Source commit", S("sourceCommit") is "unknown" ? null : S("sourceCommit"), running.SourceCommit);
        return new(differences.Count == 0 ? ManifestCheckStatus.Consistent : ManifestCheckStatus.Inconsistent, S("buildVersion"), S("channel"), fingerprint, differences);
    }

    private CompanionBuildInfo Companion()
    {
        string? version = null, buildId = null;
        try
        {
            if (File.Exists(CompanionInfoPath))
            {
                var info = JsonDocument.Parse(File.ReadAllBytes(CompanionInfoPath)).RootElement;
                version = info.TryGetProperty("version", out var v) ? v.GetString() : null;
                buildId = info.TryGetProperty("buildId", out var b) ? b.GetString() : null;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        var connected = companions?.ConnectedBuilds() ?? [];
        var note = connected.Count == 0 ? "No Browser Companion is connected."
            : version is null ? "No bundled Companion build to compare with."
            : connected.All(c => c.Version == version && c.BuildId == buildId) ? "The connected Companion is the build bundled with this BirkNext."
            : "A connected Companion differs from the build bundled with this BirkNext (it may be older or newer).";
        return new() { BundledVersion = version, BundledBuildId = buildId, Connected = [.. connected], Compatibility = ComponentCompatibility.NotAssessed,
            Note = note + " The Companion has no protocol version handshake, so compatibility is not assessed." };
    }

    private async Task<List<SchemaState>> DatabaseAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var result = new List<SchemaState>();
        foreach (var (name, context) in new (string, Func<DbContext?>)[]
        {
            ("Application", () => sp.GetService<AppDbContext>()),
            ("Delivery reporting", () => sp.GetService<DeliveryReportingDbContext>()),
            ("Canonical interpretation", () => sp.GetService<CanonicalInterpretationDbContext>()),
            ("Snapshot registry", () => sp.GetService<SnapshotRegistryDbContext>()),
        })
            result.Add(await SchemaAsync(name, context(), ct));
        return result;
    }

    /// <summary>Known (compiled) vs applied migrations of one context; reads only. A non-relational provider or an unreachable database is Unknown.</summary>
    internal static async Task<SchemaState> SchemaAsync(string name, DbContext? db, CancellationToken ct)
    {
        if (db is null || !db.Database.IsRelational()) return new(name, 0, 0, 0, null, SchemaCompatibility.Unknown, "Not a relational database in this host.");
        var known = db.Database.GetMigrations().ToList();
        try
        {
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
            var pending = known.Except(applied, StringComparer.Ordinal).Count();
            var ahead = applied.Except(known, StringComparer.Ordinal).Count();
            var status = ahead > 0 ? SchemaCompatibility.AheadOfBuild : pending > 0 ? SchemaCompatibility.PendingMigrations : SchemaCompatibility.Current;
            var note = status switch
            {
                SchemaCompatibility.AheadOfBuild => $"{ahead} applied migration(s) are unknown to this build: the database was migrated by a newer BirkNext.",
                SchemaCompatibility.PendingMigrations => $"{pending} migration(s) of this build are not applied yet.",
                _ => "Every migration of this build is applied.",
            };
            return new(name, known.Count, applied.Count, pending, applied.LastOrDefault(), status, note);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new(name, known.Count, 0, 0, null, SchemaCompatibility.Unknown, "The migration history could not be read.");
        }
    }
}
