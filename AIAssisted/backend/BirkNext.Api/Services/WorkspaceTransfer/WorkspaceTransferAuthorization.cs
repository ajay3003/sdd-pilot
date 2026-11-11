using System.Net;
using BirkNext.Api.Services.ActiveEventTesting;
using Microsoft.AspNetCore.Authorization;

namespace BirkNext.Api.Services.WorkspaceTransfer;

/// <summary>
/// Backup &amp; Transfer is its own capability (audit WP-G10). Full backup, isolated restore, package export and import, evidence export and target
/// rebinding all require it. ActiveEventExecute and IntegrationConfiguration.Write do not imply it.
/// <list type="bullet">
/// <item>Granted to an authenticated principal holding <c>WorkspaceTransfer</c> (scope, app role or permission claim).</item>
/// <item>For the local single-user tool: <c>Authorization:LocalWorkspaceTransfer = true</c>, in Development only, from a loopback peer only. This is the
/// same rule as local configuration writes; never a header.</item>
/// </list>
/// BirkNext has no per-project authorization. A package's scope is therefore decided by the server-side dependency closure of the selected project or
/// environment, never by a caller-supplied list of records.
/// </summary>
public static class WorkspaceTransferPermissions
{
    public const string WorkspaceTransfer = "WorkspaceTransfer";
    public const string Policy = "WorkspaceTransfer";
}

public sealed class WorkspaceTransferRequirement : IAuthorizationRequirement { }

public sealed class WorkspaceTransferHandler(IHostEnvironment environment, IConfiguration configuration, IHttpContextAccessor http)
    : AuthorizationHandler<WorkspaceTransferRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, WorkspaceTransferRequirement requirement)
    {
        if (BirkNextPermissions.Holds(context.User, WorkspaceTransferPermissions.WorkspaceTransfer) || LocalDevelopment()) context.Succeed(requirement);
        return Task.CompletedTask;
    }

    private bool LocalDevelopment()
    {
        if (!environment.IsDevelopment() || !bool.TryParse(configuration["Authorization:LocalWorkspaceTransfer"], out var allowed) || !allowed) return false;
        var connection = http.HttpContext?.Connection;
        return connection?.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote) &&
            (connection.LocalIpAddress is null || IPAddress.IsLoopback(connection.LocalIpAddress));
    }
}
