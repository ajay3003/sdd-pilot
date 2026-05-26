using BirkNext.Web.Components;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

public class SpecificationImportTests : BunitContext
{
    public SpecificationImportTests()
    {
        JSInterop.SetupVoid("fileImport.initDropZone", _ => true);
    }

    [Fact]
    public void Idle_ShowsDropzone()
    {
        var cut = Render<SpecificationImport>();

        cut.Find("[data-testid='import-zone']").Should().NotBeNull();
        cut.Find("[data-testid='import-file-input']").Should().NotBeNull();
    }

    [Fact]
    public async Task ValidTxtFile_ShowsFilenameAndSize()
    {
        var cut = Render<SpecificationImport>();

        await cut.Instance.OnFileDrop("spec.txt", 1_024, "The system shall allow login.");

        cut.Find("[data-testid='import-file-name']").TextContent.Should().Be("spec.txt");
        cut.Find("[data-testid='import-file-size']").TextContent.Should().Be("1.0 KB");
    }

    [Fact]
    public async Task ValidMdFile_InvokesOnFileImported()
    {
        string? receivedContent = null;

        var cut = Render<SpecificationImport>(p =>
            p.Add(c => c.OnFileImported, (string content) => { receivedContent = content; }));

        await cut.Instance.OnFileDrop("requirements.md", 512, "# Requirements\n\nThe system shall process data.");

        receivedContent.Should().Be("# Requirements\n\nThe system shall process data.");
    }

    [Fact]
    public async Task UnsupportedExtension_ShowsError()
    {
        var cut = Render<SpecificationImport>();

        await cut.Instance.OnFileDrop("document.pdf", 2_048, "some content");

        cut.Find("[data-testid='import-error']").Should().NotBeNull();
        cut.Find("[data-testid='import-error']").TextContent.Should().Contain(".pdf");
    }

    [Fact]
    public async Task OversizedFile_ShowsError()
    {
        var cut = Render<SpecificationImport>();

        await cut.Instance.OnFileDrop("large.txt", 2_000_000, new string('x', 100));

        cut.Find("[data-testid='import-error']").Should().NotBeNull();
        cut.Find("[data-testid='import-error']").TextContent.Should().Contain("too large");
    }

    [Fact]
    public async Task EmptyFile_ShowsError()
    {
        var cut = Render<SpecificationImport>();

        await cut.Instance.OnFileDrop("empty.txt", 0, "   ");

        cut.Find("[data-testid='import-error']").Should().NotBeNull();
        cut.Find("[data-testid='import-error']").TextContent.Should().Contain("empty");
    }

    [Fact]
    public async Task BomIsStripped_BeforeInvoking()
    {
        string? receivedContent = null;

        var cut = Render<SpecificationImport>(p =>
            p.Add(c => c.OnFileImported, (string content) => { receivedContent = content; }));

        var contentWithBom = '﻿' + "The system shall allow login.";
        await cut.Instance.OnFileDrop("spec.txt", 512, contentWithBom);

        receivedContent.Should().Be("The system shall allow login.");
        receivedContent.Should().NotStartWith("﻿");
    }

    [Fact]
    public async Task BinaryContent_ShowsError()
    {
        var cut = Render<SpecificationImport>();

        await cut.Instance.OnFileDrop("binary.txt", 128, "Some text\0with null byte");

        cut.Find("[data-testid='import-error']").Should().NotBeNull();
        cut.Find("[data-testid='import-error']").TextContent.Should().Contain("Binary");
    }

    [Fact]
    public async Task RemoveButton_ResetsToIdle()
    {
        var cut = Render<SpecificationImport>();
        await cut.Instance.OnFileDrop("spec.txt", 512, "Some content");

        cut.Find("[data-testid='import-remove']").Click();

        cut.Find("[data-testid='import-file-input']").Should().NotBeNull();
        cut.FindAll("[data-testid='import-file-name']").Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveButton_InvokesOnFileRemoved()
    {
        var removed = false;

        var cut = Render<SpecificationImport>(p =>
            p.Add(c => c.OnFileRemoved, () => { removed = true; }));

        await cut.Instance.OnFileDrop("spec.txt", 512, "Some content");
        cut.Find("[data-testid='import-remove']").Click();

        removed.Should().BeTrue();
    }

    [Fact]
    public async Task TryAgain_ResetsToIdle()
    {
        var cut = Render<SpecificationImport>();
        await cut.Instance.OnFileDrop("document.pdf", 512, "content");

        cut.Find("[data-testid='import-error']").Should().NotBeNull();

        cut.Instance.HandleTryAgain();
        cut.Render();

        cut.Find("[data-testid='import-file-input']").Should().NotBeNull();
        cut.FindAll("[data-testid='import-error']").Should().BeEmpty();
    }

    [Fact]
    public void IsDisabled_True_DisablesInteraction()
    {
        var cut = Render<SpecificationImport>(p =>
            p.Add(c => c.IsDisabled, true));

        cut.Find("[data-testid='import-zone']").ClassList
            .Should().Contain("is-disabled");
    }

    [Fact]
    public void DragEnter_SetsDragOverClass()
    {
        var cut = Render<SpecificationImport>();

        cut.Instance.HandleDragEnter();
        cut.Render();

        cut.Find("[data-testid='import-zone']").ClassList
            .Should().Contain("drag-over");
    }

    [Fact]
    public async Task OnFileDropError_ShowsErrorMessage()
    {
        var cut = Render<SpecificationImport>();

        await cut.Instance.OnFileDropError();

        cut.Find("[data-testid='import-error']").Should().NotBeNull();
        cut.Find("[data-testid='import-error']").TextContent
            .Should().Contain("Could not read");
    }
}
