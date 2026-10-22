using BirkNext.Integrations;
using BirkNext.TestEvidence;
using BirkNext.Web.Components.SourceDomains;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Components;

/// <summary>Source Analysis → Tests reads the snapshot's canonical tests: explicit vs candidate requirement links apart, unsupported
/// frameworks shown as a limitation (never zero tests), and a snapshot without discovery is "not assessed".</summary>
public sealed class TestEvidenceUiTests : BunitContext
{
    private static IqrSourceSnapshot Snapshot(SourceTestInventory? inventory) => new()
    {
        Id = Guid.NewGuid(), Archive = new SourceArchive("orders.zip", new string('a', 64), 1), AnalyzedAt = DateTimeOffset.Parse("2026-10-22T08:00:00Z"), TestInventory = inventory,
    };

    [Fact]
    public void Tests_view_separates_explicit_and_candidate_links_and_reports_unsupported_frameworks_as_limitations()
    {
        var inventory = new SourceTestInventory
        {
            SnapshotId = Guid.NewGuid(), Status = SourceTestDiscoveryStatus.Partial,
            Projects =
            [
                new SourceTestProject { Name = "Orders.UnitTests", Path = "tests/Orders.UnitTests/Orders.UnitTests.csproj", Framework = "xUnit", DiscoverySupported = true, DefinitionCount = 2 },
                new SourceTestProject { Name = "Legacy.Tests", Path = "tests/Legacy.Tests/Legacy.Tests.csproj", Framework = "NUnit", DiscoverySupported = false },
            ],
            Definitions =
            [
                new SourceTestDefinition { TestDefinitionId = "a", StableIdentity = "r::Orders.UnitTests::A.One", Project = "Orders.UnitTests", ClassName = "A", MethodName = "One",
                    References = [new("FR-1", TestReferenceKind.Requirement, TestEvidenceConfidence.Confirmed, "Trait", 1)] },
                new SourceTestDefinition { TestDefinitionId = "b", StableIdentity = "r::Orders.UnitTests::A.Two", Project = "Orders.UnitTests", ClassName = "A", MethodName = "Two",
                    References = [new("FR-2", TestReferenceKind.Requirement, TestEvidenceConfidence.Inferred, "Comment", 2)] },
            ],
        };
        var cut = Render<TestEvidenceWorkspace>(p => p.Add(x => x.Snapshot, Snapshot(inventory)));
        cut.Find("[data-testid=sd-tests-overview]").TextContent.Should().Contain("Tests discovered2").And.Contain("Test projects2");
        (cut.Find("[data-testid=sd-tests-explicit]").TextContent, cut.Find("[data-testid=sd-tests-candidates]").TextContent).Should().Be(("1", "1"));
        cut.FindAll("[data-testid=sd-tests-project]")[1].TextContent.Should().Contain("Not discovered").And.Contain("results can still be imported");
        cut.Find("[data-testid=sd-tests-limitations]").TextContent.Should().Contain("Legacy.Tests").And.Contain("not zero tests");
        cut.Find("[data-testid=sd-tests-review]").GetAttribute("href").Should().Be("test-coverage-review");

        Render<TestEvidenceWorkspace>(p => p.Add(x => x.Snapshot, Snapshot(null))).Find("[data-testid=sd-tests-not-assessed]").TextContent.Should().Contain("not assessed (not zero)");
    }
}
