using BirkNext.Dependencies;
using BirkNext.SourceEvidence;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Direct NuGet usage per project from stored declarations, over a generic (non-M2LB) .NET fixture: a primary "Orders.Common" repository with
/// central package management and an explicitly selected related "Orders.Api" source. Usage is always qualified by the selected scope.
/// </summary>
public sealed class DependencyUsagePresentationTests
{
    private static DeclaredDependency Pkg(string repo, string id, string? version, string owner, params string[] referencedBy) => new()
    {
        Repository = repo, Manager = "nuget", Datasource = "nuget", DepType = "nuget", PackageName = id, CurrentValue = version, OwnerFile = owner, ReferencedBy = [.. referencedBy],
    };

    private static SourceScopeEntry Entry(string repository) => new() { SnapshotId = Guid.NewGuid(), Repository = repository, ArchiveName = $"{repository}.zip" };

    private static DependencyReviewResult Result(bool withRelated)
    {
        var primary = new RepositoryDependencyReview
        {
            Repository = "Orders.Common",
            Dependencies =
            [
                Pkg("Orders.Common", "Polly", "8.2.0", "Directory.Packages.props", "src/Orders.Common/Orders.Common.csproj"),
                Pkg("Orders.Common", "xunit", "2.9.0", "Directory.Packages.props", "tests/Orders.Common.Tests/Orders.Common.Tests.csproj"),
                Pkg("Orders.Common", "Unreferenced.Package", "1.0.0", "Directory.Packages.props"),
                Pkg("Orders.Common", "Serilog", "3.1.1", "tests/Orders.Common.Tests/Orders.Common.Tests.csproj", "tests/Orders.Common.Tests/Orders.Common.Tests.csproj"),
                Pkg("Orders.Common", "StyleCop.Analyzers", "1.1.118", "Directory.Build.props", "Directory.Build.props"),
                new DeclaredDependency { Repository = "Orders.Common", Manager = "nuget", DepType = "msbuild-sdk", PackageName = "Microsoft.Build.Traversal", CurrentValue = "4.1.0", OwnerFile = "dirs.proj", ReferencedBy = ["dirs.proj"] },
                new DeclaredDependency { Repository = "Orders.Common", Manager = "dockerfile", PackageName = "mcr.microsoft.com/dotnet/aspnet", CurrentValue = "8.0", OwnerFile = "Dockerfile" },
            ],
        };
        var related = new RepositoryDependencyReview
        {
            Repository = "Orders.Api",
            Dependencies = [Pkg("Orders.Api", "Polly", null, "src/Orders.Api/Orders.Api.csproj", "src/Orders.Api/Orders.Api.csproj")],
        };
        return new DependencyReviewResult
        {
            Repositories = withRelated ? [primary, related] : [primary],
            SourceScope = new ReviewSourceScope { Primary = Entry("Orders.Common"), Related = withRelated ? [Entry("Orders.Api")] : [] },
        };
    }

    private static DependencyUsageRow Row(DependencyUsageView view, string id) => view.Rows.Single(r => r.PackageName == id);

    [Fact]
    public void CentralVersionMapsToTheConsumingProject_WithProjectIdentityFromTheCsproj()
    {
        var view = DependencyUsagePresentation.Build(Result(withRelated: false));

        var polly = Row(view, "Polly");
        polly.State.Should().Be(DependencyUsageState.UsedInCurrent);
        polly.Versions.Should().Equal("8.2.0");
        polly.DeclaredIn.Should().Equal("Directory.Packages.props");
        polly.Projects.Should().ContainSingle().Which.Should().Be(new ProjectPackageUsage("Orders.Common", false, "Orders.Common", "src/Orders.Common/Orders.Common.csproj", "8.2.0"));
        Row(view, "xunit").Projects.Single().Project.Should().Be("Orders.Common.Tests");
        DependencyUsagePresentation.UsedBy(polly).Should().Be("1 project in current source scope");
        view.CurrentProjects.Should().Be(2);
    }

    [Fact]
    public void DirectVersionedReferenceMapsToItsOwnProject()
    {
        var serilog = Row(DependencyUsagePresentation.Build(Result(false)), "Serilog");
        serilog.Projects.Single().Project.Should().Be("Orders.Common.Tests");
        serilog.Versions.Should().Equal("3.1.1");
    }

    [Fact]
    public void NoUsageFoundIsScopedAndNeverUnused_AndPropsReferencesStayUnresolved()
    {
        var view = DependencyUsagePresentation.Build(Result(false));

        var none = Row(view, "Unreferenced.Package");
        none.State.Should().Be(DependencyUsageState.NoUsageFound);
        DependencyUsagePresentation.Label(none.State).Should().Be("No usage found in selected source scope");
        DependencyUsagePresentation.UsedBy(none).Should().NotContain("Unused");
        Row(view, "StyleCop.Analyzers").State.Should().Be(DependencyUsageState.Unresolved, "a Directory.Build.props reference is not expanded to projects");
        Enum.GetValues<DependencyUsageState>().Select(DependencyUsagePresentation.Label).Should().NotContain(l => l.Contains("Unused"));
    }

    [Fact]
    public void SdksToolsAndImagesAreNotProjectUsage()
    {
        var view = DependencyUsagePresentation.Build(Result(false));
        view.Rows.Select(r => r.PackageName).Should().NotContain(["Microsoft.Build.Traversal", "mcr.microsoft.com/dotnet/aspnet"]);
    }

    [Fact]
    public void RelatedSourceUsageIsSeparate_AndItsVersionIsNeverTakenFromThePrimary()
    {
        var view = DependencyUsagePresentation.Build(Result(withRelated: true));

        var polly = Row(view, "Polly");
        polly.State.Should().Be(DependencyUsageState.UsedInCurrentAndRelated);
        polly.Versions.Should().Equal(["8.2.0"], "the version column reflects the primary source's own declaration");
        var api = polly.Projects.Single(p => p.Related);
        (api.Repository, api.Project).Should().Be(("Orders.Api", "Orders.Api"));
        api.Version.Should().BeNull("the related project's version is not resolvable from the primary's Directory.Packages.props");
        DependencyUsagePresentation.Version(api).Should().Be("Not resolved from this source relationship");
        DependencyUsagePresentation.UsedBy(polly).Should().Be("2 projects in selected source scope");
        view.RelatedProjects.Should().Be(1);
        view.HasRelated.Should().BeTrue();
    }

    [Fact]
    public void UnselectedSourcesNeverContribute()
    {
        var view = DependencyUsagePresentation.Build(Result(withRelated: false));
        view.Rows.SelectMany(r => r.Projects).Should().OnlyContain(p => p.Repository == "Orders.Common");
        view.HasRelated.Should().BeFalse();
    }

    [Fact]
    public void FiltersNarrowByUsageSearchAndRenovate()
    {
        var view = DependencyUsagePresentation.Build(Result(withRelated: true));

        view.Rows.Where(r => DependencyUsagePresentation.Matches(r, null, "Related", null)).Select(r => r.PackageName).Should().Equal("Polly");
        view.Rows.Where(r => DependencyUsagePresentation.Matches(r, "Orders.Common.Tests", null, null)).Select(r => r.PackageName).Should().BeEquivalentTo(["Serilog", "xunit"]);
        view.Rows.Where(r => DependencyUsagePresentation.Matches(r, null, nameof(DependencyUsageState.NoUsageFound), "Processed")).Select(r => r.PackageName).Should().Equal("Unreferenced.Package");
    }
}
