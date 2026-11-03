using System.Text.Json;
using System.Text.Json.Nodes;

namespace BirkNext.Api.Services;

/// <summary>Document bodies of a saved workspace's lifecycle: each revision's content by revision id, and captured generated documents by fingerprint.</summary>
public sealed class WorkspaceDocumentsDto
{
    public Dictionary<Guid, string> Revisions { get; set; } = [];
    public Dictionary<string, string> GeneratedDocuments { get; set; } = [];
}

/// <summary>
/// A saved workspace's SDD lifecycle (opaque JSON owned by the frontend) split into what bootstrap needs and the document bodies it does not.
/// The manifest keeps every record — revisions with their identity, fingerprint, role, scope and authority — and drops only the bodies
/// (revision <c>Content</c>, <c>GeneratedDocumentContents</c>), adding each revision's <c>ContentLength</c> so presence never depends on a body.
/// Bodies are served by the documents endpoint, and a write that carries a manifest is completed from the stored lifecycle
/// (<see cref="FillMissing"/>), so deferring them never loses one. Storage is unchanged.
/// </summary>
public static class WorkspaceLifecycleDocuments
{
    public const string RevisionsKey = "Revisions";
    public const string ContentKey = "Content";
    public const string ContentLengthKey = "ContentLength";
    public const string RevisionIdKey = "RevisionId";
    public const string FingerprintKey = "Fingerprint";
    public const string GeneratedContentsKey = "GeneratedDocumentContents";
    public const string GeneratedSetsKey = "GeneratedDocumentationSets";

    /// <summary>The lifecycle without document bodies; null/blank/unreadable JSON is returned as it is (nothing to defer).</summary>
    public static string? Manifest(string? json)
    {
        if (Parse(json) is not { } root) return json;
        if (root[RevisionsKey] is JsonArray revisions)
            foreach (var revision in revisions.OfType<JsonObject>())
            {
                var content = revision[ContentKey] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
                revision[ContentKey] = "";
                revision[ContentLengthKey] = content.Length;
            }
        if (root.ContainsKey(GeneratedContentsKey)) root[GeneratedContentsKey] = new JsonObject();
        return root.ToJsonString();
    }

    /// <summary>The document bodies of the lifecycle (blank bodies are omitted).</summary>
    public static WorkspaceDocumentsDto Documents(string? json)
    {
        var documents = new WorkspaceDocumentsDto();
        if (Parse(json) is not { } root) return documents;
        if (root[RevisionsKey] is JsonArray revisions)
            foreach (var revision in revisions.OfType<JsonObject>())
                if (Text(revision, RevisionIdKey) is { } id && Guid.TryParse(id, out var revisionId) && Text(revision, ContentKey) is { Length: > 0 } content)
                    documents.Revisions[revisionId] = content;
        if (root[GeneratedContentsKey] is JsonObject generated)
            foreach (var (fingerprint, node) in generated)
                if (node is JsonValue value && value.TryGetValue<string>(out var content) && content.Length > 0)
                    documents.GeneratedDocuments[fingerprint] = content;
        return documents;
    }

    /// <summary>
    /// Completes a lifecycle written from a manifest: a revision without content takes the content of the stored revision with the same id and
    /// fingerprint (the same immutable document), and a generated document the incoming sets still reference keeps its stored body. Nothing is
    /// added that the incoming lifecycle does not reference, so removals are kept. Incoming JSON that needs nothing is returned unchanged.
    /// </summary>
    public static string? FillMissing(string? incoming, string? stored)
    {
        if (Parse(incoming) is not { } root || Parse(stored) is not { } previous) return incoming;
        var changed = false;
        if (root[RevisionsKey] is JsonArray revisions && previous[RevisionsKey] is JsonArray storedRevisions)
        {
            var bodies = storedRevisions.OfType<JsonObject>()
                .Select(r => (Id: Text(r, RevisionIdKey), Fingerprint: Text(r, FingerprintKey), Content: Text(r, ContentKey)))
                .Where(r => r.Id is not null && !string.IsNullOrEmpty(r.Content))
                .GroupBy(r => (r.Id!, r.Fingerprint ?? ""))
                .ToDictionary(g => g.Key, g => g.First().Content!);
            foreach (var revision in revisions.OfType<JsonObject>())
            {
                if (!string.IsNullOrEmpty(Text(revision, ContentKey)) || Text(revision, RevisionIdKey) is not { } id) continue;
                if (bodies.TryGetValue((id, Text(revision, FingerprintKey) ?? ""), out var content))
                {
                    revision[ContentKey] = content;
                    changed = true;
                }
                if (revision.Remove(ContentLengthKey)) changed = true;
            }
        }
        if (previous[GeneratedContentsKey] is JsonObject storedGenerated && storedGenerated.Count > 0)
        {
            var referenced = root[GeneratedSetsKey] is { } sets ? Fingerprints(sets) : [];
            if (root[GeneratedContentsKey] is not JsonObject generated) root[GeneratedContentsKey] = generated = new JsonObject();
            foreach (var (fingerprint, node) in storedGenerated)
                if (!generated.ContainsKey(fingerprint) && referenced.Contains(fingerprint) && node is not null)
                {
                    generated[fingerprint] = node.DeepClone();
                    changed = true;
                }
        }
        return changed ? root.ToJsonString() : incoming;
    }

    private static HashSet<string> Fingerprints(JsonNode node)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? item)
        {
            switch (item)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                        if (key == FingerprintKey && value is JsonValue v && v.TryGetValue<string>(out var text)) found.Add(text);
                        else Walk(value);
                    break;
                case JsonArray array:
                    foreach (var value in array) Walk(value);
                    break;
            }
        }
        Walk(node);
        return found;
    }

    private static string? Text(JsonObject obj, string key) => obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }
}
