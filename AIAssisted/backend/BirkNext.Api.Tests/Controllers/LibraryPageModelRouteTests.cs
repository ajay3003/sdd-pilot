using System.Reflection;
using BirkNext.Api.Controllers;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace BirkNext.Api.Tests.Controllers;

/// <summary>The Library page models are served where the Web client asks for them (api/library-page-model/…), not at the class-name route.</summary>
public sealed class LibraryPageModelRouteTests
{
    [Fact]
    public void The_controller_route_matches_the_web_clients_route()
    {
        typeof(LibraryPageModelController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/library-page-model");
        typeof(LibraryPageModelController).GetMethods().SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>()).Select(a => a.Template)
            .Should().BeEquivalentTo(["qa-artifact-library", "sample-projects"], "the two paths LibraryPageModelService calls");
    }
}
