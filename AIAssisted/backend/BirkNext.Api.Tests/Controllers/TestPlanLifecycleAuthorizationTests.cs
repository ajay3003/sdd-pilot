using System.Reflection;
using BirkNext.Api.Controllers;
using BirkNext.Api.Services.ActiveEventTesting;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>Plan lifecycle (2026-11-23): archive, restore and delete change plans, so each needs the same write permission as saving a plan; none is a
/// DELETE verb an anonymous or cross-site form could reach by accident.</summary>
public sealed class TestPlanLifecycleAuthorizationTests
{
    [Theory]
    [InlineData(nameof(TestPlanSuggestionsController.Archive), "drafts/{id:guid}/archive")]
    [InlineData(nameof(TestPlanSuggestionsController.Unarchive), "drafts/{id:guid}/unarchive")]
    [InlineData(nameof(TestPlanSuggestionsController.Delete), "drafts/{id:guid}/delete")]
    public void Lifecycle_actions_are_posts_that_need_the_write_policy(string action, string route)
    {
        var method = typeof(TestPlanSuggestionsController).GetMethod(action, BindingFlags.Public | BindingFlags.Instance)!;
        method.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be(route);
        method.GetCustomAttribute<HttpDeleteAttribute>().Should().BeNull();
        method.GetCustomAttributes<AuthorizeAttribute>().Should().ContainSingle(a => a.Policy == BirkNextPermissions.IntegrationConfigurationWritePolicy);
        method.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
    }
}
