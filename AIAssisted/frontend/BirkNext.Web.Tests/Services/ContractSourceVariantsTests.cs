using BirkNext.ContractTesting;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Source-discovered contract files as candidate logical APIs: a role suggested by location (never an authority), GraphQL files without root
/// operations treated as fragments (not invalid, not standalone-testable), and one summary over the listed files.
/// </summary>
public sealed class ContractSourceVariantsTests
{
    private static SourceContractCandidate C(string file, string type, int operations, SourceContractCandidateState state = SourceContractCandidateState.New, string? imported = null) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, "M2LB-language.zip", null, file, System.IO.Path.GetFileName(file), type, file, operations, imported)
        {
            Module = file.Split('/')[0], State = state, Protocol = type == "OpenApi" ? ApiContractProtocol.Rest : ApiContractProtocol.GraphQl,
        };

    [Theory]
    [InlineData("AutorisasjonService/autodoc/openapi.yaml", "Generated documentation")]
    [InlineData("AutorisasjonService/specs/001-autorisasjon-initial/contracts/auth-graphql-sdl.graphql", "Specification")]
    [InlineData("AutorisasjonService/tests/M2LB.AutorisasjonService.ContractTests/GraphQL/schema.snapshot.graphql", "Test snapshot")]
    [InlineData("Frontend/src/M2LB.Frontend.Web/GraphQL/Authorization/schema.graphql", "Client schema")]
    [InlineData("Frontend/src/M2LB.Frontend.Web/GraphQL/Authorization/schema.extensions.graphql", "Schema extension")]
    [InlineData("Proxy/api.yaml", "Source")]
    public void A_role_is_suggested_by_location(string file, string role) => ContractSourceVariants.RoleHint(file).Should().Be(role);

    [Fact]
    public void A_GraphQL_file_without_root_operations_is_a_fragment_and_REST_never_is()
    {
        ContractSourceVariants.IsFragment(C("Frontend/x/schema.extensions.graphql", "GraphQl", 0)).Should().BeTrue();
        ContractSourceVariants.IsFragment(C("Frontend/x/schema.graphql", "GraphQl", 12)).Should().BeFalse();
        ContractSourceVariants.IsFragment(C("Proxy/openapi.yaml", "OpenApi", 0)).Should().BeFalse("an empty REST document is not a GraphQL fragment");
    }

    [Fact]
    public void The_summary_counts_each_listed_file_once_by_group_protocol_and_state()
    {
        var files = new[]
        {
            C("SkoleService/autodoc/openapi.yaml", "OpenApi", 5),
            C("SkoleService/autodoc/schema.graphql", "GraphQl", 8, SourceContractCandidateState.UpToDate, "skole-graphql"),
            C("SkoleService/tests/x/schema.snapshot.graphql", "GraphQl", 8, SourceContractCandidateState.NeedsReview),
            C("Frontend/x/schema.extensions.graphql", "GraphQl", 0),
        };
        ContractSourceVariants.Summary(files).Should().Be(
            "4 files in 3 candidate logical API groups · 1 REST · 3 GraphQL · 1 already imported · 2 new or changed · 1 need identity review · 1 GraphQL fragment");
        ContractSourceVariants.Summary([]).Should().Be("No contract files listed.");
    }
}
