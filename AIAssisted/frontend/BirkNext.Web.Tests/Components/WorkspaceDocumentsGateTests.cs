using BirkNext.Web.Components;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BirkNext.Web.Tests.Components;

/// <summary>
/// A workspace restored from its bootstrap manifest holds no document bodies. A page that reads documents waits for them inside the shell;
/// a page that reads none renders at once; a failure says so with Retry — never "no documents".
/// </summary>
public sealed class WorkspaceDocumentsGateTests : BunitContext
{
    private static readonly Guid Workspace = Guid.NewGuid();
    private static readonly Guid Revision = Guid.NewGuid();
    private readonly WorkspaceArtifactRepository _repository = new();
    private readonly Mock<IWorkspacePersistenceApiService> _persistence = new() { CallBase = true };

    public WorkspaceDocumentsGateTests()
    {
        _repository.RestoreSddLifecycle(System.Text.Json.JsonSerializer.Serialize(new SddLifecycleState
        {
            Revisions = [new SddArtifactRevision { RevisionId = Revision, Role = "Specification", FileName = "spec.md", Fingerprint = "fp", ContentLength = 9, WorkspaceScope = null }],
        }));
        _repository.MarkDocumentsDeferred(Workspace);
        Services.AddSingleton(_persistence.Object);
        Services.AddSingleton(_repository);
        Services.AddSingleton<IWorkspaceDocumentHydration>(sp => new WorkspaceDocumentHydration(_repository, sp, NullLogger<WorkspaceDocumentHydration>.Instance));
    }

    private IRenderedComponent<WorkspaceDocumentsGate> Gate(Type page) => Render<WorkspaceDocumentsGate>(p => p
        .Add(c => c.PageType, page).Add(c => c.ChildContent, (RenderFragment)(b => b.AddMarkupContent(0, "<p id='page'>page</p>"))));

    [Fact]
    public void A_page_that_reads_documents_waits_for_them_and_then_renders_with_them()
    {
        var pending = new TaskCompletionSource<WorkspaceDocumentsDto?>();
        _persistence.Setup(p => p.LoadDocumentsAsync(Workspace, It.IsAny<CancellationToken>())).Returns(pending.Task);

        var cut = Gate(typeof(BirkNext.Web.Pages.Dashboard));

        cut.Find("[data-testid=workspace-documents-loading]").TextContent.Should().Contain("Loading project documents");
        cut.FindAll("#page").Should().BeEmpty("the page never renders from a manifest as if it had no documents");
        pending.SetResult(new WorkspaceDocumentsDto { Revisions = { [Revision] = "# Spec 1" } });
        cut.WaitForAssertion(() => cut.Find("#page"));
        _repository.SddLifecycle.Revisions.Single().Content.Should().Be("# Spec 1");
        _repository.DeferredDocumentsWorkspaceId.Should().BeNull();
    }

    [Fact]
    public void A_page_that_reads_no_documents_renders_at_once_and_loads_nothing()
    {
        var cut = Gate(typeof(BirkNext.Web.Pages.IntegrationQualityReview));

        cut.Find("#page");
        _persistence.Verify(p => p.LoadDocumentsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void A_failed_load_is_shown_as_a_failure_with_retry_and_is_not_retried_by_other_readers()
    {
        _persistence.Setup(p => p.LoadDocumentsAsync(Workspace, It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("boom", null, System.Net.HttpStatusCode.InternalServerError));

        var cut = Gate(typeof(BirkNext.Web.Pages.Dashboard));

        cut.WaitForAssertion(() => cut.Find("[data-testid=workspace-documents-error]").TextContent.Should().Contain("still stored"));
        cut.FindAll("#page").Should().BeEmpty();
        var hydration = Services.GetRequiredService<IWorkspaceDocumentHydration>();
        hydration.EnsureAsync().Result.Should().BeFalse("another reader gets the failure");
        _persistence.Verify(p => p.LoadDocumentsAsync(Workspace, It.IsAny<CancellationToken>()), Times.Once, "nothing retries on its own");

        _persistence.Setup(p => p.LoadDocumentsAsync(Workspace, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceDocumentsDto { Revisions = { [Revision] = "# Spec 1" } });
        cut.Find("[data-testid=workspace-documents-retry]").Click();
        cut.WaitForAssertion(() => cut.Find("#page"));
    }

    [Fact]
    public void A_body_added_in_this_session_is_never_replaced_by_the_stored_one()
    {
        _repository.SddLifecycle.Revisions.Single().Content = "# edited in this session";

        _repository.ApplyDocuments(new WorkspaceDocumentsDto { Revisions = { [Revision] = "# stored" }, GeneratedDocuments = { ["g"] = "generated" } });

        _repository.SddLifecycle.Revisions.Single().Content.Should().Be("# edited in this session");
        _repository.SddLifecycle.Revisions.Single().ContentLength.Should().Be(0, "the manifest marker is cleared once bodies are loaded and is never saved");
        _repository.SddLifecycle.GeneratedDocumentContents["g"].Should().Be("generated");
    }
}
