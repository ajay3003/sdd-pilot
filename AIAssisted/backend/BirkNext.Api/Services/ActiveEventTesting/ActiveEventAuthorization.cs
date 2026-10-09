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

/// <summary>Active event execution: an authenticated principal with ActiveEventExecute (scope, app role or permission claim). No bypass.</summary>
public sealed class ActiveEventPermissionHandler : AuthorizationHandler<ActiveEventPermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveEventPermissionRequirement requirement)
    {
        if (BirkNextPermissions.Holds(context.User, BirkNextPermissions.ActiveEventExecute)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public sealed class IntegrationConfigurationWriteRequirement : IAuthorizationRequirement { }

/// <summary>
/// Integration catalog writes (the configuration Active Event execution resolves its destination from). Granted to an authenticated principal
/// holding <c>IntegrationConfiguration.Write</c>. For the local, single-user tool without Entra configured, a backend setting
/// (<c>Authorization:LocalConfigurationWrites</c> = true) additionally allows writes in the Development environment from a loopback client
/// only — never from a header, and never in any other environment. ActiveEventExecute does not imply configuration rights.
/// </summary>
public sealed class IntegrationConfigurationWriteHandler(IHostEnvironment environment, IConfiguration configuration, IHttpContextAccessor http)
    : AuthorizationHandler<IntegrationConfigurationWriteRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, IntegrationConfigurationWriteRequirement requirement)
    {
        if (BirkNextPermissions.Holds(context.User, BirkNextPermissions.IntegrationConfigurationWrite) || LocalDevelopmentWrite())
            context.Succeed(requirement);
        return Task.CompletedTask;
    }

    private bool LocalDevelopmentWrite()
    {
        if (!environment.IsDevelopment() || !bool.TryParse(configuration["Authorization:LocalConfigurationWrites"], out var allowed) || !allowed) return false;
        var connection = http.HttpContext?.Connection;
        return connection?.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote) &&
            (connection.LocalIpAddress is null || IPAddress.IsLoopback(connection.LocalIpAddress));
    }
}
