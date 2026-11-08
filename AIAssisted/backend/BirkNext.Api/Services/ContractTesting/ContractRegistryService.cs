using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.ContractTesting;
using BirkNext.SourceDomains;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.ContractTesting;

/// <summary>
/// The contract registry: OpenAPI documents (JSON or YAML, OpenAPI 3 or Swagger 2) imported by upload or from a URL become immutable versions of
/// a contract, parsed by the same reader Source Analysis uses into the shared <see cref="SourceContract"/> model. An identical document is not
/// stored again; a changed one is the next version. Importing never executes anything against the API it describes.
/// </summary>
public sealed partial class ContractRegistryService(AppDbContext db, IContractSourceFetcher fetcher, TimeProvider? clock = null)
{
    public const int MaxDocumentBytes = 5 * 1024 * 1024;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NonSlug();

    public async Task<ContractImportResult> ImportAsync(ContractImportRequest request, CancellationToken ct)
    {
        var problems = new List<string>();
        var hasContent = !string.IsNullOrEmpty(request.Content);
        var hasUrl = !string.IsNullOrWhiteSpace(request.Url);
        if (hasContent == hasUrl) return Fail("Provide either a file or a URL, not both.");
        string content, sourceLabel, fileName;
        ContractVersionSource source;
        if (hasUrl)
        {
            var fetched = await fetcher.FetchAsync(request.Url!, ct);
            if (fetched.Text is not { } text) return Fail(fetched.Problem ?? "The document could not be fetched.");
            content = text; source = ContractVersionSource.Url;
            sourceLabel = SafeUrl(fetched.FinalUrl ?? request.Url!);
            fileName = System.IO.Path.GetFileName(new Uri(fetched.FinalUrl ?? request.Url!).AbsolutePath);
        }
        else { content = request.Content!; source = ContractVersionSource.Upload; fileName = System.IO.Path.GetFileName(request.FileName ?? ""); sourceLabel = fileName; }
        if (Encoding.UTF8.GetByteCount(content) > MaxDocumentBytes) return Fail($"The document is larger than {MaxDocumentBytes / (1024 * 1024)} MB.");

        var json = content.TrimStart().StartsWith('{');
        var format = json ? "json" : "yaml";
        if (!LooksLikeOpenApi(content, json)) return Fail("Not an OpenAPI 3 or Swagger 2 document (no 'openapi' or 'swagger' version field).");
        SourceContract? parsed;
        try { parsed = ContractAnalyzer.ReadOpenApiDocument(json ? "contract.json" : "contract.yaml", content); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { return Fail($"The document could not be parsed ({ex.GetType().Name})."); }
        if (parsed is null) return Fail("The document could not be read as OpenAPI.");
        if (parsed.Operations.Count == 0) problems.Add("The document declares no operations.");

        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        var now = _clock.GetUtcNow();
        ApiContractRow? contract;
        if (!string.IsNullOrWhiteSpace(request.ContractKey))
        {
            contract = await db.ApiContracts.FirstOrDefaultAsync(c => c.Key == request.ContractKey, ct);
            if (contract is null) return Fail($"Contract {request.ContractKey} does not exist.");
        }
        else
        {
            // The same document already imported as some contract's latest version: say so instead of creating a duplicate contract.
            var same = await db.ApiContractVersions.AsNoTracking().Where(v => v.Sha256 == sha).Select(v => new { v.ContractId, v.Version }).ToListAsync(ct);
            foreach (var s in same)
            {
                var latest = await db.ApiContractVersions.Where(v => v.ContractId == s.ContractId).MaxAsync(v => v.Version, ct);
                if (latest == s.Version && await db.ApiContracts.FirstAsync(c => c.Id == s.ContractId, ct) is { } existing)
                    return new(false, true, existing.Key, latest, $"This document is already version {latest} of {existing.Name}; nothing was stored.", problems);
            }
            var name = Trim(string.IsNullOrWhiteSpace(request.Name) ? parsed.Name : request.Name.Trim(), 200);
            contract = new ApiContractRow { Id = Guid.NewGuid(), Key = await UniqueKeyAsync(name, ct), Name = name, Protocol = nameof(ApiContractProtocol.Rest), CreatedAt = now, UpdatedAt = now };
            db.ApiContracts.Add(contract);
        }
        var last = await db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == contract.Id).OrderByDescending(v => v.Version).FirstOrDefaultAsync(ct);
        if (last?.Sha256 == sha) return new(false, true, contract.Key, last.Version, $"Unchanged: identical to version {last.Version}; nothing was stored.", problems);
        var version = (last?.Version ?? 0) + 1;
        var stored = parsed with { Id = contract.Key, File = Trim(fileName, 200) };
        db.ApiContractVersions.Add(new ApiContractVersionRow
        {
            Id = Guid.NewGuid(), ContractId = contract.Id, Version = version, Sha256 = sha, Source = source.ToString(), SourceLabel = Trim(sourceLabel, 500), Format = format,
            DocumentText = content, ContractJson = JsonSerializer.Serialize(stored, ExecutionEvidenceStore.Json), OperationCount = parsed.Operations.Count, ImportedAt = now,
        });
        contract.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new(true, false, contract.Key, version, version == 1 ? $"Imported {contract.Name} as version 1 ({parsed.Operations.Count} operations)." : $"Imported version {version} of {contract.Name} ({parsed.Operations.Count} operations).", problems);

        ContractImportResult Fail(string message) => new(false, false, null, null, message, [message]);
    }

    public async Task<IReadOnlyList<ApiContractSummary>> ListAsync(CancellationToken ct)
    {
        var contracts = await db.ApiContracts.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
        var result = new List<ApiContractSummary>();
        foreach (var c in contracts) result.Add(await SummaryAsync(c, ct));
        return result;
    }

    public async Task<ApiContractDetail?> GetAsync(string key, int? version, CancellationToken ct)
    {
        if (await db.ApiContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct) is not { } contract) return null;
        var versions = await db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == contract.Id).OrderByDescending(v => v.Version).ToListAsync(ct);
        var selected = version is { } n ? versions.FirstOrDefault(v => v.Version == n) : versions.FirstOrDefault();
        if (selected is null) return null;
        return new(await SummaryAsync(contract, ct), [.. versions.Select(Info)], selected.Version, Document(selected));
    }

    /// <summary>The parsed contract of one version (null when the contract or version does not exist).</summary>
    public async Task<SourceContract?> DocumentAsync(string key, int version, CancellationToken ct) =>
        await db.ApiContracts.AsNoTracking().Where(c => c.Key == key).Join(db.ApiContractVersions.AsNoTracking().Where(v => v.Version == version), c => c.Id, v => v.ContractId, (c, v) => v)
            .FirstOrDefaultAsync(ct) is { } row ? Document(row) : null;

    public async Task<int?> LatestVersionAsync(string key, CancellationToken ct) =>
        await db.ApiContracts.AsNoTracking().Where(c => c.Key == key).Join(db.ApiContractVersions.AsNoTracking(), c => c.Id, v => v.ContractId, (c, v) => (int?)v.Version).MaxAsync(ct);

    /// <summary>Every change between two versions: operations, types and fields, and operation detail (parameters, body, responses).</summary>
    public static List<SourceEvidenceChange> Changes(SourceContract before, SourceContract after) =>
        [.. SourceEvidenceDiff.ContractChanges(before, after), .. SourceEvidenceDiff.OperationDetailChanges(before, after)];

    private async Task<ApiContractSummary> SummaryAsync(ApiContractRow c, CancellationToken ct)
    {
        var latest = await db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == c.Id).OrderByDescending(v => v.Version).Select(v => new { v.Version, v.OperationCount }).FirstOrDefaultAsync(ct);
        var tests = await db.ContractTests.AsNoTracking().CountAsync(t => t.ContractId == c.Id, ct);
        return new(c.Id, c.Key, c.Name, Enum.Parse<ApiContractProtocol>(c.Protocol), latest?.Version ?? 0, latest?.OperationCount ?? 0, tests, c.UpdatedAt);
    }

    private static ApiContractVersionInfo Info(ApiContractVersionRow v)
    {
        var doc = Document(v);
        return new(v.Version, v.Sha256, Enum.Parse<ContractVersionSource>(v.Source), v.SourceLabel, v.Format, v.ImportedAt, v.OperationCount, doc.Version, doc.ParseSupport, doc.Limitations);
    }

    private static SourceContract Document(ApiContractVersionRow v) => JsonSerializer.Deserialize<SourceContract>(v.ContractJson, ExecutionEvidenceStore.Json)!;

    private async Task<string> UniqueKeyAsync(string name, CancellationToken ct)
    {
        var slug = NonSlug().Replace(name.ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0) slug = "contract";
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        var key = slug;
        for (var n = 2; await db.ApiContracts.AnyAsync(c => c.Key == key, ct); n++) key = $"{slug}-{n}";
        return key;
    }

    /// <summary>A top-level "openapi" or "swagger" version field (JSON property or YAML key at column 0).</summary>
    private static bool LooksLikeOpenApi(string content, bool json)
    {
        if (json)
        {
            try { using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); return doc.RootElement.ValueKind == JsonValueKind.Object && (doc.RootElement.TryGetProperty("openapi", out _) || doc.RootElement.TryGetProperty("swagger", out _)); }
            catch (JsonException) { return false; }
        }
        return content.Split('\n').Any(l => l.StartsWith("openapi:", StringComparison.Ordinal) || l.StartsWith("swagger:", StringComparison.Ordinal) || l.StartsWith("\"openapi\":", StringComparison.Ordinal));
    }

    /// <summary>Scheme, host, port and path; the query (which may carry a token) is dropped.</summary>
    private static string SafeUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}{u.AbsolutePath}" : "(url)";

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}
