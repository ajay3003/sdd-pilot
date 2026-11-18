using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace BirkNext.Api.Services.ActiveEventTesting;

/// <summary>
/// Permissions carried by an Entra access token for the BirkNext API: a delegated scope (<c>scp</c>, space-separated) or an app role
/// (<c>roles</c>, one claim per role). Claim names are read as Entra emits them: inbound claim mapping is disabled on the JWT bearer handler,
/// so <c>scp</c> is not renamed to the long SOAP scope claim type.
/// </summary>
public static class BirkNextPermissions
{
    public const string ActiveEventExecute = "ActiveEventExecute";
    public const string IntegrationConfigurationWrite = "IntegrationConfiguration.Write";

    public const string ActiveEventExecutePolicy = "ActiveEventExecute";
    public const string IntegrationConfigurationWritePolicy = "IntegrationConfigurationWrite";

    public static bool Holds(ClaimsPrincipal user, string permission) =>
        user.Identity?.IsAuthenticated == true &&
        (user.FindAll("scp").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Contains(permission, StringComparer.Ordinal) ||
         user.FindAll("roles").Any(claim => string.Equals(claim.Value, permission, StringComparison.Ordinal)) ||
         user.FindAll("permission").Any(claim => string.Equals(claim.Value, permission, StringComparison.Ordinal)));
}

public sealed class ActiveEventPermissionRequirement : IAuthorizationRequirement { }

/// <summary>
/// ActiveEventExecute is a capability: "this caller may execute active/external test actions". It is held by an authenticated principal with
/// the permission (scope, app role or permission claim), or granted by the explicitly enabled, loopback-bounded local trusted execution of a
/// connected local tester under Local Tester Mode (<see cref="LocalTrustedExecution"/>). The handler enforces both paths itself (the policy has no separate
/// authenticated-user requirement); anything else — anonymous, a header, an environment name alone — is refused. The local grant adds no
/// claims and is never specific to one pack, project or route.
/// </summary>
public sealed class ActiveEventPermissionHandler(LocalTrustedExecution local, IHttpContextAccessor http) : AuthorizationHandler<ActiveEventPermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveEventPermissionRequirement requirement)
    {
        if (BirkNextPermissions.Holds(context.User, BirkNextPermissions.ActiveEventExecute) || local.Applies(http.HttpContext)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class IntegrationConfigurationWriteRequirement : IAuthorizationRequirement { }

/// <summary>
/// Integration catalog and test-plan writes (the configuration Active Event execution resolves its destination from, Test Plan drafts,
/// "Prepare test cases" and hand-written test cases). Granted to an authenticated principal holding <c>IntegrationConfiguration.Write</c>, or
/// to the connected local tester under Local Tester Mode (<see cref="LocalTrustedExecution"/>: loopback-only instance and request, localhost
/// Host, configured frontend Origin) — in any hosting environment; <c>Authorization:LocalConfigurationWrites</c> = false switches the local
/// grant off. Never from a header. ActiveEventExecute does not imply configuration rights.
/// </summary>
public sealed class IntegrationConfigurationWriteHandler(LocalTrustedExecution local, IHttpContextAccessor http)
    : AuthorizationHandler<IntegrationConfigurationWriteRequirement>
{
    public const string Setting = "Authorization:LocalConfigurationWrites";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, IntegrationConfigurationWriteRequirement requirement)
    {
        if (BirkNextPermissions.Holds(context.User, BirkNextPermissions.IntegrationConfigurationWrite)
            || local.EvaluateFor(Setting, "Local configuration writes", http.HttpContext).Allowed)
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
