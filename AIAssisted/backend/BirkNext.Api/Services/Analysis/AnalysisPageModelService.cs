using Microsoft.Extensions.Logging;

namespace BirkNext.Api.Services.Analysis;

/// <summary>
/// Service that builds analysis page models for all 5 analysis pages.
/// Orchestrates the individual page builders with error handling.
/// </summary>
public interface IAnalysisPageModelService
{
    Task<AnalysisPageModel> BuildSpecDriftModelAsync();
    Task<AnalysisPageModel> BuildImpactAnalysisModelAsync();
    Task<AnalysisPageModel> BuildRequirementsTraceabilityModelAsync();
    Task<AnalysisPageModel> BuildImplementationReviewModelAsync();
    Task<AnalysisPageModel> BuildImplementationTraceabilityModelAsync();
}

public class AnalysisPageModelService : IAnalysisPageModelService
{
    private readonly ISpecDriftPageModelBuilder _specDriftBuilder;
    private readonly IImpactAnalysisPageModelBuilder _impactAnalysisBuilder;
    private readonly IRequirementsTraceabilityPageModelBuilder _traceabilityBuilder;
    private readonly IImplementationReviewPageModelBuilder _implementationReviewBuilder;
    private readonly IImplementationTraceabilityPageModelBuilder _implementationTraceabilityBuilder;
    private readonly ILogger<AnalysisPageModelService> _logger;

    public AnalysisPageModelService(
        ISpecDriftPageModelBuilder specDriftBuilder,
        IImpactAnalysisPageModelBuilder impactAnalysisBuilder,
        IRequirementsTraceabilityPageModelBuilder traceabilityBuilder,
        IImplementationReviewPageModelBuilder implementationReviewBuilder,
        IImplementationTraceabilityPageModelBuilder implementationTraceabilityBuilder,
        ILogger<AnalysisPageModelService> logger)
    {
        _specDriftBuilder = specDriftBuilder;
        _impactAnalysisBuilder = impactAnalysisBuilder;
        _traceabilityBuilder = traceabilityBuilder;
        _implementationReviewBuilder = implementationReviewBuilder;
        _implementationTraceabilityBuilder = implementationTraceabilityBuilder;
        _logger = logger;
    }

    public async Task<AnalysisPageModel> BuildSpecDriftModelAsync()
    {
        try
        {
            _logger.LogInformation("Building Spec Drift page model");
            return await _specDriftBuilder.BuildPageModelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Spec Drift model");
            return ErrorModel("Spec Drift", "Failed to build page model");
        }
    }

    public async Task<AnalysisPageModel> BuildImpactAnalysisModelAsync()
    {
        try
        {
            _logger.LogInformation("Building Impact Analysis page model");
            return await _impactAnalysisBuilder.BuildPageModelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Impact Analysis model");
            return ErrorModel("Impact Analysis", "Failed to build page model");
        }
    }

    public async Task<AnalysisPageModel> BuildRequirementsTraceabilityModelAsync()
    {
        try
        {
            _logger.LogInformation("Building Requirements Traceability page model");
            return await _traceabilityBuilder.BuildPageModelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Requirements Traceability model");
            return ErrorModel("Requirements Traceability", "Failed to build page model");
        }
    }

    public async Task<AnalysisPageModel> BuildImplementationReviewModelAsync()
    {
        try
        {
            _logger.LogInformation("Building Implementation Review page model");
            return await _implementationReviewBuilder.BuildPageModelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Implementation Review model");
            return ErrorModel("Implementation Review", "Failed to build page model");
        }
    }

    public async Task<AnalysisPageModel> BuildImplementationTraceabilityModelAsync()
    {
        try
        {
            _logger.LogInformation("Building Implementation Traceability page model");
            return await _implementationTraceabilityBuilder.BuildPageModelAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building Implementation Traceability model");
            return ErrorModel("Implementation Traceability", "Failed to build page model");
        }
    }

    private static AnalysisPageModel ErrorModel(string title, string message)
    {
        return new AnalysisPageModel
        {
            Title = title,
            Description = "Analysis page",
            ReadinessStatus = AnalysisStatus.Fail,
            RequiredInputs = [],
            MissingInputs = [],
            Summary = new AnalysisSummary
            {
                CanRun = false,
                ReadinessMessage = message
            }
        };
    }
}
