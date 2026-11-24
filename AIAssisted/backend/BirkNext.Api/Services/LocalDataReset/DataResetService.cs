using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.Api.Services.TestData;
using BirkNext.Maintenance;
using BirkNext.TestData;

namespace BirkNext.Api.Services.LocalDataReset;

/// <summary>
/// The one Maintenance reset entry point: a chosen scope (<see cref="DataResetMode"/>), a reviewable plan, and execution that delegates to the
/// domains that own the data — the application database reset + backend state (<see cref="LocalDataResetCoordinator"/>), Delivery Management
/// (<see cref="DeliveryReportingDataReset"/>) and Test Data (protection overridden only by <see cref="DataResetMode.FullIncludingProtected"/>).
/// Backend-enforced: the Local-database / non-production guard, the typed confirmation for the full scope, one reset at a time.
/// </summary>
public sealed class DataResetService(ILocalDatabaseReset guard, LocalDataResetCoordinator coordinator, DeliveryReportingDataReset delivery,
    ILogger<DataResetService> logger, TestDataLifecycleService? testData = null)
{
    public const string FullConfirmation = "RESET EVERYTHING";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly string[] DeliveryRemoved =
        ["Delivery Management reporting projects", "Delivery snapshots (Delivery Reporting's own copies) and their comparisons", "Generated Delivery Reports and report history"];
    private static readonly string[] DeliveryNotTouched =
        ["Project imports and workspaces", "Source Analysis", "Project Evidence and Target Environments", "Test Data",
         "Quality review configuration and results", "Performance test definitions and history", "Critical E2E, Browser and Endpoint Discovery", "Authentication settings",
         "Manual test plans and manual test runs",
         .. ResettableSchemas.All.Select(s => s.Domain)];

    public async Task<DataResetPlan> PlanAsync(DataResetMode mode, CancellationToken ct)
    {
        var refusal = guard.ResetRefusal();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var (projects, snapshots, reports) = await delivery.CountsAsync(ct);
            (counts["Delivery projects"], counts["Delivery snapshots"], counts["Saved Delivery Reports"]) = (projects, snapshots, reports);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Reset plan: Delivery Management counts unavailable"); }
        var testPlans = 0;
        try { testPlans = await coordinator.CountTestPlansAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Reset plan: manual test plan count unavailable"); }
        if (mode != DataResetMode.DeliveryManagement) counts["Manual test plans kept"] = testPlans;
        if (mode != DataResetMode.DeliveryManagement && testData is not null)
        {
            var preview = await testData.PreviewResetAsync(ct);
            counts["Test Data assets removed"] = preview.AssetsToRemove.Count + (mode == DataResetMode.FullIncludingProtected ? preview.ProtectedFixtures.Count : 0);
            counts[mode == DataResetMode.FullIncludingProtected ? "Protected Test Data removed or reset" : "Protected Test Data preserved"] = preview.ProtectedDefaults.Count + preview.ProtectedFixtures.Count;
        }
        var installation = "Installation and provider settings, feature visibility, logging, the database schema and migration history, the Sample Project catalog and files outside BirkNext";
        return mode switch
        {
            DataResetMode.FullIncludingProtected => new DataResetPlan
            {
                Mode = mode, Title = "Reset everything", ConfirmationPhrase = FullConfirmation,
                Summary = "Permanently removes all resettable local BirkNext data, including protected Test Data and canonical fixtures. This cannot be undone.",
                Removed = [.. LocalDataResetCoordinator.Cleared, .. LocalDataResetCoordinator.ClearedOnlyWhenResettingEverything,
                    "Protected Test Data: the built-in defaults (e.g. skolenaervaer-reporter-default, skolenaervaer-child-default) with their values, approvals and bindings; canonical XML fixtures"],
                Preserved = [LocalDataResetCoordinator.TestPlansKept, installation, "The highest synthetic CDC key and reserved identities (test keys are never reused)", "Data in external systems (Azure, Altinn, TT02, Service Bus, Event Hub, Git)"],
                TestPlanCount = testPlans, CanRemoveTestPlans = true,
                Notes = ["Reset everything means all resettable local application data — not uninstalling BirkNext or dropping its database schema.",
                    "Manual test plans are kept unless you also tick «Also remove manual test plans». Then a backup of every plan, revision, snapshot and manual run is written to the backend's App_Data/reset-backups first; the reset is refused when it cannot be written.",
                    "The built-in Test Data defaults stay removed, also after a restart. Bring them back with their product values in Test Data → Restore built-in defaults. Canonical fixtures such as skolenaervaer-valid-report are removed and must be created again."],
                Counts = counts, Allowed = refusal is null, NotAllowedReason = refusal,
            },
            DataResetMode.ProjectDataKeepSettings => new DataResetPlan
            {
                Mode = mode, Title = "Reset project data, keep settings",
                Summary = "Removes all project and workspace data and review history, like Reset everything except protected data, but keeps system settings: Target Environments, integrations, Altinn settings, Git Sources and other configuration.",
                Removed = [.. LocalDataResetCoordinator.Cleared.Where(c => !c.StartsWith("Target-environment integration catalogs", StringComparison.Ordinal)),
                    "Target-environment results: contracts, GraphQL schemas, message flows and applied-template evidence (the integrations themselves are kept)",
                    "Each Target Environment's link to the deleted project (Project evidence and its archived links)"],
                Preserved = [.. LocalDataResetCoordinator.SettingsKept,
                    "Protected Test Data defaults and canonical XML fixtures — current values and content, same stable ids",
                    LocalDataResetCoordinator.SharedTestAreasKept, LocalDataResetCoordinator.TestPlansKept, installation,
                    "The highest synthetic CDC key and reserved identities", "Data in external systems"],
                Notes = ["Performance test definitions, Critical E2E flows and Integration Journeys are project content and are removed."],
                Counts = counts, Allowed = refusal is null, NotAllowedReason = refusal, TestPlanCount = testPlans,
            },
            DataResetMode.FullPreserveProtected => new DataResetPlan
            {
                Mode = mode, Title = "Reset everything except protected data",
                Summary = "Removes all resettable project and workspace data and review history. Protected Test Data defaults and canonical fixtures are kept with their current values and content.",
                Removed = [.. LocalDataResetCoordinator.Cleared],
                Preserved = ["Protected Test Data defaults and canonical XML fixtures — current values and content, same stable ids (nothing is restored to factory values)",
                    LocalDataResetCoordinator.SharedTestAreasKept, LocalDataResetCoordinator.TestPlansKept, installation,
                    "The highest synthetic CDC key and reserved identities", "Data in external systems"],
                Counts = counts, Allowed = refusal is null, NotAllowedReason = refusal, TestPlanCount = testPlans,
            },
            _ => new DataResetPlan
            {
                Mode = mode, Title = "Reset Delivery Management only",
                Summary = "Removes Delivery Overview and Delivery Reports data only. Saved Delivery Report history is permanently removed.",
                Removed = [.. DeliveryRemoved], Preserved = [.. DeliveryNotTouched, installation],
                Notes = ["Delivery snapshots are Delivery Reporting's own parsed copies; the archives and project imports they came from are not touched."],
                Counts = counts, Allowed = refusal is null, NotAllowedReason = refusal,
            },
        };
    }

    public async Task<DataResetResult> ResetAsync(DataResetRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Mode)) return new() { Mode = request.Mode, Status = "Refused", Message = "Unknown reset scope." };
        if (request.Mode == DataResetMode.FullIncludingProtected && !string.Equals(request.Confirmation, FullConfirmation, StringComparison.Ordinal))
            return new() { Mode = request.Mode, Status = "Refused", Message = $"Type {FullConfirmation} to confirm resetting everything, including protected data." };
        if (request.Mode != DataResetMode.FullIncludingProtected && !string.Equals(request.Confirmation, "CONFIRM", StringComparison.Ordinal))
            return new() { Mode = request.Mode, Status = "Refused", Message = "The reset was not confirmed." };
        if (request.RemoveTestPlans && request.Mode != DataResetMode.FullIncludingProtected)
            return new() { Mode = request.Mode, Status = "Refused", Message = "Manual test plans can only be removed by Reset everything. Nothing was deleted." };
        if (guard.ResetRefusal() is { } refusal) return new() { Mode = request.Mode, Status = "Refused", Message = refusal };
        if (!await Gate.WaitAsync(0, ct)) return new() { Mode = request.Mode, Status = "Blocked", Message = "A reset is already in progress." };
        try
        {
            if (request.Mode == DataResetMode.DeliveryManagement)
            {
                try
                {
                    var deleted = await delivery.ResetAsync(ct);
                    return new()
                    {
                        Mode = request.Mode, Status = "Completed", ResetAt = DateTimeOffset.UtcNow, DeletedRows = deleted, Domains = [new("Delivery Management", "Removed", $"{deleted} records")],
                        Message = $"Delivery Management reset completed: {deleted} records removed. Project imports, Source Analysis, Test Data and all other domains were not touched.",
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Delivery Management reset failed; the transaction was rolled back");
                    return new() { Mode = request.Mode, Status = "Failed", Domains = [new("Delivery Management", "Failed", "The change was rolled back; nothing was removed.")],
                        Message = "Reset failed. Delivery Management data was not changed. See server logs for details." };
                }
            }
            if (request.RemoveTestPlans)
            {
                // A dialog opened before plans were added (or removed) must never delete plans it did not show: the reviewed count must still hold.
                var now = await coordinator.CountTestPlansAsync(ct);
                if (request.ExpectedTestPlans != now)
                    return new() { Mode = request.Mode, Status = "Refused", TestPlansPreserved = now,
                        Message = $"Reset refused: there are now {now} manual test plan(s), not the {request.ExpectedTestPlans?.ToString() ?? "unknown number"} you reviewed. Review the reset again. Nothing was deleted." };
            }
            var result = await coordinator.ResetAsync(request.Mode == DataResetMode.FullIncludingProtected, keepSettings: request.Mode == DataResetMode.ProjectDataKeepSettings,
                removeTestPlans: request.RemoveTestPlans, ct);
            return new()
            {
                Mode = request.Mode,
                Status = result.Status switch { "Completed" => "Completed", "CompletedWithWarnings" => "PartiallyCompleted", var other => other },
                Message = result.Message, ResetAt = result.ResetAt, ResetEpoch = result.ResetEpoch, DeletedRows = result.DeletedRows, Domains = result.Domains, Warnings = result.Warnings,
                ProtectedDefaultsRemoved = result.TestData?.ProtectedDefaultsRemoved ?? [],
                TestPlansPreserved = result.TestPlansPreserved, TestPlansRemoved = result.TestPlansRemoved, TestPlanBackup = result.TestPlanBackup,
            };
        }
        finally { Gate.Release(); }
    }
}
