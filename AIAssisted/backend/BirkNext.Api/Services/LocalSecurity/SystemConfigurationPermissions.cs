using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Services.ActiveEventTesting;
using Microsoft.AspNetCore.Authorization;

namespace BirkNext.Api.Services.LocalSecurity;

/// <summary>
/// Changing installation configuration from System Settings (Feature Visibility, logging, diagnostics, Frontend Testing Capabilities, Microsoft
/// sign-in) is its own capability, <c>SystemConfiguration.Write</c>. Reading settings and opening pages is unaffected: feature visibility is not
/// authorization, and a hidden page stays reachable by its address. Granted to:
/// <list type="bullet">
/// <item>an authenticated principal holding <c>SystemConfiguration.Write</c> (scope, app role or permission claim; Entra);</item>
/// <item>the local single-user installation when the backend setting <c>Authorization:LocalSystemConfiguration</c> is true, with exactly the
/// loopback / Host / Origin / environment conditions of <see cref="LocalTrustedExecution"/> (Development, or the TesterNative Tester Package).
/// It is a capability grant, never an identity: the actor recorded for such a change is <c>local-session:loopback</c>.</item>
/// </list>
/// Integration-configuration, active-execution and Backup &amp; Transfer rights do not imply it, and it implies none of them.
/// </summary>
public static class SystemConfigurationPermissions
{
    public const string Permission = "SystemConfiguration.Write";
    public const string Policy = "SystemConfigurationWrite";
    public const string LocalSetting = "Authorization:LocalSystemConfiguration";
    public const string LocalCapability = "Local system configuration";

    /// <summary>A safe actor for the audit record: a hash of the token's issuer and subject (never a name, e-mail or IP), or
    /// <c>local-session:loopback</c> for the local grant. Null when neither applies (no change is then permitted anyway).</summary>
    public static string? Actor(ClaimsPrincipal user, bool localGranted)
    {
        if (BirkNextPermissions.Holds(user, Permission)
            && (user.FindFirst("oid")?.Value ?? user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value) is { Length: > 0 } subject)
        {
            var issuer = user.FindFirst("iss")?.Value ?? "";
            return ExecutionActor.AuthenticatedPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}|{subject}")))[..16].ToLowerInvariant();
        }
        return localGranted ? ExecutionActor.LocalSession : null;
    }

    /// <summary>Whether this request may change configuration, and why (shown in System Settings; the backend enforces it either way).</summary>
    public static (bool Allowed, string Reason) Evaluate(ClaimsPrincipal user, LocalTrustedExecution local, HttpContext? context)
    {
        if (BirkNextPermissions.Holds(user, Permission)) return (true, "Signed in with the SystemConfiguration.Write permission.");
        var decision = local.EvaluateFor(LocalSetting, LocalCapability, context);
        if (decision.Allowed) return (true, decision.Reason);
        return (false, user.Identity?.IsAuthenticated == true && (user.FindFirst("scp") is not null || user.FindFirst("roles") is not null)
            ? "Signed in, but without the SystemConfiguration.Write permission."
            : $"Changing configuration is not permitted for this session. {decision.Reason}");
    }
}

public sealed class SystemConfigurationWriteRequirement : IAuthorizationRequirement { }

public sealed class SystemConfigurationWriteHandler(LocalTrustedExecution local, IHttpContextAccessor http) : AuthorizationHandler<SystemConfigurationWriteRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SystemConfigurationWriteRequirement requirement)
    {
        if (SystemConfigurationPermissions.Evaluate(context.User, local, http.HttpContext).Allowed) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
