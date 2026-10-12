using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using BirkNext.Api.Services.Integrations;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.AltinnTesting;

public sealed class AltinnContractException(string message) : Exception(message);

/// <summary>A domain's expectations of an app's form: which fields it fills (exact schema paths) — supplied by the domain pack, never inferred.</summary>
public sealed record AltinnFormFieldExpectation(string Key, string Label, string Path, string JsonType, IReadOnlyList<string>? Formats = null);

/// <summary>A domain pack's binding to one Altinn app form (e.g. Skolenærvær). The generic contract service evaluates it; it never guesses fields.</summary>
public interface IAltinnFormBinding
{
    IReadOnlyList<AltinnFormFieldExpectation> Fields { get; }
    string BindingBasis { get; }
}

/// <summary>
/// The Altinn app contract of one environment, from two read-only sources: the app's source repository in a Source Analysis snapshot
/// (authoritative; uploaded through Source Analysis, which owns source evidence — this service opens no archive and accepts no upload) and
/// the app's public TT02 metadata (two anonymous GETs: application metadata and the form data type's JSON schema). Nothing is submitted and
/// nobody is authenticated during discovery. A part neither source supplies stays missing; ambiguity (several form data types, an unmatched
/// schema, a process with gateways) is reported, not resolved by guessing. Only the derived contract facts and fingerprints are kept, with the
/// Integration Quality Review message-flow configuration.
/// </summary>
public sealed class AltinnAppContractService(
    IHttpClientFactory http,
    AltinnTestingPolicy policy,
    IIntegrationMessageFlowStore messageFlows,
    IReviewSourceEvidenceProvider sources,
    IAltinnFormBinding binding,
    TimeProvider clock)
{
    private const long MaxDocumentBytes = 2 * 1024 * 1024;
    private const string BpmnNs = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private const string AltinnNs = "http://altinn.no/process";
    private static readonly string[] SupportedTaskTypes = ["data"];

    public async Task<AltinnAppContractView> ViewAsync(string environmentId, CancellationToken ct)
    {
        var package = await messageFlows.GetAsync(environmentId, ct);
        return Evaluate(environmentId, package.AltinnConfiguration.AppIdentifier, package.AppContract) with
        {
            SourceCandidates = await CandidatesAsync(environmentId, package.AltinnConfiguration.AppIdentifier?.Trim(), ct),
        };
    }

    /// <summary>Reads the public TT02 metadata of the configured, approved app. Read-only GETs without credentials.</summary>
    public async Task<AltinnAppContractView> DiscoverPublicAsync(string environmentId, CancellationToken ct)
    {
        var package = await messageFlows.GetAsync(environmentId, ct);
        var appId = package.AltinnConfiguration.AppIdentifier?.Trim();
        var approved = policy.ApprovedApp(appId, out var reason) ?? throw new AltinnContractException(reason);
        if (AltinnTestingPolicy.AltinnEnvironmentBlock(package.AltinnConfiguration) is { } blocked) throw new AltinnContractException(blocked);
        var client = http.CreateClient(AltinnTestingRequestGuard.ClientName);
        var baseUri = AltinnTestingPolicy.AppBase(approved.Org, approved.App);
        var documents = new List<(string Name, string Text)>();

        var metadataText = await GetTextAsync(client, new Uri(baseUri, "api/v1/applicationmetadata"), ct);
        documents.Add(("api/v1/applicationmetadata", metadataText));
        var metadata = ParseMetadata(metadataText, appId!);
        AltinnSchemaDocument? schema = null;
        var notes = new List<string>(metadata.Notes.Where(item => !item.StartsWith("classRef:", StringComparison.Ordinal)))
            { "The process definition is not published by the app; it comes from the app's source in Source Analysis (App/config/process/process.bpmn)." };
        if (metadata.FormDataTypeId is { } dataType)
        {
            var schemaText = await GetTextAsync(client, new Uri(baseUri, $"api/jsonschema/{Uri.EscapeDataString(dataType)}"), ct);
            documents.Add(($"api/jsonschema/{dataType}", schemaText));
            schema = ParseSchema(schemaText);
        }
        var snapshot = metadata with
        {
            Source = AltinnContractSourceKind.PublicTt02Metadata, AppId = appId!, CapturedAt = clock.GetUtcNow(), Fingerprint = Fingerprint(documents),
            Documents = documents.Select(item => item.Name).ToArray(), Fields = schema?.Fields, ProcessTasks = null, Notes = notes,
            XmlRootElement = schema?.XmlRootElement, XmlNamespace = schema?.XmlNamespace, XmlElementsQualified = schema?.XmlElementsQualified ?? false,
        };
        var record = (package.AppContract ?? new AltinnAppContractRecord(null, null)) with { PublicMetadata = snapshot };
        await messageFlows.SaveAppContractAsync(environmentId, record, ct);
        return Evaluate(environmentId, appId, record) with { SourceCandidates = await CandidatesAsync(environmentId, appId, ct) };
    }

    /// <summary>
    /// Uses the configured app's source from a Source Analysis snapshot (the newest one that contains it, or the one chosen) as the
    /// authoritative contract. The app repository is uploaded through Source Analysis like any other source archive.
    /// </summary>
    public async Task<AltinnAppContractView> UseSourceAnalysisAsync(string environmentId, Guid? snapshotId, CancellationToken ct)
    {
        var package = await messageFlows.GetAsync(environmentId, ct);
        var appId = package.AltinnConfiguration.AppIdentifier?.Trim();
        _ = policy.ApprovedApp(appId, out var reason) ?? throw new AltinnContractException(reason);
        if (!sources.SourceAnalysisEnabled) throw new AltinnContractException(ReviewSourceEvidenceProvider.Disabled);
        IqrSourceSnapshot? snapshot;
        if (snapshotId is { } id)
            snapshot = await sources.ResolveAsync(environmentId, id, ct) ?? throw new AltinnContractException("The selected Source Analysis snapshot was not found.");
        else
            snapshot = (await sources.ListAsync(environmentId, ct)).FirstOrDefault(item => AppsOf(item).Any(app => app.AppId == appId))
                ?? throw new AltinnContractException($"No Source Analysis snapshot of this environment contains the source of {appId}. Upload the app repository in Source Analysis.");
        var app = AppsOf(snapshot).FirstOrDefault(item => item.AppId == appId)
            ?? throw new AltinnContractException($"The snapshot {snapshot.Archive.FileName} does not contain the source of {appId}.");
        var record = (package.AppContract ?? new AltinnAppContractRecord(null, null)) with
        {
            SourceAnalysis = app with { SourceSnapshotId = snapshot.Id, SourceArchive = snapshot.Archive.FileName },
        };
        await messageFlows.SaveAppContractAsync(environmentId, record, ct);
        return Evaluate(environmentId, appId, record) with { SourceCandidates = await CandidatesAsync(environmentId, appId, ct) };
    }

    private async Task<IReadOnlyList<AltinnSourceCandidate>> CandidatesAsync(string environmentId, string? appId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(appId) || !sources.SourceAnalysisEnabled) return [];
        return (await sources.ListAsync(environmentId, ct))
            .Where(item => AppsOf(item).Any(app => app.AppId == appId))
            .Select(item => new AltinnSourceCandidate(item.Id, item.Archive.FileName, item.AnalyzedAt, AppsOf(item).Select(app => app.AppId).ToArray()))
            .Take(20).ToArray();
    }

    private static IReadOnlyList<AltinnContractSnapshot> AppsOf(IqrSourceSnapshot snapshot) => snapshot.EvidenceDomains?.Contracts.AltinnApps ?? [];

    /// <summary>The effective contract: for each part, the Source Analysis app source wins over public metadata. Pure; used by readiness and execution alike.</summary>
    public AltinnAppContractView Evaluate(string environmentId, string? configuredAppId, AltinnAppContractRecord? record)
    {
        var appId = configuredAppId?.Trim();
        var approved = policy.ApprovedApp(appId, out var appDetail) is not null;
        // A snapshot for another app id (the configuration changed since) is not evidence for this app.
        AltinnContractSnapshot? Usable(AltinnContractSnapshot? snapshot) => snapshot is not null && string.Equals(snapshot.AppId, appId, StringComparison.Ordinal) ? snapshot : null;
        var uploaded = Usable(record?.SourceAnalysis);
        var published = Usable(record?.PublicMetadata);
        var missing = new List<string>();
        if (!approved) missing.Add(appDetail);

        var metadataSource = uploaded?.HasApplicationMetadata == true ? uploaded : published?.HasApplicationMetadata == true ? published : null;
        var dataTypeSource = uploaded?.FormDataTypeId is not null ? uploaded : published?.FormDataTypeId is not null ? published : null;
        var schemaSource = uploaded?.Fields is not null ? uploaded : published?.Fields is not null ? published : null;
        var processSource = uploaded?.ProcessTasks is not null ? uploaded : published?.ProcessTasks is not null ? published : null;
        var dataType = dataTypeSource?.FormDataTypeId;
        if (schemaSource is not null && dataTypeSource is not null && schemaSource != dataTypeSource && schemaSource.FormDataTypeId != dataType)
        {
            missing.Add("The data model schema was read for a different form data type than the one in effect; supply both from the same source.");
            schemaSource = null;
        }
        var fields = schemaSource?.Fields ?? [];
        var tasks = processSource?.ProcessTasks ?? [];

        var parts = new List<AltinnContractPartStatus>
        {
            Part(AltinnContractPartKind.ApplicationMetadata, metadataSource, "Application metadata (data types, allowed content types).", "No application metadata from either source."),
            Part(AltinnContractPartKind.FormDataType, dataTypeSource, dataType is null ? "" : $"Form data type {dataType}.",
                (uploaded ?? published)?.FormDataTypeCandidates is { Count: > 1 } candidates ? $"Ambiguous: several app-logic data types ({string.Join(", ", candidates)})." : "No single app-logic (form) data type was found."),
            Part(AltinnContractPartKind.DataModelSchema, schemaSource, $"{fields.Count} schema fields.", "No data model schema for the form data type."),
            Part(AltinnContractPartKind.ProcessDefinition, processSource, $"{tasks.Count} process task(s): {string.Join(" → ", tasks.Select(task => $"{task.TaskId} ({task.TaskType})"))}.",
                "No process definition; add the app's source (with App/config/process/process.bpmn) through Source Analysis — the process is not part of the public metadata."),
        };
        missing.AddRange(parts.Where(part => !part.Present).Select(part => part.Detail));

        var unsupported = tasks.Where(task => !SupportedTaskTypes.Contains(task.TaskType, StringComparer.Ordinal)).ToArray();
        if (unsupported.Length > 0)
            missing.Add($"Process task type(s) {string.Join(", ", unsupported.Select(task => $"{task.TaskId} ({task.TaskType})"))} need an action contract BirkNext does not have; only data tasks are submitted.");
        if (processSource is not null && tasks.Count == 0) missing.Add("The process definition has no task.");
        var contentTypes = dataTypeSource?.AllowedContentTypes ?? [];
        var xmlReady = schemaSource?.XmlRootElement is { Length: > 0 } && schemaSource.XmlNamespace is { Length: > 0 };
        string? contentType = dataTypeSource is null ? null
            : contentTypes.Count == 0 || contentTypes.Contains("application/json", StringComparer.OrdinalIgnoreCase) ? "application/json"
            : contentTypes.Contains("application/xml", StringComparer.OrdinalIgnoreCase) && xmlReady ? "application/xml"
            : null;
        if (dataTypeSource is not null && contentType is null)
            missing.Add(contentTypes.Contains("application/xml", StringComparer.OrdinalIgnoreCase)
                ? "The form data type allows only application/xml, and the form schema declares no XML root element and namespace to write it with."
                : $"The form data type allows only {string.Join(", ", contentTypes)}; BirkNext submits the form as application/json or application/xml only.");

        var bindings = binding.Fields.Select(expected =>
        {
            var field = fields.FirstOrDefault(item => string.Equals(item.Path, expected.Path, StringComparison.Ordinal));
            var compatible = field is not null && (field.Type == expected.JsonType || (expected.JsonType == "number" && field.Type == "integer"))
                && (expected.Formats is null || expected.Formats.Contains(field.Format ?? "", StringComparer.Ordinal));
            return new AltinnFieldBinding(expected.Key, expected.Label, expected.Path, expected.JsonType, compatible,
                field is null ? (schemaSource is null ? "Not checked: no schema." : $"Not in the app's schema at {expected.Path}.")
                : compatible ? $"Found: {field.Type}{(field.Format is { } format ? $" ({format})" : "")}{(field.Required ? ", required" : "")}."
                : field.Type != expected.JsonType && !(expected.JsonType == "number" && field.Type == "integer") ? $"Found at {expected.Path} but as {field.Type}, not {expected.JsonType}."
                : $"Found at {expected.Path} but with format {field.Format ?? "(none)"}, not {string.Join(" or ", expected.Formats!)}.");
        }).ToArray();
        if (schemaSource is not null && bindings.Any(item => !item.Present))
            missing.Add($"The form schema does not contain every field BirkNext fills ({string.Join(", ", bindings.Where(item => !item.Present).Select(item => item.ExpectedPath))}). {binding.BindingBasis}");
        var bound = binding.Fields.Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
        var requiredUnbound = fields.Where(item => item.Required && !bound.Contains(item.Path) && !fields.Any(child => child.Path.StartsWith(item.Path + ".", StringComparison.Ordinal))).ToArray();

        return new AltinnAppContractView
        {
            EnvironmentId = environmentId, ConfiguredAppId = appId, AppApproved = approved, AppDetail = appDetail, FormDataTypeId = dataType, Parts = parts,
            Bindings = bindings, Fields = fields, ProcessTasks = tasks, RequiredUnboundFields = requiredUnbound, SubmissionContentType = contentType,
            XmlRootElement = schemaSource?.XmlRootElement, XmlNamespace = schemaSource?.XmlNamespace, XmlElementsQualified = schemaSource?.XmlElementsQualified ?? false,
            Sources = new[] { record?.SourceAnalysis, record?.PublicMetadata }.OfType<AltinnContractSnapshot>().ToArray(),
            Complete = missing.Count == 0, Missing = missing.Distinct().ToArray(),
        };

        static AltinnContractPartStatus Part(AltinnContractPartKind kind, AltinnContractSnapshot? source, string present, string absent) =>
            new(kind, source is not null, source?.Source, source is not null ? present : absent);
    }

    // ── Parsing (pure, internal for tests) ────────────────────────────────────────────────────────────────────────────

    internal static AltinnContractSnapshot ParseMetadata(string json, string expectedAppId)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        if (!string.Equals(id, expectedAppId, StringComparison.Ordinal))
            throw new AltinnContractException($"The application metadata is for {id ?? "an unknown app"}, not the configured app {expectedAppId}.");
        var candidates = new List<(string Id, IReadOnlyList<string> Types, string? ClassRef)>();
        if (root.TryGetProperty("dataTypes", out var dataTypes) && dataTypes.ValueKind == JsonValueKind.Array)
            foreach (var item in dataTypes.EnumerateArray())
            {
                if (!item.TryGetProperty("appLogic", out var logic) || logic.ValueKind != JsonValueKind.Object) continue;
                var classRef = logic.TryGetProperty("classRef", out var cls) ? cls.GetString() : null;
                if (string.IsNullOrWhiteSpace(classRef) || item.TryGetProperty("id", out var dataTypeId) is false || dataTypeId.GetString() is not { Length: > 0 } dataId) continue;
                var types = item.TryGetProperty("allowedContentTypes", out var allowed) && allowed.ValueKind == JsonValueKind.Array
                    ? allowed.EnumerateArray().Select(type => type.GetString() ?? "").Where(type => type.Length > 0).ToArray() : [];
                candidates.Add((dataId, types, classRef));
            }
        var single = candidates.Count == 1 ? candidates[0] : default;
        return new AltinnContractSnapshot
        {
            HasApplicationMetadata = true, FormDataTypeId = candidates.Count == 1 ? single.Id : null, FormDataTypeCandidates = candidates.Select(item => item.Id).ToArray(),
            AllowedContentTypes = candidates.Count == 1 ? single.Types : [],
            Notes = candidates.Count == 1 ? [$"classRef:{single.ClassRef}"] : candidates.Count == 0 ? ["The app has no app-logic (form) data type."] : [],
        };
    }

    /// <summary>A form schema reduced to what submission needs: leaf fields in schema order and, for XML apps, the root element and namespace.</summary>
    internal sealed record AltinnSchemaDocument(IReadOnlyList<AltinnSchemaField> Fields, string? XmlRootElement, string? XmlNamespace, bool XmlElementsQualified);

    internal static IReadOnlyList<AltinnSchemaField> FlattenSchema(string json) => ParseSchema(json).Fields;

    /// <summary>
    /// Flattens an Altinn JSON schema to leaf fields with dot paths, in schema order. Handles what Altinn apps publish: a document that is
    /// itself a JSON string (the app's jsonschema endpoint double-encodes it), local $ref, single oneOf/allOf, multi-part allOf
    /// (XSD extension: base type first), and integer XSD types carried as "number" with an @xsdType. Reads @xsdRootElement,
    /// @xsdNamespaces and ElementFormDefault so an XML-only app can be written exactly as its schema says.
    /// </summary>
    internal static AltinnSchemaDocument ParseSchema(string json)
    {
        using var outer = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        using var inner = outer.RootElement.ValueKind == JsonValueKind.String ? JsonDocument.Parse(outer.RootElement.GetString() ?? "{}", new JsonDocumentOptions { MaxDepth = 128 }) : null;
        var root = (inner ?? outer).RootElement.Clone();
        var fields = new List<AltinnSchemaField>();
        Walk(root, "", true, 0);
        var rootElement = root.TryGetProperty("@xsdRootElement", out var element) ? element.GetString() : null;
        var ns = root.TryGetProperty("@xsdNamespaces", out var namespaces) && namespaces.ValueKind == JsonValueKind.Object && namespaces.TryGetProperty("", out var defaultNs) ? defaultNs.GetString() : null;
        if (ns is null && root.TryGetProperty("@xsdSchemaAttributes", out var attrs) && attrs.TryGetProperty("TargetNamespace", out var target)) ns = target.GetString();
        var qualified = root.TryGetProperty("@xsdSchemaAttributes", out var schemaAttributes) && schemaAttributes.TryGetProperty("ElementFormDefault", out var form)
            && string.Equals(form.GetString(), "Qualified", StringComparison.OrdinalIgnoreCase);
        return new AltinnSchemaDocument(fields, rootElement, ns, qualified);

        void Walk(JsonElement node, string path, bool required, int depth)
        {
            if (depth > 16 || fields.Count > 5000) return;
            node = Resolve(root, node);
            if (Collect(node, 0) is { } shape)
            {
                if (path.Length > 0) fields.Add(new AltinnSchemaField(path, "object", null, required));
                foreach (var (name, value) in shape.Properties)
                    Walk(value, path.Length == 0 ? name : $"{path}.{name}", required && shape.Required.Contains(name), depth + 1);
                return;
            }
            var type = Type(node);
            if (type == "array" && node.TryGetProperty("items", out var items)) { Walk(items, path + "[]", false, depth + 1); return; }
            if (path.Length > 0) fields.Add(new AltinnSchemaField(path, type, node.TryGetProperty("format", out var format) ? format.GetString() : null, required));
        }

        // Properties of an object node, including every allOf part in order (an XSD extension puts the base type first).
        (List<(string Name, JsonElement Value)> Properties, HashSet<string> Required)? Collect(JsonElement node, int depth)
        {
            if (depth > 16) return null;
            var properties = new List<(string, JsonElement)>();
            var required = new HashSet<string>(StringComparer.Ordinal);
            var found = false;
            if (node.TryGetProperty("allOf", out var all) && all.ValueKind == JsonValueKind.Array)
                foreach (var part in all.EnumerateArray())
                    if (Collect(Resolve(root, part), depth + 1) is { } inherited)
                    {
                        found = true;
                        properties.AddRange(inherited.Properties.Where(item => properties.All(existing => existing.Item1 != item.Name)));
                        required.UnionWith(inherited.Required);
                    }
            if (node.TryGetProperty("properties", out var own) && own.ValueKind == JsonValueKind.Object)
            {
                found = true;
                foreach (var property in own.EnumerateObject())
                    if (!property.Name.StartsWith('@') && properties.All(existing => existing.Item1 != property.Name)) properties.Add((property.Name, property.Value));
            }
            if (node.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                required.UnionWith(req.EnumerateArray().Select(item => item.GetString() ?? ""));
            return found ? (properties, required) : null;
        }

        static JsonElement Resolve(JsonElement root, JsonElement node)
        {
            for (var guard = 0; guard < 16; guard++)
            {
                if (node.TryGetProperty("$ref", out var reference) && reference.GetString() is { } pointer && pointer.StartsWith("#/", StringComparison.Ordinal))
                {
                    var target = root;
                    foreach (var segment in pointer[2..].Split('/'))
                        if (!target.TryGetProperty(segment.Replace("~1", "/").Replace("~0", "~"), out target)) return node;
                    node = target;
                    continue;
                }
                if (!node.TryGetProperty("properties", out _))
                {
                    if (node.TryGetProperty("oneOf", out var one) && one.ValueKind == JsonValueKind.Array && one.GetArrayLength() == 1) { node = one[0]; continue; }
                    if (node.TryGetProperty("allOf", out var all) && all.ValueKind == JsonValueKind.Array && all.GetArrayLength() == 1) { node = all[0]; continue; }
                }
                return node;
            }
            return node;
        }

        static string Type(JsonElement node)
        {
            if (node.TryGetProperty("@xsdType", out var xsd) && xsd.GetString() is { } xsdType && IntegerXsdTypes.Contains(xsdType)) return "integer";
            return node.TryGetProperty("type", out var type)
                ? type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(item => item.GetString()).FirstOrDefault(item => item != "null") ?? "unknown" : type.GetString() ?? "unknown"
                : "unknown";
        }
    }

    private static readonly HashSet<string> IntegerXsdTypes = new(StringComparer.Ordinal)
    {
        "integer", "int", "long", "short", "byte", "nonNegativeInteger", "positiveInteger", "nonPositiveInteger", "negativeInteger",
        "unsignedInt", "unsignedLong", "unsignedShort", "unsignedByte",
    };

    /// <summary>Reads a sequential BPMN process (start → tasks → end, no gateways) with each task's Altinn task type.</summary>
    internal static (IReadOnlyList<AltinnProcessTask>? Tasks, string? Note) ParseProcess(string xml)
    {
        XDocument document;
        try { document = XDocument.Parse(xml, LoadOptions.None); }
        catch (XmlException) { return (null, "The process definition is not valid XML."); }
        XNamespace bpmn = BpmnNs, altinn = AltinnNs;
        var process = document.Root?.Element(bpmn + "process");
        if (process is null) return (null, "No BPMN process element.");
        if (process.Elements().Any(item => item.Name.LocalName.EndsWith("Gateway", StringComparison.Ordinal))) return (null, "The process has gateways; only sequential processes are supported.");
        var flows = process.Elements(bpmn + "sequenceFlow").ToDictionary(item => (string?)item.Attribute("sourceRef") ?? "", item => (string?)item.Attribute("targetRef") ?? "", StringComparer.Ordinal);
        var tasks = process.Elements(bpmn + "task").ToDictionary(item => (string?)item.Attribute("id") ?? "", StringComparer.Ordinal);
        var start = process.Element(bpmn + "startEvent")?.Attribute("id")?.Value;
        if (start is null) return (null, "No start event.");
        var ordered = new List<AltinnProcessTask>();
        var current = start;
        for (var guard = 0; guard < 64 && flows.TryGetValue(current, out var next); guard++)
        {
            if (!tasks.TryGetValue(next, out var task)) break;
            var type = task.Descendants(altinn + "taskType").FirstOrDefault()?.Value ?? (string?)task.Attribute(altinn + "tasktype");
            if (string.IsNullOrWhiteSpace(type)) return (null, $"Task {next} has no Altinn task type.");
            ordered.Add(new AltinnProcessTask(next, type.Trim()));
            current = next;
        }
        return ordered.Count == 0 ? (null, "No task follows the start event.") : (ordered, null);
    }

    private static async Task<string> ReadBoundedAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await CopyBoundedAsync(content, buffer, MaxDocumentBytes, ct);
        // App files are often saved with a UTF-8 byte order mark; it is not part of the JSON/XML document.
        return Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('﻿');
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, long limit, CancellationToken ct)
    {
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > limit) throw new AltinnContractException($"The document exceeds the {limit / (1024 * 1024)} MB limit.");
            await target.WriteAsync(chunk.AsMemory(0, read), ct);
        }
    }

    private static async Task<string> GetTextAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        using var response = await client.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode) throw new AltinnContractException($"The public TT02 metadata request {uri.AbsolutePath} returned HTTP {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > MaxDocumentBytes) throw new AltinnContractException("The public TT02 metadata document is too large.");
        return await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(ct), ct);
    }

    private static string Fingerprint(IEnumerable<(string Name, string Text)> documents)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, text) in documents.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData(Encoding.UTF8.GetBytes(text));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
