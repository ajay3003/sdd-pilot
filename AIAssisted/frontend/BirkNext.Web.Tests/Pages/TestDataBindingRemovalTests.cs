using BirkNext.TestCoverage;
using BirkNext.TestData;
using BirkNext.Web.Pages;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Test Data → Requests: each requirement shows its bindings (asset, bound revision, state) and a binding or the requirement can be removed
/// explicitly. Removing a binding leaves the asset and the requirement (the consumer's need) in place; nothing else changes.
/// </summary>
public sealed class TestDataBindingRemovalTests : BunitContext
{
    private sealed class Api : ITestDataApiService
    {
        public List<ConsumerTestDataRequirement> Requirements { get; } = [];
        public List<TestDataUsageReference> Usages { get; } = [];
        public List<string> Unbound { get; } = [];
        public List<string> RemovedRequirements { get; } = [];

        public Task<(TestDataCatalogView? Catalog, string? Error)> CatalogAsync(CancellationToken ct = default) =>
            Task.FromResult<(TestDataCatalogView?, string?)>((new([], [], []), null));
        public Task<(TestDataAssetDetail? Detail, string? Error)> AssetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<(TestDataAssetDetail?, string?)>((null, "none"));
        public Task<(TestDataConsumerView? View, string? Error)> ConsumersAsync(CancellationToken ct = default) =>
            Task.FromResult<(TestDataConsumerView?, string?)>((new([.. Requirements], [.. Usages], new(0, 0, 0, 0, Requirements.Count, 0, Requirements.Count, 0, 0)), null));
        public Task<string?> UnbindAsync(string usageId, CancellationToken ct = default)
        {
            Unbound.Add(usageId);
            Usages.RemoveAll(u => u.Id == usageId);
            foreach (var r in Requirements.ToList()) Requirements[Requirements.IndexOf(r)] = r with { Status = TestDataRequirementStatus.Missing, BoundAssetId = null };
            return Task.FromResult<string?>(null);
        }
        public Task<string?> RemoveRequirementAsync(string requirementId, CancellationToken ct = default)
        {
            RemovedRequirements.Add(requirementId);
            Requirements.RemoveAll(r => r.Id == requirementId);
            return Task.FromResult<string?>(null);
        }
    }

    private readonly Api _api = new();
    private static readonly TestDataConsumer Consumer = new()
    {
        ConsumerType = CoverageTestDataConsumers.ConsumerType, ConsumerId = CoverageTestDataConsumers.ConsumerId("import:acc", "t-place"), DisplayName = "PlaceOrder_NotifiesCustomer",
    };

    public TestDataBindingRemovalTests()
    {
        Services.AddSingleton<ITestDataApiService>(_api);
        _api.Requirements.Add(new() { Id = "req-1", Consumer = Consumer, Usage = "Order customer", RequestedDataType = TestDataDataType.JsonPayload, Status = TestDataRequirementStatus.Resolved, BoundAssetId = "authored:order-customer" });
        _api.Usages.Add(new() { Id = "use-1", AssetId = "authored:order-customer", AssetFingerprint = "abcdef0123456789abcd", Consumer = Consumer, Usage = "Order customer", RequirementId = "req-1", Status = TestDataUsageStatus.Resolved });
    }

    private IRenderedComponent<TestDataPage> Page()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("test-data?section=requests");
        var page = Render<TestDataPage>();
        page.WaitForAssertion(() => page.Find("[data-testid=td-page]").GetAttribute("aria-busy").Should().Be("false"));
        return page;
    }

    [Fact]
    public void A_binding_shows_its_revision_and_state_and_removing_it_keeps_the_requirement_open()
    {
        var page = Page();
        var usage = page.Find("[data-testid=td-requirement-usage]");
        (usage.GetAttribute("data-asset"), usage.GetAttribute("data-status")).Should().Be(("authored:order-customer", "Resolved"));
        usage.TextContent.Should().Contain("revision abcdef012345…");

        page.Find("[data-testid=td-unbind]").Click();

        _api.Unbound.Should().Equal("use-1");
        page.WaitForAssertion(() => page.FindAll("[data-testid=td-requirement-usage]").Should().BeEmpty());
        page.Find("[data-testid=td-requirement-status]").TextContent.Should().Be("Missing", "the consumer's need stays registered");
        page.Find("[data-testid=td-action-status]").TextContent.Should().Contain("requirement stays open");
    }

    [Fact]
    public void Removing_a_requirement_is_explicit()
    {
        var page = Page();
        page.Find("[data-testid=td-requirement-remove]").Click();
        _api.RemovedRequirements.Should().Equal("req-1");
        page.WaitForAssertion(() => page.FindAll("[data-testid=td-requirement]").Should().BeEmpty());
    }
}
