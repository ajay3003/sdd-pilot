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
/// The contract registry: OpenAPI documents (JSON or YAML, OpenAPI 3 or Swagger 2) and GraphQL schemas (SDL or an introspection result) imported
/// by upload or from a URL become immutable versions of a contract, parsed by the readers Source Analysis uses into the shared
/// <see cref="SourceContract"/> model. A contract keeps one protocol across its versions. An identical document is not
/// stored again; a changed one is the next version. Importing never executes anything against the API it describes.
/// </summary>
public sealed partial class ContractRegistryService(AppDbContext db, IContractSourceFetcher fetcher, TimeProvider? clock = null, BirkNext.Api.Services.SourceAnalysis.IReviewSourceEvidenceProvider? sources = null)
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
        if (json)
        {
            // Malformed JSON is reported with its position before any format detection.
            try { using var probe = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 128 }); }
            catch (JsonException je) { return Fail($"The JSON is not valid at line {(je.LineNumber ?? 0) + 1}, position {(je.BytePositionInLine ?? 0) + 1}."); }
        }
        string format;
        ApiContractProtocol protocol;
        SourceContract? parsed;
        try
        {
            if (LooksLikeOpenApi(content, json))
            {
                (protocol, format) = (ApiContractProtocol.Rest, json ? "json" : "yaml");
                parsed = ContractAnalyzer.ReadOpenApiDocument(json ? "contract.json" : "contract.yaml", content);
            }
            else if (json && IsIntrospection(content))
            {
                (protocol, format) = (ApiContractProtocol.GraphQl, "introspection");
                parsed = ContractAnalyzer.ReadGraphQlIntrospection("schema.json", content);
            }
            else if (!json)
            {
                (protocol, format) = (ApiContractProtocol.GraphQl, "graphql");
                parsed = ContractAnalyzer.ReadGraphQl("schema.graphql", content);
                if (parsed?.Type == SourceContractType.GraphQlOperations) return Fail("This is a GraphQL operations document, not a schema. Import the schema (SDL or an introspection result).");
                if (parsed is { ParseSupport: not DomainSupport.Supported }) return Fail("Not a complete GraphQL schema: " + string.Join(" ", parsed.Limitations));
            }
            else return Fail("Not an OpenAPI 3 / Swagger 2 document or a GraphQL schema (SDL or an introspection result).");
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or HotChocolate.Language.SyntaxException)
        {
            return Fail(ex switch
            {
                JsonException je => $"The JSON is not valid at line {(je.LineNumber ?? 0) + 1}, position {(je.BytePositionInLine ?? 0) + 1}.",
                HotChocolate.Language.SyntaxException se => $"The GraphQL schema is not valid: {se.Message}",
                _ => $"The document could not be parsed ({ex.GetType().Name}: {ex.Message.Split('.')[0]}).",
            });
        }
        if (format == "yaml" && YamlLimitations(content) is { Count: > 0 } yamlLimits)
        {
            // Nothing readable plus unsupported or malformed YAML is a refusal, not an empty contract.
            if (parsed is null || parsed.Operations.Count == 0) return Fail("The YAML could not be read: " + string.Join(" ", yamlLimits));
            parsed = parsed with { Limitations = [.. parsed.Limitations, .. yamlLimits] };
        }
        if (parsed is null) return Fail(protocol == ApiContractProtocol.Rest ? "The document could not be read as OpenAPI." : "The document could not be read as a GraphQL schema.");
        if (parsed.Operations.Count == 0) problems.Add("The document declares no operations.");

        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        var now = _clock.GetUtcNow();
        ApiContractRow? contract;
        if (!string.IsNullOrWhiteSpace(request.ContractKey))
        {
            contract = await db.ApiContracts.FirstOrDefaultAsync(c => c.Key == request.ContractKey, ct);
            if (contract is null) return Fail($"Contract {request.ContractKey} does not exist.");
            if (contract.Lifecycle == nameof(ContractLifecycle.Archived)) return Fail($"{contract.Name} is archived: it is read-only. Make it active again to add a version.");
            if (contract.Protocol != protocol.ToString())
                return Fail($"{contract.Name} is a {(contract.Protocol == nameof(ApiContractProtocol.Rest) ? "REST (OpenAPI)" : "GraphQL")} contract; a {(protocol == ApiContractProtocol.Rest ? "REST" : "GraphQL")} document cannot be its next version.");
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
            if (protocol == ApiContractProtocol.GraphQl && string.IsNullOrWhiteSpace(request.Name) && parsed.Name is "schema" or "contract") name = "GraphQL schema";
            contract = new ApiContractRow { Id = Guid.NewGuid(), Key = await UniqueKeyAsync(name, ct), Name = name, Protocol = protocol.ToString(), CreatedAt = now, UpdatedAt = now };
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

    /// <summary>
    /// REST (OpenAPI) and GraphQL schema contracts that Source Analysis snapshots found, newest snapshots first, with the store version each one
    /// was already imported as. Reading only; nothing is imported.
    /// </summary>
    public async Task<IReadOnlyList<SourceContractCandidate>> SourceCandidatesAsync(CancellationToken ct)
    {
        if (sources is null || !sources.SourceAnalysisEnabled) return [];
        var imported = await db.ApiContractVersions.AsNoTracking().Where(v => v.SourceSnapshotId != null)
            .Join(db.ApiContracts.AsNoTracking(), v => v.ContractId, c => c.Id, (v, c) => new { v.SourceSnapshotId, v.SourcePath, c.Key, v.Version }).ToListAsync(ct);
        var result = new List<SourceContractCandidate>();
        foreach (var snapshot in (await sources.ListAsync(string.Empty, ct)).Take(20))
            foreach (var c in snapshot.EvidenceDomains?.Contracts.Contracts.Where(c => c.Type is SourceContractType.OpenApi or SourceContractType.GraphQlSchema) ?? [])
            {
                var hit = imported.FirstOrDefault(i => i.SourceSnapshotId == snapshot.Id && i.SourcePath == c.Id);
                result.Add(new(snapshot.Id, snapshot.AnalyzedAt, snapshot.Archive.FileName, snapshot.Commit, c.Id, c.Name, c.Type.ToString(), c.File, c.Operations.Count,
                    hit is null ? null : ContractIdentity.ContractRef(hit.Key, hit.Version)));
            }
        return result;
    }

    /// <summary>
    /// Imports a contract a Source Analysis snapshot already holds (no re-upload): the snapshot's parsed contract becomes an immutable version with
    /// its provenance (snapshot, source path, archive). The snapshot is never changed. Identical evidence is not stored again. The snapshot keeps
    /// the parsed contract, not the document text, so such versions get structural (type-based) validation, not full schema validation.
    /// </summary>
    public async Task<ContractImportResult> ImportFromSourceAsync(SourceContractImportRequest request, CancellationToken ct)
    {
        ContractImportResult Fail(string message) => new(false, false, null, null, message, [message]);
        if (sources is null || !sources.SourceAnalysisEnabled) return Fail("Source Analysis is not available.");
        if (await sources.ResolveAsync(string.Empty, request.SnapshotId, ct) is not { } snapshot) return Fail("The source snapshot does not exist.");
        if (snapshot.EvidenceDomains?.Contracts.Contracts.FirstOrDefault(c => c.Id == request.SourceContractId) is not { } source) return Fail("The snapshot has no such contract.");
        if (source.Type is not (SourceContractType.OpenApi or SourceContractType.GraphQlSchema)) return Fail("Only OpenAPI and GraphQL schema contracts can be imported for contract testing.");
        var protocol = source.Type == SourceContractType.OpenApi ? ApiContractProtocol.Rest : ApiContractProtocol.GraphQl;
        var evidenceJson = JsonSerializer.Serialize(source, ExecutionEvidenceStore.Json);
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidenceJson))).ToLowerInvariant();
        if (await db.ApiContractVersions.AsNoTracking().Where(v => v.SourceSnapshotId == snapshot.Id && v.SourcePath == source.Id)
                .Join(db.ApiContracts.AsNoTracking(), v => v.ContractId, c => c.Id, (v, c) => new { c.Key, v.Version }).FirstOrDefaultAsync(ct) is { } already)
            return new(false, true, already.Key, already.Version, $"Already imported from this snapshot as {ContractIdentity.ContractRef(already.Key, already.Version)}; nothing was stored.", []);
        var now = _clock.GetUtcNow();
        ApiContractRow contract;
        if (!string.IsNullOrWhiteSpace(request.ContractKey))
        {
            if (await db.ApiContracts.FirstOrDefaultAsync(c => c.Key == request.ContractKey, ct) is not { } existing) return Fail($"Contract {request.ContractKey} does not exist.");
            if (existing.Lifecycle == nameof(ContractLifecycle.Archived)) return Fail($"{existing.Name} is archived: it is read-only.");
            if (existing.Protocol != protocol.ToString()) return Fail($"{existing.Name} is a {existing.Protocol} contract; this source contract is {protocol}.");
            contract = existing;
        }
        else
        {
            var name = Trim(string.IsNullOrWhiteSpace(request.Name) ? source.Name : request.Name.Trim(), 200);
            contract = new ApiContractRow { Id = Guid.NewGuid(), Key = await UniqueKeyAsync(name, ct), Name = name, Protocol = protocol.ToString(), CreatedAt = now, UpdatedAt = now };
            db.ApiContracts.Add(contract);
        }
        var last = await db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == contract.Id).OrderByDescending(v => v.Version).FirstOrDefaultAsync(ct);
        if (last?.Sha256 == sha) return new(false, true, contract.Key, last.Version, $"Unchanged: the source evidence is identical to version {last.Version}; nothing was stored.", []);
        var version = (last?.Version ?? 0) + 1;
        var stored = source with
        {
            Id = contract.Key,
            Limitations = [.. source.Limitations, "Imported from a Source Analysis snapshot: the snapshot keeps the parsed contract, not the document text, so responses get structural (type-based) validation, not full schema validation."],
        };
        db.ApiContractVersions.Add(new ApiContractVersionRow
        {
            Id = Guid.NewGuid(), ContractId = contract.Id, Version = version, Sha256 = sha, Source = nameof(ContractVersionSource.SourceSnapshot),
            SourceLabel = Trim($"{snapshot.Archive.FileName} · {source.File} · snapshot {snapshot.Id:N}" + (snapshot.Commit is { Length: > 0 } commit && commit != "Unknown" ? $" · {commit}" : ""), 500),
            Format = source.Format, DocumentText = "", ContractJson = JsonSerializer.Serialize(stored, ExecutionEvidenceStore.Json), OperationCount = source.Operations.Count, ImportedAt = now,
            SourceSnapshotId = snapshot.Id, SourcePath = Trim(source.Id, 500),
        });
        contract.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return new(true, false, contract.Key, version, $"Imported {source.Name} from source snapshot {snapshot.AnalyzedAt:yyyy-MM-dd HH:mm} as version {version} ({source.Operations.Count} operations).", []);
    }

    /// <summary>YAML features the subset reader does not read (anchors/aliases, tags): named so a partial read is never silent.</summary>
    private static List<string> YamlLimitations(string content)
    {
        var limits = new List<string>();
        if (Regex.IsMatch(content, @"(^|\s)&[A-Za-z0-9_-]+\s|:\s+\*[A-Za-z0-9_-]+", RegexOptions.Multiline)) limits.Add("YAML anchors/aliases are not resolved by the YAML subset reader.");
        if (Regex.IsMatch(content, @"\s!!?[A-Za-z]", RegexOptions.Multiline)) limits.Add("YAML tags are not interpreted by the YAML subset reader.");
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.TrimStart().StartsWith('#') || !Regex.IsMatch(line, @"(:|^\s*-)\s+[\[{]")) continue;
            if (line.Count(c => c == '[') != line.Count(c => c == ']') || line.Count(c => c == '{') != line.Count(c => c == '}'))
            { limits.Add($"A YAML flow collection is not closed on line {i + 1}."); break; }
        }
        return limits;
    }

    // ── Lifecycle and retention ─────────────────────────────────────────────────────────────────────────────────────────

    public const string RetentionPolicy = "Archive by default: an archived contract keeps every version, test and execution result. A version is deleted only when "
        + "nothing references it (no test revision, no execution evidence, not imported from a source snapshot, not the latest version) and the deletion is confirmed by typing the contract key.";

    public async Task<string?> SetLifecycleAsync(string key, ContractLifecycle lifecycle, CancellationToken ct)
    {
        if (await db.ApiContracts.FirstOrDefaultAsync(c => c.Key == key, ct) is not { } contract) return "Not found.";
        contract.Lifecycle = lifecycle.ToString();
        contract.UpdatedAt = _clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<string?> SetHiddenAsync(string key, int version, bool hidden, CancellationToken ct)
    {
        if (await db.ApiContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct) is not { } contract) return "Not found.";
        var rows = await db.ApiContractVersions.Where(v => v.ContractId == contract.Id).ToListAsync(ct);
        if (rows.FirstOrDefault(v => v.Version == version) is not { } row) return "Not found.";
        if (hidden && version == rows.Max(v => v.Version)) return "The latest version cannot be hidden.";
        row.Hidden = hidden;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Each version's references (computed from the stored tests, their revisions and the execution evidence) and whether it may be deleted.</summary>
    public async Task<ContractRetention?> RetentionAsync(string key, CancellationToken ct)
    {
        if (await db.ApiContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Key == key, ct) is not { } contract) return null;
        var versions = await db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == contract.Id).OrderByDescending(v => v.Version).ToListAsync(ct);
        var testIds = await db.ContractTests.AsNoTracking().Where(t => t.ContractId == contract.Id).Select(t => t.Id).ToListAsync(ct);
        var docs = (await db.ContractTests.AsNoTracking().Where(t => t.ContractId == contract.Id).Select(t => t.DocumentJson).ToListAsync(ct))
            .Concat(await db.ContractTestRevisions.AsNoTracking().Where(r => testIds.Contains(r.TestId)).Select(r => r.DocumentJson).ToListAsync(ct))
            .Select(d => JsonSerializer.Deserialize<ContractTestDefinition>(d, ExecutionEvidenceStore.Json)!).ToList();
        var latest = versions.Count == 0 ? 0 : versions.Max(v => v.Version);
        var result = new List<ContractVersionRetention>();
        foreach (var v in versions)
        {
            var reference = ContractIdentity.ContractRef(key, v.Version);
            var operationRef = $"\"operationRef\":\"{reference}#";
            var evidence = await db.ExecutionCalls.AsNoTracking().CountAsync(c => c.DocumentJson.Contains(operationRef), ct)
                + await db.ExecutionRuns.AsNoTracking().CountAsync(r => r.SummaryJson.Contains($"\"{reference}\""), ct);
            var tests = docs.Count(d => d.ContractVersion == v.Version);
            var refs = new List<string>();
            if (v.Version == latest) refs.Add("latest version");
            if (tests > 0) refs.Add($"{tests} test revision(s)");
            if (evidence > 0) refs.Add($"{evidence} execution record(s)");
            if (v.SourceSnapshotId is { } snapshot) refs.Add($"imported from source snapshot {snapshot:N}");
            result.Add(new(v.Version, v.Hidden, v.Version == latest, tests, evidence, v.SourceSnapshotId is not null, refs.Count == 0, refs));
        }
        var lifecycle = Enum.TryParse<ContractLifecycle>(contract.Lifecycle, out var l) ? l : ContractLifecycle.Active;
        var contractDeletable = testIds.Count == 0 && result.All(r => r.TestRevisions == 0 && r.EvidenceCalls == 0 && !r.SourceBacked);
        return new(key, lifecycle, result, contractDeletable, RetentionPolicy);
    }

    /// <summary>Deletes one unreferenced version after confirmation (the contract key typed back). Referenced history is never deleted.</summary>
    public async Task<string?> DeleteVersionAsync(string key, int version, string? confirm, CancellationToken ct)
    {
        if (!string.Equals(confirm, key, StringComparison.Ordinal)) return "Confirm the deletion by typing the contract key.";
        if (await RetentionAsync(key, ct) is not { } retention || retention.Versions.FirstOrDefault(v => v.Version == version) is not { } info) return "Not found.";
        if (!info.Deletable) return $"Version {version} is kept: {string.Join(", ", info.References)}.";
        var contractId = await db.ApiContracts.Where(c => c.Key == key).Select(c => c.Id).FirstAsync(ct);
        db.ApiContractVersions.Remove(await db.ApiContractVersions.FirstAsync(v => v.ContractId == contractId && v.Version == version, ct));
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Deletes a whole contract only when no test, evidence or source snapshot references it, after confirmation. Otherwise archive it.</summary>
    public async Task<string?> DeleteContractAsync(string key, string? confirm, CancellationToken ct)
    {
        if (!string.Equals(confirm, key, StringComparison.Ordinal)) return "Confirm the deletion by typing the contract key.";
        if (await RetentionAsync(key, ct) is not { } retention) return "Not found.";
        if (!retention.ContractDeletable) return "The contract is referenced by tests, execution evidence or a source snapshot; archive it instead.";
        var contract = await db.ApiContracts.FirstAsync(c => c.Key == key, ct);
        db.ApiContractVersions.RemoveRange(db.ApiContractVersions.Where(v => v.ContractId == contract.Id));
        db.ContractEndpointBindings.RemoveRange(db.ContractEndpointBindings.Where(b => b.ContractId == contract.Id));
        db.ApiContracts.Remove(contract);
        await db.SaveChangesAsync(ct);
        return null;
    }

    public async Task<ContractLifecycle?> LifecycleAsync(string key, CancellationToken ct) =>
        await db.ApiContracts.AsNoTracking().Where(c => c.Key == key).Select(c => c.Lifecycle).FirstOrDefaultAsync(ct) is { } value && Enum.TryParse<ContractLifecycle>(value, out var l) ? l : null;

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

    /// <summary>The stored document text and parsed contract of one version (for contract validation).</summary>
    public async Task<(string Text, SourceContract Document)?> RawAsync(string key, int version, CancellationToken ct) =>
        await db.ApiContracts.AsNoTracking().Where(c => c.Key == key).Join(db.ApiContractVersions.AsNoTracking().Where(v => v.Version == version), c => c.Id, v => v.ContractId, (c, v) => v)
            .FirstOrDefaultAsync(ct) is { } row ? (row.DocumentText, Document(row)) : null;

    public async Task<int?> LatestVersionAsync(string key, CancellationToken ct) =>
        await db.ApiContracts.AsNoTracking().Where(c => c.Key == key).Join(db.ApiContractVersions.AsNoTracking(), c => c.Id, v => v.ContractId, (c, v) => (int?)v.Version).MaxAsync(ct);

    /// <summary>Every change between two versions: operations, types and fields, and operation detail (parameters, body, responses).</summary>
    public static List<SourceEvidenceChange> Changes(SourceContract before, SourceContract after) =>
        [.. SourceEvidenceDiff.ContractChanges(before, after), .. SourceEvidenceDiff.OperationDetailChanges(before, after)];

    private async Task<ApiContractSummary> SummaryAsync(ApiContractRow c, CancellationToken ct)
    {
        var latest = await db.ApiContractVersions.AsNoTracking().Where(v => v.ContractId == c.Id).OrderByDescending(v => v.Version).Select(v => new { v.Version, v.OperationCount }).FirstOrDefaultAsync(ct);
        var tests = await db.ContractTests.AsNoTracking().CountAsync(t => t.ContractId == c.Id, ct);
        return new(c.Id, c.Key, c.Name, Enum.Parse<ApiContractProtocol>(c.Protocol), latest?.Version ?? 0, latest?.OperationCount ?? 0, tests, c.UpdatedAt)
            { Lifecycle = Enum.TryParse<ContractLifecycle>(c.Lifecycle, out var lifecycle) ? lifecycle : ContractLifecycle.Active };
    }

    private static ApiContractVersionInfo Info(ApiContractVersionRow v)
    {
        var doc = Document(v);
        return new(v.Version, v.Sha256, Enum.Parse<ContractVersionSource>(v.Source), v.SourceLabel, v.Format, v.ImportedAt, v.OperationCount, doc.Version, doc.ParseSupport, doc.Limitations)
            { SourceSnapshotId = v.SourceSnapshotId, SourcePath = v.SourcePath, HasDocument = v.DocumentText.Length > 0, Hidden = v.Hidden };
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

    /// <summary>A GraphQL introspection result: <c>{"data":{"__schema":…}}</c> or <c>{"__schema":…}</c>.</summary>
    private static bool IsIntrospection(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("__schema", out _) || root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("__schema", out _));
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Scheme, host, port and path; the query (which may carry a token) is dropped.</summary>
    private static string SafeUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Authority}{u.AbsolutePath}" : "(url)";

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}
