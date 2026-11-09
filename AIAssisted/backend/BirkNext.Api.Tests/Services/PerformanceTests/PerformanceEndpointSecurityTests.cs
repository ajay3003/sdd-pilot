using System.Reflection;
using BirkNext.Api.Controllers;
using BirkNext.Api.Services.ActiveEventTesting;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace BirkNext.Api.Tests.Services.PerformanceTests;

/// <summary>
/// Performance Test Review API surface: starting or cancelling load needs the execute permission; baseline proposals, approvals, rejections,
/// archiving and run deletion need the configuration-write permission; comparisons, regressions, trends and exports are read-only.
/// </summary>
public sealed class PerformanceEndpointSecurityTests
{
    private static readonly MethodInfo[] Actions = typeof(PerformanceTestsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any()).ToArray();

    private static string Describe(MethodInfo m)
    {
        var http = m.GetCustomAttribute<HttpMethodAttribute>()!;
        return $"{http.HttpMethods.First()} {http.Template}";
    }

    private static bool Has(MethodInfo m, string policy) => m.GetCustomAttributes<AuthorizeAttribute>().Any(a => a.Policy == policy);

    [Theory]
    [InlineData("POST runs")]
    [InlineData("POST runs/{runId:guid}/cancel")]
    public void Starting_or_cancelling_load_needs_the_execute_permission(string endpoint) =>
        Has(Actions.Single(a => Describe(a) == endpoint), BirkNextPermissions.ActiveEventExecutePolicy).Should().BeTrue(endpoint);

    [Theory]
    [InlineData("POST baselines")]
    [InlineData("POST baselines/proposals")]
    [InlineData("POST baselines/{baselineId}/approve")]
    [InlineData("POST baselines/{baselineId}/reject")]
    [InlineData("POST baselines/{baselineId}/archive")]
    [InlineData("POST runs/{runId:guid}/delete")]
    public void Baseline_decisions_and_run_deletion_need_the_configuration_write_permission(string endpoint) =>
        Has(Actions.Single(a => Describe(a) == endpoint), BirkNextPermissions.IntegrationConfigurationWritePolicy).Should().BeTrue(endpoint);

    [Theory]
    [InlineData("GET regression")]
    [InlineData("GET regression/export")]
    [InlineData("GET trends")]
    [InlineData("GET compare")]
    public void Comparisons_and_exports_are_reads(string endpoint)
    {
        var action = Actions.Single(a => Describe(a) == endpoint);
        action.GetCustomAttributes<AuthorizeAttribute>().Should().BeEmpty(endpoint);
        action.GetCustomAttributes<AllowAnonymousAttribute>().Should().BeEmpty(endpoint);
    }
}
