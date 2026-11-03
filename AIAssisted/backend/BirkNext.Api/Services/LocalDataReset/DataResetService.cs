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
         "Quality review configuration and results", "Performance test definitions and history", "Critical E2E, Browser and Endpoint Discovery", "Authentication settings"];

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
                Removed = [.. LocalDataResetCoordinator.Cleared, "Protected Test Data: edited values of protected defaults, their approvals and bindings; canonical XML fixtures"],
                Preserved = [installation, "The highest synthetic CDC key and reserved identities (test keys are never reused)", "Data in external systems (Azure, Altinn, TT02, Service Bus, Event Hub, Git)"],
                Notes = ["Reset everything means all resettable local application data — not uninstalling BirkNext or dropping its database schema.",
                    "Product-defined Test Data defaults (e.g. skolenaervaer-child-default, skolenaervaer-reporter-default) exist again after the reset with their product values: they are recreated system defaults, not preserved data. Canonical fixtures such as skolenaervaer-valid-report are removed and must be created again."],
                Counts = counts, Allowed = refusal is null, NotAllowedReason = refusal,
            },
            DataResetMode.FullPreserveProtected => new DataResetPlan
            {
                Mode = mode, Title = "Reset everything except protected data",
                Summary = "Removes all resettable project and workspace data and review history. Protected Test Data defaults and canonical fixtures are kept with their current values and content.",
                Removed = [.. LocalDataResetCoordinator.Cleared],
                Preserved = ["Protected Test Data defaults and canonical XML fixtures — current values and content, same stable ids (nothing is restored to factory values)", installation,
                    "The highest synthetic CDC key and reserved identities", "Data in external systems"],
                Counts = counts, Allowed = refusal is null, NotAllowedReason = refusal,
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
            var result = await coordinator.ResetAsync(request.Mode == DataResetMode.FullIncludingProtected, ct);
            return new()
            {
                Mode = request.Mode,
                Status = result.Status switch { "Completed" => "Completed", "CompletedWithWarnings" => "PartiallyCompleted", var other => other },
                Message = result.Message, ResetAt = result.ResetAt, DeletedRows = result.DeletedRows, Domains = result.Domains, Warnings = result.Warnings,
                SystemDefaultsRecreated = result.TestData?.SystemDefaultsRecreated ?? [],
            };
        }
        finally { Gate.Release(); }
    }
}
