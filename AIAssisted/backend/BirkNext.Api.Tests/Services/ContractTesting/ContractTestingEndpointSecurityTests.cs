using System.Reflection;
using BirkNext.Api.Controllers;
using BirkNext.Api.Services.ActiveEventTesting;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractTesting;

/// <summary>
/// The Contract Testing API surface: destructive actions and credential writes need the configuration-write permission, uploads are size-limited,
/// and no endpoint takes an arbitrary URL or request to send (runs go only through stored tests and the Execution Evidence safety policy).
/// </summary>
public sealed class ContractTestingEndpointSecurityTests
{
    private static readonly MethodInfo[] Actions = typeof(ContractTestingController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any()).ToArray();

    private static string Describe(MethodInfo m)
    {
        var http = m.GetCustomAttribute<HttpMethodAttribute>()!;
        return $"{http.HttpMethods.First()} {http.Template}";
    }

    private static bool Protected(MethodInfo m) =>
        m.GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Policy == BirkNextPermissions.IntegrationConfigurationWritePolicy);

    [Theory]
    [InlineData("DELETE contracts/{key}")]
    [InlineData("DELETE contracts/{key}/versions/{version:int}")]
    [InlineData("PUT contracts/{key}/lifecycle")]
    [InlineData("PUT contracts/{key}/versions/{version:int}/hidden")]
    [InlineData("PUT credentials")]
    [InlineData("DELETE credentials/{environmentId}")]
    public void Destructive_actions_and_credential_writes_require_the_configuration_write_permission(string endpoint) =>
        Protected(Actions.Single(a => Describe(a) == endpoint)).Should().BeTrue(endpoint);

    [Fact]
    public void Reading_never_requires_write_permission_and_no_action_is_anonymous()
    {
        Actions.Where(a => a.GetCustomAttribute<HttpGetAttribute>() is not null).Should().NotContain(a => Protected(a));
        Actions.Should().NotContain(a => a.GetCustomAttributes<AllowAnonymousAttribute>().Any());
        typeof(ContractTestingController).GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty();
    }

    [Fact]
    public void Uploads_are_size_limited_and_no_endpoint_accepts_a_raw_request_to_send()
    {
        Actions.Single(a => Describe(a) == "POST contracts").GetCustomAttribute<RequestSizeLimitAttribute>().Should().NotBeNull();
        Actions.Single(a => Describe(a) == "PUT credentials").GetCustomAttribute<RequestSizeLimitAttribute>().Should().NotBeNull();
        // The only executing endpoint takes stored test ids, never a URL, method or headers to send.
        var run = Actions.Single(a => Describe(a) == "POST runs").GetParameters().Single(p => p.GetCustomAttribute<FromBodyAttribute>() is not null).ParameterType;
        run.GetProperties().Select(p => p.Name).Should().NotContain(["Url", "Method", "Headers", "Body", "TargetUrl"]);
    }

    [Fact]
    public void Starting_or_cancelling_a_run_needs_the_execute_permission()
    {
        static bool Executes(MethodInfo m) => m.GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Policy == BirkNextPermissions.ActiveEventExecutePolicy);
        Executes(Actions.Single(a => Describe(a) == "POST runs")).Should().BeTrue();
        Executes(Actions.Single(a => Describe(a) == "POST chains/{id:guid}/runs")).Should().BeTrue();
        Protected(Actions.Single(a => Describe(a) == "DELETE chains/{id:guid}")).Should().BeTrue();
        var evidence = typeof(ExecutionEvidenceController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null).ToList();
        evidence.Should().NotBeEmpty();
        evidence.Where(m => !Executes(m)).Should().BeEmpty("every POST of Execution Evidence starts or stops sending requests");
    }
}
