using BirkNext.ProjectImport;
using BirkNext.Web.Services;
using BirkNext.Web.Services.Explorers;
using BirkNext.Web.Services.ProjectImport;
using BirkNext.Web.Tests.Pages;
using FluentAssertions;
using static BirkNext.Web.Tests.Services.ProjectImportActivationTests;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// A shared base data model plus a pack's own data model: two authored artifacts, and a derived, read-only aggregate where a pack structure
/// with the same name overrides the base one (marked, never merged field by field). The base is the module's (else project's) shared data
/// model — never a sibling pack's.
/// </summary>
public sealed class DataModelLayersTests
{
    private const string BaseModel = """
        # Data Model: Shop base

        ## Entities

        ### Customer

        | Field | Type | Nullable |
        |---|---|---|
        | Id | Guid | No |
        | Name | string | No |

        ### Order

        | Field | Type | Nullable |
        |---|---|---|
        | Id | Guid | No |
        """;

    private const string PackModel = """
        # Data Model: Refunds

        ## Entities

        ### Refund

        | Field | Type | Nullable |
        |---|---|---|
        | Id | Guid | No |
        | OrderId | Guid | No |

        ### Order

        | Field | Type | Nullable |
        |---|---|---|
        | Id | Guid | No |
        | RefundedAt | DateTimeOffset | Yes |
        """;

    private const string SiblingModel = """
        # Data Model: Invoices

        ## Entities

        ### Invoice

        | Field | Type | Nullable |
        |---|---|---|
        | Id | Guid | No |
        """;

    [Fact]
    public void The_aggregate_keeps_base_structures_adds_pack_ones_and_marks_overrides()
    {
        var parser = new DataModelAnalysisService();
        var (aggregate, origins) = DataModelLayers.Aggregate(parser.Parse(BaseModel), parser.Parse(PackModel));

        aggregate.Entities.Select(e => e.Name).Should().BeEquivalentTo(["Customer", "Refund", "Order"]);
        origins["Customer"].Should().Be(DataModelLayerOrigin.SharedBase);
        origins["Refund"].Should().Be(DataModelLayerOrigin.Pack);
        origins["Order"].Should().Be(DataModelLayerOrigin.OverriddenInPack);
        aggregate.Entities.Single(e => e.Name == "Order").Columns.Select(c => c.Name).Should().Equal(["Id", "RefundedAt"], "the pack's definition wins; fields are never merged");
        DataModelLayers.Label(DataModelLayerOrigin.OverriddenInPack).Should().Be("Feature + shared model");
    }

    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly MockSampleProjectDocumentResolver _samples = new();

    private ArtifactExplorerContext Import(params (string Path, string Content)[] documents)
    {
        _samples.Repository = _repository;
        var explorers = new ArtifactExplorerContext(_repository, _samples, _samples);
        var preview = Preview(new string('d', 64), source: false, documents);
        new ProjectImportActivation(_repository).Activate(preview, ProjectImportArtifactDiscovery.From(preview), Commit(preview, ProjectImportSourceState.NotDetected));
        return explorers;
    }

    [Fact]
    public async Task The_shared_base_is_the_module_data_model_under_a_pack_local_one_and_never_a_sibling_pack_s()
    {
        var explorers = Import(("shop/docs/data-model.md", BaseModel),
            ("shop/specs/001-refund/spec.md", Spec), ("shop/specs/001-refund/data-model.md", PackModel),
            ("shop/specs/002-invoice/spec.md", Spec.Replace("Cart", "Invoice")), ("shop/specs/002-invoice/data-model.md", SiblingModel),
            ("shop/specs/003-cart/spec.md", Spec.Replace("Cart", "Basket")));

        await explorers.SelectPackAsync("001-refund");
        var shared = await explorers.GetSharedBaseAsync(WorkspaceArtifactType.DataModel);
        shared!.Selected!.SourcePath.Should().Be("shop/docs/data-model.md");
        shared.Resolution.Should().Be("Module-shared artifact");
        (await explorers.GetStateAsync(WorkspaceArtifactType.DataModel)).Selected!.SourcePath.Should().Be("shop/specs/001-refund/data-model.md",
            "looking up the base never changes what is viewed");

        await explorers.SelectPackAsync("003-cart");
        (await explorers.GetStateAsync(WorkspaceArtifactType.DataModel)).Selected!.SourcePath.Should().Be("shop/docs/data-model.md", "pack 003 has no model of its own");
        (await explorers.GetSharedBaseAsync(WorkspaceArtifactType.DataModel)).Should().BeNull("the base is already what pack 003 views; there is no second layer");
    }

    [Fact]
    public async Task Without_a_shared_data_model_a_pack_model_has_no_base_layer()
    {
        var explorers = Import(("specs/001-refund/spec.md", Spec), ("specs/001-refund/data-model.md", PackModel),
            ("specs/002-invoice/spec.md", Spec.Replace("Cart", "Invoice")), ("specs/002-invoice/data-model.md", SiblingModel));

        await explorers.SelectPackAsync("001-refund");
        (await explorers.GetSharedBaseAsync(WorkspaceArtifactType.DataModel)).Should().BeNull("a sibling pack's data model is never a base");
    }
}
