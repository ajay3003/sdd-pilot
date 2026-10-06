using System.Diagnostics;
using BirkNext.Api.Controllers;
using BirkNext.Api.Models;
using BirkNext.Api.Services;
using BirkNext.Api.Services.SampleProjects;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Generic Sample Project inventory: recursive, bounded, deterministic, read-only, no fixed filenames, and file access
/// limited to the project's own readable documents.
/// </summary>
public sealed class SampleProjectDocumentInventoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bn-sample-inventory-" + Guid.NewGuid().ToString("N"));

    public SampleProjectDocumentInventoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Project(string slug, params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(_root, slug);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Enumerate_IsRecursive_AndReportsForwardSlashRelativePaths()
    {
        var dir = Project("nested",
            ("docs/requirements/school-attendance.md", "# Spec"),
            (".specify/memory/constitution.md", "# Constitution"),
            ("features/a/spec.md", "# A"),
            ("features/b/spec.md", "# B"),
            ("src/Program.cs", "class P {}"));

        var inventory = SampleProjectDocumentInventory.Enumerate(dir);

        inventory.Files.Select(f => f.RelativePath).Should().Equal(
            ".specify/memory/constitution.md", "docs/requirements/school-attendance.md", "features/a/spec.md", "features/b/spec.md", "src/Program.cs");
        inventory.Files.Where(f => f.IsDocument).Should().HaveCount(4);
        inventory.Files.Single(f => f.RelativePath == "src/Program.cs").IsDocument.Should().BeFalse();
        inventory.DocumentCount.Should().Be(4);
    }

    [Fact]
    public void Enumerate_SkipsBuildAndDependencyFolders()
    {
        var dir = Project("generated",
            ("spec.md", "# Spec"),
            ("node_modules/pkg/README.md", "# pkg"),
            ("bin/Debug/notes.md", "# bin"),
            ("obj/x.md", "# obj"),
            (".git/info.md", "# git"),
            ("TestResults/r.md", "# r"));

        var inventory = SampleProjectDocumentInventory.Enumerate(dir);

        inventory.Files.Select(f => f.RelativePath).Should().Equal("spec.md");
        inventory.IgnoredDirectories.Should().BeEquivalentTo([".git", "TestResults", "bin", "node_modules", "obj"]);
    }

    [Fact]
    public void Enumerate_MarksHugeAndBinaryDocumentsAsSkipped()
    {
        var dir = Project("limits", ("ok.md", "# ok"), ("fake.md", "# fake\0\0binary"));
        File.WriteAllText(Path.Combine(dir, "huge.md"), new string('x', (int)SampleProjectDocumentInventory.MaxDocumentBytes + 1));

        var inventory = SampleProjectDocumentInventory.Enumerate(dir);

        inventory.Files.Single(f => f.FileName == "ok.md").SkipReason.Should().BeNull();
        inventory.Files.Single(f => f.FileName == "huge.md").SkipReason.Should().Be(SampleInventorySkipReason.TooLarge);
        inventory.Files.Single(f => f.FileName == "fake.md").SkipReason.Should().Be(SampleInventorySkipReason.Binary);
        inventory.DocumentCount.Should().Be(1);
    }

    [Fact]
    public void Enumerate_DoesNotFollowDirectoryLinksOutOfTheProject()
    {
        var outside = Project("outside", ("secret.md", "# secret"));
        var dir = Project("linked", ("spec.md", "# Spec"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(dir, "escape"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Creating symbolic links needs a privilege on Windows; fall back to a directory junction.
            var junction = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{Path.Combine(dir, "escape")}\" \"{outside}\"")
                { CreateNoWindow = true, UseShellExecute = false });
            junction?.WaitForExit();
            if (!Directory.Exists(Path.Combine(dir, "escape"))) return; // neither links nor junctions available here
        }

        var inventory = SampleProjectDocumentInventory.Enumerate(dir);

        inventory.Files.Select(f => f.RelativePath).Should().Equal("spec.md");
        inventory.SkippedLinks.Should().Be(1);
        SampleProjectDocumentInventory.FindReadableDocument(inventory, "escape/secret.md").Should().BeNull();
    }

    [Fact]
    public void Enumerate_ThousandFileProject_IsBoundedAndFast()
    {
        var files = Enumerable.Range(0, 900).Select(i => ($"src/m{i % 30}/f{i}.cs", "class X {}"))
            .Concat(Enumerable.Range(0, 100).Select(i => ($"docs/area{i % 10}/doc{i}.md", "# Doc")))
            .ToArray();
        var dir = Project("large", files);

        var watch = Stopwatch.StartNew();
        var inventory = SampleProjectDocumentInventory.Enumerate(dir);
        watch.Stop();

        inventory.FilesScanned.Should().Be(1000);
        inventory.DocumentCount.Should().Be(100);
        inventory.Truncated.Should().BeFalse();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Enumerate_IsDeterministic()
    {
        var dir = Project("order", ("b.md", "#"), ("a/z.md", "#"), ("A.md", "#"), ("a/b/c.md", "#"));

        var first = SampleProjectDocumentInventory.Enumerate(dir).Files.Select(f => f.RelativePath).ToList();
        var second = SampleProjectDocumentInventory.Enumerate(dir).Files.Select(f => f.RelativePath).ToList();

        first.Should().Equal(second);
        first.Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("../outside/secret.md")]
    [InlineData("docs/../../outside/secret.md")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("")]
    [InlineData("./spec.md")]
    public void NormalizeRelative_RejectsTraversalAndAbsolutePaths(string path)
    {
        SampleProjectDocumentInventory.NormalizeRelative(path).Should().BeNull();
    }

    [Fact]
    public void FindReadableDocument_AcceptsBackslashesAndCase_ButOnlyListedDocuments()
    {
        var dir = Project("find", ("docs/Spec.md", "# Spec"), ("src/app.cs", "x"));
        var inventory = SampleProjectDocumentInventory.Enumerate(dir);

        SampleProjectDocumentInventory.FindReadableDocument(inventory, "docs\\spec.md")!.RelativePath.Should().Be("docs/Spec.md");
        SampleProjectDocumentInventory.FindReadableDocument(inventory, "src/app.cs").Should().BeNull("only candidate documents are served");
        SampleProjectDocumentInventory.FindReadableDocument(inventory, "docs/missing.md").Should().BeNull();
    }

    [Fact]
    public void Enumerate_NeverModifiesTheProject()
    {
        var dir = Project("readonly", ("spec.md", "# Spec"), ("docs/plan.md", "# Plan"));
        var before = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f), new FileInfo(f).Length)).ToList();

        SampleProjectDocumentInventory.Enumerate(dir);

        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f), new FileInfo(f).Length))
            .Should().Equal(before);
    }

    // ── Controller ─────────────────────────────────────────────────────────────────────────────────────────────

    private SampleProjectsController Controller() =>
        new(new SampleProjectCatalogService(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SampleProjects:BaseDirectory"] = _root }).Build()));

    [Fact]
    public void GetProjects_ListsRecursiveInventory_WithoutFixedExpectedFiles()
    {
        Project("spec-only", ("docs/requirements.md", "# Requirements"), ("src/a.cs", "x"));

        var dto = ((List<SampleProjectDto>)((OkObjectResult)Controller().GetProjects()).Value!).Single(p => p.Slug == "spec-only");

        dto.Files.Select(f => f.RelativePath).Should().Equal("docs/requirements.md", "src/a.cs");
        dto.Files.Should().OnlyContain(f => f.Exists && f.ArtifactKind == null);
        dto.Files.Single(f => f.RelativePath == "docs/requirements.md").IsSupported.Should().BeTrue();
        dto.Files.Should().NotContain(f => f.Filename == "constitution.md" || f.Filename == "tasks.md");
        dto.Discovery!.DocumentCount.Should().Be(1);
    }

    [Fact]
    public async Task GetFile_ServesNestedDocuments_AndRefusesTraversal()
    {
        Project("outside", ("secret.md", "# secret"));
        Project("proj", ("specs/001/spec.md", "# Nested spec"), ("src/app.cs", "x"));
        var controller = Controller();

        var ok = await controller.GetFile("proj", "specs/001/spec.md", CancellationToken.None);
        ((ContentResult)ok).Content.Should().Be("# Nested spec");

        (await controller.GetFile("proj", "../outside/secret.md", CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
        (await controller.GetFile("proj", "src/app.cs", CancellationToken.None)).Should().BeOfType<NotFoundObjectResult>();
        (await controller.GetFile("..", "secret.md", CancellationToken.None)).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetDocuments_ReturnsEveryReadableDocumentInPathOrder()
    {
        Project("docs", ("b/plan.md", "# Plan"), ("a/spec.md", "# Spec"), ("x.cs", "x"));

        var result = (OkObjectResult)await Controller().GetDocuments("docs", CancellationToken.None);
        var docs = (List<SampleDocumentContentDto>)result.Value!;

        docs.Select(d => d.RelativePath).Should().Equal("a/spec.md", "b/plan.md");
        docs.Should().OnlyContain(d => d.Content != null && d.Error == null);
    }

    // ── Real SampleData (regression fixtures; generic code only) ───────────────────────────────────────────────

    private static string? SampleDataRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "SampleData");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Theory]
    [InlineData("Meldingsutvekslermottak")]
    [InlineData("Skole")]
    [InlineData("SkoleAdapter")]
    public void NestedSpecKitSampleProjects_ExposeTheirNestedDocuments(string slug)
    {
        var root = SampleDataRoot();
        if (root is null || !Directory.Exists(Path.Combine(root, slug))) return;

        var inventory = SampleProjectDocumentInventory.Enumerate(Path.Combine(root, slug));

        var docs = inventory.Files.Where(f => f.IsDocument && f.SkipReason is null).Select(f => f.RelativePath).ToList();
        docs.Should().Contain(p => p.EndsWith(".specify/memory/constitution.md"));
        docs.Should().Contain(p => p.EndsWith("/spec.md") && p.Contains("/specs/"));
        docs.Should().Contain(p => p.EndsWith("/checklists/requirements.md"));
    }

    [Fact]
    public void OriginalSampleProjects_StillExposeTheirRootDocuments()
    {
        var root = SampleDataRoot();
        if (root is null) return;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var inventory = SampleProjectDocumentInventory.Enumerate(dir);
            foreach (var canonical in new[] { "constitution.md", "spec.md", "data-model.md", "plan.md", "tasks.md" }.Where(f => File.Exists(Path.Combine(dir, f))))
                SampleProjectDocumentInventory.FindReadableDocument(inventory, canonical).Should().NotBeNull($"{Path.GetFileName(dir)}/{canonical}");
        }
    }
}
