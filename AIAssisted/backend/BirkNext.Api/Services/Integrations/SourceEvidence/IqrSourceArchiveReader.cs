using System.IO.Compression;
using Path = System.IO.Path;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using BirkNext.Api.Services.DependencyReview;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.SourceEvidence;

public sealed record SourceArchiveValidationFailure(string Code, string Stage, string Message, string? EntryPath = null,
    long? Actual = null, long? Limit = null);

public sealed record SourceArchiveReadResult(IqrSourceArchiveReader.Workspace? Workspace, SourceArchiveValidationFailure? Failure, long? EntryCount = null)
{
    public bool IsValid => Workspace is not null && Failure is null;
    public void Deconstruct(out IqrSourceArchiveReader.Workspace? workspace, out string? error)
    {
        workspace = Workspace;
        error = Failure?.Message;
    }
}

/// <summary>Bounded virtual workspace. Never writes archive entries or the upload to disk.</summary>
public static class IqrSourceArchiveReader
{
    public const int MaxArchiveBytes = 50 * 1024 * 1024;
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const int MaxExpandedBytes = 100 * 1024 * 1024;
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", "packages", ".git", ".vs", "coverage", "TestResults" };
    /// <param name="ConfigurationFiles">JSON/YAML content held in memory for this analysis only. Never stored; only the source-architecture analyzer reads it,
    /// and only allow-listed entity names and endpoint hosts can leave it. Every other analyzer keeps seeing key inventories only.</param>
    /// <param name="AllPaths">Every non-ignored file path in the archive (names only, never content), including files no analyzer reads (pom.xml, .java,
    /// requirements.txt). Technology inventory uses it so unsupported technologies are reported, not silently dropped. In memory only.</param>
    /// <param name="EvidenceFiles">Infrastructure-as-code, schema/contract, properties/env and pipeline-script files (Terraform, Bicep, GraphQL SDL, protobuf,
    /// Jenkinsfile …) held in memory for the Source Analysis evidence domains only. Never stored; only redacted, typed evidence leaves the analysis.</param>
    /// <param name="DocumentFiles">Readable Markdown documents, captured only when Project Import asks for them (null otherwise). Held in memory for
    /// artifact-role classification; Source Analysis never reads them and they never enter a source snapshot.</param>
    /// <param name="SkippedDocuments">Markdown documents that were not read (too large, binary, over the document limit), with the reason.</param>
    /// <param name="EntryCount">Every entry of the archive (files and directories), as validated.</param>
    /// <param name="DocumentationCandidates">Markdown documents and documentation-workflow files (agent skills/hooks, documentation scripts), held in
    /// memory for the generated-documentation evidence only. Never stored; only paths, fingerprints, declared dates and structured keys leave it.</param>
    /// <param name="EntryModified">The archive's own modification time per file path. Archive times are often uniform (downloaded or exported
    /// archives) and are trusted only when the generated-documentation analysis finds them varied.</param>
    public sealed record Workspace(SourceArchive Archive, List<SourceFile> Files, List<string> Limitations, List<SourceConfigurationEvidence>? Configurations = null,
        List<SourceFile>? ConfigurationFiles = null, List<SourceFile>? EvidenceFiles = null, List<string>? AllPaths = null,
        List<SourceFile>? DocumentFiles = null, List<ArchiveDocumentSkip>? SkippedDocuments = null, long EntryCount = 0,
        List<SourceFile>? DocumentationCandidates = null, IReadOnlyDictionary<string, DateTimeOffset>? EntryModified = null);

    public const int MaxDocumentationCandidates = 2_000;
    public const long MaxDocumentationBytes = 24L * 1024 * 1024;
    public const long MaxWorkflowFileBytes = 256 * 1024;

    /// <summary>Non-Markdown files that may define or trigger a documentation generator: agent workflow folders (skills, hooks, commands, rules)
    /// and scripts named for documentation. JSON/YAML workflow files are already read as configuration and are not read twice.</summary>
    public static bool IsDocumentationWorkflowCandidate(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".json" or ".yaml" or ".yml" or ".cs" or ".csproj" or ".props" or ".sln" or ".slnx" or ".sql" or ".xsd") return false;
        if (extension is ".zip" or ".tar" or ".gz" or ".7z" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".dll" or ".exe" or ".pdf" or ".woff" or ".woff2") return false;
        var segments = path.Split('/');
        if (segments.Any(s => s is ".claude" or ".cursor" or ".windsurf" or ".continue" or ".aider" || s.Equals(".github", StringComparison.OrdinalIgnoreCase) && segments.Contains("prompts")))
            return true;
        var file = Path.GetFileName(path);
        if (file.Equals("Doxyfile", StringComparison.OrdinalIgnoreCase)) return true;
        return extension is ".sh" or ".ps1" or ".py" or ".cmd" or ".bat" or ".mjs" or ".js" or ".toml" or ""
            && Regex.IsMatch(file, @"(?i)(^|[-_.])(auto)?docs?([-_.]|$)|documentation|docgen|gendoc");
    }

    public sealed record ArchiveDocumentSkip(string Path, string Reason);

    /// <summary>Document rules of Sample Project discovery (Markdown only, 1 MB per document, no NUL bytes, same ignored folders). The count cap is
    /// higher than a sample folder's 500 because a project archive is often a monorepo of several Spec-Kit projects; every read still counts
    /// against the archive's expanded-read limit.</summary>
    public const int MaxDocuments = 2_000;
    public const long MaxDocumentBytes = BirkNext.Api.Services.SampleProjects.SampleProjectDocumentInventory.MaxDocumentBytes;

    /// <summary>Files the evidence domains read beyond C#/project/JSON/YAML: IaC, schemas/contracts, properties/env files and pipeline scripts.</summary>
    public static bool IsEvidenceFile(string path)
    {
        var file = Path.GetFileName(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".tf" or ".tfvars" or ".bicep" or ".bicepparam" or ".graphql" or ".graphqls" or ".gql" or ".proto" or ".xsd" or ".properties"
            || file.Equals("Jenkinsfile", StringComparison.OrdinalIgnoreCase) || file.Equals(".env", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith(".env.", StringComparison.OrdinalIgnoreCase);
    }

    public static (Workspace? Workspace, string? Error) Read(string name, byte[] bytes, CancellationToken ct = default)
    {
        var result = ReadDetailed(name, bytes, ct);
        return (result.Workspace, result.Failure?.Message);
    }

    public static SourceArchiveReadResult ReadDetailed(string name, byte[] bytes, CancellationToken ct = default) => ReadDetailed(name, bytes, captureDocuments: false, ct);

    /// <summary>
    /// The one validation path for uploaded archives (Source Analysis and Project Import). With <paramref name="captureDocuments"/>, readable
    /// Markdown documents are also captured in the same pass, under the same entry, path, size and expanded-read limits.
    /// </summary>
    public static SourceArchiveReadResult ReadDetailed(string name, byte[] bytes, bool captureDocuments, CancellationToken ct = default)
    {
        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return Reject("ARCHIVE_UNSUPPORTED_FORMAT", "Only ZIP source archives are supported.");
        if (bytes.Length == 0) return Reject("ARCHIVE_EMPTY_UPLOAD", "The uploaded file is empty.", actual: 0, limit: 1);
        if (bytes.Length > MaxArchiveBytes) return Reject("ARCHIVE_TOO_LARGE", $"Archive is {FormatBytes(bytes.Length)}; the maximum compressed size is {FormatBytes(MaxArchiveBytes)}.", actual: bytes.Length, limit: MaxArchiveBytes);
        var files = new List<SourceFile>();
        var limitations = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configurations = new List<SourceConfigurationEvidence>();
        var configurationFiles = new List<SourceFile>();
        var evidenceFiles = new List<SourceFile>();
        var allPaths = new List<string>();
        var documentFiles = new List<SourceFile>();
        var skippedDocuments = new List<ArchiveDocumentSkip>();
        var documentationCandidates = new List<SourceFile>();
        var documentationCandidatesSkipped = 0;
        long documentationBytes = 0;
        var entryModified = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var fileEntryCount = 0;
        long? entryCount = null;
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            entryCount = zip.Entries.Count;
            if (zip.Entries.Count > 20_000) return Reject("ARCHIVE_TOO_MANY_ENTRIES", $"Archive contains {zip.Entries.Count:N0} entries; the maximum is 20,000.", actual: zip.Entries.Count, limit: 20_000, entryCount: entryCount);
            long total = 0;
            long actualRead = 0;
            // Reads one entry under the expanded-read and per-file limits; a length that does not match the header is an invalid archive.
            (string? Content, SourceArchiveReadResult? Rejection) ReadEntry(ZipArchiveEntry entry, string path)
            {
                using var input = entry.Open();
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    actualRead += count;
                    if (actualRead > MaxExpandedBytes) return (null, Reject("ARCHIVE_EXPANDED_SIZE_EXCEEDED", $"Archive data exceeds the {FormatBytes(MaxExpandedBytes)} expanded read limit.", path, actualRead, MaxExpandedBytes, entryCount));
                    if (buffer.Length + count > MaxFileBytes) return (null, Reject("ARCHIVE_ENTRY_TOO_LARGE", $"Archive entry exceeds the {FormatBytes(MaxFileBytes)} analysis read limit.", path, buffer.Length + count, MaxFileBytes, entryCount));
                    buffer.Write(chunk, 0, count);
                }
                if (buffer.Length != entry.Length) return (null, Reject("ARCHIVE_INVALID_ZIP", "Invalid or incomplete ZIP archive.", path, entryCount: entryCount));
                buffer.Position = 0;
                using var textReader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                return (textReader.ReadToEnd(), null);
            }
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var originalPath = entry.FullName;
                var normalized = NormalizeEntryPath(originalPath);
                if (normalized.Failure is { } pathFailure) return new(null, pathFailure, entryCount);
                var path = normalized.Path!;
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    return Reject("ARCHIVE_SYMLINK_UNSUPPORTED", "Symbolic links are not supported in source archives.", path, entryCount: entryCount);
                if (normalized.IsRoot)
                {
                    // Common ZIP tools add a harmless ./ directory entry for the archive root.
                    if (normalized.IsDirectory) continue;
                    return Reject("ARCHIVE_INVALID_PATH", "An archive file has no usable relative path.", entryCount: entryCount);
                }
                if (!names.Add(path)) return Reject("ARCHIVE_DUPLICATE_PATH", $"Archive contains duplicate paths after normalizing separators and case: {SafeLabel(path)}.", path, entryCount: entryCount);
                if (entry.Length > MaxExpandedBytes - total) return Reject("ARCHIVE_EXPANDED_SIZE_EXCEEDED", $"Archive expands beyond the {FormatBytes(MaxExpandedBytes)} limit.", path, total + entry.Length, MaxExpandedBytes, entryCount);
                total += entry.Length;
                if (normalized.IsDirectory) continue;
                fileEntryCount++;
                if (path.Split('/').Any(Ignored.Contains)) continue;
                allPaths.Add(path);
                entryModified[path] = entry.LastWriteTime;
                var extension = Path.GetExtension(path).ToLowerInvariant();
                var markdown = BirkNext.Api.Services.SampleProjects.SampleProjectDocumentInventory.DocumentExtensions.Contains(extension);
                // Generated-documentation candidates (documentation files and documentation-workflow files) are read for the generated-documentation
                // evidence only, under the same expanded-read limit. Only derived evidence (paths, fingerprints, declared dates, structured keys) leaves it.
                SourceArchiveReadResult? CaptureDocumentationCandidate(long limit)
                {
                    // Never let documentation reads reject an archive that source analysis alone would accept: bounded by count, size, a total
                    // budget and the remaining expanded-read allowance.
                    if (documentationCandidates.Count >= MaxDocumentationCandidates || entry.Length > limit || documentationBytes + entry.Length > MaxDocumentationBytes
                        || actualRead + entry.Length > MaxExpandedBytes) { documentationCandidatesSkipped++; return null; }
                    var (text, rejection) = ReadEntry(entry, path);
                    if (rejection is not null) return rejection;
                    if (text!.Contains('\0')) { documentationCandidatesSkipped++; return null; }
                    documentationBytes += entry.Length;
                    documentationCandidates.Add(new SourceFile(path, text));
                    return null;
                }
                if (markdown && (!captureDocuments || path.Split('/').Any(BirkNext.Api.Services.SampleProjects.SampleProjectDocumentInventory.IgnoredDirectories.Contains)))
                {
                    if (CaptureDocumentationCandidate(MaxDocumentBytes) is { } candidateRejection) return candidateRejection;
                    continue;
                }
                if (captureDocuments && markdown)
                {
                    // Documents are never source: they are captured for artifact discovery only, with the Sample Project document rules.
                    if (documentFiles.Count >= MaxDocuments) { skippedDocuments.Add(new(SafeLabel(path), BirkNext.Api.Services.SampleProjects.SampleInventorySkipReason.DocumentLimit)); if (CaptureDocumentationCandidate(MaxDocumentBytes) is { } limitRejection) return limitRejection; continue; }
                    if (entry.Length > MaxDocumentBytes) { skippedDocuments.Add(new(SafeLabel(path), BirkNext.Api.Services.SampleProjects.SampleInventorySkipReason.TooLarge)); documentationCandidatesSkipped++; continue; }
                    var (document, documentRejection) = ReadEntry(entry, path);
                    if (documentRejection is not null) return documentRejection;
                    if (document!.Contains('\0')) { skippedDocuments.Add(new(SafeLabel(path), BirkNext.Api.Services.SampleProjects.SampleInventorySkipReason.Binary)); documentationCandidatesSkipped++; continue; }
                    documentFiles.Add(new SourceFile(path, document));
                    if (documentationCandidates.Count < MaxDocumentationCandidates) documentationCandidates.Add(new SourceFile(path, document));
                    continue;
                }
                if (IsDocumentationWorkflowCandidate(path) && CaptureDocumentationCandidate(MaxWorkflowFileBytes) is { } workflowRejection) return workflowRejection;
                if (extension is ".zip" or ".tar" or ".gz" or ".7z") { limitations.Add("Nested archives are not analyzed."); continue; }
                if (extension is ".tfstate" || path.EndsWith(".tfstate.backup", StringComparison.OrdinalIgnoreCase))
                { limitations.Add("Terraform state file present but not read: state can hold secrets and is runtime state, not source."); continue; }
                if (extension is ".js" or ".ts" or ".py" or ".java" or ".go" or ".fs" or ".vb")
                    limitations.Add($"Not analyzed: {extension} source (unsupported language).");
                var evidence = IsEvidenceFile(path);
                if (!evidence && extension is not (".cs" or ".csproj" or ".sln" or ".slnx" or ".json" or ".yaml" or ".yml" or ".props" or ".sql" or ".xsd")
                    && !Path.GetFileName(path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Length > MaxFileBytes) { limitations.Add("Source file exceeds the 2 MB per-file limit and was not analyzed."); continue; }
                var (read, rejection) = ReadEntry(entry, path);
                if (rejection is not null) return rejection;
                var content = read!;
                if (extension is ".json" or ".yaml" or ".yml")
                {
                    var keys = new List<string>();
                    if (extension == ".json")
                    {
                        try
                        {
                            using var json = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                            void Walk(JsonElement element, string prefix)
                            {
                                if (keys.Count >= 1024 || element.ValueKind != JsonValueKind.Object) return;
                                foreach (var property in element.EnumerateObject())
                                {
                                    if (keys.Count >= 1024) break;
                                    var key = string.IsNullOrEmpty(prefix) ? property.Name : prefix + ":" + property.Name;
                                    keys.Add(SafeLabel(key));
                                    Walk(property.Value, key);
                                }
                            }
                            Walk(json.RootElement, "");
                        }
                        catch (JsonException) { limitations.Add("A JSON configuration file could not be parsed; values excluded and other evidence retained."); }
                    }
                    else
                    {
                        keys = Regex.Matches(content, @"(?m)^\s*([\p{L}_][\p{L}\p{N}_-]*)\s*:").Select(m => SafeLabel(m.Groups[1].Value)).Distinct().Take(1024).ToList();
                        limitations.Add("YAML key inventory is heuristic; YAML values and build/test execution results are not analyzed.");
                    }
                    configurations.Add(new SourceConfigurationEvidence(SafeLabel(path), keys, extension == ".json" ? SourceConfidence.StrongSourceEvidence : SourceConfidence.Partial));
                    limitations.Add("Configuration values excluded. Key inventory is bounded to 1,024 keys per file; formal schemas are not validated.");
                    configurationFiles.Add(new SourceFile(path, content));
                    continue;
                }
                if (evidence) { evidenceFiles.Add(new SourceFile(path, content)); continue; }
                files.Add(new SourceFile(path, content));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException)
        {
            var failure = ex is NotSupportedException
                ? new SourceArchiveValidationFailure("ARCHIVE_ENCRYPTED_UNSUPPORTED", "validation", "Encrypted entries or unsupported ZIP compression methods are not supported.")
                : new SourceArchiveValidationFailure("ARCHIVE_INVALID_ZIP", "validation", "Invalid or incomplete ZIP archive.");
            return new(null, failure, entryCount);
        }
        if (fileEntryCount == 0) return Reject("ARCHIVE_EMPTY", "Archive contains no files.", entryCount: entryCount);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (documentationCandidatesSkipped > 0)
            limitations.Add($"{documentationCandidatesSkipped:N0} documentation file(s) were not read for generated-documentation evidence (size, binary content or the {MaxDocumentationCandidates:N0}-file limit).");
        return new(new Workspace(new SourceArchive(ArchiveDisplayName(name), sha, files.Count + configurations.Count + evidenceFiles.Count), files, [.. limitations], configurations, configurationFiles, evidenceFiles, allPaths,
            captureDocuments ? documentFiles : null, captureDocuments ? skippedDocuments : null, entryCount ?? 0, documentationCandidates, entryModified), null);
    }

    private sealed record NormalizedPath(string? Path, bool IsDirectory, bool IsRoot, SourceArchiveValidationFailure? Failure);

    private static NormalizedPath NormalizeEntryPath(string value)
    {
        var path = value.Replace('\\', '/');
        var isDirectory = path.EndsWith('/');
        if (path.Contains('\0')) return new(null, isDirectory, false, new("ARCHIVE_INVALID_PATH", "validation", "Archive entry contains an invalid path character."));
        if (path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || Regex.IsMatch(path, @"^[A-Za-z]:"))
            return new(null, isDirectory, false, new("ARCHIVE_ABSOLUTE_PATH", "validation", $"Archive entry must be relative to the project root: {SafeLabel(path)}.", SafeLabel(path)));
        if (path.Contains(':')) return new(null, isDirectory, false, new("ARCHIVE_INVALID_PATH", "validation", $"Archive entry contains a path character that is not portable across platforms: {SafeLabel(path)}.", SafeLabel(path)));
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") return new(null, isDirectory, false, new("ARCHIVE_PATH_TRAVERSAL", "validation", $"Archive entry escapes the project root: {SafeLabel(path)}.", SafeLabel(path)));
            segments.Add(segment);
        }
        var normalized = string.Join('/', segments);
        if (normalized.Length > 4096) return new(null, isDirectory, false, new("ARCHIVE_PATH_TOO_LONG", "validation", "An archive entry path exceeds the 4,096 character safety limit.", SafeLabel(normalized), normalized.Length, 4096));
        return new(normalized, isDirectory, normalized.Length == 0, null);
    }

    private static SourceArchiveReadResult Reject(string code, string message, string? entryPath = null, long? actual = null, long? limit = null, long? entryCount = null) =>
        new(null, new SourceArchiveValidationFailure(code, "validation", message, entryPath is null ? null : SafeLabel(entryPath), actual, limit), entryCount);

    private static string FormatBytes(long value) => value >= 1024 * 1024 ? $"{value / (1024d * 1024):0.#} MB" : $"{value / 1024d:0.#} KB";

    /// <summary>
    /// The archive's human-readable display name: the last segment of the uploaded file name (never a client path), with secrets redacted
    /// and control, path and markup characters replaced. Unlike <see cref="SafeLabel"/> it keeps the punctuation people use in file names
    /// (parentheses, spaces, &amp;, ', #), so "M2LB (2).zip" stays "M2LB (2).zip". Display metadata only: the archive fingerprint is the
    /// identity, staged files are named by id, and archive entry paths keep their own validation.
    /// </summary>
    public static string ArchiveDisplayName(string fileName)
    {
        var name = (fileName ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var safe = LocalHttpsProxy.SensitiveDataRedactor.RedactText(DependencyEvidenceRedaction.Redact(name));
        safe = Regex.Replace(safe, @"(?i)(SECRET_SENTINEL\w*|(?:password|clientsecret|accesskey|sharedaccesskey|token)[_=][^/\s]+)", "[redacted]");
        safe = Regex.Replace(safe, @"[^\p{L}\p{N}_ .,()\[\]{}+&'#@!=~\-]", "_").Trim();
        if (safe.Length == 0) safe = "archive.zip";
        return safe.Length > 200 ? safe[..200] : safe;
    }

    public static string SafeLabel(string value)
    {
        var safe = LocalHttpsProxy.SensitiveDataRedactor.RedactText(DependencyEvidenceRedaction.Redact(value));
        safe = Regex.Replace(safe, @"(?i)(SECRET_SENTINEL\w*|(?:password|clientsecret|accesskey|sharedaccesskey|token)[_=][^/\s]+)", "[redacted]");
        safe = Regex.Replace(safe, @"[^\p{L}\p{N}_./<>\[\] `:+?,\-]", "_");
        return safe.Length > 300 ? safe[..300] : safe;
    }
}
