using System.Diagnostics;
using BirkNext.Api.Services;
using HotChocolate;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BirkNext.Api.GraphQL;

/// <summary>Root mutation type.</summary>
public class Mutation
{
    /// <summary>Creates a new scenario in the given project.</summary>
    public async Task<CreateScenarioPayload> CreateScenarioAsync(
        CreateScenarioInput input,
        [Service] ScenarioService scenarioService,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken cancellationToken)
    {
        var correlationId = httpContextAccessor.HttpContext?
            .Response.Headers["X-Correlation-Id"]
            .FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        var result = await scenarioService.CreateAsync(
            title: input.Title,
            description: input.Description,
            kind: input.Kind,
            projectId: input.ProjectId,
            correlationId: correlationId,
            ct: cancellationToken);

        return new CreateScenarioPayload
        {
            Scenario = result.Scenario,
            Errors = result.Errors,
            CorrelationId = correlationId,
        };
    }

    /// <summary>Creates multiple scenarios from a completed extraction review session.</summary>
    public async Task<CreateScenariosPayload> CreateScenariosAsync(
        CreateScenariosInput input,
        [Service] ScenarioService scenarioService,
        [Service] IHttpContextAccessor httpContextAccessor,
        [Service] ILogger<Mutation> logger,
        CancellationToken cancellationToken)
    {
        if (input.Items.Count == 0)
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("ITEMS_EMPTY")
                .SetMessage("At least one item is required.")
                .Build());

        var correlationId = httpContextAccessor.HttpContext?
            .Response.Headers["X-Correlation-Id"]
            .FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        var sw = Stopwatch.StartNew();

        var items = input.Items.Select(i =>
            new CreateScenarioItemInput(i.Title, i.Description, i.Kind, i.ProjectId));

        var batchResults = await scenarioService.CreateBatchAsync(items, correlationId, cancellationToken);

        sw.Stop();

        var results = new List<ICreateScenarioResult>(batchResults.Count);
        int successCount = 0, failureCount = 0;

        foreach (var r in batchResults)
        {
            if (r.IsSuccess)
            {
                results.Add(new CreateScenarioSuccess { Scenario = r.Scenario! });
                successCount++;
            }
            else
            {
                results.Add(new CreateScenarioError
                {
                    Code = r.Error!.Code,
                    Message = r.Error.Message,
                    Field = r.Error.Field,
                });
                failureCount++;
            }
        }

        // no raw text: only numeric metrics, projectId, and correlationId — never candidate titles
        // sessionId is intentionally omitted: it is a client-side correlation token, not needed server-side
        logger.LogInformation(
            "CandidateReviewSaved: correlationId={CorrelationId}, projectId={ProjectId}, selectedCount={SelectedCount}, totalExtracted={TotalExtracted}, scenariosCreated={ScenariosCreated}, failedCount={FailedCount}, durationMs={DurationMs}",
            correlationId,
            input.Items[0].ProjectId,
            input.ExtractionMetadata?.SelectedCount ?? -1,
            input.ExtractionMetadata?.TotalExtracted ?? -1,
            successCount,
            failureCount,
            sw.ElapsedMilliseconds);

        return new CreateScenariosPayload
        {
            Results = results,
            SuccessCount = successCount,
            FailureCount = failureCount,
            CorrelationId = correlationId,
        };
    }

    /// <summary>Persists the QA review decisions for all candidates in an extraction session.</summary>
    public async Task<SaveReviewedCandidatesPayload> SaveReviewedCandidatesAsync(
        SaveReviewedCandidatesInput input,
        [Service] ReviewedCandidateService reviewedCandidateService,
        [Service] IHttpContextAccessor httpContextAccessor,
        [Service] ILogger<Mutation> logger,
        CancellationToken cancellationToken)
    {
        if (input.Items.Count == 0)
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("ITEMS_EMPTY")
                .SetMessage("At least one item is required.")
                .Build());

        var correlationId = httpContextAccessor.HttpContext?
            .Response.Headers["X-Correlation-Id"]
            .FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        var projectId = input.Items[0].ProjectId;
        var sessionId = input.SessionId ?? Guid.NewGuid().ToString();

        var items = input.Items.Select(i => new ReviewedCandidateItem(
            i.Title,
            i.Classification,
            i.ReviewStatus,
            i.SourceDocument,
            i.SourceSection,
            i.ReviewedBy,
            i.ReviewedAt));

        var savedCount = await reviewedCandidateService.SaveBatchAsync(
            projectId, sessionId, items, correlationId, cancellationToken);

        logger.LogInformation(
            "ReviewDecisionsSaved: correlationId={CorrelationId}, projectId={ProjectId}, sessionId={SessionId}, savedCount={SavedCount}",
            correlationId, projectId, sessionId, savedCount);

        return new SaveReviewedCandidatesPayload
        {
            SavedCount = savedCount,
            CorrelationId = correlationId,
        };
    }

    /// <summary>Deletes a scenario by ID.</summary>
    public async Task<DeleteScenarioPayload> DeleteScenarioAsync(
        [ID] string id,
        [Service] ScenarioService scenarioService,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken cancellationToken)
    {
        var correlationId = httpContextAccessor.HttpContext?
            .Response.Headers["X-Correlation-Id"]
            .FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        var result = await scenarioService.DeleteAsync(id, correlationId, cancellationToken);

        return new DeleteScenarioPayload
        {
            DeletedId = result.DeletedId,
            Success = result.IsSuccess,
            Errors = result.Errors,
            CorrelationId = correlationId,
        };
    }

    /// <summary>Persists the traceability links between candidates for an extraction session.</summary>
    public async Task<SaveCandidateLinksPayload> SaveCandidateLinksAsync(
        SaveCandidateLinksInput input,
        [Service] CandidateLinkService candidateLinkService,
        [Service] IHttpContextAccessor httpContextAccessor,
        [Service] ILogger<Mutation> logger,
        CancellationToken cancellationToken)
    {
        var correlationId = httpContextAccessor.HttpContext?
            .Response.Headers["X-Correlation-Id"]
            .FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        var links = input.Links.Select(l => new CandidateLinkItem(
            l.SourceCandidateRef,
            l.TargetCandidateRef,
            l.LinkType));

        var savedCount = await candidateLinkService.SaveBatchAsync(
            input.ProjectId, input.SessionId, links, correlationId, cancellationToken);

        logger.LogInformation(
            "CandidateLinksSaved: correlationId={CorrelationId}, projectId={ProjectId}, sessionId={SessionId}, savedCount={SavedCount}",
            correlationId, input.ProjectId, input.SessionId, savedCount);

        return new SaveCandidateLinksPayload
        {
            SavedCount = savedCount,
            CorrelationId = correlationId,
        };
    }

    /// <summary>Saves a QA delta review from a specification comparison.</summary>
    public async Task<SaveQaDeltaReviewPayload> SaveQaDeltaReviewAsync(
        SaveQaDeltaReviewInput input,
        [Service] QaDeltaReviewService qaDeltaReviewService,
        [Service] IHttpContextAccessor httpContextAccessor,
        [Service] ILogger<Mutation> logger,
        CancellationToken cancellationToken)
    {
        var correlationId = httpContextAccessor.HttpContext?
            .Response.Headers["X-Correlation-Id"]
            .FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        var result = await qaDeltaReviewService.CreateAsync(
            title: input.Title,
            projectId: input.ProjectId,
            oldSpecFileName: input.OldSpecFileName,
            newSpecFileName: input.NewSpecFileName,
            oldSpecHash: input.OldSpecHash,
            newSpecHash: input.NewSpecHash,
            oldSpecSize: input.OldSpecSize,
            newSpecSize: input.NewSpecSize,
            analysisProfile: input.AnalysisProfile,
            summaryJson: input.SummaryJson,
            deltaItemsJson: input.DeltaItemsJson,
            correlationId: correlationId,
            ct: cancellationToken);

        return new SaveQaDeltaReviewPayload
        {
            Review = result.Review,
            Errors = result.Errors,
            CorrelationId = correlationId,
        };
    }

    /// <summary>Deletes a QA delta review by ID.</summary>
    public async Task<DeleteQaDeltaReviewPayload> DeleteQaDeltaReviewAsync(
        [ID] string id,
        [Service] QaDeltaReviewService qaDeltaReviewService,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken cancellationToken)
    {
        var correlationId = httpContextAccessor.HttpContext?
            .Response.Headers["X-Correlation-Id"]
            .FirstOrDefault()
            ?? Guid.NewGuid().ToString();

        var result = await qaDeltaReviewService.DeleteAsync(id, correlationId, cancellationToken);

        return new DeleteQaDeltaReviewPayload
        {
            DeletedId = result.DeletedId,
            Success = result.IsSuccess,
            Errors = result.Errors,
            CorrelationId = correlationId,
        };
    }
}
