using Microsoft.AspNetCore.Authorization;

namespace BirkNext.Api.Services.ActiveEventTesting;

public sealed class ActiveEventPermissionRequirement : IAuthorizationRequirement { }

/// <summary>Accepts only a permission claim or an Entra delegated scope emitted by the configured token issuer.</summary>
public sealed class ActiveEventPermissionHandler : AuthorizationHandler<ActiveEventPermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveEventPermissionRequirement requirement)
    {
        var granted = context.User.Claims.Any(claim =>
            claim.Type == "permission" && string.Equals(claim.Value, "ActiveEventExecute", StringComparison.Ordinal)) ||
            context.User.FindAll("scp").SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Any(scope => string.Equals(scope, "ActiveEventExecute", StringComparison.Ordinal));
        if (granted) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
