using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// Source Analysis → Contracts: the Altinn 3 apps whose source is in a snapshot. Runs inside ingestion on files the canonical archive reader
/// already validated (it opens no archive of its own). For every <c>applicationmetadata.json</c> it records the app id, the single form data
/// type, that type's JSON schema (flattened) and the sequential process tasks from <c>process.bpmn</c> — each only when the files are found in
/// the same app folder and are unambiguous. Nothing is guessed; what is missing is noted.
/// </summary>
public static class AltinnAppSourceExtractor
{
    private const int MaxApps = 20;

    public static List<AltinnContractSnapshot> Extract(IReadOnlyList<(string Path, string Content)> files, Guid snapshotId, DateTimeOffset analyzedAt)
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
        return apps;
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
