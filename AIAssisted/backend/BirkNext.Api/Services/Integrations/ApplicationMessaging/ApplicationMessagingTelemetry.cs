using Azure;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations.ApplicationMessaging;

/// <summary>
/// Read-only runtime evidence of application messaging: log records written by the source-listed handler classes of one application
/// (Application Insights role from source). One bounded aggregate query — counts and a timestamp; no message, no payload, no row.
/// A source that cannot be read yields a typed state with its reason; counts are then null, never zero.
/// </summary>
public interface IApplicationMessagingTelemetrySource
{
    Task<ApplicationMessagingRuntime> GetAsync(IntegrationPlatform? platform, ApplicationMessagingEvidence application, int windowHours, CancellationToken ct);
}

public sealed class LogAnalyticsApplicationMessagingSource(IIntegrationAzureCredential azure, ILogger<LogAnalyticsApplicationMessagingSource> logger) : IApplicationMessagingTelemetrySource
{
    public const string Adapter = "Application messaging telemetry (Application Insights)";

    internal static string Literal(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Aggregate over AppTraces for the role, restricted to logger categories ending in the handler class names from source.</summary>
    internal static string HandlerLogQuery(string role, IEnumerable<string> handlerTypes) =>
        $"AppTraces | where AppRoleName == {Literal(role)} | extend category = tostring(Properties.CategoryName) " +
        $"| where {string.Join(" or ", handlerTypes.Select(t => $"category endswith {Literal("." + t)}"))} " +
        "| summarize info=countif(SeverityLevel <= 1), warn=countif(SeverityLevel == 2), error=countif(SeverityLevel >= 3), last=max(TimeGenerated)";

    public async Task<ApplicationMessagingRuntime> GetAsync(IntegrationPlatform? platform, ApplicationMessagingEvidence application, int windowHours, CancellationToken ct)
    {
        ApplicationMessagingRuntime Missing(IntegrationEvidenceState state, string reason) =>
            new() { ApplicationId = application.ApplicationId, State = state, Reason = reason, CapturedAt = DateTimeOffset.UtcNow, WindowHours = windowHours };
        if (application.Handlers.Count == 0) return Missing(IntegrationEvidenceState.NotSupported, "No Wolverine handler in this application's source, so there is no handler processing to observe.");
        if (application.TelemetryRoleName is not { Length: > 0 } role) return Missing(IntegrationEvidenceState.NotConfigured, "Source sets no OpenTelemetry service name, so the Application Insights role is unknown.");
        if (azure.Credential is null) return Missing(IntegrationEvidenceState.NotConfigured, azure.DisabledReason);
        if (!Guid.TryParse(platform?.RuntimeEvidence?.TelemetryWorkspaceId, out _))
            return Missing(IntegrationEvidenceState.NotConfigured, "No telemetry source configured (Log Analytics workspace id of Application Insights) on the bound integrations' platform.");
        var client = new LogsQueryClient(azure.Credential);
        try
        {
            var row = (await client.QueryWorkspaceAsync(platform!.RuntimeEvidence!.TelemetryWorkspaceId!, HandlerLogQuery(role, application.Handlers.Select(h => h.Type).Distinct()),
                new QueryTimeRange(TimeSpan.FromHours(windowHours)), new LogsQueryOptions { ServerTimeout = TimeSpan.FromSeconds(30) }, ct)).Value.Table.Rows.FirstOrDefault();
            var last = row?.GetDateTimeOffset("last");
            logger.LogInformation("Application messaging telemetry for {Application} (role {Role}): available, last handler log {Last}.", application.ApplicationId, role, last);
            return new ApplicationMessagingRuntime
            {
                ApplicationId = application.ApplicationId, State = IntegrationEvidenceState.Available, Reason = "Handler log records of the source-listed handler classes.",
                CapturedAt = DateTimeOffset.UtcNow, WindowHours = windowHours, LastHandlerLog = last,
                HandlerInformationLogs = row?.GetInt64("info") ?? 0, HandlerWarningLogs = row?.GetInt64("warn") ?? 0, HandlerErrorLogs = row?.GetInt64("error") ?? 0,
                Freshness = IntegrationReviewLabels.FreshnessOf(last, DateTimeOffset.UtcNow, windowHours),
            };
        }
        catch (Exception ex) when (ex is RequestFailedException or AuthenticationFailedException or CredentialUnavailableException)
        {
            var state = AzureEvidence.StateOf(ex);
            logger.LogInformation("Application messaging telemetry for {Application} unavailable: {Error}.", application.ApplicationId, AzureEvidence.Describe(ex));
            return Missing(state, state == IntegrationEvidenceState.NotAuthorized ? $"Telemetry access unauthorized ({AzureEvidence.Describe(ex)})." : $"Telemetry query failed ({AzureEvidence.Describe(ex)}).");
        }
    }
}
