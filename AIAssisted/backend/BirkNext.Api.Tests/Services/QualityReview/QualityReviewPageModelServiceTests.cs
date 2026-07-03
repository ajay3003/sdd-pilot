using BirkNext.Api.Services;
using BirkNext.Api.Services.QualityReview;
using Moq;
using Xunit;

namespace BirkNext.Api.Tests.Services.QualityReview;

/// <summary>
/// Integration tests for Quality Review page model service.
/// Verifies that the orchestration service correctly delegates to builders.
/// </summary>
public class QualityReviewPageModelServiceTests
{
    [Fact]
    public async Task BuildQualityReviewModelAsync_DelegatesTo_QualityReviewBuilder()
    {
        var qualityBuilder = new Mock<IQualityReviewPageModelBuilder_QualityReview>();
        var expectedModel = new QualityReviewPageModel
        {
            Title = "Quality Review",
            ReadinessStatus = QualityReviewStatus.Available
        };
        qualityBuilder
            .Setup(b => b.BuildPageModelAsync())
            .ReturnsAsync(expectedModel);

        var apiBuilder = new Mock<IQualityReviewPageModelBuilder_ApiQuality>();
        var frontendBuilder = new Mock<IQualityReviewPageModelBuilder_FrontendQuality>();
        var integrationBuilder = new Mock<IQualityReviewPageModelBuilder_IntegrationQuality>();

        var service = new QualityReviewPageModelService(
            qualityBuilder.Object,
            apiBuilder.Object,
            frontendBuilder.Object,
            integrationBuilder.Object,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<QualityReviewPageModelService>());

        var model = await service.BuildQualityReviewModelAsync();

        Assert.Equal("Quality Review", model.Title);
        Assert.Equal(QualityReviewStatus.Available, model.ReadinessStatus);
        qualityBuilder.Verify(b => b.BuildPageModelAsync(), Times.Once);
    }

    [Fact]
    public async Task BuildApiQualityReviewModelAsync_DelegatesTo_ApiBuilder()
    {
        var apiBuilder = new Mock<IQualityReviewPageModelBuilder_ApiQuality>();
        var expectedModel = new QualityReviewPageModel
        {
            Title = "API Quality Review",
            ReadinessStatus = QualityReviewStatus.Blocked
        };
        apiBuilder
            .Setup(b => b.BuildPageModelAsync())
            .ReturnsAsync(expectedModel);

        var qualityBuilder = new Mock<IQualityReviewPageModelBuilder_QualityReview>();
        var frontendBuilder = new Mock<IQualityReviewPageModelBuilder_FrontendQuality>();
        var integrationBuilder = new Mock<IQualityReviewPageModelBuilder_IntegrationQuality>();

        var service = new QualityReviewPageModelService(
            qualityBuilder.Object,
            apiBuilder.Object,
            frontendBuilder.Object,
            integrationBuilder.Object,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<QualityReviewPageModelService>());

        var model = await service.BuildApiQualityReviewModelAsync();

        Assert.Equal("API Quality Review", model.Title);
        Assert.Equal(QualityReviewStatus.Blocked, model.ReadinessStatus);
        apiBuilder.Verify(b => b.BuildPageModelAsync(), Times.Once);
    }

    [Fact]
    public async Task BuildFrontendQualityReviewModelAsync_DelegatesTo_FrontendBuilder()
    {
        var frontendBuilder = new Mock<IQualityReviewPageModelBuilder_FrontendQuality>();
        var expectedModel = new QualityReviewPageModel
        {
            Title = "Frontend Quality Review",
            ReadinessStatus = QualityReviewStatus.Warning
        };
        frontendBuilder
            .Setup(b => b.BuildPageModelAsync())
            .ReturnsAsync(expectedModel);

        var qualityBuilder = new Mock<IQualityReviewPageModelBuilder_QualityReview>();
        var apiBuilder = new Mock<IQualityReviewPageModelBuilder_ApiQuality>();
        var integrationBuilder = new Mock<IQualityReviewPageModelBuilder_IntegrationQuality>();

        var service = new QualityReviewPageModelService(
            qualityBuilder.Object,
            apiBuilder.Object,
            frontendBuilder.Object,
            integrationBuilder.Object,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<QualityReviewPageModelService>());

        var model = await service.BuildFrontendQualityReviewModelAsync();

        Assert.Equal("Frontend Quality Review", model.Title);
        Assert.Equal(QualityReviewStatus.Warning, model.ReadinessStatus);
        frontendBuilder.Verify(b => b.BuildPageModelAsync(), Times.Once);
    }

    [Fact]
    public async Task BuildIntegrationQualityReviewModelAsync_DelegatesTo_IntegrationBuilder()
    {
        var integrationBuilder = new Mock<IQualityReviewPageModelBuilder_IntegrationQuality>();
        var expectedModel = new QualityReviewPageModel
        {
            Title = "Integration Quality Review",
            ReadinessStatus = QualityReviewStatus.Blocked
        };
        integrationBuilder
            .Setup(b => b.BuildPageModelAsync())
            .ReturnsAsync(expectedModel);

        var qualityBuilder = new Mock<IQualityReviewPageModelBuilder_QualityReview>();
        var apiBuilder = new Mock<IQualityReviewPageModelBuilder_ApiQuality>();
        var frontendBuilder = new Mock<IQualityReviewPageModelBuilder_FrontendQuality>();

        var service = new QualityReviewPageModelService(
            qualityBuilder.Object,
            apiBuilder.Object,
            frontendBuilder.Object,
            integrationBuilder.Object,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<QualityReviewPageModelService>());

        var model = await service.BuildIntegrationQualityReviewModelAsync();

        Assert.Equal("Integration Quality Review", model.Title);
        Assert.Equal(QualityReviewStatus.Blocked, model.ReadinessStatus);
        integrationBuilder.Verify(b => b.BuildPageModelAsync(), Times.Once);
    }

    [Fact]
    public async Task Service_HandlesBuilderExceptions_Gracefully()
    {
        var qualityBuilder = new Mock<IQualityReviewPageModelBuilder_QualityReview>();
        qualityBuilder
            .Setup(b => b.BuildPageModelAsync())
            .ThrowsAsync(new InvalidOperationException("Builder error"));

        var apiBuilder = new Mock<IQualityReviewPageModelBuilder_ApiQuality>();
        var frontendBuilder = new Mock<IQualityReviewPageModelBuilder_FrontendQuality>();
        var integrationBuilder = new Mock<IQualityReviewPageModelBuilder_IntegrationQuality>();

        var service = new QualityReviewPageModelService(
            qualityBuilder.Object,
            apiBuilder.Object,
            frontendBuilder.Object,
            integrationBuilder.Object,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<QualityReviewPageModelService>());

        var model = await service.BuildQualityReviewModelAsync();

        // Should return error model instead of throwing
        Assert.Equal(QualityReviewStatus.Fail, model.ReadinessStatus);
        Assert.Contains("Failed to load", model.Summary.ReadinessMessage);
    }
}
