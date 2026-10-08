using System.Net;
using System.Text;
using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.SampleProjects;
using FluentAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Moq;
using static BirkNext.Web.Tests.Services.SampleArtifactClassifierTests;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Generic Sample Project discovery and role-based resolution: arbitrary folders and filenames, multiple documents per
/// role, optional roles, explicit choice instead of arbitrary picks, caching, and the real SampleData projects.
/// </summary>
public sealed class SampleProjectArtifactDiscoveryTests
{
    [Fact]
    public async Task CanonicalStructure_ResolvesEveryExplorerRole()
    {
        var backend = new FakeBackend().Add("canonical", new()
        {
            ["constitution.md"] = Constitution, ["spec.md"] = Spec, ["data-model.md"] = DataModel,
            ["plan.md"] = Plan, ["tasks.md"] = Tasks, ["README.md"] = "# Canonical\n\nSample.",
        });
        var resolver = backend.Resolver();

        foreach (var type in Enum.GetValues<ExplorerDocumentType>())
        {
            var result = await resolver.ResolveAsync("canonical", type);
            result.IsSuccess.Should().BeTrue(type.ToString());
        }
        (await resolver.ResolveAsync("canonical", ExplorerDocumentType.Tasks)).Filename.Should().Be("tasks.md");
    }

    [Fact]
    public async Task FlatCustomFilenames_AreResolvedByRole()
    {
        var backend = new FakeBackend().Add("flat", new()
        {
            ["requirements.md"] = Spec, ["implementation-plan.md"] = Plan, ["domain-model.md"] = DataModel, ["work-items.md"] = Tasks,
        });
        var resolver = backend.Resolver();

        (await resolver.ResolveAsync("flat", ExplorerDocumentType.Specification)).Filename.Should().Be("requirements.md");
        (await resolver.ResolveAsync("flat", ExplorerDocumentType.Plan)).Filename.Should().Be("implementation-plan.md");
        (await resolver.ResolveAsync("flat", ExplorerDocumentType.DataModel)).Filename.Should().Be("domain-model.md");
        (await resolver.ResolveAsync("flat", ExplorerDocumentType.Tasks)).Filename.Should().Be("work-items.md");
        (await resolver.ResolveAsync("flat", ExplorerDocumentType.Constitution)).IsMissing.Should().BeTrue();
    }

    [Fact]
    public async Task NestedStructure_IsDiscoveredRecursively()
    {
        var backend = new FakeBackend().Add("nested", new()
        {
            ["docs/requirements/school-attendance.md"] = Spec,
            ["docs/architecture/domain.md"] = DataModel,
            ["docs/plans/delivery.md"] = Plan,
            [".specify/memory/constitution.md"] = Constitution,
        });
        var result = await backend.Discovery().DiscoverAsync("nested");

        result!.Role(WorkspaceArtifactType.Specification).Primary!.RelativePath.Should().Be("docs/requirements/school-attendance.md");
        result.Role(WorkspaceArtifactType.DataModel).Primary!.RelativePath.Should().Be("docs/architecture/domain.md");
        result.Role(WorkspaceArtifactType.Plan).Primary!.RelativePath.Should().Be("docs/plans/delivery.md");
        result.Role(WorkspaceArtifactType.Constitution).Primary!.RelativePath.Should().Be(".specify/memory/constitution.md");
    }

    [Fact]
    public async Task MultipleSpecifications_AreAllKept_AndNeedAnExplicitChoice()
    {
        var backend = new FakeBackend().Add("multi", new()
        {
            ["specs/school.md"] = Spec.Replace("School attendance", "School"),
            ["specs/person.md"] = Spec.Replace("School attendance", "Person"),
            ["specs/reporting.md"] = Spec.Replace("School attendance", "Reporting"),
        });
        var discovery = backend.Discovery();
        var resolver = backend.Resolver(discovery);

        var result = await discovery.DiscoverAsync("multi");
        var role = result!.Role(WorkspaceArtifactType.Specification);
        role.State.Should().Be(SampleRoleState.Multiple);
        role.Documents.Select(d => d.RelativePath).Should().Equal("specs/person.md", "specs/reporting.md", "specs/school.md");
        role.Primary.Should().BeNull("several documents are never resolved by picking the first");

        var unresolved = await resolver.ResolveAsync("multi", ExplorerDocumentType.Specification);
        unresolved.RequiresSelection.Should().BeTrue();
        unresolved.IsMissing.Should().BeFalse();
        unresolved.Candidates.Should().HaveCount(3);

        discovery.ChooseDocument("multi", WorkspaceArtifactType.Specification, "specs/reporting.md");
        var chosen = await resolver.ResolveAsync("multi", ExplorerDocumentType.Specification);
        chosen.IsSuccess.Should().BeTrue();
        chosen.Filename.Should().Be("specs/reporting.md");
        chosen.Content.Should().Contain("Feature Specification: Reporting");
    }

    [Fact]
    public async Task SameFilenameInDifferentFolders_DoesNotCollide()
    {
        var backend = new FakeBackend().Add("features", new()
        {
            ["feature-a/spec.md"] = Spec.Replace("School attendance", "Feature A"),
            ["feature-b/spec.md"] = Spec.Replace("School attendance", "Feature B"),
        });
        var result = await backend.Discovery().DiscoverAsync("features");

        result!.Role(WorkspaceArtifactType.Specification).Documents.Select(d => d.RelativePath)
            .Should().Equal("feature-a/spec.md", "feature-b/spec.md");
    }

    [Fact]
    public async Task DuplicateContent_KeepsBothPathsAndFlagsTheCopy()
    {
        var backend = new FakeBackend().Add("dup", new() { ["spec.md"] = Spec, ["archive/spec.md"] = Spec });
        var result = await backend.Discovery().DiscoverAsync("dup");

        var docs = result!.Role(WorkspaceArtifactType.Specification).Documents;
        docs.Should().HaveCount(2);
        docs.Single(d => d.RelativePath == "spec.md").DuplicateOf.Should().Be("archive/spec.md");
        docs.Select(d => d.Fingerprint).Distinct().Should().ContainSingle().Which.Should().Be(ArtifactFingerprint.Compute(Spec));
    }

    [Fact]
    public async Task OnlySpecification_OtherRolesAreNeutralNotFound()
    {
        var backend = new FakeBackend().Add("spec-only", new() { ["docs/feature.md"] = Spec });
        var discovery = backend.Discovery();
        var result = await discovery.DiscoverAsync("spec-only");

        result!.Roles.Where(r => r.Role != WorkspaceArtifactType.Specification).Should().OnlyContain(r => r.State == SampleRoleState.NotFound);
        result.ParseErrors.Should().BeEmpty();
        result.Error.Should().BeNull();
        var plan = await backend.Resolver(discovery).ResolveAsync("spec-only", ExplorerDocumentType.Plan);
        plan.IsMissing.Should().BeTrue();
        plan.ErrorMessage.Should().Be("No Plan document was detected in project 'spec-only'");
    }

    [Fact]
    public async Task AmbiguousDocument_IsListedForReview_AndNotResolved()
    {
        const string mixed = "# Delivery plan and task list\n\n## Implementation approach\n\n## Milestones\n\n## Technical Context\n\n## Tasks for intake\n\n- [ ] T001 a\n- [ ] T002 b\n- [ ] T003 c\n- [ ] T004 d\n- [ ] T005 e\n";
        var backend = new FakeBackend().Add("mixed", new() { ["delivery.md"] = mixed });
        var discovery = backend.Discovery();
        var result = await discovery.DiscoverAsync("mixed");

        result!.NeedsReview.Should().ContainSingle().Which.RelativePath.Should().Be("delivery.md");
        (await backend.Resolver(discovery).ResolveAsync("mixed", ExplorerDocumentType.Plan)).IsMissing.Should().BeTrue();
        (await backend.Resolver(discovery).ResolveAsync("mixed", ExplorerDocumentType.Tasks)).IsMissing.Should().BeTrue();
    }

    [Fact]
    public async Task SourceOnlyProject_HasNoArtifactsAndNoErrors()
    {
        var backend = new FakeBackend().Add("source-only", new(), extraFiles: ["src/Program.cs", "src/App.csproj"]);
        var result = await backend.Discovery().DiscoverAsync("source-only");

        result!.Documents.Should().BeEmpty();
        result.UnsupportedFiles.Should().HaveCount(2);
        result.Roles.Should().OnlyContain(r => r.State == SampleRoleState.NotFound);
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task UnreadableDocument_IsAParseError_DistinctFromNotFound()
    {
        var backend = new FakeBackend().Add("broken", new() { ["spec.md"] = Spec, ["plan.md"] = Plan });
        backend.Unreadable.Add(("broken", "plan.md"));
        backend.BulkAvailable = false;
        var result = await backend.Discovery().DiscoverAsync("broken");

        result!.ParseErrors.Should().ContainSingle().Which.RelativePath.Should().Be("plan.md");
        result.Role(WorkspaceArtifactType.Specification).State.Should().Be(SampleRoleState.Detected);
    }

    [Fact]
    public async Task Discovery_IsCachedUntilTheInventoryChanges()
    {
        var backend = new FakeBackend().Add("cache", new() { ["spec.md"] = Spec });
        var discovery = backend.Discovery();

        await discovery.DiscoverAsync("cache");
        discovery.Invalidate();
        await discovery.DiscoverAsync("cache");
        backend.DocumentRequests.Should().Be(1, "an unchanged inventory reuses the classification");

        backend.Add("cache", new() { ["spec.md"] = Spec, ["plan.md"] = Plan });
        discovery.Invalidate();
        var changed = await discovery.DiscoverAsync("cache");
        backend.DocumentRequests.Should().Be(2);
        changed!.Role(WorkspaceArtifactType.Plan).State.Should().Be(SampleRoleState.Detected);
    }

    [Fact]
    public async Task SwitchingProjects_NeverReturnsThePreviousProjectsDocuments()
    {
        var backend = new FakeBackend()
            .Add("a", new() { ["spec.md"] = Spec.Replace("School attendance", "Project A") })
            .Add("b", new() { ["docs/requirements.md"] = Spec.Replace("School attendance", "Project B") });
        var resolver = backend.Resolver();

        (await resolver.ResolveAsync("a", ExplorerDocumentType.Specification)).Content.Should().Contain("Project A");
        var b = await resolver.ResolveAsync("b", ExplorerDocumentType.Specification);
        b.Content.Should().Contain("Project B").And.NotContain("Project A");
        (await resolver.ResolveAsync("b", ExplorerDocumentType.Plan)).IsMissing.Should().BeTrue();
    }

    [Fact]
    public async Task UnknownProject_IsInvalid()
    {
        var result = await new FakeBackend().Resolver().ResolveAsync("nope", ExplorerDocumentType.Specification);

        result.IsSuccess.Should().BeFalse();
        result.ProjectSlug.Should().BeNull();
    }

    // ── Real SampleData projects (regression fixtures; no project-specific code) ─────────────────────────────────

        [Theory]
    [InlineData("project-a")]
    [InlineData("project-b")]
    public async Task NestedProjectFixtures_FindConstitutionAndSpecification(string slug)
    {
        var backend = new FakeBackend().Add(slug, new Dictionary<string, string>
        {
            [".specify/memory/constitution.md"] = "# Fixture Constitution",
            ["specs/001-feature/spec.md"] = "# Fixture Specification",
            ["specs/001-feature/checklists/requirements.md"] = "# Requirements",
        });
        var result = await backend.Discovery().DiscoverAsync(slug);
        result!.Role(WorkspaceArtifactType.Constitution).State.Should().Be(SampleRoleState.Detected);
        result.Role(WorkspaceArtifactType.Constitution).Primary!.RelativePath.Should().EndWith(".specify/memory/constitution.md");
        result.Role(WorkspaceArtifactType.Specification).Primary!.RelativePath.Should().MatchRegex(@"(^|/)specs/001-[^/]+/spec\.md$");
        result.Unclassified.Should().ContainSingle(d => d.RelativePath.EndsWith("checklists/requirements.md"));
        result.NeedsReview.Should().BeEmpty();
        result.Roles.Where(r => r.Role is WorkspaceArtifactType.Plan or WorkspaceArtifactType.Tasks or WorkspaceArtifactType.DataModel)
            .Should().OnlyContain(r => r.State == SampleRoleState.NotFound);
    }

    [Fact]
    public async Task ProjectFixtures_ResolveTheirCanonicalFiles()
    {
        var docs = new Dictionary<string, string> { ["constitution.md"] = Constitution, ["spec.md"] = Spec,
            ["data-model.md"] = "# Data Model\n\n## Entities\n", ["plan.md"] = Plan, ["tasks.md"] = Tasks };
        var result = await new FakeBackend().Add("fixture", docs).Discovery().DiscoverAsync("fixture");
        foreach (var (file, role) in new[] { ("constitution.md", WorkspaceArtifactType.Constitution), ("spec.md", WorkspaceArtifactType.Specification),
                     ("data-model.md", WorkspaceArtifactType.DataModel), ("plan.md", WorkspaceArtifactType.Plan), ("tasks.md", WorkspaceArtifactType.Tasks) })
        {
            result!.Role(role).State.Should().Be(SampleRoleState.Detected, file);
            result.Role(role).Primary!.RelativePath.Should().Be(file);
            result.Role(role).Primary.Confidence.Should().Be(ArtifactConfidence.Confirmed, file);
        }
        result!.NeedsReview.Should().BeEmpty();
    }
[Fact]
    public async Task LargeProject_ClassifiesHundredDocumentsQuickly()
    {
        var docs = new Dictionary<string, string>();
        for (var i = 0; i < 100; i++)
            docs[$"area-{i % 10}/doc-{i:D3}.md"] = (i % 4) switch { 0 => Spec, 1 => Plan, 2 => Tasks, _ => "# Notes\n\nPlain text." };
        var backend = new FakeBackend().Add("large", docs, extraFiles: Enumerable.Range(0, 900).Select(i => $"src/f{i}.cs").ToArray());

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await backend.Discovery().DiscoverAsync("large");
        watch.Stop();

        result!.Documents.Should().HaveCount(100);
        result.Role(WorkspaceArtifactType.Specification).Documents.Should().HaveCount(25);
        result.UnsupportedFiles.Should().HaveCount(900);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    /// <summary>In-memory stand-in for the backend's inventory, bulk document and single file endpoints.</summary>
    internal sealed class FakeBackend : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly Dictionary<string, (Dictionary<string, string> Docs, string[] Extra)> _projects = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<(string Slug, string Path)> Unreadable { get; } = [];
        public bool BulkAvailable { get; set; } = true;
        public int DocumentRequests { get; private set; }

        public FakeBackend Add(string slug, Dictionary<string, string> docs, string[]? extraFiles = null)
        {
            _projects[slug] = (docs, extraFiles ?? []);
            return this;
        }

        public SampleProjectsApiService Api() => new(new HttpClient(this) { BaseAddress = new Uri("http://localhost/") });
        public SampleProjectArtifactDiscoveryService Discovery() => new(Api());
        public SampleProjectDocumentResolver Resolver(ISampleProjectArtifactDiscovery? discovery = null) =>
            new(Api(), Mock.Of<IWorkspaceSessionService>(), discovery ?? Discovery());

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            if (path == "api/sample-projects")
                return Ok(JsonSerializer.Serialize(_projects.Select(p => Dto(p.Key, p.Value.Docs, p.Value.Extra)).ToList(), Json), "application/json");

            var parts = path.Split('/');
            if (parts.Length == 4 && _projects.TryGetValue(Uri.UnescapeDataString(parts[2]), out var project))
            {
                var slug = Uri.UnescapeDataString(parts[2]);
                if (parts[3] == "documents" && BulkAvailable)
                {
                    DocumentRequests++;
                    var docs = project.Docs.OrderBy(d => d.Key, StringComparer.Ordinal)
                        .Select(d => new SampleDocumentContentDto(d.Key, Unreadable.Contains((slug, d.Key)) ? null : d.Value, null)).ToList();
                    return Ok(JsonSerializer.Serialize(docs, Json), "application/json");
                }
                if (parts[3] == "file")
                {
                    var file = QueryHelpers.ParseQuery(request.RequestUri.Query)["filename"].ToString();
                    if (project.Docs.TryGetValue(file, out var text) && !Unreadable.Contains((slug, file))) return Ok(text, "text/plain");
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static SampleProjectDto Dto(string slug, Dictionary<string, string> docs, string[] extra) => new(
            slug, slug, "", "", $"/SampleData/{slug}", false,
            docs.Select(d => new SampleFileDto(d.Key[(d.Key.LastIndexOf('/') + 1)..], true, null, null, null, true, false, d.Key,
                    Encoding.UTF8.GetByteCount(d.Value), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)))
                .Concat(extra.Select(e => new SampleFileDto(e[(e.LastIndexOf('/') + 1)..], true, null, null, null, false, false, e, 10)))
                .ToList(),
            new SampleDiscoveryStatsDto(docs.Count + extra.Length, docs.Count, [], 0, false));

        private static Task<HttpResponseMessage> Ok(string body, string mediaType) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
    }
}


