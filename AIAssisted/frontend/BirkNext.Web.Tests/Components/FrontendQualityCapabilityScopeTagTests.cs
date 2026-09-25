using BirkNext.Web.Components;
using BirkNext.Web.Models;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>Each active engine is tagged with the access path it covers; one that cannot run never claims coverage.</summary>
public sealed class FrontendQualityCapabilityScopeTagTests : BunitContext
{
    [Fact]
    public void EnginesAreTaggedWithTheirPath_AndUnavailableOnesSaySo()
    {
        FrontendQualityCapabilityRow Row(FrontendQualityEngineId id, FrontendQualityCapabilityState state) =>
            new(id, id.ToString(), FrontendQualityEngineRequirement.Required, state, null, null);
        var rows = new[]
        {
            Row(FrontendQualityEngineId.StaticSecurity, FrontendQualityCapabilityState.Enabled),
            Row(FrontendQualityEngineId.Accessibility, FrontendQualityCapabilityState.RequiresBrowserSession),
            Row(FrontendQualityEngineId.BrowserQuality, FrontendQualityCapabilityState.Ready),
        };
        var scope = new FrontendQualityReviewScope(FrontendReviewAccessScope.PublicAndAuthenticated,
            FrontendQualityAccessPathState.Available, FrontendQualityAccessPathState.Unavailable, "", false,
            FrontendReviewAccessScope.PublicOnly,
            [
                new(FrontendQualityEngineId.StaticSecurity, "Static Security", FrontendQualityEngineAccessPath.Public, true),
                new(FrontendQualityEngineId.Accessibility, "Accessibility", FrontendQualityEngineAccessPath.Authenticated, false),
                new(FrontendQualityEngineId.BrowserQuality, "Browser Quality", FrontendQualityEngineAccessPath.CompanionEvidence, true),
            ]);

        var cut = Render<FrontendQualityCapabilityList>(p => p.Add(c => c.Rows, rows).Add(c => c.Scope, scope));

        string Tag(FrontendQualityEngineId id) => cut.Find($"[data-engine-id={id}] [data-testid=fqr-capability-scope]").TextContent;
        Tag(FrontendQualityEngineId.StaticSecurity).Should().Be("Public frontend");
        Tag(FrontendQualityEngineId.Accessibility).Should().Be("Signed-in pages · not available");
        Tag(FrontendQualityEngineId.BrowserQuality).Should().Be("Companion evidence");
    }

    [Fact]
    public void WithoutAScopeNoTagIsInvented()
    {
        var cut = Render<FrontendQualityCapabilityList>(p => p.Add(c => c.Rows,
            [new FrontendQualityCapabilityRow(FrontendQualityEngineId.StaticSecurity, "Static Security", FrontendQualityEngineRequirement.Required, FrontendQualityCapabilityState.Enabled, null, null)]));
        cut.FindAll("[data-testid=fqr-capability-scope]").Should().BeEmpty();
    }
}
