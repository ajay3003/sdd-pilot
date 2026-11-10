using System.Security.Cryptography;
using System.Text;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ContractAnalysis;
using BirkNext.Api.Services.ContractTesting;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.ContractTesting;
using BirkNext.Integrations;
using BirkNext.SourceDomains;
using BirkNext.SourceEvidence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractTesting;

/// <summary>
/// CTH-F01 / CTH-F02: project-scoped contract identity (the Source Analysis repository identity, joined only by declared same-repository
/// associations), rename/move classification inside a project with recorded human decisions, legacy identity resolution that never merges or
/// guesses, and cross-project isolation of explicit targets and listings.
/// </summary>
public sealed class ContractIdentityTests
{
    private sealed class Sources : IReviewSourceEvidenceProvider
    {
        public List<IqrSourceSnapshot> All { get; } = [];
        public bool SourceAnalysisEnabled => true;
        public Task<IReadOnlyList<IqrSourceSnapshot>> ListAsync(string environmentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([.. All]);
        public Task<IqrSourceSnapshot?> ResolveAsync(string environmentId, Guid snapshotId, CancellationToken ct = default) => Task.FromResult(All.FirstOrDefault(s => s.Id == snapshotId));
    }

    private const string Person = """
        { "openapi": "3.0.3", "info": { "title": "Person API", "version": "1" }, "paths": {
          "/persons": { "get": { "responses": { "200": { "description": "ok" } } }, "post": { "responses": { "201": { "description": "created" } } } },
          "/persons/{id}": { "get": { "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ], "responses": { "200": { "description": "ok" } } } },
          "/persons/{id}/address": { "get": { "parameters": [ { "name": "id", "in": "path", "required": true, "schema": { "type": "string" } } ], "responses": { "200": { "description": "ok" } } } } } }
        """;
    // Same operations, one more, a changed response: a changed document with 4 of 5 operations shared.
    private static readonly string PersonChanged = Person.Replace("\"version\": \"1\"", "\"version\": \"2\"", StringComparison.Ordinal)
        .Replace("\"/persons/{id}/address\"", "\"/persons/{id}/contact\": { \"get\": { \"responses\": { \"200\": { \"description\": \"ok\" } } } }, \"/persons/{id}/address\"", StringComparison.Ordinal);
    private const string Orders = """
        { "openapi": "3.0.3", "info": { "title": "Person API", "version": "1" }, "paths": {
          "/orders": { "get": { "responses": { "200": { "description": "ok" } } } }, "/orders/{id}": { "delete": { "responses": { "204": { "description": "gone" } } } } } }
        """;
    private const string Schema = "type Child { id: ID! name: String! }\ntype Query { child(id: ID!): Child children: [Child!]! }\ntype Mutation { createChild(name: String!): Child! }";

    private readonly Sources _sources = new();
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"cid-{Guid.NewGuid():N}").Options);
    private ContractRegistryService Registry => new(_db, new ContractSourceFetcher(new HttpClient()), null, _sources);
    private DateTimeOffset _clock = DateTimeOffset.UtcNow.AddDays(-1);

    private IqrSourceSnapshot Snapshot(string project, params (string File, string Text)[] files)
    {
        _clock = _clock.AddMinutes(5);
        var contracts = files.Select(f => (f.File.EndsWith(".graphql", StringComparison.Ordinal)
            ? ContractAnalyzer.ReadGraphQl(f.File, f.Text)! with { Id = $"graphql:{f.File}", File = f.File }
            : ContractAnalyzer.ReadOpenApiDocument(f.File, f.Text)! with { Id = $"openapi:{f.File}", File = f.File })).ToList();
        var snapshot = new IqrSourceSnapshot
        {
            Id = Guid.NewGuid(), AnalyzedAt = _clock, Archive = new($"{project}.zip", "abc", 1), Repository = new SourceRepositoryIdentity(project, project.ToUpperInvariant(), "test"),
            EvidenceDomains = new SourceEvidenceDomainsSnapshot { Contracts = new() { Domain = SourceEvidenceDomain.Contracts, Contracts = contracts } },
        };
        _sources.All.Add(snapshot);
        foreach (var (file, text) in files)
            _db.SourceContractDocuments.Add(new SourceContractDocumentRecord { Id = Guid.NewGuid(), SnapshotId = snapshot.Id, Path = file, Text = text, CapturedAt = _clock,
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant() });
        _db.SaveChanges();
        return snapshot;
    }

    private Task<ContractImportResult> Import(IqrSourceSnapshot s, string file, string? key = null, string? name = null) =>
        Registry.ImportFromSourceAsync(new(s.Id, (file.EndsWith(".graphql", StringComparison.Ordinal) ? "graphql:" : "openapi:") + file, key, name), default);

    private async Task<SourceContractCandidate> Candidate(IqrSourceSnapshot s, string file) =>
        (await Registry.SourceCandidatesAsync(null, default)).Single(c => c.SnapshotId == s.Id && c.File == file);

    [Fact]
    public async Task Same_path_in_two_projects_is_two_contracts_and_each_project_keeps_its_own_version_history()
    {
        var a1 = Snapshot("project-a", ("Person/autodoc/openapi.json", Person));
        var b1 = Snapshot("project-b", ("Person/autodoc/openapi.json", Person));            // C: identical document, other project
        var a = await Import(a1, "Person/autodoc/openapi.json");
        var b = await Import(b1, "Person/autodoc/openapi.json");
        a.Imported.Should().BeTrue(a.Message); b.Imported.Should().BeTrue(b.Message);
        b.Key.Should().NotBe(a.Key, "the same path in another project is another contract");
        _db.ApiContracts.Single(c => c.Key == a.Key).ProjectKey.Should().Be("project-a");
        _db.ApiContracts.Single(c => c.Key == b.Key).ProjectKey.Should().Be("project-b");

        (await Import(a1, "Person/autodoc/openapi.json")).Unchanged.Should().BeTrue("F: repeated import");
        var a2 = Snapshot("project-a", ("Person/autodoc/openapi.json", Person));            // A: unchanged in a newer snapshot
        (await Candidate(a2, "Person/autodoc/openapi.json")).State.Should().Be(SourceContractCandidateState.UpToDate);
        (await Import(a2, "Person/autodoc/openapi.json")).Should().Match<ContractImportResult>(r => r.Unchanged && r.Key == a.Key && r.Version == 1);
        var a3 = Snapshot("project-a", ("Person/autodoc/openapi.json", PersonChanged));     // B/E: changed → next version of the same contract
        (await Import(a3, "Person/autodoc/openapi.json")).Should().Match<ContractImportResult>(r => r.Imported && r.Key == a.Key && r.Version == 2);
        var b2 = Snapshot("project-b", ("Person/autodoc/openapi.json", Orders));            // D: other content in project B
        (await Import(b2, "Person/autodoc/openapi.json")).Should().Match<ContractImportResult>(r => r.Imported && r.Key == b.Key && r.Version == 2);
        _db.ApiContracts.Count().Should().Be(2, "no duplicate in either project");
        _db.ApiContractVersions.Where(v => v.ContractId == _db.ApiContracts.Single(c => c.Key == a.Key).Id).Select(v => v.SourceProjectKey).Distinct().Should().Equal("project-a");

        // H: listings and explicit targets are project-scoped.
        (await Registry.ListAsync("project-a", default)).Select(c => c.Key).Should().Equal(a.Key);
        (await Registry.SourceCandidatesAsync("project-b", default)).Should().OnlyContain(c => c.ProjectKey == "project-b");
        var cross = await Import(b2, "Person/autodoc/openapi.json", key: a.Key);
        cross.Unchanged.Should().BeTrue("already imported from this snapshot"); // and never a version of project A's contract:
        var b3 = Snapshot("project-b", ("Person/autodoc/openapi.json", PersonChanged));
        (await Import(b3, "Person/autodoc/openapi.json", key: a.Key)).Problems.Should().ContainSingle(p => p.Contains("never shared across projects"));
    }

    [Fact]
    public async Task A_declared_same_repository_association_joins_two_repository_identities_into_one_project()
    {
        var lang = Snapshot("m2lb-language", ("Person/autodoc/openapi.json", Person));
        var key = (await Import(lang, "Person/autodoc/openapi.json")).Key;
        var m2lb = Snapshot("m2lb", ("Person/autodoc/openapi.json", PersonChanged));
        (await Candidate(m2lb, "Person/autodoc/openapi.json")).State.Should().Be(SourceContractCandidateState.New, "without a declaration these are different projects");
        await new SourceRepositoryAssociationService(_db).DeclareAsync(new("m2lb-language", "M2LB-language", "m2lb", "M2LB", "same project"), "tester");
        (await Candidate(m2lb, "Person/autodoc/openapi.json")).Should().Match<SourceContractCandidate>(c => c.State == SourceContractCandidateState.Changed && c.ContractKey == key);
        (await Import(m2lb, "Person/autodoc/openapi.json")).Should().Match<ContractImportResult>(r => r.Imported && r.Key == key && r.Version == 2);
        (await Registry.ProjectsAsync(default)).Should().ContainSingle().Which.Keys.Should().Equal("m2lb", "m2lb-language");
    }

    [Fact]
    public async Task An_identical_document_at_a_new_path_is_a_recorded_move_with_its_history_and_no_new_version()
    {
        var s1 = Snapshot("p", ("Autorisasjon/autodoc/openapi.json", Person));
        var key = (await Import(s1, "Autorisasjon/autodoc/openapi.json")).Key;
        var s2 = Snapshot("p", ("AutorisasjonService/autodoc/openapi.json", Person));
        var c = await Candidate(s2, "AutorisasjonService/autodoc/openapi.json");
        c.Should().Match<SourceContractCandidate>(x => x.Identity == ContractIdentityClassification.MovedUnchanged && x.State == SourceContractCandidateState.Moved && x.ContractKey == key);
        c.Candidates.Single().Evidence.Should().Contain(e => e.Contains("identical document"));
        var moved = await Import(s2, "AutorisasjonService/autodoc/openapi.json");
        moved.Should().Match<ContractImportResult>(r => r.Moved && r.Unchanged && r.Key == key && r.Version == 1);
        _db.ApiContracts.Count().Should().Be(1);
        (await Registry.DecisionsAsync(key!, default)).Should().ContainSingle(d => d.Kind == "location" && d.SubjectKey == "openapi:AutorisasjonService/autodoc/openapi.json");
        // Next analysis: the new location is now the contract's own.
        var s3 = Snapshot("p", ("AutorisasjonService/autodoc/openapi.json", PersonChanged));
        (await Import(s3, "AutorisasjonService/autodoc/openapi.json")).Should().Match<ContractImportResult>(r => r.Imported && r.Key == key && r.Version == 2);
    }

    [Fact]
    public async Task A_changed_document_at_a_new_path_needs_a_person_and_the_confirmed_decision_is_recorded()
    {
        var s1 = Snapshot("p", ("Autorisasjon/autodoc/openapi.json", Person));
        var key = (await Import(s1, "Autorisasjon/autodoc/openapi.json")).Key!;
        var s2 = Snapshot("p", ("AutorisasjonService/autodoc/openapi.json", PersonChanged));
        var c = await Candidate(s2, "AutorisasjonService/autodoc/openapi.json");
        c.Should().Match<SourceContractCandidate>(x => x.Identity == ContractIdentityClassification.MovedChanged && x.State == SourceContractCandidateState.NeedsReview);
        c.Candidates.Single().Should().Match<ContractIdentityCandidate>(x => x.Key == key && x.SharedOperations == 4 && x.SourceOperations == 5 && !x.SameDocument);
        var refused = await Import(s2, "AutorisasjonService/autodoc/openapi.json");
        refused.NeedsReview.Should().BeTrue("never a silent second contract when a strong relocation candidate exists");
        _db.ApiContracts.Count().Should().Be(1);

        var decided = await Registry.DecideIdentityAsync(new(s2.Id, "openapi:AutorisasjonService/autodoc/openapi.json", key, true), "reviewer@example.test", default);
        decided.Should().Match<ContractImportResult>(r => r.Imported && r.Key == key && r.Version == 2);
        var versions = _db.ApiContractVersions.Where(v => v.ContractId == _db.ApiContracts.Single().Id).OrderBy(v => v.Version).Select(v => v.SourcePath).ToList();
        versions.Should().Equal("openapi:Autorisasjon/autodoc/openapi.json", "openapi:AutorisasjonService/autodoc/openapi.json");
        (await Registry.DecisionsAsync(key, default)).Should().ContainSingle(d => d.Kind == "identity-confirm" && d.DecidedBy == "reviewer@example.test");
    }

    [Fact]
    public async Task Titles_alone_never_match_while_shared_operations_under_another_title_do_and_several_candidates_are_ambiguous()
    {
        var s1 = Snapshot("p", ("Person/openapi.json", Person));
        var personKey = (await Import(s1, "Person/openapi.json")).Key!;
        // Same title "Person API", unrelated operations, new path: a new contract.
        var s2 = Snapshot("p", ("Orders/openapi.json", Orders));
        (await Candidate(s2, "Orders/openapi.json")).Identity.Should().Be(ContractIdentityClassification.NewContract);
        // Different title, same operation signatures, new path: a strong candidate (review).
        var s3 = Snapshot("p", ("People/openapi.json", PersonChanged.Replace("Person API", "People", StringComparison.Ordinal)));
        (await Candidate(s3, "People/openapi.json")).Should().Match<SourceContractCandidate>(c => c.Identity == ContractIdentityClassification.MovedChanged && c.ContractKey == personKey);
        // Two existing contracts with the same operations, both moved away: ambiguous.
        var s4 = Snapshot("p", ("Copy/openapi.json", Person));
        var copyKey = (await Import(s4, "Copy/openapi.json", name: "Person copy")).Key!;
        var s5 = Snapshot("p", ("Moved/openapi.json", PersonChanged));
        var amb = await Candidate(s5, "Moved/openapi.json");
        amb.Identity.Should().Be(ContractIdentityClassification.Ambiguous);
        amb.Candidates.Select(c => c.Key).Should().BeEquivalentTo([personKey, copyKey]);
        // Rejecting one leaves the other; rejecting all imports as a new contract.
        await Registry.DecideIdentityAsync(new(s5.Id, "openapi:Moved/openapi.json", copyKey, false), "reviewer", default);
        (await Candidate(s5, "Moved/openapi.json")).Should().Match<SourceContractCandidate>(c => c.Identity == ContractIdentityClassification.MovedChanged && c.ContractKey == personKey);
        var asNew = await Registry.DecideIdentityAsync(new(s5.Id, "openapi:Moved/openapi.json", null, false), "reviewer", default);
        asNew.Imported.Should().BeTrue(asNew.Message);
        asNew.Key.Should().NotBe(personKey).And.NotBe(copyKey);
        // A different project with the same title and path is never a candidate.
        var other = Snapshot("q", ("Moved/openapi.json", PersonChanged));
        (await Candidate(other, "Moved/openapi.json")).Identity.Should().Be(ContractIdentityClassification.NewContract);
    }

    [Fact]
    public async Task A_GraphQL_schema_moved_unchanged_is_recognised()
    {
        var s1 = Snapshot("p", ("Skole/schema.graphql", Schema));
        var key = (await Import(s1, "Skole/schema.graphql")).Key;
        var s2 = Snapshot("p", ("SkoleService/schema.graphql", Schema));
        (await Candidate(s2, "SkoleService/schema.graphql")).Should().Match<SourceContractCandidate>(c => c.Identity == ContractIdentityClassification.MovedUnchanged && c.ContractKey == key);
    }

    [Fact]
    public async Task Legacy_contracts_get_their_project_only_when_unambiguous_and_ambiguous_ones_block_until_assigned()
    {
        var a = Snapshot("project-a", ("Person/openapi.json", Person));
        var b = Snapshot("project-b", ("Person/openapi.json", PersonChanged));
        // Simulate rows written before project scope (no project on contracts or versions).
        var single = (await Import(a, "Person/openapi.json")).Key!;
        var mixed = (await Import(b, "Person/openapi.json")).Key!;
        var upload = (await Registry.ImportAsync(new() { FileName = "u.json", Content = Orders }, default)).Key!;
        foreach (var c in _db.ApiContracts) { c.ProjectKey = null; c.IdentityState = null; }
        foreach (var v in _db.ApiContractVersions) v.SourceProjectKey = null;
        // The mixed contract also got a version from project A's snapshot (the pre-project registry allowed that).
        var mixedId = _db.ApiContracts.Single(c => c.Key == mixed).Id;
        var row = _db.ApiContractVersions.Single(v => v.ContractId == mixedId);
        _db.ApiContractVersions.Add(new ApiContractVersionRow { Id = Guid.NewGuid(), ContractId = mixedId, Version = 2, Sha256 = "x", Source = row.Source, SourceLabel = "legacy", Format = row.Format,
            DocumentText = "", ContractJson = row.ContractJson, OperationCount = row.OperationCount, ImportedAt = row.ImportedAt, SourceSnapshotId = a.Id, SourcePath = "openapi:Other/openapi.json" });
        _db.SaveChanges();

        var (resolved, unscoped, review) = await Registry.ResolveLegacyIdentitiesAsync(default);
        (resolved, unscoped, review).Should().Be((1, 1, 1));
        _db.ApiContracts.Single(c => c.Key == single).ProjectKey.Should().Be("project-a");
        _db.ApiContracts.Single(c => c.Key == upload).IdentityState.Should().Be("Unscoped");
        _db.ApiContracts.Single(c => c.Key == mixed).Should().Match<ApiContractRow>(c => c.ProjectKey == null && c.IdentityState == "NeedsReview");
        (await Registry.ResolveLegacyIdentitiesAsync(default)).Should().Be((0, 0, 0), "idempotent");

        // A newer project-B snapshot of the same path is blocked (ambiguous) instead of creating a second contract next to the unresolved one.
        var b2 = Snapshot("project-b", ("Person/openapi.json", PersonChanged));
        (await Candidate(b2, "Person/openapi.json")).Identity.Should().Be(ContractIdentityClassification.Ambiguous);
        (await Import(b2, "Person/openapi.json")).NeedsReview.Should().BeTrue();
        (await Registry.AssignProjectAsync(mixed, "project-c", "reviewer", default)).Should().Contain("not a project any version");
        (await Registry.AssignProjectAsync(mixed, "project-b", "reviewer", default)).Should().BeNull();
        (await Import(b2, "Person/openapi.json")).Should().Match<ContractImportResult>(r => r.Key == mixed);
        _db.ApiContracts.Count().Should().Be(3, "nothing merged, nothing duplicated");
        (await Registry.DecisionsAsync(mixed, default)).Should().ContainSingle(d => d.Kind == "project-assignment" && d.DecidedBy == "reviewer");
    }
}
