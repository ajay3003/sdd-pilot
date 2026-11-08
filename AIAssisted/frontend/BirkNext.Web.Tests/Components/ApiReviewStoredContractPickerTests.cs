using BirkNext.ApiReview;
using BirkNext.ContractTesting;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>API Quality Review uses stored contract versions: per selected service, only contracts of its protocol, every version, nothing chosen automatically.</summary>
public sealed class ApiReviewStoredContractPickerTests : BunitContext
{
    [Fact]
    public void Each_service_offers_its_protocols_stored_versions_and_a_choice_sets_the_contract_reference()
    {
        var api = new Mock<IContractTestingApiService>();
        api.Setup(a => a.ContractsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(((IReadOnlyList<ApiContractSummary>?)[
            new(Guid.NewGuid(), "children-api", "Children API", ApiContractProtocol.Rest, 2, 3, 0, DateTimeOffset.UtcNow),
            new(Guid.NewGuid(), "children-graphql", "Children GraphQL", ApiContractProtocol.GraphQl, 1, 2, 0, DateTimeOffset.UtcNow)], (string?)null));
        Services.AddSingleton(api.Object);
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        var targets = new List<ApiReviewTarget>
        {
            new() { TargetId = "rest-1", ApiType = ApiReviewTargetType.Rest, ServiceName = "Children REST", ContractSource = "https://api.example.test/swagger.json" },
            new() { TargetId = "gql-1", ApiType = ApiReviewTargetType.GraphQl, ServiceName = "Children GraphQL" },
        };
        var cut = Render<ApiReviewStoredContractPicker>(p => p.Add(x => x.Targets, targets).Add(x => x.ContractRefs, refs));
        var selects = cut.FindAll("[data-testid=aqr-stored-select]");
        selects[0].QuerySelectorAll("option").Select(o => o.GetAttribute("value")).Should().Equal("", "children-api@v2", "children-api@v1");
        selects[0].QuerySelector("option")!.TextContent.Should().Be("Published OpenAPI URL", "the default keeps the configured URL; nothing is chosen automatically");
        selects[1].QuerySelectorAll("option").Select(o => o.GetAttribute("value")).Should().Equal("", "children-graphql@v1");
        refs.Should().BeEmpty();
        selects[0].Change("children-api@v1");
        refs.Should().Equal(new Dictionary<string, string> { ["rest-1"] = "children-api@v1" });
        cut.FindAll("[data-testid=aqr-stored-select]")[0].Change("");
        refs.Should().BeEmpty();
    }
}
