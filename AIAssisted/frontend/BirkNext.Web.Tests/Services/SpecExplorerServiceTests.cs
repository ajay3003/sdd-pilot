using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Regression tests for SpecExplorerService.Parse() — each test verifies that
/// one class of spec content is parsed into exactly the right node type with
/// correct content accumulation, as specified in the personSpec.md acceptance criteria.
/// </summary>
public sealed class SpecExplorerServiceTests
{
    // =========================================================================
    // T1 — FR block: multi-line requirement with bullet sub-items
    // =========================================================================

    [Fact]
    public void FR_block_with_wrapped_lines_and_bullet_sub_items_becomes_one_requirement()
    {
        // personSpec.md FR-002: first line + narrative continuation + two bullet sub-items
        const string md = """
            # Spec

            ## Requirements

            ### Functional Requirements

            **FR-002**: All search results MUST be filtered through authorisation control before
            returning to the user, according to the security level model:
            - Levels 0 and 1: requires general `Person:SøkBarn` for the child's org unit
            - Kode 6 / Kode 7: requires child-specific `Person:SeGradertBarn` for that child

            """;

        var tree = SpecExplorerService.Parse(md);

        var reqs = AllDescendants(tree).Where(n => n.NodeType == SpecNodeType.Requirement).ToList();
        reqs.Should().HaveCount(1, "FR-002 is one logical block even though it spans 4 lines");

        var fr = reqs[0];
        fr.SpecItemId.Should().Be("FR-002");
        fr.FullContent.Should().Contain("authorisation control");
        fr.FullContent.Should().Contain("Levels 0 and 1");
        fr.FullContent.Should().Contain("Kode 6 / Kode 7");
    }

    // =========================================================================
    // T2 — SC block: multi-line success criterion
    // =========================================================================

    [Fact]
    public void SC_block_with_wrapped_lines_becomes_one_success_criterion()
    {
        // personSpec.md SC-002: three lines of content
        const string md = """
            # Spec

            ## Success Criteria

            ### Measurable Outcomes

            **SC-002**: Search results are returned within **p95 < 2 seconds** for large datasets
            (thousands of children per org unit). This SLA applies to authorised queries through
            the GraphQL search endpoint under expected load conditions.

            """;

        var tree = SpecExplorerService.Parse(md);

        var scs = AllDescendants(tree).Where(n => n.NodeType == SpecNodeType.SuccessCriterion).ToList();
        scs.Should().HaveCount(1, "SC-002 is one logical block across 3 lines");

        var sc = scs[0];
        sc.SpecItemId.Should().Be("SC-002");
        sc.FullContent.Should().Contain("p95 < 2 seconds");
        sc.FullContent.Should().Contain("GraphQL search endpoint");
    }

    // =========================================================================
    // T3 — Numbered BDD: Given/When/Then across indented lines
    // =========================================================================

    [Fact]
    public void Numbered_BDD_scenarios_are_each_one_BddScenario_node()
    {
        // personSpec.md User Story 1 acceptance scenarios 1-2
        const string md = """
            # Spec

            ## User Story 1: Child Search

            **Acceptance Scenarios**:

            1. **Given** a caseworker with `Person:SøkBarn` for an org unit, **When** they search by
               partial name, **Then** they receive a paginated summary list containing only children
               in that org unit at security level 0 or 1, within acceptable response time.
            2. **Given** a caseworker with `Person:SeGradertBarn` for a specific Kode 6 child,
               **When** they search by that child's name, **Then** the child appears in results with
               a clear address-protection flag indicating the address must not be disclosed.

            """;

        var tree = SpecExplorerService.Parse(md);

        var bdds = AllDescendants(tree).Where(n => n.NodeType == SpecNodeType.BddScenario).ToList();
        bdds.Should().HaveCount(2, "two numbered Given/When/Then blocks → two BddScenario nodes");

        bdds[0].BddGiven.Should().NotBeNullOrEmpty();
        bdds[0].BddWhen.Should().NotBeNullOrEmpty();
        bdds[0].BddThen.Should().NotBeNullOrEmpty();
        bdds[0].BddGiven.Should().Contain("SøkBarn");

        bdds[1].BddGiven.Should().Contain("SeGradertBarn");
        bdds[1].BddThen.Should().Contain("address-protection flag");
    }

    // =========================================================================
    // T4 — Q/A parsing: inline format produces QaPair, no phantom requirements
    // =========================================================================

    [Fact]
    public void Inline_QA_lines_produce_QaPair_nodes_and_no_phantom_requirements()
    {
        // personSpec.md lines 12-13: two inline Q/A items; line 13 references FR-029, US1-US5
        const string md = """
            # Spec

            ## Clarifications

            ### Session 2026-03-06

            - Q: What defines the valid BarnStatusType state values? → A: BiRK is authoritative; the Person module accepts any BarnStatusType value delivered by BiRK verbatim.
            - Q: Which user stories are served via GraphQL vs REST? → A: GraphQL covers all presentation-layer queries (US1 search, US2 profile, US3 access management display, US4 reference data); REST is used exclusively for ingestion (US5) and operation registration at startup (FR-029).

            """;

        var tree = SpecExplorerService.Parse(md);

        var pairs = AllDescendants(tree).Where(n => n.NodeType == SpecNodeType.QaPair).ToList();
        pairs.Should().HaveCount(2, "two Q/A lines → two QaPair nodes");

        pairs[0].QuestionText.Should().Contain("BarnStatusType");
        pairs[0].AnswerText.Should().Contain("BiRK is authoritative");

        pairs[1].QuestionText.Should().Contain("GraphQL vs REST");
        pairs[1].AnswerText.Should().Contain("FR-029");

        // The line mentioning FR-029, US1, US2, US3, US4, US5 must not create phantom nodes
        AllDescendants(tree)
            .Where(n => n.NodeType is SpecNodeType.Requirement or SpecNodeType.UserStory)
            .Should().BeEmpty("spec-item references inside Q/A prose must not create phantom nodes");
    }

    // =========================================================================
    // T5 — Entity parsing: bold-name definition with multi-line description
    // =========================================================================

    [Fact]
    public void Entity_definition_with_multiline_description_becomes_one_Entity_node()
    {
        // personSpec.md Person entity — 7-line description
        const string md = """
            # Spec

            ## Domain Model

            ### Key Entities

            - **Person**: Any individual relevant to child welfare work. Identified by UUID v4.
              Optionally carries national ID (fødselsnummer) and/or DUF number; both may coexist
              as the DUF number is retained as a historical secondary identifier after a
              fødselsnummer is assigned (FR-032).
            - **BarnIAndrelinjeBarnevernet**: A Person formally registered as a 2nd-line child
              welfare recipient. Each Person has at most one barn registration (1:1).

            """;

        var tree = SpecExplorerService.Parse(md);

        var entities = AllDescendants(tree).Where(n => n.NodeType == SpecNodeType.Entity).ToList();
        entities.Should().HaveCount(2, "two entity definitions");

        var person = entities[0];
        person.Title.Should().Be("Person");
        person.SpecItemId.Should().Be("Person");
        person.FullContent.Should().Contain("UUID v4");
        person.FullContent.Should().Contain("DUF number");

        var barn = entities[1];
        barn.Title.Should().Be("BarnIAndrelinjeBarnevernet");
    }

    // =========================================================================
    // T6 — Metadata parsing: Source/Status/Feature Branch at H1 depth
    // =========================================================================

    [Fact]
    public void Metadata_lines_at_H1_depth_produce_Metadata_nodes_not_clarifications()
    {
        // personSpec.md lines 3-6: four frontmatter metadata lines
        const string md = """
            # Feature Specification: Person Module Core

            **Feature Branch**: `001-person-module`
            **Created**: 2026-03-06
            **Status**: Draft
            **Source**: `docs/person-func-requirements-no.md`

            """;

        var tree = SpecExplorerService.Parse(md);

        var metas = AllDescendants(tree).Where(n => n.NodeType == SpecNodeType.Metadata).ToList();
        metas.Should().HaveCount(4, "four metadata lines: Feature Branch, Created, Status, Source");

        metas.Should().Contain(m => m.Title.Contains("Source") && m.Title.Contains("docs/person-func-requirements-no.md"));
        metas.Should().Contain(m => m.Title.Contains("Status") && m.Title.Contains("Draft"));

        AllDescendants(tree)
            .Where(n => n.NodeType == SpecNodeType.Clarification)
            .Should().BeEmpty("metadata lines must not be classified as clarifications");
    }

    // =========================================================================
    // T7 — Edge case parsing: multi-line bullet items in Edge Cases section
    // =========================================================================

    [Fact]
    public void Edge_case_bullets_with_wrapped_lines_produce_EdgeCase_nodes()
    {
        // personSpec.md Edge Cases: 6 items, first two span multiple lines
        const string md = """
            # Spec

            ## Overview

            ### Edge Cases

            - An unborn child has no birth date and no national ID — this is a valid, complete
              identity state, not a data quality problem.
            - An EMA child may initially have only a name — no national ID, no DUF number.
            - A person's national ID may become available later (DUF → fødselsnummer upgrade).

            """;

        var tree = SpecExplorerService.Parse(md);

        var edgeCases = AllDescendants(tree).Where(n => n.NodeType == SpecNodeType.EdgeCase).ToList();
        edgeCases.Should().HaveCount(3, "three edge-case bullet items");

        edgeCases[0].FullContent.Should().Contain("no birth date");
        edgeCases[0].FullContent.Should().Contain("data quality problem");

        edgeCases[1].FullContent.Should().Contain("EMA child");
        edgeCases[2].FullContent.Should().Contain("DUF → fødselsnummer");

        AllDescendants(tree)
            .Where(n => n.NodeType == SpecNodeType.Requirement)
            .Should().BeEmpty("edge-case bullets must not be misclassified as requirements");
    }

    // =========================================================================
    // T8 — API Surface parsing: GraphQL and REST bullet items
    // =========================================================================

    [Fact]
    public void API_Surface_bullets_with_wrapped_lines_produce_ApiSurfaceItem_nodes()
    {
        // personSpec.md API Surface: two items (GraphQL and REST), each multi-line
        const string md = """
            # Spec

            ## Architecture

            ### API Surface

            The Person module exposes a dual API surface per the module constitution:

            - **GraphQL** — consumed by the presentation layer for all read operations: child search
              (US1), child profile display (US2), access management display (US3), and reference data
              (US4). Provides field-level flexibility and data minimisation per GDPR Article 5.
            - **REST** — consumed by the BiRK adapter for data ingestion (US5) and by the service
              itself for operation registration at startup (FR-029). Provides predictable, idempotent
              ingestion with explicit HTTP status codes.

            """;

        var tree = SpecExplorerService.Parse(md);

        var apiItems = AllDescendants(tree).Where(n => n.NodeType == SpecNodeType.ApiSurfaceItem).ToList();
        apiItems.Should().HaveCount(2, "two API surface bullet items: GraphQL and REST");

        var graphql = apiItems[0];
        graphql.Title.Should().Contain("GraphQL");

        var rest = apiItems[1];
        rest.Title.Should().Contain("REST");

        AllDescendants(tree)
            .Where(n => n.NodeType is SpecNodeType.Requirement or SpecNodeType.UserStory)
            .Should().BeEmpty("API Surface bullets referencing FR-029/US1-5 must not create phantom spec items");
    }

    // =========================================================================
    // T9 — Inflated count regression: Q/A prose referencing FR/US IDs
    // =========================================================================

    [Fact]
    public void Spec_item_references_in_QA_prose_do_not_inflate_requirement_count()
    {
        // The old parser created a phantom node for every FR/US mention inside Q/A text.
        // This test guards against regression to the 187-requirement inflation bug.
        const string md = """
            # Spec

            ## Clarifications

            ### Q&A

            - Q: Is FR-029 in scope? → A: Yes, along with US1, US2, US3, US4, US5, FR-001 through FR-033.
            - Q: Does SC-001 cover edge cases? → A: SC-001 addresses the primary path only.

            ## Requirements

            ### Functional Requirements

            **FR-001**: The system MUST do something.

            """;

        var tree = SpecExplorerService.Parse(md);

        tree.Health.Requirements.Should().Be(1, "only the explicit FR-001 block is a requirement");
        tree.Health.Clarifications.Should().Be(2, "two Q/A pairs in the clarifications section");
        tree.Health.UserStories.Should().Be(0);
    }

    // =========================================================================
    // T10 — Metadata lines inside H1 do not inflate requirement or clarification counts
    // =========================================================================

    [Fact]
    public void Metadata_lines_inside_h1_do_not_inflate_requirement_or_clarification_counts()
    {
        const string md = """
            # Feature Specification: Person Module Core

            **Feature Branch**: `001-person-module`
            **Created**: 2026-03-06
            **Status**: Draft
            **Source**: `docs/person-func-requirements-no.md`

            ## Requirements

            ### Functional Requirements

            **FR-001**: The system MUST allow caseworkers to search for children by first name, last name, and date of birth.

            **FR-002**: The system MUST display the search result as a list of matching persons.

            """;

        var tree = SpecExplorerService.Parse(md);

        tree.Health.Requirements.Should().Be(2, "only the two explicit FR blocks are requirements");
        tree.Health.Clarifications.Should().Be(0, "metadata lines must not become clarifications");

        var metaNodes = AllDescendants(tree)
            .Where(n => n.NodeType == SpecNodeType.Metadata)
            .ToList();
        metaNodes.Should().HaveCount(4, "four frontmatter-style lines: Feature Branch, Created, Status, Source");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static IEnumerable<SpecNode> AllDescendants(SpecTree tree) =>
        tree.Roots.SelectMany(r => DescendantsOf(r));

    private static IEnumerable<SpecNode> DescendantsOf(SpecNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var d in DescendantsOf(child))
                yield return d;
    }
}
