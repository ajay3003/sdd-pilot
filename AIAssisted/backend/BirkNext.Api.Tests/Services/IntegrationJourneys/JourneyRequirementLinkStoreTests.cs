using BirkNext.Api.Data.IntegrationJourneys;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Integrations;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BirkNext.Api.Tests.Services.IntegrationJourneys;

/// <summary>
/// Requirement ↔ journey links: a requirement of the project's own specification catalog linked to the project's own current journey, with the
/// journey revision kept and an append-only history; another project's journey or catalog, an unknown requirement, a duplicate and a stale
/// change are refused; nothing is deleted.
/// </summary>
public sealed class JourneyRequirementLinkStoreTests
{
    private const string Project = "import:lendo", Other = "import:other";
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly string _database = $"journey-requirements-{Guid.NewGuid():N}";

    private IntegrationJourneysDbContext Db() => new(new DbContextOptionsBuilder<IntegrationJourneysDbContext>().UseInMemoryDatabase(_database).Options);
    private JourneyRequirementLinkStore Links() => new(Db());

    private static ProjectRequirementCatalog Catalog(string project = Project) => new()
    {
        ProjectKey = project,
        Requirements = [new("FR-001", "The system MUST register a loan.", "specs/001/spec.md"), new("FR-002", "The system MUST email a receipt.", "specs/001/spec.md")],
    };

    private async Task<string> Journey(string project = Project, string name = "Loan flow")
    {
        var write = await new DeclaredIntegrationJourneyStore(Db()).DeclareAsync(new DeclaredIntegrationJourneyRequest
            { ProjectKey = project, DisplayName = name, Components = ["Web", "Loans.Api", "loan-events"], DeclaredBy = "Kari" }, Now);
        return write.Journey!.JourneyId;
    }

    [Fact]
    public async Task A_requirement_of_the_projects_specification_is_linked_to_its_journey_with_revision_trust_and_history()
    {
        var journey = await Journey();
        var write = await Links().LinkAsync(Project, journey, new("FR-001", "specs/001/spec.md", Catalog(), "Kari"), "local-user", Now, default);

        write.Error.Should().BeNull();
        write.Link.Should().Match<JourneyRequirementLink>(l => l.Status == JourneyRequirementStatus.Confirmed && l.JourneyRevision == 1 && l.Title == "The system MUST register a loan."
            && l.LinkedBy == "Kari" && l.Trust == JourneyRequirementRules.BrowserCatalogTrust && l.History.Count == 1 && l.Version == 1);
        (await Links().ListAsync(Project, journey, default)).Should().ContainSingle(l => l.RequirementId == "FR-001");
        (await Links().ListAsync(Other, null, default)).Should().BeEmpty("another project sees none of these links");
    }

    [Fact]
    public async Task Another_projects_journey_or_catalog_an_unknown_requirement_and_a_duplicate_are_refused()
    {
        var journey = await Journey();
        var foreign = await Journey(Other, "Other flow");

        (await Links().LinkAsync(Project, foreign, new("FR-001", "specs/001/spec.md", Catalog(), null), "u", Now, default)).Status.Should().Be(404, "the journey is another project's");
        (await Links().LinkAsync(Project, journey, new("FR-001", "specs/001/spec.md", Catalog(Other), null), "u", Now, default)).Status.Should().Be(400, "another project's catalog");
        (await Links().LinkAsync(Project, journey, new("FR-999", "specs/001/spec.md", Catalog(), null), "u", Now, default)).Error.Should().Contain("not a requirement");
        (await Links().LinkAsync(Project, journey, new("FR-001", "other/spec.md", Catalog(), null), "u", Now, default)).Status.Should().Be(400, "the same id in another specification is another requirement");
        (await Links().LinkAsync(Project, journey, new("FR-001", "specs/001/spec.md", Catalog(), null), "u", Now, default)).Status.Should().Be(200);
        (await Links().LinkAsync(Project, journey, new("FR-001", "specs/001/spec.md", Catalog(), null), "u", Now, default)).Status.Should().Be(409, "already linked");
    }

    [Fact]
    public async Task A_proposal_is_confirmed_or_rejected_a_link_removed_and_relinked_with_history_and_stale_changes_refused()
    {
        var journey = await Journey();
        var proposed = (await Links().LinkAsync(Project, journey, new("FR-002", "specs/001/spec.md", Catalog(), "Auto", Proposed: true), "u", Now, default)).Link!;
        proposed.Status.Should().Be(JourneyRequirementStatus.Proposed);

        var bad = await Links().SetStatusAsync(Project, journey, proposed.Id, new(proposed.Version, JourneyRequirementStatus.Removed, null, "Kari"), "u", Now, default);
        bad.Status.Should().Be(400, "a proposal is confirmed or rejected, not removed");
        var confirmed = (await Links().SetStatusAsync(Project, journey, proposed.Id, new(proposed.Version, JourneyRequirementStatus.Confirmed, "Covers the receipt step", "Kari"), "u", Now, default)).Link!;
        (await Links().SetStatusAsync(Project, journey, proposed.Id, new(proposed.Version, JourneyRequirementStatus.Removed, null, "Ola"), "u", Now, default)).Status
            .Should().Be(409, "the change names a version someone else already changed");
        var removed = (await Links().SetStatusAsync(Project, journey, proposed.Id, new(confirmed.Version, JourneyRequirementStatus.Removed, "Wrong journey", "Ola"), "u", Now, default)).Link!;
        var again = (await Links().LinkAsync(Project, journey, new("FR-002", "specs/001/spec.md", Catalog(), "Kari"), "u", Now, default)).Link!;

        again.Status.Should().Be(JourneyRequirementStatus.Confirmed);
        again.Id.Should().Be(proposed.Id, "one identity per journey and requirement; nothing is deleted");
        again.History.Select(h => h.To).Should().Equal(JourneyRequirementStatus.Proposed, JourneyRequirementStatus.Confirmed, JourneyRequirementStatus.Removed, JourneyRequirementStatus.Confirmed);
        removed.History[2].Note.Should().Be("Wrong journey");
        (await Links().SetStatusAsync(Other, journey, proposed.Id, new(again.Version, JourneyRequirementStatus.Removed, null, null), "u", Now, default)).Status.Should().Be(404, "another project cannot change it");
    }

    [Fact]
    public void Only_meaningful_follow_ups_are_allowed()
    {
        JourneyRequirementRules.CanMove(JourneyRequirementStatus.Proposed, JourneyRequirementStatus.Confirmed).Should().BeTrue();
        JourneyRequirementRules.CanMove(JourneyRequirementStatus.Confirmed, JourneyRequirementStatus.Rejected).Should().BeFalse("a confirmed link is removed, not rejected");
        JourneyRequirementRules.CanMove(JourneyRequirementStatus.Removed, JourneyRequirementStatus.Confirmed).Should().BeTrue();
        JourneyRequirementRules.CanMove(JourneyRequirementStatus.Confirmed, JourneyRequirementStatus.Confirmed).Should().BeFalse();
    }
}
