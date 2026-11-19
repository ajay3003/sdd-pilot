using System.Diagnostics;
using BirkNext.ContractTesting;
using BirkNext.Web.Services;
using FluentAssertions;
using CI = BirkNext.Web.Services.ContractImportPresentation;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Contract Testing import rules (2026-11-18): identical copies by document hash only, related files by shared operations (REST per service), a
/// fragment's possible parents labelled as a location proposal, a preview that flags several projects, file and operation counts kept apart,
/// and a deep link resolved by snapshot + exact path (never by name).
/// </summary>
public sealed class ContractImportPresentationTests
{
    private static readonly Guid S1 = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static SourceContractCandidate File(string file, string type, string module, string project = "hub", string? sha = null, string[]? ops = null,
        SourceContractCandidateState state = SourceContractCandidateState.New, string? imported = null, Guid? snapshot = null) =>
        new(snapshot ?? S1, DateTimeOffset.UtcNow, "Hub.zip", null, $"{type.ToLowerInvariant()}:{file}", Path.GetFileName(file), type, file, (ops ?? []).Length, imported)
        {
            Module = module, Protocol = type == "OpenApi" ? ApiContractProtocol.Rest : ApiContractProtocol.GraphQl, ProjectKey = project, ProjectName = project.ToUpperInvariant(),
            DocumentSha256 = sha, Operations = [.. ops ?? []], State = state, HasDocument = sha is not null,
        };

    private static readonly string[] Auth = ["query user", "mutation activateEmergencyAccess", "mutation revokeEmergencyAccess"];

    [Fact]
    public void Identical_copies_need_the_same_document_hash_and_related_files_share_operations_without_being_merged()
    {
        var main = File("Auth/autodoc/schema.graphql", "GraphQlSchema", "Auth", sha: "aaa", ops: Auth);
        var snapshot = File("Auth/tests/schema.snapshot.graphql", "GraphQlSchema", "Auth", sha: "aaa", ops: Auth);
        var client = File("Frontend/GraphQL/schema.graphql", "GraphQlSchema", "Frontend", sha: "bbb", ops: ["query user", "mutation activateEmergencyAccess"]);
        var other = File("Other/schema.graphql", "GraphQlSchema", "Other", project: "elsewhere", sha: "aaa", ops: Auth);
        var all = new[] { main, snapshot, client, other };
        CI.IdenticalCopies(main, all).Should().Equal(snapshot);
        CI.IdenticalCopies(main, all).Should().NotContain(other, "another project's file is never a copy");
        CI.Related(main, all).Should().ContainSingle().Which.Should().Be((client, 2));
        CI.Related(File("x.graphql", "GraphQlSchema", "X"), all).Should().BeEmpty("a file without operations relates to nothing by operations");
    }

    [Fact]
    public void REST_operations_relate_only_within_one_service_module()
    {
        var a = File("AuthService/openapi.yaml", "OpenApi", "AuthService", sha: "1", ops: ["GET /health/live", "GET /users"]);
        var b = File("PersonService/openapi.yaml", "OpenApi", "PersonService", sha: "2", ops: ["GET /health/live"]);
        var a2 = File("AuthService/specs/openapi.yaml", "OpenApi", "AuthService", sha: "3", ops: ["GET /users"]);
        CI.Related(a, [a, b, a2]).Select(r => r.File).Should().Equal(a2);
        CI.Summarize([a, b, a2]).Should().Match<CI.Summary>(s => s.OperationEntries == 4 && s.DistinctOperations == 3 && s.Rest == 3 && s.Files == 3);
    }

    [Fact]
    public void A_fragment_is_explained_and_its_possible_parents_come_from_the_same_module_and_protocol()
    {
        var parent = File("Auth/schema.graphql", "GraphQlSchema", "Auth", ops: Auth);
        var extension = File("Auth/extensions.graphql", "GraphQlSchema", "Auth");
        var elsewhere = File("Person/schema.graphql", "GraphQlSchema", "Person", ops: ["query person"]);
        ContractSourceVariants.IsFragment(extension).Should().BeTrue();
        CI.PossibleParents(extension, [parent, extension, elsewhere]).Should().Equal(parent);
        CI.PossibleParents(parent, [parent, extension]).Should().BeEmpty("a schema with root operations is not a fragment");
        CI.Apply([parent, extension, elsewhere], new CI.Filter(State: "fragments")).Should().Equal(extension);
    }

    [Fact]
    public void The_preview_counts_states_projects_fragments_and_identical_copies()
    {
        var a = File("A/openapi.yaml", "OpenApi", "A", sha: "x", ops: ["GET /a"]);
        var b = File("B/openapi.yaml", "OpenApi", "B", sha: "x", ops: ["GET /a"], state: SourceContractCandidateState.Changed);
        var f = File("A/ext.graphql", "GraphQlSchema", "A");
        var foreign = File("C/openapi.yaml", "OpenApi", "C", project: "other", ops: ["GET /c"]);
        CI.Preview([a, b, f], "hub").Should().Match<CI.ImportPreview>(p => p.Contracts == 3 && p.New == 2 && p.Changed == 1 && p.Fragments == 1 && p.IdenticalPairs == 1
            && p.Projects.Count == 1 && !p.NeedsProjectConfirmation);
        CI.Preview([a, foreign], null).NeedsProjectConfirmation.Should().BeTrue("files of two projects are never imported silently together");
        CI.Preview([a, foreign], "hub").NeedsProjectConfirmation.Should().BeFalse("with a project the server validates ownership itself");
    }

    [Fact]
    public void A_deep_link_resolves_by_snapshot_and_exact_path_only()
    {
        var other = Guid.NewGuid();
        var a = File("Svc/openapi.yaml", "OpenApi", "Svc");
        var b = File("Svc/openapi.yaml", "OpenApi", "Svc", snapshot: other);
        CI.Find([a, b], S1, "Svc/openapi.yaml").Should().Be(a);
        CI.Find([a, b], null, "Svc/openapi.yaml").Should().BeNull("the same path in two snapshots is ambiguous without the snapshot");
        CI.Find([a], S1, "svc/OPENAPI.yaml").Should().BeNull("paths are exact");
        CI.Find([a], S1, "Svc\\openapi.yaml").Should().Be(a);
        CI.SourceLink(S1, "Svc/openapi.yaml", "GET /a b").Should().Be($"api-quality-review/contracts?mode=import&snapshot={S1:D}&file=Svc%2Fopenapi.yaml&operation=GET%20%2Fa%20b");
        CI.ImportedKey(File("x", "OpenApi", "x", imported: "children-api@v2")).Should().Be("children-api");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(1000)]
    public void Large_inventories_filter_summarize_and_preview_quickly(int count)
    {
        var files = Enumerable.Range(0, count).Select(i => File($"Svc{i % 40}/{(i % 3 == 0 ? "schema.graphql" : "openapi.yaml")}{i}", i % 3 == 0 ? "GraphQlSchema" : "OpenApi", $"Svc{i % 40}",
            sha: $"s{i % 7}", ops: [.. Enumerable.Range(0, 40).Select(o => $"GET /r{(i + o) % 90}")])).ToList();
        var watch = Stopwatch.StartNew();
        CI.Summarize(files);
        CI.Apply(files, new CI.Filter("r1", "rest", "actionable"));
        CI.Preview(files, "hub");
        CI.Related(files[0], files);
        watch.ElapsedMilliseconds.Should().BeLessThan(3000);
    }
}
