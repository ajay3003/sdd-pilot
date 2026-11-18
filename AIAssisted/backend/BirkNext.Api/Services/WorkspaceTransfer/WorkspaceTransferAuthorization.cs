using System.Net;
using BirkNext.Api.Services.ActiveEventTesting;
using Microsoft.AspNetCore.Authorization;

namespace BirkNext.Api.Services.WorkspaceTransfer;

/// <summary>
/// Backup &amp; Transfer is its own capability (audit WP-G10). Full backup, isolated restore, package export and import, evidence export and target
/// rebinding all require it. ActiveEventExecute and IntegrationConfiguration.Write do not imply it.
/// <list type="bullet">
/// <item>Granted to an authenticated principal holding <c>WorkspaceTransfer</c> (scope, app role or permission claim).</item>
/// <item>Granted to the connected local tester under Local Tester Mode (<see cref="LocalTrustedExecution"/>: loopback-only instance and request,
/// localhost Host, configured frontend Origin), in any hosting environment; <c>Authorization:LocalWorkspaceTransfer</c> = false switches it off.
/// Never a header.</item>
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

public sealed class WorkspaceTransferHandler(IHttpContextAccessor http, LocalTrustedExecution local)
    : AuthorizationHandler<WorkspaceTransferRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, WorkspaceTransferRequirement requirement)
    {
        if (BirkNextPermissions.Holds(context.User, WorkspaceTransferPermissions.WorkspaceTransfer)
            || local.EvaluateFor("Authorization:LocalWorkspaceTransfer", "Local Backup & Transfer", http.HttpContext).Allowed)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
