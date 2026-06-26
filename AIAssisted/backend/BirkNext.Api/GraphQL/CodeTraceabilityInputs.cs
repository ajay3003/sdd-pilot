using BirkNext.Api.Models;
using BirkNext.Api.Services;

namespace BirkNext.Api.GraphQL;

// ─── Inputs ────────────────────────────────────────────────────────────────────

public record RegisterCodeFileInput(string ProjectId, string FilePath, string? Description = null);
public record DeleteCodeFileInput(string Id, string ProjectId);
public record CreateCodeLinkInput(string ProjectId, string CodeFileId, string ScenarioId);
public record DeleteCodeLinkInput(string Id, string ProjectId);

// ─── Payloads ──────────────────────────────────────────────────────────────────

public sealed class RegisterCodeFilePayload
{
    public CodeFile? File { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
}

public sealed class DeleteCodeFilePayload
{
    public string? DeletedId { get; init; }
    public bool Success { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
}

public sealed class CreateCodeLinkPayload
{
    public CodeLink? Link { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
}

public sealed class DeleteCodeLinkPayload
{
    public string? DeletedId { get; init; }
    public bool Success { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
}
