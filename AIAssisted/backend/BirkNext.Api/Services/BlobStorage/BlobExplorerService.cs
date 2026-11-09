using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ExecutionEvidence;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.Parquet;
using BirkNext.BlobStorage;
using BirkNext.ExecutionEvidence;
using BirkNext.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Path = System.IO.Path;

namespace BirkNext.Api.Services.BlobStorage;

/// <summary>
/// Read-only Blob Storage explorer over configured stores: containers, paginated listing with virtual folders, properties (metadata redacted),
/// bounded previews (JSON, XML, CSV, NDJSON, text, Parquet through the Parquet explorer's engine), expectation checks by level, correlation with
/// Execution Evidence by exact execution id, and saved observations without content. Nothing here can change a store.
/// </summary>
public sealed class BlobExplorerService(IOptions<BlobStorageOptions> options, IHostEnvironment environment, AppDbContext db, ExecutionEvidenceStore executions,
    IIntegrationAzureCredential? azure = null, TimeProvider? clock = null)
{
    public const int TextPreviewBytes = 1024 * 1024;
    public const int ParquetPreviewBytes = 64 * 1024 * 1024;
    public const int PreviewRows = 50;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] ExecutionIdKeys = ["X-BirkNext-Execution-Id", "birknext_execution_id", "birknextexecutionid", "execution_id", "correlation_id"];
    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public List<BlobStoreView> Stores(string environmentId) => options.Value.Stores.Where(s => s.EnvironmentId == environmentId).Select(View).ToList();

    private BlobStoreView View(BlobStoreOptions s)
    {
        var (readiness, detail) = s.Kind switch
        {
            BlobStoreKind.SyntheticLocal when !environment.IsDevelopment() => (BlobStoreReadiness.Disabled, "Synthetic local stores are accepted only in Development."),
            BlobStoreKind.SyntheticLocal when string.IsNullOrWhiteSpace(s.Root) || !Directory.Exists(s.Root) => (BlobStoreReadiness.NotConfigured, "The synthetic store's folder does not exist."),
            BlobStoreKind.SyntheticLocal => (BlobStoreReadiness.Ready, "SYNTHETIC LOCAL STORE — a folder on this machine, not Azure. Nothing here says anything about a real storage account."),
            _ when !Uri.TryCreate(s.AccountUrl, UriKind.Absolute, out var u) || u.Scheme != "https" || !u.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase)
                => (BlobStoreReadiness.NotConfigured, "The account URL must be https://{account}.blob.core.windows.net."),
            _ when azure?.Credential is null => (BlobStoreReadiness.Disabled, azure?.DisabledReason ?? "No Azure identity is configured for BirkNext."),
            _ => (BlobStoreReadiness.Ready, "Configured; read-only (Storage Blob Data Reader). Not contacted until you open it."),
        };
        var endpoint = s.Kind == BlobStoreKind.SyntheticLocal ? "synthetic://" + s.Id : s.AccountUrl ?? "";
        return new BlobStoreView(s.Id, s.EnvironmentId, string.IsNullOrWhiteSpace(s.Name) ? s.Id : s.Name, s.Kind, endpoint, readiness, detail, [.. s.Containers]);
    }

    private (IBlobReadStore? Store, BlobStoreOptions? Options, string? Error) Open(string environmentId, string storeId)
    {
        var s = options.Value.Stores.FirstOrDefault(x => x.Id == storeId && x.EnvironmentId == environmentId);
        if (s is null) return (null, null, "The store is not configured for this environment.");
        var view = View(s);
        if (view.Readiness != BlobStoreReadiness.Ready) return (null, s, view.Detail);
        IBlobReadStore store = s.Kind == BlobStoreKind.SyntheticLocal ? new SyntheticLocalBlobStore(s.Root!) : new AzureBlobReadStore(new Uri(s.AccountUrl!), azure!.Credential!);
        return (store, s, null);
    }

    private static string? ContainerAllowed(BlobStoreOptions s, string container) =>
        s.Containers.Count > 0 && !s.Containers.Contains(container, StringComparer.Ordinal) ? "The container is not in this store's allowed list." : null;

    public async Task<(List<BlobContainerView>? Containers, string? Error)> ContainersAsync(string environmentId, string storeId, CancellationToken ct)
    {
        var (store, s, error) = Open(environmentId, storeId);
        if (store is null) return (null, error);
        try
        {
            var all = await store.ContainersAsync(ct);
            return (s!.Containers.Count == 0 ? all : all.Where(c => s.Containers.Contains(c.Name, StringComparer.Ordinal)).ToList(), null);
        }
        catch (BlobStoreAccessException ex) { return (null, ex.Message); }
    }

    public async Task<(BlobListing? Listing, string? Error)> ListAsync(string environmentId, string storeId, string container, string? prefix, string? continuation, int pageSize, CancellationToken ct)
    {
        var (store, s, error) = Open(environmentId, storeId);
        if (store is null) return (null, error);
        if (ContainerAllowed(s!, container) is { } denied) return (null, denied);
        try
        {
            var (items, next) = await store.ListAsync(container, string.IsNullOrEmpty(prefix) ? null : prefix, continuation, Math.Clamp(pageSize, 1, 500), ct);
            return (new BlobListing(container, prefix, items, next, Now, next is null ? $"{items.Count} entries — end of listing" : $"{items.Count} entries — more available (continue to load)"), null);
        }
        catch (BlobStoreAccessException ex) { return (null, ex.Message); }
    }

    public async Task<(BlobProperties? Properties, string? Error)> PropertiesAsync(string environmentId, string storeId, string container, string name, CancellationToken ct)
    {
        var (store, s, error) = Open(environmentId, storeId);
        if (store is null) return (null, error);
        if (ContainerAllowed(s!, container) is { } denied) return (null, denied);
        try
        {
            var p = await store.PropertiesAsync(container, name, ct);
            return p is null ? (null, "The blob does not exist.") : (p with { Metadata = Redact(p.Metadata) }, null);
        }
        catch (BlobStoreAccessException ex) { return (null, ex.Message); }
    }

    private static Dictionary<string, string> Redact(Dictionary<string, string> metadata) => metadata.Take(64).ToDictionary(kv => kv.Key,
        kv => MessagingObservationRules.IsSensitiveKey(kv.Key) ? MessagingObservationRules.Mask : MessagingObservationRules.RedactText(kv.Value.Length > 500 ? kv.Value[..500] : kv.Value).Text);

    // ── Preview ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<(BlobPreview? Preview, string? Error)> PreviewAsync(string environmentId, string storeId, string container, string name, CancellationToken ct)
    {
        var (store, s, error) = Open(environmentId, storeId);
        if (store is null) return (null, error);
        if (ContainerAllowed(s!, container) is { } denied) return (null, denied);
        try
        {
            var p = await store.PropertiesAsync(container, name, ct);
            if (p is null) return (null, "The blob does not exist.");
            var kind = KindOf(name, p.ContentType);
            if (kind == BlobPreviewKind.Binary) return (new BlobPreview { Kind = kind, Diagnostics = ["Binary content: metadata only (never rendered)."] }, null);
            if (kind == BlobPreviewKind.Parquet && p.Size > ParquetPreviewBytes)
                return (new BlobPreview { Kind = kind, Diagnostics = [$"Larger than the bounded Parquet read ({ParquetPreviewBytes / 1024 / 1024} MB): not previewed (a partial Parquet file is never treated as valid)."] }, null);
            var (bytes, complete) = await store.ReadAsync(container, name, kind == BlobPreviewKind.Parquet ? ParquetPreviewBytes : TextPreviewBytes, ct);
            return (Build(kind, bytes, complete), null);
        }
        catch (BlobStoreAccessException ex) { return (null, ex.Message); }
    }

    internal static BlobPreviewKind KindOf(string name, string? contentType)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        var ct = (contentType ?? "").ToLowerInvariant();
        if (ext == ".parquet" || ct.Contains("parquet")) return BlobPreviewKind.Parquet;
        if (ext is ".ndjson" or ".jsonl" || ct.Contains("ndjson")) return BlobPreviewKind.Ndjson;
        if (ext == ".json" || ct.Contains("json")) return BlobPreviewKind.Json;
        if (ext == ".xml" || ct.Contains("xml")) return BlobPreviewKind.Xml;
        if (ext == ".csv" || ct.Contains("csv")) return BlobPreviewKind.Csv;
        if (ext is ".txt" or ".log" || ct.StartsWith("text/", StringComparison.Ordinal)) return BlobPreviewKind.Text;
        return BlobPreviewKind.Binary;
    }

    internal static BlobPreview Build(BlobPreviewKind kind, byte[] bytes, bool complete)
    {
        var diagnostics = new List<string>();
        if (!complete) diagnostics.Add($"Only the first {bytes.Length} bytes were read: the preview is partial and syntax is not judged as a whole.");
        switch (kind)
        {
            case BlobPreviewKind.Parquet:
                try
                {
                    var file = ParquetFile.Open(bytes);
                    var readable = file.Leaves.Where(l => l.NotReadable is null).Take(12).ToList();
                    var rows = new List<List<string?>>();
                    if (file.RowGroups.Count > 0 && readable.Count > 0)
                    {
                        var columns = readable.Select(l => file.ReadColumn(0, l.Index)).ToList();
                        for (var r = 0; r < Math.Min(PreviewRows, file.RowGroups[0].Rows); r++) rows.Add([.. columns.Select(c => c[r] is null ? null : MessagingObservationRules.RedactText(ParquetFile.Format(c[r]) ?? "").Text)]);
                    }
                    return new BlobPreview
                    {
                        Kind = kind, Complete = true, BytesRead = bytes.Length, SyntaxValid = true, Columns = [.. file.Leaves.Select(l => l.Path)], ParquetRows = file.Rows, Rows = rows,
                        RecordsRead = rows.Count, Diagnostics = [.. diagnostics, $"Parquet (same engine as the Parquet Data Explorer): {file.Rows} rows in the file's metadata; first {rows.Count} rows of row group 1 shown (redacted)."],
                    };
                }
                catch (ParquetFormatException ex) { return new BlobPreview { Kind = kind, BytesRead = bytes.Length, SyntaxValid = false, Diagnostics = [.. diagnostics, ex.Message] }; }
            case BlobPreviewKind.Json:
            {
                var text = Encoding.UTF8.GetString(bytes);
                bool? valid = null; var columns = new List<string>();
                try
                {
                    using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
                    valid = true;
                    if (doc.RootElement.ValueKind == JsonValueKind.Object) columns = [.. doc.RootElement.EnumerateObject().Select(p => p.Name)];
                    else if (doc.RootElement.ValueKind == JsonValueKind.Array) diagnostics.Add($"Top-level array of {doc.RootElement.GetArrayLength()} elements.");
                }
                catch (JsonException ex) { valid = complete ? false : null; diagnostics.Add(complete ? $"Not valid JSON: {ex.Message}" : "JSON not judged (partial read)."); }
                var redacted = valid == true && MessagingObservationRules.RedactJson(text) is { } r ? r : MessagingObservationRules.RedactText(text);
                return Bounded(kind, redacted.Item1, redacted.Item2, bytes.Length, complete, valid, diagnostics, columns, null);
            }
            case BlobPreviewKind.Xml:
            {
                var text = Encoding.UTF8.GetString(bytes);
                bool? valid = null; var columns = new List<string>();
                try
                {
                    using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = TextPreviewBytes * 2 });
                    var depth = 0;
                    while (reader.Read())
                        if (reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && !columns.Contains(reader.LocalName)) columns.Add(reader.LocalName);
                        else if (reader.NodeType == XmlNodeType.Element) depth = Math.Max(depth, reader.Depth);
                    valid = true;
                    diagnostics.Add("Well-formed XML (no XSD applied: \"valid\" against a schema needs a selected XSD).");
                }
                catch (XmlException ex) { valid = complete ? false : null; diagnostics.Add(complete ? $"Not well-formed XML: {ex.Message}" : "XML not judged (partial read)."); }
                var (preview, _, redactions, _) = MessagingObservationRules.Preview(text, "application/xml");
                return Bounded(kind, preview ?? "", redactions, bytes.Length, complete, valid, diagnostics, columns, null, alreadyBounded: true);
            }
            case BlobPreviewKind.Csv:
            {
                var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
                var lines = text.Split('\n');
                if (!complete && lines.Length > 1) lines = lines[..^1]; // the last line of a partial read may be cut
                lines = lines.Where(l => l.Length > 0).ToArray();
                var delimiter = new[] { ',', ';', '\t', '|' }.OrderByDescending(d => lines.FirstOrDefault()?.Count(c => c == d) ?? 0).First();
                var header = lines.Length > 0 ? Split(lines[0], delimiter) : [];
                var widths = lines.Skip(1).Select(l => Split(l, delimiter).Count).ToList();
                if (widths.Any(w => w != header.Count)) diagnostics.Add($"{widths.Count(w => w != header.Count)} row(s) have a different number of fields than the header.");
                diagnostics.Add($"Delimiter '{(delimiter == '\t' ? "\\t" : delimiter.ToString())}'; {(complete ? "all" : "the first")} {widths.Count} data row(s) read{(complete ? "" : " (not the blob's total)")}.");
                var shown = string.Join("\n", lines.Take(PreviewRows + 1).Select(l => string.Join(delimiter, Split(l, delimiter).Select(FormulaSafe))));
                var (redacted, r) = MessagingObservationRules.RedactText(shown);
                return Bounded(kind, redacted, r, bytes.Length, complete, widths.All(w => w == header.Count), diagnostics, header, widths.Count);
            }
            case BlobPreviewKind.Ndjson:
            {
                var text = Encoding.UTF8.GetString(bytes);
                var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                if (!complete && lines.Count > 1) lines.RemoveAt(lines.Count - 1);
                var properties = new List<string>(); var bad = new List<int>(); var count = 0;
                for (var i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Length == 0) continue;
                    count++;
                    try
                    {
                        using var doc = JsonDocument.Parse(lines[i], new JsonDocumentOptions { MaxDepth = 64 });
                        if (doc.RootElement.ValueKind == JsonValueKind.Object) foreach (var p in doc.RootElement.EnumerateObject()) if (!properties.Contains(p.Name)) properties.Add(p.Name);
                    }
                    catch (JsonException) { bad.Add(i + 1); }
                }
                if (bad.Count > 0) diagnostics.Add($"{bad.Count} malformed line(s): {string.Join(", ", bad.Take(10))}{(bad.Count > 10 ? ", …" : "")}.");
                diagnostics.Add($"{count} line(s) read{(complete ? "" : " (not the blob's total)")}.");
                var shown = string.Join("\n", lines.Where(l => l.Length > 0).Take(PreviewRows).Select(l => MessagingObservationRules.RedactJson(l) is { } j ? j.Json.Replace("\n", " ").Replace("  ", "") : MessagingObservationRules.RedactText(l).Text));
                return Bounded(kind, shown, 0, bytes.Length, complete, bad.Count == 0, diagnostics, properties, count);
            }
            default:
            {
                var (redacted, r) = MessagingObservationRules.RedactText(Encoding.UTF8.GetString(bytes));
                return Bounded(BlobPreviewKind.Text, redacted, r, bytes.Length, complete, null, diagnostics, [], null);
            }
        }
    }

    private static BlobPreview Bounded(BlobPreviewKind kind, string text, int redactions, long bytes, bool complete, bool? valid, List<string> diagnostics, List<string> columns, long? records, bool alreadyBounded = false)
    {
        const int max = 16 * 1024;
        var truncated = !complete || text.Length > max;
        return new BlobPreview
        {
            Kind = kind, Text = text.Length > max ? text[..max] : text, Truncated = truncated, BytesRead = bytes, RedactedValues = redactions, SyntaxValid = complete ? valid : null,
            Diagnostics = diagnostics, Columns = columns, RecordsRead = records, Complete = complete,
        };
    }

    private static List<string> Split(string line, char delimiter)
    {
        var fields = new List<string>(); var current = new StringBuilder(); var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; } else quoted = !quoted; }
            else if (c == delimiter && !quoted) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields;
    }

    /// <summary>A cell starting with = + - @ is shown with a leading apostrophe so it can never act as a spreadsheet formula when copied.</summary>
    private static string FormulaSafe(string cell) => cell.Length > 0 && cell[0] is '=' or '+' or '-' or '@' && !double.TryParse(cell, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? "'" + cell : cell;

    // ── Expectations, correlation, observations ──────────────────────────────────────────────────────────────────────────

    public async Task<(BlobValidation? Validation, BlobObservation? Saved, string? Error)> ValidateAsync(BlobValidateRequest request, string initiator, CancellationToken ct)
    {
        var e = request.Expectation;
        var (store, s, error) = Open(request.EnvironmentId, request.StoreId);
        if (store is null) return (null, null, error);
        if (ContainerAllowed(s!, e.Container) is { } denied) return (null, null, denied);
        var checks = new List<BlobCheck>();
        try
        {
            BlobProperties? p;
            if (e.Path.EndsWith('/'))
            {
                var candidates = new List<BlobItemView>(); string? next = null;
                do { var page = await store.ListAsync(e.Container, e.Path, next, 500, ct, flat: true); candidates.AddRange(page.Items.Where(i => !i.IsPrefix)); next = page.Continuation; } while (next is not null && candidates.Count < 5000);
                var newest = candidates.Where(c => e.NotBefore is null || c.LastModified >= e.NotBefore).OrderByDescending(c => c.LastModified).FirstOrDefault();
                p = newest is null ? null : await store.PropertiesAsync(e.Container, newest.Name, ct);
                checks.Add(new(BlobCheckLevel.Exists, p is null ? BlobCheckOutcome.Fail : BlobCheckOutcome.Pass,
                    p is null ? $"No blob under {e.Path}{(e.NotBefore is { } nb ? $" newer than {nb:u}" : "")}." : $"{candidates.Count} blob(s) under the prefix; the newest{(e.NotBefore is null ? "" : " in the observation window")} is {p.Name}."));
            }
            else
            {
                p = await store.PropertiesAsync(e.Container, e.Path, ct);
                checks.Add(new(BlobCheckLevel.Exists, p is null ? BlobCheckOutcome.Fail : BlobCheckOutcome.Pass, p is null ? "The blob does not exist." : "The blob exists."));
                if (p is not null && e.NotBefore is { } nb && p.LastModified < nb)
                    checks.Add(new(BlobCheckLevel.Metadata, BlobCheckOutcome.Fail, $"Last modified {p.LastModified:u} is before the observation window ({nb:u}): not produced by this run."));
            }
            if (p is null) return (new BlobValidation { StoreId = request.StoreId, Container = e.Container, Checks = checks, CorrelationDetail = "Nothing to correlate." }, null, null);

            var metaProblems = new List<string>();
            if (e.ContentType is { } ctExpected && !string.Equals(p.ContentType, ctExpected, StringComparison.OrdinalIgnoreCase)) metaProblems.Add($"content type {p.ContentType ?? "none"} ≠ {ctExpected}");
            if (e.MinSize is { } min && p.Size < min) metaProblems.Add($"size {p.Size} < {min}");
            if (e.MaxSize is { } max && p.Size > max) metaProblems.Add($"size {p.Size} > {max}");
            if (e.ContentType is not null || e.MinSize is not null || e.MaxSize is not null)
                checks.Add(new(BlobCheckLevel.Metadata, metaProblems.Count == 0 ? BlobCheckOutcome.Pass : BlobCheckOutcome.Fail, metaProblems.Count == 0 ? "Content type and size as expected." : string.Join("; ", metaProblems)));

            var kind = e.Format ?? KindOf(p.Name, p.ContentType);
            var (bytes, complete) = await store.ReadAsync(e.Container, p.Name, kind == BlobPreviewKind.Parquet ? ParquetPreviewBytes : TextPreviewBytes, ct);
            var preview = Build(kind, bytes, complete);
            checks.Add(new(BlobCheckLevel.Readable, BlobCheckOutcome.Pass, $"{bytes.Length} byte(s) read{(complete ? " (whole blob)" : " (partial)")}."));
            checks.Add(new(BlobCheckLevel.Syntax, preview.SyntaxValid switch { true => BlobCheckOutcome.Pass, false => BlobCheckOutcome.Fail, _ => BlobCheckOutcome.NotAssessed },
                preview.SyntaxValid is null ? "Not judged (binary or partial read)." : string.Join(" ", preview.Diagnostics)));
            var missing = e.RequiredFields.Where(f => !preview.Columns.Contains(f, StringComparer.Ordinal)).ToList();
            var records = kind == BlobPreviewKind.Parquet ? preview.ParquetRows : preview.Complete ? preview.RecordsRead : null;
            var content = new List<string>();
            if (e.RequiredFields.Count > 0) content.Add(missing.Count == 0 ? $"required fields present ({string.Join(", ", e.RequiredFields)})" : $"missing fields: {string.Join(", ", missing)}");
            if (e.ExpectedRecords is { } expected) content.Add(records is null ? "record count not measurable from a partial read" : records == expected ? $"{records} records as expected" : $"{records} records ≠ {expected}");
            if (content.Count > 0)
                checks.Add(new(BlobCheckLevel.Content, missing.Count > 0 || (e.ExpectedRecords is { } x && records is { } m && m != x) ? BlobCheckOutcome.Fail
                    : e.ExpectedRecords is not null && records is null ? BlobCheckOutcome.NotAssessed : BlobCheckOutcome.Pass, string.Join("; ", content)));

            var (state, detail, run) = await CorrelateAsync(request.EnvironmentId, e.RunId, p, ct);
            checks.Add(new(BlobCheckLevel.Correlation, e.RunId is null ? BlobCheckOutcome.NotAssessed : state == BlobCorrelationState.Confirmed ? BlobCheckOutcome.Pass : BlobCheckOutcome.Fail, detail));
            var validation = new BlobValidation { StoreId = request.StoreId, Container = e.Container, BlobName = p.Name, Checks = checks, Correlation = state, CorrelationDetail = detail, CorrelatedRunId = run };
            BlobObservation? saved = null;
            if (request.Save)
            {
                saved = new BlobObservation
                {
                    EnvironmentId = request.EnvironmentId, StoreId = request.StoreId, StoreKind = s!.Kind, Container = e.Container, BlobName = p.Name, ETag = p.ETag, Size = p.Size,
                    Sha256OfBytesRead = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), WholeBlobRead = complete, Metadata = Redact(p.Metadata),
                    Validation = validation, ObservedAt = Now, ObservedBy = initiator,
                };
                db.BlobObservations.Add(new BlobObservationRecord { Id = saved.Id, EnvironmentId = saved.EnvironmentId, BlobName = saved.BlobName[..Math.Min(400, saved.BlobName.Length)], ObservedAt = saved.ObservedAt, DocumentJson = JsonSerializer.Serialize(saved, Json) });
                await db.SaveChangesAsync(ct);
            }
            return (validation, saved, null);
        }
        catch (BlobStoreAccessException ex) { return (null, null, ex.Message); }
    }

    /// <summary>Confirmed only when the blob's metadata or name carries an execution id ("{run}.{sequence}.{attempt}") that the named run sent. Never by time.</summary>
    private async Task<(BlobCorrelationState, string, Guid?)> CorrelateAsync(string environmentId, Guid? runId, BlobProperties p, CancellationToken ct)
    {
        if (runId is null) return (BlobCorrelationState.NotCorrelated, "No run named: correlation not assessed.", null);
        var run = (await executions.ListAsync(new ExecutionRunQuery(environmentId, Take: 500), ct)).FirstOrDefault(r => r.RunId == runId);
        if (run is null) return (BlobCorrelationState.NotCorrelated, "The named run is not an Execution Evidence run of this environment.", null);
        var detail = await executions.GetAsync(run.RunId, new ExecutionCallQuery(Take: 1000), ct);
        var ids = detail?.Calls.SelectMany(c => c.Attempts.Select(a => $"{run.RunId:N}.{c.Sequence}.{a.AttemptNumber}")).ToHashSet(StringComparer.Ordinal) ?? [];
        var fromMetadata = p.Metadata.Where(kv => ExecutionIdKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Value.Trim()).FirstOrDefault(ids.Contains);
        if (fromMetadata is not null) return (BlobCorrelationState.Confirmed, $"The blob's metadata carries execution id {fromMetadata} sent by run {run.RunId:N}.", run.RunId);
        var inName = ids.FirstOrDefault(id => p.Name.Contains(id, StringComparison.Ordinal));
        if (inName is not null) return (BlobCorrelationState.Confirmed, $"The blob's name contains execution id {inName} sent by run {run.RunId:N}.", run.RunId);
        return (BlobCorrelationState.NotCorrelated, "Neither the metadata nor the name carries an execution id of the run; a matching time or name pattern is not proof.", null);
    }

    public async Task<List<BlobObservation>> ObservationsAsync(string environmentId, CancellationToken ct) =>
        (await db.BlobObservations.AsNoTracking().Where(r => r.EnvironmentId == environmentId).OrderByDescending(r => r.ObservedAt).Take(200).ToListAsync(ct))
            .Select(r => JsonSerializer.Deserialize<BlobObservation>(r.DocumentJson, Json)!).ToList();

    /// <summary>Deletes the local observation only. The blob is never touched (no store method can delete).</summary>
    public async Task<bool> DeleteObservationAsync(string environmentId, Guid id, CancellationToken ct)
    {
        var row = await db.BlobObservations.FirstOrDefaultAsync(r => r.Id == id && r.EnvironmentId == environmentId, ct);
        if (row is null) return false;
        db.BlobObservations.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
