using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// Source Analysis → Contracts: the Altinn 3 apps whose source is in a snapshot. Runs inside ingestion on files the canonical archive reader
/// already validated (it opens no archive of its own). For every <c>applicationmetadata.json</c> it records the app id, the single form data
/// type, that type's JSON schema (flattened) and the sequential process tasks from <c>process.bpmn</c> — each only when the files are found in
/// the same app folder and are unambiguous. An app's OpenAPI document is equally valid evidence: the generic OpenAPI configuration values of
/// the snapshot (route, the one form data type, its content type and task, the process tasks) describe the app without applicationmetadata.json
/// or process.bpmn, and fill what the app folder does not provide. Nothing is guessed; what is missing is noted.
/// </summary>
public static class AltinnAppSourceExtractor
{
    private const int MaxApps = 20;

    public static List<AltinnContractSnapshot> Extract(IReadOnlyList<(string Path, string Content)> files, Guid snapshotId, DateTimeOffset analyzedAt,
        IReadOnlyList<SourceConfigurationValue>? declared = null)
    {
        var apps = new List<AltinnContractSnapshot>();
        var normalized = files.Select(file => (Path: file.Path.Replace('\\', '/'), Text: file.Content.TrimStart('﻿'))).ToArray();
        foreach (var metadata in normalized.Where(file => Name(file.Path) == "applicationmetadata.json").Take(MaxApps))
        {
            string? appId;
            try
            {
                using var json = JsonDocument.Parse(metadata.Text);
                appId = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("id", out var id) && json.RootElement.TryGetProperty("dataTypes", out _)
                    ? id.GetString() : null;
            }
            catch (JsonException) { continue; }
            if (appId is null || !appId.Contains('/')) continue;

            // The app folder is the parent of "config/" (Altinn layout: App/config/applicationmetadata.json, App/models, App/config/process).
            var configDir = Directory(metadata.Path);
            var appDir = configDir.EndsWith("config/", StringComparison.OrdinalIgnoreCase) ? configDir[..^"config/".Length] : configDir;
            var inApp = normalized.Where(file => file.Path.StartsWith(appDir, StringComparison.Ordinal)).ToArray();
            var notes = new List<string>();
            AltinnContractSnapshot parsed;
            try { parsed = AltinnAppContractService.ParseMetadata(metadata.Text, appId); }
            catch (Exception ex) when (ex is JsonException or AltinnContractException) { continue; }
            var documents = new List<(string Name, string Text)> { (metadata.Path, metadata.Text) };

            AltinnAppContractService.AltinnSchemaDocument? schema = null;
            if (parsed.FormDataTypeId is { } dataType)
            {
                var modelName = parsed.Notes.FirstOrDefault(item => item.StartsWith("classRef:", StringComparison.Ordinal))?["classRef:".Length..].Split('.').Last();
                var schemas = inApp.Where(file => Name(file.Path).EndsWith(".schema.json", StringComparison.Ordinal)).Where(file =>
                {
                    var stem = Name(file.Path)[..^".schema.json".Length];
                    return string.Equals(stem, dataType, StringComparison.OrdinalIgnoreCase) || (modelName is not null && string.Equals(stem, modelName, StringComparison.OrdinalIgnoreCase));
                }).ToArray();
                if (schemas.Length == 1)
                {
                    try { schema = AltinnAppContractService.ParseSchema(schemas[0].Text); documents.Add((schemas[0].Path, schemas[0].Text)); }
                    catch (JsonException) { notes.Add($"The schema {schemas[0].Path} is not readable JSON."); }
                }
                else notes.Add(schemas.Length == 0 ? $"No *.schema.json for the form data type {dataType} in the app folder." : $"Several schemas match the form data type {dataType}; none is used.");
            }

            IReadOnlyList<AltinnProcessTask>? tasks = null;
            var processes = inApp.Where(file => Name(file.Path) == "process.bpmn").ToArray();
            if (processes.Length == 1)
            {
                (tasks, var note) = AltinnAppContractService.ParseProcess(processes[0].Text);
                if (tasks is null) notes.Add($"The process definition could not be used: {note}");
                else documents.Add((processes[0].Path, processes[0].Text));
            }
            else notes.Add(processes.Length == 0 ? "No process.bpmn in the app folder (optional: the app's deployed OpenAPI contract can establish the process operation)." : "Several process.bpmn files in the app folder; none is used.");

            apps.Add(parsed with
            {
                Source = AltinnContractSourceKind.SourceAnalysisSnapshot, AppId = appId, CapturedAt = analyzedAt, Fingerprint = Fingerprint(documents),
                Documents = documents.Select(item => item.Name).ToArray(), HasApplicationMetadata = true, Fields = schema?.Fields, ProcessTasks = tasks,
                XmlRootElement = schema?.XmlRootElement, XmlNamespace = schema?.XmlNamespace, XmlElementsQualified = schema?.XmlElementsQualified ?? false,
                Notes = [.. parsed.Notes.Where(item => !item.StartsWith("classRef:", StringComparison.Ordinal)), .. notes],
                SourceSnapshotId = snapshotId,
            });
        }
        foreach (var described in FromOpenApi(normalized, declared ?? [], snapshotId, analyzedAt))
        {
            var index = apps.FindIndex(app => string.Equals(app.AppId, described.AppId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) { if (apps.Count < MaxApps) apps.Add(described); continue; }
            // The app folder wins for what it states; the OpenAPI document fills only what it does not (e.g. tasks without a process.bpmn).
            var app = apps[index];
            var filled = app.ProcessTasks is null && described.ProcessTasks is not null;
            apps[index] = app with
            {
                ProcessTasks = app.ProcessTasks ?? described.ProcessTasks, FormDataTaskId = app.FormDataTaskId ?? described.FormDataTaskId,
                AllowedContentTypes = app.AllowedContentTypes.Count > 0 ? app.AllowedContentTypes : described.AllowedContentTypes,
                Documents = [.. app.Documents, .. described.Documents.Except(app.Documents, StringComparer.Ordinal)],
                Notes = [.. app.Notes.Where(note => !(filled && note.StartsWith("No process.bpmn", StringComparison.Ordinal))),
                    .. filled ? [$"Process tasks from the app's OpenAPI document {described.Documents[0]} (no process.bpmn needed)."] : Array.Empty<string>()],
            };
        }
        return apps;
    }

    /// <summary>An app described by an OpenAPI document of the snapshot: its route is the app id; the form data type, content type, task and the
    /// process tasks come from the same document's declared values. A document without a route describes no app.</summary>
    private static IEnumerable<AltinnContractSnapshot> FromOpenApi((string Path, string Text)[] files, IReadOnlyList<SourceConfigurationValue> declared, Guid snapshotId, DateTimeOffset analyzedAt)
    {
        foreach (var document in declared.Where(value => value.EvidenceKind == SourceConfigurationEvidenceKind.OpenApi).GroupBy(value => value.Artifact, StringComparer.Ordinal))
        {
            string? Single(string key) => document.Where(value => value.SemanticKey == key).Select(value => value.Value).Distinct(StringComparer.Ordinal).ToArray() is [var one] ? one : null;
            if (Single(SourceConfigurationKeys.ApplicationRoute) is not { } route || !route.Contains('/')) continue;
            var form = document.FirstOrDefault(value => value.SemanticKey == SourceConfigurationKeys.ApplicationFormDataType);
            var tasks = document.Where(value => value.SemanticKey == SourceConfigurationKeys.ApplicationTaskId && value.Value is not null)
                .Select(value => new AltinnProcessTask(value.Value!, value.Attributes.GetValueOrDefault("Type") ?? "")).ToArray();
            var contentTypes = document.Where(value => value.SemanticKey == SourceConfigurationKeys.PayloadContentType && value.Value is not null
                && form is not null && value.Attributes.GetValueOrDefault("DataType") == form.Value).Select(value => value.Value!).Distinct(StringComparer.Ordinal).ToArray();
            var text = files.FirstOrDefault(file => SourceAnalysis.Evidence.SourceEvidenceRedaction.SafePath(file.Path) == document.Key).Text ?? "";
            var notes = new List<string> { $"Described by the app's OpenAPI document {document.Key}: route, form data type and process tasks are declared there; the form schema's fields are not." };
            if (form is null) notes.Add("The OpenAPI document declares no single form data type.");
            if (tasks.Length == 0) notes.Add("The OpenAPI document lists no process tasks.");
            yield return new AltinnContractSnapshot
            {
                Source = AltinnContractSourceKind.SourceAnalysisSnapshot, AppId = route, CapturedAt = analyzedAt, Fingerprint = Fingerprint([(document.Key, text)]),
                Documents = [document.Key], HasApplicationMetadata = false, FormDataTypeId = form?.Value,
                FormDataTypeCandidates = form?.Value is { } id ? [id] : [], AllowedContentTypes = contentTypes,
                ProcessTasks = tasks.Length == 0 ? null : tasks, FormDataTaskId = form?.Attributes.GetValueOrDefault("TaskId"),
                Notes = notes, SourceSnapshotId = snapshotId,
            };
        }
    }

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
    private static string Directory(string path) => path[..(path.LastIndexOf('/') + 1)];

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
