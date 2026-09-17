using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Phase 3 Checkpoint 4: contract compatibility and drift presentation.
///
/// ContractStatePresenter is the single source of wording for both the Integration Quality
/// Review page and the export, so asserting on it covers both surfaces and makes divergence
/// impossible. State is read from the backend verbatim and never inferred from finding counts.
/// </summary>
public class ContractStatePresentationTests
{
    private static IntegrationStatus Status(
        ContractCompatibilityStatus? compatibility = null,
        ContractDriftState? drift = null,
        int? differenceCount = null,
        int? breakingCount = null,
        int? driftCount = null,
        int? driftBreaking = null,
        string? reason = null) =>
        new()
        {
            IntegrationId = "i1",
            Name = "Orders",
            Type = IntegrationType.REST,
            Enabled = true,
            CompatibilityState = compatibility,
            DriftState = drift,
            CompatibilityDifferenceCount = differenceCount,
            CompatibilityBreakingCount = breakingCount,
            DriftDifferenceCount = driftCount,
            DriftBreakingCount = driftBreaking,
            CompatibilityReason = reason
        };

    // ── Compatibility wording ────────────────────────────────────────────────

    [Fact]
    public void CompatibleState_DoesNotMakeAnAbsoluteClaim()
    {
        var label = ContractStatePresenter.Compatibility(
            Status(ContractCompatibilityStatus.Compatible));

        label.Should().Be("No breaking incompatibility detected");
        label.Should().NotContain("fully compatible");
        label.Should().NotContain("passed");
    }

    [Fact]
    public void NotComparableState_RendersTruthfully()
    {
        var status = Status(
            ContractCompatibilityStatus.NotComparable,
            reason: "consumer expectation unavailable");

        ContractStatePresenter.Compatibility(status).Should().Be("Compatibility not assessed");
        ContractStatePresenter.CompatibilityDetail(status).Should().Contain("consumer expectation unavailable");
    }

    [Fact]
    public void NotComparableWithoutReason_StillExplainsItself()
    {
        var detail = ContractStatePresenter.CompatibilityDetail(
            Status(ContractCompatibilityStatus.NotComparable));

        detail.Should().Contain("incomplete");
    }

    [Fact]
    public void BreakingState_RendersBreakingCount()
    {
        var label = ContractStatePresenter.Compatibility(
            Status(ContractCompatibilityStatus.Breaking, differenceCount: 5, breakingCount: 2));

        label.Should().Be("Breaking incompatibility (2)");
    }

    [Fact]
    public void WarningState_RendersDifferenceCount()
    {
        var label = ContractStatePresenter.Compatibility(
            Status(ContractCompatibilityStatus.Warning, differenceCount: 3));

        label.Should().Be("Non-breaking differences (3)");
    }

    [Fact]
    public void MissingCompatibilityState_ReadsAsNotCaptured()
    {
        ContractStatePresenter.Compatibility(Status())
            .Should().Be("Not captured in this report version");
    }

    // ── Drift wording ────────────────────────────────────────────────────────

    [Fact]
    public void BaselineUnavailable_RendersTruthfully()
    {
        ContractStatePresenter.Drift(Status(drift: ContractDriftState.BaselineUnavailable))
            .Should().Be("Baseline unavailable");
    }

    [Fact]
    public void NoChange_RendersAsNoContractChange()
    {
        ContractStatePresenter.Drift(Status(drift: ContractDriftState.NoChange))
            .Should().Be("No contract change detected");
    }

    [Fact]
    public void BreakingDrift_RendersBothCounts()
    {
        ContractStatePresenter.Drift(
                Status(drift: ContractDriftState.BreakingChange, driftCount: 3, driftBreaking: 1))
            .Should().Be("3 changes since previous baseline, 1 breaking");
    }

    [Fact]
    public void NonBreakingDrift_RendersChangeCount()
    {
        ContractStatePresenter.Drift(
                Status(drift: ContractDriftState.NonBreakingChange, driftCount: 2))
            .Should().Be("Contract changed since previous baseline (2)");
    }

    [Fact]
    public void MissingDriftState_ReadsAsNotCaptured()
    {
        ContractStatePresenter.Drift(Status())
            .Should().Be("Not captured in this report version");
    }

    // ── Independence and no recomputation ────────────────────────────────────

    [Fact]
    public void CompatibilityAndDrift_AreRenderedIndependently()
    {
        var status = Status(
            compatibility: ContractCompatibilityStatus.Compatible,
            drift: ContractDriftState.BreakingChange,
            driftCount: 2, driftBreaking: 1);

        ContractStatePresenter.Compatibility(status).Should().Be("No breaking incompatibility detected");
        ContractStatePresenter.Drift(status).Should().Be("2 changes since previous baseline, 1 breaking");
    }

    [Fact]
    public void StateIsReadVerbatim_NotDerivedFromCounts()
    {
        // Breaking state with no recorded differences still renders as breaking: the presenter
        // reports backend state and never recomputes it from counts or findings.
        ContractStatePresenter.Compatibility(Status(ContractCompatibilityStatus.Breaking))
            .Should().Be("Breaking incompatibility (0)");
    }

    // ── Export carries the same states ───────────────────────────────────────

    private static IntegrationQualityReport ReportWith(params IntegrationStatus[] statuses) =>
        new()
        {
            EnvironmentName = "Dev",
            GeneratedAt = DateTime.UtcNow,
            IntegrationCount = statuses.Length,
            EnabledCount = statuses.Length,
            Statuses = statuses.ToList()
        };

    [Fact]
    public void Export_ContainsCompatibilityState()
    {
        var html = new ReportExportService().ExportIntegrationQualityReview(
            ReportWith(Status(ContractCompatibilityStatus.Breaking, breakingCount: 2)), "test");

        html.Should().Contain("Contract Evidence");
        html.Should().Contain("Breaking incompatibility (2)");
    }

    [Fact]
    public void Export_ContainsNotComparable()
    {
        var html = new ReportExportService().ExportIntegrationQualityReview(
            ReportWith(Status(ContractCompatibilityStatus.NotComparable)), "test");

        html.Should().Contain("Compatibility not assessed");
    }

    [Fact]
    public void Export_ContainsBaselineUnavailable()
    {
        var html = new ReportExportService().ExportIntegrationQualityReview(
            ReportWith(Status(drift: ContractDriftState.BaselineUnavailable)), "test");

        html.Should().Contain("Baseline unavailable");
    }

    [Fact]
    public void Export_ContainsDriftState()
    {
        var html = new ReportExportService().ExportIntegrationQualityReview(
            ReportWith(Status(drift: ContractDriftState.BreakingChange, driftCount: 3, driftBreaking: 1)),
            "test");

        html.Should().Contain("3 changes since previous baseline, 1 breaking");
    }

    [Fact]
    public void Export_ContainsProducerConsumerProvenance()
    {
        var status = new IntegrationStatus
        {
            IntegrationId = "m1", Name = "Placement Events", Type = IntegrationType.EventHub, Enabled = true,
            ProducerService = "PlacementService",
            ConsumerService = "NotificationService",
            ProducerContractSource = "assembly://producer.dll",
            ConsumerContractSource = "assembly://consumer.dll",
            CompatibilityState = ContractCompatibilityStatus.Compatible
        };

        var html = new ReportExportService().ExportIntegrationQualityReview(ReportWith(status), "test");

        html.Should().Contain("PlacementService");
        html.Should().Contain("NotificationService");
        html.Should().Contain("assembly://producer.dll");
    }

    [Fact]
    public void Export_ContainsDifferenceDetail()
    {
        var status = new IntegrationStatus
        {
            IntegrationId = "i1", Name = "Orders", Type = IntegrationType.REST, Enabled = true,
            CompatibilityState = ContractCompatibilityStatus.Breaking,
            CompatibilityBreakingCount = 1,
            CompatibilityDifferences =
            [
                new ContractDifference
                {
                    Path = "Order",
                    Property = "customerId",
                    Severity = ContractDifferenceSeverity.Breaking,
                    Explanation = "Consumer requires property 'customerId' that producer does not provide"
                }
            ]
        };

        var html = new ReportExportService().ExportIntegrationQualityReview(ReportWith(status), "test");

        html.Should().Contain("Contract Differences");
        html.Should().Contain("customerId");
        html.Should().Contain("Breaking");
    }

    [Fact]
    public void Export_DoesNotUseProhibitedCompatibilityWording()
    {
        var html = new ReportExportService().ExportIntegrationQualityReview(
            ReportWith(
                Status(ContractCompatibilityStatus.Compatible),
                Status(ContractCompatibilityStatus.NotComparable)),
            "test");

        html.Should().NotContain("fully compatible");
        html.Should().NotContain("Contract passed");
    }

    [Fact]
    public void Export_LegacyStatusWithoutContractFields_ReadsAsNotCaptured()
    {
        var html = new ReportExportService().ExportIntegrationQualityReview(
            ReportWith(Status()), "test");

        html.Should().Contain("Not captured in this report version");
    }
}
