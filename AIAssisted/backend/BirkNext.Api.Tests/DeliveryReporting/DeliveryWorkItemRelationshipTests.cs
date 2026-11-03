using System.Text;
using BirkNext.Api.Services.DeliveryReporting;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.DeliveryReporting;
using FluentAssertions;
using Xunit;
using static BirkNext.Api.Tests.DeliveryReporting.DeliveryReportingFixtures;

namespace BirkNext.Api.Tests.DeliveryReporting;

/// <summary>
/// One classifier relates every task to its feature's requirement model. Shared &amp; foundational work, functional groups and scenario groups
/// are normal structures (never "tasks without a user story" defects); only removed-story and unresolved references need review. Shapes mirror
/// the real M2LB archives: AutorisasjonService [US0]–[US8] without spec.md, HendelseService scenarios, TjenesteService's struck-through US3,
/// Frontend's [US6] against US1–US5 — and a title that merely contains the word "Removed".
/// </summary>
public sealed class DeliveryWorkItemRelationshipTests
{
    private const string Header = "# Tasks\n\n## Phase 1: Setup (Shared Infrastructure)\n\n- [x] T001 Create the solution\n- [ ] T002 [P] Configure CI\n\n## Phase 2: Foundational (Blocking Prerequisites)\n\n- [x] T003 Add the database context\n";

    private static DeliverySnapshotContent One(params (string, string)[] files) => Content(Parse(files), Friday);
    private static DeliverySnapshotContent Empty() => Content(Parse([("Other/specs/001-x/spec.md", Spec("X"))]), Monday);

    private static DeliveryFeatureHierarchy Hierarchy(DeliverySnapshotContent current, string folder) =>
        DeliverySnapshotComparison.Compare(Empty(), current).Stories.Single(s => s.FeatureKey.EndsWith(folder, StringComparison.Ordinal)).Hierarchy!;

    private static string UserStorySpec(string title, params string[] stories) =>
        $"# Feature Specification: {title}\n\n## User Scenarios & Testing\n\n" + string.Join("\n", stories.Select(s => $"### {s}\n\nText.\n")) + "\n## Requirements\n\n- **FR-001**: Something.\n";

    [Fact]
    public void Untagged_setup_and_foundational_tasks_are_shared_work_and_explicit_tags_link_to_spec_stories()
    {
        var h = Hierarchy(One(("Svc/specs/001-a/spec.md", UserStorySpec("A", "User Story 1 - List (Priority: P1)", "User Story 2 - Filter (Priority: P2)")),
            ("Svc/specs/001-a/tasks.md", Header + "\n## Phase 3: User Story 1 - List\n\n- [x] T004 [US1] Build the list\n- [ ] T005 Write the list tests\n\n## Phase 4: Polish & Cross-Cutting Concerns\n\n- [ ] T006 Update docs\n")), "001-a");

        h.Tasks.Select(t => (t.TaskId, t.Relationship)).Should().Equal(
            ("T001", DeliveryTaskRelationship.SharedFoundational), ("T002", DeliveryTaskRelationship.SharedFoundational), ("T003", DeliveryTaskRelationship.SharedFoundational),
            ("T004", DeliveryTaskRelationship.ExplicitStory), ("T005", DeliveryTaskRelationship.StoryPhase), ("T006", DeliveryTaskRelationship.SharedFoundational));
        h.RequirementModel.Should().Be("User stories");
        h.Groups.Single(g => g.Kind == DeliveryTaskRelationship.SharedFoundational).Should().Match<DeliveryWorkGroupRow>(g => g.Title == "Shared & Foundational Tasks" && g.TaskTotal == 4 && !g.NeedsReview);
    }

    [Fact]
    public void Tags_without_spec_stories_form_functional_groups_and_are_never_mapped_to_detailed_requirement_ids()
    {
        // AutorisasjonService/001: no spec.md, [US0]..[US8] in tasks.md, detailed US-1.1 ids only in checklists/.
        var current = One(("Auth/specs/001-auth/tasks.md", Header + "\n## Phase 3: Core\n\n- [x] T010 [US0] Bootstrap the service\n- [x] T011 [US1] Validate the token\n- [ ] T012 [US1] Map the claims\n"),
            ("Auth/specs/001-auth/checklists/auth-func-requirements.md", "# Requirements\n\n- US-1.1 Token validation\n- US-1.2 Claims mapping\n"));
        var h = Hierarchy(current, "001-auth");

        h.RequirementModel.Should().Be("Functional groups");
        h.Tasks.Where(t => t.TaskId is "T010" or "T011" or "T012").Should().OnlyContain(t => t.Relationship == DeliveryTaskRelationship.FunctionalGroup && t.UnresolvedStoryReference == null);
        var us1 = h.Groups.Single(g => g.Key == "US1");
        us1.Should().Match<DeliveryWorkGroupRow>(g => g.Title == "US1 — Functional group" && g.TaskTotal == 2 && g.Confidence == DeliveryMappingConfidence.GroupLevel && !g.NeedsReview);
        us1.Explanation.Should().Contain("no matching User Story in spec.md");
        DeliverySnapshotComparison.Compare(Empty(), current).Attention.Should().NotContain(a => a.Kind == DeliveryAttentionKind.BrokenTraceability, "a functional group is not broken traceability");
    }

    [Fact]
    public void Scenario_specs_get_scenario_groups_linked_only_by_phase_title_evidence_never_by_equal_numbers()
    {
        // HendelseService/001: spec "Scenario 1..4"; tasks "Phase N: User Story M — name" where M does not equal the scenario number.
        var spec = "# Feature Specification: Hendelser\n\n## User Scenarios & Testing\n\n### Scenario 1: Saksbehandler ser hendelsestidslinje for et barn\n\nText.\n\n" +
                   "### Scenario 2: Saksbehandler åpner detaljert hendelse\n\nText.\n\n### Scenario 4: Hendelsesdata innmating fra BiRK\n\nText.\n";
        var tasks = Header + "\n## Phase 3: User Story 3 — Hendelsesinnmating (Priority: P1)\n\n- [x] T010 [US3] Ingest events\n\n" +
                    "## Phase 4: User Story 1 — Hendelsestidslinje (Priority: P2)\n\n- [x] T011 [US1] Build the timeline\n\n## Phase 5: User Story 2 — Hendelsesdetalj med tilgangsstyring\n\n- [ ] T012 [US2] Detail view\n";
        var current = One(("Hendelse/specs/001-h/spec.md", spec), ("Hendelse/specs/001-h/tasks.md", tasks));
        var h = Hierarchy(current, "001-h");

        h.RequirementModel.Should().Be("Scenarios");
        h.Stories.Should().OnlyContain(s => s.Kind == DeliveryWorkItemRelationships.ScenarioKind);
        h.Tasks.Where(t => t.TaskId is "T010" or "T011" or "T012").Should().OnlyContain(t => t.Relationship == DeliveryTaskRelationship.ScenarioGroup);
        var groups = h.Groups.Where(g => g.Kind == DeliveryTaskRelationship.ScenarioGroup).ToDictionary(g => g.Key);
        groups["US1"].LinkedKey.Should().Be("S1", "the phase name \"Hendelsestidslinje\" appears in Scenario 1's title");
        groups["US2"].LinkedKey.Should().BeNull("\"Hendelsesdetalj med tilgangsstyring\" appears in no scenario title — the equal number 2 is not evidence");
        groups["US3"].LinkedKey.Should().BeNull("US3 is not Scenario 3 (there is none) and its name is not in Scenario 4's title verbatim");
        DeliverySnapshotComparison.Compare(Empty(), current).Attention.Should().NotContain(a => a.Kind == DeliveryAttentionKind.BrokenTraceability);
    }

    [Fact]
    public void A_struck_through_story_is_removed_and_its_tasks_reference_a_removed_story_but_a_title_containing_removed_is_not()
    {
        var spec = UserStorySpec("Placements", "User Story 1 - List the children (Priority: P1)", "~~User Story 3 - Dates as recorded (Priority: P2)~~ Removed 2026-10-27",
            "User Story 2 — User Deactivated When Removed from Entra Scope or Disabled (Priority: P1)");
        var current = One(("Tjeneste/specs/004-p/spec.md", spec),
            ("Tjeneste/specs/004-p/tasks.md", Header + "\n## Phase 3: Work\n\n- [x] T010 [US3] Keep recorded dates\n- [x] T011 [US2] Deactivate users\n- [ ] T012 [US1] List children\n"));
        var h = Hierarchy(current, "004-p");

        h.Stories.Single(s => s.Key == "US3").Removed.Should().BeTrue();
        h.Stories.Single(s => s.Key == "US2").Removed.Should().BeFalse("\"Removed from Entra\" is part of the title, not a removal marker");
        h.Tasks.Single(t => t.TaskId == "T010").Relationship.Should().Be(DeliveryTaskRelationship.RemovedStoryReference);
        h.Tasks.Single(t => t.TaskId == "T011").Relationship.Should().Be(DeliveryTaskRelationship.ExplicitStory);
        h.Groups.Single(g => g.Kind == DeliveryTaskRelationship.RemovedStoryReference).Should().Match<DeliveryWorkGroupRow>(g => g.Title == "US3 — References removed story" && g.NeedsReview);
        DeliverySnapshotComparison.Compare(Empty(), current).Attention.Should().ContainSingle(a => a.Kind == DeliveryAttentionKind.RemovedStoryReference);
    }

    [Fact]
    public void A_tag_naming_a_story_the_spec_does_not_define_stays_unresolved_and_visible()
    {
        var current = One(("Frontend/specs/003-polish/spec.md", UserStorySpec("Polish", "User Story 1 - Landing", "User Story 5 - Skip link")),
            ("Frontend/specs/003-polish/tasks.md", Header + "\n## Phase 8: Dev Access Profile Switcher\n\n- [ ] T030 [US6] Add the switcher\n"));
        var h = Hierarchy(current, "003-polish");

        var task = h.Tasks.Single(t => t.TaskId == "T030");
        (task.Relationship, task.UnresolvedStoryReference).Should().Be((DeliveryTaskRelationship.UnresolvedReference, "US6"));
        task.RelationshipNote.Should().Contain("names no story in spec.md").And.Contain("US1, US5");
        h.Stories.Should().NotContain(s => s.Key == "US6", "no fictional story is created");
        DeliverySnapshotComparison.Compare(Empty(), current).Attention.Should().ContainSingle(a => a.Kind == DeliveryAttentionKind.BrokenTraceability && a.Detail.Contains("US6"));
    }

    [Fact]
    public void Report_metrics_count_each_relationship_once_and_shared_work_is_inventory_not_a_quality_gap()
    {
        var current = One(("Svc/specs/001-a/spec.md", UserStorySpec("A", "User Story 1 - List")),
            ("Svc/specs/001-a/tasks.md", Header + "\n## Phase 3: User Story 1 - List\n\n- [x] T004 [US1] Build\n- [ ] T005 [US9] Unknown\n"));
        var report = WeeklyReportBuilder.Build(Guid.NewGuid(), Guid.NewGuid(), "M2LB", Friday, Empty(), current, DeliverySnapshotComparison.Compare(Empty(), current), TimeZoneInfo.Utc);
        int M(string key) => report.Metrics.Single(m => m.Key == key).Value;

        (M("tasks-explicit-story"), M("tasks-shared"), M("quality-unresolved-references"), M("quality-removed-story-references")).Should().Be((1, 3, 1, 0));
        report.Metrics.Should().NotContain(m => m.Key == "quality-unassigned-tasks", "the old single bucket mixed shared work with broken references");
        report.Metrics.Single(m => m.Key == "tasks-shared").Group.Should().Be(DeliveryMetricGroups.Inventory);
    }

    [Theory]
    [InlineData("valid", "UTF-8", false)]
    [InlineData("legacy", "Windows-1252", false)]
    [InlineData("mojibake", "UTF-8", true)]
    public void Source_text_is_decoded_safely_and_existing_mojibake_is_preserved_and_flagged(string kind, string encoding, bool suspect)
    {
        var bytes = kind switch
        {
            "valid" => Encoding.UTF8.GetBytes("Løsning for barn – æøå ÆØÅ"),
            "legacy" => CodePages().GetBytes("Løsning for barn – æøå ÆØÅ"),
            _ => Encoding.UTF8.GetBytes("kjÃ¸retid â€” fra kilden"),
        };
        var (text, used, mojibake) = SourceTextDecoder.Decode(bytes);
        (used, mojibake).Should().Be((encoding, suspect));
        if (kind != "mojibake") text.Should().Be("Løsning for barn – æøå ÆØÅ").And.NotContain("�");
        else text.Should().Be("kjÃ¸retid â€” fra kilden", "the source's own damage is never rewritten");
    }

    [Fact]
    public void A_legacy_encoded_spec_is_read_with_its_Norwegian_letters_and_reported()
    {
        var spec = CodePages().GetBytes(UserStorySpec("Løsning", "User Story 1 - Søk etter barn"));
        var parsed = DeliverySnapshotParser.Parse("m2lb.zip", ZipBytes([("Svc/specs/001-a/spec.md", spec), ("Svc/specs/001-a/tasks.md", Encoding.UTF8.GetBytes(Header))])).Snapshot!;
        parsed.Features.Single().Title.Should().Be("Løsning");
        parsed.Features.Single().Stories.Single().Title.Should().Be("Søk etter barn");
        parsed.Warnings.Should().ContainSingle(w => w.Code == "ENCODING_LEGACY_DECODED" && w.Path!.EndsWith("spec.md"));
    }

    private static Encoding CodePages()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    private static byte[] ZipBytes(IEnumerable<(string Path, byte[] Bytes)> files)
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, bytes) in files)
            {
                var entry = zip.CreateEntry(path);
                entry.LastWriteTime = EntryTime;
                using var s = entry.Open();
                s.Write(bytes);
            }
        return stream.ToArray();
    }
}
