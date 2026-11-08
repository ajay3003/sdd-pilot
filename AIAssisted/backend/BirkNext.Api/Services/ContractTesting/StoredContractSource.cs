using System.Globalization;
using System.Text.Json;
using BirkNext.Api.Services.ApiQuality;
using BirkNext.SourceDomains;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ContractTesting;

/// <summary>The contract store as API Quality Review's contract source: one store, one version identity.</summary>
public sealed class StoredContractSource(ContractRegistryService registry, Data.AppDbContext db) : IStoredContractSource
{
    public async Task<StoredContract?> GetAsync(string contractRef, CancellationToken ct)
    {
        var at = contractRef.LastIndexOf("@v", StringComparison.Ordinal);
        var key = at < 0 ? contractRef : contractRef[..at];
        int? version = at < 0 ? await registry.LatestVersionAsync(key, ct) : int.TryParse(contractRef[(at + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        if (version is null || await registry.RawAsync(key, version.Value, ct) is not { } raw) return null;
        var protocol = await db.ApiContracts.AsNoTracking().Where(c => c.Key == key).Select(c => c.Protocol).FirstAsync(ct);
        string? json = null;
        if (raw.Document.Type == SourceContractType.OpenApi && raw.Text.Length > 0)
            try { json = raw.Text.TrimStart().StartsWith('{') ? raw.Text : OpenApiSchemaValidator.Parse(raw.Text)?.ToJsonString(); } catch (Exception ex) when (ex is JsonException or FormatException) { }
        return new(key, version.Value, protocol, raw.Text, json);
    }
}
