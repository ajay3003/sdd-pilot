using BirkNext.Api.Data;
using BirkNext.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services;

public class QaDeltaReviewResult
{
    public QaDeltaReview? Review { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
}

public class DeleteQaDeltaReviewResult
{
    public string? DeletedId { get; init; }
    public IReadOnlyList<UserError> Errors { get; init; } = [];
    public bool IsSuccess => DeletedId is not null;
}

public class QaDeltaReviewService
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<QaDeltaReviewService>? _logger;

    public QaDeltaReviewService(AppDbContext dbContext, ILogger<QaDeltaReviewService>? logger = null)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<QaDeltaReviewResult> CreateAsync(
        string title,
        string projectId,
        string? oldSpecFileName,
        string? newSpecFileName,
        string? oldSpecHash,
        string? newSpecHash,
        int? oldSpecSize,
        int? newSpecSize,
        string analysisProfile,
        string summaryJson,
        string deltaItemsJson,
        string correlationId,
        CancellationToken ct = default)
    {
        var errors = Validate(title);

        if (errors.Count > 0)
        {
            _logger?.LogWarning(
                "QaDeltaReviewValidationFailed {CorrelationId} {ProjectId} {ErrorCodes}",
                correlationId, projectId, string.Join(",", errors.Select(e => e.Code)));

            return new QaDeltaReviewResult { Errors = errors };
        }

        var review = new QaDeltaReview
        {
            Title = title,
            ProjectId = projectId,
            OldSpecFileName = oldSpecFileName,
            NewSpecFileName = newSpecFileName,
            OldSpecHash = oldSpecHash,
            NewSpecHash = newSpecHash,
            OldSpecSize = oldSpecSize,
            NewSpecSize = newSpecSize,
            AnalysisProfile = analysisProfile,
            SummaryJson = summaryJson,
            DeltaItemsJson = deltaItemsJson,
        };

        try
        {
            _dbContext.QaDeltaReviews.Add(review);
            await _dbContext.SaveChangesAsync(ct);

            _logger?.LogInformation(
                "DeltaReviewSaved: correlationId={CorrelationId}, projectId={ProjectId}, reviewId={ReviewId}",
                correlationId, projectId, review.Id);

            return new QaDeltaReviewResult { Review = review };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "QaDeltaReviewCreationFailed {CorrelationId} {ProjectId}",
                correlationId, projectId);
            throw;
        }
    }

    public async Task<IReadOnlyList<QaDeltaReview>> GetAllAsync(
        string projectId,
        CancellationToken ct = default)
    {
        return await _dbContext.QaDeltaReviews
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<QaDeltaReview?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
            return null;

        return await _dbContext.QaDeltaReviews.FindAsync([guid], ct);
    }

    public async Task<DeleteQaDeltaReviewResult> DeleteAsync(
        string id,
        string correlationId,
        CancellationToken ct = default)
    {
        if (!Guid.TryParse(id, out var guid))
        {
            return new DeleteQaDeltaReviewResult
            {
                Errors = [new UserError("NOT_FOUND", "Delta review not found")]
            };
        }

        var review = await _dbContext.QaDeltaReviews.FindAsync([guid], ct);

        if (review is null)
        {
            _logger?.LogWarning(
                "QaDeltaReviewDeleteNotFound {CorrelationId} {ReviewId}",
                correlationId, id);

            return new DeleteQaDeltaReviewResult
            {
                Errors = [new UserError("NOT_FOUND", "Delta review not found")]
            };
        }

        try
        {
            _dbContext.QaDeltaReviews.Remove(review);
            await _dbContext.SaveChangesAsync(ct);

            _logger?.LogInformation(
                "QaDeltaReviewDeleted {CorrelationId} {ReviewId}",
                correlationId, id);

            return new DeleteQaDeltaReviewResult { DeletedId = id };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "QaDeltaReviewDeletionFailed {CorrelationId} {ReviewId}",
                correlationId, id);
            throw;
        }
    }

    private static List<UserError> Validate(string title)
    {
        var errors = new List<UserError>();

        if (string.IsNullOrWhiteSpace(title))
            errors.Add(new UserError("TITLE_REQUIRED", "Title is required", "title"));
        else if (title.Length > 500)
            errors.Add(new UserError("TITLE_TOO_LONG", "Title must be 500 characters or fewer", "title"));

        return errors;
    }
}
