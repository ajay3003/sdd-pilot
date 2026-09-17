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

    // ── History (Checkpoint 5) ───────────────────────────────────────────────

    private static IntegrationQualityReport HistoryReport(
        bool baselineAvailable,
        int changeCount = 0,
        DateTimeOffset? previousAt = null,
        SnapshotPersistenceState persistence = SnapshotPersistenceState.Saved,
        params IntegrationHistoricalChange[] changes) =>
        new()
        {
            EnvironmentName = "Dev",
            GeneratedAt = new DateTime(2026, 9, 17, 10, 42, 0, DateTimeKind.Utc),
            BaselineAvailable = baselineAvailable,
            HistoricalChangeCount = changeCount,
            PreviousSnapshotCapturedAt = previousAt,
            SnapshotPersistenceState = persistence,
            HistoricalChanges = changes.ToList()
        };

    [Fact]
    public void NoBaseline_RendersTruthfully()
    {
        ContractStatePresenter.History(HistoryReport(baselineAvailable: false))
            .Should().Be("No previous baseline");
    }

    [Fact]
    public void BaselineWithChanges_RendersTimestampAndCount()
    {
        var label = ContractStatePresenter.History(HistoryReport(
            baselineAvailable: true,
            changeCount: 3,
            previousAt: new DateTimeOffset(2026, 9, 16, 14, 32, 0, TimeSpan.Zero)));

        label.Should().Contain("16 Sep 2026 14:32");
        label.Should().Contain("3 changes");
    }

    [Fact]
    public void BaselineWithSingleChange_UsesSingular()
    {
        ContractStatePresenter.History(HistoryReport(
                baselineAvailable: true, changeCount: 1,
                previousAt: new DateTimeOffset(2026, 9, 16, 14, 32, 0, TimeSpan.Zero)))
            .Should().Contain("1 change since");
    }

    [Fact]
    public void BaselineWithNoChanges_SaysSo()
    {
        ContractStatePresenter.History(HistoryReport(
                baselineAvailable: true, changeCount: 0,
                previousAt: new DateTimeOffset(2026, 9, 16, 14, 32, 0, TimeSpan.Zero)))
            .Should().Contain("no changes since");
    }

    [Fact]
    public void PersistenceFailure_IsStatedNotImpliedSuccessful()
    {
        ContractStatePresenter.HistoryPersistenceNote(
                HistoryReport(baselineAvailable: false, persistence: SnapshotPersistenceState.Failed))
            .Should().Contain("could not be recorded");
    }

    [Fact]
    public void SuccessfulPersistence_AddsNoNote()
    {
        ContractStatePresenter.HistoryPersistenceNote(
                HistoryReport(baselineAvailable: true, persistence: SnapshotPersistenceState.Saved))
            .Should().BeNull();
    }

    [Fact]
    public void HistoryStateIsReadVerbatim_NotDerivedFromChangeList()
    {
        // BaselineAvailable is backend state: an empty change list must not be reported as
        // "no baseline", nor a populated one as implying a baseline.
        ContractStatePresenter.History(HistoryReport(baselineAvailable: true, changeCount: 0,
                previousAt: new DateTimeOffset(2026, 9, 16, 14, 32, 0, TimeSpan.Zero)))
            .Should().NotBe("No previous baseline");
    }

    [Fact]
    public void Export_ContainsHistorySection()
    {
        var html = new ReportExportService().ExportIntegrationQualityReview(
            HistoryReport(baselineAvailable: true, changeCount: 2,
                previousAt: new DateTimeOffset(2026, 9, 16, 14, 32, 0, TimeSpan.Zero)),
            "test");

        html.Should().Contain("History");
        html.Should().Contain("2026-09-16 14:32");
    }

    [Fact]
    public void Export_ContainsNoBaselineTruthfully()
    {
        var html = new ReportExportService().ExportIntegrationQualityReview(
            HistoryReport(baselineAvailable: false), "test");

        html.Should().Contain("No previous baseline");
    }

    [Fact]
    public void Export_ContainsHistoricalChangeDetail()
    {
        var html = new ReportExportService().ExportIntegrationQualityReview(
            HistoryReport(baselineAvailable: true, changeCount: 1,
                previousAt: new DateTimeOffset(2026, 9, 16, 14, 32, 0, TimeSpan.Zero),
                persistence: SnapshotPersistenceState.Saved,
                new IntegrationHistoricalChange
                {
                    Type = IntegrationHistoricalChangeType.ConsumerChanged,
                    IntegrationName = "Placement Events",
                    OldValue = "BillingService",
                    NewValue = "InvoicingService",
                    Description = "Consumer changed"
                }),
            "test");

        html.Should().Contain("Consumer changed");
        html.Should().Contain("BillingService");
        html.Should().Contain("InvoicingService");
    }

    [Fact]
    public void Export_StatesPersistenceFailure()
    {
        var html = new ReportExportService().ExportIntegrationQualityReview(
            HistoryReport(baselineAvailable: false, persistence: SnapshotPersistenceState.Failed), "test");

        html.Should().Contain("could not be recorded");
    }
}
