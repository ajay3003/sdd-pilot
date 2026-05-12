using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

public record UserError(string Code, string Message, string? Field = null);

public class ScenarioResult
{
    public Scenario? Scenario { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
}

public class ScenarioService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<ScenarioService>? _logger;

    public ScenarioService(AppDbContext dbContext, ILogger<ScenarioService>? logger = null)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<ScenarioResult> CreateAsync(
        string title,
        string? description,
        ScenarioKind kind,
        string projectId,
        string correlationId,
        CancellationToken ct = default)
    {
        var errors = Validate(title, kind);

        if (errors.Count > 0)
        {
            _logger?.LogWarning(
                "ScenarioValidationFailed {CorrelationId} {ProjectId} {ErrorCodes}",
                correlationId, projectId, string.Join(",", errors.Select(e => e.Code)));

            return new ScenarioResult { Errors = errors };
        }

        var scenario = new Scenario
        {
            Title = title,
            Description = description,
            Kind = kind,
            ProjectId = projectId,
        };

        try
        {
            _dbContext.Scenarios.Add(scenario);
            await _dbContext.SaveChangesAsync(ct);

            _logger?.LogInformation(
                "ScenarioCreated {CorrelationId} {ProjectId} {ScenarioId}",
                correlationId, projectId, scenario.Id);

            return new ScenarioResult { Scenario = scenario };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "ScenarioCreationFailed {CorrelationId} {ProjectId}",
                correlationId, projectId);
            throw;
        }
    }

    public async Task<IReadOnlyList<Scenario>> GetAllAsync(
        string projectId,
        CancellationToken ct = default)
    {
        return await _dbContext.Scenarios
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    private static List<UserError> Validate(string title, ScenarioKind kind)
    {
        var errors = new List<UserError>();

        if (string.IsNullOrWhiteSpace(title))
            errors.Add(new UserError("TITLE_REQUIRED", "Title is required.", "title"));

        if (!Enum.IsDefined(typeof(ScenarioKind), kind))
            errors.Add(new UserError("INVALID_KIND", "A valid type must be selected.", "kind"));

        return errors;
    }
}
