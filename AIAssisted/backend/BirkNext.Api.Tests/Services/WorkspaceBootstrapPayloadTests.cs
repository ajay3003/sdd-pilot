using System.Text.Json.Nodes;
using BirkNext.Api.Controllers;
using BirkNext.Api.Data;
using BirkNext.Api.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Application start restores a workspace from its bootstrap: identity, artifacts and the lifecycle as a manifest (every record, no document
/// bodies). Bodies come from documents/{id}; a write that carries a manifest is completed from the stored lifecycle, so nothing is lost.
/// </summary>
public sealed class WorkspaceBootstrapPayloadTests : IDisposable
{
    private static readonly Guid Spec = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Plan = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private static string Lifecycle(string specContent = "# Specification\nFR-001", string planContent = "# Plan") => new JsonObject
    {
        ["SchemaVersion"] = 1,
        ["CurrentProjectImportId"] = "import-1",
        ["Revisions"] = new JsonArray
        {
            new JsonObject { ["RevisionId"] = Spec.ToString(), ["Role"] = "Specification", ["FileName"] = "spec.md", ["Fingerprint"] = "fp-spec", ["Content"] = specContent, ["WorkspaceScope"] = "import:import-1" },
            new JsonObject { ["RevisionId"] = Plan.ToString(), ["Role"] = "Plan", ["FileName"] = "plan.md", ["Fingerprint"] = "fp-plan", ["Content"] = planContent, ["WorkspaceScope"] = "import:import-1" },
        },
        ["GeneratedDocumentationSets"] = new JsonArray { new JsonObject { ["Artifacts"] = new JsonArray { new JsonObject { ["Fingerprint"] = "gen-1" } } } },
        ["GeneratedDocumentContents"] = new JsonObject { ["gen-1"] = "generated body" },
        ["ProjectImports"] = new JsonArray { new JsonObject { ["ImportId"] = "import-1", ["ProjectName"] = "M2LB" } },
    }.ToJsonString();

    [Fact]
    public void The_manifest_keeps_every_record_and_identity_but_no_document_body()
    {
        var manifest = JsonNode.Parse(WorkspaceLifecycleDocuments.Manifest(Lifecycle())!)!.AsObject();

        var revisions = manifest["Revisions"]!.AsArray().Select(r => r!.AsObject()).ToList();
        revisions.Should().HaveCount(2, "every revision record stays: presence, roles and selection are answered without bodies");
        revisions.Should().OnlyContain(r => (string)r["Content"]! == "");
        revisions.Select(r => (string)r["Fingerprint"]!).Should().Equal("fp-spec", "fp-plan");
        revisions.Select(r => (int)r["ContentLength"]!).Should().Equal("# Specification\nFR-001".Length, "# Plan".Length);
        manifest["GeneratedDocumentContents"]!.AsObject().Count.Should().Be(0);
        manifest["ProjectImports"]!.AsArray().Should().ContainSingle("workspace identity (imports, current import) is bootstrap state");
        ((string)manifest["CurrentProjectImportId"]!).Should().Be("import-1");
    }

    [Fact]
    public void Documents_returns_every_body_by_revision_id_and_generated_fingerprint()
    {
        var documents = WorkspaceLifecycleDocuments.Documents(Lifecycle());

        documents.Revisions.Should().BeEquivalentTo(new Dictionary<Guid, string> { [Spec] = "# Specification\nFR-001", [Plan] = "# Plan" });
        documents.GeneratedDocuments.Should().BeEquivalentTo(new Dictionary<string, string> { ["gen-1"] = "generated body" });
    }

    [Fact]
    public void A_manifest_write_takes_each_body_from_the_stored_revision_with_the_same_id_and_fingerprint()
    {
        var stored = Lifecycle();
        var incoming = JsonNode.Parse(WorkspaceLifecycleDocuments.Manifest(stored)!)!.AsObject();
        // In this session: the plan was replaced by a new revision (different fingerprint, its own body) and the spec's body was never loaded.
        var plan = incoming["Revisions"]!.AsArray()[1]!.AsObject();
        plan["Fingerprint"] = "fp-plan-2";
        plan["Content"] = "# Plan v2";
        plan.Remove("ContentLength");

        var merged = JsonNode.Parse(WorkspaceLifecycleDocuments.FillMissing(incoming.ToJsonString(), stored)!)!.AsObject();

        var revisions = merged["Revisions"]!.AsArray().Select(r => r!.AsObject()).ToList();
        ((string)revisions[0]["Content"]!).Should().Be("# Specification\nFR-001", "the body was deferred, not deleted");
        ((string)revisions[1]["Content"]!).Should().Be("# Plan v2", "a body the session holds is never replaced");
        revisions.Should().OnlyContain(r => !r.ContainsKey("ContentLength"), "the manifest marker is not stored");
        ((string)merged["GeneratedDocumentContents"]!["gen-1"]!).Should().Be("generated body");
    }

    [Fact]
    public void A_body_is_only_restored_for_the_same_document_and_removals_are_kept()
    {
        var stored = Lifecycle();
        var incoming = JsonNode.Parse(WorkspaceLifecycleDocuments.Manifest(stored)!)!.AsObject();
        incoming["Revisions"]!.AsArray()[0]!["Fingerprint"] = "another-document"; // same id, different content identity
        incoming["Revisions"]!.AsArray().RemoveAt(1);                               // the plan revision was removed in this session
        incoming["GeneratedDocumentationSets"] = new JsonArray();                    // and the generated set with it

        var merged = JsonNode.Parse(WorkspaceLifecycleDocuments.FillMissing(incoming.ToJsonString(), stored)!)!.AsObject();

        merged["Revisions"]!.AsArray().Should().ContainSingle();
        ((string)merged["Revisions"]![0]!["Content"]!).Should().BeEmpty("a different document never borrows another one's body");
        merged["GeneratedDocumentContents"]!.AsObject().Count.Should().Be(0, "an unreferenced generated document is not brought back");
    }

    [Fact]
    public void A_complete_lifecycle_is_written_unchanged()
    {
        var full = Lifecycle();
        WorkspaceLifecycleDocuments.FillMissing(full, Lifecycle("# Older spec")).Should().BeSameAs(full);
    }

    [Fact]
    public async Task Auto_save_and_save_from_a_manifest_keep_the_stored_bodies()
    {
        var service = new WorkspacePersistenceService(_db, NullLogger<WorkspacePersistenceService>.Instance);
        await service.SaveAsAsync("M2LB", [], Lifecycle());

        await service.AutoSaveAsync(null, "", [], WorkspaceLifecycleDocuments.Manifest(Lifecycle()));
        var afterAutoSave = await service.LoadAsync((await service.GetCurrentWorkspaceIdAsync())!.Value);
        WorkspaceLifecycleDocuments.Documents(afterAutoSave!.SddLifecycleJson).Revisions.Should().HaveCount(2);

        await service.SaveCurrentAsync(null, [], WorkspaceLifecycleDocuments.Manifest(Lifecycle()));
        var afterSave = await service.LoadAsync(afterAutoSave.Id);
        WorkspaceLifecycleDocuments.Documents(afterSave!.SddLifecycleJson).Should().BeEquivalentTo(WorkspaceLifecycleDocuments.Documents(Lifecycle()));

        var copy = await service.SaveAsAsync("M2LB copy", [], WorkspaceLifecycleDocuments.Manifest(Lifecycle()));
        WorkspaceLifecycleDocuments.Documents(copy.SddLifecycleJson).Revisions.Should().HaveCount(2, "save-as from a manifest copies the current workspace's bodies");
    }

    [Fact]
    public async Task Bootstrap_is_the_workspace_without_bodies_and_documents_returns_them()
    {
        var service = new WorkspacePersistenceService(_db, NullLogger<WorkspacePersistenceService>.Instance);
        var saved = await service.SaveAsAsync("M2LB", [], Lifecycle());
        var controller = new WorkspacePersistenceController(service, NullLogger<WorkspacePersistenceController>.Instance);

        var bootstrap = (await controller.Bootstrap(saved.Id)).Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<WorkspaceBootstrapDto>().Which;
        bootstrap.Name.Should().Be("M2LB");
        bootstrap.DocumentsDeferred.Should().BeTrue();
        bootstrap.DeferredDocuments.Should().Be(3);
        bootstrap.SddLifecycleJson.Should().NotContain("FR-001").And.NotContain("generated body");

        var documents = (await controller.Documents(saved.Id)).Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeOfType<WorkspaceDocumentsDto>().Which;
        documents.Revisions[Spec].Should().Be("# Specification\nFR-001");

        (await controller.Bootstrap(Guid.NewGuid())).Result.Should().BeOfType<NotFoundObjectResult>();
        (await controller.Documents(Guid.NewGuid())).Result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task The_workspace_list_carries_summaries_without_lifecycle_or_artifact_bodies()
    {
        var service = new WorkspacePersistenceService(_db, NullLogger<WorkspacePersistenceService>.Instance);
        await service.SaveAsAsync("M2LB", [new WorkspaceArtifactDto { ArtifactType = BirkNext.Api.Models.ArtifactType.Specification, FileName = "spec.md", Content = "# Spec" }], Lifecycle());
        var controller = new WorkspacePersistenceController(service, NullLogger<WorkspacePersistenceController>.Instance);

        var list = (await controller.List()).Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<List<SavedWorkspaceDto>>().Which;

        list.Should().ContainSingle().Which.Should().Match<SavedWorkspaceDto>(w => w.SddLifecycleJson == null && w.Artifacts.Count == 1 && w.Artifacts[0].Content == "");
    }
}
