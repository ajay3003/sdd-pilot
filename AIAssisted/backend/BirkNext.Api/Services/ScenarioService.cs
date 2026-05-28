using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

public record UserError(string Code, string Message, string? Field = null);

public record CreateScenarioItemInput(string Title, string? Description, ScenarioKind Kind, string ProjectId);

public sealed class BatchScenarioResult
{
    public Scenario? Scenario { get; init; }
    public UserError? Error { get; init; }
    public bool IsSuccess => Scenario is not null;
}

public class ScenarioResult
{
    public Scenario? Scenario { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
}

public class DeleteScenarioResult
{
    public string? DeletedId { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    public bool IsSuccess => DeletedId is not null;
}

public class ReorderTestScenariosResult
{
    public bool Success { get; init; }
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

        int displayOrder = 0;
        if (kind == ScenarioKind.Test)
        {
            var maxOrder = await _dbContext.Scenarios
                .Where(s => s.ProjectId == projectId && s.Kind == ScenarioKind.Test)
                .MaxAsync(s => (int?)s.DisplayOrder, ct) ?? -1;
            displayOrder = maxOrder + 1;
        }

        var scenario = new Scenario
        {
            Title = title,
            Description = description,
            Kind = kind,
            ProjectId = projectId,
            DisplayOrder = displayOrder,
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

    public async Task<IReadOnlyList<BatchScenarioResult>> CreateBatchAsync(
        IEnumerable<CreateScenarioItemInput> items,
        string correlationId,
        CancellationToken ct = default)
    {
        var itemsList = items.ToList();
        var results = new BatchScenarioResult[itemsList.Count];
        var validScenarios = new List<(int Index, Scenario Scenario)>(itemsList.Count);

        var maxOrderByProject = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < itemsList.Count; i++)
        {
            var item = itemsList[i];

            if (string.IsNullOrWhiteSpace(item.ProjectId))
            {
                results[i] = new BatchScenarioResult
                {
                    Error = new UserError("PROJECT_ID_REQUIRED", "Project ID is required", "projectId")
                };
                continue;
            }

            var errors = Validate(item.Title, item.Kind);
            if (errors.Count > 0)
            {
                results[i] = new BatchScenarioResult { Error = errors[0] };
                continue;
            }

            int displayOrder = 0;
            if (item.Kind == ScenarioKind.Test)
            {
                if (!maxOrderByProject.TryGetValue(item.ProjectId, out var currentMax))
                {
                    currentMax = await _dbContext.Scenarios
                        .Where(s => s.ProjectId == item.ProjectId && s.Kind == ScenarioKind.Test)
                        .MaxAsync(s => (int?)s.DisplayOrder, ct) ?? -1;
                }
                displayOrder = currentMax + 1;
                maxOrderByProject[item.ProjectId] = displayOrder;
            }

            var scenario = new Scenario
            {
                Title = item.Title,
                Description = item.Description,
                Kind = item.Kind,
                ProjectId = item.ProjectId,
                DisplayOrder = displayOrder,
            };

            _dbContext.Scenarios.Add(scenario);
            validScenarios.Add((i, scenario));
        }

        if (validScenarios.Count > 0)
        {
            try
            {
                await _dbContext.SaveChangesAsync(ct);
                foreach (var (index, scenario) in validScenarios)
                    results[index] = new BatchScenarioResult { Scenario = scenario };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "BatchScenarioCreationFailed {CorrelationId}", correlationId);
                throw;
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<Scenario>> GetAllAsync(
        string projectId,
        CancellationToken ct = default)
    {
        return await _dbContext.Scenarios
            .Where(s => s.ProjectId == projectId)
            .OrderBy(s => s.Kind == ScenarioKind.Test ? s.DisplayOrder : int.MaxValue)
            .ThenByDescending(s => s.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<ReorderTestScenariosResult> ReorderTestScenariosAsync(
        string projectId,
        IReadOnlyList<string> orderedIds,
        string correlationId,
        CancellationToken ct = default)
    {
        var guids = new List<Guid>(orderedIds.Count);
        foreach (var id in orderedIds)
        {
            if (!Guid.TryParse(id, out var guid))
            {
                return new ReorderTestScenariosResult
                {
                    Errors = [new UserError("INVALID_ID", $"Invalid scenario ID: {id}")]
                };
            }
            guids.Add(guid);
        }

        var scenarios = await _dbContext.Scenarios
            .Where(s => s.ProjectId == projectId && s.Kind == ScenarioKind.Test && guids.Contains(s.Id))
            .ToListAsync(ct);

        if (scenarios.Count != guids.Count)
        {
            return new ReorderTestScenariosResult
            {
                Errors = [new UserError("SCENARIOS_NOT_FOUND", "One or more scenarios were not found")]
            };
        }

        var scenarioById = scenarios.ToDictionary(s => s.Id);
        for (int i = 0; i < guids.Count; i++)
            scenarioById[guids[i]].DisplayOrder = i;

        await _dbContext.SaveChangesAsync(ct);

        _logger?.LogInformation(
            "ScenariosReordered {CorrelationId} {ProjectId} {Count}",
            correlationId, projectId, guids.Count);

        return new ReorderTestScenariosResult { Success = true };
    }

    public async Task<DeleteScenarioResult> DeleteAsync(
        string id,
        string correlationId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
        {
            return new DeleteScenarioResult
            {
                Errors = [new UserError("NOT_FOUND", "Scenario not found")]
            };
        }

        var scenario = await _dbContext.Scenarios.FindAsync([guid], ct);

        if (scenario is null)
        {
            _logger?.LogWarning(
                "ScenarioDeleteNotFound {CorrelationId} {ScenarioId}",
                correlationId, id);

            return new DeleteScenarioResult
            {
                Errors = [new UserError("NOT_FOUND", "Scenario not found")]
            };
        }

        try
        {
            _dbContext.Scenarios.Remove(scenario);
            await _dbContext.SaveChangesAsync(ct);

            _logger?.LogInformation(
                "ScenarioDeleted {CorrelationId} {ScenarioId}",
                correlationId, id);

            return new DeleteScenarioResult { DeletedId = id };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "ScenarioDeletionFailed {CorrelationId} {ScenarioId}",
                correlationId, id);
            throw;
        }
    }

    private static List<UserError> Validate(string title, ScenarioKind kind)
    {
        var errors = new List<UserError>();

        if (string.IsNullOrWhiteSpace(title))
            errors.Add(new UserError("TITLE_REQUIRED", "Title is required", "title"));
        else if (title.Length > 500)
            errors.Add(new UserError("TITLE_TOO_LONG", "Title must be 500 characters or fewer", "title"));

        if (!Enum.IsDefined(typeof(ScenarioKind), kind))
            errors.Add(new UserError("INVALID_KIND", "A valid type must be selected", "kind"));

        return errors;
    }
}
