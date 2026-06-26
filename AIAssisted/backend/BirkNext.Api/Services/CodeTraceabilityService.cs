using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

// ─── Result types ──────────────────────────────────────────────────────────────

public class RegisterCodeFileResult
{
    public CodeFile? File { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
}

public class DeleteCodeFileResult
{
    public string? DeletedId { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    public bool IsSuccess => DeletedId is not null;
}

public class CreateCodeLinkResult
{
    public CodeLink? Link { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
}

public class DeleteCodeLinkResult
{
    public string? DeletedId { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    public bool IsSuccess => DeletedId is not null;
}

// ─── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// Manages the code traceability graph: CodeFile registry and CodeLink associations.
///
/// Connects the QA artifact world (Scenarios) to the code world (CodeFiles).
/// Deliberately separate from TraceLinkService — code links have different
/// semantics and will gain future extensions (git commits, PRs, AI sessions).
///
/// Reuses ImpactAnalysisService and SpecDriftDetectionService through the
/// existing Scenario + TraceLink data — no recalculation of risk or coverage.
///
/// Future extension hooks (not implemented):
///   - CommitHash on CodeLink: pin links to specific git snapshots
///   - Repository scanning: auto-discover CodeFiles from a repo
///   - AI suggestions: propose CodeFile → Scenario links based on content
/// </summary>
public sealed class CodeTraceabilityService
{
    private readonly AppDbContext _db;
    private readonly ILogger<CodeTraceabilityService> _logger;

    public CodeTraceabilityService(AppDbContext db, ILogger<CodeTraceabilityService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Code File CRUD ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CodeFile>> GetCodeFilesAsync(
        string projectId, CancellationToken ct = default) =>
        await _db.CodeFiles
            .Where(f => f.ProjectId == projectId)
            .OrderBy(f => f.FilePath)
            .ToListAsync(ct);

    public async Task<RegisterCodeFileResult> RegisterCodeFileAsync(
        string projectId,
        string filePath,
        string? description,
        CancellationToken ct = default)
    {
        var trimmed = filePath.Trim().Replace('\\', '/');
        if (string.IsNullOrEmpty(trimmed))
            return new RegisterCodeFileResult
            {
                Errors = [new UserError("EMPTY_PATH", "File path must not be empty.")],
            };

        if (trimmed.Length > 1000)
            return new RegisterCodeFileResult
            {
                Errors = [new UserError("PATH_TOO_LONG", "File path must not exceed 1000 characters.")],
            };

        var duplicate = await _db.CodeFiles
            .AnyAsync(f => f.ProjectId == projectId && f.FilePath == trimmed, ct);

        if (duplicate)
            return new RegisterCodeFileResult
            {
                Errors = [new UserError("DUPLICATE_PATH", $"A code file at \"{trimmed}\" is already registered.")],
            };

        var fileName = System.IO.Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(fileName)) fileName = trimmed;

        var file = new CodeFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            FilePath = trimmed,
            FileName = fileName,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatedAt = DateTime.UtcNow,
        };

        _db.CodeFiles.Add(file);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("CodeTraceability_FileRegistered {ProjectId} {FilePath}", projectId, trimmed);
        return new RegisterCodeFileResult { File = file };
    }

    public async Task<DeleteCodeFileResult> DeleteCodeFileAsync(
        Guid id, string projectId, CancellationToken ct = default)
    {
        var file = await _db.CodeFiles
            .FirstOrDefaultAsync(f => f.Id == id && f.ProjectId == projectId, ct);

        if (file is null)
            return new DeleteCodeFileResult
            {
                Errors = [new UserError("NOT_FOUND", "Code file not found.")],
            };

        // Cascade-delete its links
        var links = await _db.CodeLinks
            .Where(l => l.CodeFileId == id)
            .ToListAsync(ct);

        _db.CodeLinks.RemoveRange(links);
        _db.CodeFiles.Remove(file);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("CodeTraceability_FileDeleted {ProjectId} {FileId}", projectId, id);
        return new DeleteCodeFileResult { DeletedId = id.ToString() };
    }

    // ── Code Link CRUD ─────────────────────────────────────────────────────────

    public async Task<CreateCodeLinkResult> CreateCodeLinkAsync(
        string projectId,
        Guid codeFileId,
        Guid scenarioId,
        CancellationToken ct = default)
    {
        var fileExists = await _db.CodeFiles
            .AnyAsync(f => f.Id == codeFileId && f.ProjectId == projectId, ct);

        if (!fileExists)
            return new CreateCodeLinkResult
            {
                Errors = [new UserError("FILE_NOT_FOUND", "Code file not found in this project.")],
            };

        var scenario = await _db.Scenarios
            .FirstOrDefaultAsync(s => s.Id == scenarioId && s.ProjectId == projectId, ct);

        if (scenario is null)
            return new CreateCodeLinkResult
            {
                Errors = [new UserError("SCENARIO_NOT_FOUND", "Scenario not found in this project.")],
            };

        if (scenario.Kind == ScenarioKind.NeedsClarification)
            return new CreateCodeLinkResult
            {
                Errors = [new UserError("INVALID_KIND", "Only requirements and tests can be linked to code files.")],
            };

        var duplicate = await _db.CodeLinks
            .AnyAsync(l => l.CodeFileId == codeFileId && l.ScenarioId == scenarioId, ct);

        if (duplicate)
            return new CreateCodeLinkResult
            {
                Errors = [new UserError("DUPLICATE_LINK", "This code link already exists.")],
            };

        var link = new CodeLink
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            CodeFileId = codeFileId,
            ScenarioId = scenarioId,
            ScenarioKind = scenario.Kind.ToString(),
            CreatedAt = DateTime.UtcNow,
        };

        _db.CodeLinks.Add(link);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "CodeTraceability_LinkCreated {ProjectId} {FileId} {ScenarioId} {Kind}",
            projectId, codeFileId, scenarioId, scenario.Kind);

        return new CreateCodeLinkResult { Link = link };
    }

    public async Task<DeleteCodeLinkResult> DeleteCodeLinkAsync(
        Guid id, string projectId, CancellationToken ct = default)
    {
        var link = await _db.CodeLinks
            .FirstOrDefaultAsync(l => l.Id == id && l.ProjectId == projectId, ct);

        if (link is null)
            return new DeleteCodeLinkResult
            {
                Errors = [new UserError("NOT_FOUND", "Code link not found.")],
            };

        _db.CodeLinks.Remove(link);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("CodeTraceability_LinkDeleted {ProjectId} {LinkId}", projectId, id);
        return new DeleteCodeLinkResult { DeletedId = id.ToString() };
    }

    // ── Read / Analysis ────────────────────────────────────────────────────────

    public async Task<CodeImpact?> GetCodeImpactAsync(
        string projectId, Guid codeFileId, CancellationToken ct = default)
    {
        var file = await _db.CodeFiles
            .FirstOrDefaultAsync(f => f.Id == codeFileId && f.ProjectId == projectId, ct);

        if (file is null) return null;

        var links = await _db.CodeLinks
            .Where(l => l.CodeFileId == codeFileId)
            .ToListAsync(ct);

        var scenarioIds = links.Select(l => l.ScenarioId).ToHashSet();
        var scenariosById = (await _db.Scenarios
            .Where(s => scenarioIds.Contains(s.Id))
            .ToListAsync(ct))
            .ToDictionary(s => s.Id);

        var linked = links
            .Where(l => scenariosById.ContainsKey(l.ScenarioId))
            .Select(l => new CodeLinkWithScenario { Link = l, Scenario = scenariosById[l.ScenarioId] })
            .ToList();

        return new CodeImpact
        {
            File = file,
            LinkedRequirements = linked
                .Where(x => x.Scenario.Kind == ScenarioKind.Requirement)
                .OrderBy(x => x.Scenario.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            LinkedTests = linked
                .Where(x => x.Scenario.Kind == ScenarioKind.Test)
                .OrderBy(x => x.Scenario.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    public async Task<CodeSummary> GetCodeSummaryAsync(
        string projectId, CancellationToken ct = default)
    {
        var files = await _db.CodeFiles
            .Where(f => f.ProjectId == projectId)
            .Select(f => f.Id)
            .ToListAsync(ct);

        var links = await _db.CodeLinks
            .Where(l => l.ProjectId == projectId)
            .ToListAsync(ct);

        var filesWithLinks = links.Select(l => l.CodeFileId).ToHashSet();

        return new CodeSummary
        {
            TotalFiles = files.Count,
            LinkedRequirements = links.Count(l => l.ScenarioKind == ScenarioKind.Requirement.ToString()),
            LinkedTests = links.Count(l => l.ScenarioKind == ScenarioKind.Test.ToString()),
            UnlinkedFiles = files.Count(id => !filesWithLinks.Contains(id)),
        };
    }
}
